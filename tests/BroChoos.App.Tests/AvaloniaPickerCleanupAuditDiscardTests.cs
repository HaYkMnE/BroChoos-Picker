using BroChoos.App.Audit;
using BroChoos.App.Picker;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.App.Tests;

// The start-up step AvaloniaPicker.ShowAsync runs before its UI thread: remove a leftover
// cleanup_events.jsonl when cleanup is off, once per process, fail-open. Tested through the
// internal static body and through RunPreUiStartup on a real AvaloniaPicker instance, so no
// Avalonia and no UI thread are started. Nothing here touches %LocalAppData%: the fakes never
// reach the disk and the real-writer case uses a temp folder.
public sealed class AvaloniaPickerCleanupAuditDiscardTests
{
	[Fact]
	public void TwoCalls_DiscardRunsExactlyOnce()
	{
		int guard = 0;
		FakeDiscarder discarder = new(CleanupAuditDiscardOutcome.Deleted);
		RecordingLogger logger = new();

		AvaloniaPicker.DiscardDisabledCleanupAuditOnce(ref guard, cleanupEnabled: false, discarder, () => logger);
		AvaloniaPicker.DiscardDisabledCleanupAuditOnce(ref guard, cleanupEnabled: false, discarder, () => logger);

		_ = discarder.Calls.Should().Be(1, "the guard lets the discard run once per picker (once per process)");
		_ = discarder.EnabledSeen.Should().Equal(false);
		_ = logger.Entries.Should().BeEmpty("a successful delete is not a problem");
	}

	// The guard is per picker instance: a separate guard means a separate run.
	[Fact]
	public void SeparateGuards_RunIndependently()
	{
		int first = 0;
		int second = 0;
		FakeDiscarder discarder = new(CleanupAuditDiscardOutcome.NotPresent);

		AvaloniaPicker.DiscardDisabledCleanupAuditOnce(ref first, cleanupEnabled: false, discarder, () => new RecordingLogger());
		AvaloniaPicker.DiscardDisabledCleanupAuditOnce(ref second, cleanupEnabled: false, discarder, () => new RecordingLogger());

		_ = discarder.Calls.Should().Be(2);
	}

	// The failure is logged as a warning carrying ONLY the exception type: no path, no message.
	[Fact]
	public void Failed_LogsOneWarning_WithTheExceptionTypeOnly()
	{
		int guard = 0;
		FakeDiscarder discarder = new(CleanupAuditDiscardOutcome.Failed, failureType: nameof(IOException));
		RecordingLogger logger = new();

		Action run = () => AvaloniaPicker.DiscardDisabledCleanupAuditOnce(ref guard, cleanupEnabled: false, discarder, () => logger);

		_ = run.Should().NotThrow("the step is fail-open");
		_ = logger.Entries.Should().ContainSingle();
		RecordingLogger.Entry entry = logger.Entries[0];
		_ = entry.Level.Should().Be(LogLevel.Warning);
		_ = entry.Exception.Should().BeNull("the exception object (and its message) is never handed to the logger");
		_ = entry.Fields.Should().ContainKey("ExceptionType").WhoseValue.Should().Be("IOException");
		_ = entry.Fields.Keys.Should().BeEquivalentTo(["ExceptionType", "{OriginalFormat}"], "no other field (path, host) is logged");
		_ = entry.Message.Should().Contain("IOException")
			.And.NotContain("cleanup_events.jsonl")
			.And.NotContain(":\\")
			.And.NotContain("LocalAppData");
	}

	// Every outcome except Failed is silent. The enum is internal, so a public theory takes it as int.
	[Theory]
	[InlineData((int)CleanupAuditDiscardOutcome.CleanupEnabled)]
	[InlineData((int)CleanupAuditDiscardOutcome.NotPresent)]
	[InlineData((int)CleanupAuditDiscardOutcome.Deleted)]
	[InlineData((int)CleanupAuditDiscardOutcome.SkippedLink)]
	[InlineData((int)CleanupAuditDiscardOutcome.SkippedUnexpectedPath)]
	public void NonFailedOutcome_LogsNothing(int outcomeValue)
	{
		CleanupAuditDiscardOutcome outcome = (CleanupAuditDiscardOutcome)outcomeValue;
		_ = Enum.IsDefined(outcome).Should().BeTrue();
		int guard = 0;
		RecordingLogger logger = new();

		AvaloniaPicker.DiscardDisabledCleanupAuditOnce(ref guard, cleanupEnabled: false, new FakeDiscarder(outcome), () => logger);

		_ = logger.Entries.Should().BeEmpty();
	}

	// Enabled: the flag reaches the discarder as true, and with the REAL writer the file is left
	// exactly as it was and nothing is logged.
	[Fact]
	public void CleanupEnabled_RealWriter_LeavesTheFileUntouched_AndLogsNothing()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-r8-wiring", Guid.NewGuid().ToString("N"));
		string logs = Path.Combine(root, "BroChoos", "logs");
		string path = Path.Combine(logs, "cleanup_events.jsonl");
		_ = Directory.CreateDirectory(logs);
		const string Content = "{\"ts\":\"2026-01-01T00:00:00Z\",\"host\":\"private.example.org\"}\n";
		File.WriteAllText(path, Content);
		DateTime before = File.GetLastWriteTimeUtc(path);

		try
		{
			int guard = 0;
			FakeDiscarder flagProbe = new(CleanupAuditDiscardOutcome.CleanupEnabled);
			AvaloniaPicker.DiscardDisabledCleanupAuditOnce(ref guard, cleanupEnabled: true, flagProbe, () => new RecordingLogger());
			_ = flagProbe.EnabledSeen.Should().Equal(true);

			int realGuard = 0;
			RecordingLogger logger = new();
			AvaloniaPicker.DiscardDisabledCleanupAuditOnce(ref realGuard, cleanupEnabled: true, new LocalTrackingCleanupAuditWriter(path), () => logger);

			_ = File.ReadAllText(path).Should().Be(Content);
			_ = File.GetLastWriteTimeUtc(path).Should().Be(before);
			_ = logger.Entries.Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// WIRING, ON A REAL AvaloniaPicker INSTANCE. RunPreUiStartup is the exact method ShowAsync runs
	// first; constructing the picker does no work (no Avalonia, no file access), and the fake
	// discarder replaces only the disk step, so nothing here touches %LocalAppData%.
	[Fact]
	public void RunPreUiStartup_DiscardsWithTheInstancesShippedOptions_ExactlyOnce()
	{
		FakeDiscarder discarder = new(CleanupAuditDiscardOutcome.Deleted);
		CountingLoggerFactory loggers = new();
		AvaloniaPicker picker = NewPicker(loggers, discarder);

		picker.RunPreUiStartup();
		picker.RunPreUiStartup();

		_ = discarder.Calls.Should().Be(1, "the per-instance guard runs the discard once");
		_ = discarder.EnabledSeen.Should().Equal([picker.TrackingCleanupOptions.Enabled],
			"the instance passes its own cleanup flag");
		_ = picker.TrackingCleanupOptions.Enabled.Should().BeFalse("this release ships cleanup off");
		_ = loggers.Created.Should().Be(0, "no logger is built when nothing failed");
	}

	// Without an override the discard runs against the SAME writer instance the picker hands to its
	// session, so the file removed is the file the session would write. Checked by reference only:
	// RunPreUiStartup is NOT called here, so the real writer never touches the disk.
	[Fact]
	public void DefaultDiscarder_IsTheInstancesOwnAuditWriter()
	{
		AvaloniaPicker picker = NewPicker(new CountingLoggerFactory(), discarder: null);

		_ = picker.CleanupAuditDiscarderOverride.Should().BeNull("production composition never sets the override");
		_ = picker.CleanupAuditDiscarder.Should().BeSameAs(picker.TrackingCleanupAuditWriter);
	}

	// A failure creates the logger exactly once and logs one warning carrying only the type.
	[Fact]
	public void RunPreUiStartup_Failure_CreatesTheLoggerOnce_AndLogsTheTypeOnly()
	{
		CountingLoggerFactory loggers = new();
		AvaloniaPicker picker = NewPicker(loggers, new FakeDiscarder(CleanupAuditDiscardOutcome.Failed, nameof(UnauthorizedAccessException)));

		picker.RunPreUiStartup();
		picker.RunPreUiStartup();

		_ = loggers.Created.Should().Be(1);
		_ = loggers.Logger.Entries.Should().ContainSingle();
		_ = loggers.Logger.Entries[0].Level.Should().Be(LogLevel.Warning);
		_ = loggers.Logger.Entries[0].Fields.Should().ContainKey("ExceptionType").WhoseValue.Should().Be("UnauthorizedAccessException");
		_ = loggers.Logger.Entries[0].Exception.Should().BeNull();
	}

	private static AvaloniaPicker NewPicker(CountingLoggerFactory loggers, ICleanupAuditDiscarder? discarder)
	{
		// Only the constructor runs, which stores these; none of them is called by RunPreUiStartup.
		// With no override the init accessor is not used, so the production default stays in place.
		return discarder is null
			? new AvaloniaPicker(loggers, null!, null!, null!, null!, null!, null!, null!, null!, null!)
			: new AvaloniaPicker(loggers, null!, null!, null!, null!, null!, null!, null!, null!, null!)
			{
				CleanupAuditDiscarderOverride = discarder,
			};
	}

	private sealed class CountingLoggerFactory : ILoggerFactory
	{
		public int Created { get; private set; }

		public RecordingLogger Logger { get; } = new();

		public ILogger CreateLogger(string categoryName)
		{
			Created++;
			return Logger;
		}

		public void AddProvider(ILoggerProvider provider)
		{
		}

		public void Dispose()
		{
		}
	}

	private sealed class FakeDiscarder(CleanupAuditDiscardOutcome outcome, string? failureType = null) : ICleanupAuditDiscarder
	{
		public int Calls { get; private set; }

		public List<bool> EnabledSeen { get; } = [];

		public CleanupAuditDiscardOutcome DiscardWhenDisabled(bool cleanupEnabled, out string? failure)
		{
			Calls++;
			EnabledSeen.Add(cleanupEnabled);
			failure = failureType;
			return outcome;
		}
	}

	private sealed class RecordingLogger : ILogger
	{
		public sealed record Entry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, string?> Fields);

		public List<Entry> Entries { get; } = [];

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			Dictionary<string, string?> fields = [];
			if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
			{
				foreach (KeyValuePair<string, object?> pair in values)
				{
					fields[pair.Key] = pair.Value?.ToString();
				}
			}

			Entries.Add(new Entry(logLevel, formatter(state, exception), exception, fields));
		}
	}
}
