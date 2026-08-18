[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipNative
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$nativeOutputDirectory = Join-Path $artifactsDirectory 'native'

New-Item -ItemType Directory -Force -Path $artifactsDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $nativeOutputDirectory | Out-Null

if (-not $SkipNative) {
    $requiredNativeInputs = @(
        (Join-Path $repositoryRoot 'third_party\runtime\paddle\lib\paddle_inference.dll'),
        (Join-Path $repositoryRoot 'third_party\runtime\opencv\build\x64\vc16\lib\opencv_world470.lib'),
        (Join-Path $repositoryRoot 'models\PP-OCRv5_mobile_det_infer\inference.pdiparams'),
        (Join-Path $repositoryRoot 'models\PP-OCRv5_mobile_rec_infer\inference.pdiparams')
    )
    $missingNativeInputs = @($requiredNativeInputs | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($missingNativeInputs.Count -gt 0) {
        $relativeMissing = $missingNativeInputs | ForEach-Object {
            [System.IO.Path]::GetRelativePath($repositoryRoot, $_)
        }
        throw "Native OCR build dependencies are missing:`n - $($relativeMissing -join "`n - ")`nRun .\scripts\setup-build-dependencies.ps1 first."
    }

    $cmake = Get-Command cmake -ErrorAction SilentlyContinue
    if (-not $cmake) {
        $cmakePath = 'C:\Program Files\CMake\bin\cmake.exe'
        if (Test-Path $cmakePath) {
            $cmake = Get-Item $cmakePath
        } else {
            throw 'CMake is required to build TransOcrNative.dll.'
        }
    }
    $cmakeExecutable = if ($cmake.Source) { $cmake.Source } else { $cmake.FullName }

    $nativeBuildDirectory = Join-Path $repositoryRoot 'artifacts\native-build'
    $paddleSourceDirectory = Join-Path $repositoryRoot 'third_party\PaddleOCR\deploy\cpp_infer'
    $paddleRuntimeDirectory = Join-Path $repositoryRoot 'third_party\runtime'
    $opencvDirectory = Join-Path $paddleRuntimeDirectory 'opencv\build'
    & $cmakeExecutable -S $paddleSourceDirectory -B $nativeBuildDirectory -A x64 `
        "-DPADDLE_LIB=$paddleRuntimeDirectory" `
        "-DOPENCV_DIR=$opencvDirectory" `
        '-DWITH_STATIC_LIB=OFF' `
        '-DWITH_GPU=OFF' `
        '-DWITH_MKL=ON' `
        '-DCMAKE_POLICY_VERSION_MINIMUM=3.5'
    if ($LASTEXITCODE -ne 0) {
        throw "CMake configuration failed with exit code $LASTEXITCODE."
    }
    & $cmakeExecutable --build $nativeBuildDirectory --config Release --target TransOcrNative TransOcrNative.AbiSmoke TransOcrNative.Host
    if ($LASTEXITCODE -ne 0) {
        throw "Native build failed with exit code $LASTEXITCODE."
    }

    $builtDll = Get-ChildItem $nativeBuildDirectory -Filter 'TransOcrNative.dll' -Recurse | Select-Object -First 1
    if (-not $builtDll) {
        throw 'TransOcrNative.dll was not produced.'
    }
    Get-ChildItem -LiteralPath $builtDll.DirectoryName -Filter '*.dll' | Copy-Item -Destination $nativeOutputDirectory -Force
    $builtHost = Get-ChildItem $nativeBuildDirectory -Filter 'TransOcrNative.Host.exe' -Recurse | Select-Object -First 1
    if (-not $builtHost) {
        throw 'TransOcrNative.Host.exe was not produced.'
    }
    Copy-Item $builtHost.FullName -Destination $nativeOutputDirectory -Force

    $smokeTest = Get-ChildItem $nativeBuildDirectory -Filter 'TransOcrNative.AbiSmoke.exe' -Recurse | Select-Object -First 1
    if (-not $smokeTest) {
        throw 'TransOcrNative.AbiSmoke.exe was not produced.'
    }
    $modelsPath = (Join-Path $repositoryRoot 'models').Replace('\', '/')
    $pipelineConfigPath = (Join-Path $repositoryRoot 'third_party\PaddleOCR\deploy\cpp_infer\src\configs\OCR.yaml').Replace('\', '/')
    & $smokeTest.FullName $modelsPath $pipelineConfigPath
    if ($LASTEXITCODE -ne 0) {
        throw "Native ABI smoke test failed with exit code $LASTEXITCODE."
    }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $dotnetPath = 'C:\Program Files\dotnet\dotnet.exe'
    if (Test-Path $dotnetPath) {
        $dotnet = Get-Item $dotnetPath
    } else {
        throw '.NET SDK is required to build the WinUI application.'
    }
}

$dotnetExecutable = if ($dotnet.Source) { $dotnet.Source } else { $dotnet.FullName }
& $dotnetExecutable build (Join-Path $repositoryRoot 'src\TransReader.App\TransReader.App.csproj') -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw ".NET build failed with exit code $LASTEXITCODE."
}
