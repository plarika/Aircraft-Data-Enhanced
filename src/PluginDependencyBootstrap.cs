// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace SDRSharp.AircraftDataEnhanced;

internal static class PluginDependencyBootstrap
{
    private static string _pluginDirectory = AppContext.BaseDirectory;

    // Intentional for a dynamically loaded SDR# plugin: register dependency
    // resolution before Microsoft.Data.Sqlite is first used.
    [ModuleInitializer]
    public static void Initialize()
    {
        var assembly = typeof(PluginDependencyBootstrap).Assembly;
        _pluginDirectory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;

        AssemblyLoadContext.Default.Resolving += ResolveManagedAssembly;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveUnmanagedLibrary;
    }

    private static Assembly? ResolveManagedAssembly(
        AssemblyLoadContext context,
        AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (string.IsNullOrWhiteSpace(simpleName))
            return null;

        var candidate = Path.Combine(_pluginDirectory, simpleName + ".dll");
        if (!File.Exists(candidate))
            return null;

        try
        {
            return context.LoadFromAssemblyPath(Path.GetFullPath(candidate));
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr ResolveUnmanagedLibrary(
        Assembly requestingAssembly,
        string libraryName)
    {
        _ = requestingAssembly;

        var fileName = libraryName.EndsWith(
            ".dll",
            StringComparison.OrdinalIgnoreCase)
                ? libraryName
                : libraryName + ".dll";

        var candidates = new[]
        {
            Path.Combine(_pluginDirectory, fileName),
            Path.Combine(_pluginDirectory, "runtimes", "win-x86", "native", fileName),
            Path.Combine(_pluginDirectory, "runtimes", "win", "native", fileName)
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                return NativeLibrary.Load(Path.GetFullPath(candidate));
            }
            catch
            {
            }
        }

        return IntPtr.Zero;
    }
}
