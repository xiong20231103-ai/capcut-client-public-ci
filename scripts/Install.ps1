$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'CapCutBatchClient.exe'
$example = Join-Path $root 'capcut.config.example.json'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $example)) { throw 'Portable package is incomplete. Extract the entire ZIP before installing.' }
if (-not (Test-Path -LiteralPath (Join-Path $root 'capcut.config.json'))) { Copy-Item -LiteralPath $example -Destination (Join-Path $root 'capcut.config.json') }
New-Item -ItemType Directory -Path (Join-Path $root 'data'),(Join-Path $root 'logs') -Force | Out-Null
Get-ChildItem -LiteralPath $root -File | Unblock-File -ErrorAction SilentlyContinue
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'CapCut Auto Caption Client.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $root
$shortcut.Description = 'CapCut auto caption batch client'
$shortcut.Save()
Write-Host "Installed shortcut: $shortcutPath" -ForegroundColor Green
Start-Process -FilePath $exe -WorkingDirectory $root
