// SPDX-License-Identifier: MIT
using SDRSharp.Common;
using System.Text;
using System.Windows.Forms;

namespace SDRSharp.AircraftDataEnhanced;

public sealed class AircraftDataPlugin : ISharpPlugin
{
    private AircraftDataPanel? _panel;
    private UserControl? _errorPanel;

    public string DisplayName => "Aircraft Data Enhanced";

    public UserControl Gui =>
        _panel ??
        _errorPanel ??
        throw new InvalidOperationException("Plugin not initialized.");

    public void Initialize(ISharpControl control)
    {
        ArgumentNullException.ThrowIfNull(control);

        try
        {
            RuntimeDataPaths.EnsureInitialized();

            _panel =
                new AircraftDataPanel(
                    control);
        }
        catch (Exception ex)
        {
            WriteStartupLog(ex);

            _errorPanel = new UserControl
            {
                Dock = DockStyle.Fill,
                BackColor = AdeVisualTheme.AppBackground,
                ForeColor = AdeVisualTheme.TextPrimary,
                Padding = new Padding(12)
            };
            var text = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = AdeVisualTheme.NavigationBackground,
                ForeColor = AdeVisualTheme.Error,
                BorderStyle = BorderStyle.FixedSingle,
                Font = AdeVisualTheme.UiFont(9.0f),
                Text =
                    "Aircraft Data Enhanced failed to initialize." +
                    Environment.NewLine +
                    Environment.NewLine +
                    ex +
                    Environment.NewLine +
                    Environment.NewLine +
                    "Log: %LOCALAPPDATA%\\AircraftDataEnhanced\\startup-error.log"
            };
            _errorPanel.Controls.Add(text);
        }
    }

    public void Close()
    {
        _panel?.Shutdown();
        _panel?.Dispose();
        _panel = null;

        _errorPanel?.Dispose();
        _errorPanel = null;
    }

    private static void WriteStartupLog(Exception ex)
    {
        try
        {
            var path =
                RuntimeDataPaths.StartupLogPath;

            var directory =
                Path.GetDirectoryName(
                    path);

            if (!string.IsNullOrWhiteSpace(
                    directory))
            {
                Directory.CreateDirectory(
                    directory);
            }
            var content = new StringBuilder()
                .AppendLine($"UTC: {DateTimeOffset.UtcNow:O}")
                .AppendLine($"Runtime: {Environment.Version}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}")
                .AppendLine()
                .AppendLine(ex.ToString())
                .ToString();

            File.WriteAllText(path, content, Encoding.UTF8);
        }
        catch
        {
            // Logging must never prevent SDRSharp from continuing.
        }
    }
}
