# TransReader Installation and User Guide

**English** | [简体中文](INSTALL.zh-CN.md)

## Installer

TransReader currently supports Windows x64 and publishes one supported Setup package. It installs per-user without administrator access and provides a Start Menu entry, optional desktop shortcut, and standard uninstaller. OCR and local language models are on-demand components and are not bundled into the base installer.

`X.Y.Z` is the version number, for example `TransReader-v0.3.2-win-x64-setup.exe`. The Release also provides `TransReader-OCR-PP-OCRv5-mobile-win-x64.zip` for offline OCR import.

## Requirements

- 64-bit Windows 10 20H1 (19041) or later.
- An x64 processor with AVX support.
- At least 8 GB RAM for online mode; 16 GB is recommended for local AI.
- About 166 MB for the base app; the optional OCR component adds about 333 MB.
- Hy-MT2 1.8B or Qwen3 1.7B adds about 1.1–1.3 GB each; both together need about 2.5 GB.
- Microsoft Edge WebView2 Runtime. It is normally included with Windows 11. Install the official runtime if the translation pane stays blank.

## Install

1. Download `TransReader-vX.Y.Z-win-x64-setup.exe` and `TransReader-vX.Y.Z-SHA256SUMS.txt` from GitHub Releases.
2. Optionally verify the file before running it:

   ```powershell
   Get-FileHash .\TransReader-vX.Y.Z-win-x64-setup.exe -Algorithm SHA256
   ```

   The result must match the corresponding line in `SHA256SUMS.txt`.
3. Run the installer, choose a language, and review the MIT License.
4. Complete the wizard. The default directory is `%LOCALAPPDATA%\Programs\TransReader`, so administrator rights are not required.
5. Launch TransReader from the Start Menu.

> Community builds may not yet carry a commercial code-signing certificate, so Windows SmartScreen can show “Unknown publisher.” Only download releases from this project's GitHub Releases page and verify SHA-256 first.

## First run: start reading in three minutes

1. Open TransReader and choose a paper, manual, book, or scanned PDF.
2. Select a translation mode:
   - **Online API:** first run has no configured model or API key. Open AI Center → Add Model, select a MiMo, Kimi, GLM, or DeepSeek template (or enter a custom OpenAI-compatible URL), use Discover Models if desired, then save and test it.
   - **Local translation:** open AI Center → Local Components. Hy-MT2 1.8B Q4_K_M is recommended for translation and is more practical on ordinary Windows PCs than the 7B build. Install Qwen3 1.7B separately for reader questions and library analysis.
   - **OCR:** the first operation that needs recognition prompts for a roughly 112 MB download. You can also install it under Local Components or import the offline ZIP from the Release.
3. The original page remains on the left and the structured translation appears on the right. OCR and local models each have independent enable, verify, reload, repair, force-reinstall, and uninstall controls.
4. Select translated text to ask for an explanation or continue a contextual conversation.
5. Import long-term reading material into the library to retain progress, thumbnails, OCR, and translation caches.

## What does online mode send?

API keys are stored only in Windows Credential Manager and are never written to `settings.json` or echoed into the edit form or generated JSON. Depending on the selected model and multimodal setting, online mode sends the current page image or OCR text, necessary cross-page context, and your question to the endpoint you configured. Local mode does not send page content to an external model API.

For sensitive documents, use local mode and also set Library Analysis and Reader Assistant to a local model source.

## Updating

The app checks stable GitHub Releases at most once per day. You can also check manually under AI Center → Application Update. “Download and install” verifies the installer against the SHA-256 file published with the Release before launching the setup wizard and closing the current app. You may alternatively close TransReader and run a newer Setup manually. `%LOCALAPPDATA%\TransReader` is preserved during an in-place upgrade.

## Uninstalling

Open Windows Settings → Apps → Installed apps, find TransReader, and choose Uninstall.

To protect imported documents, uninstalling preserves `%LOCALAPPDATA%\TransReader`. Delete that directory manually only after backing up anything important; the operation cannot be undone.

## Troubleshooting

### The application does not open

Confirm the Windows version, x64 architecture, and AVX support. Check `%LOCALAPPDATA%\TransReader\logs\crashes.log`. Remove personal paths, document text, and secrets before attaching logs to an issue.

### The translation pane is blank

Install or repair Microsoft Edge WebView2 Runtime, then restart TransReader.

### OCR initialization fails

Open AI Center → Local Components → OCR, then try Reload followed by Install / Smart Repair. If needed, use Force Reinstall or download the offline OCR ZIP from the same Release and choose Import Offline Package. Components live under `%LOCALAPPDATA%\TransReader\ocr\versions`; the error view preserves the actual PaddleOCR diagnostic output.

### An online API cannot connect

Check the OpenAI-compatible Base URL, exact model name, account balance, and quota. Some gateways do not implement thinking parameters or JSON Schema. TransReader automatically retries common compatibility cases but cannot bypass provider permissions.

### A local AI download was interrupted

Choose Install again to resume. OCR switches between the GitHub Release and `ghproxy.net`; local models switch between Hugging Face and `hf-mirror`. Every component is verified by file size and SHA-256.
