param([string]$Version = '')
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim() }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'UiSmoke.ps1')
if ($LASTEXITCODE -ne 0) { throw 'UI smoke tests failed.' }

$releaseDir = Join-Path $repoRoot 'release'
$stage = Join-Path $env:TEMP ("CapCutAutoCaptionClient-" + [guid]::NewGuid().ToString('N'))
$zipName = "CapCutAutoCaptionClient-v$Version-win-x64.zip"
$zipPath = Join-Path $releaseDir $zipName
New-Item -ItemType Directory -Path $releaseDir,$stage,(Join-Path $stage 'docs'),(Join-Path $stage 'data'),(Join-Path $stage 'logs') -Force | Out-Null
try {
  Copy-Item -LiteralPath (Join-Path $repoRoot 'bin\CapCutBatchClient.exe') -Destination $stage
  Copy-Item -LiteralPath (Join-Path $repoRoot 'bin\tools') -Destination (Join-Path $stage 'tools') -Recurse -Force
  Copy-Item -LiteralPath (Join-Path $repoRoot 'config\capcut.config.example.json') -Destination $stage
  Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'),(Join-Path $repoRoot 'CHANGELOG.md') -Destination $stage
  # Release packages contain user-facing installation notes only.  Timestamped
  # deployment evidence may contain machine-specific paths, task IDs or
  # historical state and must remain in the source tree, never in a portable
  # client archive.
  Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -Filter '*.md' |
    Where-Object { $_.Name -notmatch '^(?:20\d{2}[-_])|stable-marker-hard-gate' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage 'docs') }
  Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\Install.ps1') -Destination $stage
  Set-Content -LiteralPath (Join-Path $stage 'StartClient.cmd') -Value @('@echo off','start "" "%~dp0CapCutBatchClient.exe"') -Encoding ASCII
  if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
  Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal
} finally {
  if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$hashPath = Join-Path $releaseDir ("CapCutAutoCaptionClient-v$Version-win-x64.sha256")
Set-Content -LiteralPath $hashPath -Value "$hash  $zipName" -Encoding ASCII
Write-Output "Package: $zipPath"
Write-Output "SHA256: $hash"
