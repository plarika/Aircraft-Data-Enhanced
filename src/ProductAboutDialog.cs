// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.InteropServices;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed class ProductAboutDialog : Form
{
    private readonly TextBox _systemInformation;

    private ProductAboutDialog(
        LocalHistoryStatus databaseStatus)
    {
        Text =
            "Aircraft Data Enhanced — About";

        StartPosition =
            FormStartPosition.CenterParent;

        FormBorderStyle =
            FormBorderStyle.FixedDialog;

        MaximizeBox =
            false;

        MinimizeBox =
            false;

        ShowInTaskbar =
            false;

        ClientSize =
            new Size(
                720,
                510);

        BackColor =
            Color.FromArgb(
                18,
                23,
                29);

        ForeColor =
            Color.Gainsboro;

        Font =
            new Font(
                "Segoe UI",
                9.0f);

        var root =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,
                ColumnCount =
                    1,
                RowCount =
                    4,
                Padding =
                    new Padding(
                        20),
                BackColor =
                    BackColor
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

        var brand =
            new FlowLayoutPanel
            {
                Dock =
                    DockStyle.Top,
                AutoSize =
                    true,
                WrapContents =
                    false,
                BackColor =
                    Color.Transparent,
                Margin =
                    Padding.Empty
            };

        brand.Controls.Add(
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
                        27.0f,
                        FontStyle.Bold),
                Padding =
                    new Padding(
                        0,
                        0,
                        18,
                        0)
            });

        var identity =
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

        identity.Controls.Add(
            new Label
            {
                Text =
                    "AIRCRAFT DATA ENHANCED",
                AutoSize =
                    true,
                ForeColor =
                    Color.White,
                Font =
                    new Font(
                        "Segoe UI",
                        18.0f,
                        FontStyle.Bold)
            });

        identity.Controls.Add(
            new Label
            {
                Text =
                    "Air Operations Terminal · v0.19.0-beta",
                AutoSize =
                    true,
                ForeColor =
                    Color.LightSteelBlue,
                Font =
                    new Font(
                        "Segoe UI",
                        10.0f,
                        FontStyle.Regular)
            });

        brand.Controls.Add(
            identity);

        var description =
            new Label
            {
                Text =
                    "Local VDL2/AVLC/ACARS reception, verified aircraft sessions, " +
                    "airport-style operations board and embedded SQLite history.",
                AutoSize =
                    true,
                MaximumSize =
                    new Size(
                        660,
                        0),
                ForeColor =
                    Color.Gainsboro,
                Padding =
                    new Padding(
                        0,
                        12,
                        0,
                        12)
            };

        _systemInformation =
            new TextBox
            {
                Dock =
                    DockStyle.Fill,
                Multiline =
                    true,
                ReadOnly =
                    true,
                ScrollBars =
                    ScrollBars.Vertical,
                WordWrap =
                    false,
                BackColor =
                    Color.FromArgb(
                        12,
                        16,
                        21),
                ForeColor =
                    Color.FromArgb(
                        215,
                        228,
                        238),
                BorderStyle =
                    BorderStyle.FixedSingle,
                Font =
                    new Font(
                        "Consolas",
                        9.0f),
                Text =
                    BuildSystemInformation(
                        databaseStatus)
            };

        var buttons =
            new FlowLayoutPanel
            {
                Dock =
                    DockStyle.Fill,
                AutoSize =
                    true,
                FlowDirection =
                    FlowDirection.RightToLeft,
                WrapContents =
                    false,
                Padding =
                    new Padding(
                        0,
                        12,
                        0,
                        0)
            };

        var close =
            CreateButton(
                "Close");

        close.DialogResult =
            DialogResult.OK;

        var copy =
            CreateButton(
                "Copy system information");

        copy.Click +=
            (_, _) =>
            {
                try
                {
                    Clipboard.SetText(
                        _systemInformation.Text);
                }
                catch
                {
                }
            };

        buttons.Controls.Add(
            close);

        buttons.Controls.Add(
            copy);

        root.Controls.Add(
            brand,
            0,
            0);

        root.Controls.Add(
            description,
            0,
            1);

        root.Controls.Add(
            _systemInformation,
            0,
            2);

        root.Controls.Add(
            buttons,
            0,
            3);

        Controls.Add(
            root);

        AcceptButton =
            close;

        CancelButton =
            close;
    }

    public static void ShowProductDialog(
        IWin32Window owner,
        LocalHistoryStatus databaseStatus)
    {
        using var dialog =
            new ProductAboutDialog(
                databaseStatus);

        dialog.ShowDialog(
            owner);
    }

    private static string BuildSystemInformation(
        LocalHistoryStatus databaseStatus)
    {
        var assembly =
            Assembly.GetExecutingAssembly();

        var assemblyVersion =
            assembly.GetName()
                .Version?
                .ToString()
            ??
            "unknown";

        return
            "PRODUCT\r\n" +
            "-------\r\n" +
            "Aircraft Data Enhanced\r\n" +
            "Release: 0.19.0-beta\r\n" +
            $"Assembly: {assemblyVersion}\r\n" +
            "Workspace: Air Operations Terminal\r\n\r\n" +

            "RUNTIME\r\n" +
            "-------\r\n" +
            $"Framework: {RuntimeInformation.FrameworkDescription}\r\n" +
            $"OS: {RuntimeInformation.OSDescription}\r\n" +
            $"Process architecture: {RuntimeInformation.ProcessArchitecture}\r\n" +
            $"64-bit OS: {Environment.Is64BitOperatingSystem}\r\n" +
            $"64-bit process: {Environment.Is64BitProcess}\r\n" +
            $"Machine: {Environment.MachineName}\r\n\r\n" +

            "LOCAL DATABASE\r\n" +
            "--------------\r\n" +
            $"State: {databaseStatus.State}\r\n" +
            $"Ready: {databaseStatus.Ready}\r\n" +
            $"Path: {databaseStatus.DatabasePath}\r\n" +
            $"Messages: {databaseStatus.StoredMessages}\r\n" +
            $"Aircraft: {databaseStatus.StoredAircraft}\r\n" +
            $"Pending writes: {databaseStatus.PendingWrites}\r\n" +
            $"Dropped writes: {databaseStatus.DroppedWrites}\r\n" +
            $"Last error: {databaseStatus.LastError}\r\n\r\n" +

            "LOCAL PREFERENCES\r\n" +
            "-----------------\r\n" +
            $"Path: {UiPreferencesStore.PreferencesPath}\r\n\r\n" +

            "PIPELINE\r\n" +
            "--------\r\n" +
            "IQ → D8PSK → VDL2 → Reed-Solomon → HDLC/FCS → AVLC → ACARS\r\n" +
            "Only verified ACARS/AVLC messages with a valid ICAO24 are published.";
    }

    private static Button CreateButton(
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
                        43,
                        53,
                        64),
                ForeColor =
                    Color.White,
                Padding =
                    new Padding(
                        8,
                        3,
                        8,
                        3),
                Margin =
                    new Padding(
                        6,
                        0,
                        0,
                        0)
            };

        button.FlatAppearance.BorderColor =
            Color.FromArgb(
                78,
                96,
                112);

        return
            button;
    }
}
