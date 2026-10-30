using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BroChoos.App.Logging;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.App.Tests;

// The local problem log (option A): problems.log under the logs folder, one JSON object per
// line, Warning+ always, Information only for the context-step allowlist, and NEVER a URL,
// path, host, user name or exception message.
public sealed partial class ProblemLogTests : IDisposable
{
	private const string SecretUrl = "https://secret.example/path?token=x";
	private const string SecretPath = @"C:\Users\Someone\x";

	private static readonly EventId RouteEvent = new(1, "safe-mode.route");
	private static readonly EventId OtherEvent = new(2, "not-allowlisted");

	private readonly string _root = Path.Combine(Path.GetTempPath(), "brochoos-problemlog-tests", Guid.NewGuid().ToString("N"));
	private readonly string _logPath;

	public ProblemLogTests()
	{
		_ = Directory.CreateDirectory(_root);
		_logPath = Path.Combine(_root, "logs", "problems.log");
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// Best-effort temp cleanup.
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Format_WritesOneJsonObjectPerLine_WithShortCategoryAndExceptionTypeOnly()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath));
		using ProblemLogLoggerProvider provider = new(writer);
		ILogger logger = provider.CreateLogger("BroChoos.App.Picker.PickerSession");

		logger.LogWarning(new InvalidOperationException("boom"), "Picker launch unknown for {BrowserId} ({ErrorCode})", "edge-stable", "launch.helper-timeout");
		_ = writer.Flush(TimeSpan.FromSeconds(5));

		string[] lines = File.ReadAllLines(_logPath);
		_ = lines.Should().ContainSingle();
		using JsonDocument json = JsonDocument.Parse(lines[0]);
		JsonElement root = json.RootElement;
		string ts = root.GetProperty("ts").GetString()!;
		_ = ts.Should().EndWith("Z");
		_ = DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture).Offset.Should().Be(TimeSpan.Zero);
		_ = root.GetProperty("level").GetString().Should().Be("Warning");
		_ = root.GetProperty("category").GetString().Should().Be("PickerSession");
		_ = root.GetProperty("event").GetString().Should().Be("Picker launch unknown for {BrowserId} ({ErrorCode})");
		_ = root.GetProperty("exType").GetString().Should().Be("InvalidOperationException");
		_ = root.GetProperty("data").GetProperty("ErrorCode").GetString().Should().Be("launch.helper-timeout");
		_ = root.GetProperty("data").GetProperty("BrowserId").GetString().Should().Be("edge-stable");
		_ = lines[0].Should().NotContain("boom");
	}

	[Fact]
	public void LevelFilter_WritesWarningAndAbove_DropsDebugTraceAndPlainInformation()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath));
		using ProblemLogLoggerProvider provider = new(writer);
		ILogger logger = provider.CreateLogger("Test");

		logger.LogTrace("trace line");
		logger.LogDebug("debug line");
		logger.LogInformation("plain information line");
		logger.LogWarning("warning line");
		logger.LogError("error line");
		logger.LogCritical("critical line");
		_ = writer.Flush(TimeSpan.FromSeconds(5));

		string[] levels = [.. File.ReadAllLines(_logPath).Select(l => ReadString(l, "level"))];
		_ = levels.Should().Equal("Warning", "Error", "Critical");
	}

	[Fact]
	public void Allowlist_WritesOnlyAllowlistedInformation_AndOnlyIdCodeFieldsOfMarkers()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath));
		using ProblemLogLoggerProvider provider = new(writer);
		ILogger logger = provider.CreateLogger("BroChoos.Core.Runners.SafeModeRunner");

		if (logger.IsEnabled(LogLevel.Information))
		{
			logger.LogInformation(RouteEvent, "Safe mode route {Route} action {ActionId}", "user-requested", "reset-config");
			logger.LogInformation(OtherEvent, "Other {Route}", "user-requested");
		}

		_ = ProblemLog.RecordMarker(writer, "FIRST_FRAME_REQUESTED cards=3");
		_ = ProblemLog.RecordMarker(writer, "LAUNCH_REQUESTED target=edge-stable");
		_ = ProblemLog.RecordMarker(writer, "KEY ARROW delta=1");
		_ = ProblemLog.RecordMarker(writer, "FIRST_RUN_ACTION add-manually admitted browser=custom-x-1234abcd custom=True");
		_ = writer.Flush(TimeSpan.FromSeconds(5));

		string[] lines = File.ReadAllLines(_logPath);
		_ = lines.Select(l => ReadString(l, "event")).Should().Equal("safe-mode.route", "picker.opened", "card.chosen", "first-run.action");
		_ = ReadData(lines[0], "Route").Should().Be("user-requested");
		_ = ReadData(lines[0], "ActionId").Should().Be("reset-config");
		_ = ReadData(lines[1], "cards").Should().Be("3");
		_ = ReadData(lines[2], "target").Should().Be("edge-stable");
		_ = ReadData(lines[3], "action").Should().Be("add-manually");
		_ = ReadData(lines[3], "outcome").Should().Be("admitted");
		_ = lines[3].Should().NotContain("custom-x-1234abcd");
	}

	[Fact]
	public void Redaction_NeverWritesUrlPathHostOrExceptionMessage()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath));
		using ProblemLogLoggerProvider provider = new(writer);
		ILogger logger = provider.CreateLogger("BroChoos.App.Picker.PickerSession");
		InvalidOperationException secretException = new($"failed {SecretUrl} at {SecretPath}");

		logger.LogWarning(secretException, "Opening https://secret.example/path?token=x from C:\\Users\\Someone\\x");
		logger.LogError(secretException, "Launch broke for {ErrorCode} {BrowserId} {Url}", SecretPath, SecretUrl, SecretUrl);
		_ = ProblemLog.RecordMarker(writer, $"LAUNCH_OK target={SecretUrl}");
		_ = ProblemLog.RecordMarker(writer, $"FWD_URL_RECEIVED count={SecretPath}");
		ProblemLog.RecordUnhandled(writer, new UnhandledExceptionEventArgs(secretException, isTerminating: false));
		_ = writer.Flush(TimeSpan.FromSeconds(5));

		string content = File.ReadAllText(_logPath);
		_ = File.ReadAllLines(_logPath).Should().HaveCount(5);
		string[] forbiddenFragments = ["secret", "example", "token", "https", "Someone", "Users", "failed "];
		foreach (string forbidden in forbiddenFragments)
		{
			_ = content.Should().NotContain(forbidden);
		}
	}

	[Fact]
	public void Rotation_AtSizeCap_KeepsOneOldFileAndCurrentUnderCap()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { MaxBytes = 1000 });
		using ProblemLogLoggerProvider provider = new(writer);
		ILogger logger = provider.CreateLogger("Test");

		for (int i = 0; i < 40; i++)
		{
			logger.LogWarning("rotation filler line {Count}", i);
		}

		_ = writer.Flush(TimeSpan.FromSeconds(5));
		string rotated = Path.Combine(_root, "logs", "problems.1.log");
		_ = File.Exists(rotated).Should().BeTrue();
		_ = new FileInfo(_logPath).Length.Should().BeLessThanOrEqualTo(1000);
		_ = Directory.GetFiles(Path.Combine(_root, "logs")).Should().HaveCount(2);
		_ = File.ReadAllLines(rotated).Concat(File.ReadAllLines(_logPath)).Should().OnlyContain(l => ReadString(l, "level") == "Warning");
	}

	[Fact]
	public void Pruning_OnStartup_DropsLinesOlderThan14Days_InBothFiles()
	{
		DateTimeOffset now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
		_ = Directory.CreateDirectory(Path.Combine(_root, "logs"));
		string rotated = Path.Combine(_root, "logs", "problems.1.log");
		File.WriteAllLines(_logPath, [Line("old-current", now.AddDays(-20)), Line("recent-current", now.AddDays(-1))]);
		File.WriteAllLines(rotated, [Line("old-rotated", now.AddDays(-15)), Line("recent-rotated", now.AddDays(-13))]);

		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { Clock = () => now });
		_ = writer.Write(new ProblemLogEntry(LogLevel.Warning, "test", "new-line", null, []));

		// The drain runs on a pool task. On a loaded CI runner (the Debug job, whole solution in
		// parallel) it did not finish inside the old 5 s wait, whose result was discarded, so the
		// file was read before the prune ran. This wait is the test's own, not a product budget:
		// it is generous, and the drain must be confirmed complete before the file is read.
		_ = writer.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue("the startup prune and the append must have been written before the file is read");

		_ = File.ReadAllLines(_logPath).Select(l => ReadString(l, "event")).Should().Equal("recent-current", "new-line");
		_ = File.ReadAllLines(rotated).Select(l => ReadString(l, "event")).Should().Equal("recent-rotated");
	}

	[Fact]
	public async Task ConcurrentWriters_OnSameFile_LoseOrCorruptNoLines()
	{
		ProblemLogWriter first = new(new ProblemLogOptions(_logPath));
		ProblemLogWriter second = new(new ProblemLogOptions(_logPath));

		await Task.WhenAll(
			Task.Run(() => WriteMany(first, "writer-a")),
			Task.Run(() => WriteMany(second, "writer-b")));
		_ = first.Flush(TimeSpan.FromSeconds(10));
		_ = second.Flush(TimeSpan.FromSeconds(10));

		string[] lines = File.ReadAllLines(_logPath);
		_ = lines.Should().HaveCount(400);
		string[] events = [.. lines.Select(l => ReadString(l, "event"))];
		_ = events.Count(e => e == "writer-a").Should().Be(200);
		_ = events.Count(e => e == "writer-b").Should().Be(200);
	}

	[Fact]
	public void UnwritableFolder_IsBestEffort_AndNeverThrows()
	{
		string blocker = Path.Combine(_root, "blocker");
		File.WriteAllText(blocker, "a file where the logs folder should be");
		ProblemLogWriter writer = new(new ProblemLogOptions(Path.Combine(blocker, "logs", "problems.log")));
		using ProblemLogLoggerProvider provider = new(writer);
		ILogger logger = provider.CreateLogger("Test");

		bool crashWritten = true;
		InvalidOperationException failure = new("x");
		Action write = () =>
		{
			_ = writer.Write(new ProblemLogEntry(LogLevel.Error, "test", "x", null, []));
			logger.LogCritical(failure, "still no throw");
			_ = ProblemLog.RecordMarker(writer, "LAUNCH_OK target=edge-stable");
			_ = writer.Flush(TimeSpan.FromSeconds(5));
			crashWritten = writer.WriteNow(new ProblemLogEntry(LogLevel.Critical, "crash", "x", null, []));
		};

		_ = write.Should().NotThrow();
		_ = crashWritten.Should().BeFalse();
		_ = writer.DroppedCount.Should().BeGreaterThanOrEqualTo(4);
	}

	[Fact]
	public void CrashHandlers_RecordExceptionTypeOnly_AndObserveUnobservedTasks()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath));
		UnobservedTaskExceptionEventArgs unobserved = new(new AggregateException(new TimeoutException(SecretUrl)));

		ProblemLog.RecordUnhandled(writer, new UnhandledExceptionEventArgs(new InvalidOperationException(SecretPath), isTerminating: true));
		ProblemLog.RecordUnobserved(writer, unobserved);

		string[] lines = File.ReadAllLines(_logPath);
		_ = lines.Should().HaveCount(2);
		_ = ReadString(lines[0], "level").Should().Be("Critical");
		_ = ReadString(lines[0], "event").Should().Be("crash.unhandled");
		_ = ReadString(lines[0], "exType").Should().Be("InvalidOperationException");
		_ = ReadString(lines[1], "level").Should().Be("Error");
		_ = ReadString(lines[1], "exType").Should().Be("TimeoutException");
		_ = unobserved.Observed.Should().BeTrue();
		_ = string.Concat(lines).Should().NotContain("Someone").And.NotContain("secret");
	}

	[Fact]
	public void Write_ReturnsWithoutWaitingForDisk()
	{
		// A "slow disk": every append on the drain worker takes 300 ms.
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath)) { BeforeAppend = static () => Thread.Sleep(300) };

		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		_ = writer.Write(Entry("first"));
		_ = writer.Write(Entry("second"));
		watch.Stop();

		_ = watch.ElapsedMilliseconds.Should().BeLessThan(150);
		_ = writer.Flush(TimeSpan.FromSeconds(5)).Should().BeTrue();
		_ = Events().Should().Equal("first", "second");
	}

	[Fact]
	public void Queue_IsDrainedInOrder()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { AutoDrain = false });

		_ = writer.Write(Entry("a"));
		_ = writer.Write(Entry("b"));
		_ = writer.Write(Entry("c"));
		_ = File.Exists(_logPath).Should().BeFalse("nothing touches the disk until the drain");

		_ = writer.Flush(TimeSpan.FromSeconds(1)).Should().BeTrue();
		_ = Events().Should().Equal("a", "b", "c");
	}

	[Fact]
	public void Queue_Overflow_DropsOldest_AndWritesDropCountLine()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { AutoDrain = false, QueueCapacity = 3 });

		foreach (string name in new[] { "e0", "e1", "e2", "e3", "e4" })
		{
			_ = writer.Write(Entry(name));
		}

		_ = writer.Flush(TimeSpan.FromSeconds(1)).Should().BeTrue();
		string[] lines = File.ReadAllLines(_logPath);
		_ = lines.Select(l => ReadString(l, "event")).Should().Equal("problemlog.dropped", "e2", "e3", "e4");
		_ = ReadData(lines[0], "Count").Should().Be("2");
		_ = writer.DroppedCount.Should().Be(2);
	}

	[Fact]
	public void CrashWrite_IsSynchronous_AndIncludesEarlierQueuedLines()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { AutoDrain = false });
		_ = writer.Write(Entry("before-1"));
		_ = writer.Write(Entry("before-2"));

		ProblemLog.RecordUnhandled(writer, new UnhandledExceptionEventArgs(new InvalidOperationException("x"), isTerminating: true));

		// No Flush: the crash path itself drained and wrote, on this thread.
		_ = Events().Should().Equal("before-1", "before-2", "crash.unhandled");
	}

	[Fact]
	public void Shutdown_Flush_DrainsQueuedContent()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath)) { BeforeAppend = static () => Thread.Sleep(20) };
		using ProblemLogLoggerProvider provider = new(writer);
		ILogger logger = provider.CreateLogger("Test");
		for (int i = 0; i < 5; i++)
		{
			logger.LogWarning("shutdown line {Count}", i);
		}

		_ = writer.Flush(TimeSpan.FromSeconds(5)).Should().BeTrue();
		_ = File.ReadAllLines(_logPath).Should().HaveCount(5);
	}

	[Fact]
	public void Shutdown_Drain_IsBoundedBy500Ms_EvenWhenTheSinkHangs()
	{
		// A fake sink that hangs far longer than the shutdown budget.
		using ManualResetEventSlim release = new(false);
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath)) { BeforeAppend = () => release.Wait(TimeSpan.FromSeconds(10)) };
		ProblemLogLoggerProvider provider = new(writer);
		_ = writer.Write(Entry("stuck"));

		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		bool drained = writer.Flush(ProblemLog.ShutdownDrainTimeout);
		provider.Dispose();
		watch.Stop();
		release.Set();

		_ = ProblemLog.ShutdownDrainTimeout.Should().Be(TimeSpan.FromMilliseconds(500));
		_ = drained.Should().BeFalse();
		// Two bounded waits (ProcessExit flush + provider dispose), each 500 ms, plus slack.
		_ = watch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(900).And.BeLessThan(5000);
		_ = writer.Flush(TimeSpan.FromSeconds(5)).Should().BeTrue();
	}

	[Fact]
	public void StartupPrune_DeletesStalePruneTempFiles_OnlyOwnOnes()
	{
		string logs = Path.Combine(_root, "logs");
		_ = Directory.CreateDirectory(logs);
		string staleCurrent = Path.Combine(logs, "problems.log.prune-0123456789abcdef0123456789abcdef.tmp");
		string staleRotated = Path.Combine(logs, "problems.1.log.prune-fedcba9876543210fedcba9876543210.tmp");
		string foreign = Path.Combine(logs, "other.tmp");
		File.WriteAllText(staleCurrent, "x");
		File.WriteAllText(staleRotated, "x");
		File.WriteAllText(foreign, "x");
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { AutoDrain = false });

		_ = writer.Write(Entry("one"));
		_ = writer.Flush(TimeSpan.FromSeconds(1));

		_ = File.Exists(staleCurrent).Should().BeFalse();
		_ = File.Exists(staleRotated).Should().BeFalse();
		_ = File.Exists(foreign).Should().BeTrue();
	}

	[Fact]
	public void ReasonCode_IsKept_ForIpcRejectionLines()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { AutoDrain = false });
		using ProblemLogLoggerProvider provider = new(writer);
		ILogger logger = provider.CreateLogger("BroChoos.Platform.Windows.SingleInstance.WindowsSingleInstanceGuard");

		logger.LogWarning("Rejected activation before enqueue ({ReasonCode})", "ipc.trust-anchor.unavailable");
		logger.LogWarning("Rejected activation frame ({ReasonCode})", "request.expiry");
		logger.LogWarning("Rejected activation frame ({ReasonCode})", "secret.example.com");
		_ = writer.Flush(TimeSpan.FromSeconds(1));

		string[] lines = File.ReadAllLines(_logPath);
		_ = ReadData(lines[0], "ReasonCode").Should().Be("ipc.trust-anchor.unavailable");
		_ = ReadData(lines[1], "ReasonCode").Should().Be("request.expiry");
		_ = ReadData(lines[2], "ReasonCode").Should().Be(ProblemLog.UnlistedCode);
		_ = lines[2].Should().NotContain("secret").And.NotContain("example");
	}

	[Fact]
	public void SrcLoggerCalls_UseNoInterpolatedTemplates()
	{
		// Backs CA2254 (.editorconfig, [src/**.cs], error): the problem log writes the message
		// TEMPLATE, so an interpolated template would carry runtime values into the file.
		_ = InterpolatedLoggerTemplate().IsMatch("_logger.LogWarning(ex, $\"bad {x}\");").Should().BeTrue("the probe must catch a known violation");
		_ = InterpolatedLoggerTemplate().IsMatch("_logger.LogWarning($@\"bad {x}\");").Should().BeTrue("$@ verbatim interpolation");
		_ = InterpolatedLoggerTemplate().IsMatch("_logger.LogError(ex, @$\"bad {x}\");").Should().BeTrue("@$ verbatim interpolation");
		_ = InterpolatedLoggerTemplate().IsMatch("_logger.LogWarning(\"ok {X}\", x);").Should().BeFalse();
		_ = InterpolatedLoggerTemplate().IsMatch("_logger.LogWarning(@\"ok {X}\", x);").Should().BeFalse("plain verbatim is constant");

		string src = Path.Combine(FindRepositoryRoot(), "src");
		string[] offenders = [.. Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) && !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase))
			.Where(f => InterpolatedLoggerTemplate().IsMatch(File.ReadAllText(f)))];

		_ = offenders.Should().BeEmpty();
	}

	[Fact]
	public void Prune_IsAttemptedOncePerProcess_EvenWhenItFails()
	{
		DateTimeOffset now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
		_ = Directory.CreateDirectory(Path.Combine(_root, "logs"));
		string rotated = Path.Combine(_root, "logs", "problems.1.log");
		File.WriteAllLines(rotated, [Line("old-rotated", now.AddDays(-30))]);
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { Clock = () => now, AutoDrain = false });

		// No read sharing: ReadAllLines in the prune throws on every attempt.
		using (FileStream holder = new(rotated, FileMode.Open, FileAccess.Read, FileShare.Write))
		{
			_ = writer.Write(Entry("one"));
			_ = writer.Flush(TimeSpan.FromSeconds(1));
			_ = writer.Write(Entry("two"));
			_ = writer.Flush(TimeSpan.FromSeconds(1));
		}

		_ = writer.PruneAttempts.Should().Be(1);
		_ = Events().Should().Equal("one", "two");
	}

	[Fact]
	public void Prune_RewritesViaTempFile_AndLeavesNoTempBehind()
	{
		DateTimeOffset now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
		_ = Directory.CreateDirectory(Path.Combine(_root, "logs"));
		File.WriteAllLines(_logPath, [Line("old", now.AddDays(-20)), Line("recent", now.AddDays(-1))]);
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { Clock = () => now, AutoDrain = false });

		_ = writer.Write(Entry("new"));
		_ = writer.Flush(TimeSpan.FromSeconds(1));

		_ = Events().Should().Equal("recent", "new");
		_ = Directory.GetFiles(Path.Combine(_root, "logs"), "*.tmp").Should().BeEmpty();
	}

	[Fact]
	public void AbandonedMutex_PartialLastLine_IsTerminatedBeforeNextLine()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { AutoDrain = false });

		// First write does this process's one prune, so the partial line below is not pruned away.
		_ = writer.Write(Entry("first"));
		_ = writer.Flush(TimeSpan.FromSeconds(3));
		File.AppendAllText(_logPath, "{\"ts\":\"2026-10-27T11:00:00.000Z\",\"lev");

		// The keeper handle keeps the kernel object alive; the owner thread exits while owning it,
		// which abandons the mutex (closing the owner's handle does not release ownership).
		string name = ProblemLogWriter.BuildMutexName(_logPath);
		using Mutex keeper = new(false, name);
		Thread owner = new(() =>
		{
			using Mutex mutex = new(false, name);
			_ = mutex.WaitOne();
		});
		owner.Start();
		owner.Join();

		_ = writer.Write(Entry("after-abandon"));
		_ = writer.Flush(TimeSpan.FromSeconds(3));

		string[] lines = File.ReadAllLines(_logPath);
		_ = lines.Should().HaveCount(3);
		_ = ReadString(lines[2], "event").Should().Be("after-abandon");
	}

	[Fact]
	public void RotationBlocked_StillAppendsUpToTwiceCap_ThenDropsAndCounts()
	{
		_ = Directory.CreateDirectory(Path.Combine(_root, "logs"));
		File.WriteAllText(_logPath, string.Empty);
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { MaxBytes = 1000, AutoDrain = false });

		// A viewer that shares read/write but NOT delete: File.Move fails.
		using (FileStream viewer = new(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
		{
			for (int i = 0; i < 40; i++)
			{
				_ = writer.Write(Entry("blocked-rotation-filler"));
			}

			_ = writer.Flush(TimeSpan.FromSeconds(3));
		}

		long length = new FileInfo(_logPath).Length;
		_ = length.Should().BeGreaterThan(1000).And.BeLessThanOrEqualTo(2000);
		_ = File.Exists(Path.Combine(_root, "logs", "problems.1.log")).Should().BeFalse();
		_ = writer.DroppedCount.Should().BeGreaterThan(0);
	}

	[Fact]
	public void MutexName_IsSessionLocal_AndPathScoped()
	{
		string name = ProblemLogWriter.BuildMutexName(_logPath);

		_ = name.Should().StartWith(@"Local\BroChoos.ProblemLog.");
		_ = name.Should().NotContain("Global");
		_ = ProblemLogWriter.BuildMutexName(_logPath.ToUpperInvariant()).Should().Be(name);
		_ = ProblemLogWriter.BuildMutexName(Path.Combine(_root, "other.log")).Should().NotBe(name);
	}

	[Theory]
	[InlineData("launch.helper-timeout")]
	[InlineData("launch.error.failed")]
	[InlineData("ipc.frame.length")]
	[InlineData("ipc.peer.frame.fields")]
	[InlineData("ipc.queue.full")]
	[InlineData("consent.discovery.failed")]
	[InlineData("manual-browser.not-a-file")]
	[InlineData("systemfallback.start-failed")]
	[InlineData("storage.helper-timeout")]
	[InlineData("shortlink.consent-required")]
	[InlineData("network.helper-not-found")]
	[InlineData("cleanup.no-change")]
	[InlineData("none")]
	[InlineData("launch.helper-invalid-request")]
	[InlineData("storage.helper-execution-failed")]
	[InlineData("shortlink.redirect-loop")]
	public void ErrorCode_AcceptsRealCodes(string code)
	{
		_ = ProblemLog.TrySanitizeField("ErrorCode", code, out string? safe).Should().BeTrue();
		_ = safe.Should().Be(code);
	}

	[Theory]
	[InlineData("secret.example.com")]
	[InlineData("intranet.corp")]
	[InlineData("bit.ly")]
	[InlineData("Launch.Helper")]
	[InlineData("launch..x")]
	[InlineData("launch.helper_timeout")]
	[InlineData("10.0.0.1")]
	[InlineData("ipc.10")]
	[InlineData("xn--e1afmkfd.xn--p1ai")]
	[InlineData("ipc.xn--p1ai")]
	[InlineData("request.example.com")]
	[InlineData("ipc.example.com")]
	[InlineData("launch.example.com")]
	[InlineData("ack.evil.org")]
	[InlineData("ipc.example.com.url")]
	[InlineData("launch.example.com.failed")]
	[InlineData("launch.brand-new-code")]
	[InlineData("Launch.Helper-Timeout")]
	[InlineData("https://secret.example/path?token=x")]
	[InlineData(@"C:\Users\Someone\x")]
	public void ErrorCode_NotOnAllowlist_IsWrittenAsUnlisted(string value)
	{
		foreach (string key in new[] { "ErrorCode", "Code", "ReasonCode" })
		{
			_ = ProblemLog.TrySanitizeField(key, value, out string? safe).Should().BeTrue();
			_ = safe.Should().Be(ProblemLog.UnlistedCode);
		}
	}

	[Fact]
	public void ErrorCode_RandomNewCode_IsUnlisted()
	{
		string fresh = "launch." + Guid.NewGuid().ToString("N")[..12];

		_ = ProblemLog.TrySanitizeField("ErrorCode", fresh, out string? safe).Should().BeTrue();
		_ = safe.Should().Be(ProblemLog.UnlistedCode);
	}

	[Fact]
	public void EveryAllowlistedCode_IsWrittenAsIs()
	{
		_ = ProblemLog.KnownErrorCodes.Should().NotBeEmpty();
		foreach (string code in ProblemLog.KnownErrorCodes)
		{
			_ = ProblemLog.TrySanitizeField("ErrorCode", code, out string? safe).Should().BeTrue();
			_ = safe.Should().Be(code, "allowlisted code {0} must pass unchanged", code);
		}
	}

	// Architecture check: every code literal a producer in src/ emits must be on the allowlist,
	// so a new code cannot silently degrade to "unlisted". Producers are found two ways:
	// (a) literals of a known code family ("ipc.queue.full", "launch.helper-timeout"), and
	// (b) literals assigned to or passed as an error/reason code (errorCode = "...",
	//     ReasonCode: "...", Reject(url, "..."), Fail(id, "..."), Unavailable("..."), ...).
	[Fact]
	public void EveryProducedCodeLiteral_IsOnTheAllowlist()
	{
		_ = CodeFamilyLiteral().IsMatch("x = \"ipc.queue.full\";").Should().BeTrue();
		_ = CodeProducerLiteral().IsMatch("errorCode = \"request.kind\";").Should().BeTrue();
		_ = CodeProducerLiteral().IsMatch("return Reject(request.Url, \"shortlink.timeout\");").Should().BeTrue();

		string src = Path.Combine(FindRepositoryRoot(), "src");
		HashSet<string> produced = new(StringComparer.Ordinal);
		foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
		{
			if (file.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
				|| file.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
				|| file.EndsWith("ProblemLog.cs", StringComparison.OrdinalIgnoreCase)
				|| file.EndsWith("LocalizationService.cs", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string text = File.ReadAllText(file);
			foreach (Match match in CodeFamilyLiteral().Matches(text))
			{
				_ = produced.Add(match.Groups[1].Value);
			}

			foreach (Match match in CodeProducerLiteral().Matches(text))
			{
				// Only dotted values are codes; "configuration"/"target-missing" are categories.
				if (match.Groups[1].Value.Contains('.', StringComparison.Ordinal))
				{
					_ = produced.Add(match.Groups[1].Value);
				}
			}
		}

		// Non-code strings that share a family prefix (event names, a helper command name).
		string[] notCodes = ["safe-mode.route", "first-run.find-browsers"];
		string[] missing = [.. produced.Except(notCodes).Where(c => !ProblemLog.KnownErrorCodes.Contains(c)).Order(StringComparer.Ordinal)];

		_ = produced.Count.Should().BeGreaterThan(50, "the scan must actually find the producers");
		_ = missing.Should().BeEmpty("add new codes to ProblemLog.KnownErrorCodes");
	}

	// Reflection half of the producer check: catches codes held in constants of ANY family, which
	// the literal grep above only sees for the known families. A const string field counts when
	// its name ends in "Code" or starts with "Error", or it lives in a "...Codes" class, and its
	// value is shaped like a code (dotted lowercase). Helper executables declare no such
	// constants (their codes are inline literals, covered by the grep), so only the three
	// referenced assemblies are reflected.
	[Fact]
	public void EveryCodeConstant_InAppCoreAndPlatform_IsOnTheAllowlist()
	{
		System.Reflection.Assembly[] assemblies =
		[
			typeof(ProblemLog).Assembly,
			typeof(Core.Abstractions.ManualBrowserAdmissionCodes).Assembly,
			typeof(BrowserCatalogPersistenceCodes).Assembly,
		];

		List<string> found = [];
		List<string> missing = [];
		foreach (System.Reflection.Assembly assembly in assemblies)
		{
			Type?[] types;
			try
			{
				types = assembly.GetTypes();
			}
			catch (System.Reflection.ReflectionTypeLoadException ex)
			{
				types = ex.Types;
			}

			foreach (Type type in types.OfType<Type>())
			{
				const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
					| System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
				foreach (System.Reflection.FieldInfo field in type.GetFields(Flags))
				{
					bool codeNamed = field.Name.EndsWith("Code", StringComparison.Ordinal)
						|| field.Name.StartsWith("Error", StringComparison.Ordinal)
						|| type.Name.EndsWith("Codes", StringComparison.Ordinal);
					if (!field.IsLiteral || field.FieldType != typeof(string) || !codeNamed
						|| field.GetRawConstantValue() is not string value || !CodeShaped().IsMatch(value))
					{
						continue;
					}

					found.Add(value);
					if (!ProblemLog.KnownErrorCodes.Contains(value))
					{
						missing.Add($"{type.FullName}.{field.Name} = \"{value}\"");
					}
				}
			}
		}

		// Known constants the scan must see, so a broken filter cannot pass vacuously.
		_ = found.Should().Contain(["launch.private-unsupported", "launch.recursion-guard", "systemfallback.start-failed", "manual-browser.admitted", "storage.persist.unknown"]);
		_ = missing.Should().BeEmpty("add new code constants to ProblemLog.KnownErrorCodes");
	}

	[Fact]
	public void EveryCatalogPersistenceReason_MapsToADistinctAllowlistedCode()
	{
		BrowserCatalogPersistenceReason[] reasons = Enum.GetValues<BrowserCatalogPersistenceReason>();
		string[] codes = [.. reasons.Select(BrowserCatalogPersistenceCodes.ToErrorCode)];

		_ = codes.Should().OnlyContain(c => ProblemLog.KnownErrorCodes.Contains(c));
		_ = codes.Should().OnlyHaveUniqueItems();
		_ = codes.Should().NotContain(BrowserCatalogPersistenceCodes.UnknownCode, "every defined member has its own code");
		_ = codes.Should().NotContain(c => reasons.Any(r => r.ToString() == c), "codes are not enum names");

		string undefined = BrowserCatalogPersistenceCodes.ToErrorCode((BrowserCatalogPersistenceReason)int.MaxValue);
		_ = undefined.Should().Be(BrowserCatalogPersistenceCodes.UnknownCode);
		_ = ProblemLog.KnownErrorCodes.Should().Contain(undefined);
	}

	[GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
	private static partial Regex CodeShaped();

	[GeneratedRegex(@"""((?:launch|storage|shortlink|consent|manual-browser|systemfallback|first-run|ipc|request|ack|frame|fullscreen|network|cleanup)\.[a-z0-9.-]+)""", RegexOptions.CultureInvariant)]
	private static partial Regex CodeFamilyLiteral();

	[GeneratedRegex(@"(?:\b(?:[Ee]rrorCode|[Rr]easonCode|[Cc]ode)\s*[:=]\s*|\b(?:Reject|Fail|Unavailable|Timeout|Unknown|Unchanged)\([^;""]*?)""([a-z][a-z0-9.-]*)""", RegexOptions.CultureInvariant)]
	private static partial Regex CodeProducerLiteral();

	[Theory]
	[InlineData("ipc.request.rejected")]
	[InlineData("ipc.trust-anchor.unavailable")]
	[InlineData("ipc.peer.frame.base64")]
	[InlineData("ipc.request.expired")]
	[InlineData("request.field-count")]
	[InlineData("request.kind")]
	[InlineData("request.url")]
	[InlineData("request.id")]
	[InlineData("request.expiry")]
	[InlineData("ack.kind")]
	[InlineData("ack.id")]
	[InlineData("frame.length")]
	[InlineData("ipc.frame.length")]
	[InlineData("ipc.peer.frame.fields")]
	[InlineData("ipc.queue.full")]
	[InlineData("launch.error.failed")]
	[InlineData("consent.discovery.failed")]
	[InlineData("ack.status")]
	[InlineData("frame.utf8")]
	[InlineData("frame.version")]
	[InlineData("frame.trailing")]
	[InlineData("fullscreen.queue.expired")]
	[InlineData("fullscreen.queue.drop-oldest")]
	public void ReasonCode_AcceptsRealIpcCodes(string code)
	{
		_ = ProblemLog.TrySanitizeField("ReasonCode", code, out string? safe).Should().BeTrue();
		_ = safe.Should().Be(code);
	}

	[Fact]
	public void SameCrashException_ReportedTwice_IsRecordedOnce_WithNoTimeWindow()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath) { AutoDrain = false });
		InvalidOperationException crash = new("x");

		ProblemLog.RecordUnhandled(writer, new UnhandledExceptionEventArgs(crash, isTerminating: false));
		// Longer than the old 1 s window: dedupe is by reference alone now.
		Thread.Sleep(1100);
		ProblemLog.RecordUnhandled(writer, new UnhandledExceptionEventArgs(crash, isTerminating: true));
		ProblemLog.RecordUnhandled(writer, new UnhandledExceptionEventArgs(new InvalidOperationException("y"), isTerminating: true));

		_ = Events().Should().Equal("crash.unhandled", "crash.unhandled");
	}

	[GeneratedRegex(@"\bLog\w*\(\s*(?:[\w.!]+\s*,\s*)*(?:\$@?|@\$)""", RegexOptions.CultureInvariant)]
	private static partial Regex InterpolatedLoggerTemplate();

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? current = new(AppContext.BaseDirectory);
		while (current is not null && !File.Exists(Path.Combine(current.FullName, "BroChoos.sln")))
		{
			current = current.Parent;
		}

		return current?.FullName ?? throw new InvalidOperationException("BroChoos.sln not found above the test output folder.");
	}

	private static ProblemLogEntry Entry(string eventName)
	{
		return new ProblemLogEntry(LogLevel.Warning, "test", eventName, null, []);
	}

	private string[] Events()
	{
		return [.. File.ReadAllLines(_logPath).Select(l => ReadString(l, "event"))];
	}

	private static void WriteMany(ProblemLogWriter writer, string eventName)
	{
		for (int i = 0; i < 200; i++)
		{
			_ = writer.Write(new ProblemLogEntry(LogLevel.Warning, "test", eventName, null, []));
		}
	}

	private static string Line(string eventName, DateTimeOffset ts)
	{
		return ProblemLogWriter.Format(new ProblemLogEntry(LogLevel.Warning, "test", eventName, null, []), ts);
	}

	private static string ReadString(string line, string property)
	{
		using JsonDocument json = JsonDocument.Parse(line);
		return json.RootElement.GetProperty(property).GetString() ?? "<null>";
	}

	private static string? ReadData(string line, string key)
	{
		using JsonDocument json = JsonDocument.Parse(line);
		return json.RootElement.GetProperty("data").GetProperty(key).GetString();
	}
}
