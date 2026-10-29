namespace BroChoos.Core.Cli;

public sealed class ParsedArgs
{
	public string? Url { get; set; }
	public bool SafeMode { get; set; }
	public bool NoUi { get; set; }
	public bool ShowHelp { get; set; }

	// 0A-4 default-apps actions. At most one is accepted per invocation; the
	// strict parser rejects a combination (exit 3), and safe mode only records
	// the flags so the ignored-input warning can name them.
	public bool Register { get; set; }
	public bool Unregister { get; set; }
	public bool VerifyDefaults { get; set; }
	public bool OpenDefaultApps { get; set; }
	// 0A-5 (D-v3.5-072): the RAW value of --portable-data, exactly as typed. The
	// parser stores it and nothing else: no trimming, no full-path resolution, no
	// existence probe, no directory creation. Whoever resolves the storage root
	// owns that normalization (StorageRootResolver already does it).
	public string? PortableDataPath { get; set; }

	// W8.2: the optional per-action selector for safe mode (`--safe-mode-action <id>`).
	// Stored RAW like PortableDataPath: the runner owns vocabulary validation, the
	// parser only carries the token. Null means "offer the full recovery contract".
	public string? SafeModeAction { get; set; }

	// True when --safe-mode was combined with a --safe-mode-action whose value was missing,
	// option-like or blank. The rescue hatch never throws, so the malformed selector is
	// carried as state: Bootstrap turns it into a request SafeModeRunner reports as failed,
	// instead of silently running the full recovery contract the user did not ask for.
	public bool SafeModeActionInvalid { get; set; }

	// W8.1: `--settings` opens the frozen 12-section Settings shell and exits. It is a
	// UI-only mode: Bootstrap is UI-free and does not act on it; the App routes it to the
	// Settings launcher before Bootstrap runs. Parsed here so it is a DOCUMENTED, first-
	// class option rather than an undocumented argument the parser would reject.
	public bool Settings { get; set; }

	public bool HasUrl => Url is not null;
	public bool HasAnyDefaultsAction => Register || Unregister || VerifyDefaults || OpenDefaultApps;

	// Safe-mode lenient parse tallies. Counts only; raw values are never kept (privacy).
	internal int IgnoredPositionalCount { get; set; }
	internal int IgnoredUnknownOptionCount { get; set; }
}
