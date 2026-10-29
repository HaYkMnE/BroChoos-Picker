namespace BroChoos.Core.Storage;

// Machine-readable WHY for every ConfigLoadDecision. An enum, not a string, so a
// later logging layer (D-489 keeps every surface in 0B) can emit a redacted event
// without inventing user-facing wording here.
public enum ConfigLoadReason
{
	// D-085: the file is missing, so defaults are created and persisted. Per the
	// owner ruling this applies whenever the file is missing, not only first run.
	FileAbsentDefaultsCreated,

	// The file would not open (lock, denied ACL). D-083: the link must still open,
	// so run on in-memory defaults. No repair, no write — the unreadable bytes may
	// belong to a newer version (D-066), which no repair may risk.
	OpenFailedDefaultsInMemoryOnly,

	// Envelope and body both good, schema exactly what this build expects.
	DocumentLoadedSchemaCompatible,

	// Same schemaVersion, newer schemaRevision: additive fields this build does
	// not know are present. Known fields are read; nothing is written back, because
	// the serializer would drop the unknown members permanently (D-070).
	DocumentLoadedNewerRevisionKnownFieldsOnly,

	// The main file was written by a newer app version (D-066): "never overwrite
	// silently"; "Old app writing newer schema: forbidden."
	MainFileNewerSchemaReadOnly,

	// The main file's envelope parsed but is not a legal envelope (negative
	// version/revision). A corrupt envelope is never classified as writable.
	MainFileEnvelopeInvalidReadOnly,

	// Older schemaVersion. 0A ships ZERO migrations, so no migration may be
	// invented and nothing may be written; the document is not trusted either.
	MainFileOlderSchemaMigrationUnavailableReadOnly,

	// A candidate backup exists, but its OWN envelope is newer-version. Promoting
	// it would convert a recoverable corrupt-file problem into a permanent
	// read-only lockout, so it is not promoted and the outcome is read-only.
	BackupNewerSchemaNotPromotable,

	// Owner decision 3: the quarantine-move failed, so the original is NOT
	// preserved and route (b) is unsatisfied. Fail closed; explicitly do not retry.
	QuarantineFailedReadOnlyNoRetry,

	// Owner decision 2: the envelope is cleared but the body is garbage. This must
	// NEVER become a plain overwrite — preserve the original, then promote the
	// backup.
	BodyUnparseableQuarantineThenRestore,

	// As above, but no promotable backup exists, so defaults follow the quarantine.
	BodyUnparseableQuarantineThenDefaults,

	// The envelope did not parse, so the schema is unknowable. Owner decision 1
	// route (b): preserve the original BEFORE restoring anything.
	EnvelopeUnparseableQuarantineThenRestore,

	// As above, with no promotable backup, so defaults follow the quarantine.
	EnvelopeUnparseableQuarantineThenDefaults,

	// The original is already preserved in backups/corrupted, so the backup is
	// promoted without a further quarantine-move.
	OriginalAlreadyPreservedRestoreFromBackup,

	// The original is already preserved and no promotable backup exists, so
	// defaults are written.
	OriginalAlreadyPreservedCreateDefaults,

	// Defensive backstop: a capability gate refused to mint the token a mutation
	// needs, in a branch the planner's own ordering should already have excluded.
	// Unreachable by construction, kept because the correct response to "my
	// invariant is not holding" is to fail CLOSED to read-only, never to guess and
	// write. No test reaches this; that is the point of it.
	ClearanceRefusedReadOnly
}
