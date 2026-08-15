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

    private static readonly TimeSpan
        WholeLookupTimeout =
            TimeSpan.FromSeconds(12);

    private static readonly TimeSpan
        ProviderLookupTimeout =
            TimeSpan.FromSeconds(4);

    private readonly HttpClient _client;

    private readonly SemaphoreSlim
        _aircraftNetworkGate =
            new(
                1,
                1);

    private readonly SemaphoreSlim
        _routeNetworkGate =
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
                // Every provider has its own linked timeout below. Keeping the
                // HttpClient timeout infinite prevents one provider timeout
                // from being misinterpreted as cancellation of the complete
                // lookup and blocking the fallback provider.
                Timeout =
                    Timeout.InfiniteTimeSpan
            };

        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "AircraftDataEnhanced",
                "1.0.0"));

        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/json"));
    }

    public async Task<AircraftMetadata> LookupAircraftAsync(
        string icao,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (!AircraftOnlineLookup.TryNormalizeIcao(
                icao,
                out var normalized))
        {
            return AircraftMetadata.Unavailable(
                string.Empty,
                "invalid_icao");
        }

        if (!forceRefresh &&
            TryReadCache(
                _aircraftCache,
                normalized,
                out var cached))
        {
            return cached;
        }

        using var operationTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        operationTimeout.CancelAfter(
            WholeLookupTimeout);

        var token =
            operationTimeout.Token;

        var gateHeld =
            false;

        try
        {
            await _aircraftNetworkGate.WaitAsync(
                    token)
                .ConfigureAwait(
                    false);

            gateHeld =
                true;

            ThrowIfDisposed();

            if (!forceRefresh &&
                TryReadCache(
                    _aircraftCache,
                    normalized,
                    out cached))
            {
                return cached;
            }

            // ADSBdb is tried first because it provides both aircraft and
            // route data and has been responsive on the target installation.
            // A provider-specific timeout must never prevent the fallback.
            var primary =
                await LookupAircraftAdsbDbAsync(
                        normalized,
                        token)
                    .ConfigureAwait(
                        false);

            var value =
                primary;

            if (!primary.Found &&
                !IsDefinitiveNotFound(
                    primary.Status))
            {
                var fallback =
                    await LookupAircraftHexDbAsync(
                            normalized,
                            token)
                        .ConfigureAwait(
                            false);

                value =
                    SelectAircraftResult(
                        normalized,
                        primary,
                        fallback);
            }
            else if (!primary.Found)
            {
                // A not-found result from one provider is not definitive for
                // the combined lookup; check the fallback database as well.
                var fallback =
                    await LookupAircraftHexDbAsync(
                            normalized,
                            token)
                        .ConfigureAwait(
                            false);

                value =
                    SelectAircraftResult(
                        normalized,
                        primary,
                        fallback);
            }

            var lifetime =
                CacheLifetime(
                    value.Found,
                    value.Status,
                    notFoundLifetime:
                        TimeSpan.FromMinutes(30));

            if (value.Found ||
                !forceRefresh)
            {
                WriteCache(
                    _aircraftCache,
                    normalized,
                    value,
                    lifetime);
            }

            return value;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            var unavailable =
                AircraftMetadata.Unavailable(
                    normalized,
                    "whole_lookup_timeout");

            if (!forceRefresh)
            {
                WriteCache(
                    _aircraftCache,
                    normalized,
                    unavailable,
                    TimeSpan.FromMinutes(2));
            }

            return unavailable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var unavailable =
                AircraftMetadata.Unavailable(
                    normalized,
                    "lookup_failed:" +
                    ex.GetType().Name);

            if (!forceRefresh)
            {
                WriteCache(
                    _aircraftCache,
                    normalized,
                    unavailable,
                    TimeSpan.FromMinutes(2));
            }

            return unavailable;
        }
        finally
        {
            if (gateHeld)
            {
                SafeReleaseNetworkGate(
                    _aircraftNetworkGate);
            }
        }
    }

    public async Task<FlightRouteMetadata> LookupRouteAsync(
        string callsign,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
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

        if (!forceRefresh &&
            TryReadCache(
                _routeCache,
                normalized,
                out var cached))
        {
            return cached;
        }

        using var operationTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        operationTimeout.CancelAfter(
            WholeLookupTimeout);

        var token =
            operationTimeout.Token;

        var gateHeld =
            false;

        try
        {
            await _routeNetworkGate.WaitAsync(
                    token)
                .ConfigureAwait(
                    false);

            gateHeld =
                true;

            ThrowIfDisposed();

            if (!forceRefresh &&
                TryReadCache(
                    _routeCache,
                    normalized,
                    out cached))
            {
                return cached;
            }

            var primary =
                await LookupRouteAdsbDbAsync(
                        normalized,
                        token)
                    .ConfigureAwait(
                        false);

            var value =
                primary;

            if (!primary.Found)
            {
                var fallback =
                    await LookupRouteHexDbAsync(
                            normalized,
                            token)
                        .ConfigureAwait(
                            false);

                value =
                    SelectRouteResult(
                        normalized,
                        primary,
                        fallback);
            }

            var lifetime =
                CacheLifetime(
                    value.Found,
                    value.Status,
                    notFoundLifetime:
                        TimeSpan.FromMinutes(15));

            if (value.Found ||
                !forceRefresh)
            {
                WriteCache(
                    _routeCache,
                    normalized,
                    value,
                    lifetime);
            }

            return value;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            var unavailable =
                FlightRouteMetadata.Unavailable(
                    normalized,
                    "whole_lookup_timeout");

            if (!forceRefresh)
            {
                WriteCache(
                    _routeCache,
                    normalized,
                    unavailable,
                    TimeSpan.FromMinutes(2));
            }

            return unavailable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var unavailable =
                FlightRouteMetadata.Unavailable(
                    normalized,
                    "lookup_failed:" +
                    ex.GetType().Name);

            if (!forceRefresh)
            {
                WriteCache(
                    _routeCache,
                    normalized,
                    unavailable,
                    TimeSpan.FromMinutes(2));
            }

            return unavailable;
        }
        finally
        {
            if (gateHeld)
            {
                SafeReleaseNetworkGate(
                    _routeNetworkGate);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed =
            true;

        _client.Dispose();
        _aircraftNetworkGate.Dispose();
        _routeNetworkGate.Dispose();
    }

    private async Task<AircraftMetadata> LookupAircraftAdsbDbAsync(
        string normalized,
        CancellationToken cancellationToken)
    {
        using var providerTimeout =
            CreateProviderTimeout(
                cancellationToken);

        try
        {
            using var response =
                await _client.GetAsync(
                        "https://api.adsbdb.com/v0/aircraft/" +
                        Uri.EscapeDataString(
                            normalized),
                        HttpCompletionOption.ResponseHeadersRead,
                        providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            if (response.StatusCode ==
                HttpStatusCode.NotFound)
            {
                return AircraftMetadata.Unavailable(
                    normalized,
                    "adsbdb_not_found");
            }

            if (!response.IsSuccessStatusCode)
            {
                return AircraftMetadata.Unavailable(
                    normalized,
                    $"adsbdb_http_{(int)response.StatusCode}");
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                        providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            using var document =
                await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken:
                            providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            var root =
                UnwrapObject(
                    document.RootElement,
                    "response",
                    "aircraft");

            var registration =
                ReadString(
                    root,
                    "registration");

            var manufacturer =
                ReadString(
                    root,
                    "manufacturer");

            var type =
                ReadString(
                    root,
                    "type");

            var icaoType =
                ReadString(
                    root,
                    "icao_type");

            var owner =
                ReadString(
                    root,
                    "registered_owner");

            var operatorCode =
                ReadString(
                    root,
                    "registered_owner_operator_flag_code");

            if (registration.Length == 0 &&
                manufacturer.Length == 0 &&
                type.Length == 0 &&
                owner.Length == 0)
            {
                return AircraftMetadata.Unavailable(
                    normalized,
                    "adsbdb_not_found");
            }

            return new AircraftMetadata(
                normalized,
                registration,
                manufacturer,
                type,
                icaoType,
                owner,
                operatorCode,
                true,
                "ok_adsbdb");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return AircraftMetadata.Unavailable(
                normalized,
                "adsbdb_timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AircraftMetadata.Unavailable(
                normalized,
                "adsbdb_failed:" +
                ex.GetType().Name);
        }
    }

    private async Task<AircraftMetadata> LookupAircraftHexDbAsync(
        string normalized,
        CancellationToken cancellationToken)
    {
        using var providerTimeout =
            CreateProviderTimeout(
                cancellationToken);

        try
        {
            using var response =
                await _client.GetAsync(
                        "https://hexdb.io/api/v1/aircraft/" +
                        normalized.ToLowerInvariant(),
                        HttpCompletionOption.ResponseHeadersRead,
                        providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            if (response.StatusCode ==
                HttpStatusCode.NotFound)
            {
                return AircraftMetadata.Unavailable(
                    normalized,
                    "hexdb_not_found");
            }

            if (!response.IsSuccessStatusCode)
            {
                return AircraftMetadata.Unavailable(
                    normalized,
                    $"hexdb_http_{(int)response.StatusCode}");
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                        providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            using var document =
                await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken:
                            providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            var root =
                document.RootElement;

            var registration =
                ReadString(
                    root,
                    "Registration");

            var manufacturer =
                ReadString(
                    root,
                    "Manufacturer");

            var type =
                ReadString(
                    root,
                    "Type");

            var icaoType =
                ReadString(
                    root,
                    "ICAOTypeCode");

            var owner =
                ReadString(
                    root,
                    "RegisteredOwners");

            var operatorCode =
                ReadString(
                    root,
                    "OperatorFlagCode");

            if (registration.Length == 0 &&
                manufacturer.Length == 0 &&
                type.Length == 0 &&
                owner.Length == 0)
            {
                return AircraftMetadata.Unavailable(
                    normalized,
                    "hexdb_not_found");
            }

            return new AircraftMetadata(
                normalized,
                registration,
                manufacturer,
                type,
                icaoType,
                owner,
                operatorCode,
                true,
                "ok_hexdb");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return AircraftMetadata.Unavailable(
                normalized,
                "hexdb_timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AircraftMetadata.Unavailable(
                normalized,
                "hexdb_failed:" +
                ex.GetType().Name);
        }
    }

    private async Task<FlightRouteMetadata> LookupRouteAdsbDbAsync(
        string normalized,
        CancellationToken cancellationToken)
    {
        using var providerTimeout =
            CreateProviderTimeout(
                cancellationToken);

        try
        {
            using var response =
                await _client.GetAsync(
                        "https://api.adsbdb.com/v0/callsign/" +
                        Uri.EscapeDataString(
                            normalized),
                        HttpCompletionOption.ResponseHeadersRead,
                        providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            if (response.StatusCode ==
                HttpStatusCode.NotFound)
            {
                return FlightRouteMetadata.Unavailable(
                    normalized,
                    "adsbdb_not_found");
            }

            if (!response.IsSuccessStatusCode)
            {
                return FlightRouteMetadata.Unavailable(
                    normalized,
                    $"adsbdb_http_{(int)response.StatusCode}");
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                        providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            using var document =
                await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken:
                            providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            var root =
                UnwrapObject(
                    document.RootElement,
                    "response",
                    "flightroute");

            var origin =
                ReadNestedString(
                    root,
                    "origin",
                    "icao_code");

            var midpoint =
                ReadNestedString(
                    root,
                    "midpoint",
                    "icao_code");

            var destination =
                ReadNestedString(
                    root,
                    "destination",
                    "icao_code");

            var route =
                BuildRoute(
                    origin,
                    midpoint,
                    destination);

            return route.Length == 0
                ? FlightRouteMetadata.Unavailable(
                    normalized,
                    "adsbdb_route_empty")
                : new FlightRouteMetadata(
                    normalized,
                    route,
                    true,
                    "ok_adsbdb");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return FlightRouteMetadata.Unavailable(
                normalized,
                "adsbdb_timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FlightRouteMetadata.Unavailable(
                normalized,
                "adsbdb_failed:" +
                ex.GetType().Name);
        }
    }

    private async Task<FlightRouteMetadata> LookupRouteHexDbAsync(
        string normalized,
        CancellationToken cancellationToken)
    {
        using var providerTimeout =
            CreateProviderTimeout(
                cancellationToken);

        try
        {
            using var response =
                await _client.GetAsync(
                        "https://hexdb.io/api/v1/route/icao/" +
                        Uri.EscapeDataString(
                            normalized),
                        HttpCompletionOption.ResponseHeadersRead,
                        providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            if (response.StatusCode ==
                HttpStatusCode.NotFound)
            {
                return FlightRouteMetadata.Unavailable(
                    normalized,
                    "hexdb_not_found");
            }

            if (!response.IsSuccessStatusCode)
            {
                return FlightRouteMetadata.Unavailable(
                    normalized,
                    $"hexdb_http_{(int)response.StatusCode}");
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                        providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            using var document =
                await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken:
                            providerTimeout.Token)
                    .ConfigureAwait(
                        false);

            var route =
                ReadString(
                    document.RootElement,
                    "route");

            return route.Length == 0
                ? FlightRouteMetadata.Unavailable(
                    normalized,
                    "hexdb_route_empty")
                : new FlightRouteMetadata(
                    normalized,
                    route,
                    true,
                    "ok_hexdb");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return FlightRouteMetadata.Unavailable(
                normalized,
                "hexdb_timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FlightRouteMetadata.Unavailable(
                normalized,
                "hexdb_failed:" +
                ex.GetType().Name);
        }
    }

    private static AircraftMetadata SelectAircraftResult(
        string normalized,
        AircraftMetadata primary,
        AircraftMetadata fallback)
    {
        if (primary.Found)
            return primary;

        if (fallback.Found)
            return fallback;

        if (IsDefinitiveNotFound(
                primary.Status) &&
            IsDefinitiveNotFound(
                fallback.Status))
        {
            return AircraftMetadata.Unavailable(
                normalized,
                "not_found");
        }

        return AircraftMetadata.Unavailable(
            normalized,
            CombineStatuses(
                primary.Status,
                fallback.Status));
    }

    private static FlightRouteMetadata SelectRouteResult(
        string normalized,
        FlightRouteMetadata primary,
        FlightRouteMetadata fallback)
    {
        if (primary.Found)
            return primary;

        if (fallback.Found)
            return fallback;

        if (IsDefinitiveNotFound(
                primary.Status) &&
            IsDefinitiveNotFound(
                fallback.Status))
        {
            return FlightRouteMetadata.Unavailable(
                normalized,
                "not_found");
        }

        return FlightRouteMetadata.Unavailable(
            normalized,
            CombineStatuses(
                primary.Status,
                fallback.Status));
    }

    private static TimeSpan CacheLifetime(
        bool found,
        string status,
        TimeSpan notFoundLifetime)
    {
        if (found)
            return TimeSpan.FromHours(24);

        if (status.Contains(
                "429",
                StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMinutes(5);
        }

        if (string.Equals(
                status,
                "not_found",
                StringComparison.OrdinalIgnoreCase))
        {
            return notFoundLifetime;
        }

        // Avoid repeatedly querying providers that are currently slow or
        // unreachable while still allowing an automatic retry later.
        return TimeSpan.FromMinutes(2);
    }

    private static bool IsDefinitiveNotFound(
        string status) =>
        status.EndsWith(
            "_not_found",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            status,
            "not_found",
            StringComparison.OrdinalIgnoreCase);

    private static string CombineStatuses(
        string first,
        string second)
    {
        if (string.IsNullOrWhiteSpace(
                first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(
                second) ||
            string.Equals(
                first,
                second,
                StringComparison.OrdinalIgnoreCase))
        {
            return first;
        }

        return first +
            "+" +
            second;
    }

    private static CancellationTokenSource CreateProviderTimeout(
        CancellationToken cancellationToken)
    {
        var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            ProviderLookupTimeout);

        return timeout;
    }

    private static JsonElement UnwrapObject(
        JsonElement root,
        params string[] propertyPath)
    {
        var current =
            root;

        foreach (var propertyName in propertyPath)
        {
            if (current.ValueKind !=
                    JsonValueKind.Object ||
                !current.TryGetProperty(
                    propertyName,
                    out var next))
            {
                break;
            }

            current =
                next;
        }

        return current;
    }

    private static string ReadNestedString(
        JsonElement root,
        string objectName,
        string propertyName)
    {
        if (root.ValueKind !=
                JsonValueKind.Object ||
            !root.TryGetProperty(
                objectName,
                out var nested) ||
            nested.ValueKind !=
                JsonValueKind.Object)
        {
            return string.Empty;
        }

        return ReadString(
            nested,
            propertyName);
    }

    private static string BuildRoute(
        string origin,
        string midpoint,
        string destination)
    {
        var parts =
            new[]
            {
                origin,
                midpoint,
                destination
            }
            .Where(
                value =>
                    !string.IsNullOrWhiteSpace(
                        value))
            .Select(
                value =>
                    value.Trim()
                        .ToUpperInvariant())
            .ToArray();

        return parts.Length >= 2
            ? string.Join(
                "-",
                parts)
            : string.Empty;
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

    private static void SafeReleaseNetworkGate(
        SemaphoreSlim gate)
    {
        try
        {
            gate.Release();
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
