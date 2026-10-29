using BroChoos.App.Localization;
using BroChoos.App.Settings;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using System.Globalization;
using Xunit;

namespace BroChoos.App.Tests;

// W8.1 F1 — CONFLICT RECOVERY WITHOUT DEFEATING CAS.
//
// The defect this pins: SetControlDepth compared every save against _observedGeneration,
// which was only ever set in the constructor. Once a concurrent writer landed, the next
// save conflicted and the view model NEVER refreshed its observed generation, so every
// subsequent save from that window conflicted too — a permanent wedge until the window was
// recreated.
//
// The fix must recover WITHOUT weakening compare-and-swap: after a conflict the view model
// re-observes the real generation (the generation the coordinator itself observed at
// conflict time, or Absent when the file is gone) and retries against THAT. A save that is
// still based on genuinely stale knowledge is still refused.
// LocalizationService.CurrentCulture is process-wide static state; serialized with the other culture-touching classes.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class SettingsConflictRecoveryTests
{
	// A scripted store that lets a test drive the exact sequence of commit results and
	// generations a real coordinator would produce, without a disk.
	private sealed class ScriptedSettingsStore : ISettingsStore
	{
		private SettingsDocument _document = new();
		private bool _absent = true;

		public int SaveCalls { get; private set; }

		// The expectation the LAST save was attempted with, so a test can prove the retry
		// used fresh knowledge rather than repeating the stale token.
		public ExpectedDocumentGeneration? LastExpected { get; private set; }

		// Simulates a DIFFERENT writer committing a document behind this store's back.
		public void ExternalWriterCommits(SettingsControlDepth depth)
		{
			_document = _document with { ControlDepth = depth, Generation = _document.Generation + 1 };
			_absent = false;
		}

		public SettingsDocument LoadOrDefault()
		{
			return _document;
		}

		public ExpectedDocumentGeneration ObserveExpectedGeneration()
		{
			return _absent
				? ExpectedDocumentGeneration.Absent
				: ExpectedDocumentGeneration.Match(_document.Generation);
		}

		public MutationCommitResult Save(SettingsDocument document, ExpectedDocumentGeneration expectedGeneration)
		{
			SaveCalls++;
			LastExpected = expectedGeneration;

			// Real CAS semantics: the expectation must match the CURRENT state.
			bool matches = expectedGeneration.ExpectsDocument
				? !_absent && expectedGeneration.Value == _document.Generation
				: _absent;

			if (!matches)
			{
				return MutationCommitResult.Conflict(_absent ? null : _document.Generation);
			}

			_document = document with { Generation = _document.Generation + 1 };
			_absent = false;
			return MutationCommitResult.Committed(_document.Generation, _document.Generation - 1);
		}
	}

	[Fact]
	public void Conflict_DoesNotWedge_TheNextSaveSucceeds()
	{
		ScriptedSettingsStore store = new();
		SettingsViewModel vm = new(store);

		// A concurrent writer lands BEFORE our first save: the store is now at
		// generation 1 while the view model still believes the document is absent.
		store.ExternalWriterCommits(SettingsControlDepth.Guided);

		MutationCommitResult first = vm.SetControlDepth(SettingsControlDepth.Expert)!;
		_ = first.Status.Should().Be(MutationCommitStatus.Conflict, "the stale first save must be refused, not silently win");

		// F1: the very next save must SUCCEED because the view model re-observed the
		// real generation after the conflict.
		MutationCommitResult second = vm.SetControlDepth(SettingsControlDepth.Expert)!;
		_ = second.Status.Should().Be(MutationCommitStatus.Committed, "a conflict must not permanently wedge subsequent saves");
		_ = store.LastExpected!.Value.ExpectsDocument.Should().BeTrue("the retry must expect the now-present document");
		_ = store.LastExpected!.Value.Value.Should().Be(1, "the retry must use the generation the coordinator observed");
		_ = vm.ControlDepth.Should().Be(SettingsControlDepth.Expert);
	}

	// The recovery must NOT become "accept anything": a write whose expectation is still
	// genuinely stale is refused again, forever, until it re-observes.
	[Fact]
	public void Recovery_DoesNotDefeatCas_AStillStaleWriterIsRefusedAgain()
	{
		ScriptedSettingsStore store = new();
		SettingsViewModel vm = new(store);

		store.ExternalWriterCommits(SettingsControlDepth.Guided);
		_ = vm.SetControlDepth(SettingsControlDepth.Expert)!.Status.Should().Be(MutationCommitStatus.Conflict);

		// Another concurrent writer lands between our conflict and our retry. The
		// refreshed expectation (generation 1) is stale again, so this save conflicts -
		// the view model must not have "learned to accept" anything.
		store.ExternalWriterCommits(SettingsControlDepth.Standard);

		MutationCommitResult retry = vm.SetControlDepth(SettingsControlDepth.Expert)!;
		_ = retry.Status.Should().Be(MutationCommitStatus.Conflict);

		// And it recovers once more, proving the refresh is repeatable rather than a
		// one-shot that itself wedges.
		MutationCommitResult afterSecondConflict = vm.SetControlDepth(SettingsControlDepth.Expert)!;
		_ = afterSecondConflict.Status.Should().Be(MutationCommitStatus.Committed);
	}

	// F3 at the view-model boundary: a first save against an ABSENT document must succeed.
	[Fact]
	public void FreshStore_FirstSaveSucceeds_WithoutAPriorExternalWrite()
	{
		ScriptedSettingsStore store = new();
		SettingsViewModel vm = new(store);

		MutationCommitResult first = vm.SetControlDepth(SettingsControlDepth.Expert)!;

		_ = first.Status.Should().Be(MutationCommitStatus.Committed, "the first save on a fresh store must not conflict");
		_ = first.CommittedGeneration.Should().Be(1);
		_ = store.LastExpected!.Value.ExpectsDocument.Should().BeFalse("a fresh store is expected as Absent");
	}

	// F-C: with NO store composed, the status must say the change is NOT DURABLE - not the
	// refusal sentence, which would be a false claim about a conflict that never happened.
	[Fact]
	public void NoStore_StatusSaysNotDurable_NotRefused()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			SettingsViewModel vm = new() { SelectedControlDepth = SettingsControlDepth.Expert };

			_ = vm.ControlDepthSaveStatusText.Should().NotBeNullOrWhiteSpace();
			_ = vm.ControlDepthSaveStatusText.Should().Contain("not saved", "the not-durable message must say the change is not saved");
			_ = vm.ControlDepthSaveStatusText.Should().NotContain("refused", "a null store is not a refusal - the conflict wording would be false");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	// F-A THROUGH THE UI PATH the reviewer reproduced: a corrupt on-disk settings.json plus a
	// depth selection must NOT overwrite the corrupt bytes. This drives the VM's real save
	// path (the property the ComboBox binds) against a real SettingsStore on a temp root.
	[Fact]
	public void UiSavePath_CorruptFile_IsRefused_AndLeavesTheFileByteForByteIntact()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-settings-corrupt-" + Guid.NewGuid().ToString("N")[..12]);
		_ = Directory.CreateDirectory(Path.Combine(root, "backups"));
		try
		{
			string path = Path.Combine(root, "settings.json");
			const string corrupt = "{ this is not json";
			File.WriteAllText(path, corrupt);

			SettingsStore store = new(
				new FixedRoot(root),
				new ConfigFileObserver(new SystemStorageReadFileSystem()),
				new GenerationMutationCoordinator(new SystemStorageReadFileSystem()));

			// The exact call the editor's ComboBox makes.
			SettingsViewModel vm = new(store) { SelectedControlDepth = SettingsControlDepth.Expert };

			_ = vm.ControlDepthSaveStatusText.Should().NotBe(LocalizationService.GetString("Settings.Depth.Saved"), "the corrupt file must not report a successful save");
			_ = File.ReadAllText(path).Should().Be(corrupt, "the corrupt file must be left for repair/quarantine, not overwritten");
		}
		finally
		{
			try
			{
				Directory.Delete(root, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

	private sealed class FixedRoot(string root) : IStorageRootResolver
	{
		public string Root { get; } = root;
		public string Backups { get; } = Path.Combine(root, "backups");
		public string Logs { get; } = Path.Combine(root, "logs");
		public string Temp { get; } = Path.Combine(root, "temp");
	}
}
