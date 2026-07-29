// SPDX-License-Identifier: MIT
using System.Text;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed class AircraftDashboardControl : UserControl
{
    private readonly AircraftMetadataService
        _metadataService =
            new();

    private CancellationTokenSource?
        _lookupCancellation;

    private Vdl2Message?
        _message;

    private AircraftMetadata?
        _metadata;

    private FlightRouteMetadata?
        _route;

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
                Color.DeepSkyBlue,
            ActiveLinkColor =
                Color.White,
            VisitedLinkColor =
                Color.DeepSkyBlue
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
            "Aircraft details");

    private readonly Button _liveMap =
        ActionButton(
            "Live map");

    private readonly Button _flight =
        ActionButton(
            "Search flight");

    private readonly Button _copy =
        ActionButton(
            "Copy details");

    private readonly Button _refresh =
        ActionButton(
            "Refresh online");

    public AircraftDashboardControl()
    {
        Dock =
            DockStyle.Fill;

        BackColor =
            Color.FromArgb(
                26,
                30,
                36);

        ForeColor =
            Color.Gainsboro;

        Padding =
            new Padding(14);

        BuildInterface();

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
                StartOnlineLookup();

        SetMessage(
            null);
    }

    public void SetMessage(
        Vdl2Message? message)
    {
        _lookupCancellation?.Cancel();
        _lookupCancellation?.Dispose();
        _lookupCancellation =
            null;

        _message =
            message;

        _metadata =
            null;

        _route =
            null;

        if (message is null ||
            !AircraftOnlineLookup.TryNormalizeIcao(
                message.Icao,
                out var normalized))
        {
            _icao.Text =
                "------";

            _status.Text =
                "Select an AVLC aircraft with a valid ICAO24 address.";

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

            return;
        }

        _icao.Text =
            normalized;

        _status.Text =
            "Verified AVLC aircraft · loading online identity…";

        _registration.Text =
            Display(
                message.Registration);

        _aircraft.Text =
            "Loading…";

        _operator.Text =
            "Loading…";

        _callsign.Text =
            Display(
                message.Callsign);

        _routeLabel.Text =
            string.IsNullOrWhiteSpace(
                message.Callsign)
                ? "Callsign not present in this frame"
                : "Loading…";

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

        SetActionsEnabled(
            true);

        StartOnlineLookup();
    }

    protected override void Dispose(
        bool disposing)
    {
        if (disposing)
        {
            _lookupCancellation?.Cancel();
            _lookupCancellation?.Dispose();
            _metadataService.Dispose();
        }

        base.Dispose(
            disposing);
    }

    private void StartOnlineLookup()
    {
        if (_message is null ||
            !AircraftOnlineLookup.TryNormalizeIcao(
                _message.Icao,
                out _))
        {
            return;
        }

        _lookupCancellation?.Cancel();
        _lookupCancellation?.Dispose();

        _lookupCancellation =
            new CancellationTokenSource();

        _ =
            LoadOnlineAsync(
                _message,
                _lookupCancellation.Token);
    }

    private async Task LoadOnlineAsync(
        Vdl2Message message,
        CancellationToken cancellationToken)
    {
        _status.Text =
            "Online identity: connecting…";

        try
        {
            var metadataTask =
                _metadataService.LookupAircraftAsync(
                    message.Icao,
                    cancellationToken);

            Task<FlightRouteMetadata>?
                routeTask =
                    null;

            if (!string.IsNullOrWhiteSpace(
                message.Callsign))
            {
                routeTask =
                    _metadataService.LookupRouteAsync(
                        message.Callsign,
                        cancellationToken);
            }

            var metadata =
                await metadataTask;

            FlightRouteMetadata? route =
                null;

            if (routeTask is not null)
            {
                route =
                    await routeTask;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (IsDisposed ||
                Disposing ||
                !string.Equals(
                    _message?.DedupKey,
                    message.DedupKey,
                    StringComparison.Ordinal))
            {
                return;
            }

            _metadata =
                metadata;

            _route =
                route;

            if (metadata.Found)
            {
                _registration.Text =
                    DisplayFirst(
                        metadata.Registration,
                        message.Registration);

                _aircraft.Text =
                    CombineAircraft(
                        metadata);

                _operator.Text =
                    CombineOperator(
                        metadata);

                _status.Text =
                    "Online identity loaded from HexDB.";
            }
            else
            {
                _aircraft.Text =
                    "Aircraft identity not found";

                _operator.Text =
                    "—";

                _status.Text =
                    "Online identity unavailable: " +
                    metadata.Status;
            }

            if (route is not null)
            {
                _routeLabel.Text =
                    route.Found
                        ? route.Route
                        : "Route unavailable: " +
                          route.Status;
            }
        }
        catch (OperationCanceledException)
        {
            // A different row was selected.
        }
        catch (Exception ex)
        {
            if (!IsDisposed &&
                !Disposing)
            {
                _status.Text =
                    "Online identity failed: " +
                    ex.GetType().Name;
            }
        }
    }

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
                Dock =
                    DockStyle.Fill,
                ColumnCount =
                    1,
                RowCount =
                    4,
                BackColor =
                    BackColor,
                Padding =
                    new Padding(0)
            };

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));

        root.RowStyles.Add(
            new RowStyle(
                SizeType.Percent,
                100));

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));

        var titleRow =
            new FlowLayoutPanel
            {
                Dock =
                    DockStyle.Fill,
                AutoSize =
                    true,
                WrapContents =
                    true,
                BackColor =
                    BackColor
            };

        titleRow.Controls.Add(
            new Label
            {
                Text =
                    "AIRCRAFT",
                AutoSize =
                    true,
                Font =
                    new Font(
                        Font,
                        FontStyle.Bold),
                ForeColor =
                    Color.Silver,
                Padding =
                    new Padding(
                        0,
                        12,
                        10,
                        0)
            });

        titleRow.Controls.Add(
            _icao);

        root.Controls.Add(
            titleRow,
            0,
            0);

        _status.ForeColor =
            Color.LightSteelBlue;

        _status.Padding =
            new Padding(
                0,
                4,
                0,
                12);

        root.Controls.Add(
            _status,
            0,
            1);

        var values =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Top,
                AutoSize =
                    true,
                ColumnCount =
                    2,
                BackColor =
                    BackColor,
                Padding =
                    new Padding(
                        0,
                        4,
                        0,
                        10)
            };

        values.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize));

        values.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100));

        AddValue(
            values,
            0,
            "Registration",
            _registration);

        AddValue(
            values,
            1,
            "Aircraft",
            _aircraft);

        AddValue(
            values,
            2,
            "Operator",
            _operator);

        AddValue(
            values,
            3,
            "Callsign / flight",
            _callsign);

        AddValue(
            values,
            4,
            "Route",
            _routeLabel);

        AddValue(
            values,
            5,
            "Direction",
            _direction);

        AddValue(
            values,
            6,
            "Ground station",
            _station);

        AddValue(
            values,
            7,
            "Received",
            _received);

        AddValue(
            values,
            8,
            "Frequency",
            _frequency);

        root.Controls.Add(
            values,
            0,
            2);

        var actions =
            new FlowLayoutPanel
            {
                Dock =
                    DockStyle.Fill,
                AutoSize =
                    true,
                WrapContents =
                    true,
                BackColor =
                    BackColor,
                Padding =
                    new Padding(
                        0,
                        8,
                        0,
                        0)
            };

        actions.Controls.Add(
            _details);

        actions.Controls.Add(
            _liveMap);

        actions.Controls.Add(
            _flight);

        actions.Controls.Add(
            _copy);

        actions.Controls.Add(
            _refresh);

        root.Controls.Add(
            actions,
            0,
            3);

        Controls.Add(
            root);
    }

    private static void AddValue(
        TableLayoutPanel table,
        int row,
        string name,
        Label value)
    {
        table.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));

        table.Controls.Add(
            new Label
            {
                Text =
                    name + ":",
                AutoSize =
                    true,
                ForeColor =
                    Color.Silver,
                Padding =
                    new Padding(
                        0,
                        4,
                        12,
                        4)
            },
            0,
            row);

        table.Controls.Add(
            value,
            1,
            row);
    }

    private static Label ValueLabel() =>
        new()
        {
            AutoSize =
                true,
            ForeColor =
                Color.WhiteSmoke,
            Padding =
                new Padding(
                    0,
                    4,
                    0,
                    4),
            MaximumSize =
                new Size(
                    600,
                    0)
        };

    private static Button ActionButton(
        string text)
    {
        var button =
            new Button
            {
                Text =
                    text,
                AutoSize =
                    true,
                FlatStyle =
                    FlatStyle.Flat,
                BackColor =
                    Color.FromArgb(
                        45,
                        52,
                        62),
                ForeColor =
                    Color.WhiteSmoke,
                Padding =
                    new Padding(
                        5,
                        2,
                        5,
                        2)
            };

        button.FlatAppearance.BorderColor =
            Color.FromArgb(
                80,
                92,
                108);

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

        _refresh.Enabled =
            enabled;
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
