using System.Globalization;
using BroChoos.Core.Privacy;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

public sealed class ActivationHistoryStoreTests
{
	[Fact]
	public void Record_SerializesAllowlistOnly_AndSkipOneConsumesWithoutEntry()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-history-store-" + Guid.NewGuid().ToString("N")[..12]);
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);
		try
		{
			FixedStorageRoot storageRoot = new(root);
			SystemStorageReadFileSystem readFs = new();
			ActivationHistoryStore store = new(
				storageRoot,
				new ConfigFileObserver(readFs),
				new GenerationMutationCoordinator(readFs));

			ActivationHistoryRecord record = new(
				DateTimeOffset.Parse("2026-01-01T10:00:00Z", CultureInfo.InvariantCulture),
				"example.com",
				"edge-stable",
				"Profile 1",
				"chrome.exe",
				"work-domains");

			ActivationHistoryMutationResult first = store.Record(new ActivationHistoryRecordRequest(
				ExpectedDocumentGeneration.Absent,
				new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: false),
				record));

			_ = first.Applied.Should().BeTrue();
			_ = first.Document!.Entries.Should().ContainSingle();

			string historyPath = Path.Combine(root, "history.json");
			string json = File.ReadAllText(historyPath);
			_ = json.Should().Contain("\"mainDomain\"");
			_ = json.Should().Contain("\"browserId\"");
			_ = json.Should().Contain("\"profileId\"");
			_ = json.Should().Contain("\"confirmedSourceApp\"");
			_ = json.Should().Contain("\"matchedRuleId\"");
			_ = json.IndexOf("url", StringComparison.OrdinalIgnoreCase).Should().BeLessThan(0);
			_ = json.IndexOf("query", StringComparison.OrdinalIgnoreCase).Should().BeLessThan(0);
			_ = json.IndexOf("fragment", StringComparison.OrdinalIgnoreCase).Should().BeLessThan(0);
			_ = json.IndexOf("clipboard", StringComparison.OrdinalIgnoreCase).Should().BeLessThan(0);

			ActivationHistoryMutationResult armSkip = store.Record(new ActivationHistoryRecordRequest(
				ExpectedDocumentGeneration.Match(first.Document.Generation),
				new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: true),
				CandidateRecord: null));

			_ = armSkip.Applied.Should().BeTrue();
			_ = armSkip.Document!.Settings.SkipNextLink.Should().BeTrue();

			ActivationHistoryMutationResult consumeSkip = store.Record(new ActivationHistoryRecordRequest(
				ExpectedDocumentGeneration.Match(armSkip.Document.Generation),
				new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: false),
				CandidateRecord: null));

			_ = consumeSkip.Applied.Should().BeTrue();
			_ = consumeSkip.Document!.Settings.SkipNextLink.Should().BeFalse();
			_ = consumeSkip.Document.Entries.Should().ContainSingle("skip-one must consume without recording a link");
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	[Fact]
	public void ClearConfirmed_UsesCas_AndConflictLeavesFileUnchanged()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-history-store-" + Guid.NewGuid().ToString("N")[..12]);
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);
		try
		{
			FixedStorageRoot storageRoot = new(root);
			SystemStorageReadFileSystem readFs = new();
			ActivationHistoryStore store = new(
				storageRoot,
				new ConfigFileObserver(readFs),
				new GenerationMutationCoordinator(readFs));

			ActivationHistoryRecord record = new(
				DateTimeOffset.UtcNow,
				"example.com",
				"edge-stable",
				null,
				null,
				null);

			ActivationHistoryMutationResult created = store.Record(new ActivationHistoryRecordRequest(
				ExpectedDocumentGeneration.Absent,
				new ActivationHistorySettingsContract(),
				record));

			_ = created.Applied.Should().BeTrue();

			ActivationHistoryMutationResult conflict = store.ClearConfirmed(ExpectedDocumentGeneration.Match(created.Document!.Generation + 10));
			_ = conflict.Applied.Should().BeFalse();
			_ = conflict.Commit.Status.Should().Be(MutationCommitStatus.Conflict);

			ActivationHistoryMutationResult cleared = store.ClearConfirmed(ExpectedDocumentGeneration.Match(created.Document.Generation));
			_ = cleared.Applied.Should().BeTrue();
			_ = cleared.Document!.Entries.Should().BeEmpty();
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	[Fact]
	public void LoadWithOutcome_MissingIsNotDamage_CorruptIsDamage_AndValueIsDefault()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-history-store-" + Guid.NewGuid().ToString("N")[..12]);
		_ = Directory.CreateDirectory(Path.Combine(root, "backups"));
		try
		{
			SystemStorageReadFileSystem readFs = new();
			ActivationHistoryStore store = new(
				new FixedStorageRoot(root),
				new ConfigFileObserver(readFs),
				new GenerationMutationCoordinator(readFs));

			ConfigReadResult<ActivationHistoryDocument> missing = store.LoadWithOutcome();
			_ = missing.Outcome.Should().Be(ConfigReadOutcome.Missing);
			_ = ConfigReadOutcomes.IsDamaged(missing.Outcome).Should().BeFalse();

			File.WriteAllText(Path.Combine(root, "history.json"), "not json at all");
			ConfigReadResult<ActivationHistoryDocument> corrupt = store.LoadWithOutcome();
			_ = corrupt.Outcome.Should().Be(ConfigReadOutcome.Corrupt);
			_ = corrupt.Value.Entries.Should().BeEmpty();
			_ = corrupt.Value.Settings.Enabled.Should().BeTrue("the default document is returned unchanged");
			_ = store.LoadOrDefault().Entries.Should().BeEmpty("LoadOrDefault keeps its behaviour");

			// Over the 8 MiB read ceiling: never read (the observer still says OpenFailed, so no
			// repair is licensed), but the user's history is not in use, so it is reported.
			using (FileStream big = File.Create(Path.Combine(root, "history.json")))
			{
				big.SetLength((8L * 1024 * 1024) + 1);
			}

			ConfigReadResult<ActivationHistoryDocument> tooLarge = store.LoadWithOutcome();
			_ = tooLarge.Outcome.Should().Be(ConfigReadOutcome.TooLarge);
			_ = ConfigReadOutcomes.IsDamaged(tooLarge.Outcome).Should().BeTrue();
			_ = tooLarge.Value.Entries.Should().BeEmpty();
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
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
