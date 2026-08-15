// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace SDRSharp.Radio;

/// <summary>
/// Compile-time-only SDR# contract used by CI. Never distribute this assembly
/// with the plugin and never use it instead of the official SDR# SDK.
/// </summary>
[StructLayout(
    LayoutKind.Sequential)]
public struct Complex
{
    public float Real;

    public float Imag;
}

public interface IBaseProcessor
{
    bool Enabled
    {
        get;
        set;
    }
}

public interface IStreamProcessor
{
    double SampleRate
    {
        get;
        set;
    }
}

public unsafe interface IIQProcessor
{
    void Process(
        Complex* buffer,
        int length);
}
