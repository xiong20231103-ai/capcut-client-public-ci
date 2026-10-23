param(
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceDir = Join-Path $repoRoot 'src'
$outputDir = Join-Path $repoRoot 'bin'
$output = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $outputDir 'CapCutBatchClient.exe'
} else {
    [System.IO.Path]::GetFullPath($OutputPath)
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found. Enable .NET Framework 4.x.' }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
$gac = Join-Path $env:WINDIR 'assembly\GAC_MSIL'
$windowsBase = Get-ChildItem (Join-Path $gac 'WindowsBase') -Recurse -Filter 'WindowsBase.dll' | Select-Object -First 1 -ExpandProperty FullName
$automationClient = Get-ChildItem (Join-Path $gac 'UIAutomationClient') -Recurse -Filter 'UIAutomationClient.dll' | Select-Object -First 1 -ExpandProperty FullName
$automationTypes = Get-ChildItem (Join-Path $gac 'UIAutomationTypes') -Recurse -Filter 'UIAutomationTypes.dll' | Select-Object -First 1 -ExpandProperty FullName
$sources = Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:$windowsBase /reference:$automationClient /reference:$automationTypes /out:$output $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $repoRoot 'config\capcut.config.example.json') -Destination (Join-Path (Split-Path -Parent $output) 'capcut.config.example.json') -Force
$runtimeTools = Join-Path (Split-Path -Parent $output) 'tools'
New-Item -ItemType Directory -Path $runtimeTools -Force | Out-Null
Copy-Item -Path (Join-Path $repoRoot 'tools\*') -Destination $runtimeTools -Recurse -Force
$probeSource = Join-Path $repoRoot 'tools-src\CapCutFileDialogProbe.cs'
$policySource = Join-Path $sourceDir 'FileDialogIdentityPolicy.cs'
if (Test-Path -LiteralPath $probeSource) {
    $probeOutput = Join-Path $runtimeTools 'CapCutFileDialogProbe.exe'
    & $compiler /nologo /target:exe /optimize+ /codepage:65001 `
        /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll `
        /reference:$windowsBase /reference:$automationClient /reference:$automationTypes `
        /out:$probeOutput $probeSource $policySource
    if ($LASTEXITCODE -ne 0) { throw "File dialog probe build failed with exit code $LASTEXITCODE" }
}
$nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
if ($null -eq $nodeCommand) { throw 'Node.js runtime was not found; the fixed draft-style engine cannot be bundled.' }
$nodeRuntimeDir = Join-Path $runtimeTools 'node'
New-Item -ItemType Directory -Path $nodeRuntimeDir -Force | Out-Null
Copy-Item -LiteralPath $nodeCommand.Source -Destination (Join-Path $nodeRuntimeDir 'node.exe') -Force
Write-Output "Built: $output"
