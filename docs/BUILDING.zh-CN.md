# 从源码构建 TransReader

[English](BUILDING.md) | **简体中文**

## 先决条件

- Windows 10/11 x64
- .NET 10 SDK
- Visual Studio 2022 Build Tools，包含“使用 C++ 的桌面开发”和 Windows SDK
- CMake 3.20 或更高版本
- PowerShell 7 或 Windows PowerShell 5.1
- 支持 AVX 的 CPU

## 全新克隆后的完整构建

```powershell
git clone <repository-url>
cd TransReader
.\scripts\setup-build-dependencies.ps1
.\scripts\build.ps1 -Configuration Release
dotnet test .\tests\TransReader.Core.Tests -c Release
```

依赖脚本下载并校验固定版本的 Paddle Inference 3.0.0、OpenCV 4.7.0 和 PP-OCRv5 mobile 模型。缓存位于 `artifacts/dependency-cache`，解压后的运行时与模型分别位于 `third_party/runtime` 和 `models`；这些目录均不会提交到 Git。

仅检查 C# 代码时可以运行：

```powershell
dotnet build .\src\TransReader.App\TransReader.App.csproj -c Release -p:Platform=x64 -p:SkipNativeCheck=true
```

该命令生成的应用没有 OCR 能力，只适用于 CI 或托管代码开发。

## 生成发布包

安装 Inno Setup 6 或 7 后运行：

```powershell
.\scripts\publish.ps1 -Version 0.3.0
```

输出位于 `artifacts/release`：

- `TransReader-v0.3.0-win-x64-setup.exe`
- `TransReader-v0.3.0-win-x64-portable.zip`
- `TransReader-v0.3.0-SHA256SUMS.txt`

如果只需要 Portable：

```powershell
.\scripts\publish.ps1 -Version 0.3.0 -PortableOnly
```

发布脚本使用 self-contained .NET publish，因此最终用户不需要单独安装 .NET 10 Runtime。WebView2 Runtime 仍是系统要求。

## 发布新版本

1. 更新 `Directory.Build.props` 和 `CHANGELOG.md`。
2. 完整运行构建、测试和 `scripts/publish.ps1`。
3. 检查两个发布包和 SHA-256。
4. 创建 `vX.Y.Z` 标签。GitHub Actions 会重新从官方依赖源构建并生成 Release。
