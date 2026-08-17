# Contributing / 参与贡献

Thank you for helping TransReader become a calmer, faster place to read difficult PDFs. Bug fixes, documentation, accessibility improvements, provider compatibility work, and focused feature proposals are all welcome.

感谢你帮助译页变成一个更安静、更流畅的 PDF 深度阅读工具。错误修复、文档、无障碍改进、模型兼容和聚焦的功能建议都很欢迎。

## Before opening a change / 开始之前

1. Search existing issues and pull requests.
2. For a large feature or architecture change, open an issue first so the direction can be discussed.
3. Never attach private PDFs, API keys, complete local settings, or unsanitized logs.

大型功能或架构调整请先开 issue 讨论。不要上传私人 PDF、API Key、完整本地配置或未脱敏日志。

## Development / 开发

Follow [Building from Source](docs/BUILDING.md) or [中文构建手册](docs/BUILDING.zh-CN.md).

Before submitting:

```powershell
dotnet test .\tests\TransReader.Core.Tests -c Release
dotnet build .\src\TransReader.App\TransReader.App.csproj -c Release -p:Platform=x64 -p:SkipNativeCheck=true
```

- Keep warnings at zero; the repository treats warnings as errors.
- Add or update tests for behavior changes.
- Update both English and Chinese docs for user-facing changes.
- Include screenshots for UI changes.
- Keep generated output, models, runtimes, logs, and local configuration out of Git.

请保持零警告，为行为变化补充测试；面向用户的变化同步更新中英文文档，界面变化附截图。

## Pull requests / 拉取请求

Keep each pull request focused. Explain the user-visible result, important tradeoffs, and how it was verified. By contributing, you agree that your contribution is provided under the repository's MIT License.

每个 PR 尽量只解决一件事，并说明用户可见结果、关键取舍和验证方式。提交贡献即表示你同意按本仓库 MIT License 提供该贡献。
