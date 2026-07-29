// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal enum AircraftOnlineProvider
{
    Planespotters,
    AdsbExchange,
    FlightSearch
}

internal static class AircraftOnlineLookup
{
    public static bool TryNormalizeIcao(
        string? value,
        out string normalized)
    {
        normalized =
            (value ?? string.Empty)
            .Trim()
            .ToUpperInvariant();

        if (normalized.Length != 6)
        {
            normalized =
                string.Empty;

            return false;
        }

        foreach (var character in normalized)
        {
            var valid =
                character is >= '0' and <= '9' or
                >= 'A' and <= 'F';

            if (!valid)
            {
                normalized =
                    string.Empty;

                return false;
            }
        }

        return true;
    }

    public static string BuildUrl(
        AircraftOnlineProvider provider,
        string icao,
        string? registration = null,
        string? callsign = null)
    {
        if (!TryNormalizeIcao(
            icao,
            out var normalized))
        {
            throw new ArgumentException(
                "ICAO24 must contain exactly six hexadecimal characters.",
                nameof(icao));
        }

        var lower =
            normalized.ToLowerInvariant();

        return provider switch
        {
            AircraftOnlineProvider.Planespotters =>
                $"https://www.planespotters.net/hex/{normalized}",

            AircraftOnlineProvider.AdsbExchange =>
                $"https://globe.adsbexchange.com/?icao={lower}",

            AircraftOnlineProvider.FlightSearch =>
                BuildFlightSearchUrl(
                    normalized,
                    registration,
                    callsign),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(provider),
                    provider,
                    "Unknown aircraft lookup provider.")
        };
    }

    public static void Open(
        AircraftOnlineProvider provider,
        string icao,
        string? registration = null,
        string? callsign = null)
    {
        var url =
            BuildUrl(
                provider,
                icao,
                registration,
                callsign);

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
    }

    private static string BuildFlightSearchUrl(
        string icao,
        string? registration,
        string? callsign)
    {
        var parts =
            new List<string>();

        if (!string.IsNullOrWhiteSpace(
            callsign))
        {
            parts.Add(
                callsign.Trim());
        }

        if (!string.IsNullOrWhiteSpace(
            registration))
        {
            parts.Add(
                registration.Trim());
        }

        parts.Add(icao);
        parts.Add("current flight");
        parts.Add("departure arrival");

        return
            "https://www.bing.com/search?q=" +
            Uri.EscapeDataString(
                string.Join(
                    " ",
                    parts));
    }
}
