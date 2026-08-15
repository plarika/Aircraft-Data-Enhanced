// SPDX-License-Identifier: MIT
using System.Text;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed record AcarsMessage(
    bool Parsed,
    bool CrcValid,
    bool FinalBlock,
    string Direction,
    string Mode,
    string Registration,
    string RawRegistration,
    string Acknowledgement,
    string Label,
    string BlockId,
    string MessageNumber,
    string MessageSequence,
    string FlightId,
    string Sublabel,
    string MessageFunction,
    string Text,
    string Status)
{
    public bool MoreBlocks => !FinalBlock;

    public string MessageNumberWithSequence =>
        string.IsNullOrWhiteSpace(MessageNumber)
            ? string.Empty
            : MessageNumber + MessageSequence;

    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                "ACARS"
            };

            if (!string.IsNullOrWhiteSpace(Registration))
                parts.Add($"Reg {Registration}");

            if (!string.IsNullOrWhiteSpace(FlightId))
                parts.Add($"Flight {FlightId}");

            if (!string.IsNullOrWhiteSpace(Label))
                parts.Add($"Label {Label}");

            if (!string.IsNullOrWhiteSpace(MessageNumberWithSequence))
                parts.Add($"Msg {MessageNumberWithSequence}");

            parts.Add(CrcValid ? "CRC OK" : "CRC warning");

            if (!string.IsNullOrWhiteSpace(Text))
                parts.Add(Text);

            return string.Join(" · ", parts);
        }
    }
}

internal static class AcarsMessageParser
{
    private const byte Del = 0x7F;
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const byte Etb = 0x17;
    private const byte Ack = 0x06;
    private const byte Nak = 0x15;
    private const int MinimumFrameLength = 16;

    public static bool TryParse(
        ReadOnlySpan<byte> raw,
        string direction,
        out AcarsMessage? message)
    {
        message = null;

        if (raw.Length < MinimumFrameLength)
            return false;

        if (raw[^1] != Del)
            return false;

        var crcValid =
            CalculateCrc(raw[..^1]) == 0;

        // The ACARS parity bit occupies bit 7. Preserve the original bytes
        // for CRC, then remove parity before parsing printable fields.
        var frame = new byte[raw.Length];

        for (var index = 0;
             index < raw.Length;
             index++)
        {
            frame[index] =
                (byte)(raw[index] & 0x7F);
        }

        // DEL, two CRC octets and ETX/ETB are at the end.
        var logicalLength =
            frame.Length - 1 - 2;

        if (logicalLength < 13)
            return false;

        var finalMarker =
            frame[logicalLength - 1];

        var finalBlock =
            finalMarker == Etx;

        if (!finalBlock &&
            finalMarker != Etb)
        {
            return false;
        }

        logicalLength--;

        var offset = 0;
        var mode =
            PrintableChar(frame[offset++])
                .ToString();

        if (offset + 7 > logicalLength)
            return false;

        var rawRegistration =
            ReadAscii(frame, offset, 7);

        offset += 7;

        if (offset >= logicalLength)
            return false;

        var acknowledgement =
            DecodeAcknowledgement(frame[offset++]);

        if (offset + 2 > logicalLength)
            return false;

        var labelFirst =
            PrintableChar(frame[offset++]);

        var labelSecondByte =
            frame[offset++];

        var labelSecond =
            labelSecondByte == Del
                ? 'd'
                : PrintableChar(labelSecondByte);

        var label =
            new string(
                new[]
                {
                    labelFirst,
                    labelSecond
                });

        if (offset >= logicalLength)
            return false;

        var blockIdChar =
            frame[offset++] == 0
                ? ' '
                : PrintableChar(frame[offset - 1]);

        var blockId =
            blockIdChar.ToString();

        var downlink =
            blockIdChar is >= '0' and <= '9';

        if (offset >= logicalLength)
        {
            if (downlink)
                return false;

            message =
                Create(
                    crcValid,
                    finalBlock,
                    direction,
                    mode,
                    rawRegistration,
                    acknowledgement,
                    label,
                    blockId,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "acars_empty_uplink");

            return true;
        }

        if (frame[offset] != Stx)
            return false;

        offset++;

        var messageNumber =
            string.Empty;

        var messageSequence =
            string.Empty;

        var flightId =
            string.Empty;

        if (downlink)
        {
            if (logicalLength - offset < 10)
                return false;

            messageNumber =
                ReadAscii(frame, offset, 3);

            messageSequence =
                PrintableChar(frame[offset + 3])
                    .ToString();

            offset += 4;

            flightId =
                CleanToken(
                    ReadAscii(frame, offset, 6));

            offset += 6;
        }

        var textLength =
            Math.Max(
                0,
                logicalLength - offset);

        var text =
            CleanText(
                frame.AsSpan(
                    offset,
                    textLength));

        message =
            Create(
                crcValid,
                finalBlock,
                direction,
                mode,
                rawRegistration,
                acknowledgement,
                label,
                blockId,
                messageNumber,
                messageSequence,
                flightId,
                string.Empty,
                string.Empty,
                text,
                crcValid
                    ? "acars_valid"
                    : "acars_crc_warning");

        return true;
    }

    private static AcarsMessage Create(
        bool crcValid,
        bool finalBlock,
        string direction,
        string mode,
        string rawRegistration,
        string acknowledgement,
        string label,
        string blockId,
        string messageNumber,
        string messageSequence,
        string flightId,
        string sublabel,
        string messageFunction,
        string text,
        string status) =>
        new(
            true,
            crcValid,
            finalBlock,
            direction,
            mode,
            NormalizeRegistration(
                rawRegistration),
            rawRegistration.Trim(),
            acknowledgement,
            label.Trim(),
            blockId.Trim(),
            CleanToken(messageNumber),
            CleanToken(messageSequence),
            CleanToken(flightId),
            CleanToken(sublabel),
            CleanToken(messageFunction),
            text,
            status);

    private static ushort CalculateCrc(
        ReadOnlySpan<byte> data)
    {
        var crc = 0;

        foreach (var value in data)
        {
            crc ^=
                value;

            for (var bit = 0;
                 bit < 8;
                 bit++)
            {
                crc =
                    (crc & 1) != 0
                        ? (crc >> 1) ^
                          0x8408
                        : crc >> 1;
            }
        }

        return (ushort)crc;
    }

    private static string NormalizeRegistration(
        string value)
    {
        value =
            value
                .Trim()
                .TrimStart('.')
                .Trim();

        return CleanToken(value);
    }

    private static string ReadAscii(
        IReadOnlyList<byte> data,
        int offset,
        int length)
    {
        var builder =
            new StringBuilder(length);

        for (var index = 0;
             index < length;
             index++)
        {
            builder.Append(
                PrintableChar(
                    data[offset + index]));
        }

        return builder.ToString();
    }

    private static string CleanText(
        ReadOnlySpan<byte> data)
    {
        var builder =
            new StringBuilder(data.Length);

        foreach (var value in data)
        {
            if (value is 0x0D or 0x0A)
            {
                if (builder.Length > 0 &&
                    builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(
                value is >= 0x20 and <= 0x7E
                    ? (char)value
                    : '.');
        }

        return builder
            .ToString()
            .Trim();
    }

    private static string CleanToken(
        string value) =>
        new string(
            value
                .Where(
                    character =>
                        character is >= ' ' and <= '~')
                .ToArray())
            .Trim();

    private static char PrintableChar(
        byte value) =>
        value is >= 0x20 and <= 0x7E
            ? (char)value
            : value == Del
                ? (char)Del
                : '.';

    private static string DecodeAcknowledgement(
        byte value) =>
        value switch
        {
            Nak => "!",
            Ack => "^",
            _ => PrintableChar(value).ToString()
        };
}
