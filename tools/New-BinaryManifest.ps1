#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory, [string]$ManifestPath)
$ErrorActionPreference = "Stop"
$root=(Resolve-Path -LiteralPath $OutputDirectory).ProviderPath
if (-not $ManifestPath) { $ManifestPath=Join-Path $root "BINARY_MANIFEST.json" }
$files=Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object FullName -ne ([IO.Path]::GetFullPath($ManifestPath)) | ForEach-Object {
  $relative = $_.FullName.Substring($root.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
  $relative = $relative.Replace([IO.Path]::DirectorySeparatorChar, '/')
  [ordered]@{ path=$relative; length=$_.Length; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
}
$result=[ordered]@{ schemaVersion=1; version="1.0.0"; generatedAt=(Get-Date).ToUniversalTime().ToString("o"); files=@($files) }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($ManifestPath), ($result|ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Host "[OK] Binary manifest: $ManifestPath"
