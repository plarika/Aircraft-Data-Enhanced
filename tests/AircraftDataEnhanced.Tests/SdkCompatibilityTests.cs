// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace SDRSharp.AircraftDataEnhanced;

internal static class SdkCompatibilityTests
{
    public static void Run()
    {
        var sdkDirectory =
            Path.Combine(
                AppContext.BaseDirectory,
                "sdk-reference");

        var approvedPath =
            Path.Combine(
                AppContext.BaseDirectory,
                "sdk",
                "approved-sdks.json");

        Assert(
            Directory.Exists(
                sdkDirectory),
            "The inert SDK metadata fixture directory is missing.");

        Assert(
            File.Exists(
                approvedPath),
            "approved-sdks.json is missing from the test output.");

        using var document =
            JsonDocument.Parse(
                File.ReadAllText(
                    approvedPath));

        var root =
            document.RootElement;

        Assert(
            root.TryGetProperty(
                "activeHostRevision",
                out var activeRevisionElement) &&
            activeRevisionElement.ValueKind ==
                JsonValueKind.Number,
            "The active SDR# host revision is not registered.");

        var activeRevision =
            activeRevisionElement.GetInt32();

        var matchingEntries =
            root.GetProperty(
                    "approved")
                .EnumerateArray()
                .Where(
                    entry =>
                        entry.GetProperty(
                                "hostRevision")
                            .GetInt32() ==
                        activeRevision)
                .ToArray();

        Assert(
            matchingEntries.Length == 1,
            $"Expected one exact SDK registration for revision {activeRevision}; found {matchingEntries.Length}.");

        var registration =
            matchingEntries[0];

        ValidateAssembly(
            Path.Combine(
                sdkDirectory,
                "SDRSharp.Radio.dll"),
            FindExpectedFile(
                registration,
                "SDRSharp.Radio.dll"),
            "SDRSharp.Radio",
            [
                new MetadataContract(
                    "SDRSharp.Radio",
                    "Complex",
                    ["Real", "Imag"],
                    [],
                    []),
                new MetadataContract(
                    "SDRSharp.Radio",
                    "IIQProcessor",
                    [],
                    ["Process"],
                    []),
                new MetadataContract(
                    "SDRSharp.Radio",
                    "IStreamProcessor",
                    [],
                    [],
                    ["SampleRate"]),
                new MetadataContract(
                    "SDRSharp.Radio",
                    "IBaseProcessor",
                    [],
                    [],
                    ["Enabled"])
            ]);

        ValidateAssembly(
            Path.Combine(
                sdkDirectory,
                "SDRSharp.Common.dll"),
            FindExpectedFile(
                registration,
                "SDRSharp.Common.dll"),
            "SDRSharp.Common",
            [
                new MetadataContract(
                    "SDRSharp.Common",
                    "ISharpPlugin",
                    [],
                    ["Initialize"],
                    ["Gui"]),
                new MetadataContract(
                    "SDRSharp.Common",
                    "ISharpControl",
                    [],
                    ["RegisterStreamHook"],
                    ["Frequency"])
            ]);
    }

    private static JsonElement FindExpectedFile(
        JsonElement registration,
        string fileName)
    {
        var matches =
            registration.GetProperty(
                    "files")
                .EnumerateArray()
                .Where(
                    file =>
                        string.Equals(
                            file.GetProperty(
                                    "name")
                                .GetString(),
                            fileName,
                            StringComparison.Ordinal))
                .ToArray();

        Assert(
            matches.Length == 1,
            $"The approved SDK registration does not contain exactly one {fileName} entry.");

        return matches[0];
    }

    private static void ValidateAssembly(
        string path,
        JsonElement expected,
        string expectedAssemblyName,
        IReadOnlyList<MetadataContract> contracts)
    {
        Assert(
            File.Exists(
                path),
            $"SDK metadata fixture is missing: {path}");

        var file =
            new FileInfo(
                path);

        AssertEqual(
            expected.GetProperty(
                    "length")
                .GetInt64(),
            file.Length,
            $"{file.Name} length");

        using (
            var stream =
                File.OpenRead(
                    path))
        {
            var hash =
                Convert.ToHexString(
                        SHA256.HashData(
                            stream))
                    .ToLowerInvariant();

            AssertEqual(
                expected.GetProperty(
                        "sha256")
                    .GetString() ??
                    string.Empty,
                hash,
                $"{file.Name} SHA-256");
        }

        var versionInfo =
            FileVersionInfo.GetVersionInfo(
                path);

        AssertEqual(
            ReadExpectedString(
                expected,
                "fileVersion"),
            versionInfo.FileVersion ??
            string.Empty,
            $"{file.Name} FileVersion");

        AssertEqual(
            ReadExpectedString(
                expected,
                "productVersion"),
            versionInfo.ProductVersion ??
            string.Empty,
            $"{file.Name} ProductVersion");

        using var metadataStream =
            File.OpenRead(
                path);

        using var peReader =
            new PEReader(
                metadataStream,
                PEStreamOptions.PrefetchMetadata);

        Assert(
            peReader.HasMetadata,
            $"{file.Name} does not contain CLI metadata.");

        var reader =
            peReader.GetMetadataReader();

        Assert(
            reader.IsAssembly,
            $"{file.Name} is not an assembly metadata image.");

        var assemblyDefinition =
            reader.GetAssemblyDefinition();

        var assemblyName =
            reader.GetString(
                assemblyDefinition.Name);

        AssertEqual(
            expectedAssemblyName,
            assemblyName,
            $"{file.Name} assembly name");

        AssertEqual(
            ReadExpectedString(
                expected,
                "assemblyVersion"),
            assemblyDefinition.Version.ToString(),
            $"{file.Name} AssemblyVersion");

        foreach (var contract in
                 contracts)
        {
            ValidateContract(
                reader,
                file.Name,
                contract);
        }
    }

    private static void ValidateContract(
        MetadataReader reader,
        string fileName,
        MetadataContract contract)
    {
        var matchingTypes =
            reader.TypeDefinitions
                .Select(
                    handle =>
                        reader.GetTypeDefinition(
                            handle))
                .Where(
                    definition =>
                        string.Equals(
                            reader.GetString(
                                definition.Namespace),
                            contract.Namespace,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            reader.GetString(
                                definition.Name),
                            contract.Name,
                            StringComparison.Ordinal))
                .ToArray();

        Assert(
            matchingTypes.Length == 1,
            $"{fileName} is missing metadata type {contract.Namespace}.{contract.Name}.");

        var type =
            matchingTypes[0];

        var fields =
            type.GetFields()
                .Select(
                    handle =>
                        reader.GetString(
                            reader.GetFieldDefinition(
                                    handle)
                                .Name))
                .ToHashSet(
                    StringComparer.Ordinal);

        var methods =
            type.GetMethods()
                .Select(
                    handle =>
                        reader.GetString(
                            reader.GetMethodDefinition(
                                    handle)
                                .Name))
                .ToHashSet(
                    StringComparer.Ordinal);

        var properties =
            type.GetProperties()
                .Select(
                    handle =>
                        reader.GetString(
                            reader.GetPropertyDefinition(
                                    handle)
                                .Name))
                .ToHashSet(
                    StringComparer.Ordinal);

        foreach (var field in
                 contract.Fields)
        {
            Assert(
                fields.Contains(
                    field),
                $"{contract.Namespace}.{contract.Name}.{field} field is missing.");
        }

        foreach (var method in
                 contract.Methods)
        {
            Assert(
                methods.Contains(
                    method),
                $"{contract.Namespace}.{contract.Name}.{method} method is missing.");
        }

        foreach (var property in
                 contract.Properties)
        {
            Assert(
                properties.Contains(
                    property),
                $"{contract.Namespace}.{contract.Name}.{property} property is missing.");
        }
    }

    private static string ReadExpectedString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(
                propertyName,
                out var property) ||
            property.ValueKind ==
                JsonValueKind.Null)
        {
            return string.Empty;
        }

        return property.GetString() ??
               string.Empty;
    }

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string label)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(
                expected,
                actual))
        {
            throw new InvalidOperationException(
                $"{label} mismatch. Expected '{expected}'; found '{actual}'.");
        }
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                message);
        }
    }

    private sealed record MetadataContract(
        string Namespace,
        string Name,
        string[] Fields,
        string[] Methods,
        string[] Properties);
}