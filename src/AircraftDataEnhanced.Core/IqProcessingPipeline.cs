// SPDX-License-Identifier: MIT
using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;

namespace SDRSharp.AircraftDataEnhanced;

internal readonly record struct IqPipelineSnapshot(
    string State,
    int Capacity,
    int Pending,
    int PeakPending,
    long ReceivedBlocks,
    long ProcessedBlocks,
    long DroppedBlocks,
    long FaultedBlocks,
    long RentedBuffers,
    long ReturnedBuffers,
    double AverageQueueLatencyMs,
    double MaximumQueueLatencyMs,
    double AverageProcessingMs,
    double MaximumProcessingMs,
    string LastError);

internal sealed class IqProcessingPipeline<TSample> : IDisposable
    where TSample : unmanaged
{
    private readonly record struct QueuedBlock(
        TSample[] Buffer,
        int Length,
        double SampleRate,
        long EnqueuedTimestamp);

    private readonly Channel<QueuedBlock> _channel;
    private readonly Action<TSample[], int, double> _processor;
    private readonly ArrayPool<TSample> _pool;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private readonly object _statusGate = new();
    private readonly int _capacity;
    private readonly TimeSpan _drainTimeout;

    private int _accepting = 1;
    private int _disposed;
    private int _pending;
    private int _peakPending;
    private long _receivedBlocks;
    private long _processedBlocks;
    private long _droppedBlocks;
    private long _faultedBlocks;
    private long _rentedBuffers;
    private long _returnedBuffers;
    private long _queueLatencyTicks;
    private long _maximumQueueLatencyTicks;
    private long _processingTicks;
    private long _maximumProcessingTicks;
    private long _lastDropTimestamp;
    private long _lastFaultTimestamp;
    private string _lastError = string.Empty;

    public IqProcessingPipeline(
        Action<TSample[], int, double> processor,
        int capacity = 8,
        ArrayPool<TSample>? pool = null,
        TimeSpan? drainTimeout = null)
    {
        _processor =
            processor ??
            throw new ArgumentNullException(
                nameof(processor));

        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity));
        }

        _capacity =
            capacity;

        _pool =
            pool ??
            ArrayPool<TSample>.Shared;

        _drainTimeout =
            drainTimeout ??
            TimeSpan.FromSeconds(5);

        _channel =
            Channel.CreateBounded<QueuedBlock>(
                new BoundedChannelOptions(
                    capacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode =
                        BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations =
                        false
                });

        _worker =
            Task.Run(
                WorkerLoopAsync);
    }

    public unsafe bool TryEnqueue(
        TSample* source,
        int length,
        double sampleRate)
    {
        if (source is null ||
            length <= 0 ||
            sampleRate <= 0 ||
            Volatile.Read(
                ref _accepting) == 0)
        {
            return false;
        }

        Interlocked.Increment(
            ref _receivedBlocks);

        TSample[]? buffer =
            null;

        try
        {
            buffer =
                _pool.Rent(
                    length);

            Interlocked.Increment(
                ref _rentedBuffers);

            fixed (TSample* destination =
                   buffer)
            {
                var byteCount =
                    checked(
                        (long)length *
                        sizeof(TSample));

                Buffer.MemoryCopy(
                    source,
                    destination,
                    checked(
                        (long)buffer.Length *
                        sizeof(TSample)),
                    byteCount);
            }

            return TryQueue(
                buffer,
                length,
                sampleRate);
        }
        catch (Exception ex)
        {
            if (buffer is not null)
            {
                ReturnBuffer(
                    buffer);
            }

            RecordFault(
                ex);

            return false;
        }
    }

    public bool TryEnqueue(
        ReadOnlySpan<TSample> samples,
        double sampleRate)
    {
        if (samples.IsEmpty ||
            sampleRate <= 0 ||
            Volatile.Read(
                ref _accepting) == 0)
        {
            return false;
        }

        Interlocked.Increment(
            ref _receivedBlocks);

        TSample[]? buffer =
            null;

        try
        {
            buffer =
                _pool.Rent(
                    samples.Length);

            Interlocked.Increment(
                ref _rentedBuffers);

            samples.CopyTo(
                buffer);

            return TryQueue(
                buffer,
                samples.Length,
                sampleRate);
        }
        catch (Exception ex)
        {
            if (buffer is not null)
            {
                ReturnBuffer(
                    buffer);
            }

            RecordFault(
                ex);

            return false;
        }
    }

    public IqPipelineSnapshot Snapshot()
    {
        var pending =
            Math.Max(
                0,
                Volatile.Read(
                    ref _pending));

        var peakPending =
            Math.Max(
                pending,
                Volatile.Read(
                    ref _peakPending));

        var processed =
            Interlocked.Read(
                ref _processedBlocks);

        var faulted =
            Interlocked.Read(
                ref _faultedBlocks);

        var measured =
            Math.Max(
                1,
                processed +
                faulted);

        var queueLatencyTicks =
            Interlocked.Read(
                ref _queueLatencyTicks);

        var processingTicks =
            Interlocked.Read(
                ref _processingTicks);

        var now =
            Stopwatch.GetTimestamp();

        var recentDrop =
            IsRecent(
                now,
                Interlocked.Read(
                    ref _lastDropTimestamp),
                TimeSpan.FromSeconds(
                    5));

        var recentFault =
            IsRecent(
                now,
                Interlocked.Read(
                    ref _lastFaultTimestamp),
                TimeSpan.FromSeconds(
                    30));

        string lastError;

        lock (_statusGate)
        {
            lastError =
                _lastError;
        }

        string state;

        if (Volatile.Read(
                ref _disposed) != 0)
        {
            state =
                "Stopped";
        }
        else if (recentDrop)
        {
            state =
                "Overloaded";
        }
        else if (recentFault ||
                 pending >
                 Math.Max(
                     1,
                     _capacity / 2))
        {
            state =
                "Degraded";
        }
        else
        {
            state =
                "Healthy";
        }

        return new IqPipelineSnapshot(
            state,
            _capacity,
            pending,
            peakPending,
            Interlocked.Read(
                ref _receivedBlocks),
            processed,
            Interlocked.Read(
                ref _droppedBlocks),
            faulted,
            Interlocked.Read(
                ref _rentedBuffers),
            Interlocked.Read(
                ref _returnedBuffers),
            TicksToMilliseconds(
                queueLatencyTicks) /
            measured,
            TicksToMilliseconds(
                Interlocked.Read(
                    ref _maximumQueueLatencyTicks)),
            TicksToMilliseconds(
                processingTicks) /
            measured,
            TicksToMilliseconds(
                Interlocked.Read(
                    ref _maximumProcessingTicks)),
            lastError);
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
            ref _accepting,
            0);

        _channel.Writer.TryComplete();

        var completed =
            false;

        try
        {
            completed =
                _worker.Wait(
                    _drainTimeout);
        }
        catch (AggregateException ex)
        {
            RecordFault(
                ex.Flatten());
        }

        if (!completed)
        {
            _cancellation.Cancel();

            try
            {
                completed =
                    _worker.Wait(
                        TimeSpan.FromSeconds(
                            1));
            }
            catch (AggregateException ex)
            {
                RecordFault(
                    ex.Flatten());
            }
        }

        if (!completed)
        {
            RecordFault(
                new TimeoutException(
                    "The IQ worker did not stop within the shutdown timeout."));
        }

        if (_worker.IsCompleted)
        {
            _cancellation.Dispose();
        }
    }

    private bool TryQueue(
        TSample[] buffer,
        int length,
        double sampleRate)
    {
        var pending =
            Interlocked.Increment(
                ref _pending);

        var block =
            new QueuedBlock(
                buffer,
                length,
                sampleRate,
                Stopwatch.GetTimestamp());

        if (_channel.Writer.TryWrite(
                block))
        {
            UpdateMaximum(
                ref _peakPending,
                pending);

            return true;
        }

        Interlocked.Decrement(
            ref _pending);

        Interlocked.Increment(
            ref _droppedBlocks);

        Interlocked.Exchange(
            ref _lastDropTimestamp,
            Stopwatch.GetTimestamp());

        ReturnBuffer(
            buffer);

        return false;
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (await _channel.Reader
                       .WaitToReadAsync(
                           _cancellation.Token)
                       .ConfigureAwait(
                           false))
            {
                while (_channel.Reader.TryRead(
                           out var block))
                {
                    ProcessBlock(
                        block);
                }
            }
        }
        catch (OperationCanceledException)
            when (_cancellation.IsCancellationRequested)
        {
            // Shutdown timeout requested cancellation.
        }
        catch (Exception ex)
        {
            RecordFault(
                ex);
        }
        finally
        {
            while (_channel.Reader.TryRead(
                       out var remaining))
            {
                Interlocked.Decrement(
                    ref _pending);

                Interlocked.Increment(
                    ref _droppedBlocks);

                ReturnBuffer(
                    remaining.Buffer);
            }
        }
    }

    private void ProcessBlock(
        QueuedBlock block)
    {
        Interlocked.Decrement(
            ref _pending);

        var started =
            Stopwatch.GetTimestamp();

        var queueLatency =
            Math.Max(
                0,
                started -
                block.EnqueuedTimestamp);

        Interlocked.Add(
            ref _queueLatencyTicks,
            queueLatency);

        UpdateMaximum(
            ref _maximumQueueLatencyTicks,
            queueLatency);

        try
        {
            _processor(
                block.Buffer,
                block.Length,
                block.SampleRate);

            Interlocked.Increment(
                ref _processedBlocks);
        }
        catch (Exception ex)
        {
            RecordFault(
                ex);
        }
        finally
        {
            var processingTicks =
                Math.Max(
                    0,
                    Stopwatch.GetTimestamp() -
                    started);

            Interlocked.Add(
                ref _processingTicks,
                processingTicks);

            UpdateMaximum(
                ref _maximumProcessingTicks,
                processingTicks);

            ReturnBuffer(
                block.Buffer);
        }
    }

    private void RecordFault(
        Exception exception)
    {
        Interlocked.Increment(
            ref _faultedBlocks);

        Interlocked.Exchange(
            ref _lastFaultTimestamp,
            Stopwatch.GetTimestamp());

        lock (_statusGate)
        {
            _lastError =
                exception.GetType().Name +
                ": " +
                exception.Message;
        }
    }

    private void ReturnBuffer(
        TSample[] buffer)
    {
        try
        {
            _pool.Return(
                buffer,
                clearArray: false);
        }
        finally
        {
            Interlocked.Increment(
                ref _returnedBuffers);
        }
    }

    private static bool IsRecent(
        long now,
        long timestamp,
        TimeSpan window)
    {
        if (timestamp <= 0)
            return false;

        var elapsed =
            now -
            timestamp;

        return elapsed >= 0 &&
               elapsed <=
               window.TotalSeconds *
               Stopwatch.Frequency;
    }

    private static double TicksToMilliseconds(
        long ticks)
    {
        return ticks <= 0
            ? 0
            : ticks *
              1000.0 /
              Stopwatch.Frequency;
    }

    private static void UpdateMaximum(
        ref int target,
        int candidate)
    {
        var current =
            Volatile.Read(
                ref target);

        while (candidate > current)
        {
            var observed =
                Interlocked.CompareExchange(
                    ref target,
                    candidate,
                    current);

            if (observed == current)
                return;

            current =
                observed;
        }
    }

    private static void UpdateMaximum(
        ref long target,
        long candidate)
    {
        var current =
            Interlocked.Read(
                ref target);

        while (candidate > current)
        {
            var observed =
                Interlocked.CompareExchange(
                    ref target,
                    candidate,
                    current);

            if (observed == current)
                return;

            current =
                observed;
        }
    }
}
