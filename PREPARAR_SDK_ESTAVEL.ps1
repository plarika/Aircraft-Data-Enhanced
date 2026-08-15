#requires -Version 5.1
[CmdletBinding()]
param(
    [int]$HostRevision = 0,
    [string]$HostExecutable
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRootDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($HostExecutable)) {
    $HostExecutable = Read-Host "Full path to SDRSharp.dotnet9.exe"
}

$resolvedHost = Resolve-Path -LiteralPath $HostExecutable -ErrorAction Stop
$hostPath = $resolvedHost.ProviderPath

if ((Split-Path -Leaf $hostPath) -ne "SDRSharp.dotnet9.exe") {
    throw "HostExecutable must point to SDRSharp.dotnet9.exe."
}

$hostVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($hostPath)
$versionText = "$($hostVersion.FileVersion) $($hostVersion.ProductVersion)"
$revisionMatches = [regex]::Matches($versionText, '(?<!\d)(1921|1922)(?!\d)')
$detectedRevisions = @($revisionMatches | ForEach-Object { [int]$_.Value } | Select-Object -Unique)

if ($HostRevision -eq 0 -and $detectedRevisions.Count -eq 1) {
    $HostRevision = $detectedRevisions[0]
}

if ($HostRevision -eq 0) {
    $HostRevision = [int](Read-Host "SDR# host revision (1921 production or 1922 beta x86)")
}

if ($HostRevision -notin @(1921, 1922)) {
    throw "Supported target revisions are 1921 and 1922."
}

if (
    $detectedRevisions.Count -eq 1 -and
    $detectedRevisions[0] -ne $HostRevision
) {
    throw "The executable metadata indicates revision $($detectedRevisions[0]), not $HostRevision."
}

$libDirectory = Join-Path $scriptRootDirectory "lib"
$fingerprintScript = Join-Path $scriptRootDirectory "tools\Get-SdrSharpSdkFingerprint.ps1"
$fingerprintJson = & $fingerprintScript -LibDirectory $libDirectory

if (-not $?) {
    throw "Failed to calculate the SDR# SDK fingerprint."
}

$fingerprint = ConvertFrom-Json -InputObject ($fingerprintJson -join [Environment]::NewLine)

$approvedPath = Join-Path $scriptRootDirectory "sdk\approved-sdks.json"
$approvedDirectory = Split-Path -Parent $approvedPath
[System.IO.Directory]::CreateDirectory($approvedDirectory) | Out-Null

if (Test-Path -LiteralPath $approvedPath -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $approvedPath -Raw | ConvertFrom-Json
}
else {
    $manifest = [pscustomobject]@{
        schemaVersion = 2
        activeHostRevision = $HostRevision
        approved = @()
    }
}

function Test-SameSdkFingerprint {
    param(
        [Parameter(Mandatory = $true)]
        $Entry,

        [Parameter(Mandatory = $true)]
        $CurrentFiles
    )

    foreach ($currentFile in @($CurrentFiles)) {
        $expected = @(
            $Entry.files |
            Where-Object { $_.name -eq $currentFile.name }
        )

        if ($expected.Count -ne 1) {
            return $false
        }

        foreach ($propertyName in @(
            "sha256",
            "length",
            "assemblyVersion",
            "fileVersion",
            "productVersion"
        )) {
            $expectedValue = [string]$expected[0].$propertyName
            $currentValue = [string]$currentFile.$propertyName

            if ($expectedValue -ne $currentValue) {
                return $false
            }
        }
    }

    return $true
}

$existingEntries = @()

if (
    $manifest.PSObject.Properties.Name -contains "approved" -and
    $null -ne $manifest.approved
) {
    $existingEntries = @($manifest.approved)
}

$keptEntries = @()

foreach ($candidate in $existingEntries) {
    $sameRevision = [int]$candidate.hostRevision -eq $HostRevision
    $sameFingerprint = Test-SameSdkFingerprint -Entry $candidate -CurrentFiles $fingerprint.files

    if (-not $sameRevision -and -not $sameFingerprint) {
        $keptEntries += $candidate
    }
}

$hostFile = Get-Item -LiteralPath $hostPath
$hostMetadata = [ordered]@{
    name = $hostFile.Name
    sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $hostPath).Hash.ToLowerInvariant()
    length = [int64]$hostFile.Length
    fileVersion = [string]$hostVersion.FileVersion
    productVersion = [string]$hostVersion.ProductVersion
}

$entry = [ordered]@{
    label = "Local SDK registered against the exact SDR# host executable; final stable approval requires smoke and soak tests"
    hostRevision = $HostRevision
    registeredAt = (Get-Date).ToUniversalTime().ToString("o")
    hostExecutable = $hostMetadata
    files = @($fingerprint.files)
}

if (-not ($manifest.PSObject.Properties.Name -contains "schemaVersion")) {
    $manifest | Add-Member -NotePropertyName schemaVersion -NotePropertyValue 2
}
else {
    $manifest.schemaVersion = 2
}

if (-not ($manifest.PSObject.Properties.Name -contains "activeHostRevision")) {
    $manifest | Add-Member -NotePropertyName activeHostRevision -NotePropertyValue $HostRevision
}
else {
    $manifest.activeHostRevision = $HostRevision
}

if (-not ($manifest.PSObject.Properties.Name -contains "approved")) {
    $manifest | Add-Member -NotePropertyName approved -NotePropertyValue @()
}

$manifest.approved = @($keptEntries) + @($entry)

[System.IO.File]::WriteAllText(
    $approvedPath,
    ($manifest | ConvertTo-Json -Depth 10),
    [System.Text.UTF8Encoding]::new($false)
)

Write-Host "[OK] Exact SDK hashes registered for SDR# revision $HostRevision."
Write-Host "[OK] Host executable: $($hostMetadata.fileVersion) / $($hostMetadata.productVersion)"

$verifyScript = Join-Path $scriptRootDirectory "tools\Verify-SdrSharpSdk.ps1"
$verifyParameters = @{
    LibDirectory = $libDirectory
    ApprovedPath = $approvedPath
    HostRevision = $HostRevision
}

& $verifyScript @verifyParameters

if (-not $?) {
    throw "The exact SDR# SDK verification failed."
}

$pythonCommand = $null
$pythonArguments = @()

if (Get-Command py -ErrorAction SilentlyContinue) {
    $pythonCommand = "py"
    $pythonArguments = @("-3")
}
elseif (Get-Command python -ErrorAction SilentlyContinue) {
    $pythonCommand = "python"
}
else {
    throw "Python 3.10 or later was not found."
}

$manifestScript = Join-Path $scriptRootDirectory "tools\generate_release_manifest.py"
& $pythonCommand @pythonArguments $manifestScript $scriptRootDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao atualizar RELEASE_MANIFEST.json."
}

Write-Host "[OK] Active exact SDK target: SDR# revision $HostRevision."
Write-Host "[INFO] Execute BUILD_E_INSTALAR_TUDO.bat, perform the host smoke test, then run the soak gates."