// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal readonly record struct BurstDetectorSnapshot(
    bool Active,
    bool NoiseReady,
    double WarmupRemainingMs,
    double NoiseDb,
    double CurrentDb,
    double MarginDb,
    double EnterThresholdDb,
    double ExitThresholdDb,
    double ActiveDurationMs,
    double DcRatio,
    double PhaseActivity,
    double AmplitudeVariation,
    double ActiveNoiseRiseDb,
    bool ActiveNoiseTracking,
    ulong CompletedEvents,
    ulong ForcedClosures,
    ulong RejectedEvents,
    string Classification);

internal readonly record struct BlockFeatures(
    double PowerDb,
    double DcRatio,
    double PhaseActivity,
    double AmplitudeVariation);

/// <summary>
/// Passive managed RF pre-classifier.
/// It detects and classifies energy events but does not decode VDL2.
/// </summary>
internal sealed class ManagedBurstDetector
{
    private const double MinimumPower = 1e-20;
    private const int MinimumBurstBlocks = 2;
    private const int MaximumQueueSize = 256;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _jsonQueue = new();

    private double _enterThresholdDb = 5.0;
    private double _exitThresholdDb = 2.0;
    private double _maximumBurstMs = 1400.0;
    private double _continuousCarrierMs = 650.0;
    private double _noiseWarmupMs = 1800.0;

    private double _noiseDb = -90.0;
    private double _smoothedDb = -90.0;
    private bool _initialized;
    private bool _noiseReady;
    private long _warmupSamples;

    private bool _burstActive;
    private int _burstBlocks;
    private double _burstPeakDb = -120.0;
    private double _burstSumDb;
    private double _burstDcSum;
    private double _burstPhaseSum;
    private double _burstAmplitudeVariationSum;
    private long _burstSamples;

    private double _burstNoiseAtStartDb = -90.0;
    private double _activeFloorDb = -90.0;
    private int _activeFloorStableBlocks;
    private bool _activeNoiseTracking;
    private double _activeNoiseRiseDb;

    private long _totalSamples;
    private double _lastSampleRate;
    private long _lastCenterFrequency;
    private ulong _burstId;
    private ulong _completedEvents;
    private ulong _forcedClosures;
    private ulong _rejectedEvents;
    private int _queued;

    private double _lastDcRatio;
    private double _lastPhaseActivity;
    private double _lastAmplitudeVariation;
    private string _classification = "Noise learning";

    public string Status => "Managed C# adaptive RF pre-classifier ready";

    public double EnterThresholdDb
    {
        get { lock (_gate) return _enterThresholdDb; }
        set
        {
            lock (_gate)
            {
                _enterThresholdDb = Math.Clamp(value, 2.0, 20.0);
                if (_exitThresholdDb >= _enterThresholdDb)
                {
                    _exitThresholdDb = Math.Max(
                        1.0,
                        _enterThresholdDb - 1.0);
                }
            }
        }
    }

    public double ExitThresholdDb
    {
        get { lock (_gate) return _exitThresholdDb; }
        set
        {
            lock (_gate)
            {
                _exitThresholdDb = Math.Clamp(
                    value,
                    1.0,
                    Math.Max(1.0, _enterThresholdDb - 0.5));
            }
        }
    }

    public double MaximumBurstMs
    {
        get { lock (_gate) return _maximumBurstMs; }
        set
        {
            lock (_gate)
                _maximumBurstMs = Math.Clamp(value, 100.0, 10_000.0);
        }
    }

    public unsafe void Process(
        float* interleavedIq,
        int complexCount,
        double sampleRate,
        long centerFrequencyHz)
    {
        if (interleavedIq is null || complexCount <= 0 || sampleRate <= 0)
            return;

        var features = ExtractFeatures(interleavedIq, complexCount);
        if (!double.IsFinite(features.PowerDb))
            return;

        lock (_gate)
        {
            _lastSampleRate = sampleRate;
            _lastCenterFrequency = centerFrequencyHz;
            _totalSamples += complexCount;
            _lastDcRatio = features.DcRatio;
            _lastPhaseActivity = features.PhaseActivity;
            _lastAmplitudeVariation = features.AmplitudeVariation;

            if (!_initialized)
            {
                _noiseDb = features.PowerDb;
                _smoothedDb = features.PowerDb;
                _initialized = true;
                _classification = "Noise learning";
            }
            else
            {
                _smoothedDb = Smooth(
                    _smoothedDb,
                    features.PowerDb,
                    0.20);
            }

            if (!_noiseReady)
            {
                UpdateNoise(features.PowerDb, allowFastRise: true);
                _warmupSamples += complexCount;

                if (_warmupSamples / sampleRate * 1000.0 >= _noiseWarmupMs)
                {
                    _noiseReady = true;
                    _classification = "Idle";
                }

                return;
            }

            if (!_burstActive)
            {
                UpdateNoise(features.PowerDb, allowFastRise: false);

                if (_smoothedDb >= _noiseDb + _enterThresholdDb)
                    StartBurst(features, complexCount);

                return;
            }

            AccumulateBurst(features, complexCount);

            var activeMs =
                _burstSamples / _lastSampleRate * 1000.0;

            UpdateActiveNoiseFloor(
                features.PowerDb,
                activeMs);

            _classification = ClassifyLive(
                activeMs,
                Mean(_burstDcSum),
                Mean(_burstPhaseSum),
                Mean(_burstAmplitudeVariationSum));

            if (_smoothedDb <= _noiseDb + _exitThresholdDb)
            {
                var reason =
                    _activeNoiseTracking &&
                    _activeNoiseRiseDb >= 2.0
                        ? "adaptive_noise_floor_recovered"
                        : "signal_returned_to_noise";

                CompleteBurst(
                    forced: false,
                    reason);
            }
            else if (activeMs >= _maximumBurstMs)
            {
                _forcedClosures++;
                CompleteBurst(
                    forced: true,
                    reason: "maximum_duration_reached");

                if (_smoothedDb >= _noiseDb + _enterThresholdDb)
                    StartBurst(features, complexCount);
            }
        }
    }

    public bool TryReadJson(out string json)
    {
        if (_jsonQueue.TryDequeue(out var value))
        {
            Interlocked.Decrement(ref _queued);
            json = value;
            return true;
        }

        json = string.Empty;
        return false;
    }

    public BurstDetectorSnapshot Snapshot()
    {
        lock (_gate)
        {
            var activeMs =
                _burstActive && _lastSampleRate > 0
                    ? _burstSamples /
                      _lastSampleRate * 1000.0
                    : 0.0;

            var warmupElapsedMs =
                _lastSampleRate > 0
                    ? _warmupSamples /
                      _lastSampleRate * 1000.0
                    : 0.0;

            return new BurstDetectorSnapshot(
                _burstActive,
                _noiseReady,
                Math.Max(
                    0.0,
                    _noiseWarmupMs - warmupElapsedMs),
                _noiseDb,
                _smoothedDb,
                _smoothedDb - _noiseDb,
                _enterThresholdDb,
                _exitThresholdDb,
                activeMs,
                _lastDcRatio,
                _lastPhaseActivity,
                _lastAmplitudeVariation,
                _activeNoiseRiseDb,
                _activeNoiseTracking,
                _completedEvents,
                _forcedClosures,
                _rejectedEvents,
                _classification);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _initialized = false;
            _noiseReady = false;
            _warmupSamples = 0;
            _noiseDb = -90.0;
            _smoothedDb = -90.0;
            _totalSamples = 0;
            _completedEvents = 0;
            _forcedClosures = 0;
            _rejectedEvents = 0;
            _burstId = 0;
            _lastDcRatio = 0;
            _lastPhaseActivity = 0;
            _lastAmplitudeVariation = 0;
            _classification = "Noise learning";
            ResetBurst();
        }

        while (_jsonQueue.TryDequeue(out _))
            Interlocked.Decrement(ref _queued);
    }

    private unsafe static BlockFeatures ExtractFeatures(
        float* iq,
        int complexCount)
    {
        var stride = Math.Max(1, complexCount / 2048);

        double sumI = 0;
        double sumQ = 0;
        double sumPower = 0;
        double sumAmplitude = 0;
        double sumAmplitudeSquared = 0;
        var measured = 0;

        for (var n = 0; n < complexCount; n += stride)
        {
            var i = iq[n * 2];
            var q = iq[n * 2 + 1];

            if (!float.IsFinite(i) || !float.IsFinite(q))
                continue;

            var power =
                (double)i * i +
                (double)q * q;

            var amplitude = Math.Sqrt(power);

            sumI += i;
            sumQ += q;
            sumPower += power;
            sumAmplitude += amplitude;
            sumAmplitudeSquared += amplitude * amplitude;
            measured++;
        }

        if (measured < 4)
            return new BlockFeatures(double.NaN, 0, 0, 0);

        var meanI = sumI / measured;
        var meanQ = sumQ / measured;
        var meanPower = Math.Max(
            sumPower / measured,
            MinimumPower);

        var dcPower =
            meanI * meanI +
            meanQ * meanQ;

        var dcRatio = Math.Clamp(
            dcPower / meanPower,
            0.0,
            1.0);

        var meanAmplitude =
            sumAmplitude / measured;

        var amplitudeVariance = Math.Max(
            0.0,
            sumAmplitudeSquared / measured -
            meanAmplitude * meanAmplitude);

        var amplitudeVariation =
            meanAmplitude > 1e-12
                ? Math.Sqrt(amplitudeVariance) /
                  meanAmplitude
                : 0.0;

        double phaseSum = 0;
        double phaseSquaredSum = 0;
        var phaseCount = 0;
        double previousI = 0;
        double previousQ = 0;
        var hasPrevious = false;

        for (var n = 0; n < complexCount; n += stride)
        {
            var i =
                (double)iq[n * 2] -
                meanI;

            var q =
                (double)iq[n * 2 + 1] -
                meanQ;

            if (!double.IsFinite(i) || !double.IsFinite(q))
                continue;

            var power = i * i + q * q;
            if (power < meanPower * 0.02)
                continue;

            if (hasPrevious)
            {
                var cross =
                    previousI * q -
                    previousQ * i;

                var dot =
                    previousI * i +
                    previousQ * q;

                var delta = Math.Atan2(
                    cross,
                    dot);

                phaseSum += delta;
                phaseSquaredSum += delta * delta;
                phaseCount++;
            }

            previousI = i;
            previousQ = q;
            hasPrevious = true;
        }

        var phaseActivity = 0.0;

        if (phaseCount > 1)
        {
            var phaseMean =
                phaseSum / phaseCount;

            var phaseVariance = Math.Max(
                0.0,
                phaseSquaredSum / phaseCount -
                phaseMean * phaseMean);

            phaseActivity = Math.Sqrt(phaseVariance);
        }

        return new BlockFeatures(
            10.0 * Math.Log10(meanPower),
            dcRatio,
            phaseActivity,
            amplitudeVariation);
    }

    private void UpdateNoise(
        double blockDb,
        bool allowFastRise)
    {
        double alpha;

        if (blockDb < _noiseDb)
            alpha = 0.08;
        else
            alpha = allowFastRise ? 0.01 : 0.0005;

        _noiseDb = Smooth(
            _noiseDb,
            blockDb,
            alpha);
    }

    private void UpdateActiveNoiseFloor(
        double blockDb,
        double activeMs)
    {
        if (!_burstActive)
            return;

        if (blockDb < _activeFloorDb)
        {
            _activeFloorDb = Smooth(
                _activeFloorDb,
                blockDb,
                0.18);
        }
        else
        {
            _activeFloorDb = Smooth(
                _activeFloorDb,
                blockDb,
                0.02);
        }

        if (activeMs < _continuousCarrierMs * 0.75)
            return;

        var stable =
            Math.Abs(blockDb - _activeFloorDb) <= 1.5;

        var belowBurstPeak =
            _activeFloorDb <= _burstPeakDb - 2.5;

        var candidateMargin =
            _activeFloorDb - _noiseDb;

        var moderateRise =
            candidateMargin >= _exitThresholdDb &&
            candidateMargin <= 12.0;

        if (stable && belowBurstPeak && moderateRise)
        {
            _activeFloorStableBlocks++;
        }
        else
        {
            _activeFloorStableBlocks = Math.Max(
                0,
                _activeFloorStableBlocks - 1);
        }

        if (activeMs < _continuousCarrierMs ||
            _activeFloorStableBlocks < 4)
        {
            return;
        }

        _activeNoiseTracking = true;

        var target = Math.Min(
            _activeFloorDb,
            _smoothedDb);

        if (target > _noiseDb)
        {
            _noiseDb = Smooth(
                _noiseDb,
                target,
                0.12);
        }

        _activeNoiseRiseDb = Math.Max(
            _activeNoiseRiseDb,
            _noiseDb - _burstNoiseAtStartDb);
    }

    private void StartBurst(
        BlockFeatures features,
        int complexCount)
    {
        _burstActive = true;
        _burstBlocks = 1;
        _burstPeakDb = features.PowerDb;
        _burstSumDb = features.PowerDb;
        _burstDcSum = features.DcRatio;
        _burstPhaseSum = features.PhaseActivity;
        _burstAmplitudeVariationSum =
            features.AmplitudeVariation;
        _burstSamples = complexCount;

        _burstNoiseAtStartDb = _noiseDb;
        _activeFloorDb = features.PowerDb;
        _activeFloorStableBlocks = 0;
        _activeNoiseTracking = false;
        _activeNoiseRiseDb = 0.0;

        _classification = "RF event";
    }

    private void AccumulateBurst(
        BlockFeatures features,
        int complexCount)
    {
        _burstBlocks++;
        _burstPeakDb = Math.Max(
            _burstPeakDb,
            features.PowerDb);
        _burstSumDb += features.PowerDb;
        _burstDcSum += features.DcRatio;
        _burstPhaseSum += features.PhaseActivity;
        _burstAmplitudeVariationSum +=
            features.AmplitudeVariation;
        _burstSamples += complexCount;
    }

    private void CompleteBurst(
        bool forced,
        string reason)
    {
        if (_burstBlocks < MinimumBurstBlocks ||
            _lastSampleRate <= 0)
        {
            ResetBurst();
            return;
        }

        var durationMs =
            _burstSamples /
            _lastSampleRate * 1000.0;

        var meanDb =
            Mean(_burstSumDb);

        var meanDcRatio =
            Mean(_burstDcSum);

        var meanPhaseActivity =
            Mean(_burstPhaseSum);

        var meanAmplitudeVariation =
            Mean(_burstAmplitudeVariationSum);

        var snrDb =
            _burstPeakDb -
            _burstNoiseAtStartDb;

        var startSeconds =
            (_totalSamples - _burstSamples) /
            _lastSampleRate;

        var classification = ClassifyFinal(
            durationMs,
            snrDb,
            meanDcRatio,
            meanPhaseActivity,
            meanAmplitudeVariation);

        var acceptedAsCandidate =
            classification == "VDL2-CANDIDATE";

        if (!acceptedAsCandidate)
            _rejectedEvents++;

        var payload = new
        {
            protocol = classification,
            type = ClassificationType(classification),
            direction = "unknown",
            frequency_hz = _lastCenterFrequency,
            frequency_mhz =
                _lastCenterFrequency /
                1_000_000.0,
            burst_id = ++_burstId,
            start_s = Math.Round(startSeconds, 6),
            duration_ms = Math.Round(durationMs, 3),
            signal_db = Math.Round(_burstPeakDb, 3),
            mean_db = Math.Round(meanDb, 3),
            noise_start_db =
                Math.Round(_burstNoiseAtStartDb, 3),
            noise_end_db =
                Math.Round(_noiseDb, 3),
            noise_rise_db =
                Math.Round(_activeNoiseRiseDb, 3),
            snr_db = Math.Round(snrDb, 3),
            dc_ratio =
                Math.Round(meanDcRatio, 5),
            phase_activity_rad =
                Math.Round(meanPhaseActivity, 5),
            amplitude_variation =
                Math.Round(meanAmplitudeVariation, 5),
            active_noise_tracking =
                _activeNoiseTracking,
            forced_close = forced,
            close_reason = reason,
            text = ClassificationText(classification)
        };

        Enqueue(JsonSerializer.Serialize(payload));
        _completedEvents++;
        ResetBurst();
    }

    private string ClassifyLive(
        double durationMs,
        double dcRatio,
        double phaseActivity,
        double amplitudeVariation)
    {
        if (dcRatio >= 0.35)
            return "DC interference";

        if (durationMs >= _continuousCarrierMs)
        {
            if (_activeNoiseTracking &&
                _activeNoiseRiseDb >= 2.0)
            {
                return "Continuous noise rise";
            }

            if (phaseActivity >= 0.14 &&
                amplitudeVariation >= 0.04)
            {
                return "Continuous modulated";
            }

            return "Narrow carrier";
        }

        if (phaseActivity >= 0.14 &&
            amplitudeVariation >= 0.04)
        {
            return "Modulated burst";
        }

        return "RF event";
    }

    private string ClassifyFinal(
        double durationMs,
        double snrDb,
        double dcRatio,
        double phaseActivity,
        double amplitudeVariation)
    {
        if (dcRatio >= 0.35)
            return "DC-INTERFERENCE";

        if (_activeNoiseTracking &&
            _activeNoiseRiseDb >= 2.0)
        {
            return "CONTINUOUS-NOISE-RISE";
        }

        if (durationMs >= _continuousCarrierMs &&
            phaseActivity >= 0.14 &&
            amplitudeVariation >= 0.04)
        {
            return "CONTINUOUS-MODULATED";
        }

        var impulseCompatible =
            durationMs >= 5.0 &&
            durationMs <= 45.0 &&
            snrDb >= 8.0 &&
            phaseActivity >= 0.35 &&
            amplitudeVariation >= 0.30;

        if (impulseCompatible)
            return "BROADBAND-IMPULSE";

        if (durationMs >= _continuousCarrierMs &&
            phaseActivity < 0.10)
        {
            return "RF-CARRIER";
        }

        var durationCompatible =
            durationMs >= 8.0 &&
            durationMs <= 900.0;

        var snrCompatible =
            snrDb >= 4.0 &&
            snrDb <= 45.0;

        var phaseCompatible =
            phaseActivity >= 0.12;

        var amplitudeCompatible =
            amplitudeVariation >= 0.025;

        var dcCompatible =
            dcRatio < 0.20;

        if (durationCompatible &&
            snrCompatible &&
            phaseCompatible &&
            amplitudeCompatible &&
            dcCompatible)
        {
            return "VDL2-CANDIDATE";
        }

        if (durationMs >= _continuousCarrierMs)
            return "CONTINUOUS-MODULATED";

        return "RF-BURST";
    }

    private static string ClassificationType(
        string classification) =>
        classification switch
        {
            "VDL2-CANDIDATE" =>
                "spectral_vdl2_candidate",
            "BROADBAND-IMPULSE" =>
                "broadband_impulse",
            "CONTINUOUS-NOISE-RISE" =>
                "continuous_noise_rise",
            "CONTINUOUS-MODULATED" =>
                "continuous_modulated",
            "DC-INTERFERENCE" =>
                "dc_interference",
            "RF-CARRIER" =>
                "continuous_or_narrow_carrier",
            _ =>
                "unclassified_rf_burst"
        };

    private static string ClassificationText(
        string classification) =>
        classification switch
        {
            "VDL2-CANDIDATE" =>
                "Bounded modulated RF burst compatible with the spectral VDL2 pre-classifier; symbol structure not yet validated",
            "BROADBAND-IMPULSE" =>
                "Short broadband impulse; rejected as a normal VDL2 packet",
            "CONTINUOUS-NOISE-RISE" =>
                "Slow broadband noise-floor rise tracked during an active event",
            "CONTINUOUS-MODULATED" =>
                "Long modulated or noise-like signal; routed to continuous/interference diagnostics",
            "DC-INTERFERENCE" =>
                "Strong DC or centre spike detected; rejected as VDL2",
            "RF-CARRIER" =>
                "Continuous or narrow RF carrier; rejected as VDL2",
            _ =>
                "RF burst detected but insufficient characteristics for VDL2 classification"
        };

    private double Mean(double sum) =>
        _burstBlocks > 0
            ? sum / _burstBlocks
            : 0.0;

    private void Enqueue(string json)
    {
        while (
            Volatile.Read(ref _queued) >= MaximumQueueSize &&
            _jsonQueue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _queued);
        }

        _jsonQueue.Enqueue(json);
        Interlocked.Increment(ref _queued);
    }

    private void ResetBurst()
    {
        _burstActive = false;
        _burstBlocks = 0;
        _burstPeakDb = -120.0;
        _burstSumDb = 0;
        _burstDcSum = 0;
        _burstPhaseSum = 0;
        _burstAmplitudeVariationSum = 0;
        _burstSamples = 0;

        _burstNoiseAtStartDb = _noiseDb;
        _activeFloorDb = _noiseDb;
        _activeFloorStableBlocks = 0;
        _activeNoiseTracking = false;
        _activeNoiseRiseDb = 0;

        _classification =
            _noiseReady
                ? "Idle"
                : "Noise learning";
    }

    private static double Smooth(
        double current,
        double value,
        double alpha) =>
        current + alpha * (value - current);
}
