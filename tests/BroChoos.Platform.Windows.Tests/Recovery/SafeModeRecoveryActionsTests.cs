using BroChoos.Core.Abstractions;
using BroChoos.Core.Runners;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Recovery;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Recovery;

// W8.2 behavioural suite for the REAL recovery actions. Every assertion here is
// about an OBSERVABLE EFFECT — a file that exists, bytes that changed, a service
// that was invoked — because that is exactly what the placeholder did not have.
//
// The routes that are genuinely unavailable without a composed seam are asserted
// to report UNAVAILABLE with a reason, never "available"/"completed": that is the
// interface-truthfulness property the whole slice exists for.
public sealed class SafeModeRecoveryActionsTests : IDisposable
{
	private readonly List<string> _temporaryDirectories = [];

	public void Dispose()
	{
		foreach (string directory in _temporaryDirectories)
		{
			try
			{
				Directory.Delete(directory, recursive: true);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	// ===================== describe: F1 truthfulness =====================

	// F1 (W8.2 interface truthfulness), asserted against the REAL factory rather than a
	// fake provider: Describe performs NOTHING, so a route this build CAN offer must be
	// reported Ready ("offered; will run when selected") — never Completed, which the
	// enum reserves for an observed effect. This is the mutation anchor: reverting
	// SafeModeRecoveryActionsFactory.Ready to Completed fails exactly this test.
	[Fact]
	public void Describe_OfferedRoutes_ReportReadyNeverCompleted()
	{
		SafeModeRecoveryActionsFactory factory = BuildFactory(
			registration: new RecordingRegistration(),
			detector: new RecordingDetector(),
			dataFolderOpener: new RecordingDataFolderOpener());

		foreach (string actionId in SafeModeActionIds.All)
		{
			SafeModeActionOutcome outcome = factory.Describe(actionId);

			_ = outcome.ActionId.Should().Be(actionId);
			_ = outcome.Status.Should().Be(
				SafeModeActionStatus.Ready,
				"describe performs nothing, so every offerable route is Ready, not Completed ({0})",
				actionId);
			_ = outcome.Status.Should().NotBe(SafeModeActionStatus.Completed);
			_ = outcome.Reason.Should().NotBeNullOrWhiteSpace();
		}
	}

	// The other describe outcome stays honest too: a route whose seam is NOT wired is
	// Unavailable, never Ready and never Completed.
	[Fact]
	public void Describe_WhenTheSeamIsMissing_ReportsUnavailable()
	{
		SafeModeRecoveryActionsFactory factory = BuildFactory(
			registration: null,
			detector: null,
			dataFolderOpener: null);

		_ = factory.Describe(SafeModeActionIds.OpenDataFolder).Status.Should().Be(SafeModeActionStatus.Unavailable);
		_ = factory.Describe(SafeModeActionIds.RegistrationRepair).Status.Should().Be(SafeModeActionStatus.Unavailable);
		_ = factory.Describe(SafeModeActionIds.SyntheticLinkProbe).Status.Should().Be(SafeModeActionStatus.Unavailable);
	}

	private static SafeModeRecoveryActionsFactory BuildFactory(
		ICapabilityRegistrationService? registration,
		IBrowserDetector? detector,
		IDataFolderOpener? dataFolderOpener)
	{
		return new SafeModeRecoveryActionsFactory(
			NullLogger<SafeModeRecoveryActions>.Instance,
			new FixedEnvironmentProbe(Path.GetTempPath()),
			new SystemStorageFileSystem(),
			new ConfigFileObserver(new SystemStorageReadFileSystem()),
			TimeProvider.System,
			new SystemStorageRepairFileSystem(),
			registration,
			detector,
			dataFolderOpener);
	}

	// ===================== 1. diagnostics-export =====================

	[Fact]
	public void DiagnosticsExport_WritesARedactedArtifactIntoTheDataFolder()
	{
		StorageRootResolver root = CreateRoot();
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.DiagnosticsExport);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);

		string artifact = Path.Combine(root.Root, "diagnostics", SafeModeRecoveryActions.DiagnosticsFileName);
		_ = File.Exists(artifact).Should().BeTrue("the export must produce a real file, not a printed line");

		string content = File.ReadAllText(artifact);
		_ = content.Should().Contain("\"artifact\": \"safe-mode-diagnostics\"");
		// Redaction: the artifact must not embed the machine-local root path.
		_ = content.Should().NotContain(root.Root, "diagnostics must omit machine-local paths by default (D-075/D-097)");
		_ = content.Should().NotContain(root.Backups);
	}

	// ===================== 2. reset-defaults =====================

	[Fact]
	public void ResetDefaults_WritesARealDocumentAndKeepsAPreResetBackup()
	{
		StorageRootResolver root = CreateRoot();
		string destination = Path.Combine(root.Root, "app.json");
		File.WriteAllText(destination, "this is not json at all");
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.ResetDefaults);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);

		string written = File.ReadAllText(destination);
		_ = written.Should().Contain("\"schemaVersion\": 1");
		_ = written.Should().Contain("\"schemaRevision\": 0");

		// The old bytes are recoverable: the route now runs through ConfigRepairExecutor,
		// which QUARANTINES the unreadable original (a MOVE into backups/corrupted) before
		// writing defaults. That preserves the original bytes as strongly as the atomic
		// writer's .bak minting did, and the reason text below reports exactly this.
		_ = Directory.EnumerateFiles(Path.Combine(root.Backups, "corrupted"), "app.json.*.corrupted")
			.Should().NotBeEmpty("reset must preserve the unreadable previous file before replacing it");

		// THE OTHER HALF OF THAT FACT, asserted as an effect rather than assumed: the
		// preservation was a QUARANTINE MOVE, so the defaults write found no destination
		// and took AtomicJsonFileStore's File.Move branch, which mints NO .bak. That is
		// why the reason may say "preserved" and must not say "backed up": on this path
		// there is no backup file anywhere.
		_ = Directory.EnumerateFiles(root.Backups, "*.bak").Should().BeEmpty(
			"the unreadable original was quarantined into backups/corrupted, so the defaults write minted no .bak");

		// PINNED BY EQUALITY, NOT BY A NEEDLE (the same close-out as the NEW-1
		// registration facts below). The property is "the reason is the one sentence this
		// arm composes and NOTHING else": any appended clause — a false backup claim in
		// ANY wording — changes the string and fails here. Contain("preserved first")
		// left exactly that hole: appending ", and the pre-write state was saved to the
		// backup store" kept every ResetDefaults fact green on this path, where the
		// assertion above shows no backup exists.
		_ = outcome.Reason.Should().Be(
			"safe defaults were written through the atomic writer; the previous file was preserved first",
			"the quarantined original was preserved before the write, so the reason is that sentence VERBATIM");
	}

	// F2 (W8.2 reason-text accuracy): when app.json is ABSENT there is no previous
	// file to preserve, and the atomic store's File.Move branch mints NO backup. The
	// reason must say so instead of asserting a backup that never happened.
	//
	// BOTH HALVES ARE ASSERTED AS EFFECTS HERE, not taken from the reason's own claim:
	// the destination holds the defaults, and the backups root holds NO .bak, which is
	// what makes "no previous configuration existed" the whole truth and any appended
	// backup clause false. The reason itself is then pinned BY EQUALITY — the needle
	// version left a false appended claim green (see the previous fact).
	[Fact]
	public void ResetDefaults_WhenNoPreviousConfigurationExists_ReportsNoPreviousConfiguration()
	{
		StorageRootResolver root = CreateRoot();
		string destination = Path.Combine(root.Root, "app.json");
		_ = File.Exists(destination).Should().BeFalse();
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.ResetDefaults);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = File.ReadAllText(destination).Should().Contain("\"schemaVersion\": 1");

		// The effect the wording must not contradict: defaults were written, and the
		// File.Move branch minted no backup of any kind.
		_ = Directory.EnumerateFiles(root.Backups, "*.bak").Should().BeEmpty(
			"no previous configuration existed, so the defaults write minted no backup");

		_ = outcome.Reason.Should().Be(
			"safe defaults were written through the atomic writer; no previous configuration existed",
			"no previous file existed and no backup was minted, so the reason is that sentence VERBATIM — an appended backup claim of ANY wording fails here");
	}

	// reset-defaults must NOT destroy a present, READABLE configuration: the executor's
	// planner returns UseLoadedDocument for it, which mutates nothing. Reporting a
	// performed reset there would be the same untruthfulness F1/F2 are about.
	//
	// THE REASON IS PINNED BY EQUALITY TOO, for the same reason as the two facts above:
	// this arm composes one deterministic sentence, and a needle left an appended claim
	// green. The sentence already says "defaults did not need to be written", so a false
	// "…; the previous file was backed up first" riding behind it would be the exact
	// untruth this slice exists to remove — and equality is what catches it.
	[Fact]
	public void ResetDefaults_WhenTheConfigurationIsReadable_LeavesItAloneAndSaysSo()
	{
		StorageRootResolver root = CreateRoot();
		string destination = Path.Combine(root.Root, "app.json");
		File.WriteAllText(destination, "{\"schemaVersion\":1,\"schemaRevision\":0,\"keep\":\"me\"}");
		byte[] before = File.ReadAllBytes(destination);
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.ResetDefaults);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = File.ReadAllBytes(destination).Should().Equal(before, "a readable configuration must not be overwritten by a reset");
		_ = outcome.Reason.Should().Be(
			"the configuration was already readable; defaults did not need to be written",
			"nothing was written, so the reason is that sentence VERBATIM — any appended backup claim fails here");
	}

	// A corrupt app.json with a compatible backup: the repair promotes the backup and writes NO
	// defaults. Reset-defaults must say so instead of reporting "already readable".
	[Fact]
	public void ResetDefaults_WhenACompatibleBackupIsPromoted_ReportsTheRestoreNotDefaults()
	{
		StorageRootResolver root = CreateRoot();
		string destination = Path.Combine(root.Root, "app.json");
		File.WriteAllText(destination, "corrupt on disk");
		File.WriteAllText(
			Path.Combine(root.Backups, "app.json.20260101000000000.0123456789abcdef0123456789abcdef.bak"),
			"{\"schemaVersion\":1,\"schemaRevision\":0,\"handEditedByTheUser\":\"keep me\"}");
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.ResetDefaults);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = File.ReadAllText(destination).Should().Contain("handEditedByTheUser");
		_ = outcome.Reason.Should().Be("a compatible configuration backup was restored instead of writing defaults");
	}

	// ===================== 3. restore-backup =====================

	[Fact]
	public void RestoreBackup_PromotesTheNewestCompatibleBackupThroughTheRepairExecutor()
	{
		StorageRootResolver root = CreateRoot();
		string destination = Path.Combine(root.Root, "app.json");
		File.WriteAllText(destination, "corrupt on disk");
		// A real, compatible backup minted under the writer's own name shape.
		string backupName = "app.json.20260101000000000.0123456789abcdef0123456789abcdef.bak";
		File.WriteAllText(
			Path.Combine(root.Backups, backupName),
			"{\"schemaVersion\":1,\"schemaRevision\":0,\"handEditedByTheUser\":\"keep me\"}");

		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RestoreBackup);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		// Restore is a BYTE promotion: the unmapped member must survive it.
		_ = File.ReadAllText(destination).Should().Contain("handEditedByTheUser");

		// PINNED BY EQUALITY, like every other RestoreBackup arm. This arm composes ONE
		// deterministic sentence, so an appended clause — a false "the previous file was
		// backed up first" riding behind a real promotion, or a false claim that defaults
		// were written — changes the string and fails here. The needle version of this
		// fact asserted nothing about the reason at all, which left every such clause green.
		_ = outcome.Reason.Should().Be(
			"the newest compatible configuration backup was restored",
			"a byte promotion replaced the destination with the backup, so the reason is that sentence VERBATIM");
	}

	// NEW-2 (W8.2 reason-text accuracy, independently confirmed). The triple
	// (BackupPromoted == false, DestinationUsable == true) is ALSO produced when the
	// destination was CORRUPT and there was NO promotable backup: the executor
	// quarantines the corrupt original, then writes FRESH DEFAULTS. The old code mapped
	// that to Completed + "the configuration was already readable; no backup needed to
	// be restored", which is false in all three parts — nothing was readable, defaults
	// WERE written, and the original was quarantined. `restore-backup` promised a
	// restore and performed none, so the honest status is Failed and the reason must
	// name what actually happened.
	//
	// THE REASON IS PINNED BY EQUALITY, NOT BY TWO NotContain NEEDLES. The needles
	// pinned only two wordings of the old lie; an audit proved that appending a FALSE
	// backup claim to this arm, or replacing the whole sentence with unrelated text
	// that merely contains "defaults" and avoids both needles, kept this fact green.
	// Equality is the assertion that fails for EVERY such edit, because any edit
	// changes the string. This arm's reason is deterministic: the quarantine succeeded,
	// so OriginalPreserved is true and the first variant is the only one composable here.
	[Fact]
	public void RestoreBackup_WhenThereIsNoPromotableBackup_WritesDefaultsAndNeverClaimsAReadableConfigOrARestore()
	{
		StorageRootResolver root = CreateRoot();
		string destination = Path.Combine(root.Root, "app.json");
		File.WriteAllText(destination, "this is not json at all");
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RestoreBackup);

		_ = outcome.Status.Should().Be(
			SafeModeActionStatus.Failed,
			"restore-backup restored nothing: no backup was promotable, so the action's promise was not kept");

		// The effect the reason must now admit to: fresh defaults at the destination,
		// and the corrupt original preserved in backups/corrupted (a quarantine, which is
		// NOT a restore and NOT a backup promotion).
		_ = File.ReadAllText(destination).Should().Contain("\"schemaVersion\": 1");
		_ = Directory.EnumerateFiles(Path.Combine(root.Backups, "corrupted"), "app.json.*.corrupted")
			.Should().NotBeEmpty("the corrupt original is quarantined before the defaults write");

		_ = outcome.Reason.Should().Be(
			"no usable configuration backup could be restored; the unreadable configuration was preserved and safe defaults were written instead",
			"the corrupt original was quarantined (so it was preserved) and fresh defaults were written, so the reason is that sentence VERBATIM — any appended clause fails here");
	}

	// The OTHER variant of the DefaultsPersisted arm, and it had NO test: the audit
	// proved that mutating this one literal ("safe defaults were written instead",
	// SafeModeRecoveryActions.cs:200) stayed GREEN across the whole Platform suite.
	//
	// WHY IT IS A SEPARATE ARM. The arm's reason is a ternary on OriginalPreserved, and
	// the fact above pins only the TRUE side of it: a CORRUPT destination whose
	// quarantine SUCCEEDS sets preserved = true in Perform (ConfigRepairExecutor.cs:574),
	// so the "was preserved" sentence is the only one composable there. This test drives
	// the false side, where nothing was quarantined because there was nothing to
	// preserve.
	//
	// THE OUTCOME IS PRODUCED BY THE ABSENT-ORIGINAL PATH, which is the ordinary route
	// and needs NO double. An absent app.json observes ConfigFileAccess.Absent, so the
	// planner takes PlanForAbsentFile (ConfigLoadPlanner.cs:78-89) and PersistClearance
	// mints NoExistingFileToProtect — NOT a quarantine route. The decision is therefore
	// CreateDefaultsAndPersist with QuarantineFirst == false (ConfigLoadDecision.cs:59-66),
	// so Perform never enters the quarantine branch and `preserved` stays false through
	// PersistDefaults (ConfigRepairExecutor.cs:751-758). That is exactly
	// (DefaultsPersisted == true, OriginalPreserved == false, BackupPromoted == false,
	// DestinationUsable == true), and it is REACHABLE at this seam without touching
	// production.
	//
	// PINNED BY EQUALITY, like every other RestoreBackup arm: this variant composes one
	// deterministic sentence, so any appended clause — a false backup claim of any
	// wording — changes the string and fails here. A needle would leave such a clause
	// green, which is precisely the hole the audit found on the other variant.
	[Fact]
	public void RestoreBackup_WhenNoPreviousConfigurationExisted_WritesDefaultsAndSaysNothingWasPreserved()
	{
		StorageRootResolver root = CreateRoot();
		string destination = Path.Combine(root.Root, "app.json");
		_ = File.Exists(destination).Should().BeFalse();
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RestoreBackup);

		_ = outcome.Status.Should().Be(
			SafeModeActionStatus.Failed,
			"restore-backup restored nothing: no backup existed and no previous configuration was preserved, so the action's promise was not kept");

		// The effects the false side of the ternary rests on: fresh defaults at the
		// destination, and — because there was NO original — nothing quarantined and no
		// backup minted, so "was preserved" would be a claim this outcome cannot support.
		//
		// The quarantine directory is NOT merely empty, it was never CREATED: it is not
		// pre-provisioned (owner decision 3), and Quarantine is the only writer of it, so
		// its absence is the strongest available evidence that no Move happened. This is
		// asserted as a directory, not enumerated — an absent directory would throw.
		_ = File.ReadAllText(destination).Should().Contain("\"schemaVersion\": 1");
		_ = Directory.Exists(Path.Combine(root.Backups, "corrupted")).Should().BeFalse(
			"no original existed, so no quarantine move could have happened and the quarantine directory was never created");
		_ = Directory.EnumerateFiles(root.Backups, "*.bak").Should().BeEmpty(
			"no backup existed and the File.Move branch minted none");

		_ = outcome.Reason.Should().Be(
			"no usable configuration backup could be restored; safe defaults were written instead",
			"nothing was preserved and defaults were written, so the reason is that sentence VERBATIM — any appended preservation or backup claim fails here");
	}

	// The non-mutating branch of NEW-2, still reachable and still honest: a SOUND
	// destination means the executor planned UseLoadedDocument and wrote nothing. Here
	// "already readable" is literally true and restore-backup is a no-op, so Completed
	// with that wording is the correct report — the fix must not push this into Failed.
	[Fact]
	public void RestoreBackup_WhenTheDestinationIsSound_SaysAlreadyReadableAndWritesNothing()
	{
		StorageRootResolver root = CreateRoot();
		string destination = Path.Combine(root.Root, "app.json");
		File.WriteAllText(destination, "{\"schemaVersion\":1,\"schemaRevision\":0,\"keep\":\"me\"}");
		byte[] before = File.ReadAllBytes(destination);
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RestoreBackup);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = File.ReadAllBytes(destination).Should().Equal(before, "a sound configuration must not be touched by a restore");

		// PINNED BY EQUALITY, NOT BY Contain("already readable"). The needle left a
		// false appended claim green — an audit appended "the previous file was backed
		// up first" to this arm and every test stayed green, because the sentence still
		// contained the needle. The property is "the reason is this ONE sentence and
		// nothing else", and equality is the only assertion that holds it.
		_ = outcome.Reason.Should().Be(
			"the configuration was already readable; no backup needed to be restored",
			"nothing was written and nothing was restored, so the reason is that sentence VERBATIM");
	}

	// The FOURTH and last reachable RestoreBackup arm: nothing usable was restored AND
	// nothing readable remains, so the session runs on in-memory defaults. It is
	// reachable, and by an ordinary state rather than an exotic one: a corrupt
	// destination whose quarantine itself FAILS. ConfigRepairExecutor.Perform
	// re-plans that observation and the planner returns QuarantineFailedReadOnly
	// (owner decision 2, fail CLOSED and never retry), whose outcome is
	// (BackupPromoted false, DefaultsPersisted false, DestinationUsable false) — the
	// arm-(c) triple. The seam is the repair filesystem, not the disk: Move is the
	// quarantine's only acting call, and a real Move cannot be made to fail on these
	// small temp files without depending on the machine's ACLs, so this uses a
	// first-call-fails double and proves the arm is reachable at all.
	//
	// PINNED BY EQUALITY, like the other three arms: this arm composes one
	// deterministic sentence, so an appended clause fails here.
	[Fact]
	public void RestoreBackup_WhenTheDestinationStaysUnusable_ReportsThatNothingUsableWasRestored()
	{
		StorageRootResolver root = CreateRoot();
		string destination = Path.Combine(root.Root, "app.json");
		File.WriteAllText(destination, "this is not json at all");
		SafeModeRecoveryActions actions = Build(root, repairFileSystem: new FailingFirstMoveRepairFileSystem());

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RestoreBackup);

		_ = outcome.Status.Should().Be(
			SafeModeActionStatus.Failed,
			"the quarantine failed, so nothing was preserved, nothing was written and the arm-c report is the only honest one");

		// The effect: the original is STILL THERE, unreadable and unpreserved. Nothing
		// was written over it (fail closed), so the destination holds the original bytes.
		_ = File.ReadAllText(destination).Should().Be(
			"this is not json at all",
			"a failed quarantine licenses no write, so the corrupt original must be left in place");

		_ = outcome.Reason.Should().Be(
			"no usable configuration backup could be restored and the destination remains unusable",
			"nothing was usable and nothing readable remains, so the reason is that sentence VERBATIM — any appended claim fails here");
	}

	// ===================== reset-defaults: the second failure arm =====================

	// The OTHER failure arm of ResetDefaults, and it had NO test. Its reason —
	// "defaults could not be written and the existing configuration is not usable" — is
	// composed for exactly (!DefaultsPersisted && !DestinationUsable), which is
	// reachable two ways: a Degraded outcome (the defaults write itself failed after
	// the quarantine succeeded, executor:768) or the catch arm at :282. The repair
	// seam drives the FIRST of those honestly — a writer whose atomic publish throws.
	//
	// WHY THE WRITER AND NOT THE DISK: AtomicConfigDocumentWriter builds an
	// AtomicJsonFileStore per write, so a double that throws IOException from Write is
	// the same failure shape the executor's own fail-open test uses
	// (ConfigRepairExecutorTests.Execute_WhenTheWriteFails_FailsOpenInsteadOfCrashingStartup).
	// Seeding the destination with malformed bytes is what makes the planner reach a
	// write at all: an ABSENT destination takes the same PersistDefaults call, so the
	// quarantined-variant seed exercises a strictly larger path.
	[Fact]
	public void ResetDefaults_WhenTheDefaultsWriteFails_ReportsThatDefaultsCouldNotBeWrittenAndTheConfigurationIsNotUsable()
	{
		StorageRootResolver root = CreateRoot();
		string destination = Path.Combine(root.Root, "app.json");
		File.WriteAllText(destination, "this is not json at all");
		SafeModeRecoveryActions actions = Build(root, writer: new ThrowingConfigDocumentWriter());

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.ResetDefaults);

		_ = outcome.Status.Should().Be(
			SafeModeActionStatus.Failed,
			"defaults were not written and nothing readable remains, so the reset did not succeed");

		// The effect the wording rests on: the quarantine moved the original away (that
		// is logged before the write), and the failed write left NO defaults at the
		// destination — so "defaults could not be written" is the whole truth.
		_ = File.Exists(destination).Should().BeFalse(
			"the atomic publish threw, so the defaults must not be at the destination");
		_ = Directory.EnumerateFiles(Path.Combine(root.Backups, "corrupted"), "app.json.*.corrupted")
			.Should().NotBeEmpty("the original was quarantined before the write that then failed");

		_ = outcome.Reason.Should().Be(
			"defaults could not be written and the existing configuration is not usable",
			"the defaults write failed and nothing readable remains, so the reason is that sentence VERBATIM — any appended clause fails here");
	}

	// ===================== 4. open-data-folder =====================

	[Fact]
	public void OpenDataFolder_WithoutAShellSeam_ReportsUnavailableWithAReason()
	{
		StorageRootResolver root = CreateRoot();
		SafeModeRecoveryActions actions = Build(root, dataFolderOpener: null);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.OpenDataFolder);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Unavailable);
		_ = outcome.Reason.Should().NotBeNullOrWhiteSpace();
		_ = outcome.Status.Should().NotBe(SafeModeActionStatus.Completed);
	}

	[Fact]
	public void OpenDataFolder_WithAShellSeam_InvokesItWithTheResolvedRoot()
	{
		StorageRootResolver root = CreateRoot();
		RecordingDataFolderOpener opener = new();
		SafeModeRecoveryActions actions = Build(root, dataFolderOpener: opener);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.OpenDataFolder);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = opener.OpenedPaths.Should().Equal(root.Root);
	}

	// ===================== 5. registration-repair =====================

	[Fact]
	public void RegistrationRepair_WithoutARegistrationSeam_ReportsUnavailableWithAReason()
	{
		StorageRootResolver root = CreateRoot();
		SafeModeRecoveryActions actions = Build(root, registration: null);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RegistrationRepair);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Unavailable);
		_ = outcome.Reason.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void RegistrationRepair_ReDeclaresCapabilitiesThroughTheExistingService()
	{
		StorageRootResolver root = CreateRoot();
		RecordingRegistration registration = new();
		SafeModeRecoveryActions actions = Build(root, registration: registration);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RegistrationRepair);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = registration.RegisterCalls.Should().Be(1, "repair must invoke the real registration machinery");
	}

	// NEW-1 (W8.2 reason-text accuracy, independently confirmed): the service REFUSES a
	// run with NO backup sink or an unwritable backup BEFORE touching the registry, and
	// both refusals carry BackupFilePath = null (WindowsCapabilityRegistrationService
	// :55-58 and :73-78). The old failure text asserted "a pre-write backup was taken"
	// unconditionally — a claim that is false on exactly those refusals, which is the
	// only picture most users would ever see. The reason must report the REAL refusal
	// reason the service returned and must NOT claim a backup it did not take.
	//
	// PINNED BY EQUALITY, NOT BY A NEEDLE. The property is "a null BackupFilePath means
	// the refusal is passed through VERBATIM — nothing is appended that could assert a
	// backup". A single NotContain(needle) does NOT pin that property: re-introducing a
	// false claim with DIFFERENT wording ("The pre-write state was backed up first.",
	// "The pre-write snapshot was saved.", even "The registry was restored from the
	// backup.") left the needle green. Equality against the service's own refusal string
	// is the only assertion that fails for EVERY such appended clause, whatever its
	// wording, because any appended clause changes the string.
	[Fact]
	public void RegistrationRepair_WhenTheServiceRefusesWithoutABackup_DoesNotClaimABackupWasTaken()
	{
		StorageRootResolver root = CreateRoot();
		string refusal =
			"LocalAppData is unavailable; refusing to modify the registry without a backup sink.";
		RecordingRegistration registration = new()
		{
			RegisterResult = new CapabilityRegistrationResult(
				false,
				[],
				[],
				null,
				refusal),
		};
		SafeModeRecoveryActions actions = Build(root, registration: registration);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RegistrationRepair);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Failed);

		// BackupFilePath is null, so the report may say NOTHING beyond the refusal the
		// service itself returned: any appended clause — whatever its wording — is a
		// backup claim the null path cannot support. Equality is the assertion that
		// fails on ALL of them; a NotContain(needle) fails only on the one needle.
		_ = outcome.Reason.Should().Be(
			refusal,
			"a null BackupFilePath means no backup was taken, so the reason must be the service's refusal VERBATIM, with no appended backup claim");
	}

	// The other half of NEW-1: a refusal whose backup write FAILED does carry a reason
	// too, and it must survive into the report verbatim rather than being replaced by a
	// canned line. Kept separate from the test above because the two refusals are two
	// different code paths in the service with two different messages.
	//
	// Same equality pin: this refusal also carries BackupFilePath = null, so the report
	// is the refusal string and NOTHING else. `NotContain("backup was taken")` was
	// especially weak here — the refusal's own text contains "backup could not be
	// written", so a false appended claim phrased any other way sailed through.
	[Fact]
	public void RegistrationRepair_WhenTheBackupWriteFails_ReportsThatRefusalAndStillDoesNotClaimASuccessfulBackup()
	{
		StorageRootResolver root = CreateRoot();
		string refusal = "The pre-write backup could not be written; the registry was not touched.";
		RecordingRegistration registration = new()
		{
			RegisterResult = new CapabilityRegistrationResult(
				false,
				[],
				[],
				null,
				refusal),
		};
		SafeModeRecoveryActions actions = Build(root, registration: registration);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RegistrationRepair);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Failed);

		// The failed backup write is a refusal with BackupFilePath = null, so no backup
		// claim of ANY phrasing is supportable: the reason is the refusal VERBATIM.
		_ = outcome.Reason.Should().Be(
			refusal,
			"the backup write failed (BackupFilePath is null), so the reason is the service's refusal VERBATIM, with no appended backup claim");
	}

	// A FAILURE that did take a backup (a partial write, mid-write failure, or a failed
	// post-write verification) must still NOT report "a pre-write backup was taken" as a
	// bare assertion: the backup is only real when the service hands back its path. Here
	// it does, so the report may — and must — say so, together with the real reason.
	[Fact]
	public void RegistrationRepair_WhenAFailureCarriesABackupPath_ReportsTheBackupAndTheRealReason()
	{
		StorageRootResolver root = CreateRoot();
		RecordingRegistration registration = new()
		{
			RegisterResult = new CapabilityRegistrationResult(
				false,
				[],
				[],
				@"C:\Users\someone\AppData\Local\BroChoos\backups\registration-20260101T000000000Z-abcd.json",
				"A capability key could not be written (permissions or hive state)."),
		};
		SafeModeRecoveryActions actions = Build(root, registration: registration);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RegistrationRepair);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Failed);
		_ = outcome.Reason.Should().Contain("A capability key could not be written");

		// D-075 / D-097: the reason is user-facing text and must carry NO filesystem path,
		// even though the result the service returned does. Only the FACT of the backup
		// may appear, never where it landed.
		_ = outcome.Reason.Should().NotContain(@"C:\Users\someone");
		_ = outcome.Reason.Should().NotContain("registration-20260101T000000000Z-abcd.json");
	}

	// A SUCCESS must not be silent about what it did either: when the service reports a
	// backup path, the success genuinely included a pre-write snapshot, and the report
	// should be able to say so without claiming it when the path is absent (a run that
	// merely VERIFIED a pre-existing shape). Both halves are asserted here so neither can
	// drift.
	[Fact]
	public void RegistrationRepair_Success_MentionsTheBackupOnlyWhenTheServiceReportedOne()
	{
		StorageRootResolver root = CreateRoot();

		RecordingRegistration withBackup = new()
		{
			RegisterResult = new CapabilityRegistrationResult(
				true,
				[],
				[],
				@"C:\Users\someone\AppData\Local\BroChoos\backups\registration-20260101T000000000Z-abcd.json",
				null),
		};
		SafeModeActionOutcome backedUp = Build(root, registration: withBackup)
			.Perform(SafeModeActionIds.RegistrationRepair);

		_ = backedUp.Status.Should().Be(SafeModeActionStatus.Completed);

		// PINNED BY EQUALITY, NOT BY Contain("backed up first"). This arm composes ONE
		// static sentence — the backup PATH is never rendered, only the boolean fact that
		// a backup exists (SafeModeRecoveryActions.cs:372-375), so the string is
		// deterministic and a needle is strictly weaker than equality here: any appended
		// clause would sail past Contain. The NotContain below still carries the D-075 /
		// D-097 redaction claim, but the TEXT ITSELF is now pinned verbatim.
		_ = backedUp.Reason.Should().Be(
			"BroChoos handler capabilities were re-declared (the default is still the user's choice); the pre-write state was backed up first",
			"the service handed back a backup path, so a pre-write snapshot really was taken and the reason is that sentence VERBATIM");
		_ = backedUp.Reason.Should().NotContain(@"C:\Users\someone");

		RecordingRegistration withoutBackup = new()
		{
			RegisterResult = new CapabilityRegistrationResult(true, [], [], null, null),
		};
		SafeModeActionOutcome verifiedOnly = Build(CreateRoot(), registration: withoutBackup)
			.Perform(SafeModeActionIds.RegistrationRepair);

		_ = verifiedOnly.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = verifiedOnly.Reason.Should().NotContain(
			"backed up first",
			"a run that reported no backup path took none, so the success must not claim one");
	}

	// ===================== 6. synthetic-link-probe =====================

	[Fact]
	public void SyntheticLinkProbe_WithoutADetectorSeam_ReportsUnavailableWithAReason()
	{
		StorageRootResolver root = CreateRoot();
		SafeModeRecoveryActions actions = Build(root, detector: null);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.SyntheticLinkProbe);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Unavailable);
		_ = outcome.Reason.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void SyntheticLinkProbe_RunsTheRealDetectorAndLaunchesNothing()
	{
		StorageRootResolver root = CreateRoot();
		RecordingDetector detector = new();
		SafeModeRecoveryActions actions = Build(root, detector: detector);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.SyntheticLinkProbe);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = detector.DetectCalls.Should().Be(1, "the probe must actually observe discovery");
		_ = outcome.Reason.Should().Contain("candidate");
	}

	// ===================== 7. repair-settings =====================

	// The picker's notice action: an unreadable history.json is preserved, then reset, so the
	// next picker run reads it (Loaded) and shows no notice. A missing rules.json stays missing.
	[Fact]
	public void RepairSettings_PreservesAndResetsAnUnreadableHistory_AndLeavesAMissingRulesFileAlone()
	{
		StorageRootResolver root = CreateRoot();
		string history = Path.Combine(root.Root, "history.json");
		File.WriteAllText(history, "not json");
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = outcome.Reason.Should().Be("the picker settings were repaired; unreadable files were preserved first (restored=0 reset=1 kept=0 unusable=0)");
		_ = Directory.EnumerateFiles(Path.Combine(root.Backups, "corrupted"), "history.json.*.corrupted").Should().ContainSingle();
		_ = File.Exists(Path.Combine(root.Root, "rules.json")).Should().BeFalse("a missing file is a first run, not damage");

		SystemStorageReadFileSystem readFs = new();
		ActivationHistoryStore store = new(root, new ConfigFileObserver(readFs), new GenerationMutationCoordinator(readFs));
		_ = store.LoadWithOutcome().Outcome.Should().Be(ConfigReadOutcome.Loaded, "the picker reads the repaired file");
	}

	[Fact]
	public void RepairSettings_PromotesACompatibleRulesBackup()
	{
		StorageRootResolver root = CreateRoot();
		File.WriteAllText(Path.Combine(root.Root, "rules.json"), "{ broken");
		File.WriteAllText(
			Path.Combine(root.Backups, "rules.json.20260101000000000.0123456789abcdef0123456789abcdef.bak"),
			"{\"schemaVersion\":1,\"schemaRevision\":0,\"generation\":3,\"rules\":[]}");
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = outcome.Reason.Should().EndWith("(restored=1 reset=0 kept=0 unusable=0)");
		_ = File.ReadAllText(Path.Combine(root.Root, "rules.json")).Should().Contain("\"generation\":3");
	}

	// A newer-schema file belongs to a newer build: hard stop, nothing written, Failed.
	[Fact]
	public void RepairSettings_LeavesANewerSchemaFileUntouched_AndFails()
	{
		StorageRootResolver root = CreateRoot();
		string rules = Path.Combine(root.Root, "rules.json");
		File.WriteAllText(rules, "{\"schemaVersion\":99,\"schemaRevision\":0,\"generation\":1,\"rules\":[]}");
		byte[] before = File.ReadAllBytes(rules);
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Failed);
		_ = File.ReadAllBytes(rules).Should().Equal(before);
	}

	// Option (a): an oversized file is MOVED, unread, into backups\corrupted (its bytes kept
	// exactly), and defaults replace it, so the notice does not come back forever.
	[Fact]
	public void RepairSettings_OversizedFile_IsMovedUnreadAndReplacedByDefaults()
	{
		StorageRootResolver root = CreateRoot();
		string history = Path.Combine(root.Root, "history.json");
		const long Oversized = (8L * 1024 * 1024) + 1;
		using (FileStream big = File.Create(history))
		{
			big.SetLength(Oversized);
		}

		SafeModeActionOutcome outcome = Build(root).Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = outcome.Reason.Should().EndWith("(restored=0 reset=1 kept=0 unusable=0)");
		string quarantined = Directory.EnumerateFiles(Path.Combine(root.Backups, "corrupted"), "history.json.*.corrupted")
			.Should().ContainSingle().Subject;
		_ = new FileInfo(quarantined).Length.Should().Be(Oversized, "the move preserves every byte, unread");
		_ = PickerVerdict<ActivationHistoryDocument>(root, "history.json").Should().Be(ConfigReadOutcome.Loaded);
	}

	// PARSES BUT DOES NOT BIND: the envelope reads, a required member is missing. The picker
	// shows the notice (Corrupt), and the repair must agree and fix it, not call it readable.
	[Fact]
	public void RepairSettings_ParsesButDoesNotBind_IsTreatedAsCorrupt_AndRepaired()
	{
		StorageRootResolver root = CreateRoot();
		File.WriteAllText(Path.Combine(root.Root, "rules.json"), "{\"schemaVersion\":1,\"schemaRevision\":0,\"generation\":1}");
		_ = PickerVerdict<RulesDocument>(root, "rules.json").Should().Be(ConfigReadOutcome.Corrupt, "the picker shows the notice for it");

		SafeModeActionOutcome outcome = Build(root).Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = outcome.Reason.Should().NotStartWith("the picker settings were already readable");
		_ = PickerVerdict<RulesDocument>(root, "rules.json").Should().Be(ConfigReadOutcome.Loaded, "the next picker run shows no notice");
	}

	// A newer REVISION is readable for the planner, but the picker rejects it (Newer), and this
	// build would drop its extra fields on a write. Kept, untouched, reported - never "readable".
	[Fact]
	public void RepairSettings_NewerRevision_IsKeptUntouched_AndCountedAsKept()
	{
		StorageRootResolver root = CreateRoot();
		string rules = Path.Combine(root.Root, "rules.json");
		File.WriteAllText(rules, "{\"schemaVersion\":1,\"schemaRevision\":5,\"generation\":1,\"rules\":[]}");
		byte[] before = File.ReadAllBytes(rules);

		SafeModeActionOutcome outcome = Build(root).Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Failed);
		_ = outcome.Reason.Should().EndWith("(restored=0 reset=0 kept=1 unusable=0)");
		_ = File.ReadAllBytes(rules).Should().Equal(before);
	}

	[Fact]
	public void RepairSettings_OlderSchema_IsKeptUntouched()
	{
		StorageRootResolver root = CreateRoot();
		string history = Path.Combine(root.Root, "history.json");
		File.WriteAllText(history, "{\"schemaVersion\":0,\"schemaRevision\":0,\"generation\":1,\"settings\":{},\"entries\":[]}");
		byte[] before = File.ReadAllBytes(history);

		SafeModeActionOutcome outcome = Build(root).Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Failed);
		_ = File.ReadAllBytes(history).Should().Equal(before);
		_ = PickerVerdict<ActivationHistoryDocument>(root, "history.json").Should().Be(ConfigReadOutcome.Incompatible);
	}

	// An INVALID envelope (negative schemaVersion) is a planner hard stop: no quarantine, no
	// write. The repair keeps it untouched and the picker names it InvalidEnvelope, whose
	// message asks the user to move the file - it is not "this version cannot convert it".
	[Fact]
	public void RepairSettings_InvalidEnvelope_IsKeptUntouched_AndClassifiedAsInvalidEnvelope()
	{
		StorageRootResolver root = CreateRoot();
		string rules = Path.Combine(root.Root, "rules.json");
		File.WriteAllText(rules, "{\"schemaVersion\":-1,\"schemaRevision\":0,\"generation\":1,\"rules\":[]}");
		byte[] before = File.ReadAllBytes(rules);
		_ = PickerVerdict<RulesDocument>(root, "rules.json").Should().Be(ConfigReadOutcome.InvalidEnvelope);

		SafeModeActionOutcome outcome = Build(root).Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Failed);
		_ = outcome.Reason.Should().EndWith("(restored=0 reset=0 kept=1 unusable=0)");
		_ = File.ReadAllBytes(rules).Should().Equal(before);
		_ = Directory.Exists(Path.Combine(root.Backups, "corrupted")).Should().BeFalse("a hard stop quarantines nothing");
	}

	// A quarantine that cannot move the file writes nothing and is counted unusable, so the
	// action fails (exit 11) and the picker re-reads Corrupt -> error naming the command.
	[Fact]
	public void RepairSettings_FailedQuarantine_WritesNothing_AndIsUnusable()
	{
		StorageRootResolver root = CreateRoot();
		string history = Path.Combine(root.Root, "history.json");
		File.WriteAllText(history, "not json");

		SafeModeActionOutcome outcome = Build(root, repairFileSystem: new FailingFirstMoveRepairFileSystem()).Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Failed);
		_ = outcome.Reason.Should().EndWith("(restored=0 reset=0 kept=0 unusable=1)");
		_ = File.ReadAllText(history).Should().Be("not json");
	}

	// The defaults write fails AFTER the quarantine moved the file: the destination is now
	// absent. Unusable (exit 11), and the picker re-reads Missing -> the "still broken" error.
	[Fact]
	public void RepairSettings_WriteFailsAfterQuarantine_IsUnusable_AndTheOriginalIsPreserved()
	{
		StorageRootResolver root = CreateRoot();
		File.WriteAllText(Path.Combine(root.Root, "history.json"), "not json");

		SafeModeActionOutcome outcome = Build(root, writer: new ThrowingConfigDocumentWriter()).Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Failed);
		_ = outcome.Reason.Should().EndWith("(restored=0 reset=0 kept=0 unusable=1)");
		_ = PickerVerdict<ActivationHistoryDocument>(root, "history.json").Should().Be(ConfigReadOutcome.Missing);
		_ = Directory.EnumerateFiles(Path.Combine(root.Backups, "corrupted"), "history.json.*.corrupted").Should().ContainSingle();
	}

	// THE PER-FILE LOCK IS HELD from the observation through the last write: while another
	// thread holds it, the repair waits; it never writes between "moved" and "replaced".
	[Fact]
	public async Task RepairSettings_WaitsForThePerFileLock_AndWritesNothingWhileItIsHeld()
	{
		StorageRootResolver root = CreateRoot();
		string history = Path.Combine(root.Root, "history.json");
		File.WriteAllText(history, "not json");
		using ManualResetEventSlim taken = new();
		using ManualResetEventSlim release = new();

		Thread holder = new(() =>
		{
			using Mutex m = new(initiallyOwned: false, AtomicJsonFileStore.BuildMutexName(history));
			_ = m.WaitOne();
			taken.Set();
			_ = release.Wait(TimeSpan.FromSeconds(10));
			m.ReleaseMutex();
		});
		holder.Start();
		_ = taken.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

		Task<SafeModeActionOutcome> repair = Task.Run(() => Build(root).Perform(SafeModeActionIds.RepairSettings));
		Task first = await Task.WhenAny(repair, Task.Delay(TimeSpan.FromMilliseconds(500)));
		_ = ReferenceEquals(first, repair).Should().BeFalse("the repair must wait for the writer's lock");
		_ = File.ReadAllText(history).Should().Be("not json", "nothing was moved or written while the lock was held");

		release.Set();
		holder.Join();
		SafeModeActionOutcome done = await repair.WaitAsync(TimeSpan.FromSeconds(10));
		_ = done.Status.Should().Be(SafeModeActionStatus.Completed);
	}

	private static ConfigReadOutcome PickerVerdict<TDocument>(StorageRootResolver root, string fileName)
		where TDocument : ConfigDocument
	{
		return new ConfigFileObserver(new SystemStorageReadFileSystem())
			.ObserveReadOutcome<TDocument>(Path.Combine(root.Root, fileName)).Outcome;
	}

	[Fact]
	public void RepairSettings_ReadableFiles_ChangeNothing()
	{
		StorageRootResolver root = CreateRoot();
		string rules = Path.Combine(root.Root, "rules.json");
		File.WriteAllText(rules, "{\"schemaVersion\":1,\"schemaRevision\":0,\"generation\":2,\"rules\":[]}");
		byte[] before = File.ReadAllBytes(rules);

		SafeModeActionOutcome outcome = Build(root).Perform(SafeModeActionIds.RepairSettings);

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Completed);
		_ = outcome.Reason.Should().StartWith("the picker settings were already readable");
		_ = File.ReadAllBytes(rules).Should().Equal(before);
	}

	// ===================== unknown action =====================

	[Fact]
	public void UnknownAction_ReportsUnavailableRatherThanPretendingSuccess()
	{
		StorageRootResolver root = CreateRoot();
		SafeModeRecoveryActions actions = Build(root);

		SafeModeActionOutcome outcome = actions.Perform("no-such-action");

		_ = outcome.Status.Should().Be(SafeModeActionStatus.Unavailable);
	}

	// ===================== helpers =====================

	private static SafeModeRecoveryActions Build(
		StorageRootResolver root,
		ICapabilityRegistrationService? registration = null,
		IBrowserDetector? detector = null,
		IDataFolderOpener? dataFolderOpener = null,
		IStorageRepairFileSystem? repairFileSystem = null,
		IConfigDocumentWriter? writer = null)
	{
		ConfigRepairExecutor executor = new(
			new ConfigFileObserver(new SystemStorageReadFileSystem()),
			repairFileSystem ?? new SystemStorageRepairFileSystem(),
			new SystemStorageFileSystem(),
			writer ?? new AtomicConfigDocumentWriter(root.Backups),
			TimeProvider.System,
			new InMemoryConfigRepairLog());

		return new SafeModeRecoveryActions(
			NullLogger<SafeModeRecoveryActions>.Instance,
			root,
			executor,
			registration,
			detector,
			dataFolderOpener);
	}

	// A REAL storage root under temp, built through the production resolver so the
	// recovery actions see exactly the directory layout production provisions.
	private StorageRootResolver CreateRoot()
	{
		string directory = Path.Combine(Path.GetTempPath(), "brochoos-w82-" + Guid.NewGuid().ToString("N")[..12]);
		_ = Directory.CreateDirectory(directory);
		_temporaryDirectories.Add(directory);

		return new StorageRootResolver(
			new FixedEnvironmentProbe(directory),
			new SystemStorageFileSystem(),
			portableDataArgument: directory);
	}

	private sealed class FixedEnvironmentProbe(string dataRoot) : IEnvironmentProbe
	{
		public string? CurrentProcessPath => null;

		public string? GetEnvironmentVariable(string name)
		{
			return string.Equals(name, "LocalAppData", StringComparison.OrdinalIgnoreCase) ? dataRoot : null;
		}

		public bool FileExists(string path)
		{
			return File.Exists(path);
		}
	}

	// Fails the FIRST Move and delegates every later call to the real filesystem.
	// Move is the quarantine's only acting call, so a first-call failure makes the
	// quarantine fail; the delegate would be wrong to leave unimplemented, and the two
	// members the quarantine never calls are delegated for the same reason.
	private sealed class FailingFirstMoveRepairFileSystem : IStorageRepairFileSystem
	{
		private readonly SystemStorageRepairFileSystem _real = new();
		private bool _failed;

		public void Move(string sourcePath, string destinationPath)
		{
			if (!_failed)
			{
				_failed = true;
				throw new IOException("the quarantine move was refused");
			}

			_real.Move(sourcePath, destinationPath);
		}

		public byte[] ReadAllBytes(string sourcePath)
		{
			return _real.ReadAllBytes(sourcePath);
		}

		public void WriteBytesAtomically(
			string destinationPath,
			byte[] bytes,
			ExpectedDocumentGeneration expectedGeneration,
			TimeSpan writeLockTimeout)
		{
			_real.WriteBytesAtomically(destinationPath, bytes, expectedGeneration, writeLockTimeout);
		}
	}

	// The write seam's failure shape: PersistDefaults catches IOException and converts
	// it to a fail-open Degraded outcome, which is the outcome the reset arm under test
	// is defined by. One instance is good for one write, which is all these facts make.
	private sealed class ThrowingConfigDocumentWriter : IConfigDocumentWriter
	{
		public void Write(string destinationPath, ConfigDocument document, TimeSpan writeLockTimeout)
		{
			throw new IOException("the atomic publish was refused");
		}
	}

	private sealed class RecordingRegistration : ICapabilityRegistrationService
	{
		public int RegisterCalls { get; private set; }

		// The REAL service answers with a success or a refusal that may or may not carry
		// a backup path; a double that can only say "true" cannot exercise the difference
		// the reason text has to honour, so the result is settable.
		public CapabilityRegistrationResult RegisterResult { get; set; } =
			new(true, [], [], null, null);

		public CapabilityRegistrationResult RegisterAll()
		{
			RegisterCalls++;
			return RegisterResult;
		}

		public CapabilityRegistrationResult UnregisterAll()
		{
			return new CapabilityRegistrationResult(true, [], [], null, null);
		}
	}

	private sealed class RecordingDetector : IBrowserDetector
	{
		public int DetectCalls { get; private set; }

		public IReadOnlyList<BrowserCandidate> Detect()
		{
			DetectCalls++;
			return
			[
				new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\fake\msedge.exe", "stable", "known-path"),
			];
		}
	}

	private sealed class RecordingDataFolderOpener : IDataFolderOpener
	{
		private readonly List<string> _openedPaths = [];

		public IReadOnlyList<string> OpenedPaths => _openedPaths;

		public bool TryOpen(string directoryPath)
		{
			_openedPaths.Add(directoryPath);
			return true;
		}
	}
}
