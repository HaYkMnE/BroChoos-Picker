namespace BroChoos.Core.Storage;

// W8.1 — settings.json. 0A-5 pinned the schema envelope only; 0B adds the FIRST
// additive user-setting field, per the plan's ownership map ("Add settings IDs/consents,
// never localized strings") and D-064/D-070 (additive-0B fields).
//
// WHY ONLY ControlDepth SO FAR, AND WHY IT IS A SCALAR. W8.1's minimal behavior needs
// exactly one setting to demonstrate "immediate safe-preference save via mutation" —
// the Control-depth preference the shell already shows on its Home dashboard. It is
// persisted as a STABLE ID (the enum name), never a localized string, so a display
// language change can never churn the file (D-070). It is deliberately a scalar and
// NOT a collection: a collection member would oblige an arm in
// ConfigFileObserver.HasUnusableCollectionMember, a null-member/null-element test, and
// the EveryCollectionMemberOnACoreDocument_IsAccountedFor inventory. No such obligation
// is created here, and none is dodged.
//
// ADDITIVE-MEMBER RULE (every config document carries it). A member added later MUST
// carry an explicit default, because the read path's binder makes a no-default
// positional parameter mandatory in the JSON — an older file would then land on
// BodyUnparseable, which licenses quarantine-then-overwrite of intact data. `ControlDepth`
// is therefore an init-only property with an explicit default, NOT a positional
// parameter, so a settings.json written by the envelope-only 0A-5 build still binds
// (the missing property falls back to the default) instead of being treated as corrupt.
public sealed record SettingsDocument : ConfigDocument
{
	public SettingsControlDepth ControlDepth { get; init; } = SettingsControlDepth.Standard;

	// The second additive member, under the same rule as ControlDepth: an init-only property
	// with an explicit default, persisted as the enum NAME. A settings.json written before this
	// member existed binds to Horizontal instead of being treated as corrupt.
	public SettingsPickerLayout PickerLayout { get; init; } = SettingsPickerLayout.Horizontal;
}

// How the picker arranges the browser tiles. A stable, non-localized identifier; the display
// text lives in localization. Horizontal (one row, icon above name) is the default.
public enum SettingsPickerLayout
{
	Horizontal,
	Vertical,
}

// The stable, non-localized identifier for the Control-depth preference. The DISPLAY
// text lives in localization; only this name is persisted. Vocabulary is intentionally
// tiny: it is the smallest set that can demonstrate a safe, reversible preference save.
public enum SettingsControlDepth
{
	Standard,
	Guided,
	Expert,
}
