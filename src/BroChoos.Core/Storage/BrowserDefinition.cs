namespace BroChoos.Core.Storage;

// One launchable browser as persisted in browsers.json (0A-5). BrowserId is a
// stable identifier ("edge-stable", "chrome-stable"); DetectionSource records the
// D-181 source tier that produced the entry ("known-path" or "registry").
//
// LastVerifiedAt is the ISO-8601 UTC instant the exe path was last confirmed to
// exist; null means "not yet verified".
//
// Deferred to 0B as ADDITIVE fields within the same schemaVersion (D-070 — never
// silently rewritten here, and never added via a schemaVersion change):
//   icon, trustBadge, launchContract, supportsProfiles, supportsPrivateMode,
//   privateModeArgument, profileArgumentPattern, urlArgumentPosition,
//   missing/unresolved.
//
// EVERY ADDITIVE MEMBER ADDED BELOW MUST CARRY AN EXPLICIT DEFAULT. This is a
// D-070 requirement, not a style preference. The read path binds with
// RespectRequiredConstructorParameters = true, which makes a positional parameter
// WITHOUT a default mandatory in the JSON — so a later build that adds one without
// a default cannot read any file an earlier build wrote. MEASURED on .NET 10,
// reading an existing revision-0 file that has no such key:
//
//   additive member, NO default   -> JsonException: missing required properties
//                                    including: 'theme'
//   additive member, WITH default -> binds
//
// A file the previous revision wrote is exactly what D-070 promises will stay
// readable, and BodyUnparseable licenses a destructive repair — so the omission
// would not merely fail to read, it would quarantine the user's list and write
// defaults over it. See ConfigFileObserver's BodyOptions for the full rule.
public sealed record BrowserDefinition(
	string BrowserId,
	string DisplayName,
	string Vendor,
	string Channel,
	string ExePath,
	string DetectionSource,
	// EXPLICITLY DEFAULTED, and the default is load-bearing. Its doc comment above
	// says null means "not yet verified", so this member is optional in spirit —
	// but without "= null" the binder made it MANDATORY, and a hand-edited or
	// third-party browsers.json omitting the key became BodyUnparseable, i.e.
	// quarantine-then-defaults: the user's entire browser list replaced because one
	// nullable field was absent. The mirror test cannot catch this, because the
	// writer always emits every constructor parameter.
	DateTimeOffset? LastVerifiedAt = null,
	IReadOnlyList<string>? ProfileIds = null,
	bool HasUnresolvedProfiles = false,
	bool IsCustomBrowser = false,
	bool IsPortableBrowser = false,
	// Additive, defaulted for the same reason as LastVerifiedAt: an older browsers.json without
	// it still binds. Index-aligned with ProfileIds; personal data, never logged.
	IReadOnlyList<string>? ProfileNames = null);
