using BroChoos.App.Audit;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Routing;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

// Captures Console.Out (the [picker] marker), so it is serialized with the other console-capture classes.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class TrackingCleanupPickerFlowTests
{
	[Fact]
	public async Task CleanupEnabled_CardLaunchUsesCleanedUrl_AndAuditNeverStoresFullUrl()
	{
		RecordingCleanupAuditWriter auditWriter = new();
		FakeLaunchHelperClient launchClient = new();
		PickerSession session = NewSession(
			"https://example.com/path?utm_source=newsletter&x=1#frag",
			launchClient,
			auditWriter,
			new TrackingCleanupOptions(Enabled: true));

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		_ = launchClient.LastRequest.Should().NotBeNull();
		_ = launchClient.LastRequest!.Url.Should().Be("https://example.com/path?x=1#frag");

		_ = auditWriter.Events.Should().ContainSingle();
		_ = auditWriter.Events[0].Host.Should().Be("example.com");
		_ = auditWriter.Events[0].RemovedKeys.Should().Equal("utm_source");
		_ = auditWriter.Events[0].RawUrl.Should().BeNull();
	}

	[Fact]
	public async Task CleanupEnabled_UseOriginalOneShot_RevertsLaunchToOriginalUrl()
	{
		RecordingCleanupAuditWriter auditWriter = new();
		FakeLaunchHelperClient launchClient = new();
		PickerSession session = NewSession(
			"https://example.com/path?utm_source=newsletter&x=1",
			launchClient,
			auditWriter,
			new TrackingCleanupOptions(Enabled: true));

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.UseOriginal, 0)]);
		session.ClickCardForTests(1);

		_ = launchClient.LastRequest.Should().NotBeNull();
		_ = launchClient.LastRequest!.Url.Should().Be("https://example.com/path?utm_source=newsletter&x=1");
	}

	// PRIVACY: with cleanup OFF (the shipped state, AvaloniaPicker: Enabled: false) nothing was cleaned,
	// so there is nothing to audit. Writing an event anyway would put the host of EVERY opened link
	// into cleanup_events.jsonl for 90 days. Both the synchronous and the live background branch are
	// checked, and a URL with tracking parameters is used so "nothing to remove" cannot explain it.
	[Fact]
	public async Task CleanupDisabled_WritesNoAuditEvent_OnEitherBranch()
	{
		RecordingCleanupAuditWriter auditWriter = new();
		FakeLaunchHelperClient launchClient = new();
		PickerSession session = NewSession(
			"https://private.example.org/path?utm_source=newsletter&x=1",
			launchClient,
			auditWriter,
			new TrackingCleanupOptions(Enabled: false));

		session.ApplyCleanupAtSessionStartInBackgroundForTests();
		_ = session.PendingCleanupAuditForTests.Should().BeNull("no background audit write is started when cleanup is off");

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		_ = launchClient.LastRequest!.Url.Should().Be("https://private.example.org/path?utm_source=newsletter&x=1", "cleanup is off, so the link is opened unchanged");
		_ = auditWriter.Events.Should().BeEmpty("cleanup is off, so no host may be written to the cleanup audit");
		_ = session.CompleteCountForTests.Should().Be(1, "the session still completes normally");
	}

	// ...and with cleanup ON the existing audit still happens, including for a link where
	// nothing was removed (Changed = false), which is the pre-existing behaviour.
	[Fact]
	public async Task CleanupEnabled_StillWritesOneAuditEvent_EvenWhenNothingWasRemoved()
	{
		RecordingCleanupAuditWriter auditWriter = new();
		PickerSession session = NewSession(
			"https://example.com/plain?x=1",
			new FakeLaunchHelperClient(),
			auditWriter,
			new TrackingCleanupOptions(Enabled: true));

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		_ = auditWriter.Events.Should().ContainSingle();
		_ = auditWriter.Events[0].Host.Should().Be("example.com");
		_ = auditWriter.Events[0].Changed.Should().BeFalse();
	}

	// The cleanup audit is a diagnostic: a writer failure at session start (file held by another
	// process, disk full / access denied, mutex timeout) must not escape and kill the picker before
	// the link can be opened. The failure is recorded by exception TYPE only.
	[Theory]
	[InlineData(typeof(IOException))]
	[InlineData(typeof(UnauthorizedAccessException))]
	[InlineData(typeof(TimeoutException))]
	public void AuditWriterThrowsAtSessionStart_PickerStillOpens_AndFailureNamesTypeOnly(Type exceptionType)
	{
		RecordingLogger logger = new();
		ThrowingCleanupAuditWriter auditWriter = new(exceptionType);
		PickerSession session = NewSession(
			"https://example.com/secret-path?utm_source=newsletter&x=1",
			new FakeLaunchHelperClient(),
			auditWriter,
			new TrackingCleanupOptions(Enabled: true),
			logger);

		TextWriter original = Console.Out;
		using StringWriter capture = new();
		Action open = session.InitializeForTests;
		try
		{
			Console.SetOut(capture);
			_ = open.Should().NotThrow("an audit write failure must never block the link");
		}
		finally
		{
			Console.SetOut(original);
		}

		_ = auditWriter.Calls.Should().Be(1);
		_ = session.CardCountForTests.Should().Be(1, "the picker opened with its card");
		_ = session.CompleteCountForTests.Should().Be(0, "the session is not terminal");
		_ = session.EffectiveUrlForTests.Should().Be("https://example.com/secret-path?x=1");

		string output = capture.ToString();
		_ = output.Should().Contain($"CLEANUP_AUDIT_WRITE_FAILED type={exceptionType.Name}");
		_ = output.Should().NotContain("secret-message").And.NotContain("secret-path").And.NotContain("example.com");
		_ = logger.Warnings.Should().ContainSingle(m => m.Contains(exceptionType.Name, StringComparison.Ordinal));
		_ = logger.Warnings.Should().NotContain(m => m.Contains("secret", StringComparison.Ordinal) || m.Contains("example.com", StringComparison.Ordinal));
		_ = logger.SawExceptionObject.Should().BeFalse("the exception object (and its message) is never handed to the logger");
	}

	// Live branch: the audit write runs on a pool thread. A throwing writer there must not
	// fault the task (nothing escapes), and the failure is still recorded by type only.
	[Fact]
	public async Task BackgroundAuditWrite_ThrowingWriter_DoesNotEscape_AndTaskCompletesCleanly()
	{
		ThrowingCleanupAuditWriter auditWriter = new(typeof(IOException));
		PickerSession session = NewSession(
			"https://example.com/p?utm_source=x",
			new FakeLaunchHelperClient(),
			auditWriter,
			new TrackingCleanupOptions(Enabled: true));

		TextWriter original = Console.Out;
		using StringWriter capture = new();
		try
		{
			Console.SetOut(capture);
			session.ApplyCleanupAtSessionStartInBackgroundForTests();
			Task pending = session.PendingCleanupAuditForTests!;
			_ = pending.Should().NotBeNull();
			Task finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
			_ = finished.Should().BeSameAs(pending, "the background write finishes");
			_ = pending.IsFaulted.Should().BeFalse("WriteCleanupAuditFailOpen swallows the writer's exception");
		}
		finally
		{
			Console.SetOut(original);
		}

		_ = auditWriter.Calls.Should().Be(1);
		_ = capture.ToString().Should().Contain("CLEANUP_AUDIT_WRITE_FAILED type=IOException");
	}

	// Complete() waits for a pending background audit write, so a picker process that exits
	// right after the choice does not lose the line. This test checks THAT it waits, not how
	// long: the drain timeout is raised to 10 s so a loaded machine cannot push the 300 ms
	// write past the product's 1 s bound (that bound is checked by the test below).
	[Fact]
	public async Task Complete_WaitsForPendingBackgroundAuditWrite()
	{
		using BlockingCleanupAuditWriter auditWriter = new(TimeSpan.FromMilliseconds(300));
		PickerSession session = NewSession(
			"https://example.com/p?utm_source=x",
			new FakeLaunchHelperClient(),
			auditWriter,
			new TrackingCleanupOptions(Enabled: true));
		session.CleanupAuditDrainTimeoutForTests = TimeSpan.FromSeconds(10);

		session.ApplyCleanupAtSessionStartInBackgroundForTests();
		_ = auditWriter.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		_ = session.CompleteCountForTests.Should().Be(1);
		_ = auditWriter.Finished.Should().BeTrue("Complete() waited for the audit write before shutting down");
		_ = session.PendingCleanupAuditForTests!.IsCompleted.Should().BeTrue();
	}

	// ...but only up to the bound: a writer that hangs does not hold the session open.
	[Fact]
	public async Task Complete_AuditWait_IsBoundedByOneSecond()
	{
		using BlockingCleanupAuditWriter auditWriter = new(TimeSpan.FromSeconds(10));
		PickerSession session = NewSession(
			"https://example.com/p?utm_source=x",
			new FakeLaunchHelperClient(),
			auditWriter,
			new TrackingCleanupOptions(Enabled: true));

		session.ApplyCleanupAtSessionStartInBackgroundForTests();
		_ = auditWriter.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);
		watch.Stop();

		// Read BEFORE releasing the writer: once released it finishes on its own thread.
		bool finishedWhenSessionEnded = auditWriter.Finished;
		auditWriter.Release();

		_ = PickerSession.CleanupAuditDrainTimeout.Should().Be(TimeSpan.FromSeconds(1));
		_ = session.CleanupAuditDrainTimeoutForTests.Should().Be(PickerSession.CleanupAuditDrainTimeout, "a session uses the product bound unless a test overrides it");
		_ = session.CompleteCountForTests.Should().Be(1);
		_ = finishedWhenSessionEnded.Should().BeFalse("the session ended without waiting for the hung writer");
		_ = watch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(900).And.BeLessThan(5000);
	}

	private sealed class BlockingCleanupAuditWriter(TimeSpan hold) : ITrackingCleanupAuditWriter, IDisposable
	{
		private readonly ManualResetEventSlim _release = new(false);
		private volatile bool _finished;

		public ManualResetEventSlim Started { get; } = new(false);

		public bool Finished => _finished;

		private int _calls;

		// Only the FIRST call (the background write the test starts) blocks. The test entry
		// points then run InitializeForTests, whose synchronous audit write returns at once.
		public void Write(TrackingCleanupAuditEvent cleanupEvent)
		{
			_ = cleanupEvent;
			if (Interlocked.Increment(ref _calls) != 1)
			{
				return;
			}

			Started.Set();
			_ = _release.Wait(hold);
			_finished = true;
		}

		public void Release()
		{
			_release.Set();
		}

		public void Dispose()
		{
			_release.Set();
			_release.Dispose();
			Started.Dispose();
		}
	}

	private static PickerSession NewSession(
		string url,
		FakeLaunchHelperClient launchClient,
		ITrackingCleanupAuditWriter auditWriter,
		TrackingCleanupOptions cleanupOptions,
		ILogger<PickerSession>? logger = null)
	{
		BrowserSnapshot snapshot = new([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
		], 1, DateTimeOffset.UtcNow);

		ActivationRequest activation = new(
			url,
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		return new PickerSession(
			new PickerRequest(activation, snapshot, FirstRunConsentContract.None),
			launchClient,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			logger ?? NullLogger<PickerSession>.Instance,
			cleanupOptions,
			auditWriter);
	}

	private sealed class FakeLaunchHelperClient : ILaunchHelperClient
	{
		public LaunchHelperRequest? LastRequest { get; private set; }

		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null));
		}
	}

	private sealed class FakeStorageHelperClient : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, null, null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.publish-not-used-by-this-test"));
		}
	}

	private sealed class FakeSystemFallbackLauncher : ISystemFallbackLauncher
	{
		public LaunchResult Launch(string url)
		{
			return new LaunchResult(LaunchStatus.Success, "system", null, null, null);
		}
	}

	private sealed class RecordingCleanupAuditWriter : ITrackingCleanupAuditWriter
	{
		public List<TrackingCleanupAuditEvent> Events { get; } = [];

		public void Write(TrackingCleanupAuditEvent cleanupEvent)
		{
			Events.Add(cleanupEvent);
		}
	}

	private sealed class ThrowingCleanupAuditWriter(Type exceptionType) : ITrackingCleanupAuditWriter
	{
		public int Calls { get; private set; }

		public void Write(TrackingCleanupAuditEvent cleanupEvent)
		{
			_ = cleanupEvent;
			Calls++;
			throw (Exception)Activator.CreateInstance(exceptionType, "secret-message https://example.com/secret-path")!;
		}
	}

	private sealed class RecordingLogger : ILogger<PickerSession>
	{
		private readonly Lock _gate = new();
		private readonly List<string> _warnings = [];
		private bool _sawExceptionObject;

		public IReadOnlyList<string> Warnings
		{
			get
			{
				lock (_gate)
				{
					return [.. _warnings];
				}
			}
		}

		public bool SawExceptionObject
		{
			get
			{
				lock (_gate)
				{
					return _sawExceptionObject;
				}
			}
		}

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
			lock (_gate)
			{
				_sawExceptionObject |= exception is not null;
				if (logLevel == LogLevel.Warning)
				{
					_warnings.Add(formatter(state, exception));
				}
			}
		}
	}
}
