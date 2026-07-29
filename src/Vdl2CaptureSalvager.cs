// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed record Vdl2SalvageResult(
    CaptureInfo SourceCapture,
    CaptureInfo[] AnalysisCaptures,
    int BoundedBurstCount,
    bool SplitApplied,
    string Status);

internal sealed class Vdl2CaptureSalvager
{
    private readonly record struct BurstRegion(
        int Start,
        int End,
        double NoisePower,
        double SignalPower,
        double SnrDb);

    private const int MaximumBurstsPerCapture = 12;
    private const double DetectionWindowMs = 1.5;
    private const double MaximumGapMs = 2.0;
    private const double MinimumBurstMs = 8.0;
    private const double MaximumBurstMs = 1200.0;
    private const double BoundaryGuardMs = 8.0;
    private const double DetectionExpansionMs = 2.0;
    private const double ExtractionGuardMs = 12.0;

    public string SalvageDirectory { get; }

    public Vdl2CaptureSalvager(
        string analysisDirectory)
    {
        SalvageDirectory =
            Path.Combine(
                analysisDirectory,
                "salvage");

        Directory.CreateDirectory(
            SalvageDirectory);
    }

    public Task<Vdl2SalvageResult> PrepareAsync(
        CaptureInfo capture,
        bool diagnosticMode,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => PrepareCore(
                capture,
                diagnosticMode,
                cancellationToken),
            cancellationToken);
    }

    private Vdl2SalvageResult PrepareCore(
        CaptureInfo capture,
        bool diagnosticMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (diagnosticMode ||
            capture.Limited ||
            !File.Exists(capture.IqPath) ||
            capture.SampleRate <= 0)
        {
            return new Vdl2SalvageResult(
                capture,
                [capture],
                0,
                false,
                "direct_analysis");
        }

        var interleaved =
            ReadInterleaved(
                capture.IqPath,
                cancellationToken);

        if (interleaved.Length < 1024 ||
            (interleaved.Length & 1) != 0)
        {
            return new Vdl2SalvageResult(
                capture,
                [capture],
                0,
                false,
                "capture_too_short_for_salvage");
        }

        var bursts =
            FindBoundedBursts(
                interleaved,
                capture.SampleRate,
                cancellationToken);

        var mustSplit =
            capture.ContinuousOrInterference ||
            bursts.Count > 1;

        if (!mustSplit)
        {
            return new Vdl2SalvageResult(
                capture,
                [capture],
                bursts.Count,
                false,
                bursts.Count == 1
                    ? "single_bounded_burst"
                    : "no_salvage_burst");
        }

        if (bursts.Count == 0)
        {
            return new Vdl2SalvageResult(
                capture,
                Array.Empty<CaptureInfo>(),
                0,
                false,
                "continuous_capture_without_bounded_burst");
        }

        var children =
            new List<CaptureInfo>(
                bursts.Count);

        for (var index = 0;
             index < bursts.Count;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var child =
                WriteChildCapture(
                    capture,
                    interleaved,
                    bursts[index],
                    index,
                    bursts.Count,
                    cancellationToken);

            children.Add(
                child);
        }

        PruneOldSalvageFiles(
            maximumFilePairs: 300);

        return new Vdl2SalvageResult(
            capture,
            children.ToArray(),
            bursts.Count,
            true,
            capture.ContinuousOrInterference
                ? "continuous_salvage_ready"
                : "multi_burst_split_ready");
    }

    private static float[] ReadInterleaved(
        string path,
        CancellationToken cancellationToken)
    {
        using var stream =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);

        if (stream.Length % sizeof(float) != 0)
        {
            throw new InvalidDataException(
                "IQ file length is not aligned to float32.");
        }

        var floatCount =
            checked(
                (int)(
                    stream.Length /
                    sizeof(float)));

        var values =
            new float[floatCount];

        using var reader =
            new BinaryReader(
                stream);

        for (var index = 0;
             index < values.Length;
             index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
            }

            values[index] =
                reader.ReadSingle();
        }

        return values;
    }

    private static IReadOnlyList<BurstRegion> FindBoundedBursts(
        float[] interleaved,
        double sampleRate,
        CancellationToken cancellationToken)
    {
        var sampleCount =
            interleaved.Length /
            2;

        var power =
            new double[
                sampleCount];

        for (var index = 0;
             index < sampleCount;
             index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
            }

            var inPhase =
                interleaved[
                    index * 2];

            var quadrature =
                interleaved[
                    index * 2 + 1];

            if (!float.IsFinite(inPhase) ||
                !float.IsFinite(quadrature))
            {
                power[index] = 0;
                continue;
            }

            power[index] =
                inPhase *
                inPhase
                +
                quadrature *
                quadrature;
        }

        var window =
            Math.Max(
                8,
                (int)Math.Round(
                    sampleRate *
                    DetectionWindowMs /
                    1000.0));

        var smoothed =
            MovingAverage(
                power,
                window);

        var sorted =
            (double[])smoothed.Clone();

        Array.Sort(
            sorted);

        var percentileIndex =
            Math.Clamp(
                (int)(
                    sorted.Length *
                    0.30),
                0,
                sorted.Length - 1);

        var noisePower =
            Math.Max(
                sorted[
                    percentileIndex],
                1e-20);

        var threshold =
            noisePower *
            4.0;

        var boundaryThreshold =
            noisePower *
            2.0;

        var maximumGap =
            Math.Max(
                1,
                (int)Math.Round(
                    sampleRate *
                    MaximumGapMs /
                    1000.0));

        var minimumLength =
            Math.Max(
                16,
                (int)Math.Round(
                    sampleRate *
                    MinimumBurstMs /
                    1000.0));

        var maximumLength =
            Math.Max(
                minimumLength,
                (int)Math.Round(
                    sampleRate *
                    MaximumBurstMs /
                    1000.0));

        var guardLength =
            Math.Max(
                8,
                (int)Math.Round(
                    sampleRate *
                    BoundaryGuardMs /
                    1000.0));

        var expansion =
            Math.Max(
                1,
                (int)Math.Round(
                    sampleRate *
                    DetectionExpansionMs /
                    1000.0));

        var bursts =
            new List<BurstRegion>();

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
                sum +=
                    smoothed[index];
            }

            return sum /
                (rangeEnd -
                 rangeStart);
        }

        void Evaluate(
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

            var leadingNoise =
                MeanRange(
                    segmentStart -
                        guardLength,
                    segmentStart);

            var trailingNoise =
                MeanRange(
                    segmentEnd,
                    segmentEnd +
                        guardLength);

            if (leadingNoise >
                    boundaryThreshold ||
                trailingNoise >
                    boundaryThreshold)
            {
                return;
            }

            var signalPower =
                MeanRange(
                    segmentStart,
                    segmentEnd);

            var snrDb =
                10.0 *
                Math.Log10(
                    Math.Max(
                        signalPower /
                        noisePower,
                        1e-12));

            if (snrDb < 5.0)
                return;

            var edgeContrastDb =
                10.0 *
                Math.Log10(
                    Math.Max(
                        signalPower /
                        Math.Max(
                            leadingNoise,
                            trailingNoise),
                        1e-12));

            if (edgeContrastDb < 3.0)
                return;

            var expandedStart =
                Math.Max(
                    guardLength,
                    segmentStart -
                        expansion);

            var expandedEnd =
                Math.Min(
                    smoothed.Length -
                        guardLength,
                    segmentEnd +
                        expansion);

            bursts.Add(
                new BurstRegion(
                    expandedStart,
                    expandedEnd,
                    noisePower,
                    signalPower,
                    snrDb));
        }

        for (var index = 0;
             index < smoothed.Length;
             index++)
        {
            if ((index & 8191) == 0)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
            }

            if (smoothed[index] >
                threshold)
            {
                if (start < 0)
                    start = index;

                lastActive = index;
                continue;
            }

            if (start >= 0 &&
                index -
                    lastActive >
                maximumGap)
            {
                Evaluate(
                    start,
                    lastActive + 1);

                start = -1;
                lastActive = -1;
            }
        }

        if (start >= 0)
        {
            Evaluate(
                start,
                lastActive + 1);
        }

        return bursts
            .OrderBy(
                burst =>
                    burst.Start)
            .Take(
                MaximumBurstsPerCapture)
            .ToArray();
    }

    private CaptureInfo WriteChildCapture(
        CaptureInfo source,
        float[] interleaved,
        BurstRegion burst,
        int index,
        int total,
        CancellationToken cancellationToken)
    {
        var guardSamples =
            Math.Max(
                8,
                (int)Math.Round(
                    source.SampleRate *
                    ExtractionGuardMs /
                    1000.0));

        var sourceSamples =
            interleaved.Length /
            2;

        var childStart =
            Math.Max(
                0,
                burst.Start -
                    guardSamples);

        var childEnd =
            Math.Min(
                sourceSamples,
                burst.End +
                    guardSamples);

        var childSamples =
            childEnd -
            childStart;

        var childId =
            $"{source.Id}-S{index + 1:00}";

        var baseName =
            $"vdl2-salvage-{childId}-{source.FrequencyHz}Hz";

        var iqPath =
            Path.Combine(
                SalvageDirectory,
                baseName +
                    ".iqf32");

        var metadataPath =
            Path.Combine(
                SalvageDirectory,
                baseName +
                    ".json");

        using (
            var stream =
                new FileStream(
                    iqPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan))
        using (
            var writer =
                new BinaryWriter(
                    stream))
        {
            var firstFloat =
                checked(
                    childStart *
                    2);

            var floatCount =
                checked(
                    childSamples *
                    2);

            for (var offset = 0;
                 offset < floatCount;
                 offset++)
            {
                if ((offset & 8191) == 0)
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                }

                writer.Write(
                    interleaved[
                        firstFloat +
                        offset]);
            }
        }

        var startOffsetMs =
            childStart /
            source.SampleRate *
            1000.0;

        var durationMs =
            childSamples /
            source.SampleRate *
            1000.0;

        var metadata =
            new
            {
                schema_version = 1,
                stage =
                    "vdl2_capture_salvage",
                parent_capture_id =
                    source.Id,
                parent_iq_file =
                    Path.GetFileName(
                        source.IqPath),
                source_continuous_or_interference =
                    source.ContinuousOrInterference,
                source_completion_reason =
                    source.CompletionReason,
                salvage_index =
                    index + 1,
                salvage_count =
                    total,
                frequency_hz =
                    source.FrequencyHz,
                sample_rate =
                    source.SampleRate,
                complex_samples =
                    childSamples,
                duration_ms =
                    Math.Round(
                        durationMs,
                        3),
                source_start_ms =
                    Math.Round(
                        startOffsetMs,
                        3),
                bounded_start_ms =
                    Math.Round(
                        burst.Start /
                        source.SampleRate *
                        1000.0,
                        3),
                bounded_duration_ms =
                    Math.Round(
                        (burst.End -
                         burst.Start) /
                        source.SampleRate *
                        1000.0,
                        3),
                estimated_snr_db =
                    Math.Round(
                        burst.SnrDb,
                        3),
                pre_buffer_ms =
                    Math.Round(
                        (
                            burst.Start -
                            childStart
                        ) /
                        source.SampleRate *
                        1000.0,
                        3),
                post_buffer_ms =
                    Math.Round(
                        (
                            childEnd -
                            burst.End
                        ) /
                        source.SampleRate *
                        1000.0,
                        3)
            };

        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(
                metadata,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

        return new CaptureInfo(
            childId,
            source.CreatedAt +
                TimeSpan.FromMilliseconds(
                    startOffsetMs),
            source.FrequencyHz,
            source.SampleRate,
            childSamples,
            durationMs,
            "Salvaged bounded burst",
            source.ContinuousOrInterference
                ? "continuous_salvage_burst"
                : "multi_burst_salvage",
            Limited: false,
            ContinuousOrInterference: false,
            QualityScore:
                Math.Max(
                    75.0,
                    source.QualityScore),
            RecommendedForD8psk:
                true,
            IqPath:
                iqPath,
            MetadataPath:
                metadataPath);
    }

    private void PruneOldSalvageFiles(
        int maximumFilePairs)
    {
        try
        {
            var iqFiles =
                new DirectoryInfo(
                    SalvageDirectory)
                .EnumerateFiles(
                    "*.iqf32")
                .OrderByDescending(
                    file =>
                        file.LastWriteTimeUtc)
                .Skip(
                    Math.Max(
                        1,
                        maximumFilePairs))
                .ToArray();

            foreach (var iqFile in
                     iqFiles)
            {
                try
                {
                    var metadata =
                        Path.ChangeExtension(
                            iqFile.FullName,
                            ".json");

                    iqFile.Delete();

                    if (File.Exists(
                        metadata))
                    {
                        File.Delete(
                            metadata);
                    }
                }
                catch
                {
                    // Best-effort maintenance only.
                }
            }
        }
        catch
        {
            // Salvage decoding must not fail because cleanup was unavailable.
        }
    }

    private static double[] MovingAverage(
        double[] values,
        int window)
    {
        var result =
            new double[
                values.Length];

        var prefix =
            new double[
                values.Length + 1];

        for (var index = 0;
             index < values.Length;
             index++)
        {
            prefix[
                index + 1] =
                prefix[index] +
                values[index];
        }

        var half =
            window /
            2;

        for (var index = 0;
             index < values.Length;
             index++)
        {
            var start =
                Math.Max(
                    0,
                    index -
                        half);

            var end =
                Math.Min(
                    values.Length,
                    start +
                        window);

            start =
                Math.Max(
                    0,
                    end -
                        window);

            result[index] =
                (
                    prefix[end] -
                    prefix[start]
                ) /
                Math.Max(
                    1,
                    end -
                    start);
        }

        return result;
    }
}
