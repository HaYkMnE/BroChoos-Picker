# Run Dev

> Developer notes for building and running BroChoos from source. The milestone sections
> below are kept as historical background; where they disagree with `README.md`, the
> README describes the current behaviour.

## Requirements

- Windows 10/11
- .NET 10 SDK matching `global.json`

## Commands

```powershell
dotnet restore BroChoos.sln
dotnet build BroChoos.sln --configuration Debug
dotnet test BroChoos.sln --configuration Debug
dotnet run --project src/BroChoos.App -- --no-ui https://example.com   # explicit-choice required: fails closed
```

Solution builds output to `bin\x64\<Config>\...` (a direct project build without `-p:Platform=x64` still uses `bin\<Config>\...`); old `bin\Debug` / `bin\Release` folders can be deleted by hand.

## Browser detection and launch (historical)

> Superseded: `BroChoos.exe <URL>` now shows the Picker; it does not launch the first
> candidate. The Picker starts from an empty snapshot, and on the URL path detection runs
> only after first-run consent (Find browsers, in `BroChoos.StorageHelper`). Detection now covers
> Edge, Chrome, Brave and Firefox (`WindowsBrowserDetector`). The text below is the original
> record.

`BroChoos.exe <URL>` now detects a browser and actually opens the URL. Scope is
deliberately Edge and Chrome only; the Picker UI, profiles and rules come later.

Detection covers the first three D-181 tiers: known install paths first, then the
registry (`HKLM`/`HKCU` `Software\Clients\StartMenuInternet`, then
`RegisteredApplications`). Later D-181 tiers (App Paths, Start Menu shortcuts,
portable/custom `.exe`) are later milestones (the full tier list lives in the locked
decision archive, not in this repo). Duplicates are collapsed by executable path. The
first candidate is the launch target, and vendor order (Edge before Chrome) is
applied after tier discovery and therefore dominates it.

Launching (D-183) uses `ProcessStartInfo` with `ArgumentList`, passing the URL as
its own argument with `UseShellExecute=false`. No shell, no command-string
concatenation. A target equal to the running BroChoos executable is refused
(D-304 recursion guard). Failures come back as a `LaunchResult`, never an
exception (D-308/D-309).

## Exit codes (canonical)

This table is the exit-code contract for the whole product. Other docs point here
instead of restating parts of it. The constants live in
`src/BroChoos.Core/ExitCodes.cs`.

| Code | Constant | Meaning |
| ---- | -------- | ------- |
| 0 | `Success` | Picker closed after a launch or a user cancel; `--help`; a secondary instance that forwarded |
| 1 | `GeneralError` | `--no-ui <URL>` (fails closed, opens nothing); the single-instance guard could neither acquire nor forward (`UrlRunner`); or an unexpected exception reached `Bootstrap` |
| 2 | `NoArgs` | No URL to open (help text is printed) |
| 3 | `InvalidUrl` | The URL failed validation, or the arguments were rejected: unknown option, second positional argument, missing/blank/option-shaped `--portable-data` value, missing/blank/option-shaped `--safe-mode-action` value, `--safe-mode-action` without `--safe-mode` ("requires --safe-mode"), two default-apps actions at once, or `--settings` combined with another mode (URL, default-apps action, `--no-ui`, `--portable-data`) |
| 4 | `NoBrowserDetected` | Declared, not currently returned by any path |
| 5 | `BrowserLaunchFailed` | Declared, not currently returned by any path (a failed launch inside the Picker is shown in the window; the Picker stays open) |
| 10 | `SafeModeOk` | `--safe-mode` completed |
| 11 | `SafeModeFailed` | An attempted safe-mode action failed or was unavailable, an unknown `--safe-mode-action` id was given, or the run was cancelled before the contract was shown (`SafeModeRunner.RunAsync`): a run with `--safe-mode --safe-mode-action <id>` fails when THAT action did not complete; a full-contract run fails only when some action failed |

`ConfigCorruption` (99) is declared in `ExitCodes.cs`, and `Bootstrap` checks for it
(`urlExitCode == ExitCodes.ConfigCorruption`) to enter the broken-config safe-mode route.
No code path returns 99 today, so that fallback, and its
`[safe-mode] routing=config-corruption-recovery` line, cannot be reached. Treat 99 as
reserved.

Console output may echo the URL because it is the user's own input. Persistent log
lines stay URL-free.

### Launch smoke test

From the repo root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/smoke-launch.ps1
```

The script asserts the current `--no-ui` contract: `--no-ui <url>` must exit 1,
print the "requires an explicit browser choice; nothing was opened" notice on
stderr, report `[startup] INGRESS_ACCEPTED` on stdout, and print neither
`Opening in` nor a `[secondary]` forward line. It opens no browser window. Each
check prints an explicit `PASS`/`FAIL` line. Close any running BroChoos first, or
the run becomes a secondary instance and the last check fails.

`BROCHOOS_TEST_NO_BROWSERS` is a test-only override read in exactly one place
(`WindowsBrowserDetector`). It is never set by the product and is never written to
a log. The smoke script no longer uses it, because `--no-ui` returns before
detection runs.

### Expected test counts differ by configuration

Release runs FEWER tests than Debug, because Debug-only scaffolding is excluded from
compilation in every non-Debug configuration and its tests go with it. The exact
difference changes whenever a scaffold is added or retired, so no number is recorded
here. The current figure, and the named tests that make it up, live in the one place
kept up to date: the comment above
`Detect_TestOnlyOverride_ForcesEmptyDetection` in
`tests/BroChoos.Platform.Windows.Tests/Browsers/WindowsBrowserDetectorTests.cs`. CI
builds Release, so CI reports the lower total. A Release run reporting the same count
as Debug means a configuration gate stopped working and Debug-only code is shipping — treat that as a defect, not a
quirk. (The two `DpiAwarenessTests` are deliberately NOT configuration-gated: they
swap/restore the thread DPI context through pure Win32 plus product code that
compiles into every build, and they pass in both configurations — see the comment
at the top of `tests/BroChoos.App.Tests/DpiAwarenessTests.cs`.)

What matters is the DIRECTION, not any particular figure: Release below Debug is
healthy, Release equal to Debug is a defect. Absolute totals move whenever tests are
added and the size of the gap moves whenever scaffolding is added or retired, so
neither is recorded here — a number in this file goes stale silently, and a reader who
trusts it diagnoses a break that does not exist.

## Minimal Picker shell

`BroChoos.exe <URL>` now shows the Picker instead of launching directly: a small
borderless topmost window near the cursor with three zones (D-125) - a link card
(host + short read URL, long query strings collapsed), one choice card per
detected browser (slot number, name, browser id), and a bottom bar with the key
hints. `BroChoos.exe --no-ui <URL>` is preserved as a typed compatibility input
but now fails closed with explicit-choice-required behavior: it opens nothing,
performs no candidate discovery, and does not show the Picker.

Keyboard (D-130): `1`-`9` focus the card in that slot, arrows and Tab move the
focus, `Enter` acts ONLY on the visibly focused card (D-131; with no card
focused, Enter does nothing), `Esc` cancels (closes and opens NOTHING, exit code
0 - a user cancel is not a failure), `F` opens the URL through the system
association (fallback surface, D-130), `Ctrl+C` writes the URL to the clipboard.
BroChoos reads the clipboard once at open (to support the not-yet-wired
smart-skip/recovery path; ON by default, `ClipboardReadAtOpenEnabled`) and writes it
on `Ctrl+C` and on the cancel/dismiss path (ON by default, `CancelToClipboardEnabled`);
both switches are options-driven via `PickerClipboardOptions`. The read value is
session-local and transient for one session - it stays in memory, is never logged, is
never surfaced in the UI, and is never used to launch anything.
A persisted settings store now exists: `--settings` opens the Settings shell
(`src/BroChoos.App/Settings/`), which persists through the generation/CAS
store (`SettingsStore` + `GenerationMutationCoordinator` + `AtomicJsonFileStore`), including
the ControlDepth choice. Still open: the two clipboard switches above have no
Settings-UI toggle yet, and the composition root still passes the default-on values for them.

Placement (D-149) is computed from `GetCursorPos` + `MonitorFromPoint` BEFORE
the window is shown, so there is no default-position flash (spike finding D2),
and the window size is converted with the TARGET monitor's own DPI scale before
clamping into that monitor's work area (spike finding D1).

Foreground activation runs the spike-proven ladder (`BringWindowToTop` ->
`SetForegroundWindow`, then attach to the incumbent foreground thread's input
queue and retry), always VERIFIED by re-reading `GetForegroundWindow()`. The
ladder runs off the UI thread with a ~2.5 s cap (the inner attach budget is 1.5 s;
the headroom absorbs one blocked Win32 call, so the ladder's own bounded verdict
wins over the outer cap) so a hung foreground application
can never freeze the Picker (spike item 10). If verification still fails, the
window stays topmost and the taskbar flashes instead. `SPI_SETFOREGROUNDLOCKTIMEOUT`
is never touched.

Single instance: the existing `WindowsSingleInstanceGuard` starts in the primary
role with the same per-SID pipe. A URL forwarded by a second instance re-routes
into the running Picker (the link card updates and foreground is re-acquired):
the guard raises an event and `ForwardedUrlRouter` hands it to the session. Every
arrival goes through one FIFO queue and is delivered exactly once, in arrival order.
When no picker session exists yet (a `--no-ui` primary, or a forward landing before
the window is up), the router prints the URL-free line
`[primary received] forwarded activation buffered` and keeps the request queued.

A launch failure inside the Picker is fail-open: the window stays up and shows
the error in the bottom bar with the retry choices (another card, `F`, `Ctrl+C`,
`Esc`). The Picker path returns 0 both on a successful launch and on a user
cancel. `--no-ui` returns 1 (fail closed); see the table above.

`.axaml` is now a governed file type (LF, no BOM): see `*.axaml` in
`.gitattributes` and the matching section in `.editorconfig`, added so
`dotnet format --verify-no-changes` stays green.

## Default Apps / URL handoff dev flow

BroChoos can now honestly register itself as a Windows link-handler candidate
(D-187: capability declaration only — the USER picks the default in Windows
Settings, and BroChoos NEVER writes UserChoice). Four CLI actions, all per-user
(HKCU only, D-273), exit 0 on success and 1 (`GeneralError`) on failure — the
canonical exit-code table is unchanged:

```powershell
BroChoos.exe --register           # declare http/https/.url capabilities (backup first, idempotent)
BroChoos.exe --unregister         # remove ONLY BroChoos-owned keys (backup first, prints restore path)
BroChoos.exe --verify-defaults    # three-step table: registered / selected (read-only UserChoice snapshot) / handled
BroChoos.exe --open-default-apps  # Russian guide + opens ms-settings:defaultapps, then re-verifies
```

The registered shape (v13 §14): `HKCU\Software\Classes\{http,https,.url}`
(`URL Protocol`="" + `shell\open\command` `"<exe>" "%1"`), the ProgIDs
`HKCU\Software\Classes\BroChoosURL` and `BroChoosURL.File` the capability model
resolves through, `HKCU\Software\Clients\StartMenuInternet\BroChoos` with
`Capabilities` (URLAssociations / FileAssociations), and
`HKCU\Software\RegisteredApplications\BroChoos`. Every mutation is preceded by a
backups\registration-{utc}-{4hex-suffix}.json snapshot and followed by one JSONL line per
action in logs\system_changes.log (locked D-291 schema: ts/action/result/path;
redacted — no URLs, no full paths, no PII; both under `%LocalAppData%\BroChoos`).
The D-187 invariant is code-enforced: the registry write seam THROWS on any
UserChoice path, pinned by tests. `ms-settings:defaultapps` is opened with
`UseShellExecute=true` — the single sanctioned exception, documented in
`WindowsDefaultAppsGuide` (a protocol URI only the shell can resolve, constant
value, empty arguments).

### Registration smoke test

From the repo root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/registration-smoke.ps1
```

Runs the full cycle on the real user hive (register -> verify -> guide ->
unregister -> verify) with the UserChoice byte-identical invariant check, and
ALWAYS re-unregisters in `finally`. Windows Settings opens once for the guide
step — EXPECTED. `tools/url-activation-sim.ps1` simulates a default-handler
activation end to end (pipeline markers + graceful OS close) and re-proves
UserChoice is untouched.
