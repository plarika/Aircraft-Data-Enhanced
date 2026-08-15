// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Windows.Forms;
using SDRSharp.Radio;

namespace SDRSharp.Common;

/// <summary>
/// Compile-time-only SDR# contracts for public CI. Runtime testing must use the
/// official SDK binaries obtained from Airspy.
/// </summary>
public enum ProcessorType
{
    DecimatedAndFilteredIQ
}

public interface ISharpControl :
    INotifyPropertyChanged
{
    long Frequency
    {
        get;
        set;
    }

    void RegisterStreamHook(
        IStreamProcessor processor,
        ProcessorType processorType);
}

public interface ISharpPlugin
{
    string DisplayName
    {
        get;
    }

    UserControl Gui
    {
        get;
    }

    void Initialize(
        ISharpControl control);

    void Close();
}
