// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal readonly record struct SignalSnapshot(
    long Blocks,
    long Samples,
    double SampleRate,
    double RmsDbfs,
    double PeakDbfs,
    double DcI,
    double DcQ,
    long ClippedSamples,
    long BurstCount,
    DateTimeOffset LastBlockAt);

internal sealed class SignalMetrics
{
    private readonly object _gate = new();
    private long _blocks;
    private long _samples;
    private long _clipped;
    private long _bursts;
    private double _sampleRate;
    private double _rmsDbfs = -120;
    private double _peakDbfs = -120;
    private double _dcI;
    private double _dcQ;
    private double _noiseFloorDbfs = -90;
    private bool _burstActive;
    private DateTimeOffset _lastBlockAt;

    public unsafe void Process(float* interleavedIq, int complexLength, double sampleRate)
    {
        if (interleavedIq is null || complexLength <= 0)
            return;

        // Decimate the statistics workload; this never modifies the IQ buffer.
        var stride = Math.Max(1, complexLength / 4096);
        double sumPower = 0;
        double sumI = 0;
        double sumQ = 0;
        double peakPower = 0;
        long clipped = 0;
        long measured = 0;

        for (var n = 0; n < complexLength; n += stride)
        {
            var i = interleavedIq[n * 2];
            var q = interleavedIq[n * 2 + 1];

            if (!float.IsFinite(i) || !float.IsFinite(q))
                continue;

            var power = (double)i * i + (double)q * q;
            sumPower += power;
            sumI += i;
            sumQ += q;
            peakPower = Math.Max(peakPower, power);
            if (Math.Abs(i) >= 0.999f || Math.Abs(q) >= 0.999f)
                clipped++;
            measured++;
        }

        if (measured == 0)
            return;

        var meanPower = Math.Max(sumPower / measured, 1e-20);
        var rmsDbfs = 10.0 * Math.Log10(meanPower);
        var peakDbfs = 10.0 * Math.Log10(Math.Max(peakPower, 1e-20));

        lock (_gate)
        {
            _blocks++;
            _samples += complexLength;
            _sampleRate = sampleRate;
            _rmsDbfs = Smooth(_rmsDbfs, rmsDbfs, 0.15);
            _peakDbfs = peakDbfs;
            _dcI = Smooth(_dcI, sumI / measured, 0.10);
            _dcQ = Smooth(_dcQ, sumQ / measured, 0.10);
            _clipped += clipped;
            _lastBlockAt = DateTimeOffset.Now;

            // Passive energy-burst detector. It is only a diagnostic, not a VDL2 decoder.
            if (!_burstActive)
            {
                _noiseFloorDbfs = Smooth(_noiseFloorDbfs, rmsDbfs, 0.005);
                if (rmsDbfs > _noiseFloorDbfs + 7.0)
                {
                    _burstActive = true;
                    _bursts++;
                }
            }
            else if (rmsDbfs < _noiseFloorDbfs + 3.0)
            {
                _burstActive = false;
            }
        }
    }

    public SignalSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SignalSnapshot(
                _blocks, _samples, _sampleRate, _rmsDbfs, _peakDbfs,
                _dcI, _dcQ, _clipped, _bursts, _lastBlockAt);
        }
    }

    private static double Smooth(double current, double value, double alpha) =>
        current + alpha * (value - current);
}
