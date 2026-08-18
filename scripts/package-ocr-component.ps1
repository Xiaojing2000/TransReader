[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\release'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $repositoryRoot 'artifacts\ocr-component-staging'
$assetName = 'TransReader-OCR-PP-OCRv5-mobile-win-x64.zip'
$assetPath = Join-Path $OutputDirectory $assetName

$runtimeFiles = Get-ChildItem (Join-Path $repositoryRoot 'artifacts\native') -File -Filter '*.dll' |
    Where-Object Name -NotIn @('TransOcrNative.dll')
$modelRoots = @(
    Join-Path $repositoryRoot 'models\PP-OCRv5_mobile_det_infer'
    Join-Path $repositoryRoot 'models\PP-OCRv5_mobile_rec_infer'
)
$missingModels = @($modelRoots | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($runtimeFiles.Count -eq 0 -or $missingModels.Count -gt 0) {
    throw 'OCR runtime/model outputs are missing. Run scripts\build.ps1 first.'
}

if (-not $VerifyOnly) {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($file in $runtimeFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $stage $file.Name)
    }
    $modelsStage = Join-Path $stage 'models'
    New-Item -ItemType Directory -Path $modelsStage | Out-Null
    foreach ($modelRoot in $modelRoots) {
        Copy-Item -LiteralPath $modelRoot -Destination $modelsStage -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination $stage

    $payloadFiles = Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName
    $manifest = [ordered]@{
        schemaVersion = 1
        componentVersion = 'paddleocr-ppocrv5-mobile-cpu-v2'
        architecture = 'win-x64'
        files = @($payloadFiles | ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    }
    $json = $manifest | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText((Join-Path $stage 'payload-manifest.json'), $json + "`n", [Text.UTF8Encoding]::new($false))

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $assetPath) { Remove-Item -LiteralPath $assetPath -Force }
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($assetPath, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName) {
                $entryName = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')
                $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = [IO.File]::OpenRead($file.FullName)
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}

if (-not (Test-Path -LiteralPath $assetPath)) { throw "OCR component not found: $assetPath" }
$item = Get-Item -LiteralPath $assetPath
$hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "OCR component: $assetPath"
Write-Host "Size: $($item.Length)"
Write-Host "SHA256: $hash"
