# Changelog

All notable changes to TransReader are documented here. The project follows Semantic Versioning once public releases begin.

## [Unreleased]

## [0.3.1] - 2026-08-18

### Added

- In-app stable-release checks with installer download progress and SHA-256 verification.
- Provider templates for MiMo, Kimi, GLM, and DeepSeek, live configuration JSON preview, and `/models` discovery.
- Reproducible build dependency bootstrap with pinned URLs, sizes, and SHA-256 checks.
- Self-contained Windows x64 Setup packaging.
- English and Simplified Chinese installation/build manuals.
- GitHub CI, automated tagged releases, Dependabot, issue forms, and community files.
- Real product screenshots and a redesigned bilingual project introduction.

### Fixed

- Release publishes now include the generated WinUI PRI resources, preventing both Setup and extracted builds from exiting silently while loading `MainWindow.xaml`.
- Startup event handlers no longer access services before window initialization has completed.

### Changed

- Releases now ship one supported Windows x64 Setup package instead of separate Setup and Portable packages.
- The release payload references only required Windows App SDK components and excludes unused AI, ML, Widgets, Toolkit, symbols, and diagnostic helpers.
- First run starts with zero online models. Templates are offered only when adding a model, and an API key is never prefilled or echoed into the form or generated JSON.
- Online providers now use their own default temperature and built-in authentication convention; the configuration form no longer exposes temperature or authentication controls.
- Connection testing now shows progress and results next to the button and stops with a clear timeout after 20 seconds.

### Security

- Expanded ignore rules for local credentials and certificate files.
- Release SHA-256 manifest generation.
- Online credentials use a fresh Windows Credential Manager namespace so keys saved by development or older builds cannot appear as bundled defaults.

## [0.3.0] - 2026-08-17

### Added

- Side-by-side PDF and translated Markdown reading.
- Local PP-OCRv5 processing for scanned PDFs.
- OpenAI-compatible online translation and optional Qwen3 1.7B local translation.
- Reader assistant, library analysis, domain-aware prompts, caching, and usage statistics.
