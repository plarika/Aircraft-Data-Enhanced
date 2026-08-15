// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal sealed record AircraftSessionSnapshot(
    string Icao,
    string Registration,
    string Callsign,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int MessageCount,
    string LastProtocol,
    string LastDirection,
    string LastGroundStation,
    string LastLabel,
    string LastMessageId,
    string LastText,
    double? LastFrequencyMhz,
    double? BestSignalDb,
    Vdl2Message LatestMessage,
    IReadOnlyList<Vdl2Message> RecentMessages)
{
    public TimeSpan Age(
        DateTimeOffset now) =>
        now >= LastSeen
            ? now - LastSeen
            : TimeSpan.Zero;

    public TimeSpan Duration =>
        LastSeen >= FirstSeen
            ? LastSeen - FirstSeen
            : TimeSpan.Zero;
}

internal sealed class AircraftSessionStore
{
    private sealed class MutableSession
    {
        public required string Icao { get; init; }

        public string Registration { get; set; } =
            string.Empty;

        public string Callsign { get; set; } =
            string.Empty;

        public DateTimeOffset FirstSeen { get; set; }

        public DateTimeOffset LastSeen { get; set; }

        public int MessageCount { get; set; }

        public string LastProtocol { get; set; } =
            string.Empty;

        public string LastDirection { get; set; } =
            string.Empty;

        public string LastGroundStation { get; set; } =
            string.Empty;

        public string LastLabel { get; set; } =
            string.Empty;

        public string LastMessageId { get; set; } =
            string.Empty;

        public string LastText { get; set; } =
            string.Empty;

        public double? LastFrequencyMhz { get; set; }

        public double? BestSignalDb { get; set; }

        public required Vdl2Message LatestMessage { get; set; }

        public LinkedList<Vdl2Message> RecentMessages { get; } =
            new();
    }

    private readonly object _gate =
        new();

    private readonly Dictionary<
        string,
        MutableSession> _sessions =
            new(
                StringComparer.OrdinalIgnoreCase);

    private readonly int _maxSessions;
    private readonly int _maxMessagesPerSession;
    private readonly TimeSpan _retention;
    private long _version;
    private int _addsSincePrune;

    public AircraftSessionStore(
        int maxSessions = 2000,
        int maxMessagesPerSession = 200,
        TimeSpan? retention = null)
    {
        _maxSessions =
            Math.Clamp(
                maxSessions,
                100,
                20_000);

        _maxMessagesPerSession =
            Math.Clamp(
                maxMessagesPerSession,
                10,
                2000);

        _retention =
            retention ??
            TimeSpan.FromHours(24);
    }

    public long Version =>
        Interlocked.Read(
            ref _version);

    public int TotalCount
    {
        get
        {
            lock (_gate)
                return _sessions.Count;
        }
    }

    public bool TryAdd(
        Vdl2Message message)
    {
        ArgumentNullException.ThrowIfNull(
            message);

        if (!AircraftOnlineLookup.TryNormalizeIcao(
            message.Icao,
            out var icao))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(
                icao,
                out var session))
            {
                session =
                    new MutableSession
                    {
                        Icao =
                            icao,
                        FirstSeen =
                            message.ReceivedAt,
                        LastSeen =
                            message.ReceivedAt,
                        LatestMessage =
                            message
                    };

                _sessions.Add(
                    icao,
                    session);
            }

            session.FirstSeen =
                message.ReceivedAt <
                session.FirstSeen
                    ? message.ReceivedAt
                    : session.FirstSeen;

            var isLatest =
                message.ReceivedAt >=
                session.LastSeen;

            session.LastSeen =
                message.ReceivedAt >
                session.LastSeen
                    ? message.ReceivedAt
                    : session.LastSeen;

            session.MessageCount++;

            UpdateIdentity(
                session,
                message);

            if (isLatest)
            {
                session.LatestMessage =
                    message;

                session.LastProtocol =
                    message.Protocol ??
                    string.Empty;

                session.LastDirection =
                    message.Direction ??
                    string.Empty;

                session.LastGroundStation =
                    ResolveGroundStation(
                        message);

                session.LastLabel =
                    message.Label ??
                    string.Empty;

                session.LastMessageId =
                    message.AcarsMessageId;

                session.LastText =
                    message.Text ??
                    string.Empty;

                session.LastFrequencyMhz =
                    message.FrequencyMhz;
            }

            if (message.SignalDb.HasValue &&
                (!session.BestSignalDb.HasValue ||
                 message.SignalDb.Value >
                 session.BestSignalDb.Value))
            {
                session.BestSignalDb =
                    message.SignalDb;
            }

            session.RecentMessages.AddFirst(
                message);

            while (session.RecentMessages.Count >
                   _maxMessagesPerSession)
            {
                session.RecentMessages.RemoveLast();
            }

            _addsSincePrune++;

            if (_addsSincePrune >= 128 ||
                _sessions.Count >
                _maxSessions)
            {
                PruneLocked(
                    DateTimeOffset.Now);

                _addsSincePrune =
                    0;
            }

            Interlocked.Increment(
                ref _version);

            return true;
        }
    }

    public IReadOnlyList<AircraftSessionSnapshot> Snapshot(
        string? filter,
        TimeSpan activeWindow,
        int limit = 500,
        DateTimeOffset? now = null)
    {
        var referenceTime =
            now ??
            DateTimeOffset.Now;

        var normalizedFilter =
            filter?.Trim() ??
            string.Empty;

        lock (_gate)
        {
            PruneLocked(
                referenceTime);

            IEnumerable<MutableSession> query =
                _sessions.Values;

            if (activeWindow !=
                TimeSpan.MaxValue)
            {
                var cutoff =
                    referenceTime -
                    activeWindow;

                query =
                    query.Where(
                        session =>
                            session.LastSeen >=
                            cutoff);
            }

            if (normalizedFilter.Length > 0)
            {
                query =
                    query.Where(
                        session =>
                            Contains(
                                session.Icao,
                                normalizedFilter) ||
                            Contains(
                                session.Registration,
                                normalizedFilter) ||
                            Contains(
                                session.Callsign,
                                normalizedFilter) ||
                            Contains(
                                session.LastProtocol,
                                normalizedFilter) ||
                            Contains(
                                session.LastDirection,
                                normalizedFilter) ||
                            Contains(
                                session.LastGroundStation,
                                normalizedFilter) ||
                            Contains(
                                session.LastLabel,
                                normalizedFilter) ||
                            Contains(
                                session.LastMessageId,
                                normalizedFilter) ||
                            Contains(
                                session.LastText,
                                normalizedFilter));
            }

            return query
                .OrderByDescending(
                    session =>
                        session.LastSeen)
                .Take(
                    Math.Clamp(
                        limit,
                        1,
                        5000))
                .Select(
                    CreateSnapshot)
                .ToArray();
        }
    }

    public int ActiveCount(
        TimeSpan activeWindow,
        DateTimeOffset? now = null)
    {
        var referenceTime =
            now ??
            DateTimeOffset.Now;

        lock (_gate)
        {
            PruneLocked(
                referenceTime);

            if (activeWindow ==
                TimeSpan.MaxValue)
            {
                return _sessions.Count;
            }

            var cutoff =
                referenceTime -
                activeWindow;

            return _sessions.Values.Count(
                session =>
                    session.LastSeen >=
                    cutoff);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sessions.Clear();
            _addsSincePrune =
                0;

            Interlocked.Increment(
                ref _version);
        }
    }

    private AircraftSessionSnapshot CreateSnapshot(
        MutableSession session) =>
        new(
            session.Icao,
            session.Registration,
            session.Callsign,
            session.FirstSeen,
            session.LastSeen,
            session.MessageCount,
            session.LastProtocol,
            session.LastDirection,
            session.LastGroundStation,
            session.LastLabel,
            session.LastMessageId,
            session.LastText,
            session.LastFrequencyMhz,
            session.BestSignalDb,
            session.LatestMessage,
            session.RecentMessages.ToArray());

    private void PruneLocked(
        DateTimeOffset now)
    {
        var cutoff =
            now -
            _retention;

        foreach (var icao in _sessions
            .Where(
                pair =>
                    pair.Value.LastSeen <
                    cutoff)
            .Select(
                pair =>
                    pair.Key)
            .ToArray())
        {
            _sessions.Remove(
                icao);
        }

        if (_sessions.Count <=
            _maxSessions)
        {
            return;
        }

        foreach (var icao in _sessions.Values
            .OrderBy(
                session =>
                    session.LastSeen)
            .Take(
                _sessions.Count -
                _maxSessions)
            .Select(
                session =>
                    session.Icao)
            .ToArray())
        {
            _sessions.Remove(
                icao);
        }
    }

    private static void UpdateIdentity(
        MutableSession session,
        Vdl2Message message)
    {
        if (!string.IsNullOrWhiteSpace(
            message.Registration))
        {
            session.Registration =
                message.Registration.Trim();
        }

        if (!string.IsNullOrWhiteSpace(
            message.Callsign))
        {
            session.Callsign =
                message.Callsign.Trim();
        }
    }

    private static string ResolveGroundStation(
        Vdl2Message message)
    {
        if (message.Destination?.StartsWith(
                "GS:",
                StringComparison.OrdinalIgnoreCase) ==
            true)
        {
            return message.Destination;
        }

        if (message.Source?.StartsWith(
                "GS:",
                StringComparison.OrdinalIgnoreCase) ==
            true)
        {
            return message.Source;
        }

        return
            string.Equals(
                message.Direction,
                "Air → Ground",
                StringComparison.OrdinalIgnoreCase)
                ? message.Destination ??
                  string.Empty
                : message.Source ??
                  string.Empty;
    }

    private static bool Contains(
        string? value,
        string filter) =>
        value?.Contains(
            filter,
            StringComparison.OrdinalIgnoreCase) ==
        true;
}
