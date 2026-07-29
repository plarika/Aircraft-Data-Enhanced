// SPDX-License-Identifier: MIT
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed record LocalHistoryStatus(
    bool Ready,
    bool Faulted,
    string State,
    string DatabasePath,
    long StoredMessages,
    long StoredAircraft,
    long WrittenMessages,
    long DuplicateMessages,
    long DroppedWrites,
    int PendingWrites,
    long FileBytes,
    DateTimeOffset? FirstMessage,
    DateTimeOffset? LastMessage,
    string LastError);

internal sealed record LocalHistoryQuery(
    string Search,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Limit = 500);

internal sealed record HistoricalAircraftSnapshot(
    string Icao,
    string Registration,
    string Callsign,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    long MessageCount,
    string LastProtocol,
    string LastDirection,
    string LastGroundStation,
    string LastLabel,
    string LastMessageId,
    string LastText,
    double? LastFrequencyMhz,
    double? BestSignalDb,
    Vdl2Message LatestMessage)
{
    public TimeSpan Duration =>
        LastSeen >= FirstSeen
            ? LastSeen - FirstSeen
            : TimeSpan.Zero;
}

internal sealed class LocalHistoryDatabase : IDisposable
{
    private abstract record HistoryWorkItem;
    private sealed record WriteMessage(Vdl2Message Message) : HistoryWorkItem;
    private sealed record ClearHistory(TaskCompletionSource<bool> Completion) : HistoryWorkItem;
    private sealed record VacuumDatabase(TaskCompletionSource<bool> Completion) : HistoryWorkItem;

    private readonly Channel<HistoryWorkItem> _workQueue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _knownAircraft =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _databaseDirectory;
    private readonly string _databasePath;
    private readonly string _connectionString;

    private int _ready;
    private int _faulted;
    private int _disposed;
    private int _pendingWrites;
    private long _version;
    private long _storedMessages;
    private long _storedAircraft;
    private long _writtenMessages;
    private long _duplicateMessages;
    private long _droppedWrites;
    private long _firstMessageUnixMs = long.MaxValue;
    private long _lastMessageUnixMs = long.MinValue;
    private string _state = "Starting";
    private string _lastError = string.Empty;

    public LocalHistoryDatabase(int queueCapacity = 2048)
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        _databaseDirectory = Path.Combine(localAppData, "AircraftDataEnhanced");
        _databasePath = Path.Combine(_databaseDirectory, "aircraft-history.sqlite3");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };

        _connectionString = builder.ToString();

        _workQueue = Channel.CreateBounded<HistoryWorkItem>(
            new BoundedChannelOptions(Math.Clamp(queueCapacity, 128, 20_000))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

        _worker = Task.Run(WorkerLoopAsync);
    }

    public string DatabaseDirectory => _databaseDirectory;
    public string DatabasePath => _databasePath;
    public long Version => Interlocked.Read(ref _version);
    public bool Ready => Volatile.Read(ref _ready) != 0;

    public bool TryEnqueue(Vdl2Message message)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (!VerifiedAircraftMessagePolicy.TryAccept(
            message,
            out var verifiedMessage,
            out _))
        {
            return false;
        }

        var queued = _workQueue.Writer.TryWrite(
            new WriteMessage(verifiedMessage));

        if (queued)
            Interlocked.Increment(ref _pendingWrites);
        else
            Interlocked.Increment(ref _droppedWrites);

        return queued;
    }

    public Task<IReadOnlyList<Vdl2Message>> QueryMessagesAsync(
        LocalHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!Ready)
            return Task.FromResult<IReadOnlyList<Vdl2Message>>(
                Array.Empty<Vdl2Message>());

        return Task.Run(
            () => QueryMessagesCore(query, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<HistoricalAircraftSnapshot>> QueryAircraftAsync(
        LocalHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!Ready)
            return Task.FromResult<IReadOnlyList<HistoricalAircraftSnapshot>>(
                Array.Empty<HistoricalAircraftSnapshot>());

        return Task.Run(
            () => QueryAircraftCore(query, cancellationToken),
            cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await _workQueue.Writer.WriteAsync(
            new ClearHistory(completion),
            cancellationToken);

        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        await completion.Task;
    }

    public async Task VacuumAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await _workQueue.Writer.WriteAsync(
            new VacuumDatabase(completion),
            cancellationToken);

        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        await completion.Task;
    }

    public LocalHistoryStatus StatusSnapshot()
    {
        long fileBytes = 0;
        try
        {
            if (File.Exists(_databasePath))
                fileBytes = new FileInfo(_databasePath).Length;
        }
        catch
        {
        }

        var firstUnix = Interlocked.Read(ref _firstMessageUnixMs);
        var lastUnix = Interlocked.Read(ref _lastMessageUnixMs);

        return new LocalHistoryStatus(
            Ready,
            Volatile.Read(ref _faulted) != 0,
            _state,
            _databasePath,
            Interlocked.Read(ref _storedMessages),
            Interlocked.Read(ref _storedAircraft),
            Interlocked.Read(ref _writtenMessages),
            Interlocked.Read(ref _duplicateMessages),
            Interlocked.Read(ref _droppedWrites),
            Volatile.Read(ref _pendingWrites),
            fileBytes,
            firstUnix == long.MaxValue
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(firstUnix),
            lastUnix == long.MinValue
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(lastUnix),
            _lastError);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _workQueue.Writer.TryComplete();

        try
        {
            if (!_worker.Wait(TimeSpan.FromSeconds(3)))
            {
                _shutdown.Cancel();
                _worker.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch
        {
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
        _readGate.Dispose();
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            Directory.CreateDirectory(_databaseDirectory);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            ConfigureConnection(connection);
            EnsureSchema(connection);
            LoadCounters(connection);

            _state = "Ready";
            Volatile.Write(ref _ready, 1);
            Interlocked.Increment(ref _version);

            var batch = new List<Vdl2Message>(100);

            while (await _workQueue.Reader.WaitToReadAsync(_shutdown.Token))
            {
                while (_workQueue.Reader.TryRead(out var item))
                {
                    switch (item)
                    {
                        case WriteMessage write:
                            Interlocked.Decrement(ref _pendingWrites);
                            batch.Add(write.Message);
                            if (batch.Count >= 100)
                                FlushBatch(connection, batch);
                            break;

                        case ClearHistory clear:
                            FlushBatch(connection, batch);
                            HandleClear(connection, clear);
                            break;

                        case VacuumDatabase vacuum:
                            FlushBatch(connection, batch);
                            HandleVacuum(connection, vacuum);
                            break;
                    }
                }

                FlushBatch(connection, batch);
            }

            FlushBatch(connection, batch);
            Checkpoint(connection);
            _state = "Closed";
        }
        catch (OperationCanceledException)
        {
            _state = "Stopped";
        }
        catch (Exception ex)
        {
            _lastError = ex.GetType().Name + ": " + ex.Message;
            _state = "Error";
            Volatile.Write(ref _faulted, 1);
        }
        finally
        {
            Volatile.Write(ref _ready, 0);
        }
    }

    private void FlushBatch(
        SqliteConnection connection,
        List<Vdl2Message> batch)
    {
        if (batch.Count == 0)
            return;

        WriteBatch(connection, batch);
        batch.Clear();
    }

    private void WriteBatch(
        SqliteConnection connection,
        IReadOnlyList<Vdl2Message> messages)
    {
        using var transaction = connection.BeginTransaction();

        foreach (var message in messages)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO messages
                (
                    dedup_key, received_unix_ms, protocol, direction, icao,
                    registration, callsign, source, destination, label,
                    message_text, frequency_mhz, signal_db, valid, raw_json,
                    acars_mode, acars_block_id, acars_message_number,
                    acars_message_sequence, acars_acknowledgement,
                    acars_crc_valid, acars_more_blocks, acars_sublabel,
                    acars_message_function
                )
                VALUES
                (
                    $dedup_key, $received_unix_ms, $protocol, $direction, $icao,
                    $registration, $callsign, $source, $destination, $label,
                    $message_text, $frequency_mhz, $signal_db, 1, $raw_json,
                    $acars_mode, $acars_block_id, $acars_message_number,
                    $acars_message_sequence, $acars_acknowledgement,
                    $acars_crc_valid, $acars_more_blocks, $acars_sublabel,
                    $acars_message_function
                )
                RETURNING id;
                """;

            AddMessageParameters(insert, message);
            var scalar = insert.ExecuteScalar();

            if (scalar is null || scalar is DBNull)
            {
                Interlocked.Increment(ref _duplicateMessages);
                continue;
            }

            var messageId = Convert.ToInt64(
                scalar,
                System.Globalization.CultureInfo.InvariantCulture);

            UpsertAircraft(connection, transaction, messageId, message);

            Interlocked.Increment(ref _storedMessages);
            Interlocked.Increment(ref _writtenMessages);

            if (_knownAircraft.TryAdd(message.Icao, 0))
                Interlocked.Increment(ref _storedAircraft);

            UpdateTimeBounds(message.ReceivedAt.ToUnixTimeMilliseconds());
        }

        transaction.Commit();
        Interlocked.Increment(ref _version);
    }

    private static void AddMessageParameters(
        SqliteCommand command,
        Vdl2Message message)
    {
        command.Parameters.AddWithValue("$dedup_key", message.DedupKey);
        command.Parameters.AddWithValue(
            "$received_unix_ms",
            message.ReceivedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$protocol", message.Protocol ?? string.Empty);
        command.Parameters.AddWithValue("$direction", message.Direction ?? string.Empty);
        command.Parameters.AddWithValue("$icao", message.Icao ?? string.Empty);
        command.Parameters.AddWithValue("$registration", message.Registration ?? string.Empty);
        command.Parameters.AddWithValue("$callsign", message.Callsign ?? string.Empty);
        command.Parameters.AddWithValue("$source", message.Source ?? string.Empty);
        command.Parameters.AddWithValue("$destination", message.Destination ?? string.Empty);
        command.Parameters.AddWithValue("$label", message.Label ?? string.Empty);
        command.Parameters.AddWithValue("$message_text", message.Text ?? string.Empty);
        command.Parameters.AddWithValue(
            "$frequency_mhz",
            message.FrequencyMhz is double frequency ? frequency : DBNull.Value);
        command.Parameters.AddWithValue(
            "$signal_db",
            message.SignalDb is double signal ? signal : DBNull.Value);
        command.Parameters.AddWithValue("$raw_json", message.RawJson ?? string.Empty);
        command.Parameters.AddWithValue("$acars_mode", message.AcarsMode ?? string.Empty);
        command.Parameters.AddWithValue("$acars_block_id", message.AcarsBlockId ?? string.Empty);
        command.Parameters.AddWithValue(
            "$acars_message_number",
            message.AcarsMessageNumber ?? string.Empty);
        command.Parameters.AddWithValue(
            "$acars_message_sequence",
            message.AcarsMessageSequence ?? string.Empty);
        command.Parameters.AddWithValue(
            "$acars_acknowledgement",
            message.AcarsAcknowledgement ?? string.Empty);
        command.Parameters.AddWithValue(
            "$acars_crc_valid",
            message.AcarsCrcValid.HasValue
                ? message.AcarsCrcValid.Value ? 1 : 0
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$acars_more_blocks",
            message.AcarsMoreBlocks.HasValue
                ? message.AcarsMoreBlocks.Value ? 1 : 0
                : DBNull.Value);
        command.Parameters.AddWithValue("$acars_sublabel", message.AcarsSublabel ?? string.Empty);
        command.Parameters.AddWithValue(
            "$acars_message_function",
            message.AcarsMessageFunction ?? string.Empty);
    }

    private static void UpsertAircraft(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        Vdl2Message message)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO aircraft
            (
                icao, registration, callsign, first_seen_unix_ms,
                last_seen_unix_ms, message_count, last_protocol,
                last_direction, last_ground_station, last_label,
                last_message_id, last_text, last_frequency_mhz,
                best_signal_db, latest_message_row_id
            )
            VALUES
            (
                $icao, $registration, $callsign, $received,
                $received, 1, $protocol, $direction, $ground_station,
                $label, $message_id, $text, $frequency, $signal,
                $message_row_id
            )
            ON CONFLICT(icao) DO UPDATE SET
                registration = CASE
                    WHEN excluded.registration <> '' THEN excluded.registration
                    ELSE aircraft.registration END,
                callsign = CASE
                    WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                         AND excluded.callsign <> '' THEN excluded.callsign
                    ELSE aircraft.callsign END,
                first_seen_unix_ms = MIN(
                    aircraft.first_seen_unix_ms,
                    excluded.first_seen_unix_ms),
                last_seen_unix_ms = MAX(
                    aircraft.last_seen_unix_ms,
                    excluded.last_seen_unix_ms),
                message_count = aircraft.message_count + 1,
                last_protocol = CASE
                    WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                    THEN excluded.last_protocol ELSE aircraft.last_protocol END,
                last_direction = CASE
                    WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                    THEN excluded.last_direction ELSE aircraft.last_direction END,
                last_ground_station = CASE
                    WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                    THEN excluded.last_ground_station ELSE aircraft.last_ground_station END,
                last_label = CASE
                    WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                    THEN excluded.last_label ELSE aircraft.last_label END,
                last_message_id = CASE
                    WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                    THEN excluded.last_message_id ELSE aircraft.last_message_id END,
                last_text = CASE
                    WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                    THEN excluded.last_text ELSE aircraft.last_text END,
                last_frequency_mhz = CASE
                    WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                    THEN excluded.last_frequency_mhz ELSE aircraft.last_frequency_mhz END,
                best_signal_db = CASE
                    WHEN excluded.best_signal_db IS NULL THEN aircraft.best_signal_db
                    WHEN aircraft.best_signal_db IS NULL
                         OR excluded.best_signal_db > aircraft.best_signal_db
                    THEN excluded.best_signal_db ELSE aircraft.best_signal_db END,
                latest_message_row_id = CASE
                    WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                    THEN excluded.latest_message_row_id
                    ELSE aircraft.latest_message_row_id END;
            """;

        command.Parameters.AddWithValue("$icao", message.Icao);
        command.Parameters.AddWithValue("$registration", message.Registration ?? string.Empty);
        command.Parameters.AddWithValue("$callsign", message.Callsign ?? string.Empty);
        command.Parameters.AddWithValue("$received", message.ReceivedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$protocol", message.Protocol ?? string.Empty);
        command.Parameters.AddWithValue("$direction", message.Direction ?? string.Empty);
        command.Parameters.AddWithValue("$ground_station", ResolveGroundStation(message));
        command.Parameters.AddWithValue("$label", message.Label ?? string.Empty);
        command.Parameters.AddWithValue("$message_id", message.AcarsMessageId);
        command.Parameters.AddWithValue("$text", message.Text ?? string.Empty);
        command.Parameters.AddWithValue(
            "$frequency",
            message.FrequencyMhz is double frequency ? frequency : DBNull.Value);
        command.Parameters.AddWithValue(
            "$signal",
            message.SignalDb is double signal ? signal : DBNull.Value);
        command.Parameters.AddWithValue("$message_row_id", messageId);
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<Vdl2Message> QueryMessagesCore(
        LocalHistoryQuery query,
        CancellationToken cancellationToken)
    {
        _readGate.Wait(cancellationToken);
        try
        {
            using var connection = OpenReadConnection();
            using var command = connection.CreateCommand();
            var where = BuildWhereClause(
                command,
                query,
                "received_unix_ms",
                includeAircraftSearch: false);

            command.CommandText =
                """
                SELECT
                    received_unix_ms, protocol, direction, icao,
                    registration, callsign, source, destination, label,
                    message_text, frequency_mhz, signal_db, valid, raw_json,
                    acars_mode, acars_block_id, acars_message_number,
                    acars_message_sequence, acars_acknowledgement,
                    acars_crc_valid, acars_more_blocks, acars_sublabel,
                    acars_message_function
                FROM messages
                """ +
                where +
                """
                ORDER BY received_unix_ms DESC
                LIMIT $limit;
                """;

            command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));
            using var reader = command.ExecuteReader();
            var result = new List<Vdl2Message>();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ReadMessage(reader));
            }

            return result;
        }
        finally
        {
            _readGate.Release();
        }
    }

    private IReadOnlyList<HistoricalAircraftSnapshot> QueryAircraftCore(
        LocalHistoryQuery query,
        CancellationToken cancellationToken)
    {
        _readGate.Wait(cancellationToken);
        try
        {
            using var connection = OpenReadConnection();
            using var command = connection.CreateCommand();
            var where = BuildWhereClause(
                command,
                query,
                "a.last_seen_unix_ms",
                includeAircraftSearch: true);

            command.CommandText =
                """
                SELECT
                    a.icao, a.registration, a.callsign,
                    a.first_seen_unix_ms, a.last_seen_unix_ms,
                    a.message_count, a.last_protocol, a.last_direction,
                    a.last_ground_station, a.last_label, a.last_message_id,
                    a.last_text, a.last_frequency_mhz, a.best_signal_db,
                    m.received_unix_ms AS m_received_unix_ms,
                    m.protocol AS m_protocol,
                    m.direction AS m_direction,
                    m.icao AS m_icao,
                    m.registration AS m_registration,
                    m.callsign AS m_callsign,
                    m.source AS m_source,
                    m.destination AS m_destination,
                    m.label AS m_label,
                    m.message_text AS m_message_text,
                    m.frequency_mhz AS m_frequency_mhz,
                    m.signal_db AS m_signal_db,
                    m.valid AS m_valid,
                    m.raw_json AS m_raw_json,
                    m.acars_mode AS m_acars_mode,
                    m.acars_block_id AS m_acars_block_id,
                    m.acars_message_number AS m_acars_message_number,
                    m.acars_message_sequence AS m_acars_message_sequence,
                    m.acars_acknowledgement AS m_acars_acknowledgement,
                    m.acars_crc_valid AS m_acars_crc_valid,
                    m.acars_more_blocks AS m_acars_more_blocks,
                    m.acars_sublabel AS m_acars_sublabel,
                    m.acars_message_function AS m_acars_message_function
                FROM aircraft a
                LEFT JOIN messages m ON m.id = a.latest_message_row_id
                """ +
                where +
                """
                ORDER BY a.last_seen_unix_ms DESC
                LIMIT $limit;
                """;

            command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));
            using var reader = command.ExecuteReader();
            var result = new List<HistoricalAircraftSnapshot>();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var icao = ReadString(reader, "icao");
                var firstSeen = DateTimeOffset.FromUnixTimeMilliseconds(
                    reader.GetInt64(reader.GetOrdinal("first_seen_unix_ms")));
                var lastSeen = DateTimeOffset.FromUnixTimeMilliseconds(
                    reader.GetInt64(reader.GetOrdinal("last_seen_unix_ms")));

                Vdl2Message latestMessage;
                if (reader.IsDBNull(reader.GetOrdinal("m_received_unix_ms")))
                {
                    latestMessage = new Vdl2Message(
                        lastSeen,
                        ReadString(reader, "last_protocol"),
                        ReadString(reader, "last_direction"),
                        icao,
                        ReadString(reader, "registration"),
                        ReadString(reader, "callsign"),
                        string.Empty,
                        ReadString(reader, "last_ground_station"),
                        ReadString(reader, "last_label"),
                        ReadString(reader, "last_text"),
                        ReadNullableDouble(reader, "last_frequency_mhz"),
                        ReadNullableDouble(reader, "best_signal_db"),
                        true,
                        string.Empty);
                }
                else
                {
                    latestMessage = ReadMessage(reader, "m_");
                }

                result.Add(new HistoricalAircraftSnapshot(
                    icao,
                    ReadString(reader, "registration"),
                    ReadString(reader, "callsign"),
                    firstSeen,
                    lastSeen,
                    reader.GetInt64(reader.GetOrdinal("message_count")),
                    ReadString(reader, "last_protocol"),
                    ReadString(reader, "last_direction"),
                    ReadString(reader, "last_ground_station"),
                    ReadString(reader, "last_label"),
                    ReadString(reader, "last_message_id"),
                    ReadString(reader, "last_text"),
                    ReadNullableDouble(reader, "last_frequency_mhz"),
                    ReadNullableDouble(reader, "best_signal_db"),
                    latestMessage));
            }

            return result;
        }
        finally
        {
            _readGate.Release();
        }
    }

    private static string BuildWhereClause(
        SqliteCommand command,
        LocalHistoryQuery query,
        string timeColumn,
        bool includeAircraftSearch)
    {
        var clauses = new List<string>();

        if (query.FromUtc.HasValue)
        {
            clauses.Add(timeColumn + " >= $from_utc");
            command.Parameters.AddWithValue(
                "$from_utc",
                query.FromUtc.Value.ToUnixTimeMilliseconds());
        }

        if (query.ToUtc.HasValue)
        {
            clauses.Add(timeColumn + " <= $to_utc");
            command.Parameters.AddWithValue(
                "$to_utc",
                query.ToUtc.Value.ToUnixTimeMilliseconds());
        }

        var search = (query.Search ?? string.Empty).Trim();
        if (search.Length > 0)
        {
            clauses.Add(includeAircraftSearch
                ? "(a.icao LIKE $search OR a.registration LIKE $search " +
                  "OR a.callsign LIKE $search OR a.last_label LIKE $search " +
                  "OR a.last_text LIKE $search OR a.last_ground_station LIKE $search)"
                : "(icao LIKE $search OR registration LIKE $search " +
                  "OR callsign LIKE $search OR label LIKE $search " +
                  "OR message_text LIKE $search OR source LIKE $search " +
                  "OR destination LIKE $search)");
            command.Parameters.AddWithValue("$search", "%" + search + "%");
        }

        return clauses.Count == 0
            ? Environment.NewLine
            : Environment.NewLine + "WHERE " +
              string.Join(" AND ", clauses) + Environment.NewLine;
    }

    private SqliteConnection OpenReadConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=2000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static Vdl2Message ReadMessage(
        SqliteDataReader reader,
        string prefix = "")
    {
        var received = DateTimeOffset.FromUnixTimeMilliseconds(
            reader.GetInt64(reader.GetOrdinal(prefix + "received_unix_ms")));

        return new Vdl2Message(
            received,
            ReadString(reader, prefix + "protocol"),
            ReadString(reader, prefix + "direction"),
            ReadString(reader, prefix + "icao"),
            ReadString(reader, prefix + "registration"),
            ReadString(reader, prefix + "callsign"),
            ReadString(reader, prefix + "source"),
            ReadString(reader, prefix + "destination"),
            ReadString(reader, prefix + "label"),
            ReadString(reader, prefix + "message_text"),
            ReadNullableDouble(reader, prefix + "frequency_mhz"),
            ReadNullableDouble(reader, prefix + "signal_db"),
            ReadInt32(reader, prefix + "valid") != 0,
            ReadString(reader, prefix + "raw_json"),
            ReadString(reader, prefix + "acars_mode"),
            ReadString(reader, prefix + "acars_block_id"),
            ReadString(reader, prefix + "acars_message_number"),
            ReadString(reader, prefix + "acars_message_sequence"),
            ReadString(reader, prefix + "acars_acknowledgement"),
            ReadNullableBoolean(reader, prefix + "acars_crc_valid"),
            ReadNullableBoolean(reader, prefix + "acars_more_blocks"),
            ReadString(reader, prefix + "acars_sublabel"),
            ReadString(reader, prefix + "acars_message_function"));
    }

    private static string ReadString(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static int ReadInt32(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
    }

    private static double? ReadNullableDouble(
        SqliteDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static bool? ReadNullableBoolean(
        SqliteDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal) != 0;
    }

    private void HandleClear(SqliteConnection connection, ClearHistory clear)
    {
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                DELETE FROM aircraft;
                DELETE FROM messages;
                DELETE FROM sqlite_sequence WHERE name = 'messages';
                """;
            command.ExecuteNonQuery();
            transaction.Commit();

            _knownAircraft.Clear();
            Interlocked.Exchange(ref _storedMessages, 0);
            Interlocked.Exchange(ref _storedAircraft, 0);
            Interlocked.Exchange(ref _firstMessageUnixMs, long.MaxValue);
            Interlocked.Exchange(ref _lastMessageUnixMs, long.MinValue);
            Interlocked.Increment(ref _version);
            Checkpoint(connection);
            clear.Completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            clear.Completion.TrySetException(ex);
        }
    }

    private void HandleVacuum(SqliteConnection connection, VacuumDatabase vacuum)
    {
        try
        {
            Checkpoint(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM;";
            command.ExecuteNonQuery();
            Interlocked.Increment(ref _version);
            vacuum.Completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            vacuum.Completion.TrySetException(ex);
        }
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            journal.ExecuteScalar();
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=2000;
            PRAGMA foreign_keys=ON;
            PRAGMA temp_store=MEMORY;
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_info
            (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            INSERT INTO schema_info(key, value)
            VALUES('schema_version', '1')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            CREATE TABLE IF NOT EXISTS messages
            (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                dedup_key TEXT NOT NULL UNIQUE,
                received_unix_ms INTEGER NOT NULL,
                protocol TEXT NOT NULL,
                direction TEXT NOT NULL,
                icao TEXT NOT NULL COLLATE NOCASE,
                registration TEXT NOT NULL,
                callsign TEXT NOT NULL,
                source TEXT NOT NULL,
                destination TEXT NOT NULL,
                label TEXT NOT NULL,
                message_text TEXT NOT NULL,
                frequency_mhz REAL NULL,
                signal_db REAL NULL,
                valid INTEGER NOT NULL,
                raw_json TEXT NOT NULL,
                acars_mode TEXT NOT NULL,
                acars_block_id TEXT NOT NULL,
                acars_message_number TEXT NOT NULL,
                acars_message_sequence TEXT NOT NULL,
                acars_acknowledgement TEXT NOT NULL,
                acars_crc_valid INTEGER NULL,
                acars_more_blocks INTEGER NULL,
                acars_sublabel TEXT NOT NULL,
                acars_message_function TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_messages_received
                ON messages(received_unix_ms DESC);
            CREATE INDEX IF NOT EXISTS ix_messages_icao_received
                ON messages(icao, received_unix_ms DESC);
            CREATE INDEX IF NOT EXISTS ix_messages_callsign
                ON messages(callsign);
            CREATE INDEX IF NOT EXISTS ix_messages_frequency
                ON messages(frequency_mhz);

            CREATE TABLE IF NOT EXISTS aircraft
            (
                icao TEXT PRIMARY KEY COLLATE NOCASE,
                registration TEXT NOT NULL,
                callsign TEXT NOT NULL,
                first_seen_unix_ms INTEGER NOT NULL,
                last_seen_unix_ms INTEGER NOT NULL,
                message_count INTEGER NOT NULL,
                last_protocol TEXT NOT NULL,
                last_direction TEXT NOT NULL,
                last_ground_station TEXT NOT NULL,
                last_label TEXT NOT NULL,
                last_message_id TEXT NOT NULL,
                last_text TEXT NOT NULL,
                last_frequency_mhz REAL NULL,
                best_signal_db REAL NULL,
                latest_message_row_id INTEGER NULL,
                FOREIGN KEY(latest_message_row_id)
                    REFERENCES messages(id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS ix_aircraft_last_seen
                ON aircraft(last_seen_unix_ms DESC);
            CREATE INDEX IF NOT EXISTS ix_aircraft_registration
                ON aircraft(registration);
            CREATE INDEX IF NOT EXISTS ix_aircraft_callsign
                ON aircraft(callsign);
            """;
        command.ExecuteNonQuery();
    }

    private void LoadCounters(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*), MIN(received_unix_ms), MAX(received_unix_ms) FROM messages;";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                Interlocked.Exchange(ref _storedMessages, reader.GetInt64(0));
                if (!reader.IsDBNull(1))
                    Interlocked.Exchange(ref _firstMessageUnixMs, reader.GetInt64(1));
                if (!reader.IsDBNull(2))
                    Interlocked.Exchange(ref _lastMessageUnixMs, reader.GetInt64(2));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT icao FROM aircraft;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                _knownAircraft.TryAdd(reader.GetString(0), 0);
        }

        Interlocked.Exchange(ref _storedAircraft, _knownAircraft.Count);
    }

    private void UpdateTimeBounds(long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _firstMessageUnixMs);
            if (value >= current)
                break;
            if (Interlocked.CompareExchange(ref _firstMessageUnixMs, value, current) == current)
                break;
        }

        while (true)
        {
            var current = Interlocked.Read(ref _lastMessageUnixMs);
            if (value <= current)
                break;
            if (Interlocked.CompareExchange(ref _lastMessageUnixMs, value, current) == current)
                break;
        }
    }

    private static string ResolveGroundStation(Vdl2Message message)
    {
        if (message.Direction.Contains("Air", StringComparison.OrdinalIgnoreCase))
            return message.Destination ?? string.Empty;
        return message.Source ?? string.Empty;
    }

    private static void Checkpoint(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch
        {
        }
    }
}
