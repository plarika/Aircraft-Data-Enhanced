// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal sealed record OperationsBoardStatus(
    DateTimeOffset LocalNow,
    DateTimeOffset UtcNow,
    string RfState,
    string RfDetail,
    string DecoderState,
    string DecoderDetail,
    string AircraftState,
    string AircraftDetail,
    string DatabaseState,
    string DatabaseDetail,
    string PipelineState,
    string PipelineDetail);

internal sealed class AirOperationsBoardControl : UserControl
{
    private sealed record WindowOption(
        string Label,
        TimeSpan Window)
    {
        public override string ToString() =>
            Label;
    }

    private sealed class StatusTile : Panel
    {
        private readonly Label _title;
        private readonly Label _value;
        private readonly Label _detail;

        public StatusTile(
            string title)
        {
            Dock =
                DockStyle.Fill;

            Margin =
                new Padding(
                    4);

            Padding =
                new Padding(
                    10,
                    7,
                    10,
                    7);

            BackColor =
                AdeVisualTheme.Surface;

            BorderStyle =
                BorderStyle.FixedSingle;

            _title =
                new Label
                {
                    Text =
                        title.ToUpperInvariant(),
                    AutoSize =
                        true,
                    ForeColor =
                        AdeVisualTheme.TextSecondary,
                    Font =
                        new Font(
                            "Segoe UI",
                            8.0f,
                            FontStyle.Bold)
                };

            _value =
                new Label
                {
                    Text =
                        "WAITING",
                    AutoSize =
                        true,
                    ForeColor =
                        Color.White,
                    Font =
                        new Font(
                            "Consolas",
                            12.0f,
                            FontStyle.Bold),
                    Top =
                        24
                };

            _detail =
                new Label
                {
                    Text =
                        "No data",
                    AutoEllipsis =
                        true,
                    ForeColor =
                        AdeVisualTheme.TextSecondary,
                    Left =
                        1,
                    Top =
                        48,
                    Width =
                        220,
                    Height =
                        20
                };

            Controls.Add(
                _title);

            Controls.Add(
                _value);

            Controls.Add(
                _detail);

            Resize +=
                (_, _) =>
                    _detail.Width =
                        Math.Max(
                            40,
                            ClientSize.Width -
                            20);
        }

        public void UpdateValue(
            string value,
            string detail,
            bool healthy)
        {
            _value.Text =
                value;

            _detail.Text =
                detail;

            _value.ForeColor =
                healthy
                    ? AdeVisualTheme.Success
                    : AdeVisualTheme.Warning;
        }
    }

    private IReadOnlyList<AircraftSessionSnapshot>
        _currentSessions =
            Array.Empty<AircraftSessionSnapshot>();

    private bool _refreshing;
    private string _lastPublishedIcao =
        string.Empty;

    private readonly TextBox _filter =
        new()
        {
            PlaceholderText =
                "ICAO, matrícula, voo, estação…",
            Width =
                240,
            BackColor =
                AdeVisualTheme.SurfaceRaised,
            ForeColor =
                AdeVisualTheme.TextPrimary,
            BorderStyle =
                BorderStyle.FixedSingle
        };

    private readonly ComboBox _window =
        new()
        {
            DropDownStyle =
                ComboBoxStyle.DropDownList,
            Width =
                145
        };

    private readonly Label _localClock =
        new()
        {
            AutoSize =
                true,
            ForeColor =
                Color.White,
            Font =
                new Font(
                    "Consolas",
                    13.0f,
                    FontStyle.Bold)
        };

    private readonly Label _utcClock =
        new()
        {
            AutoSize =
                true,
            ForeColor =
                AdeVisualTheme.TextSecondary,
            Font =
                new Font(
                    "Consolas",
                    9.0f,
                    FontStyle.Regular),
            Padding =
                new Padding(
                    0,
                    3,
                    0,
                    0)
        };

    private readonly Label _summary =
        new()
        {
            AutoSize =
                true,
            ForeColor =
                AdeVisualTheme.TextSecondary,
            Padding =
                new Padding(
                    10,
                    7,
                    0,
                    0)
        };

    private readonly StatusTile _rfTile =
        new(
            "RF link");

    private readonly StatusTile _decoderTile =
        new(
            "Decoder");

    private readonly StatusTile _aircraftTile =
        new(
            "Aircraft");

    private readonly StatusTile _databaseTile =
        new(
            "Database");

    private readonly StatusTile _pipelineTile =
        new(
            "Pipeline");

    private readonly DataGridView _board =
        CreateBoardGrid();

    public AirOperationsBoardControl()
    {
        Dock =
            DockStyle.Fill;

        BackColor =
            AdeVisualTheme.AppBackground;

        ForeColor =
            AdeVisualTheme.TextPrimary;

        BuildInterface();
        ConfigureColumns();

        _window.Items.AddRange(
        [
            new WindowOption(
                "Active: 5 min",
                TimeSpan.FromMinutes(5)),
            new WindowOption(
                "Active: 15 min",
                TimeSpan.FromMinutes(15)),
            new WindowOption(
                "Active: 30 min",
                TimeSpan.FromMinutes(30)),
            new WindowOption(
                "Active: 60 min",
                TimeSpan.FromHours(1)),
            new WindowOption(
                "Retained: 24 h",
                TimeSpan.MaxValue)
        ]);

        _window.SelectedIndex =
            1;

        _filter.TextChanged +=
            (_, _) =>
                FilterChanged?.Invoke(
                    this,
                    EventArgs.Empty);

        _window.SelectedIndexChanged +=
            (_, _) =>
                FilterChanged?.Invoke(
                    this,
                    EventArgs.Empty);

        _board.SelectionChanged +=
            (_, _) =>
            {
                if (!_refreshing)
                {
                    PublishSelectedSession();
                }
            };

        _board.CellDoubleClick +=
            (_, e) =>
            {
                if (e.RowIndex >=
                    0)
                {
                    PublishSelectedSession(
                        force: true);
                }
            };

        _board.CellContentClick +=
            (_, e) =>
            {
                if (e.RowIndex <
                        0 ||
                    e.ColumnIndex <
                        0)
                {
                    return;
                }

                if (_board.Columns[
                        e.ColumnIndex].Name !=
                    "ICAO")
                {
                    return;
                }

                _board.Rows[
                    e.RowIndex].Selected =
                    true;

                OpenSelectedAircraft();
            };
    }

    public event EventHandler?
        FilterChanged;

    public event EventHandler?
        SelectionCleared;

    public event Action<AircraftSessionSnapshot>?
        SessionSelected;

    public string FilterText =>
        _filter.Text.Trim();

    public TimeSpan ActiveWindow =>
        _window.SelectedItem is
            WindowOption option
                ? option.Window
                : TimeSpan.FromMinutes(
                    15);

    public int SelectedWindowIndex
    {
        get =>
            _window.SelectedIndex;

        set =>
            _window.SelectedIndex =
                Math.Clamp(
                    value,
                    0,
                    Math.Max(
                        0,
                        _window.Items.Count -
                        1));
    }

    public void UpdateStatus(
        OperationsBoardStatus status)
    {
        _localClock.Text =
            status.LocalNow
                .ToString(
                    "yyyy-MM-dd  HH:mm:ss");

        _utcClock.Text =
            status.UtcNow
                .ToString(
                    "'UTC'  HH:mm:ss");

        _rfTile.UpdateValue(
            status.RfState,
            status.RfDetail,
            string.Equals(
                status.RfState,
                "ONLINE",
                StringComparison.OrdinalIgnoreCase));

        _decoderTile.UpdateValue(
            status.DecoderState,
            status.DecoderDetail,
            !string.Equals(
                status.DecoderState,
                "NO DATA",
                StringComparison.OrdinalIgnoreCase));

        _aircraftTile.UpdateValue(
            status.AircraftState,
            status.AircraftDetail,
            true);

        _databaseTile.UpdateValue(
            status.DatabaseState,
            status.DatabaseDetail,
            string.Equals(
                status.DatabaseState,
                "READY",
                StringComparison.OrdinalIgnoreCase));

        _pipelineTile.UpdateValue(
            status.PipelineState,
            status.PipelineDetail,
            !string.Equals(
                status.PipelineState,
                "ERROR",
                StringComparison.OrdinalIgnoreCase));
    }

    public void RefreshSessions(
        IReadOnlyList<AircraftSessionSnapshot> sessions)
    {
        ArgumentNullException.ThrowIfNull(
            sessions);

        _currentSessions =
            sessions;

        var selectedIcao =
            SelectedSession()?.Icao ??
            string.Empty;

        _refreshing =
            true;

        _board.SuspendLayout();

        try
        {
            _board.Rows.Clear();

            var now =
                DateTimeOffset.Now;

            foreach (var session in
                     sessions)
            {
                var age =
                    session.Age(
                        now);

                var state =
                    SessionState(
                        age);

                var flight =
                    string.IsNullOrWhiteSpace(
                        session.Callsign)
                        ? "—"
                        : session.Callsign;

                var registration =
                    string.IsNullOrWhiteSpace(
                        session.Registration)
                        ? "—"
                        : session.Registration;

                var station =
                    string.IsNullOrWhiteSpace(
                        session.LastGroundStation)
                        ? "—"
                        : session.LastGroundStation;

                var frequency =
                    session.LastFrequencyMhz?
                        .ToString(
                            "0.000") ??
                    "—";

                var index =
                    _board.Rows.Add(
                        state,
                        flight,
                        registration,
                        session.Icao,
                        session.LastDirection,
                        station,
                        frequency,
                        session.MessageCount,
                        session.LastSeen
                            .ToLocalTime()
                            .ToString(
                                "HH:mm:ss"),
                        FormatAge(
                            age),
                        session.LastLabel,
                        session.LastText);

                var row =
                    _board.Rows[
                        index];

                row.Tag =
                    session;

                ApplyStateStyle(
                    row,
                    state);

                row.Cells[
                    "Last text"].ToolTipText =
                    session.LastText;

                if (selectedIcao.Length >
                        0 &&
                    string.Equals(
                        selectedIcao,
                        session.Icao,
                        StringComparison.OrdinalIgnoreCase))
                {
                    row.Selected =
                        true;
                }
            }

            if (_board.SelectedRows.Count ==
                    0 &&
                _board.Rows.Count >
                    0)
            {
                _board.Rows[
                    0].Selected =
                    true;
            }
        }
        finally
        {
            _board.ResumeLayout();

            _refreshing =
                false;
        }

        _summary.Text =
            $"{sessions.Count} aircraft on board · " +
            $"{sessions.Sum(session => session.MessageCount)} messages in active sessions";

        PublishSelectedSession();
    }

    public void PublishCurrentSelection()
    {
        PublishSelectedSession(
            force: true);
    }

    private void BuildInterface()
    {
        var root =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,
                ColumnCount =
                    1,
                RowCount =
                    4,
                BackColor =
                    BackColor,
                Padding =
                    Padding.Empty,
                Margin =
                    Padding.Empty
            };

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));

        root.RowStyles.Add(
            new RowStyle(
                SizeType.Absolute,
                86));

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));

        root.RowStyles.Add(
            new RowStyle(
                SizeType.Percent,
                100));

        var titleBar =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Top,
                AutoSize =
                    true,
                ColumnCount =
                    2,
                BackColor =
                    Color.FromArgb(
                        18,
                        24,
                        31),
                Padding =
                    new Padding(
                        14,
                        9,
                        14,
                        9),
                Margin =
                    Padding.Empty
            };

        titleBar.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100));

        titleBar.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize));

        var identity =
            new FlowLayoutPanel
            {
                AutoSize =
                    true,
                WrapContents =
                    false,
                BackColor =
                    Color.Transparent,
                Margin =
                    Padding.Empty
            };

        identity.Controls.Add(
            new Label
            {
                Text =
                    "ADE",
                AutoSize =
                    true,
                ForeColor =
                    Color.FromArgb(
                        91,
                        213,
                        255),
                Font =
                    new Font(
                        "Consolas",
                        17.0f,
                        FontStyle.Bold),
                Padding =
                    new Padding(
                        0,
                        1,
                        12,
                        0)
            });

        identity.Controls.Add(
            new Label
            {
                Text =
                    "AIR OPERATIONS TERMINAL",
                AutoSize =
                    true,
                ForeColor =
                    Color.White,
                Font =
                    new Font(
                        "Segoe UI",
                        15.0f,
                        FontStyle.Bold)
            });

        identity.Controls.Add(
            new Label
            {
                Text =
                    "PUBLIC BETA",
                AutoSize =
                    true,
                BackColor =
                    Color.FromArgb(
                        44,
                        89,
                        112),
                ForeColor =
                    Color.White,
                Font =
                    new Font(
                        "Segoe UI",
                        8.0f,
                        FontStyle.Bold),
                Padding =
                    new Padding(
                        7,
                        4,
                        7,
                        4),
                Margin =
                    new Padding(
                        12,
                        2,
                        0,
                        0)
            });

        var clocks =
            new FlowLayoutPanel
            {
                AutoSize =
                    true,
                FlowDirection =
                    FlowDirection.TopDown,
                WrapContents =
                    false,
                BackColor =
                    Color.Transparent,
                Margin =
                    Padding.Empty
            };

        clocks.Controls.Add(
            _localClock);

        clocks.Controls.Add(
            _utcClock);

        titleBar.Controls.Add(
            identity,
            0,
            0);

        titleBar.Controls.Add(
            clocks,
            1,
            0);

        var statusGrid =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,
                ColumnCount =
                    5,
                BackColor =
                    AdeVisualTheme.AppBackground,
                Padding =
                    new Padding(
                        6,
                        3,
                        6,
                        3),
                Margin =
                    Padding.Empty
            };

        for (var index = 0;
             index < 5;
             index++)
        {
            statusGrid.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Percent,
                    20));
        }

        statusGrid.Controls.Add(
            _rfTile,
            0,
            0);

        statusGrid.Controls.Add(
            _decoderTile,
            1,
            0);

        statusGrid.Controls.Add(
            _aircraftTile,
            2,
            0);

        statusGrid.Controls.Add(
            _databaseTile,
            3,
            0);

        statusGrid.Controls.Add(
            _pipelineTile,
            4,
            0);

        var filterBar =
            new FlowLayoutPanel
            {
                Dock =
                    DockStyle.Top,
                AutoSize =
                    true,
                WrapContents =
                    true,
                BackColor =
                    Color.FromArgb(
                        22,
                        28,
                        35),
                Padding =
                    new Padding(
                        10,
                        6,
                        10,
                        6),
                Margin =
                    Padding.Empty
            };

        filterBar.Controls.Add(
            new Label
            {
                Text =
                    "BOARD WINDOW",
                AutoSize =
                    true,
                ForeColor =
                    AdeVisualTheme.TextSecondary,
                Font =
                    new Font(
                        "Segoe UI",
                        8.0f,
                        FontStyle.Bold),
                Padding =
                    new Padding(
                        0,
                        7,
                        6,
                        0)
            });

        filterBar.Controls.Add(
            _window);

        filterBar.Controls.Add(
            _filter);

        filterBar.Controls.Add(
            _summary);

        root.Controls.Add(
            titleBar,
            0,
            0);

        root.Controls.Add(
            statusGrid,
            0,
            1);

        root.Controls.Add(
            filterBar,
            0,
            2);

        root.Controls.Add(
            _board,
            0,
            3);

        Controls.Add(
            root);
    }

    private void ConfigureColumns()
    {
        AddTextColumn(
            "State",
            76);

        AddTextColumn(
            "Flight",
            92);

        AddTextColumn(
            "Registration",
            100);

        _board.Columns.Add(
            new DataGridViewLinkColumn
            {
                Name =
                    "ICAO",
                HeaderText =
                    "ICAO24",
                Width =
                    78,
                LinkColor =
                    Color.FromArgb(
                        91,
                        213,
                        255),
                ActiveLinkColor =
                    Color.White,
                VisitedLinkColor =
                    Color.FromArgb(
                        91,
                        213,
                        255),
                TrackVisitedState =
                    false,
                ToolTipText =
                    "Open aircraft identity"
            });

        AddTextColumn(
            "Direction",
            104);

        AddTextColumn(
            "Ground station",
            104);

        AddTextColumn(
            "Freq MHz",
            76);

        AddTextColumn(
            "Messages",
            72);

        AddTextColumn(
            "Last",
            74);

        AddTextColumn(
            "Age",
            68);

        AddTextColumn(
            "Label",
            54);

        AddTextColumn(
            "Last text",
            360,
            DataGridViewAutoSizeColumnMode.Fill);
    }

    private void AddTextColumn(
        string name,
        int width,
        DataGridViewAutoSizeColumnMode autoSize =
            DataGridViewAutoSizeColumnMode.None)
    {
        _board.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name =
                    name,
                HeaderText =
                    name,
                Width =
                    width,
                AutoSizeMode =
                    autoSize,
                SortMode =
                    DataGridViewColumnSortMode.Automatic
            });
    }

    private static DataGridView CreateBoardGrid()
    {
        var grid =
            new DataGridView
            {
                Dock =
                    DockStyle.Fill,
                ReadOnly =
                    true,
                AllowUserToAddRows =
                    false,
                AllowUserToDeleteRows =
                    false,
                AllowUserToResizeRows =
                    false,
                AutoGenerateColumns =
                    false,
                SelectionMode =
                    DataGridViewSelectionMode.FullRowSelect,
                MultiSelect =
                    false,
                RowHeadersVisible =
                    false,
                BackgroundColor =
                    AdeVisualTheme.AppBackground,
                BorderStyle =
                    BorderStyle.None,
                CellBorderStyle =
                    DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor =
                    AdeVisualTheme.Divider,
                EnableHeadersVisualStyles =
                    false,
                Font =
                    new Font(
                        "Consolas",
                        10.0f,
                        FontStyle.Regular)
            };

        grid.ColumnHeadersDefaultCellStyle.BackColor =
            AdeVisualTheme.HeaderBackground;

        grid.ColumnHeadersDefaultCellStyle.ForeColor =
            Color.White;

        grid.ColumnHeadersDefaultCellStyle.Font =
            new Font(
                "Segoe UI",
                9.0f,
                FontStyle.Bold);

        grid.ColumnHeadersHeight =
            34;

        grid.DefaultCellStyle.BackColor =
            AdeVisualTheme.Surface;

        grid.DefaultCellStyle.ForeColor =
            AdeVisualTheme.TextPrimary;

        grid.DefaultCellStyle.SelectionBackColor =
            AdeVisualTheme.SurfaceSelected;

        grid.DefaultCellStyle.SelectionForeColor =
            Color.White;

        grid.AlternatingRowsDefaultCellStyle.BackColor =
            AdeVisualTheme.SurfaceRaised;

        grid.RowTemplate.Height =
            31;

        return grid;
    }

    private AircraftSessionSnapshot?
        SelectedSession()
    {
        return _board.SelectedRows.Count >
                0
            ? _board.SelectedRows[
                0].Tag as
                AircraftSessionSnapshot
            : null;
    }

    private void PublishSelectedSession(
        bool force = false)
    {
        var session =
            SelectedSession();

        if (session is null)
        {
            _lastPublishedIcao =
                string.Empty;

            SelectionCleared?.Invoke(
                this,
                EventArgs.Empty);

            return;
        }

        if (!force &&
            string.Equals(
                _lastPublishedIcao,
                session.Icao,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastPublishedIcao =
            session.Icao;

        SessionSelected?.Invoke(
            session);
    }

    private void OpenSelectedAircraft()
    {
        var session =
            SelectedSession();

        if (session is null)
            return;

        try
        {
            AircraftOnlineLookup.Open(
                AircraftOnlineProvider.Planespotters,
                session.Icao,
                session.Registration,
                session.Callsign);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Aircraft details",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string SessionState(
        TimeSpan age)
    {
        if (age <
            TimeSpan.FromMinutes(
                2))
        {
            return
                "LIVE";
        }

        if (age <
            TimeSpan.FromMinutes(
                10))
        {
            return
                "ACTIVE";
        }

        return
            "RECENT";
    }

    private static string FormatAge(
        TimeSpan age)
    {
        if (age <
            TimeSpan.FromMinutes(
                1))
        {
            return
                $"{Math.Max(0, (int)age.TotalSeconds)} s";
        }

        if (age <
            TimeSpan.FromHours(
                1))
        {
            return
                $"{(int)age.TotalMinutes} min";
        }

        return
            $"{(int)age.TotalHours} h " +
            $"{age.Minutes} min";
    }

    private static void ApplyStateStyle(
        DataGridViewRow row,
        string state)
    {
        row.Cells[
            "State"].Style.Font =
            new Font(
                "Consolas",
                9.0f,
                FontStyle.Bold);

        row.Cells[
            "State"].Style.ForeColor =
            state switch
            {
                "LIVE" =>
                    AdeVisualTheme.Success,

                "ACTIVE" =>
                    Color.FromArgb(
                        91,
                        213,
                        255),

                _ =>
                    AdeVisualTheme.Warning
            };
    }
}
