using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

// W8.1: the settings document persists through the SAME W3 generation/CAS machinery every
// other durable document uses (ConfigFileObserver + GenerationMutationCoordinator +
// AtomicJsonFileStore). The plan's minimal behavior is "immediate safe-preference save via
// mutation", so these tests pin the contracts that sentence implies and nothing more:
//
//   - a safe preference round-trips through a real settings.json;
//   - the CAS guard refuses a stale writer rather than overwriting a newer document;
//   - a corrupt file fails OPEN (settings are preferences; losing them must never block
//     the settings window, D-v3.5-083) while the destination is still not blindly
//     overwritten;
//   - an envelope-only (pre-W8.1) file reads as the documented default, never as corrupt.
//
// A new storage mechanism is NOT involved: SettingsStore composes the same three W3 types
// RulesStore and ActivationHistoryStore do, and this file is the settings-document proof
// that those types carry the same guarantees for settings.json.
public sealed class SettingsStoreTests
{
	private static (SettingsStore Store, string Root, string Backups) NewStore()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-settings-store-" + Guid.NewGuid().ToString("N")[..12]);
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);
		SystemStorageReadFileSystem readFs = new();
		SettingsStore store = new(
			new FixedStorageRoot(root),
			new ConfigFileObserver(readFs),
			new GenerationMutationCoordinator(readFs));
		return (store, root, backups);
	}

	[Fact]
	public void Save_ThenLoad_RoundTripsPreferenceAndGeneration()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			SettingsDocument document = new() { ControlDepth = SettingsControlDepth.Expert };

			MutationCommitResult commit = store.Save(document, ExpectedDocumentGeneration.Absent);

			_ = commit.Status.Should().Be(MutationCommitStatus.Committed);
			_ = commit.CommittedGeneration.Should().Be(1);

			SettingsDocument loaded = store.LoadOrDefault();
			_ = loaded.Generation.Should().Be(1);
			_ = loaded.ControlDepth.Should().Be(SettingsControlDepth.Expert);

			// The document is a real file in the storage root, on the established name.
			_ = File.Exists(Path.Combine(root, "settings.json")).Should().BeTrue();
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void LoadOrDefault_AbsentFile_ReturnsDefaultGenerationZeroDocument()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			SettingsDocument loaded = store.LoadOrDefault();

			_ = loaded.Generation.Should().Be(0);
			_ = loaded.ControlDepth.Should().Be(SettingsControlDepth.Standard);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// F3: the OBSERVED EXPECTED-GENERATION TOKEN, which is what a writer must send to CAS.
	// This is the seam the view model uses instead of guessing: an ABSENT file is expected
	// as Absent (not Match(0)), and a present file is expected as Match(generation).
	//
	// The defect this exists to close: sending Match(0) for an absent file makes the
	// coordinator's MatchesExpected return false (ExpectsDocument && Access==Absent), so
	// the FIRST save on a clean install silently Conflicts forever.
	[Fact]
	public void ObserveExpectedGeneration_AbsentFile_IsAbsentToken_NotMatchZero()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			ExpectedDocumentGeneration observed = store.ObserveExpectedGeneration();

			_ = observed.ExpectsDocument.Should().BeFalse("an absent file must be expected as Absent, not Match(0)");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void ObserveExpectedGeneration_PresentFile_IsMatchOfItsGeneration()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			ExpectedDocumentGeneration absent = store.ObserveExpectedGeneration();
			_ = store.Save(new SettingsDocument { ControlDepth = SettingsControlDepth.Guided }, absent);

			ExpectedDocumentGeneration observed = store.ObserveExpectedGeneration();

			_ = observed.ExpectsDocument.Should().BeTrue();
			_ = observed.Value.Should().Be(1);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// F3: THE FRESH-INSTALL FIRST SAVE. On a clean install there is no settings.json, so a
	// writer that observed Absent must commit and create generation 1 - deterministically,
	// not "after some other write creates the file".
	[Fact]
	public void FreshInstall_FirstSave_WithObservedAbsent_SucceedsAndCreatesGenerationOne()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			_ = File.Exists(Path.Combine(root, "settings.json")).Should().BeFalse();

			MutationCommitResult first = store.Save(
				new SettingsDocument { ControlDepth = SettingsControlDepth.Expert },
				store.ObserveExpectedGeneration());

			_ = first.Status.Should().Be(MutationCommitStatus.Committed);
			_ = first.CommittedGeneration.Should().Be(1);

			SettingsDocument loaded = store.LoadOrDefault();
			_ = loaded.Generation.Should().Be(1);
			_ = loaded.ControlDepth.Should().Be(SettingsControlDepth.Expert);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// F3's other half, and the CAS guarantee itself: fixing the first save must NOT make a
	// genuinely stale writer win. A second writer still holding the ABSENT token after the
	// file exists must be refused.
	[Fact]
	public void AfterFirstSave_SecondWriterStillExpectingAbsent_IsRefused()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			ExpectedDocumentGeneration absent = store.ObserveExpectedGeneration();
			_ = store.Save(new SettingsDocument { ControlDepth = SettingsControlDepth.Guided }, absent);

			MutationCommitResult stale = store.Save(
				new SettingsDocument { ControlDepth = SettingsControlDepth.Expert },
				ExpectedDocumentGeneration.Absent);

			_ = stale.Status.Should().Be(MutationCommitStatus.Conflict);
			_ = store.LoadOrDefault().ControlDepth.Should().Be(SettingsControlDepth.Guided);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void Save_WithStaleExpectedGeneration_IsAConflictAndLeavesFileUnchanged()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			_ = store.Save(new SettingsDocument { ControlDepth = SettingsControlDepth.Guided }, ExpectedDocumentGeneration.Absent);

			// The destination is now generation 1; a caller that still believes it is
			// absent must be refused, not silently win.
			MutationCommitResult stale = store.Save(
				new SettingsDocument { ControlDepth = SettingsControlDepth.Expert },
				ExpectedDocumentGeneration.Absent);

			_ = stale.Status.Should().Be(MutationCommitStatus.Conflict);

			SettingsDocument loaded = store.LoadOrDefault();
			_ = loaded.Generation.Should().Be(1);
			_ = loaded.ControlDepth.Should().Be(SettingsControlDepth.Guided);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void Save_WithMatchingGeneration_AdvancesGenerationAndReplacesPreference()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			_ = store.Save(new SettingsDocument { ControlDepth = SettingsControlDepth.Standard }, ExpectedDocumentGeneration.Absent);

			MutationCommitResult second = store.Save(
				new SettingsDocument { ControlDepth = SettingsControlDepth.Expert },
				ExpectedDocumentGeneration.Match(1));

			_ = second.Status.Should().Be(MutationCommitStatus.Committed);
			_ = second.CommittedGeneration.Should().Be(2);

			SettingsDocument loaded = store.LoadOrDefault();
			_ = loaded.Generation.Should().Be(2);
			_ = loaded.ControlDepth.Should().Be(SettingsControlDepth.Expert);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// FAIL OPEN, BUT NOT A BLIND OVERWRITE. A corrupt settings file must not throw and must
	// not block the settings window; the honest fallback is the default document. The
	// corrupt file still occupies the destination, so the generation/CAS guard refuses to
	// overwrite it blindly - the same fail-closed direction every other W3 document takes.
	[Fact]
	public void LoadOrDefault_CorruptedDocument_FailsOpenAndRefusesBlindOverwrite()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			string path = Path.Combine(root, "settings.json");
			File.WriteAllText(path, "{ this is not json");

			SettingsDocument loaded = store.LoadOrDefault();

			_ = loaded.Generation.Should().Be(0);
			_ = loaded.ControlDepth.Should().Be(SettingsControlDepth.Standard);

			MutationCommitResult overwrite = store.Save(
				new SettingsDocument { ControlDepth = SettingsControlDepth.Expert },
				ExpectedDocumentGeneration.Absent);

			_ = overwrite.Status.Should().Be(MutationCommitStatus.Conflict);
			_ = File.ReadAllText(path).Should().Be("{ this is not json");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// F-A: THE BLIND SPOT THE PREVIOUS TEST MISSED. The test above sends
	// ExpectedDocumentGeneration.Absent - a token the view model no longer uses. The token
	// the VM ACTUALLY sends is whatever ObserveExpectedGeneration() returns, so the guard
	// must hold for THAT token, not only for Absent.
	//
	// Before the fix, ObserveExpectedGeneration returned Match(0) for a present-but-corrupt
	// file and the coordinator's MatchesExpected computed the unparseable body's generation
	// as 0 - so Match(0) MATCHED, the commit succeeded, and AtomicJsonFileStore.Replace
	// destroyed the corrupt bytes. This test drives the real path and byte-compares the
	// destination to prove it is left intact.
	[Fact]
	public void CorruptPresentFile_ObservedTokenIsNeverMatchable_AndSaveRefusesWithFileIntact()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			string path = Path.Combine(root, "settings.json");
			const string corrupt = "{ this is not json";
			File.WriteAllText(path, corrupt);

			// The token the VM would send - the whole point of the finding.
			ExpectedDocumentGeneration observed = store.ObserveExpectedGeneration();

			MutationCommitResult overwrite = store.Save(
				new SettingsDocument { ControlDepth = SettingsControlDepth.Expert },
				observed);

			_ = overwrite.Status.Should().Be(MutationCommitStatus.Conflict, "a corrupt present file must never be matched, whatever token is sent");
			_ = File.ReadAllText(path).Should().Be(corrupt, "the corrupt bytes must be left EXACTLY intact for repair/quarantine to handle");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// F-A, envelope variant: a file whose ENVELOPE does not parse (a different observation
	// shape - EnvelopeUnparseable, no schema verdict) must be equally non-matchable.
	[Fact]
	public void EnvelopeUnparseableFile_SaveRefusesWithFileIntact()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			string path = Path.Combine(root, "settings.json");
			const string corrupt = "not even an object";
			File.WriteAllText(path, corrupt);

			ExpectedDocumentGeneration observed = store.ObserveExpectedGeneration();
			MutationCommitResult overwrite = store.Save(
				new SettingsDocument { ControlDepth = SettingsControlDepth.Expert },
				observed);

			_ = overwrite.Status.Should().Be(MutationCommitStatus.Conflict);
			_ = File.ReadAllText(path).Should().Be(corrupt);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// F-A, open-failure variant: a file that exists but cannot be read claims nothing about
	// its bytes, so no token may match it either (a locked file must not be replaced).
	[Fact]
	public void UnreadablePresentFile_ObservedTokenIsNeverMatchable()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			string path = Path.Combine(root, "settings.json");
			const string content = "{ \"schemaVersion\": 1, \"schemaRevision\": 0, \"generation\": 4 }";
			File.WriteAllText(path, content);

			// Hold the file open with no sharing so the observer cannot read it.
			using (FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
			{
				ExpectedDocumentGeneration observed = store.ObserveExpectedGeneration();
				MutationCommitResult overwrite = store.Save(
					new SettingsDocument { ControlDepth = SettingsControlDepth.Expert },
					observed);

				_ = overwrite.Status.Should().Be(MutationCommitStatus.Conflict, "a file that could not be read must not be overwritten by a CAS save");
			}

			_ = File.ReadAllText(path).Should().Be(content);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// The ADDITIVE-MEMBER rule, proven at the storage boundary rather than only in the
	// serializer: a settings.json written by the envelope-only 0A-5 build must bind to the
	// documented default. If it did not, the observer would classify it BodyUnparseable -
	// which licenses quarantine-then-overwrite of intact data.
	[Fact]
	public void LoadOrDefault_EnvelopeOnlyFileFromPreW8_1Build_ReadsAsDefaultNotCorrupt()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			string path = Path.Combine(root, "settings.json");
			File.WriteAllText(path, "{\"schemaVersion\":1,\"schemaRevision\":0,\"generation\":2}");

			SettingsDocument loaded = store.LoadOrDefault();

			_ = loaded.Generation.Should().Be(2);
			_ = loaded.ControlDepth.Should().Be(SettingsControlDepth.Standard);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void Save_AtomicWrite_LeavesAPriorGenerationBackup()
	{
		(SettingsStore store, string root, string backups) = NewStore();
		try
		{
			_ = store.Save(new SettingsDocument { ControlDepth = SettingsControlDepth.Standard }, ExpectedDocumentGeneration.Absent);
			_ = store.Save(new SettingsDocument { ControlDepth = SettingsControlDepth.Expert }, ExpectedDocumentGeneration.Match(1));

			string[] backupNames = Directory.GetFiles(backups, "settings.json.*");
			_ = backupNames.Should().NotBeEmpty(
				"the W3 atomic store mints a backup of the prior generation before replacing it");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void PickerLayout_RoundTrips_AndAMissingFileReadsHorizontal()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			_ = store.LoadOrDefault().PickerLayout.Should().Be(SettingsPickerLayout.Horizontal);

			_ = store.Save(new SettingsDocument { PickerLayout = SettingsPickerLayout.Vertical }, ExpectedDocumentGeneration.Absent)
				.Status.Should().Be(MutationCommitStatus.Committed);
			_ = store.LoadOrDefault().PickerLayout.Should().Be(SettingsPickerLayout.Vertical);
			_ = File.ReadAllText(Path.Combine(root, "settings.json")).Should().Contain("\"Vertical\"", "the layout is stored as its stable name");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// ONE SMALL FILE BEFORE THE FIRST FRAME. The picker reads settings.json once at start; this
	// measures that read on a real file, so the cost is a number, not a guess.
	[Fact]
	public void LoadOrDefault_IsCheapEnoughForPickerStart()
	{
		(SettingsStore store, string root, _) = NewStore();
		try
		{
			_ = store.Save(new SettingsDocument { PickerLayout = SettingsPickerLayout.Vertical }, ExpectedDocumentGeneration.Absent);
			_ = store.LoadOrDefault();

			const int Reads = 50;
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			for (int i = 0; i < Reads; i++)
			{
				_ = store.LoadOrDefault();
			}

			watch.Stop();
			double perReadMs = watch.Elapsed.TotalMilliseconds / Reads;
			Console.WriteLine($"SETTINGS_READ_MS per_read={perReadMs:0.000} bytes={new FileInfo(Path.Combine(root, "settings.json")).Length}");
			_ = perReadMs.Should().BeLessThan(20, "the picker reads this file before its first frame");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	private static void DeleteQuietly(string root)
	{
		try
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private sealed class FixedStorageRoot(string root) : IStorageRootResolver
	{
		public string Root { get; } = root;
		public string Backups { get; } = Path.Combine(root, "backups");
		public string Logs { get; } = Path.Combine(root, "logs");
		public string Temp { get; } = Path.Combine(root, "temp");
	}
}
