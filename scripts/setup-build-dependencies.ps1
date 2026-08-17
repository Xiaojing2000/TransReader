[CmdletBinding()]
param(
    [switch]$ForceDownload
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$cacheDirectory = Join-Path $repositoryRoot 'artifacts\dependency-cache'
$runtimeDirectory = Join-Path $repositoryRoot 'third_party\runtime'
$modelsDirectory = Join-Path $repositoryRoot 'models'

New-Item -ItemType Directory -Force -Path $cacheDirectory, $runtimeDirectory, $modelsDirectory | Out-Null

$dependencies = @(
    [pscustomobject]@{
        Name = 'Paddle Inference 3.0.0 (Windows x64 CPU, AVX, MKL, VS2019)'
        FileName = 'paddle_inference-3.0.0-win-x64-avx-mkl-vs2019.zip'
        Url = 'https://paddle-inference-lib.bj.bcebos.com/3.0.0/cxx_c/Windows/CPU/x86-64_avx-mkl-vs2019/paddle_inference.zip'
        Size = 83959479L
        Sha256 = '01a03d1b4e994f193151975f8bd71954ed9df8ad1ae4fa186371d54f85ccdc26'
        Kind = 'paddle'
    },
    [pscustomobject]@{
        Name = 'OpenCV 4.7.0 (Windows)'
        FileName = 'opencv-4.7.0-windows.exe'
        Url = 'https://github.com/opencv/opencv/releases/download/4.7.0/opencv-4.7.0-windows.exe'
        Size = 185396441L
        Sha256 = '7fab7be68a4ab7f1b70759b0e58d4c4ffc2b8aee72642df6f2dfcc6c161b2465'
        Kind = 'opencv'
    },
    [pscustomobject]@{
        Name = 'PP-OCRv5 mobile detection model'
        FileName = 'PP-OCRv5_mobile_det_infer.tar'
        Url = 'https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/PP-OCRv5_mobile_det_infer.tar'
        Size = 4935680L
        Sha256 = '50446e5d01ac2a73d5319c89513281f6578414c888c602f9af13f93feefffc58'
        Kind = 'model'
    },
    [pscustomobject]@{
        Name = 'PP-OCRv5 mobile recognition model'
        FileName = 'PP-OCRv5_mobile_rec_infer.tar'
        Url = 'https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/PP-OCRv5_mobile_rec_infer.tar'
        Size = 16834560L
        Sha256 = '566b9512b34e34a9f0db54d87b51fa5a0b9ed2cf1ab7e49728cc0b8b5a64f414'
        Kind = 'model'
    }
)

function Test-VerifiedFile {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [long]$Size,
        [Parameter(Mandatory)] [string]$Sha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne $Size) { return $false }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    return $actual.Equals($Sha256, [StringComparison]::OrdinalIgnoreCase)
}

function Get-VerifiedDependency {
    param([Parameter(Mandatory)] $Dependency)

    $destination = Join-Path $cacheDirectory $Dependency.FileName
    if (-not $ForceDownload -and
        (Test-VerifiedFile -Path $destination -Size $Dependency.Size -Sha256 $Dependency.Sha256)) {
        Write-Host "Using cached $($Dependency.Name)."
        return $destination
    }

    $partial = "$destination.partial"
    Write-Host "Downloading $($Dependency.Name)..."
    Invoke-WebRequest -Uri $Dependency.Url -OutFile $partial -UseBasicParsing
    if (-not (Test-VerifiedFile -Path $partial -Size $Dependency.Size -Sha256 $Dependency.Sha256)) {
        throw "Integrity check failed for $($Dependency.Name). Delete '$partial' and try again."
    }
    Move-Item -LiteralPath $partial -Destination $destination -Force
    return $destination
}

$archives = @{}
foreach ($dependency in $dependencies) {
    $archives[$dependency.Kind + ':' + $dependency.FileName] = Get-VerifiedDependency $dependency
}

Write-Host 'Extracting Paddle Inference...'
Expand-Archive -LiteralPath $archives['paddle:paddle_inference-3.0.0-win-x64-avx-mkl-vs2019.zip'] `
    -DestinationPath $runtimeDirectory -Force

Write-Host 'Extracting OpenCV...'
$opencvArchive = $archives['opencv:opencv-4.7.0-windows.exe']
& $opencvArchive "-o$runtimeDirectory" -y | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "OpenCV extraction failed with exit code $LASTEXITCODE."
}

$tar = Get-Command tar.exe -ErrorAction SilentlyContinue
if (-not $tar) { $tar = Get-Command tar -ErrorAction SilentlyContinue }
if (-not $tar) { throw 'tar is required to extract the PP-OCRv5 model archives.' }

Write-Host 'Extracting PP-OCRv5 models...'
foreach ($dependency in $dependencies | Where-Object Kind -eq 'model') {
    $archive = $archives['model:' + $dependency.FileName]
    & $tar.Source -xf $archive -C $modelsDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Model extraction failed for $($dependency.FileName)."
    }
}

$expectedOutputs = @(
    (Join-Path $runtimeDirectory 'paddle\lib\paddle_inference.dll'),
    (Join-Path $runtimeDirectory 'opencv\build\x64\vc16\lib\opencv_world470.lib'),
    (Join-Path $modelsDirectory 'PP-OCRv5_mobile_det_infer\inference.pdiparams'),
    (Join-Path $modelsDirectory 'PP-OCRv5_mobile_rec_infer\inference.pdiparams')
)
$missing = @($expectedOutputs | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing.Count -gt 0) {
    throw "Dependency extraction finished, but required files are missing:`n - $($missing -join "`n - ")"
}

Write-Host 'Build dependencies are ready. Run .\scripts\build.ps1 -Configuration Release.' -ForegroundColor Green
