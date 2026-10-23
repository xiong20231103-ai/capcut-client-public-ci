param(
    [string]$ImagePath = '',
    [string[]]$ExpectedTexts = @()
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
if ([string]::IsNullOrWhiteSpace($ImagePath) -or $ExpectedTexts.Count -eq 0) {
    $requestPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'data\visible-text-ocr-request.txt'
    $request = @(Get-Content -LiteralPath $requestPath -Encoding UTF8)
    if ($request.Count -ge 2) {
        $ImagePath = $request[0]
        $ExpectedTexts = @($request | Select-Object -Skip 1)
    }
}
if ([string]::IsNullOrWhiteSpace($ImagePath)) { throw 'OCR image path is missing.' }
if ($ExpectedTexts.Count -eq 0) { throw 'OCR marker list is empty.' }

function Await-WinRt {
    param(
        [Parameter(Mandatory = $true)]$Operation,
        [Parameter(Mandatory = $true)][Type]$ResultType
    )
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq 'AsTask' -and
            $_.IsGenericMethodDefinition -and
            $_.GetParameters().Count -eq 1
        } |
        Select-Object -First 1
    if ($null -eq $method) { throw 'Windows Runtime AsTask bridge is unavailable.' }
    $task = $method.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait()
    return $task.Result
}

function Normalize-Text([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return (($Value.ToUpperInvariant().ToCharArray() |
        Where-Object { [char]::IsLetterOrDigit($_) }) -join '')
}

$resolvedImage = (Resolve-Path -LiteralPath $ImagePath -ErrorAction Stop).Path
$storageFileType = [Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$randomAccessStreamType = [Windows.Storage.Streams.IRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
$bitmapDecoderClass = [Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$bitmapDecoderType = [Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$softwareBitmapType = [Windows.Graphics.Imaging.SoftwareBitmap,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$ocrEngineClass = [Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
$ocrResultType = [Windows.Media.Ocr.OcrResult,Windows.Foundation,ContentType=WindowsRuntime]
$languageType = [Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]

$file = Await-WinRt ($storageFileType::GetFileFromPathAsync($resolvedImage)) $storageFileType
$stream = $null
$bitmap = $null
try {
    $stream = Await-WinRt ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) $randomAccessStreamType
    $decoder = Await-WinRt ($bitmapDecoderClass::CreateAsync($stream)) $bitmapDecoderType
    $bitmap = Await-WinRt ($decoder.GetSoftwareBitmapAsync()) $softwareBitmapType
    $engine = $null
    try {
        $engine = $ocrEngineClass::TryCreateFromLanguage([Activator]::CreateInstance(
            $languageType, @('zh-Hans')))
    } catch { }
    if ($null -eq $engine) { $engine = $ocrEngineClass::TryCreateFromUserProfileLanguages() }
    if ($null -eq $engine) { throw 'Windows OCR engine is unavailable.' }
    $result = Await-WinRt ($engine.RecognizeAsync($bitmap)) $ocrResultType
    $recognized = Normalize-Text $result.Text
    foreach ($marker in $ExpectedTexts) {
        $normalizedMarker = Normalize-Text $marker
        if ($normalizedMarker.Length -gt 0 -and $recognized.Contains($normalizedMarker)) {
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            Write-Output ("{0}`t{1}" -f $marker, (($result.Text -replace '\s+', ' ').Trim()))
            exit 0
        }
    }
    $compact = (($result.Text -replace '\s+', ' ').Trim())
    if ($compact.Length -gt 500) { $compact = $compact.Substring(0, 500) }
    throw "None of the expected OCR markers were found. Recognized: $compact"
} finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
}
