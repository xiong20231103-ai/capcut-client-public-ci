$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$gac = Join-Path $env:WINDIR 'assembly\GAC_MSIL'
$windowsBase = Get-ChildItem (Join-Path $gac 'WindowsBase') -Recurse -Filter 'WindowsBase.dll' | Select-Object -First 1 -ExpandProperty FullName
$automationClient = Get-ChildItem (Join-Path $gac 'UIAutomationClient') -Recurse -Filter 'UIAutomationClient.dll' | Select-Object -First 1 -ExpandProperty FullName
$automationTypes = Get-ChildItem (Join-Path $gac 'UIAutomationTypes') -Recurse -Filter 'UIAutomationTypes.dll' | Select-Object -First 1 -ExpandProperty FullName
$sources = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Filter '*.cs' | Where-Object { $_.Name -ne 'Program.cs' } | ForEach-Object { $_.FullName }
$output = Join-Path $env:TEMP 'CapCutUiSmokeTests.exe'
& $compiler /nologo /target:exe /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:$windowsBase /reference:$automationClient /reference:$automationTypes /out:$output $sources (Join-Path $repoRoot 'tests\CapCutUiSmokeTests.cs')
if ($LASTEXITCODE -ne 0) { throw "UI smoke test build failed with exit code $LASTEXITCODE" }
& $output
if ($LASTEXITCODE -ne 0) { throw "UI smoke test failed with exit code $LASTEXITCODE" }
