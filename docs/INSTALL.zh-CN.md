# TransReader 安装与使用手册

[English](INSTALL.md) | **简体中文**

## 安装包

TransReader 当前只支持 Windows x64，并只发布一个受支持的 Setup 安装包。它安装到当前用户目录，创建开始菜单项，可选桌面快捷方式和标准卸载入口，不需要管理员权限。本地 AI 模型在首次使用时按需下载，不会塞进安装包。

`X.Y.Z` 是版本号，例如 `TransReader-v0.3.1-win-x64-setup.exe`。

## 系统要求

- Windows 10 20H1（19041）或更高版本，64 位。
- 支持 AVX 指令集的 x64 CPU。
- 在线模式建议至少 8 GB 内存；本地 AI 建议 16 GB。
- 约 520 MB 可用磁盘空间；安装本地 AI 后还需约 1.3 GB。
- Microsoft Edge WebView2 Runtime。Windows 11 通常已自带；如果译文区域无法显示，请安装微软官方 WebView2 Runtime。

## 安装

1. 从 GitHub Releases 下载 `TransReader-vX.Y.Z-win-x64-setup.exe` 和 `TransReader-vX.Y.Z-SHA256SUMS.txt`。
2. 可选但推荐：用 PowerShell 校验下载文件：

   ```powershell
   Get-FileHash .\TransReader-vX.Y.Z-win-x64-setup.exe -Algorithm SHA256
   ```

   结果应与 `SHA256SUMS.txt` 中对应的一行完全一致。
3. 双击安装包，选择语言并阅读 MIT License。
4. 按向导完成安装。默认位置为 `%LOCALAPPDATA%\Programs\TransReader`，不需要管理员权限。
5. 从开始菜单启动 TransReader。

> 当前社区构建可能没有商业代码签名。Windows SmartScreen 可能显示“未知发布者”。请只从本项目 GitHub Releases 下载，并先核对 SHA-256；不要从网盘或二次打包站获取。

## 第一次使用：三分钟开始阅读

1. 打开 TransReader，点击“打开 PDF”，选择论文、说明书、电子书或扫描文档。
2. 选择翻译方式：
   - **在线 API**：首次启动的在线模型列表和 API Key 都为空。进入“AI 中心 → 添加模型”，选择 MiMo、Kimi、GLM、DeepSeek 模板或自定义 OpenAI 兼容地址；输入 URL 和 Key 后可点击“检测模型”，选择模型并保存。
   - **本地 AI**：进入“AI 中心 → 本地 AI”，点击安装。应用会下载 Qwen3 1.7B 与 llama.cpp，并在下载完成后进行 SHA-256 校验。
3. 页面左侧保留原始 PDF，右侧显示结构化译文。扫描 PDF 会自动调用本地 PP-OCRv5。
4. 在译文中选中文字，可直接请求解释、概念梳理或继续追问。
5. 将经常阅读的资料导入文献库，TransReader 会保存阅读进度、缩略图、OCR 和翻译缓存。

## 在线模式会发送什么？

API Key 只保存在 Windows 凭据库，不会写进 `settings.json`，也不会回显到编辑框或自动生成的配置 JSON。在线模式会向你配置的端点发送当前页图像或 OCR 文本、必要的跨页上下文以及你的问题；具体内容取决于模型是否启用多模态。本地模式不会把页面内容发送到外部 API。

如果文档敏感，请使用本地模式，并确认“文献库整理”和“阅读助手”的模型来源也设为本地。

## 更新

应用每天最多自动检查一次 GitHub Releases 的稳定版本，也可在“AI 中心 → 应用更新”手动检查。选择“下载并安装”后，应用会先按 Release 中的 SHA-256 校验安装包，校验通过后再启动安装向导并退出当前版本。也可以手动退出应用并运行新版本 Setup。文献库和设置位于 `%LOCALAPPDATA%\TransReader`，覆盖升级不会删除它们。

## 卸载

打开 Windows“设置 → 应用 → 已安装的应用”，搜索 TransReader 并卸载。

为防止误删文献，卸载程序默认保留 `%LOCALAPPDATA%\TransReader`。确认不再需要文献库、缓存、本地模型和设置后，可手动删除该目录。此操作不可恢复，请先备份重要 PDF。

## 常见问题

### 双击后没有界面

确认系统版本、x64 架构和 AVX 支持。查看 `%LOCALAPPDATA%\TransReader\logs\crashes.log`，提交 issue 前请删除个人路径、文档内容和任何敏感信息。

### 译文区域空白

安装或修复 Microsoft Edge WebView2 Runtime，然后重新启动应用。

### OCR 初始化失败

不要从安装目录删除 `models`、`TransOcrNative.Host.exe`、`TransReader.App.pri` 或旁边的 DLL。重新运行 Setup 可修复安装。

### 在线 API 无法连接

检查 Base URL 是否为 OpenAI 兼容端点、模型名是否正确、余额/配额是否充足。代理网关可能不支持 `thinking` 或 JSON Schema，TransReader 会对常见不兼容项自动降级，但无法绕过服务商权限限制。

### 本地 AI 下载中断

重新点击安装即可续传。应用会在官方源与镜像之间切换，并在安装前校验文件大小和 SHA-256。
