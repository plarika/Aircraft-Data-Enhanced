// SPDX-License-Identifier: MIT
using SDRSharp.Common;
using SDRSharp.Radio;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Forms;

namespace SDRSharp.AircraftDataEnhanced;

public sealed class AircraftDataPanel : UserControl
{
    private const string ProductTitle =
        "Aircraft Data Enhanced — VDL2 Air Operations Terminal v0.19.0-beta";

    private sealed record PendingAnalysisUi(
        string State,
        string Details);

    private readonly ISharpControl _control;
    private readonly IqStreamProcessor _iqProcessor = new();
    private readonly SignalMetrics _metrics = new();
    private readonly ManagedBurstDetector _burstDetector = new();
    private readonly DecoderStats _stats = new();
    private readonly MessageStore _store;
    private readonly AircraftSessionStore _sessionStore =
        new();
    private readonly LocalHistoryDatabase _historyDatabase =
        new();
    private readonly LocalHistoryControl _historyControl;
    private readonly AirOperationsBoardControl _operationsBoard =
        new();
    private readonly UiPreferences _uiPreferences =
        UiPreferencesStore.Load();
    private readonly JsonlExporter _exporter = new();
    private readonly IqCaptureManager _captureManager = new();
    private readonly D8pskSymbolAnalyzer _d8pskAnalyzer = new();
    private readonly Vdl2AnalysisScheduler _analysisScheduler;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly object _pendingUiGate = new();

    private long _centerFrequency;
    private long _lastGridVersion = -1;
    private long _lastSessionVersion = -1;
    private long _lastSessionUiRefreshTicks;
    private long _lastHistoryVersion = -1;
    private long _lastHistoryUiRefreshTicks;
    private long _validAvlcFrames;
    private long _invalidFcsFrames;
    private long _salvagedAvlcFrames;
    private long _filteredUnknownMessages;
    private int _gridRefreshRequested = 1;
    private int _sessionRefreshRequested = 1;
    private int _historyRefreshRequested = 1;
    private int _historyRefreshInProgress;
    private bool _refreshingGrid;
    private bool _operationsBoardViewActive;
    private bool _sessionViewActive;
    private bool _historyViewActive;
    private volatile bool _shutdown;
    private volatile bool _autoAnalyzeD8psk = true;
    private D8pskAnalysisResult? _latestD8pskResult;
    private Vdl2Message? _selectedContextMessage;
    private PendingAnalysisUi? _pendingAnalysisUi;
    private string? _pendingDecoderState;
    private string? _pendingD8pskState;

    private readonly ToolStripStatusLabel _compactIqStatus =
        new()
        {
            Text = "IQ waiting",
            BorderSides = ToolStripStatusLabelBorderSides.Right
        };
    private readonly ToolStripStatusLabel _compactDecoderStatus =
        new()
        {
            Text = "Decoder idle",
            BorderSides = ToolStripStatusLabelBorderSides.Right
        };
    private readonly ToolStripStatusLabel _compactMessageStatus =
        new()
        {
            Text = "Messages 0",
            BorderSides = ToolStripStatusLabelBorderSides.Right
        };
    private readonly ToolStripStatusLabel _compactAircraftStatus =
        new()
        {
            Text = "Aircraft 0",
            BorderSides = ToolStripStatusLabelBorderSides.Right
        };
    private readonly ToolStripStatusLabel _compactDatabaseStatus =
        new()
        {
            Text = "DB starting",
            BorderSides = ToolStripStatusLabelBorderSides.Right
        };
    private readonly ToolStripStatusLabel _compactPipelineStatus =
        new()
        {
            Text = "Pipeline idle",
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

    private readonly Label _iqState = new() { AutoSize = true, Text = "IQ: waiting" };
    private readonly Label _decoderState = new() { AutoSize = true };
    private readonly Label _metricsLabel = new() { AutoSize = true };
    private readonly Label _messageStats = new() { AutoSize = true };
    private readonly Label _pipelineState = new()
    {
        AutoSize = true,
        Text = "Pipeline: idle"
    };
    private readonly Button _clear = new() { Text = "Clear live", AutoSize = true };
    private readonly Button _export = new() { Text = "Export JSONL", AutoSize = true };
    private readonly Button _openHistory = new() { Text = "Open history", AutoSize = true };
    private readonly Button _refreshHistory = new() { Text = "Refresh history", AutoSize = true };
    private readonly Button _clearHistory = new() { Text = "Clear local DB", AutoSize = true };
    private readonly Label _databaseStatus = new()
    {
        Text = "Local DB: starting",
        AutoSize = true,
        Padding = new Padding(8, 6, 0, 0)
    };
    private readonly Label _burstState = new() { AutoSize = true, Text = "Burst: IDLE" };
    private readonly Label _spectralState = new() { AutoSize = true, Text = "Spectral: waiting" };
    private readonly SpectrumWaterfallControl _spectrumWaterfall = new()
    {
        Dock = DockStyle.Fill
    };
    private readonly Dictionary<long, ChannelStatusCard> _channelCards = new();
    private readonly CheckBox _pauseWaterfall = new()
    {
        Text = "Pause waterfall",
        AutoSize = true
    };
    private readonly Button _clearWaterfall = new()
    {
        Text = "Clear waterfall",
        AutoSize = true
    };
    private readonly NumericUpDown _waterfallMin = new()
    {
        Minimum = -140,
        Maximum = -20,
        Value = -100,
        Width = 70
    };
    private readonly NumericUpDown _waterfallMax = new()
    {
        Minimum = -120,
        Maximum = 10,
        Value = -35,
        Width = 70
    };
    private readonly NumericUpDown _waterfallContrast = new()
    {
        Minimum = 25,
        Maximum = 400,
        Value = 100,
        Increment = 25,
        Width = 70
    };
    private readonly ComboBox _channelHistoryFilter = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 130
    };
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = true
    };
    private readonly Button _openCaptures = new()
    {
        Text = "Open captures",
        AutoSize = true
    };
    private readonly Label _captureStatus = new()
    {
        Text = "Captures: 0",
        AutoSize = true,
        Padding = new Padding(8, 6, 0, 0)
    };
    private readonly Label _d8pskState = new()
    {
        Text = "VDL2: waiting for a bounded capture",
        AutoSize = true
    };
    private readonly CheckBox _autoD8psk = new()
    {
        Text = "Auto VDL2 decode",
        Checked = true,
        AutoSize = true
    };
    private readonly CheckBox _diagnosticLimited = new()
    {
        Text = "Diagnostic limited",
        Checked = false,
        AutoSize = true
    };
    private readonly Button _analyzeLatest = new()
    {
        Text = "Analyze latest",
        AutoSize = true
    };
    private readonly Button _openAnalysis = new()
    {
        Text = "Open analysis",
        AutoSize = true
    };
    private readonly Button _openAircraftOnline = new()
    {
        Text = "Aircraft details",
        AutoSize = true,
        Enabled = false
    };
    private readonly ContextMenuStrip _aircraftOnlineMenu = new();
    private readonly AircraftDashboardControl _aircraftDashboard =
        new()
        {
            Dock = DockStyle.Fill
        };
    private readonly ActiveAircraftSessionsControl _activeSessions =
        new()
        {
            Dock = DockStyle.Fill
        };
    private readonly TextBox _sessionDetails =
        new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Text = "Select an active aircraft session."
        };
    private readonly TextBox _d8pskDetails = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Text = "No D8PSK symbol analysis yet."
    };
    private readonly NumericUpDown _threshold = new()
    {
        Minimum = 2,
        Maximum = 20,
        DecimalPlaces = 1,
        Increment = 0.5M,
        Value = 5.0M,
        Width = 70
    };
    private readonly NumericUpDown _maxBurst = new()
    {
        Minimum = 100,
        Maximum = 10000,
        Increment = 100,
        Value = 1400,
        Width = 80
    };
    private readonly TextBox _filter = new()
    {
        PlaceholderText = "ICAO, matrícula, callsign, texto…",
        Dock = DockStyle.Fill
    };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false
    };

    public unsafe AircraftDataPanel(ISharpControl control)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _store = new MessageStore(_stats);
        _historyControl = new LocalHistoryControl(_historyDatabase);
        ApplyStoredControlPreferences();
        _analysisScheduler =
            new Vdl2AnalysisScheduler(
                _d8pskAnalyzer,
                capacity: 8);

        _analysisScheduler.AnalysisStarted +=
            OnAnalysisStarted;
        _analysisScheduler.AnalysisCompleted +=
            OnAnalysisCompleted;
        _analysisScheduler.BatchCompleted +=
            OnAnalysisBatchCompleted;
        _analysisScheduler.AnalysisDropped +=
            OnAnalysisDropped;

        Dock = DockStyle.Fill;
        MinimumSize = new Size(520, 340);
        BuildUi();

        _decoderState.Text = $"Detector: {_burstDetector.Status}";
        _centerFrequency = ReadFrequencySafely();

        _iqProcessor.BlockAvailable += OnIqBlock;
        _control.RegisterStreamHook(_iqProcessor, ProcessorType.DecimatedAndFilteredIQ);
        _control.PropertyChanged += ControlPropertyChanged;

        _clear.Click += (_, _) =>
        {
            _store.Clear();
            _sessionStore.Clear();
            _burstDetector.Reset();
            Interlocked.Exchange(
                ref _gridRefreshRequested,
                1);
            Interlocked.Exchange(
                ref _sessionRefreshRequested,
                1);
        };
        _export.Click += ExportClicked;
        _filter.TextChanged += (_, _) =>
            Interlocked.Exchange(
                ref _gridRefreshRequested,
                1);
        _activeSessions.FilterChanged +=
            (_, _) =>
                Interlocked.Exchange(
                    ref _sessionRefreshRequested,
                    1);
        _activeSessions.SessionSelected +=
            ShowAircraftSession;
        _activeSessions.MessageSelected +=
            ShowSessionMessage;
        _activeSessions.SelectionCleared +=
            (_, _) =>
                ClearAircraftSessionSelection();

        _operationsBoard.FilterChanged +=
            (_, _) =>
            {
                _uiPreferences.OperationsWindowIndex =
                    _operationsBoard.SelectedWindowIndex;

                SaveUiPreferences();

                Interlocked.Exchange(
                    ref _sessionRefreshRequested,
                    1);
            };

        _operationsBoard.SessionSelected +=
            ShowAircraftSession;

        _operationsBoard.SelectionCleared +=
            (_, _) =>
                ClearAircraftSessionSelection();

        _historyControl.RefreshRequested +=
            (_, _) =>
                Interlocked.Exchange(ref _historyRefreshRequested, 1);
        _historyControl.AircraftSelected += ShowHistoricalAircraft;
        _historyControl.MessageSelected += ShowHistoricalMessage;
        _historyControl.SelectionCleared +=
            (_, _) => ClearHistorySelection();
        _openHistory.Click += (_, _) => ShowLocalHistoryFolder();
        _refreshHistory.Click +=
            (_, _) => Interlocked.Exchange(ref _historyRefreshRequested, 1);
        _clearHistory.Click +=
            ClearHistoryButtonClicked;

        _threshold.ValueChanged += (_, _) =>
            _burstDetector.EnterThresholdDb = (double)_threshold.Value;
        _maxBurst.ValueChanged += (_, _) =>
            _burstDetector.MaximumBurstMs = (double)_maxBurst.Value;

        _pauseWaterfall.CheckedChanged += (_, _) =>
            _spectrumWaterfall.Paused = _pauseWaterfall.Checked;
        _clearWaterfall.Click += (_, _) =>
            _spectrumWaterfall.ClearWaterfall();
        _waterfallMin.ValueChanged +=
            (_, _) =>
            {
                _spectrumWaterfall.MinimumDb =
                    (float)_waterfallMin.Value;

                _uiPreferences.WaterfallMinimumDb =
                    _waterfallMin.Value;

                SaveUiPreferences();
            };

        _waterfallMax.ValueChanged +=
            (_, _) =>
            {
                _spectrumWaterfall.MaximumDb =
                    (float)_waterfallMax.Value;

                _uiPreferences.WaterfallMaximumDb =
                    _waterfallMax.Value;

                SaveUiPreferences();
            };

        _waterfallContrast.ValueChanged +=
            (_, _) =>
            {
                _spectrumWaterfall.Contrast =
                    (float)_waterfallContrast.Value /
                    100f;

                _uiPreferences.WaterfallContrastPercent =
                    _waterfallContrast.Value;

                SaveUiPreferences();
            };
        _channelHistoryFilter.SelectedIndexChanged += (_, _) =>
            Interlocked.Exchange(
                ref _gridRefreshRequested,
                1);
        _grid.SelectionChanged += (_, _) =>
        {
            if (!_refreshingGrid)
                UpdateSelectedEventDetails();
        };
        _grid.CellContentClick +=
            GridCellContentClick;
        _grid.CellDoubleClick +=
            GridCellDoubleClick;
        _grid.CellMouseDown +=
            GridCellMouseDown;
        _openAircraftOnline.Click += (_, _) =>
            OpenSelectedAircraftOnline(
                AircraftOnlineProvider.Planespotters);
        BuildAircraftOnlineMenu();
        _openCaptures.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _captureManager.CaptureDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open captures",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        _captureManager.CaptureCompleted += OnCaptureCompleted;
        _autoD8psk.CheckedChanged += (_, _) =>
            _autoAnalyzeD8psk = _autoD8psk.Checked;
        _analyzeLatest.Click += (_, _) =>
            AnalyzeLatestCapture();
        _openAnalysis.Click += (_, _) =>
            OpenAnalysisFolder();

        _burstDetector.EnterThresholdDb = (double)_threshold.Value;
        _burstDetector.MaximumBurstMs = (double)_maxBurst.Value;
        _spectrumWaterfall.MinimumDb = (float)_waterfallMin.Value;
        _spectrumWaterfall.MaximumDb = (float)_waterfallMax.Value;
        _spectrumWaterfall.Contrast = (float)_waterfallContrast.Value / 100f;
        _spectrumWaterfall.FilterBandwidthHz = 25_000;

        _channelHistoryFilter.Items.Add("All channels");
        _channelHistoryFilter.Items.Add("136.725 MHz");
        _channelHistoryFilter.Items.Add("136.775 MHz");
        _channelHistoryFilter.Items.Add("136.875 MHz");
        _channelHistoryFilter.Items.Add("136.975 MHz");
        _channelHistoryFilter.SelectedIndex = 0;

        _uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();
    }

    private void BuildUi()
    {
        SuspendLayout();

        BackColor =
            Color.FromArgb(
                20,
                24,
                30);

        ForeColor =
            Color.Gainsboro;

        Font =
            new Font(
                "Segoe UI",
                9.0f,
                FontStyle.Regular,
                GraphicsUnit.Point);

        _details.BackColor =
            Color.FromArgb(
                24,
                29,
                36);
        _details.ForeColor =
            Color.Gainsboro;
        _details.BorderStyle =
            BorderStyle.None;

        _sessionDetails.BackColor =
            Color.FromArgb(
                24,
                29,
                36);
        _sessionDetails.ForeColor =
            Color.Gainsboro;
        _sessionDetails.BorderStyle =
            BorderStyle.None;

        _d8pskDetails.BackColor =
            Color.FromArgb(
                18,
                22,
                28);
        _d8pskDetails.ForeColor =
            Color.Gainsboro;
        _d8pskDetails.BorderStyle =
            BorderStyle.None;

        var menuStrip =
            new MenuStrip
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(34, 40, 48),
                ForeColor = Color.WhiteSmoke,
                Renderer = new ToolStripProfessionalRenderer(),
                Padding = new Padding(6, 2, 0, 2)
            };

        var statusStrip =
            new StatusStrip
            {
                Dock = DockStyle.Bottom,
                BackColor = Color.FromArgb(31, 36, 43),
                ForeColor = Color.Gainsboro,
                SizingGrip = false
            };

        statusStrip.Items.AddRange(
        [
            _compactIqStatus,
            _compactDecoderStatus,
            _compactMessageStatus,
            _compactAircraftStatus,
            _compactDatabaseStatus,
            _compactPipelineStatus
        ]);

        var controlCenter =
            BuildControlCenter();

        var channelsPanel =
            BuildChannelsPanel();

        var eventSplit =
            new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Color.FromArgb(42, 48, 57),
                SplitterWidth = 5
            };

        var dataViews =
            new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.Normal,
                Padding = new Point(12, 5)
            };

        var operationsView =
            CreateWorkspaceTab(
                "Operations Board",
                _operationsBoard);

        var messagesView =
            CreateWorkspaceTab(
                "Verified Messages",
                _grid);

        var sessionsView =
            CreateWorkspaceTab(
                "Active Aircraft",
                _activeSessions);

        var historyView =
            CreateWorkspaceTab(
                "Local History",
                _historyControl);

        dataViews.TabPages.Add(
            operationsView);

        dataViews.TabPages.Add(
            messagesView);

        dataViews.TabPages.Add(
            sessionsView);

        dataViews.TabPages.Add(
            historyView);

        dataViews.SelectedIndex =
            Math.Clamp(
                _uiPreferences.SelectedWorkspace,
                0,
                dataViews.TabPages.Count -
                1);

        _operationsBoardViewActive =
            dataViews.SelectedIndex ==
            0;

        _sessionViewActive =
            dataViews.SelectedIndex ==
            2;

        _historyViewActive =
            dataViews.SelectedIndex ==
            3;

        dataViews.SelectedIndexChanged +=
            (_, _) =>
            {
                _operationsBoardViewActive =
                    dataViews.SelectedIndex ==
                    0;

                _sessionViewActive =
                    dataViews.SelectedIndex ==
                    2;

                _historyViewActive =
                    dataViews.SelectedIndex ==
                    3;

                _uiPreferences.SelectedWorkspace =
                    dataViews.SelectedIndex;

                SaveUiPreferences();

                if (_operationsBoardViewActive)
                {
                    Interlocked.Exchange(
                        ref _sessionRefreshRequested,
                        1);

                    RefreshSessionsIfNeeded();
                    _operationsBoard.PublishCurrentSelection();
                }
                else if (_sessionViewActive)
                {
                    Interlocked.Exchange(
                        ref _sessionRefreshRequested,
                        1);

                    RefreshSessionsIfNeeded();
                    _activeSessions.PublishCurrentSelection();
                }
                else if (_historyViewActive)
                {
                    Interlocked.Exchange(
                        ref _historyRefreshRequested,
                        1);

                    RefreshHistoryIfNeeded();
                    _historyControl.PublishCurrentSelection();
                }
                else
                {
                    UpdateSelectedEventDetails();
                }
            };

        eventSplit.Panel1.Controls.Add(
            dataViews);

        var detailsTabs =
            new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.Normal,
                Padding = new Point(12, 5)
            };

        var aircraftTab =
            CreateWorkspaceTab(
                "Aircraft",
                _aircraftDashboard);

        var sessionTab =
            CreateWorkspaceTab(
                "Session",
                _sessionDetails);

        var eventTab =
            CreateWorkspaceTab(
                "Message",
                _details);

        var decoderTab =
            CreateWorkspaceTab(
                "Decoder",
                _d8pskDetails);

        detailsTabs.TabPages.Add(aircraftTab);
        detailsTabs.TabPages.Add(sessionTab);
        detailsTabs.TabPages.Add(eventTab);
        detailsTabs.TabPages.Add(decoderTab);
        eventSplit.Panel2.Controls.Add(detailsTabs);

        var mainSplit =
            new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = Color.FromArgb(42, 48, 57),
                SplitterWidth = 5
            };

        mainSplit.Panel1.Controls.Add(_spectrumWaterfall);
        mainSplit.Panel2.Controls.Add(eventSplit);

        var searchBar =
            new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 7,
                BackColor = Color.FromArgb(27, 32, 39),
                Padding = new Padding(8, 6, 8, 6),
                Margin = Padding.Empty
            };

        searchBar.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));
        searchBar.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));
        searchBar.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));
        searchBar.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));
        searchBar.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));
        searchBar.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));
        searchBar.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));

        var productTitle =
            new Label
            {
                Text = "AIRCRAFT DATA ENHANCED",
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font(
                    Font,
                    FontStyle.Bold),
                Padding = new Padding(0, 7, 16, 0)
            };

        var versionBadge =
            new Label
            {
                Text = "VDL2 Live Pipeline · ACARS · Air Operations Terminal · v0.19.0-beta",
                AutoSize = true,
                ForeColor = Color.LightSteelBlue,
                Padding = new Padding(0, 7, 18, 0)
            };

        var searchLabel =
            new Label
            {
                Text = "Search",
                AutoSize = true,
                ForeColor = Color.Silver,
                Padding = new Padding(0, 7, 8, 0)
            };

        _filter.BackColor =
            Color.FromArgb(39, 45, 54);
        _filter.ForeColor =
            Color.WhiteSmoke;
        _filter.BorderStyle =
            BorderStyle.FixedSingle;
        _filter.Margin =
            new Padding(0, 2, 8, 2);

        var controlButton =
            CreateCommandButton(
                "Control Center");

        var analyzeButton =
            CreateCommandButton(
                "Analyze latest");

        var aircraftButton =
            CreateCommandButton(
                "Aircraft details");

        controlButton.Click += (_, _) =>
            controlCenter.Visible =
                !controlCenter.Visible;

        analyzeButton.Click += (_, _) =>
            _analyzeLatest.PerformClick();

        aircraftButton.Click += (_, _) =>
            _openAircraftOnline.PerformClick();

        searchBar.Controls.Add(productTitle, 0, 0);
        searchBar.Controls.Add(versionBadge, 1, 0);
        searchBar.Controls.Add(searchLabel, 2, 0);
        searchBar.Controls.Add(_filter, 3, 0);
        searchBar.Controls.Add(controlButton, 4, 0);
        searchBar.Controls.Add(analyzeButton, 5, 0);
        searchBar.Controls.Add(aircraftButton, 6, 0);

        AddColumn("Time", 74);
        AddColumn("Protocol", 68);
        AddColumn("Direction", 102);
        AddIcaoLinkColumn();
        AddColumn("Registration", 92);
        AddColumn("Flight", 82);
        AddColumn("Label", 56);
        AddColumn("Message", 68);
        AddColumn("Freq MHz", 76);
        AddColumn(
            "Text",
            360,
            DataGridViewAutoSizeColumnMode.Fill);
        ApplyModernGridStyle();

        var workspace =
            new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = BackColor,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };

        workspace.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));
        workspace.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));
        workspace.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));
        workspace.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        workspace.Controls.Add(searchBar, 0, 0);
        workspace.Controls.Add(controlCenter, 0, 1);
        workspace.Controls.Add(channelsPanel, 0, 2);
        workspace.Controls.Add(mainSplit, 0, 3);

        searchBar.Visible =
            _uiPreferences.CommandBarVisible;

        searchBar.Margin =
            Padding.Empty;

        controlCenter.Visible =
            _uiPreferences.ControlCenterVisible;

        channelsPanel.Visible =
            _uiPreferences.ChannelMonitorVisible;

        mainSplit.Panel1Collapsed =
            !_uiPreferences.WaterfallVisible;

        eventSplit.Panel2Collapsed =
            !_uiPreferences.DetailsVisible;

        BuildApplicationMenu(
            menuStrip,
            searchBar,
            controlCenter,
            channelsPanel,
            mainSplit,
            eventSplit,
            dataViews);

        var splitLayoutReady = false;

        void ApplySafeSplitterDistance()
        {
            if (!mainSplit.IsHandleCreated ||
                !eventSplit.IsHandleCreated)
            {
                return;
            }

            if (mainSplit.Height < 80 ||
                eventSplit.Width < 120)
            {
                return;
            }

            mainSplit.SuspendLayout();
            eventSplit.SuspendLayout();

            try
            {
                var horizontalAvailable =
                    mainSplit.Height -
                    mainSplit.SplitterWidth;

                if (horizontalAvailable >= 80)
                {
                    var upperMinimum =
                        Math.Min(
                            110,
                            Math.Max(
                                20,
                                horizontalAvailable / 4));

                    var lowerMinimum =
                        Math.Min(
                            140,
                            Math.Max(
                                20,
                                horizontalAvailable / 3));

                    mainSplit.Panel1MinSize = 0;
                    mainSplit.Panel2MinSize = 0;

                    mainSplit.SplitterDistance =
                        Math.Clamp(
                            (int)(horizontalAvailable * 0.43),
                            upperMinimum,
                            Math.Max(
                                upperMinimum,
                                horizontalAvailable - lowerMinimum));

                    mainSplit.Panel1MinSize =
                        upperMinimum;
                    mainSplit.Panel2MinSize =
                        lowerMinimum;
                }

                var verticalAvailable =
                    eventSplit.Width -
                    eventSplit.SplitterWidth;

                if (verticalAvailable >= 120)
                {
                    var tableMinimum =
                        Math.Min(
                            260,
                            Math.Max(
                                40,
                                verticalAvailable / 3));

                    var detailMinimum =
                        Math.Min(
                            210,
                            Math.Max(
                                40,
                                verticalAvailable / 4));

                    eventSplit.Panel1MinSize = 0;
                    eventSplit.Panel2MinSize = 0;

                    eventSplit.SplitterDistance =
                        Math.Clamp(
                            (int)(verticalAvailable * 0.68),
                            tableMinimum,
                            Math.Max(
                                tableMinimum,
                                verticalAvailable - detailMinimum));

                    eventSplit.Panel1MinSize =
                        tableMinimum;
                    eventSplit.Panel2MinSize =
                        detailMinimum;
                }

                splitLayoutReady = true;
            }
            catch (InvalidOperationException)
            {
                splitLayoutReady = false;
            }
            finally
            {
                eventSplit.ResumeLayout();
                mainSplit.ResumeLayout();
            }
        }

        mainSplit.HandleCreated += (_, _) =>
            BeginInvoke((MethodInvoker)ApplySafeSplitterDistance);

        eventSplit.HandleCreated += (_, _) =>
            BeginInvoke((MethodInvoker)ApplySafeSplitterDistance);

        mainSplit.Resize += (_, _) =>
        {
            if (splitLayoutReady)
                ApplySafeSplitterDistance();
        };

        eventSplit.Resize += (_, _) =>
        {
            if (splitLayoutReady)
                ApplySafeSplitterDistance();
        };

        Controls.Clear();
        Controls.Add(workspace);
        Controls.Add(statusStrip);
        Controls.Add(menuStrip);

        menuStrip.BringToFront();
        statusStrip.BringToFront();

        ResumeLayout();
    }

    private Panel BuildControlCenter()
    {
        var container =
            new Panel
            {
                Dock = DockStyle.Top,
                Height = 154,
                BackColor = Color.FromArgb(24, 29, 36),
                Padding = new Padding(8, 4, 8, 8)
            };

        var tabs =
            new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(12, 4)
            };

        var statusFlow =
            CreateControlFlow();

        foreach (var label in new[]
                 {
                     _iqState,
                     _decoderState,
                     _burstState,
                     _spectralState,
                     _metricsLabel,
                     _messageStats,
                     _pipelineState,
                     _d8pskState
                 })
        {
            label.ForeColor =
                Color.Gainsboro;
            label.Padding =
                new Padding(6, 5, 12, 5);
            statusFlow.Controls.Add(label);
        }

        var decoderFlow =
            CreateControlFlow();

        decoderFlow.Controls.Add(
            CreateControlLabel("Threshold dB"));
        decoderFlow.Controls.Add(_threshold);
        decoderFlow.Controls.Add(
            CreateControlLabel("Maximum burst ms"));
        decoderFlow.Controls.Add(_maxBurst);
        decoderFlow.Controls.Add(_autoD8psk);
        decoderFlow.Controls.Add(_diagnosticLimited);
        decoderFlow.Controls.Add(_analyzeLatest);
        decoderFlow.Controls.Add(_openAnalysis);

        var displayFlow =
            CreateControlFlow();

        displayFlow.Controls.Add(_pauseWaterfall);
        displayFlow.Controls.Add(_clearWaterfall);
        displayFlow.Controls.Add(
            CreateControlLabel("Minimum dBFS"));
        displayFlow.Controls.Add(_waterfallMin);
        displayFlow.Controls.Add(
            CreateControlLabel("Maximum dBFS"));
        displayFlow.Controls.Add(_waterfallMax);
        displayFlow.Controls.Add(
            CreateControlLabel("Contrast %"));
        displayFlow.Controls.Add(_waterfallContrast);
        displayFlow.Controls.Add(
            CreateControlLabel("History"));
        displayFlow.Controls.Add(_channelHistoryFilter);

        var dataFlow =
            CreateControlFlow();

        dataFlow.Controls.Add(_clear);
        dataFlow.Controls.Add(_export);
        dataFlow.Controls.Add(_openCaptures);
        dataFlow.Controls.Add(_captureStatus);
        dataFlow.Controls.Add(_openAircraftOnline);
        dataFlow.Controls.Add(_openHistory);
        dataFlow.Controls.Add(_refreshHistory);
        dataFlow.Controls.Add(_clearHistory);
        dataFlow.Controls.Add(_databaseStatus);

        tabs.TabPages.Add(
            CreateWorkspaceTab(
                "System status",
                statusFlow));
        tabs.TabPages.Add(
            CreateWorkspaceTab(
                "Decoder",
                decoderFlow));
        tabs.TabPages.Add(
            CreateWorkspaceTab(
                "Waterfall",
                displayFlow));
        tabs.TabPages.Add(
            CreateWorkspaceTab(
                "Data",
                dataFlow));

        container.Controls.Add(tabs);

        return container;
    }

    private FlowLayoutPanel BuildChannelsPanel()
    {
        var panel =
            new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                WrapContents = true,
                BackColor = Color.FromArgb(24, 29, 36),
                Padding = new Padding(8, 5, 8, 5)
            };

        foreach (var frequency in new long[]
                 {
                     136_725_000,
                     136_775_000,
                     136_875_000,
                     136_975_000
                 })
        {
            var card =
                new ChannelStatusCard(frequency);

            card.Selected +=
                TuneChannel;

            _channelCards[frequency] =
                card;

            panel.Controls.Add(card);
        }

        return panel;
    }

    private void BuildApplicationMenu(
        MenuStrip menu,
        Control commandBar,
        Control controlCenter,
        Control channelsPanel,
        SplitContainer mainSplit,
        SplitContainer eventSplit,
        TabControl dataViews)
    {
        var fileMenu =
            new ToolStripMenuItem("File");

        fileMenu.DropDownItems.Add(
            MenuAction(
                "Export JSONL…",
                (_, _) => _export.PerformClick()));

        fileMenu.DropDownItems.Add(
            MenuAction(
                "Open captures folder",
                (_, _) => _openCaptures.PerformClick()));

        fileMenu.DropDownItems.Add(
            MenuAction(
                "Open analysis folder",
                (_, _) => _openAnalysis.PerformClick()));
        fileMenu.DropDownItems.Add(
            MenuAction(
                "Open local history folder",
                (_, _) => ShowLocalHistoryFolder()));

        fileMenu.DropDownItems.Add(
            new ToolStripSeparator());

        fileMenu.DropDownItems.Add(
            MenuAction(
                "Clear live view",
                (_, _) => _clear.PerformClick()));
        fileMenu.DropDownItems.Add(
            MenuAction(
                "Clear local history database…",
                async (_, _) => await ClearLocalHistoryAsync()));

        var viewMenu =
            new ToolStripMenuItem("View");

        var showCommandBar =
            ToggleMenuItem(
                "Command bar",
                commandBar.Visible,
                value =>
                {
                    commandBar.Visible =
                        value;

                    _uiPreferences.CommandBarVisible =
                        value;

                    SaveUiPreferences();
                });

        var showControlCenter =
            ToggleMenuItem(
                "Control Center",
                controlCenter.Visible,
                value =>
                {
                    controlCenter.Visible =
                        value;

                    _uiPreferences.ControlCenterVisible =
                        value;

                    SaveUiPreferences();
                });

        var showChannels =
            ToggleMenuItem(
                "Channel monitor",
                channelsPanel.Visible,
                value =>
                {
                    channelsPanel.Visible =
                        value;

                    _uiPreferences.ChannelMonitorVisible =
                        value;

                    SaveUiPreferences();
                });

        var showWaterfall =
            ToggleMenuItem(
                "Waterfall",
                !mainSplit.Panel1Collapsed,
                value =>
                {
                    mainSplit.Panel1Collapsed =
                        !value;

                    _uiPreferences.WaterfallVisible =
                        value;

                    SaveUiPreferences();
                });

        var showDetails =
            ToggleMenuItem(
                "Aircraft / message details",
                !eventSplit.Panel2Collapsed,
                value =>
                {
                    eventSplit.Panel2Collapsed =
                        !value;

                    _uiPreferences.DetailsVisible =
                        value;

                    SaveUiPreferences();
                });

        viewMenu.DropDownItems.Add(
            MenuAction(
                "Operations board",
                (_, _) =>
                    dataViews.SelectedIndex =
                        0));

        viewMenu.DropDownItems.Add(
            MenuAction(
                "Verified messages",
                (_, _) =>
                    dataViews.SelectedIndex =
                        1));

        viewMenu.DropDownItems.Add(
            MenuAction(
                "Active aircraft sessions",
                (_, _) =>
                    dataViews.SelectedIndex =
                        2));

        viewMenu.DropDownItems.Add(
            MenuAction(
                "Local history database",
                (_, _) =>
                    dataViews.SelectedIndex =
                        3));
        viewMenu.DropDownItems.Add(
            new ToolStripSeparator());
        viewMenu.DropDownItems.Add(showCommandBar);
        viewMenu.DropDownItems.Add(showControlCenter);
        viewMenu.DropDownItems.Add(showChannels);
        viewMenu.DropDownItems.Add(showWaterfall);
        viewMenu.DropDownItems.Add(showDetails);

        var decoderMenu =
            new ToolStripMenuItem("Decoder");

        var autoDecode =
            ToggleMenuItem(
                "Automatic VDL2 decoding",
                _autoD8psk.Checked,
                value =>
                    _autoD8psk.Checked = value);

        var diagnostic =
            ToggleMenuItem(
                "Diagnostic limited captures",
                _diagnosticLimited.Checked,
                value =>
                    _diagnosticLimited.Checked = value);

        _autoD8psk.CheckedChanged += (_, _) =>
            autoDecode.Checked =
                _autoD8psk.Checked;

        _diagnosticLimited.CheckedChanged += (_, _) =>
            diagnostic.Checked =
                _diagnosticLimited.Checked;

        decoderMenu.DropDownItems.Add(autoDecode);
        decoderMenu.DropDownItems.Add(diagnostic);
        decoderMenu.DropDownItems.Add(
            new ToolStripSeparator());
        decoderMenu.DropDownItems.Add(
            MenuAction(
                "Analyze latest capture",
                (_, _) => _analyzeLatest.PerformClick()));
        decoderMenu.DropDownItems.Add(
            MenuAction(
                "Reset detector",
                (_, _) => _burstDetector.Reset()));

        var waterfallMenu =
            new ToolStripMenuItem("Waterfall");

        var pause =
            ToggleMenuItem(
                "Pause",
                _pauseWaterfall.Checked,
                value =>
                    _pauseWaterfall.Checked = value);

        _pauseWaterfall.CheckedChanged += (_, _) =>
            pause.Checked =
                _pauseWaterfall.Checked;

        waterfallMenu.DropDownItems.Add(pause);
        waterfallMenu.DropDownItems.Add(
            MenuAction(
                "Clear",
                (_, _) => _clearWaterfall.PerformClick()));
        waterfallMenu.DropDownItems.Add(
            new ToolStripSeparator());
        waterfallMenu.DropDownItems.Add(
            MenuAction(
                "Preset: Normal",
                (_, _) => ApplyWaterfallPreset(-100, -35, 100)));
        waterfallMenu.DropDownItems.Add(
            MenuAction(
                "Preset: Weak signals",
                (_, _) => ApplyWaterfallPreset(-115, -48, 140)));
        waterfallMenu.DropDownItems.Add(
            MenuAction(
                "Preset: Strong signals",
                (_, _) => ApplyWaterfallPreset(-90, -25, 85)));

        var aircraftMenu =
            new ToolStripMenuItem("Aircraft");

        aircraftMenu.DropDownItems.Add(
            MenuAction(
                "Show operations board",
                (_, _) =>
                    dataViews.SelectedIndex =
                        0));

        aircraftMenu.DropDownItems.Add(
            MenuAction(
                "Show active aircraft sessions",
                (_, _) =>
                    dataViews.SelectedIndex =
                        2));
        aircraftMenu.DropDownItems.Add(
            new ToolStripSeparator());

        aircraftMenu.DropDownItems.Add(
            MenuAction(
                "Open aircraft details",
                (_, _) => OpenSelectedAircraftOnline(
                    AircraftOnlineProvider.Planespotters)));
        aircraftMenu.DropDownItems.Add(
            MenuAction(
                "Open live map",
                (_, _) => OpenSelectedAircraftOnline(
                    AircraftOnlineProvider.AdsbExchange)));
        aircraftMenu.DropDownItems.Add(
            MenuAction(
                "Search current flight / route",
                (_, _) => OpenSelectedAircraftOnline(
                    AircraftOnlineProvider.FlightSearch)));
        aircraftMenu.DropDownItems.Add(
            MenuAction(
                "Copy ICAO24",
                (_, _) => CopySelectedAircraftIcao()));

        var historyMenu =
            new ToolStripMenuItem("History");

        historyMenu.DropDownItems.Add(
            MenuAction(
                "Show local history",
                (_, _) =>
                    dataViews.SelectedIndex =
                        3));
        historyMenu.DropDownItems.Add(
            MenuAction(
                "Refresh",
                (_, _) => Interlocked.Exchange(ref _historyRefreshRequested, 1)));
        historyMenu.DropDownItems.Add(
            MenuAction(
                "Open database folder",
                (_, _) => ShowLocalHistoryFolder()));
        historyMenu.DropDownItems.Add(
            MenuAction(
                "Database information",
                (_, _) => ShowLocalHistoryInformation()));
        historyMenu.DropDownItems.Add(new ToolStripSeparator());
        historyMenu.DropDownItems.Add(
            MenuAction(
                "Compact database",
                async (_, _) => await VacuumLocalHistoryAsync()));
        historyMenu.DropDownItems.Add(
            MenuAction(
                "Clear local history…",
                async (_, _) => await ClearLocalHistoryAsync()));

        var helpMenu =
            new ToolStripMenuItem("Help");

        helpMenu.DropDownItems.Add(
            MenuAction(
                "About / system information",
                (_, _) =>
                    ShowProductAboutDialog()));

        menu.Items.AddRange(
        [
            fileMenu,
            viewMenu,
            decoderMenu,
            waterfallMenu,
            aircraftMenu,
            historyMenu,
            helpMenu
        ]);
    }

    private void ApplyWaterfallPreset(
        decimal minimum,
        decimal maximum,
        decimal contrast)
    {
        _waterfallMin.Value =
            Math.Clamp(
                minimum,
                _waterfallMin.Minimum,
                _waterfallMin.Maximum);

        _waterfallMax.Value =
            Math.Clamp(
                maximum,
                _waterfallMax.Minimum,
                _waterfallMax.Maximum);

        _waterfallContrast.Value =
            Math.Clamp(
                contrast,
                _waterfallContrast.Minimum,
                _waterfallContrast.Maximum);
    }

    private static ToolStripMenuItem MenuAction(
        string text,
        EventHandler action)
    {
        var item =
            new ToolStripMenuItem(text);

        item.Click +=
            action;

        return item;
    }

    private static ToolStripMenuItem ToggleMenuItem(
        string text,
        bool initialValue,
        Action<bool> changed)
    {
        var item =
            new ToolStripMenuItem(text)
            {
                CheckOnClick = true,
                Checked = initialValue
            };

        item.CheckedChanged += (_, _) =>
            changed(item.Checked);

        return item;
    }

    private static FlowLayoutPanel CreateControlFlow() =>
        new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true,
            BackColor = Color.FromArgb(24, 29, 36),
            Padding = new Padding(8)
        };

    private static Label CreateControlLabel(
        string text) =>
        new()
        {
            Text = text + ":",
            AutoSize = true,
            ForeColor = Color.Silver,
            Padding = new Padding(8, 7, 2, 0)
        };

    private static Button CreateCommandButton(
        string text)
    {
        var button =
            new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(43, 51, 62),
                ForeColor = Color.WhiteSmoke,
                Padding = new Padding(5, 2, 5, 2),
                Margin = new Padding(3, 1, 3, 1)
            };

        button.FlatAppearance.BorderColor =
            Color.FromArgb(75, 88, 104);

        button.FlatAppearance.MouseOverBackColor =
            Color.FromArgb(54, 66, 80);

        return button;
    }

    private static TabPage CreateWorkspaceTab(
        string title,
        Control content)
    {
        var page =
            new TabPage(title)
            {
                BackColor = Color.FromArgb(24, 29, 36),
                ForeColor = Color.Gainsboro,
                Padding = new Padding(4)
            };

        content.Dock =
            DockStyle.Fill;

        page.Controls.Add(content);

        return page;
    }

    private void ApplyModernGridStyle()
    {
        _grid.BackgroundColor =
            Color.FromArgb(
                24,
                28,
                34);

        _grid.BorderStyle =
            BorderStyle.None;

        _grid.CellBorderStyle =
            DataGridViewCellBorderStyle.SingleHorizontal;

        _grid.GridColor =
            Color.FromArgb(
                55,
                62,
                72);

        _grid.EnableHeadersVisualStyles =
            false;

        _grid.ColumnHeadersDefaultCellStyle.BackColor =
            Color.FromArgb(
                42,
                48,
                57);

        _grid.ColumnHeadersDefaultCellStyle.ForeColor =
            Color.WhiteSmoke;

        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor =
            Color.FromArgb(
                42,
                48,
                57);

        _grid.ColumnHeadersDefaultCellStyle.Font =
            new Font(
                _grid.Font,
                FontStyle.Bold);

        _grid.ColumnHeadersHeight =
            30;

        _grid.DefaultCellStyle.BackColor =
            Color.FromArgb(
                28,
                33,
                40);

        _grid.DefaultCellStyle.ForeColor =
            Color.Gainsboro;

        _grid.DefaultCellStyle.SelectionBackColor =
            Color.FromArgb(
                38,
                92,
                132);

        _grid.DefaultCellStyle.SelectionForeColor =
            Color.White;

        _grid.AlternatingRowsDefaultCellStyle.BackColor =
            Color.FromArgb(
                32,
                38,
                46);

        _grid.RowTemplate.Height =
            26;
    }

    private void AddIcaoLinkColumn()
    {
        _grid.Columns.Add(
            new DataGridViewLinkColumn
            {
                Name = "ICAO",
                HeaderText = "ICAO",
                Width = 70,
                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.None,
                SortMode =
                    DataGridViewColumnSortMode.NotSortable,
                TrackVisitedState = false,
                UseColumnTextForLinkValue = false,
                ToolTipText =
                    "Click to open verified aircraft details. " +
                    "Right-click for live map and flight search."
            });
    }

    private void BuildAircraftOnlineMenu()
    {
        _aircraftOnlineMenu.Items.Clear();

        AddAircraftOnlineMenuItem(
            "Open aircraft details",
            AircraftOnlineProvider.Planespotters);

        AddAircraftOnlineMenuItem(
            "Open ADS-B Exchange live map",
            AircraftOnlineProvider.AdsbExchange);

        AddAircraftOnlineMenuItem(
            "Search current flight / route",
            AircraftOnlineProvider.FlightSearch);

        _aircraftOnlineMenu.Items.Add(
            new ToolStripSeparator());

        var copyItem =
            new ToolStripMenuItem(
                "Copy ICAO24");

        copyItem.Click += (_, _) =>
            CopySelectedAircraftIcao();

        _aircraftOnlineMenu.Items.Add(
            copyItem);
    }

    private void AddAircraftOnlineMenuItem(
        string text,
        AircraftOnlineProvider provider)
    {
        var item =
            new ToolStripMenuItem(
                text);

        item.Click += (_, _) =>
            OpenSelectedAircraftOnline(
                provider);

        _aircraftOnlineMenu.Items.Add(
            item);
    }

    private Vdl2Message? SelectedAircraftMessage()
    {
        if (_selectedContextMessage is not null)
        {
            return
                _selectedContextMessage;
        }

        if (_grid.SelectedRows.Count == 0)
            return null;

        return
            _grid.SelectedRows[0].Tag
            as Vdl2Message;
    }

    private void GridCellContentClick(
        object? sender,
        DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.ColumnIndex < 0 ||
            _grid.Columns[e.ColumnIndex].Name != "ICAO")
        {
            return;
        }

        _grid.Rows[e.RowIndex].Selected = true;

        OpenSelectedAircraftOnline(
            AircraftOnlineProvider.Planespotters);
    }

    private void GridCellDoubleClick(
        object? sender,
        DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0)
            return;

        _grid.Rows[e.RowIndex].Selected = true;

        OpenSelectedAircraftOnline(
            AircraftOnlineProvider.Planespotters);
    }

    private void GridCellMouseDown(
        object? sender,
        DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right ||
            e.RowIndex < 0)
        {
            return;
        }

        _grid.ClearSelection();
        _grid.Rows[e.RowIndex].Selected = true;

        if (!TryGetSelectedAircraftIcao(
            out _))
        {
            return;
        }

        _aircraftOnlineMenu.Show(
            _grid,
            e.Location);
    }

    private bool TryGetSelectedAircraftIcao(
        out string icao)
    {
        var message =
            SelectedAircraftMessage();

        return
            AircraftOnlineLookup.TryNormalizeIcao(
                message?.Icao,
                out icao);
    }

    private void OpenSelectedAircraftOnline(
        AircraftOnlineProvider provider)
    {
        if (!TryGetSelectedAircraftIcao(
            out var icao))
        {
            MessageBox.Show(
                this,
                "Select a valid AVLC aircraft row containing a six-character ICAO24 address.",
                "Aircraft online",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        try
        {
            var message =
                SelectedAircraftMessage();

            AircraftOnlineLookup.Open(
                provider,
                icao,
                message?.Registration,
                message?.Callsign);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Aircraft online",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void CopySelectedAircraftIcao()
    {
        if (!TryGetSelectedAircraftIcao(
            out var icao))
        {
            return;
        }

        try
        {
            Clipboard.SetText(
                icao);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Copy ICAO24",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void TuneChannel(long frequencyHz)
    {
        try
        {
            _captureManager.ResetForRetune();
            _control.Frequency = frequencyHz;
            Interlocked.Exchange(ref _centerFrequency, frequencyHz);
            _burstDetector.Reset();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Tune error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddColumn(string name, int width,
        DataGridViewAutoSizeColumnMode mode = DataGridViewAutoSizeColumnMode.None)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = name,
            Width = width,
            AutoSizeMode = mode,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private unsafe void OnIqBlock(Complex* buffer, double sampleRate, int length)
    {
        if (_shutdown || buffer is null || length <= 0)
            return;

        var iq = (float*)buffer;
        _metrics.Process(iq, length, sampleRate);
        _spectrumWaterfall.PushIq(buffer, length, sampleRate);

        _burstDetector.Process(iq, length, sampleRate, Interlocked.Read(ref _centerFrequency));

        var captureSnapshot = _burstDetector.Snapshot();
        _captureManager.PushIq(
            iq,
            length,
            sampleRate,
            Interlocked.Read(ref _centerFrequency),
            captureSnapshot);

        for (var n = 0; n < 32 && _burstDetector.TryReadJson(out var json); n++)
        {
            _stats.OnReceived();
            if (Vdl2JsonParser.TryParse(
                json,
                out var message,
                out _))
            {
                if (!VerifiedAircraftMessagePolicy.TryAccept(
                    message!,
                    out var verifiedMessage,
                    out _))
                {
                    _stats.OnInvalid();

                    Interlocked.Increment(
                        ref _filteredUnknownMessages);

                    continue;
                }

                if (_store.TryAdd(
                    verifiedMessage))
                {
                    _stats.OnAccepted();

                    _sessionStore.TryAdd(
                        verifiedMessage);
                    _historyDatabase.TryEnqueue(
                        verifiedMessage);

                    try
                    {
                        _exporter.Write(
                            verifiedMessage);
                    }
                    catch
                    {
                    }
                }
            }
            else
            {
                _stats.OnInvalid();
            }
        }
    }

    private void ControlPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName?.Contains(
            "Frequency",
            StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        var newFrequency = ReadFrequencySafely();
        var oldFrequency = Interlocked.Exchange(
            ref _centerFrequency,
            newFrequency);

        if (oldFrequency > 0 &&
            newFrequency > 0 &&
            oldFrequency != newFrequency)
        {
            _captureManager.ResetForRetune();
        }
    }

    private long ReadFrequencySafely()
    {
        try { return _control.Frequency; }
        catch { return 0; }
    }

    private void OnCaptureCompleted(
        CaptureInfo capture)
    {
        if (IsDisposed ||
            Disposing ||
            _shutdown)
        {
            return;
        }

        if (!_autoAnalyzeD8psk ||
            capture.Limited)
        {
            return;
        }

        var queued =
            _analysisScheduler.Enqueue(
                capture,
                automatic: true,
                diagnosticMode: false);

        if (!queued)
        {
            SetD8pskState(
                $"VDL2: capture {capture.Id} was not queued");
        }
    }

    private void AnalyzeLatestCapture()
    {
        var captures =
            _captureManager.Snapshot();

        if (captures.Count == 0)
        {
            MessageBox.Show(
                this,
                "No IQ capture is available in the current session.",
                "VDL2 analysis",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        var capture =
            captures[0];

        var queued =
            _analysisScheduler.Enqueue(
                capture,
                automatic: false,
                diagnosticMode:
                    _diagnosticLimited.Checked);

        if (!queued)
        {
            MessageBox.Show(
                this,
                "The capture is already queued or the bounded analysis queue is full.",
                "VDL2 analysis",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private void OnAnalysisStarted(
        Vdl2AnalysisRequest request)
    {
        SetD8pskState(
            request.Automatic
                ? $"VDL2: processing {request.Capture.Id}…"
                : $"VDL2: manual analysis {request.Capture.Id}…");
    }

    private void OnAnalysisDropped(
        Vdl2AnalysisRequest request,
        string reason)
    {
        _stats.OnDropped();

        SetD8pskState(
            $"VDL2: dropped {request.Capture.Id} · {reason}");
    }

    private void OnAnalysisCompleted(
        Vdl2AnalysisCompletion completion)
    {
        var result =
            completion.Result;

        _latestD8pskResult =
            result;

        Interlocked.Add(
            ref _invalidFcsFrames,
            result.Frame?.Payload?.FcsInvalidFrames ??
            0);

        PublishAvlcMessages(
            completion.EffectiveCapture,
            result,
            completion.Salvaged);

        var pending =
            BuildAnalysisUi(
                completion);

        lock (_pendingUiGate)
        {
            _pendingAnalysisUi =
                pending;
        }

        Interlocked.Exchange(
            ref _gridRefreshRequested,
            1);
    }

    private void OnAnalysisBatchCompleted(
        Vdl2AnalysisBatchSummary summary)
    {
        if (summary.Status ==
            "CONTINUOUS-CAPTURE-WITH-VALID-AVLC")
        {
            SetDecoderState(
                $"Decoder: salvaged {summary.ValidAvlcFrames} AVLC · " +
                $"aircraft {summary.PublishedAircraftCandidates}");
        }
        else if (summary.ValidAvlcFrames > 0)
        {
            SetDecoderState(
                $"Decoder: AVLC {summary.ValidAvlcFrames} valid · " +
                $"aircraft {summary.PublishedAircraftCandidates}");
        }

        SetD8pskState(
            $"VDL2: {summary.Status} · " +
            $"bursts {summary.AnalysedBursts}/{summary.BoundedBursts} · " +
            $"FCS V:{summary.ValidAvlcFrames} F:{summary.InvalidFcsFrames}");
    }

    private PendingAnalysisUi BuildAnalysisUi(
        Vdl2AnalysisCompletion completion)
    {
        var result =
            completion.Result;

        if (!result.Success)
        {
            return new PendingAnalysisUi(
                "VDL2: analysis failed",
                result.Error ??
                    "Unknown VDL2 analysis error.");
        }

        var frame =
            result.Frame;

        var prefix =
            completion.Salvaged
                ? $"Salvaged burst {completion.BurstIndex}/{completion.BurstCount} · "
                : string.Empty;

        var state =
            (frame?.Payload?.FcsValidFrames ?? 0) > 0
                ? $"VDL2: {prefix}AVLC VALID · " +
                  $"{frame!.Payload!.FcsValidFrames} frame(s) · " +
                  $"aircraft {frame.Payload.Frames.Count(item => item.Icao.Length == 6)}"
                : frame?.HeaderValid == true
                    ? $"VDL2: {prefix}{frame.Status} · " +
                      $"{frame.TransmissionLengthBits} bits · " +
                      $"preamble {frame.PreambleRmsDeg:0.0}°"
                    : frame?.PreambleFound == true
                        ? $"VDL2: {prefix}{frame.Status} · " +
                          $"preamble {frame.PreambleRmsDeg:0.0}° · " +
                          $"corr {frame.PreambleCorrelation:0.000}"
                        : result.D8pskCandidate
                            ? $"D8PSK: {prefix}VDL2-SYMBOL-CANDIDATE · " +
                              $"R8 {result.R8:0.000}"
                            : $"D8PSK: {prefix}{result.Status} · " +
                              $"R8 {result.R8:0.000} · " +
                              $"timing Z {result.TimingRobustZ:0.0}";

        var details =
            new System.Text.StringBuilder(
                2048);

        details
            .Append("Source capture: ")
            .AppendLine(
                completion.Request.Capture.Id)
            .Append("Analysed capture: ")
            .AppendLine(
                completion.EffectiveCapture.Id)
            .Append("Salvaged: ")
            .AppendLine(
                completion.Salvaged.ToString())
            .Append("Burst: ")
            .Append(completion.BurstIndex)
            .Append(" / ")
            .AppendLine(
                completion.BurstCount.ToString())
            .Append("Capture: ")
            .AppendLine(
                result.CaptureId)
            .Append("Status: ")
            .AppendLine(
                result.Status)
            .Append("D8PSK candidate: ")
            .AppendLine(
                result.D8pskCandidate.ToString())
            .AppendLine()
            .Append("Symbol rate: ")
            .Append(
                result.SymbolRate.ToString("0"))
            .AppendLine(" sym/s")
            .Append("Samples/symbol: ")
            .Append(
                result.SamplesPerSymbol.ToString("0.000"))
            .AppendLine()
            .Append("Best burst start: ")
            .Append(
                result.BestBurstStartMs.ToString("0.000"))
            .AppendLine(" ms")
            .Append("Best burst duration: ")
            .Append(
                result.BestBurstDurationMs.ToString("0.000"))
            .AppendLine(" ms")
            .Append("Estimated SNR: ")
            .Append(
                result.EstimatedSnrDb.ToString("0.00"))
            .AppendLine(" dB")
            .Append("Frequency offset: ")
            .Append(
                result.EstimatedFrequencyOffsetHz.ToString(
                    "+0.0;-0.0;0.0"))
            .AppendLine(" Hz")
            .Append("Timing offset: ")
            .Append(
                result.TimingOffsetSamples.ToString("0.000"))
            .AppendLine(" samples")
            .Append("Symbols: ")
            .AppendLine(
                result.SymbolCount.ToString())
            .Append("Differential phase RMS: ")
            .Append(
                result.DifferentialPhaseRmsDeg.ToString("0.00"))
            .AppendLine("°")
            .Append("Legacy cluster score: ")
            .Append(
                (result.ClusterScore * 100.0).ToString("0.0"))
            .AppendLine("%")
            .Append("R8: ")
            .AppendLine(
                result.R8.ToString("0.0000"))
            .Append("R8 statistical threshold: ")
            .AppendLine(
                result.R8Threshold.ToString("0.0000"))
            .Append("R8 corrected p-value: ")
            .AppendLine(
                result.R8CorrectedPValue.ToString("0.000E+0"))
            .Append("Timing median R8: ")
            .AppendLine(
                result.TimingMedianR8.ToString("0.0000"))
            .Append("Timing contrast: ")
            .AppendLine(
                result.TimingContrast.ToString("0.0000"))
            .Append("Timing robust Z: ")
            .AppendLine(
                result.TimingRobustZ.ToString("0.00"))
            .Append("Diagnostic only: ")
            .AppendLine(
                result.DiagnosticOnly.ToString())
            .Append("Amplitude CV: ")
            .AppendLine(
                result.AmplitudeCv.ToString("0.000"))
            .AppendLine()
            .AppendLine("VDL2 FRAME SYNC")
            .Append("Preamble found: ")
            .AppendLine(
                (frame?.PreambleFound ?? false).ToString())
            .Append("Preamble symbol index: ")
            .AppendLine(
                (frame?.PreambleSymbolIndex ?? -1).ToString())
            .Append("Preamble RMS: ")
            .Append(
                (frame?.PreambleRmsDeg ?? 0).ToString("0.00"))
            .AppendLine("°")
            .Append("Preamble correlation: ")
            .AppendLine(
                (frame?.PreambleCorrelation ?? 0).ToString("0.0000"))
            .Append("Residual frequency offset: ")
            .Append(
                (frame?.ResidualFrequencyOffsetHz ?? 0).ToString(
                    "+0.0;-0.0;0.0"))
            .AppendLine(" Hz")
            .AppendLine()
            .AppendLine("PHYSICAL HEADER")
            .Append("Header available: ")
            .AppendLine(
                (frame?.HeaderAvailable ?? false).ToString())
            .Append("Header FEC valid: ")
            .AppendLine(
                (frame?.HeaderFecValid ?? false).ToString())
            .Append("Header corrected: ")
            .AppendLine(
                (frame?.HeaderCorrected ?? false).ToString())
            .Append("Syndrome before/after: ")
            .Append(
                (frame?.HeaderSyndromeBefore ?? 0).ToString())
            .Append(" / ")
            .AppendLine(
                (frame?.HeaderSyndromeAfter ?? 0).ToString())
            .Append("Transmission length: ")
            .Append(
                (frame?.TransmissionLengthBits ?? 0).ToString())
            .Append(" bits / ")
            .Append(
                (frame?.TransmissionLengthOctets ?? 0).ToString())
            .AppendLine(" octets")
            .Append("Header hex: ")
            .AppendLine(
                frame?.HeaderHex ??
                string.Empty)
            .Append("Header valid: ")
            .AppendLine(
                (frame?.HeaderValid ?? false).ToString())
            .Append("Frame status: ")
            .AppendLine(
                frame?.Status ??
                "not_run")
            .AppendLine()
            .AppendLine("PAYLOAD / AVLC")
            .Append("Payload status: ")
            .AppendLine(
                frame?.Payload?.Status ??
                "not_attempted")
            .Append("Data/FEC octets: ")
            .Append(
                (frame?.Payload?.DataOctets ?? 0).ToString())
            .Append(" / ")
            .AppendLine(
                (frame?.Payload?.FecOctets ?? 0).ToString())
            .Append("RS valid: ")
            .AppendLine(
                (frame?.Payload?.ReedSolomonValid ?? false).ToString())
            .Append("Corrected data symbols: ")
            .AppendLine(
                (frame?.Payload?.CorrectedSymbols ?? 0).ToString())
            .Append("HDLC frames: ")
            .AppendLine(
                (frame?.Payload?.HdlcFrames ?? 0).ToString())
            .Append("FCS valid / invalid: ")
            .Append(
                (frame?.Payload?.FcsValidFrames ?? 0).ToString())
            .Append(" / ")
            .AppendLine(
                (frame?.Payload?.FcsInvalidFrames ?? 0).ToString())
            .Append("Published aircraft frames: ")
            .AppendLine(
                (frame?.Payload?.Frames.Length ?? 0).ToString())
            .AppendLine()
            .AppendLine("Hard-bit preview:")
            .AppendLine(
                result.BitPreview)
            .AppendLine()
            .AppendLine("Report:")
            .AppendLine(
                result.ReportPath)
            .AppendLine()
            .AppendLine("Symbols CSV:")
            .AppendLine(
                result.SymbolsCsvPath)
            .AppendLine()
            .Append(
                "Continuous captures and multi-burst captures are split into " +
                "bounded child captures. Only Reed-Solomon-valid, HDLC-valid " +
                "and FCS-valid AVLC frames are published.");

        return new PendingAnalysisUi(
            state,
            details.ToString());
    }

    private void PublishAvlcMessages(
        CaptureInfo capture,
        D8pskAnalysisResult result,
        bool salvaged)
    {
        var frames =
            result.Frame?.Payload?.Frames;

        if (frames is null ||
            frames.Length == 0 ||
            result.DiagnosticOnly)
        {
            return;
        }

        var published = 0;
        var aircraft = 0;

        foreach (var frame in frames)
        {
            if (!frame.FcsValid)
                continue;

            _stats.OnReceived();

            var rawJson =
                JsonSerializer.Serialize(
                    new
                    {
                        source = "AircraftDataEnhanced",
                        stage = "vdl2_payload_avlc",
                        capture_id = capture.Id,
                        capture.CreatedAt,
                        capture.FrequencyHz,
                        capture.SampleRate,
                        capture.QualityScore,
                        salvaged,
                        frame
                    });

            var acars =
                frame.Acars;

            var message =
                new Vdl2Message(
                    DateTimeOffset.Now,
                    acars is not null
                        ? "ACARS"
                        : "AVLC",
                    frame.Direction,
                    frame.Icao,
                    acars?.Registration ??
                        string.Empty,
                    acars?.FlightId ??
                        string.Empty,
                    frame.Source,
                    frame.Destination,
                    acars?.Label ??
                        frame.Label,
                    acars?.Text.Length > 0
                        ? acars.Text
                        : frame.Text,
                    capture.FrequencyHz /
                        1_000_000.0,
                    null,
                    true,
                    rawJson,
                    acars?.Mode ??
                        string.Empty,
                    acars?.BlockId ??
                        string.Empty,
                    acars?.MessageNumber ??
                        string.Empty,
                    acars?.MessageSequence ??
                        string.Empty,
                    acars?.Acknowledgement ??
                        string.Empty,
                    acars?.CrcValid,
                    acars?.MoreBlocks,
                    acars?.Sublabel ??
                        string.Empty,
                    acars?.MessageFunction ??
                        string.Empty);

            if (!VerifiedAircraftMessagePolicy.TryAccept(
                message,
                out var verifiedMessage,
                out _))
            {
                Interlocked.Increment(
                    ref _filteredUnknownMessages);

                continue;
            }

            if (!_store.TryAdd(
                verifiedMessage))
            {
                continue;
            }

            _sessionStore.TryAdd(
                verifiedMessage);
            _historyDatabase.TryEnqueue(
                verifiedMessage);

            _stats.OnAccepted();
            published++;

            Interlocked.Increment(
                ref _validAvlcFrames);

            if (salvaged)
            {
                Interlocked.Increment(
                    ref _salvagedAvlcFrames);
            }

            if (!string.IsNullOrWhiteSpace(
                frame.Icao))
            {
                aircraft++;
            }

            try
            {
                _exporter.Write(verifiedMessage);
            }
            catch
            {
            }
        }

        if (published > 0)
        {
            SetDecoderState(
                salvaged
                    ? $"Decoder: salvaged AVLC {published} · aircraft {aircraft}"
                    : $"Decoder: AVLC {published} valid · aircraft {aircraft}");
        }
    }

    private void SetDecoderState(
        string text)
    {
        if (_shutdown)
            return;

        lock (_pendingUiGate)
        {
            _pendingDecoderState =
                text;
        }
    }

    private void SetD8pskState(
        string text)
    {
        if (_shutdown)
            return;

        lock (_pendingUiGate)
        {
            _pendingD8pskState =
                text;
        }
    }

    private void ApplyPendingUi()
    {
        PendingAnalysisUi? analysis;
        string? decoderState;
        string? d8pskState;

        lock (_pendingUiGate)
        {
            analysis =
                _pendingAnalysisUi;

            _pendingAnalysisUi =
                null;

            decoderState =
                _pendingDecoderState;

            _pendingDecoderState =
                null;

            d8pskState =
                _pendingD8pskState;

            _pendingD8pskState =
                null;
        }

        if (analysis is not null)
        {
            _d8pskState.Text =
                analysis.State;

            _d8pskDetails.Text =
                analysis.Details;
        }

        if (decoderState is not null)
        {
            _decoderState.Text =
                decoderState;
        }

        if (d8pskState is not null)
        {
            _d8pskState.Text =
                d8pskState;
        }
    }

    private void OpenAnalysisFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName =
                        _d8pskAnalyzer.AnalysisDirectory,
                    UseShellExecute = true
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Open analysis",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void RefreshUi()
    {
        ApplyPendingUi();

        var snapshot = _metrics.Snapshot();
        var age = snapshot.LastBlockAt == default
            ? TimeSpan.MaxValue
            : DateTimeOffset.Now - snapshot.LastBlockAt;

        _iqState.Text = age < TimeSpan.FromSeconds(2)
            ? $"IQ: ACTIVE · {snapshot.SampleRate / 1_000_000.0:0.000} MS/s · {Interlocked.Read(ref _centerFrequency) / 1_000_000.0:0.000000} MHz"
            : "IQ: NO DATA — start the SDR source and press Play";

        _metricsLabel.Text =
            $"RMS {snapshot.RmsDbfs:0.0} dBFS | Peak {snapshot.PeakDbfs:0.0} dBFS | " +
            $"DC I {snapshot.DcI:+0.0000;-0.0000;0.0000} Q {snapshot.DcQ:+0.0000;-0.0000;0.0000} | " +
            $"Clipped {snapshot.ClippedSamples} | Energy bursts {snapshot.BurstCount}";

        var detector = _burstDetector.Snapshot();

        if (!detector.NoiseReady)
        {
            _burstState.Text =
                $"Noise learning: {detector.WarmupRemainingMs:0} ms remaining · current {detector.CurrentDb:0.0} dBFS";
        }
        else
        {
            _burstState.Text = detector.Active
                ? $"Burst: ACTIVE · {detector.Classification} · " +
                  $"{detector.ActiveDurationMs:0} ms · " +
                  $"margin {detector.MarginDb:0.0} dB · " +
                  $"noise rise {detector.ActiveNoiseRiseDb:0.0} dB" +
                  (detector.ActiveNoiseTracking
                      ? " · adaptive tracking"
                      : string.Empty)
                : $"Burst: IDLE · noise {detector.NoiseDb:0.0} dBFS · " +
                  $"margin {detector.MarginDb:0.0} dB";
        }

        _spectralState.Text =
            $"Spectral: DC {detector.DcRatio * 100.0:0.0}% | phase σ {detector.PhaseActivity:0.000} rad | " +
            $"amplitude CV {detector.AmplitudeVariation:0.000}";

        _messageStats.Text =
            $"Events RX {_stats.Received} | accepted {_stats.Accepted} | " +
            $"duplicates {_stats.Duplicates} | invalid {_stats.Invalid} | " +
            $"dropped {_stats.Dropped} | AVLC V:{Interlocked.Read(ref _validAvlcFrames)} " +
            $"F:{Interlocked.Read(ref _invalidFcsFrames)} " +
            $"S:{Interlocked.Read(ref _salvagedAvlcFrames)} " +
            $"U:{Interlocked.Read(ref _filteredUnknownMessages)} | " +
            $"completed {detector.CompletedEvents} | forced {detector.ForcedClosures} | " +
            $"rejected {detector.RejectedEvents}";

        var captureState = _captureManager.StatusSnapshot();
        _captureStatus.Text =
            $"IQ A:{captureState.AcceptedCaptures} " +
            $"R:{captureState.RejectedCaptures} " +
            $"L:{captureState.LimitedCaptures} " +
            $"C:{captureState.ContinuousCaptures} " +
            $"WQ:{captureState.PendingWrites} " +
            $"WD:{captureState.DroppedWrites} · " +
            (captureState.Capturing
                ? $"REC {captureState.CurrentDurationMs:0} ms"
                : captureState.State);

        var analysisState =
            _analysisScheduler.Snapshot();

        _pipelineState.Text =
            $"Pipeline: {(analysisState.Busy ? "BUSY" : "idle")} · " +
            $"queue {analysisState.Pending}/8 · " +
            $"done {analysisState.Completed} · " +
            $"drop {analysisState.Dropped} · " +
            $"salvage {analysisState.SalvageBatches} · " +
            $"WF rows {_spectrumWaterfall.RowCount} " +
            $"drop {_spectrumWaterfall.DroppedFrames}" +
            (analysisState.Busy
                ? $" · {analysisState.ActiveCaptureId}"
                : string.Empty);

        _compactIqStatus.Text =
            age < TimeSpan.FromSeconds(2)
                ? $"IQ ACTIVE · {snapshot.SampleRate / 1_000.0:0.0} kS/s · " +
                  $"{Interlocked.Read(ref _centerFrequency) / 1_000_000.0:0.000000} MHz"
                : "IQ NO DATA";

        _compactDecoderStatus.Text =
            detector.Active
                ? $"Burst {detector.Classification} · {detector.MarginDb:0.0} dB"
                : $"Decoder idle · noise {detector.NoiseDb:0.0} dBFS";

        _compactMessageStatus.Text =
            $"Verified {Interlocked.Read(ref _validAvlcFrames)} · " +
            $"unknown hidden {Interlocked.Read(ref _filteredUnknownMessages)} · " +
            $"FCS fail {Interlocked.Read(ref _invalidFcsFrames)}";

        var activeAircraft =
            _sessionStore.ActiveCount(
                TimeSpan.FromMinutes(15));

        _compactAircraftStatus.Text =
            $"Aircraft {activeAircraft} active · " +
            $"{_sessionStore.TotalCount} retained";

        var databaseState =
            _historyDatabase.StatusSnapshot();

        _databaseStatus.Text = databaseState.Ready
            ? $"Local DB: {databaseState.StoredMessages} messages · " +
              $"{databaseState.StoredAircraft} aircraft · " +
              $"{FormatFileSize(databaseState.FileBytes)} · queue {databaseState.PendingWrites}"
            : databaseState.Faulted
                ? $"Local DB error: {databaseState.LastError}"
                : $"Local DB: {databaseState.State}";

        _compactDatabaseStatus.Text = databaseState.Ready
            ? $"DB {databaseState.StoredMessages} msg · {databaseState.StoredAircraft} ac"
            : databaseState.Faulted
                ? "DB ERROR"
                : "DB starting";

        _compactPipelineStatus.Text =
            analysisState.Busy
                ? $"Pipeline BUSY · queue {analysisState.Pending}/8 · {analysisState.ActiveCaptureId}"
                : $"Pipeline idle · queue {analysisState.Pending}/8 · done {analysisState.Completed}";

        _operationsBoard.UpdateStatus(
            new OperationsBoardStatus(
                DateTimeOffset.Now,
                DateTimeOffset.UtcNow,
                age <
                    TimeSpan.FromSeconds(
                        2)
                    ? "ONLINE"
                    : "NO DATA",
                age <
                    TimeSpan.FromSeconds(
                        2)
                    ? $"{snapshot.SampleRate / 1_000.0:0.0} kS/s · " +
                      $"{Interlocked.Read(ref _centerFrequency) / 1_000_000.0:0.000000} MHz"
                    : "Start the SDR source and press Play",
                detector.Active
                    ? "BURST"
                    : detector.NoiseReady
                        ? "MONITOR"
                        : "LEARNING",
                detector.NoiseReady
                    ? $"noise {detector.NoiseDb:0.0} dBFS · margin {detector.MarginDb:0.0} dB"
                    : $"{detector.WarmupRemainingMs:0} ms remaining",
                $"{activeAircraft} ACTIVE",
                $"{Interlocked.Read(ref _validAvlcFrames)} verified frames · " +
                $"{_sessionStore.TotalCount} retained",
                databaseState.Ready
                    ? "READY"
                    : databaseState.Faulted
                        ? "ERROR"
                        : "STARTING",
                databaseState.Ready
                    ? $"{databaseState.StoredMessages} messages · " +
                      $"{databaseState.StoredAircraft} aircraft · " +
                      $"{FormatFileSize(databaseState.FileBytes)}"
                    : databaseState.LastError.Length >
                        0
                        ? databaseState.LastError
                        : databaseState.State,
                analysisState.Busy
                    ? "BUSY"
                    : "IDLE",
                $"queue {analysisState.Pending}/8 · " +
                $"done {analysisState.Completed} · " +
                $"drop {analysisState.Dropped}"));

        var activeFrequency = Interlocked.Read(ref _centerFrequency);
        foreach (var pair in _channelCards)
        {
            var active = Math.Abs(pair.Key - activeFrequency) <= 1000;
            pair.Value.UpdateState(
                active,
                detector.Active ? detector.Classification : "Monitoring",
                snapshot.RmsDbfs);
        }

        RefreshGridIfNeeded();
        RefreshSessionsIfNeeded();
        RefreshHistoryIfNeeded();
    }

    private void UpdateSelectedEventDetails()
    {
        if (_operationsBoardViewActive ||
            _sessionViewActive ||
            _historyViewActive)
        {
            return;
        }

        if (_grid.SelectedRows.Count == 0 ||
            _grid.SelectedRows[0].Tag is not Vdl2Message message)
        {
            DisplayMessageDetails(
                null);

            return;
        }

        DisplayMessageDetails(
            message);
    }

    private void ClearAircraftSessionSelection()
    {
        if (!_sessionViewActive &&
            !_operationsBoardViewActive)
        {
            return;
        }

        _sessionDetails.Text =
            "No aircraft session matches the current activity window and filter.";

        DisplayMessageDetails(
            null);
    }

    private void ShowAircraftSession(
        AircraftSessionSnapshot session)
    {
        if (!_sessionViewActive &&
            !_operationsBoardViewActive)
        {
            return;
        }

        _sessionDetails.Text =
            BuildSessionDetails(
                session);

        DisplayMessageDetails(
            session.LatestMessage);
    }

    private void ShowSessionMessage(
        Vdl2Message message)
    {
        if (!_sessionViewActive)
            return;

        DisplayMessageDetails(
            message);
    }

    private void DisplayMessageDetails(
        Vdl2Message? message)
    {
        _selectedContextMessage =
            message;

        if (message is null)
        {
            _openAircraftOnline.Enabled =
                false;

            _aircraftDashboard.SetMessage(
                null);

            _details.Text =
                "Select a message to inspect its details.";

            return;
        }

        _openAircraftOnline.Enabled =
            AircraftOnlineLookup.TryNormalizeIcao(
                message.Icao,
                out _);

        _aircraftDashboard.SetMessage(
            message);

        var acarsCrc =
            message.AcarsCrcValid.HasValue
                ? message.AcarsCrcValid.Value
                    ? "valid"
                    : "warning"
                : "not available";

        var moreBlocks =
            message.AcarsMoreBlocks.HasValue
                ? message.AcarsMoreBlocks.Value
                    ? "yes"
                    : "no"
                : "not available";

        _details.Text =
            $"Received: {message.ReceivedAt:yyyy-MM-dd HH:mm:ss.fff}\r\n" +
            $"Protocol: {message.Protocol}\r\n" +
            $"Direction: {message.Direction}\r\n" +
            $"Frequency: {message.FrequencyMhz:0.000000} MHz\r\n" +
            $"ICAO24: {message.Icao}\r\n" +
            $"Registration: {message.Registration}\r\n" +
            $"Flight / callsign: {message.Callsign}\r\n" +
            $"Source: {message.Source}\r\n" +
            $"Destination: {message.Destination}\r\n\r\n" +
            "ACARS envelope:\r\n" +
            $"Mode: {message.AcarsMode}\r\n" +
            $"Label: {message.Label}\r\n" +
            $"Block ID: {message.AcarsBlockId}\r\n" +
            $"Message: {message.AcarsMessageId}\r\n" +
            $"Acknowledgement: {message.AcarsAcknowledgement}\r\n" +
            $"More blocks: {moreBlocks}\r\n" +
            $"Inner ACARS CRC: {acarsCrc}\r\n" +
            $"Sublabel: {message.AcarsSublabel}\r\n" +
            $"MFI: {message.AcarsMessageFunction}\r\n\r\n" +
            "Online lookup:\r\n" +
            "- Click ICAO24 for aircraft identity and registration.\r\n" +
            "- Right-click for live map or current-flight search.\r\n" +
            "- Route matching is stronger when the ACARS flight ID is present.\r\n\r\n" +
            $"Message text:\r\n{message.Text}\r\n\r\n" +
            $"Raw JSON:\r\n{message.RawJson}";
    }

    private static string BuildSessionDetails(
        AircraftSessionSnapshot session)
    {
        var age =
            session.Age(
                DateTimeOffset.Now);

        var labels =
            string.Join(
                ", ",
                session.RecentMessages
                    .Select(
                        message =>
                            message.Label)
                    .Where(
                        label =>
                            !string.IsNullOrWhiteSpace(
                                label))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Take(12));

        var flights =
            string.Join(
                ", ",
                session.RecentMessages
                    .Select(
                        message =>
                            message.Callsign)
                    .Where(
                        callsign =>
                            !string.IsNullOrWhiteSpace(
                                callsign))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Take(8));

        return
            $"ICAO24: {session.Icao}\r\n" +
            $"Registration: {session.Registration}\r\n" +
            $"Current / recent flight: {session.Callsign}\r\n" +
            $"Flights seen: {flights}\r\n\r\n" +
            $"First seen: {session.FirstSeen.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n" +
            $"Last seen: {session.LastSeen.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n" +
            $"Session duration: {FormatSessionDuration(session.Duration)}\r\n" +
            $"Age: {FormatSessionDuration(age)}\r\n" +
            $"Messages: {session.MessageCount}\r\n" +
            $"Recent history retained: {session.RecentMessages.Count}\r\n\r\n" +
            $"Last protocol: {session.LastProtocol}\r\n" +
            $"Last direction: {session.LastDirection}\r\n" +
            $"Ground station: {session.LastGroundStation}\r\n" +
            $"Last label: {session.LastLabel}\r\n" +
            $"Last message ID: {session.LastMessageId}\r\n" +
            $"Labels seen: {labels}\r\n" +
            $"Last frequency: {session.LastFrequencyMhz:0.000000} MHz\r\n" +
            $"Best signal: {session.BestSignalDb:0.0} dB\r\n\r\n" +
            $"Latest text:\r\n{session.LastText}";
    }

    private static string FormatSessionDuration(
        TimeSpan duration)
    {
        if (duration <
            TimeSpan.FromMinutes(1))
        {
            return
                $"{Math.Max(0, (int)duration.TotalSeconds)} seconds";
        }

        if (duration <
            TimeSpan.FromHours(1))
        {
            return
                $"{(int)duration.TotalMinutes} minutes " +
                $"{duration.Seconds} seconds";
        }

        return
            $"{(int)duration.TotalHours} hours " +
            $"{duration.Minutes} minutes";
    }



private void ApplyStoredControlPreferences()
{
    _waterfallMin.Value =
        Math.Clamp(
            _uiPreferences.WaterfallMinimumDb,
            _waterfallMin.Minimum,
            _waterfallMin.Maximum);

    _waterfallMax.Value =
        Math.Clamp(
            _uiPreferences.WaterfallMaximumDb,
            _waterfallMax.Minimum,
            _waterfallMax.Maximum);

    _waterfallContrast.Value =
        Math.Clamp(
            _uiPreferences.WaterfallContrastPercent,
            _waterfallContrast.Minimum,
            _waterfallContrast.Maximum);

    _operationsBoard.SelectedWindowIndex =
        _uiPreferences.OperationsWindowIndex;
}

private void SaveUiPreferences()
{
    _uiPreferences.OperationsWindowIndex =
        _operationsBoard.SelectedWindowIndex;

    _uiPreferences.WaterfallMinimumDb =
        _waterfallMin.Value;

    _uiPreferences.WaterfallMaximumDb =
        _waterfallMax.Value;

    _uiPreferences.WaterfallContrastPercent =
        _waterfallContrast.Value;

    UiPreferencesStore.Save(
        _uiPreferences);
}

private void ShowProductAboutDialog()
{
    ProductAboutDialog.ShowProductDialog(
        this,
        _historyDatabase.StatusSnapshot());
}

    private void ClearHistorySelection()
    {
        if (!_historyViewActive)
            return;

        _sessionDetails.Text =
            "No stored aircraft or message matches the current history filter.";
        DisplayMessageDetails(null);
    }

    private void ShowHistoricalAircraft(HistoricalAircraftSnapshot aircraft)
    {
        if (!_historyViewActive)
            return;

        _sessionDetails.Text = BuildHistoricalAircraftDetails(aircraft);
        DisplayMessageDetails(aircraft.LatestMessage);
    }

    private void ShowHistoricalMessage(Vdl2Message message)
    {
        if (!_historyViewActive)
            return;

        _sessionDetails.Text =
            "Stored message\r\n" +
            "--------------\r\n" +
            $"Database: {_historyDatabase.DatabasePath}\r\n" +
            $"Received: {message.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}\r\n" +
            $"ICAO24: {message.Icao}\r\n" +
            $"Registration: {message.Registration}\r\n" +
            $"Flight: {message.Callsign}\r\n" +
            $"Protocol: {message.Protocol}\r\n" +
            $"Label: {message.Label}\r\n" +
            $"Message ID: {message.AcarsMessageId}\r\n";

        DisplayMessageDetails(message);
    }

    private static string BuildHistoricalAircraftDetails(
        HistoricalAircraftSnapshot aircraft)
    {
        return
            "Persisted aircraft history\r\n" +
            "----------------------------\r\n" +
            $"ICAO24: {aircraft.Icao}\r\n" +
            $"Registration: {aircraft.Registration}\r\n" +
            $"Latest flight: {aircraft.Callsign}\r\n" +
            $"First stored: {aircraft.FirstSeen.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n" +
            $"Last stored: {aircraft.LastSeen.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n" +
            $"Observed duration: {FormatSessionDuration(aircraft.Duration)}\r\n" +
            $"Stored messages: {aircraft.MessageCount}\r\n" +
            $"Last protocol: {aircraft.LastProtocol}\r\n" +
            $"Last direction: {aircraft.LastDirection}\r\n" +
            $"Ground station: {aircraft.LastGroundStation}\r\n" +
            $"Last label: {aircraft.LastLabel}\r\n" +
            $"Last message ID: {aircraft.LastMessageId}\r\n" +
            $"Last frequency: {aircraft.LastFrequencyMhz:0.000000} MHz\r\n" +
            $"Best signal: {aircraft.BestSignalDb:0.0} dB\r\n\r\n" +
            $"Latest text:\r\n{aircraft.LastText}";
    }

    private async void RefreshHistoryIfNeeded()
    {
        if (!_historyViewActive)
            return;

        var version = _historyDatabase.Version;
        var nowTicks = Environment.TickCount64;
        var ageRefreshDue = nowTicks -
            Interlocked.Read(ref _lastHistoryUiRefreshTicks) >= 5000;
        var forced = Interlocked.Exchange(ref _historyRefreshRequested, 0) != 0 ||
            ageRefreshDue;

        if (!forced && version == Interlocked.Read(ref _lastHistoryVersion))
            return;

        if (Interlocked.Exchange(ref _historyRefreshInProgress, 1) != 0)
            return;

        try
        {
            await _historyControl.RefreshAsync();
            Interlocked.Exchange(ref _lastHistoryVersion, version);
            Interlocked.Exchange(ref _lastHistoryUiRefreshTicks, nowTicks);
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _historyRefreshInProgress, 0);
        }
    }

    private void ShowLocalHistoryFolder()
    {
        _historyControl.OpenDatabaseFolder();
    }

    private void ShowLocalHistoryInformation()
    {
        var status = _historyDatabase.StatusSnapshot();
        MessageBox.Show(
            this,
            $"State: {status.State}\r\n" +
            $"Database: {status.DatabasePath}\r\n" +
            $"Messages: {status.StoredMessages}\r\n" +
            $"Aircraft: {status.StoredAircraft}\r\n" +
            $"File size: {FormatFileSize(status.FileBytes)}\r\n" +
            $"Pending writes: {status.PendingWrites}\r\n" +
            $"Dropped writes: {status.DroppedWrites}\r\n" +
            $"Duplicates ignored: {status.DuplicateMessages}\r\n" +
            $"First message: {status.FirstMessage?.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n" +
            $"Last message: {status.LastMessage?.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n" +
            (status.LastError.Length > 0 ? $"Last error: {status.LastError}\r\n" : string.Empty),
            "Local history database",
            MessageBoxButtons.OK,
            status.Faulted ? MessageBoxIcon.Error : MessageBoxIcon.Information);
    }

    private async void ClearHistoryButtonClicked(
        object? sender,
        EventArgs e)
    {
        _ = sender;
        _ = e;

        await ClearLocalHistoryAsync();
    }

    private async Task ClearLocalHistoryAsync()
    {
        var result = MessageBox.Show(
            this,
            "This permanently deletes the stored local aircraft and message history.\r\n\r\n" +
            "IQ captures and decoder analysis files are not deleted.",
            "Clear local history database",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes)
            return;

        try
        {
            await _historyDatabase.ClearAsync();
            Interlocked.Exchange(ref _historyRefreshRequested, 1);
            if (_historyViewActive)
                RefreshHistoryIfNeeded();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Clear local history",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task VacuumLocalHistoryAsync()
    {
        try
        {
            await _historyDatabase.VacuumAsync();
            Interlocked.Exchange(ref _historyRefreshRequested, 1);
            MessageBox.Show(
                this,
                "The embedded SQLite database was compacted.",
                "Local history",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Compact local history",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024L * 1024L)
            return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024L * 1024L)
            return $"{bytes / 1024.0 / 1024.0:0.0} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB";
    }

    private void RefreshSessionsIfNeeded()
    {
        if (!_operationsBoardViewActive &&
            !_sessionViewActive)
        {
            return;
        }

        var version =
            _sessionStore.Version;

        var nowTicks =
            Environment.TickCount64;

        var ageRefreshDue =
            nowTicks -
            Interlocked.Read(
                ref _lastSessionUiRefreshTicks) >=
            5000;

        var forced =
            Interlocked.Exchange(
                ref _sessionRefreshRequested,
                0) != 0 ||
            ageRefreshDue;

        if (!forced &&
            version ==
                Interlocked.Read(
                    ref _lastSessionVersion))
        {
            return;
        }

        if (_operationsBoardViewActive)
        {
            var boardSessions =
                _sessionStore.Snapshot(
                    _operationsBoard.FilterText,
                    _operationsBoard.ActiveWindow,
                    500);

            _operationsBoard.RefreshSessions(
                boardSessions);
        }

        if (_sessionViewActive)
        {
            var sessions =
                _sessionStore.Snapshot(
                    _activeSessions.FilterText,
                    _activeSessions.ActiveWindow,
                    500);

            _activeSessions.RefreshSessions(
                sessions);
        }

        Interlocked.Exchange(
            ref _lastSessionVersion,
            version);

        Interlocked.Exchange(
            ref _lastSessionUiRefreshTicks,
            nowTicks);
    }

    private void RefreshGridIfNeeded()
    {
        var version =
            _store.Version;

        var forced =
            Interlocked.Exchange(
                ref _gridRefreshRequested,
                0) != 0;

        if (!forced &&
            version ==
                Interlocked.Read(
                    ref _lastGridVersion))
        {
            return;
        }

        RefreshGrid();

        Interlocked.Exchange(
            ref _lastGridVersion,
            version);
    }

    private void RefreshGrid()
    {
        if (_grid.IsCurrentCellInEditMode)
            return;

        var selectedKey =
            _grid.SelectedRows.Count > 0 &&
            _grid.SelectedRows[0].Tag is Vdl2Message selectedMessage
                ? selectedMessage.DedupKey
                : string.Empty;

        var snapshot =
            _store.Snapshot(
                _filter.Text,
                500);

        if (_channelHistoryFilter.SelectedIndex > 0)
        {
            var selectedText = _channelHistoryFilter.SelectedItem?.ToString() ?? string.Empty;
            if (double.TryParse(
                selectedText.Replace(" MHz", string.Empty),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var selectedMhz))
            {
                snapshot = snapshot
                    .Where(m => m.FrequencyMhz.HasValue &&
                                Math.Abs(m.FrequencyMhz.Value - selectedMhz) < 0.001)
                    .ToArray();
            }
        }
        _refreshingGrid = true;
        _grid.SuspendLayout();

        try
        {
            _grid.Rows.Clear();
            foreach (var m in snapshot)
            {
                var index = _grid.Rows.Add(
                    m.ReceivedAt.ToString("HH:mm:ss"),
                    m.Protocol ?? string.Empty,
                    m.Direction ?? string.Empty,
                    m.Icao ?? string.Empty,
                    m.Registration ?? string.Empty,
                    m.Callsign ?? string.Empty,
                    m.Label ?? string.Empty,
                    m.AcarsMessageId,
                    m.FrequencyMhz?.ToString("0.000") ?? string.Empty,
                    m.Text ?? string.Empty);
                _grid.Rows[index].Cells["Text"].ToolTipText = m.RawJson;
                _grid.Rows[index].Tag = m;

                if (selectedKey.Length > 0 &&
                    string.Equals(
                        selectedKey,
                        m.DedupKey,
                        StringComparison.Ordinal))
                {
                    _grid.Rows[index].Selected = true;
                }
            }
        }
        finally
        {
            _grid.ResumeLayout();
            _refreshingGrid = false;
        }

        UpdateSelectedEventDetails();
    }

    private void ExportClicked(object? sender, EventArgs e)
    {
        if (_exporter.Enabled)
        {
            _exporter.Disable();
            _export.Text = "Export JSONL";
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "JSON Lines (*.jsonl)|*.jsonl|All files (*.*)|*.*",
            FileName = $"aircraft-data-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl",
            InitialDirectory = Application.StartupPath
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _exporter.Enable(dialog.FileName);
            _export.Text = "Stop export";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public unsafe void Shutdown()
    {
        if (_shutdown)
            return;

        _shutdown = true;
        SaveUiPreferences();
        _uiTimer.Stop();
        _iqProcessor.Enabled = false;
        _iqProcessor.BlockAvailable -= OnIqBlock;
        _control.PropertyChanged -= ControlPropertyChanged;
        _captureManager.CaptureCompleted -= OnCaptureCompleted;
        _analysisScheduler.AnalysisStarted -= OnAnalysisStarted;
        _analysisScheduler.AnalysisCompleted -= OnAnalysisCompleted;
        _analysisScheduler.BatchCompleted -= OnAnalysisBatchCompleted;
        _analysisScheduler.AnalysisDropped -= OnAnalysisDropped;
        _analysisScheduler.Dispose();
        _captureManager.Dispose();
        _d8pskAnalyzer.Dispose();
        _exporter.Dispose();
        _historyControl.Dispose();
        _operationsBoard.Dispose();
        _historyDatabase.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
            _uiTimer.Dispose();
            _aircraftOnlineMenu.Dispose();
            _aircraftDashboard.Dispose();
        }
        base.Dispose(disposing);
    }
}
