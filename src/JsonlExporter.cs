// SPDX-License-Identifier: MIT
using System.Text;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed class JsonlExporter : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _path;

    public bool Enabled { get; private set; }
    public string Path => _path ?? string.Empty;

    public void Enable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            DisableInternal();
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };
            _path = path;
            Enabled = true;
        }
    }

    public void Write(Vdl2Message message)
    {
        lock (_gate)
        {
            if (!Enabled || _writer is null)
                return;
            _writer.WriteLine(message.RawJson);
        }
    }

    public void Disable()
    {
        lock (_gate)
            DisableInternal();
    }

    private void DisableInternal()
    {
        Enabled = false;
        _writer?.Dispose();
        _writer = null;
        _path = null;
    }

    public void Dispose() => Disable();
}
