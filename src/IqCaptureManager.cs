// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed record CaptureInfo(
    string Id,
    DateTimeOffset CreatedAt,
    long FrequencyHz,
    double SampleRate,
    int ComplexSamples,
    double DurationMs,
    string TriggerClassification,
    string CompletionReason,
    bool Limited,
    bool ContinuousOrInterference,
    double QualityScore,
    bool RecommendedForD8psk,
    string IqPath,
    string MetadataPath);

internal readonly record struct CaptureManagerSnapshot(
    bool Capturing,
    bool Armed,
    bool CooldownActive,
    double CurrentDurationMs,
    ulong AcceptedCaptures,
    ulong RejectedCaptures,
    ulong LimitedCaptures,
    ulong ContinuousCaptures,
    int PendingWrites,
    ulong DroppedWrites,
    string State,
    string LastReason);

internal sealed class IqCaptureManager : IDisposable
{
    private sealed record PendingCapture(
        string Id,
        DateTimeOffset StartedAt,
        long FrequencyHz,
        double SampleRate,
        List<float[]> Blocks,
        long ComplexSamples,
        long PreBufferSamples,
        long EventEndSampleIndex,
        string TriggerClassification,
        string FinalClassification,
        string CompletionReason,
        bool Limited,
        bool ContinuousOrInterference,
        double TriggerActiveDurationMs,
        double TriggerNoiseDb,
        double TriggerSignalDb,
        double TriggerMarginDb,
        double TriggerDcRatio,
        double TriggerPhaseActivity,
        double TriggerAmplitudeVariation,
        double PeakSignalDb,
        double PeakMarginDb,
        double MeanDcRatio,
        double MeanPhaseActivity,
        double MeanAmplitudeVariation,
        double MaximumDetectorActiveDurationMs);

    private sealed record QualityResult(
        double Score,
        string Grade,
        bool Recommended,
        string[] Flags);

    private readonly object _gate = new();
    private readonly Queue<float[]> _preBuffer = new();
    private readonly Queue<CaptureInfo> _captures = new();
    private readonly object _writeQueueGate = new();
    private readonly Queue<PendingCapture> _writeQueue = new();
    private readonly SemaphoreSlim _writeSignal = new(0);
    private readonly CancellationTokenSource _writeCancellation = new();
    private readonly Task _writeWorker;

    private const int MaximumSessionCaptures = 100;
    private const int MaximumPendingWrites = 4;
    private const double PreBufferMs = 250.0;
    private const double PostBufferMs = 250.0;
    private const double MaximumCaptureMs = 2500.0;
    private const double MinimumTriggerDurationMs = 20.0;
    private const int CooldownMs = 750;

    private volatile bool _disposed;
    private bool _capturing;
    private bool _armed;
    private bool _triggerEligiblePrevious;
    private bool _triggerAttemptSeenInDetectorBurst;

    private List<float[]>? _currentBlocks;
    private DateTimeOffset _captureStartedAt;
    private long _currentFrequency;
    private double _currentSampleRate;
    private string _triggerClassification = "UNKNOWN";
    private string _finalClassification = "UNKNOWN";
    private string _lastReason = "startup";

    private long _preBufferComplexSamples;
    private long _preBufferFrequency;
    private double _preBufferSampleRate;

    private long _remainingPostSamples;
    private long _totalCapturedComplexSamples;
    private long _capturePreBufferSamples;
    private long _eventEndSampleIndex = -1;
    private long _cooldownUntilTick;

    private double _triggerActiveDurationMs;
    private double _triggerNoiseDb;
    private double _triggerSignalDb;
    private double _triggerMarginDb;
    private double _triggerDcRatio;
    private double _triggerPhaseActivity;
    private double _triggerAmplitudeVariation;

    private double _peakSignalDb = -140.0;
    private double _peakMarginDb = -140.0;
    private double _weightedDcSum;
    private double _weightedPhaseSum;
    private double _weightedAmplitudeVariationSum;
    private long _featureWeight;
    private double _maximumDetectorActiveDurationMs;

    private ulong _acceptedCaptures;
    private ulong _rejectedCaptures;
    private ulong _limitedCaptures;
    private ulong _continuousCaptures;
    private ulong _droppedWrites;

    public event Action<CaptureInfo>? CaptureCompleted;

    public string CaptureDirectory { get; }

    public IqCaptureManager()
    {
        CaptureDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Plugins",
            "AircraftDataEnhanced",
            "captures");

        Directory.CreateDirectory(CaptureDirectory);

        _writeWorker =
            Task.Run(
                WriteWorkerAsync);
    }

    public unsafe void PushIq(
        float* interleavedIq,
        int complexCount,
        double sampleRate,
        long frequencyHz,
        BurstDetectorSnapshot detector)
    {
        if (interleavedIq is null ||
            complexCount <= 0 ||
            sampleRate <= 0 ||
            frequencyHz <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
                return;
        }

        var currentBlock = new float[checked(complexCount * 2)];

        fixed (float* destination = currentBlock)
        {
            Buffer.MemoryCopy(
                interleavedIq,
                destination,
                currentBlock.Length * sizeof(float),
                currentBlock.Length * sizeof(float));
        }

        PendingCapture? pending = null;

        lock (_gate)
        {
            if (_disposed)
                return;

            var nowTick = Environment.TickCount64;

            if (!detector.Active)
            {
                _triggerAttemptSeenInDetectorBurst = false;

                if (detector.NoiseReady &&
                    string.Equals(
                        detector.Classification,
                        "Idle",
                        StringComparison.Ordinal) &&
                    nowTick >= _cooldownUntilTick)
                {
                    _armed = true;
                }
            }

            var triggerEligible =
                detector.NoiseReady &&
                detector.Active &&
                detector.ActiveDurationMs >= MinimumTriggerDurationMs &&
                string.Equals(
                    detector.Classification,
                    "Modulated burst",
                    StringComparison.Ordinal);

            var newTriggerAttempt =
                triggerEligible &&
                !_triggerEligiblePrevious &&
                !_triggerAttemptSeenInDetectorBurst;

            _triggerEligiblePrevious = triggerEligible;

            if (newTriggerAttempt)
                _triggerAttemptSeenInDetectorBurst = true;

            if (_capturing)
            {
                if (!detector.Active && _eventEndSampleIndex < 0)
                {
                    _eventEndSampleIndex = _totalCapturedComplexSamples;
                    _remainingPostSamples =
                        (long)Math.Round(sampleRate * PostBufferMs / 1000.0);
                }

                var appendedSamples = AppendCurrentBlock(
                    currentBlock,
                    complexCount,
                    detector);

                var maximumSamples =
                    (long)Math.Round(sampleRate * MaximumCaptureMs / 1000.0);

                var continuousOrInterference =
                    IsContinuousOrInterference(detector);

                if (continuousOrInterference)
                {
                    pending = FinalizeCaptureLocked(
                        "continuous_or_interference",
                        limited: false,
                        continuousOrInterference: true,
                        detector);
                }
                else if (_totalCapturedComplexSamples >= maximumSamples)
                {
                    pending = FinalizeCaptureLocked(
                        "maximum_duration_reached",
                        limited: true,
                        continuousOrInterference: false,
                        detector);
                }
                else if (!detector.Active)
                {
                    _remainingPostSamples -= appendedSamples;

                    if (_remainingPostSamples <= 0)
                    {
                        pending = FinalizeCaptureLocked(
                            "post_buffer_complete",
                            limited: false,
                            continuousOrInterference: false,
                            detector);
                    }
                }
            }
            else if (newTriggerAttempt)
            {
                if (CanStartCapture(nowTick, detector, out var rejectionReason))
                {
                    StartCaptureLocked(
                        sampleRate,
                        frequencyHz,
                        detector);

                    AppendCurrentBlock(
                        currentBlock,
                        complexCount,
                        detector);

                    var maximumSamples =
                        (long)Math.Round(sampleRate * MaximumCaptureMs / 1000.0);

                    if (_totalCapturedComplexSamples >= maximumSamples)
                    {
                        pending = FinalizeCaptureLocked(
                            "maximum_duration_reached",
                            limited: true,
                            continuousOrInterference: false,
                            detector);
                    }
                }
                else
                {
                    _rejectedCaptures++;
                    _lastReason = rejectionReason;
                    AddToPreBuffer(
                        currentBlock,
                        complexCount,
                        sampleRate,
                        frequencyHz);
                }
            }
            else
            {
                AddToPreBuffer(
                    currentBlock,
                    complexCount,
                    sampleRate,
                    frequencyHz);
            }
        }

        if (pending is not null)
            EnqueueWrite(pending);
    }

    public CaptureManagerSnapshot StatusSnapshot()
    {
        lock (_gate)
        {
            var nowTick = Environment.TickCount64;
            var cooldownActive = nowTick < _cooldownUntilTick;

            var durationMs =
                _capturing && _currentSampleRate > 0
                    ? _totalCapturedComplexSamples /
                      _currentSampleRate * 1000.0
                    : 0.0;

            string state;
            if (_disposed)
                state = "Disposed";
            else if (_capturing)
                state = "Recording";
            else if (cooldownActive)
                state = "Cooldown";
            else if (_armed)
                state = "Armed";
            else
                state = "Waiting for idle";

            int pendingWrites;

            lock (_writeQueueGate)
            {
                pendingWrites =
                    _writeQueue.Count;
            }

            return new CaptureManagerSnapshot(
                _capturing,
                _armed,
                cooldownActive,
                durationMs,
                _acceptedCaptures,
                _rejectedCaptures,
                _limitedCaptures,
                _continuousCaptures,
                pendingWrites,
                _droppedWrites,
                state,
                _lastReason);
        }
    }

    public IReadOnlyList<CaptureInfo> Snapshot()
    {
        lock (_gate)
            return _captures.Reverse().ToArray();
    }

    public void ResetForRetune()
    {
        lock (_gate)
        {
            if (_capturing)
            {
                _rejectedCaptures++;
                _lastReason = "retune_aborted_capture";
            }
            else
            {
                _lastReason = "retune";
            }

            ClearPreBufferLocked();
            ResetCurrentCaptureLocked();

            _armed = false;
            _triggerEligiblePrevious = false;
            _triggerAttemptSeenInDetectorBurst = false;
            _cooldownUntilTick = Environment.TickCount64 + CooldownMs;
        }
    }

    private bool CanStartCapture(
        long nowTick,
        BurstDetectorSnapshot detector,
        out string rejectionReason)
    {
        if (!detector.NoiseReady)
        {
            rejectionReason = "noise_not_ready";
            return false;
        }

        if (nowTick < _cooldownUntilTick)
        {
            rejectionReason = "cooldown_active";
            return false;
        }

        if (!_armed)
        {
            rejectionReason = "not_rearmed_after_idle";
            return false;
        }

        if (detector.ActiveDurationMs < MinimumTriggerDurationMs)
        {
            rejectionReason = "trigger_too_short";
            return false;
        }

        if (!string.Equals(
            detector.Classification,
            "Modulated burst",
            StringComparison.Ordinal))
        {
            rejectionReason = "not_modulated";
            return false;
        }

        rejectionReason = "accepted_trigger";
        return true;
    }

    private void AddToPreBuffer(
        float[] block,
        int complexCount,
        double sampleRate,
        long frequencyHz)
    {
        var frequencyChanged =
            _preBufferFrequency != 0 &&
            _preBufferFrequency != frequencyHz;

        var sampleRateChanged =
            _preBufferSampleRate > 0 &&
            Math.Abs(_preBufferSampleRate - sampleRate) > 0.5;

        if (frequencyChanged || sampleRateChanged)
            ClearPreBufferLocked();

        _preBufferFrequency = frequencyHz;
        _preBufferSampleRate = sampleRate;
        _preBuffer.Enqueue(block);
        _preBufferComplexSamples += complexCount;

        var maximumSamples =
            (long)Math.Round(sampleRate * PreBufferMs / 1000.0);

        while (_preBufferComplexSamples > maximumSamples &&
               _preBuffer.Count > 1)
        {
            var removed = _preBuffer.Dequeue();
            _preBufferComplexSamples -= removed.Length / 2;
        }
    }

    private void StartCaptureLocked(
        double sampleRate,
        long frequencyHz,
        BurstDetectorSnapshot detector)
    {
        _capturing = true;
        _armed = false;
        _captureStartedAt = DateTimeOffset.Now;
        _currentSampleRate = sampleRate;
        _currentFrequency = frequencyHz;
        _triggerClassification = detector.Classification;
        _finalClassification = detector.Classification;
        _remainingPostSamples = 0;
        _totalCapturedComplexSamples = 0;
        _eventEndSampleIndex = -1;

        _triggerActiveDurationMs = detector.ActiveDurationMs;
        _triggerNoiseDb = detector.NoiseDb;
        _triggerSignalDb = detector.CurrentDb;
        _triggerMarginDb = detector.MarginDb;
        _triggerDcRatio = detector.DcRatio;
        _triggerPhaseActivity = detector.PhaseActivity;
        _triggerAmplitudeVariation = detector.AmplitudeVariation;

        _peakSignalDb = detector.CurrentDb;
        _peakMarginDb = detector.MarginDb;
        _weightedDcSum = 0;
        _weightedPhaseSum = 0;
        _weightedAmplitudeVariationSum = 0;
        _featureWeight = 0;
        _maximumDetectorActiveDurationMs = detector.ActiveDurationMs;

        _currentBlocks = new List<float[]>();

        while (_preBuffer.Count > 0)
        {
            var preBlock = _preBuffer.Dequeue();
            _currentBlocks.Add(preBlock);
            _totalCapturedComplexSamples += preBlock.Length / 2;
        }

        _capturePreBufferSamples = _totalCapturedComplexSamples;
        _preBufferComplexSamples = 0;
        _preBufferFrequency = 0;
        _preBufferSampleRate = 0;
        _lastReason = "recording_modulated_trigger";
    }

    private int AppendCurrentBlock(
        float[] block,
        int complexCount,
        BurstDetectorSnapshot detector)
    {
        if (_currentBlocks is null || _currentSampleRate <= 0)
            return 0;

        var maximumSamples =
            (long)Math.Round(
                _currentSampleRate * MaximumCaptureMs / 1000.0);

        var remaining =
            Math.Max(0, maximumSamples - _totalCapturedComplexSamples);

        if (remaining <= 0)
            return 0;

        var appendedSamples =
            (int)Math.Min(complexCount, remaining);

        float[] storedBlock;
        if (appendedSamples == complexCount)
        {
            storedBlock = block;
        }
        else
        {
            storedBlock = new float[checked(appendedSamples * 2)];
            Array.Copy(block, storedBlock, storedBlock.Length);
        }

        _currentBlocks.Add(storedBlock);
        _totalCapturedComplexSamples += appendedSamples;
        _finalClassification = detector.Classification;
        _peakSignalDb = Math.Max(_peakSignalDb, detector.CurrentDb);
        _peakMarginDb = Math.Max(_peakMarginDb, detector.MarginDb);
        _maximumDetectorActiveDurationMs = Math.Max(
            _maximumDetectorActiveDurationMs,
            detector.ActiveDurationMs);

        _weightedDcSum += detector.DcRatio * appendedSamples;
        _weightedPhaseSum += detector.PhaseActivity * appendedSamples;
        _weightedAmplitudeVariationSum +=
            detector.AmplitudeVariation * appendedSamples;
        _featureWeight += appendedSamples;

        return appendedSamples;
    }

    private PendingCapture? FinalizeCaptureLocked(
        string completionReason,
        bool limited,
        bool continuousOrInterference,
        BurstDetectorSnapshot detector)
    {
        if (!_capturing ||
            _currentBlocks is null ||
            _currentBlocks.Count == 0 ||
            _currentSampleRate <= 0)
        {
            ResetCurrentCaptureLocked();
            return null;
        }

        if (_eventEndSampleIndex < 0 && !detector.Active)
            _eventEndSampleIndex = _totalCapturedComplexSamples;

        if (limited)
            _limitedCaptures++;

        if (continuousOrInterference)
            _continuousCaptures++;

        var meanDc =
            _featureWeight > 0
                ? _weightedDcSum / _featureWeight
                : _triggerDcRatio;

        var meanPhase =
            _featureWeight > 0
                ? _weightedPhaseSum / _featureWeight
                : _triggerPhaseActivity;

        var meanAmplitudeVariation =
            _featureWeight > 0
                ? _weightedAmplitudeVariationSum / _featureWeight
                : _triggerAmplitudeVariation;

        var id = _captureStartedAt.ToString("yyyyMMdd-HHmmss-fff");

        var pending = new PendingCapture(
            id,
            _captureStartedAt,
            _currentFrequency,
            _currentSampleRate,
            _currentBlocks,
            _totalCapturedComplexSamples,
            _capturePreBufferSamples,
            _eventEndSampleIndex,
            _triggerClassification,
            _finalClassification,
            completionReason,
            limited,
            continuousOrInterference,
            _triggerActiveDurationMs,
            _triggerNoiseDb,
            _triggerSignalDb,
            _triggerMarginDb,
            _triggerDcRatio,
            _triggerPhaseActivity,
            _triggerAmplitudeVariation,
            _peakSignalDb,
            _peakMarginDb,
            meanDc,
            meanPhase,
            meanAmplitudeVariation,
            _maximumDetectorActiveDurationMs);

        _lastReason = completionReason;
        ResetCurrentCaptureLocked();
        _armed = false;
        _cooldownUntilTick =
            Environment.TickCount64 + CooldownMs;

        return pending;
    }

    private void EnqueueWrite(
        PendingCapture pending)
    {
        var accepted = false;

        lock (_writeQueueGate)
        {
            if (!_disposed &&
                _writeQueue.Count <
                    MaximumPendingWrites)
            {
                _writeQueue.Enqueue(
                    pending);

                accepted = true;
            }
        }

        if (accepted)
        {
            _writeSignal.Release();
            return;
        }

        lock (_gate)
        {
            _rejectedCaptures++;
            _droppedWrites++;
            _lastReason =
                "capture_write_queue_full";
        }
    }

    private async Task WriteWorkerAsync()
    {
        var token =
            _writeCancellation.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _writeSignal
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

            PendingCapture? pending =
                null;

            lock (_writeQueueGate)
            {
                if (_writeQueue.Count > 0)
                {
                    pending =
                        _writeQueue.Dequeue();
                }
            }

            if (pending is null)
                continue;

            try
            {
                await PersistCaptureAsync(
                    pending)
                    .ConfigureAwait(
                        false);
            }
            catch
            {
                lock (_gate)
                {
                    _rejectedCaptures++;
                    _lastReason =
                        "capture_write_worker_failed";
                }
            }
        }
    }

    private async Task PersistCaptureAsync(PendingCapture pending)
    {
        try
        {
            var quality = ComputeQuality(pending);
            var baseName =
                $"vdl2-candidate-{pending.Id}-{pending.FrequencyHz}Hz";

            var iqPath =
                Path.Combine(CaptureDirectory, baseName + ".iqf32");
            var metadataPath =
                Path.Combine(CaptureDirectory, baseName + ".json");

            var temporaryIqPath = iqPath + ".tmp";
            var temporaryMetadataPath = metadataPath + ".tmp";

            try
            {
                Directory.CreateDirectory(CaptureDirectory);

                using (var stream = new FileStream(
                    temporaryIqPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.SequentialScan))
                using (var writer = new BinaryWriter(
                    stream,
                    Encoding.UTF8,
                    leaveOpen: false))
                {
                    foreach (var block in pending.Blocks)
                    {
                        foreach (var value in block)
                            writer.Write(value);
                    }
                }

                string iqSha256;
                using (var stream = File.OpenRead(temporaryIqPath))
                {
                    iqSha256 =
                        Convert.ToHexString(
                            SHA256.HashData(stream))
                        .ToLowerInvariant();
                }

                var durationMs =
                    pending.ComplexSamples /
                    pending.SampleRate * 1000.0;

                var preBufferMs =
                    pending.PreBufferSamples /
                    pending.SampleRate * 1000.0;

                var postBufferSamples =
                    pending.EventEndSampleIndex >= 0
                        ? Math.Max(
                            0,
                            pending.ComplexSamples -
                            pending.EventEndSampleIndex)
                        : 0;

                var postBufferMs =
                    postBufferSamples /
                    pending.SampleRate * 1000.0;

                var metadata = new
                {
                    schema_version = 3,
                    format = "complex_float32_interleaved_le",
                    layout = "I0,Q0,I1,Q1,...",
                    capture_started_at = pending.StartedAt,
                    saved_at = DateTimeOffset.Now,
                    frequency_hz = pending.FrequencyHz,
                    frequency_mhz =
                        pending.FrequencyHz / 1_000_000.0,
                    sample_rate = pending.SampleRate,
                    complex_samples = pending.ComplexSamples,
                    duration_ms = Math.Round(durationMs, 3),
                    maximum_capture_ms = MaximumCaptureMs,
                    pre_buffer_ms = Math.Round(preBufferMs, 3),
                    post_buffer_ms = Math.Round(postBufferMs, 3),
                    trigger_classification =
                        pending.TriggerClassification,
                    final_live_classification =
                        pending.FinalClassification,
                    completion_reason =
                        pending.CompletionReason,
                    limited = pending.Limited,
                    continuous_or_interference =
                        pending.ContinuousOrInterference,
                    capture_classification =
                        pending.ContinuousOrInterference
                            ? "CONTINUOUS-OR-INTERFERENCE"
                            : "BOUNDED-CAPTURE",
                    trigger = new
                    {
                        active_duration_ms =
                            Math.Round(
                                pending.TriggerActiveDurationMs,
                                3),
                        noise_dbfs =
                            Math.Round(pending.TriggerNoiseDb, 3),
                        signal_dbfs =
                            Math.Round(pending.TriggerSignalDb, 3),
                        margin_db =
                            Math.Round(pending.TriggerMarginDb, 3),
                        dc_ratio =
                            Math.Round(pending.TriggerDcRatio, 6),
                        phase_activity_rad =
                            Math.Round(
                                pending.TriggerPhaseActivity,
                                6),
                        amplitude_variation =
                            Math.Round(
                                pending.TriggerAmplitudeVariation,
                                6)
                    },
                    aggregate = new
                    {
                        peak_signal_dbfs =
                            Math.Round(pending.PeakSignalDb, 3),
                        peak_margin_db =
                            Math.Round(pending.PeakMarginDb, 3),
                        mean_dc_ratio =
                            Math.Round(pending.MeanDcRatio, 6),
                        mean_phase_activity_rad =
                            Math.Round(
                                pending.MeanPhaseActivity,
                                6),
                        mean_amplitude_variation =
                            Math.Round(
                                pending.MeanAmplitudeVariation,
                                6),
                        maximum_detector_active_duration_ms =
                            Math.Round(
                                pending.MaximumDetectorActiveDurationMs,
                                3)
                    },
                    quality_score =
                        Math.Round(quality.Score, 1),
                    quality_grade = quality.Grade,
                    recommended_for_d8psk =
                        quality.Recommended,
                    quality_flags = quality.Flags,
                    iq_file = Path.GetFileName(iqPath),
                    iq_sha256 = iqSha256
                };

                await File.WriteAllTextAsync(
                    temporaryMetadataPath,
                    JsonSerializer.Serialize(
                        metadata,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        }))
                    .ConfigureAwait(false);

                File.Move(
                    temporaryIqPath,
                    iqPath,
                    overwrite: false);

                File.Move(
                    temporaryMetadataPath,
                    metadataPath,
                    overwrite: false);

                var info = new CaptureInfo(
                    pending.Id,
                    pending.StartedAt,
                    pending.FrequencyHz,
                    pending.SampleRate,
                    checked((int)Math.Min(
                        int.MaxValue,
                        pending.ComplexSamples)),
                    durationMs,
                    pending.TriggerClassification,
                    pending.CompletionReason,
                    pending.Limited,
                    pending.ContinuousOrInterference,
                    quality.Score,
                    quality.Recommended,
                    iqPath,
                    metadataPath);

                lock (_gate)
                {
                    if (_disposed)
                        return;

                    _captures.Enqueue(info);

                    while (_captures.Count >
                           MaximumSessionCaptures)
                    {
                        _captures.Dequeue();
                    }

                    _acceptedCaptures++;
                    _lastReason =
                        quality.Recommended
                            ? "capture_saved_recommended"
                            : "capture_saved";
                }

                try
                {
                    CaptureCompleted?.Invoke(info);
                }
                catch
                {
                    // A consumer notification must not invalidate the capture.
                }
            }
            catch
            {
                DeleteIfExists(temporaryIqPath);
                DeleteIfExists(temporaryMetadataPath);

                lock (_gate)
                {
                    _rejectedCaptures++;
                    _lastReason = "capture_write_failed";
                }
            }
        }
        finally
        {
            // One bounded worker serializes all capture persistence.
        }
    }

    private static QualityResult ComputeQuality(
        PendingCapture capture)
    {
        var flags = new List<string>();
        var score = 0.0;

        if (string.Equals(
            capture.TriggerClassification,
            "Modulated burst",
            StringComparison.Ordinal))
        {
            score += 20.0;
        }
        else
        {
            flags.Add("unexpected_trigger_classification");
        }

        score += 20.0 * Normalize(
            capture.PeakMarginDb,
            4.0,
            28.0);

        score += 15.0 * Normalize(
            capture.MeanPhaseActivity,
            0.10,
            0.35);

        score += 10.0 * Normalize(
            capture.MeanAmplitudeVariation,
            0.02,
            0.20);

        score += 15.0 *
            (1.0 - Normalize(
                capture.MeanDcRatio,
                0.02,
                0.25));

        var activeDuration =
            capture.MaximumDetectorActiveDurationMs;

        if (activeDuration >= 20.0 &&
            activeDuration <= 1200.0)
        {
            score += 10.0;
        }
        else
        {
            flags.Add("duration_outside_preferred_range");
        }

        if (string.Equals(
            capture.CompletionReason,
            "post_buffer_complete",
            StringComparison.Ordinal))
        {
            score += 10.0;
        }
        else
        {
            flags.Add(capture.CompletionReason);
        }

        if (capture.Limited)
        {
            score -= 30.0;
            flags.Add("capture_limited");
        }

        if (capture.ContinuousOrInterference)
        {
            score -= 40.0;
            flags.Add("continuous_or_interference");
        }

        if (capture.MeanDcRatio >= 0.20)
        {
            score -= 20.0;
            flags.Add("high_dc_ratio");
        }

        if (capture.PeakMarginDb > 50.0)
        {
            score -= 10.0;
            flags.Add("suspiciously_high_margin");
        }

        score = Math.Clamp(score, 0.0, 100.0);

        var recommended =
            score >= 60.0 &&
            !capture.Limited &&
            !capture.ContinuousOrInterference &&
            capture.MeanDcRatio < 0.20 &&
            capture.MeanPhaseActivity >= 0.12 &&
            activeDuration >= 20.0 &&
            activeDuration <= 1200.0;

        if (recommended)
            flags.Add("recommended_for_d8psk");

        var grade =
            score >= 85.0 ? "excellent" :
            score >= 70.0 ? "good" :
            score >= 55.0 ? "fair" :
            "poor";

        return new QualityResult(
            score,
            grade,
            recommended,
            flags.Distinct(
                StringComparer.Ordinal)
                .ToArray());
    }

    private static bool IsContinuousOrInterference(
        BurstDetectorSnapshot detector)
    {
        if (!detector.Active)
            return false;

        return detector.Classification is
            "Continuous noise rise" or
            "Continuous modulated" or
            "Narrow carrier" or
            "DC interference";
    }

    private static double Normalize(
        double value,
        double minimum,
        double maximum)
    {
        if (maximum <= minimum)
            return 0.0;

        return Math.Clamp(
            (value - minimum) /
            (maximum - minimum),
            0.0,
            1.0);
    }

    private void ClearPreBufferLocked()
    {
        _preBuffer.Clear();
        _preBufferComplexSamples = 0;
        _preBufferFrequency = 0;
        _preBufferSampleRate = 0;
    }

    private void ResetCurrentCaptureLocked()
    {
        _capturing = false;
        _currentBlocks = null;
        _captureStartedAt = default;
        _currentFrequency = 0;
        _currentSampleRate = 0;
        _triggerClassification = "UNKNOWN";
        _finalClassification = "UNKNOWN";
        _remainingPostSamples = 0;
        _totalCapturedComplexSamples = 0;
        _capturePreBufferSamples = 0;
        _eventEndSampleIndex = -1;

        _triggerActiveDurationMs = 0;
        _triggerNoiseDb = 0;
        _triggerSignalDb = 0;
        _triggerMarginDb = 0;
        _triggerDcRatio = 0;
        _triggerPhaseActivity = 0;
        _triggerAmplitudeVariation = 0;

        _peakSignalDb = -140.0;
        _peakMarginDb = -140.0;
        _weightedDcSum = 0;
        _weightedPhaseSum = 0;
        _weightedAmplitudeVariationSum = 0;
        _featureWeight = 0;
        _maximumDetectorActiveDurationMs = 0;
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_capturing)
            {
                _rejectedCaptures++;
                _lastReason =
                    "shutdown_aborted_capture";
            }

            ClearPreBufferLocked();
            ResetCurrentCaptureLocked();
        }

        lock (_writeQueueGate)
        {
            _writeQueue.Clear();
        }

        _writeCancellation.Cancel();

        try
        {
            _writeSignal.Release();
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
                _writeWorker.Wait(
                    TimeSpan.FromSeconds(
                        5));
        }
        catch
        {
        }

        if (workerStopped)
        {
            _writeCancellation.Dispose();
            _writeSignal.Dispose();
        }
    }
}
