using System.Diagnostics;
using BroChoos.App.Audit;
using BroChoos.App.Picker;
using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Routing;
using BroChoos.Core.Snapshots;
using BroChoos.Core.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

// Audit follow-ups on the picker session's per-link and post-launch state:
//   1. a short-link result for an older link is dropped after a forward replaced the link;
//   2. a picker kept open by a failure after a deferred deactivation is brought back to front;
//   3. a link forwarded during a launch is held, and a successful launch keeps the picker open
//      for it instead of completing and losing it;
//   5. a forward drops the armed use-original even when the new link has tracking parameters,
//      and the cards are re-ranked against the new link's rules;
//   6a. fail-open catches leave a log line with the exception TYPE only (never the URL).
// Reads localized status text (the forward-waiting note), so it is serialized with the classes
// that switch the static LocalizationService.CurrentCulture.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class PickerAuditFollowupTests
{
	private static readonly TimeSpan EffectBudget = TimeSpan.FromSeconds(10);

	[Fact]
	public async Task StaleShortLinkResolution_FinishingAfterAForward_IsDropped()
	{
		TaskCompletionSource<ShortLinkResolveResponse> resolution = new(TaskCreationOptions.RunContinuationsAsynchronously);
		RecordingLaunchHelperClient launch = new();
		PickerSession session = NewSession(
			"https://bit.ly/start",
			launch,
			network: new PendingNetworkHelperClient(resolution),
			shortLinks: new ShortLinkExpansionOptions(Enabled: true));

		session.ArmShortLinkConsentForNextAttemptForTests();
		Task resolving = session.ResolveShortLinkInBackgroundForTestsAsync();
		Forward(session, "https://second.example/new");

		_ = resolution.TrySetResult(new ShortLinkResolveResponse(
			HelperClientDispatch.Dispatched,
			"https://bit.ly/start",
			"https://resolved.example/old",
			ErrorCode: null,
			FromCache: false));
		await resolving;
		session.ClickCardForTests(1);

		_ = launch.LastRequest.Should().NotBeNull();
		_ = launch.LastRequest!.Url.Should().Be("https://second.example/new", "a resolution started for the previous link must not be applied to the forwarded one");
	}

	[Theory]
	[InlineData(LaunchStatus.Failed)]
	[InlineData(LaunchStatus.Unknown)]
	public async Task DeferredDeactivation_ThenNonSuccess_RequestsForegroundOnce(LaunchStatus status)
	{
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession session = NewSession("https://example.com/", new PendingLaunchHelperClient(pending));
		session.ConfigureClipboardHooksForTests(null, _ => { });

		session.ClickCardForTests(1);
		_ = await session.SimulateExternalDismissalForTestsAsync(); // deferred: the browser took focus
		_ = session.ForegroundRequestCountForTests.Should().Be(0, "nothing is requested while the launch is in flight");

		pending.SetResult(new LaunchHelperOutcome(status, "edge-stable", "launch.start-failed", "launch", "launch.error.failed"));

		_ = (await WaitUntilAsync(() => session.IsErrorStatusVisibleForTests, EffectBudget)).Should().BeTrue();
		_ = session.CompleteCountForTests.Should().Be(0, "the picker stays open for the failure");
		_ = session.ForegroundRequestCountForTests.Should().Be(1, "the open picker lost focus to the launched browser and must be brought back once");
	}

	[Fact]
	public async Task Failure_WithoutADeferredDeactivation_DoesNotRequestForeground()
	{
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession session = NewSession("https://example.com/", new PendingLaunchHelperClient(pending));

		session.ClickCardForTests(1);
		pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Failed, "edge-stable", "launch.start-failed", "launch", "launch.error.failed"));

		_ = (await WaitUntilAsync(() => session.IsErrorStatusVisibleForTests, EffectBudget)).Should().BeTrue();
		_ = session.ForegroundRequestCountForTests.Should().Be(0, "the picker never lost focus, so there is nothing to restore");
	}

	[Fact]
	public async Task ForwardDuringInFlightLaunch_IsHeld_AndSuccessKeepsThePickerOpenForIt()
	{
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession session = NewSession("https://first.example/a", new PendingLaunchHelperClient(pending));

		session.ClickCardForTests(1);
		Forward(session, "https://second.example/b");

		_ = session.EffectiveUrlForTests.Should().Be("https://first.example/a", "a forward must not change the link under an in-flight launch");
		_ = session.FullscreenQueueCountForTests.Should().Be(1, "the forwarded link is held, not dropped");

		pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Success, "edge-stable", null, null, null));

		// The drain runs on the launch task and writes the link (UpdateUrl), then the delivered list,
		// then the foreground request. Waiting only for the FIRST write let the asserts below sample
		// between them; wait for the LAST write, then check the whole final state.
		_ = (await WaitUntilAsync(
			() => session.EffectiveUrlForTests == "https://second.example/b" && session.ForegroundRequestCountForTests >= 1,
			EffectBudget)).Should().BeTrue("the held link is applied once the launch is over");
		_ = session.CompleteCountForTests.Should().Be(0, "completing would end the session and lose the forwarded link");
		_ = session.DeliveredUrlsForTests.Should().Equal("https://second.example/b");
		_ = session.FullscreenQueueCountForTests.Should().Be(0);
		_ = session.ForegroundRequestCountForTests.Should().Be(1, "the launched browser has focus; the picker comes back for the new link");
	}

	// R3-1: a failed launch keeps link A (so "Try again" retries A), keeps the forward held, and
	// shows a non-error "another link is waiting" line next to the failure.
	[Theory]
	[InlineData(LaunchStatus.Failed)]
	[InlineData(LaunchStatus.Unknown)]
	public async Task ForwardHeld_ThenNonSuccess_KeepsLinkA_AndShowsForwardWaiting(LaunchStatus status)
	{
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession session = NewSession("https://first.example/a", new PendingLaunchHelperClient(pending));

		session.ClickCardForTests(1);
		Forward(session, "https://second.example/b");
		pending.SetResult(new LaunchHelperOutcome(status, "edge-stable", "launch.start-failed", "launch", "launch.error.failed"));

		// The failure is painted first and the waiting note appended right after it, so wait for
		// the final rendered state rather than sampling between the two writes.
		_ = (await WaitUntilAsync(() => session.IsForwardWaitingVisibleForTests, EffectBudget)).Should().BeTrue("the user is told another link is waiting");
		_ = session.IsErrorStatusVisibleForTests.Should().BeTrue("the failure for A is shown");
		_ = session.EffectiveUrlForTests.Should().Be("https://first.example/a", "the retry must target A, not the held forward");
		_ = session.FullscreenQueueCountForTests.Should().Be(1, "the forward stays held");
		_ = session.CompleteCountForTests.Should().Be(0);
	}

	// R3-4: the success-path drain runs in a fire-and-forget task. If it throws, the exception is
	// logged by type and the session completes instead of hanging half-open.
	[Fact]
	public async Task SuccessPathDrainThrows_IsLoggedByType_AndCompletesTheSession()
	{
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		RecordingLogger logger = new();
		PickerSession session = NewSession("https://first.example/a", new PendingLaunchHelperClient(pending), logger: logger);
		session.DrainHeldForwardsForTests = () => throw new InvalidOperationException("https://second.example/b");

		session.ClickCardForTests(1);
		Forward(session, "https://second.example/b");
		pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Success, "edge-stable", null, null, null));

		_ = (await WaitUntilAsync(() => session.CompleteCountForTests == 1, EffectBudget)).Should().BeTrue("a failed drain completes the session");
		_ = logger.Snapshot().Should().Contain(m => m.Contains("InvalidOperationException", StringComparison.Ordinal));
		_ = logger.Snapshot().Should().NotContain(m => m.Contains("second.example", StringComparison.Ordinal), "the URL is never logged");
	}

	// R3-2: Esc during a launch with a forward held loses the forward. Not redesigned: it is
	// logged with a count only.
	[Fact]
	public async Task DismissWithAHeldForward_LogsTheLoss_WithoutTheUrl()
	{
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		RecordingLogger logger = new();
		PickerSession session = NewSession("https://first.example/a", new PendingLaunchHelperClient(pending), logger: logger);
		session.ConfigureClipboardHooksForTests(null, _ => { });

		session.ClickCardForTests(1);
		Forward(session, "https://second.example/b");
		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);

		_ = session.CompleteCountForTests.Should().Be(1);
		_ = logger.Snapshot().Should().Contain(m => m.Contains("held forwarded links were not opened", StringComparison.Ordinal));
		_ = logger.Snapshot().Should().NotContain(m => m.Contains("second.example", StringComparison.Ordinal));
		_ = pending.TrySetResult(new LaunchHelperOutcome(LaunchStatus.Failed, "edge-stable", "x", "launch", null));
	}

	// 6c (revised by the tile layout): the tile's screen-reader name is the browser name - the
	// number and reason are no longer drawn, so they are no longer read.
	[Fact]
	public void ChoiceCard_AccessibleName_IsTheBrowserName_AndIsBound()
	{
		ChoiceCardViewModel card = new(
			new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path"),
			2,
			2,
			RankingReasonCode.AllChoices,
			null);

		_ = card.AccessibleName.Should().Be("Google Chrome");

		string axaml = File.ReadAllText(Path.Combine(Root(), "src", "BroChoos.App", "Picker", "PickerWindow.axaml"));
		_ = axaml.Should().Contain("Classes=\"choiceCard\"").And.Contain("AutomationProperties.Name=\"{Binding AccessibleName}\"");
	}

	private static string Root()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BroChoos.sln")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
	}

	[Fact]
	public async Task ForwardedTrackedLink_DoesNotInheritTheArmedUseOriginal()
	{
		RecordingLaunchHelperClient launch = new();
		PickerSession session = NewSession(
			"https://first.example/a?utm_source=news&k=1",
			launch,
			cleanup: new TrackingCleanupOptions(Enabled: true));

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.UseOriginal, 0)]);
		Forward(session, "https://second.example/b?utm_source=mail&z=2");
		session.ClickCardForTests(1);

		_ = launch.LastRequest!.Url.Should().Be(
			"https://second.example/b?z=2",
			"use-original was armed for the previous link; the new tracked link must launch cleaned");
	}

	[Fact]
	public void ForwardedLink_IsRankedAgainstItsOwnRules()
	{
		RecordingLaunchHelperClient launch = new();
		RuleSetDocument rules = new(1, [
			new RoutingRule(
				"second-to-chrome",
				1,
				true,
				new RoutingTargetRef("chrome-stable", null),
				[new RuleCondition(RuleMatchType.HostEquals, "second.example")])
		]);
		PickerSession session = NewSession("https://first.example/a", launch, rules: rules);
		session.InitializeForTests();

		Forward(session, "https://second.example/b");
		session.ClickCardForTests(1);

		_ = launch.LastRequest!.BrowserId.Should().Be("chrome-stable", "the rule matches the forwarded link, so card 1 must be re-ranked to Chrome");
	}

	[Fact]
	public async Task ShortLinkFailure_IsLoggedByExceptionTypeOnly()
	{
		RecordingLogger logger = new();
		PickerSession session = NewSession(
			"https://bit.ly/start",
			new RecordingLaunchHelperClient(),
			network: new ThrowingNetworkHelperClient(),
			shortLinks: new ShortLinkExpansionOptions(Enabled: true),
			logger: logger);

		session.ArmShortLinkConsentForNextAttemptForTests();
		await session.ResolveShortLinkInBackgroundForTestsAsync();

		_ = logger.Snapshot().Should().Contain(m => m.Contains("InvalidOperationException", StringComparison.Ordinal));
		_ = logger.Snapshot().Should().NotContain(m => m.Contains("bit.ly", StringComparison.Ordinal), "the log must never carry the URL");
	}

	[Fact]
	public async Task ClipboardReadFailure_IsLoggedByExceptionTypeOnly()
	{
		RecordingLogger logger = new();
		PickerSession session = NewSession("https://example.com/secret-path", new RecordingLaunchHelperClient(), logger: logger);
		session.ConfigureClipboardHooksForTests(() => throw new InvalidOperationException("https://example.com/secret-path"), null);

		await session.CaptureClipboardAtOpenForTestsAsync();

		_ = logger.Snapshot().Should().Contain(m => m.Contains("InvalidOperationException", StringComparison.Ordinal));
		_ = logger.Snapshot().Should().NotContain(m => m.Contains("secret-path", StringComparison.Ordinal), "the exception message (which may hold the URL) must not be logged");
	}

	[Fact]
	public async Task CancelToClipboardFailure_IsLoggedByExceptionTypeOnly_AndStillDismisses()
	{
		RecordingLogger logger = new();
		PickerSession session = NewSession("https://example.com/secret-path", new RecordingLaunchHelperClient(), logger: logger);
		session.ConfigureClipboardHooksForTests(null, _ => throw new IOException("https://example.com/secret-path"));

		int exit = await session.SimulateExternalDismissalForTestsAsync();

		_ = exit.Should().Be(ExitCodes.Success, "the catch stays fail-open");
		_ = session.CompleteCountForTests.Should().Be(1);
		_ = logger.Snapshot().Should().Contain(m => m.Contains("IOException", StringComparison.Ordinal));
		_ = logger.Snapshot().Should().NotContain(m => m.Contains("secret-path", StringComparison.Ordinal));
	}

	private static void Forward(PickerSession session, string url)
	{
		session.PostDrainForTests = action => action();
		session.OnForwardedActivation(new ActivationRequest(
			url,
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5)));
	}

	private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan budget)
	{
		Stopwatch elapsed = Stopwatch.StartNew();
		while (!condition() && elapsed.Elapsed < budget)
		{
			await Task.Delay(10);
		}

		return condition();
	}

	private static PickerSession NewSession(
		string url,
		ILaunchHelperClient launch,
		TrackingCleanupOptions? cleanup = null,
		INetworkHelperClient? network = null,
		ShortLinkExpansionOptions? shortLinks = null,
		RuleSetDocument? rules = null,
		ILogger<PickerSession>? logger = null)
	{
		BrowserSnapshot snapshot = new([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path"),
			new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path"),
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
			launch,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			logger ?? NullLogger<PickerSession>.Instance,
			cleanup,
			new NullAuditWriter(),
			shortLinks,
			network,
			activationHistoryStore: null,
			rulesStore: rules is null ? null : new FixedRulesStore(rules));
	}

	private sealed class RecordingLogger : ILogger<PickerSession>
	{
		private readonly Lock _gate = new();
		private readonly List<string> _messages = [];

		public IReadOnlyList<string> Snapshot()
		{
			lock (_gate)
			{
				return [.. _messages];
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
				_messages.Add(formatter(state, exception));
			}
		}
	}

	private sealed class FixedRulesStore(RuleSetDocument rules) : IRulesStore
	{
		public RuleSetDocument LoadOrDefault()
		{
			return rules;
		}

		public MutationCommitResult Save(RulesDocument document, ExpectedDocumentGeneration expectedGeneration)
		{
			_ = document;
			_ = expectedGeneration;
			return MutationCommitResult.Conflict(observedGeneration: null);
		}
	}

	private sealed class PendingLaunchHelperClient(TaskCompletionSource<LaunchHelperOutcome> pending) : ILaunchHelperClient
	{
		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			return pending.Task;
		}
	}

	private sealed class RecordingLaunchHelperClient : ILaunchHelperClient
	{
		public LaunchHelperRequest? LastRequest { get; private set; }

		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null));
		}
	}

	private sealed class PendingNetworkHelperClient(TaskCompletionSource<ShortLinkResolveResponse> pending) : INetworkHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Rejected);
		}

		public Task<ShortLinkResolveResponse> ResolveShortLinkAsync(ShortLinkResolveRequest request, CancellationToken cancellationToken)
		{
			return pending.Task;
		}
	}

	private sealed class ThrowingNetworkHelperClient : INetworkHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Rejected);
		}

		public Task<ShortLinkResolveResponse> ResolveShortLinkAsync(ShortLinkResolveRequest request, CancellationToken cancellationToken)
		{
			return Task.FromException<ShortLinkResolveResponse>(new InvalidOperationException(request.Url));
		}
	}

	private sealed class FakeSystemFallbackLauncher : ISystemFallbackLauncher
	{
		public LaunchResult Launch(string url)
		{
			return new LaunchResult(LaunchStatus.Success, "system", null, null, null);
		}
	}

	private sealed class NullAuditWriter : ITrackingCleanupAuditWriter
	{
		public void Write(TrackingCleanupAuditEvent cleanupEvent)
		{
			_ = cleanupEvent;
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
}
