namespace BroChoos.Core.Storage;

// Pure D-064/D-066/D-070 decision: an observed schema envelope versus the schema
// this build expects (SchemaVersioning.Version, SchemaVersioning.Revision).
// Returns an enum only — no I/O, no mutation, and no invented migration. 0A-5
// recognizes exactly (1, 0); older versions map to OlderVersionMigrationRequired
// purely as a validation note, because 0A ships zero migrations.
public static class SchemaCompatibilityEvaluator
{
	public static SchemaCompatibility Evaluate(int observedSchemaVersion, int observedSchemaRevision)
	{
		// Corrupt envelope guard: D-064/D-066/D-070 never define negative values,
		// so a negative version OR revision is Invalid regardless of the other.
		// Classifying either as writable would be unsafe, hence this precedes the
		// locked rules below (which are unchanged for valid inputs).
		if (observedSchemaVersion < 0 || observedSchemaRevision < 0)
		{
			return SchemaCompatibility.Invalid;
		}

		if (observedSchemaVersion > SchemaVersioning.Version)
		{
			return SchemaCompatibility.NewerVersionReadOnly;
		}

		if (observedSchemaVersion < SchemaVersioning.Version)
		{
			return SchemaCompatibility.OlderVersionMigrationRequired;
		}

		// Same schemaVersion: only the minor/additive revision can differ.
		return observedSchemaRevision > SchemaVersioning.Revision
			? SchemaCompatibility.NewerRevisionReadKnownOnly
			: SchemaCompatibility.CompletelyCompatible;
	}

	// Reads a document's own envelope and evaluates it against the expected schema.
	public static SchemaCompatibility Evaluate(ConfigDocument document)
	{
		return Evaluate(document.SchemaVersion, document.SchemaRevision);
	}
}
