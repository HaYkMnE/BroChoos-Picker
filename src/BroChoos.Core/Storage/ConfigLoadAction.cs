namespace BroChoos.Core.Storage;

// The instruction the planner produced for one file. This is the WHAT; the reason
// code is the WHY.
public enum ConfigLoadAction
{
	// The bytes the executor parsed are good — use that document. No write.
	UseLoadedDocument,

	// D-083 fail-open: run on in-memory defaults and write NOTHING. Used when the
	// file could not be opened, so its content (possibly newer) is unknown.
	UseInMemoryDefaults,

	// D-085: "create missing default config on first run". A write is licensed.
	CreateDefaultsAndPersist,

	// D-085: "restore corrupted JSON from backup". The original has already been
	// preserved, so no quarantine-move is bundled here.
	RestoreFromBackup,

	// Owner decision 1 route (b): preserve the original in backups/corrupted
	// FIRST, then promote the named backup. Ordering is part of the instruction.
	QuarantineThenRestoreFromBackup,

	// Preserve the original in backups/corrupted FIRST, then write defaults. Used
	// when no promotable backup exists.
	QuarantineThenCreateDefaults,

	// D-066/D-070: no restore, no default template, no quarantine-move, no write
	// of any kind. The app may still read what it can, but it must not mutate.
	HardStopReadOnly,

	// Owner decision 3: the quarantine itself failed. Fail closed to read-only.
	// Explicitly NOT a retry.
	QuarantineFailedReadOnly
}
