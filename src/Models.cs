// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed record Vdl2Message(
    DateTimeOffset ReceivedAt,
    string Protocol,
    string Direction,
    string Icao,
    string Registration,
    string Callsign,
    string Source,
    string Destination,
    string Label,
    string Text,
    double? FrequencyMhz,
    double? SignalDb,
    bool Valid,
    string RawJson,
    string AcarsMode = "",
    string AcarsBlockId = "",
    string AcarsMessageNumber = "",
    string AcarsMessageSequence = "",
    string AcarsAcknowledgement = "",
    bool? AcarsCrcValid = null,
    bool? AcarsMoreBlocks = null,
    string AcarsSublabel = "",
    string AcarsMessageFunction = "")
{
    public string AcarsMessageId =>
        string.IsNullOrWhiteSpace(AcarsMessageNumber)
            ? string.Empty
            : AcarsMessageNumber + AcarsMessageSequence;

    public string DedupKey =>
        $"{Protocol}|{Direction}|{Icao}|{Registration}|{Callsign}|{Source}|{Destination}|{Label}|{AcarsMessageId}|{Text}|{FrequencyMhz:0.000}";
}

internal sealed class DecoderStats
{
    private long _received;
    private long _accepted;
    private long _duplicates;
    private long _invalid;
    private long _dropped;

    public long Received => Interlocked.Read(ref _received);
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Duplicates => Interlocked.Read(ref _duplicates);
    public long Invalid => Interlocked.Read(ref _invalid);
    public long Dropped => Interlocked.Read(ref _dropped);

    public void OnReceived() => Interlocked.Increment(ref _received);
    public void OnAccepted() => Interlocked.Increment(ref _accepted);
    public void OnDuplicate() => Interlocked.Increment(ref _duplicates);
    public void OnInvalid() => Interlocked.Increment(ref _invalid);
    public void OnDropped() => Interlocked.Increment(ref _dropped);
}

internal static class JsonValue
{
    public static string FirstString(JsonElement root, params string[] paths)
    {
        foreach (var path in paths)
        {
            if (TryPath(root, path, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString()?.Trim() ?? string.Empty;
                if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    return value.ToString();
            }
        }
        return string.Empty;
    }

    public static double? FirstDouble(JsonElement root, params string[] paths)
    {
        foreach (var path in paths)
        {
            if (!TryPath(root, path, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out number))
                return number;
        }
        return null;
    }

    private static bool TryPath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }
}
