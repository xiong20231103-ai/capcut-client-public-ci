param(
    [string]$ImagePath = '',
    [string]$ExpectedTitle = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
if ([string]::IsNullOrWhiteSpace($ImagePath) -or
    [string]::IsNullOrWhiteSpace($ExpectedTitle)) {
    $requestPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'data\draft-title-ocr-request.txt'
    $request = @(Get-Content -LiteralPath $requestPath -Encoding UTF8)
    if ($request.Count -ge 2) {
        $ImagePath = $request[0]
        $ExpectedTitle = $request[1]
    }
}
if ([string]::IsNullOrWhiteSpace($ImagePath)) { throw 'OCR image path is missing.' }
if ([string]::IsNullOrWhiteSpace($ExpectedTitle)) { throw 'Expected OCR draft title is missing.' }

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

function Normalize-Title([string]$Value) {
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

$file = Await-WinRt ($storageFileType::GetFileFromPathAsync($resolvedImage)) $storageFileType
$stream = $null
$bitmap = $null
try {
    $stream = Await-WinRt ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) $randomAccessStreamType
    $decoder = Await-WinRt ($bitmapDecoderClass::CreateAsync($stream)) $bitmapDecoderType
    $bitmap = Await-WinRt ($decoder.GetSoftwareBitmapAsync()) $softwareBitmapType
    $engine = $ocrEngineClass::TryCreateFromUserProfileLanguages()
    if ($null -eq $engine) { throw 'Windows OCR engine is unavailable.' }
    $result = Await-WinRt ($engine.RecognizeAsync($bitmap)) $ocrResultType
    $expected = Normalize-Title $ExpectedTitle
    $matches = @($result.Lines | Where-Object {
        (Normalize-Title $_.Text) -ceq $expected
    })
    if ($matches.Count -ne 1) {
        throw "Expected exact OCR title '$ExpectedTitle' matched $($matches.Count) lines."
    }
    $line = $matches[0]
    $words = @($line.Words)
    if ($words.Count -eq 0) { throw 'The exact OCR title has no word bounds.' }
    $left = [int][math]::Floor(($words | ForEach-Object { $_.BoundingRect.X } | Measure-Object -Minimum).Minimum)
    $top = [int][math]::Floor(($words | ForEach-Object { $_.BoundingRect.Y } | Measure-Object -Minimum).Minimum)
    $right = [int][math]::Ceiling(($words | ForEach-Object {
        $_.BoundingRect.X + $_.BoundingRect.Width
    } | Measure-Object -Maximum).Maximum)
    $bottom = [int][math]::Ceiling(($words | ForEach-Object {
        $_.BoundingRect.Y + $_.BoundingRect.Height
    } | Measure-Object -Maximum).Maximum)
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    Write-Output ("{0}`t{1}`t{2}`t{3}`t{4}" -f
        $left, $top, ($right - $left), ($bottom - $top), $line.Text)
} finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
}
