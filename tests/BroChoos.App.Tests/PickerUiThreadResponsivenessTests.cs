using System.Diagnostics;
using BroChoos.App.Audit;
using BroChoos.App.Picker;
using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

// H1 (HIGH, independent deep audit): LaunchCard and the first-run action both ended in
// .GetAwaiter().GetResult() ON THE UI THREAD, and the helper clients' budgets are seconds
// long (launch 5 s; storage unbounded before this change), so one click could freeze the
// picker window for seconds - or, on a hung helper, forever.
//
// WHAT THIS PINS: the caller RETURNS IMMEDIATELY while the helper call is still in flight.
// The click runs on a dedicated thread, which is the property this contract needs - the
// production caller is the STA UI thread, and the fix must hand the wait off rather than
// perform it there.
//
// The dedicated thread also keeps the test independent of whatever
// SynchronizationContext the Avalonia XAML probes may have left installed on a reused
// thread-pool thread: a fresh Thread starts with SynchronizationContext.Current == null
// (guaranteed), so the continuation can run on the completing thread instead of being
// posted to a dispatcher this test does not pump.
public sealed class PickerUiThreadResponsivenessTests
{
	[Fact]
	public async Task CardLaunch_DoesNotBlockTheCaller_WhileTheHelperIsStillRunning()
	{
		BlockingLaunchHelperClient launch = new();
		PickerSession session = NewSession(launch);

		bool returned = RunOnADedicatedThread(() => session.ClickCardForTests(1));

		_ = returned.Should().BeTrue(
			"a click must not block its caller while the helper is still running: in production the caller "
			+ "is the picker's UI thread and the launch helper carries a 5 s budget, so a blocked caller is "
			+ "a frozen window (H1). Pre-fix this call waits on the incomplete task forever");
		_ = launch.LaunchCalls.Should().Be(1, "the launch must still have been started exactly once");
		_ = session.CompleteCountForTests.Should().Be(0, "the launch is still in flight, so nothing may have completed yet");

		launch.Complete();

		_ = await WaitUntilAsync(() => session.CompleteCountForTests == 1);
		_ = session.CompleteCountForTests.Should().Be(
			1,
			"the completed launch must still finish the session exactly as the synchronous path did");
	}

	// THE BEHAVIOUR THE FIX MUST NOT CHANGE. While the helper call blocked the UI thread, a
	// second click was impossible - the thread was inside the first call. Handing the wait off
	// restores input, so without an in-flight guard the window would accept a second click and
	// open a SECOND browser. This pins that the second click is ignored while the first launch
	// is unresolved, and that the retry path (Unknown recovery) is still reachable afterwards.
	[Fact]
	public async Task SecondClick_WhileTheFirstLaunchIsStillInFlight_IsIgnored()
	{
		BlockingLaunchHelperClient launch = new();
		PickerSession session = NewSession(launch);

		_ = RunOnADedicatedThread(() => session.ClickCardForTests(1)).Should().BeTrue();
		_ = launch.LaunchCalls.Should().Be(1);

		// A second click while the first helper call is unresolved: the window IS responsive
		// now, so this click really does arrive - and it must not start a second launch.
		session.ClickCardForTests(1);

		_ = launch.LaunchCalls.Should().Be(
			1,
			"the picker must open exactly one browser per user choice; a responsive window makes a "
			+ "second click reachable, so the in-flight launch has to ignore it");

		// Once the outcome is in (Unknown, the documented recoverable state), a further explicit
		// Enter is the retry the product promises - the guard must not have broken it.
		launch.Complete(LaunchStatus.Unknown);
		_ = await WaitUntilAsync(() => !session.IsLaunchInFlightForTests);

		session.ClickCardForTests(1);
		_ = launch.LaunchCalls.Should().Be(2, "after an Unknown outcome a further explicit activation is the documented retry");
	}

	[Fact]
	public async Task FirstRunDiscovery_DoesNotBlockTheCaller_WhileTheStorageHelperIsStillRunning()
	{
		BlockingStorageHelperClient storage = new();
		PickerSession session = NewSession(
			new BlockingLaunchHelperClient(),
			storage,
			firstRunRequired: true,
			emptySnapshot: true);

		bool returned = RunOnADedicatedThread(() => session.FirstRunActionForTests(FirstRunAction.FindBrowsers));

		_ = returned.Should().BeTrue(
			"the first-run scan must not block its caller either: the storage helper was the ONE helper with "
			+ "no budget at all, so a hung child blocked the picker indefinitely (H1)");
		_ = storage.DiscoverCalls.Should().Be(1, "the scan must still have been dispatched exactly once");

		storage.Complete();

		_ = await WaitUntilAsync(() => session.CardCountForTests == 1);
		_ = session.CardCountForTests.Should().Be(1, "the discovered snapshot must still be applied to the picker");
	}

	// DEFECT #36 (MED) AND ITS REGRESSION, TOGETHER. The dismissal path must satisfy BOTH:
	//
	//   (a) it must NOT park the UI thread on the clipboard (`SetTextAsync(url)
	//       .GetAwaiter().GetResult()` blocked for as long as the OS clipboard service took, and
	//       another process can hold the clipboard open indefinitely - the H1 freeze, on Esc /
	//       click-outside / Alt+Tab / Win+D);
	//
	//   (b) the write must ACTUALLY COMPLETE, and before the session shuts down. A "fix" that
	//       merely POSTS the write and returns is NOT correct: Complete(ExitCodes.Success) runs
	//       `_desktop?.Shutdown(exitCode)` synchronously right after, and Avalonia 11.3.18's
	//       ClassicDesktopStyleApplicationLifetime -> Dispatcher.UIThread.InvokeShutdown() ->
	//       ShutdownImpl() ABORTS every queued operation (VERIFIED: Dispatcher.MainLoop.cs). A
	//       posted write is therefore guaranteed to be dropped, and this test EXISTS BECAUSE the
	//       first fix made exactly that mistake while a seam-replacing test hid it.
	//
	// HOW THE TEST DISCRIMINATES, and why it does NOT use a real
	// ClassicDesktopStyleApplicationLifetime. It drives the production entry point with NO
	// dispatcher-handoff substitution (the old DispatchWriteForTests = action => action() is gone;
	// that inline swap is what concealed the regression) and asserts the ORDERING between the
	// clipboard write and Complete():
	//
	//   * while the write is PENDING, CompleteCountForTests must be 0. The old design posted the
	//     write and returned immediately, so Complete() ran at once with the write still pending
	//     (CompleteCount == 1, WriteCompleted == false) - THIS is the discriminator, and it is
	//     exactly the state that made the posted write droppable: by the time Complete->Shutdown
	//     drains the queue, the write is still sitting in it. The fix AWAITS the write first, so
	//     Complete cannot run until the write has finished.
	//
	//   * once the write is RELEASED, CompleteCountForTests becomes 1 and WriteCompleted is true,
	//     i.e. the dismissal finished by COMPLETING the write, not by racing past it.
	//
	// WHAT IS NOT COVERED, stated honestly. This test host has no running dispatcher loop, so it
	// cannot run Avalonia's real ClassicDesktopStyleApplicationLifetime.Shutdown() to observe the
	// queue drain first-hand - doing so would shut down the PROCESS-WIDE dispatcher that the
	// shared AvaloniaUiCollection window probes depend on, and was MEASURED to break four of them.
	// The drain-and-abort behaviour that a POSTED write would suffer is therefore established
	// against the pinned framework SOURCES (Dispatcher.MainLoop.cs ShutdownImpl aborts every
	// queued operation) and by the ordering assertion above, which fails for the posted design
	// because posted work cannot complete before the synchronous Complete() that follows it.
	//
	// The write's own wait is never released by the test until it has observed the pending state,
	// so nothing here is timing-dependent: the pending assertions are read before Complete can
	// legitimately have run, and the released assertions read a definitive completed task.
	[Fact]
	public async Task ExternalDismissal_DoesNotBlockTheCaller_AndCompletesTheWriteBeforeTheSessionTerminates()
	{
		PendingClipboardWrite clipboard = new();
		PickerSession session = NewSessionWithPendingClipboard(clipboard);

		// Drive the real entry point on a dedicated thread. A fresh Thread starts with
		// SynchronizationContext.Current == null (MEASURED elsewhere in this file), so the await's
		// continuation is NOT parked on a dispatcher this test never pumps - it resumes on the
		// completing thread when the write is released below.
		Thread thread = new(() => _ = session.SimulateExternalDismissalForTestsAsync()) { IsBackground = true };
		thread.Start();

		// (a) The write must be STARTED, and the caller must not have parked waiting on it: the
		// moment the write is observed, the session must still be running (Complete has not run).
		bool writeStarted = await WaitUntilAsync(() => clipboard.WriteCalls == 1);
		_ = writeStarted.Should().BeTrue(
			"the dismissal must reach the clipboard write; pre-fix (sync-over-async) the caller was blocked "
			+ "inside GetResult() and reached the write only after the OS released the clipboard");

		_ = clipboard.WriteCompleted.Should().BeFalse("the clipboard write was deliberately never released");

		// THE DISCRIMINATOR. The write is still pending, so a correct implementation (which AWAITS
		// it before Complete) cannot have terminated the session. The old post-and-drop design
		// would have run Complete() here already, with the write still pending and therefore still
		// in the queue for the shutdown to abort.
		_ = session.CompleteCountForTests.Should().Be(
			0,
			"the session must not terminate while the clipboard write is still pending: that is exactly the "
			+ "window in which a POSTED write sits in the dispatcher queue and is aborted by the shutdown Complete() "
			+ "triggers, silently dropping the copy (the regression this fix removes)");

		// Release the write: only NOW may the dismissal finish, and it must finish by actually
		// completing the write before terminating.
		clipboard.Complete();

		bool terminated = await WaitUntilAsync(() => session.CompleteCountForTests == 1);
		_ = terminated.Should().BeTrue("the dismissal must complete once the clipboard write is released");
		_ = clipboard.WriteCompleted.Should().BeTrue("the write must really have completed, not been dropped");
		_ = session.LastClipboardWriteForTests.Should().Be("https://example.com/", "a completed write is recorded");

		// The write could not have been aborted by the shutdown, because the shutdown (inside
		// Complete) had not run yet when the write completed: CompleteCountForTests went 0 -> 1
		// AFTER WriteCompleted turned true, which is precisely the ordering a posted write cannot
		// achieve.
	}

	// The companion half: the write is best-effort, so a clipboard that THROWS must not break the
	// dismissal, and the cleanup-recovery toast - which the contract says is shown only after a
	// SUCCESSFUL write - must not appear for a failed one. This guards the toast call against a
	// regression that showed it unconditionally, and it also pins that a failed write still lets
	// the dismissal finish (the async-void body must not let the throw escape).
	[Fact]
	public async Task ExternalDismissal_WhenTheClipboardWriteFails_StillDismissesAndSkipsTheToast()
	{
		bool cleanupToastShown = false;
		PickerSession session = NewSession(new BlockingLaunchHelperClient());
		session.WriteClipboardAsyncForTests = ThrowingWrite;
		session.ConfigureToastHooksForTests(
			onGhostToast: null,
			onGhostToastClosed: null,
			onCleanupToast: _ => cleanupToastShown = true,
			onCleanupToastClosed: null);

		int exit = await session.SimulateExternalDismissalForTestsAsync();

		_ = exit.Should().Be(ExitCodes.Success, "a failing clipboard write must never fail the dismissal");
		_ = session.CompleteCountForTests.Should().Be(1, "the dismissal must still complete exactly once");

		await Task.Delay(50);
		_ = cleanupToastShown.Should().BeFalse(
			"the cleanup-recovery toast is shown only AFTER a successful write, so a failed write must not show it");
	}

	// The caller RETURNS; the helper call is still in flight. Five seconds is a deadlock
	// detector, not a performance assertion: MEASURED over six consecutive full-suite runs the
	// caller returned in 1-19 ms, and the pre-fix code never returns at all (the fake helper
	// completes only when the test releases it).
	private static bool RunOnADedicatedThread(Action action)
	{
		Thread thread = new(() => action()) { IsBackground = true };
		thread.Start();

		return thread.Join(TimeSpan.FromSeconds(5));
	}

	private static async Task<bool> WaitUntilAsync(Func<bool> condition)
	{
		Stopwatch elapsed = Stopwatch.StartNew();

		while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(10))
		{
			await Task.Delay(25);
		}

		return condition();
	}

	private static PickerSession NewSession(
		ILaunchHelperClient launchClient,
		IStorageHelperClient? storageClient = null,
		bool firstRunRequired = false,
		bool emptySnapshot = false)
	{
		BrowserSnapshot snapshot = emptySnapshot
			? BrowserSnapshot.Empty
			: new BrowserSnapshot([
				new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
			], 1, DateTimeOffset.UtcNow);

		ActivationRequest activation = new(
			"https://example.com/",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		return new PickerSession(
			new PickerRequest(
				activation,
				snapshot,
				firstRunRequired ? FirstRunConsentContract.Required : FirstRunConsentContract.None),
			launchClient,
			new FirstRunConsentActionCoordinator(storageClient ?? new ImmediateStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance,
			trackingCleanupOptions: null,
			trackingCleanupAuditWriter: new InertAuditWriter());
	}

	// Same session shape, plus the pending clipboard write defect #36 needs. The URL is the same
	// safe https://example.com/ the other tests use, so the smart-skip rule lets the write through
	// and the assertion is about BLOCKING, not about the skip policy.
	private static PickerSession NewSessionWithPendingClipboard(PendingClipboardWrite clipboard)
	{
		PickerSession session = NewSession(new BlockingLaunchHelperClient());
		session.WriteClipboardAsyncForTests = clipboard.WriteAsync;
		return session;
	}

	// Never completes until the test releases it, so a caller that WAITS on the write can be told
	// apart from a caller that hands the wait off. Mirrors BlockingLaunchHelperClient, and -
	// unlike the pre-existing synchronous WriteClipboardForTests seam - it can express a write that
	// is still PENDING, which is the state the defect is about.
	private sealed class PendingClipboardWrite
	{
		private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public int WriteCalls { get; private set; }

		public bool WriteCompleted => _completion.Task.IsCompleted;

		public Task WriteAsync(string url)
		{
			_ = url;
			WriteCalls++;
			return _completion.Task;
		}

		// Releases the write. Called by the test AFTER it has observed the dismissal yield the
		// thread, so the write can be shown to resume the dismissal rather than having been
		// awaited synchronously (which would have parked the caller).
		public void Complete()
		{
			_ = _completion.TrySetResult();
		}
	}

	// Best-effort proof: the write fails the way a contended/locked clipboard does in production,
	// and the dismissal must neither block, nor crash, nor show the success toast.
	private static Task ThrowingWrite(string url)
	{
		_ = url;
		throw new InvalidOperationException("clipboard is held open by another process");
	}

	// MEASURED WHY THIS EXISTS. PickerSession defaults to LocalTrackingCleanupAuditWriter,
	// which acquires a PROCESS-GLOBAL named mutex (5 s timeout) and rewrites the user's real
	// %LocalAppData%\BroChoos\logs\cleanup_events.jsonl (2000-line cap) on every session
	// start. xUnit runs classes in parallel, and several existing classes in this assembly
	// build a session without injecting a writer, so they contend on that same mutex. In the
	// full suite this test's 5 s join budget was consumed by that contention (MEASURED:
	// isolated 43 ms, full-suite >5000 ms), which would make it fail for a reason that has
	// nothing to do with the helper handoff it is supposed to prove. Injecting an inert
	// writer removes the unrelated global resource AND stops the suite writing the user's log.
	private sealed class InertAuditWriter : ITrackingCleanupAuditWriter
	{
		public void Write(TrackingCleanupAuditEvent cleanupEvent)
		{
			_ = cleanupEvent;
		}
	}

	// Never completes until the test releases it, so a caller that WAITS on it can be told
	// apart from a caller that hands the wait off.
	private sealed class BlockingLaunchHelperClient : ILaunchHelperClient
	{
		private readonly TaskCompletionSource<LaunchHelperOutcome> _completion = new();

		public int LaunchCalls { get; private set; }

		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			LaunchCalls++;
			return _completion.Task;
		}

		public void Complete(LaunchStatus status = LaunchStatus.Success)
		{
			_ = _completion.TrySetResult(new LaunchHelperOutcome(status, "edge-stable", null, null, null));
		}
	}

	private sealed class BlockingStorageHelperClient : IStorageHelperClient
	{
		private readonly TaskCompletionSource<StorageDiscoveryResponse> _completion = new();

		public int DiscoverCalls { get; private set; }

		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			_ = command;
			_ = cancellationToken;
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			DiscoverCalls++;
			return _completion.Task;
		}

		public void Complete()
		{
			BrowserSnapshot snapshot = new([
				new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
			], 2, DateTimeOffset.UtcNow);

			_ = _completion.TrySetResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, snapshot, null));
		}

		// W6.2: refusal by contract. This client exists to hold the DISCOVERY call open, so a
		// publication that silently succeeded here would be an unused success path pretending
		// to be exercised.
		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.publish-not-used-by-this-test"));
		}
	}

	private sealed class ImmediateStorageHelperClient : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			_ = command;
			_ = cancellationToken;
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, null, null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = request;
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
