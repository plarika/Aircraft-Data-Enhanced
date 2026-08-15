// SPDX-License-Identifier: MIT
using System.Text;
using System.Threading.Channels;

namespace SDRSharp.AircraftDataEnhanced;

internal readonly record struct JsonlExporterSnapshot(
    bool Enabled,
    string Path,
    int PendingWrites,
    long WrittenRecords,
    long DroppedRecords,
    bool Faulted,
    string LastError);

/// <summary>
/// Bounded, single-writer JSONL exporter. The SDR IQ callback only performs a
/// non-blocking Channel.TryWrite; all filesystem I/O runs on the worker.
/// </summary>
internal sealed class JsonlExporter : IDisposable
{
    private const int DefaultQueueCapacity =
        1024;

    private const int MaximumRecordCharacters =
        256 * 1024;

    private const int MaximumBatchSize =
        128;

    private readonly object _gate =
        new();

    private readonly Channel<string> _queue;

    private readonly CancellationTokenSource
        _shutdown =
            new();

    private readonly Task _worker;

    private StreamWriter? _writer;

    private string? _path;

    private int _enabled;

    private int _disposed;

    private int _pendingWrites;

    private int _faulted;

    private long _writtenRecords;

    private long _droppedRecords;

    private string _lastError =
        string.Empty;

    public JsonlExporter(
        int queueCapacity =
            DefaultQueueCapacity)
    {
        _queue =
            Channel.CreateBounded<string>(
                new BoundedChannelOptions(
                    Math.Clamp(
                        queueCapacity,
                        128,
                        20_000))
                {
                    SingleReader =
                        true,
                    SingleWriter =
                        false,

                    // Wait mode makes TryWrite return false when full without
                    // ever blocking the IQ callback.
                    FullMode =
                        BoundedChannelFullMode.Wait,

                    AllowSynchronousContinuations =
                        false
                });

        _worker =
            Task.Run(
                WorkerAsync);
    }

    public bool Enabled =>
        Volatile.Read(
            ref _enabled) != 0;

    public string Path
    {
        get
        {
            lock (_gate)
                return _path ??
                    string.Empty;
        }
    }

    public void Enable(
        string path)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(
                ref _disposed) != 0,
            this);

        ArgumentException.ThrowIfNullOrWhiteSpace(
            path);

        var fullPath =
            System.IO.Path.GetFullPath(
                path);

        var directory =
            System.IO.Path.GetDirectoryName(
                fullPath);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        Disable();

        var stream =
            new FileStream(
                fullPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);

        var writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier:
                        false),
                64 * 1024,
                leaveOpen:
                    false)
            {
                AutoFlush =
                    false
            };

        lock (_gate)
        {
            if (Volatile.Read(
                    ref _disposed) != 0)
            {
                writer.Dispose();

                throw new ObjectDisposedException(
                    nameof(JsonlExporter));
            }

            _writer =
                writer;

            _path =
                fullPath;

            _lastError =
                string.Empty;

            Volatile.Write(
                ref _faulted,
                0);

            Volatile.Write(
                ref _enabled,
                1);
        }
    }

    public bool TryWrite(
        Vdl2Message message)
    {
        ArgumentNullException.ThrowIfNull(
            message);

        if (Volatile.Read(
                ref _disposed) != 0 ||
            Volatile.Read(
                ref _enabled) == 0)
        {
            return false;
        }

        var record =
            message.RawJson;

        if (string.IsNullOrWhiteSpace(
                record) ||
            record.Length >
                MaximumRecordCharacters)
        {
            Interlocked.Increment(
                ref _droppedRecords);

            return false;
        }

        Interlocked.Increment(
            ref _pendingWrites);

        if (_queue.Writer.TryWrite(
                record))
        {
            return true;
        }

        Interlocked.Decrement(
            ref _pendingWrites);

        Interlocked.Increment(
            ref _droppedRecords);

        return false;
    }

    public JsonlExporterSnapshot StatusSnapshot()
    {
        lock (_gate)
        {
            return new JsonlExporterSnapshot(
                Enabled,
                _path ??
                    string.Empty,
                Math.Max(
                    0,
                    Volatile.Read(
                        ref _pendingWrites)),
                Interlocked.Read(
                    ref _writtenRecords),
                Interlocked.Read(
                    ref _droppedRecords),
                Volatile.Read(
                    ref _faulted) != 0,
                _lastError);
        }
    }

    public void Disable()
    {
        Volatile.Write(
            ref _enabled,
            0);

        var deadline =
            Environment.TickCount64 +
            2_000;

        while (Volatile.Read(
                   ref _pendingWrites) > 0 &&
               Environment.TickCount64 <
                   deadline)
        {
            Thread.Sleep(
                10);
        }

        lock (_gate)
            DisableInternal();
    }

    private async Task WorkerAsync()
    {
        var token =
            _shutdown.Token;

        try
        {
            while (await _queue.Reader
                       .WaitToReadAsync(
                           token)
                       .ConfigureAwait(
                           false))
            {
                var batch =
                    new List<string>(
                        MaximumBatchSize);

                while (batch.Count <
                           MaximumBatchSize &&
                       _queue.Reader.TryRead(
                           out var record))
                {
                    batch.Add(
                        record);

                    Interlocked.Decrement(
                        ref _pendingWrites);
                }

                if (batch.Count == 0)
                    continue;

                PersistBatch(
                    batch);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            while (_queue.Reader.TryRead(
                       out _))
            {
                Interlocked.Decrement(
                    ref _pendingWrites);

                Interlocked.Increment(
                    ref _droppedRecords);
            }
        }
    }

    private void PersistBatch(
        IReadOnlyList<string> batch)
    {
        lock (_gate)
        {
            if (_writer is null)
            {
                Interlocked.Add(
                    ref _droppedRecords,
                    batch.Count);

                return;
            }

            try
            {
                foreach (var record in
                         batch)
                {
                    _writer.WriteLine(
                        record);
                }

                _writer.Flush();

                Interlocked.Add(
                    ref _writtenRecords,
                    batch.Count);
            }
            catch (Exception ex)
            {
                Interlocked.Add(
                    ref _droppedRecords,
                    batch.Count);

                _lastError =
                    ex.GetType().Name +
                    ": " +
                    ex.Message;

                Volatile.Write(
                    ref _faulted,
                    1);

                Volatile.Write(
                    ref _enabled,
                    0);

                DisableInternal();
            }
        }
    }

    private void DisableInternal()
    {
        Volatile.Write(
            ref _enabled,
            0);

        try
        {
            _writer?.Flush();
        }
        catch (Exception ex)
        {
            _lastError =
                ex.GetType().Name +
                ": " +
                ex.Message;

            Volatile.Write(
                ref _faulted,
                1);
        }

        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            _lastError =
                ex.GetType().Name +
                ": " +
                ex.Message;

            Volatile.Write(
                ref _faulted,
                1);
        }

        _writer =
            null;

        _path =
            null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }

        Volatile.Write(
            ref _enabled,
            0);

        _queue.Writer.TryComplete();

        var stopped =
            false;

        try
        {
            stopped =
                _worker.Wait(
                    TimeSpan.FromSeconds(
                        5));
        }
        catch
        {
        }

        if (!stopped)
        {
            _shutdown.Cancel();

            try
            {
                _worker.Wait(
                    TimeSpan.FromSeconds(
                        1));
            }
            catch
            {
            }
        }

        lock (_gate)
            DisableInternal();

        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
