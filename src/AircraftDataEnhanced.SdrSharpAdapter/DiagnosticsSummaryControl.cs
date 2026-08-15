// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal sealed class DiagnosticsSummaryControl : UserControl
{
    private readonly DiagnosticBar _queueBar = new();
    private readonly DiagnosticBar _memoryBar = new();
    private readonly Label _queueValue = ValueLabel();
    private readonly Label _processingValue = ValueLabel();
    private readonly Label _memoryValue = ValueLabel();
    private readonly Label _databaseValue = ValueLabel();
    private readonly Label _exportValue = ValueLabel();
    private readonly Label _waterfallValue = ValueLabel();
    private readonly Label _overallStatus = new()
    {
        Text = "WAITING",
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = AdeVisualTheme.TextSecondary,
        BackColor = AdeVisualTheme.SurfaceSelected,
        Font = AdeVisualTheme.UiFont(7.8f, FontStyle.Bold),
        Padding = new Padding(10, 5, 10, 5),
        Margin = Padding.Empty
    };

    public DiagnosticsSummaryControl()
    {
        Dock = DockStyle.Fill;
        BackColor = AdeVisualTheme.AppBackground;
        ForeColor = AdeVisualTheme.TextPrimary;
        Padding = new Padding(12);
        AutoScroll = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Color.Transparent,
            Padding = Padding.Empty,
            Margin = new Padding(0, 10, 0, 0),
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));

        metrics.Controls.Add(
            BuildMetricSection("▤", "IQ QUEUE USAGE", _queueValue, _queueBar),
            0,
            0);
        metrics.Controls.Add(
            BuildMetricSection("⌁", "PROCESSING", _processingValue, null),
            1,
            0);
        metrics.Controls.Add(
            BuildMetricSection("◫", "MEMORY", _memoryValue, _memoryBar),
            0,
            1);
        metrics.Controls.Add(
            BuildMetricSection("◉", "PERSISTENCE", _databaseValue, null),
            1,
            1);
        metrics.Controls.Add(
            BuildMetricSection("⇧", "EXPORT", _exportValue, null),
            0,
            2);
        metrics.Controls.Add(
            BuildMetricSection("▥", "WATERFALL", _waterfallValue, null),
            1,
            2);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(metrics, 0, 1);
        Controls.Add(root);

        var compactLayoutApplied = false;
        var metricsLayoutUpdating = false;

        void ApplyMetricsLayout(bool compact)
        {
            if (metricsLayoutUpdating || compactLayoutApplied == compact)
                return;

            metricsLayoutUpdating = true;
            var cards = metrics.Controls.Cast<Control>().ToArray();

            metrics.SuspendLayout();
            try
            {
                // Remove the existing controls before changing the dimensions.
                // A FixedSize TableLayoutPanel throws when its capacity is reduced
                // while controls still occupy cells that no longer exist.
                metrics.Controls.Clear();
                metrics.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
                metrics.ColumnStyles.Clear();
                metrics.RowStyles.Clear();

                metrics.ColumnCount = compact ? 1 : 2;
                metrics.RowCount = compact ? cards.Length : (cards.Length + 1) / 2;

                if (compact)
                {
                    metrics.ColumnStyles.Add(
                        new ColumnStyle(SizeType.Percent, 100));
                }
                else
                {
                    metrics.ColumnStyles.Add(
                        new ColumnStyle(SizeType.Percent, 50));
                    metrics.ColumnStyles.Add(
                        new ColumnStyle(SizeType.Percent, 50));
                }

                for (var row = 0; row < metrics.RowCount; row++)
                {
                    metrics.RowStyles.Add(
                        new RowStyle(SizeType.Absolute, 104));
                }

                for (var index = 0; index < cards.Length; index++)
                {
                    var column = compact ? 0 : index % 2;
                    var row = compact ? index : index / 2;
                    metrics.Controls.Add(cards[index], column, row);
                }

                metrics.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
                compactLayoutApplied = compact;
            }
            finally
            {
                metrics.ResumeLayout(performLayout: true);
                metricsLayoutUpdating = false;
            }
        }

        Resize += (_, _) =>
            ApplyMetricsLayout(ClientSize.Width < 620);

        // ClientSize may still be zero while the control is being constructed.
        // Apply the first layout explicitly so the initial docking pass is safe.
        ApplyMetricsLayout(ClientSize.Width < 620);
    }

    public void UpdateState(
        int queuePercent,
        string queueText,
        bool processingHealthy,
        string processingText,
        long workingSetBytes,
        bool databaseHealthy,
        string databaseText,
        bool exportHealthy,
        string exportText,
        int waterfallRows,
        long waterfallDroppedFrames)
    {
        queuePercent = Math.Clamp(queuePercent, 0, 100);
        _queueBar.Value = queuePercent;
        _queueBar.State = queuePercent >= 90
            ? AdeVisualState.Warning
            : AdeVisualState.Active;
        _queueValue.Text = $"{queuePercent}%  ·  {queueText}";
        _queueValue.ForeColor = queuePercent >= 90
            ? AdeVisualTheme.Warning
            : AdeVisualTheme.AccentBright;

        _processingValue.Text = processingText;
        _processingValue.ForeColor = processingHealthy
            ? AdeVisualTheme.Success
            : AdeVisualTheme.Warning;

        var workingSetMb = workingSetBytes / 1024d / 1024d;
        var memoryPercent = (int)Math.Clamp(workingSetMb / 512d * 100d, 0d, 100d);
        _memoryBar.Value = memoryPercent;
        _memoryBar.State = memoryPercent >= 80
            ? AdeVisualState.Warning
            : AdeVisualState.Active;
        _memoryValue.Text = $"{workingSetMb:0.0} MiB  ·  {memoryPercent}% of 512 MiB budget";
        _memoryValue.ForeColor = memoryPercent >= 80
            ? AdeVisualTheme.Warning
            : AdeVisualTheme.TextPrimary;

        _databaseValue.Text = databaseText;
        _databaseValue.ForeColor = databaseHealthy
            ? AdeVisualTheme.Success
            : AdeVisualTheme.Warning;

        _exportValue.Text = exportText;
        _exportValue.ForeColor = exportHealthy
            ? AdeVisualTheme.Success
            : AdeVisualTheme.TextSecondary;

        _waterfallValue.Text = $"{waterfallRows} rows  ·  {waterfallDroppedFrames} dropped";
        _waterfallValue.ForeColor = waterfallDroppedFrames == 0
            ? AdeVisualTheme.Success
            : AdeVisualTheme.Warning;

        var healthy =
            processingHealthy &&
            databaseHealthy &&
            queuePercent < 90 &&
            waterfallDroppedFrames == 0;

        _overallStatus.Text = healthy ? "●  HEALTHY" : "●  ATTENTION";
        _overallStatus.ForeColor = healthy
            ? AdeVisualTheme.Success
            : AdeVisualTheme.Warning;
    }

    private Control BuildHeader()
    {
        var header = new AdeCardPanel
        {
            Dock = DockStyle.Top,
            Height = 74,
            Padding = new Padding(16, 12, 14, 12),
            Margin = Padding.Empty
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(
            new Label
            {
                Text = "SYSTEM DIAGNOSTICS",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = AdeVisualTheme.TextPrimary,
                Font = AdeVisualTheme.UiFont(10.2f, FontStyle.Bold),
                Margin = Padding.Empty
            },
            0,
            0);

        layout.Controls.Add(_overallStatus, 1, 0);
        layout.SetRowSpan(_overallStatus, 2);

        layout.Controls.Add(
            new Label
            {
                Text = "Live health of the decoder, queues, memory and local persistence.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                ForeColor = AdeVisualTheme.TextSecondary,
                Font = AdeVisualTheme.UiFont(8.2f),
                AutoEllipsis = true,
                Margin = Padding.Empty
            },
            0,
            1);

        header.Controls.Add(layout);
        return header;
    }

    private static Control BuildMetricSection(
        string glyph,
        string title,
        Label value,
        DiagnosticBar? bar)
    {
        var card = new AdeCardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 11, 14, 11),
            Margin = new Padding(4)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = bar is null ? 2 : 3,
            BackColor = Color.Transparent,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        if (bar is not null)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 13));

        var icon = new Label
        {
            Text = glyph,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = AdeVisualTheme.AccentBright,
            Font = AdeVisualTheme.UiFont(14.0f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        layout.Controls.Add(icon, 0, 0);
        layout.SetRowSpan(icon, bar is null ? 2 : 3);

        layout.Controls.Add(
            new Label
            {
                Text = title,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = AdeVisualTheme.TextSecondary,
                Font = AdeVisualTheme.UiFont(7.8f, FontStyle.Bold),
                Margin = Padding.Empty
            },
            1,
            0);

        layout.Controls.Add(value, 1, 1);

        if (bar is not null)
        {
            bar.Dock = DockStyle.Fill;
            bar.Margin = new Padding(0, 4, 0, 0);
            layout.Controls.Add(bar, 1, 2);
        }

        card.Controls.Add(layout);
        return card;
    }

    private static Label ValueLabel() =>
        new()
        {
            Text = "Waiting for runtime data",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = AdeVisualTheme.TextSecondary,
            Font = AdeVisualTheme.UiFont(8.65f, FontStyle.Bold),
            AutoEllipsis = true,
            Margin = Padding.Empty
        };
}

internal sealed class DiagnosticBar : Control
{
    private int _value;
    private AdeVisualState _state = AdeVisualState.Active;

    public DiagnosticBar()
    {
        DoubleBuffered = true;
        Height = 8;
        BackColor = AdeVisualTheme.Divider;
    }

    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 100);
            Invalidate();
        }
    }

    public AdeVisualState State
    {
        get => _state;
        set
        {
            _state = value;
            Invalidate();
        }
    }

    protected override void OnPaint(
        PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.Clear(AdeVisualTheme.Divider);

        var track = new Rectangle(0, 1, Math.Max(1, Width - 1), Math.Max(4, Height - 2));
        var trackRadius = Math.Max(1, Math.Min(3, track.Width / 2));
        using var trackPath = DashboardMetricCard.RoundedRectangle(track, trackRadius);
        using var trackBrush = new SolidBrush(AdeVisualTheme.Divider);
        e.Graphics.FillPath(trackBrush, trackPath);

        var width = (int)Math.Round(track.Width * (_value / 100d));
        if (width <= 0)
            return;

        var fill = new Rectangle(track.X, track.Y, Math.Max(4, width), track.Height);
        var fillRadius = Math.Max(1, Math.Min(3, fill.Width / 2));
        using var fillPath = DashboardMetricCard.RoundedRectangle(fill, fillRadius);
        using var brush = new SolidBrush(AdeVisualTheme.StateColor(_state));
        e.Graphics.FillPath(brush, fillPath);
    }
}
