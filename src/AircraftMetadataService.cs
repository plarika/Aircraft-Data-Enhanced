// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed record AircraftMetadata(
    string Icao,
    string Registration,
    string Manufacturer,
    string Type,
    string IcaoTypeCode,
    string Operator,
    string OperatorCode,
    bool Found,
    string Status)
{
    public static AircraftMetadata Unavailable(
        string icao,
        string status) =>
        new(
            icao,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            status);
}

internal sealed record FlightRouteMetadata(
    string Callsign,
    string Route,
    bool Found,
    string Status)
{
    public static FlightRouteMetadata Unavailable(
        string callsign,
        string status) =>
        new(
            callsign,
            string.Empty,
            false,
            status);
}

internal sealed class AircraftMetadataService : IDisposable
{
    private sealed record CacheEntry<T>(
        T Value,
        DateTimeOffset ExpiresAt);

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _networkGate =
        new(
            1,
            1);

    private readonly ConcurrentDictionary<
        string,
        CacheEntry<AircraftMetadata>>
        _aircraftCache =
            new(
                StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<
        string,
        CacheEntry<FlightRouteMetadata>>
        _routeCache =
            new(
                StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public AircraftMetadataService()
    {
        _client =
            new HttpClient
            {
                Timeout =
                    TimeSpan.FromSeconds(6)
            };

        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "AircraftDataEnhanced",
                "0.15"));

        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/json"));
    }

    public async Task<AircraftMetadata> LookupAircraftAsync(
        string icao,
        CancellationToken cancellationToken)
    {
        if (!AircraftOnlineLookup.TryNormalizeIcao(
            icao,
            out var normalized))
        {
            return AircraftMetadata.Unavailable(
                string.Empty,
                "invalid_icao");
        }

        if (TryReadCache(
            _aircraftCache,
            normalized,
            out var cached))
        {
            return cached;
        }

        await _networkGate.WaitAsync(
            cancellationToken)
            .ConfigureAwait(
                false);

        try
        {
            ThrowIfDisposed();

            if (TryReadCache(
                _aircraftCache,
                normalized,
                out cached))
            {
                return cached;
            }

            using var response =
                await _client.GetAsync(
                    "https://hexdb.io/api/v1/aircraft/" +
                    normalized.ToLowerInvariant(),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                    .ConfigureAwait(
                        false);

            if (response.StatusCode ==
                HttpStatusCode.NotFound)
            {
                var missing =
                    AircraftMetadata.Unavailable(
                        normalized,
                        "not_found");

                WriteCache(
                    _aircraftCache,
                    normalized,
                    missing,
                    TimeSpan.FromMinutes(30));

                return missing;
            }

            if (!response.IsSuccessStatusCode)
            {
                return AircraftMetadata.Unavailable(
                    normalized,
                    $"http_{(int)response.StatusCode}");
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken)
                    .ConfigureAwait(
                        false);

            using var document =
                await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken:
                        cancellationToken)
                    .ConfigureAwait(
                        false);

            var root =
                document.RootElement;

            var metadata =
                new AircraftMetadata(
                    normalized,
                    ReadString(
                        root,
                        "Registration"),
                    ReadString(
                        root,
                        "Manufacturer"),
                    ReadString(
                        root,
                        "Type"),
                    ReadString(
                        root,
                        "ICAOTypeCode"),
                    ReadString(
                        root,
                        "RegisteredOwners"),
                    ReadString(
                        root,
                        "OperatorFlagCode"),
                    true,
                    "ok");

            WriteCache(
                _aircraftCache,
                normalized,
                metadata,
                TimeSpan.FromHours(24));

            return metadata;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AircraftMetadata.Unavailable(
                normalized,
                "lookup_failed: " +
                ex.GetType().Name);
        }
        finally
        {
            SafeReleaseNetworkGate();
        }
    }

    public async Task<FlightRouteMetadata> LookupRouteAsync(
        string callsign,
        CancellationToken cancellationToken)
    {
        var normalized =
            (callsign ?? string.Empty)
            .Trim()
            .ToUpperInvariant();

        if (normalized.Length < 3 ||
            normalized.Length > 12)
        {
            return FlightRouteMetadata.Unavailable(
                normalized,
                "callsign_unavailable");
        }

        if (TryReadCache(
            _routeCache,
            normalized,
            out var cached))
        {
            return cached;
        }

        await _networkGate.WaitAsync(
            cancellationToken)
            .ConfigureAwait(
                false);

        try
        {
            ThrowIfDisposed();

            if (TryReadCache(
                _routeCache,
                normalized,
                out cached))
            {
                return cached;
            }

            using var response =
                await _client.GetAsync(
                    "https://hexdb.io/api/v1/route/icao/" +
                    Uri.EscapeDataString(
                        normalized),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                    .ConfigureAwait(
                        false);

            if (response.StatusCode ==
                HttpStatusCode.NotFound)
            {
                var missing =
                    FlightRouteMetadata.Unavailable(
                        normalized,
                        "not_found");

                WriteCache(
                    _routeCache,
                    normalized,
                    missing,
                    TimeSpan.FromMinutes(15));

                return missing;
            }

            if (!response.IsSuccessStatusCode)
            {
                return FlightRouteMetadata.Unavailable(
                    normalized,
                    $"http_{(int)response.StatusCode}");
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken)
                    .ConfigureAwait(
                        false);

            using var document =
                await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken:
                        cancellationToken)
                    .ConfigureAwait(
                        false);

            var route =
                ReadString(
                    document.RootElement,
                    "route");

            var value =
                string.IsNullOrWhiteSpace(
                    route)
                    ? FlightRouteMetadata.Unavailable(
                        normalized,
                        "route_empty")
                    : new FlightRouteMetadata(
                        normalized,
                        route,
                        true,
                        "ok");

            WriteCache(
                _routeCache,
                normalized,
                value,
                TimeSpan.FromMinutes(15));

            return value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FlightRouteMetadata.Unavailable(
                normalized,
                "lookup_failed: " +
                ex.GetType().Name);
        }
        finally
        {
            SafeReleaseNetworkGate();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.Dispose();
        _networkGate.Dispose();
    }

    private static string ReadString(
        JsonElement root,
        string propertyName)
    {
        if (root.ValueKind !=
                JsonValueKind.Object ||
            !root.TryGetProperty(
                propertyName,
                out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String =>
                value.GetString()?.Trim() ??
                string.Empty,

            JsonValueKind.Number or
            JsonValueKind.True or
            JsonValueKind.False =>
                value.ToString(),

            _ =>
                string.Empty
        };
    }

    private static bool TryReadCache<T>(
        ConcurrentDictionary<
            string,
            CacheEntry<T>> cache,
        string key,
        out T value)
    {
        if (cache.TryGetValue(
                key,
                out var entry) &&
            entry.ExpiresAt >
                DateTimeOffset.UtcNow)
        {
            value =
                entry.Value;

            return true;
        }

        cache.TryRemove(
            key,
            out _);

        value =
            default!;

        return false;
    }

    private static void WriteCache<T>(
        ConcurrentDictionary<
            string,
            CacheEntry<T>> cache,
        string key,
        T value,
        TimeSpan lifetime)
    {
        cache[key] =
            new CacheEntry<T>(
                value,
                DateTimeOffset.UtcNow +
                lifetime);
    }

    private void SafeReleaseNetworkGate()
    {
        try
        {
            _networkGate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }
}
