// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed class MessageStore
{
    private readonly object _gate = new();
    private readonly LinkedList<Vdl2Message> _messages = new();
    private readonly Dictionary<string, DateTimeOffset> _dedup = new(StringComparer.Ordinal);
    private readonly int _maxMessages;
    private readonly TimeSpan _dedupWindow;
    private readonly DecoderStats _stats;
    private long _version;

    public MessageStore(DecoderStats stats, int maxMessages = 5000, TimeSpan? dedupWindow = null)
    {
        _stats = stats;
        _maxMessages = Math.Clamp(maxMessages, 100, 100_000);
        _dedupWindow = dedupWindow ?? TimeSpan.FromSeconds(3);
    }

    public long Version => Interlocked.Read(ref _version);

    public int Count
    {
        get
        {
            lock (_gate)
                return _messages.Count;
        }
    }

    public bool TryAdd(Vdl2Message message)
    {
        if (!VerifiedAircraftMessagePolicy.TryAccept(
            message,
            out message,
            out _))
        {
            return false;
        }

        lock (_gate)
        {
            PruneDedup(message.ReceivedAt);
            if (_dedup.TryGetValue(message.DedupKey, out var previous) &&
                message.ReceivedAt - previous <= _dedupWindow)
            {
                _stats.OnDuplicate();
                return false;
            }

            _dedup[message.DedupKey] = message.ReceivedAt;
            _messages.AddFirst(message);
            while (_messages.Count > _maxMessages)
                _messages.RemoveLast();

            Interlocked.Increment(ref _version);
            return true;
        }
    }

    public IReadOnlyList<Vdl2Message> Snapshot(string filter, int limit = 1000)
    {
        filter = filter?.Trim() ?? string.Empty;
        lock (_gate)
        {
            IEnumerable<Vdl2Message> query = _messages;
            if (filter.Length > 0)
            {
                query = query.Where(m =>
                    Contains(m.Icao, filter) ||
                    Contains(m.Registration, filter) ||
                    Contains(m.Callsign, filter) ||
                    Contains(m.Source, filter) ||
                    Contains(m.Destination, filter) ||
                    Contains(m.Label, filter) ||
                    Contains(m.Text, filter));
            }
            return query.Take(Math.Clamp(limit, 1, 10_000)).ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
            _dedup.Clear();
            Interlocked.Increment(ref _version);
        }
    }

    private void PruneDedup(DateTimeOffset now)
    {
        if (_dedup.Count < 4096)
            return;
        var cutoff = now - _dedupWindow - TimeSpan.FromSeconds(1);
        foreach (var key in _dedup.Where(x => x.Value < cutoff).Select(x => x.Key).ToArray())
            _dedup.Remove(key);
    }

    private static bool Contains(string value, string filter) =>
        value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
}
