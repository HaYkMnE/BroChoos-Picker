# Manual Unregister

BroChoos registers itself with Windows: per-user (HKCU) capability keys for
http, https, and .url, the `BroChoosURL` / `BroChoosURL.File` ProgIDs,
`Software\Clients\StartMenuInternet\BroChoos`, and the `RegisteredApplications`
capability pointer. It never writes `UserChoice` — the user's selection (including
its hash) belongs to Windows Settings alone.

To remove the registration: run

    .\BroChoos.exe --unregister

The tool removes ONLY keys BroChoos itself owns (a surgical walk that prunes empty
parents and refuses to touch non-empty shared keys), then prints the restore
instructions. Every run first takes a timestamped backup into
`%LocalAppData%\BroChoos\backups` (files named `registration-{utc}-{4hex}.json`),
so pre-existing foreign state captured there can be restored by hand if ever needed.

If the tool itself is unavailable, the exact paths it owns are listed in
`RUN_DEV.md` (Default Apps / URL handoff section); `.\BroChoos.exe --verify-defaults` shows
which targets are currently registered. The owned keys can be deleted with
`reg delete` or Registry Editor under
`HKEY_CURRENT_USER\Software\Classes` and
`HKEY_CURRENT_USER\Software\Clients\StartMenuInternet` — delete ONLY the
BroChoos-named entries (`BroChoosURL`, `BroChoosURL.File`, `BroChoos`) and BroChoos
capability values from the `http`, `https`, and `.url` keys. Never delete or modify
`UserChoice`, and never remove content you do not recognize: foreign verbs may share
those scheme keys.

This file covers the Windows registration only. For a full uninstall, see
"How do I uninstall?" in `README.md`; settings and logs stay in
`%LocalAppData%\BroChoos` until you delete that folder.
