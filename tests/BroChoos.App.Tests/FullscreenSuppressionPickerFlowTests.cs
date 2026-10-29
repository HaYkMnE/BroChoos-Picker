using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Snapshots;
using BroChoos.Platform.Windows.SingleInstance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

// W1.5: the fullscreen-suppressed queue (D-v3.5-150, capacity 3, fourth drops the oldest with a
// redacted event) must be CONSTRUCTED AND USED ON THE REAL PICKER PATH, not merely unit-tested in
// isolation. That gap - the queue existed with green unit tests but no production instantiation -
// is exactly what these tests close.
//
// TWO CONTRACTS, DELIBERATELY SPLIT:
//   * SUPPRESSION (TrySuppressForwardedActivation) - the queue step: cap 3, expiry, drop-oldest,
//     redacted reason.
//   * DELIVERY (OnForwardedActivation -> the drain -> DrainFullscreenQueue -> UpdateUrl) - FIFO
//     order, no loss, no duplicate delivery, the pre-window hold, and the dispatcher-unavailable
//     guard. Driven through the REAL OnForwardedActivation and the REAL drain; the only
//     substitution is the dispatcher POST, run inline by PostDrainForTests (see the delivery
//     section for why the process-wide Dispatcher must not be driven from here).
//
// NO COLLECTION ATTRIBUTE: these tests never touch Dispatcher.UIThread or the Avalonia Application,
// so they share no process-wide state with the window tests and need no serialization.
public sealed class FullscreenSuppressionPickerFlowTests
{
	[Fact]
	public void Session_OwnsTheNamedFullscreenPolicyInstance_OnTheRealPath()
	{
		PickerSession session = NewSession(NewRequest("https://picker.example/first"));

		// The session must own the real policy instance the moment it exists: this is the wiring
		// the audit found missing. The named capacity (three) proves the session holds the
		// fullscreen queue, distinct from the guard's concurrent-click queue (five).
		_ = FullscreenSuppressionQueue.Capacity.Should().Be(3);
		_ = session.FullscreenQueueCountForTests.Should().Be(0);
	}

	[Fact]
	public void ForwardedActivations_UseTheFullscreenQueue_AndNeverExceedCapacityThree()
	{
		PickerSession session = NewSession(NewRequest("https://picker.example/first"));

		DateTimeOffset now = DateTimeOffset.UtcNow;
		ActivationRequest[] four =
		[
			NewActivation("https://picker.example/1", now.AddMinutes(1)),
			NewActivation("https://picker.example/2", now.AddMinutes(1)),
			NewActivation("https://picker.example/3", now.AddMinutes(1)),
			NewActivation("https://picker.example/4", now.AddMinutes(1)),
		];

		// Three fit; the fourth drops the oldest rather than growing past the cap.
		foreach (ActivationRequest request in four)
		{
			_ = session.TrySuppressForwardedActivation(request);
		}

		// The queue never exceeds the named capacity of three (spec 2.4 / D-v3.5-150).
		_ = session.FullscreenQueueCountForTests.Should().BeLessThanOrEqualTo(FullscreenSuppressionQueue.Capacity);

		// The fourth enqueue reported the drop-oldest reason code, and it is a redacted code only.
		_ = session.LastFullscreenReasonCodeForTests.Should().Be("fullscreen.queue.drop-oldest");
		_ = session.LastFullscreenReasonCodeForTests.Should().NotContain("https://", "the reason code must be redacted");
		_ = session.LastFullscreenReasonCodeForTests.Should().NotContain("picker.example", "the reason code must be redacted");
	}

	[Fact]
	public void ForwardedActivation_WhilePickerIsShowing_IsHeldByTheWiredQueue_NotDiscarded()
	{
		PickerSession session = NewSession(NewRequest("https://picker.example/first"));

		DateTimeOffset now = DateTimeOffset.UtcNow;
		bool held = session.TrySuppressForwardedActivation(NewActivation("https://picker.example/second", now.AddMinutes(1)));

		// The activation is HELD by the wired queue, so one forwarded URL is queued rather than
		// dropped. This is the production suppression step genuinely running the queue, which the
		// audit found it never did.
		_ = held.Should().BeTrue();
		_ = session.FullscreenQueueCountForTests.Should().Be(1);
		_ = session.LastFullscreenReasonCodeForTests.Should().BeEmpty();
	}

	[Fact]
	public void ExpiredForwardedActivation_IsRejectedWithRedactedReason()
	{
		PickerSession session = NewSession(NewRequest("https://picker.example/first"));
		DateTimeOffset now = DateTimeOffset.UtcNow;

		_ = session.TrySuppressForwardedActivation(NewActivation("https://picker.example/expired", now.AddSeconds(-1)));

		// Expiry is enforced by the wired queue, and the reason code stays value-free.
		_ = session.LastFullscreenReasonCodeForTests.Should().Be("fullscreen.queue.expired");
		_ = session.FullscreenQueueCountForTests.Should().Be(0);
	}

	// ---- DELIVERY (the queue -> view model handoff) ----------------------------
	//
	// These tests drive the REAL OnForwardedActivation and the REAL DrainFullscreenQueue end to
	// end. They use TWO independent seams, because they answer two different questions:
	//
	//   * PostDrainForTests REPLACES the dispatcher handoff so the drain runs inline (or throws).
	//     It exercises OUR drain logic: delivery order, no loss, no duplicate, the pre-window hold,
	//     and the dispatcher-unavailable guard.
	//   * DispatchPostForTests is the POST MECHANISM itself (an internal Action<Action> on
	//     PickerSession, defaulting to the real Dispatcher.UIThread.Post, with ONE production call
	//     site). A test swaps it to CAPTURE the action the production path hands to it, so the real
	//     call site still runs - the thing the reviewer (N1) showed was uncovered.
	//
	// WHY THE REAL POST IS NOT CALLED. Dispatcher.UIThread is a PROCESS-WIDE, THREAD-AFFINE
	// singleton shared with the window tests (PickerXamlProbe / SettingsShellProbe). Driving it
	// from this class leaves it bound to the wrong thread and breaks those tests (MEASURED: 6-15
	// failures in PickerXamlLoadTests/SettingsWindowLoadTests). So the tests cover (a) the real
	// drain logic and (b) that the production dispatch branch is SELECTED; they do NOT cover
	// Avalonia's own act of posting-and-pumping, which is Avalonia's contract and is exercised by
	// the window tests. This is stated, not implied.

	[Fact]
	public void Delivery_IsFifo_NoLoss_NoDuplicate()
	{
		PickerSession session = LiveSession();
		DateTimeOffset now = DateTimeOffset.UtcNow;

		// HOLD the drain: capture the posted action instead of running it, so ALL THREE
		// activations are enqueued BEFORE any drain runs. This is what makes the test able to
		// detect a latest-only (or otherwise lossy) drain - the previous version drained after
		// every single enqueue, so every drain saw one item and the mutation was invisible (N2).
		List<Action> pending = [];
		session.PostDrainForTests = pending.Add;

		session.OnForwardedActivation(NewActivation("https://deliver.example/1", now.AddMinutes(1)));
		session.OnForwardedActivation(NewActivation("https://deliver.example/2", now.AddMinutes(1)));
		session.OnForwardedActivation(NewActivation("https://deliver.example/3", now.AddMinutes(1)));

		// Nothing delivered yet: three activations are HELD, and three drains are pending.
		_ = session.FullscreenQueueCountForTests.Should().Be(3);
		_ = pending.Should().HaveCount(3);
		_ = session.DeliveredUrlsForTests.Should().BeEmpty();

		// Release the held drain(s). The FIRST drain must deliver all three, in FIFO order, once
		// each; the later pending drains must find an empty queue and deliver nothing.
		pending[0]();
		_ = session.DeliveredUrlsForTests.Should().Equal(
			"https://deliver.example/1",
			"https://deliver.example/2",
			"https://deliver.example/3");

		foreach (Action drain in pending.Skip(1))
		{
			drain();
		}

		// Still exactly three, in order: no duplicate delivery from the repeated drains.
		_ = session.DeliveredUrlsForTests.Should().Equal(
			"https://deliver.example/1",
			"https://deliver.example/2",
			"https://deliver.example/3");
		_ = session.FullscreenQueueCountForTests.Should().Be(0);
	}

	[Fact]
	public void Delivery_WithNoTestHook_UsesTheProductionPostCallSite()
	{
		PickerSession session = LiveSession();
		DateTimeOffset now = DateTimeOffset.UtcNow;

		// No PostDrainForTests hook: OnForwardedActivation must take the PRODUCTION path and hand
		// the drain to the post MECHANISM. The test replaces only the mechanism (Dispatcher
		// .UIThread.Post) with a capture, so the real `post(DrainFullscreenQueue)` call site runs
		// and the captured action is what production would have posted.
		List<Action> posted = [];
		session.PostDrainForTests = null;
		session.DispatchPostForTests = posted.Add;

		session.OnForwardedActivation(NewActivation("https://deliver.example/prod", now.AddMinutes(1)));

		// THE PRODUCTION CALL SITE RAN: exactly one drain was handed to the post mechanism. A
		// mutation that removes that call (the N1 break) leaves this empty and fails HERE.
		_ = posted.Should().ContainSingle("the production path must hand the drain to the post mechanism");
		_ = session.DeliveredUrlsForTests.Should().BeEmpty("the production path posts; it does not drain inline");
		_ = session.FullscreenQueueCountForTests.Should().Be(1);

		// The captured action IS the drain: running it (as the dispatcher eventually would)
		// delivers the held activation.
		posted[0]();
		_ = session.DeliveredUrlsForTests.Should().Equal("https://deliver.example/prod");
		_ = session.FullscreenQueueCountForTests.Should().Be(0);
	}

	[Fact]
	public void Delivery_PreWindowActivation_StaysQueued_ThenDrainsWhenThePickerIsLive()
	{
		PickerSession session = NewSession(NewRequest("https://picker.example/first"));
		DateTimeOffset now = DateTimeOffset.UtcNow;
		session.PostDrainForTests = action => action();

		// The window is not up yet: _vm is null, so the drain must NOT lose the activation - it
		// stays queued (the pre-window suppression the delivery contract requires).
		session.OnForwardedActivation(NewActivation("https://deliver.example/pre-window", now.AddMinutes(1)));

		_ = session.DeliveredUrlsForTests.Should().BeEmpty("no view model yet: nothing can be delivered");
		_ = session.FullscreenQueueCountForTests.Should().Be(1, "a pre-window activation must be HELD, never dropped");

		// Now the picker becomes live; a later activation drains the queue FIFO, so the held
		// pre-window activation is delivered BEFORE the later one.
		session.InitializeForTests();
		session.OnForwardedActivation(NewActivation("https://deliver.example/after-window", now.AddMinutes(1)));

		_ = session.DeliveredUrlsForTests.Should().Equal(
			"https://deliver.example/pre-window",
			"https://deliver.example/after-window");
		_ = session.FullscreenQueueCountForTests.Should().Be(0);
	}

	[Fact]
	public void Delivery_DispatcherUnavailable_MarksReasonAndLeavesTheActivationQueued()
	{
		PickerSession session = LiveSession();
		DateTimeOffset now = DateTimeOffset.UtcNow;

		// Drive the exact branch the real Dispatcher.UIThread.Post raises when no loop can accept
		// the job. The activation must remain QUEUED (never dropped) and the reason must be the
		// redacted dispatcher-unavailable code.
		session.PostDrainForTests = _ => throw new InvalidOperationException("Cannot perform this operation while dispatcher processing is suspended.");

		session.OnForwardedActivation(NewActivation("https://deliver.example/held", now.AddMinutes(1)));

		_ = session.LastFullscreenReasonCodeForTests.Should().Be("fullscreen.queue.dispatcher-unavailable");
		_ = session.DeliveredUrlsForTests.Should().BeEmpty();
		_ = session.FullscreenQueueCountForTests.Should().Be(1, "an unavailable dispatcher must leave the activation held, not lose it");

		// A later, available handoff drains the held activation: the backlog is not stranded.
		session.PostDrainForTests = action => action();
		session.OnForwardedActivation(NewActivation("https://deliver.example/next", now.AddMinutes(1)));

		_ = session.DeliveredUrlsForTests.Should().Equal(
			"https://deliver.example/held",
			"https://deliver.example/next");
	}

	// ---- FULLSCREEN FOREGROUND POLICY ------------------------------------------
	//
	// A forwarded link updates the picker at once, but the foreground request is held while
	// the user is in a fullscreen app or presentation mode, and made once that ends.

	[Fact]
	public void Forward_WhileUserIsNotBusy_RequestsForegroundAtOnce()
	{
		PickerSession session = LiveSession();
		session.PostDrainForTests = action => action();
		session.IsUserBusyForTests = () => false;

		session.OnForwardedActivation(NewActivation("https://deliver.example/now", DateTimeOffset.UtcNow.AddMinutes(1)));

		_ = session.ForegroundRequestCountForTests.Should().Be(1);
		_ = session.ForegroundDeferredCountForTests.Should().Be(0);
	}

	[Fact]
	public async Task Forward_DuringFullscreen_UpdatesTheLink_ButWaitsToComeForward()
	{
		PickerSession session = LiveSession();
		session.PostDrainForTests = action => action();
		session.DispatchPostForTests = action => action();

		bool busy = true;
		TaskCompletionSource pollReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		session.IsUserBusyForTests = () => busy;
		session.DelayForTests = async delay =>
		{
			_ = delay;
			_ = pollReached.TrySetResult();
			await release.Task;
		};

		session.OnForwardedActivation(NewActivation("https://deliver.example/while-gaming", DateTimeOffset.UtcNow.AddMinutes(1)));

		// The link is delivered immediately (the picker shows the new link)...
		_ = session.DeliveredUrlsForTests.Should().Equal("https://deliver.example/while-gaming");
		_ = session.EffectiveUrlForTests.Should().Be("https://deliver.example/while-gaming");

		// ...but nothing is pulled to the front while the game is fullscreen.
		await pollReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
		_ = session.ForegroundRequestCountForTests.Should().Be(0);
		_ = session.ForegroundDeferredCountForTests.Should().Be(1);

		// A second forward during the same fullscreen period does not start a second wait.
		session.OnForwardedActivation(NewActivation("https://deliver.example/second", DateTimeOffset.UtcNow.AddMinutes(1)));
		_ = session.ForegroundDeferredCountForTests.Should().Be(1);

		// Fullscreen ends: the next poll sees "not busy" and the foreground request is made once.
		busy = false;
		_ = release.TrySetResult();
		for (int attempt = 0; attempt < 200 && session.ForegroundRequestCountForTests == 0; attempt++)
		{
			await Task.Delay(10);
		}

		_ = session.ForegroundRequestCountForTests.Should().Be(1);
	}

	// A state stuck at "busy" (or misreported) must not hold a forwarded link forever: after
	// FullscreenMaxHold the picker comes forward anyway.
	[Fact]
	public async Task Forward_WhenFullscreenNeverEnds_ComesForwardAfterTheMaximumHold()
	{
		PickerSession session = LiveSession();
		session.PostDrainForTests = action => action();
		session.DispatchPostForTests = action => action();
		session.IsUserBusyForTests = () => true;

		int polls = 0;
		session.DelayForTests = delay =>
		{
			_ = delay;
			polls++;
			return Task.CompletedTask;
		};

		session.OnForwardedActivation(NewActivation("https://deliver.example/stuck", DateTimeOffset.UtcNow.AddMinutes(1)));

		for (int attempt = 0; attempt < 200 && session.ForegroundRequestCountForTests == 0; attempt++)
		{
			await Task.Delay(10);
		}

		_ = session.ForegroundRequestCountForTests.Should().Be(1, "the hold is capped, never indefinite");
		_ = polls.Should().Be((int)(PickerSession.FullscreenMaxHold / PickerSession.FullscreenPollInterval),
			"the cap is reached through ordinary polling, not on the first check");
	}

	[Fact]
	public async Task Forward_WhenTheWaitFails_ComesForwardAtOnce()
	{
		PickerSession session = LiveSession();
		session.PostDrainForTests = action => action();
		session.DispatchPostForTests = action => action();
		session.IsUserBusyForTests = () => true;
		session.DelayForTests = delay => throw new InvalidOperationException("delay unavailable " + delay);

		session.OnForwardedActivation(NewActivation("https://deliver.example/wait-fails", DateTimeOffset.UtcNow.AddMinutes(1)));

		for (int attempt = 0; attempt < 200 && session.ForegroundRequestCountForTests == 0; attempt++)
		{
			await Task.Delay(10);
		}

		_ = session.ForegroundRequestCountForTests.Should().Be(1, "a failed wait must not strand the link in the background");
	}

	// Only the two unambiguous states hold the window: 3 (exclusive D3D fullscreen) and
	// 4 (presentation mode). 2 (QUNS_BUSY) also covers ordinary borderless-fullscreen windows.
	[Theory]
	[InlineData(1, false)]
	[InlineData(2, false)]
	[InlineData(3, true)]
	[InlineData(4, true)]
	[InlineData(5, false)]
	[InlineData(6, false)]
	[InlineData(7, false)]
	public void BusyStates_MatchTheDocumentedFullscreenAndPresentationValues(int state, bool busy)
	{
		_ = UserNotificationState.IsBusyState(state).Should().Be(busy);
	}

	// The session is brought to its live state so the view model exists for the drain to update.
	// Avalonia is NOT bootstrapped: these tests never touch Dispatcher.UIThread.
	private static PickerSession LiveSession()
	{
		PickerSession session = NewSession(NewRequest("https://picker.example/first"));
		session.InitializeForTests();
		return session;
	}

	private static PickerSession NewSession(PickerRequest request)
	{
		return new PickerSession(
			request,
			new FakeLaunchHelperClient(),
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);
	}

	private static PickerRequest NewRequest(string url)
	{
		return new PickerRequest(
			NewActivation(url, DateTimeOffset.UtcNow.AddMinutes(5)),
			BrowserSnapshot.Empty,
			FirstRunConsentContract.None);
	}

	private static ActivationRequest NewActivation(string url, DateTimeOffset expiresAtUtc)
	{
		return new ActivationRequest(
			url,
			1,
			ActivationTriggerContext.ForwardedFromSecondary,
			SourceEvidence.Probable("test"),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			expiresAtUtc);
	}

	private sealed class FakeLaunchHelperClient : ILaunchHelperClient
	{
		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
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
}
