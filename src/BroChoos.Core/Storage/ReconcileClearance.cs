namespace BroChoos.Core.Storage;

// Licenses rewriting an existing document only after the whole document was parsed,
// its schema was exactly compatible, and the planner accepted that same state as an
// ordinary loaded document. This is separate from PersistClearance: an absent file
// has nothing to reconcile, while a present file must never receive the naked-write
// clearance used for first-run creation and preserved-original repair.
public sealed class ReconcileClearance
{
	private ReconcileClearance()
	{
	}

	public static ReconcileClearance? TryClear(
		ConfigFileObservation observation,
		ConfigLoadDecision decision)
	{
		ArgumentNullException.ThrowIfNull(observation);
		ArgumentNullException.ThrowIfNull(decision);

		return observation.Access == ConfigFileAccess.Opened
			&& observation.Envelope == EnvelopeParse.Parsed
			&& observation.Body == DocumentBodyParse.Parsed
			&& observation.Schema == SchemaCompatibility.CompletelyCompatible
			&& decision.Action == ConfigLoadAction.UseLoadedDocument
			&& decision.Reason == ConfigLoadReason.DocumentLoadedSchemaCompatible
			&& !decision.MutatesDisk
			&& decision.Clearance is null
			? new ReconcileClearance()
			: null;
	}
}
