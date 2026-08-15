// SPDX-License-Identifier: MIT
using System.Buffers;
using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace SDRSharp.AircraftDataEnhanced;

internal static class Program
{
    private static readonly List<
        (string Name, Action Test)> Tests =
    [
        (
            "VDL2 header descrambling and FEC",
            TestVdl2Header),
        (
            "RS(255,249) correction",
            TestReedSolomon),
        (
            "HDLC stuffing and FCS",
            TestHdlcAndFcs),
        (
            "VDL2 payload bounds and RS",
            TestPayloadDecoder),
        (
            "ACARS envelope",
            TestAcarsEnvelope),
        (
            "Verified ICAO policy",
            TestVerifiedAircraftPolicy),
        (
            "Bounded IQ processing pipeline",
            TestIqProcessingPipeline),
        (
            "Golden full synthetic VDL2 IQ",
            GoldenVectorTests.Run),
        (
            "Persistence integration",
            PersistenceIntegrationTests.Run),
        (
            "Exact SDR# SDK ABI contract",
            SdkCompatibilityTests.Run)
    ];

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(
                argument =>
                    string.Equals(
                        argument,
                        "--soak",
                        StringComparison.OrdinalIgnoreCase)))
        {
            return SoakRunner.Run(args);
        }

        var failures =
            new List<string>();

        foreach (var test in
                 Tests)
        {
            try
            {
                test.Test();

                Console.WriteLine(
                    $"[OK] {test.Name}");
            }
            catch (Exception ex)
            {
                failures.Add(
                    test.Name +
                    ": " +
                    ex);

                Console.Error.WriteLine(
                    $"[FAIL] {test.Name}: {ex.Message}");
            }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine(
                $"[OK] {Tests.Count} C# regression groups passed.");

            return 0;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine(
            string.Join(
                Environment.NewLine +
                Environment.NewLine,
                failures));

        return 1;
    }

    private static void TestVdl2Header()
    {
        var type =
            typeof(Vdl2FrameDecoder);

        var reverseBits =
            PrivateStatic(
                type,
                "ReverseBits");

        var syndrome =
            PrivateStatic(
                type,
                "CalculateSyndrome");

        var descramble =
            PrivateStatic(
                type,
                "Descramble");

        foreach (var length in
                 new[]
                 {
                     1,
                     515,
                     1024,
                     4095,
                     0x1FFF,
                     0x3FFF
                 })
        {
            var encodedLength =
                Invoke<uint>(
                    reverseBits,
                    (uint)length,
                    17);

            var baseHeader =
                encodedLength <<
                5;

            var validHeaders =
                Enumerable
                    .Range(
                        0,
                        32)
                    .Select(
                        fec =>
                            baseHeader |
                            (uint)fec)
                    .Where(
                        value =>
                            Invoke<int>(
                                syndrome,
                                value) == 0)
                    .ToArray();

            AssertEqual(
                1,
                validHeaders.Length,
                "A header must have exactly one valid 5-bit FEC value.");

            var validHeader =
                validHeaders[0];

            var clearBits =
                BitsMsbFirst(
                    validHeader,
                    25);

            // XOR scrambling is symmetric.
            var scrambled =
                Invoke<List<int>>(
                    descramble,
                    clearBits);

            var decoded =
                Invoke<List<int>>(
                    descramble,
                    scrambled);

            AssertSequenceEqual(
                clearBits,
                decoded,
                "Header descrambler round-trip failed.");

            AssertHeaderDecodes(
                decoded,
                length,
                syndrome,
                reverseBits);

            for (var position = 0;
                 position < 25;
                 position++)
            {
                var damaged =
                    scrambled.ToList();

                damaged[position] ^=
                    1;

                var damagedClear =
                    Invoke<List<int>>(
                        descramble,
                        damaged);

                AssertHeaderDecodes(
                    damagedClear,
                    length,
                    syndrome,
                    reverseBits);
            }
        }
    }

    private static void AssertHeaderDecodes(
        IReadOnlyList<int> clearBits,
        int expectedLength,
        MethodInfo syndrome,
        MethodInfo reverseBits)
    {
        const int headerBits =
            25;

        const int headerPayloadBits =
            22;

        const int transmissionLengthBits =
            17;

        const int headerFecBits =
            5;

        var rawHeader =
            BuildWordMsbFirst(
                clearBits,
                headerBits);

        var header =
            rawHeader &
            ((1u <<
              headerPayloadBits) -
             1u);

        var syndromeBefore =
            Invoke<int>(
                syndrome,
                header);

        var corrected =
            header;

        var correctionCount =
            0;

        if (syndromeBefore != 0)
        {
            for (var bitPosition = 0;
                 bitPosition < headerBits;
                 bitPosition++)
            {
                var trial =
                    header ^
                    (1u <<
                     bitPosition);

                if (Invoke<int>(
                        syndrome,
                        trial) != 0)
                {
                    continue;
                }

                correctionCount++;
                corrected =
                    trial;
            }
        }

        if (syndromeBefore != 0 &&
            correctionCount != 1)
        {
            corrected =
                header;
        }

        var syndromeAfter =
            Invoke<int>(
                syndrome,
                corrected);

        AssertEqual(
            0,
            syndromeAfter,
            "Header syndrome did not clear.");

        var encodedLength =
            (corrected >>
             headerFecBits) &
            ((1u <<
              transmissionLengthBits) -
             1u);

        var decodedLength =
            (int)Invoke<uint>(
                reverseBits,
                encodedLength,
                transmissionLengthBits);

        AssertEqual(
            expectedLength,
            decodedLength,
            "Header transmission length changed.");
    }

    private static void TestReedSolomon()
    {
        var codeword =
            new byte[255];

        var erasures =
            new[]
            {
                253,
                254
            };

        codeword[253] =
            0x55;

        codeword[254] =
            0xA7;

        codeword[11] =
            0x3C;

        codeword[77] =
            0xE1;

        var result =
            ReedSolomon255249.Decode(
                codeword,
                erasures,
                out var correctedLocations);

        Assert(
            result >= 0,
            "RS decoder rejected a correctable codeword.");

        Assert(
            codeword.All(
                value =>
                    value == 0),
            "RS decoder did not restore the all-zero codeword.");

        Assert(
            correctedLocations.Length >=
            2,
            "RS decoder did not report corrected locations.");

        AssertThrows<ArgumentException>(
            () =>
                ReedSolomon255249.Decode(
                    new byte[254],
                    null,
                    out _));
    }

    private static void TestHdlcAndFcs()
    {
        var payload =
            Encoding.ASCII.GetBytes(
                "SYNTHETIC VDL2 AVLC TEST");

        var frame =
            AppendHdlcFcs(
                payload);

        AssertEqual(
            (ushort)0xF0B8,
            CalculateHdlcFcs(
                frame),
            "Generated HDLC frame does not have the expected residual.");

        var bits =
            new List<int>();

        bits.AddRange(
        [
            0,
            1,
            1,
            1,
            1,
            1,
            1,
            0
        ]);

        bits.AddRange(
            StuffBits(
                BytesToBitsLsbFirst(
                    frame)));

        bits.AddRange(
        [
            0,
            1,
            1,
            1,
            1,
            1,
            1,
            0
        ]);

        var extract =
            PrivateStatic(
                typeof(Vdl2PayloadDecoder),
                "ExtractHdlcFrames");

        var result =
            extract.Invoke(
                null,
                new object?[]
                {
                    bits
                })
            ??
            throw new InvalidOperationException(
                "HDLC result was null.");

        var framesProperty =
            result.GetType()
                .GetProperty(
                    "Frames")
            ??
            throw new MissingMemberException(
                "HDLC Frames property was not found.");

        var frames =
            framesProperty.GetValue(
                result)
            as IEnumerable
            ??
            throw new InvalidOperationException(
                "HDLC Frames was not enumerable.");

        var recovered =
            frames
                .Cast<object>()
                .Select(
                    item =>
                        (byte[])item)
                .ToArray();

        AssertEqual(
            1,
            recovered.Length,
            "Expected exactly one extracted HDLC frame.");

        AssertSequenceEqual(
            frame,
            recovered[0],
            "Extracted HDLC frame differs from the source.");

        var calculateFcs =
            PrivateStatic(
                typeof(Vdl2PayloadDecoder),
                "CalculateFcs");

        var actualResidual =
            Invoke<ushort>(
                calculateFcs,
                frame);

        AssertEqual(
            (ushort)0xF0B8,
            actualResidual,
            "Production CalculateFcs returned an invalid residual.");
    }

    private static void TestPayloadDecoder()
    {
        const int dataOctets =
            249;

        var bits =
            new List<int>(
                25 +
                255 *
                8);

        bits.AddRange(
            Enumerable.Repeat(
                0,
                25));

        // An all-zero RS codeword is valid. With no HDLC flags, the decoder
        // must complete cleanly and report hdlc_no_frame.
        bits.AddRange(
            Enumerable.Repeat(
                0,
                255 *
                8));

        var result =
            Vdl2PayloadDecoder.Decode(
                bits,
                dataOctets *
                8);

        Assert(
            result.Attempted,
            "Payload decoder did not run.");

        Assert(
            result.Complete,
            "Payload decoder did not complete.");

        Assert(
            result.ReedSolomonValid,
            "All-zero RS codeword should be valid.");

        AssertEqual(
            "hdlc_no_frame",
            result.Status,
            "Unexpected empty-payload status.");

        var truncated =
            Vdl2PayloadDecoder.Decode(
                bits.Take(
                        bits.Count -
                        1)
                    .ToArray(),
                dataOctets *
                8);

        AssertEqual(
            "payload_truncated",
            truncated.Status,
            "Truncated payload was not rejected.");
    }

    private static void TestAcarsEnvelope()
    {
        var logical =
            new List<byte>
            {
                (byte)'2'
            };

        logical.AddRange(
            Encoding.ASCII.GetBytes(
                ".N12345"));

        logical.Add(
            0x06);

        logical.AddRange(
            Encoding.ASCII.GetBytes(
                "H1"));

        logical.Add(
            (byte)'1');

        logical.Add(
            0x02);

        logical.AddRange(
            Encoding.ASCII.GetBytes(
                "001A"));

        logical.AddRange(
            Encoding.ASCII.GetBytes(
                "TP1234"));

        logical.AddRange(
            Encoding.ASCII.GetBytes(
                "TEST MESSAGE"));

        logical.Add(
            0x03);

        var frame =
            AppendAcarsCrc(
                logical.ToArray())
                .Concat(
                    new byte[]
                    {
                        0x7F
                    })
                .ToArray();

        var parsed =
            AcarsMessageParser.TryParse(
                frame,
                "Air → Ground",
                out var message);

        Assert(
            parsed &&
            message is not null,
            "ACARS parser rejected a valid synthetic envelope.");

        Assert(
            message!.CrcValid,
            "ACARS CRC should be valid.");

        AssertEqual(
            "N12345",
            message.Registration,
            "ACARS registration normalization failed.");

        AssertEqual(
            "TP1234",
            message.FlightId,
            "ACARS flight ID failed.");

        AssertEqual(
            "H1",
            message.Label,
            "ACARS label failed.");

        AssertEqual(
            "001",
            message.MessageNumber,
            "ACARS message number failed.");

        AssertEqual(
            "A",
            message.MessageSequence,
            "ACARS message sequence failed.");

        AssertEqual(
            "TEST MESSAGE",
            message.Text,
            "ACARS text failed.");
    }

    private static void TestVerifiedAircraftPolicy()
    {
        var accepted =
            new Vdl2Message(
                DateTimeOffset.UtcNow,
                "avlc",
                "Air → Ground",
                "abcdef",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "TEST",
                136.975,
                null,
                true,
                "{}");

        Assert(
            VerifiedAircraftMessagePolicy.TryAccept(
                accepted,
                out var normalized,
                out var reason),
            "Valid AVLC aircraft message was rejected: " +
            reason);

        AssertEqual(
            "ABCDEF",
            normalized.Icao,
            "ICAO24 was not normalized.");

        var invalidProtocol =
            accepted with
            {
                Protocol =
                    "VDL2"
            };

        Assert(
            !VerifiedAircraftMessagePolicy.TryAccept(
                invalidProtocol,
                out _,
                out var protocolReason),
            "Unverified protocol was accepted.");

        AssertEqual(
            "protocol_not_verified_avlc",
            protocolReason,
            "Unexpected protocol rejection reason.");

        var invalidIcao =
            accepted with
            {
                Icao =
                    "ZZZZZZ"
            };

        Assert(
            !VerifiedAircraftMessagePolicy.TryAccept(
                invalidIcao,
                out _,
                out var icaoReason),
            "Invalid ICAO24 was accepted.");

        AssertEqual(
            "icao24_missing_or_invalid",
            icaoReason,
            "Unexpected ICAO rejection reason.");
    }

    private static void TestIqProcessingPipeline()
    {
        var order =
            new List<int>();

        var orderGate =
            new object();

        using var firstStarted =
            new ManualResetEventSlim(
                false);

        using var releaseFirst =
            new ManualResetEventSlim(
                false);

        var pool =
            new TrackingArrayPool<int>();

        IqPipelineSnapshot finalSnapshot;

        using (var pipeline =
               new IqProcessingPipeline<int>(
                   (buffer, length, sampleRate) =>
                   {
                       Assert(
                           length == 1,
                           "The IQ pipeline changed the block length.");

                       Assert(
                           Math.Abs(
                               sampleRate -
                               2_400_000.0) <
                           0.5,
                           "The IQ pipeline changed the sample rate.");

                       var value =
                           buffer[0];

                       if (value == 1)
                       {
                           firstStarted.Set();

                           Assert(
                               releaseFirst.Wait(
                                   TimeSpan.FromSeconds(
                                       5)),
                               "The saturation test did not release the first block.");
                       }

                       if (value == 4)
                       {
                           throw new InvalidOperationException(
                               "synthetic worker failure");
                       }

                       lock (orderGate)
                       {
                           order.Add(
                               value);
                       }
                   },
                   capacity: 2,
                   pool: pool,
                   drainTimeout:
                       TimeSpan.FromSeconds(
                           2)))
        {
            Assert(
                pipeline.TryEnqueue(
                    new[]
                    {
                        1
                    },
                    2_400_000.0),
                "The first IQ block was rejected.");

            Assert(
                firstStarted.Wait(
                    TimeSpan.FromSeconds(
                        5)),
                "The IQ worker did not start.");

            Assert(
                pipeline.TryEnqueue(
                    new[]
                    {
                        2
                    },
                    2_400_000.0),
                "The second IQ block was rejected.");

            Assert(
                pipeline.TryEnqueue(
                    new[]
                    {
                        3
                    },
                    2_400_000.0),
                "The third IQ block was rejected.");

            Assert(
                !pipeline.TryEnqueue(
                    new[]
                    {
                        99
                    },
                    2_400_000.0),
                "A saturated bounded queue accepted an extra IQ block.");

            var saturated =
                pipeline.Snapshot();

            AssertEqual(
                1L,
                saturated.DroppedBlocks,
                "The saturated queue did not count the dropped block.");

            AssertEqual(
                "Overloaded",
                saturated.State,
                "A recent queue drop must report Overloaded.");

            Assert(
                saturated.PeakPending <=
                saturated.Capacity,
                "Peak queue depth exceeded the configured capacity.");

            releaseFirst.Set();

            WaitUntil(
                () =>
                    pipeline.Snapshot().ProcessedBlocks >=
                    3,
                "The ordered IQ blocks were not processed.");

            Assert(
                pipeline.TryEnqueue(
                    new[]
                    {
                        4
                    },
                    2_400_000.0),
                "The synthetic fault block was rejected.");

            Assert(
                pipeline.TryEnqueue(
                    new[]
                    {
                        5
                    },
                    2_400_000.0),
                "The post-fault IQ block was rejected.");

            WaitUntil(
                () =>
                {
                    var snapshot =
                        pipeline.Snapshot();

                    return snapshot.ProcessedBlocks >=
                               4 &&
                           snapshot.FaultedBlocks >=
                               1 &&
                           snapshot.Pending ==
                               0 &&
                           snapshot.RentedBuffers ==
                               snapshot.ReturnedBuffers;
                },
                "The IQ worker did not recover after an exception.");

            lock (orderGate)
            {
                AssertSequenceEqual(
                    new[]
                    {
                        1,
                        2,
                        3,
                        5
                    },
                    order,
                    "IQ block ordering or exception recovery failed.");
            }

            finalSnapshot =
                pipeline.Snapshot();
        }

        AssertEqual(
            pool.RentCount,
            pool.ReturnCount,
            "Every rented IQ buffer must be returned.");

        AssertEqual(
            finalSnapshot.RentedBuffers,
            finalSnapshot.ReturnedBuffers,
            "Pipeline buffer accounting is unbalanced.");

        AssertEqual(
            1L,
            finalSnapshot.FaultedBlocks,
            "The synthetic worker fault was not counted.");

        Assert(
            finalSnapshot.MaximumQueueLatencyMs >=
            finalSnapshot.AverageQueueLatencyMs,
            "Maximum queue latency is smaller than the average.");

        Assert(
            finalSnapshot.MaximumProcessingMs >=
            finalSnapshot.AverageProcessingMs,
            "Maximum processing time is smaller than the average.");
    }

    private static void WaitUntil(
        Func<bool> condition,
        string message)
    {
        var deadline =
            DateTime.UtcNow +
            TimeSpan.FromSeconds(
                5);

        while (DateTime.UtcNow <
               deadline)
        {
            if (condition())
                return;

            Thread.Sleep(
                10);
        }

        throw new InvalidOperationException(
            message);
    }

    private sealed class TrackingArrayPool<T> :
        ArrayPool<T>
    {
        private int _rentCount;
        private int _returnCount;

        public int RentCount =>
            Volatile.Read(
                ref _rentCount);

        public int ReturnCount =>
            Volatile.Read(
                ref _returnCount);

        public override T[] Rent(
            int minimumLength)
        {
            Interlocked.Increment(
                ref _rentCount);

            return new T[
                Math.Max(
                    1,
                    minimumLength)];
        }

        public override void Return(
            T[] array,
            bool clearArray = false)
        {
            ArgumentNullException.ThrowIfNull(
                array);

            if (clearArray)
            {
                Array.Clear(
                    array,
                    0,
                    array.Length);
            }

            Interlocked.Increment(
                ref _returnCount);
        }
    }

    private static MethodInfo PrivateStatic(
        Type type,
        string name) =>
        type.GetMethod(
            name,
            BindingFlags.NonPublic |
            BindingFlags.Static)
        ??
        throw new MissingMethodException(
            type.FullName,
            name);

    private static T Invoke<T>(
        MethodInfo method,
        params object?[] arguments)
    {
        var value =
            method.Invoke(
                null,
                arguments);

        return value is T typed
            ? typed
            : throw new InvalidCastException(
                $"Method {method.Name} did not return {typeof(T).Name}.");
    }

    private static List<int> BitsMsbFirst(
        uint value,
        int count)
    {
        var result =
            new List<int>(
                count);

        for (var index = 0;
             index < count;
             index++)
        {
            result.Add(
                (int)(
                    value >>
                    (count -
                     index -
                     1)) &
                1);
        }

        return result;
    }

    private static uint BuildWordMsbFirst(
        IReadOnlyList<int> bits,
        int count)
    {
        uint value =
            0;

        for (var index = 0;
             index < count;
             index++)
        {
            value =
                (value <<
                 1) |
                ((uint)bits[index] &
                 1u);
        }

        return value;
    }

    private static IEnumerable<int>
        BytesToBitsLsbFirst(
            IReadOnlyList<byte> bytes)
    {
        foreach (var value in
                 bytes)
        {
            for (var bit = 0;
                 bit < 8;
                 bit++)
            {
                yield return
                    (value >>
                     bit) &
                    1;
            }
        }
    }

    private static IEnumerable<int>
        StuffBits(
            IEnumerable<int> input)
    {
        var consecutiveOnes =
            0;

        foreach (var rawBit in
                 input)
        {
            var bit =
                rawBit &
                1;

            yield return
                bit;

            if (bit == 0)
            {
                consecutiveOnes =
                    0;

                continue;
            }

            consecutiveOnes++;

            if (consecutiveOnes !=
                5)
            {
                continue;
            }

            yield return
                0;

            consecutiveOnes =
                0;
        }
    }

    private static byte[] AppendHdlcFcs(
        IReadOnlyList<byte> data)
    {
        var candidate =
            new byte[
                data.Count +
                2];

        for (var index = 0;
             index < data.Count;
             index++)
        {
            candidate[index] =
                data[index];
        }

        for (var first = 0;
             first <= 0xFF;
             first++)
        {
            candidate[^2] =
                (byte)first;

            for (var second = 0;
                 second <= 0xFF;
                 second++)
            {
                candidate[^1] =
                    (byte)second;

                if (CalculateHdlcFcs(
                        candidate) ==
                    0xF0B8)
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException(
            "Could not generate HDLC FCS.");
    }

    private static ushort CalculateHdlcFcs(
        IReadOnlyList<byte> data)
    {
        var crc =
            0xFFFF;

        foreach (var value in
                 data)
        {
            crc ^=
                value;

            for (var bit = 0;
                 bit < 8;
                 bit++)
            {
                crc =
                    (crc &
                     1) != 0
                        ? (crc >>
                           1) ^
                          0x8408
                        : crc >>
                          1;
            }
        }

        return (ushort)crc;
    }

    private static byte[] AppendAcarsCrc(
        IReadOnlyList<byte> data)
    {
        var candidate =
            new byte[
                data.Count +
                2];

        for (var index = 0;
             index < data.Count;
             index++)
        {
            candidate[index] =
                data[index];
        }

        for (var first = 0;
             first <= 0xFF;
             first++)
        {
            candidate[^2] =
                (byte)first;

            for (var second = 0;
                 second <= 0xFF;
                 second++)
            {
                candidate[^1] =
                    (byte)second;

                if (CalculateAcarsCrc(
                        candidate) ==
                    0)
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException(
            "Could not generate ACARS CRC.");
    }

    private static ushort CalculateAcarsCrc(
        IReadOnlyList<byte> data)
    {
        var crc =
            0;

        foreach (var value in
                 data)
        {
            crc ^=
                value;

            for (var bit = 0;
                 bit < 8;
                 bit++)
            {
                crc =
                    (crc &
                     1) != 0
                        ? (crc >>
                           1) ^
                          0x8408
                        : crc >>
                          1;
            }
        }

        return (ushort)crc;
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                message);
        }
    }

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default
                .Equals(
                    expected,
                    actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected {expected}; actual {actual}.");
        }
    }

    private static void AssertSequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(
                actual))
        {
            throw new InvalidOperationException(
                message);
        }
    }

    private static void AssertThrows<TException>(
        Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}.");
    }
}
