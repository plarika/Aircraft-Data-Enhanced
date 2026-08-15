// SPDX-License-Identifier: MIT
using System.Numerics;
using System.Text;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed record Vdl2FrameSyncResult(
    bool PreambleFound,
    int TimingPhaseIndex,
    double TimingOffsetSamples,
    int PreambleSymbolIndex,
    double PreambleRmsDeg,
    double PreambleCorrelation,
    double ResidualFrequencyOffsetHz,
    double ResidualPhaseSlopeRadPerSymbol,
    int SymbolsAfterPreamble,
    int RawBitCount,
    string RawBitPreview,
    bool HeaderAvailable,
    bool HeaderFecValid,
    bool HeaderCorrected,
    int HeaderCorrectedBitFromMsb,
    int HeaderSyndromeBefore,
    int HeaderSyndromeAfter,
    int ReservedBits,
    int TransmissionLengthBits,
    int TransmissionLengthOctets,
    int HeaderFecBits,
    string RawHeaderBits,
    string DescrambledHeaderBits,
    string HeaderHex,
    bool HeaderValid,
    string Status,
    Vdl2PayloadResult? Payload = null)
{
    public static Vdl2FrameSyncResult Empty(string status) => new(
        false, -1, 0, -1, 0, 0, 0, 0, 0, 0, string.Empty,
        false, false, false, -1, 0, 0, 0, 0, 0, 0,
        string.Empty, string.Empty, string.Empty, false, status);
}

internal static class Vdl2FrameDecoder
{
    private readonly record struct PreambleCandidate(
        int TimingPhaseIndex,
        double TimingOffsetSamples,
        int SymbolIndex,
        double RmsRad,
        double Correlation,
        double PhaseSlopeRadPerSymbol,
        Complex[] Symbols);

    private const int TimingPhases = 128;
    private const int PreambleSymbols = 16;
    private const int MaximumPreambleSearchSymbols = 256;
    private const int HeaderBits = 25;
    private const int HeaderPayloadBits = 22;
    private const int TransmissionLengthBits = 17;
    private const int HeaderFecBits = 5;
    private const int MaximumFrameLength = 0x3FFF;
    private const int MaximumCorrectedFrameLength = 0x1FFF;
    private const ushort LfsrInitialValue = 0x6959;

    private static readonly int[] GrayCode = [0, 1, 3, 2, 6, 7, 5, 4];

    private static readonly double[] ExpectedPreamblePhases =
    [
         0 * Math.PI / 4,  3 * Math.PI / 4, -3 * Math.PI / 4,  1 * Math.PI / 4,
         1 * Math.PI / 4,  2 * Math.PI / 4,  0 * Math.PI / 4,  4 * Math.PI / 4,
        -3 * Math.PI / 4,  4 * Math.PI / 4, -2 * Math.PI / 4,  3 * Math.PI / 4,
         1 * Math.PI / 4, -2 * Math.PI / 4, -3 * Math.PI / 4,  0 * Math.PI / 4
    ];

    private static readonly uint[] HeaderParityMasks =
    [
        0b0000000011111111111110000u,
        0b0011111100001111111101000u,
        0b1100011100110000111100100u,
        0b1101101101010011001100010u,
        0b0110100111100101010100001u
    ];

    public static Vdl2FrameSyncResult Decode(
        Complex[] filtered,
        int burstStart,
        int burstEnd,
        double sampleRate,
        double symbolRate)
    {
        if (filtered.Length < 2 || burstStart < 0 || burstEnd > filtered.Length ||
            burstEnd - burstStart < 64 || sampleRate <= 0 || symbolRate <= 0)
        {
            return Vdl2FrameSyncResult.Empty("frame_input_invalid");
        }

        var best = FindBestPreamble(filtered, burstStart, burstEnd, sampleRate, symbolRate);
        if (best is null)
            return Vdl2FrameSyncResult.Empty("no_preamble");

        var candidate = best.Value;
        var rmsDeg = candidate.RmsRad * 180.0 / Math.PI;
        var residualHz = candidate.PhaseSlopeRadPerSymbol * symbolRate / (2.0 * Math.PI);
        var accepted = candidate.RmsRad <= 0.42 && candidate.Correlation >= 0.91;

        if (!accepted)
        {
            return new Vdl2FrameSyncResult(
                false, candidate.TimingPhaseIndex, candidate.TimingOffsetSamples,
                candidate.SymbolIndex, rmsDeg, candidate.Correlation, residualHz,
                candidate.PhaseSlopeRadPerSymbol, 0, 0, string.Empty,
                false, false, false, -1, 0, 0, 0, 0, 0, 0,
                string.Empty, string.Empty, string.Empty, false,
                "preamble_metric_rejected");
        }

        var bits = DemodulateAfterPreamble(
            candidate.Symbols,
            candidate.SymbolIndex,
            candidate.PhaseSlopeRadPerSymbol);

        var symbolsAfter = Math.Max(
            0,
            candidate.Symbols.Length - (candidate.SymbolIndex + PreambleSymbols));
        var preview = BitsToString(bits, Math.Min(bits.Count, 192));

        if (bits.Count < HeaderBits)
        {
            return new Vdl2FrameSyncResult(
                true, candidate.TimingPhaseIndex, candidate.TimingOffsetSamples,
                candidate.SymbolIndex, rmsDeg, candidate.Correlation, residualHz,
                candidate.PhaseSlopeRadPerSymbol, symbolsAfter, bits.Count, preview,
                false, false, false, -1, 0, 0, 0, 0, 0, 0,
                BitsToString(bits, bits.Count), string.Empty, string.Empty, false,
                "preamble_found_header_unavailable");
        }

        var descrambled = Descramble(bits);
        var rawHeaderBits = BitsToString(bits, HeaderBits);
        var descrambledHeaderBits = BitsToString(descrambled, HeaderBits);
        var rawHeader = BuildWordMsbFirst(descrambled, HeaderBits);

        // The three reserved bits are forced to zero before header FEC checking.
        var header = rawHeader & ((1u << HeaderPayloadBits) - 1u);
        var syndromeBefore = CalculateSyndrome(header);
        var corrected = header;
        var correctedBitFromMsb = -1;
        var correctionCount = 0;

        if (syndromeBefore != 0)
        {
            for (var bitPosition = 0; bitPosition < HeaderBits; bitPosition++)
            {
                var trial = header ^ (1u << bitPosition);
                if (CalculateSyndrome(trial) != 0)
                    continue;
                correctionCount++;
                corrected = trial;
                correctedBitFromMsb = HeaderBits - 1 - bitPosition;
            }
        }

        if (syndromeBefore != 0 && correctionCount != 1)
        {
            corrected = header;
            correctedBitFromMsb = -1;
        }

        var syndromeAfter = CalculateSyndrome(corrected);
        var headerCorrected = syndromeBefore != 0 && correctionCount == 1;
        var fecValid = syndromeAfter == 0 && (syndromeBefore == 0 || correctionCount == 1);
        var reservedBits = (int)((corrected >> HeaderPayloadBits) & 0x7u);
        var encodedLength = (corrected >> HeaderFecBits) & ((1u << TransmissionLengthBits) - 1u);
        var lengthBits = (int)ReverseBits(encodedLength, TransmissionLengthBits);
        var lengthOctets = (lengthBits + 7) / 8;
        var fecBits = (int)(corrected & ((1u << HeaderFecBits) - 1u));
        var lengthLimit = headerCorrected ? MaximumCorrectedFrameLength : MaximumFrameLength;
        var lengthPlausible = lengthBits > 0 && lengthBits <= lengthLimit;
        var valid = fecValid && reservedBits == 0 && lengthPlausible;

        var headerStatus = !fecValid
            ? "header_fec_failed"
            : reservedBits != 0
                ? "header_reserved_bits_invalid"
                : !lengthPlausible
                    ? "header_length_invalid"
                    : "VDL2-HEADER-VALID";

        var payload =
            valid
                ? Vdl2PayloadDecoder.Decode(
                    descrambled,
                    lengthBits)
                : Vdl2PayloadResult.NotAttempted(
                    headerStatus);

        var status =
            valid && payload.Attempted
                ? payload.Status
                : headerStatus;

        return new Vdl2FrameSyncResult(
            true, candidate.TimingPhaseIndex, candidate.TimingOffsetSamples,
            candidate.SymbolIndex, rmsDeg, candidate.Correlation, residualHz,
            candidate.PhaseSlopeRadPerSymbol, symbolsAfter, bits.Count, preview,
            true, fecValid, headerCorrected, correctedBitFromMsb,
            syndromeBefore, syndromeAfter, reservedBits, lengthBits, lengthOctets,
            fecBits, rawHeaderBits, descrambledHeaderBits, corrected.ToString("X7"),
            valid, status, payload);
    }

    private static PreambleCandidate? FindBestPreamble(
        Complex[] filtered,
        int burstStart,
        int burstEnd,
        double sampleRate,
        double symbolRate)
    {
        var samplesPerSymbol = sampleRate / symbolRate;
        PreambleCandidate? best = null;
        var bestMetric = double.PositiveInfinity;

        for (var phaseIndex = 0; phaseIndex < TimingPhases; phaseIndex++)
        {
            var offset = phaseIndex / (double)TimingPhases * samplesPerSymbol;
            var symbols = SampleSymbols(filtered, burstStart, burstEnd, offset, samplesPerSymbol);
            if (symbols.Length < PreambleSymbols)
                continue;

            var maximumStart = Math.Min(
                MaximumPreambleSearchSymbols,
                symbols.Length - PreambleSymbols);

            for (var symbolIndex = 0; symbolIndex <= maximumStart; symbolIndex++)
            {
                var candidate = EvaluatePreamble(symbols, phaseIndex, offset, symbolIndex);
                var metric = candidate.RmsRad + 0.05 * (1.0 - candidate.Correlation);
                if (metric >= bestMetric)
                    continue;
                bestMetric = metric;
                best = candidate;
            }
        }

        return best;
    }

    private static PreambleCandidate EvaluatePreamble(
        Complex[] symbols,
        int timingPhaseIndex,
        double timingOffset,
        int symbolIndex)
    {
        Span<double> errors = stackalloc double[PreambleSymbols];

        for (var index = 0; index < PreambleSymbols; index++)
        {
            var symbol = symbols[symbolIndex + index];
            var actual = Math.Atan2(symbol.Imaginary, symbol.Real);
            var current = WrapPhase(actual - ExpectedPreamblePhases[index]);

            if (index > 0)
            {
                while (current - errors[index - 1] > Math.PI)
                    current -= 2.0 * Math.PI;
                while (current - errors[index - 1] < -Math.PI)
                    current += 2.0 * Math.PI;
            }

            errors[index] = current;
        }

        var meanIndex = (PreambleSymbols - 1) / 2.0;
        var meanError = 0.0;
        for (var index = 0; index < PreambleSymbols; index++)
            meanError += errors[index];
        meanError /= PreambleSymbols;

        var numerator = 0.0;
        var denominator = 0.0;
        for (var index = 0; index < PreambleSymbols; index++)
        {
            var centered = index - meanIndex;
            numerator += centered * (errors[index] - meanError);
            denominator += centered * centered;
        }

        var slope = denominator > 0 ? numerator / denominator : 0.0;
        var intercept = meanError - slope * meanIndex;
        var squaredError = 0.0;
        var correlationReal = 0.0;
        var correlationImaginary = 0.0;

        for (var index = 0; index < PreambleSymbols; index++)
        {
            var residual = WrapPhase(errors[index] - (intercept + slope * index));
            squaredError += residual * residual;
            correlationReal += Math.Cos(residual);
            correlationImaginary += Math.Sin(residual);
        }

        var rms = Math.Sqrt(squaredError / PreambleSymbols);
        var correlation = Math.Sqrt(
            correlationReal * correlationReal +
            correlationImaginary * correlationImaginary) / PreambleSymbols;

        return new PreambleCandidate(
            timingPhaseIndex,
            timingOffset,
            symbolIndex,
            rms,
            correlation,
            slope,
            symbols);
    }

    private static Complex[] SampleSymbols(
        Complex[] samples,
        int start,
        int end,
        double offset,
        double samplesPerSymbol)
    {
        var result = new List<Complex>();
        for (var position = start + offset; position < end - 1; position += samplesPerSymbol)
        {
            var index = (int)Math.Floor(position);
            var fraction = position - index;
            result.Add(samples[index] * (1.0 - fraction) + samples[index + 1] * fraction);
        }
        return result.ToArray();
    }

    private static List<int> DemodulateAfterPreamble(
        Complex[] symbols,
        int preambleSymbolIndex,
        double phaseSlopeRadPerSymbol)
    {
        var firstDataSymbol = preambleSymbolIndex + PreambleSymbols;
        var bits = new List<int>(Math.Max(0, (symbols.Length - firstDataSymbol) * 3));

        for (var index = firstDataSymbol; index < symbols.Length; index++)
        {
            var current = Math.Atan2(symbols[index].Imaginary, symbols[index].Real);
            var previous = Math.Atan2(symbols[index - 1].Imaginary, symbols[index - 1].Real);
            var differential = NormalizeZeroToTwoPi(current - previous - phaseSlopeRadPerSymbol);
            var sector = (int)Math.Round(differential / (Math.PI / 4.0)) % 8;
            var value = GrayCode[sector];
            bits.Add((value >> 2) & 1);
            bits.Add((value >> 1) & 1);
            bits.Add(value & 1);
        }

        return bits;
    }

    private static List<int> Descramble(IReadOnlyList<int> bits)
    {
        var output = new List<int>(bits.Count);
        var lfsr = LfsrInitialValue;

        foreach (var bit in bits)
        {
            var feedback = ((lfsr >> 0) ^ (lfsr >> 14)) & 1;
            lfsr = (ushort)((lfsr >> 1) | (feedback << 14));
            output.Add(bit ^ feedback);
        }

        return output;
    }

    private static uint BuildWordMsbFirst(IReadOnlyList<int> bits, int count)
    {
        uint value = 0;
        for (var index = 0; index < count; index++)
            value = (value << 1) | ((uint)bits[index] & 1u);
        return value;
    }

    private static int CalculateSyndrome(uint header)
    {
        var syndrome = 0;
        for (var row = 0; row < HeaderParityMasks.Length; row++)
            syndrome |= Parity(header & HeaderParityMasks[row]) << row;
        return syndrome;
    }

    private static int Parity(uint value)
    {
        value ^= value >> 16;
        value ^= value >> 8;
        value ^= value >> 4;
        value &= 0xFu;
        return (0x6996 >> (int)value) & 1;
    }

    private static uint ReverseBits(uint value, int bitCount)
    {
        uint result = 0;
        for (var index = 0; index < bitCount; index++)
        {
            result = (result << 1) | (value & 1u);
            value >>= 1;
        }
        return result;
    }

    private static string BitsToString(IReadOnlyList<int> bits, int count)
    {
        var maximum = Math.Min(count, bits.Count);
        var builder = new StringBuilder(maximum);
        for (var index = 0; index < maximum; index++)
            builder.Append(bits[index] == 0 ? '0' : '1');
        return builder.ToString();
    }

    private static double WrapPhase(double phase)
    {
        while (phase <= -Math.PI)
            phase += 2.0 * Math.PI;
        while (phase > Math.PI)
            phase -= 2.0 * Math.PI;
        return phase;
    }

    private static double NormalizeZeroToTwoPi(double phase)
    {
        phase %= 2.0 * Math.PI;
        return phase < 0 ? phase + 2.0 * Math.PI : phase;
    }
}
