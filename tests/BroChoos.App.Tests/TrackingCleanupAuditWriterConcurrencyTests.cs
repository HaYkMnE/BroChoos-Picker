using BroChoos.App.Audit;
using BroChoos.Core.Routing;
using BroChoos.Platform.Windows.Tests.TestSupport;
using FluentAssertions;
using Xunit;
using System.Globalization;
using System.Text.Json;

namespace BroChoos.App.Tests;

public sealed class TrackingCleanupAuditWriterConcurrencyTests
{
	[Fact]
	public async Task LegacyTrimThenAppendSequence_WhenFileIsLocked_ThrowsIoException()
	{
		string testRoot = Path.Combine(Path.GetTempPath(), "brochoos-w73-tests", Guid.NewGuid().ToString("N"));
		string logPath = Path.Combine(testRoot, "cleanup_events.jsonl");
		_ = Directory.CreateDirectory(testRoot);

		try
		{
			string seedLine = BuildAuditLine(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture), changed: false, "seed", []);
			File.WriteAllText(logPath, seedLine + Environment.NewLine);

			using FileStream lockStream = new(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			string secondLine = BuildAuditLine(DateTimeOffset.Parse("2026-01-02T00:00:00Z", CultureInfo.InvariantCulture), changed: true, "tracking.cleaned", ["utm_source"]);
			Action legacyWrite = () => LegacyTrimThenAppend(logPath, secondLine);

			_ = legacyWrite.Should().Throw<IOException>();
		}
		finally
		{
			if (File.Exists(logPath))
			{
				File.Delete(logPath);
			}
			if (Directory.Exists(testRoot))
			{
				Directory.Delete(testRoot, recursive: true);
			}

			TestTempCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(testRoot)!);
		}

		await Task.CompletedTask;
	}

	[Fact]
	public void Write_WhenFileIsTemporarilyLockedByNeighboringWriter_RetriesAndAppends()
	{
		string testRoot = Path.Combine(Path.GetTempPath(), "brochoos-w73-tests", Guid.NewGuid().ToString("N"));
		string logPath = Path.Combine(testRoot, "cleanup_events.jsonl");
		_ = Directory.CreateDirectory(testRoot);

		LocalTrackingCleanupAuditWriter writer = new(logPath);
		TrackingCleanupAuditEvent cleanupEvent = new(
			DateTimeOffset.UtcNow,
			"example.com",
			Changed: true,
			ReasonCode: "tracking.cleaned",
			RemovedKeys: ["utm_source"],
			AppliedPackId: null,
			RawUrl: "https://example.com/?utm_source=secret");

		FileStream? lockStream = null;
		try
		{
			lockStream = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			// The neighbour releases its lock on a DEDICATED thread that is confirmed running
			// before Write starts. A thread-pool Task.Run + Task.Delay could be scheduled late
			// under a parallel test run and miss the writer's bounded retry budget (~600 ms),
			// which made this test fail intermittently for a reason unrelated to the writer.
			using ManualResetEventSlim releaserStarted = new(initialState: false);
			Thread releaser = new(() =>
			{
				releaserStarted.Set();
				Thread.Sleep(200);
				lockStream.Dispose();
				lockStream = null;
			})
			{
				IsBackground = true,
				Name = "audit-lock-releaser",
			};
			releaser.Start();
			_ = releaserStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the lock releaser must be running before the write under test");

			Action write = () => writer.Write(cleanupEvent);
			_ = write.Should().NotThrow();
			_ = releaser.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();

			string[] lines = File.ReadAllLines(logPath);
			_ = lines.Should().ContainSingle();
			_ = lines[0].Should().Contain("\"host\":\"example.com\"");
			_ = lines[0].Should().NotContain("utm_source=secret");
		}
		finally
		{
			lockStream?.Dispose();
			if (File.Exists(logPath))
			{
				File.Delete(logPath);
			}
			if (Directory.Exists(testRoot))
			{
				Directory.Delete(testRoot, recursive: true);
			}

			TestTempCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(testRoot)!);
		}
	}

	// DEFECT #15, PART A. A line that is VALID JSON but whose `ts` is not an RFC3339 string
	// used to escape TryGetTimestamp as a FormatException (the ONLY catch there is
	// JsonException), aborting WriteOnce and therefore the whole audit write - so one bad
	// historical line permanently cost EVERY later cleanup event its audit record.
	// What is asserted is the caller-visible contract, not an internal detail: the new event
	// IS written, and the unreadable line is GONE (the documented "malformed historical lines
	// drop out on rewrite" intent, which is why the surviving line is the new one and not the
	// bad one).
	[Theory]
	[InlineData("{\"ts\":\"n/a\"}", "a non-ISO ts string")]
	[InlineData("{\"ts\":\"2026-13-45\"}", "an out-of-range month")]
	[InlineData("{\"ts\":\"2026-02-30T00:00:00Z\"}", "a day that does not exist")]
	[InlineData("{\"ts\":\"\"}", "an empty ts string")]
	[InlineData("123", "a bare JSON number instead of an object")]
	[InlineData("null", "a bare JSON null instead of an object")]
	[InlineData("{}", "an object with no ts at all")]
	[InlineData("{\"ts\":12345}", "a numeric ts")]
	public void Write_MalformedOrUnreadableTimestampLine_DoesNotAbortTheAuditWrite_AndDropsTheBadLine(
		string poisonedLine,
		string why)
	{
		string testRoot = Path.Combine(Path.GetTempPath(), "brochoos-w73-tests", Guid.NewGuid().ToString("N"));
		string logPath = Path.Combine(testRoot, "cleanup_events.jsonl");
		_ = Directory.CreateDirectory(testRoot);

		try
		{
			string goodHistoricalLine = BuildAuditLine(DateTimeOffset.UtcNow.AddDays(-1), changed: false, "seed", []);
			File.WriteAllLines(logPath, [goodHistoricalLine, poisonedLine]);
			_ = File.ReadAllLines(logPath).Should().HaveCount(2, "the fixture must actually contain the poisoned line before the write under test");

			LocalTrackingCleanupAuditWriter writer = new(logPath);
			TrackingCleanupAuditEvent cleanupEvent = new(
				DateTimeOffset.UtcNow,
				"example.com",
				Changed: true,
				ReasonCode: "tracking.cleaned",
				RemovedKeys: ["utm_source"],
				AppliedPackId: null);

			Action write = () => writer.Write(cleanupEvent);
			_ = write.Should().NotThrow($"a line with {why} must not be able to abort the audit write");

			string[] lines = File.ReadAllLines(logPath);
			_ = lines.Should().HaveCount(2, "the good historical line and the new event survive; the unreadable line drops out");
			_ = lines.Should().Contain(l => l.Contains("\"reason\":\"tracking.cleaned\""), "the new event MUST be written even though a bad historical line preceded it");
			_ = lines.Should().Contain(l => l.Contains("\"reason\":\"seed\""), "a readable in-window historical line is not collateral damage");
		}
		finally
		{
			if (File.Exists(logPath))
			{
				File.Delete(logPath);
			}
			if (Directory.Exists(testRoot))
			{
				Directory.Delete(testRoot, recursive: true);
			}

			TestTempCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(testRoot)!);
		}
	}

	// DEFECT #15, PART B - the poison must not depend on WHERE it sits. The file-realistic
	// framing is "long healthy history, one bad line in the middle, then writes resume"; if
	// the bad line only ever worked at some positions this would pass for the wrong reason.
	[Fact]
	public void Write_BadTimestampLineBuriedInTheMiddleOfHistory_StillWritesTheNewEvent()
	{
		string testRoot = Path.Combine(Path.GetTempPath(), "brochoos-w73-tests", Guid.NewGuid().ToString("N"));
		string logPath = Path.Combine(testRoot, "cleanup_events.jsonl");
		_ = Directory.CreateDirectory(testRoot);

		try
		{
			List<string> seeded = [];
			for (int i = 0; i < 5; i++)
			{
				seeded.Add(BuildAuditLine(DateTimeOffset.UtcNow.AddDays(-10 + i), changed: false, "history", []));
			}
			seeded.Insert(3, "{\"ts\":\"not-a-timestamp\"}");
			File.WriteAllLines(logPath, seeded);

			LocalTrackingCleanupAuditWriter writer = new(logPath);
			TrackingCleanupAuditEvent cleanupEvent = new(
				DateTimeOffset.UtcNow,
				"mid.example",
				Changed: true,
				ReasonCode: "tracking.cleaned",
				RemovedKeys: [],
				AppliedPackId: null);

			Action write = () => writer.Write(cleanupEvent);
			_ = write.Should().NotThrow();

			string[] lines = File.ReadAllLines(logPath);
			_ = lines.Should().HaveCount(6, "5 healthy lines survive plus the new event; only the bad line drops");
			_ = lines.Should().Contain(l => l.Contains("\"host\":\"mid.example\""));
		}
		finally
		{
			if (File.Exists(logPath))
			{
				File.Delete(logPath);
			}
			if (Directory.Exists(testRoot))
			{
				Directory.Delete(testRoot, recursive: true);
			}

			TestTempCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(testRoot)!);
		}
	}

	// DEFECT #16. WriteOnce trims and THEN appends, so the post-write steady state must be
	// EXACTLY the cap - the cap is a cap on the FILE, which is what the responsiveness test's
	// "2000-line cap" describes. Before the fix, TrimRetentionIfNeeded kept up to MaxEntries and
	// the append took the file to MaxEntries + 1. The file is seeded AT the cap so the very
	// first write already overflows, and then three more writes prove the file does not creep by
	// one line per write (which is the failure mode a single write could hide).
	[Fact]
	public void Write_RepeatedWritesPastTheRetentionCap_SettleAtExactlyMaxEntries()
	{
		string testRoot = Path.Combine(Path.GetTempPath(), "brochoos-w73-tests", Guid.NewGuid().ToString("N"));
		string logPath = Path.Combine(testRoot, "cleanup_events.jsonl");
		_ = Directory.CreateDirectory(testRoot);

		int cap = TrackingCleanupAuditPolicy.MaxEntries;

		try
		{
			List<string> atCap = [];
			for (int i = 0; i < cap; i++)
			{
				atCap.Add(BuildAuditLine(DateTimeOffset.UtcNow.AddMinutes(-i), changed: false, "history", []));
			}
			File.WriteAllLines(logPath, atCap);
			_ = File.ReadAllLines(logPath).Should().HaveCount(cap, "the fixture must actually be at the cap before the writes under test");

			LocalTrackingCleanupAuditWriter writer = new(logPath);

			for (int i = 0; i < 4; i++)
			{
				writer.Write(new TrackingCleanupAuditEvent(
					DateTimeOffset.UtcNow,
					"cap.example",
					Changed: true,
					ReasonCode: "tracking.cleaned",
					RemovedKeys: [],
					AppliedPackId: null));
			}

			string[] lines = File.ReadAllLines(logPath);
			_ = lines.Should().HaveCount(cap, $"the steady state of a file whose cap is {cap} must be exactly {cap} lines, not {cap + 1}");

			// THE NEWEST EVENT IS THE ONE THAT SURVIVES. A trim that dropped the wrong end would
			// also land on `cap` lines, so the count alone does not pin the ordering.
			_ = lines[^1].Should().Contain("\"host\":\"cap.example\"", "the just-written event occupies the last line");
			_ = lines[0].Should().Contain("\"reason\":\"history\"", "the oldest retained line is still history, not a duplicate of the newest write");
		}
		finally
		{
			if (File.Exists(logPath))
			{
				File.Delete(logPath);
			}
			if (Directory.Exists(testRoot))
			{
				Directory.Delete(testRoot, recursive: true);
			}

			TestTempCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(testRoot)!);
		}
	}

	// DEFECT #16, the boundary the off-by-one actually lives on. A file sitting at exactly the
	// cap (all in-window) plus ONE write is the smallest reproduction: the pre-fix code kept
	// all 2000 and appended, producing 2001.
	[Fact]
	public void Write_WhenFileAlreadyHoldsExactlyTheCap_StaysAtTheCap()
	{
		string testRoot = Path.Combine(Path.GetTempPath(), "brochoos-w73-tests", Guid.NewGuid().ToString("N"));
		string logPath = Path.Combine(testRoot, "cleanup_events.jsonl");
		_ = Directory.CreateDirectory(testRoot);

		int cap = TrackingCleanupAuditPolicy.MaxEntries;

		try
		{
			List<string> atCap = [];
			for (int i = 0; i < cap; i++)
			{
				atCap.Add(BuildAuditLine(DateTimeOffset.UtcNow.AddMinutes(-i), changed: false, "history", []));
			}
			File.WriteAllLines(logPath, atCap);
			_ = File.ReadAllLines(logPath).Should().HaveCount(cap, "the fixture must start at exactly the cap");

			LocalTrackingCleanupAuditWriter writer = new(logPath);
			writer.Write(new TrackingCleanupAuditEvent(
				DateTimeOffset.UtcNow,
				"boundary.example",
				Changed: true,
				ReasonCode: "tracking.cleaned",
				RemovedKeys: [],
				AppliedPackId: null));

			string[] lines = File.ReadAllLines(logPath);
			_ = lines.Should().HaveCount(cap, "writing one event into a file at the cap must evict one, not grow the file to cap + 1");
			_ = lines.Should().Contain(l => l.Contains("\"host\":\"boundary.example\""), "the newly written event is what the cap must never discard");
		}
		finally
		{
			if (File.Exists(logPath))
			{
				File.Delete(logPath);
			}
			if (Directory.Exists(testRoot))
			{
				Directory.Delete(testRoot, recursive: true);
			}

			TestTempCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(testRoot)!);
		}
	}

	// THE JSON LINE SHAPE IS A CONTRACT. This defect pair is in the writer's retention and
	// timestamp handling, and nothing here may quietly reshape an emitted record: six keys,
	// exactly, in the order the record declares them, all on ONE line.
	[Fact]
	public void Write_EmitsThePinnedJsonLineShape()
	{
		string testRoot = Path.Combine(Path.GetTempPath(), "brochoos-w73-tests", Guid.NewGuid().ToString("N"));
		string logPath = Path.Combine(testRoot, "cleanup_events.jsonl");
		_ = Directory.CreateDirectory(testRoot);

		try
		{
			LocalTrackingCleanupAuditWriter writer = new(logPath);
			writer.Write(new TrackingCleanupAuditEvent(
				new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
				"shape.example",
				Changed: true,
				ReasonCode: "tracking.cleaned",
				RemovedKeys: ["utm_source", "gclid"],
				AppliedPackId: "affiliate"));

			string[] lines = File.ReadAllLines(logPath);
			_ = lines.Should().ContainSingle();

			using JsonDocument document = JsonDocument.Parse(lines[0]);
			List<string> keys = [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
			_ = keys.Should().Equal("ts", "host", "changed", "reason", "removedKeys", "pack");
			_ = document.RootElement.GetProperty("host").GetString().Should().Be("shape.example");
			_ = document.RootElement.GetProperty("removedKeys").EnumerateArray().Select(e => e.GetString()).Should().Equal("utm_source", "gclid");
			_ = document.RootElement.GetProperty("pack").GetString().Should().Be("affiliate");
		}
		finally
		{
			if (File.Exists(logPath))
			{
				File.Delete(logPath);
			}
			if (Directory.Exists(testRoot))
			{
				Directory.Delete(testRoot, recursive: true);
			}

			TestTempCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(testRoot)!);
		}
	}

	private static void LegacyTrimThenAppend(string logPath, string newLine)
	{
		DateTimeOffset cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(90);
		List<string> kept = [];

		foreach (string line in File.ReadLines(logPath))
		{
			if (TryGetTimestamp(line, out DateTimeOffset ts) && ts >= cutoff)
			{
				kept.Add(line);
			}
		}

		if (kept.Count > 2000)
		{
			int skip = kept.Count - 2000;
			kept = [.. kept.Skip(skip)];
		}

		File.WriteAllLines(logPath, kept);
		File.AppendAllLines(logPath, [newLine]);
	}

	private static bool TryGetTimestamp(string line, out DateTimeOffset timestamp)
	{
		try
		{
			using JsonDocument json = JsonDocument.Parse(line);
			if (!json.RootElement.TryGetProperty("ts", out JsonElement tsElement))
			{
				timestamp = default;
				return false;
			}

			if (tsElement.ValueKind is JsonValueKind.String &&
				tsElement.GetDateTimeOffset() is DateTimeOffset value)
			{
				timestamp = value;
				return true;
			}
		}
		catch (JsonException)
		{
			// mirror legacy behavior
		}

		timestamp = default;
		return false;
	}

	private static string BuildAuditLine(DateTimeOffset timestamp, bool changed, string reason, IReadOnlyList<string> removedKeys)
	{
		return JsonSerializer.Serialize(new
		{
			ts = timestamp.UtcDateTime,
			host = "example.com",
			changed,
			reason,
			removedKeys,
			pack = (string?)null,
		});
	}
}
