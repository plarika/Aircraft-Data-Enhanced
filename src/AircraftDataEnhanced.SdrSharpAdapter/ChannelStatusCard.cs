// SPDX-License-Identifier: MIT
using System.Drawing.Drawing2D;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed class ChannelStatusCard : UserControl
{
    private readonly Label _frequency = new();
    private readonly Label _status = new();
    private readonly Label _level = new();
    private bool _active;

    public long FrequencyHz { get; }

    public event Action<long>? Selected;

    public ChannelStatusCard(long frequencyHz)
    {
        FrequencyHz = frequencyHz;

        Width = 154;
        Height = 70;
        Margin = new Padding(4);
        Padding = new Padding(12, 8, 8, 6);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        BackColor = AdeVisualTheme.Surface;
        ForeColor = AdeVisualTheme.TextPrimary;

        _frequency.Text = $"{frequencyHz / 1_000_000.0:0.000} MHz";
        _frequency.Font = AdeVisualTheme.UiFont(9.5f, FontStyle.Bold);
        _frequency.ForeColor = AdeVisualTheme.TextPrimary;
        _frequency.AutoSize = true;
        _frequency.Location = new Point(12, 8);
        _frequency.BackColor = Color.Transparent;

        _status.Text = "STANDBY";
        _status.Font = AdeVisualTheme.UiFont(7.5f, FontStyle.Bold);
        _status.ForeColor = AdeVisualTheme.TextSecondary;
        _status.AutoSize = true;
        _status.Location = new Point(12, 31);
        _status.BackColor = Color.Transparent;

        _level.Text = "Click to tune";
        _level.Font = AdeVisualTheme.UiFont(7.8f);
        _level.ForeColor = AdeVisualTheme.TextMuted;
        _level.AutoSize = true;
        _level.Location = new Point(12, 48);
        _level.BackColor = Color.Transparent;

        Controls.Add(_frequency);
        Controls.Add(_status);
        Controls.Add(_level);

        Click += (_, _) => Selected?.Invoke(FrequencyHz);
        foreach (Control child in Controls)
            child.Click += (_, _) => Selected?.Invoke(FrequencyHz);
    }

    public void UpdateState(bool active, string status, double levelDb)
    {
        _active = active;
        BackColor = active
            ? AdeVisualTheme.SurfaceSelected
            : AdeVisualTheme.Surface;

        _frequency.ForeColor = active
            ? AdeVisualTheme.Cyan
            : AdeVisualTheme.TextPrimary;

        _status.ForeColor = active
            ? AdeVisualTheme.Success
            : AdeVisualTheme.TextSecondary;

        _level.ForeColor = active
            ? AdeVisualTheme.TextPrimary
            : AdeVisualTheme.TextMuted;

        _status.Text = active
            ? status.ToUpperInvariant()
            : "STANDBY";

        _level.Text = active
            ? $"{levelDb:0.0} dBFS"
            : "Click to tune";

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? AdeVisualTheme.AppBackground);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRectangle(bounds, 7);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(
            _active
                ? AdeVisualTheme.Cyan
                : AdeVisualTheme.Border);
        using var accent = new SolidBrush(
            _active
                ? AdeVisualTheme.Success
                : AdeVisualTheme.TextMuted);

        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        e.Graphics.FillEllipse(accent, Width - 19, 12, 7, 7);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
