namespace BroChoos.Core.Storage;

// The planner's instruction for ONE config file (D-071: atomicity is per file, so
// there is deliberately no trio-wide verdict type — "app.json good, browsers.json
// corrupt" stays two independent decisions).
//
// This is a sealed CLASS with get-only properties, not a record, and that is a
// safety decision rather than a style one: a record's `with` expression would let
// a holder take a legitimately-obtained UseInMemoryDefaults decision and clone it
// as `with { Action = CreateDefaultsAndPersist }`, laundering a read-only outcome
// into a write. Get-only properties on a non-record sealed class close that.
//
// Every factory is internal, so ConfigLoadPlanner is the ONLY producer of a
// decision — the executor in Platform.Windows can consume one but can never mint
// one. And every MUTATING factory additionally demands a capability token it
// cannot fabricate, so even inside Core a restore or a write is unreachable
// without the evidence that licenses it.
public sealed class ConfigLoadDecision
{
	private ConfigLoadDecision(
		ConfigLoadAction action,
		ConfigLoadReason reason,
		ClearanceRoute? clearance,
		BackupName? backupToPromote)
	{
		Action = action;
		Reason = reason;
		Clearance = clearance;
		BackupToPromote = backupToPromote;
	}

	// WHAT the executor must do.
	public ConfigLoadAction Action { get; }

	// WHY, machine-readable. A later logging layer renders this; D-489 keeps every
	// user-facing surface out of 0A-5, so no wording is invented here.
	public ConfigLoadReason Reason { get; }

	// Which of owner decision 1's routes licensed the mutation. Non-null exactly
	// when the action writes or moves bytes.
	public ClearanceRoute? Clearance { get; }

	// The bare NAME of the backup to promote. Non-null exactly for the two restore
	// actions; never a path.
	public BackupName? BackupToPromote { get; }

	// True when the executor is about to write, move or replace bytes. Lets an
	// executor assert the invariant once, centrally.
	public bool MutatesDisk => Action is
		ConfigLoadAction.CreateDefaultsAndPersist or
		ConfigLoadAction.RestoreFromBackup or
		ConfigLoadAction.QuarantineThenRestoreFromBackup or
		ConfigLoadAction.QuarantineThenCreateDefaults;

	// True when the executor must move the original into backups/corrupted BEFORE
	// doing anything else in this instruction.
	public bool QuarantineFirst => Action is
		ConfigLoadAction.QuarantineThenRestoreFromBackup or
		ConfigLoadAction.QuarantineThenCreateDefaults;

	// ---- non-mutating outcomes: no token needed, because nothing is written ----

	internal static ConfigLoadDecision UseLoadedDocument(ConfigLoadReason reason)
	{
		return new(ConfigLoadAction.UseLoadedDocument, reason, null, null);
	}

	// D-083 fail-open: never trap the user's link, but write nothing.
	internal static ConfigLoadDecision UseInMemoryDefaults(ConfigLoadReason reason)
	{
		return new(ConfigLoadAction.UseInMemoryDefaults, reason, null, null);
	}

	// D-066/D-070 hard stop: no restore, no template, no quarantine-move, no write.
	internal static ConfigLoadDecision HardStopReadOnly(ConfigLoadReason reason)
	{
		return new(ConfigLoadAction.HardStopReadOnly, reason, null, null);
	}

	// Owner decision 3: fails CLOSED, and is not a retry signal.
	internal static ConfigLoadDecision QuarantineFailedReadOnly()
	{
		return new(
			ConfigLoadAction.QuarantineFailedReadOnly,
			ConfigLoadReason.QuarantineFailedReadOnlyNoRetry,
			null,
			null);
	}

	// ---- mutating outcomes: each demands the token that licenses it ----

	// Writes a default document. Requires a PersistClearance, which only exists
	// for an ABSENT file or an original already preserved in backups/corrupted.
	internal static ConfigLoadDecision CreateDefaultsAndPersist(
		PersistClearance clearance,
		ConfigLoadReason reason)
	{
		ArgumentNullException.ThrowIfNull(clearance);

		return new ConfigLoadDecision(
			ConfigLoadAction.CreateDefaultsAndPersist,
			reason,
			clearance.Route,
			null);
	}

	// Promotes a backup over the destination. Requires BOTH tokens: the persist
	// clearance proves the destination may be written at all, and the promotion
	// clearance proves the SOURCE's own envelope was validated first.
	internal static ConfigLoadDecision RestoreFromBackup(
		PersistClearance clearance,
		BackupPromotionClearance promotion,
		ConfigLoadReason reason)
	{
		ArgumentNullException.ThrowIfNull(clearance);
		ArgumentNullException.ThrowIfNull(promotion);

		return new ConfigLoadDecision(
			ConfigLoadAction.RestoreFromBackup,
			reason,
			clearance.Route,
			promotion.Name);
	}

	// Preserve the original FIRST, then promote the validated backup. The ordering
	// is baked into the action, so it is not a sequence an executor can re-order.
	internal static ConfigLoadDecision QuarantineThenRestoreFromBackup(
		RepairClearance clearance,
		BackupPromotionClearance promotion,
		ConfigLoadReason reason)
	{
		ArgumentNullException.ThrowIfNull(clearance);
		ArgumentNullException.ThrowIfNull(promotion);

		return new ConfigLoadDecision(
			ConfigLoadAction.QuarantineThenRestoreFromBackup,
			reason,
			ClearanceRoute.OriginalPreservedBeforeAnyWrite,
			promotion.Name);
	}

	// Preserve the original FIRST, then write defaults.
	internal static ConfigLoadDecision QuarantineThenCreateDefaults(
		RepairClearance clearance,
		ConfigLoadReason reason)
	{
		ArgumentNullException.ThrowIfNull(clearance);

		return new ConfigLoadDecision(
			ConfigLoadAction.QuarantineThenCreateDefaults,
			reason,
			ClearanceRoute.OriginalPreservedBeforeAnyWrite,
			null);
	}
}
