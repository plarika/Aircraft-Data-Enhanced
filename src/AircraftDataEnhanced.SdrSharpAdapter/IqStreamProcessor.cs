// SPDX-License-Identifier: MIT
using SDRSharp.Radio;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed class IqStreamProcessor : IIQProcessor, IStreamProcessor, IBaseProcessor
{
    public unsafe delegate void IqBlockHandler(Complex* buffer, double sampleRate, int length);
    public event IqBlockHandler? BlockAvailable;

    public double SampleRate { get; set; }
    public bool Enabled { get; set; } = true;

    public unsafe void Process(Complex* buffer, int length)
    {
        if (!Enabled || buffer is null || length <= 0)
            return;

        BlockAvailable?.Invoke(buffer, SampleRate, length);
    }
}
