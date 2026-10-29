namespace BroChoos.Core.Storage;

// D-064 / D-066 / D-070 — the outcomes of comparing an observed
// document envelope against the (1, 0) this build reads and writes.
public enum SchemaCompatibility
{
	// Same schemaVersion, and a schemaRevision this build already knows
	// (<= expected). Fully safe to read and write.
	CompletelyCompatible,

	// Same schemaVersion, newer schemaRevision: additive fields this build does
	// not know may be present (D-064). Reading known fields is safe; unsafe
	// writes must be avoided.
	NewerRevisionReadKnownOnly,

	// Older schemaVersion: a step-by-step migration is required before writing
	// (D-064). 0A has ZERO migrations, so downstream this carries a validation
	// note only — no migration is invented here (D-070).
	OlderVersionMigrationRequired,

	// Newer schemaVersion: written by a newer, unsupported app version. Never
	// overwrite; open read-only or safe mode only (D-066).
	NewerVersionReadOnly,

	// Defensive: a negative schemaVersion or schemaRevision (0 is valid; nothing
	// below 0 is). D-064/D-066/D-070 never define negative values, so a
	// corrupt envelope must NEVER be classified as writable — it is Invalid
	// (validation-only, no write, no migration).
	Invalid
}
