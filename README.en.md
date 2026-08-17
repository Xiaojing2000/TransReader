<div align="center">
  <img src="https://xiaojing2000.github.io/TransReader/images/app-icon.png" width="96" alt="TransReader icon">
  <h1>TransReader</h1>
  <p><strong>Stay with the paper. Let translation meet you on the page.</strong></p>
  <p>A local-first PDF translation reader for Windows - built for papers, books, manuals, formulas, and the moments when a paragraph deserves more than a quick copy-and-paste.</p>
  <p><a href="README.md">简体中文</a> · <strong>English</strong></p>
  <p>
    <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=for-the-badge&logo=windows11&logoColor=white" alt="Windows 10 and 11">
    <img src="https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10">
    <img src="https://img.shields.io/badge/OCR-PP--OCRv5-22A699?style=for-the-badge" alt="PP-OCRv5">
    <img src="https://img.shields.io/badge/License-MIT-F4B942?style=for-the-badge" alt="MIT License">
  </p>
</div>

![TransReader side-by-side reader](https://xiaojing2000.github.io/TransReader/images/reader-overview.jpg)

<p align="center"><strong><a href="https://github.com/Xiaojing2000/TransReader/releases/latest">🚀 Download the latest release</a></strong></p>

## Reading a hard PDF should not feel like operating five different tools

You know the routine: open the paper, copy a paragraph, repair a broken formula, paste it into a translator, switch to a chat window, then hunt for the original sentence again. On a scanned PDF, add OCR to the pile. After a few pages, the document has disappeared behind the workflow.

**TransReader keeps the document at the center.** The original page stays on the left. A clean Markdown translation grows on the right. Scanned pages go through local PP-OCRv5, formulas render with KaTeX, and a selected passage can become a focused question without losing the surrounding page or book context.

It is surprisingly refreshing: open the PDF, start reading, and remain in the same place.

<p align="center"><img src="https://xiaojing2000.github.io/TransReader/images/feature-grid.svg" width="100%" alt="TransReader feature cards"></p>

## What makes it useful

| | |
| --- | --- |
| **📖 Compare without losing your place**<br>The source page and translation remain side by side. Figures, footnotes, labels, and equations are always one glance away. | **🔍 Read scanned PDFs, locally**<br>PP-OCRv5 mobile detection and recognition run on the CPU through Paddle Inference. No GPU is required. |
| **🌐 Bring your own model**<br>Use MiMo, Kimi, DeepSeek, GLM, or any OpenAI-compatible endpoint. Text-only and multimodal providers are both supported. | **🏠 Go fully local when the document matters**<br>Install Qwen3 1.7B from inside the app. llama.cpp handles local translation, library analysis, and reader questions. |
| **💬 Ask the paragraph, not a blank chatbot**<br>Select translated text and ask for an explanation. The assistant receives the passage, current page, and nearby reading context. | **🗂️ Build a library that remembers**<br>Keep PDFs, progress, thumbnails, OCR, translations, topics, summaries, and domain-aware terminology together. |

## A quick tour

<table>
  <tr>
    <td width="50%"><img src="https://xiaojing2000.github.io/TransReader/images/library.jpg" alt="TransReader document library"></td>
    <td width="50%"><img src="https://xiaojing2000.github.io/TransReader/images/ai-center.jpg" alt="TransReader AI Center"></td>
  </tr>
  <tr>
    <td><strong>A library for long reading</strong><br>Search, classify, tag, favorite, resume, and keep reading progress instead of reopening anonymous files from a downloads folder.</td>
    <td><strong>One place for online and local AI</strong><br>Switch modes, manage endpoints, test connections, view usage, choose assistant behavior, and install the local model.</td>
  </tr>
</table>

The screenshots above are from the real Windows application using a synthetic public demo paper. No private document or API credential is shown.

## Download and install

TransReader currently ships for **Windows x64** in two familiar formats:

| Package | Choose it when... |
| --- | --- |
| `TransReader-vX.Y.Z-win-x64-setup.exe` | You want the normal Windows installation experience: Start Menu entry, optional desktop shortcut, repair-friendly upgrades, and a standard uninstaller. **Recommended for most people.** |
| `TransReader-vX.Y.Z-win-x64-portable.zip` | You want to extract the app yourself, keep it on another drive, or use it without an installer. |

### Download now

- [Windows setup — recommended](https://github.com/Xiaojing2000/TransReader/releases/latest/download/TransReader-v0.3.0-win-x64-setup.exe)
- [Windows portable ZIP](https://github.com/Xiaojing2000/TransReader/releases/latest/download/TransReader-v0.3.0-win-x64-portable.zip)
- [SHA-256 checksums](https://github.com/Xiaojing2000/TransReader/releases/latest/download/TransReader-v0.3.0-SHA256SUMS.txt)
- [Source code ZIP](https://github.com/Xiaojing2000/TransReader/archive/refs/tags/v0.3.0.zip)
- [All releases and release notes](https://github.com/Xiaojing2000/TransReader/releases)

GitHub also generates `Source code (zip)` and `Source code (tar.gz)` automatically for every tagged release. Use the checksum file to verify downloaded packages.

> Community builds may not yet have a commercial code-signing certificate. If Windows SmartScreen shows “Unknown publisher,” verify the SHA-256 file and make sure the download came from this repository's Releases page.

See the complete [English installation and user guide](docs/INSTALL.md) or [中文安装与使用手册](docs/INSTALL.zh-CN.md).

### Requirements

- Windows 10 20H1 (19041) or later, x64.
- An x64 processor with AVX support.
- 8 GB RAM for online use; 16 GB is recommended for local AI.
- About 600 MB for the app after installation, plus about 1.3 GB for the optional local model and runtime.
- Microsoft Edge WebView2 Runtime (normally included with Windows 11).

## Start reading in three minutes

1. Install TransReader or extract the complete Portable ZIP.
2. Open a PDF. Native PDFs and scanned pages use the same reading surface.
3. Pick a translation path:
   - **Online:** open AI Center, select a preset or add an OpenAI-compatible endpoint, enter the model and API key, then test it.
   - **Local:** open AI Center → Local AI and choose Install. The model and runtime are downloaded with size and SHA-256 verification.
4. Read the source on the left and the translation on the right. Select a passage whenever it deserves an explanation.
5. Import documents into the library when you want progress, cached results, and organization to survive the session.

## Online or local? You decide per document

| | Online API | Local AI |
| --- | --- | --- |
| Best at | Strong multimodal models, speed, complex page understanding | Sensitive documents, offline reading, predictable local control |
| Content sent out | Current page image or OCR text, necessary context, and your question | Nothing to an external model API |
| API key | Stored in Windows Credential Manager | Not required |
| Hardware | Modest CPU and memory | CPU inference; 16 GB RAM recommended |
| Cost | Determined by your provider | No per-token API charge |

OCR always runs locally. In online mode, only the endpoint you selected receives content. For confidential documents, also set Library Analysis and Reader Assistant to a local model source.

## Formulas, layout, and context are first-class

TransReader does more than replace English words with Chinese words:

- Markdown and KaTeX preserve headings, lists, code, inline formulas, and display equations.
- A page-level OCR cache avoids repeating expensive recognition work.
- A translation cache lets you turn pages quickly and resume later.
- A compact running summary and terminology context help names and technical terms remain stable across pages.
- Domain profiles adapt prompts for mathematics, computer science, physics, and other material, with user-editable hints.
- Outdated page work is cancelled when you turn quickly, so the interface follows the reader instead of a stale background task.

## Privacy and local data

- API keys are saved through Windows Credential Manager, not in the repository or `settings.json`.
- Settings, library data, logs, downloaded local models, and caches live under `%LOCALAPPDATA%\TransReader`.
- Local mode keeps page inference on the device.
- Online mode sends content to the user-configured provider; its privacy policy, terms, and charges apply.
- Logs should be sanitized before they are attached to a public issue.

See [SECURITY.md](SECURITY.md) for vulnerability reporting and security guidance.

## Build from source

A fresh clone can prepare the pinned native dependencies automatically:

```powershell
.\scripts\setup-build-dependencies.ps1
.\scripts\build.ps1 -Configuration Release
dotnet test .\tests\TransReader.Core.Tests -c Release
```

To create both release packages after installing Inno Setup:

```powershell
.\scripts\publish.ps1 -Version 0.3.0
```

Read [Building TransReader from Source](docs/BUILDING.md) or [中文构建手册](docs/BUILDING.zh-CN.md) for prerequisites, dependency locations, CI-only builds, and release steps.

## Project map

```text
src/TransReader.App          WinUI 3 interface and application orchestration
src/TransReader.Core         OCR, translation, storage, library, and document logic
native/TransOcrNative        Stable C ABI and native PaddleOCR host
third_party/PaddleOCR        Selected PaddleOCR C++ inference source snapshot
tests/TransReader.Core.Tests Core unit tests
scripts                      Dependency, build, and release automation
installer                    Inno Setup definition
```

## Current scope

TransReader is currently a Windows x64 project and the interface is primarily Simplified Chinese. English documentation is complete; application UI localization is a welcome future contribution. The project is moving toward its first public release, so practical bug reports and installation feedback are especially valuable.

## Contributing

Contributions are warmly welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md), keep private PDFs and credentials out of issues, and include screenshots for visual changes.

If TransReader gives you back even ten minutes of attention during a difficult paper, it is already doing the job it was built for. ⭐

## License

TransReader source code is released under the [MIT License](LICENSE). Third-party libraries, runtimes, and models remain under their respective licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
