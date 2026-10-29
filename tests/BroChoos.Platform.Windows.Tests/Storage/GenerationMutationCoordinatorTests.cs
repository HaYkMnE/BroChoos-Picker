using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

public sealed class GenerationMutationCoordinatorTests
{
	[Fact]
	public void Commit_CreateAbsent_ComputesGenerationOne_AndPublishesAfterCommitOnly()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-gen-coordinator-" + Guid.NewGuid().ToString("N")[..12]);
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);
		try
		{
			string destination = Path.Combine(root, "browsers.json");
			GenerationMutationCoordinator coordinator = new(new SystemStorageReadFileSystem());

			long? callbackGeneration = null;
			bool destinationVisibleInsideCallback = false;
			MutationCommitResult result = coordinator.Commit(
				destination,
				new BrowsersDocument([]),
				ExpectedDocumentGeneration.Absent,
				TimeSpan.FromSeconds(2),
				backups,
				(committed, committedDocument) =>
				{
					callbackGeneration = committed;
					_ = committedDocument.Generation.Should().Be(committed);
					destinationVisibleInsideCallback = File.Exists(destination);
				});

			_ = result.Status.Should().Be(MutationCommitStatus.Committed);
			_ = result.ObservedGeneration.Should().Be(0);
			_ = result.CommittedGeneration.Should().Be(1);
			_ = callbackGeneration.Should().Be(1);
			_ = destinationVisibleInsideCallback.Should().BeTrue();

			using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(destination));
			_ = json.RootElement.GetProperty("generation").GetInt64().Should().Be(1);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Commit_UpdateMatch_IncrementsGeneration()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-gen-coordinator-" + Guid.NewGuid().ToString("N")[..12]);
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);
		try
		{
			string destination = Path.Combine(root, "browsers.json");
			AtomicJsonFileStore seedStore = new(backups, TimeSpan.FromSeconds(2));
			seedStore.Write(destination, new BrowsersDocument([]) { Generation = 3 });

			GenerationMutationCoordinator coordinator = new(new SystemStorageReadFileSystem());
			MutationCommitResult result = coordinator.Commit(
				destination,
				new BrowsersDocument([new BrowserDefinition("edge-stable", "Edge", "Microsoft", "stable", "edge.exe", "known-path")]),
				ExpectedDocumentGeneration.Match(3),
				TimeSpan.FromSeconds(2),
				backups,
				(_, _) => { });

			_ = result.Status.Should().Be(MutationCommitStatus.Committed);
			_ = result.ObservedGeneration.Should().Be(3);
			_ = result.CommittedGeneration.Should().Be(4);

			using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(destination));
			_ = json.RootElement.GetProperty("generation").GetInt64().Should().Be(4);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Commit_StaleExpectedGeneration_ConflictsWithoutOverwrite()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-gen-coordinator-" + Guid.NewGuid().ToString("N")[..12]);
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);
		try
		{
			string destination = Path.Combine(root, "browsers.json");
			AtomicJsonFileStore seedStore = new(backups, TimeSpan.FromSeconds(2));
			seedStore.Write(destination, new BrowsersDocument([]) { Generation = 5 });
			string before = File.ReadAllText(destination);

			GenerationMutationCoordinator coordinator = new(new SystemStorageReadFileSystem());
			bool callbackCalled = false;
			MutationCommitResult result = coordinator.Commit(
				destination,
				new BrowsersDocument([]),
				ExpectedDocumentGeneration.Match(4),
				TimeSpan.FromSeconds(2),
				backups,
				(_, _) => callbackCalled = true);

			_ = result.Status.Should().Be(MutationCommitStatus.Conflict);
			_ = result.CommittedGeneration.Should().BeNull();
			_ = result.ObservedGeneration.Should().Be(5);
			_ = callbackCalled.Should().BeFalse();
			_ = File.ReadAllText(destination).Should().Be(before);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Commit_ReplacePath_PreservesPriorGenerationInBackup()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-gen-coordinator-" + Guid.NewGuid().ToString("N")[..12]);
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);
		try
		{
			string destination = Path.Combine(root, "browsers.json");
			AtomicJsonFileStore seedStore = new(backups, TimeSpan.FromSeconds(2));
			seedStore.Write(destination, new BrowsersDocument([]) { Generation = 11 });

			GenerationMutationCoordinator coordinator = new(new SystemStorageReadFileSystem());
			_ = coordinator.Commit(
				destination,
				new BrowsersDocument([]),
				ExpectedDocumentGeneration.Match(11),
				TimeSpan.FromSeconds(2),
				backups,
				(_, _) => { });

			string backup = Directory.GetFiles(backups, "*.bak", SearchOption.TopDirectoryOnly)
				.OrderByDescending(File.GetLastWriteTimeUtc)
				.First();
			using System.Text.Json.JsonDocument backupJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(backup));
			_ = backupJson.RootElement.GetProperty("generation").GetInt64().Should().Be(11);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// F-A INVARIANT, pinned at the CAS authority itself: when the destination EXISTS but
	// cannot be read as a whole document (unparseable body, unparseable envelope, or
	// unreadable bytes), NO expected token may match it. A commit must refuse rather than
	// replace bytes whose generation we cannot know. Overwriting such a file is the job of
	// the explicit quarantine/repair route (ConfigRepairExecutor), never a plain CAS save.
	//
	// Match(0) is the exact token that used to slip through: MatchesExpected computed the
	// unparseable body's generation as 0 and accepted it.
	[Theory]
	[InlineData("{ this is not json")]
	[InlineData("not even an object")]
	public void Commit_UnparseableDestination_RefusesAnyExpectedToken_AndLeavesBytesIntact(string corrupt)
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-gen-coordinator-" + Guid.NewGuid().ToString("N")[..12]);
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);
		try
		{
			string destination = Path.Combine(root, "settings.json");
			File.WriteAllText(destination, corrupt);

			GenerationMutationCoordinator coordinator = new(new SystemStorageReadFileSystem());

			// Neither the absent token nor any Match(n) - including the Match(0) an
			// unparseable body used to yield - may win.
			ExpectedDocumentGeneration[] tokens =
			[
				ExpectedDocumentGeneration.Absent,
				ExpectedDocumentGeneration.Match(0),
				ExpectedDocumentGeneration.Match(1),
			];

			foreach (ExpectedDocumentGeneration token in tokens)
			{
				bool callbackCalled = false;
				MutationCommitResult result = coordinator.Commit(
					destination,
					new SettingsDocument { ControlDepth = SettingsControlDepth.Expert },
					token,
					TimeSpan.FromSeconds(2),
					backups,
					(_, _) => callbackCalled = true);

				_ = result.Status.Should().Be(MutationCommitStatus.Conflict, $"token {token} must not match an unparseable destination");
				_ = callbackCalled.Should().BeFalse();
				_ = File.ReadAllText(destination).Should().Be(corrupt, "the corrupt bytes must be preserved for repair");
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
