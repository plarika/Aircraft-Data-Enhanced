#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepositoryRoot = ".",
    [string]$OutputZip = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepositoryRoot).ProviderPath
$parent = Split-Path -Parent $root
$name = Split-Path -Leaf $root

if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = Join-Path $parent ($name + "-source.zip")
}

$python = $null
$pythonArguments = @()
if (Get-Command py -ErrorAction SilentlyContinue) {
    $python = "py"
    $pythonArguments = @("-3")
}
elseif (Get-Command python -ErrorAction SilentlyContinue) {
    $python = "python"
}
else {
    throw "Python 3 was not found."
}

$staging = Join-Path $env:TEMP ("ADE-public-release-" + [Guid]::NewGuid().ToString("N"))
$stagedRoot = Join-Path $staging $name
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -LiteralPath $root -Destination $stagedRoot -Recurse -Force

try {
    $forbiddenDirectories = @(
        "bin", "obj", ".vs", "artifacts", "captures", "analysis",
        "diagnostics", "__pycache__", ".venv", "venv"
    )

    Get-ChildItem -LiteralPath $stagedRoot -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $forbiddenDirectories -contains $_.Name } |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }

    $forbiddenExtensions = @(
        ".log", ".sqlite", ".sqlite3", ".jsonl", ".wal", ".shm",
        ".iqf32", ".cf32", ".wav", ".mp3", ".mp4", ".mkv", ".avi"
    )

    Get-ChildItem -LiteralPath $stagedRoot -File -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $forbiddenExtensions -contains $_.Extension.ToLowerInvariant() -or
            $_.Name -eq "BUILD_INSTALL.log"
        } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
        }

    # Proprietary SDR# SDK references are local build inputs only.
    Remove-Item -LiteralPath (Join-Path $stagedRoot "lib\SDRSharp.Common.dll") -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $stagedRoot "lib\SDRSharp.Radio.dll") -Force -ErrorAction SilentlyContinue

    & $python @pythonArguments (Join-Path $stagedRoot "tools\generate_release_manifest.py") $stagedRoot
    if ($LASTEXITCODE -ne 0) { throw "Failed to generate RELEASE_MANIFEST.json." }

    & $python @pythonArguments (Join-Path $stagedRoot "tools\verify_release_manifest.py") $stagedRoot
    if ($LASTEXITCODE -ne 0) { throw "Release manifest verification failed." }

    & $python @pythonArguments (Join-Path $stagedRoot "tools\audit_public_repository.py") $stagedRoot --strict-public
    if ($LASTEXITCODE -ne 0) { throw "Strict public repository audit failed." }

    if (Test-Path -LiteralPath $OutputZip) {
        Remove-Item -LiteralPath $OutputZip -Force
    }

    Compress-Archive -Path $stagedRoot -DestinationPath $OutputZip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $OutputZip -Algorithm SHA256).Hash.ToLowerInvariant()

    Write-Host "[OK] Public source release created: $OutputZip"
    Write-Host "[OK] SHA-256: $hash"
    Write-Host "[OK] Local SDK DLLs remain untouched in the developer workspace."
    Write-Host "[OK] Public archive excludes SDK binaries, runtime data, build outputs and personal paths."
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
