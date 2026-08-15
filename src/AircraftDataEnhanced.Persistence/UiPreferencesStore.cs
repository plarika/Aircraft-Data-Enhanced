// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed class UiPreferences
{
    public int SelectedWorkspace { get; set; } =
        0;

    public bool CommandBarVisible { get; set; }

    public bool ControlCenterVisible { get; set; }

    public bool ChannelMonitorVisible { get; set; }

    public bool WaterfallVisible { get; set; } =
        true;

    public bool DetailsVisible { get; set; } =
        true;

    public int OperationsWindowIndex { get; set; } =
        1;

    public decimal WaterfallMinimumDb { get; set; } =
        -100;

    public decimal WaterfallMaximumDb { get; set; } =
        -35;

    public decimal WaterfallContrastPercent { get; set; } =
        100;
}

internal static class UiPreferencesStore
{
    private static readonly object Gate =
        new();

    private static readonly JsonSerializerOptions
        JsonOptions =
            new()
            {
                WriteIndented =
                    true,
                PropertyNameCaseInsensitive =
                    true
            };

    public static string PreferencesDirectory =>
        RuntimeDataPaths.RootDirectory;

    public static string PreferencesPath =>
        RuntimeDataPaths.PreferencesPath;

    public static UiPreferences Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(
                    PreferencesPath))
                {
                    return
                        Normalize(
                            new UiPreferences());
                }

                var json =
                    File.ReadAllText(
                        PreferencesPath);

                var preferences =
                    JsonSerializer.Deserialize<
                        UiPreferences>(
                        json,
                        JsonOptions)
                    ??
                    new UiPreferences();

                return
                    Normalize(
                        preferences);
            }
            catch
            {
                return
                    Normalize(
                        new UiPreferences());
            }
        }
    }

    public static void Save(
        UiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(
            preferences);

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(
                    PreferencesDirectory);

                var normalized =
                    Normalize(
                        preferences);

                var json =
                    JsonSerializer.Serialize(
                        normalized,
                        JsonOptions);

                var temporaryPath =
                    PreferencesPath +
                    ".tmp";

                File.WriteAllText(
                    temporaryPath,
                    json);

                File.Move(
                    temporaryPath,
                    PreferencesPath,
                    overwrite:
                        true);
            }
            catch
            {
            }
        }
    }

    private static UiPreferences Normalize(
        UiPreferences preferences)
    {
        preferences.SelectedWorkspace =
            Math.Clamp(
                preferences.SelectedWorkspace,
                0,
                3);

        preferences.OperationsWindowIndex =
            Math.Clamp(
                preferences.OperationsWindowIndex,
                0,
                4);

        preferences.WaterfallMinimumDb =
            Math.Clamp(
                preferences.WaterfallMinimumDb,
                -140,
                -20);

        preferences.WaterfallMaximumDb =
            Math.Clamp(
                preferences.WaterfallMaximumDb,
                -120,
                10);

        if (preferences.WaterfallMaximumDb <=
            preferences.WaterfallMinimumDb)
        {
            preferences.WaterfallMaximumDb =
                Math.Min(
                    10,
                    preferences.WaterfallMinimumDb +
                    20);
        }

        preferences.WaterfallContrastPercent =
            Math.Clamp(
                preferences.WaterfallContrastPercent,
                25,
                400);

        return
            preferences;
    }
}
