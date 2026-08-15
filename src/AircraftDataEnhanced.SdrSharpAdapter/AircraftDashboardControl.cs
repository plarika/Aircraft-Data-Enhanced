// SPDX-License-Identifier: MIT
using System.Text;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed class AircraftDashboardControl : UserControl
{
    public event EventHandler? BackRequested;

    private readonly AircraftMetadataService
        _metadataService =
            new();

    private CancellationTokenSource?
        _aircraftLookupCancellation;

    private CancellationTokenSource?
        _routeLookupCancellation;

    private bool
        _aircraftLookupInProgress;

    private bool
        _routeLookupInProgress;

    private readonly System.Windows.Forms.Timer
        _selectionClearTimer =
            new()
            {
                Interval = 900
            };

    private readonly System.Windows.Forms.Timer
        _lookupWatchdog =
            new()
            {
                Interval = 1000
            };

    private DateTimeOffset
        _aircraftLookupStartedAt;

    private DateTimeOffset
        _routeLookupStartedAt;

    private Vdl2Message?
        _pendingDashboardResetMessage;

    private const int LookupWatchdogSeconds = 12;

    private static readonly object
        LookupLogGate =
            new();

    private string
        _selectedIcao =
            string.Empty;

    private string
        _selectedCallsign =
            string.Empty;

    private Vdl2Message?
        _message;

    private AircraftMetadata?
        _metadata;

    private FlightRouteMetadata?
        _route;

    private readonly LinkLabel _back =
        new()
        {
            Text = "← Back to aircraft list",
            AutoSize = true,
            LinkColor = AdeVisualTheme.AccentBright,
            ActiveLinkColor = Color.White,
            VisitedLinkColor = AdeVisualTheme.AccentBright,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Font = AdeVisualTheme.UiFont(8.5f, FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 10)
        };

    private readonly LinkLabel _icao =
        new()
        {
            Text = "------",
            AutoSize = true,
            Font =
                new Font(
                    FontFamily.GenericMonospace,
                    20,
                    FontStyle.Bold),
            LinkColor =
                AdeVisualTheme.AccentBright,
            ActiveLinkColor =
                Color.White,
            VisitedLinkColor =
                AdeVisualTheme.AccentBright
        };

    private readonly Label _status =
        new()
        {
            Text =
                "Select an AVLC aircraft.",
            AutoSize = true
        };

    private readonly Label _registration =
        ValueLabel();

    private readonly Label _aircraft =
        ValueLabel();

    private readonly Label _operator =
        ValueLabel();

    private readonly Label _callsign =
        ValueLabel();

    private readonly Label _routeLabel =
        ValueLabel();

    private readonly Label _direction =
        ValueLabel();

    private readonly Label _station =
        ValueLabel();

    private readonly Label _received =
        ValueLabel();

    private readonly Label _frequency =
        ValueLabel();

    private readonly Button _details =
        ActionButton(
            "ⓘ   Aircraft details");

    private readonly Button _liveMap =
        ActionButton(
            "▧   Live map");

    private readonly Button _flight =
        ActionButton(
            "⌕   Search flight");

    private readonly Button _copy =
        ActionButton(
            "▣   Copy details");

    private readonly Button _refresh =
        ActionButton(
            "↻   Refresh online");

    public AircraftDashboardControl()
    {
        Dock =
            DockStyle.Fill;

        BackColor =
            AdeVisualTheme.Surface;

        ForeColor =
            AdeVisualTheme.TextPrimary;

        Padding =
            new Padding(14);

        BuildInterface();

        _back.LinkClicked +=
            (_, _) =>
                BackRequested?.Invoke(
                    this,
                    EventArgs.Empty);

        _icao.LinkClicked +=
            (_, _) =>
                Open(
                    AircraftOnlineProvider.Planespotters);

        _details.Click +=
            (_, _) =>
                Open(
                    AircraftOnlineProvider.Planespotters);

        _liveMap.Click +=
            (_, _) =>
                Open(
                    AircraftOnlineProvider.AdsbExchange);

        _flight.Click +=
            (_, _) =>
                Open(
                    AircraftOnlineProvider.FlightSearch);

        _copy.Click +=
            (_, _) =>
                CopyDetails();

        _refresh.Click +=
            (_, _) =>
                RefreshOnline();

        _selectionClearTimer.Tick +=
            (_, _) =>
                CommitPendingDashboardReset();

        _lookupWatchdog.Tick +=
            (_, _) =>
                CheckLookupWatchdog();

        _lookupWatchdog.Start();

        CommitDashboardReset(
            null);
    }

    public void SetMessage(
        Vdl2Message? message)
    {
        if (message is null ||
            !AircraftOnlineLookup.TryNormalizeIcao(
                message.Icao,
                out var normalizedIcao))
        {
            ScheduleDashboardReset(
                message);

            return;
        }

        CancelPendingDashboardReset();

        if (string.Equals(
                _message?.DedupKey,
                message.DedupKey,
                StringComparison.Ordinal))
        {
            _message =
                message;

            return;
        }

        var normalizedCallsign =
            NormalizeCallsign(
                message.Callsign);

        var sameIcao =
            string.Equals(
                _selectedIcao,
                normalizedIcao,
                StringComparison.OrdinalIgnoreCase);

        var sameCallsign =
            string.Equals(
                _selectedCallsign,
                normalizedCallsign,
                StringComparison.OrdinalIgnoreCase);

        _message =
            message;

        _selectedIcao =
            normalizedIcao;

        _selectedCallsign =
            normalizedCallsign;

        UpdateMessageFields(
            message,
            normalizedIcao);

        SetActionsEnabled(
            true);

        if (!sameIcao)
        {
            CancelAircraftLookup();

            _metadata =
                null;

            _aircraft.Text =
                "Loading…";

            _operator.Text =
                "Loading…";

            StartAircraftLookup(
                forceRefresh: false);
        }
        else if (_metadata is not null)
        {
            ApplyAircraftMetadata(
                _metadata,
                message,
                refreshed: false);
        }
        else if (!_aircraftLookupInProgress)
        {
            _aircraft.Text =
                "Loading…";

            _operator.Text =
                "Loading…";

            StartAircraftLookup(
                forceRefresh: false);
        }

        if (normalizedCallsign.Length == 0)
        {
            CancelRouteLookup();

            _route =
                null;

            _routeLabel.Text =
                "Callsign not present in this frame";
        }
        else if (!sameCallsign)
        {
            CancelRouteLookup();

            _route =
                null;

            _routeLabel.Text =
                "Loading…";

            StartRouteLookup(
                forceRefresh: false);
        }
        else if (_route is not null)
        {
            ApplyRouteMetadata(
                _route,
                refreshed: false);
        }
        else if (!_routeLookupInProgress)
        {
            _routeLabel.Text =
                "Loading…";

            StartRouteLookup(
                forceRefresh: false);
        }

        UpdateRefreshEnabled();
    }

    protected override void Dispose(
        bool disposing)
    {
        if (disposing)
        {
            _selectionClearTimer.Stop();
            _lookupWatchdog.Stop();
            _selectionClearTimer.Dispose();
            _lookupWatchdog.Dispose();
            CancelAllLookups();
            _metadataService.Dispose();
        }

        base.Dispose(
            disposing);
    }

    private void RefreshOnline()
    {
        if (_message is null ||
            _selectedIcao.Length == 0)
        {
            return;
        }

        SetLookupStatus(
            "Refreshing online aircraft identity…",
            AdeVisualState.Active);

        if (_metadata is null)
        {
            _aircraft.Text =
                "Loading…";

            _operator.Text =
                "Loading…";
        }

        StartAircraftLookup(
            forceRefresh: true);

        if (_selectedCallsign.Length > 0)
        {
            if (_route is null)
            {
                _routeLabel.Text =
                    "Loading…";
            }

            StartRouteLookup(
                forceRefresh: true);
        }
    }

    private void StartAircraftLookup(
        bool forceRefresh)
    {
        if (_selectedIcao.Length == 0 ||
            (_aircraftLookupInProgress &&
             !forceRefresh))
        {
            return;
        }

        CancelAircraftLookup();

        var request =
            new CancellationTokenSource();

        _aircraftLookupCancellation =
            request;

        _aircraftLookupInProgress =
            true;

        _aircraftLookupStartedAt =
            DateTimeOffset.UtcNow;

        WriteLookupLog(
            $"aircraft start icao={_selectedIcao} force={forceRefresh}");

        SetLookupStatus(
            forceRefresh
                ? "Refreshing online aircraft identity…"
                : "Online identity: connecting…",
            AdeVisualState.Active);

        UpdateRefreshEnabled();

        _ =
            LoadAircraftAsync(
                _selectedIcao,
                request,
                forceRefresh);
    }

    private async Task LoadAircraftAsync(
        string icao,
        CancellationTokenSource request,
        bool forceRefresh)
    {
        try
        {
            var metadata =
                await _metadataService.LookupAircraftAsync(
                    icao,
                    request.Token,
                    forceRefresh);

            request.Token.ThrowIfCancellationRequested();

            if (IsDisposed ||
                Disposing ||
                !string.Equals(
                    _selectedIcao,
                    icao,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (forceRefresh &&
                !metadata.Found &&
                _metadata?.Found == true)
            {
                SetLookupStatus(
                    "Online refresh failed (" +
                    metadata.Status +
                    "); showing the last valid identity.",
                    AdeVisualState.Warning);

                return;
            }

            WriteLookupLog(
                $"aircraft result icao={icao} found={metadata.Found} status={metadata.Status}");

            _metadata =
                metadata;

            ApplyAircraftMetadata(
                metadata,
                _message,
                forceRefresh);
        }
        catch (OperationCanceledException)
        {
            WriteLookupLog(
                $"aircraft cancelled icao={icao}");
        }
        catch (Exception ex)
        {
            WriteLookupLog(
                $"aircraft exception icao={icao} type={ex.GetType().Name} message={ex.Message}");

            if (!IsDisposed &&
                !Disposing &&
                string.Equals(
                    _selectedIcao,
                    icao,
                    StringComparison.OrdinalIgnoreCase))
            {
                SetLookupStatus(
                    "Online identity failed: " +
                    ex.GetType().Name,
                    AdeVisualState.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(
                    _aircraftLookupCancellation,
                    request))
            {
                _aircraftLookupCancellation =
                    null;

                _aircraftLookupInProgress =
                    false;

                _aircraftLookupStartedAt =
                    default;

                UpdateRefreshEnabled();
            }

            request.Dispose();
        }
    }

    private void StartRouteLookup(
        bool forceRefresh)
    {
        if (_selectedCallsign.Length == 0 ||
            (_routeLookupInProgress &&
             !forceRefresh))
        {
            return;
        }

        CancelRouteLookup();

        var request =
            new CancellationTokenSource();

        _routeLookupCancellation =
            request;

        _routeLookupInProgress =
            true;

        _routeLookupStartedAt =
            DateTimeOffset.UtcNow;

        WriteLookupLog(
            $"route start callsign={_selectedCallsign} force={forceRefresh}");

        UpdateRefreshEnabled();

        _ =
            LoadRouteAsync(
                _selectedCallsign,
                request,
                forceRefresh);
    }

    private async Task LoadRouteAsync(
        string callsign,
        CancellationTokenSource request,
        bool forceRefresh)
    {
        try
        {
            var route =
                await _metadataService.LookupRouteAsync(
                    callsign,
                    request.Token,
                    forceRefresh);

            request.Token.ThrowIfCancellationRequested();

            if (IsDisposed ||
                Disposing ||
                !string.Equals(
                    _selectedCallsign,
                    callsign,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (forceRefresh &&
                !route.Found &&
                _route?.Found == true)
            {
                _routeLabel.Text =
                    _route.Route +
                    " · refresh failed (" +
                    route.Status +
                    ")";

                return;
            }

            WriteLookupLog(
                $"route result callsign={callsign} found={route.Found} status={route.Status}");

            _route =
                route;

            ApplyRouteMetadata(
                route,
                forceRefresh);
        }
        catch (OperationCanceledException)
        {
            WriteLookupLog(
                $"route cancelled callsign={callsign}");
        }
        catch (Exception ex)
        {
            WriteLookupLog(
                $"route exception callsign={callsign} type={ex.GetType().Name} message={ex.Message}");

            if (!IsDisposed &&
                !Disposing &&
                string.Equals(
                    _selectedCallsign,
                    callsign,
                    StringComparison.OrdinalIgnoreCase))
            {
                _routeLabel.Text =
                    "Route lookup failed: " +
                    ex.GetType().Name;
            }
        }
        finally
        {
            if (ReferenceEquals(
                    _routeLookupCancellation,
                    request))
            {
                _routeLookupCancellation =
                    null;

                _routeLookupInProgress =
                    false;

                _routeLookupStartedAt =
                    default;

                UpdateRefreshEnabled();
            }

            request.Dispose();
        }
    }

    private void ApplyAircraftMetadata(
        AircraftMetadata metadata,
        Vdl2Message? message,
        bool refreshed)
    {
        if (metadata.Found)
        {
            _registration.Text =
                DisplayFirst(
                    metadata.Registration,
                    message?.Registration);

            _aircraft.Text =
                CombineAircraft(
                    metadata);

            _operator.Text =
                CombineOperator(
                    metadata);

            var provider =
                metadata.Status.Contains(
                    "adsbdb",
                    StringComparison.OrdinalIgnoreCase)
                    ? "ADSBdb"
                    : "HexDB";

            SetLookupStatus(
                refreshed
                    ? $"Online identity refreshed from {provider}."
                    : $"Online identity loaded from {provider}.",
                AdeVisualState.Success);

            return;
        }

        _registration.Text =
            Display(
                message?.Registration);

        _aircraft.Text =
            metadata.Status == "not_found"
                ? "Aircraft identity not found"
                : "Aircraft identity unavailable";

        _operator.Text =
            "—";

        SetLookupStatus(
            "Online identity unavailable: " +
            metadata.Status,
            StatusState(
                metadata.Status));
    }

    private void ApplyRouteMetadata(
        FlightRouteMetadata route,
        bool refreshed)
    {
        _routeLabel.Text =
            route.Found
                ? route.Route +
                  (refreshed
                      ? " · refreshed"
                      : string.Empty)
                : "Route unavailable: " +
                  route.Status;
    }

    private void UpdateMessageFields(
        Vdl2Message message,
        string normalizedIcao)
    {
        _icao.Text =
            normalizedIcao;

        _registration.Text =
            _metadata?.Found == true
                ? DisplayFirst(
                    _metadata.Registration,
                    message.Registration)
                : Display(
                    message.Registration);

        _callsign.Text =
            Display(
                message.Callsign);

        _direction.Text =
            Display(
                message.Direction);

        _station.Text =
            Display(
                message.Destination);

        _received.Text =
            message.ReceivedAt
                .ToLocalTime()
                .ToString(
                    "yyyy-MM-dd HH:mm:ss");

        _frequency.Text =
            message.FrequencyMhz.HasValue
                ? message.FrequencyMhz.Value
                    .ToString(
                        "0.000000") +
                  " MHz"
                : "—";
    }

    private void ResetDashboard()
    {
        _icao.Text =
            "------";

        SetLookupStatus(
            "Select an AVLC aircraft with a valid ICAO24 address.",
            AdeVisualState.Neutral);

        _registration.Text =
            "—";

        _aircraft.Text =
            "—";

        _operator.Text =
            "—";

        _callsign.Text =
            "—";

        _routeLabel.Text =
            "—";

        _direction.Text =
            "—";

        _station.Text =
            "—";

        _received.Text =
            "—";

        _frequency.Text =
            "—";

        SetActionsEnabled(
            false);
    }

    private void ScheduleDashboardReset(
        Vdl2Message? message)
    {
        _pendingDashboardResetMessage =
            message;

        _selectionClearTimer.Stop();
        _selectionClearTimer.Start();
    }

    private void CancelPendingDashboardReset()
    {
        _selectionClearTimer.Stop();
        _pendingDashboardResetMessage =
            null;
    }

    private void CommitPendingDashboardReset()
    {
        _selectionClearTimer.Stop();

        var message =
            _pendingDashboardResetMessage;

        _pendingDashboardResetMessage =
            null;

        CommitDashboardReset(
            message);
    }

    private void CommitDashboardReset(
        Vdl2Message? message)
    {
        CancelAllLookups();

        _message =
            message;

        _selectedIcao =
            string.Empty;

        _selectedCallsign =
            string.Empty;

        _metadata =
            null;

        _route =
            null;

        ResetDashboard();
    }

    private void CheckLookupWatchdog()
    {
        var now =
            DateTimeOffset.UtcNow;

        if (_aircraftLookupInProgress &&
            _aircraftLookupStartedAt != default &&
            now - _aircraftLookupStartedAt >
                TimeSpan.FromSeconds(
                    LookupWatchdogSeconds))
        {
            var icao =
                _selectedIcao;

            WriteLookupLog(
                $"aircraft watchdog timeout icao={icao}");

            CancelAircraftLookup();

            if (icao.Length > 0)
            {
                _metadata =
                    AircraftMetadata.Unavailable(
                        icao,
                        "timeout");

                ApplyAircraftMetadata(
                    _metadata,
                    _message,
                    refreshed: false);
            }
        }

        if (_routeLookupInProgress &&
            _routeLookupStartedAt != default &&
            now - _routeLookupStartedAt >
                TimeSpan.FromSeconds(
                    LookupWatchdogSeconds))
        {
            var callsign =
                _selectedCallsign;

            WriteLookupLog(
                $"route watchdog timeout callsign={callsign}");

            CancelRouteLookup();

            if (callsign.Length > 0)
            {
                _route =
                    FlightRouteMetadata.Unavailable(
                        callsign,
                        "timeout");

                ApplyRouteMetadata(
                    _route,
                    refreshed: false);
            }
        }
    }

    private static void WriteLookupLog(
        string message)
    {
        try
        {
            var directory =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "AircraftDataEnhanced");

            Directory.CreateDirectory(
                directory);

            var path =
                Path.Combine(
                    directory,
                    "aircraft-lookup.log");

            lock (LookupLogGate)
            {
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O} [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never break the SDR# UI.
        }
    }

    private void CancelAllLookups()
    {
        CancelAircraftLookup();
        CancelRouteLookup();
    }

    private void CancelAircraftLookup()
    {
        var request =
            _aircraftLookupCancellation;

        _aircraftLookupCancellation =
            null;

        _aircraftLookupInProgress =
            false;

        _aircraftLookupStartedAt =
            default;

        request?.Cancel();

        UpdateRefreshEnabled();
    }

    private void CancelRouteLookup()
    {
        var request =
            _routeLookupCancellation;

        _routeLookupCancellation =
            null;

        _routeLookupInProgress =
            false;

        _routeLookupStartedAt =
            default;

        request?.Cancel();

        UpdateRefreshEnabled();
    }

    private void UpdateRefreshEnabled()
    {
        _refresh.Enabled =
            _selectedIcao.Length > 0 &&
            !_aircraftLookupInProgress &&
            !_routeLookupInProgress;
    }

    private void SetLookupStatus(
        string text,
        AdeVisualState state)
    {
        _status.Text =
            text;

        _status.ForeColor =
            AdeVisualTheme.StateColor(
                state);

        _status.BackColor =
            state switch
            {
                AdeVisualState.Success => Color.FromArgb(14, 49, 35),
                AdeVisualState.Error => Color.FromArgb(58, 23, 29),
                AdeVisualState.Warning => Color.FromArgb(47, 37, 16),
                AdeVisualState.Active => Color.FromArgb(12, 38, 56),
                _ => AdeVisualTheme.SurfaceRaised
            };
    }

    private static AdeVisualState StatusState(
        string status)
    {
        if (status.Contains(
                "429",
                StringComparison.OrdinalIgnoreCase) ||
            status.Contains(
                "timeout",
                StringComparison.OrdinalIgnoreCase))
        {
            return AdeVisualState.Warning;
        }

        if (status.StartsWith(
                "lookup_failed",
                StringComparison.OrdinalIgnoreCase))
        {
            return AdeVisualState.Error;
        }

        return AdeVisualState.Warning;
    }

    private static string NormalizeCallsign(
        string? callsign) =>
        (callsign ?? string.Empty)
        .Trim()
        .ToUpperInvariant();

    private void Open(
        AircraftOnlineProvider provider)
    {
        if (_message is null ||
            !AircraftOnlineLookup.TryNormalizeIcao(
                _message.Icao,
                out var icao))
        {
            return;
        }

        try
        {
            AircraftOnlineLookup.Open(
                provider,
                icao,
                DisplayFirstRaw(
                    _metadata?.Registration,
                    _message.Registration),
                _message.Callsign);
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

    private void CopyDetails()
    {
        if (_message is null)
            return;

        var builder =
            new StringBuilder();

        builder
            .Append("ICAO24: ")
            .AppendLine(_icao.Text)
            .Append("Registration: ")
            .AppendLine(_registration.Text)
            .Append("Aircraft: ")
            .AppendLine(_aircraft.Text)
            .Append("Operator: ")
            .AppendLine(_operator.Text)
            .Append("Callsign: ")
            .AppendLine(_callsign.Text)
            .Append("Route: ")
            .AppendLine(_routeLabel.Text)
            .Append("Direction: ")
            .AppendLine(_direction.Text)
            .Append("Station: ")
            .AppendLine(_station.Text)
            .Append("Received: ")
            .AppendLine(_received.Text)
            .Append("Frequency: ")
            .AppendLine(_frequency.Text);

        try
        {
            Clipboard.SetText(
                builder.ToString());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Copy aircraft",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void BuildInterface()
    {
        var root =
            new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = AdeVisualTheme.AppBackground,
                Padding = new Padding(16)
            };

        root.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(_back, 0, 0);

        var header =
            new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 12)
            };

        header.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 58));
        header.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 42));

        var heading =
            new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = Padding.Empty
            };

        heading.Controls.Add(
            new Label
            {
                Text = "Aircraft Details",
                AutoSize = true,
                ForeColor = AdeVisualTheme.TextPrimary,
                Font = AdeVisualTheme.UiFont(16.0f, FontStyle.Bold),
                Margin = Padding.Empty
            });

        heading.Controls.Add(
            new Label
            {
                Text = "Live message data enriched with online aircraft identity",
                AutoSize = true,
                ForeColor = AdeVisualTheme.TextSecondary,
                Font = AdeVisualTheme.UiFont(8.6f),
                Margin = new Padding(0, 3, 0, 0)
            });

        var identity =
            new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 1, 0, 0),
                Margin = Padding.Empty
            };

        identity.Controls.Add(
            new Label
            {
                Text = "ICAO24",
                AutoSize = true,
                ForeColor = AdeVisualTheme.TextMuted,
                Font = AdeVisualTheme.UiFont(7.5f, FontStyle.Bold),
                Padding = new Padding(0, 10, 8, 0)
            });

        _icao.Font = AdeVisualTheme.UiFont(17.0f, FontStyle.Bold);
        _icao.Padding = new Padding(0, 2, 10, 0);
        identity.Controls.Add(_icao);

        _status.ForeColor = AdeVisualTheme.Warning;
        _status.BackColor = Color.FromArgb(47, 37, 16);
        _status.BorderStyle = BorderStyle.FixedSingle;
        _status.Font = AdeVisualTheme.UiFont(8.2f, FontStyle.Bold);
        _status.Padding = new Padding(9, 7, 9, 7);
        _status.Margin = new Padding(8, 2, 0, 0);
        identity.Controls.Add(_status);

        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(identity, 1, 0);
        root.Controls.Add(header, 0, 1);

        var content =
            new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

        content.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 76));
        content.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 24));
        content.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        var detailsCard =
            new AdeCardPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                Margin = new Padding(0, 0, 8, 0)
            };

        var values =
            new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                BackColor = AdeVisualTheme.SurfaceRaised,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };

        values.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 42));
        values.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 170));
        values.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        AddValue(values, 0, "▣", "Registration", _registration);
        AddValue(values, 1, "✈", "Aircraft", _aircraft);
        AddValue(values, 2, "▦", "Operator", _operator);
        AddValue(values, 3, "◉", "Callsign / Flight", _callsign);
        AddValue(values, 4, "↗", "Route", _routeLabel);
        AddValue(values, 5, "⌖", "Direction", _direction);
        AddValue(values, 6, "⌁", "Ground Station", _station);
        AddValue(values, 7, "◴", "Received", _received);
        AddValue(values, 8, "≈", "Frequency", _frequency);

        detailsCard.Controls.Add(values);

        var actionsCard =
            new AdeCardPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                Margin = new Padding(8, 0, 0, 0)
            };

        var actions =
            new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = AdeVisualTheme.SurfaceRaised,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };

        foreach (var button in new[]
                 {
                     _details,
                     _liveMap,
                     _flight,
                     _copy,
                     _refresh
                 })
        {
            button.Width = 230;
            button.Height = 44;
            button.AutoSize = false;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(14, 0, 8, 0);
            button.Margin = new Padding(0, 0, 0, 10);
            actions.Controls.Add(button);
        }

        actionsCard.Controls.Add(actions);
        content.Controls.Add(detailsCard, 0, 0);
        content.Controls.Add(actionsCard, 1, 0);
        root.Controls.Add(content, 0, 2);

        Controls.Add(root);

        void ApplyResponsiveLayout()
        {
            if (Width < 840)
            {
                content.RowCount = 2;
                content.RowStyles.Clear();
                content.RowStyles.Add(
                    new RowStyle(SizeType.Percent, 100));
                content.RowStyles.Add(
                    new RowStyle(SizeType.AutoSize));
                content.ColumnStyles[0].Width = 100;
                content.ColumnStyles[1].Width = 0;
                content.SetCellPosition(
                    detailsCard,
                    new TableLayoutPanelCellPosition(0, 0));
                content.SetColumnSpan(detailsCard, 2);
                content.SetCellPosition(
                    actionsCard,
                    new TableLayoutPanelCellPosition(0, 1));
                content.SetColumnSpan(actionsCard, 2);
                detailsCard.Margin = new Padding(0, 0, 0, 8);
                actionsCard.Margin = new Padding(0, 8, 0, 0);
                actions.FlowDirection = FlowDirection.LeftToRight;
                actions.WrapContents = true;
                actionsCard.Visible = true;

                foreach (Control child in actions.Controls)
                {
                    child.Width = 154;
                }
            }
            else
            {
                content.RowCount = 1;
                content.RowStyles.Clear();
                content.RowStyles.Add(
                    new RowStyle(SizeType.Percent, 100));
                content.ColumnStyles[0].Width = 76;
                content.ColumnStyles[1].Width = 24;
                content.SetColumnSpan(detailsCard, 1);
                content.SetColumnSpan(actionsCard, 1);
                content.SetCellPosition(
                    detailsCard,
                    new TableLayoutPanelCellPosition(0, 0));
                content.SetCellPosition(
                    actionsCard,
                    new TableLayoutPanelCellPosition(1, 0));
                detailsCard.Margin = new Padding(0, 0, 8, 0);
                actionsCard.Margin = new Padding(8, 0, 0, 0);
                actions.FlowDirection = FlowDirection.TopDown;
                actions.WrapContents = false;
                actionsCard.Visible = true;

                foreach (Control child in actions.Controls)
                {
                    child.Width = 230;
                }
            }
        }

        Resize += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();
    }

    private static void AddValue(
        TableLayoutPanel table,
        int row,
        string glyph,
        string name,
        Label value)
    {
        table.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 48));

        table.Controls.Add(
            new Label
            {
                Text = glyph,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AdeVisualTheme.AccentBright,
                Font = AdeVisualTheme.UiFont(12.0f, FontStyle.Bold),
                Margin = Padding.Empty
            },
            0,
            row);

        table.Controls.Add(
            new Label
            {
                Text = name,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = AdeVisualTheme.TextSecondary,
                Font = AdeVisualTheme.UiFont(8.8f, FontStyle.Regular),
                Margin = Padding.Empty,
                Padding = new Padding(4, 0, 12, 0)
            },
            1,
            row);

        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        value.Margin = Padding.Empty;
        value.Padding = new Padding(0, 0, 4, 0);
        table.Controls.Add(
            value,
            2,
            row);
    }

    private static Label ValueLabel() =>
        new()
        {
            AutoSize = false,
            ForeColor = AdeVisualTheme.TextPrimary,
            Font = AdeVisualTheme.UiFont(9.3f, FontStyle.Regular),
            MaximumSize = new Size(900, 0)
        };

    private static Button ActionButton(
        string text)
    {
        var button =
            new Button
            {
                Text = text,
                AutoSize = false
            };

        AdeVisualTheme.StyleButton(
            button);

        return button;
    }

    private void SetActionsEnabled(
        bool enabled)
    {
        _icao.Enabled =
            enabled;

        _details.Enabled =
            enabled;

        _liveMap.Enabled =
            enabled;

        _flight.Enabled =
            enabled;

        _copy.Enabled =
            enabled;

        if (!enabled)
        {
            _refresh.Enabled =
                false;

            return;
        }

        UpdateRefreshEnabled();
    }

    private static string Display(
        string? value) =>
        string.IsNullOrWhiteSpace(
            value)
            ? "—"
            : value.Trim();

    private static string DisplayFirst(
        string? first,
        string? second) =>
        Display(
            DisplayFirstRaw(
                first,
                second));

    private static string DisplayFirstRaw(
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
        var parts =
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

        var result =
            string.Join(
                " ",
                parts);

        return
            string.IsNullOrWhiteSpace(
                result)
                ? "Unknown aircraft type"
                : result;
    }

    private static string CombineOperator(
        AircraftMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(
            metadata.OperatorCode))
        {
            return Display(
                metadata.Operator);
        }

        if (string.IsNullOrWhiteSpace(
            metadata.Operator))
        {
            return metadata.OperatorCode;
        }

        return
            metadata.Operator +
            " (" +
            metadata.OperatorCode +
            ")";
    }
}
