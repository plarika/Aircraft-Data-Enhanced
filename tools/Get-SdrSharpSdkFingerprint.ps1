#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$LibDirectory,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($LibDirectory)) {
    $LibDirectory = Join-Path $scriptDirectory "..\lib"
}

$resolvedLib = Resolve-Path -LiteralPath $LibDirectory -ErrorAction Stop
$lib = $resolvedLib.ProviderPath

$items = foreach ($name in @(
    "SDRSharp.Common.dll",
    "SDRSharp.Radio.dll"
)) {
    $path = Join-Path $lib $name

    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing SDK DLL: $path"
    }

    $assembly = [System.Reflection.AssemblyName]::GetAssemblyName($path)
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    $file = Get-Item -LiteralPath $path

    [ordered]@{
        name = $name
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        length = [int64]$file.Length
        assemblyVersion = $assembly.Version.ToString()
        fileVersion = [string]$version.FileVersion
        productVersion = [string]$version.ProductVersion
    }
}

$result = [ordered]@{
    schemaVersion = 2
    capturedAt = (Get-Date).ToUniversalTime().ToString("o")
    platform = "x86"
    targetFramework = "net9.0-windows"
    files = @($items)
}

$json = $result | ConvertTo-Json -Depth 8

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    [System.IO.File]::WriteAllText(
        $fullOutputPath,
        $json,
        [System.Text.UTF8Encoding]::new($false)
    )
}

$json