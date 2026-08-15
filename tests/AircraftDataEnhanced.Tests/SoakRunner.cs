// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal static class SoakRunner
{
    private sealed record SoakReport(
        string Version,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        double DurationSeconds,
        long BlocksSubmitted,
        long MessagesSubmitted,
        long WorkingSetStartBytes,
        long WorkingSetEndBytes,
        long WorkingSetPeakBytes,
        int WaterfallRows,
        long WaterfallDroppedFrames,
        double WaterfallChecksum,
        IqPipelineSnapshot Pipeline,
        JsonlExporterSnapshot Exporter,
        LocalHistoryStatus Database,
        bool Passed,
        string[] Failures);

    public static int Run(
        string[] args)
    {
        var duration =
            ReadDuration(
                args,
                TimeSpan.FromHours(
                    8));

        var outputRoot =
            ReadOption(
                args,
                "--output") ??
            Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "soak",
                DateTime.UtcNow.ToString(
                    "yyyyMMdd-HHmmss"));

        Directory.CreateDirectory(
            outputRoot);

        var started =
            DateTimeOffset.UtcNow;

        var process =
            Process.GetCurrentProcess();

        var startWorkingSet =
            process.WorkingSet64;

        var peakWorkingSet =
            startWorkingSet;

        var blocks =
            0L;

        var messages =
            0L;

        var failures =
            new List<string>();

        var databasePath =
            Path.Combine(
                outputRoot,
                "soak-history.sqlite3");

        var jsonlPath =
            Path.Combine(
                outputRoot,
                "soak-messages.jsonl");

        using var database =
            new LocalHistoryDatabase(
                4096,
                databasePath);

        using var exporter =
            new JsonlExporter(
                4096);

        exporter.Enable(
            jsonlPath);

        PersistenceIntegrationTests.WaitUntil(
            () => database.Ready,
            "SQLite did not become ready for soak.");

        var waterfall =
            new HeadlessWaterfallProcessor(
                fftSize: 256,
                maxRows: 96,
                targetFps: 8);

        var vector =
            LoadGoldenSamples();

        using var pipeline =
            new IqProcessingPipeline<Complex>(
                (
                    buffer,
                    length,
                    sampleRate) =>
                {
                    waterfall.PushIq(
                        buffer,
                        length,
                        sampleRate);

                    var sequence =
                        Interlocked.Increment(
                            ref blocks);

                    if (sequence % 8 != 0)
                    {
                        return;
                    }

                    var message =
                        PersistenceIntegrationTests.CreateMessage(
                            sequence);

                    if (!database.TryEnqueue(
                            message))
                    {
                        throw new InvalidOperationException(
                            "SQLite soak enqueue failed.");
                    }

                    if (!exporter.TryWrite(message))
                    {
                        throw new InvalidOperationException(
                            "JSONL soak enqueue failed.");
                    }

                    Interlocked.Increment(
                        ref messages);
                },
                capacity: 16,
                drainTimeout:
                    TimeSpan.FromSeconds(
                        10));

        var stopwatch =
            Stopwatch.StartNew();

        var nextSample =
            TimeSpan.Zero;

        var blockDuration =
            TimeSpan.FromSeconds(
                vector.Length /
                84_000.0);

        while (stopwatch.Elapsed <
               duration)
        {
            if (!pipeline.TryEnqueue(
                    vector,
                    84_000.0))
            {
                failures.Add(
                    "IQ queue rejected a real-time block.");
            }

            nextSample +=
                blockDuration;

            while (stopwatch.Elapsed <
                   nextSample)
            {
                Thread.Sleep(
                    1);
            }

            process.Refresh();

            peakWorkingSet =
                Math.Max(
                    peakWorkingSet,
                    process.WorkingSet64);

            if (failures.Count >
                100)
            {
                break;
            }
        }

        pipeline.Dispose();

        PersistenceIntegrationTests.WaitUntil(
            () =>
                database.StatusSnapshot()
                    .PendingWrites == 0,
            "SQLite did not drain after soak.",
            30);

        PersistenceIntegrationTests.WaitUntil(
            () =>
                exporter.StatusSnapshot()
                    .PendingWrites == 0,
            "JSONL did not drain after soak.",
            30);

        exporter.Disable();

        var pipelineStatus =
            pipeline.Snapshot();

        var exporterStatus =
            exporter.StatusSnapshot();

        var databaseStatus =
            database.StatusSnapshot();

        process.Refresh();

        var endWorkingSet =
            process.WorkingSet64;

        if (pipelineStatus.DroppedBlocks !=
            0)
        {
            failures.Add(
                $"Dropped IQ blocks: {pipelineStatus.DroppedBlocks}");
        }

        if (pipelineStatus.FaultedBlocks !=
            0)
        {
            failures.Add(
                $"Faulted IQ blocks: {pipelineStatus.FaultedBlocks}");
        }

        if (pipelineStatus.RentedBuffers !=
            pipelineStatus.ReturnedBuffers)
        {
            failures.Add(
                "IQ ArrayPool accounting is unbalanced.");
        }

        if (exporterStatus.Faulted)
        {
            failures.Add(
                "JSONL exporter faulted: " +
                exporterStatus.LastError);
        }

        if (exporterStatus.DroppedRecords !=
            0)
        {
            failures.Add(
                $"Dropped JSONL records: {exporterStatus.DroppedRecords}");
        }

        if (databaseStatus.Faulted)
        {
            failures.Add(
                "SQLite faulted: " +
                databaseStatus.LastError);
        }

        if (databaseStatus.DroppedWrites !=
            0)
        {
            failures.Add(
                $"Dropped SQLite writes: {databaseStatus.DroppedWrites}");
        }

        if (waterfall.RowCount == 0)
        {
            failures.Add(
                "The headless waterfall did not produce rows.");
        }

        if (!double.IsFinite(
                waterfall.Checksum))
        {
            failures.Add(
                "The headless waterfall produced a non-finite checksum.");
        }

        if (peakWorkingSet >
            startWorkingSet +
            512L *
            1024 *
            1024)
        {
            failures.Add(
                "Working set grew by more than 512 MiB.");
        }

        var report =
            new SoakReport(
                "1.0.0",
                started,
                DateTimeOffset.UtcNow,
                stopwatch.Elapsed.TotalSeconds,
                blocks,
                messages,
                startWorkingSet,
                endWorkingSet,
                peakWorkingSet,
                waterfall.RowCount,
                waterfall.DroppedFrames,
                waterfall.Checksum,
                pipelineStatus,
                exporterStatus,
                databaseStatus,
                failures.Count == 0,
                failures.ToArray());

        var reportPath =
            Path.Combine(
                outputRoot,
                "SOAK_REPORT.json");

        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented =
                        true
                }));

        Console.WriteLine(
            $"[INFO] Soak report: {reportPath}");

        Console.WriteLine(
            report.Passed
                ? "[OK] P2 soak test passed."
                : "[FAIL] P2 soak test failed.");

        foreach (var failure in
                 failures)
        {
            Console.Error.WriteLine(
                "[FAIL] " +
                failure);
        }

        return report.Passed
            ? 0
            : 1;
    }

    private static Complex[] LoadGoldenSamples()
    {
        var path =
            Path.Combine(
                AppContext.BaseDirectory,
                "testdata",
                "golden",
                "vdl2_full_frame_iq_f32le.bin");

        var bytes =
            File.ReadAllBytes(
                path);

        var samples =
            new Complex[
                bytes.Length /
                8];

        for (var index = 0;
             index < samples.Length;
             index++)
        {
            samples[index] =
                new Complex(
                    BitConverter.ToSingle(
                        bytes,
                        index * 8),
                    BitConverter.ToSingle(
                        bytes,
                        index * 8 + 4));
        }

        return samples;
    }

    private static TimeSpan ReadDuration(
        string[] args,
        TimeSpan fallback)
    {
        var value =
            ReadOption(
                args,
                "--duration");

        return value is not null &&
               TimeSpan.TryParse(
                   value,
                   out var parsed) &&
               parsed >
               TimeSpan.Zero
            ? parsed
            : fallback;
    }

    private static string? ReadOption(
        string[] args,
        string name)
    {
        for (var index = 0;
             index <
             args.Length - 1;
             index++)
        {
            if (string.Equals(
                    args[index],
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private sealed class HeadlessWaterfallProcessor
    {
        private readonly int _fftSize;
        private readonly int _maxRows;
        private readonly int _targetFps;
        private readonly double[] _real;
        private readonly double[] _imaginary;

        private long _lastUpdateMilliseconds;
        private long _droppedFrames;
        private int _processing;
        private int _rowCount;
        private double _checksum;

        public HeadlessWaterfallProcessor(
            int fftSize,
            int maxRows,
            int targetFps)
        {
            if (fftSize < 2 ||
                (fftSize &
                 (fftSize - 1)) !=
                0)
            {
                throw new ArgumentException(
                    "FFT size must be a power of two.",
                    nameof(fftSize));
            }

            _fftSize =
                fftSize;

            _maxRows =
                Math.Max(
                    1,
                    maxRows);

            _targetFps =
                Math.Clamp(
                    targetFps,
                    2,
                    20);

            _real =
                new double[
                    fftSize];

            _imaginary =
                new double[
                    fftSize];
        }

        public int RowCount =>
            Volatile.Read(
                ref _rowCount);

        public long DroppedFrames =>
            Interlocked.Read(
                ref _droppedFrames);

        public double Checksum =>
            Volatile.Read(
                ref _checksum);

        public void PushIq(
            Complex[] buffer,
            int length,
            double sampleRate)
        {
            if (length <
                _fftSize ||
                sampleRate <=
                0)
            {
                return;
            }

            var now =
                Environment.TickCount64;

            var interval =
                Math.Max(
                    1,
                    1000 /
                    _targetFps);

            if (now -
                Interlocked.Read(
                    ref _lastUpdateMilliseconds) <
                interval)
            {
                return;
            }

            if (Interlocked.Exchange(
                    ref _processing,
                    1) != 0)
            {
                Interlocked.Increment(
                    ref _droppedFrames);

                return;
            }

            try
            {
                Interlocked.Exchange(
                    ref _lastUpdateMilliseconds,
                    now);

                var start =
                    Math.Max(
                        0,
                        (length -
                         _fftSize) /
                        2);

                for (var index = 0;
                     index <
                     _fftSize;
                     index++)
                {
                    var sample =
                        buffer[
                            start +
                            index];

                    var window =
                        0.5 -
                        0.5 *
                        Math.Cos(
                            2.0 *
                            Math.PI *
                            index /
                            (_fftSize -
                             1));

                    _real[index] =
                        sample.Real *
                        window;

                    _imaginary[index] =
                        sample.Imaginary *
                        window;
                }

                FftInPlace(
                    _real,
                    _imaginary);

                var scale =
                    1.0 /
                    (_fftSize *
                     _fftSize);

                var checksum =
                    0.0;

                for (var index = 0;
                     index <
                     _fftSize;
                     index++)
                {
                    var shifted =
                        (index +
                         _fftSize /
                         2) %
                        _fftSize;

                    var power =
                        (
                            _real[shifted] *
                            _real[shifted] +
                            _imaginary[shifted] *
                            _imaginary[shifted]
                        ) *
                        scale;

                    var decibels =
                        10.0 *
                        Math.Log10(
                            Math.Max(
                                power,
                                1e-20));

                    checksum +=
                        decibels *
                        (index +
                         1);
                }

                Volatile.Write(
                    ref _checksum,
                    checksum);

                var currentRows =
                    Volatile.Read(
                        ref _rowCount);

                if (currentRows <
                    _maxRows)
                {
                    Volatile.Write(
                        ref _rowCount,
                        currentRows +
                        1);
                }
            }
            catch
            {
                Interlocked.Increment(
                    ref _droppedFrames);
            }
            finally
            {
                Volatile.Write(
                    ref _processing,
                    0);
            }
        }

        private static void FftInPlace(
            double[] real,
            double[] imaginary)
        {
            var length =
                real.Length;

            for (int index = 1,
                 reversed = 0;
                 index <
                 length;
                 index++)
            {
                var bit =
                    length >>
                    1;

                for (;
                     (reversed &
                      bit) !=
                     0;
                     bit >>=
                     1)
                {
                    reversed ^=
                        bit;
                }

                reversed ^=
                    bit;

                if (index >=
                    reversed)
                {
                    continue;
                }

                (
                    real[index],
                    real[reversed]
                ) =
                (
                    real[reversed],
                    real[index]
                );

                (
                    imaginary[index],
                    imaginary[reversed]
                ) =
                (
                    imaginary[reversed],
                    imaginary[index]
                );
            }

            for (var stageLength = 2;
                 stageLength <=
                 length;
                 stageLength <<=
                 1)
            {
                var angle =
                    -2.0 *
                    Math.PI /
                    stageLength;

                var stepReal =
                    Math.Cos(
                        angle);

                var stepImaginary =
                    Math.Sin(
                        angle);

                for (var offset = 0;
                     offset <
                     length;
                     offset +=
                     stageLength)
                {
                    var twiddleReal =
                        1.0;

                    var twiddleImaginary =
                        0.0;

                    for (var element = 0;
                         element <
                         stageLength /
                         2;
                         element++)
                    {
                        var evenReal =
                            real[
                                offset +
                                element];

                        var evenImaginary =
                            imaginary[
                                offset +
                                element];

                        var oddIndex =
                            offset +
                            element +
                            stageLength /
                            2;

                        var oddReal =
                            real[oddIndex] *
                            twiddleReal -
                            imaginary[oddIndex] *
                            twiddleImaginary;

                        var oddImaginary =
                            real[oddIndex] *
                            twiddleImaginary +
                            imaginary[oddIndex] *
                            twiddleReal;

                        real[
                            offset +
                            element] =
                            evenReal +
                            oddReal;

                        imaginary[
                            offset +
                            element] =
                            evenImaginary +
                            oddImaginary;

                        real[oddIndex] =
                            evenReal -
                            oddReal;

                        imaginary[oddIndex] =
                            evenImaginary -
                            oddImaginary;

                        var nextReal =
                            twiddleReal *
                            stepReal -
                            twiddleImaginary *
                            stepImaginary;

                        twiddleImaginary =
                            twiddleReal *
                            stepImaginary +
                            twiddleImaginary *
                            stepReal;

                        twiddleReal =
                            nextReal;
                    }
                }
            }
        }
    }
}