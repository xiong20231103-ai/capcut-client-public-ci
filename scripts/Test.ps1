$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$testRuntime = Join-Path $env:TEMP 'CapCutCoreTestsRuntime'
New-Item -ItemType Directory -Path $testRuntime -Force | Out-Null
$output = Join-Path $testRuntime 'CapCutCoreTests.exe'
$gac = Join-Path $env:WINDIR 'assembly\GAC_MSIL'
$windowsBase = Get-ChildItem (Join-Path $gac 'WindowsBase') -Recurse -Filter 'WindowsBase.dll' | Select-Object -First 1 -ExpandProperty FullName
$automationClient = Get-ChildItem (Join-Path $gac 'UIAutomationClient') -Recurse -Filter 'UIAutomationClient.dll' | Select-Object -First 1 -ExpandProperty FullName
& $compiler /nologo /target:exe /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:$windowsBase /reference:$automationClient /out:$output (Join-Path $repoRoot 'src\Core.cs') (Join-Path $repoRoot 'src\CapCutVersionGuard.cs') (Join-Path $repoRoot 'src\KnownCapCutFailures.cs') (Join-Path $repoRoot 'src\CapCutSessionOwnership.cs') (Join-Path $repoRoot 'src\MediaPlanning.cs') (Join-Path $repoRoot 'src\CategoryBatch.cs') (Join-Path $repoRoot 'src\Handoff.cs') (Join-Path $repoRoot 'src\PostProcessing.cs') (Join-Path $repoRoot 'src\OutputQuality.cs') (Join-Path $repoRoot 'src\SamplePreparation.cs') (Join-Path $repoRoot 'src\DraftTimelineAssembly.cs') (Join-Path $repoRoot 'src\CapCutCliDraftStyleEngine.cs') (Join-Path $repoRoot 'src\DraftSubtitleStyle.cs') (Join-Path $repoRoot 'src\DraftCardVision.cs') (Join-Path $repoRoot 'src\DraftTitleOcr.cs') (Join-Path $repoRoot 'src\DraftRootIsolation.cs') (Join-Path $repoRoot 'src\CapCutHomeShellPolicy.cs') (Join-Path $repoRoot 'src\CapCutImportDialogPolicy.cs') (Join-Path $repoRoot 'src\FileDialogIdentityPolicy.cs') (Join-Path $repoRoot 'src\HdrNoticeVision.cs') (Join-Path $repoRoot 'src\BatchRunner.cs') (Join-Path $repoRoot 'src\TimelineVision.cs') (Join-Path $repoRoot 'src\InteractionVision.cs') (Join-Path $repoRoot 'tests\CapCutCoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw "Core test build failed with exit code $LASTEXITCODE" }
$runtimeTools = Join-Path $testRuntime 'tools'
New-Item -ItemType Directory -Path $runtimeTools -Force | Out-Null
Copy-Item -Path (Join-Path $repoRoot 'tools\*') -Destination $runtimeTools -Recurse -Force
$nodeCommand = Get-Command node.exe -ErrorAction Stop
$nodeRuntimeDir = Join-Path $runtimeTools 'node'
New-Item -ItemType Directory -Path $nodeRuntimeDir -Force | Out-Null
Copy-Item -LiteralPath $nodeCommand.Source -Destination (Join-Path $nodeRuntimeDir 'node.exe') -Force
& $output
if ($LASTEXITCODE -ne 0) { throw "Core tests failed with exit code $LASTEXITCODE" }
& $nodeCommand.Source (Join-Path $repoRoot 'tests\CaptionMirrorPolicyTests.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Caption mirror policy tests failed' }
