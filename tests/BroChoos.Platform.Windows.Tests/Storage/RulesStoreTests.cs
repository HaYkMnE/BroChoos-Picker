using BroChoos.Core.Routing;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

// W7.1: the rules document persists through the SAME W3 generation/CAS machinery every
// other durable document uses (ConfigFileObserver + GenerationMutationCoordinator +
// AtomicJsonFileStore). These tests pin the three contracts the plan names explicitly:
// round-trip, generation/CAS conflict, and corrupted-document recovery.
public sealed class RulesStoreTests
{
	private static RoutingRule Rule(string id, int order, string host)
	{
		return new RoutingRule(
			id,
			order,
			true,
			new RoutingTargetRef("chrome-stable", null),
			[new RuleCondition(RuleMatchType.HostEquals, host)]);
	}

	private static (RulesStore Store, string Root, string Backups) NewStore()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-rules-store-" + Guid.NewGuid().ToString("N")[..12]);
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);
		SystemStorageReadFileSystem readFs = new();
		RulesStore store = new(
			new FixedStorageRoot(root),
			new ConfigFileObserver(readFs),
			new GenerationMutationCoordinator(readFs));
		return (store, root, backups);
	}

	[Fact]
	public void Save_ThenLoad_RoundTripsRulesAndGeneration()
	{
		(RulesStore store, string root, _) = NewStore();
		try
		{
			RulesDocument document = new([Rule("work-domains", 1, "docs.example.com")]);

			MutationCommitResult commit = store.Save(document, ExpectedDocumentGeneration.Absent);

			_ = commit.Status.Should().Be(MutationCommitStatus.Committed);
			_ = commit.CommittedGeneration.Should().Be(1);

			RuleSetDocument loaded = store.LoadOrDefault();
			_ = loaded.Generation.Should().Be(1);
			_ = loaded.Rules.Should().ContainSingle();
			_ = loaded.Rules[0].RuleId.Should().Be("work-domains");
			_ = loaded.Rules[0].Conditions[0].Value.Should().Be("docs.example.com");

			// The document is a real file in the storage root, on the established name.
			_ = File.Exists(Path.Combine(root, "rules.json")).Should().BeTrue();
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void LoadOrDefault_AbsentFile_ReturnsEmptyGenerationZeroDocument()
	{
		(RulesStore store, string root, _) = NewStore();
		try
		{
			RuleSetDocument loaded = store.LoadOrDefault();

			_ = loaded.Generation.Should().Be(0);
			_ = loaded.Rules.Should().BeEmpty();
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void Save_WithStaleExpectedGeneration_IsAConflictAndLeavesFileUnchanged()
	{
		(RulesStore store, string root, _) = NewStore();
		try
		{
			_ = store.Save(new RulesDocument([Rule("first", 1, "docs.example.com")]), ExpectedDocumentGeneration.Absent);

			// The destination is now generation 1; a caller that believes it is still
			// absent (or names the wrong generation) must be refused, not silently win.
			MutationCommitResult stale = store.Save(
				new RulesDocument([Rule("second", 2, "other.example.com")]),
				ExpectedDocumentGeneration.Absent);

			_ = stale.Status.Should().Be(MutationCommitStatus.Conflict);

			RuleSetDocument loaded = store.LoadOrDefault();
			_ = loaded.Generation.Should().Be(1);
			_ = loaded.Rules[0].RuleId.Should().Be("first");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void Save_WithMatchingGeneration_AdvancesGenerationAndReplacesRules()
	{
		(RulesStore store, string root, _) = NewStore();
		try
		{
			_ = store.Save(new RulesDocument([Rule("first", 1, "docs.example.com")]), ExpectedDocumentGeneration.Absent);

			MutationCommitResult second = store.Save(
				new RulesDocument([Rule("second", 2, "other.example.com")]),
				ExpectedDocumentGeneration.Match(1));

			_ = second.Status.Should().Be(MutationCommitStatus.Committed);
			_ = second.CommittedGeneration.Should().Be(2);

			RuleSetDocument loaded = store.LoadOrDefault();
			_ = loaded.Generation.Should().Be(2);
			_ = loaded.Rules[0].RuleId.Should().Be("second");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// RECOVERY: a corrupted rules document must NOT throw and must NOT be used. The
	// store reports "no rules" (fail open: suggestions are optional), and because the
	// corrupt file still occupies the destination the generation/CAS guard refuses to
	// overwrite it blindly — the same fail-closed direction every other W3 document
	// takes. Nothing here deletes the user's file.
	[Fact]
	public void LoadOrDefault_CorruptedDocument_FailsOpenAndRefusesBlindOverwrite()
	{
		(RulesStore store, string root, _) = NewStore();
		try
		{
			string path = Path.Combine(root, "rules.json");
			File.WriteAllText(path, "{ this is not json");

			RuleSetDocument loaded = store.LoadOrDefault();

			_ = loaded.Generation.Should().Be(0);
			_ = loaded.Rules.Should().BeEmpty();

			MutationCommitResult overwrite = store.Save(
				new RulesDocument([Rule("recovered", 1, "docs.example.com")]),
				ExpectedDocumentGeneration.Absent);

			_ = overwrite.Status.Should().Be(MutationCommitStatus.Conflict);
			_ = File.ReadAllText(path).Should().Be("{ this is not json");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// The outcome names WHY the default was returned. Missing is a first run (no notice);
	// a corrupt or newer-schema file is damage the picker reports.
	[Fact]
	public void LoadWithOutcome_ReportsLoadedMissingCorruptAndIncompatible()
	{
		(RulesStore store, string root, _) = NewStore();
		try
		{
			string path = Path.Combine(root, "rules.json");

			_ = store.LoadWithOutcome().Outcome.Should().Be(ConfigReadOutcome.Missing);

			_ = store.Save(new RulesDocument([Rule("first", 1, "docs.example.com")]), ExpectedDocumentGeneration.Absent);
			ConfigReadResult<RuleSetDocument> loaded = store.LoadWithOutcome();
			_ = loaded.Outcome.Should().Be(ConfigReadOutcome.Loaded);
			_ = loaded.Value.Rules.Should().ContainSingle();

			File.WriteAllText(path, "{ this is not json");
			ConfigReadResult<RuleSetDocument> corrupt = store.LoadWithOutcome();
			_ = corrupt.Outcome.Should().Be(ConfigReadOutcome.Corrupt);
			_ = corrupt.Value.Rules.Should().BeEmpty();
			_ = ConfigReadOutcomes.IsDamaged(corrupt.Outcome).Should().BeTrue();

			File.WriteAllText(path, """{"schemaVersion":99,"schemaRevision":0,"generation":1,"rules":[]}""");
			ConfigReadResult<RuleSetDocument> newer = store.LoadWithOutcome();
			_ = newer.Outcome.Should().Be(ConfigReadOutcome.Newer);
			_ = newer.Value.Generation.Should().Be(0);
			_ = File.ReadAllText(path).Should().Contain("\"schemaVersion\":99", "the read path never rewrites the file");

			using (FileStream big = File.Create(path))
			{
				big.SetLength((8L * 1024 * 1024) + 1);
			}

			_ = store.LoadWithOutcome().Outcome.Should().Be(ConfigReadOutcome.TooLarge);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void Save_AtomicWrite_LeavesAPriorGenerationBackup()
	{
		(RulesStore store, string root, string backups) = NewStore();
		try
		{
			_ = store.Save(new RulesDocument([Rule("first", 1, "docs.example.com")]), ExpectedDocumentGeneration.Absent);
			_ = store.Save(new RulesDocument([Rule("second", 2, "other.example.com")]), ExpectedDocumentGeneration.Match(1));

			string[] backupNames = Directory.GetFiles(backups, "rules.json.*");
			_ = backupNames.Should().NotBeEmpty(
				"the W3 atomic store mints a backup of the prior generation before replacing it");
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
