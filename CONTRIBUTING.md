# Contributing to BroChoos Picker

Thanks for helping. Bug reports, translation fixes and pull requests are all welcome. By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- For a bug, open an issue with the bug report form.
- For a new feature, open a feature request first, so we can agree on the approach before you write code.
- For a security problem, follow [SECURITY.md](SECURITY.md) instead of opening an issue.

## Dev setup

You need:

- Windows 10 or 11, x64;
- the .NET SDK pinned in [global.json](global.json) (10.0.201, later patches of the 10.0.2xx feature band allowed);
- Git.

Any editor works. The repository's `.editorconfig` sets the formatting rules.

Clone and build:

```powershell
git clone https://github.com/HaYkMnE/BroChoos-Picker.git
cd BroChoos-Picker
dotnet restore BroChoos.sln
dotnet build BroChoos.sln --configuration Release --no-restore
```

## Build and test

These are the checks CI runs. Run them before you open a pull request:

```powershell
dotnet format BroChoos.sln --verify-no-changes --no-restore
dotnet build BroChoos.sln --configuration Release --no-restore
dotnet test BroChoos.sln --configuration Release --no-build
```

CI also builds and tests the Debug configuration, because some test scaffolding exists only in Debug:

```powershell
dotnet build BroChoos.sln --configuration Debug --no-restore
dotnet test BroChoos.sln --configuration Debug --no-build
```

Run the app from source:

```powershell
dotnet run --project src/BroChoos.App -- --help
dotnet run --project src/BroChoos.App -- https://example.com
dotnet run --project src/BroChoos.App -- --settings
```

`--register` and `--unregister` change your user registry (`HKCU`). Use `--unregister` to clean up after testing.

## Project layout

| Project | Role |
| --- | --- |
| `src/BroChoos.Core` | Platform-neutral logic: CLI parsing, ranking, storage contracts, privacy projections |
| `src/BroChoos.Platform.Windows` | Browser and profile discovery, registration, storage, single instance |
| `src/BroChoos.App` | Avalonia UI: picker, settings, localization, problem log |
| `src/BroChoos.LaunchHelper` | Starts the chosen browser |
| `src/BroChoos.StorageHelper` | Writes the browser catalog after consent |
| `src/BroChoos.NetworkHelper` | The only project allowed to use the network (short-link expansion) |
| `tests/` | Test projects, including architecture tests that enforce these boundaries |

## Code style

- C#: tabs for indentation, CRLF line endings, file-scoped namespaces, braces on a new line.
- JSON, YAML, XML, AXAML and Markdown: two spaces, LF line endings.
- Warnings are errors (`TreatWarningsAsErrors`). Fix the warning; do not suppress it without a comment that explains why.
- Never log a URL, host, file path or user name. The problem log enforces this, and tests check it.
- New user-visible text goes into all three language packs (en, ru, hy). Tests check that the packs have the same keys.
- Do not add network calls outside `BroChoos.NetworkHelper`. The architecture tests will fail.

## Pull request checklist

- [ ] The PR does one thing and links its issue.
- [ ] `dotnet format`, build and tests pass locally (commands above).
- [ ] New behaviour has tests.
- [ ] User-visible text is in `en`, `ru` and `hy`.
- [ ] No URLs, paths or personal data in logs.
- [ ] Docs (README, PRIVACY.md, CHANGELOG.md) are updated if behaviour changed.

## License

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE).
