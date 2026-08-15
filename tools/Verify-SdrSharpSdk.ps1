#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$LibDirectory,
    [string]$ApprovedPath,
    [int]$HostRevision = 0
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($LibDirectory)) {
    $LibDirectory = Join-Path $scriptDirectory "..\lib"
}

if ([string]::IsNullOrWhiteSpace($ApprovedPath)) {
    $ApprovedPath = Join-Path $scriptDirectory "..\sdk\approved-sdks.json"
}

$resolvedApprovedPath = Resolve-Path -LiteralPath $ApprovedPath -ErrorAction Stop
$approved = Get-Content -LiteralPath $resolvedApprovedPath.ProviderPath -Raw | ConvertFrom-Json

if ($HostRevision -eq 0) {
    if (
        $approved.PSObject.Properties.Name -contains "activeHostRevision" -and
        $null -ne $approved.activeHostRevision
    ) {
        $HostRevision = [int]$approved.activeHostRevision
    }
}

if ($HostRevision -notin @(1921, 1922)) {
    throw "No active SDR# host revision is selected. Run PREPARAR_SDK_ESTAVEL.ps1 with the real SDRSharp.dotnet9.exe."
}

$fingerprintScript = Join-Path $scriptDirectory "Get-SdrSharpSdkFingerprint.ps1"
$currentJson = & $fingerprintScript -LibDirectory $LibDirectory

if (-not $?) {
    throw "Failed to calculate the SDR# SDK fingerprint."
}

$current = ConvertFrom-Json -InputObject ($currentJson -join [Environment]::NewLine)

$entries = @(
    $approved.approved |
    Where-Object { [int]$_.hostRevision -eq $HostRevision }
)

if ($entries.Count -eq 0) {
    throw "No approved SDK fingerprint exists for SDR# revision $HostRevision."
}

if ($entries.Count -gt 1) {
    throw "More than one SDK fingerprint exists for SDR# revision $HostRevision. Re-run PREPARAR_SDK_ESTAVEL.ps1 to normalize the approval file."
}

$entry = $entries[0]
$requiredNames = @(
    "SDRSharp.Common.dll",
    "SDRSharp.Radio.dll"
)

foreach ($name in $requiredNames) {
    $actual = @($current.files | Where-Object { $_.name -eq $name })
    $expected = @($entry.files | Where-Object { $_.name -eq $name })

    if ($actual.Count -ne 1 -or $expected.Count -ne 1) {
        throw "SDK fingerprint is incomplete for $name."
    }

    $actualFile = $actual[0]
    $expectedFile = $expected[0]

    foreach ($propertyName in @(
        "sha256",
        "length",
        "assemblyVersion",
        "fileVersion",
        "productVersion"
    )) {
        $actualValue = [string]$actualFile.$propertyName
        $expectedValue = [string]$expectedFile.$propertyName

        if ($actualValue -ne $expectedValue) {
            throw "SDK mismatch for $name property $propertyName. Expected '$expectedValue'; found '$actualValue'."
        }
    }
}

Write-Host "[OK] Exact SDR# SDK matches the active revision $HostRevision registration."
Write-Host "[OK] $($entry.label)"