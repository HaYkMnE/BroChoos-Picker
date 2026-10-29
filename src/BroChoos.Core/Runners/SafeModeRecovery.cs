namespace BroChoos.Core.Runners;

// W8.2: the recovery contract Safe Mode exposes. This file owns NO I/O and no
// rendering: it is the vocabulary shared by whichever platform actions exist and
// by the runner that reports them. The reason it exists at all is that the
// previous runner hardcoded six action ids and printed `status=available` for
// every one of them without any of them doing anything — an interface that
// advertised recoveries that did not exist. The types below make the honest
// states explicit so the runner cannot fall back to that shape.

// The six recovery routes the plan names. Kept as a closed vocabulary rather
// than free strings so a report cannot silently grow an action nobody performs.
public static class SafeModeActionIds
{
	public const string DiagnosticsExport = "diagnostics-export";

	public const string RestoreBackup = "restore-backup";

	public const string ResetDefaults = "reset-defaults";

	public const string OpenDataFolder = "open-data-folder";

	public const string RegistrationRepair = "registration-repair";

	public const string SyntheticLinkProbe = "synthetic-link-probe";

	// Repairs the picker's own documents, history.json and rules.json, through the same
	// repair executor restore-backup uses for app.json: an unreadable file is preserved in
	// backups\corrupted, then the newest compatible backup is promoted, or defaults are
	// written when none is usable. A missing file is left missing; a newer-schema file is
	// left untouched. The picker's config-recovery notice runs this action.
	public const string RepairSettings = "repair-settings";

	// The published order of the recovery contract, and the ONLY list a caller
	// may use to enumerate "what does Safe Mode offer". A per-action selector is
	// validated against it so an unknown id is rejected rather than reported.
	public static readonly string[] All =
	[
		DiagnosticsExport,
		RestoreBackup,
		ResetDefaults,
		OpenDataFolder,
		RegistrationRepair,
		SyntheticLinkProbe,
		RepairSettings,
	];

	public static bool IsKnown(string actionId)
	{
		return Array.IndexOf(All, actionId) >= 0;
	}
}

// What actually happened to one action. The distinction between Ready and
// Completed matters: Ready is the describe-time contract entry ("offered; it will
// run when selected"), Completed is the observed outcome of a real attempt, and
// Unavailable names a route this build does not own. Nothing may report Completed
// without a corresponding state change — so DESCRIBE, which performs nothing, can
// never answer Completed either.
public enum SafeModeActionStatus
{
	// OFFERED, NOT PERFORMED. Produced by a read-only Describe for a route this build
	// CAN perform once one action is explicitly selected. It is a statement about the
	// CONTRACT, not about work done: a bare --safe-mode run reports every offered
	// route as Ready and touches nothing. This member exists because the honest
	// describe-time answer is neither Completed (nothing ran) nor Unavailable (this
	// build can do it) — and reusing Completed for it would advertise recoveries that
	// did not run, which is the very defect the W8.2 slice removes.
	Ready,

	// The action ran and its observable effect was produced.
	Completed,

	// The action ran and failed. The reason is user-readable and leaks no path
	// or value, exactly as the rest of the recovery surface already does.
	Failed,

	// This build does not own the platform seam the action needs. The reason
	// states that plainly; a no-op is NEVER reported as available.
	Unavailable,

	// The action was offered by the contract but was not requested in this run.
	// Only produced when a per-action selector asked for a different action.
	NotRequested,
}

// One action's real outcome. Reason is user-facing text and MUST NOT carry a
// filesystem path, a URL or any other value-shaped data (D-075 / D-097).
public sealed record SafeModeActionOutcome(
	string ActionId,
	SafeModeActionStatus Status,
	string Reason);

// The whole run. Outcomes preserve the published action order so a reader sees a
// stable contract regardless of what was requested.
public sealed record SafeModeRecoveryReport(IReadOnlyList<SafeModeActionOutcome> Outcomes)
{
	// A run succeeds when nothing that was actually attempted failed. Unavailable
	// is an honest statement about this build, not a failure of the run.
	public bool Succeeded => Outcomes.All(outcome => outcome.Status != SafeModeActionStatus.Failed);

	public bool AnyFailed => !Succeeded;
}

// HOW THE RUN WAS ENTERED. Only the caller knows this (Bootstrap took one branch
// or the other), so it travels on the request and the runner prints it as given;
// the runner never infers it from configuration state. The route used to be a
// hardcoded `routing=config-corruption-recovery` line, which told a user who had
// simply typed --safe-mode on a healthy configuration that it was corrupt.
public enum SafeModeEntryRoute
{
	// The user asked for Safe Mode on the command line (--safe-mode).
	UserRequested,

	// Bootstrap fell back to Safe Mode because the URL path reported config
	// corruption (ExitCodes.ConfigCorruption).
	ConfigCorruptionFallback,
}

// The raw request a Safe Mode run carries. ActionId null means "offer the full
// contract"; a non-null value selects exactly one action, which is what makes a
// recovery invocable individually (W8.2 minimal behaviour). Route defaults to
// UserRequested: only the corruption fallback may claim otherwise.
public sealed record SafeModeRequest(string? ActionId = null, SafeModeEntryRoute Route = SafeModeEntryRoute.UserRequested)
{
	public static readonly SafeModeRequest All = new();

	public static readonly SafeModeRequest ConfigCorruptionFallback = new(Route: SafeModeEntryRoute.ConfigCorruptionFallback);

	// A selector the command line carried but could not supply a usable id for. The empty
	// id is never a known action, so SafeModeRunner rejects it (SafeModeFailed) without
	// throwing and without running any recovery.
	public static readonly SafeModeRequest InvalidSelector = new(string.Empty);

	public static SafeModeRequest Only(string actionId)
	{
		return new SafeModeRequest(actionId);
	}
}

// The acting side. One member: perform the requested action and report what really
// happened. Perform is only ever called for one EXPLICITLY SELECTED action, which is
// what keeps a bare --safe-mode non-mutating. An implementation that cannot perform
// an action MUST return Unavailable with a truthful reason — never Completed and
// never Available.
//
// Describing a route is deliberately NOT here: describing must not provision, and
// this object only exists after a storage root was resolved. The describe half lives
// on ISafeModeRecoveryActionsProvider, which the composition can answer without
// touching the disk.
public interface ISafeModeRecoveryActions
{
	SafeModeActionOutcome Perform(string actionId);
}

// A Safe Mode run with no acting side available. It is deliberately honest: every
// action reports Unavailable, so a composition that forgets to wire the platform
// implementation fails the interface-truthfulness test rather than advertising
// recoveries it cannot deliver.
public sealed class UnavailableSafeModeRecoveryActions(string reason) : ISafeModeRecoveryActions
{
	private readonly string _reason = reason;

	public SafeModeActionOutcome Perform(string actionId)
	{
		return new SafeModeActionOutcome(actionId, SafeModeActionStatus.Unavailable, _reason);
	}
}

// HOW CORE OBTAINS THE ACTING SIDE WITHOUT REFERENCING THE PLATFORM ASSEMBLY.
// The platform recovery actions need a resolved storage root, and resolving one
// IS provisioning — so it cannot happen while the container is composed, before
// --portable-data is parsed. Core therefore asks for the actions at RUN time,
// handing over the parsed value, and the platform factory composes the resolver
// inside that call.
//
// A null value is not an error: it means the resolver's D-072 priority applies
// (a .portable marker, then installed mode under %LocalAppData%).
public interface ISafeModeRecoveryActionsProvider
{
	// READ-ONLY and MUST NOT PROVISION. A bare --safe-mode reports the contract
	// through this member, so composing a storage root here would create directories
	// on the one command line that must touch nothing. The answer describes the
	// capability the composition CAN offer; it performs no work.
	SafeModeActionOutcome Describe(string actionId);

	// LICENSED TO PERFORM WORK, including provisioning a storage root. Called only
	// when one action was explicitly selected. Throw is a legal answer — an unusable
	// storage root is exactly the state Safe Mode exists to report — and the runner
	// converts it into a truthful failure.
	ISafeModeRecoveryActions Create(string? portableDataArgument);
}

// The provider a composition uses when no platform recovery is wired. It never
// throws and never claims a recovery: every action is Unavailable, with the reason
// naming the missing integration.
public sealed class UnavailableSafeModeRecoveryActionsProvider(string reason) : ISafeModeRecoveryActionsProvider
{
	private readonly string _reason = reason;

	public SafeModeActionOutcome Describe(string actionId)
	{
		return new SafeModeActionOutcome(actionId, SafeModeActionStatus.Unavailable, _reason);
	}

	public ISafeModeRecoveryActions Create(string? portableDataArgument)
	{
		return new UnavailableSafeModeRecoveryActions(_reason);
	}
}
