// SPDX-License-Identifier: MIT
using System.Text;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed record Vdl2AvlcFrame(
    int Index,
    bool FcsValid,
    ushort FcsResidual,
    int LengthOctets,
    string Direction,
    string Icao,
    string Source,
    string Destination,
    string FrameType,
    string Label,
    string InformationProtocol,
    string Text,
    string RawHex,
    AcarsMessage? Acars = null);

internal sealed record Vdl2PayloadResult(
    bool Attempted,
    bool Complete,
    int TransmissionLengthBits,
    int DataOctets,
    int FecOctets,
    int RequiredRawBits,
    int AvailableRawBits,
    int ReedSolomonBlocks,
    bool ReedSolomonValid,
    int CorrectedSymbols,
    int ErasureSymbols,
    int HdlcFrames,
    int HdlcUnstuffErrors,
    int FcsValidFrames,
    int FcsInvalidFrames,
    string CorrectedPayloadHex,
    string Status,
    Vdl2AvlcFrame[] Frames)
{
    public static Vdl2PayloadResult NotAttempted(
        string status) =>
        new(
            false,
            false,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            0,
            0,
            0,
            0,
            0,
            0,
            string.Empty,
            status,
            Array.Empty<Vdl2AvlcFrame>());
}

internal static class Vdl2PayloadDecoder
{
    private readonly record struct AvlcAddress(
        int Address,
        int Type,
        bool StatusBit,
        string TypeName,
        string Display);

    private static readonly int[] HdlcFlag =
    [
        0, 1, 1, 1, 1, 1, 1, 0
    ];

    private const int HeaderBits = 25;
    private const int DataSymbolsPerBlock = 249;
    private const int SymbolsPerBlock = 255;
    private const ushort GoodFcsResidual = 0xF0B8;

    public static Vdl2PayloadResult Decode(
        IReadOnlyList<int> descrambledBits,
        int transmissionLengthBits)
    {
        if (transmissionLengthBits <= 0)
        {
            return Vdl2PayloadResult.NotAttempted(
                "payload_length_invalid");
        }

        if (descrambledBits.Count < HeaderBits)
        {
            return Vdl2PayloadResult.NotAttempted(
                "payload_header_missing");
        }

        var dataOctets =
            (transmissionLengthBits + 7) /
            8;

        var fullBlocks =
            dataOctets /
            DataSymbolsPerBlock;

        var partialLength =
            dataOctets %
            DataSymbolsPerBlock;

        var blockCount =
            fullBlocks +
            (partialLength > 0 ? 1 : 0);

        var lastRowLength =
            partialLength > 0
                ? partialLength
                : DataSymbolsPerBlock;

        var fecOctets =
            fullBlocks * 6 +
            (partialLength > 0
                ? GetFecOctetCount(
                    partialLength)
                : 0);

        var inputOctets =
            dataOctets +
            fecOctets;

        if (fecOctets == 0)
        {
            return new Vdl2PayloadResult(
                true,
                false,
                transmissionLengthBits,
                dataOctets,
                0,
                0,
                Math.Max(
                    0,
                    descrambledBits.Count -
                    HeaderBits),
                blockCount,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                string.Empty,
                "payload_no_fec",
                Array.Empty<Vdl2AvlcFrame>());
        }

        var requiredRawBits =
            inputOctets *
            8;

        var availableRawBits =
            Math.Max(
                0,
                descrambledBits.Count -
                HeaderBits);

        if (blockCount <= 0)
        {
            return new Vdl2PayloadResult(
                true,
                false,
                transmissionLengthBits,
                dataOctets,
                fecOctets,
                requiredRawBits,
                availableRawBits,
                0,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                string.Empty,
                "payload_length_invalid",
                Array.Empty<Vdl2AvlcFrame>());
        }

        if (availableRawBits < requiredRawBits)
        {
            return new Vdl2PayloadResult(
                true,
                false,
                transmissionLengthBits,
                dataOctets,
                fecOctets,
                requiredRawBits,
                availableRawBits,
                blockCount,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                string.Empty,
                "payload_truncated",
                Array.Empty<Vdl2AvlcFrame>());
        }

        var dataValues = ReadOctetsLsbFirst(
            descrambledBits,
            HeaderBits,
            dataOctets);

        var fecValues = ReadOctetsLsbFirst(
            descrambledBits,
            HeaderBits +
                dataOctets * 8,
            fecOctets);

        var rows =
            Enumerable
                .Range(
                    0,
                    blockCount)
                .Select(
                    _ =>
                        new byte[
                            SymbolsPerBlock])
                .ToArray();

        DeinterleaveInto(
            dataValues,
            blockCount,
            rows,
            DataSymbolsPerBlock,
            0);

        var fecRows =
            blockCount -
            (GetFecOctetCount(
                 lastRowLength) == 0
                ? 1
                : 0);

        DeinterleaveInto(
            fecValues,
            fecRows,
            rows,
            6,
            DataSymbolsPerBlock);

        var correctedPayload =
            new List<byte>(
                dataOctets);

        var correctedSymbols = 0;
        var erasureSymbols = 0;
        var reedSolomonValid = true;

        for (var rowIndex = 0;
             rowIndex < rows.Length;
             rowIndex++)
        {
            var dataLength =
                rowIndex == rows.Length - 1
                    ? lastRowLength
                    : DataSymbolsPerBlock;

            const int dataOffset = 0;

            var transmittedFec =
                GetFecOctetCount(
                    dataLength);

            var erasures =
                Enumerable
                    .Range(
                        DataSymbolsPerBlock +
                        transmittedFec,
                        6 -
                        transmittedFec)
                    .ToArray();

            erasureSymbols +=
                erasures.Length;

            var result =
                ReedSolomon255249.Decode(
                    rows[rowIndex],
                    erasures,
                    out var correctedLocations);

            if (result < 0)
            {
                reedSolomonValid = false;
                break;
            }

            correctedSymbols +=
                correctedLocations.Count(
                    location =>
                        location >= dataOffset &&
                        location <
                            dataOffset +
                            dataLength);

            for (var index = 0;
                 index < dataLength;
                 index++)
            {
                correctedPayload.Add(
                    rows[rowIndex][
                        dataOffset +
                        index]);
            }
        }

        if (!reedSolomonValid)
        {
            return new Vdl2PayloadResult(
                true,
                true,
                transmissionLengthBits,
                dataOctets,
                fecOctets,
                requiredRawBits,
                availableRawBits,
                blockCount,
                false,
                correctedSymbols,
                erasureSymbols,
                0,
                0,
                0,
                0,
                string.Empty,
                "rs_uncorrectable",
                Array.Empty<Vdl2AvlcFrame>());
        }

        var payloadBits =
            BytesToBitsLsbFirst(
                correctedPayload,
                transmissionLengthBits);

        var hdlcResult =
            ExtractHdlcFrames(
                payloadBits);

        var validFrames =
            new List<Vdl2AvlcFrame>();

        var invalidFcs = 0;
        var frameIndex = 0;

        foreach (var frameBytes in
                 hdlcResult.Frames)
        {
            var fcsResidual =
                CalculateFcs(
                    frameBytes);

            var fcsValid =
                fcsResidual ==
                GoodFcsResidual;

            if (!fcsValid)
            {
                invalidFcs++;
                frameIndex++;
                continue;
            }

            if (TryParseAvlc(
                frameBytes,
                frameIndex,
                fcsResidual,
                out var avlc))
            {
                validFrames.Add(avlc);
            }

            frameIndex++;
        }

        string status;

        if (validFrames.Count > 0)
        {
            status = "AVLC-VALID";
        }
        else if (hdlcResult.Frames.Count == 0)
        {
            status =
                hdlcResult.UnstuffErrors > 0
                    ? "hdlc_unstuff_failed"
                    : "hdlc_no_frame";
        }
        else if (invalidFcs > 0)
        {
            status = "avlc_fcs_failed";
        }
        else
        {
            status = "avlc_parse_failed";
        }

        return new Vdl2PayloadResult(
            true,
            true,
            transmissionLengthBits,
            dataOctets,
            fecOctets,
            requiredRawBits,
            availableRawBits,
            blockCount,
            true,
            correctedSymbols,
            erasureSymbols,
            hdlcResult.Frames.Count,
            hdlcResult.UnstuffErrors,
            validFrames.Count,
            invalidFcs,
            Convert.ToHexString(
                correctedPayload.ToArray()),
            status,
            validFrames.ToArray());
    }

    private static int GetFecOctetCount(
        int dataOctets) =>
        dataOctets switch
        {
            < 3 => 0,
            < 31 => 2,
            < 68 => 4,
            _ => 6
        };

    private static byte[] ReadOctetsLsbFirst(
        IReadOnlyList<int> bits,
        int bitOffset,
        int octetCount)
    {
        var output =
            new byte[octetCount];

        for (var octet = 0;
             octet < octetCount;
             octet++)
        {
            var value = 0;

            for (var bit = 0;
                 bit < 8;
                 bit++)
            {
                value |=
                    (bits[
                        bitOffset +
                        octet * 8 +
                        bit] &
                     1) <<
                    bit;
            }

            output[octet] =
                (byte)value;
        }

        return output;
    }

    private static void DeinterleaveInto(
        IReadOnlyList<byte> input,
        int rowCount,
        byte[][] output,
        int fillWidth,
        int offset)
    {
        if (rowCount <= 0)
        {
            if (input.Count > 0)
            {
                throw new InvalidDataException(
                    "VDL2 deinterleaver received data with zero rows.");
            }

            return;
        }

        var lastRowLength =
            input.Count -
            (rowCount - 1) *
            fillWidth;

        if (lastRowLength < 0 ||
            lastRowLength > fillWidth)
        {
            throw new InvalidDataException(
                "VDL2 deinterleaver has an invalid final row length.");
        }

        var row = 0;
        var column = offset;
        var lastRowEnd =
            lastRowLength +
            offset;

        foreach (var value in input)
        {
            if (row == rowCount - 1 &&
                column >= lastRowEnd)
            {
                if (column < SymbolsPerBlock)
                    output[row][column] = 0;

                row = 0;
                column++;
            }

            if (column >= SymbolsPerBlock)
            {
                throw new InvalidDataException(
                    "VDL2 deinterleaver exceeded one RS row.");
            }

            output[row][column] =
                value;

            row++;

            if (row == rowCount)
            {
                row = 0;
                column++;
            }
        }
    }

    private static List<int> BytesToBitsLsbFirst(
        IReadOnlyList<byte> bytes,
        int bitCount)
    {
        var output =
            new List<int>(
                bitCount);

        for (var bitIndex = 0;
             bitIndex < bitCount;
             bitIndex++)
        {
            var octet =
                bytes[
                    bitIndex /
                    8];

            output.Add(
                (octet >>
                 (bitIndex % 8)) &
                1);
        }

        return output;
    }

    private sealed record HdlcResult(
        List<byte[]> Frames,
        int UnstuffErrors);

    private static HdlcResult ExtractHdlcFrames(
        IReadOnlyList<int> bits)
    {
        var flags =
            new List<int>();

        for (var index = 0;
             index <= bits.Count - 8;
             index++)
        {
            if (!IsFlag(bits, index))
                continue;

            flags.Add(index);
            index += 7;
        }

        var frames =
            new List<byte[]>();

        var unstuffErrors = 0;

        for (var flagIndex = 0;
             flagIndex + 1 < flags.Count;
             flagIndex++)
        {
            var start =
                flags[flagIndex] +
                8;

            var end =
                flags[flagIndex + 1];

            if (end <= start)
                continue;

            if (!TryUnstuff(
                bits,
                start,
                end,
                out var unstuffed))
            {
                unstuffErrors++;
                continue;
            }

            if (unstuffed.Count < 88 ||
                unstuffed.Count % 8 != 0)
            {
                if (unstuffed.Count > 0)
                    unstuffErrors++;

                continue;
            }

            var frame =
                new byte[
                    unstuffed.Count /
                    8];

            for (var octet = 0;
                 octet < frame.Length;
                 octet++)
            {
                var value = 0;

                for (var bit = 0;
                     bit < 8;
                     bit++)
                {
                    value |=
                        unstuffed[
                            octet * 8 +
                            bit] <<
                        bit;
                }

                frame[octet] =
                    (byte)value;
            }

            frames.Add(frame);
        }

        return new HdlcResult(
            frames,
            unstuffErrors);
    }

    private static bool IsFlag(
        IReadOnlyList<int> bits,
        int offset)
    {
        for (var index = 0;
             index < HdlcFlag.Length;
             index++)
        {
            if ((bits[offset + index] & 1) !=
                HdlcFlag[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryUnstuff(
        IReadOnlyList<int> input,
        int start,
        int end,
        out List<int> output)
    {
        output =
            new List<int>(
                Math.Max(
                    0,
                    end - start));

        var consecutiveOnes = 0;

        for (var index = start;
             index < end;
             index++)
        {
            var bit =
                input[index] &
                1;

            output.Add(bit);

            if (bit == 0)
            {
                consecutiveOnes = 0;
                continue;
            }

            consecutiveOnes++;

            if (consecutiveOnes != 5)
                continue;

            index++;

            if (index >= end ||
                (input[index] & 1) != 0)
            {
                output.Clear();
                return false;
            }

            consecutiveOnes = 0;
        }

        return true;
    }

    private static ushort CalculateFcs(
        IReadOnlyList<byte> data)
    {
        var crc = 0xFFFF;

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

    private static bool TryParseAvlc(
        byte[] frame,
        int index,
        ushort fcsResidual,
        out Vdl2AvlcFrame result)
    {
        result = null!;

        if (frame.Length < 11)
            return false;

        var destination =
            ParseAddress(
                frame,
                0);

        var source =
            ParseAddress(
                frame,
                4);

        var control =
            frame[8];

        var informationLength =
            frame.Length -
            11;

        var information =
            informationLength > 0
                ? frame
                    .Skip(9)
                    .Take(
                        informationLength)
                    .ToArray()
                : Array.Empty<byte>();

        var direction =
            source.Type == 1
                ? "Air → Ground"
                : destination.Type == 1
                    ? "Ground → Air"
                    : "Ground/Unknown";

        var icao =
            source.Type == 1
                ? source.Address.ToString("X6")
                : destination.Type == 1
                    ? destination.Address.ToString("X6")
                    : string.Empty;

        var frameType =
            DecodeFrameType(
                control,
                source.StatusBit,
                out var label);

        var acarsEnvelope =
            information.Length >= 3 &&
            information[0] == 0xFF &&
            information[1] == 0xFF &&
            information[2] == 0x01;

        AcarsMessage? acars =
            null;

        if (acarsEnvelope &&
            information.Length > 3)
        {
            AcarsMessageParser.TryParse(
                information.AsSpan(3),
                direction,
                out acars);
        }

        var informationProtocol =
            acarsEnvelope
                ? "ACARS"
                : information.Length > 0
                    ? "X.25/Other"
                    : "No information";

        var text =
            acars?.Summary ??
            BuildText(
                frameType,
                informationProtocol,
                information);

        result =
            new Vdl2AvlcFrame(
                index,
                true,
                fcsResidual,
                frame.Length,
                direction,
                icao,
                source.Display,
                destination.Display,
                frameType,
                label,
                informationProtocol,
                text,
                Convert.ToHexString(frame),
                acars);

        return true;
    }

    private static AvlcAddress ParseAddress(
        IReadOnlyList<byte> frame,
        int offset)
    {
        var encoded =
            (uint)(frame[offset] >> 1) |
            ((uint)frame[offset + 1] << 6) |
            ((uint)frame[offset + 2] << 13) |
            ((uint)(frame[offset + 3] & 0xFE) << 20);

        var decoded =
            ReverseBits(
                encoded,
                28);

        var address =
            (int)(
                decoded &
                0xFFFFFFu);

        var type =
            (int)(
                (decoded >> 24) &
                0x7u);

        var status =
            ((decoded >> 27) &
             1u) != 0;

        var typeName =
            type switch
            {
                1 => "Aircraft",
                4 => "Ground administrative",
                5 => "Ground delegated",
                7 => "All stations",
                _ => $"Type {type}"
            };

        var prefix =
            type switch
            {
                1 => "AIR",
                4 => "GS-ADMIN",
                5 => "GS",
                7 => "ALL",
                _ => $"T{type}"
            };

        return new AvlcAddress(
            address,
            type,
            status,
            typeName,
            $"{prefix}:{address:X6}");
    }

    private static string DecodeFrameType(
        byte control,
        bool sourceStatusBit,
        out string label)
    {
        if ((control & 1) == 0)
        {
            var sendSequence =
                (control >> 1) &
                0x7;

            var poll =
                (control >> 4) &
                0x1;

            var receiveSequence =
                (control >> 5) &
                0x7;

            label =
                $"I S{sendSequence} R{receiveSequence} P{poll}";

            return "AVLC I-frame";
        }

        if ((control & 0x3) == 0x1)
        {
            var function =
                (control >> 2) &
                0x3;

            var functionName =
                function switch
                {
                    0 => "RR",
                    1 => "RNR",
                    2 => "REJ",
                    3 => "SREJ",
                    _ => "S"
                };

            var pollFinal =
                (control >> 4) &
                0x1;

            var receiveSequence =
                (control >> 5) &
                0x7;

            label =
                $"{functionName} R{receiveSequence} PF{pollFinal}";

            return "AVLC S-frame";
        }

        var modifier =
            (control >> 2) &
            0x3F;

        var commandModifier =
            modifier &
            0x3B;

        var command =
            commandModifier switch
            {
                0x00 => "UI",
                0x03 => "DM",
                0x10 => "DISC",
                0x18 => "UA",
                0x21 => "FRMR",
                0x2B => "XID",
                0x38 => "TEST",
                _ => $"U-{commandModifier:X2}"
            };

        var commandResponse =
            sourceStatusBit
                ? "Response"
                : "Command";

        label =
            $"{command} · {commandResponse}";

        return "AVLC U-frame";
    }

    private static string BuildText(
        string frameType,
        string informationProtocol,
        IReadOnlyList<byte> information)
    {
        var builder =
            new StringBuilder();

        builder
            .Append(frameType)
            .Append(" · ")
            .Append(informationProtocol)
            .Append(" · ")
            .Append(information.Count)
            .Append(" information octets");

        var printableOffset =
            informationProtocol == "ACARS" &&
            information.Count >= 3
                ? 3
                : 0;

        var printable =
            new string(
                information
                    .Skip(printableOffset)
                    .Take(96)
                    .Select(
                        value =>
                            value is >= 32 and <= 126
                                ? (char)value
                                : '.')
                    .ToArray())
                .Trim('.');

        if (!string.IsNullOrWhiteSpace(
            printable))
        {
            builder
                .Append(" · ")
                .Append(printable);
        }

        return builder.ToString();
    }

    private static uint ReverseBits(
        uint value,
        int bitCount)
    {
        uint reversed = 0;

        for (var index = 0;
             index < bitCount;
             index++)
        {
            reversed =
                (reversed << 1) |
                (value & 1u);

            value >>= 1;
        }

        return reversed;
    }
}
