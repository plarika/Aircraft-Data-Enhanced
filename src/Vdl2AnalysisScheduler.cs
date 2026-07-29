// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal sealed record Vdl2AnalysisRequest(
    CaptureInfo Capture,
    bool Automatic,
    bool DiagnosticMode);

internal sealed record Vdl2AnalysisCompletion(
    Vdl2AnalysisRequest Request,
    CaptureInfo EffectiveCapture,
    bool Salvaged,
    int BurstIndex,
    int BurstCount,
    D8pskAnalysisResult Result);

internal sealed record Vdl2AnalysisBatchSummary(
    Vdl2AnalysisRequest Request,
    int BoundedBursts,
    int AnalysedBursts,
    int SuccessfulAnalyses,
    int ValidAvlcFrames,
    int InvalidFcsFrames,
    int PublishedAircraftCandidates,
    bool SalvageApplied,
    string Status);

internal readonly record struct Vdl2AnalysisQueueSnapshot(
    bool Busy,
    int Pending,
    long Enqueued,
    long Completed,
    long Dropped,
    long SalvageBatches,
    string ActiveCaptureId);

internal sealed class Vdl2AnalysisScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly LinkedList<Vdl2AnalysisRequest> _queue = new();
    private readonly HashSet<string> _queuedIds =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly D8pskSymbolAnalyzer _analyzer;
    private readonly Vdl2CaptureSalvager _salvager;
    private readonly Task _worker;
    private readonly int _capacity;

    private volatile bool _disposed;
    private bool _busy;
    private string _activeCaptureId = string.Empty;
    private long _enqueued;
    private long _completed;
    private long _dropped;
    private long _salvageBatches;

    public event Action<Vdl2AnalysisRequest>? AnalysisStarted;
    public event Action<Vdl2AnalysisCompletion>? AnalysisCompleted;
    public event Action<Vdl2AnalysisBatchSummary>? BatchCompleted;
    public event Action<Vdl2AnalysisRequest, string>? AnalysisDropped;

    public Vdl2AnalysisScheduler(
        D8pskSymbolAnalyzer analyzer,
        int capacity = 8)
    {
        _analyzer =
            analyzer ??
            throw new ArgumentNullException(
                nameof(analyzer));

        _capacity =
            Math.Clamp(
                capacity,
                2,
                32);

        _salvager =
            new Vdl2CaptureSalvager(
                analyzer.AnalysisDirectory);

        _worker =
            Task.Run(
                WorkerAsync);
    }

    public bool Enqueue(
        CaptureInfo capture,
        bool automatic,
        bool diagnosticMode)
    {
        var request =
            new Vdl2AnalysisRequest(
                capture,
                automatic,
                diagnosticMode);

        Vdl2AnalysisRequest? dropped =
            null;

        string? dropReason =
            null;

        lock (_gate)
        {
            if (_disposed)
            {
                dropReason =
                    "scheduler_disposed";
            }
            else if (
                string.Equals(
                    _activeCaptureId,
                    capture.Id,
                    StringComparison.Ordinal) ||
                _queuedIds.Contains(
                    capture.Id))
            {
                dropReason =
                    "capture_already_queued";
            }
            else if (
                _queue.Count >=
                _capacity)
            {
                if (automatic)
                {
                    dropReason =
                        "analysis_queue_full";
                }
                else
                {
                    var node =
                        _queue.First;

                    while (
                        node is not null &&
                        !node.Value.Automatic)
                    {
                        node =
                            node.Next;
                    }

                    node ??=
                        _queue.First;

                    if (node is not null)
                    {
                        dropped =
                            node.Value;

                        _queuedIds.Remove(
                            node.Value.Capture.Id);

                        _queue.Remove(
                            node);

                        _dropped++;
                    }
                }
            }

            if (dropReason is null)
            {
                _queue.AddLast(
                    request);

                _queuedIds.Add(
                    capture.Id);

                _enqueued++;
            }
            else
            {
                _dropped++;
            }
        }

        if (dropped is not null)
        {
            SafeInvoke(
                AnalysisDropped,
                dropped,
                "manual_request_replaced_queued_analysis");
        }

        if (dropReason is not null)
        {
            SafeInvoke(
                AnalysisDropped,
                request,
                dropReason);

            return false;
        }

        _signal.Release();
        return true;
    }

    public Vdl2AnalysisQueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new Vdl2AnalysisQueueSnapshot(
                _busy,
                _queue.Count,
                _enqueued,
                _completed,
                _dropped,
                _salvageBatches,
                _activeCaptureId);
        }
    }

    private async Task WorkerAsync()
    {
        var token =
            _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal
                    .WaitAsync(
                        token)
                    .ConfigureAwait(
                        false);
            }
            catch (
                OperationCanceledException)
            {
                break;
            }

            Vdl2AnalysisRequest? request =
                null;

            lock (_gate)
            {
                if (_queue.First is not null)
                {
                    request =
                        _queue.First.Value;

                    _queue.RemoveFirst();

                    _queuedIds.Remove(
                        request.Capture.Id);

                    _busy = true;

                    _activeCaptureId =
                        request.Capture.Id;
                }
            }

            if (request is null)
                continue;

            SafeInvoke(
                AnalysisStarted,
                request);

            try
            {
                await ProcessRequestAsync(
                    request,
                    token)
                    .ConfigureAwait(
                        false);
            }
            catch (
                OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var failed =
                    new D8pskAnalysisResult(
                        request.Capture.Id,
                        DateTimeOffset.Now,
                        false,
                        false,
                        request.DiagnosticMode,
                        10_500.0,
                        request.Capture.SampleRate /
                            10_500.0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        "analysis_scheduler_failed",
                        ex.ToString());

                SafeInvoke(
                    AnalysisCompleted,
                    new Vdl2AnalysisCompletion(
                        request,
                        request.Capture,
                        false,
                        0,
                        1,
                        failed));

                SafeInvoke(
                    BatchCompleted,
                    new Vdl2AnalysisBatchSummary(
                        request,
                        0,
                        1,
                        0,
                        0,
                        0,
                        0,
                        false,
                        "analysis_scheduler_failed"));
            }
            finally
            {
                lock (_gate)
                {
                    _busy = false;
                    _activeCaptureId =
                        string.Empty;
                    _completed++;
                }
            }
        }
    }

    private async Task ProcessRequestAsync(
        Vdl2AnalysisRequest request,
        CancellationToken cancellationToken)
    {
        var salvage =
            await _salvager
                .PrepareAsync(
                    request.Capture,
                    request.DiagnosticMode,
                    cancellationToken)
                .ConfigureAwait(
                    false);

        if (salvage.SplitApplied)
        {
            lock (_gate)
                _salvageBatches++;
        }

        var captures =
            salvage.AnalysisCaptures;

        if (captures.Length == 0)
        {
            // Keep one diagnostic result for the UI, but the parent remains
            // marked continuous so it can never publish an aircraft.
            var result =
                await _analyzer
                    .AnalyzeAsync(
                        request.Capture,
                        diagnosticMode: true,
                        cancellationToken:
                            cancellationToken)
                    .ConfigureAwait(
                        false);

            SafeInvoke(
                AnalysisCompleted,
                new Vdl2AnalysisCompletion(
                    request,
                    request.Capture,
                    false,
                    0,
                    0,
                    result));

            SafeInvoke(
                BatchCompleted,
                new Vdl2AnalysisBatchSummary(
                    request,
                    salvage.BoundedBurstCount,
                    1,
                    result.Success ? 1 : 0,
                    0,
                    result.Frame?.Payload?.FcsInvalidFrames ?? 0,
                    0,
                    false,
                    salvage.Status));

            return;
        }

        var successful =
            0;

        var validAvlc =
            0;

        var invalidFcs =
            0;

        var aircraftCandidates =
            0;

        for (var index = 0;
             index < captures.Length;
             index++)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var effective =
                captures[index];

            var result =
                await _analyzer
                    .AnalyzeAsync(
                        effective,
                        request.DiagnosticMode,
                        cancellationToken)
                    .ConfigureAwait(
                        false);

            if (result.Success)
                successful++;

            var valid =
                result.Frame?.Payload?.FcsValidFrames ??
                0;

            var invalid =
                result.Frame?.Payload?.FcsInvalidFrames ??
                0;

            validAvlc +=
                valid;

            invalidFcs +=
                invalid;

            aircraftCandidates +=
                result.Frame?.Payload?.Frames.Count(
                    frame =>
                        frame.FcsValid &&
                        frame.Icao.Length == 6) ??
                0;

            SafeInvoke(
                AnalysisCompleted,
                new Vdl2AnalysisCompletion(
                    request,
                    effective,
                    salvage.SplitApplied,
                    index + 1,
                    captures.Length,
                    result));
        }

        var status =
            salvage.SplitApplied &&
            validAvlc > 0 &&
            request.Capture.ContinuousOrInterference
                ? "CONTINUOUS-CAPTURE-WITH-VALID-AVLC"
                : validAvlc > 0
                    ? "AVLC-VALID"
                    : salvage.Status;

        SafeInvoke(
            BatchCompleted,
            new Vdl2AnalysisBatchSummary(
                request,
                salvage.BoundedBurstCount,
                captures.Length,
                successful,
                validAvlc,
                invalidFcs,
                aircraftCandidates,
                salvage.SplitApplied,
                status));
    }

    private static void SafeInvoke<T>(
        Action<T>? handler,
        T value)
    {
        if (handler is null)
            return;

        try
        {
            handler(value);
        }
        catch
        {
            // A UI or logging consumer must not stop the decoder worker.
        }
    }

    private static void SafeInvoke<T1, T2>(
        Action<T1, T2>? handler,
        T1 first,
        T2 second)
    {
        if (handler is null)
            return;

        try
        {
            handler(
                first,
                second);
        }
        catch
        {
            // A UI or logging consumer must not stop the decoder worker.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _queue.Clear();
            _queuedIds.Clear();
        }

        _cancellation.Cancel();

        try
        {
            _signal.Release();
        }
        catch (
            ObjectDisposedException)
        {
        }

        var workerStopped =
            false;

        try
        {
            workerStopped =
                _worker.Wait(
                    TimeSpan.FromSeconds(
                        5));
        }
        catch
        {
        }

        if (workerStopped)
        {
            _cancellation.Dispose();
            _signal.Dispose();
        }
    }
}
