namespace BroChoos.Core.Abstractions;

// W6.2: the caller-supplied facts for a manual/portable browser admission, and the typed
// answer. Core owns both shapes so the App does not have to reference the Windows platform
// assembly to state what a user picked; the DECISION belongs to the platform validator,
// which is the only place the environment/identity seams live.
//
// Kind is explicit rather than inferred from a flag pair on purpose: "custom" and
// "portable" are two distinct admission paths in the plan (custom = user-selected
// executable identity; portable = user-selected self-contained install), and a caller that
// has to set two independent booleans can set neither, one, or both, every combination of
// which then needs its own interpretation. One value cannot be three-quarters set.
public enum ManualBrowserKind
{
	Custom,
	Portable,
}

public sealed record ManualBrowserAdmissionRequest(
	ManualBrowserKind Kind,
	string ExePath,
	string? DisplayName = null);

// The refusal vocabulary. Codes are stable, machine-greppable tokens in the same
// hyphen-lowercase shape as every other diagnostic string in this codebase.
//
// EVERY REFUSAL CARRIES A DISTINCT CODE, and that is the point rather than polish: a
// surface that shows "Invalid browser" for a network path, a directory, a missing file and
// a non-executable tells the user nothing about what to change, and it makes the security
// gate indistinguishable from a typo in a diagnostic. The user-facing sentence is localized
// from the code by the App; the code itself never varies.
public static class ManualBrowserAdmissionCodes
{
	public const string Admitted = "manual-browser.admitted";
	public const string EmptyPath = "manual-browser.empty-path";
	public const string PathRejected = "manual-browser.path-rejected";
	public const string NotAFile = "manual-browser.not-a-file";
	public const string NotAnExecutable = "manual-browser.not-an-executable";
	public const string MissingFile = "manual-browser.missing-file";
	public const string SelfReference = "manual-browser.self-reference";
}

// The admission seam. It is here, in Core, rather than App-side, because BOTH ends of the
// dependency need to name it without referencing each other: the coordinator in
// BroChoos.App is the consumer, and BroChoos.Platform.Windows.Browsers.ManualBrowserAdmitter
// is the implementation. App must not reference Platform.Windows's implementation types and
// Platform.Windows must not reference App (the architecture suite asserts both directions),
// so the contract goes in the one assembly both already depend on.
//
// It carries the ANSWER only — admitted / refused, with a reason code — so a test or a fake
// states the outcome it wants without re-deriving path or identity rules. A second
// IMPLEMENTATION would be a second story about what "valid browser" means, which is exactly
// what W6.2 must not grow.
public interface IManualBrowserAdmissionGate
{
	ManualBrowserAdmissionResult Admit(ManualBrowserAdmissionRequest request);
}

// The typed answer. Admitted means exactly one thing: the platform validator produced a
// candidate that may be shown as a choice. It is NOT a launchability guarantee — W6.3 owns
// launch outcomes — and a refused admission never yields a partial candidate, so a caller
// cannot accidentally publish one.
public sealed record ManualBrowserAdmissionResult(
	bool Admitted,
	string Code,
	BrowserCandidate? Candidate = null)
{
	public static ManualBrowserAdmissionResult Refused(string code)
	{
		return new ManualBrowserAdmissionResult(false, code);
	}

	public static ManualBrowserAdmissionResult Accepted(BrowserCandidate candidate)
	{
		return new ManualBrowserAdmissionResult(true, ManualBrowserAdmissionCodes.Admitted, candidate);
	}
}
