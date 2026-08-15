// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed record D8pskAnalysisResult(
    string CaptureId,
    DateTimeOffset CompletedAt,
    bool Success,
    bool D8pskCandidate,
    bool DiagnosticOnly,
    double SymbolRate,
    double SamplesPerSymbol,
    double BestBurstStartMs,
    double BestBurstDurationMs,
    double EstimatedSnrDb,
    double EstimatedFrequencyOffsetHz,
    double TimingOffsetSamples,
    int SymbolCount,
    double DifferentialPhaseRmsDeg,
    double ClusterScore,
    double R8,
    double R8Threshold,
    double R8CorrectedPValue,
    double TimingMedianR8,
    double TimingContrast,
    double TimingRobustZ,
    double AmplitudeCv,
    string BitPreview,
    string ReportPath,
    string SymbolsCsvPath,
    string Status,
    string? Error,
    Vdl2FrameSyncResult? Frame = null);

internal sealed class D8pskSymbolAnalyzer : IDisposable
{
    private readonly record struct BurstRegion(
        int Start,
        int End,
        double NoisePower,
        double SignalPower,
        double SnrDb);

    private readonly record struct TimingResult(
        double OffsetSamples,
        Complex[] Symbols,
        int[] Sectors,
        double[] DifferentialPhases,
        double[] PhaseErrors,
        double PhaseRmsRad,
        double ClusterScore,
        double R8,
        double R8Threshold,
        double R8CorrectedPValue,
        double TimingMedianR8,
        double TimingContrast,
        double TimingRobustZ,
        double AmplitudeCv);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private const double Vdl2SymbolRate = 10_500.0;
    private const double RrcAlpha = 0.6;
    private const int TimingPhases = 128;
    private const int MaximumCsvSymbols = 4096;

    private static readonly string[] GrayBits =
    [
        "000",
        "001",
        "011",
        "010",
        "110",
        "111",
        "101",
        "100"
    ];

    public string AnalysisDirectory { get; }

    public D8pskSymbolAnalyzer(
        string? analysisDirectory = null)
    {
        AnalysisDirectory =
            string.IsNullOrWhiteSpace(analysisDirectory)
                ? RuntimeDataPaths.AnalysisDirectory
                : Path.GetFullPath(analysisDirectory);

        Directory.CreateDirectory(
            AnalysisDirectory);
    }

    public async Task<D8pskAnalysisResult> AnalyzeAsync(
        CaptureInfo capture,
        bool diagnosticMode = false,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D8pskSymbolAnalyzer));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await Task.Run(
                () => AnalyzeCore(
                    capture,
                    diagnosticMode,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            return new D8pskAnalysisResult(
                capture.Id,
                DateTimeOffset.Now,
                false,
                false,
                diagnosticMode,
                Vdl2SymbolRate,
                capture.SampleRate / Vdl2SymbolRate,
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
                "analysis_failed",
                ex.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    private D8pskAnalysisResult AnalyzeCore(
        CaptureInfo capture,
        bool diagnosticMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var baseName =
            Path.GetFileNameWithoutExtension(capture.IqPath);

        var reportPath = Path.Combine(
            AnalysisDirectory,
            baseName + ".d8psk.json");

        var csvPath = Path.Combine(
            AnalysisDirectory,
            baseName + ".symbols.csv");

        if (capture.Limited && !diagnosticMode)
        {
            return CreateTerminalResult(
                capture,
                reportPath,
                "limited_capture_rejected",
                diagnosticOnly: false,
                "Normal D8PSK analysis rejects captures closed by the hard duration limit.");
        }

        if (capture.ContinuousOrInterference && !diagnosticMode)
        {
            return CreateTerminalResult(
                capture,
                reportPath,
                "continuous_or_interference_rejected",
                diagnosticOnly: false,
                "Normal D8PSK analysis rejects continuous or interference captures.");
        }

        if (!File.Exists(capture.IqPath))
        {
            throw new FileNotFoundException(
                "IQ capture not found.",
                capture.IqPath);
        }

        if (capture.SampleRate <= Vdl2SymbolRate * 2.0)
        {
            throw new InvalidOperationException(
                $"Sample rate {capture.SampleRate:0.###} S/s is too low.");
        }

        var iq = ReadIq(capture.IqPath);
        if (iq.Length < 512)
        {
            return CreateTerminalResult(
                capture,
                reportPath,
                "capture_too_short",
                diagnosticMode,
                "Capture is too short for symbol analysis.");
        }

        RemoveDc(iq);

        var metadataHints =
            ReadMetadataHints(capture.MetadataPath);

        var burst = FindBestBurst(
            iq,
            capture.SampleRate,
            metadataHints.PreBufferMs,
            metadataHints.PostBufferMs);

        if (burst is null)
        {
            return CreateTerminalResult(
                capture,
                reportPath,
                "no_bounded_burst",
                diagnosticMode,
                "No RF region had both a detectable leading edge and a detectable trailing return to noise.");
        }

        var boundedBurst = burst.Value;
        var segmentLength =
            boundedBurst.End -
            boundedBurst.Start;

        if (segmentLength < 128)
        {
            return CreateTerminalResult(
                capture,
                reportPath,
                "bounded_burst_too_short",
                diagnosticMode,
                "The bounded RF region is too short for timing recovery.");
        }

        var frequencyOffset =
            EstimateSpectralCentroidOffset(
                iq,
                boundedBurst.Start,
                boundedBurst.End,
                capture.SampleRate);

        CorrectFrequency(
            iq,
            capture.SampleRate,
            frequencyOffset);

        var taps = CreateRootRaisedCosineTaps(
            capture.SampleRate,
            Vdl2SymbolRate,
            RrcAlpha,
            spanSymbols: 10);

        var filtered = ApplyFir(iq, taps);

        var timing = FindBestTiming(
            filtered,
            boundedBurst.Start,
            boundedBurst.End,
            capture.SampleRate,
            Vdl2SymbolRate);

        var phaseRmsDeg =
            timing.PhaseRmsRad *
            180.0 /
            Math.PI;

        var bitPreview = BuildBitPreview(
            timing.Sectors,
            maximumSymbols: 128);

        var frame = Vdl2FrameDecoder.Decode(
            filtered,
            boundedBurst.Start,
            boundedBurst.End,
            capture.SampleRate,
            Vdl2SymbolRate);

        var r8StatisticallySignificant =
            timing.R8 >= timing.R8Threshold &&
            timing.R8CorrectedPValue <= 0.001;

        var minimumTimingContrast = Math.Max(
            0.04,
            timing.R8Threshold * 0.25);

        var timingClearlySeparated =
            timing.TimingContrast >= minimumTimingContrast &&
            timing.TimingRobustZ >= 3.0;

        var rawD8pskCandidate =
            timing.Symbols.Length >= 80 &&
            r8StatisticallySignificant &&
            timingClearlySeparated &&
            timing.AmplitudeCv <= 1.20 &&
            Math.Abs(frequencyOffset) <= 5_000.0 &&
            boundedBurst.SnrDb >= 5.0;

        var d8pskCandidate =
            rawD8pskCandidate &&
            !diagnosticMode &&
            !capture.Limited &&
            !capture.ContinuousOrInterference;

        var headerAccepted =
            frame.HeaderValid &&
            !diagnosticMode &&
            !capture.Limited &&
            !capture.ContinuousOrInterference;

        var finalCandidate =
            d8pskCandidate ||
            headerAccepted;

        string status;

        if (headerAccepted)
        {
            status = frame.Payload?.Status ?? frame.Status;
        }
        else if (diagnosticMode)
        {
            status = "diagnostic_only";
        }
        else if (frame.PreambleFound && frame.HeaderAvailable)
        {
            status = frame.Status;
        }
        else if (d8pskCandidate)
        {
            status = frame.PreambleFound
                ? "VDL2-SYMBOL-CANDIDATE"
                : "vdl2_symbol_candidate_no_preamble";
        }
        else if (!r8StatisticallySignificant)
        {
            status = "r8_not_significant";
        }
        else if (!timingClearlySeparated)
        {
            status = "timing_contrast_insufficient";
        }
        else
        {
            status = frame.Status;
        }

        WriteSymbolsCsv(csvPath, timing);

        WriteReport(
            reportPath,
            capture,
            boundedBurst,
            frequencyOffset,
            timing,
            phaseRmsDeg,
            bitPreview,
            finalCandidate,
            frame,
            diagnosticMode,
            status,
            minimumTimingContrast,
            csvPath);

        return new D8pskAnalysisResult(
            capture.Id,
            DateTimeOffset.Now,
            true,
            finalCandidate,
            diagnosticMode,
            Vdl2SymbolRate,
            capture.SampleRate / Vdl2SymbolRate,
            boundedBurst.Start /
                capture.SampleRate *
                1000.0,
            (boundedBurst.End - boundedBurst.Start) /
                capture.SampleRate *
                1000.0,
            boundedBurst.SnrDb,
            frequencyOffset,
            timing.OffsetSamples,
            timing.Symbols.Length,
            phaseRmsDeg,
            timing.ClusterScore,
            timing.R8,
            timing.R8Threshold,
            timing.R8CorrectedPValue,
            timing.TimingMedianR8,
            timing.TimingContrast,
            timing.TimingRobustZ,
            timing.AmplitudeCv,
            bitPreview,
            reportPath,
            csvPath,
            status,
            null,
            frame);
    }

    private static Complex[] ReadIq(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);

        if (stream.Length % 8 != 0)
        {
            throw new InvalidDataException(
                "IQ file size is not a multiple of one complex float32 sample.");
        }

        var sampleCount = checked((int)(stream.Length / 8));
        var result = new Complex[sampleCount];

        using var reader = new BinaryReader(
            stream,
            Encoding.UTF8,
            leaveOpen: false);

        for (var i = 0; i < sampleCount; i++)
        {
            var inPhase = reader.ReadSingle();
            var quadrature = reader.ReadSingle();

            if (!float.IsFinite(inPhase) ||
                !float.IsFinite(quadrature))
            {
                result[i] = Complex.Zero;
            }
            else
            {
                result[i] = new Complex(
                    inPhase,
                    quadrature);
            }
        }

        return result;
    }

    private static void RemoveDc(Complex[] samples)
    {
        if (samples.Length == 0)
            return;

        Complex sum = Complex.Zero;

        foreach (var sample in samples)
            sum += sample;

        var mean = sum / samples.Length;

        for (var i = 0; i < samples.Length; i++)
            samples[i] -= mean;
    }

    private static (double PreBufferMs, double PostBufferMs)
        ReadMetadataHints(string metadataPath)
    {
        try
        {
            if (!File.Exists(metadataPath))
                return (0, 0);

            using var document =
                JsonDocument.Parse(
                    File.ReadAllText(metadataPath));

            var root = document.RootElement;

            var pre = root.TryGetProperty(
                "pre_buffer_ms",
                out var preElement)
                ? preElement.GetDouble()
                : 0.0;

            var post = root.TryGetProperty(
                "post_buffer_ms",
                out var postElement)
                ? postElement.GetDouble()
                : 0.0;

            return (
                Math.Max(0, pre),
                Math.Max(0, post));
        }
        catch
        {
            return (0, 0);
        }
    }

    private static BurstRegion? FindBestBurst(
        Complex[] samples,
        double sampleRate,
        double preBufferMs,
        double postBufferMs)
    {
        _ = preBufferMs;
        _ = postBufferMs;

        var power = new double[samples.Length];

        for (var i = 0; i < samples.Length; i++)
        {
            power[i] =
                samples[i].Magnitude *
                samples[i].Magnitude;
        }

        var window = Math.Max(
            8,
            (int)Math.Round(
                sampleRate * 0.0015));

        var smoothed =
            MovingAverage(power, window);

        var sorted =
            (double[])smoothed.Clone();

        Array.Sort(sorted);

        var percentileIndex = Math.Clamp(
            (int)(sorted.Length * 0.30),
            0,
            sorted.Length - 1);

        var noisePower = Math.Max(
            sorted[percentileIndex],
            1e-20);

        var threshold =
            noisePower * 4.0;

        var boundaryThreshold =
            noisePower * 2.0;

        var maximumGap = Math.Max(
            1,
            (int)Math.Round(
                sampleRate * 0.002));

        var minimumLength = Math.Max(
            16,
            (int)Math.Round(
                sampleRate * 0.008));

        var maximumLength = Math.Max(
            minimumLength,
            (int)Math.Round(
                sampleRate * 1.2));

        var guardLength = Math.Max(
            8,
            (int)Math.Round(
                sampleRate * 0.008));

        var bestStart = -1;
        var bestEnd = -1;
        var bestScore =
            double.NegativeInfinity;

        var bestSignalPower =
            noisePower;

        var start = -1;
        var lastActive = -1;

        double MeanRange(
            int rangeStart,
            int rangeEnd)
        {
            if (rangeEnd <= rangeStart)
                return double.PositiveInfinity;

            var sum = 0.0;

            for (var index = rangeStart;
                 index < rangeEnd;
                 index++)
            {
                sum += smoothed[index];
            }

            return sum /
                (rangeEnd - rangeStart);
        }

        void EvaluateSegment(
            int segmentStart,
            int segmentEnd)
        {
            var length =
                segmentEnd -
                segmentStart;

            if (length < minimumLength ||
                length > maximumLength)
            {
                return;
            }

            if (segmentStart < guardLength ||
                segmentEnd >
                    smoothed.Length -
                    guardLength)
            {
                return;
            }

            var leadingNoise = MeanRange(
                segmentStart - guardLength,
                segmentStart);

            var trailingNoise = MeanRange(
                segmentEnd,
                segmentEnd + guardLength);

            if (leadingNoise > boundaryThreshold ||
                trailingNoise > boundaryThreshold)
            {
                return;
            }

            var mean = MeanRange(
                segmentStart,
                segmentEnd);

            var snrLinear = Math.Max(
                mean / noisePower,
                1e-12);

            var snrDb =
                10.0 *
                Math.Log10(snrLinear);

            if (snrDb < 5.0)
                return;

            var edgeContrastDb =
                10.0 *
                Math.Log10(
                    Math.Max(
                        mean /
                        Math.Max(
                            leadingNoise,
                            trailingNoise),
                        1e-12));

            if (edgeContrastDb < 3.0)
                return;

            var durationSeconds =
                length / sampleRate;

            var score =
                snrDb *
                Math.Sqrt(
                    Math.Max(
                        durationSeconds,
                        1e-6));

            if (score <= bestScore)
                return;

            bestScore = score;
            bestStart = segmentStart;
            bestEnd = segmentEnd;
            bestSignalPower = mean;
        }

        for (var i = 0;
             i < smoothed.Length;
             i++)
        {
            if (smoothed[i] > threshold)
            {
                if (start < 0)
                    start = i;

                lastActive = i;
                continue;
            }

            if (start >= 0 &&
                i - lastActive > maximumGap)
            {
                EvaluateSegment(
                    start,
                    lastActive + 1);

                start = -1;
                lastActive = -1;
            }
        }

        if (start >= 0)
        {
            EvaluateSegment(
                start,
                lastActive + 1);
        }

        if (bestStart < 0)
            return null;

        var expansion = Math.Max(
            1,
            (int)Math.Round(
                sampleRate * 0.002));

        bestStart = Math.Max(
            guardLength,
            bestStart - expansion);

        bestEnd = Math.Min(
            samples.Length - guardLength,
            bestEnd + expansion);

        var snr = 10.0 * Math.Log10(
            Math.Max(
                bestSignalPower /
                noisePower,
                1e-12));

        return new BurstRegion(
            bestStart,
            bestEnd,
            noisePower,
            bestSignalPower,
            snr);
    }

    private static double[] MovingAverage(
        double[] values,
        int window)
    {
        var result = new double[values.Length];
        var prefix = new double[values.Length + 1];

        for (var i = 0; i < values.Length; i++)
            prefix[i + 1] = prefix[i] + values[i];

        var half = window / 2;

        for (var i = 0; i < values.Length; i++)
        {
            var start = Math.Max(0, i - half);
            var end = Math.Min(
                values.Length,
                i + half + 1);

            result[i] =
                (prefix[end] - prefix[start]) /
                Math.Max(1, end - start);
        }

        return result;
    }

    private static double EstimateSpectralCentroidOffset(
        Complex[] samples,
        int start,
        int end,
        double sampleRate)
    {
        var length = end - start;
        var fftSize = 1;

        while (fftSize < length)
            fftSize <<= 1;

        fftSize = Math.Clamp(
            fftSize,
            1024,
            131_072);

        var fft = new Complex[fftSize];
        var copyLength = Math.Min(
            length,
            fftSize);

        for (var i = 0; i < copyLength; i++)
        {
            var window =
                copyLength <= 1
                    ? 1.0
                    : 0.5 -
                      0.5 * Math.Cos(
                          2.0 * Math.PI * i /
                          (copyLength - 1));

            fft[i] = samples[start + i] * window;
        }

        FftInPlace(fft);

        var numerator = 0.0;
        var denominator = 0.0;
        var maximumOffset = Math.Min(
            10_000.0,
            sampleRate * 0.45);

        for (var bin = 0; bin < fftSize; bin++)
        {
            var frequency =
                bin <= fftSize / 2
                    ? bin * sampleRate / fftSize
                    : (bin - fftSize) *
                      sampleRate / fftSize;

            if (Math.Abs(frequency) >
                maximumOffset)
            {
                continue;
            }

            var power =
                fft[bin].Magnitude *
                fft[bin].Magnitude;

            numerator += frequency * power;
            denominator += power;
        }

        if (denominator <= 1e-20)
            return 0.0;

        return Math.Clamp(
            numerator / denominator,
            -maximumOffset,
            maximumOffset);
    }

    private static void CorrectFrequency(
        Complex[] samples,
        double sampleRate,
        double frequencyOffset)
    {
        if (Math.Abs(frequencyOffset) < 0.01)
            return;

        var phaseIncrement =
            -2.0 * Math.PI *
            frequencyOffset /
            sampleRate;

        var oscillator =
            Complex.One;

        var step = Complex.FromPolarCoordinates(
            1.0,
            phaseIncrement);

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= oscillator;
            oscillator *= step;

            if ((i & 2047) == 2047)
            {
                var magnitude =
                    oscillator.Magnitude;

                if (magnitude > 0)
                    oscillator /= magnitude;
            }
        }
    }

    private static double[] CreateRootRaisedCosineTaps(
        double sampleRate,
        double symbolRate,
        double alpha,
        int spanSymbols)
    {
        var samplesPerSymbol =
            sampleRate / symbolRate;

        var half =
            (int)Math.Ceiling(
                spanSymbols *
                samplesPerSymbol / 2.0);

        var taps = new double[
            checked(half * 2 + 1)];

        for (var index = -half;
             index <= half;
             index++)
        {
            var t =
                index / samplesPerSymbol;

            double value;

            if (Math.Abs(t) < 1e-12)
            {
                value =
                    1.0 +
                    alpha *
                    (4.0 / Math.PI - 1.0);
            }
            else if (
                Math.Abs(
                    Math.Abs(t) -
                    1.0 / (4.0 * alpha)) <
                1e-8)
            {
                value =
                    alpha / Math.Sqrt(2.0) *
                    (
                        (1.0 + 2.0 / Math.PI) *
                        Math.Sin(
                            Math.PI /
                            (4.0 * alpha))
                        +
                        (1.0 - 2.0 / Math.PI) *
                        Math.Cos(
                            Math.PI /
                            (4.0 * alpha))
                    );
            }
            else
            {
                var numerator =
                    Math.Sin(
                        Math.PI * t *
                        (1.0 - alpha))
                    +
                    4.0 * alpha * t *
                    Math.Cos(
                        Math.PI * t *
                        (1.0 + alpha));

                var denominator =
                    Math.PI * t *
                    (
                        1.0 -
                        Math.Pow(
                            4.0 * alpha * t,
                            2.0)
                    );

                value =
                    Math.Abs(denominator) < 1e-12
                        ? 0.0
                        : numerator / denominator;
            }

            taps[index + half] = value;
        }

        var energy = 0.0;
        foreach (var tap in taps)
            energy += tap * tap;

        var normalization =
            Math.Sqrt(Math.Max(energy, 1e-20));

        for (var i = 0; i < taps.Length; i++)
            taps[i] /= normalization;

        return taps;
    }

    private static Complex[] ApplyFir(
        Complex[] samples,
        double[] taps)
    {
        var output = new Complex[samples.Length];
        var half = taps.Length / 2;

        for (var i = 0; i < samples.Length; i++)
        {
            Complex sum = Complex.Zero;

            for (var tap = 0;
                 tap < taps.Length;
                 tap++)
            {
                var sourceIndex =
                    i + tap - half;

                if ((uint)sourceIndex >=
                    (uint)samples.Length)
                {
                    continue;
                }

                sum += samples[sourceIndex] *
                       taps[tap];
            }

            output[i] = sum;
        }

        return output;
    }

    private static TimingResult FindBestTiming(
        Complex[] filtered,
        int start,
        int end,
        double sampleRate,
        double symbolRate)
    {
        var samplesPerSymbol =
            sampleRate / symbolRate;

        var candidates =
            new List<TimingResult>(
                TimingPhases);

        for (var phaseIndex = 0;
             phaseIndex < TimingPhases;
             phaseIndex++)
        {
            var offset =
                phaseIndex /
                (double)TimingPhases *
                samplesPerSymbol;

            var symbols = SampleSymbols(
                filtered,
                start,
                end,
                offset,
                samplesPerSymbol);

            if (symbols.Length < 16)
                continue;

            var sectors =
                new int[symbols.Length - 1];

            var phases =
                new double[symbols.Length - 1];

            var errors =
                new double[symbols.Length - 1];

            var squaredError = 0.0;
            double r8Real = 0.0;
            double r8Imaginary = 0.0;

            for (var i = 1;
                 i < symbols.Length;
                 i++)
            {
                var differential =
                    symbols[i] *
                    Complex.Conjugate(
                        symbols[i - 1]);

                var phase = Math.Atan2(
                    differential.Imaginary,
                    differential.Real);

                var sector = NormalizeSector(
                    (int)Math.Round(
                        phase /
                        (Math.PI / 4.0)));

                var expected =
                    SectorToSignedAngle(
                        sector);

                var error = WrapPhase(
                    phase - expected);

                sectors[i - 1] = sector;
                phases[i - 1] = phase;
                errors[i - 1] = error;
                squaredError += error * error;

                var eighthPhase =
                    8.0 * phase;

                r8Real += Math.Cos(eighthPhase);
                r8Imaginary += Math.Sin(eighthPhase);
            }

            var observationCount =
                errors.Length;

            var phaseRms = Math.Sqrt(
                squaredError /
                observationCount);

            var clusterScore = Math.Clamp(
                1.0 -
                phaseRms /
                (Math.PI / 8.0),
                0.0,
                1.0);

            var r8 = Math.Sqrt(
                r8Real * r8Real +
                r8Imaginary * r8Imaginary) /
                observationCount;

            const double familyFalseAlarm = 0.001;

            var perTimingFalseAlarm =
                familyFalseAlarm /
                TimingPhases;

            var r8Threshold = Math.Clamp(
                Math.Sqrt(
                    -Math.Log(
                        perTimingFalseAlarm) /
                    observationCount),
                0.0,
                1.0);

            var correctedPValue = Math.Clamp(
                TimingPhases *
                Math.Exp(
                    -observationCount *
                    r8 *
                    r8),
                0.0,
                1.0);

            var amplitudeCv =
                ComputeAmplitudeCv(symbols);

            candidates.Add(
                new TimingResult(
                    offset,
                    symbols,
                    sectors,
                    phases,
                    errors,
                    phaseRms,
                    clusterScore,
                    r8,
                    r8Threshold,
                    correctedPValue,
                    0.0,
                    0.0,
                    0.0,
                    amplitudeCv));
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "Timing recovery produced no valid symbol sequence.");
        }

        var best = candidates
            .OrderByDescending(
                candidate =>
                    candidate.R8 -
                    0.05 *
                    Math.Min(
                        candidate.AmplitudeCv,
                        2.0))
            .First();

        var r8Values = candidates
            .Select(
                candidate =>
                    candidate.R8)
            .OrderBy(value => value)
            .ToArray();

        var medianR8 =
            Median(r8Values);

        var deviations = r8Values
            .Select(
                value =>
                    Math.Abs(
                        value -
                        medianR8))
            .OrderBy(value => value)
            .ToArray();

        var mad =
            Median(deviations);

        var robustSigma =
            Math.Max(
                1.4826 * mad,
                1.0 /
                Math.Sqrt(
                    2.0 *
                    Math.Max(
                        1,
                        best.DifferentialPhases.Length)));

        var timingContrast =
            best.R8 -
            medianR8;

        var timingRobustZ =
            timingContrast /
            robustSigma;

        return best with
        {
            TimingMedianR8 = medianR8,
            TimingContrast = timingContrast,
            TimingRobustZ = timingRobustZ
        };
    }

    private static Complex[] SampleSymbols(
        Complex[] samples,
        int start,
        int end,
        double offset,
        double samplesPerSymbol)
    {
        var list = new List<Complex>();
        var position = start + offset;

        while (position < end - 1)
        {
            var index =
                (int)Math.Floor(position);

            var fraction =
                position - index;

            var value =
                samples[index] *
                (1.0 - fraction)
                +
                samples[index + 1] *
                fraction;

            list.Add(value);
            position += samplesPerSymbol;
        }

        return list.ToArray();
    }

    private static double ComputeAmplitudeCv(
        Complex[] symbols)
    {
        var sum = 0.0;

        foreach (var symbol in symbols)
            sum += symbol.Magnitude;

        var mean =
            sum / Math.Max(
                1,
                symbols.Length);

        if (mean <= 1e-20)
            return double.PositiveInfinity;

        var variance = 0.0;

        foreach (var symbol in symbols)
        {
            var difference =
                symbol.Magnitude - mean;

            variance +=
                difference * difference;
        }

        variance /=
            Math.Max(
                1,
                symbols.Length);

        return Math.Sqrt(variance) / mean;
    }

    private static int NormalizeSector(int sector)
    {
        sector %= 8;
        if (sector < 0)
            sector += 8;
        return sector;
    }

    private static double SectorToSignedAngle(
        int sector)
    {
        var normalized =
            NormalizeSector(sector);

        var angle =
            normalized *
            Math.PI / 4.0;

        if (angle > Math.PI)
            angle -= 2.0 * Math.PI;

        return angle;
    }

    private static double WrapPhase(double phase)
    {
        while (phase <= -Math.PI)
            phase += 2.0 * Math.PI;

        while (phase > Math.PI)
            phase -= 2.0 * Math.PI;

        return phase;
    }

    private static string BuildBitPreview(
        int[] sectors,
        int maximumSymbols)
    {
        var builder = new StringBuilder();
        var count = Math.Min(
            sectors.Length,
            maximumSymbols);

        for (var i = 0; i < count; i++)
            builder.Append(
                GrayBits[
                    NormalizeSector(
                        sectors[i])]);

        return builder.ToString();
    }

    private static void WriteSymbolsCsv(
        string path,
        TimingResult timing)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        using var writer = new StreamWriter(
            path,
            append: false,
            Encoding.UTF8);

        writer.WriteLine(
            "index,i,q,amplitude,diff_phase_deg,sector,bits,error_deg");

        var count = Math.Min(
            timing.Symbols.Length,
            MaximumCsvSymbols);

        for (var i = 0; i < count; i++)
        {
            var symbol = timing.Symbols[i];

            if (i == 0)
            {
                writer.WriteLine(
                    FormattableString.Invariant(
                        $"{i},{symbol.Real:R},{symbol.Imaginary:R},{symbol.Magnitude:R},,,,"));
                continue;
            }

            var differentialIndex = i - 1;
            var sector =
                timing.Sectors[
                    differentialIndex];

            writer.WriteLine(
                FormattableString.Invariant(
                    $"{i},{symbol.Real:R},{symbol.Imaginary:R},{symbol.Magnitude:R},{timing.DifferentialPhases[differentialIndex] * 180.0 / Math.PI:R},{sector},{GrayBits[sector]},{timing.PhaseErrors[differentialIndex] * 180.0 / Math.PI:R}"));
        }
    }

    private static void WriteReport(
        string path,
        CaptureInfo capture,
        BurstRegion burst,
        double frequencyOffset,
        TimingResult timing,
        double phaseRmsDeg,
        string bitPreview,
        bool d8pskCandidate,
        Vdl2FrameSyncResult frame,
        bool diagnosticOnly,
        string status,
        double minimumTimingContrast,
        string csvPath)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        var sectorHistogram =
            new int[8];

        foreach (var sector in timing.Sectors)
        {
            sectorHistogram[
                NormalizeSector(sector)]++;
        }

        var report = new
        {
            schema_version = 2,
            stage =
                "statistical_d8psk_symbol_diagnostics",
            completed_at =
                DateTimeOffset.Now,
            analysis_mode =
                diagnosticOnly
                    ? "diagnostic"
                    : "normal",
            capture = new
            {
                capture.Id,
                capture.CreatedAt,
                capture.FrequencyHz,
                frequency_mhz =
                    capture.FrequencyHz /
                    1_000_000.0,
                capture.SampleRate,
                capture.ComplexSamples,
                capture.DurationMs,
                capture.TriggerClassification,
                capture.CompletionReason,
                capture.Limited,
                capture.ContinuousOrInterference,
                capture.QualityScore,
                capture.RecommendedForD8psk,
                iq_file =
                    Path.GetFileName(
                        capture.IqPath),
                metadata_file =
                    Path.GetFileName(
                        capture.MetadataPath)
            },
            physical_layer = new
            {
                modulation = "D8PSK",
                symbol_rate =
                    Vdl2SymbolRate,
                bit_rate = 31_500,
                pulse_shape =
                    "root_raised_cosine",
                rolloff = RrcAlpha,
                samples_per_symbol =
                    capture.SampleRate /
                    Vdl2SymbolRate
            },
            burst = new
            {
                bounded = true,
                start_sample =
                    burst.Start,
                end_sample =
                    burst.End,
                start_ms =
                    burst.Start /
                    capture.SampleRate *
                    1000.0,
                duration_ms =
                    (burst.End - burst.Start) /
                    capture.SampleRate *
                    1000.0,
                estimated_noise_dbfs =
                    10.0 *
                    Math.Log10(
                        Math.Max(
                            burst.NoisePower,
                            1e-20)),
                estimated_signal_dbfs =
                    10.0 *
                    Math.Log10(
                        Math.Max(
                            burst.SignalPower,
                            1e-20)),
                estimated_snr_db =
                    burst.SnrDb
            },
            synchronization = new
            {
                estimated_frequency_offset_hz =
                    frequencyOffset,
                timing_offset_samples =
                    timing.OffsetSamples,
                timing_phases_tested =
                    TimingPhases,
                symbol_count =
                    timing.Symbols.Length,
                timing_median_r8 =
                    timing.TimingMedianR8,
                timing_contrast =
                    timing.TimingContrast,
                minimum_timing_contrast =
                    minimumTimingContrast,
                timing_robust_z =
                    timing.TimingRobustZ,
                timing_contrast_valid =
                    timing.TimingContrast >=
                        minimumTimingContrast &&
                    timing.TimingRobustZ >= 3.0
            },
            statistical_eight_phase_test =
                new
                {
                    metric = "R8",
                    definition =
                        "abs(mean(exp(j*8*differential_phase)))",
                    r8 = timing.R8,
                    threshold =
                        timing.R8Threshold,
                    family_false_alarm_probability =
                        0.001,
                    timing_trials =
                        TimingPhases,
                    corrected_p_value =
                        timing.R8CorrectedPValue,
                    significant =
                        timing.R8 >=
                            timing.R8Threshold &&
                        timing.R8CorrectedPValue <=
                            0.001
                },
            constellation = new
            {
                differential_phase_rms_deg =
                    phaseRmsDeg,
                legacy_cluster_score =
                    timing.ClusterScore,
                amplitude_cv =
                    timing.AmplitudeCv,
                sector_histogram =
                    sectorHistogram,
                warning =
                    "Phase RMS is diagnostic only and is not used as the primary D8PSK acceptance test."
            },
            frame_sync = new
            {
                preamble_found = frame.PreambleFound,
                timing_phase_index = frame.TimingPhaseIndex,
                timing_offset_samples = frame.TimingOffsetSamples,
                preamble_symbol_index = frame.PreambleSymbolIndex,
                preamble_rms_deg = frame.PreambleRmsDeg,
                preamble_correlation = frame.PreambleCorrelation,
                residual_frequency_offset_hz = frame.ResidualFrequencyOffsetHz,
                residual_phase_slope_rad_per_symbol = frame.ResidualPhaseSlopeRadPerSymbol,
                symbols_after_preamble = frame.SymbolsAfterPreamble,
                raw_bit_count = frame.RawBitCount,
                status = frame.PreambleFound ? "preamble_found" : frame.Status
            },
            physical_header = new
            {
                available = frame.HeaderAvailable,
                fec_valid = frame.HeaderFecValid,
                corrected = frame.HeaderCorrected,
                corrected_bit_from_msb = frame.HeaderCorrectedBitFromMsb,
                syndrome_before = frame.HeaderSyndromeBefore,
                syndrome_after = frame.HeaderSyndromeAfter,
                reserved_bits = frame.ReservedBits,
                transmission_length_bits = frame.TransmissionLengthBits,
                transmission_length_octets = frame.TransmissionLengthOctets,
                fec_bits = frame.HeaderFecBits,
                raw_header_bits = frame.RawHeaderBits,
                descrambled_header_bits = frame.DescrambledHeaderBits,
                header_hex = frame.HeaderHex,
                valid = frame.HeaderValid,
                status = frame.Status
            },
            payload = new
            {
                attempted = frame.Payload?.Attempted ?? false,
                complete = frame.Payload?.Complete ?? false,
                transmission_length_bits = frame.Payload?.TransmissionLengthBits ?? 0,
                data_octets = frame.Payload?.DataOctets ?? 0,
                fec_octets = frame.Payload?.FecOctets ?? 0,
                required_raw_bits = frame.Payload?.RequiredRawBits ?? 0,
                available_raw_bits = frame.Payload?.AvailableRawBits ?? 0,
                reed_solomon_blocks = frame.Payload?.ReedSolomonBlocks ?? 0,
                reed_solomon_valid = frame.Payload?.ReedSolomonValid ?? false,
                corrected_symbols = frame.Payload?.CorrectedSymbols ?? 0,
                erasure_symbols = frame.Payload?.ErasureSymbols ?? 0,
                hdlc_frames = frame.Payload?.HdlcFrames ?? 0,
                hdlc_unstuff_errors = frame.Payload?.HdlcUnstuffErrors ?? 0,
                fcs_valid_frames = frame.Payload?.FcsValidFrames ?? 0,
                fcs_invalid_frames = frame.Payload?.FcsInvalidFrames ?? 0,
                corrected_payload_hex = frame.Payload?.CorrectedPayloadHex ?? string.Empty,
                status = frame.Payload?.Status ?? "not_attempted",
                avlc_frames = frame.Payload?.Frames ?? Array.Empty<Vdl2AvlcFrame>()
            },
            hard_decision = new
            {
                mapping =
                    new[]
                    {
                        "0deg=000",
                        "45deg=001",
                        "90deg=011",
                        "135deg=010",
                        "180deg=110",
                        "225deg=111",
                        "270deg=101",
                        "315deg=100"
                    },
                bit_preview = bitPreview,
                scrambled = true,
                physical_header_descrambled = frame.HeaderAvailable,
                physical_header_fec_valid = frame.HeaderFecValid,
                payload_fec_decoded = frame.Payload?.ReedSolomonValid ?? false,
                unique_word_validated = frame.PreambleFound,
                avlc_decoded = (frame.Payload?.FcsValidFrames ?? 0) > 0
            },
            conclusion = new
            {
                classification = status,
                d8psk_candidate = d8pskCandidate,
                preamble_valid = frame.PreambleFound,
                physical_header_valid = frame.HeaderValid,
                diagnostic_only =
                    diagnosticOnly,
                status,
                warning =
                    "This stage validates the VDL2 preamble, physical header, Reed-Solomon payload and AVLC FCS. Callsign/registration databases and higher-layer ACARS, X.25 and ATN parsing remain limited."
            },
            symbols_csv =
                Path.GetFileName(csvPath)
        };

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
    }

    private D8pskAnalysisResult CreateTerminalResult(
        CaptureInfo capture,
        string reportPath,
        string status,
        bool diagnosticOnly,
        string reason)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(reportPath)!);

        var report = new
        {
            schema_version = 2,
            stage =
                "statistical_d8psk_symbol_diagnostics",
            completed_at =
                DateTimeOffset.Now,
            analysis_mode =
                diagnosticOnly
                    ? "diagnostic"
                    : "normal",
            capture = new
            {
                capture.Id,
                capture.CreatedAt,
                capture.FrequencyHz,
                capture.SampleRate,
                capture.ComplexSamples,
                capture.DurationMs,
                capture.TriggerClassification,
                capture.CompletionReason,
                capture.Limited,
                capture.ContinuousOrInterference,
                capture.QualityScore,
                capture.RecommendedForD8psk,
                iq_file =
                    Path.GetFileName(
                        capture.IqPath),
                metadata_file =
                    Path.GetFileName(
                        capture.MetadataPath)
            },
            burst = new
            {
                bounded = false
            },
            conclusion = new
            {
                classification = status,
                d8psk_candidate = false,
                diagnostic_only =
                    diagnosticOnly,
                status,
                reason
            }
        };

        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

        return new D8pskAnalysisResult(
            capture.Id,
            DateTimeOffset.Now,
            true,
            false,
            diagnosticOnly,
            Vdl2SymbolRate,
            capture.SampleRate /
                Vdl2SymbolRate,
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
            reportPath,
            string.Empty,
            status,
            null);
    }

    private static double Median(
        IReadOnlyList<double> sortedValues)
    {
        if (sortedValues.Count == 0)
            return 0.0;

        var middle =
            sortedValues.Count / 2;

        if ((sortedValues.Count & 1) == 1)
            return sortedValues[middle];

        return (
            sortedValues[middle - 1] +
            sortedValues[middle]) /
            2.0;
    }

    private static void FftInPlace(
        Complex[] values)
    {
        var length = values.Length;

        for (int i = 1, j = 0;
             i < length;
             i++)
        {
            var bit = length >> 1;

            for (;
                 (j & bit) != 0;
                 bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (values[i], values[j]) =
                    (values[j], values[i]);
            }
        }

        for (var size = 2;
             size <= length;
             size <<= 1)
        {
            var angle =
                -2.0 * Math.PI / size;

            var sizeRoot =
                Complex.FromPolarCoordinates(
                    1.0,
                    angle);

            for (var start = 0;
                 start < length;
                 start += size)
            {
                var root = Complex.One;
                var half = size / 2;

                for (var offset = 0;
                     offset < half;
                     offset++)
                {
                    var even =
                        values[start + offset];

                    var odd =
                        values[
                            start +
                            offset +
                            half] *
                        root;

                    values[start + offset] =
                        even + odd;

                    values[
                        start +
                        offset +
                        half] =
                        even - odd;

                    root *= sizeRoot;
                }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _gate.Dispose();
    }
}
