using BroChoos.App.Localization;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Storage;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

// Reads localized status text, so it joins the culture-serializing collection.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class ActivationHistoryPickerFlowTests
{
	[Fact]
	public async Task CtrlH_ArmsSkipOne_AndNextLaunchIsNotRecorded()
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new();
		PickerSession session = NewSession(launch, store);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.SkipHistoryOnce, 0)]);
		session.ClickCardForTests(1);

		_ = launch.LastRequest.Should().NotBeNull();
		_ = store.Records.Should().ContainSingle(r => r.CandidateRecord == null && r.Settings.SkipNextLink);
		_ = store.Records.Should().ContainSingle(r => r.CandidateRecord == null && !r.Settings.SkipNextLink);
		_ = store.Records.Should().NotContain(r => r.CandidateRecord != null);
	}

	[Fact]
	public async Task CtrlBackspace_PressedTwice_ClearsHistory_WithConfirmedMutationPath()
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new();
		PickerSession session = NewSession(launch, store);

		_ = await session.ShowForTestsAsync([
			new PickerKeyAction(PickerKey.ClearHistoryConfirmed, 0),
			new PickerKeyAction(PickerKey.ClearHistoryConfirmed, 0),
		]);

		_ = store.ClearCalls.Should().Be(1);
	}

	[Fact]
	public async Task CtrlBackspace_PressedOnce_OnlyArms_AndSaysHowToConfirm()
	{
		RecordingActivationHistoryStore store = new();
		PickerSession session = NewSession(new FakeLaunchHelperClient(), store);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.ClearHistoryConfirmed, 0)]);

		_ = store.ClearCalls.Should().Be(0, "one press must never clear history");
		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.HistoryClearArmed"));
	}

	[Fact]
	public async Task CtrlBackspace_SecondPressAfterTheWindow_ArmsAgain_InsteadOfClearing()
	{
		RecordingActivationHistoryStore store = new();
		PickerSession session = NewSession(new FakeLaunchHelperClient(), store);
		ManualTimeProvider time = new(DateTimeOffset.UnixEpoch);
		session.UseTimeProviderForTests(time);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.ClearHistoryConfirmed, 0)]);
		time.Advance(PickerSession.ClearHistoryConfirmWindow + TimeSpan.FromMilliseconds(1));
		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.ClearHistoryConfirmed, 0)]);

		_ = store.ClearCalls.Should().Be(0, "an expired arm must not turn a later press into a clear");

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.ClearHistoryConfirmed, 0)]);
		_ = store.ClearCalls.Should().Be(1, "the re-armed press followed by a prompt second press clears");
	}

	[Fact]
	public async Task CtrlBackspace_AForwardedLinkBetweenPresses_Disarms()
	{
		RecordingActivationHistoryStore store = new();
		PickerSession session = NewSession(new FakeLaunchHelperClient(), store);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.ClearHistoryConfirmed, 0)]);

		session.PostDrainForTests = action => action();
		session.OnForwardedActivation(new ActivationRequest(
			"https://example.org/next",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5)));

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.ClearHistoryConfirmed, 0)]);

		_ = store.ClearCalls.Should().Be(0, "the press after a new link only re-arms; it must not clear");
	}

	[Fact]
	public async Task CtrlBackspace_AnotherKeyBetweenPresses_Disarms()
	{
		RecordingActivationHistoryStore store = new();
		PickerSession session = NewSession(new FakeLaunchHelperClient(), store);

		_ = await session.ShowForTestsAsync([
			new PickerKeyAction(PickerKey.ClearHistoryConfirmed, 0),
			new PickerKeyAction(PickerKey.UseOriginal, 0),
			new PickerKeyAction(PickerKey.ClearHistoryConfirmed, 0),
		]);

		_ = store.ClearCalls.Should().Be(0);
	}

	private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	[Fact]
	public async Task Launch_RecordsProjectedEntry_WhenHistoryEnabledAndNotSkipped()
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new();
		PickerSession session = NewSession(launch, store);

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		ActivationHistoryRecordRequest committed = store.Records.Last(r => r.CandidateRecord is not null);
		_ = committed.CandidateRecord!.MainDomain.Should().Be("example.com");
		_ = committed.CandidateRecord.BrowserId.Should().Be("edge-stable");
		_ = committed.CandidateRecord.ProfileId.Should().Be("Profile 7");
	}

	// Firefox is launched with no profile switch (the launch helper gives Gecko no profile
	// argument), so the history entry must not name the catalog's first Firefox profile.
	[Fact]
	public async Task FirefoxLaunch_RecordsNoProfile_BecauseNoProfileWasLaunched()
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new();
		PickerSession session = NewSession(
			launch,
			store,
			new BrowserCandidate("firefox-release", "Mozilla Firefox", @"C:\\Firefox\\firefox.exe", "release", "known-path")
			{
				ProfileIds = ["default-release"],
			});

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		ActivationHistoryRecordRequest committed = store.Records.Last(r => r.CandidateRecord is not null);
		_ = committed.CandidateRecord!.BrowserId.Should().Be("firefox-release");
		_ = committed.CandidateRecord.ProfileId.Should().BeNull("Firefox was launched without a profile switch");
	}

	// History names exactly the profile the launch passed. The launch helper sends a profile switch
	// to Chromium ids only, so a custom/unknown id (which gets none) must record no profile either.
	[Fact]
	public async Task CustomBrowserLaunch_RecordsNoProfile_BecauseOnlyChromiumGetsAProfileSwitch()
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new();
		PickerSession session = NewSession(
			launch,
			store,
			new BrowserCandidate("custom-portable", "Portable Browser", @"C:\\Portable\\browser.exe", "custom", "manual")
			{
				ProfileIds = ["Default"],
			});

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		ActivationHistoryRecordRequest committed = store.Records.Last(r => r.CandidateRecord is not null);
		_ = committed.CandidateRecord!.BrowserId.Should().Be("custom-portable");
		_ = committed.CandidateRecord.ProfileId.Should().BeNull("a custom browser is launched without a profile switch");
	}

	// ONE TILE PER CHROMIUM PROFILE: digit N launches tile N with ITS --profile-directory, and
	// history records that same profile.
	[Theory]
	[InlineData(1, "Profile 2")]
	[InlineData(2, "Default")]
	[InlineData(3, "Profile 3")]
	public async Task EachProfileTile_LaunchesAndRecordsItsOwnProfile(int digit, string expectedProfile)
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new();
		PickerSession session = NewSession(
			launch,
			store,
			new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\\Chrome\\chrome.exe", "stable", "known-path")
			{
				ProfileIds = ["Profile 2", "Default", "Profile 3"],
				ProfileNames = ["Work", "Anna", "Travel"],
			});

		// Slot N is what digit N names (PickerViewModel.FocusSlot); Enter needs a rendered focus
		// ring this windowless session does not have, so the tile is pressed the way the other
		// launch tests press one.
		session.ClickCardForTests(digit);
		_ = await session.ShowForTestsAsync([]);

		_ = session.CardCountForTests.Should().Be(3, "three Chromium profiles are three tiles");
		_ = launch.LastRequest.Should().NotBeNull();
		_ = launch.LastRequest!.ProfileId.Should().Be(expectedProfile);
		_ = LaunchHelperArguments.TryBuild(launch.LastRequest, out IReadOnlyList<string> argv, out _).Should().BeTrue();
		_ = argv.Should().Contain($"--profile-directory={expectedProfile}");
		ActivationHistoryRecordRequest committed = store.Records.Last(r => r.CandidateRecord is not null);
		_ = committed.CandidateRecord!.ProfileId.Should().Be(expectedProfile);
	}

	// Ctrl+H means "do not record the next launch". It deliberately survives a forward: keeping it
	// armed is the privacy-safe direction (at worst one launch goes unrecorded, never one extra
	// recorded), and the forward itself must not write the store.
	[Fact]
	public async Task ForwardedLink_KeepsTheArmedSkipHistory_AndDoesNotWriteTheStore()
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new();
		PickerSession session = NewSession(launch, store);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.SkipHistoryOnce, 0)]);
		int recordsBeforeForward = store.Records.Count;

		session.PostDrainForTests = action => action();
		session.OnForwardedActivation(new ActivationRequest(
			"https://example.org/next",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5)));
		_ = store.Records.Count.Should().Be(recordsBeforeForward, "a forward must not write the history store");

		session.ClickCardForTests(1);

		_ = launch.LastRequest.Should().NotBeNull();
		_ = store.Records.Should().NotContain(r => r.CandidateRecord != null, "the armed skip still applies to the next launch after a forward");
	}

	// History is recorded when the launch SUCCEEDS, not when the card is clicked: a failed,
	// timed-out or unknown outcome opened nothing (or nothing known), so it creates no entry.
	[Theory]
	[InlineData(LaunchStatus.Failed)]
	[InlineData(LaunchStatus.Timeout)]
	[InlineData(LaunchStatus.Unknown)]
	public async Task UnsuccessfulLaunch_RecordsNoHistoryEntry(LaunchStatus status)
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new() { Status = status };
		PickerSession session = NewSession(launch, store);

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		_ = launch.LastRequest.Should().NotBeNull("the launch was attempted");
		_ = store.Records.Should().NotContain(r => r.CandidateRecord != null, $"a {status} launch must not create a history entry");
		_ = store.LoadOrDefault().Entries.Should().BeEmpty();
	}

	// The skip-next-link one-shot belongs to the next SUCCESSFUL launch: an unsuccessful one
	// keeps it armed (no entry, no consumption).
	[Theory]
	[InlineData(LaunchStatus.Failed)]
	[InlineData(LaunchStatus.Timeout)]
	[InlineData(LaunchStatus.Unknown)]
	public async Task UnsuccessfulLaunch_KeepsTheSkipFlagArmed(LaunchStatus status)
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new() { Status = status };
		PickerSession session = NewSession(launch, store);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.SkipHistoryOnce, 0)]);
		session.ClickCardForTests(1);

		_ = launch.LastRequest.Should().NotBeNull("the launch was attempted");
		_ = store.LoadOrDefault().Settings.SkipNextLink.Should().BeTrue($"a {status} launch must not consume the skip flag");
		_ = store.Records.Should().ContainSingle("only the arming write exists; the unsuccessful launch wrote nothing")
			.Which.Settings.SkipNextLink.Should().BeTrue();
	}

	[Fact]
	public async Task SuccessfulLaunch_AfterAnUnsuccessfulOne_ConsumesTheSkipFlag_WithoutAnEntry()
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new() { Status = LaunchStatus.Failed };
		PickerSession session = NewSession(launch, store);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.SkipHistoryOnce, 0)]);
		session.ClickCardForTests(1);
		_ = store.LoadOrDefault().Settings.SkipNextLink.Should().BeTrue();

		launch.Status = LaunchStatus.Success;
		session.ClickCardForTests(1);

		_ = store.LoadOrDefault().Settings.SkipNextLink.Should().BeFalse("the successful launch consumed the one-shot");
		_ = store.Records.Should().NotContain(r => r.CandidateRecord != null, "the skipped launch is not recorded");
	}

	[Fact]
	public async Task SuccessfulLaunch_WithoutSkip_RecordsExactlyOneEntry()
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new();
		PickerSession session = NewSession(launch, store);

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		_ = store.LoadOrDefault().Entries.Should().ContainSingle();
	}

	// FAIL-OPEN: the browser is already open when the history is written. A history store that
	// throws (lock timeout, disk, permissions) must not fault the fire-and-forget launch task, or
	// the picker would stay open with the in-flight flag stuck even though the launch worked.
	[Theory]
	[InlineData(typeof(IOException))]
	[InlineData(typeof(TimeoutException))]
	[InlineData(typeof(UnauthorizedAccessException))]
	public async Task HistoryWriteThrows_AfterASuccessfulLaunch_StillCompletesTheSession(Type failure)
	{
		RecordingActivationHistoryStore store = new() { RecordFailure = (Exception)Activator.CreateInstance(failure, "secret-path")! };
		CapturingLogger logger = new();
		FakeLaunchHelperClient launch = new();
		PickerSession session = NewSession(launch, store, logger: logger);

		session.ClickCardForTests(1);
		await session.LastLaunchTaskForTests!;

		_ = session.IsLaunchInFlightForTests.Should().BeFalse("the outcome was handled");
		_ = session.CompleteCountForTests.Should().Be(1, "a successful launch ends the session whatever the history store did");
		_ = logger.Messages.Should().Contain(m => m.Contains(failure.Name, StringComparison.Ordinal), "the exception TYPE is logged");
		_ = logger.Messages.Should().NotContain(m => m.Contains("secret-path", StringComparison.Ordinal), "the exception message is never logged");
	}

	[Fact]
	public async Task HistoryWriteThrows_WithAHeldForward_StillKeepsThePickerOpenForTheForward()
	{
		RecordingActivationHistoryStore store = new() { RecordFailure = new IOException("lock timeout") };
		FakeLaunchHelperClient launch = new() { Pending = new TaskCompletionSource<LaunchHelperOutcome>(TaskCreationOptions.RunContinuationsAsynchronously) };
		PickerSession session = NewSession(launch, store);

		session.ClickCardForTests(1);
		Forward(session, "https://second.example/b");
		launch.Pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Success, "edge-stable", null, null, null));
		await session.LastLaunchTaskForTests!;

		_ = session.IsLaunchInFlightForTests.Should().BeFalse();
		_ = session.CompleteCountForTests.Should().Be(0, "completing would lose the held forwarded link");
		_ = session.DeliveredUrlsForTests.Should().Equal("https://second.example/b");
		_ = session.FullscreenQueueCountForTests.Should().Be(0);
	}

	// ESC / OS CLOSE DURING THE HELPER WAIT. The outcome is not known when the session ends, and in
	// the real app the dispatcher is shut down with it, so the outcome handler may never run. The
	// armed skip flag therefore cannot wait for the outcome: it is consumed when the session ends
	// (the link it was armed for is gone either way), and a later Success then records no entry.
	[Theory]
	[InlineData(LaunchStatus.Success)]
	[InlineData(LaunchStatus.Failed)]
	public async Task EscapeDuringALaunch_WithArmedSkip_ConsumesTheFlag_WhateverTheOutcome(LaunchStatus outcome)
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new() { Pending = new TaskCompletionSource<LaunchHelperOutcome>(TaskCreationOptions.RunContinuationsAsynchronously) };
		PickerSession session = NewSession(launch, store);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.SkipHistoryOnce, 0)]);
		session.ClickCardForTests(1);
		_ = session.IsLaunchInFlightForTests.Should().BeTrue();
		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);

		_ = session.CompleteCountForTests.Should().Be(1);
		_ = store.LoadOrDefault().Settings.SkipNextLink.Should().BeFalse("the session ended before the outcome; the flag must not outlive it");

		launch.Pending.SetResult(new LaunchHelperOutcome(outcome, "edge-stable", outcome == LaunchStatus.Success ? null : "launch.start-failed", "launch", null));
		await session.LastLaunchTaskForTests!;

		_ = store.Records.Should().NotContain(r => r.CandidateRecord != null, "the launch was to be skipped, and a failed one is never recorded");
		_ = store.LoadOrDefault().Settings.SkipNextLink.Should().BeFalse();
	}

	[Fact]
	public async Task WindowClosedDuringALaunch_WithArmedSkip_ConsumesTheFlag()
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new() { Pending = new TaskCompletionSource<LaunchHelperOutcome>(TaskCreationOptions.RunContinuationsAsynchronously) };
		PickerSession session = NewSession(launch, store);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.SkipHistoryOnce, 0)]);
		session.ClickCardForTests(1);
		session.SimulateWindowClosedForTests();

		_ = session.CompleteCountForTests.Should().Be(1);
		_ = store.LoadOrDefault().Settings.SkipNextLink.Should().BeFalse();
	}

	// Only a launch still waiting for its outcome changes the rule. Cancelling with nothing launched,
	// or after a failed launch, keeps the flag armed for the next link as before.
	[Fact]
	public async Task EscapeWithoutALaunch_KeepsTheArmedSkipFlag()
	{
		RecordingActivationHistoryStore store = new();
		PickerSession session = NewSession(new FakeLaunchHelperClient(), store);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.SkipHistoryOnce, 0), new PickerKeyAction(PickerKey.Escape, 0)]);

		_ = session.CompleteCountForTests.Should().Be(1);
		_ = store.LoadOrDefault().Settings.SkipNextLink.Should().BeTrue();
	}

	[Fact]
	public async Task FailedLaunch_ThenEscape_KeepsTheArmedSkipFlag()
	{
		RecordingActivationHistoryStore store = new();
		PickerSession session = NewSession(new FakeLaunchHelperClient { Status = LaunchStatus.Failed }, store);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.SkipHistoryOnce, 0)]);
		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);

		_ = session.CompleteCountForTests.Should().Be(1);
		_ = store.LoadOrDefault().Settings.SkipNextLink.Should().BeTrue("the failed launch did not use the one-shot");
		_ = store.Records.Should().NotContain(r => r.CandidateRecord != null);
	}

	// A successful launch whose outcome is observed after the session ended is still a launch: it is
	// recorded (when the process is still alive to observe it).
	[Fact]
	public async Task EscapeDuringALaunch_WithoutSkip_ThenSuccess_StillRecordsTheEntry()
	{
		RecordingActivationHistoryStore store = new();
		FakeLaunchHelperClient launch = new() { Pending = new TaskCompletionSource<LaunchHelperOutcome>(TaskCreationOptions.RunContinuationsAsynchronously) };
		PickerSession session = NewSession(launch, store);

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);
		launch.Pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Success, "edge-stable", null, null, null));
		await session.LastLaunchTaskForTests!;

		_ = store.LoadOrDefault().Entries.Should().ContainSingle();
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

	private sealed class CapturingLogger : ILogger<PickerSession>
	{
		private readonly Lock _gate = new();
		private readonly List<string> _messages = [];

		public IReadOnlyList<string> Messages
		{
			get
			{
				lock (_gate)
				{
					return [.. _messages];
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
				_messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.GetType().Name + ": " + exception.Message));
			}
		}
	}

	private static PickerSession NewSession(FakeLaunchHelperClient launch, RecordingActivationHistoryStore store, BrowserCandidate? candidate = null, ILogger<PickerSession>? logger = null)
	{
		BrowserSnapshot snapshot = new([
			candidate ?? new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\\Edge\\msedge.exe", "stable", "known-path")
			{
				ProfileIds = ["Profile 7"],
			}
		], 3, DateTimeOffset.UtcNow);

		ActivationRequest activation = new(
			"https://example.com/path?token=abc&x=1#frag",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Confirmed(@"C:\\Program Files\\Google\\Chrome\\chrome.exe"),
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
			trackingCleanupOptions: null,
			trackingCleanupAuditWriter: null,
			shortLinkExpansionOptions: null,
			networkHelperClient: null,
			activationHistoryStore: store);
	}

	private sealed class FakeLaunchHelperClient : ILaunchHelperClient
	{
		public LaunchHelperRequest? LastRequest { get; private set; }

		public LaunchStatus Status { get; set; } = LaunchStatus.Success;

		// When set, the launch stays in flight until the test completes it.
		public TaskCompletionSource<LaunchHelperOutcome>? Pending { get; set; }

		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Pending?.Task ?? Task.FromResult(Status == LaunchStatus.Success
				? new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null)
				: new LaunchHelperOutcome(Status, request.BrowserId, "launch.test-outcome", "launch", "launch.error.failed"));
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

	private sealed class RecordingActivationHistoryStore : IActivationHistoryStore
	{
		private ActivationHistoryDocument _document = new(new ActivationHistorySettingsContract(), []);
		public List<ActivationHistoryRecordRequest> Records { get; } = [];
		public int ClearCalls { get; private set; }

		// When set, every Record throws it, as a lock timeout or a disk error would.
		public Exception? RecordFailure { get; set; }

		public ActivationHistoryDocument LoadOrDefault()
		{
			return _document;
		}

		public ActivationHistoryMutationResult Record(ActivationHistoryRecordRequest request)
		{
			if (RecordFailure is not null)
			{
				throw RecordFailure;
			}

			Records.Add(request);
			if (!request.Settings.Enabled)
			{
				_document = _document with { Settings = new ActivationHistorySettingsContract(false, false), Generation = _document.Generation + 1 };
				return Applied();
			}

			if (request.Settings.SkipNextLink)
			{
				_document = _document with { Settings = new ActivationHistorySettingsContract(true, true), Generation = _document.Generation + 1 };
				return Applied();
			}

			if (request.CandidateRecord is null)
			{
				_document = _document with { Settings = new ActivationHistorySettingsContract(true, false), Generation = _document.Generation + 1 };
				return Applied();
			}

			List<Core.Privacy.ActivationHistoryRecord> entries = [.. _document.Entries, request.CandidateRecord];
			_document = _document with
			{
				Settings = new ActivationHistorySettingsContract(true, false),
				Entries = entries,
				Generation = _document.Generation + 1,
			};
			return Applied();
		}

		public ActivationHistoryMutationResult ClearConfirmed(ExpectedDocumentGeneration expectedGeneration)
		{
			_ = expectedGeneration;
			ClearCalls++;
			_document = _document with { Entries = [], Generation = _document.Generation + 1 };
			return Applied();
		}

		private ActivationHistoryMutationResult Applied()
		{
			return new ActivationHistoryMutationResult(
				Applied: true,
				MutationCommitResult.Committed(_document.Generation, Math.Max(0, _document.Generation - 1)),
				NewGeneration: _document.Generation,
				Document: _document);
		}
	}
}
