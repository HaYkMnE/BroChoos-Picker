# Changelog

All notable changes to BroChoos Picker are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-10-29

Initial public release.

### Added

- Browser picker window that opens next to the mouse pointer when you click a link, with one tile per browser, and one tile per profile for Chrome, Edge and Brave (up to nine tiles).
- Detection of Microsoft Edge, Google Chrome and Brave (stable and preview channels) and Mozilla Firefox (stable, Developer Edition, Nightly).
- Chromium profile discovery for Edge, Chrome and Brave; links open in the chosen profile. The last-used profile is listed first.
- Firefox support: links open in the install's default profile.
- Manual add of a portable or custom browser by choosing its executable; drives are never scanned.
- First-run consent: no browser scan until you choose "Find browsers" (or add one by hand, or skip).
- Keyboard control: `1`–`9`, Left/Right arrows (Up/Down in vertical layout), `Enter`, `Esc`, `F` (open with the system default), `Ctrl+C` (copy link), `Ctrl+H` (skip history for the next link), `Ctrl+Backspace` twice within 4 seconds (clear history), `F1` (shortcut list).
- Cancel copies the link to the clipboard; clicking outside the window closes it.
- Single instance: links clicked while the picker is open are forwarded to it over a local named pipe.
- The picker holds back while a fullscreen game or presentation runs (up to 30 seconds).
- Per-user registration as a Windows browser for `http`, `https` and `.url` (`--register`, `--unregister`, `--verify-defaults`, `--open-default-apps`). `UserChoice` is never written.
- Per-user installer `BroChoos-Picker-Setup-1.0.0-x64.exe` (no admin rights) that registers on install and unregisters on uninstall.
- Portable zip `BroChoos-Picker-1.0.0-x64-portable.zip` with a `.portable` marker, so settings stay in `BroChoosConfig` next to `BroChoos.exe`. `--portable-data <path>` picks another data folder.
- Both builds are self-contained: the .NET runtime is included.
- `SHA256SUMS.txt` and GitHub build provenance attestations for both release files.
- Settings backups: the newest 10 per file are kept; backups older than 30 days are pruned the next time that settings file is saved.
- Settings window (`--settings`).
- Safe mode (`--safe-mode`) with recovery actions: diagnostics export, restore backup, reset defaults, open data folder, registration repair, synthetic link probe, repair settings.
- Local activation history that stores only the main domain of each link.
- Local problem log without URLs, paths or user names; 14-day retention and 10 MB rotation.
- Icon disk cache so tiles appear at once.
- User interface in English, Russian and Armenian.
- Windows high-contrast support and per-monitor DPI awareness.

[Unreleased]: https://github.com/HaYkMnE/BroChoos-Picker/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/HaYkMnE/BroChoos-Picker/releases/tag/v1.0.0
