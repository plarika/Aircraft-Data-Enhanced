// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal static class Vdl2JsonParser
{
    public static bool TryParse(string json, out Vdl2Message? message, out string error)
    {
        message = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Empty datagram.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "JSON root is not an object.";
                return false;
            }

            var protocol = JsonValue.FirstString(root,
                "app.name", "app", "protocol", "type", "vdl2");
            if (string.IsNullOrWhiteSpace(protocol))
                protocol = "VDL2";

            var direction = NormalizeDirection(JsonValue.FirstString(root,
                "direction", "avlc.direction", "link_direction", "message.direction"));

            var icao = NormalizeHex(JsonValue.FirstString(root,
                "icao", "aircraft.icao", "acars.icao", "avlc.src.addr", "avlc.dst.addr"));

            var registration = JsonValue.FirstString(root,
                "registration", "aircraft.registration", "acars.reg", "acars.registration");

            var callsign = JsonValue.FirstString(root,
                "callsign", "flight", "aircraft.callsign", "acars.flight");

            var source = JsonValue.FirstString(root,
                "source", "src", "avlc.src.addr", "avlc.src.type");
            var destination = JsonValue.FirstString(root,
                "destination", "dst", "avlc.dst.addr", "avlc.dst.type");

            var label = JsonValue.FirstString(root,
                "label", "acars.label", "message.label");

            var text = JsonValue.FirstString(root,
                "text", "message.text", "acars.text", "message", "data");
            if (string.IsNullOrWhiteSpace(text))
                text = CompactJson(root);

            var frequency = JsonValue.FirstDouble(root,
                "freq", "frequency", "frequency_mhz", "frequency_hz", "metadata.freq");
            if (frequency is > 1_000_000)
                frequency /= 1_000_000.0;

            var signal = JsonValue.FirstDouble(root,
                "signal", "signal_db", "metadata.signal", "metadata.snr");

            message = new Vdl2Message(
                DateTimeOffset.Now,
                protocol,
                direction,
                icao,
                registration,
                callsign,
                source,
                destination,
                label,
                text,
                frequency,
                signal,
                true,
                json);

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Parser error: {ex.Message}";
            return false;
        }
    }

    private static string NormalizeDirection(string value)
    {
        value = value.Trim().ToLowerInvariant();
        return value switch
        {
            "air2gnd" or "air_to_ground" or "downlink" or "a2g" => "Air → Ground",
            "gnd2air" or "ground_to_air" or "uplink" or "g2a" => "Ground → Air",
            _ => string.IsNullOrWhiteSpace(value) ? "Unknown" : value
        };
    }

    private static string NormalizeHex(string value)
    {
        value = value.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return value.ToUpperInvariant();
    }

    private static string CompactJson(JsonElement root)
    {
        var result = root.GetRawText();
        return result.Length <= 500 ? result : result[..500] + "…";
    }
}
