// SPDX-License-Identifier: MIT
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal static class GoldenVectorTests
{
    private sealed record Expected(
        double SampleRate,
        double SymbolRate,
        int SampleCount,
        int TransmissionLengthBits,
        string ExpectedStatus,
        bool HeaderValid,
        bool PayloadComplete,
        bool ReedSolomonValid,
        int HdlcFrames,
        string IqSha256);

    public static void Run()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "testdata", "golden");
        var iqPath = Path.Combine(directory, "vdl2_full_frame_iq_f32le.bin");
        var expectedPath = Path.Combine(directory, "vdl2_full_frame_expected.json");
        var expected = JsonSerializer.Deserialize<Expected>(
            File.ReadAllText(expectedPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Golden expected JSON is invalid.");

        var bytes = File.ReadAllBytes(iqPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        AssertEqual(expected.IqSha256, actualHash, "Golden IQ SHA-256 changed.");
        Assert(bytes.Length % 8 == 0, "Golden IQ length is not complex-f32le aligned.");

        var samples = new Complex[bytes.Length / 8];
        for (var index = 0; index < samples.Length; index++)
        {
            var real = BitConverter.ToSingle(bytes, index * 8);
            var imaginary = BitConverter.ToSingle(bytes, index * 8 + 4);
            samples[index] = new Complex(real, imaginary);
        }

        AssertEqual(expected.SampleCount, samples.Length, "Golden sample count changed.");
        var result = Vdl2FrameDecoder.Decode(
            samples,
            0,
            samples.Length,
            expected.SampleRate,
            expected.SymbolRate);

        AssertEqual(expected.HeaderValid, result.HeaderValid, "Golden header validity changed.");
        AssertEqual(expected.TransmissionLengthBits, result.TransmissionLengthBits, "Golden length changed.");
        AssertEqual(expected.ExpectedStatus, result.Status, "Golden decoder status changed.");
        Assert(result.Payload is not null, "Golden payload was not attempted.");
        AssertEqual(expected.PayloadComplete, result.Payload!.Complete, "Golden payload completeness changed.");
        AssertEqual(expected.ReedSolomonValid, result.Payload.ReedSolomonValid, "Golden RS status changed.");
        AssertEqual(expected.HdlcFrames, result.Payload.HdlcFrames, "Golden HDLC count changed.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected={expected}; Actual={actual}.");
    }
}
