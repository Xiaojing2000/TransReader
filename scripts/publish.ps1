[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$PortableOnly,
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

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
}

$project = Join-Path $repositoryRoot 'src\TransReader.App\TransReader.App.csproj'
dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:Platform=x64 -p:Version=$Version -p:PublishSingleFile=false `
    -o $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\INSTALL.md') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\INSTALL.zh-CN.md') -Destination $publishDirectory

$portableName = "TransReader-v$Version-win-x64-portable.zip"
$portablePath = Join-Path $releaseRoot $portableName
if (Test-Path -LiteralPath $portablePath) { Remove-Item -LiteralPath $portablePath -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDirectory,
    $portablePath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true)
Write-Host "Created $portableName" -ForegroundColor Green

if (-not $PortableOnly) {
    $innoCandidates = @(
        $InnoSetupPath,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        'C:\Program Files\Inno Setup 7\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $iscc = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $iscc) {
        throw 'Inno Setup compiler was not found. Install Inno Setup 6/7 or rerun with -PortableOnly.'
    }

    $iss = Join-Path $repositoryRoot 'installer\TransReader.iss'
    & $iscc "/DMyAppVersion=$Version" "/DSourceDir=$publishDirectory" "/DOutputDir=$releaseRoot" $iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
}

$releaseFiles = Get-ChildItem -LiteralPath $releaseRoot -File |
    Where-Object Name -Like "TransReader-v$Version-win-x64-*" |
    Sort-Object Name
$checksumPath = Join-Path $releaseRoot "TransReader-v$Version-SHA256SUMS.txt"
$checksumLines = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
[System.IO.File]::WriteAllLines($checksumPath, $checksumLines, [System.Text.UTF8Encoding]::new($false))

Write-Host "Release artifacts are ready in $releaseRoot" -ForegroundColor Green
$releaseFiles + (Get-Item -LiteralPath $checksumPath) | Select-Object Name, Length, FullName
