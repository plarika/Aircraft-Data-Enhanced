// SPDX-License-Identifier: MIT
using System.Drawing.Drawing2D;

namespace SDRSharp.AircraftDataEnhanced;

internal enum AdeVisualState
{
    Neutral,
    Active,
    Success,
    Warning,
    Error,
    Purple
}

internal static class AdeVisualTheme
{
    public static readonly Color AppBackground = Color.FromArgb(11, 14, 20);
    public static readonly Color NavigationBackground = Color.FromArgb(15, 19, 26);
    public static readonly Color HeaderBackground = Color.FromArgb(15, 19, 26);
    public static readonly Color Surface = Color.FromArgb(21, 26, 35);
    public static readonly Color SurfaceRaised = Color.FromArgb(21, 26, 35);
    public static readonly Color SurfaceHover = Color.FromArgb(26, 32, 44);
    public static readonly Color SurfaceSelected = Color.FromArgb(23, 36, 61);
    public static readonly Color Border = Color.FromArgb(30, 38, 51);
    public static readonly Color BorderStrong = Color.FromArgb(42, 53, 68);
    public static readonly Color Divider = Color.FromArgb(30, 38, 51);

    public static readonly Color TextPrimary = Color.FromArgb(226, 232, 240);
    public static readonly Color TextSecondary = Color.FromArgb(148, 163, 184);
    public static readonly Color TextMuted = Color.FromArgb(100, 116, 139);

    public static readonly Color Accent = Color.FromArgb(59, 130, 246);
    public static readonly Color AccentBright = Color.FromArgb(96, 165, 250);
    public static readonly Color Cyan = Color.FromArgb(56, 189, 248);
    public static readonly Color Success = Color.FromArgb(34, 197, 94);
    public static readonly Color Warning = Color.FromArgb(234, 179, 8);
    public static readonly Color Error = Color.FromArgb(239, 68, 68);
    public static readonly Color Purple = Color.FromArgb(168, 85, 247);

    public static Font UiFont(
        float size = 9.0f,
        FontStyle style = FontStyle.Regular) =>
        new(
            "Segoe UI",
            size,
            style,
            GraphicsUnit.Point);

    public static Color StateColor(
        AdeVisualState state) =>
        state switch
        {
            AdeVisualState.Active => Accent,
            AdeVisualState.Success => Success,
            AdeVisualState.Warning => Warning,
            AdeVisualState.Error => Error,
            AdeVisualState.Purple => Purple,
            _ => TextMuted
        };

    public static void StyleButton(
        Button button,
        bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary
            ? Color.FromArgb(37, 99, 235)
            : SurfaceRaised;
        button.ForeColor = TextPrimary;
        button.Font = UiFont(8.75f, FontStyle.Bold);
        button.Padding = new Padding(8, 3, 8, 3);
        button.Margin = new Padding(4, 2, 4, 2);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary
            ? Accent
            : BorderStrong;
        button.FlatAppearance.MouseOverBackColor = primary
            ? Color.FromArgb(59, 130, 246)
            : SurfaceHover;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 64, 175);
    }

    public static void StyleTextBox(
        TextBoxBase textBox)
    {
        textBox.BackColor = Color.FromArgb(8, 19, 30);
        textBox.ForeColor = TextPrimary;
        textBox.BorderStyle = BorderStyle.FixedSingle;
    }

    public static void StyleComboBox(
        ComboBox comboBox)
    {
        comboBox.BackColor = Color.FromArgb(8, 19, 30);
        comboBox.ForeColor = TextPrimary;
        comboBox.FlatStyle = FlatStyle.Flat;
    }

    public static void StyleNumeric(
        NumericUpDown numeric)
    {
        numeric.BackColor = Color.FromArgb(8, 19, 30);
        numeric.ForeColor = TextPrimary;
        numeric.BorderStyle = BorderStyle.FixedSingle;
    }

    public static void StyleGrid(
        DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Divider;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 32;
        grid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBackground;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBackground;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.Font = UiFont(8.25f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = TextPrimary;
        grid.DefaultCellStyle.SelectionBackColor = SurfaceSelected;
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.DefaultCellStyle.Font = UiFont(8.75f);
        grid.DefaultCellStyle.Padding = new Padding(4, 1, 4, 1);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(15, 31, 46);
        grid.AlternatingRowsDefaultCellStyle.ForeColor = TextPrimary;
        grid.RowTemplate.Height = 29;
        grid.RowHeadersVisible = false;
    }

    public static void ApplyTree(
        Control root)
    {
        foreach (Control child in root.Controls)
        {
            switch (child)
            {
                case DashboardMetricCard:
                case AdeCardPanel:
                case WorkspaceNavigationRail:
                case SpectrumWaterfallControl:
                case ChannelStatusCard:
                    continue;

                case ModernTabControl:
                    break;

                case Button button:
                    StyleButton(
                        button,
                        string.Equals(
                            button.Tag as string,
                            "ade-primary",
                            StringComparison.Ordinal));
                    break;

                case TextBoxBase textBox:
                    StyleTextBox(textBox);
                    break;

                case ComboBox comboBox:
                    StyleComboBox(comboBox);
                    break;

                case NumericUpDown numeric:
                    StyleNumeric(numeric);
                    break;

                case DataGridView grid:
                    StyleGrid(grid);
                    break;

                case CheckBox checkBox:
                    checkBox.ForeColor = TextPrimary;
                    checkBox.BackColor = Color.Transparent;
                    checkBox.FlatStyle = FlatStyle.Flat;
                    break;

                case RadioButton radioButton:
                    radioButton.ForeColor = TextPrimary;
                    radioButton.BackColor = Color.Transparent;
                    radioButton.FlatStyle = FlatStyle.Flat;
                    break;

                case LinkLabel linkLabel:
                    linkLabel.LinkColor = AccentBright;
                    linkLabel.ActiveLinkColor = Color.White;
                    linkLabel.VisitedLinkColor = Purple;
                    break;

                case Label label:
                    if (label.ForeColor == SystemColors.ControlText ||
                        label.ForeColor == Color.Black)
                    {
                        label.ForeColor = TextPrimary;
                    }
                    break;

                case GroupBox groupBox:
                    groupBox.ForeColor = TextSecondary;
                    groupBox.BackColor = Surface;
                    break;

                case TabPage page:
                    page.BackColor = Surface;
                    page.ForeColor = TextPrimary;
                    break;

                case TableLayoutPanel table:
                    if (table.BackColor == SystemColors.Control ||
                        table.BackColor == Color.Empty)
                    {
                        table.BackColor = Surface;
                    }
                    break;

                case FlowLayoutPanel flow:
                    if (flow.BackColor == SystemColors.Control ||
                        flow.BackColor == Color.Empty)
                    {
                        flow.BackColor = Surface;
                    }
                    break;

                case Panel panel:
                    if (panel.BackColor == SystemColors.Control ||
                        panel.BackColor == Color.Empty)
                    {
                        panel.BackColor = Surface;
                    }
                    break;
            }

            ApplyTree(child);
        }
    }
}

internal sealed class AdeToolStripColorTable : ProfessionalColorTable
{
    public override Color ToolStripGradientBegin => AdeVisualTheme.HeaderBackground;
    public override Color ToolStripGradientMiddle => AdeVisualTheme.HeaderBackground;
    public override Color ToolStripGradientEnd => AdeVisualTheme.HeaderBackground;
    public override Color MenuStripGradientBegin => AdeVisualTheme.HeaderBackground;
    public override Color MenuStripGradientEnd => AdeVisualTheme.HeaderBackground;
    public override Color StatusStripGradientBegin => AdeVisualTheme.HeaderBackground;
    public override Color StatusStripGradientEnd => AdeVisualTheme.HeaderBackground;
    public override Color ImageMarginGradientBegin => AdeVisualTheme.Surface;
    public override Color ImageMarginGradientMiddle => AdeVisualTheme.Surface;
    public override Color ImageMarginGradientEnd => AdeVisualTheme.Surface;
    public override Color MenuItemSelected => AdeVisualTheme.SurfaceHover;
    public override Color MenuItemBorder => AdeVisualTheme.BorderStrong;
    public override Color MenuBorder => AdeVisualTheme.BorderStrong;
    public override Color MenuItemPressedGradientBegin => AdeVisualTheme.SurfaceRaised;
    public override Color MenuItemPressedGradientMiddle => AdeVisualTheme.SurfaceRaised;
    public override Color MenuItemPressedGradientEnd => AdeVisualTheme.SurfaceRaised;
    public override Color SeparatorDark => AdeVisualTheme.Divider;
    public override Color SeparatorLight => AdeVisualTheme.Divider;
    public override Color ButtonSelectedBorder => AdeVisualTheme.Accent;
    public override Color ButtonSelectedGradientBegin => AdeVisualTheme.SurfaceHover;
    public override Color ButtonSelectedGradientMiddle => AdeVisualTheme.SurfaceHover;
    public override Color ButtonSelectedGradientEnd => AdeVisualTheme.SurfaceHover;
}

internal sealed class AdeToolStripRenderer : ToolStripProfessionalRenderer
{
    public AdeToolStripRenderer()
        : base(new AdeToolStripColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(
        ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? AdeVisualTheme.TextPrimary
            : AdeVisualTheme.TextMuted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBorder(
        ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(AdeVisualTheme.Divider);
        e.Graphics.DrawLine(
            pen,
            e.AffectedBounds.Left,
            e.AffectedBounds.Bottom - 1,
            e.AffectedBounds.Right,
            e.AffectedBounds.Bottom - 1);
    }
}

internal sealed class AdeCardPanel : Panel
{
    public AdeCardPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = AdeVisualTheme.SurfaceRaised;
        Padding = new Padding(1);
        Margin = new Padding(6);
    }

    protected override void OnPaint(
        PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var background = new SolidBrush(AdeVisualTheme.SurfaceRaised);
        using var border = new Pen(AdeVisualTheme.Border);
        using var path = DashboardMetricCard.RoundedRectangle(bounds, 10);
        e.Graphics.FillPath(background, path);
        e.Graphics.DrawPath(border, path);
    }
}

internal sealed class DashboardMetricCard : Control
{
    private string _title;
    private string _value = "—";
    private string _detail = string.Empty;
    private AdeVisualState _state;

    public DashboardMetricCard(
        string title)
    {
        _title = title;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = AdeVisualTheme.Surface;
        ForeColor = AdeVisualTheme.TextPrimary;
        Size = new Size(166, 82);
        MinimumSize = new Size(142, 78);
        Margin = new Padding(5, 4, 5, 4);
        Cursor = Cursors.Default;
        AccessibleName = title;
    }

    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? string.Empty;
            AccessibleName = _title;
            Invalidate();
        }
    }

    public string ValueText
    {
        get => _value;
        set
        {
            _value = value ?? string.Empty;
            AccessibleDescription = _value;
            Invalidate();
        }
    }

    public string DetailText
    {
        get => _detail;
        set
        {
            _detail = value ?? string.Empty;
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

    public void Set(
        string value,
        string detail,
        AdeVisualState state = AdeVisualState.Neutral)
    {
        _value = value ?? string.Empty;
        _detail = detail ?? string.Empty;
        _state = state;
        AccessibleDescription = _value + " " + _detail;
        Invalidate();
    }

    protected override void OnPaint(
        PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var background = new SolidBrush(AdeVisualTheme.SurfaceRaised);
        using var border = new Pen(AdeVisualTheme.Border);
        using var accent = new SolidBrush(AdeVisualTheme.StateColor(_state));
        using var path = RoundedRectangle(bounds, 10);
        e.Graphics.FillPath(background, path);
        e.Graphics.DrawPath(border, path);

        var stateColor = AdeVisualTheme.StateColor(_state);
        using var stateBrush = new SolidBrush(stateColor);
        e.Graphics.FillEllipse(stateBrush, Width - 18, 11, 7, 7);

        var glyph = GlyphForTitle(_title);
        using var glyphFont = AdeVisualTheme.UiFont(15.5f, FontStyle.Bold);
        TextRenderer.DrawText(
            e.Graphics,
            glyph,
            glyphFont,
            new Rectangle(13, 11, 28, 26),
            stateColor,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);

        var titleRect = new Rectangle(47, 10, Width - 70, 18);
        var valueRect = new Rectangle(47, 31, Width - 60, 27);
        var detailRect = new Rectangle(14, Height - 22, Width - 28, 16);

        using var titleFont = AdeVisualTheme.UiFont(7.6f, FontStyle.Bold);
        using var valueFont = AdeVisualTheme.UiFont(12.4f, FontStyle.Bold);
        using var detailFont = AdeVisualTheme.UiFont(7.8f);

        TextRenderer.DrawText(
            e.Graphics,
            _title.ToUpperInvariant(),
            titleFont,
            titleRect,
            AdeVisualTheme.TextSecondary,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(
            e.Graphics,
            _value,
            valueFont,
            valueRect,
            AdeVisualTheme.TextPrimary,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(
            e.Graphics,
            _detail,
            detailFont,
            detailRect,
            stateColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private static string GlyphForTitle(
        string title) =>
        title.ToUpperInvariant() switch
        {
            "FREQUENCY" => "⌁",
            "SIGNAL QUALITY" => "▥",
            "ACTIVE AIRCRAFT" => "✈",
            "VERIFIED MESSAGES" => "✉",
            "IQ QUEUE" => "▤",
            "DATABASE" => "◉",
            "EXPORT" => "⇧",
            _ => "●"
        };

    internal static GraphicsPath RoundedRectangle(
        Rectangle bounds,
        int radius)
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

internal sealed class ModernTabControl : TabControl
{
    public ModernTabControl()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Normal;
        Padding = new Point(14, 6);
        BackColor = AdeVisualTheme.Surface;
        ForeColor = AdeVisualTheme.TextPrimary;
        Font = AdeVisualTheme.UiFont(8.75f, FontStyle.Bold);
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
    }

    protected override void OnDrawItem(
        DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= TabPages.Count)
            return;

        var selected = SelectedIndex == e.Index;
        var rect = GetTabRect(e.Index);
        var fill = selected
            ? AdeVisualTheme.SurfaceSelected
            : AdeVisualTheme.HeaderBackground;
        var text = selected
            ? AdeVisualTheme.TextPrimary
            : AdeVisualTheme.TextSecondary;

        using var brush = new SolidBrush(fill);
        e.Graphics.FillRectangle(brush, rect);

        if (selected)
        {
            using var accent = new SolidBrush(AdeVisualTheme.Accent);
            e.Graphics.FillRectangle(accent, rect.Left, rect.Bottom - 3, rect.Width, 3);
        }

        TextRenderer.DrawText(
            e.Graphics,
            TabPages[e.Index].Text,
            Font,
            rect,
            text,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    protected override void OnPaintBackground(
        PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(AdeVisualTheme.Surface);
    }
}

internal sealed class NavigationRailButton : Button
{
    private bool _selected;
    private string _glyph = "•";
    private string _caption = string.Empty;

    public NavigationRailButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = AdeVisualTheme.NavigationBackground;
        ForeColor = AdeVisualTheme.TextSecondary;
        Text = string.Empty;
        Cursor = Cursors.Hand;
        TabStop = true;
        DoubleBuffered = true;
    }

    public string Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value ?? string.Empty;
            Invalidate();
        }
    }

    public string Caption
    {
        get => _caption;
        set
        {
            _caption = value ?? string.Empty;
            AccessibleName = _caption;
            Invalidate();
        }
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Invalidate();
        }
    }

    protected override void OnPaint(
        PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = _selected
            ? AdeVisualTheme.SurfaceSelected
            : ClientRectangle.Contains(PointToClient(Cursor.Position))
                ? AdeVisualTheme.SurfaceHover
                : AdeVisualTheme.NavigationBackground;

        using var fillBrush = new SolidBrush(fill);
        pevent.Graphics.FillRectangle(fillBrush, ClientRectangle);

        if (_selected)
        {
            using var accent = new SolidBrush(AdeVisualTheme.Accent);
            pevent.Graphics.FillRectangle(accent, 0, 5, 4, Height - 10);
        }

        var glyphColor = _selected
            ? AdeVisualTheme.AccentBright
            : AdeVisualTheme.TextSecondary;
        var textColor = _selected
            ? AdeVisualTheme.TextPrimary
            : AdeVisualTheme.TextSecondary;

        using var glyphFont = AdeVisualTheme.UiFont(13.5f, FontStyle.Bold);
        using var captionFont = AdeVisualTheme.UiFont(9.0f, _selected ? FontStyle.Bold : FontStyle.Regular);

        TextRenderer.DrawText(
            pevent.Graphics,
            _glyph,
            glyphFont,
            new Rectangle(13, 0, 30, Height),
            glyphColor,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);

        TextRenderer.DrawText(
            pevent.Graphics,
            _caption,
            captionFont,
            new Rectangle(50, 0, Width - 58, Height),
            textColor,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(
        EventArgs e)
    {
        base.OnMouseEnter(e);
        Invalidate();
    }

    protected override void OnMouseLeave(
        EventArgs e)
    {
        base.OnMouseLeave(e);
        Invalidate();
    }
}

internal sealed class WorkspaceNavigationRail : UserControl
{
    private readonly FlowLayoutPanel _items;
    private readonly Dictionary<string, NavigationRailButton> _buttons = new(StringComparer.Ordinal);
    private string _selectedKey = string.Empty;

    public WorkspaceNavigationRail()
    {
        Width = 200;
        Dock = DockStyle.Left;
        BackColor = AdeVisualTheme.NavigationBackground;
        Padding = new Padding(8, 14, 8, 10);

        _items = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        Controls.Add(_items);
    }

    public Button AddItem(
        string key,
        string text,
        EventHandler clicked)
    {
        var button = new NavigationRailButton
        {
            Glyph = GlyphForKey(key),
            Caption = text,
            Width = 182,
            Height = 42,
            Margin = new Padding(1, 1, 1, 1)
        };

        button.Click += clicked;
        button.Click += (_, _) => SelectItem(key);

        _buttons[key] = button;
        _items.Controls.Add(button);
        return button;
    }

    public void SelectItem(
        string key)
    {
        _selectedKey = key ?? string.Empty;

        foreach (var pair in _buttons)
        {
            pair.Value.Selected = string.Equals(
                pair.Key,
                _selectedKey,
                StringComparison.Ordinal);
        }
    }

    private static string GlyphForKey(
        string key) =>
        key switch
        {
            "overview" => "▦",
            "aircraft" => "✈",
            "messages" => "▣",
            "waterfall" => "▥",
            "history" => "◴",
            "diagnostics" => "⌁",
            "settings" => "⚙",
            "export" => "⇧",
            "about" => "ⓘ",
            _ => "•"
        };
}
