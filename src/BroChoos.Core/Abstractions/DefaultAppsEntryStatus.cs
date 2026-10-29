namespace BroChoos.Core.Abstractions;

// One line of the D-268/D-496 three-step Default Apps model for a single target:
//   Registered — BroChoos capability keys exist in HKCU (capability declared);
//   Selected   — the CURRENT UserChoice ProgId, a READ-ONLY snapshot of what the
//                user picked in Windows (null when nothing is recorded);
//   Handled    — whether BroChoos actually processed activations. 0A-4 has no
//                activation history sink, so the note reports honestly "no
//                activation recorded yet" instead of inventing one.
public sealed record DefaultAppsEntryStatus(
	DefaultAppsTarget Target,
	string Scheme,
	string ExpectedProgId,
	bool Registered,
	string? SelectedProgId,
	string HandledNote)
{
	// True when the recorded UserChoice ProgId is the one this milestone declares.
	// Deliberately null (not false) when no choice is recorded: "no selection"
	// is a different state from "another app is selected", and collapsing them
	// would make an unconfigured machine look like a lost one.
	public bool? SelectedIsBroChoos =>
		SelectedProgId is null
			? null
			: string.Equals(SelectedProgId, ExpectedProgId, StringComparison.OrdinalIgnoreCase);
}
