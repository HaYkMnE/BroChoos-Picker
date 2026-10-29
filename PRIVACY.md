# Privacy

BroChoos Picker is a browser picker that runs on your PC. This page explains, in plain words, what it stores, where, for how long, and when it uses the network.

## Short version

- **No telemetry.** No analytics, no usage statistics, no automatic crash reports.
- **Your data is stored locally.** All files below are on your PC. Settings and the browser list live under `%LocalAppData%\BroChoos` (installed version; the portable version keeps settings and the browser list in `BroChoosConfig` next to the exe). Logs are always under `%LocalAppData%\BroChoos`.
- **The picker makes no network connections.** The only network code is a planned, opt-in short-link helper that cannot be turned on in this release. The browser you choose then loads the link as usual.

## What is stored, and where

In the installed version, data lives in `%LocalAppData%\BroChoos`. In portable mode (a `.portable` file next to `BroChoos.exe`, or `--portable-data <path>`), the settings files below marked *data folder* move to that portable folder. The `logs\` files (including `problems.log`) and the `backups\registration-*.json` files are always written to `%LocalAppData%\BroChoos`, even in portable mode.

| File | What it holds | How long it is kept |
| --- | --- | --- |
| `browsers.json` (data folder) | Browsers found on your PC, their program paths, and browser profile names. Profile names are often a person's name. They are shown on tiles and never logged or exported. | Until you delete it |
| `settings.json`, `app.json`, `rules.json` (data folder) | Your settings | Until you delete them |
| `history.json` (data folder) | For each link you open: the time, the **main domain only** (for example `example.com`, never the full URL), the browser and profile you chose, the app the link came from (if known), and the matched rule (if any). In the picker, `Ctrl+H` skips the next link and `Ctrl+Backspace` pressed twice within 4 seconds clears the history. The Settings window has no history controls yet. | No automatic expiry yet |
| `backups\` (data folder) | Earlier versions of each settings file | The newest 10 per file. Backups older than 30 days are pruned the next time that settings file is saved. |
| `backups\corrupted\` (data folder) | Damaged settings files moved aside by repair, kept byte for byte | Until you delete them |
| `cache\icons\` (data folder) | Copies of browser program icons, so tiles appear at once. File names are hashes, never paths. At most 64 files and 8 MB. Safe to delete. | Oldest removed when full |
| `logs\problems.log` | Warnings, errors and crashes, plus short steps such as "picker opened with 4 tiles". Each line holds a time, a level, an event name, an error type and short codes. **Never** a URL, host name, file path, user name, error message or stack trace. | Lines older than 14 days are removed once per run; the file rotates at 10 MB, keeping one older file |
| `logs\system_changes.log` | One line per change BroChoos made to its own registry entries | Until you delete it |
| `logs\cleanup_events.jsonl` | Written only when tracking-parameter cleanup is on. Tracking cleanup is a planned feature that cannot be turned on in this release, so this release writes nothing to this file. If a leftover file from an earlier test build exists, the picker tries to delete it when it starts. It skips the file if the file or its folders are a link or junction, and if the delete fails it logs only the error type and carries on. When cleanup is on in a future release, each line holds the time, the link's host name, a reason code, whether the link changed, the cleanup rule set used, and the names of removed parameters; never the full URL. | Not written in this release. When cleanup is on: 90 days, at most 2000 lines |
| `backups\registration-*.json` | A copy of BroChoos's registry entries, taken before each registration change | Until you delete them |
| `diagnostics\safe-mode-diagnostics.json` (data folder) | Written only when you choose "export diagnostics" in safe mode. Holds build and storage status only; no URLs, paths, profile names or history. You decide whether to share it. | Until you delete it |

`history.json` has no automatic retention limit in this release. Clear it with `Ctrl+Backspace` (twice) in the picker, or delete the file.

## The clipboard

When the picker opens, it reads the clipboard once and keeps the value in memory for that window only. It is never saved, shown or sent. When you cancel the picker, or press `Ctrl+C`, BroChoos Picker writes the link to the clipboard.

## When the network is used

- **The picker:** never. Network code lives only in the NetworkHelper process; automated tests forbid network APIs in the core and the picker.
- **Opening a link:** BroChoos Picker hands the link to the browser you chose. The browser then loads it, as it would without BroChoos.
- **Short-link expansion (off, planned):** the helper can expand links from `bit.ly`, `t.co`, `tinyurl.com`, `shorturl.at` and `is.gd` with `HEAD` requests only, without cookies or a referrer, refusing private network addresses, at most five redirects and 1.5 seconds. It is a planned, opt-in feature that cannot be turned on in this release; when it ships, it will need your consent before it runs.
- **Updates:** BroChoos Picker does not check for updates. Update it through winget, Scoop, or a new download.

Links forwarded between two BroChoos windows travel over a local named pipe on your PC, not the network.

## Registry

BroChoos Picker writes only under `HKEY_CURRENT_USER`, to register itself as a browser you can choose. It never writes the Windows `UserChoice` key, which records your default browser. `BroChoos.exe --unregister` (also run by the uninstaller) removes only the keys it created.

## Removing your data

Uninstall BroChoos Picker, then delete `%LocalAppData%\BroChoos`. For the portable version, also delete its folder.

## Questions

Open an issue on [GitHub](https://github.com/HaYkMnE/BroChoos-Picker/issues). For security problems, see [SECURITY.md](SECURITY.md).
