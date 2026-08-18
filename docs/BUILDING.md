# Building TransReader from Source

**English** | [简体中文](BUILDING.zh-CN.md)

## Prerequisites

- Windows 10/11 x64
- .NET 10 SDK
- Visual Studio 2022 Build Tools with Desktop development with C++ and a Windows SDK
- CMake 3.20 or later
- PowerShell 7 or Windows PowerShell 5.1
- An AVX-capable processor

## Complete build from a fresh clone

```powershell
git clone <repository-url>
cd TransReader
.\scripts\setup-build-dependencies.ps1
.\scripts\build.ps1 -Configuration Release
dotnet test .\tests\TransReader.Core.Tests -c Release
```

The dependency script downloads and verifies pinned builds of Paddle Inference 3.0.0, OpenCV 4.7.0, and PP-OCRv5 mobile models. Downloads are cached under `artifacts/dependency-cache`; extracted runtimes and models live in `third_party/runtime` and `models`. All of these directories are ignored by Git.

For a managed-code-only check:

```powershell
dotnet build .\src\TransReader.App\TransReader.App.csproj -c Release -p:Platform=x64 -p:SkipNativeCheck=true
```

That build does not contain working OCR and is intended only for CI or managed-code development.

## Build release packages

Install Inno Setup 6 or 7, then run:

```powershell
.\scripts\publish.ps1 -Version 0.3.2
```

The output under `artifacts/release` contains:

- `TransReader-v0.3.2-win-x64-setup.exe`
- `TransReader-OCR-PP-OCRv5-mobile-win-x64.zip`
- `TransReader-v0.3.2-SHA256SUMS.txt`

The base Setup intentionally excludes Paddle/OpenCV/MKL runtime DLLs and PP-OCRv5 models. `package-ocr-component.ps1` builds their deterministic, manifest-bearing optional component; the checksum file covers both release artifacts. No Portable ZIP is produced.

The release script performs a self-contained .NET publish, so end users do not need to install the .NET 10 Runtime separately. WebView2 Runtime remains a system requirement.

## Release a new version

1. Update `Directory.Build.props` and `CHANGELOG.md`.
2. Run the complete build, tests, and `scripts/publish.ps1`.
3. Inspect the Setup package and its SHA-256 value.
4. Create a `vX.Y.Z` tag. GitHub Actions rebuilds from the official dependency sources and creates the Release.
