// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

/// <summary>
/// Resolves and creates all writable runtime paths outside the SDR# installation
/// directory. Legacy capture and analysis files are migrated on a best-effort
/// basis without overwriting files already present in the destination.
/// </summary>
internal static class RuntimeDataPaths
{
    private const string ProductDirectoryName =
        "AircraftDataEnhanced";

    private static readonly Lazy<string> RootPath =
        new(
            ResolveRootDirectory,
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<bool> Initialization =
        new(
            InitializeCore,
            LazyThreadSafetyMode.ExecutionAndPublication);

    public static string RootDirectory
    {
        get
        {
            EnsureInitialized();
            return RootPath.Value;
        }
    }

    public static string CapturesDirectory
    {
        get
        {
            EnsureInitialized();
            return EnsureDirectory(
                Path.Combine(
                    RootPath.Value,
                    "captures"));
        }
    }

    public static string AnalysisDirectory
    {
        get
        {
            EnsureInitialized();
            return EnsureDirectory(
                Path.Combine(
                    RootPath.Value,
                    "analysis"));
        }
    }

    public static string ExportsDirectory
    {
        get
        {
            EnsureInitialized();
            return EnsureDirectory(
                Path.Combine(
                    RootPath.Value,
                    "exports"));
        }
    }

    public static string DatabasePath
    {
        get
        {
            EnsureInitialized();
            return Path.Combine(
                RootPath.Value,
                "aircraft-history.sqlite3");
        }
    }

    public static string PreferencesPath
    {
        get
        {
            EnsureInitialized();
            return Path.Combine(
                RootPath.Value,
                "ui-preferences.json");
        }
    }

    public static string StartupLogPath
    {
        get
        {
            EnsureInitialized();
            return Path.Combine(
                RootPath.Value,
                "startup-error.log");
        }
    }

    public static void EnsureInitialized() =>
        _ = Initialization.Value;

    private static bool InitializeCore()
    {
        EnsureDirectory(
            RootPath.Value);

        EnsureDirectory(
            Path.Combine(
                RootPath.Value,
                "captures"));

        EnsureDirectory(
            Path.Combine(
                RootPath.Value,
                "analysis"));

        EnsureDirectory(
            Path.Combine(
                RootPath.Value,
                "exports"));

        MigrateLegacyRuntimeData();

        return true;
    }

    private static string ResolveRootDirectory()
    {
        var configuredRoot =
            Environment.GetEnvironmentVariable(
                "AIRCRAFT_DATA_ENHANCED_DATA_ROOT");

        if (!string.IsNullOrWhiteSpace(
                configuredRoot))
        {
            return Path.GetFullPath(
                configuredRoot);
        }

        var localAppData =
            Environment.GetFolderPath(
                Environment.SpecialFolder
                    .LocalApplicationData);

        if (string.IsNullOrWhiteSpace(
                localAppData))
        {
            throw new InvalidOperationException(
                "Windows LocalApplicationData could not be resolved.");
        }

        return Path.GetFullPath(
            Path.Combine(
                localAppData,
                ProductDirectoryName));
    }

    private static string EnsureDirectory(
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            path);

        var fullPath =
            Path.GetFullPath(
                path);

        Directory.CreateDirectory(
            fullPath);

        return fullPath;
    }

    private static void MigrateLegacyRuntimeData()
    {
        var legacyRoot =
            Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Plugins",
                    ProductDirectoryName));

        if (!Directory.Exists(
                legacyRoot))
        {
            return;
        }

        MigrateDirectory(
            Path.Combine(
                legacyRoot,
                "captures"),
            Path.Combine(
                RootPath.Value,
                "captures"));

        MigrateDirectory(
            Path.Combine(
                legacyRoot,
                "analysis"),
            Path.Combine(
                RootPath.Value,
                "analysis"));

        MigrateFile(
            Path.Combine(
                legacyRoot,
                "startup-error.log"),
            Path.Combine(
                RootPath.Value,
                "startup-error.log"));

        DeleteEmptyDirectories(
            legacyRoot);
    }

    private static void MigrateDirectory(
        string sourceDirectory,
        string destinationDirectory)
    {
        try
        {
            if (!Directory.Exists(
                    sourceDirectory))
            {
                return;
            }

            var sourceRoot =
                Path.GetFullPath(
                    sourceDirectory);

            var destinationRoot =
                EnsureDirectory(
                    destinationDirectory);

            foreach (var sourcePath in
                     Directory.EnumerateFiles(
                         sourceRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relative =
                    Path.GetRelativePath(
                        sourceRoot,
                        sourcePath);

                if (relative.StartsWith(
                        "..",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var destinationPath =
                    Path.GetFullPath(
                        Path.Combine(
                            destinationRoot,
                            relative));

                if (!destinationPath.StartsWith(
                        destinationRoot +
                        Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                MigrateFile(
                    sourcePath,
                    destinationPath);
            }

            DeleteEmptyDirectories(
                sourceRoot);
        }
        catch
        {
            // Migration is best-effort. Existing runtime data must never
            // prevent the plugin from starting.
        }
    }

    private static void MigrateFile(
        string sourcePath,
        string destinationPath)
    {
        try
        {
            if (!File.Exists(
                    sourcePath) ||
                File.Exists(
                    destinationPath))
            {
                return;
            }

            var destinationDirectory =
                Path.GetDirectoryName(
                    destinationPath);

            if (!string.IsNullOrWhiteSpace(
                    destinationDirectory))
            {
                Directory.CreateDirectory(
                    destinationDirectory);
            }

            File.Copy(
                sourcePath,
                destinationPath,
                overwrite: false);

            var sourceLength =
                new FileInfo(
                    sourcePath)
                    .Length;

            var destinationLength =
                new FileInfo(
                    destinationPath)
                    .Length;

            if (sourceLength ==
                destinationLength)
            {
                File.Delete(
                    sourcePath);
            }
        }
        catch
        {
            // Keep the source file if migration cannot be completed safely.
        }
    }

    private static void DeleteEmptyDirectories(
        string root)
    {
        try
        {
            if (!Directory.Exists(
                    root))
            {
                return;
            }

            foreach (var directory in
                     Directory
                         .EnumerateDirectories(
                             root,
                             "*",
                             SearchOption.AllDirectories)
                         .OrderByDescending(
                             path =>
                                 path.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(
                        directory)
                    .Any())
                {
                    Directory.Delete(
                        directory);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(
                    root)
                .Any())
            {
                Directory.Delete(
                    root);
            }
        }
        catch
        {
            // Cleanup is best-effort.
        }
    }
}
