using System.Reflection;
using System.Text;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

// Facts for the ACTING half of the config read path. The observer and the planner are
// already pinned by their own suites; what is pinned here is the ORDER in which bytes
// move, and the two places where getting that order wrong destroys a user's file.
//
// THE ORDERING FACTS USE A FAKE DISK, DELIBERATELY, and that is not a shortcut. The
// property under test is ORDER — "no byte is written before the original is preserved"
// — and a real filesystem can only show the OUTCOME. A recorder that appends every
// operation to one list can show that no write operation precedes the move, which is
// the invariant itself rather than a consequence of it.
//
// THE BYTE-COPY FACT USES A REAL DISK, EQUALLY DELIBERATELY. Its claim is about the
// BYTES on disk, so a fake that "copies" by assigning an array would pass while proving
// nothing about File.Copy. That one drives SystemStorageRepairFileSystem and the real
// AtomicJsonFileStore against a temp directory, and reads the resulting bytes back.
public sealed class ConfigRepairExecutorTests : IDisposable
{
	private const string FileName = "app.json";

	// A whole, compatible document that ALSO carries a member this build does not map.
	// The unmapped member is the entire point: it is what a deserialize-then-reserialize
	// promotion silently drops (UnmappedMemberHandling.Skip), so its survival is the
	// evidence that the promotion copied bytes.
	private const string CompatibleBackupWithUnmappedMember =
							 /*lang=json,strict*/
							 "{\"schemaVersion\":1,\"schemaRevision\":0,\"handEditedByTheUser\":\"keep me\"}";

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

	// ===================== THE ORDERING INVARIANT (owner decision 1) =====================

	// THE LOAD-BEARING FACT. Owner decision 1: no byte is written, moved or replaced
	// until either the envelope is proved not-newer or the original is preserved in
	// backups\corrupted. Here the envelope does NOT parse, so the schema is unknowable
	// and only the preservation route is available — which makes this the strictest
	// shape of the invariant.
	//
	// ASSERTED AS AN ORDER, NOT AS AN OUTCOME: the move must appear in the operation log
	// BEFORE any write, and asserting the outcome alone would pass for an executor that
	// wrote first and quarantined afterwards.
	[Fact]
	public void Execute_CorruptFileWithNoBackup_PreservesTheOriginalBeforeAnyByteIsWritten()
	{
		FakeDisk disk = new();
		disk.AddFile(FileName, "this is not json at all");

		ConfigRepairOutcome outcome = Execute(disk);

		int move = disk.Operations.FindIndex(entry => entry.StartsWith("move:", StringComparison.Ordinal));
		int write = disk.Operations.FindIndex(entry => entry.StartsWith("write:", StringComparison.Ordinal));

		_ = move.Should().BeGreaterThanOrEqualTo(0, "the original must be preserved");
		_ = write.Should().BeGreaterThanOrEqualTo(0, "defaults must then be written");
		_ = move.Should().BeLessThan(write, "no byte may be written before the original is preserved");

		_ = outcome.OriginalPreserved.Should().BeTrue();
		_ = outcome.DefaultsPersisted.Should().BeTrue();
		_ = outcome.DestinationUsable.Should().BeTrue();
	}

	// Owner decision 2: a quarantine that fails FAILS CLOSED to read-only and never
	// retries. The strong half of the assertion is the ABSENCE of every mutating
	// operation — a defaults write here would overwrite an original that is still in
	// place and unpreserved, which is the worst outcome on this path.
	[Fact]
	public void Execute_WhenQuarantineFails_WritesNothingAtAllAndFailsClosedToReadOnly()
	{
		FakeDisk disk = new() { MoveFailure = new IOException("the disk is full") };
		disk.AddFile(FileName, "this is not json at all");

		ConfigRepairOutcome outcome = Execute(disk);

		_ = disk.Operations.Should().NotContain(entry => entry.StartsWith("write:", StringComparison.Ordinal));
		_ = disk.Operations.Should().NotContain(entry => entry.StartsWith("writebytes:", StringComparison.Ordinal));

		_ = outcome.Action.Should().Be(ConfigLoadAction.QuarantineFailedReadOnly);
		_ = outcome.Reason.Should().Be(ConfigLoadReason.QuarantineFailedReadOnlyNoRetry);
		_ = outcome.OriginalPreserved.Should().BeFalse();
		_ = outcome.DefaultsPersisted.Should().BeFalse();
		_ = outcome.ReadOnlyForSession.Should().BeTrue();

		// The original is still exactly where it was, byte for byte.
		_ = disk.ReadText(FileName).Should().Be("this is not json at all");
	}

	// ===================== BYTE-LEVEL PROMOTION (owner decision 4) =====================

	// OWNER DECISION 4, AS A NAMED FACT. Promotion must be a BYTE-LEVEL copy even for a
	// CompletelyCompatible backup. Core's BackupPromotionClearance cannot enforce HOW an
	// executor copies — it can only refuse to mint a token — so it records the byte-copy
	// obligation as an executor precondition and this fact is where that precondition is
	// actually held.
	//
	// THE UNMAPPED MEMBER IS THE INSTRUMENT. A promotion implemented as
	// deserialize-then-reserialize would run with UnmappedMemberHandling.Skip and drop
	// "handEditedByTheUser" permanently while still producing a valid, compatible
	// document — passing any assertion that only checked the envelope. Requiring the
	// bytes to match exactly is what makes that mutation fail.
	//
	// REAL DISK, REAL File.Copy, REAL AtomicJsonFileStore: the claim is about bytes, so a
	// fake would prove nothing about the framework call that moves them.
	[Fact]
	public void Execute_PromotingACompatibleBackup_CopiesItByteForByteIncludingMembersThisBuildDoesNotMap()
	{
		string root = CreateTemporaryDirectory();
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);

		string destination = Path.Combine(root, FileName);
		File.WriteAllText(destination, "}{ truncated garbage");

		// The stamped name shape AtomicJsonFileStore mints, so the observer can order it.
		string backupName = $"{FileName}.20260101120000000.{Guid.NewGuid():N}.bak";
		string backupPath = Path.Combine(backups, backupName);
		byte[] backupBytes = Encoding.UTF8.GetBytes(CompatibleBackupWithUnmappedMember);
		File.WriteAllBytes(backupPath, backupBytes);

		ConfigRepairExecutor executor = new(
			new ConfigFileObserver(new SystemStorageReadFileSystem()),
			new SystemStorageRepairFileSystem(),
			new SystemStorageFileSystem(),
			new AtomicConfigDocumentWriter(backups),
			TimeProvider.System,
			new InMemoryConfigRepairLog());

		ConfigRepairOutcome outcome = executor.Execute(destination, backups, new AppDocument());

		_ = outcome.BackupPromoted.Should().BeTrue();
		_ = outcome.OriginalPreserved.Should().BeTrue();
		_ = outcome.DestinationUsable.Should().BeTrue();

		// BYTE FOR BYTE. Not "parses equivalently" — identical bytes.
		_ = File.ReadAllBytes(destination).Should().Equal(backupBytes);
		_ = File.ReadAllText(destination).Should().Contain("handEditedByTheUser");

		// The promoted backup is left in place, unmodified.
		_ = File.ReadAllBytes(backupPath).Should().Equal(backupBytes);
	}

	// Owner decision 7: the quarantined original lands strictly inside
	// backups\corrupted, and its name cannot collide with a backup name. The extension
	// test is the machine-checkable half of "cannot collide": the write side mints
	// ".bak", so a name ending ".corrupted" is not a name it can ever produce.
	[Fact]
	public void Execute_QuarantinedOriginal_LandsOnlyInBackupsCorruptedWithANameNoBackupCanCollideWith()
	{
		string root = CreateTemporaryDirectory();
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);

		string destination = Path.Combine(root, FileName);
		File.WriteAllText(destination, "not json");

		ConfigRepairExecutor executor = new(
			new ConfigFileObserver(new SystemStorageReadFileSystem()),
			new SystemStorageRepairFileSystem(),
			new SystemStorageFileSystem(),
			new AtomicConfigDocumentWriter(backups),
			TimeProvider.System,
			new InMemoryConfigRepairLog());

		_ = executor.Execute(destination, backups, new AppDocument());

		string quarantine = Path.Combine(backups, "corrupted");
		string[] quarantined = Directory.GetFiles(quarantine);

		_ = quarantined.Should().HaveCount(1);
		_ = Path.GetFileName(quarantined[0]).Should().StartWith(FileName).And.EndWith(".corrupted");

		// backups\ itself is enumerated NON-RECURSIVELY by the observer, which is what
		// keeps the quarantined file out of the promotable-candidate list. Nothing the
		// quarantine wrote sits directly in backups\ alongside the real backups.
		_ = Directory.GetFiles(backups).Should().NotContain(entry => entry.EndsWith(".corrupted", StringComparison.Ordinal));
	}

	// ===================== HARD STOPS: NOTHING IS WRITTEN =====================

	// Owner decision 5: an older schemaVersion is a HARD STOP to read-only, because 0A
	// ships no migrations and none may be invented.
	[Fact]
	public void Execute_OlderSchemaVersionDestination_HardStopsReadOnlyAndWritesNothing()
	{
		FakeDisk disk = new();
		disk.AddFile(FileName, /*lang=json,strict*/ "{\"schemaVersion\":0,\"schemaRevision\":0}");

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.Action.Should().Be(ConfigLoadAction.HardStopReadOnly);
		_ = outcome.Reason.Should().Be(ConfigLoadReason.MainFileOlderSchemaMigrationUnavailableReadOnly);
		_ = outcome.ReadOnlyForSession.Should().BeTrue();
		_ = disk.MutatingOperations.Should().BeEmpty();
	}

	// D-066: a newer schemaVersion is never overwritten, silently or otherwise.
	[Fact]
	public void Execute_NewerSchemaVersionDestination_HardStopsReadOnlyAndWritesNothing()
	{
		FakeDisk disk = new();
		disk.AddFile(FileName, /*lang=json,strict*/ "{\"schemaVersion\":99,\"schemaRevision\":0}");

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.Action.Should().Be(ConfigLoadAction.HardStopReadOnly);
		_ = outcome.Reason.Should().Be(ConfigLoadReason.MainFileNewerSchemaReadOnly);
		_ = disk.MutatingOperations.Should().BeEmpty();
	}

	// Owner decision 3: a NEWER-VERSION backup is never promoted, and the refusal does
	// not merely skip it — writing defaults beside data a newer version owns is still a
	// write, so this hard-stops. The backup file must be left completely untouched.
	[Fact]
	public void Execute_NewerVersionBackup_IsNeverPromotedAndTheBackupFileIsLeftUntouched()
	{
		FakeDisk disk = new();
		disk.AddFile(FileName, "not json");
		string backupName = $"{FileName}.20260101120000000.{Guid.NewGuid():N}.bak";
		const string BackupBody = /*lang=json,strict*/ "{\"schemaVersion\":99,\"schemaRevision\":0}";
		disk.AddBackup(backupName, BackupBody);

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.Action.Should().Be(ConfigLoadAction.HardStopReadOnly);
		_ = outcome.Reason.Should().Be(ConfigLoadReason.BackupNewerSchemaNotPromotable);
		_ = outcome.BackupPromoted.Should().BeFalse();
		_ = disk.MutatingOperations.Should().BeEmpty();
		_ = disk.ReadText(Path.Combine("backups", backupName)).Should().Be(BackupBody);
	}

	// A NEWER-REVISION backup is a weaker case and takes the other route: it is refused
	// for promotion (Core mints no token for it), and the repair falls through to
	// quarantine-then-defaults with the backup file left intact for a later build.
	[Fact]
	public void Execute_NewerRevisionBackup_IsNotPromotedButFallsThroughToQuarantineThenDefaults()
	{
		FakeDisk disk = new();
		disk.AddFile(FileName, "not json");
		string backupName = $"{FileName}.20260101120000000.{Guid.NewGuid():N}.bak";
		const string BackupBody = /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":7}";
		disk.AddBackup(backupName, BackupBody);

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.BackupPromoted.Should().BeFalse();
		_ = outcome.OriginalPreserved.Should().BeTrue();
		_ = outcome.DefaultsPersisted.Should().BeTrue();
		_ = disk.Operations.Should().NotContain(entry => entry.StartsWith("writebytes:", StringComparison.Ordinal));
		_ = disk.ReadText(Path.Combine("backups", backupName)).Should().Be(BackupBody);
	}

	// Promotion validates the SAME bytes it publishes, with the observer's BOM handling. A
	// compatible backup that starts with a UTF-8 BOM is selected by the observer (it skips the
	// BOM), so promotion must accept it too and copy its bytes verbatim.
	[Fact]
	public void Execute_BomPrefixedCompatibleBackup_IsPromotedByteForByte()
	{
		FakeDisk disk = new();
		disk.AddFile(FileName, "not json");
		string backupName = $"{FileName}.20260101120000000.{Guid.NewGuid():N}.bak";
		byte[] bomBytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}")];
		disk.AddBackupBytes(backupName, bomBytes);

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.BackupPromoted.Should().BeTrue();
		_ = disk.GetBytes(FileName).Should().Equal(bomBytes);
	}

	// The captured snapshot is what gets validated, not an earlier read: here the observer's
	// read sees a sound backup, but the bytes captured for promotion carry a duplicate envelope
	// key, which the observer's counting scan refuses. Nothing may be published.
	[Fact]
	public void Execute_CapturedBackupBytesFailTheObserverClassification_AreNotPublished()
	{
		FakeDisk disk = new()
		{
			CapturedBytesOverride = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0,\"schemaRevision\":0}"),
		};
		disk.AddFile(FileName, "not json");
		string backupName = $"{FileName}.20260101120000000.{Guid.NewGuid():N}.bak";
		disk.AddBackup(backupName, /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.BackupPromoted.Should().BeFalse();
		_ = disk.Operations.Should().NotContain(entry => entry.StartsWith("writebytes:", StringComparison.Ordinal));
	}

	// The CAPTURED snapshot is held to the observer's 8 MiB ceiling too. The file's reported
	// length is small (so Promote's pre-read check passes), but the captured bytes are a
	// compatible document padded with whitespace past the ceiling: it would bind, so the
	// ceiling alone is what refuses it.
	[Fact]
	public void Execute_CapturedBackupBytesOverTheReadCeiling_AreNotPublished()
	{
		byte[] envelope = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");
		byte[] oversized = new byte[(8 * 1024 * 1024) + 1];
		Array.Fill(oversized, (byte)' ');
		envelope.CopyTo(oversized, 0);
		FakeDisk disk = new() { CapturedBytesOverride = oversized };
		disk.AddFile(FileName, "not json");
		disk.AddBackup($"{FileName}.20260101120000000.{Guid.NewGuid():N}.bak", /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.BackupPromoted.Should().BeFalse();
		_ = disk.Operations.Should().NotContain(entry => entry.StartsWith("writebytes:", StringComparison.Ordinal));
	}

	// THE LOCK WAIT IS NOT CHARGED TO THE REPAIR BUDGET. The per-file lock is held by another
	// writer while the (fake) clock runs far past the 1.5 s repair budget, then released. The
	// budget starts at acquisition, so the compatible backup is still chosen and promoted;
	// starting it before the wait made ChooseBackup see an exhausted budget and fall back to
	// quarantine + defaults over a good backup.
	[Fact]
	public async Task Execute_LockWonLate_StillChoosesAndPromotesACompatibleBackup()
	{
		FakeDisk disk = new();
		disk.AddFile(FileName, "not json");
		disk.AddBackup($"{FileName}.20260101120000000.{Guid.NewGuid():N}.bak", /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");
		SteppedClock clock = new();
		using ManualResetEventSlim waiting = new(false);
		ConfigRepairExecutor executor = new(new ConfigFileObserver(disk), disk, disk, disk, clock, new InMemoryConfigRepairLog())
		{
			BeforeLockWait = waiting.Set,
		};

		using ManualResetEventSlim held = new(false);
		using ManualResetEventSlim release = new(false);
		Thread holder = new(() =>
		{
			using Mutex mutex = new(initiallyOwned: false, AtomicJsonFileStore.BuildMutexName(FileName));
			_ = mutex.WaitOne();
			held.Set();
			release.Wait();
			mutex.ReleaseMutex();
		});
		holder.Start();
		_ = held.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

		Task<ConfigRepairOutcome> run = Task.Run(() => executor.Execute(FileName, "backups", new AppDocument()));
		// The seam fires immediately before WaitOne, so everything Execute does before the wait
		// (including any budget start that a regression moved there) has already happened, and
		// the lock cannot be acquired until `release` below, i.e. after the clock jump.
		_ = waiting.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the executor must reach the lock wait");
		clock.Advance(TimeSpan.FromSeconds(10));
		release.Set();
		holder.Join();
		ConfigRepairOutcome outcome = await run;

		_ = outcome.BackupPromoted.Should().BeTrue("the repair budget started when the lock was acquired, not when the wait began");
		_ = outcome.DefaultsPersisted.Should().BeFalse();
	}

	// A backup that grew past the read ceiling AFTER the observer selected it is not read
	// whole and promoted, even though its (fake) bytes would still bind.
	[Fact]
	public void Execute_BackupGrewPastTheReadCeilingAfterSelection_IsNotReadOrPublished()
	{
		string backupName = $"{FileName}.20260101120000000.{Guid.NewGuid():N}.bak";
		string backupPath = Path.Combine("backups", backupName);
		FakeDisk disk = new() { GrowingPath = backupPath };
		disk.AddFile(FileName, "not json");
		disk.AddBackup(backupName, /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.BackupPromoted.Should().BeFalse();
		_ = disk.Operations.Should().NotContain("readbytes:" + backupPath);
		_ = disk.Operations.Should().NotContain(entry => entry.StartsWith("writebytes:", StringComparison.Ordinal));
	}

	// ===================== THE NON-REPAIR ROUTES =====================

	// D-085: a missing file yields created defaults, with NO quarantine — there is no
	// original to preserve, so route (a) of owner decision 1 applies unconditionally.
	[Fact]
	public void Execute_AbsentFile_PersistsDefaultsWithoutQuarantiningAnything()
	{
		FakeDisk disk = new();

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.Action.Should().Be(ConfigLoadAction.CreateDefaultsAndPersist);
		_ = outcome.Reason.Should().Be(ConfigLoadReason.FileAbsentDefaultsCreated);
		_ = outcome.DefaultsPersisted.Should().BeTrue();
		_ = outcome.OriginalPreserved.Should().BeFalse();
		_ = disk.Operations.Should().NotContain(entry => entry.StartsWith("move:", StringComparison.Ordinal));
	}

	// A sound document is used as-is: no quarantine, no write, no backup consulted.
	[Fact]
	public void Execute_SoundDocument_UsesItAndTouchesNothing()
	{
		FakeDisk disk = new();
		disk.AddFile(FileName, /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.Action.Should().Be(ConfigLoadAction.UseLoadedDocument);
		_ = outcome.DestinationUsable.Should().BeTrue();
		_ = outcome.ReadOnlyForSession.Should().BeFalse();
		_ = disk.MutatingOperations.Should().BeEmpty();
	}

	// Planner step 2: a file that exists but will not open ENDS the read path. Its
	// unreadable bytes may belong to a newer version, so no repair and no write.
	[Fact]
	public void Execute_UnreadableFile_UsesInMemoryDefaultsAndWritesNothing()
	{
		FakeDisk disk = new();
		disk.AddFile(FileName, "irrelevant");
		disk.OpenFailure = new IOException("held by another process");

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.Action.Should().Be(ConfigLoadAction.UseInMemoryDefaults);
		_ = outcome.Reason.Should().Be(ConfigLoadReason.OpenFailedDefaultsInMemoryOnly);
		_ = outcome.DefaultsPersisted.Should().BeFalse();
		_ = disk.MutatingOperations.Should().BeEmpty();
	}

	// ===================== THE BUDGET AND THE DEADLINE =====================

	// Owner decision 8: iterating candidates on refusal is the executor's retry loop and
	// it CONSUMES THE ATTEMPT BUDGET. Five refusable candidates are present; the walk
	// must stop at the D-089 count rather than reading all five.
	//
	// COUNTED THROUGH THE LOG rather than through a clock, so the fact needs no fake
	// TimeProvider and cannot go flaky on a slow machine.
	[Fact]
	public void Execute_WalkingRefusedBackupCandidates_StopsAtTheAttemptBudgetInsteadOfWalkingThemAll()
	{
		FakeDisk disk = new();
		disk.AddFile(FileName, "not json");

		for (int i = 0; i < 5; i++)
		{
			// Each candidate is itself unparseable, so none is ever promotable.
			disk.AddBackup($"{FileName}.2026010112000000{i}.{Guid.NewGuid():N}.bak", "also not json");
		}

		InMemoryConfigRepairLog log = new();
		ConfigRepairOutcome outcome = Execute(disk, log);

		_ = log.Entries.Count(entry => entry.Step == ConfigRepairStep.BackupCandidateRefused)
			.Should().Be(RepairAttemptBudget.MaxAttempts);
		_ = log.Entries.Should().Contain(entry => entry.Step == ConfigRepairStep.AttemptBudgetDenied);

		// The budget running out is not a failure: the fallback still preserves the
		// original before writing defaults.
		_ = outcome.OriginalPreserved.Should().BeTrue();
		_ = outcome.DefaultsPersisted.Should().BeTrue();
	}

	// THE STEP 0 MECHANISM, PINNED. The write-lock timeout handed to the writer is
	// derived from the REMAINING repair budget, never left at AtomicJsonFileStore's 10 s
	// default — which alone is several times the whole budget, and was measured to be the
	// dominant blocking cost on this path.
	//
	// AN UPPER BOUND ONLY, and deliberately so: a lower bound would be a load-sensitive
	// assertion on elapsed time and would make the suite flaky. What matters is that the
	// value cannot exceed the budget.
	[Fact]
	public void Execute_DerivesTheWriteLockTimeoutFromTheRemainingBudgetRatherThanTheStoreDefault()
	{
		FakeDisk disk = new();

		_ = Execute(disk);

		_ = disk.WriteLockTimeouts.Should().HaveCount(1);
		_ = disk.WriteLockTimeouts[0].Should().BePositive();
		_ = disk.WriteLockTimeouts[0].Should().BeLessThanOrEqualTo(RepairAttemptBudget.DefaultTimeBudget);
		_ = disk.WriteLockTimeouts[0].Should().BeLessThan(TimeSpan.FromSeconds(10));
	}

	// ===================== FAIL OPEN (owner decision 10) =====================

	// Owner decision 10 / D-v3.5-083: a failure to repair leaves the app usable on
	// in-memory defaults and NEVER crashes startup. A throwing writer is the realistic
	// shape — the destination could not be published.
	[Fact]
	public void Execute_WhenTheWriteFails_FailsOpenInsteadOfCrashingStartup()
	{
		FakeDisk disk = new() { WriteFailure = new IOException("the volume disappeared") };
		disk.AddFile(FileName, "not json");

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.OriginalPreserved.Should().BeTrue();
		_ = outcome.DefaultsPersisted.Should().BeFalse();
		_ = outcome.DestinationUsable.Should().BeFalse();
	}

	// The fail-open guarantee must survive a BROKEN COLLABORATOR too, not merely an
	// ordinary filesystem state. A seam throwing a type outside the file vocabulary is a
	// defect, and a defect on the picker's interactive path still must not take the app
	// down.
	[Fact]
	public void Execute_WhenASeamThrowsAnUnexpectedType_StillFailsOpen()
	{
		FakeDisk disk = new() { MoveFailure = new InvalidTimeZoneException("a broken seam") };
		disk.AddFile(FileName, "not json");

		ConfigRepairOutcome outcome = Execute(disk);

		_ = outcome.DestinationUsable.Should().BeFalse();
		_ = outcome.DefaultsPersisted.Should().BeFalse();
	}

	// A caller bug is the ONE thing that is not converted: it is fixable at the call
	// site and has touched nothing, exactly as on the read side.
	[Fact]
	public void Execute_NullArguments_AreRejectedAsCallerBugsRatherThanConvertedIntoOutcomes()
	{
		FakeDisk disk = new();
		ConfigRepairExecutor executor = Build(disk, new InMemoryConfigRepairLog());

		_ = Assert.Throws<ArgumentNullException>(() => executor.Execute(null!, "backups", new AppDocument()));
		_ = Assert.Throws<ArgumentNullException>(() => executor.Execute(FileName, null!, new AppDocument()));
		_ = Assert.Throws<ArgumentNullException>(() => executor.Execute<AppDocument>(FileName, "backups", null!));
	}

	// ===================== REDACTION (owner decision 9) =====================

	// D-v3.5-075 / D-v3.5-097: no full user file path may reach a log, because an
	// installed-mode path embeds the Windows ACCOUNT NAME. And a framework exception is
	// never rendered — DirectoryNotFoundException embeds the path in its own message, and
	// an InnerException chain carries it too.
	//
	// ASSERTED AGAINST A PATH WITH A DISTINCTIVE DIRECTORY SEGMENT, so a leak is caught
	// wherever in the event it appeared rather than only in the field this fact expects.
	[Fact]
	public void Execute_RepairLog_RecordsTheBareFileNameAndATypeName_NeverAPathOrARenderedException()
	{
		// An INSTALLED-MODE shaped path, because that is the shape that embeds the account
		// name. The file must be seeded at this same path, not at the bare name: the
		// executor asks the seam about the path it was given, so seeding the bare name
		// would send this down the Absent route and the quarantine failure asserted below
		// would never happen.
		const string InstalledPath = @"C:\Users\a-distinctive-account\AppData\Local\BroChoos\app.json";

		FakeDisk disk = new() { MoveFailure = new DirectoryNotFoundException(InstalledPath) };
		disk.AddFile(InstalledPath, "not json");

		InMemoryConfigRepairLog log = new();
		_ = Execute(disk, log, destinationPath: InstalledPath);

		_ = log.Entries.Should().NotBeEmpty();

		foreach (ConfigRepairEvent entry in log.Entries)
		{
			_ = entry.FileName.Should().Be(FileName);
			_ = entry.FileName.Should().NotContain("a-distinctive-account");
			_ = entry.FileName.Should().NotContainAny("\\", "/");

			// The TYPE name, never the message. The message of this very exception is a
			// full path, so a rendered exception would fail this outright.
			_ = (entry.FailureType ?? string.Empty).Should().NotContain("a-distinctive-account");
			_ = (entry.FailureType ?? string.Empty).Should().NotContainAny("\\", "/", ":");
		}

		_ = log.Entries.Should().Contain(entry =>
			entry.Step == ConfigRepairStep.QuarantineFailed
			&& entry.FailureType == nameof(DirectoryNotFoundException));
	}

	// ===================== PER-FILE INDEPENDENCE (D-071) =====================

	// D-071 makes atomicity PER FILE and refuses a trio-wide transaction, so a
	// partially-good result is legitimate: one corrupt file being repaired must not
	// change what happens to a sound sibling. There is deliberately no trio-wide entry
	// point to test — the independence IS the absence of one.
	[Fact]
	public void Execute_PerFile_GivesEachFileItsOwnOutcomeSoAPartiallyGoodResultIsLegitimate()
	{
		FakeDisk disk = new();
		disk.AddFile("app.json", /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");
		disk.AddFile("settings.json", "not json");

		ConfigRepairExecutor executor = Build(disk, new InMemoryConfigRepairLog());

		ConfigRepairOutcome app = executor.Execute("app.json", "backups", new AppDocument());
		ConfigRepairOutcome settings = executor.Execute("settings.json", "backups", new SettingsDocument());

		_ = app.Action.Should().Be(ConfigLoadAction.UseLoadedDocument);
		_ = app.OriginalPreserved.Should().BeFalse();

		_ = settings.Action.Should().Be(ConfigLoadAction.CreateDefaultsAndPersist);
		_ = settings.OriginalPreserved.Should().BeTrue();

		// app.json was never touched by settings.json's repair.
		_ = disk.ReadText("app.json").Should().Be(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");
	}

	// ============ THE STRUCTURAL PROPERTY BEHIND OWNER DECISION 1 ============

	// THE TYPE HEADER'S LOAD-BEARING CLAIM, MADE MACHINE-CHECKABLE. ConfigRepairExecutor's
	// header states that no ConfigLoadDecision can reach the acting code from outside:
	// Perform is private, and Execute calls ConfigLoadPlanner.Plan ITSELF instead of
	// accepting a decision, so this type's externally visible surface neither takes a
	// decision nor hands one out. Until this fact existed that was a true sentence no
	// compiler checked, which is the defect shape this slice keeps producing.
	//
	// WHY THE ROUTE AND NOT THE CAPABILITY TOKENS. The tokens were MEASURED not to be an
	// independent second layer: TryClear is public on PersistClearance, RepairClearance and
	// BackupPromotionClearance, and every ConfigFileObservation factory is public static, so
	// a caller holding Core's internals can mint genuine tokens and forge every mutating
	// decision. What actually keeps a forged or stale decision away from Perform is that
	// there is no route in. So the route is what is pinned here.
	//
	// BindingFlags.DeclaredOnly IS DELIBERATELY NOT SET, and the trap it avoids is real:
	// GetMembers on a CLASS returns inherited members (unlike on an interface, which returns
	// none of its base interfaces'). Inherited members are exactly as callable as declared
	// ones, so they belong in a claim about what the surface ACCEPTS — and were a base class
	// ever introduced between this type and object, DeclaredOnly would turn its members into
	// a blind spot while callers could still reach them. The cost of including them today is
	// object's own members, none of which mentions a ConfigLoadDecision.
	//
	// PRIVATE AND INTERNAL MEMBERS ARE OUT OF SCOPE BY DESIGN, not by oversight: Perform
	// TAKES a decision and must, and ChooseBackup returns a tuple containing one. Those are
	// the mechanism, not a leak.
	//
	// WHAT THIS FACT PROVES, AND WHAT IT CANNOT — read this before treating it as the whole
	// of the route property. It reads SIGNATURES, and it follows only STRUCTURAL TYPE
	// COMPOSITION: a decision named outright, or reached through an array, a by-ref, a
	// pointer or a generic argument, to any depth. A visible member whose signature mentions
	// a plain non-generic wrapper that merely HOLDS a ConfigLoadDecision in a field passes
	// this fact untouched, and so would one taking object.
	//
	// THAT GAP IS LEFT OPEN DELIBERATELY, and closing it here would be the wrong repair.
	// Following what a signature type can hold means walking field graphs, which asks what
	// can FLOW out of the type — a call-graph question wearing a signature-shape costume —
	// and it would turn this fact into a miniature analyzer with its own false positives to
	// keep. The wrapper and object routes are closed BY HAND in ConfigRepairExecutor's
	// header, and recorded there as hand-closed precisely because nothing reports their
	// return.
	[Fact]
	public void ConfigRepairExecutor_ExternallyVisibleSurface_NeitherAcceptsNorReturnsAConfigLoadDecision()
	{
		// typeof, not a string lookup, so a typo in the type name cannot compile at all —
		// the strongest available form of "the type was found".
		Type decision = typeof(ConfigLoadDecision);

		MemberInfo[] members = typeof(ConfigRepairExecutor).GetMembers(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

		// POSITIVE CONTROL, AND IT GUARDS THE REAL VACUITY RISK. A broken mentions-predicate
		// would report no offenders and the assertion below would pass while proving nothing.
		// The same predicate run over the NON-visible surface must still find the members that
		// legitimately traffic in decisions, which is what shows the detector detects.
		List<string> mechanism = [.. members
			.Where(member => !IsExternallyVisible(member))
			.Select(member => DescribeDecisionMentions(member, decision))
			.OfType<string>()];

		_ = mechanism.Should().NotBeEmpty(
			"the private mechanism does traffic in ConfigLoadDecision, so finding none of it "
			+ "would mean this fact's own detector is broken and the assertion below is vacuous. "
			+ "WHAT THIS CONTROL CATCHES IS WHOLESALE DETECTOR DEATH AND ONLY THAT, measured "
			+ "rather than assumed: every private member it counts carries a decision as a DIRECT "
			+ "parameter, so the count is unchanged whether the composition walk in Mentions lives "
			+ "or dies, and emptying that walk leaves this green. An earlier version of this "
			+ "sentence offered ChooseBackup's decision-carrying tuple as evidence the walk works; "
			+ "that member is detected on its direct parameter alone, so the tuple proves nothing "
			+ "here. The walk is exercised by "
			+ nameof(DecisionMentionDetector_FindsDecisionsReachableOnlyThroughComposition)
			+ " instead, against a fixture that mentions a decision by composition ONLY");

		List<string> offenders = [.. members
			.Where(IsExternallyVisible)
			.Select(member => DescribeDecisionMentions(member, decision))
			.OfType<string>()];

		_ = offenders.Should().BeEmpty(
			$"ConfigRepairExecutor's public and protected surface must neither accept nor return "
			+ $"a ConfigLoadDecision, and these members do: {string.Join("; ", offenders)}. A "
			+ $"decision arriving from OUTSIDE bypasses Execute's own ConfigLoadPlanner.Plan "
			+ $"call, which is the only thing that keeps a forged or stale decision away from the "
			+ $"private Perform — and Perform is where bytes move, so this is owner decision 1's "
			+ $"ordering invariant, not a style rule. Make the member private or internal; if the "
			+ $"surface genuinely must change, ConfigRepairExecutor's type header claims this "
			+ $"property and must stop claiming it");
	}

	// THE DETECTOR'S COMPOSITION WALK, MADE OBSERVABLE. The control in the fact above proves
	// only that the detector is not wholly dead, and that limit was MEASURED rather than
	// reasoned about: emptying Mentions down to `candidate == decision` left the whole suite
	// green, because every private member that control counts also carries a decision as a
	// DIRECT parameter. The walk was structurally present and exercised by nothing.
	//
	// A FIXTURE, NOT A REAL MEMBER, and the choice is the point. Keying this on ChooseBackup's
	// decision-carrying tuple would reproduce the decay being removed here: refactor that
	// return to a named type and the fact stops testing the walk SILENTLY, still green. The
	// fixture below belongs to this fact, so nothing can be changed out from under it. It also
	// closes the array and by-ref arms, which no other fact reaches at all.
	//
	// EACH ASSERTION IS ITS OWN ARM, so a failure names which arm died rather than reporting
	// that something somewhere is broken.
	[Fact]
	public void DecisionMentionDetector_FindsDecisionsReachableOnlyThroughComposition()
	{
		Type decision = typeof(ConfigLoadDecision);
		Type fixture = typeof(DecisionCompositionFixture);

		// nameof, so renaming a fixture member is a COMPILE error rather than a null lookup
		// that would quietly turn these assertions into assertions about nothing.
		MethodInfo? throughTuple = fixture.GetMethod(
			nameof(DecisionCompositionFixture.ReturnsTupleContainingDecision));
		PropertyInfo? throughArray = fixture.GetProperty(
			nameof(DecisionCompositionFixture.ArrayOfDecisions));
		MethodInfo? throughByRef = fixture.GetMethod(
			nameof(DecisionCompositionFixture.TakesDecisionByRef));

		_ = throughTuple.Should().NotBeNull("the fixture member must be reflectable at all");
		_ = throughArray.Should().NotBeNull("the fixture member must be reflectable at all");
		_ = throughByRef.Should().NotBeNull("the fixture member must be reflectable at all");

		// THE GENERIC-ARGUMENT ARM. The return type is ValueTuple<ConfigLoadDecision, int>,
		// which does not equal ConfigLoadDecision and has no element type, so this is found
		// only by descending into generic arguments.
		_ = DescribeDecisionMentions(throughTuple!, decision).Should().NotBeNull(
			"a ConfigLoadDecision reached only through a generic argument must be detected: "
			+ "the IsGenericType arm of Mentions is what finds it, and with that arm gone this "
			+ "member looks clean. That arm is the reason a visible member returning a tuple, a "
			+ "list or a task of decisions cannot slip past the surface fact");

		// THE ELEMENT-TYPE ARM, ARRAY FLAVOUR. ConfigLoadDecision[] is neither the decision
		// nor a generic type; only HasElementType reaches through it.
		_ = DescribeDecisionMentions(throughArray!, decision).Should().NotBeNull(
			"a ConfigLoadDecision reached only through an array must be detected: the "
			+ "HasElementType arm of Mentions is what finds it, and with that arm gone a visible "
			+ "member trafficking in arrays of decisions looks clean");

		// THE ELEMENT-TYPE ARM, BY-REF FLAVOUR. The parameter's type is ConfigLoadDecision&,
		// a distinct Type object that likewise fails the equality check.
		_ = DescribeDecisionMentions(throughByRef!, decision).Should().NotBeNull(
			"a ConfigLoadDecision reached only through a by-ref parameter must be detected: a "
			+ "ref or out parameter's ParameterType is ConfigLoadDecision&, which does NOT equal "
			+ "typeof(ConfigLoadDecision), so Mentions must UNWRAP ConfigLoadDecision& to "
			+ "ConfigLoadDecision. A failure here means it no longer does");
	}

	// ===================== helpers =====================

	// THE FIXTURE FOR THE FACT ABOVE. Its defining property: every member mentions
	// ConfigLoadDecision ONLY THROUGH COMPOSITION, never as a bare parameter or return type.
	// That is what makes each member invisible unless the matching arm of Mentions is alive,
	// and it is the property to preserve if these members are ever edited — a direct
	// ConfigLoadDecision anywhere in here would mask the very walk this fixture exists to
	// exercise, which is exactly how the real members came to mask it.
	//
	// NEVER INVOKED. These exist to be reflected over, so the bodies are unreachable by
	// design rather than unfinished.
	private static class DecisionCompositionFixture
	{
		public static ConfigLoadDecision[] ArrayOfDecisions => [];

		public static (ConfigLoadDecision Decision, int Attempt) ReturnsTupleContainingDecision()
		{
			throw new NotSupportedException("reflected over, never called");
		}

		public static void TakesDecisionByRef(ref ConfigLoadDecision decision)
		{
			throw new NotSupportedException("reflected over, never called");
		}
	}

	// Reachable from outside this assembly THROUGH THE TYPE: public, or protected on a type
	// someone could derive from. Internal is excluded deliberately — it cannot be reached by
	// a forger outside the assembly, which is the threat this fact is about.
	private static bool IsExternallyVisible(MemberInfo member)
	{
		return member switch
		{
			MethodBase method => IsExternallyVisible(method),
			PropertyInfo property => property.GetAccessors(nonPublic: true).Any(IsExternallyVisible),
			FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
			EventInfo @event =>
				IsExternallyVisible(@event.AddMethod) || IsExternallyVisible(@event.RemoveMethod),
			Type nested => nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem,
			_ => false
		};
	}

	private static bool IsExternallyVisible(MethodBase? method)
	{
		return method is not null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);
	}

	// Names the member AND where the decision appears, because a bare count is useless to
	// whoever has to fix it.
	private static string? DescribeDecisionMentions(MemberInfo member, Type decision)
	{
		List<string> places = [];

		switch (member)
		{
			case MethodInfo method:
				if (Mentions(method.ReturnType, decision))
				{
					places.Add($"returns {method.ReturnType.Name}");
				}

				AddParameterMentions(method.GetParameters(), decision, places);
				break;

			case ConstructorInfo constructor:
				AddParameterMentions(constructor.GetParameters(), decision, places);
				break;

			case PropertyInfo property:
				if (Mentions(property.PropertyType, decision))
				{
					places.Add($"property type {property.PropertyType.Name}");
				}

				AddParameterMentions(property.GetIndexParameters(), decision, places);
				break;

			case FieldInfo field:
				if (Mentions(field.FieldType, decision))
				{
					places.Add($"field type {field.FieldType.Name}");
				}

				break;

			default:
				break;
		}

		return places.Count == 0
			? null
			: $"{member.MemberType} {member.DeclaringType?.Name}.{member.Name} ({string.Join(" and ", places)})";
	}

	private static void AddParameterMentions(ParameterInfo[] parameters, Type decision, List<string> places)
	{
		foreach (ParameterInfo parameter in parameters.Where(parameter => Mentions(parameter.ParameterType, decision)))
		{
			places.Add($"parameter '{parameter.Name}' of type {parameter.ParameterType.Name}");
		}
	}

	// A decision MENTIONED anywhere in a signature type counts, not only one named outright:
	// ChooseBackup already returns a tuple containing a ConfigLoadDecision, so an equality
	// check against typeof(ConfigLoadDecision) would miss that exact shape if it were ever
	// made visible. Covers arrays, by-ref, pointers and every generic argument.
	private static bool Mentions(Type? candidate, Type decision)
	{
		return candidate is not null
			&& (candidate == decision
			|| (candidate.HasElementType
			? Mentions(candidate.GetElementType(), decision)
			: candidate.IsGenericType
			&& candidate.GetGenericArguments().Any(argument => Mentions(argument, decision))));
	}

	// A monotonic clock that only moves when the test says so (milliseconds as ticks).
	private sealed class SteppedClock : TimeProvider
	{
		private long _milliseconds;

		public override long TimestampFrequency => 1000;

		public override long GetTimestamp()
		{
			return Interlocked.Read(ref _milliseconds);
		}

		public void Advance(TimeSpan by)
		{
			_ = Interlocked.Add(ref _milliseconds, (long)by.TotalMilliseconds);
		}
	}

	private static ConfigRepairExecutor Build(FakeDisk disk, IConfigRepairLog log)
	{
		return new(new ConfigFileObserver(disk), disk, disk, disk, TimeProvider.System, log);
	}

	private static ConfigRepairOutcome Execute(
		FakeDisk disk,
		IConfigRepairLog? log = null,
		string destinationPath = FileName)
	{
		return Build(disk, log ?? new InMemoryConfigRepairLog())
			.Execute(destinationPath, "backups", new AppDocument());
	}

	private string CreateTemporaryDirectory()
	{
		string path = Path.Combine(Path.GetTempPath(), "brochoos-exec-" + Guid.NewGuid().ToString("N")[..12]);
		_ = Directory.CreateDirectory(path);
		_temporaryDirectories.Add(path);
		return path;
	}

	// ONE fake standing in for all four seams, so every operation lands in ONE ordered
	// list. That is what makes the ordering invariant assertable: with separate fakes the
	// relative order of a move and a write could not be observed at all.
	//
	// Paths are treated as opaque ordinal keys, case-sensitively. This fake is not
	// modelling NTFS — it is recording the sequence of operations the executor performs.
	private sealed class FakeDisk : IStorageReadFileSystem, IStorageRepairFileSystem, IStorageFileSystem, IConfigDocumentWriter
	{
		private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

		private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "backups" };

		public List<string> Operations { get; } = [];

		public List<TimeSpan> WriteLockTimeouts { get; } = [];

		public Exception? MoveFailure { get; init; }

		public Exception? WriteFailure { get; init; }

		public Exception? OpenFailure { get; set; }

		// Every operation that changes a byte. The hard-stop facts assert this is EMPTY,
		// which is a stronger statement than any per-operation check.
		public IEnumerable<string> MutatingOperations =>
			Operations.Where(entry =>
				entry.StartsWith("move:", StringComparison.Ordinal)
				|| entry.StartsWith("copy:", StringComparison.Ordinal)
				|| entry.StartsWith("writebytes:", StringComparison.Ordinal)
				|| entry.StartsWith("write:", StringComparison.Ordinal));

		public void AddFile(string path, string content)
		{
			_files[path] = Encoding.UTF8.GetBytes(content);
		}

		public void AddBackup(string name, string content)
		{
			_files[Path.Combine("backups", name)] = Encoding.UTF8.GetBytes(content);
		}

		// When set, ReadAllBytes (the promotion capture) returns these bytes instead of the file's.
		public byte[]? CapturedBytesOverride { get; init; }

		public void AddBackupBytes(string name, byte[] content)
		{
			_files[Path.Combine("backups", name)] = content;
		}

		public byte[] GetBytes(string path)
		{
			return _files[path];
		}

		public string ReadText(string path)
		{
			return Encoding.UTF8.GetString(_files[path]);
		}

		public bool FileExists(string path)
		{
			return _files.ContainsKey(path);
		}

		public bool DirectoryExists(string path)
		{
			return _directories.Contains(path);
		}

		public Stream OpenRead(string path)
		{
			Operations.Add("read:" + path);

			return OpenFailure is not null ? throw OpenFailure : (Stream)new MemoryStream(_files[path], writable: false);
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			// NON-RECURSIVE, matching the seam's contract: only entries whose directory is
			// exactly this one, so a file under backups\corrupted is never listed.
			return [.. _files.Keys.Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.Ordinal))];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return _files.ContainsKey(path) ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		}

		public long GetLength(string path)
		{
			if (GrowingPath is not null && string.Equals(path, GrowingPath, StringComparison.Ordinal) && ++_growingQueries > 1)
			{
				// The file grew past the read ceiling after the observer selected it.
				return 9L * 1024 * 1024;
			}

			return _files.TryGetValue(path, out byte[]? bytes) ? bytes.Length : throw new FileNotFoundException();
		}

		private int _growingQueries;

		// A path whose FIRST length query reports its real size and every later one reports
		// more than the 8 MiB read ceiling.
		public string? GrowingPath { get; init; }

		public void CreateDirectory(string path)
		{
			Operations.Add("mkdir:" + path);
			_ = _directories.Add(path);
		}

		public void Move(string sourcePath, string destinationPath)
		{
			Operations.Add($"move:{sourcePath}->{destinationPath}");

			if (MoveFailure is not null)
			{
				throw MoveFailure;
			}

			if (_files.ContainsKey(destinationPath))
			{
				// Matching File.Move's two-argument form, which refuses to overwrite.
				throw new IOException("the destination already exists");
			}

			_files[destinationPath] = _files[sourcePath];
			_ = _files.Remove(sourcePath);
		}

		public byte[] ReadAllBytes(string sourcePath)
		{
			Operations.Add("readbytes:" + sourcePath);
			return [.. CapturedBytesOverride ?? _files[sourcePath]];
		}

		public void WriteBytesAtomically(
			string destinationPath,
			byte[] bytes,
			ExpectedDocumentGeneration expectedGeneration,
			TimeSpan writeLockTimeout)
		{
			Operations.Add("writebytes:" + destinationPath);
			_ = expectedGeneration.ExpectsDocument.Should().BeFalse(
				"promotion writes after quarantine, so destination is absent and CAS must use the explicit absent-document token");
			_files[destinationPath] = [.. bytes];
		}

		public void Write(string destinationPath, ConfigDocument document, TimeSpan writeLockTimeout)
		{
			Operations.Add("write:" + destinationPath);
			WriteLockTimeouts.Add(writeLockTimeout);

			if (WriteFailure is not null)
			{
				throw WriteFailure;
			}

			_files[destinationPath] = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");
		}
	}
}
