namespace BroChoos.Core.Abstractions;

// One launchable browser installation. BrowserId is a stable identifier
// ("edge-stable", "chrome-stable"); DetectionSource records which D-181 source
// tier produced the candidate ("known-path" or "registry" in 0A-2).
public sealed record BrowserCandidate(
	string BrowserId,
	string DisplayName,
	string ExePath,
	string Channel,
	string DetectionSource)
{
	public IReadOnlyList<string>? ProfileIds { get; init; }

	// User-visible profile names, index-aligned with ProfileIds ("" where none is known).
	// PERSONAL DATA: a profile is often named after a person. Shown in the picker UI only;
	// never logged, never in the problem log or the diagnostics bundle.
	public IReadOnlyList<string>? ProfileNames { get; init; }

	public bool HasUnresolvedProfiles { get; init; }

	public bool IsCustomBrowser { get; init; }

	public bool IsPortableBrowser { get; init; }
}
