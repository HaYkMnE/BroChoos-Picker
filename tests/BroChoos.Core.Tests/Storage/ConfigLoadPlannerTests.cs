using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Storage;

// Pins the owner-decision-1 ordering invariant on the config READ path:
//
//   read bytes -> open failure ENDS it -> parse envelope only -> evaluate schema
//   BEFORE any repair decision -> only a cleared schema admits content repair ->
//   quarantine BEFORE restore when the envelope is unknowable -> validate the
//   BACKUP's own envelope before promoting it -> only then restore.
//
// D-071 makes atomicity per file, so every case here is a single-file decision.
public sealed class ConfigLoadPlannerTests
{
	private static readonly BackupName Backup = new("app.json.bak");

	// ---- absent: D-085 "create missing default config on first run" ----

	// Per the owner ruling, defaults are created whenever the file is MISSING, not
	// only on the very first run. There is no configuration to destroy.
	[Fact]
	public void Plan_FileAbsent_CreatesDefaultsAndPersists()
	{
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(ConfigFileObservation.Absent());

		_ = decision.Action.Should().Be(ConfigLoadAction.CreateDefaultsAndPersist);
		_ = decision.Reason.Should().Be(ConfigLoadReason.FileAbsentDefaultsCreated);
		_ = decision.Clearance.Should().Be(ClearanceRoute.NoExistingFileToProtect);
		_ = decision.BackupToPromote.Should().BeNull();
		_ = decision.QuarantineFirst.Should().BeFalse();
	}

	// ---- open failure: ends the read path, NO repair, NO write ----

	// Distinct from absent: bytes we could not read may belong to a newer version
	// (D-066), so nothing may be repaired or written. D-083 still holds — the link
	// opens, on in-memory defaults.
	[Fact]
	public void Plan_OpenFailed_UsesInMemoryDefaultsAndWritesNothing()
	{
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(ConfigFileObservation.OpenFailed());

		_ = decision.Action.Should().Be(ConfigLoadAction.UseInMemoryDefaults);
		_ = decision.Reason.Should().Be(ConfigLoadReason.OpenFailedDefaultsInMemoryOnly);
		_ = decision.MutatesDisk.Should().BeFalse();
		_ = decision.QuarantineFirst.Should().BeFalse();
		_ = decision.Clearance.Should().BeNull();
	}

	// The two "no usable document" states must NOT collapse into one outcome:
	// absent licenses a write, unopenable licenses nothing.
	[Fact]
	public void Plan_OpenFailedVersusAbsent_AreDifferentOutcomes()
	{
		ConfigLoadDecision openFailed = ConfigLoadPlanner.Plan(ConfigFileObservation.OpenFailed());
		ConfigLoadDecision absent = ConfigLoadPlanner.Plan(ConfigFileObservation.Absent());

		_ = openFailed.Action.Should().NotBe(absent.Action);
		_ = openFailed.MutatesDisk.Should().BeFalse();
		_ = absent.MutatesDisk.Should().BeTrue();
	}

	// An unopenable file is not repaired even when a perfectly healthy backup is
	// sitting right there — the schema of what is on disk is unknowable.
	[Fact]
	public void Plan_OpenFailedWithHealthyBackup_StillRefusesToRepair()
	{
		ConfigFileObservation observation = ConfigFileObservation.OpenFailed()
			.WithBackup(BackupObservation.Loaded(Backup, SchemaCompatibility.CompletelyCompatible));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.UseInMemoryDefaults);
		_ = decision.MutatesDisk.Should().BeFalse();
		_ = decision.BackupToPromote.Should().BeNull();
	}

	// ---- healthy loads ----

	[Fact]
	public void Plan_EnvelopeAndBodyGoodAtExpectedSchema_UsesLoadedDocument()
	{
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(
			ConfigFileObservation.Loaded(SchemaCompatibility.CompletelyCompatible));

		_ = decision.Action.Should().Be(ConfigLoadAction.UseLoadedDocument);
		_ = decision.Reason.Should().Be(ConfigLoadReason.DocumentLoadedSchemaCompatible);
		_ = decision.MutatesDisk.Should().BeFalse();
	}

	// D-070: additive fields this build does not know are present. Read the known
	// ones; write nothing back, because the serializer would drop the rest.
	[Fact]
	public void Plan_NewerRevisionSameVersion_ReadsKnownFieldsAndWritesNothing()
	{
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(
			ConfigFileObservation.Loaded(SchemaCompatibility.NewerRevisionReadKnownOnly));

		_ = decision.Action.Should().Be(ConfigLoadAction.UseLoadedDocument);
		_ = decision.Reason.Should().Be(ConfigLoadReason.DocumentLoadedNewerRevisionKnownFieldsOnly);
		_ = decision.MutatesDisk.Should().BeFalse();
	}

	// A browser-catalog reconciliation is a separate write command layered over a
	// successful read. It is licensed only by the exact observation/decision pair
	// that proves the whole current-schema document was accepted by the planner.
	[Fact]
	public void ReconcileClearance_ExactlyCompatibleLoadedDecision_MintsToken()
	{
		ConfigFileObservation observation = ConfigFileObservation.Loaded(
			SchemaCompatibility.CompletelyCompatible);
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = ReconcileClearance.TryClear(observation, decision).Should().NotBeNull();
	}

	[Theory]
	[InlineData(SchemaCompatibility.NewerRevisionReadKnownOnly)]
	[InlineData(SchemaCompatibility.OlderVersionMigrationRequired)]
	[InlineData(SchemaCompatibility.NewerVersionReadOnly)]
	[InlineData(SchemaCompatibility.Invalid)]
	public void ReconcileClearance_AnythingButExactlyCompatibleLoadedDocument_MintsNoToken(
		SchemaCompatibility schema)
	{
		ConfigFileObservation observation = ConfigFileObservation.Loaded(schema);
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = ReconcileClearance.TryClear(observation, decision).Should().BeNull();
	}

	[Fact]
	public void ReconcileClearance_UnparseableBodyOrMissingFile_MintsNoToken()
	{
		ConfigFileObservation bodyUnparseable = ConfigFileObservation.BodyUnparseable(
			SchemaCompatibility.CompletelyCompatible);
		ConfigFileObservation absent = ConfigFileObservation.Absent();

		_ = ReconcileClearance.TryClear(bodyUnparseable, ConfigLoadPlanner.Plan(bodyUnparseable))
			.Should().BeNull();
		_ = ReconcileClearance.TryClear(absent, ConfigLoadPlanner.Plan(absent))
			.Should().BeNull();
	}

	[Fact]
	public void ReconcileClearance_MismatchedObservationAndDecision_MintsNoToken()
	{
		ConfigFileObservation observation = ConfigFileObservation.Loaded(
			SchemaCompatibility.CompletelyCompatible);
		ConfigLoadDecision newerRevisionDecision = ConfigLoadPlanner.Plan(
			ConfigFileObservation.Loaded(SchemaCompatibility.NewerRevisionReadKnownOnly));

		_ = ReconcileClearance.TryClear(observation, newerRevisionDecision).Should().BeNull();
	}

	// ---- D-066 / D-070 hard stops on the MAIN file ----

	// "never overwrite silently"; "Old app writing newer schema: forbidden."
	// No restore, no default template, no quarantine-move, no write of any kind.
	[Fact]
	public void Plan_MainFileNewerVersion_HardStopsReadOnlyWithNoWriteOfAnyKind()
	{
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(
			ConfigFileObservation.Loaded(SchemaCompatibility.NewerVersionReadOnly));

		_ = decision.Action.Should().Be(ConfigLoadAction.HardStopReadOnly);
		_ = decision.Reason.Should().Be(ConfigLoadReason.MainFileNewerSchemaReadOnly);
		_ = decision.MutatesDisk.Should().BeFalse();
		_ = decision.QuarantineFirst.Should().BeFalse();
		_ = decision.BackupToPromote.Should().BeNull();
		_ = decision.Clearance.Should().BeNull();
	}

	// A corrupt (negative) envelope must never be classified as writable.
	[Fact]
	public void Plan_MainFileInvalidEnvelope_HardStopsReadOnly()
	{
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(
			ConfigFileObservation.Loaded(SchemaCompatibility.Invalid));

		_ = decision.Action.Should().Be(ConfigLoadAction.HardStopReadOnly);
		_ = decision.Reason.Should().Be(ConfigLoadReason.MainFileEnvelopeInvalidReadOnly);
		_ = decision.MutatesDisk.Should().BeFalse();
	}

	// 0A ships ZERO migrations (D-070), so no migration may be invented and nothing
	// may be written — even though the document itself parsed.
	[Fact]
	public void Plan_MainFileOlderVersion_HardStopsReadOnlyBecauseNoMigrationExists()
	{
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(
			ConfigFileObservation.Loaded(SchemaCompatibility.OlderVersionMigrationRequired));

		_ = decision.Action.Should().Be(ConfigLoadAction.HardStopReadOnly);
		_ = decision.Reason.Should()
			.Be(ConfigLoadReason.MainFileOlderSchemaMigrationUnavailableReadOnly);
		_ = decision.MutatesDisk.Should().BeFalse();
	}

	// ---- owner decision 2: a cleared envelope over a garbage body ----

	// THE HOLE THIS CLOSES: the envelope parses as compatible, so a naive
	// implementation "knows the schema is fine" and overwrites in place — losing the
	// original bytes forever. A write is licensed only on a WHOLE-DOCUMENT parse, so
	// this must quarantine first.
	[Theory]
	[InlineData(SchemaCompatibility.CompletelyCompatible)]
	[InlineData(SchemaCompatibility.NewerRevisionReadKnownOnly)]
	public void Plan_EnvelopeClearedButBodyUnparseable_QuarantinesFirstAndNeverOverwrites(
		SchemaCompatibility schema)
	{
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(
			ConfigFileObservation.BodyUnparseable(schema));

		_ = decision.Action.Should().Be(ConfigLoadAction.QuarantineThenCreateDefaults);
		_ = decision.Reason.Should().Be(ConfigLoadReason.BodyUnparseableQuarantineThenDefaults);
		_ = decision.QuarantineFirst.Should().BeTrue();
		_ = decision.Clearance.Should().Be(ClearanceRoute.OriginalPreservedBeforeAnyWrite);

		// The specific thing that must never happen: a bare overwrite of a present file.
		_ = decision.Action.Should().NotBe(ConfigLoadAction.CreateDefaultsAndPersist);
		_ = decision.Action.Should().NotBe(ConfigLoadAction.RestoreFromBackup);
	}

	[Fact]
	public void Plan_EnvelopeClearedBodyUnparseableWithGoodBackup_QuarantinesThenRestores()
	{
		ConfigFileObservation observation = ConfigFileObservation.BodyUnparseable(
			SchemaCompatibility.CompletelyCompatible,
			BackupObservation.Loaded(Backup, SchemaCompatibility.CompletelyCompatible));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.QuarantineThenRestoreFromBackup);
		_ = decision.Reason.Should().Be(ConfigLoadReason.BodyUnparseableQuarantineThenRestore);
		_ = decision.QuarantineFirst.Should().BeTrue();
		_ = decision.BackupToPromote.Should().Be(Backup);
	}

	// ---- envelope unknowable: quarantine BEFORE restore ----

	// The envelope did not parse, so no schema verdict exists and none may be
	// invented. The only legal move is to preserve the original first.
	[Fact]
	public void Plan_EnvelopeUnparseableWithGoodBackup_QuarantinesBeforeRestoring()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable(
			BackupObservation.Loaded(Backup, SchemaCompatibility.CompletelyCompatible));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.QuarantineThenRestoreFromBackup);
		_ = decision.Reason.Should().Be(ConfigLoadReason.EnvelopeUnparseableQuarantineThenRestore);
		_ = decision.QuarantineFirst.Should().BeTrue();
		_ = decision.BackupToPromote.Should().Be(Backup);

		// A plain restore would touch the destination while the original is still
		// unpreserved. That is the ordering bug this asserts against.
		_ = decision.Action.Should().NotBe(ConfigLoadAction.RestoreFromBackup);
	}

	[Fact]
	public void Plan_EnvelopeUnparseableWithNoBackup_QuarantinesThenCreatesDefaults()
	{
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(
			ConfigFileObservation.EnvelopeUnparseable());

		_ = decision.Action.Should().Be(ConfigLoadAction.QuarantineThenCreateDefaults);
		_ = decision.Reason.Should().Be(ConfigLoadReason.EnvelopeUnparseableQuarantineThenDefaults);
		_ = decision.QuarantineFirst.Should().BeTrue();
	}

	// An unparseable envelope carries NO schema verdict, and the decision must not
	// need one. The observation factory takes no SchemaCompatibility at all, so the
	// planner is structurally unable to consult a verdict it cannot have.
	[Fact]
	public void Plan_EnvelopeUnparseable_DecidesWithoutAnySchemaVerdict()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable();

		_ = observation.Schema.Should().BeNull();
		_ = observation.Envelope.Should().Be(EnvelopeParse.Unparseable);
		_ = ConfigLoadPlanner.Plan(observation).Action
			.Should().Be(ConfigLoadAction.QuarantineThenCreateDefaults);
	}

	// ---- the backup's OWN envelope is validated before promotion ----

	// D-085 attaches no schema precondition to "restore corrupted JSON from backup",
	// but a backup minted by a newer version IS newer-schema. Promoting it converts a
	// recoverable corrupt-file problem into a permanent read-only lockout, so it must
	// not be promoted, and the outcome is read-only — not restore, and not defaults.
	[Fact]
	public void Plan_BackupEnvelopeIsNewerVersion_IsNotPromotedAndOutcomeIsReadOnly()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable(
			BackupObservation.Loaded(Backup, SchemaCompatibility.NewerVersionReadOnly));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.HardStopReadOnly);
		_ = decision.Reason.Should().Be(ConfigLoadReason.BackupNewerSchemaNotPromotable);
		_ = decision.MutatesDisk.Should().BeFalse();
		_ = decision.QuarantineFirst.Should().BeFalse();
		_ = decision.BackupToPromote.Should().BeNull();
	}

	[Fact]
	public void Plan_BackupEnvelopeIsInvalid_IsNotPromotedAndOutcomeIsReadOnly()
	{
		ConfigFileObservation observation = ConfigFileObservation.BodyUnparseable(
			SchemaCompatibility.CompletelyCompatible,
			BackupObservation.Loaded(Backup, SchemaCompatibility.Invalid));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.HardStopReadOnly);
		_ = decision.Reason.Should().Be(ConfigLoadReason.BackupNewerSchemaNotPromotable);
		_ = decision.MutatesDisk.Should().BeFalse();
	}

	// A backup whose own envelope is unreadable cannot be proved not-newer, so it is
	// not promotable — but it also does not block quarantining-then-defaulting,
	// because a quarantine MOVE destroys nothing.
	[Fact]
	public void Plan_BackupEnvelopeUnparseable_FallsBackToQuarantineThenDefaults()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable(
			BackupObservation.EnvelopeUnparseable(Backup));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.QuarantineThenCreateDefaults);
		_ = decision.BackupToPromote.Should().BeNull();
	}

	// A backup that is old-but-legal is no better than the corrupt file: 0A has no
	// migrations, so it is not promotable either.
	[Fact]
	public void Plan_BackupIsOlderVersion_IsNotPromotable()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable(
			BackupObservation.Loaded(Backup, SchemaCompatibility.OlderVersionMigrationRequired));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.QuarantineThenCreateDefaults);
		_ = decision.BackupToPromote.Should().BeNull();
	}

	// A backup with a readable envelope but a garbage body is no better than the
	// file it would replace.
	[Fact]
	public void Plan_BackupBodyUnparseable_IsNotPromotable()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable(
			BackupObservation.BodyUnparseable(Backup, SchemaCompatibility.CompletelyCompatible));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.QuarantineThenCreateDefaults);
		_ = decision.BackupToPromote.Should().BeNull();
	}

	// OWNER RULING: a backup carrying additive fields this build cannot name is NOT
	// promotable. Safe promotion would require the executor to copy bytes, and Core
	// cannot enforce that; the serializer runs with UnmappedMemberHandling.Skip, so a
	// promotion that deserialised and re-serialised would drop those fields
	// permanently (D-066 "never overwrite silently", D-070). Refusal is the
	// enforcement, because a comment is not one.
	//
	// Refusing is not a hard stop and destroys nothing. The corrupt ORIGINAL is
	// quarantined and defaults are written, while the newer-revision backup FILE is
	// left byte-for-byte intact for a later build that can promote it safely. The
	// user loses customisations for this session, not data.
	[Fact]
	public void Plan_BackupHasNewerRevision_IsRefusedAndTheBackupFileIsLeftUntouched()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable(
			BackupObservation.Loaded(Backup, SchemaCompatibility.NewerRevisionReadKnownOnly));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		// The fall-through outcome: quarantine the original, then write defaults.
		_ = decision.Action.Should().Be(ConfigLoadAction.QuarantineThenCreateDefaults);
		_ = decision.Reason.Should().Be(ConfigLoadReason.EnvelopeUnparseableQuarantineThenDefaults);
		_ = decision.BackupToPromote.Should().BeNull();

		// THE LOAD-BEARING ASSERTION: the decision carries no instruction that would
		// touch the backup at all. Naming it is the only channel by which a decision
		// can reach the backup, and the two restore actions are the only actions that
		// consume that name — so with neither, the backup file cannot be read,
		// promoted, moved or overwritten by this instruction.
		_ = decision.Action.Should().NotBe(ConfigLoadAction.RestoreFromBackup);
		_ = decision.Action.Should().NotBe(ConfigLoadAction.QuarantineThenRestoreFromBackup);

		// And this is NOT the strictly-worse alternative of blocking everything: the
		// safe defaults write still happens behind a quarantine.
		_ = decision.Action.Should().NotBe(ConfigLoadAction.HardStopReadOnly);
		_ = decision.QuarantineFirst.Should().BeTrue();
		_ = decision.Clearance.Should().Be(ClearanceRoute.OriginalPreservedBeforeAnyWrite);
	}

	// The gate itself, directly: only an EXACTLY compatible backup mints a promotion
	// token. Pins the ruling at the token rather than only through Plan.
	[Theory]
	[InlineData(SchemaCompatibility.NewerRevisionReadKnownOnly)]
	[InlineData(SchemaCompatibility.OlderVersionMigrationRequired)]
	[InlineData(SchemaCompatibility.NewerVersionReadOnly)]
	[InlineData(SchemaCompatibility.Invalid)]
	public void BackupPromotionClearance_AnythingButExactlyCompatible_MintsNoToken(
		SchemaCompatibility schema)
	{
		_ = BackupPromotionClearance.TryClear(BackupObservation.Loaded(Backup, schema))
			.Should().BeNull();
	}

	[Fact]
	public void BackupPromotionClearance_ExactlyCompatible_MintsToken()
	{
		BackupPromotionClearance? promotion = BackupPromotionClearance.TryClear(
			BackupObservation.Loaded(Backup, SchemaCompatibility.CompletelyCompatible));

		// No schema assertion: the token carries no schema member, precisely because
		// minting is reachable only from the CompletelyCompatible arm, so any such
		// assertion could never fail. What CAN fail is carried below — that a token
		// was minted at all, and that it names the backup that was validated rather
		// than some other file.
		_ = promotion.Should().NotBeNull();
		_ = promotion!.Name.Should().Be(Backup);
	}

	// Refusing a newer-revision backup must NOT be implemented by widening the
	// newer-or-invalid hard stop. That would additionally block the safe defaults
	// write while protecting a backup file nothing was going to overwrite — strictly
	// worse, and this pins it.
	[Fact]
	public void BackupPromotionClearance_NewerRevision_IsNotTreatedAsAHardStopBlocker()
	{
		_ = BackupPromotionClearance.IsBlockedByNewerOrInvalidSchema(
			BackupObservation.Loaded(Backup, SchemaCompatibility.NewerRevisionReadKnownOnly))
			.Should().BeFalse();
	}

	// ---- route (b) already satisfied: the original is preserved ----

	[Fact]
	public void Plan_OriginalAlreadyPreservedWithGoodBackup_RestoresWithoutAnotherQuarantine()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable(
			BackupObservation.Loaded(Backup, SchemaCompatibility.CompletelyCompatible),
			QuarantineAttempt.Succeeded);

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.RestoreFromBackup);
		_ = decision.Reason.Should().Be(ConfigLoadReason.OriginalAlreadyPreservedRestoreFromBackup);
		_ = decision.QuarantineFirst.Should().BeFalse();
		_ = decision.Clearance.Should().Be(ClearanceRoute.OriginalPreservedBeforeAnyWrite);
		_ = decision.BackupToPromote.Should().Be(Backup);
	}

	[Fact]
	public void Plan_OriginalAlreadyPreservedWithNoBackup_CreatesDefaults()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable(
			backup: null,
			quarantine: QuarantineAttempt.Succeeded);

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.CreateDefaultsAndPersist);
		_ = decision.Reason.Should().Be(ConfigLoadReason.OriginalAlreadyPreservedCreateDefaults);
		_ = decision.Clearance.Should().Be(ClearanceRoute.OriginalPreservedBeforeAnyWrite);
	}

	// ---- owner decision 3: quarantine can itself fail ----

	// The quarantine target is not pre-provisioned and the disk can be full. A
	// failed quarantine means the original is NOT preserved, so route (b) is
	// unsatisfied and NOTHING may be written. Fail closed, and do not loop.
	[Fact]
	public void Plan_QuarantineFailed_FailsClosedToReadOnlyAndIsNotARetry()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable(
			backup: null,
			quarantine: QuarantineAttempt.Failed);

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.QuarantineFailedReadOnly);
		_ = decision.Reason.Should().Be(ConfigLoadReason.QuarantineFailedReadOnlyNoRetry);
		_ = decision.MutatesDisk.Should().BeFalse();
		_ = decision.QuarantineFirst.Should().BeFalse();
		_ = decision.Clearance.Should().BeNull();

		// Not a retry: the planner must not hand back another quarantine attempt.
		_ = decision.Action.Should().NotBe(ConfigLoadAction.QuarantineThenCreateDefaults);
		_ = decision.Action.Should().NotBe(ConfigLoadAction.QuarantineThenRestoreFromBackup);
	}

	// Even with a flawless backup available, a failed quarantine writes nothing:
	// promoting the backup would overwrite an original that is still unpreserved.
	[Fact]
	public void Plan_QuarantineFailedWithGoodBackup_StillWritesNothing()
	{
		ConfigFileObservation observation = ConfigFileObservation.BodyUnparseable(
			SchemaCompatibility.CompletelyCompatible,
			BackupObservation.Loaded(Backup, SchemaCompatibility.CompletelyCompatible),
			QuarantineAttempt.Failed);

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.QuarantineFailedReadOnly);
		_ = decision.MutatesDisk.Should().BeFalse();
		_ = decision.BackupToPromote.Should().BeNull();
	}

	// ---- THE ORDERING TESTS: schema check STRICTLY precedes any repair ----

	// If someone moved the repair branch ahead of the schema evaluation, this file —
	// newer schemaVersion, garbage body, and a perfectly healthy backup begging to be
	// promoted — would be quarantined and overwritten, permanently destroying
	// configuration a newer app version owns. D-066 has no second line of defence.
	// The correct order returns a hard stop and touches nothing.
	[Fact]
	public void Plan_NewerSchemaWithCorruptBodyAndHealthyBackup_HardStopsBecauseSchemaIsCheckedBeforeRepair()
	{
		ConfigFileObservation observation = ConfigFileObservation.BodyUnparseable(
			SchemaCompatibility.NewerVersionReadOnly,
			BackupObservation.Loaded(Backup, SchemaCompatibility.CompletelyCompatible));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.HardStopReadOnly);
		_ = decision.Reason.Should().Be(ConfigLoadReason.MainFileNewerSchemaReadOnly);
		_ = decision.MutatesDisk.Should().BeFalse();
		_ = decision.QuarantineFirst.Should().BeFalse();
		_ = decision.BackupToPromote.Should().BeNull();
	}

	// The same trap for an illegal envelope: repair-before-schema would quarantine
	// and overwrite it.
	[Fact]
	public void Plan_InvalidSchemaWithCorruptBodyAndHealthyBackup_HardStopsBecauseSchemaIsCheckedBeforeRepair()
	{
		ConfigFileObservation observation = ConfigFileObservation.BodyUnparseable(
			SchemaCompatibility.Invalid,
			BackupObservation.Loaded(Backup, SchemaCompatibility.CompletelyCompatible));

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

		_ = decision.Action.Should().Be(ConfigLoadAction.HardStopReadOnly);
		_ = decision.Reason.Should().Be(ConfigLoadReason.MainFileEnvelopeInvalidReadOnly);
		_ = decision.MutatesDisk.Should().BeFalse();
	}

	// The type-level half of the same guarantee: no schema-forbidden observation can
	// even MINT the token a repair requires, so a reordered planner still could not
	// construct the instruction.
	[Theory]
	[InlineData(SchemaCompatibility.NewerVersionReadOnly)]
	[InlineData(SchemaCompatibility.Invalid)]
	[InlineData(SchemaCompatibility.OlderVersionMigrationRequired)]
	public void RepairClearance_SchemaForbidsRepair_MintsNoToken(SchemaCompatibility schema)
	{
		_ = RepairClearance.TryClear(ConfigFileObservation.BodyUnparseable(schema)).Should().BeNull();
		_ = RepairClearance.TryClear(ConfigFileObservation.Loaded(schema)).Should().BeNull();
	}

	[Theory]
	[InlineData(SchemaCompatibility.CompletelyCompatible)]
	[InlineData(SchemaCompatibility.NewerRevisionReadKnownOnly)]
	public void RepairClearance_ClearedSchema_MintsTokenViaEnvelopeProvenNotNewer(SchemaCompatibility schema)
	{
		RepairClearance? clearance = RepairClearance.TryClear(
			ConfigFileObservation.BodyUnparseable(schema));

		_ = clearance.Should().NotBeNull();
		_ = clearance!.Basis.Should().Be(RepairClearanceBasis.EnvelopeProvenNotNewer);
	}

	// An unparseable envelope clears on the preserve-first basis only.
	[Fact]
	public void RepairClearance_EnvelopeUnparseable_MintsTokenViaPreserveFirst()
	{
		RepairClearance? clearance = RepairClearance.TryClear(
			ConfigFileObservation.EnvelopeUnparseable());

		_ = clearance.Should().NotBeNull();
		_ = clearance!.Basis.Should().Be(RepairClearanceBasis.EnvelopeUnknowableSoPreserveFirst);
	}

	// The naked-write gate: a file that is PRESENT never licenses a bare overwrite,
	// however healthy its envelope looks. This is what makes owner decision 2
	// structural rather than a comment.
	[Theory]
	[InlineData(SchemaCompatibility.CompletelyCompatible)]
	[InlineData(SchemaCompatibility.NewerRevisionReadKnownOnly)]
	[InlineData(SchemaCompatibility.NewerVersionReadOnly)]
	[InlineData(SchemaCompatibility.Invalid)]
	public void PersistClearance_PresentUnpreservedFile_MintsNoToken(SchemaCompatibility schema)
	{
		_ = PersistClearance.TryClear(ConfigFileObservation.Loaded(schema)).Should().BeNull();
		_ = PersistClearance.TryClear(ConfigFileObservation.BodyUnparseable(schema)).Should().BeNull();
	}

	[Fact]
	public void PersistClearance_OpenFailed_MintsNoToken()
	{
		_ = PersistClearance.TryClear(ConfigFileObservation.OpenFailed()).Should().BeNull();
	}

	[Fact]
	public void PersistClearance_QuarantineFailed_MintsNoToken()
	{
		ConfigFileObservation observation = ConfigFileObservation.EnvelopeUnparseable(
			backup: null,
			quarantine: QuarantineAttempt.Failed);

		_ = PersistClearance.TryClear(observation).Should().BeNull();
	}

	[Fact]
	public void PersistClearance_AbsentOrPreserved_MintsToken()
	{
		_ = PersistClearance.TryClear(ConfigFileObservation.Absent())!.Route
			.Should().Be(ClearanceRoute.NoExistingFileToProtect);

		_ = PersistClearance.TryClear(
			ConfigFileObservation.EnvelopeUnparseable(null, QuarantineAttempt.Succeeded))!.Route
			.Should().Be(ClearanceRoute.OriginalPreservedBeforeAnyWrite);
	}

	// ---- cross-cutting invariants over the WHOLE input space ----

	// One theory over the full cross-product, asserting every ordering invariant at
	// once. Stated over all inputs rather than per scenario, so a future branch added
	// to the planner cannot quietly violate the invariant in a combination nobody
	// wrote a named test for.
	[Theory]
	[MemberData(nameof(AllObservations))]
	public void Plan_AnyObservation_HoldsEveryOrderingInvariant(ConfigFileObservation observation)
	{
		// D-083 fail-open: a config read failure must never trap the user's link, so
		// the planner always answers instead of throwing.
		Func<ConfigLoadDecision> act = () => ConfigLoadPlanner.Plan(observation);
		_ = act.Should().NotThrow();

		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);
		_ = decision.Should().NotBeNull();

		// Nothing writes without a clearance route, and nothing carries a clearance
		// route without writing.
		_ = decision.Clearance.HasValue.Should().Be(
			decision.MutatesDisk,
			"a clearance route is carried exactly when the decision mutates disk");

		// A backup name is carried exactly by the two restore actions.
		bool restores = decision.Action
			is ConfigLoadAction.RestoreFromBackup
			or ConfigLoadAction.QuarantineThenRestoreFromBackup;
		_ = (decision.BackupToPromote is not null).Should().Be(
			restores,
			"a backup is named exactly when the decision restores one");

		// THE ORDERING INVARIANT: a file that is PRESENT is never written unless the
		// original is preserved first — either by a quarantine bundled ahead of the
		// write, or by one that already succeeded.
		if (observation.Access != ConfigFileAccess.Absent && decision.MutatesDisk)
		{
			bool preserved = decision.QuarantineFirst
				|| observation.Quarantine == QuarantineAttempt.Succeeded;

			_ = preserved.Should().BeTrue(
				"a present file was about to be written with its original unpreserved");
		}

		// A newer-version or illegal envelope on the main file never yields a write.
		if (observation.Schema is SchemaCompatibility.NewerVersionReadOnly
			or SchemaCompatibility.Invalid)
		{
			_ = decision.MutatesDisk.Should().BeFalse(
				"D-066 forbids writing over newer or illegal configuration");
			_ = decision.QuarantineFirst.Should().BeFalse();
		}

		// A newer-version or illegal BACKUP is never promoted.
		if (observation.Backup?.Schema is SchemaCompatibility.NewerVersionReadOnly
			or SchemaCompatibility.Invalid)
		{
			_ = decision.BackupToPromote.Should().BeNull(
				"promoting a newer-schema backup would be a permanent read-only lockout");
		}

		// OWNER RULING, stated over the whole space: a promotion is licensed ONLY for
		// an exactly-compatible backup. Anything else — newer revision, older version,
		// garbage body, unreadable envelope — is never named, because Core cannot
		// enforce that the executor promotes by copying bytes and a round-trip through
		// the Skip-configured serializer would drop members it cannot map.
		if (observation.Backup?.Schema != SchemaCompatibility.CompletelyCompatible)
		{
			_ = decision.BackupToPromote.Should().BeNull(
				"only an exactly-compatible backup may be promoted");
		}

		// An unopenable file is never repaired or written (distinct from absent).
		if (observation.Access == ConfigFileAccess.OpenFailed)
		{
			_ = decision.MutatesDisk.Should().BeFalse();
			_ = decision.Action.Should().Be(ConfigLoadAction.UseInMemoryDefaults);
		}

		// The defensive backstop must stay unreachable across the entire space; if it
		// ever appears, an internal invariant has slipped.
		_ = decision.Reason.Should().NotBe(ConfigLoadReason.ClearanceRefusedReadOnly);
	}

	[Fact]
	public void Plan_NullObservation_IsRejected()
	{
		Action act = () => _ = ConfigLoadPlanner.Plan(null!);

		_ = act.Should().Throw<ArgumentNullException>();
	}

	// The full cross-product of access state x envelope/body state x schema verdict x
	// backup state x quarantine state, built through the named factories so no
	// impossible observation can enter the matrix.
	//
	// WHAT THE 312 IS, AND WHAT IT IS NOT. It is real invariant coverage: each case
	// asserts several independent ordering invariants at once, and under an
	// ordering mutation (repair moved ahead of the schema check) roughly 30 of the 32
	// resulting failures came from this theory rather than from the named tests. That
	// is its job — catching a violation in a combination nobody wrote a named test
	// for.
	//
	// It is NOT a count of distinct behaviour, and 312 must never be cited as
	// behavioural coverage. The cross-product multiplies dimensions that most cases
	// do not read: Absent x 8 backup shapes x 3 quarantine states is 24 cases
	// exercising exactly two code paths (defaults-and-persist, or the fail-closed
	// contradictory-report branch when the quarantine Failed), and backup shape is
	// irrelevant to every OpenFailed case because step 2 returns before the backup is
	// ever consulted. Distinct behavioural equivalence classes here number a few
	// dozen, not 312.
	public static TheoryData<ConfigFileObservation> AllObservations()
	{
		SchemaCompatibility[] schemas =
		[
			SchemaCompatibility.CompletelyCompatible,
			SchemaCompatibility.NewerRevisionReadKnownOnly,
			SchemaCompatibility.OlderVersionMigrationRequired,
			SchemaCompatibility.NewerVersionReadOnly,
			SchemaCompatibility.Invalid,
		];

		BackupObservation?[] backups =
		[
			null,
			BackupObservation.Loaded(Backup, SchemaCompatibility.CompletelyCompatible),
			BackupObservation.Loaded(Backup, SchemaCompatibility.NewerRevisionReadKnownOnly),
			BackupObservation.Loaded(Backup, SchemaCompatibility.OlderVersionMigrationRequired),
			BackupObservation.Loaded(Backup, SchemaCompatibility.NewerVersionReadOnly),
			BackupObservation.Loaded(Backup, SchemaCompatibility.Invalid),
			BackupObservation.BodyUnparseable(Backup, SchemaCompatibility.CompletelyCompatible),
			BackupObservation.EnvelopeUnparseable(Backup),
		];

		QuarantineAttempt[] quarantines =
		[
			QuarantineAttempt.NotAttempted,
			QuarantineAttempt.Succeeded,
			QuarantineAttempt.Failed,
		];

		TheoryData<ConfigFileObservation> data = [];

		foreach (BackupObservation? backup in backups)
		{
			foreach (QuarantineAttempt quarantine in quarantines)
			{
				data.Add(ConfigFileObservation.Absent()
					.WithBackup(backup).WithQuarantine(quarantine));
				data.Add(ConfigFileObservation.OpenFailed()
					.WithBackup(backup).WithQuarantine(quarantine));
				data.Add(ConfigFileObservation.EnvelopeUnparseable(backup, quarantine));

				foreach (SchemaCompatibility schema in schemas)
				{
					data.Add(ConfigFileObservation.Loaded(schema, backup, quarantine));
					data.Add(ConfigFileObservation.BodyUnparseable(schema, backup, quarantine));
				}
			}
		}

		return data;
	}
}
