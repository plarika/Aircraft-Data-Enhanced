// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal sealed class ActiveAircraftSessionsControl : UserControl
{
    private sealed record SessionWindowOption(
        string Label,
        TimeSpan Window)
    {
        public override string ToString() =>
            Label;
    }

    private readonly AircraftMetadataService
        _metadataService =
            new();

    private readonly CancellationTokenSource
        _disposeCancellation =
            new();

    private readonly Dictionary<
        string,
        AircraftMetadata> _metadata =
            new(
                StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string>
        _metadataPending =
            new(
                StringComparer.OrdinalIgnoreCase);

    private readonly object _metadataGate =
        new();

    private IReadOnlyList<AircraftSessionSnapshot>
        _currentSessions =
            Array.Empty<AircraftSessionSnapshot>();

    private bool _refreshing;
    private string _lastPublishedSessionKey =
        string.Empty;
    private string _lastPublishedMessageKey =
        string.Empty;

    private readonly TextBox _filter =
        new()
        {
            PlaceholderText =
                "ICAO, matrícula, voo, operador…",
            Width =
                220
        };

    private readonly ComboBox _window =
        new()
        {
            DropDownStyle =
                ComboBoxStyle.DropDownList,
            Width =
                128
        };

    private readonly Label _summary =
        new()
        {
            Text =
                "No active aircraft.",
            AutoSize =
                true,
            ForeColor =
                Color.LightSteelBlue,
            Padding =
                new Padding(
                    8,
                    7,
                    0,
                    0)
        };

    private readonly DataGridView _sessionsGrid =
        CreateGrid();

    private readonly DataGridView _historyGrid =
        CreateGrid();

    public ActiveAircraftSessionsControl()
    {
        Dock =
            DockStyle.Fill;

        BackColor =
            Color.FromArgb(
                20,
                24,
                30);

        ForeColor =
            Color.Gainsboro;

        BuildInterface();
        ConfigureSessionColumns();
        ConfigureHistoryColumns();

        _window.Items.AddRange(
        [
            new SessionWindowOption(
                "Active: 5 min",
                TimeSpan.FromMinutes(5)),
            new SessionWindowOption(
                "Active: 15 min",
                TimeSpan.FromMinutes(15)),
            new SessionWindowOption(
                "Active: 30 min",
                TimeSpan.FromMinutes(30)),
            new SessionWindowOption(
                "Active: 60 min",
                TimeSpan.FromHours(1)),
            new SessionWindowOption(
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

        _sessionsGrid.SelectionChanged +=
            (_, _) =>
            {
                if (!_refreshing)
                {
                    PublishSelectedSession();
                }
            };

        _sessionsGrid.CellContentClick +=
            (_, e) =>
            {
                if (e.RowIndex >= 0 &&
                    e.ColumnIndex >= 0 &&
                    _sessionsGrid.Columns[
                        e.ColumnIndex].Name ==
                    "ICAO")
                {
                    _sessionsGrid.Rows[
                        e.RowIndex].Selected =
                        true;

                    OpenSelectedAircraftDetails();
                }
            };

        _sessionsGrid.CellDoubleClick +=
            (_, e) =>
            {
                if (e.RowIndex >= 0)
                {
                    PublishSelectedSession(
                        force: true);
                }
            };

        _historyGrid.SelectionChanged +=
            (_, _) =>
            {
                if (!_refreshing)
                {
                    PublishSelectedHistoryMessage();
                }
            };

        _historyGrid.CellDoubleClick +=
            (_, e) =>
            {
                if (e.RowIndex >= 0)
                {
                    PublishSelectedHistoryMessage(
                        force: true);
                }
            };
    }

    public event EventHandler?
        FilterChanged;

    public event EventHandler?
        SelectionCleared;

    public event Action<AircraftSessionSnapshot>?
        SessionSelected;

    public event Action<Vdl2Message>?
        MessageSelected;

    public string FilterText =>
        _filter.Text.Trim();

    public TimeSpan ActiveWindow =>
        _window.SelectedItem is
            SessionWindowOption option
                ? option.Window
                : TimeSpan.FromMinutes(15);

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

        _sessionsGrid.SuspendLayout();

        try
        {
            _sessionsGrid.Rows.Clear();

            var now =
                DateTimeOffset.Now;

            foreach (var session in sessions)
            {
                AircraftMetadata? metadata;

                lock (_metadataGate)
                {
                    _metadata.TryGetValue(
                        session.Icao,
                        out metadata);
                }

                var age =
                    session.Age(
                        now);

                var state =
                    SessionState(
                        age);

                var registration =
                    FirstNonEmpty(
                        metadata?.Registration,
                        session.Registration);

                var aircraft =
                    metadata?.Found ==
                    true
                        ? CombineAircraft(
                            metadata)
                        : string.Empty;

                var operatorName =
                    metadata?.Found ==
                    true
                        ? CombineOperator(
                            metadata)
                        : string.Empty;

                var index =
                    _sessionsGrid.Rows.Add(
                        state,
                        session.Icao,
                        registration,
                        session.Callsign,
                        aircraft,
                        operatorName,
                        session.MessageCount,
                        session.FirstSeen
                            .ToLocalTime()
                            .ToString(
                                "HH:mm:ss"),
                        session.LastSeen
                            .ToLocalTime()
                            .ToString(
                                "HH:mm:ss"),
                        FormatAge(
                            age),
                        session.LastLabel,
                        session.LastFrequencyMhz?
                            .ToString(
                                "0.000") ??
                        string.Empty);

                var row =
                    _sessionsGrid.Rows[index];

                row.Tag =
                    session;

                row.Cells["Last"].ToolTipText =
                    session.LastText;

                ApplyStateStyle(
                    row,
                    state);

                if (selectedIcao.Length > 0 &&
                    string.Equals(
                        selectedIcao,
                        session.Icao,
                        StringComparison.OrdinalIgnoreCase))
                {
                    row.Selected =
                        true;
                }
            }

            if (_sessionsGrid.SelectedRows.Count ==
                    0 &&
                _sessionsGrid.Rows.Count >
                    0)
            {
                _sessionsGrid.Rows[0].Selected =
                    true;
            }
        }
        finally
        {
            _sessionsGrid.ResumeLayout();
            _refreshing =
                false;
        }

        UpdateSummary();
        PublishSelectedSession();
        ScheduleMetadataLookups(
            sessions);
    }

    public void PublishCurrentSelection()
    {
        PublishSelectedSession(
            force: true);
    }

    protected override void Dispose(
        bool disposing)
    {
        if (disposing)
        {
            _disposeCancellation.Cancel();
            _disposeCancellation.Dispose();
            _metadataService.Dispose();
        }

        base.Dispose(
            disposing);
    }

    private void BuildInterface()
    {
        var toolbar =
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
                        27,
                        32,
                        39),
                Padding =
                    new Padding(
                        8,
                        5,
                        8,
                        5),
                Margin =
                    Padding.Empty
            };

        toolbar.Controls.Add(
            new Label
            {
                Text =
                    "ACTIVE AIRCRAFT",
                AutoSize =
                    true,
                ForeColor =
                    Color.WhiteSmoke,
                Font =
                    new Font(
                        Font,
                        FontStyle.Bold),
                Padding =
                    new Padding(
                        0,
                        7,
                        12,
                        0)
            });

        toolbar.Controls.Add(
            _window);

        toolbar.Controls.Add(
            _filter);

        toolbar.Controls.Add(
            _summary);

        var split =
            new SplitContainer
            {
                Dock =
                    DockStyle.Fill,
                Orientation =
                    Orientation.Horizontal,
                SplitterWidth =
                    5,
                BackColor =
                    Color.FromArgb(
                        42,
                        48,
                        57)
            };

        split.Panel1.Controls.Add(
            _sessionsGrid);

        var historyHost =
            new Panel
            {
                Dock =
                    DockStyle.Fill,
                BackColor =
                    BackColor
            };

        var historyTitle =
            new Label
            {
                Text =
                    "SESSION MESSAGE HISTORY",
                Dock =
                    DockStyle.Top,
                Height =
                    26,
                ForeColor =
                    Color.Silver,
                BackColor =
                    Color.FromArgb(
                        27,
                        32,
                        39),
                Padding =
                    new Padding(
                        8,
                        5,
                        0,
                        0)
            };

        historyHost.Controls.Add(
            _historyGrid);

        historyHost.Controls.Add(
            historyTitle);

        split.Panel2.Controls.Add(
            historyHost);

        split.HandleCreated +=
            (_, _) =>
            {
                BeginInvoke(
                    (MethodInvoker)(() =>
                    {
                        if (split.Height < 100)
                            return;

                        var available =
                            split.Height -
                            split.SplitterWidth;

                        split.Panel1MinSize =
                            40;

                        split.Panel2MinSize =
                            40;

                        split.SplitterDistance =
                            Math.Clamp(
                                (int)(
                                    available *
                                    0.64),
                                40,
                                Math.Max(
                                    40,
                                    available -
                                    40));
                    }));
            };

        Controls.Add(
            split);

        Controls.Add(
            toolbar);
    }

    private void ConfigureSessionColumns()
    {
        AddTextColumn(
            _sessionsGrid,
            "State",
            62);

        AddLinkColumn(
            _sessionsGrid,
            "ICAO",
            70);

        AddTextColumn(
            _sessionsGrid,
            "Registration",
            92);

        AddTextColumn(
            _sessionsGrid,
            "Flight",
            82);

        AddTextColumn(
            _sessionsGrid,
            "Aircraft",
            160);

        AddTextColumn(
            _sessionsGrid,
            "Operator",
            150);

        AddTextColumn(
            _sessionsGrid,
            "Messages",
            66);

        AddTextColumn(
            _sessionsGrid,
            "First",
            72);

        AddTextColumn(
            _sessionsGrid,
            "Last",
            72);

        AddTextColumn(
            _sessionsGrid,
            "Age",
            72);

        AddTextColumn(
            _sessionsGrid,
            "Label",
            56);

        AddTextColumn(
            _sessionsGrid,
            "Freq MHz",
            76);
    }

    private void ConfigureHistoryColumns()
    {
        AddTextColumn(
            _historyGrid,
            "Time",
            74);

        AddTextColumn(
            _historyGrid,
            "Direction",
            104);

        AddTextColumn(
            _historyGrid,
            "Flight",
            82);

        AddTextColumn(
            _historyGrid,
            "Label",
            56);

        AddTextColumn(
            _historyGrid,
            "Message",
            68);

        AddTextColumn(
            _historyGrid,
            "Freq MHz",
            76);

        AddTextColumn(
            _historyGrid,
            "Text",
            300,
            DataGridViewAutoSizeColumnMode.Fill);
    }

    private AircraftSessionSnapshot?
        SelectedSession()
    {
        if (_sessionsGrid.SelectedRows.Count ==
                0)
        {
            return null;
        }

        return
            _sessionsGrid.SelectedRows[0].Tag
            as AircraftSessionSnapshot;
    }

    private void OpenSelectedAircraftDetails()
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

    private void PublishSelectedSession(
        bool force = false)
    {
        var session =
            SelectedSession();

        if (session is null)
        {
            _historyGrid.Rows.Clear();
            _summary.Text =
                "No active aircraft.";

            if (force ||
                _lastPublishedSessionKey.Length >
                    0)
            {
                _lastPublishedSessionKey =
                    string.Empty;

                _lastPublishedMessageKey =
                    string.Empty;

                SelectionCleared?.Invoke(
                    this,
                    EventArgs.Empty);
            }

            return;
        }

        RefreshHistory(
            session);

        var key =
            session.Icao +
            "|" +
            session.LatestMessage.DedupKey +
            "|" +
            session.MessageCount;

        if (!force &&
            string.Equals(
                key,
                _lastPublishedSessionKey,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastPublishedSessionKey =
            key;

        SessionSelected?.Invoke(
            session);
    }

    private void RefreshHistory(
        AircraftSessionSnapshot session)
    {
        var selectedKey =
            _historyGrid.SelectedRows.Count >
                0 &&
            _historyGrid.SelectedRows[0].Tag is
                Vdl2Message selected
                    ? selected.DedupKey
                    : string.Empty;

        _refreshing =
            true;

        _historyGrid.SuspendLayout();

        try
        {
            _historyGrid.Rows.Clear();

            foreach (var message in
                session.RecentMessages)
            {
                var index =
                    _historyGrid.Rows.Add(
                        message.ReceivedAt
                            .ToLocalTime()
                            .ToString(
                                "HH:mm:ss"),
                        message.Direction,
                        message.Callsign,
                        message.Label,
                        message.AcarsMessageId,
                        message.FrequencyMhz?
                            .ToString(
                                "0.000") ??
                        string.Empty,
                        message.Text);

                var row =
                    _historyGrid.Rows[index];

                row.Tag =
                    message;

                row.Cells["Text"].ToolTipText =
                    message.RawJson;

                if (selectedKey.Length > 0 &&
                    string.Equals(
                        selectedKey,
                        message.DedupKey,
                        StringComparison.Ordinal))
                {
                    row.Selected =
                        true;
                }
            }

            if (_historyGrid.SelectedRows.Count ==
                    0 &&
                _historyGrid.Rows.Count >
                    0)
            {
                _historyGrid.Rows[0].Selected =
                    true;
            }
        }
        finally
        {
            _historyGrid.ResumeLayout();
            _refreshing =
                false;
        }
    }

    private void PublishSelectedHistoryMessage(
        bool force = false)
    {
        if (_historyGrid.SelectedRows.Count ==
                0 ||
            _historyGrid.SelectedRows[0].Tag is
                not Vdl2Message message)
        {
            return;
        }

        if (!force &&
            string.Equals(
                message.DedupKey,
                _lastPublishedMessageKey,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastPublishedMessageKey =
            message.DedupKey;

        MessageSelected?.Invoke(
            message);
    }

    private void UpdateSummary()
    {
        var selected =
            SelectedSession();

        if (selected is null)
        {
            _summary.Text =
                $"{_currentSessions.Count} aircraft";

            return;
        }

        _summary.Text =
            $"{_currentSessions.Count} aircraft · " +
            $"{selected.Icao} · " +
            $"{selected.MessageCount} messages · " +
            $"last {FormatAge(selected.Age(DateTimeOffset.Now))}";
    }

    private void ScheduleMetadataLookups(
        IReadOnlyList<AircraftSessionSnapshot> sessions)
    {
        foreach (var session in sessions.Take(24))
        {
            var shouldStart =
                false;

            lock (_metadataGate)
            {
                if (_metadataPending.Count >=
                    8)
                {
                    break;
                }

                if (!_metadata.ContainsKey(
                        session.Icao) &&
                    !_metadataPending.Contains(
                        session.Icao))
                {
                    _metadataPending.Add(
                        session.Icao);

                    shouldStart =
                        true;
                }
            }

            if (shouldStart)
            {
                _ =
                    LoadMetadataAsync(
                        session.Icao,
                        _disposeCancellation.Token);
            }
        }
    }

    private async Task LoadMetadataAsync(
        string icao,
        CancellationToken cancellationToken)
    {
        try
        {
            var metadata =
                await _metadataService.LookupAircraftAsync(
                    icao,
                    cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            lock (_metadataGate)
            {
                _metadata[icao] =
                    metadata;
            }

            if (!IsDisposed &&
                !Disposing)
            {
                ApplyMetadataToVisibleRow(
                    icao,
                    metadata);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_metadataGate)
            {
                _metadataPending.Remove(
                    icao);
            }
        }
    }

    private void ApplyMetadataToVisibleRow(
        string icao,
        AircraftMetadata metadata)
    {
        foreach (DataGridViewRow row in
            _sessionsGrid.Rows)
        {
            if (row.Tag is not
                    AircraftSessionSnapshot session ||
                !string.Equals(
                    session.Icao,
                    icao,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (metadata.Found)
            {
                row.Cells["Registration"].Value =
                    FirstNonEmpty(
                        metadata.Registration,
                        session.Registration);

                row.Cells["Aircraft"].Value =
                    CombineAircraft(
                        metadata);

                row.Cells["Operator"].Value =
                    CombineOperator(
                        metadata);
            }

            break;
        }
    }

    private static DataGridView CreateGrid()
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
                    Color.FromArgb(
                        24,
                        28,
                        34),
                BorderStyle =
                    BorderStyle.None,
                CellBorderStyle =
                    DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor =
                    Color.FromArgb(
                        55,
                        62,
                        72),
                EnableHeadersVisualStyles =
                    false
            };

        grid.ColumnHeadersDefaultCellStyle.BackColor =
            Color.FromArgb(
                42,
                48,
                57);

        grid.ColumnHeadersDefaultCellStyle.ForeColor =
            Color.WhiteSmoke;

        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor =
            Color.FromArgb(
                42,
                48,
                57);

        grid.ColumnHeadersDefaultCellStyle.Font =
            new Font(
                grid.Font,
                FontStyle.Bold);

        grid.ColumnHeadersHeight =
            30;

        grid.DefaultCellStyle.BackColor =
            Color.FromArgb(
                28,
                33,
                40);

        grid.DefaultCellStyle.ForeColor =
            Color.Gainsboro;

        grid.DefaultCellStyle.SelectionBackColor =
            Color.FromArgb(
                38,
                92,
                132);

        grid.DefaultCellStyle.SelectionForeColor =
            Color.White;

        grid.AlternatingRowsDefaultCellStyle.BackColor =
            Color.FromArgb(
                32,
                38,
                46);

        grid.RowTemplate.Height =
            26;

        return grid;
    }

    private static void AddTextColumn(
        DataGridView grid,
        string name,
        int width,
        DataGridViewAutoSizeColumnMode mode =
            DataGridViewAutoSizeColumnMode.None)
    {
        grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name =
                    name,
                HeaderText =
                    name,
                Width =
                    width,
                AutoSizeMode =
                    mode,
                SortMode =
                    DataGridViewColumnSortMode.NotSortable
            });
    }

    private static void AddLinkColumn(
        DataGridView grid,
        string name,
        int width)
    {
        grid.Columns.Add(
            new DataGridViewLinkColumn
            {
                Name =
                    name,
                HeaderText =
                    name,
                Width =
                    width,
                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.None,
                SortMode =
                    DataGridViewColumnSortMode.NotSortable,
                TrackVisitedState =
                    false,
                UseColumnTextForLinkValue =
                    false
            });
    }

    private static void ApplyStateStyle(
        DataGridViewRow row,
        string state)
    {
        row.Cells["State"].Style.ForeColor =
            state switch
            {
                "LIVE" =>
                    Color.LightGreen,

                "RECENT" =>
                    Color.Khaki,

                _ =>
                    Color.Silver
            };

    }

    private static string SessionState(
        TimeSpan age)
    {
        if (age <=
            TimeSpan.FromMinutes(2))
        {
            return "LIVE";
        }

        if (age <=
            TimeSpan.FromMinutes(10))
        {
            return "RECENT";
        }

        return "IDLE";
    }

    private static string FormatAge(
        TimeSpan age)
    {
        if (age <
            TimeSpan.FromMinutes(1))
        {
            return
                $"{Math.Max(0, (int)age.TotalSeconds)} s";
        }

        if (age <
            TimeSpan.FromHours(1))
        {
            return
                $"{(int)age.TotalMinutes} min";
        }

        return
            $"{(int)age.TotalHours} h " +
            $"{age.Minutes} min";
    }

    private static string FirstNonEmpty(
        string? first,
        string? second)
    {
        if (!string.IsNullOrWhiteSpace(
            first))
        {
            return first.Trim();
        }

        return
            second?.Trim() ??
            string.Empty;
    }

    private static string CombineAircraft(
        AircraftMetadata metadata)
    {
        var values =
            new[]
            {
                metadata.Manufacturer,
                metadata.Type,
                string.IsNullOrWhiteSpace(
                    metadata.IcaoTypeCode)
                    ? string.Empty
                    : $"({metadata.IcaoTypeCode})"
            }
            .Where(
                value =>
                    !string.IsNullOrWhiteSpace(
                        value));

        return
            string.Join(
                " ",
                values);
    }

    private static string CombineOperator(
        AircraftMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(
            metadata.OperatorCode))
        {
            return
                metadata.Operator;
        }

        if (string.IsNullOrWhiteSpace(
            metadata.Operator))
        {
            return
                metadata.OperatorCode;
        }

        return
            metadata.Operator +
            " (" +
            metadata.OperatorCode +
            ")";
    }
}
