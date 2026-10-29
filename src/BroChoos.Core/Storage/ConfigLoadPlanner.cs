namespace BroChoos.Core.Storage;

// The PURE, platform-neutral read-path decision layer. No file I/O, no filesystem
// namespace, no platform types, no logger, no clock, no user-facing text (D-489
// keeps every surface in 0B). Maps one ConfigFileObservation to one
// ConfigLoadDecision.
//
// D-071 makes atomicity per file and explicitly refuses an ACID transaction across
// the JSON trio, so this decides for ONE file at a time. "app.json good,
// browsers.json corrupt" is a legitimate state and stays two decisions.
//
// The ordering below is owner decision 1, and the METHOD BODY ORDER is the
// invariant. Read it top to bottom:
//
//   1. absent            -> nothing to protect, defaults may be persisted
//   2. open failed       -> ENDS here: defaults in memory, no repair, no write
//   3. envelope parsed   -> SCHEMA IS EVALUATED HERE, before step 5 exists
//   4. whole document ok -> use it
//   5. repair            -> only reachable once step 3 has cleared or the schema
//                           is unknowable, and quarantine precedes every write
//
// Steps 3 and 5 cannot be swapped by accident, because step 5 cannot construct a
// restore or a write without a token that only a passed schema check can mint.
public static class ConfigLoadPlanner
{
	public static ConfigLoadDecision Plan(ConfigFileObservation observation)
	{
		ArgumentNullException.ThrowIfNull(observation);

		// STEP 1 — D-085: "create missing default config on first run". Per the
		// owner ruling, a missing file always yields created defaults, not only on
		// the very first run. There is no existing configuration to destroy.
		if (observation.Access == ConfigFileAccess.Absent)
		{
			return PlanForAbsentFile(observation);
		}

		// STEP 2 — the file exists but would not open (locked by another process,
		// denied ACL). Owner decision 1: this ENDS the read path. Defaults only, NO
		// repair, NO write of any kind — bytes we could not read may belong to a
		// newer version (D-066), and a repair would risk destroying them. D-083
		// still holds: the URL opens, on in-memory defaults.
		if (observation.Access == ConfigFileAccess.OpenFailed)
		{
			return ConfigLoadDecision.UseInMemoryDefaults(
				ConfigLoadReason.OpenFailedDefaultsInMemoryOnly);
		}

		// STEP 3 — the ENVELOPE only, evaluated BEFORE any repair decision exists.
		if (observation.Envelope == EnvelopeParse.Parsed)
		{
			ConfigLoadDecision? hardStop = TryHardStopOnSchema(observation.Schema);
			if (hardStop is not null)
			{
				return hardStop;
			}

			// STEP 4 — envelope cleared AND the whole document parsed: use it.
			// Owner decision 2 lives in the fact that this is the ONLY branch a
			// clean envelope can take to a non-repair outcome; a cleared envelope
			// over a garbage body falls through to step 5, never to an overwrite.
			if (observation.Body == DocumentBodyParse.Parsed)
			{
				return ConfigLoadDecision.UseLoadedDocument(
					observation.Schema == SchemaCompatibility.NewerRevisionReadKnownOnly
						? ConfigLoadReason.DocumentLoadedNewerRevisionKnownFieldsOnly
						: ConfigLoadReason.DocumentLoadedSchemaCompatible);
			}
		}

		// STEP 5 — repair. Reachable in exactly two ways: a cleared envelope over
		// an unparseable body, or an envelope that did not parse at all (schema
		// unknowable). Every newer-version and illegal-envelope case has already
		// returned above.
		return PlanRepair(observation);
	}

	private static ConfigLoadDecision PlanForAbsentFile(ConfigFileObservation observation)
	{
		PersistClearance? clearance = PersistClearance.TryClear(observation);

		// Absent AND a failed quarantine is a contradictory report, so fail closed
		// rather than write into a state we do not understand.
		return clearance is null
			? ConfigLoadDecision.QuarantineFailedReadOnly()
			: ConfigLoadDecision.CreateDefaultsAndPersist(
				clearance,
				ConfigLoadReason.FileAbsentDefaultsCreated);
	}

	// D-066 / D-070 hard stops. Nothing here writes, moves or restores anything.
	private static ConfigLoadDecision? TryHardStopOnSchema(SchemaCompatibility? schema)
	{
		return schema switch
		{
			// "never overwrite silently"; "Old app writing newer schema: forbidden."
			// There is no second line of defence: the serializer drops unknown
			// members, so one wrong write destroys the newer fields permanently.
			SchemaCompatibility.NewerVersionReadOnly =>
				ConfigLoadDecision.HardStopReadOnly(ConfigLoadReason.MainFileNewerSchemaReadOnly),

			// A negative version/revision is not a legal envelope, and a corrupt
			// envelope must never be classified as writable.
			SchemaCompatibility.Invalid =>
				ConfigLoadDecision.HardStopReadOnly(ConfigLoadReason.MainFileEnvelopeInvalidReadOnly),

			// 0A ships ZERO migrations (D-070), so no migration may be invented and
			// nothing may be written.
			SchemaCompatibility.OlderVersionMigrationRequired =>
				ConfigLoadDecision.HardStopReadOnly(
					ConfigLoadReason.MainFileOlderSchemaMigrationUnavailableReadOnly),

			// Non-hard-stop states must flow into the normal read/repair path.
			SchemaCompatibility.CompletelyCompatible => null,
			SchemaCompatibility.NewerRevisionReadKnownOnly => null,
			null => null,
			_ => null,
		};
	}

	private static ConfigLoadDecision PlanRepair(ConfigFileObservation observation)
	{
		// Owner decision 3: the quarantine target directory is not pre-provisioned
		// and the disk can be full, so quarantine can itself fail. Fail CLOSED to
		// read-only. This is explicitly NOT a retry: the original is still in place
		// and unpreserved, so route (b) is unsatisfied and no write is licensed.
		if (observation.Quarantine == QuarantineAttempt.Failed)
		{
			return ConfigLoadDecision.QuarantineFailedReadOnly();
		}

		// Validate the BACKUP's own envelope before considering promotion. A backup
		// minted by a newer version is itself newer-schema; promoting it converts a
		// recoverable corrupt-file problem into a permanent read-only lockout. That
		// is a hard stop, not a fall-through to defaults — writing defaults would
		// still be a write alongside data a newer version owns.
		if (BackupPromotionClearance.IsBlockedByNewerOrInvalidSchema(observation.Backup))
		{
			return ConfigLoadDecision.HardStopReadOnly(
				ConfigLoadReason.BackupNewerSchemaNotPromotable);
		}

		BackupPromotionClearance? promotion = BackupPromotionClearance.TryClear(observation.Backup);

		// Route (b) already satisfied: the original sits in backups/corrupted, so a
		// write can no longer destroy it and no second quarantine-move is needed.
		if (observation.Quarantine == QuarantineAttempt.Succeeded)
		{
			PersistClearance? persist = PersistClearance.TryClear(observation);
			return persist is null
				? ConfigLoadDecision.HardStopReadOnly(ConfigLoadReason.ClearanceRefusedReadOnly)
				: promotion is null
				? ConfigLoadDecision.CreateDefaultsAndPersist(
					persist,
					ConfigLoadReason.OriginalAlreadyPreservedCreateDefaults)
				: ConfigLoadDecision.RestoreFromBackup(
					persist,
					promotion,
					ConfigLoadReason.OriginalAlreadyPreservedRestoreFromBackup);
		}

		// Nothing preserved yet, so the quarantine must be bundled AHEAD of the
		// write. The token proves the schema check ran and did not forbid this.
		RepairClearance? repair = RepairClearance.TryClear(observation);
		if (repair is null)
		{
			return ConfigLoadDecision.HardStopReadOnly(ConfigLoadReason.ClearanceRefusedReadOnly);
		}

		bool envelopeUnknowable = observation.Envelope == EnvelopeParse.Unparseable;

		return promotion is null
			? ConfigLoadDecision.QuarantineThenCreateDefaults(
				repair,
				envelopeUnknowable
					? ConfigLoadReason.EnvelopeUnparseableQuarantineThenDefaults
					: ConfigLoadReason.BodyUnparseableQuarantineThenDefaults)
			: ConfigLoadDecision.QuarantineThenRestoreFromBackup(
			repair,
			promotion,
			envelopeUnknowable
				? ConfigLoadReason.EnvelopeUnparseableQuarantineThenRestore
				: ConfigLoadReason.BodyUnparseableQuarantineThenRestore);
	}
}
