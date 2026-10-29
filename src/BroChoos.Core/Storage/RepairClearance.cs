namespace BroChoos.Core.Storage;

// UNFORGEABLE CAPABILITY TOKEN #1 — licenses a quarantine-FIRST content repair.
//
// The constructor is private, and the ONLY code path that reaches it is
// TryClear, which performs the schema check itself. So the token is not "a flag
// somebody remembered to set"; its existence IS the evidence that the schema was
// evaluated and did not forbid touching the file. Note `private`, not `internal`:
// BroChoos.Core has InternalsVisibleTo("BroChoos.Core.Tests"), so an internal
// constructor would be forgeable from the test assembly and the guarantee would
// be theatre. A plain sealed class, not a record, because a record's `with`
// expression would let a holder clone a valid token with a different Basis.
public sealed class RepairClearance
{
	private RepairClearance(RepairClearanceBasis basis)
	{
		Basis = basis;
	}

	// On what evidence the schema check cleared, never re-assignable. This is
	// TOKEN-INTERNAL and is a different question from the route a decision reports:
	// the decision built from this token always reports
	// ClearanceRoute.OriginalPreservedBeforeAnyWrite, because in a quarantine-first
	// instruction it is the preservation — not the envelope — that licenses the
	// write. The two questions used to share one enum, which left the enum with a
	// member no decision could ever carry; they are now separate types so neither
	// has an unobservable value.
	public RepairClearanceBasis Basis { get; }

	// Returns a token only when the schema check does not forbid repair, and null
	// otherwise. Owner decision 1: "evaluate schema BEFORE any repair decision".
	public static RepairClearance? TryClear(ConfigFileObservation observation)
	{
		ArgumentNullException.ThrowIfNull(observation);

		// No bytes means nothing to repair. Absent files are handled by
		// PersistClearance; an unopenable file ends the read path entirely.
		if (observation.Access != ConfigFileAccess.Opened)
		{
			return null;
		}

		// Envelope unknowable: the schema verdict cannot exist, so the only legal
		// move is route (b) — preserve the original before anything is written.
		// Quarantining a file MOVES it into backups/corrupted, which destroys
		// nothing, so it is safe even for content that turns out to be newer.
		if (observation.Envelope == EnvelopeParse.Unparseable)
		{
			return new RepairClearance(RepairClearanceBasis.EnvelopeUnknowableSoPreserveFirst);
		}

		// Envelope parsed, so a verdict must exist. Only a CLEARED schema admits
		// content repair. NewerVersionReadOnly and Invalid are hard stops (D-066);
		// OlderVersionMigrationRequired is refused too, because 0A ships zero
		// migrations and no migration may be invented here (D-070).
		return observation.Schema switch
		{
			SchemaCompatibility.CompletelyCompatible or SchemaCompatibility.NewerRevisionReadKnownOnly =>
				new RepairClearance(RepairClearanceBasis.EnvelopeProvenNotNewer),
			SchemaCompatibility.OlderVersionMigrationRequired => null,
			SchemaCompatibility.NewerVersionReadOnly => null,
			SchemaCompatibility.Invalid => null,
			null => null,
			_ => null,
		};
	}
}
