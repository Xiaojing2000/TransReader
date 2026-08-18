# 译页 TransReader v0.3.1

v0.3.1 是一次 Windows 启动、发行体积和在线 API 配置修复版本。

## 下载

- **`TransReader-v0.3.1-win-x64-setup.exe`**：唯一受支持的 Windows x64 安装包。
- **`TransReader-v0.3.1-SHA256SUMS.txt`**：安装包 SHA-256 校验文件。
- GitHub 会自动提供 Source code (zip) 与 Source code (tar.gz)。

本版本不再提供 Portable 便携包。覆盖安装会保留 `%LOCALAPPDATA%\TransReader` 中的设置、文献库和缓存。

> 当前社区构建可能没有商业代码签名。若 Windows SmartScreen 显示“未知发布者”，请确认文件来自本仓库，并核对 SHA-256。

## 主要变化

- 修复 Setup 安装后无响应：发行包现在包含 WinUI 必需的 `TransReader.App.pri` 资源，并修正窗口初始化顺序。
- 精简发行内容：去除开发符号、诊断工具及未使用的 AI、ML、Widgets、Toolkit 组件。
- 首次启动不再预置在线模型或 API Key；已保存的 Key 不会回显到编辑框或配置 JSON。
- 新增 MiMo、Kimi、GLM、DeepSeek 模板、配置 JSON 预览以及 `/models` 模型检测。
- 在线请求不再设置温度，使用供应商默认值；界面不再要求用户选择鉴权方式。
- “测试连接”和“检测模型”增加就地进度、结果与 20 秒超时提示。
- 新增应用内稳定版检查、安装包下载进度和 SHA-256 校验。

---

v0.3.1 focuses on Windows startup reliability, release size, and online API setup.

- The release now contains the WinUI PRI resources required by installed builds and fixes early window initialization.
- Releases contain one supported Windows x64 Setup package; no Portable ZIP is published.
- Development symbols, diagnostics, and unused AI/ML/Widgets/Toolkit components are excluded.
- First run starts with no online model or API key, and saved keys are never echoed into the form or generated JSON.
- MiMo, Kimi, GLM, and DeepSeek templates, JSON preview, and `/models` discovery are available.
- Online requests use provider-default temperature behavior; connection tests now show progress, results, and a 20-second timeout.
- Stable-release checks, verified downloads, and SHA-256 validation are available in the app.
