// SPDX-License-Identifier: LGPL-2.1-or-later
// Adapted from the classic Phil Karn/libfec Reed-Solomon implementation.
// See LICENSES/LGPL-2.1-or-later.txt and THIRD_PARTY_NOTICES.md.
namespace SDRSharp.AircraftDataEnhanced;

/// <summary>
/// Reed-Solomon RS(255,249) decoder used by VDL Mode 2.
/// Parameters: GF(256), polynomial 0x187, FCR 120, primitive root 1,
/// six parity symbols and no pad symbols.
///
/// The algorithm is an independent managed-C# adaptation of the classic
/// Phil Karn/libfec Berlekamp-Massey/Forney decoder. See
/// THIRD_PARTY_REFERENCES.md and LICENSE_PROTOCOL_COMPONENTS.md.
/// </summary>
internal static class ReedSolomon255249
{
    private const int Mm = 8;
    private const int Nn = 255;
    private const int GfPolynomial = 0x187;
    private const int FirstConsecutiveRoot = 120;
    private const int PrimitiveRoot = 1;
    private const int RootCount = 6;
    private const int Pad = 0;
    private const int A0 = Nn;

    private static readonly int[] AlphaTo = new int[Nn + 1];
    private static readonly int[] IndexOf = new int[Nn + 1];

    static ReedSolomon255249()
    {
        BuildFieldTables();
    }

    public static int Decode(
        byte[] data,
        IReadOnlyList<int>? erasurePositions,
        out int[] correctedLocations)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length != Nn)
        {
            throw new ArgumentException(
                "RS(255,249) decoder requires exactly 255 symbols.",
                nameof(data));
        }

        var erasures = erasurePositions?.ToArray() ?? Array.Empty<int>();

        if (erasures.Length > RootCount)
        {
            correctedLocations = Array.Empty<int>();
            return -1;
        }

        foreach (var position in erasures)
        {
            if (position < 0 || position >= Nn)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(erasurePositions),
                    position,
                    "Erasure positions must be between 0 and 254.");
            }
        }

        var syndrome = new int[RootCount];
        var lambda = new int[RootCount + 1];
        var b = new int[RootCount + 1];
        var t = new int[RootCount + 1];
        var omega = new int[RootCount + 1];
        var root = new int[RootCount];
        var register = new int[RootCount + 1];
        var locations = new int[RootCount];

        for (var index = 0; index < RootCount; index++)
            syndrome[index] = data[0];

        for (var symbolIndex = 1;
             symbolIndex < Nn - Pad;
             symbolIndex++)
        {
            for (var rootIndex = 0;
                 rootIndex < RootCount;
                 rootIndex++)
            {
                if (syndrome[rootIndex] == 0)
                {
                    syndrome[rootIndex] = data[symbolIndex];
                }
                else
                {
                    syndrome[rootIndex] =
                        data[symbolIndex] ^
                        AlphaTo[ModNn(
                            IndexOf[syndrome[rootIndex]] +
                            (FirstConsecutiveRoot + rootIndex) *
                            PrimitiveRoot)];
                }
            }
        }

        var syndromeError = 0;

        for (var index = 0; index < RootCount; index++)
        {
            syndromeError |= syndrome[index];
            syndrome[index] = IndexOf[syndrome[index]];
        }

        if (syndromeError == 0)
        {
            correctedLocations = Array.Empty<int>();
            return 0;
        }

        lambda[0] = 1;

        if (erasures.Length > 0)
        {
            lambda[1] = AlphaTo[ModNn(
                PrimitiveRoot *
                (Nn - 1 - erasures[0]))];

            for (var erasureIndex = 1;
                 erasureIndex < erasures.Length;
                 erasureIndex++)
            {
                var u = ModNn(
                    PrimitiveRoot *
                    (Nn - 1 - erasures[erasureIndex]));

                for (var coefficient = erasureIndex + 1;
                     coefficient > 0;
                     coefficient--)
                {
                    var previous = IndexOf[
                        lambda[coefficient - 1]];

                    if (previous != A0)
                    {
                        lambda[coefficient] ^=
                            AlphaTo[ModNn(u + previous)];
                    }
                }
            }
        }

        for (var index = 0;
             index <= RootCount;
             index++)
        {
            b[index] = IndexOf[lambda[index]];
        }

        var step = erasures.Length;
        var locatorDegreeEstimate = erasures.Length;

        while (++step <= RootCount)
        {
            var discrepancy = 0;

            for (var index = 0;
                 index < step;
                 index++)
            {
                if (lambda[index] != 0 &&
                    syndrome[step - index - 1] != A0)
                {
                    discrepancy ^=
                        AlphaTo[ModNn(
                            IndexOf[lambda[index]] +
                            syndrome[step - index - 1])];
                }
            }

            discrepancy = IndexOf[discrepancy];

            if (discrepancy == A0)
            {
                ShiftRight(b, A0);
                continue;
            }

            t[0] = lambda[0];

            for (var index = 0;
                 index < RootCount;
                 index++)
            {
                t[index + 1] =
                    b[index] != A0
                        ? lambda[index + 1] ^
                          AlphaTo[ModNn(
                              discrepancy +
                              b[index])]
                        : lambda[index + 1];
            }

            if (2 * locatorDegreeEstimate <=
                step + erasures.Length - 1)
            {
                locatorDegreeEstimate =
                    step +
                    erasures.Length -
                    locatorDegreeEstimate;

                for (var index = 0;
                     index <= RootCount;
                     index++)
                {
                    b[index] =
                        lambda[index] == 0
                            ? A0
                            : ModNn(
                                IndexOf[lambda[index]] -
                                discrepancy +
                                Nn);
                }
            }
            else
            {
                ShiftRight(b, A0);
            }

            Array.Copy(
                t,
                lambda,
                RootCount + 1);
        }

        var locatorDegree = 0;

        for (var index = 0;
             index <= RootCount;
             index++)
        {
            lambda[index] = IndexOf[lambda[index]];

            if (lambda[index] != A0)
                locatorDegree = index;
        }

        for (var index = 1;
             index <= RootCount;
             index++)
        {
            register[index] = lambda[index];
        }

        var count = 0;
        var locationIndex = 0;

        for (var index = 1;
             index <= Nn;
             index++)
        {
            var evaluation = 1;

            for (var coefficient = locatorDegree;
                 coefficient > 0;
                 coefficient--)
            {
                if (register[coefficient] == A0)
                    continue;

                register[coefficient] = ModNn(
                    register[coefficient] +
                    coefficient);

                evaluation ^=
                    AlphaTo[register[coefficient]];
            }

            if (evaluation == 0)
            {
                root[count] = index;
                locations[count] = locationIndex;
                count++;

                if (count == locatorDegree)
                    break;
            }

            locationIndex = ModNn(
                locationIndex + 1);
        }

        if (locatorDegree != count)
        {
            correctedLocations = Array.Empty<int>();
            return -1;
        }

        var evaluatorDegree = locatorDegree - 1;

        for (var index = 0;
             index <= evaluatorDegree;
             index++)
        {
            var value = 0;

            for (var coefficient = index;
                 coefficient >= 0;
                 coefficient--)
            {
                if (syndrome[index - coefficient] != A0 &&
                    lambda[coefficient] != A0)
                {
                    value ^=
                        AlphaTo[ModNn(
                            syndrome[index - coefficient] +
                            lambda[coefficient])];
                }
            }

            omega[index] = IndexOf[value];
        }

        for (var errorIndex = count - 1;
             errorIndex >= 0;
             errorIndex--)
        {
            var numeratorOne = 0;

            for (var index = evaluatorDegree;
                 index >= 0;
                 index--)
            {
                if (omega[index] != A0)
                {
                    numeratorOne ^=
                        AlphaTo[ModNn(
                            omega[index] +
                            index * root[errorIndex])];
                }
            }

            var numeratorTwo = AlphaTo[ModNn(
                root[errorIndex] *
                (FirstConsecutiveRoot - 1) +
                Nn)];

            var denominator = 0;

            for (var index =
                     Math.Min(
                         locatorDegree,
                         RootCount - 1) &
                     ~1;
                 index >= 0;
                 index -= 2)
            {
                if (lambda[index + 1] != A0)
                {
                    denominator ^=
                        AlphaTo[ModNn(
                            lambda[index + 1] +
                            index * root[errorIndex])];
                }
            }

            if (denominator == 0)
            {
                correctedLocations = Array.Empty<int>();
                return -1;
            }

            var location = locations[errorIndex];

            if (numeratorOne != 0 &&
                location >= Pad)
            {
                data[location - Pad] ^=
                    (byte)AlphaTo[ModNn(
                        IndexOf[numeratorOne] +
                        IndexOf[numeratorTwo] +
                        Nn -
                        IndexOf[denominator])];
            }
        }

        correctedLocations =
            locations
                .Take(count)
                .ToArray();

        return count;
    }

    private static void BuildFieldTables()
    {
        var shiftRegister = 1;

        for (var index = 0;
             index < Nn;
             index++)
        {
            IndexOf[shiftRegister] = index;
            AlphaTo[index] = shiftRegister;

            shiftRegister <<= 1;

            if ((shiftRegister &
                 (1 << Mm)) != 0)
            {
                shiftRegister ^=
                    GfPolynomial;
            }

            shiftRegister &= Nn;
        }

        IndexOf[0] = A0;
        AlphaTo[A0] = 0;
    }

    private static int ModNn(int value)
    {
        while (value >= Nn)
        {
            value -= Nn;
            value =
                (value >> Mm) +
                (value & Nn);
        }

        return value;
    }

    private static void ShiftRight(
        int[] values,
        int firstValue)
    {
        for (var index = values.Length - 1;
             index > 0;
             index--)
        {
            values[index] =
                values[index - 1];
        }

        values[0] = firstValue;
    }
}
