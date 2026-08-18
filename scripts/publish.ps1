[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [string]$InnoSetupPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$releaseRoot = Join-Path $artifactsRoot 'release'
$stagingRoot = Join-Path $artifactsRoot 'release-staging'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props')
    $Version = [string]$props.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a valid release version."
}

New-Item -ItemType Directory -Force -Path $releaseRoot, $stagingRoot | Out-Null
$publishDirectory = Join-Path $stagingRoot "TransReader-v$Version-win-x64"
$legacyPortablePath = Join-Path $releaseRoot "TransReader-v$Version-win-x64-portable.zip"

function Assert-PathUnderArtifacts {
    param([Parameter(Mandatory)] [string]$Path)
    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd('\') + '\'
    $resolvedTarget = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    if (-not $resolvedTarget.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside artifacts: $Path"
    }
}

if (Test-Path -LiteralPath $publishDirectory) {
    Assert-PathUnderArtifacts $publishDirectory
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $legacyPortablePath) {
    Assert-PathUnderArtifacts $legacyPortablePath
    Remove-Item -LiteralPath $legacyPortablePath -Force
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
}

& (Join-Path $PSScriptRoot 'package-ocr-component.ps1') -OutputDirectory $releaseRoot
$ocrComponentPath = Join-Path $releaseRoot 'TransReader-OCR-PP-OCRv5-mobile-win-x64.zip'

$project = Join-Path $repositoryRoot 'src\TransReader.App\TransReader.App.csproj'
dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:Platform=x64 -p:Version=$Version -p:PublishSingleFile=false `
    -o $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\INSTALL.md') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\INSTALL.zh-CN.md') -Destination $publishDirectory

# .NET self-contained publishes include debugger/dump helpers that are not used
# by the end-user application. Remove only the known diagnostic tools; coreclr,
# clrjit, clrgc, hostfxr and resource DLLs remain untouched.
$diagnosticFiles = @(
    'createdump.exe',
    'Microsoft.DiaSymReader.Native.amd64.dll',
    'mscordaccore.dll',
    'mscordbi.dll'
)
Get-ChildItem -LiteralPath $publishDirectory -File |
    Where-Object {
        $_.Name -in $diagnosticFiles -or
        $_.Name -like 'mscordaccore_amd64_amd64_*.dll'
    } |
    Remove-Item -Force

function Assert-ReleasePayload {
    param([Parameter(Mandatory)] [string]$Directory)

    $required = @(
        'TransReader.App.exe',
        'TransReader.App.pri',
        'Microsoft.UI.Xaml.dll',
        'Microsoft.Web.WebView2.Core.dll',
        'TransOcrNative.dll',
        'TransOcrNative.Host.exe',
        'OCR.yaml',
        'OcrPayloadManifest.json',
        'abseil_dll.dll',
        'polyclipping.dll'
    )
    $missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Directory $_) -PathType Leaf) }
    if ($missing) {
        throw "Release payload is missing required files: $($missing -join ', ')"
    }

    $files = Get-ChildItem -LiteralPath $Directory -File -Recurse
    $forbidden = $files | Where-Object {
        $_.Extension -eq '.pdb' -or
        $_.Name -like 'CommunityToolkit.*' -or
        $_.Name -like 'Microsoft.Windows.AI.*' -or
        $_.Name -like 'Microsoft.Windows.Widgets.*' -or
        $_.Name -like 'onnxruntime*.dll' -or
        $_.Name -in @(
            'DirectML.dll', 'Microsoft.ML.OnnxRuntime.dll', 'System.Numerics.Tensors.dll',
            'paddle_inference.dll', 'opencv_world470.dll', 'mkldnn.dll', 'mklml.dll',
            'libiomp5md.dll', 'common.dll'
        ) -or
        $_.FullName -like '*\models\PP-OCRv5_mobile_*'
    }
    if ($forbidden) {
        throw "Release payload contains development or unused component files: $($forbidden.Name -join ', ')"
    }

    $payloadBytes = ($files | Measure-Object Length -Sum).Sum
    $payloadMiB = [Math]::Round($payloadBytes / 1MB, 2)
    if ($payloadBytes -gt 180MB) {
        throw "Release payload is $payloadMiB MiB, above the 180 MiB limit."
    }
    Write-Host "Validated release payload: $($files.Count) files, $payloadMiB MiB" -ForegroundColor Green
}

Assert-ReleasePayload -Directory $publishDirectory

$innoCandidates = @(
    $InnoSetupPath,
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    'C:\Program Files\Inno Setup 7\ISCC.exe',
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$iscc = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup compiler was not found. Install Inno Setup 6 or 7.'
}

$iss = Join-Path $repositoryRoot 'installer\TransReader.iss'
& $iscc "/DMyAppVersion=$Version" "/DSourceDir=$publishDirectory" "/DOutputDir=$releaseRoot" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$releaseFiles = @(
    Get-Item -LiteralPath (Join-Path $releaseRoot "TransReader-v$Version-win-x64-setup.exe")
    Get-Item -LiteralPath $ocrComponentPath
) | Sort-Object Name
if ($releaseFiles.Count -ne 2) {
    throw "Expected Setup and OCR component artifacts for version $Version."
}
$checksumPath = Join-Path $releaseRoot "TransReader-v$Version-SHA256SUMS.txt"
$checksumLines = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
[System.IO.File]::WriteAllLines($checksumPath, $checksumLines, [System.Text.UTF8Encoding]::new($false))

Write-Host "Release artifacts are ready in $releaseRoot" -ForegroundColor Green
@($releaseFiles) + @(Get-Item -LiteralPath $checksumPath) | Select-Object Name, Length, FullName
