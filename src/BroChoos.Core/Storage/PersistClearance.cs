namespace BroChoos.Core.Storage;

// UNFORGEABLE CAPABILITY TOKEN #2 — licenses writing bytes to the destination
// WITHOUT a quarantine bundled into the same instruction.
//
// This is the strictest gate in the slice, because it is the only one that admits
// a naked write. It mints a token in exactly two situations, matching owner
// decision 1's "EITHER (a) ... OR (b) ...":
//
//   * the file is Absent — there is no configuration to destroy, newer or not;
//   * the original has ALREADY been preserved in backups/corrupted.
//
// An existing, openable file with a merely compatible envelope does NOT get a
// token. That is deliberate and is the fix for the hole owner decision 2 closes:
// "envelope parses as compatible but the body is garbage" can never reach a plain
// overwrite, because no plain-overwrite licence exists for a file that is present.
// Private constructor for the same reason as RepairClearance: `internal` would be
// forgeable from BroChoos.Core.Tests via InternalsVisibleTo.
public sealed class PersistClearance
{
	private PersistClearance(ClearanceRoute route)
	{
		Route = route;
	}

	public ClearanceRoute Route { get; }

	public static PersistClearance? TryClear(ConfigFileObservation observation)
	{
		ArgumentNullException.ThrowIfNull(observation);

		// Owner decision 3: a failed quarantine means the original is NOT
		// preserved, so route (b) is unsatisfied. Checked first so that a Failed
		// quarantine can never be read as "well, the file is gone anyway".
		if (observation.Quarantine == QuarantineAttempt.Failed)
		{
			return null;
		}

		// Route (b): the original already sits in backups/corrupted, so whatever
		// its schema was, it still exists and writing cannot destroy it.
		if (observation.Quarantine == QuarantineAttempt.Succeeded)
		{
			return new PersistClearance(ClearanceRoute.OriginalPreservedBeforeAnyWrite);
		}

		// D-085: "create missing default config on first run". Nothing to protect.
		// Note this is Absent only — OpenFailed is NOT here, because bytes we
		// could not read may belong to a newer version (D-066).
		//
		// DEFENCE IN DEPTH, AND UNREACHABLE THROUGH Plan TODAY — read this before
		// trusting any coverage number. The `: null` arm refuses a file that is
		// PRESENT and unpreserved, which is the naked-write hole owner decision 2
		// closes. But ConfigLoadPlanner has no call site that reaches this method
		// with such an observation: the only two callers are the Absent branch
		// (PlanForAbsentFile) and the Quarantine == Succeeded branch of PlanRepair,
		// and both are handled above. So the refusal is pinned ONLY by its direct
		// unit test (PersistClearance_PresentUnpreservedFile_MintsNoToken) — a
		// mutation that made this arm mint a token failed exactly one test and left
		// all 312 cross-product matrix cases green. Matrix-green is therefore NOT
		// evidence that this gate is exercised end to end. Keep it anyway: it is the
		// standing guard for a future planner branch that starts calling TryClear
		// from somewhere new, and a green matrix must not be read as permission to
		// delete it.
		return observation.Access == ConfigFileAccess.Absent
			? new PersistClearance(ClearanceRoute.NoExistingFileToProtect)
			: null;
	}
}
