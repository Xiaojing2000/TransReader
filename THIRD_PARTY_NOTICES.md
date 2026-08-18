# 第三方组件声明 / Third-Party Notices

译页 TransReader 基于以下开源组件与模型构建，感谢其作者与社区。
本项目代码采用 MIT License（见 `LICENSE`）；下列组件按其各自许可证使用。

## 源码 / 库

| 组件 | 用途 | 许可证 | 出处 |
| --- | --- | --- | --- |
| PaddleOCR（`third_party/PaddleOCR`） | OCR 检测/识别模型与 C++ 推理管线来源；快照溯源见 `UPSTREAM.md` | Apache-2.0 | https://github.com/PaddlePaddle/PaddleOCR |
| Paddle Inference 3.0.0 | OCR C++ CPU 推理运行时 | Apache-2.0 | https://www.paddlepaddle.org.cn/inference/v3.0/guides/install/download_lib.html |
| OpenCV 4.7.0 | OCR 图像处理运行时 | Apache-2.0 | https://github.com/opencv/opencv/releases/tag/4.7.0 |
| llama.cpp b9632 | 本地大模型推理运行时（llama-server） | MIT | https://github.com/ggml-org/llama.cpp/releases/tag/b9632 |
| markdown-it | 译文 Markdown 渲染 | MIT | https://github.com/markdown-it/markdown-it |
| KaTeX | 数学公式渲染 | MIT | https://github.com/KaTeX/KaTeX |
| .NET 10 / Windows App SDK 1.8 / CommunityToolkit | 应用框架与控件 | MIT | https://github.com/dotnet, https://github.com/microsoft/WindowsAppSDK, https://github.com/CommunityToolkit/Windows |
| Microsoft.Data.Sqlite.Core 10.0.10 / SQLitePCLRaw.provider.winsqlite3 2.1.11 | 文献库与缓存索引 | MIT / Apache-2.0 | https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core, https://www.nuget.org/packages/SQLitePCLRaw.provider.winsqlite3 |
| WebView2 Runtime | 译文与助手界面渲染 | 微软专有运行时（随 Windows 分发） | https://developer.microsoft.com/microsoft-edge/webview2/ |
| Inno Setup Chinese Simplified Translation | 安装/卸载界面简体中文翻译 | MIT | https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation |

## 模型与运行时（构建/运行期下载，不随仓库分发）

| 组件 | 用途 | 许可证 | 出处 |
| --- | --- | --- | --- |
| PP-OCRv5 mobile det/rec 推理模型（Paddle 3.0 official inference artifacts） | 本地 OCR | Apache-2.0 | https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/ |
| Qwen3-1.7B Q4_K_M GGUF（revision `9bcdc2d703843e5e820383fe115eb0f7ad586643`） | 本地离线翻译/分析模型 | Apache-2.0 | https://huggingface.co/second-state/Qwen3-1.7B-GGUF |
| Tencent Hy-MT2-1.8B Q4_K_M GGUF（revision `1cd5208700acedef4ef93019b6cfc148b8522d45`） | 本地专业翻译模型 | Apache-2.0 | https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF |

## 说明

- 仓库内直接分发的 markdown-it、KaTeX、PaddleOCR、Abseil、Clipper、nlohmann/json 与 Inno Setup 简体中文翻译均保留了对应许可证文本。发布脚本会把本项目 `LICENSE` 和本文件复制到每个二进制包。
- 构建期下载的 Paddle Inference、OpenCV 与 OCR 模型使用固定 URL、文件大小和 SHA-256；详见 `scripts/setup-build-dependencies.ps1`。
- xUnit、Microsoft.NET.Test.Sdk 与测试运行器仅用于测试，不进入最终应用包，并按各自许可证使用。
- 在线翻译/问答由用户自行配置的第三方 API 提供，相关服务条款与费用由用户与对应服务商约定。
