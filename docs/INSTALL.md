# TransReader Installation and User Guide

**English** | [简体中文](INSTALL.zh-CN.md)

## Which package should I download?

TransReader currently supports Windows x64. Both packages contain the same features. The optional local AI model is downloaded on demand instead of being bundled into either package.

| File | Best for | System integration |
| --- | --- | --- |
| `TransReader-vX.Y.Z-win-x64-setup.exe` | Most users - recommended | Per-user install, Start Menu entry, optional desktop shortcut, standard uninstaller |
| `TransReader-vX.Y.Z-win-x64-portable.zip` | Portable use, restricted PCs, or manually managed installations | No installer registration; extract the whole archive and run it |

`X.Y.Z` is the version number, for example `TransReader-v0.3.0-win-x64-setup.exe`.

## Requirements

- 64-bit Windows 10 20H1 (19041) or later.
- An x64 processor with AVX support.
- At least 8 GB RAM for online mode; 16 GB is recommended for local AI.
- About 600 MB of free disk space, plus about 1.3 GB when local AI is installed.
- Microsoft Edge WebView2 Runtime. It is normally included with Windows 11. Install the official runtime if the translation pane stays blank.

## Recommended: Setup installer

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

## Portable package

1. Download `TransReader-vX.Y.Z-win-x64-portable.zip`.
2. Extract the entire archive to a writable directory such as `D:\Apps\TransReader`.
3. Do not run it from the ZIP preview and do not copy only the main EXE.
4. Run `TransReader.App.exe`.

The application folder is portable, but settings, caches, and the library remain under `%LOCALAPPDATA%\TransReader` so an update cannot accidentally remove your reading data.

## First run: start reading in three minutes

1. Open TransReader and choose a paper, manual, book, or scanned PDF.
2. Select a translation mode:
   - **Online API:** open AI Center, select a preset or add any OpenAI-compatible endpoint, enter the model and API key, then test the connection.
   - **Local AI:** open AI Center → Local AI and choose Install. TransReader downloads Qwen3 1.7B and llama.cpp and verifies both with SHA-256.
3. The original page remains on the left and the structured translation appears on the right. Scanned PDFs are recognized locally with PP-OCRv5.
4. Select translated text to ask for an explanation or continue a contextual conversation.
5. Import long-term reading material into the library to retain progress, thumbnails, OCR, and translation caches.

## What does online mode send?

API keys are stored only in Windows Credential Manager and are never written to `settings.json`. Depending on the selected model and multimodal setting, online mode sends the current page image or OCR text, necessary cross-page context, and your question to the endpoint you configured. Local mode does not send page content to an external model API.

For sensitive documents, use local mode and also set Library Analysis and Reader Assistant to a local model source.

## Updating

- **Setup:** close TransReader and run the newer installer. Program files are replaced while `%LOCALAPPDATA%\TransReader` is preserved.
- **Portable:** close the app and extract the new version to a new folder. Verify it works before removing the old program folder.

## Uninstalling

- Setup: open Windows Settings → Apps → Installed apps, find TransReader, and choose Uninstall.
- Portable: close the app and remove its extracted program directory.

To protect imported documents, uninstalling preserves `%LOCALAPPDATA%\TransReader`. Delete that directory manually only after backing up anything important; the operation cannot be undone.

## Troubleshooting

### The application does not open

Confirm the Windows version, x64 architecture, and AVX support. Check `%LOCALAPPDATA%\TransReader\logs\crashes.log`. Remove personal paths, document text, and secrets before attaching logs to an issue.

### The translation pane is blank

Install or repair Microsoft Edge WebView2 Runtime, then restart TransReader.

### OCR initialization fails

Do not remove `models`, `TransOcrNative.Host.exe`, or adjacent DLLs. Re-extract the complete Portable archive or rerun Setup to repair the installation.

### An online API cannot connect

Check the OpenAI-compatible Base URL, exact model name, account balance, and quota. Some gateways do not implement thinking parameters or JSON Schema. TransReader automatically retries common compatibility cases but cannot bypass provider permissions.

### A local AI download was interrupted

Choose Install again to resume. TransReader can switch between the listed sources and verifies file size and SHA-256 before installation.
