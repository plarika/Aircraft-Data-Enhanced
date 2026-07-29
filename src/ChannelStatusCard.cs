// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal sealed class ChannelStatusCard : UserControl
{
    private readonly Label _frequency = new();
    private readonly Label _status = new();
    private readonly Label _level = new();

    public long FrequencyHz { get; }

    public event Action<long>? Selected;

    public ChannelStatusCard(long frequencyHz)
    {
        FrequencyHz = frequencyHz;

        Width = 150;
        Height = 72;
        Margin = new Padding(4);
        Padding = new Padding(6);
        BorderStyle = BorderStyle.FixedSingle;
        Cursor = Cursors.Hand;

        _frequency.Text = $"{frequencyHz / 1_000_000.0:0.000} MHz";
        _frequency.Font = new Font(Font, FontStyle.Bold);
        _frequency.AutoSize = true;

        _status.Text = "Standby";
        _status.AutoSize = true;
        _status.Top = 24;

        _level.Text = "No data";
        _level.AutoSize = true;
        _level.Top = 44;

        Controls.Add(_frequency);
        Controls.Add(_status);
        Controls.Add(_level);

        Click += (_, _) => Selected?.Invoke(FrequencyHz);
        foreach (Control child in Controls)
            child.Click += (_, _) => Selected?.Invoke(FrequencyHz);
    }

    public void UpdateState(bool active, string status, double levelDb)
    {
        BackColor = active ? Color.FromArgb(45, 75, 45) : SystemColors.Control;
        ForeColor = active ? Color.White : SystemColors.ControlText;
        foreach (Control child in Controls)
            child.ForeColor = ForeColor;

        _status.Text = active ? status : "Standby";
        _level.Text = active ? $"{levelDb:0.0} dBFS" : "Click to tune";
    }
}
