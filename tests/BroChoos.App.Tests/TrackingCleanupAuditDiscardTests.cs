using System.Diagnostics;
using BroChoos.App.Audit;
using BroChoos.Platform.Windows.Tests.TestSupport;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// LocalTrackingCleanupAuditWriter.DiscardWhenDisabled: with cleanup off, a leftover
// cleanup_events.jsonl (hosts written by earlier internal builds) is removed, and nothing else is.
// Every case runs in its own temp tree shaped like the real one (<root>\BroChoos\logs\...); none
// touches %LocalAppData%.
public sealed class TrackingCleanupAuditDiscardTests : IDisposable
{
	private const string Content = "{\"ts\":\"2026-01-01T00:00:00Z\",\"host\":\"private.example.org\"}\n";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "brochoos-r7-discard", Guid.NewGuid().ToString("N"));

	private string LogsDirectory => Path.Combine(_root, "BroChoos", "logs");

	private string LogPath => Path.Combine(LogsDirectory, "cleanup_events.jsonl");

	[Fact]
	public void Disabled_ExistingFile_IsDeleted()
	{
		_ = Directory.CreateDirectory(LogsDirectory);
		File.WriteAllText(LogPath, Content);

		CleanupAuditDiscardOutcome outcome = new LocalTrackingCleanupAuditWriter(LogPath).DiscardWhenDisabled(cleanupEnabled: false, out string? failure);

		_ = outcome.Should().Be(CleanupAuditDiscardOutcome.Deleted);
		_ = failure.Should().BeNull();
		_ = File.Exists(LogPath).Should().BeFalse("the leftover host log is removed when cleanup is off");
		_ = Directory.Exists(LogsDirectory).Should().BeTrue("only the file goes, the logs folder stays");
	}

	[Fact]
	public void Disabled_NoFile_IsANoOp()
	{
		_ = Directory.CreateDirectory(LogsDirectory);

		CleanupAuditDiscardOutcome outcome = new LocalTrackingCleanupAuditWriter(LogPath).DiscardWhenDisabled(cleanupEnabled: false, out _);

		_ = outcome.Should().Be(CleanupAuditDiscardOutcome.NotPresent);
	}

	[Fact]
	public void Enabled_ExistingFile_IsUntouched()
	{
		_ = Directory.CreateDirectory(LogsDirectory);
		File.WriteAllText(LogPath, Content);
		DateTime before = File.GetLastWriteTimeUtc(LogPath);

		CleanupAuditDiscardOutcome outcome = new LocalTrackingCleanupAuditWriter(LogPath).DiscardWhenDisabled(cleanupEnabled: true, out _);

		_ = outcome.Should().Be(CleanupAuditDiscardOutcome.CleanupEnabled);
		_ = File.ReadAllText(LogPath).Should().Be(Content);
		_ = File.GetLastWriteTimeUtc(LogPath).Should().Be(before);
	}

	[Fact]
	public void Disabled_FileLockedByAnotherHandle_DoesNotThrow_AndReportsTheTypeOnly()
	{
		_ = Directory.CreateDirectory(LogsDirectory);
		File.WriteAllText(LogPath, Content);
		LocalTrackingCleanupAuditWriter writer = new(LogPath);

		// A throw here fails the test, so a plain call is the "does not throw" assertion.
		CleanupAuditDiscardOutcome outcome;
		string? failure;
		using (new FileStream(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			outcome = writer.DiscardWhenDisabled(cleanupEnabled: false, out failure);
		}

		_ = outcome.Should().Be(CleanupAuditDiscardOutcome.Failed);
		_ = failure.Should().Be(nameof(IOException), "only the exception type is reported, never its message or the path");
		_ = File.Exists(LogPath).Should().BeTrue();
	}

	[Fact]
	public void Disabled_DifferentFileName_IsNeverDeleted()
	{
		_ = Directory.CreateDirectory(LogsDirectory);
		string other = Path.Combine(LogsDirectory, "problems.log");
		File.WriteAllText(other, "keep");

		CleanupAuditDiscardOutcome outcome = new LocalTrackingCleanupAuditWriter(other).DiscardWhenDisabled(cleanupEnabled: false, out _);

		_ = outcome.Should().Be(CleanupAuditDiscardOutcome.SkippedUnexpectedPath);
		_ = File.ReadAllText(other).Should().Be("keep");
	}

	[Fact]
	public void Disabled_DirectoryWithTheFileName_IsLeftAlone()
	{
		_ = Directory.CreateDirectory(LogPath);

		CleanupAuditDiscardOutcome outcome = new LocalTrackingCleanupAuditWriter(LogPath).DiscardWhenDisabled(cleanupEnabled: false, out _);

		_ = outcome.Should().Be(CleanupAuditDiscardOutcome.SkippedUnexpectedPath);
		_ = Directory.Exists(LogPath).Should().BeTrue();
	}

	// A symlink at the file path is not followed and not removed; its target survives. Creating a
	// file symlink needs Developer Mode or the symlink privilege, so the case skips without them.
	[SkippableFact]
	public void Disabled_SymlinkedFile_IsNotFollowed_AndTargetSurvives()
	{
		_ = Directory.CreateDirectory(LogsDirectory);
		string outside = Path.Combine(_root, "outside");
		_ = Directory.CreateDirectory(outside);
		string target = Path.Combine(outside, "precious.txt");
		File.WriteAllText(target, "precious");

		try
		{
			_ = File.CreateSymbolicLink(LogPath, target);
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
		{
			Skip.If(true, $"file symlinks are not permitted for this user ({ex.GetType().Name})");
		}

		CleanupAuditDiscardOutcome outcome = new LocalTrackingCleanupAuditWriter(LogPath).DiscardWhenDisabled(cleanupEnabled: false, out _);

		_ = outcome.Should().Be(CleanupAuditDiscardOutcome.SkippedLink);
		_ = File.ReadAllText(target).Should().Be("precious", "the link target is never touched");
		_ = (File.GetAttributes(LogPath) & FileAttributes.ReparsePoint).Should().Be(FileAttributes.ReparsePoint, "the link itself is left in place");
	}

	// A junction at the logs folder (no privilege needed) redirects to a folder BroChoos does not
	// own; the file "inside" it is really outside and must survive.
	[SkippableFact]
	public void Disabled_JunctionedLogsFolder_IsNotFollowed_AndTargetFileSurvives()
	{
		_ = Directory.CreateDirectory(Path.Combine(_root, "BroChoos"));
		string outside = Path.Combine(_root, "outside-logs");
		_ = Directory.CreateDirectory(outside);
		string targetFile = Path.Combine(outside, "cleanup_events.jsonl");
		File.WriteAllText(targetFile, Content);

		Skip.IfNot(CreateJunction(LogsDirectory, outside), "could not create a junction in the temp folder");

		CleanupAuditDiscardOutcome outcome = new LocalTrackingCleanupAuditWriter(LogPath).DiscardWhenDisabled(cleanupEnabled: false, out _);

		_ = outcome.Should().Be(CleanupAuditDiscardOutcome.SkippedLink);
		_ = File.ReadAllText(targetFile).Should().Be(Content, "a file behind a junctioned logs folder is never deleted");
	}

	public void Dispose()
	{
		try
		{
			// Remove links first so the recursive delete can never walk through one.
			foreach (string link in new[] { LogPath, LogsDirectory })
			{
				if (Path.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
				{
					if (Directory.Exists(link))
					{
						Directory.Delete(link);
					}
					else
					{
						File.Delete(link);
					}
				}
			}

			if (Directory.Exists(_root))
			{
				Directory.Delete(_root, recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best-effort temp cleanup.
		}

		// The fixed parent goes once the last per-test folder is gone; best effort, never throws.
		TestTempCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(_root)!);
	}

	private static bool CreateJunction(string link, string target)
	{
		ProcessStartInfo start = new("cmd.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		start.ArgumentList.Add("/c");
		start.ArgumentList.Add("mklink");
		start.ArgumentList.Add("/J");
		start.ArgumentList.Add(link);
		start.ArgumentList.Add(target);

		using Process? process = Process.Start(start);
		return process is not null
			&& process.WaitForExit(10_000)
			&& process.ExitCode == 0
			&& Directory.Exists(link)
			&& (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
	}
}
