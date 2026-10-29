namespace BroChoos.Core.Storage;

// UNFORGEABLE CAPABILITY TOKEN #3 — licenses promoting ONE specific backup.
//
// D-085 attaches no schema precondition to "restore corrupted JSON from backup",
// while D-066 forbids ever overwriting newer configuration. A backup minted by a
// newer app version is itself newer-schema, and promoting it turns a recoverable
// corrupt-file problem into a permanent read-only lockout. This token is where
// that is reconciled: the backup's OWN envelope must be validated before it can
// be named as a restore source.
//
// The BackupName lives INSIDE the token, so a decision cannot restore a different
// file from the one that was validated — the name and its clearance travel as one
// value and cannot be separated.
//
// NEWER-REVISION BACKUPS ARE REFUSED, NOT DOCUMENTED-AROUND. A backup at a newer
// schemaRevision carries additive fields this build cannot name, and the serializer
// runs with UnmappedMemberHandling.Skip, so a promotion implemented as
// deserialize-then-reserialize would drop them permanently — the exact destruction
// D-066 ("never overwrite silently") and D-070 ("Old app writing newer schema:
// forbidden") prohibit. Core cannot enforce HOW the executor copies bytes, and a
// comment is not an enforcement mechanism, so the requirement is enforced the only
// way this layer can enforce anything: by refusing to mint the token. Only
// CompletelyCompatible is promotable.
//
// Refusing does NOT hard-stop the read path. With no promotion token the planner
// falls through to QuarantineThenCreateDefaults: the corrupt ORIGINAL moves to
// backups/corrupted, defaults are written, and the newer-revision backup file is
// never touched. Zero bytes destroyed, and a later build that honours byte-copy
// semantics can still promote it. The user loses customisations for one session,
// not data. This is also why newer-revision is deliberately NOT added to
// IsBlockedByNewerOrInvalidSchema: that would additionally block the safe defaults
// write while protecting a backup file nothing was going to overwrite.
//
// EXECUTOR PRECONDITION (still live, now narrowed to CompletelyCompatible):
// promotion must be a byte-level copy, not a deserialize-then-reserialize. A
// compatible envelope proves the file is not NEWER; it does not prove the file
// carries no unmapped members. Hand edits, a sibling tool's additions, or fields
// written at a revision this build happens to know all survive a byte copy and are
// silently dropped by a round-trip. That residual loss is a fidelity failure rather
// than a D-066 violation, but it is still a silent overwrite of the user's bytes.
public sealed class BackupPromotionClearance
{
	private BackupPromotionClearance(BackupName name)
	{
		Name = name;
	}

	// The bare file name of the validated backup. Never a path — Core is
	// platform-neutral and composes no paths.
	public BackupName Name { get; }

	// The token deliberately carries NO schema member. Minting is reachable only
	// from the CompletelyCompatible arm below, so such a property could hold exactly
	// one value forever: it would inform no caller, and it would mislead the logging
	// slice into reporting a variance that cannot exist. Once a consumer bound to it,
	// removing it from a public Core type would be a breaking change — so it is
	// absent by construction rather than forbidden by a comment.

	// Returns a token only for a backup that is whole AND whose own envelope
	// clears as EXACTLY compatible. Null for every other case, so "no promotable
	// backup" is the default rather than something a caller has to remember to
	// check.
	public static BackupPromotionClearance? TryClear(BackupObservation? backup)
	{
		// No candidate at all is a perfectly ordinary state, not an error.
		if (backup is null)
		{
			return null;
		}

		// Its own envelope must have been read: an unknowable backup schema cannot
		// be proved not-newer, so it is not promotable.
		if (backup.Envelope != EnvelopeParse.Parsed)
		{
			return null;
		}

		// A backup with a garbage body is no better than the file it would replace.
		if (backup.Body != DocumentBodyParse.Parsed)
		{
			return null;
		}

		// EXACTLY compatible only. NewerRevisionReadKnownOnly is deliberately absent:
		// Core cannot enforce that the executor promotes by copying bytes, and with
		// UnmappedMemberHandling.Skip a round-tripping promotion would permanently
		// drop the additive fields (D-066/D-070). Refusal is the enforcement. The
		// cost is a lost session of customisations; the alternative risks losing the
		// data itself, and the backup file is left intact for a build that can
		// promote it safely.
		return backup.Schema switch
		{
			SchemaCompatibility.CompletelyCompatible => new BackupPromotionClearance(backup.Name),
			SchemaCompatibility.NewerRevisionReadKnownOnly => null,
			SchemaCompatibility.OlderVersionMigrationRequired => null,
			SchemaCompatibility.NewerVersionReadOnly => null,
			SchemaCompatibility.Invalid => null,
			null => null,
			_ => null,
		};
	}

	// True when the candidate is unusable specifically BECAUSE it is newer-version
	// or has an illegal envelope. That case is not "fall through to defaults" — it
	// is a hard stop, because writing defaults would still be a write next to data
	// a newer version owns (D-066).
	public static bool IsBlockedByNewerOrInvalidSchema(BackupObservation? backup)
	{
		return backup?.Schema is SchemaCompatibility.NewerVersionReadOnly or SchemaCompatibility.Invalid;
	}
}
