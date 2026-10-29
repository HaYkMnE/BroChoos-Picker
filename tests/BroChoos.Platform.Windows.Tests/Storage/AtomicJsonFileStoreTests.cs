using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

// D-063 atomic-write happy path, end to end against the real filesystem. Each
// test owns a unique subdir under %TEMP% and deletes it in a finally, so nothing
// leaks onto the developer's machine.
public sealed class AtomicJsonFileStoreTests
{
	private static readonly JsonSerializerOptions ReadOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	// Mirrors AtomicJsonFileStore's write-affecting options (indented + camelCase)
	// so tests can compute the canonical on-disk bytes to compare against.
	// JsonUnmappedMemberHandling and the enum converter do not change these bytes.
	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	[Fact]
	public void RoundTrip_AppDocument_ReadsBackSchemaVersion1()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "app.json");

			new AtomicJsonFileStore().Write(path, new AppDocument());

			AppDocument read = Resolve<AppDocument>(path);

			_ = read.SchemaVersion.Should().Be(1);
			_ = read.SchemaRevision.Should().Be(0);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void FirstCreate_DestinationAbsent_CreatesValidJsonWithNoTemp()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "app.json");

			new AtomicJsonFileStore().Write(path, new AppDocument());

			_ = File.Exists(path).Should().BeTrue();

			using JsonDocument parsed = JsonDocument.Parse(File.ReadAllText(path));
			_ = parsed.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);

			_ = Directory.GetFiles(dir).Should().ContainSingle();
			_ = Directory.GetFiles(dir, "*.tmp").Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void Replace_SecondWrite_ContentBecomesSecondAndSingleFileNoTemp()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "browsers.json");
			AtomicJsonFileStore store = new();

			store.Write(path, new BrowsersDocument([CreateBrowser("first")]));
			store.Write(path, new BrowsersDocument([CreateBrowser("second-a"), CreateBrowser("second-b")]));

			BrowsersDocument read = Resolve<BrowsersDocument>(path);

			_ = read.Browsers.Should().HaveCount(2);
			_ = read.Browsers.Select(browser => browser.BrowserId)
				.Should().BeEquivalentTo(["second-a", "second-b"]);

			_ = Directory.GetFiles(dir).Should().ContainSingle();
			_ = Directory.GetFiles(dir, "*.tmp").Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void Write_BrowsersDocumentCastToBase_PreservesBrowserList()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "browsers.json");
			BrowsersDocument browsers = new([CreateBrowser("edge-stable")]);
			ConfigDocument asBase = browsers;

			new AtomicJsonFileStore().Write(path, asBase);

			BrowsersDocument read = Resolve<BrowsersDocument>(path);

			_ = read.Browsers.Should().ContainSingle();
			_ = read.Browsers[0].BrowserId.Should().Be("edge-stable");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void Write_ProducesUtf8BytesWithoutBom()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "app.json");

			new AtomicJsonFileStore().Write(path, new AppDocument());

			byte[] bytes = File.ReadAllBytes(path);

			_ = bytes.Length.Should().BeGreaterThanOrEqualTo(3);
			_ = BitConverter.ToString(bytes, 0, 3).Should().NotBe("EF-BB-BF");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void Replace_DestinationBytesEqualSecondDocumentExactly()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "browsers.json");
			AtomicJsonFileStore store = new();
			BrowsersDocument second = new([CreateBrowser("second-a"), CreateBrowser("second-b")]);

			store.Write(path, new BrowsersDocument([CreateBrowser("first")]));
			store.Write(path, second);

			byte[] expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
				.GetBytes(JsonSerializer.Serialize(second, WriteOptions));
			byte[] actual = File.ReadAllBytes(path);

			_ = actual.SequenceEqual(expected).Should().BeTrue(
				"the destination must be exactly the second document, with no leftover bytes or stray whitespace");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void ReplaceWithBackup()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");
			AtomicJsonFileStore store = new(backupsDir);
			BrowsersDocument first = new([CreateBrowser("first")]);

			store.Write(path, first);
			store.Write(path, new BrowsersDocument([CreateBrowser("second")]));

			string[] backups = Directory.GetFiles(backupsDir);

			_ = backups.Should().ContainSingle();

			byte[] expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
				.GetBytes(JsonSerializer.Serialize(first, WriteOptions));
			_ = File.ReadAllBytes(backups[0]).SequenceEqual(expected).Should().BeTrue(
				"the backup must hold the previous revision, not the new one");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void RetentionKeepsLast10()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");
			AtomicJsonFileStore store = new(backupsDir);

			// 12 writes: the first creates the destination, the next 11 replace it
			// => 11 backups; retention must keep exactly the NEWEST 10 and drop the
			// oldest (iteration-0). Distinct content per iteration proves the
			// surviving set is the RIGHT one, not merely the right COUNT. Ordering
			// derives from the millisecond timestamp embedded in each backup's NAME,
			// not from filesystem creation time (File.Replace stamps every backup
			// with the replaced destination's creation time, so all backups share
			// one ctime and it cannot order anything). The sleep is what keeps those
			// NAME-EMBEDDED timestamps distinct, making the order deterministic.
			const int totalWrites = 12;
			for (int i = 0; i < totalWrites; i++)
			{
				store.Write(path, new BrowsersDocument([CreateBrowser($"iteration-{i}")]));
				Thread.Sleep(10);
			}

			string[] backups = Directory.GetFiles(backupsDir);
			_ = backups.Should().HaveCount(10);

			IEnumerable<string> survivingIds = backups
				.Select(Resolve<BrowsersDocument>)
				.Select(document => document.Browsers[0].BrowserId);
			_ = survivingIds.Should().BeEquivalentTo(
				Enumerable.Range(1, 10).Select(i => $"iteration-{i}"));

			// The newest write lives in the destination, not among the backups.
			_ = Resolve<BrowsersDocument>(path).Browsers[0].BrowserId
				.Should().Be($"iteration-{totalWrites - 1}");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void RetentionDropsOldBackup()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");
			AtomicJsonFileStore store = new(backupsDir);

			// Distinct content per write is what makes the survivor assertion
			// DISCRIMINATING. A contentless document serializes to identical bytes
			// on every revision, so a regression that kept the first or the third
			// revision would still satisfy a byte comparison.
			BrowsersDocument first = new([CreateBrowser("rev-1")]);
			BrowsersDocument second = new([CreateBrowser("rev-2")]);
			BrowsersDocument third = new([CreateBrowser("rev-3")]);

			// Write 1 creates the destination (no backup); write 2 replaces it and
			// mints the sole backup, holding revision 1.
			store.Write(path, first);
			store.Write(path, second);

			string[] backups = Directory.GetFiles(backupsDir);
			_ = backups.Should().ContainSingle();

			// Age the backup the way the filesystem genuinely ages one: by its
			// NAME-EMBEDDED mint timestamp. Backdating creation time would prove
			// nothing, because File.Replace overwrites the backup's ctime with the
			// replaced destination's ctime, so retention cannot and must not read
			// it. Renaming reproduces the real 31-day-old-backup state exactly.
			// The name keeps the document's own prefix, otherwise PruneBackups —
			// which matches on "<document name>." — would not even consider it.
			string stale = Path.Combine(
				backupsDir,
				$"{Path.GetFileName(path)}.{DateTime.UtcNow.AddDays(-31):yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.bak");
			File.Move(backups[0], stale);

			// Write 3 mints a backup holding revision 2 and prunes the stale one.
			store.Write(path, third);

			string[] after = Directory.GetFiles(backupsDir);
			_ = after.Should().HaveCount(1);
			_ = File.Exists(stale).Should().BeFalse("the backup past the age cap must be pruned");
			_ = Path.GetFileName(after[0]).Should().NotBe(
				Path.GetFileName(stale),
				"the survivor must be the freshly minted backup, not the stale one");

			// Named first so a wrong-revision regression reports WHICH revision
			// survived instead of an opaque byte mismatch.
			_ = Resolve<BrowsersDocument>(after[0]).Browsers[0].BrowserId.Should().Be(
				"rev-2",
				"the survivor must hold the revision the third write displaced, not the first or the third");

			byte[] expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
				.GetBytes(JsonSerializer.Serialize(second, WriteOptions));
			_ = File.ReadAllBytes(after[0]).SequenceEqual(expected).Should().BeTrue(
				"the survivor must hold the revision the third write displaced, byte for byte");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// Regression: Win32 ReplaceFile (behind File.Replace) transfers the REPLACED
	// destination's creation time onto the backup file. A destination older than
	// BackupMaxAge — the normal steady state for a config created at install and
	// still in use — therefore stamps every backup, including the one just minted
	// by this very write, as stale. Retention must not delete it: age must derive
	// from the authoritative name-embedded timestamp, not filesystem ctime.
	[Fact]
	public void RetentionKeepsFreshBackupWhenDestinationIsOlderThanMaxAge()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");
			AtomicJsonFileStore store = new(backupsDir);
			BrowsersDocument first = new([CreateBrowser("first")]);

			store.Write(path, first);

			// The destination predates the age cap; the backup about to be minted
			// does not.
			File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-60));

			store.Write(path, new BrowsersDocument([CreateBrowser("second")]));

			string survivor = Directory.GetFiles(backupsDir).Should().ContainSingle(
				"the backup minted by this write is brand new and must survive retention "
				+ "even though File.Replace copied the old destination's creation time onto it")
				.Which;

			byte[] expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
				.GetBytes(JsonSerializer.Serialize(first, WriteOptions));
			_ = File.ReadAllBytes(survivor).SequenceEqual(expected).Should().BeTrue(
				"the surviving backup must hold the previous revision");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// Covers BackupTimestamp's fallback: a file in the backups directory that
	// matches the document prefix but carries no store-minted stamp at the
	// expected position cannot be aged by name, so retention falls back to
	// last-write time.
	[Fact]
	public void RetentionUsesLastWriteTimeForForeignBackupWithoutStamp()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "app.json");
			AtomicJsonFileStore store = new(backupsDir);
			store.Write(path, new AppDocument());

			string foreign = Path.Combine(backupsDir, "app.json.legacy.bak");
			File.WriteAllText(foreign, "{}");
			File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-31));

			store.Write(path, new AppDocument());

			_ = File.Exists(foreign).Should().BeFalse(
				"an unparseable foreign backup is aged by last-write time, which ReplaceFile does not rewrite");
			_ = Directory.GetFiles(backupsDir).Should().ContainSingle();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// The opposite direction of the test above, and the reason both are needed:
	// that one only proves a STALE stamp-less file gets pruned, which a regression
	// returning DateTime.MinValue for every unparseable name would also satisfy —
	// MinValue is stale. This pins that a FRESH stamp-less file SURVIVES, so the
	// fallback has to read a real last-write time to pass both.
	[Fact]
	public void RetentionKeepsFreshForeignBackupWithoutStamp()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "app.json");
			AtomicJsonFileStore store = new(backupsDir);
			store.Write(path, new AppDocument());

			string foreign = Path.Combine(backupsDir, "app.json.legacy.bak");
			File.WriteAllText(foreign, "{}");
			File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow);

			// Replacing write: mints one backup, then prunes. The foreign file is
			// well inside both caps (fresh, and the total is 2 against a count cap
			// of 10), so retention must leave it alone.
			store.Write(path, new AppDocument());

			_ = File.Exists(foreign).Should().BeTrue(
				"a stamp-less foreign backup that is FRESH by last-write time sits inside the age cap and must survive");
			_ = Directory.GetFiles(backupsDir).Should().HaveCount(
				2,
				"the surviving set is the fresh foreign file plus the backup this write minted");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void FirstCreate()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "app.json");
			AtomicJsonFileStore store = new(backupsDir);

			store.Write(path, new AppDocument());

			_ = File.Exists(path).Should().BeTrue();
			_ = Directory.GetFiles(backupsDir).Should().BeEmpty();
			_ = Directory.GetFiles(dataDir, "*.tmp").Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// The DirectoryNotFoundException contract the Write catch block explicitly names
	// as a reason it rethrows bare rather than wrapping. That comment claimed the
	// contract was pinned by other tests; it was not — no test asserted this type
	// anywhere, so a regression that wrapped every failure in IOException would have
	// broken the documented XML-doc promise ("a missing directory surfaces as a clean
	// DirectoryNotFoundException") with the whole suite still green.
	//
	// The missing directory is caught by the temp FileStream, before any bytes reach
	// the destination — hence destinationModified false. Data["path"] must still be
	// stamped, because a framework exception rethrown as-is carries no classification
	// of its own.
	[Fact]
	public void Write_ToPathInsideAMissingDirectory_ThrowsDirectoryNotFoundAndReportsDestinationUnmodified()
	{
		string dir = NewTempDirectory();
		try
		{
			// The parent exists; the subdirectory deliberately does not, and the store
			// must never create it (the storage root resolver pre-provisions).
			string path = Path.Combine(dir, "not-provisioned", "app.json");

			Action write = () => new AtomicJsonFileStore().Write(path, new AppDocument());

			DirectoryNotFoundException missing = write.Should().Throw<DirectoryNotFoundException>(
				"the XML-doc contract promises a missing destination directory surfaces as a CLEAN "
				+ "DirectoryNotFoundException; wrapping it in IOException would break every caller "
				+ "distinguishing 'not provisioned' from 'write failed'")
				.Which;

			_ = missing.Data["destinationModified"].Should().Be(
				false,
				"nothing was staged and nothing was published, so the destination is provably untouched");
			_ = missing.Data["path"].Should().Be(
				path,
				"a bare rethrow carries no classification of its own, so Write must still stamp the path for a diagnostic sink");

			_ = Directory.Exists(Path.Combine(dir, "not-provisioned")).Should().BeFalse(
				"the store must never provision a destination directory itself");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// D-076: the mutex must identify a FILE, not a file name. Per D-072 an
	// installed root and a portable root can each hold their own app.json; those
	// are independent files and must not serialize against each other, which is
	// why the hash covers the full normalized path. The case fold is equally
	// load-bearing: NTFS is case-insensitive, so two spellings of ONE file must
	// resolve to ONE mutex or the TOCTOU window the lock closes stays open.
	[Fact]
	public void BuildMutexName_KeyedOnFullPath_CaseInsensitiveAndNormalized()
	{
		const string prefix = @"Local\BroChoos_ConfigWrite_";
		const string installed = @"C:\ProgramData\BroChoos\data\app.json";
		const string portable = @"C:\Portable\BroChoos\data\app.json";

		string name = AtomicJsonFileStore.BuildMutexName(installed);

		_ = AtomicJsonFileStore.BuildMutexName(installed).Should().Be(
			name,
			"the name must be stable across calls, otherwise two writers never meet on the same kernel object");
		_ = AtomicJsonFileStore.BuildMutexName(portable).Should().NotBe(
			name,
			"the same file NAME under two different roots is two different files (D-072) and must not block each other");
		_ = AtomicJsonFileStore.BuildMutexName(installed.ToUpperInvariant()).Should().Be(
			name,
			"NTFS is case-insensitive, so a case-different spelling of one file must take the SAME mutex");
		_ = AtomicJsonFileStore.BuildMutexName(@"C:\ProgramData\BroChoos\data\..\data\app.json").Should().Be(
			name,
			"the path is normalized before hashing, so relative segments cannot split the lock");

		_ = name.Should().StartWith(prefix);

		string hash = name[prefix.Length..];
		_ = hash.Length.Should().Be(32, "16 digest bytes, hex-encoded");
		_ = hash.All(Uri.IsHexDigit).Should().BeTrue("the name must be a legal kernel object name");
	}

	// The lock must never turn into a hang: acquisition is bounded and a contended
	// write surfaces the same recoverable IOException the store already reports for
	// write failures. Holding a mutex built from the store's OWN name derivation
	// also proves the store and this test agree on the name.
	[Fact]
	public void Write_WhileAnotherThreadHoldsTheLock_FailsRecoverablyThenSucceedsOnRelease()
	{
		string dir = NewTempDirectory();
		string path = Path.Combine(dir, "app.json");
		using ManualResetEventSlim held = new(initialState: false);
		using ManualResetEventSlim release = new(initialState: false);
		Exception? holderFailure = null;

		// The hold MUST live on another thread. A Win32 mutex is reentrant for its
		// owning thread, so holding it on the test thread and then calling Write on
		// the test thread would re-acquire it and assert nothing.
		Thread holder = new(() =>
		{
			try
			{
				using Mutex external = new(
					initiallyOwned: false,
					AtomicJsonFileStore.BuildMutexName(path));

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

			AtomicJsonFileStore store = new(
				backupsDirectory: null,
				writeLockTimeout: TimeSpan.FromMilliseconds(100));

			Action contended = () => store.Write(path, new AppDocument());

			// The message constraint is load-bearing: a bare Throw<IOException>()
			// would also be satisfied by an UNRELATED IOException — a regression in
			// the temp write, for instance — and would then keep passing while the
			// lock timeout it claims to cover had stopped working entirely.
			IOException timedOut = contended.Should().Throw<IOException>(
				"a contended config write must fail recoverably and promptly; the picker must never hang on a config write")
				.WithMessage("*config-write lock*").Which;

			// D-075 / D-097: no full user file path in a message a caller might log
			// verbatim. The file name is in the text; the full path rides in Data.
			_ = timedOut.Message.Should().NotContain(
				dir,
				"the timeout message must not carry the full destination path — in installed mode it embeds the Windows account name, and any caller logging ex.Message would breach D-075/D-097");
			_ = timedOut.Message.Should().Contain("app.json", "the file name alone is enough to identify which document was contended");
			_ = timedOut.Data["path"].Should().Be(
				path,
				"a redaction-aware diagnostic sink must still be able to recover the full path");

			_ = File.Exists(path).Should().BeFalse("nothing may be published while another writer holds the lock");
			_ = Directory.GetFiles(dir, "*.tmp").Should().BeEmpty(
				"the staged temp must be cleaned up even when the write never reaches the swap");

			release.Set();
			_ = holder.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
			_ = holderFailure.Should().BeNull();

			store.Write(path, new AppDocument());

			_ = File.Exists(path).Should().BeTrue("the very same write succeeds once the lock is free");
		}
		finally
		{
			release.Set();
			_ = holder.Join(TimeSpan.FromSeconds(5));
			Directory.Delete(dir, recursive: true);
		}
	}

	// The defect this slice closes: File.Exists and the File.Replace/File.Move that
	// follows it are not one atomic step. Unserialized, a peer can create the
	// destination inside that gap and turn File.Move into a spurious failure, or the
	// two publishes can cross. Two threads hammering one destination must produce
	// neither an escaping exception nor a torn file.
	[Fact]
	public void ConcurrentWrites_ToOneDestination_PublishSerially()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "browsers.json");

			// An explicit, generous lock timeout. The DEFAULT 10s would be the only
			// wall-clock failure mode in this suite: 40 contended acquisitions on a
			// loaded machine could exceed it, throw, and fail the test for a reason
			// that has nothing to do with the serialization under test. Contention
			// stays exactly as real; only the give-up threshold moves.
			AtomicJsonFileStore store = new(writeLockTimeout: TimeSpan.FromSeconds(60));
			const int iterations = 20;
			List<Exception> failures = [];
			object gate = new();

			void WriteRepeatedly(string tag)
			{
				try
				{
					for (int i = 0; i < iterations; i++)
					{
						store.Write(path, new BrowsersDocument([CreateBrowser($"{tag}-{i}")]));
					}
				}
				catch (Exception failure)
				{
					lock (gate)
					{
						failures.Add(failure);
					}
				}
			}

			Thread first = new(() => WriteRepeatedly("a")) { IsBackground = true };
			Thread second = new(() => WriteRepeatedly("b")) { IsBackground = true };

			first.Start();
			second.Start();

			_ = first.Join(TimeSpan.FromSeconds(60)).Should().BeTrue();
			_ = second.Join(TimeSpan.FromSeconds(60)).Should().BeTrue();

			_ = failures.Should().BeEmpty(
				"the per-file lock must serialize the publish, so no writer trips over another's swap");

			// Complete, valid JSON — and one of the documents actually written,
			// not a truncated or interleaved hybrid.
			BrowsersDocument read = Resolve<BrowsersDocument>(path);
			_ = read.Browsers.Should().ContainSingle();
			_ = read.Browsers[0].BrowserId.Should().MatchRegex("^[ab]-[0-9]{1,2}$");

			_ = Directory.GetFiles(dir, "*.tmp").Should().BeEmpty("no attempt may leave a temp behind");
			_ = Directory.GetFiles(dir).Should().ContainSingle("only the destination survives");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// The catch (AbandonedMutexException) branch decides whether ONE crashed writer
	// wedges EVERY future config write behind a dead process. It must absorb the
	// abandoned state, take ownership and finish the publish.
	//
	// Mechanism: a thread that acquires a named mutex and then TERMINATES without
	// releasing leaves the mutex abandoned; the next waiter receives
	// AbandonedMutexException while genuinely OWNING it.
	//
	// DO NOT "SIMPLIFY" BY DROPPING THE keeper HANDLE BELOW. A kernel mutex lives
	// only while at least one handle to it is open. Without a second handle held
	// open here, the abandoning thread's handle is the last one, the object is
	// DESTROYED when that thread goes away, and the store's WaitOne then creates a
	// brand-new mutex and acquires it cleanly — no exception, the branch under test
	// never runs, and this test passes while proving nothing at all.
	[Fact]
	public void Write_AfterAnotherThreadAbandonsTheLock_AbsorbsAbandonedMutexAndCompletes()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "browsers.json");
			string mutexName = AtomicJsonFileStore.BuildMutexName(path);

			// Keeps the kernel object alive across the abandoning thread's death.
			// Never waited on, so it never owns anything.
			using Mutex keeper = new(initiallyOwned: false, mutexName);

			Exception? abandonerFailure = null;
			bool abandoned = false;

			// Deliberately NOT disposed and NOT released: exiting while owning is
			// precisely what a crashed or killed writer does.
			Thread abandoner = new(() =>
			{
				try
				{
					Mutex doomed = new(initiallyOwned: false, mutexName);
					abandoned = doomed.WaitOne(TimeSpan.FromSeconds(5));
				}
				catch (Exception failure)
				{
					abandonerFailure = failure;
				}
			})
			{ IsBackground = true };

			abandoner.Start();
			_ = abandoner.Join(TimeSpan.FromSeconds(10)).Should().BeTrue(
				"the abandoning thread must actually terminate — abandonment is recorded on thread exit");
			_ = abandonerFailure.Should().BeNull();
			_ = abandoned.Should().BeTrue("the abandoning thread must have really taken the lock it abandons");

			AtomicJsonFileStore store = new(
				backupsDirectory: null,
				writeLockTimeout: TimeSpan.FromSeconds(5));
			BrowsersDocument document = new([CreateBrowser("after-abandon")]);

			Action afterAbandon = () => store.Write(path, document);

			_ = afterAbandon.Should().NotThrow(
				"a writer that died holding the lock must not wedge every later config write; the store takes ownership of the abandoned mutex and completes");

			// The write must have actually PUBLISHED, not merely survived the wait.
			_ = Resolve<BrowsersDocument>(path).Browsers[0].BrowserId.Should().Be("after-abandon");
			_ = Directory.GetFiles(dir, "*.tmp").Should().BeEmpty("the staged temp must not survive a successful publish");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// Pins ReleaseMutex, which is otherwise MUTATION-INVISIBLE: delete the release
	// and the whole suite still goes green, because the next writer simply absorbs
	// the abandoned mutex and carries on while mutual exclusion still holds.
	//
	// DO NOT "SIMPLIFY" EITHER OF THE TWO DETAILS BELOW. Both look like ceremony and
	// both are load-bearing; removing either leaves a test that passes unconditionally,
	// including against the very missing-release regression it exists to catch:
	//  - The observer runs on a SEPARATE thread. A Win32 mutex is reentrant for its
	//    owner, so if Write forgot to release, the TEST thread would still own it
	//    and a re-wait on the test thread would succeed and prove nothing.
	//  - The keeper handle is opened BEFORE the write, so the kernel object
	//    survives the store's `using Mutex` disposal. Otherwise a missing release
	//    would destroy the object with it, and the observer would create a fresh
	//    one and acquire it cleanly — a false pass.
	[Fact]
	public void Write_ReleasesTheLockCleanly_NextWaiterAcquiresWithoutAbandonedMutex()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "app.json");
			string mutexName = AtomicJsonFileStore.BuildMutexName(path);

			using Mutex keeper = new(initiallyOwned: false, mutexName);

			new AtomicJsonFileStore().Write(path, new AppDocument());
			_ = File.Exists(path).Should().BeTrue("the write under test must have succeeded before its release is judged");

			bool acquiredCleanly = false;
			bool sawAbandoned = false;
			Exception? observerFailure = null;

			Thread observer = new(() =>
			{
				try
				{
					using Mutex next = new(initiallyOwned: false, mutexName);
					try
					{
						acquiredCleanly = next.WaitOne(TimeSpan.FromSeconds(2));
						if (acquiredCleanly)
						{
							next.ReleaseMutex();
						}
					}
					catch (AbandonedMutexException)
					{
						// The wait succeeded, so this thread owns it — release so the
						// object is left clean regardless of the assertion outcome.
						sawAbandoned = true;
						next.ReleaseMutex();
					}
				}
				catch (Exception failure)
				{
					observerFailure = failure;
				}
			})
			{ IsBackground = true };

			observer.Start();
			_ = observer.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
			_ = observerFailure.Should().BeNull();

			_ = sawAbandoned.Should().BeFalse(
				"the store must call ReleaseMutex on the happy path: the next waiter saw AbandonedMutexException, "
				+ "which means Write returned still owning the lock and every later writer only works by absorbing an abandoned mutex");
			_ = acquiredCleanly.Should().BeTrue(
				"the store must call ReleaseMutex on the happy path: a fresh waiter on another thread could not take the lock after a completed write");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// The other concurrency test runs with backupsDirectory: null, so under
	// contention the File.Replace-with-backup path and the retention prune are never
	// exercised — and the prune runs INSIDE the critical section, where a throw
	// would escape Write and leave the mutex path half-travelled. This is the same
	// race with backups provisioned.
	[Fact]
	public void ConcurrentWrites_WithBackupsDirectory_PublishSeriallyAndRespectRetentionCap()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");

			// Explicit generous lock timeout, for the same reason as the other
			// concurrency test: the DEFAULT 10s is the only wall-clock failure mode in
			// this suite, and a contended acquisition on a loaded machine could exceed
			// it and fail the test for a reason unrelated to what is under test.
			AtomicJsonFileStore store = new(backupsDir, writeLockTimeout: TimeSpan.FromSeconds(60));
			const int iterations = 15;
			List<Exception> failures = [];
			object gate = new();

			void WriteRepeatedly(string tag)
			{
				try
				{
					for (int i = 0; i < iterations; i++)
					{
						store.Write(path, new BrowsersDocument([CreateBrowser($"{tag}-{i}")]));
					}
				}
				catch (Exception failure)
				{
					lock (gate)
					{
						failures.Add(failure);
					}
				}
			}

			Thread first = new(() => WriteRepeatedly("a")) { IsBackground = true };
			Thread second = new(() => WriteRepeatedly("b")) { IsBackground = true };

			first.Start();
			second.Start();

			_ = first.Join(TimeSpan.FromSeconds(120)).Should().BeTrue();
			_ = second.Join(TimeSpan.FromSeconds(120)).Should().BeTrue();

			_ = failures.Should().BeEmpty(
				"neither the backup replace nor the in-critical-section prune may throw under contention");

			// One complete, valid document — one of the ones actually written, not a
			// truncated or interleaved hybrid. Which writer wins is deliberately not
			// asserted; only that a whole document won.
			BrowsersDocument read = Resolve<BrowsersDocument>(path);
			_ = read.Browsers.Should().ContainSingle();
			_ = read.Browsers[0].BrowserId.Should().MatchRegex("^[ab]-[0-9]{1,2}$");

			_ = Directory.GetFiles(dataDir, "*.tmp").Should().BeEmpty("no attempt may leave a temp behind");
			_ = Directory.GetFiles(dataDir).Should().ContainSingle("only the destination survives in the data dir");

			// 30 writes: one creates the destination, the other 29 replace it and mint
			// a backup each. The count is EXACTLY the cap, not merely at most it,
			// because mint and prune both run inside the critical section: no writer
			// can slip a mint between another's Replace and its prune, so every write
			// after the tenth leaves the directory holding precisely 10. All 29 are
			// fresh, so the age cap removes nothing.
			//
			// Asserting equality is the more discriminating choice. "<= 10" is also
			// satisfied by a prune that deletes far too much — the degenerate
			// "keep zero" regression that RetentionKeepsFreshBackupWhenDestination...
			// covers single-threaded would pass here unnoticed.
			_ = Directory.GetFiles(backupsDir).Length.Should().Be(
				10,
				"retention must hold this document's backups at exactly BackupRetentionCount when mint and prune are serialized by the write lock");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// D-063 step 9 (A), the PRE-publish half: a failure before the swap must leave the
	// previous revision untouched AND say so machine-checkably.
	//
	// The failure is injected the way the lock test already does it — an external
	// holder on another thread plus a short timeout — because that is a genuine
	// pre-publish failure of the real code path, not a simulated one.
	//
	// Catches: a regression that reports destinationModified as true (or omits it)
	// for a failure that published nothing. A caller trusting that would run
	// corruption recovery against a perfectly good file, or warn the user their
	// config may be damaged when it demonstrably is not.
	[Fact]
	public void Write_FailingBeforeThePublish_LeavesOldFileIntactAndReportsDestinationUnmodified()
	{
		string dir = NewTempDirectory();
		string path = Path.Combine(dir, "browsers.json");
		using ManualResetEventSlim held = new(initialState: false);
		using ManualResetEventSlim release = new(initialState: false);
		Exception? holderFailure = null;

		// A Win32 mutex is reentrant for its owning thread, so the hold must live on
		// another thread or Write would simply re-acquire it and never fail.
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
			// An established previous revision is what "preserve old file" is about, so
			// there must BE an old file. Distinct content makes the byte comparison
			// discriminating: a contentless document serializes identically on every
			// revision, so a regression that overwrote it would still compare equal.
			BrowsersDocument established = new([CreateBrowser("established")]);
			new AtomicJsonFileStore().Write(path, established);
			byte[] before = File.ReadAllBytes(path);

			holder.Start();
			_ = held.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the holder thread must reach the hold");
			_ = holderFailure.Should().BeNull();

			AtomicJsonFileStore store = new(
				backupsDirectory: null,
				writeLockTimeout: TimeSpan.FromMilliseconds(100));

			Action blocked = () => store.Write(path, new BrowsersDocument([CreateBrowser("never-published")]));

			IOException failed = blocked.Should().Throw<IOException>()
				.WithMessage("*config-write lock*").Which;

			_ = failed.Data["destinationModified"].Should().Be(
				false,
				"nothing was published, so the caller must be told the destination is untouched — "
				+ "a caller that cannot distinguish this from a torn publish would run recovery against a good file");
			_ = failed.Data["restored"].Should().Be(
				false,
				"a pre-publish failure restores nothing because nothing was lost");

			_ = File.ReadAllBytes(path).SequenceEqual(before).Should().BeTrue(
				"D-063 step 9: a failed write must preserve the old file byte for byte");
			_ = Resolve<BrowsersDocument>(path).Browsers[0].BrowserId.Should().Be(
				"established",
				"the surviving revision must be the one that was already there, not the one that failed to publish");

			_ = Directory.GetFiles(dir, "*.tmp").Should().BeEmpty(
				"D-063 step 9: the staged temp of a failed attempt must be deleted");
		}
		finally
		{
			release.Set();
			_ = holder.Join(TimeSpan.FromSeconds(5));
			Directory.Delete(dir, recursive: true);
		}
	}

	// Pins WHERE `published = true` sits relative to the swap — otherwise
	// MUTATION-INVISIBLE: moving the flag to BEFORE File.Replace/File.Move leaves the
	// whole suite green, because no other test makes the SWAP ITSELF fail while a
	// destination exists. Every other pre-publish failure in this suite (lock timeout,
	// serialize, temp write) aborts before the flag is anywhere near being set.
	//
	// What the mutation costs: a swap that THREW published nothing, yet would be
	// reported as destinationModified: true, sending a caller into corruption recovery
	// against a perfectly intact previous revision — or warning the user their config
	// may be damaged when it demonstrably is not. That is the D-063 step 9 (A)
	// classification inverted on the exact case it exists to distinguish.
	//
	// Injection: a backupsDirectory that does NOT exist, with the destination already
	// established. The replacing branch then hands File.Replace a backup path inside a
	// missing directory, and Win32 ReplaceFile fails (probe-verified: IOException,
	// HResult 0x80070497 / ERROR_UNABLE_TO_REMOVE_REPLACED) having rolled the
	// destination back untouched. A genuine failure of the real call rather than a
	// simulation, and it needs no seam the store does not already have — the store
	// never creating the backups directory is itself a locked behaviour, which is
	// precisely what makes this injection available.
	//
	// FUTURE READER — IF THIS TEST STARTS FAILING AT ITS INJECTION rather than at a
	// classification assertion, i.e. the swap no longer throws at all and Write returns
	// successfully so the Should().Throw<IOException>() below is what breaks: the cause
	// is that BACKUPS-DIRECTORY PROVISIONING was added (the store, or something on its
	// path, now creates the directory this test deliberately leaves absent). That
	// removes the only condition the injection leans on. It does NOT mean the D-063
	// step 9 (A) failure classification broke, and the fix is NOT to make the store
	// stop provisioning. Re-inject the swap failure some other way — the requirement is
	// only that File.Replace itself fails while the destination already exists — and
	// keep the assertions below exactly as they are, because they are what pins where
	// `published = true` sits relative to the swap.
	[Fact]
	public void Write_WhenTheSwapItselfFails_ReportsDestinationUnmodifiedAndKeepsTheOldBytes()
	{
		string root = NewTempDirectory();
		try
		{
			string dataDir = Path.Combine(root, "data");
			_ = Directory.CreateDirectory(dataDir);

			// Deliberately NOT created. The store must never provision it, so every
			// File.Replace that tries to mint a backup in here fails.
			string missingBackupsDir = Path.Combine(root, "backups");

			string path = Path.Combine(dataDir, "browsers.json");
			AtomicJsonFileStore store = new(missingBackupsDir);

			// Write 1 takes the FIRST-CREATE branch (File.Move, no backup), so it
			// succeeds even with the backups directory absent, and establishes the
			// revision that must survive. Distinct content makes the byte comparison
			// discriminating: a contentless document serializes identically on every
			// revision, so a regression that overwrote it would still compare equal.
			BrowsersDocument established = new([CreateBrowser("established")]);
			store.Write(path, established);
			byte[] before = File.ReadAllBytes(path);

			// Write 2 takes the REPLACING branch and dies inside the swap.
			Action swapFails = () => store.Write(
				path,
				new BrowsersDocument([CreateBrowser("never-published")]));

			IOException failed = swapFails.Should().Throw<IOException>(
				"a failed File.Replace must surface as the same recoverable write failure the store reports elsewhere")
				.Which;

			_ = failed.Data["destinationModified"].Should().Be(
				false,
				"the swap THREW, so nothing was published: `published = true` must be set strictly AFTER "
				+ "File.Replace/File.Move returns. Set before, this failure would send the caller into "
				+ "corruption recovery against a file that is byte-for-byte intact");
			_ = failed.Data["restored"].Should().Be(
				false,
				"nothing was lost, so nothing was restored");

			_ = File.ReadAllBytes(path).SequenceEqual(before).Should().BeTrue(
				"D-063 step 9: a failed swap must preserve the previous revision byte for byte");
			_ = Resolve<BrowsersDocument>(path).Browsers[0].BrowserId.Should().Be(
				"established",
				"the surviving revision must be the one already there, not the one that failed to publish");

			_ = Directory.Exists(missingBackupsDir).Should().BeFalse(
				"the store must never create the backups directory");
			_ = Directory.GetFiles(dataDir, "*.tmp").Should().BeEmpty(
				"D-063 step 9: the staged temp of the failed attempt must be deleted by the finally");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// D-063 step 9 (B) / D-085 "recover interrupted atomic write": the swap committed
	// but the destination does not PARSE, so the bytes on disk are not the document
	// that was written. That is genuinely bad content, and the backup this write minted
	// holds the revision it displaced — so recovery restores it.
	//
	// Driven through the internal VerifyPublished rather than Write, because this state
	// is unreachable from outside: the bytes on disk are the very string step 2 already
	// validated, so only a foreign process mutating the destination inside the critical
	// section could produce it, and a test could only reproduce that as a race. Same
	// precedent as the BuildMutexName tests. No observer seam is added to the write
	// path for it (that is a later slice).
	//
	// Catches: a regression that reports the torn publish but performs no restore,
	// leaving the user's config unparseable when a known-good revision was sitting
	// right there — and equally one that restores but reports restored as false, which
	// would send the caller into a recovery it does not need.
	[Fact]
	public void PostPublishParseFailure_WithBackup_RestoresPreviousRevisionAndReportsIt()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");
			AtomicJsonFileStore store = new(backupsDir);
			BrowsersDocument first = new([CreateBrowser("rev-1")]);

			// Write 1 creates the destination; write 2 replaces it and mints the sole
			// backup, holding revision 1 — exactly the backup a real publish would hand
			// to recovery.
			store.Write(path, first);
			store.Write(path, new BrowsersDocument([CreateBrowser("rev-2")]));

			string backup = Directory.GetFiles(backupsDir).Should().ContainSingle().Which;

			// A DECOY, and the reason it is here: without it, the store's decision to
			// restore from the CAPTURED backup path is MUTATION-INVISIBLE. Replace the
			// captured path with "the newest file in the backups directory" and this
			// test still passed, because the captured backup was the only file there —
			// so "the captured one" and "the newest one" were the same file and the
			// assertion could not tell them apart. The production comment calls that
			// substitution out as a race hazard (a concurrent writer's mint can win and
			// recovery then restores a revision this write never displaced); the decoy
			// is what turns that reasoning into something a test can fail on.
			//
			// Same "<name>.<stamp>.<guid>.bak" shape the store mints, so a
			// newest-file regression would genuinely select it, but DIFFERENT content
			// and NEWER by BOTH age keys — the name-embedded stamp AND last-write time —
			// so it wins under either notion of "newest".
			BrowsersDocument decoyRevision = new([CreateBrowser("decoy-never-restore-me")]);
			string decoy = Path.Combine(
				backupsDir,
				$"{Path.GetFileName(path)}.{DateTime.UtcNow.AddMinutes(5):yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.bak");
			File.WriteAllBytes(
				decoy,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
					.GetBytes(JsonSerializer.Serialize(decoyRevision, WriteOptions)));
			File.SetLastWriteTimeUtc(decoy, DateTime.UtcNow.AddMinutes(5));

			// Valid JSON on purpose: if recovery restored the decoy, the destination
			// would still PARSE, so only a content assertion catches it.
			_ = File.Exists(decoy).Should().BeTrue("the decoy must actually exist or it constrains nothing");

			// The state a publish interrupted mid-write leaves behind: the destination
			// exists but its bytes are not a document.
			File.WriteAllText(path, "{ \"browsers\": [ truncated");

			Action verify = () => AtomicJsonFileStore.VerifyPublished(path, typeof(BrowsersDocument), backup);

			IOException failure = verify.Should().Throw<IOException>(
				"an unverifiable published destination is a recoverable write failure, not a silent success")
				.Which;

			_ = failure.Data["destinationModified"].Should().Be(
				true,
				"the swap already committed, so this is materially different from a pre-publish failure and must not be reported as if nothing happened");
			_ = failure.Data["restored"].Should().Be(
				true,
				"a backup from this write existed, so D-085 recovery must have restored it");

			_ = failure.InnerException.Should().BeOfType<JsonException>(
				"the original cause must survive for diagnosis");

			// Named first so a decoy-restore regression reports WHICH revision landed
			// instead of an opaque byte mismatch.
			_ = Resolve<BrowsersDocument>(path).Browsers[0].BrowserId.Should().Be(
				"rev-1",
				"recovery must restore the backup THIS write captured, not merely the newest file in the "
				+ "backups directory — a concurrent writer's mint can be newer, and restoring it would put "
				+ "back a revision this write never displaced");

			byte[] expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
				.GetBytes(JsonSerializer.Serialize(first, WriteOptions));
			_ = File.ReadAllBytes(path).SequenceEqual(expected).Should().BeTrue(
				"the destination must be restored to the revision the failed write displaced, byte for byte");

			_ = File.Exists(decoy).Should().BeTrue(
				"recovery copies FROM a backup and must not consume or move the files it did not choose");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// Pins that Write actually CALLS the post-publish verification. Everything else in
	// this suite drives VerifyPublished directly, which pins its BEHAVIOUR but not its
	// WIRING: nothing else walks the real write path to the point where step 7 and step
	// 9 (B) would run. Deleting the VerifyPublished(...) call from Write fails this test
	// and Write_WhenAFailureEscapes_RethrowKeepsTheFrameThatActuallyFailed (which reaches
	// its failure through the same call site) — and nothing else. No suite-wide count is
	// quoted here on purpose: it goes stale on every test added.
	//
	// DO NOT "SIMPLIFY" THIS TEST BY CALLING VerifyPublished DIRECTLY. Driving the
	// internal method is exactly what the other step-9 tests already do, and it is
	// precisely what CANNOT catch a missing call site. The whole value here is that
	// the ONLY entry point touched is the public Write. Equally, do not "clean this up"
	// into an observer or callback hook on the write path: that seam is a later slice
	// and deliberately does not exist yet, which is why the test reaches for the seam
	// below instead.
	//
	// THE SEAM, and why it works without any production change: Write closes the
	// staged temp's FileStream BEFORE it acquires the per-file mutex, and the temp
	// carries no share protection once that stream is closed. A writer parked on the
	// mutex therefore leaves a fully staged, freely writable temp sitting on disk. So
	// the test takes the mutex FIRST, lets the writer stage and park, rewrites the temp
	// with bytes that are not a document, and releases. Write then publishes those
	// bytes for real — File.Replace commits, so the destination genuinely holds
	// unparseable content — and step 7's read-back is what has to notice. This is a
	// genuine torn publish produced through the real code path, not a simulated one.
	//
	// The mutex is taken on the TEST thread and Write runs on a BACKGROUND thread
	// because a Win32 mutex is reentrant for its owning thread: hold it and call Write
	// on one thread and Write would simply re-acquire it, never park, and the seam
	// would never open.
	[Fact]
	public void Write_WhenTheStagedTempIsCorruptedBeforeThePublish_VerifiesTheDestinationAndRecovers()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		string path = Path.Combine(dataDir, "browsers.json");

		// Generous, for the same reason the concurrency tests are: the writer parks on
		// the mutex for as long as this test needs to arm the seam, and the DEFAULT 10s
		// would turn a slow machine into a lock timeout — a completely different
		// failure than the one under test.
		AtomicJsonFileStore store = new(backupsDir, writeLockTimeout: TimeSpan.FromSeconds(60));

		// Revision 1 must be established BEFORE the contended write, because only the
		// replacing branch mints the immediate backup that step 9 (B) recovery restores
		// from. Distinct content makes the final byte comparison discriminating.
		BrowsersDocument first = new([CreateBrowser("rev-1")]);
		store.Write(path, first);

		Exception? writerFailure = null;
		bool writerReturnedNormally = false;

		Thread writer = new(() =>
		{
			try
			{
				store.Write(path, new BrowsersDocument([CreateBrowser("rev-2")]));
				writerReturnedNormally = true;
			}
			catch (Exception failure)
			{
				writerFailure = failure;
			}
		})
		{ IsBackground = true };

		// Acquired BEFORE the writer starts. Started first, the writer could run the
		// entire publish before this test ever arms the seam, and the test would pass
		// while proving nothing.
		using Mutex writeLock = new(initiallyOwned: false, AtomicJsonFileStore.BuildMutexName(path));

		// ONE outer guard, so the writer is always joined and the temp root always
		// removed even when an assertion below fails — which includes the deliberate
		// failure a mutation-testing run of this very test is supposed to produce. The
		// mutex release is an inner finally because it must happen FIRST: the writer
		// cannot finish, and so cannot be joined, while the test still holds the lock.
		try
		{
			bool held = false;

			try
			{
				held = writeLock.WaitOne(TimeSpan.FromSeconds(10));
				_ = held.Should().BeTrue("the test could not take the lock it needs to park the writer on");

				writer.Start();

				// Wait for the writer to stage its temp and park. The overwrite is
				// retried rather than attempted once: the temp appears at CreateNew but
				// is held FileShare.None until Write closes the stream, so an overwrite
				// racing that window fails with a sharing violation. Retrying until it
				// succeeds is what makes this deterministic instead of timing-dependent.
				string? staged = null;
				DateTime deadline = DateTime.UtcNow.AddSeconds(10);

				while (staged is null && DateTime.UtcNow < deadline)
				{
					string[] temps = Directory.GetFiles(dataDir, $"{Path.GetFileName(path)}.*.tmp");

					if (temps.Length == 1)
					{
						try
						{
							// Deliberately unparseable: valid-looking JSON would pass
							// the read-back and prove nothing.
							File.WriteAllText(temps[0], "{ \"browsers\": [ truncated");
							staged = temps[0];
							break;
						}
						catch (IOException)
						{
							// Still being filled; the stream has not closed yet.
						}
					}

					if (writer.Join(TimeSpan.FromMilliseconds(25)))
					{
						// The writer finished without ever parking — the seam never
						// opened, so there is nothing left to wait for.
						break;
					}
				}

				// Fails loudly rather than passing vacuously: a test that never armed
				// the seam would otherwise sail through every assertion below for the
				// wrong reason.
				_ = staged.Should().NotBeNull(
					"the test never managed to corrupt the staged temp, so the call site was never exercised "
					+ "and nothing below proves anything. Writer state: returnedNormally="
					+ writerReturnedNormally.ToString()
					+ ", failure="
					+ (writerFailure?.ToString() ?? "<none>"));
			}
			finally
			{
				if (held)
				{
					writeLock.ReleaseMutex();
				}
			}

			_ = writer.Join(TimeSpan.FromSeconds(60)).Should().BeTrue("the writer must not hang once the lock is free");

			// THE assertion the mutation flips. With the VerifyPublished call removed
			// from Write, the corrupted bytes are published and Write returns happily,
			// telling the caller its config was saved when the file on disk is not a
			// document at all.
			_ = writerReturnedNormally.Should().BeFalse(
				"Write must CALL the post-publish verification: it published bytes that do not parse, so it "
				+ "cannot report success. Returning normally here means D-063 step 7 is not wired into the "
				+ "write path at all, however well VerifyPublished behaves when called directly");

			IOException failure = writerFailure.Should().BeOfType<IOException>(
				"an unverifiable published destination is a recoverable write failure, shaped like the store's others")
				.Which;

			_ = failure.Data["destinationModified"].Should().Be(
				true,
				"File.Replace committed, so the destination really was modified");
			_ = failure.Data["restored"].Should().Be(
				true,
				"this write minted a backup holding revision 1, so D-085 recovery must have restored it");
			_ = failure.InnerException.Should().BeOfType<JsonException>(
				"the parse failure that condemned the publish must survive for diagnosis");

			// D-075 / D-097, on the message this path composes.
			_ = failure.Message.Should().NotContain(
				dataDir,
				"the message must not carry the directory — in installed mode it embeds the Windows account name");
			_ = failure.Message.Should().Contain(
				"browsers.json",
				"the file name alone is enough to identify which document failed");

			// Recovery actually happened: the destination is revision 1 again, not the
			// truncated bytes that were published.
			byte[] expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
				.GetBytes(JsonSerializer.Serialize(first, WriteOptions));
			_ = File.ReadAllBytes(path).SequenceEqual(expected).Should().BeTrue(
				"the destination must be restored byte for byte to the revision the failed write displaced");

			_ = Directory.GetFiles(dataDir, "*.tmp").Should().BeEmpty(
				"the corrupted temp was consumed by File.Replace, which is what made the publish genuinely torn");
		}
		finally
		{
			_ = writer.Join(TimeSpan.FromSeconds(10));
			Directory.Delete(root, recursive: true);
		}
	}

	// Pins that Write's classification catch RETHROWS (`throw;`) rather than throwing
	// the caught instance (`throw failure;`). Otherwise MUTATION-INVISIBLE: swap the
	// bare rethrow for `throw failure;` and the whole suite stays green, because every
	// other assertion looks at the exception TYPE, MESSAGE and DATA — none of which the
	// mutation changes. Only the stack trace does.
	//
	// What the mutation costs: `throw failure;` resets the trace to the rethrow line in
	// Write, erasing every frame BELOW it. The frame that actually says where the write
	// died — VerifyPublished here — disappears, and a field diagnostic report degrades
	// to "something in Write failed". Diagnostics only, but it is free to keep and the
	// comment in Write claims it.
	//
	// It reaches the failure through the SAME seam as
	// Write_WhenTheStagedTempIsCorruptedBeforeThePublish_VerifiesTheDestinationAndRecovers
	// — read that test's comment for the full explanation of why the seam exists and why
	// the mutex must be taken on the TEST thread while Write runs on a BACKGROUND one.
	// The seam is duplicated rather than shared because that test pins the WIRING of
	// step 7 and this one pins the SHAPE of the rethrow; folding them together would
	// make one failure mean two different things. Kept deliberately thin: everything
	// about recovery is already asserted there, so the only assertion here is the frame.
	[Fact]
	public void Write_WhenAFailureEscapes_RethrowKeepsTheFrameThatActuallyFailed()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		string path = Path.Combine(dataDir, "browsers.json");
		AtomicJsonFileStore store = new(backupsDir, writeLockTimeout: TimeSpan.FromSeconds(60));

		// The replacing branch is required: only it reaches the post-publish
		// verification that throws from a helper frame below Write.
		store.Write(path, new BrowsersDocument([CreateBrowser("rev-1")]));

		Exception? escaped = null;

		Thread writer = new(() =>
		{
			try
			{
				store.Write(path, new BrowsersDocument([CreateBrowser("rev-2")]));
			}
			catch (Exception failure)
			{
				escaped = failure;
			}
		})
		{ IsBackground = true };

		using Mutex writeLock = new(initiallyOwned: false, AtomicJsonFileStore.BuildMutexName(path));

		try
		{
			bool held = false;

			try
			{
				held = writeLock.WaitOne(TimeSpan.FromSeconds(10));
				_ = held.Should().BeTrue("the test could not take the lock it needs to park the writer on");

				writer.Start();

				string? staged = null;
				DateTime deadline = DateTime.UtcNow.AddSeconds(10);

				while (staged is null && DateTime.UtcNow < deadline)
				{
					string[] temps = Directory.GetFiles(dataDir, $"{Path.GetFileName(path)}.*.tmp");

					if (temps.Length == 1)
					{
						try
						{
							File.WriteAllText(temps[0], "{ \"browsers\": [ truncated");
							staged = temps[0];
							break;
						}
						catch (IOException)
						{
							// Still being filled; the stream has not closed yet.
						}
					}

					if (writer.Join(TimeSpan.FromMilliseconds(25)))
					{
						break;
					}
				}

				_ = staged.Should().NotBeNull(
					"the test never managed to corrupt the staged temp, so no failure was produced and there is "
					+ "no stack trace to inspect");
			}
			finally
			{
				if (held)
				{
					writeLock.ReleaseMutex();
				}
			}

			_ = writer.Join(TimeSpan.FromSeconds(60)).Should().BeTrue("the writer must not hang once the lock is free");

			Exception failed = escaped.Should().BeOfType<IOException>(
				"the torn publish must escape Write as its usual recoverable write failure")
				.Which;

			_ = failed.StackTrace.Should().Contain(
				"VerifyPublished",
				"Write must rethrow with a bare `throw;`, which preserves the frames BELOW the catch. "
				+ "`throw failure;` resets the trace to the rethrow line and erases VerifyPublished — the one "
				+ "frame that says where the write actually died. Trace was: " + (failed.StackTrace ?? "<null>"));
		}
		finally
		{
			_ = writer.Join(TimeSpan.FromSeconds(10));
			Directory.Delete(root, recursive: true);
		}
	}

	// The other half of (B): the same torn publish with NO backup to restore from —
	// the first-create branch (there was no previous revision) or no backups directory
	// configured. Recovery is impossible, and the honest report is that the destination
	// holds unverified content.
	//
	// Catches: a regression that swallows the failure when it cannot repair it, which
	// would return from Write as a success while the destination is unparseable — the
	// worst possible outcome, because the caller believes its config was saved.
	[Fact]
	public void PostPublishParseFailure_WithNoBackup_ReportsUnverifiedAndNoRestore()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "app.json");
			new AtomicJsonFileStore().Write(path, new AppDocument());

			File.WriteAllText(path, "not json at all");

			// backupPath null is exactly what the first-create branch and a
			// backups-less store hand to verification.
			Action verify = () => AtomicJsonFileStore.VerifyPublished(path, typeof(AppDocument), backupPath: null);

			IOException failure = verify.Should().Throw<IOException>(
				"an unrepairable torn publish must still be REPORTED; swallowing it would tell the caller its config was saved when it was not")
				.Which;

			_ = failure.Data["destinationModified"].Should().Be(
				true,
				"the destination was replaced and its content is unverified");
			_ = failure.Data["restored"].Should().Be(
				false,
				"no backup existed, so the store must report honestly that it could not repair anything rather than implying it did");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// D-063 step 9 (B), the CANNOT-OPEN half, and the deliberate NON-revert that makes
	// it different in kind from the cannot-parse half. Otherwise MUTATION-INVISIBLE:
	// invert the branch to revert-from-backup and the whole suite stays green, because
	// every existing test reaching this path passes backupPath: null — with nothing to
	// revert FROM, a reverting regression and the correct code behave identically.
	//
	// What the mutation costs: the bytes were flushed to disk before the swap, so a
	// destination that merely could not be OPENED is almost certainly a perfectly good
	// write behind a transient sharing violation (antivirus, a search indexer, a
	// foreign handle). Reverting on that evidence would silently discard the user's
	// just-saved config and hand back the previous revision — data loss dressed up as
	// recovery. This test is the only thing standing between that branch and a
	// plausible-looking "make recovery more thorough" edit.
	//
	// DELIBERATE DEVIATION, stated because it changes what the test proves. The
	// obvious blocker is FileShare.None, but probe-verified it CANNOT kill the mutant:
	// FileShare.None also blocks File.Copy, so a reverting regression would fail its
	// own copy, report restored: false and leave the destination untouched — pixel-for-
	// pixel the correct behaviour. FileShare.Write is the share mode that
	// discriminates: it denies the read-back the Read access it needs (so verification
	// genuinely fails the way a sharing violation makes it fail) while permitting the
	// Write access File.Copy needs (so a reverting regression SUCCEEDS at reverting and
	// this test catches it). The seam must keep that property; a "tidy-up" to
	// FileShare.None would quietly disarm the test.
	[Fact]
	public void PostPublishOpenFailure_WithBackupAvailable_ReportsUnverifiedAndRefusesToRevert()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "browsers.json");
			BrowsersDocument published = new([CreateBrowser("published-and-almost-certainly-good")]);
			new AtomicJsonFileStore(backupsDir).Write(path, published);

			// A non-null backup with DIFFERENT content — the case no existing test
			// covers, and the only case in which the mutation is observable at all.
			// Shaped exactly like a store-minted backup so nothing rejects it.
			BrowsersDocument previous = new([CreateBrowser("previous-must-not-come-back")]);
			string backup = Path.Combine(
				backupsDir,
				$"{Path.GetFileName(path)}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.bak");
			File.WriteAllBytes(
				backup,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
					.GetBytes(JsonSerializer.Serialize(previous, WriteOptions)));

			// Captured while the file is still readable, i.e. before the blocker.
			byte[] publishedBytes = File.ReadAllBytes(path);

			IOException failure;
			using (FileStream blocker = new(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Write))
			{
				Action verify = () => AtomicJsonFileStore.VerifyPublished(
					path,
					typeof(BrowsersDocument),
					backup);

				failure = verify.Should().Throw<IOException>(
					"a destination that cannot be read back is UNVERIFIED, and that must be reported rather than assumed fine")
					.Which;
			}

			_ = failure.Data["destinationModified"].Should().Be(
				true,
				"the swap committed, so the destination genuinely was replaced");
			_ = failure.Data["restored"].Should().Be(
				false,
				"a transient open failure must NOT trigger a revert, so there is nothing to report as restored");
			_ = failure.InnerException.Should().BeAssignableTo<IOException>(
				"the sharing violation that defeated the read-back must survive for diagnosis");

			// The load-bearing assertion: the published revision is still there.
			byte[] after = File.ReadAllBytes(path);
			_ = after.SequenceEqual(publishedBytes).Should().BeTrue(
				"an open failure must leave the PUBLISHED bytes in place: they were flushed to disk before the "
				+ "swap and are almost certainly correct, so reverting them would discard a good write");

			byte[] backupBytes = File.ReadAllBytes(backup);
			_ = after.SequenceEqual(backupBytes).Should().BeFalse(
				"the destination must not have been reverted to the backup");
			_ = Resolve<BrowsersDocument>(path).Browsers[0].BrowserId.Should().Be(
				"published-and-almost-certainly-good",
				"the revision on disk must be the one this write published, not the one a revert would have restored");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// Pins the SIZE of the post-publish read retry budget, not just that retrying
	// happens. Otherwise MUTATION-INVISIBLE: cut PostPublishReadAttempts from 3 to 1
	// and the whole suite stays green, because every other test on this path uses a
	// blocker that never lets go, so one attempt and three attempts fail identically.
	//
	// What the mutation costs: the budget exists precisely to ride out a MOMENTARY
	// sharing violation on the destination — antivirus or an indexer touching the file
	// microseconds after the swap. At one attempt there is no second look, so a
	// perfectly good, already-committed write gets reported to the user as unverified.
	// A false write-failure on the picker's interactive path is exactly what this
	// budget is here to prevent, and a future "simplification" to a single attempt
	// looks entirely reasonable without this test.
	//
	// WHY THE ASSERTION IS SHAPED THIS WAY:
	//  - It is a LOWER bound on elapsed time, so machine load, a noisy CI agent and
	//    timer granularity can only push the measurement FURTHER into passing. The
	//    test cannot become flaky in the failing direction.
	//  - 90ms rather than 100ms: three attempts burn two 50ms delays (~100ms), and
	//    Thread.Sleep resolution is bounded by the ~15.6ms default timer tick, so a
	//    genuine two-delay run can legitimately land just under 100. 90 clears that
	//    boundary while still discriminating hard against a 1-attempt budget, which
	//    burns no delays at all and measures ~0ms.
	//  - FileShare.Write is DELIBERATE and must stay: it denies the read-back the Read
	//    access it needs (so the open genuinely fails the way a sharing violation makes
	//    it fail) while still permitting writes. It is the same seam the
	//    refuses-to-revert test above documents at length — keep the two consistent,
	//    and do not "tidy" either of them to FileShare.None.
	[Fact]
	public void PostPublishOpenFailure_ConsumesTheWholeRetryBudget()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "app.json");
			new AtomicJsonFileStore().Write(path, new AppDocument());

			using FileStream blocker = new(path, FileMode.Open, FileAccess.Read, FileShare.Write);

			Stopwatch elapsed = Stopwatch.StartNew();
			try
			{
				AtomicJsonFileStore.VerifyPublished(path, typeof(AppDocument), backupPath: null);
			}
			catch (IOException)
			{
				// Expected: the destination is unopenable for the whole run. WHEN it
				// gives up is the subject here, not THAT it gives up (the sibling tests
				// above pin the reporting).
			}

			elapsed.Stop();

			_ = elapsed.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(
				90,
				"PostPublishReadAttempts is 3, so a persistently unopenable destination must burn two "
				+ "50ms retry delays before being reported unverified; a budget of 1 would return "
				+ "instantly and give a transient sharing violation no time to clear. Measured: "
				+ elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// D-075 / D-097: no full user file path in a message a caller might log verbatim.
	// In installed mode the path embeds the Windows account name, so a caller doing
	// logger.Error(ex.Message) would leak the username. The step-9 recovery errors are
	// new message-producing code and must obey the same rule the lock timeout does.
	//
	// Catches: the natural regression of interpolating destinationPath into the new
	// error text — which reads perfectly, passes every functional test, and silently
	// breaches a locked privacy decision.
	[Fact]
	public void PostPublishFailure_MessageNamesTheFileOnly_FullPathRidesInData()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "settings.json");
			new AtomicJsonFileStore().Write(path, new SettingsDocument());

			File.WriteAllText(path, "{ broken");

			Action verify = () => AtomicJsonFileStore.VerifyPublished(path, typeof(SettingsDocument), backupPath: null);

			IOException failure = verify.Should().Throw<IOException>().Which;

			_ = failure.Message.Should().NotContain(
				dir,
				"the message must not carry the directory — in installed mode it embeds the Windows account name, and any caller logging ex.Message would breach D-075/D-097");
			_ = failure.Message.Should().Contain(
				"settings.json",
				"the file name alone is enough to identify which document failed");
			_ = failure.Data["path"].Should().Be(
				path,
				"a redaction-aware diagnostic sink must still be able to recover the full path");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// D-063 step 9 (C) / D-085 "delete stale .tmp": a process killed between the temp
	// write and the swap leaves an orphan its own finally can never reach, because the
	// process is gone. Only a later write can clean it up.
	//
	// Both directions are asserted in one test on purpose. Pinning only the deletion
	// would be satisfied by a regression that deletes EVERY matching temp regardless of
	// age — which would destroy a live peer writer's in-flight staging the moment the
	// FileShare.None window happened to be open. The fresh-survivor half is what makes
	// the 24h threshold load-bearing in both directions.
	[Fact]
	public void Write_SweepsStaleOrphanTemp_ButKeepsAFreshOne()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "app.json");
			AtomicJsonFileStore store = new();
			store.Write(path, new AppDocument());

			// Orphans of the shape Write itself stages: "<destination>.<guid>.tmp".
			string stale = $"{path}.{Guid.NewGuid():N}.tmp";
			string fresh = $"{path}.{Guid.NewGuid():N}.tmp";
			File.WriteAllText(stale, "{}");
			File.WriteAllText(fresh, "{}");

			// 25 hours is just past the 24h threshold; the fresh one keeps its real
			// last-write time. Age is measured by last-write time, which unlike creation
			// time no filesystem operation here rewrites.
			File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-25));

			store.Write(path, new AppDocument());

			_ = File.Exists(stale).Should().BeFalse(
				"an orphan temp past the staleness threshold must be swept — its owning process is gone, so no finally can ever remove it");
			_ = File.Exists(fresh).Should().BeTrue(
				"a FRESH temp may still belong to a live writer mid-publish and must never be touched by the sweep");

			// The write itself must still be intact and its own temp gone.
			_ = Resolve<AppDocument>(path).SchemaVersion.Should().Be(1);
			_ = Directory.GetFiles(dir, "*.tmp").Should().ContainSingle(
				"only the fresh orphan survives; this write's own temp was consumed by the swap");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// The sweep's blast radius. Documents share a directory in the real layout (app,
	// browsers and settings all sit in data\), so a sweep keyed too loosely would let a
	// write to one document delete another document's in-flight staging.
	//
	// Catches: a regression that matches on "*.tmp" alone, or on a Win32 search pattern
	// instead of an ordinal prefix test. The victim here is deliberately BOTH stale and
	// a legitimate temp of a real sibling document, so age cannot be what saves it —
	// only the name filter can.
	[Fact]
	public void Write_SweepDoesNotTouchAnotherDestinationsOrphanTemp()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "app.json");
			AtomicJsonFileStore store = new();
			store.Write(path, new AppDocument());

			string foreign = Path.Combine(dir, $"browsers.json.{Guid.NewGuid():N}.tmp");
			File.WriteAllText(foreign, "{}");
			File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddHours(-25));

			store.Write(path, new AppDocument());

			_ = File.Exists(foreign).Should().BeTrue(
				"the sweep must only ever consider THIS destination's own temp pattern; a write to app.json deleting browsers.json's staging would corrupt an unrelated document");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
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
		string path = Path.Combine(Path.GetTempPath(), "BroChoos.AtomicJsonFileStore", Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(path);
		return path;
	}

	// Backup-aware layout: a data dir for the document and a pre-provisioned
	// backups dir, mirroring StorageRootResolver's eager provisioning (the store
	// does NOT create the backups dir itself).
	private static (string DataDir, string BackupsDir, string Root) NewStorageLayout()
	{
		string root = Path.Combine(Path.GetTempPath(), "BroChoos.AtomicJsonFileStore", Guid.NewGuid().ToString("N"));
		string dataDir = Path.Combine(root, "data");
		string backupsDir = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(dataDir);
		_ = Directory.CreateDirectory(backupsDir);
		return (dataDir, backupsDir, root);
	}
}
