using System.Text;
using System.Text.Json;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

// D-v3.5-493 kill-test seam. These tests pin the OBSERVER CONTRACT the later
// kill-process harness will be built on: which stages fire, in what order, and which
// stage is conditional. The harness parks a killer at a named checkpoint, so a stage
// that fires in the wrong order — or fires on a path where it logically did not happen,
// or silently stops firing — does not produce a failing harness. It produces a HANGING
// one, or worse a harness that reports success having never reached its checkpoint.
// That is why the order and the conditional stage are asserted explicitly here rather
// than left to the harness slice to discover.
//
// Each test owns a unique subdir under %TEMP% and deletes it in a finally, matching
// AtomicJsonFileStoreTests, so nothing leaks onto the developer's machine.
public sealed class AtomicWriteObserverTests
{
	private static readonly JsonSerializerOptions ReadOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	// Mirrors AtomicJsonFileStore's write-affecting options, like AtomicJsonFileStoreTests,
	// so a test can compute the canonical on-disk bytes to compare against.
	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	// The full sequence, on the branch that reaches every stage.
	[Fact]
	public void ReplacingWrite_NotifiesEverySevenStagesInOrder()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");

			// Write 1 establishes the destination through a store with NO observer, so
			// the recording below captures exactly ONE replacing write and nothing else.
			new AtomicJsonFileStore(backupsDir).Write(path, new BrowsersDocument([CreateBrowser("rev-1")]));

			RecordingObserver observer = new();
			AtomicJsonFileStore store = new(backupsDir, writeLockTimeout: null, observer: observer);

			store.Write(path, new BrowsersDocument([CreateBrowser("rev-2")]));

			// Should().Equal asserts ORDER, not just membership — the whole point. A
			// BeEquivalentTo here would pass on a shuffled sequence and leave the
			// harness's checkpoint ordering unpinned.
			_ = observer.Stages.Should().Equal(
				[
					AtomicWriteStage.Validated,
					AtomicWriteStage.TempFlushed,
					AtomicWriteStage.LockAcquired,
					AtomicWriteStage.BeforePublish,
					AtomicWriteStage.AfterPublish,
					AtomicWriteStage.AfterPrune,
					AtomicWriteStage.AfterVerify,
				],
				"a replacing write reaches every stage exactly once, in publish order; the "
				+ "kill harness pauses at these checkpoints, so a reordering silently moves "
				+ "where it kills the writer and invalidates the atomicity it proves");

			// The observer must OBSERVE, never alter. If injecting one changed the
			// outcome, every harness result would be measuring the harness.
			_ = Resolve<BrowsersDocument>(path).Browsers[0].BrowserId.Should().Be(
				"rev-2",
				"the observer is passive: injecting one must not change what gets published");
			_ = Directory.GetFiles(backupsDir).Should().ContainSingle(
				"the replacing write must still mint its backup with an observer attached");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// WHY THIS TEST EARNS ITS KEEP: AfterPrune is CONDITIONAL. PruneBackups is called
	// only inside the destination-exists branch, so a first-create write never reaches
	// that stage — six stages fire, not seven.
	//
	// A harness that assumed a fixed seven-stage sequence would, on a first-create
	// write, PARK FOREVER on a checkpoint that never fires: the writer has already
	// finished and exited, and the harness is still waiting to be told retention ran.
	// That is a hang, not an assertion failure, so it would burn a CI slot rather than
	// report a cause. Pinning the count and the absence here is what tells the harness
	// author the sequence is branch-dependent before they write the wait.
	//
	// The asymmetry is INTENDED and is not to be "fixed" by calling PruneBackups (or
	// firing AfterPrune) on the first-create path: there is no backup on that path to
	// prune, and D-063 step 5 mints one only when a previous revision was displaced.
	[Fact]
	public void FirstCreateWrite_NotifiesSixStagesInOrder_WithAfterPruneAbsent()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");
			RecordingObserver observer = new();

			// A backups directory IS configured and provisioned, so the missing
			// AfterPrune is attributable to the first-create branch alone and not to
			// backups being switched off.
			AtomicJsonFileStore store = new(backupsDir, writeLockTimeout: null, observer: observer);

			_ = File.Exists(path).Should().BeFalse("this test is about the first-create branch");

			store.Write(path, new BrowsersDocument([CreateBrowser("only-revision")]));

			_ = observer.Stages.Should().Equal(
				[
					AtomicWriteStage.Validated,
					AtomicWriteStage.TempFlushed,
					AtomicWriteStage.LockAcquired,
					AtomicWriteStage.BeforePublish,
					AtomicWriteStage.AfterPublish,
					AtomicWriteStage.AfterVerify,
				],
				"a first-create write takes the File.Move branch, which runs no retention");

			_ = observer.Stages.Should().NotContain(
				AtomicWriteStage.AfterPrune,
				"AfterPrune must stay conditional: a harness waiting on it unconditionally "
				+ "would hang forever on a first-create write instead of failing");

			_ = Directory.GetFiles(backupsDir).Should().BeEmpty(
				"D-063: a first-create write displaces no revision, so it mints no backup — "
				+ "which is exactly why there is nothing to prune");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// The seam must be INVISIBLE to every existing caller. The observer parameter is
	// optional and last, so all pre-seam call sites still compile and still behave
	// identically — this test exercises the two-argument and zero-argument forms.
	//
	// The never-injected recorder below is deliberately a WEAK assertion and is not
	// what this test rests on: an observer the store was never handed obviously cannot
	// be called. The real proof is that the write SUCCEEDS. Drop the
	// `?? NullAtomicWriteObserver.Instance` in the constructor and _observer stays
	// null, so the first OnStage call on the write path throws
	// NullReferenceException — this test is what catches that.
	[Fact]
	public void Write_WithNoObserverArgument_BehavesExactlyAsBefore()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");

			// An observer that exists but is never passed in. Nothing may reach it.
			RecordingObserver neverInjected = new();

			BrowsersDocument second = new([CreateBrowser("rev-2")]);

			// Zero-argument form (no backups), then the two-argument form, covering both
			// the first-create and the replacing branch without naming the parameter.
			new AtomicJsonFileStore().Write(
				Path.Combine(dataDir, "app.json"),
				new AppDocument());

			AtomicJsonFileStore store = new(backupsDir);
			store.Write(path, new BrowsersDocument([CreateBrowser("rev-1")]));
			store.Write(path, second);

			// Byte-exact, so "succeeded" means the real thing and not merely "did not throw".
			byte[] expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
				.GetBytes(JsonSerializer.Serialize(second, WriteOptions));
			_ = File.ReadAllBytes(path).SequenceEqual(expected).Should().BeTrue(
				"omitting the observer must leave the published bytes exactly as they were "
				+ "before the seam existed");

			_ = Directory.GetFiles(backupsDir).Should().ContainSingle(
				"backups behaviour is unchanged by the seam");
			_ = Directory.GetFiles(dataDir, "*.tmp").Should().BeEmpty(
				"temp cleanup is unchanged by the seam");

			_ = neverInjected.Stages.Should().BeEmpty(
				"nothing may call an observer the store was never given");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// The no-try/catch decision, pinned. An observer that throws must ESCAPE Write.
	//
	// Verified against the real classification code rather than assumed: the throw
	// happens at AfterPublish, which sits AFTER `published = true`, so
	// Classify(failure, destinationPath, published) stamps destinationModified: true.
	// That is the honest answer — File.Replace HAS committed by then and the destination
	// really does hold the new revision, which this test also confirms on disk.
	//
	// Catches two regressions at once:
	//  - wrapping the observer calls in try/catch, which would swallow the throw and
	//    return successfully. That looks harmless and would silently disarm the kill
	//    harness: its checkpoint exception would vanish and it would report success
	//    having never paused;
	//  - moving OnStage(AfterPublish) to BEFORE `published = true`, which would report
	//    destinationModified: false about a destination that was demonstrably replaced,
	//    inverting the D-063 step 9 (A) classification on a committed publish.
	[Fact]
	public void ThrowingObserverAtAfterPublish_EscapesWrite_AndReportsDestinationModified()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");

			new AtomicJsonFileStore(backupsDir).Write(path, new BrowsersDocument([CreateBrowser("rev-1")]));

			ThrowingObserver observer = new(AtomicWriteStage.AfterPublish);
			AtomicJsonFileStore store = new(backupsDir, writeLockTimeout: null, observer: observer);
			BrowsersDocument published = new([CreateBrowser("rev-2")]);

			Action write = () => store.Write(path, published);

			InvalidOperationException failed = write.Should().Throw<InvalidOperationException>(
				"observer calls are deliberately NOT wrapped in try/catch: a throwing "
				+ "observer is a broken implementation and must be visible, never swallowed")
				.Which;

			_ = failed.Data["destinationModified"].Should().Be(
				true,
				"the throw happens after File.Replace committed and after `published = true`, "
				+ "so the destination WAS modified and the caller must be told so");
			_ = failed.Data["path"].Should().Be(
				path,
				"the full path rides in Data for a redaction-aware sink, as with every other "
				+ "write failure");

			// The claim above is checked against the disk, not just against Data.
			byte[] expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
				.GetBytes(JsonSerializer.Serialize(published, WriteOptions));
			_ = File.ReadAllBytes(path).SequenceEqual(expected).Should().BeTrue(
				"destinationModified: true must be the truth: the swap committed before the "
				+ "observer threw, so the new revision really is on disk");

			// The write lock is released through the `acquired`-guarded finally even on
			// this path, so a later writer must not hit an abandoned mutex.
			Action later = () => new AtomicJsonFileStore(backupsDir).Write(
				path,
				new BrowsersDocument([CreateBrowser("rev-3")]));
			_ = later.Should().NotThrow(
				"an escaping observer failure must still release the write lock");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// Pins WHERE OnStage(LockAcquired) sits relative to the `if (!acquired) throw`
	// guard — otherwise MUTATION-INVISIBLE, because every other test in this file
	// acquires the lock successfully and would stay green with the call moved up.
	//
	// What the mutation costs: fired merely after WaitOne returns, LockAcquired would
	// also announce a TIMED-OUT acquisition — a lock this thread does not hold. A kill
	// harness would then release its killer against a writer that is already failing
	// and has published nothing, and would attribute whatever it saw to a publish that
	// never started.
	//
	// Injection is the same one the lock tests already use — a real external holder on
	// another thread plus a short timeout — so this is the genuine failure path, not a
	// simulation.
	[Fact]
	public void TimedOutAcquisition_DoesNotNotifyLockAcquired()
	{
		string dir = NewTempDirectory();
		string path = Path.Combine(dir, "browsers.json");
		using ManualResetEventSlim held = new(initialState: false);
		using ManualResetEventSlim release = new(initialState: false);
		Exception? holderFailure = null;

		// A Win32 mutex is reentrant for its owning thread, so the hold must live on
		// another thread or Write would simply re-acquire it and never time out.
		Thread holder = new(() =>
		{
			try
			{
				using Mutex external = new(initiallyOwned: false, AtomicJsonFileStore.BuildMutexName(path));

				if (!external.WaitOne(TimeSpan.FromSeconds(5)))
				{
					throw new InvalidOperationException("the test could not take the lock it intends to hold");
				}

				held.Set();
				_ = release.Wait(TimeSpan.FromSeconds(30));
				external.ReleaseMutex();
			}
			catch (Exception failure)
			{
				holderFailure = failure;
				held.Set();
			}
		})
		{ IsBackground = true };

		try
		{
			holder.Start();
			_ = held.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the holder thread must reach the hold");
			_ = holderFailure.Should().BeNull();

			RecordingObserver observer = new();
			AtomicJsonFileStore store = new(
				backupsDirectory: null,
				writeLockTimeout: TimeSpan.FromMilliseconds(100),
				observer: observer);

			Action blocked = () => store.Write(path, new BrowsersDocument([CreateBrowser("never-published")]));

			_ = blocked.Should().Throw<IOException>().WithMessage("*config-write lock*");

			_ = observer.Stages.Should().NotContain(
				AtomicWriteStage.LockAcquired,
				"the wait TIMED OUT, so no lock was acquired: OnStage(LockAcquired) must sit "
				+ "after the !acquired guard, never merely after WaitOne returns");

			// The stages before the lock still fire — this is a genuine walk down the
			// write path that failed at the lock, not an early abort somewhere else.
			_ = observer.Stages.Should().Equal(
				[
					AtomicWriteStage.Validated,
					AtomicWriteStage.TempFlushed,
				],
				"validation and the temp write happen OUTSIDE the lock, so both must have "
				+ "been announced before the acquisition failed");
		}
		finally
		{
			release.Set();
			_ = holder.Join(TimeSpan.FromSeconds(5));
			Directory.Delete(dir, recursive: true);
		}
	}

	// Pins that AfterPrune is conditional on the BRANCH ALONE and never on whether a
	// backups directory is configured — otherwise MUTATION-INVISIBLE. Guarding the
	// AfterPrune call on `_backupsDirectory is not null` leaves every other test in this
	// file green: the replacing test configures a backups directory, and the
	// first-create test expects AfterPrune absent anyway.
	//
	// The enum member promises AfterPrune fires on every REPLACING write regardless,
	// which is honest because PruneBackups early-returns internally when there is no
	// backups directory — retention "ran" in the only sense the stage claims. A harness
	// parking on AfterPrune against a no-backups store would, under the mutation, wait
	// for a stage that never arrives from a writer that has already exited: a HANG that
	// burns a CI slot without naming a cause, not an assertion failure.
	[Fact]
	public void ReplacingWrite_WithNullBackupsDirectory_StillFiresSevenStages()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "browsers.json");
			new AtomicJsonFileStore().Write(path, new BrowsersDocument([CreateBrowser("rev-1")]));

			RecordingObserver observer = new();
			new AtomicJsonFileStore(backupsDirectory: null, writeLockTimeout: null, observer: observer)
				.Write(path, new BrowsersDocument([CreateBrowser("rev-2")]));

			_ = observer.Stages.Should().Equal(
				[
					AtomicWriteStage.Validated, AtomicWriteStage.TempFlushed,
					AtomicWriteStage.LockAcquired, AtomicWriteStage.BeforePublish,
					AtomicWriteStage.AfterPublish, AtomicWriteStage.AfterPrune,
					AtomicWriteStage.AfterVerify,
				],
				"AfterPrune is conditional on the BRANCH, never on the backups directory: "
				+ "PruneBackups early-returns internally, so a harness waiting on AfterPrune "
				+ "against a no-backups store must not hang");
		}
		finally { Directory.Delete(dir, recursive: true); }
	}

	// Pins that AfterVerify fires INSIDE the write lock — otherwise MUTATION-INVISIBLE.
	// Moving the call below ReleaseMutex leaves the stage SEQUENCE identical, so every
	// order-asserting test above stays green; only the LOCK STATE at the checkpoint
	// changes, and nothing observed it until now.
	//
	// That lock state is precisely what the harness needs: a checkpoint where the
	// publish is committed AND verified but the lock has not yet been handed to the next
	// writer. Under the mutation a peer writer could acquire the lock and publish its
	// own revision while the harness still believes it has the destination pinned, so
	// whatever the harness then observed on disk would be attributed to the wrong write.
	[Fact]
	public void ReplacingWrite_AtAfterVerify_StillHoldsTheWriteLock()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "browsers.json");
			new AtomicJsonFileStore().Write(path, new BrowsersDocument([CreateBrowser("rev-1")]));

			LockProbingObserver observer = new(path);
			new AtomicJsonFileStore(backupsDirectory: null, writeLockTimeout: null, observer: observer)
				.Write(path, new BrowsersDocument([CreateBrowser("rev-2")]));

			_ = observer.LockWasHeldAtAfterVerify.Should().Be(
				true,
				"the harness needs a verified-but-still-locked checkpoint: at AfterVerify the "
				+ "publish is committed and read back, yet the per-file write lock must still "
				+ "be HELD, so no peer writer can slip a revision in before the harness acts");
		}
		finally { Directory.Delete(dir, recursive: true); }
	}

	private static T Resolve<T>(string path)
		where T : ConfigDocument
	{
		return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReadOptions)!;
	}

	private static BrowserDefinition CreateBrowser(string browserId)
	{
		return new(
			browserId,
			$"{browserId} Display",
			"Vendor",
			"stable",
			$@"C:\path\{browserId}.exe",
			"registry",
			null);
	}

	private static string NewTempDirectory()
	{
		string path = Path.Combine(Path.GetTempPath(), "BroChoos.AtomicWriteObserver", Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(path);
		return path;
	}

	// Backup-aware layout, mirroring StorageRootResolver's eager provisioning: the
	// store does NOT create the backups directory itself and must never start.
	private static (string DataDir, string BackupsDir, string Root) NewStorageLayout()
	{
		string root = Path.Combine(Path.GetTempPath(), "BroChoos.AtomicWriteObserver", Guid.NewGuid().ToString("N"));
		string dataDir = Path.Combine(root, "data");
		string backupsDir = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(dataDir);
		_ = Directory.CreateDirectory(backupsDir);
		return (dataDir, backupsDir, root);
	}

	// Records the sequence verbatim. Writes arrive synchronously on the writing thread
	// and every test here writes from one thread, so a plain List is sufficient and no
	// synchronization is implied to be part of the contract.
	private sealed class RecordingObserver : IAtomicWriteObserver
	{
		private readonly List<AtomicWriteStage> _stages = [];

		internal IReadOnlyList<AtomicWriteStage> Stages => _stages;

		public void OnStage(AtomicWriteStage stage)
		{
			_stages.Add(stage);
		}
	}

	// Answers ONE question at AfterVerify: is the per-file write mutex still held?
	//
	// THE SEPARATE THREAD IS MANDATORY, and must not be collapsed into a same-thread
	// probe. A Win32 mutex is REENTRANT for its owning thread, and OnStage runs ON
	// the writing thread, so a
	// same-thread WaitOne(TimeSpan.Zero) would SUCCEED by recursing on the hold this
	// probe is trying to detect. It would report "not held" while the lock is held,
	// pass identically whether AfterVerify sits inside or outside the lock, and prove
	// nothing at all. A thread that does not own the mutex is the only observer that
	// can tell the two states apart. This mirrors the external-holder threads the
	// lock tests already use, for the same reentrancy reason.
	//
	// TimeSpan.Zero, so a held lock is reported immediately instead of stalling the
	// write inside its own critical section. Join publishes the probe thread's write
	// to `tookIt` to this thread, so no extra synchronization is needed to read it.
	private sealed class LockProbingObserver(string path) : IAtomicWriteObserver
	{
		internal bool? LockWasHeldAtAfterVerify { get; private set; }

		public void OnStage(AtomicWriteStage stage)
		{
			if (stage != AtomicWriteStage.AfterVerify) { return; }

			bool tookIt = false;
			Thread probe = new(() =>
			{
				using Mutex m = new(initiallyOwned: false, AtomicJsonFileStore.BuildMutexName(path));
				try { tookIt = m.WaitOne(TimeSpan.Zero); if (tookIt) { m.ReleaseMutex(); } }
				catch (AbandonedMutexException) { tookIt = true; }
			})
			{ IsBackground = true };

			probe.Start();

			// A timed-out Join must NOT be reported as "held". Join returning false
			// means the probe never ran, so `tookIt` is still its initial false and
			// !tookIt would compute true — the assertion would pass without a single
			// measurement being taken, which is the dangerous direction for a proof
			// test. null instead, so an unmeasured run fails loudly.
			bool joined = probe.Join(TimeSpan.FromSeconds(5));
			LockWasHeldAtAfterVerify = joined ? !tookIt : null;
		}
	}

	// Deliberately VIOLATES the "must not throw" contract, which is the only way to
	// pin that the write path does not swallow the violation.
	private sealed class ThrowingObserver(AtomicWriteStage throwAt) : IAtomicWriteObserver
	{
		public void OnStage(AtomicWriteStage stage)
		{
			if (stage == throwAt)
			{
				throw new InvalidOperationException(
					FormattableString.Invariant($"test observer failing deliberately at {stage}"));
			}
		}
	}
}
