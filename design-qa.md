# Design QA

- Source visual truth: 设计稿截图（本地临时文件，未入库）。
- Implementation evidence: live Windows capture of `TransReader.App.exe`, page 37, captured 2026-08-12 with Windows Graphics Capture.
- Viewport: 1489 × 894 physical pixels; maximized desktop app; light theme; system density as configured by the user.
- Density normalization: source screenshots are focused crops at native pixels; implementation was checked both as a full-window native capture and focused live control regions. No screenshot scaling was used for alignment judgments.
- State: page 37 loaded; visual draft/fusion review active; OCR drawer collapsed; thumbnail pane open.

## Full-view comparison evidence

The live page-37 capture shows the three 44-DIP headers sharing one baseline. The page navigation Symbol is optically centered within the toolbar, the translation status pill uses a centered icon/text row, and the OCR row is no longer overlaid. The translation title is compact and the bottom OCR drawer remains inside its own row. The assistant implementation is no longer a fixed overlay; it is an exclusive WebView mode controlled by the WinUI header.

## Focused region evidence

- Brand icon: inspected generated 16, 20, 24, 32, 48 and 64 px frames together. Small frames retain the layered front/back panels and readable central white mark; the application explicitly assigns the multi-frame ICO to `AppWindow`.
- Toolbar navigation: live capture shows the WinUI pane Symbol centered in a 36 × 32 DIP hit target with a pale selected background.
- Translation status: live capture shows the completion/working icon and label sharing a 26-DIP pill baseline with no vertical drift.
- OCR drawer: live capture shows icon, `OCR 原文`, `查看`, and chevron in non-overlapping grid columns.
- Assistant: DOM structure and responsive CSS were checked for wide sidebar (168 px), narrow history disclosure, independently scrolling conversation, preserved mode scroll positions, fixed composer, pending/stop/error states, and forced-colors tokens.

## Findings and comparison history

1. Earlier P1: WebView assistant covered the translated page and duplicated headings/page metadata.
   Fix: replaced the fixed drawer with mutually exclusive translation/assistant views and moved the assistant title/return action to the native WinUI header.
   Post-fix evidence: no fixed-position assistant remains; page title and OCR row are hidden in assistant mode.
2. Earlier P1: OCR label and right action occupied the same grid track and visibly overlapped.
   Fix: changed to `Auto / * / Auto` columns, ellipsis on the title, and responsive hiding of the `查看` label.
   Post-fix evidence: live page-37 capture shows separated columns and no collision.
3. Earlier P2: custom pane Path was coarse and visually off-center.
   Fix: replaced it with WinUI `OpenPane` / `ClosePane` Symbols and a restrained brand-tinted checked state.
   Post-fix evidence: live toolbar capture shows consistent optical size and alignment with neighboring WinUI icons.
4. Earlier P2: translation status text had no shared icon/text layout.
   Fix: introduced a 26-DIP state pill with centralized state-to-icon mapping.
   Post-fix evidence: live completion and working states are centered and do not shift.
5. Earlier P2: taskbar icon used one mechanically scaled complex source.
   Fix: generated optically cropped and separately sharpened frames for 16–512 px, created a 16–256 PNG-frame ICO, and assigned it to `AppWindow`.
   Post-fix evidence: focused multi-size frame inspection shows improved canvas coverage and preserved layered identity.

## Required fidelity surfaces

- Fonts and typography: Segoe UI / Microsoft YaHei UI retained; 12 px status label has explicit 16 px line height; history and empty-state hierarchy use restrained weights and truncation.
- Spacing and rhythm: native header remains 44 DIP; state pill is 26 DIP; assistant sidebar is 168 px; OCR and assistant composer use explicit non-overlapping grids.
- Colors and tokens: existing WinUI theme resources remain authoritative; assistant CSS uses light/dark variables and forced-color overrides; brand accent is limited to selected/primary states.
- Image quality and assets: existing supplied brand source was retained; dedicated raster frames and ICO were generated from it rather than replacing it with a fabricated logo.
- Copy and content: existing Chinese labels and assistant history data are preserved; duplicate page/helper headings were removed.

## Verification

- `node --check reader.js`: passed.
- Core unit tests: 25 passed, 0 failed.
- WinUI x64 build: passed with 0 warnings and 0 errors.
- Primary interactions checked: document open, page-37 navigation, translation status transition, translation/assistant mode wiring, OCR collapsed state.
- Console errors: JavaScript syntax check passed; WebView remained loaded during live capture with no visible script error state.

## Follow-up polish

- P3: Windows may retain a stale taskbar icon until the next shell refresh; the executable and AppWindow now both reference the corrected ICO, and no icon-cache deletion was performed.

final result: passed
