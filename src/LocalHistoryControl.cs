// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal sealed class LocalHistoryControl : UserControl
{
    private sealed record PeriodOption(string Label, int Mode)
    {
        public override string ToString() => Label;
    }

    private readonly LocalHistoryDatabase _database;
    private readonly System.Windows.Forms.Timer _filterDebounce = new() { Interval = 450 };
    private CancellationTokenSource? _refreshCancellation;
    private int _refreshing;
    private bool _populating;

    private IReadOnlyList<HistoricalAircraftSnapshot> _currentAircraft =
        Array.Empty<HistoricalAircraftSnapshot>();
    private IReadOnlyList<Vdl2Message> _currentMessages =
        Array.Empty<Vdl2Message>();

    private readonly TextBox _filter = new()
    {
        PlaceholderText = "ICAO, matrícula, voo, label, texto…",
        Width = 230
    };

    private readonly ComboBox _period = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 130
    };

    private readonly ComboBox _limit = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 82
    };

    private readonly Button _refresh = CommandButton("Refresh");
    private readonly Button _openFolder = CommandButton("Open folder");

    private readonly Label _summary = new()
    {
        Text = "Embedded database is starting…",
        AutoSize = true,
        ForeColor = Color.LightSteelBlue,
        Padding = new Padding(8, 7, 0, 0)
    };

    private readonly TabControl _views = new()
    {
        Dock = DockStyle.Fill,
        Padding = new Point(12, 5)
    };

    private readonly DataGridView _aircraftGrid = CreateGrid();
    private readonly DataGridView _messagesGrid = CreateGrid();

    public LocalHistoryControl(LocalHistoryDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(20, 24, 30);
        ForeColor = Color.Gainsboro;

        BuildInterface();
        ConfigureAircraftColumns();
        ConfigureMessageColumns();

        _period.Items.AddRange([
            new PeriodOption("Today", 0),
            new PeriodOption("Last 24 h", 1),
            new PeriodOption("Last 7 days", 2),
            new PeriodOption("Last 30 days", 3),
            new PeriodOption("All history", 4)
        ]);
        _period.SelectedIndex = 2;

        _limit.Items.AddRange([250, 500, 1000, 2500]);
        _limit.SelectedItem = 500;

        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        };

        _filter.TextChanged += (_, _) =>
        {
            _filterDebounce.Stop();
            _filterDebounce.Start();
        };

        _period.SelectedIndexChanged += (_, _) =>
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        _limit.SelectedIndexChanged += (_, _) =>
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        _refresh.Click += (_, _) =>
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        _openFolder.Click += (_, _) => OpenDatabaseFolder();
        _views.SelectedIndexChanged += (_, _) => PublishCurrentSelection();

        _aircraftGrid.SelectionChanged += (_, _) =>
        {
            if (!_populating)
                PublishAircraftSelection();
        };
        _messagesGrid.SelectionChanged += (_, _) =>
        {
            if (!_populating)
                PublishMessageSelection();
        };

        _aircraftGrid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 &&
                _aircraftGrid.Columns[e.ColumnIndex].Name == "ICAO")
            {
                _aircraftGrid.Rows[e.RowIndex].Selected = true;
                OpenSelectedAircraft();
            }
        };

        _aircraftGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                PublishAircraftSelection();
        };
        _messagesGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                PublishMessageSelection();
        };
    }

    public event EventHandler? RefreshRequested;
    public event EventHandler? SelectionCleared;
    public event Action<HistoricalAircraftSnapshot>? AircraftSelected;
    public event Action<Vdl2Message>? MessageSelected;

    public async Task RefreshAsync()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _refreshCancellation, next);
        previous?.Cancel();
        previous?.Dispose();

        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
            return;

        try
        {
            var status = _database.StatusSnapshot();
            if (!status.Ready)
            {
                _summary.Text = status.Faulted
                    ? "Database error: " + status.LastError
                    : "Embedded SQLite database is starting…";
                return;
            }

            _summary.Text = "Loading local history…";
            var query = BuildQuery();
            var aircraftTask = _database.QueryAircraftAsync(query, next.Token);
            var messagesTask = _database.QueryMessagesAsync(query, next.Token);
            await Task.WhenAll(aircraftTask, messagesTask);
            next.Token.ThrowIfCancellationRequested();

            _currentAircraft = aircraftTask.Result;
            _currentMessages = messagesTask.Result;
            PopulateAircraft();
            PopulateMessages();
            UpdateSummary(_database.StatusSnapshot());
            PublishCurrentSelection();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _summary.Text = "History query failed: " + ex.GetType().Name + " · " + ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    public void PublishCurrentSelection()
    {
        if (_views.SelectedIndex == 0)
            PublishAircraftSelection();
        else
            PublishMessageSelection();
    }

    public void OpenDatabaseFolder()
    {
        try
        {
            Directory.CreateDirectory(_database.DatabaseDirectory);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _database.DatabaseDirectory,
                    UseShellExecute = true
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Open history folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _filterDebounce.Dispose();
        }
        base.Dispose(disposing);
    }

    private LocalHistoryQuery BuildQuery()
    {
        var now = DateTimeOffset.Now;
        DateTimeOffset? from = _period.SelectedItem is PeriodOption option
            ? option.Mode switch
            {
                0 => new DateTimeOffset(
                    now.Year, now.Month, now.Day, 0, 0, 0, now.Offset)
                    .ToUniversalTime(),
                1 => now.AddHours(-24).ToUniversalTime(),
                2 => now.AddDays(-7).ToUniversalTime(),
                3 => now.AddDays(-30).ToUniversalTime(),
                _ => null
            }
            : now.AddDays(-7).ToUniversalTime();

        var limit = _limit.SelectedItem is int selected ? selected : 500;
        return new LocalHistoryQuery(
            _filter.Text.Trim(),
            from,
            DateTimeOffset.UtcNow,
            limit);
    }

    private void PopulateAircraft()
    {
        var selectedIcao = SelectedAircraft()?.Icao ?? string.Empty;
        _populating = true;
        _aircraftGrid.SuspendLayout();
        try
        {
            _aircraftGrid.Rows.Clear();
            foreach (var aircraft in _currentAircraft)
            {
                var index = _aircraftGrid.Rows.Add(
                    aircraft.Icao,
                    aircraft.Registration,
                    aircraft.Callsign,
                    aircraft.MessageCount,
                    aircraft.FirstSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    aircraft.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    FormatDuration(aircraft.Duration),
                    aircraft.LastLabel,
                    aircraft.LastFrequencyMhz?.ToString("0.000") ?? string.Empty,
                    aircraft.BestSignalDb?.ToString("0.0") ?? string.Empty,
                    aircraft.LastText);

                var row = _aircraftGrid.Rows[index];
                row.Tag = aircraft;
                if (selectedIcao.Length > 0 &&
                    string.Equals(selectedIcao, aircraft.Icao, StringComparison.OrdinalIgnoreCase))
                {
                    row.Selected = true;
                }
            }

            if (_aircraftGrid.SelectedRows.Count == 0 && _aircraftGrid.Rows.Count > 0)
                _aircraftGrid.Rows[0].Selected = true;
        }
        finally
        {
            _aircraftGrid.ResumeLayout();
            _populating = false;
        }
    }

    private void PopulateMessages()
    {
        var selectedKey = SelectedMessage()?.DedupKey ?? string.Empty;
        _populating = true;
        _messagesGrid.SuspendLayout();
        try
        {
            _messagesGrid.Rows.Clear();
            foreach (var message in _currentMessages)
            {
                var index = _messagesGrid.Rows.Add(
                    message.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    message.Protocol,
                    message.Direction,
                    message.Icao,
                    message.Registration,
                    message.Callsign,
                    message.Label,
                    message.AcarsMessageId,
                    message.FrequencyMhz?.ToString("0.000") ?? string.Empty,
                    message.Text);

                var row = _messagesGrid.Rows[index];
                row.Tag = message;
                if (selectedKey.Length > 0 &&
                    string.Equals(selectedKey, message.DedupKey, StringComparison.Ordinal))
                {
                    row.Selected = true;
                }
            }

            if (_messagesGrid.SelectedRows.Count == 0 && _messagesGrid.Rows.Count > 0)
                _messagesGrid.Rows[0].Selected = true;
        }
        finally
        {
            _messagesGrid.ResumeLayout();
            _populating = false;
        }
    }

    private void UpdateSummary(LocalHistoryStatus status)
    {
        _summary.Text =
            $"SQLite local · {status.StoredMessages} messages · " +
            $"{status.StoredAircraft} aircraft · {FormatBytes(status.FileBytes)} · " +
            $"showing {_currentAircraft.Count} aircraft / {_currentMessages.Count} messages" +
            (status.PendingWrites > 0 ? $" · queue {status.PendingWrites}" : string.Empty) +
            (status.DroppedWrites > 0 ? $" · dropped {status.DroppedWrites}" : string.Empty);
    }

    private HistoricalAircraftSnapshot? SelectedAircraft() =>
        _aircraftGrid.SelectedRows.Count > 0
            ? _aircraftGrid.SelectedRows[0].Tag as HistoricalAircraftSnapshot
            : null;

    private Vdl2Message? SelectedMessage() =>
        _messagesGrid.SelectedRows.Count > 0
            ? _messagesGrid.SelectedRows[0].Tag as Vdl2Message
            : null;

    private void PublishAircraftSelection()
    {
        var aircraft = SelectedAircraft();
        if (aircraft is null)
        {
            SelectionCleared?.Invoke(this, EventArgs.Empty);
            return;
        }
        AircraftSelected?.Invoke(aircraft);
    }

    private void PublishMessageSelection()
    {
        var message = SelectedMessage();
        if (message is null)
        {
            SelectionCleared?.Invoke(this, EventArgs.Empty);
            return;
        }
        MessageSelected?.Invoke(message);
    }

    private void OpenSelectedAircraft()
    {
        var aircraft = SelectedAircraft();
        if (aircraft is null)
            return;

        try
        {
            AircraftOnlineLookup.Open(
                AircraftOnlineProvider.Planespotters,
                aircraft.Icao,
                aircraft.Registration,
                aircraft.Callsign);
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

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = BackColor,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            BackColor = Color.FromArgb(27, 32, 39),
            Padding = new Padding(8, 6, 8, 6)
        };

        toolbar.Controls.Add(new Label
        {
            Text = "LOCAL HISTORY",
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 7, 10, 0)
        });
        toolbar.Controls.Add(_filter);
        toolbar.Controls.Add(_period);
        toolbar.Controls.Add(_limit);
        toolbar.Controls.Add(_refresh);
        toolbar.Controls.Add(_openFolder);
        toolbar.Controls.Add(_summary);

        var aircraftTab = new TabPage("Historical Aircraft")
        {
            BackColor = BackColor,
            ForeColor = ForeColor,
            Padding = Padding.Empty
        };
        aircraftTab.Controls.Add(_aircraftGrid);

        var messagesTab = new TabPage("Historical Messages")
        {
            BackColor = BackColor,
            ForeColor = ForeColor,
            Padding = Padding.Empty
        };
        messagesTab.Controls.Add(_messagesGrid);

        _views.TabPages.Add(aircraftTab);
        _views.TabPages.Add(messagesTab);
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(_views, 0, 1);
        Controls.Add(root);
    }

    private void ConfigureAircraftColumns()
    {
        _aircraftGrid.Columns.Add(new DataGridViewLinkColumn
        {
            Name = "ICAO",
            HeaderText = "ICAO",
            Width = 72,
            LinkColor = Color.DeepSkyBlue,
            ActiveLinkColor = Color.White,
            VisitedLinkColor = Color.DeepSkyBlue,
            TrackVisitedState = false,
            ToolTipText = "Open aircraft details"
        });
        AddTextColumn(_aircraftGrid, "Registration", 92);
        AddTextColumn(_aircraftGrid, "Flight", 82);
        AddTextColumn(_aircraftGrid, "Messages", 72);
        AddTextColumn(_aircraftGrid, "First", 122);
        AddTextColumn(_aircraftGrid, "Last", 122);
        AddTextColumn(_aircraftGrid, "Duration", 82);
        AddTextColumn(_aircraftGrid, "Label", 54);
        AddTextColumn(_aircraftGrid, "Freq MHz", 72);
        AddTextColumn(_aircraftGrid, "Best dB", 64);
        AddTextColumn(
            _aircraftGrid,
            "Last text",
            320,
            DataGridViewAutoSizeColumnMode.Fill);
    }

    private void ConfigureMessageColumns()
    {
        AddTextColumn(_messagesGrid, "Time", 128);
        AddTextColumn(_messagesGrid, "Protocol", 66);
        AddTextColumn(_messagesGrid, "Direction", 92);
        AddTextColumn(_messagesGrid, "ICAO", 72);
        AddTextColumn(_messagesGrid, "Registration", 92);
        AddTextColumn(_messagesGrid, "Flight", 82);
        AddTextColumn(_messagesGrid, "Label", 54);
        AddTextColumn(_messagesGrid, "Message", 68);
        AddTextColumn(_messagesGrid, "Freq MHz", 72);
        AddTextColumn(
            _messagesGrid,
            "Text",
            360,
            DataGridViewAutoSizeColumnMode.Fill);
    }

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(24, 28, 34),
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(55, 62, 72),
            EnableHeadersVisualStyles = false
        };

        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(42, 48, 57);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.WhiteSmoke;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);
        grid.ColumnHeadersHeight = 30;
        grid.DefaultCellStyle.BackColor = Color.FromArgb(28, 33, 40);
        grid.DefaultCellStyle.ForeColor = Color.Gainsboro;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 92, 132);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(32, 38, 46);
        grid.RowTemplate.Height = 26;
        return grid;
    }

    private static void AddTextColumn(
        DataGridView grid,
        string name,
        int width,
        DataGridViewAutoSizeColumnMode autoSize = DataGridViewAutoSizeColumnMode.None)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = name,
            Width = width,
            AutoSizeMode = autoSize,
            SortMode = DataGridViewColumnSortMode.Automatic
        });
    }

    private static Button CommandButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 52, 62),
            ForeColor = Color.WhiteSmoke,
            Padding = new Padding(5, 2, 5, 2)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(80, 92, 108);
        return button;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
            return $"{Math.Max(0, (int)duration.TotalSeconds)} s";
        if (duration < TimeSpan.FromHours(1))
            return $"{(int)duration.TotalMinutes} min";
        if (duration < TimeSpan.FromDays(1))
            return $"{(int)duration.TotalHours} h {duration.Minutes} min";
        return $"{(int)duration.TotalDays} d {duration.Hours} h";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024L * 1024L)
            return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024L * 1024L)
            return $"{bytes / 1024.0 / 1024.0:0.0} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB";
    }
}
