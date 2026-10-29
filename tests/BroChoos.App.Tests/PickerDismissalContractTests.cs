using System.Diagnostics;
using BroChoos.App.Picker;
using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

// W5.2 DISMISSAL CONTRACT (spec 3.3, plan "click-outside, focus loss/Alt+Tab, Win+D, sleep,
// and lock dismiss the Picker").
//
// WHAT WAS MISSING. Esc (two-stage; the first Esc closes details, the second dismisses) and
// the 100 ms Enter gate already existed. The dismissal contract had NO path at all for the
// OS-level dismissals: nothing in src/BroChoos.App handled the window losing activation, so
// clicking another window, Alt+Tab, or Win+D left the borderless topmost picker sitting on
// the desktop - the user had no way to make it go away except Esc.
//
// WHAT THESE PIN. An external dismissal (the window's Deactivated event, which is what the
// platform raises for click-outside / Alt+Tab / Win+D / lock) must reach the SAME cancellation
// path Esc uses - cancel-to-clipboard and a Success exit - and it must close the WHOLE picker,
// not just a details layer: unlike Esc, there is no visible details step to consume first.
//
// THE WINDOW-LEVEL HALF LIVES IN PickerXamlLoadTests (`Window_deactivation_invokes_the_dismiss_callback`,
// `Window_deactivation_without_a_dismiss_callback_is_safe`), NOT here: those facts construct a real
// Window on the shared, process-wide Avalonia dispatcher, which is thread-affine, so a probe driven
// from a class running in parallel fails with "Call from invalid thread" (MEASURED). These facts are
// Avalonia-free on purpose - they drive PickerSession directly and read source - so this class can
// run concurrently with the probe host without racing it.
public sealed class PickerDismissalContractTests
{
	[Fact]
	public async Task Session_ExternalDismissal_UsesSameCancelPathAsEscape()
	{
		string? written = null;
		PickerSession session = NewSession();
		session.ConfigureClipboardHooksForTests(null, value => written = value);

		int exit = await session.SimulateExternalDismissalForTestsAsync();

		_ = exit.Should().Be(ExitCodes.Success, "an external dismissal is a user cancel, not a failure");
		_ = session.CompleteCountForTests.Should().Be(1, "the dismissal must terminate the session exactly once");
		_ = written.Should().Be("https://example.com/", "the external dismissal must reach the SAME cancel-to-clipboard path Esc uses");
		_ = session.LastClipboardWriteForTests.Should().Be("https://example.com/");
	}

	[Fact]
	public async Task Session_ExternalDismissal_SkipsClipboardForSensitiveTargets()
	{
		string? written = null;
		PickerSession session = NewSession("https://example.com/?access_token=abc");
		session.ConfigureClipboardHooksForTests(null, value => written = value);

		int exit = await session.SimulateExternalDismissalForTestsAsync();

		_ = exit.Should().Be(ExitCodes.Success);
		_ = written.Should().BeNull("the smart-skip rule must hold on the external dismissal path too");
	}

	// F2: the dismissal guard on OnExternalDismissal was not test-enforced. It exists because a
	// real deactivate can be delivered MORE THAN ONCE, and a dismissal after a launch already
	// completed must do nothing - including not re-running the cancel-to-clipboard write on a dead
	// session. This drives repeated dismissals and pins the write count at exactly 1.
	//
	// BOTH GUARDS ARE EXERCISED, because they cover two different windows and only one of them
	// is `_terminal`. The first half below uses the SYNCHRONOUS write seam, so every repeat
	// arrives after the session is already terminal - that is the `_terminal` window. The second
	// half switches to an ASYNC write that is held PENDING, so the repeats arrive while the first
	// write is still in flight and the session is NOT yet terminal - the window only the
	// synchronous dismissal guard (TryClaimDismissal) can cover, and the one in which this
	// contract was actually broken (DEFECT #36-c). Keeping both halves in one fact is deliberate:
	// the contract is a single sentence ("the dismissal side effects run exactly once"), and it
	// now has to hold irrespective of whether the duplicate arrives before or after the write
	// resolves.
	[Fact]
	public async Task Session_RepeatedExternalDismissal_WritesClipboardExactlyOnce()
	{
		// Half one: repeats AFTER the first dismissal has settled (terminal-guard window).
		int writes = 0;
		PickerSession session = NewSession();
		session.ConfigureClipboardHooksForTests(null, _ => writes++);

		_ = await session.SimulateExternalDismissalForTestsAsync();
		int firstExit = session.CompleteCountForTests;

		// The platform may re-deliver Deactivated; every repeat must be a no-op.
		_ = await session.SimulateExternalDismissalForTestsAsync();
		_ = await session.SimulateExternalDismissalForTestsAsync();
		_ = await session.SimulateExternalDismissalForTestsAsync();

		_ = writes.Should().Be(1, "the terminal guard must keep the cancel-to-clipboard write from running on a dead session");
		_ = session.CompleteCountForTests.Should().Be(firstExit, "a repeat dismissal must not complete the session again");
		_ = session.CompleteCountForTests.Should().Be(1, "the session terminates exactly once");

		// Half two: repeats DURING the first dismissal, while its clipboard write is still
		// pending and `_terminal` has not been set yet (synchronous dismissal-guard window). The
		// session is fresh, so nothing above can be what keeps the count at 1.
		PendingClipboardWrite pendingClipboard = new();
		PickerSession pendingSession = NewSession();
		pendingSession.WriteClipboardAsyncForTests = pendingClipboard.WriteAsync;

		Thread firstDismissal = new(() => _ = pendingSession.SimulateExternalDismissalForTestsAsync()) { IsBackground = true };
		firstDismissal.Start();

		_ = (await WaitUntilAsync(() => pendingClipboard.WriteCalls == 1, EffectBudget)).Should().BeTrue("the first dismissal must reach the write");
		_ = pendingClipboard.WriteCompleted.Should().BeFalse("the write is held pending, so the session is not terminal yet");

		// Three duplicates, exactly like the repeats above, but now inside the pending window.
		Thread duplicateOne = new(() => _ = pendingSession.SimulateExternalDismissalForTestsAsync()) { IsBackground = true };
		Thread duplicateTwo = new(() => _ = pendingSession.SimulateExternalDismissalForTestsAsync()) { IsBackground = true };
		Thread duplicateThree = new(() => _ = pendingSession.SimulateExternalDismissalForTestsAsync()) { IsBackground = true };
		duplicateOne.Start();
		duplicateTwo.Start();
		duplicateThree.Start();

		// Barrier: JOIN the duplicates instead of waiting a fixed budget. Each duplicate's dismissal
		// runs synchronously on its own thread up to the first await - a guarded one returns at the
		// claim, an unguarded one reaches the (counted) write call - so once the thread has finished
		// that work the count is final. A fixed 2 s wait could expire before a thread even started
		// under load and pass vacuously. The assertion is the discriminator, not the wait.
		_ = duplicateOne.Join(EffectBudget).Should().BeTrue("the duplicate dismissal must run");
		_ = duplicateTwo.Join(EffectBudget).Should().BeTrue("the duplicate dismissal must run");
		_ = duplicateThree.Join(EffectBudget).Should().BeTrue("the duplicate dismissal must run");

		_ = pendingClipboard.WriteCalls.Should().Be(
			1,
			"a repeat dismissal delivered while the first write is still pending must not start a second write: "
			+ "`_terminal` does not exist yet at that point, so only the synchronous claim covers this window");

		pendingClipboard.Complete();
		_ = await WaitUntilAsync(() => pendingSession.CompleteCountForTests == 1, EffectBudget);

		_ = pendingSession.CompleteCountForTests.Should().Be(1, "the session terminates exactly once even with the pending-window repeats");
		_ = pendingClipboard.WriteCalls.Should().Be(1, "no repeat may ever produce a second cancel-to-clipboard write");
	}

	// DEFECT #36-c (MED): THE PENDING-WRITE WINDOW THE TEST ABOVE CANNOT SEE.
	//
	// The test above is honest but INCOMPLETE, and its own green is what let the defect live: it
	// uses the SYNCHRONOUS `WriteClipboardForTests` seam, whose `Action<string>` body runs to
	// completion inside the first call. The first dismissal therefore finishes and reaches
	// `Complete` - which sets `_terminal`, the only guard either caller had - BEFORE the second
	// dismissal is ever driven, so every repeat really does see a terminal session and the
	// write count is 1 for the wrong reason.
	//
	// In production the write is AWAITED (`WriteClipboardAsyncForTests` / the real
	// `IClipboard.SetTextAsync`), and `_terminal` is set only INSIDE `Complete` - i.e. AFTER the
	// await. That leaves a real window, however long the OS clipboard service takes (and another
	// process holding the clipboard open makes it unbounded), in which the session is neither
	// terminal nor dismiss-guarded. A duplicate `Deactivated` - which PickerWindow's own comment
	// says can be delivered more than once, and which the handler forwards on EVERY deactivation -
	// or a repeat Escape arriving in that window re-enters and runs the cancel-to-clipboard write
	// a SECOND time. The damage is not the exit code (Complete is idempotent, exit stays Success)
	// but the duplicated write itself: a second OLE clipboard write can retry for about a second,
	// and it is the user's URL being handed to the clipboard service twice.
	//
	// THE DISCRIMINATOR. The clipboard write is held PENDING, so the first dismissal is parked
	// exactly where production parks. A duplicate dismissal is then driven through the SAME
	// production entry point while that first write is unresolved. Pre-fix the second call
	// re-enters the cancel path and increments the write count to 2; post-fix the synchronous
	// dismissal guard (claimed BEFORE the await) turns it into an immediate no-op, so the count
	// stays 1 and the only write that ever completes is the first one.
	//
	// The test observes the state BEFORE releasing the write, so it cannot pass by accident of
	// the duplicate arriving after the first dismissal finished: `clipboard.WriteCompleted` is
	// asserted false at the moment the duplicate is driven.
	[Fact]
	public async Task Session_DuplicateDismissal_WhileTheFirstClipboardWriteIsPending_DoesNotWriteTwice()
	{
		PendingClipboardWrite clipboard = new();
		PickerSession session = NewSession();
		session.WriteClipboardAsyncForTests = clipboard.WriteAsync;

		// Drive the first dismissal on its own thread; it parks on the pending write. The fresh
		// thread starts with SynchronizationContext.Current == null (MEASURED in
		// PickerUiThreadResponsivenessTests), so the continuation is not posted to a dispatcher
		// this test never pumps.
		Thread first = new(() => _ = session.SimulateExternalDismissalForTestsAsync()) { IsBackground = true };
		first.Start();

		bool started = await WaitUntilAsync(() => clipboard.WriteCalls == 1, EffectBudget);
		_ = started.Should().BeTrue("the first dismissal must reach the clipboard write");
		_ = clipboard.WriteCompleted.Should().BeFalse("the write is deliberately held pending, which is the window under test");

		// THE DUPLICATE. This is the second `Deactivated` the platform may deliver, arriving while
		// the first write is still in flight - and, critically, while the session is NOT yet
		// terminal (`Complete` has not run). It must be a complete no-op.
		Thread second = new(() => _ = session.SimulateExternalDismissalForTestsAsync()) { IsBackground = true };
		second.Start();

		// Give the duplicate every chance to re-enter and start a second write. A bounded wait is
		// the barrier: the unguarded path reaches the write within microseconds, while a guarded
		// one consumes the budget and passes - and the assertion below is the discriminator either
		// way, because the count is read AFTER the wait, not before it.
		// Joined, not waited: see the pending-window half of the test above for why.
		_ = second.Join(EffectBudget).Should().BeTrue("the duplicate dismissal must run");

		_ = clipboard.WriteCalls.Should().Be(
			1,
			"a duplicate dismissal arriving while the first cancel-to-clipboard write is still pending must not "
			+ "start a second clipboard write: `_terminal` is set only inside Complete (after the await), so "
			+ "without a guard claimed BEFORE the await the dismissal side effect runs twice");

		// Release the first write: one dismissal completes, exactly once.
		clipboard.Complete();
		_ = await WaitUntilAsync(() => session.CompleteCountForTests == 1, EffectBudget);

		_ = clipboard.WriteCompleted.Should().BeTrue("the one write that was started must still be awaited to completion");
		_ = session.CompleteCountForTests.Should().Be(1, "the session terminates exactly once");
		_ = clipboard.WriteCalls.Should().Be(1, "no duplicate write may be started even after the first one completes");
		_ = session.LastClipboardWriteForTests.Should().Be("https://example.com/");
	}

	// F3 (REWRITTEN CONTRACT): a deactivation while a launch is in flight is deferred; success
	// completes once with no clipboard write; failure shows the error and keeps the picker open.
	//
	// THIS REPLACES THE EARLIER "DEACTIVATION = CANCEL" CONTRACT for the in-flight window. The
	// earlier version of this fact required a deactivation during the launch to run the cancel
	// path (one clipboard write, session completed) and the late outcome to be ignored. In
	// production that deactivation is almost always the browser we just opened stealing focus,
	// so the old contract overwrote the user's clipboard on every successful launch and, on a
	// failed launch, ended the session before the error could be shown. Outside the in-flight
	// window nothing changes: a deactivation is still a cancel (pinned by the facts above).
	//
	// WHAT IS STILL PINNED from the original purpose: a late outcome never acts twice, never
	// completes the session twice, and never copies the link.
	//
	// TWO DISCRIMINATORS, one per guard:
	//   * Half one pins the DEFERRAL. Without the in-flight branch in OnExternalDismissal the
	//     deactivation writes the clipboard and completes the session at once: `writes` becomes 1
	//     and `CompleteCountForTests` is 1 before the outcome is released.
	//   * Half two pins the POST-AWAIT RE-CHECK. The session is made terminal while the launch is
	//     still pending (deferred deactivation, then an OS close). `IsLaunchInFlightForTests` is
	//     cleared only on the post-await path, after the `if (_terminal || vm is null) return;`
	//     re-check, so with the re-check present it stays true; without it, it flips to false
	//     within microseconds of `SetResult`. The bounded wait gives the unguarded path time to
	//     flip it. No console capture is used: this class runs in parallel with the Avalonia XAML
	//     probes, and `Console.SetOut` is process-wide.
	[Fact]
	public async Task Session_DeactivationDuringInFlightLaunch_IsDeferred_SuccessCompletesOnceWithNoClipboardWrite()
	{
		// Half one: the deferral.
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int writes = 0;
		PickerSession session = NewSession(launchHelper: new PendingLaunchHelperClient(pending));
		session.ConfigureClipboardHooksForTests(null, _ => writes++);

		session.ClickCardForTests(1); // starts the launch; the helper has not returned
		_ = session.IsLaunchInFlightForTests.Should().BeTrue("the helper call is deliberately still pending");

		_ = await session.SimulateExternalDismissalForTestsAsync();

		_ = writes.Should().Be(0, "a deactivation during an in-flight launch is deferred: it must not run cancel-to-clipboard");
		_ = session.CompleteCountForTests.Should().Be(0, "a deferred deactivation must not end the session");
		_ = session.IsLaunchInFlightForTests.Should().BeTrue("the launch is still pending");

		pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Success, "edge-stable", null, null, null));

		_ = (await WaitUntilAsync(() => session.CompleteCountForTests == 1, EffectBudget)).Should().BeTrue("a successful launch completes the session");

		// The platform may re-deliver Deactivated after the launch completed: still a no-op.
		_ = await session.SimulateExternalDismissalForTestsAsync();

		_ = session.CompleteCountForTests.Should().Be(1, "the session completes exactly once");
		_ = writes.Should().Be(0, "a successful launch must never copy the link");
		_ = session.LastClipboardWriteForTests.Should().BeNull();

		// Half two: the post-await re-check. The session becomes terminal while the launch is
		// still pending, so the late Success outcome must be moot.
		TaskCompletionSource<LaunchHelperOutcome> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession closed = NewSession(launchHelper: new PendingLaunchHelperClient(late));
		closed.ConfigureClipboardHooksForTests(null, _ => writes++);

		closed.ClickCardForTests(1);
		_ = await closed.SimulateExternalDismissalForTestsAsync(); // deferred
		closed.SimulateWindowClosedForTests(); // OS close while the launch is in flight
		_ = closed.CompleteCountForTests.Should().Be(1);

		late.SetResult(new LaunchHelperOutcome(LaunchStatus.Success, "edge-stable", null, null, null));

		// Negative barrier: give the unguarded continuation time to clear the flag.
		_ = await WaitUntilAsync(() => !closed.IsLaunchInFlightForTests);

		_ = closed.IsLaunchInFlightForTests.Should().BeTrue(
			"the late outcome must not enter the post-await body on a terminal session: that body clears "
			+ "the in-flight flag and treats the outcome as live, which the `if (_terminal || vm is null) "
			+ "return;` re-check exists to prevent");
		_ = closed.CompleteCountForTests.Should().Be(1, "the late outcome must not complete the session a second time");
		_ = writes.Should().Be(0, "neither the deferred deactivation nor the late outcome may copy the link");
	}

	// F3-b (REWRITTEN CONTRACT): a deactivation while a launch is in flight is deferred; success
	// completes once with no clipboard write; failure shows the error and keeps the picker open.
	// This fact pins the FAILURE half. It replaces the earlier "deactivation = cancel" contract for
	// the same reason as the fact above: the opening browser steals focus, and the old contract
	// ended the session first, so a failed launch was never shown to the user.
	//
	// Half one: the deferred deactivation leaves the session live, the Failed outcome paints the
	// fail-open error and the picker stays open. A later deactivation, with no launch in flight,
	// is a normal cancel again: the deferral does not leak past the launch.
	// Half two: the post-await re-check, through the view-model surface. The session becomes
	// terminal while the launch is pending; a late Failed outcome acted on would call
	// `vm.ShowLaunchFailure`, so the error status must stay hidden.
	[Fact]
	public async Task Session_DeactivationDuringInFlightLaunch_IsDeferred_FailureShowsErrorAndKeepsPickerOpen()
	{
		// Half one: deferral, then a visible failure.
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int writes = 0;
		PickerSession session = NewSession(launchHelper: new PendingLaunchHelperClient(pending));
		session.ConfigureClipboardHooksForTests(null, _ => writes++);

		session.ClickCardForTests(1);
		_ = await session.SimulateExternalDismissalForTestsAsync();

		_ = session.CompleteCountForTests.Should().Be(0, "a deferred deactivation must not end the session");
		_ = writes.Should().Be(0, "a deferred deactivation must not copy the link");

		pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Failed, "edge-stable", "launch.start-failed", "launch", "launch.error.failed"));

		_ = (await WaitUntilAsync(() => session.IsErrorStatusVisibleForTests, EffectBudget)).Should().BeTrue("a failed launch must show its error");
		_ = session.CompleteCountForTests.Should().Be(0, "a failed launch keeps the picker open");
		_ = session.IsLaunchInFlightForTests.Should().BeFalse("the in-flight window is over: the user may pick another card");
		_ = writes.Should().Be(0, "the late outcome must not copy the link");

		// No launch in flight any more: a deactivation is a user cancel again, exactly once.
		_ = (await session.SimulateExternalDismissalForTestsAsync()).Should().Be(ExitCodes.Success);
		_ = session.CompleteCountForTests.Should().Be(1, "the later dismissal completes the session exactly once");
		_ = writes.Should().Be(1, "the later dismissal is the normal cancel-to-clipboard path");

		// Half two: the post-await re-check, observed through the error status.
		TaskCompletionSource<LaunchHelperOutcome> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession closed = NewSession(launchHelper: new PendingLaunchHelperClient(late));
		closed.ConfigureClipboardHooksForTests(null, _ => { });

		closed.ClickCardForTests(1);
		_ = await closed.SimulateExternalDismissalForTestsAsync(); // deferred
		closed.SimulateWindowClosedForTests(); // OS close while the launch is in flight
		_ = closed.CompleteCountForTests.Should().Be(1);

		late.SetResult(new LaunchHelperOutcome(LaunchStatus.Failed, "edge-stable", "launch.start-failed", "launch", "launch.error.failed"));

		// Negative barrier: the unguarded Failed branch paints the status when it resumes.
		_ = await WaitUntilAsync(() => closed.IsErrorStatusVisibleForTests);

		_ = closed.IsErrorStatusVisibleForTests.Should().BeFalse(
			"a late Failed outcome must not paint the error on a terminal session: the "
			+ "`if (_terminal || vm is null) return;` re-check is what makes the outcome moot");
		_ = closed.CompleteCountForTests.Should().Be(1, "the late outcome must not complete the session a second time");
	}

	[Fact]
	public void Session_ExternalDismissal_IsSingleStageUnlikeEsc()
	{
		// Unlike Esc - whose FIRST press closes an open details layer and only the SECOND dismisses -
		// an external dismissal must dismiss in ONE event: there is no visible details step to
		// consume, and the plan says these OS events dismiss the picker. The details layer has no
		// open entry point in this milestone (IsDetailsOpen is only ever written false by
		// HandleEscape), so the reachable assertion is the structural one: the dismissal path must
		// NOT route through the two-stage view-model handler.
		string source = File.ReadAllText(Path.Combine(Root(), "src", "BroChoos.App", "Picker", "PickerSession.cs"));

		// DEFECT #36 REGRESSION: the signature is now `async void`, and the awaited write is what
		// keeps the clipboard copy from being aborted by the shutdown Complete() triggers (see
		// TryCancelToClipboardOnDismissAsync). The structural pins below are deliberately written
		// against the CURRENT shape and will fail if either the async shape or the reach into the
		// cancel path changes.
		int dismissalStart = source.IndexOf("private async void OnExternalDismissal()", StringComparison.Ordinal);
		_ = dismissalStart.Should().BeGreaterThan(-1, "OnExternalDismissal must exist as the single external-dismissal entry point, and must be async so its clipboard write completes before Complete() shuts the session down");

		int dismissalEnd = source.IndexOf("\n\tprivate ", dismissalStart + 10, StringComparison.Ordinal);
		string body = dismissalEnd > dismissalStart
			? source[dismissalStart..dismissalEnd]
			: source[dismissalStart..];

		_ = body.Should().Contain("await TryCancelToClipboardOnDismissAsync()",
			"an external dismissal is a cancel, so it must reach the same cancel-to-clipboard path Esc uses, "
			+ "and it must AWAIT it: a fire-and-forget (or posted) write would be aborted by the shutdown Complete() runs");
		_ = body.Should().Contain("Complete(ExitCodes.Success)",
			"an external dismissal exits Success, like a user cancel");
		_ = body.Should().NotContain("HandleEscape",
			"routing through HandleEscape would make the dismissal two-stage (details first); an external "
			+ "dismissal must close the whole picker in one event");
	}

	// Bounded wait for the unguarded continuation to reveal itself. It returns when the condition
	// is satisfied OR the budget expires, so the caller's assertion is the discriminator either
	// way: a regression flips the condition within microseconds and fails the assertion, while the
	// guarded code simply consumes the budget and passes. (Negative assertions need this barrier;
	// asserting immediately after SetResult would race the continuation and could pass vacuously.)
	//
	// TWO BUDGETS. A NEGATIVE barrier (waiting for a regression that must NOT happen) spends its
	// whole budget on every passing run, so it keeps the 2 s default. A POSITIVE wait (waiting for
	// an effect that MUST happen) returns as soon as it is true and only costs time on failure, so
	// it gets EffectBudget: 2 s expired once (2.16 s) under memory pressure. Neither is a product
	// timing contract.
	private static readonly TimeSpan EffectBudget = TimeSpan.FromSeconds(10);

	private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? budget = null)
	{
		TimeSpan limit = budget ?? TimeSpan.FromSeconds(2);
		Stopwatch elapsed = Stopwatch.StartNew();
		while (!condition() && elapsed.Elapsed < limit)
		{
			await Task.Delay(10);
		}

		return condition();
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

	private static PickerSession NewSession(string url = "https://example.com/", ILaunchHelperClient? launchHelper = null)
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
			launchHelper ?? new FakeLaunchHelperClient(),
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);
	}

	private sealed class FakeLaunchHelperClient : ILaunchHelperClient
	{
		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null));
		}
	}

	// Holds the launch open so a dismissal can interleave with the awaited helper call (F3).
	private sealed class PendingLaunchHelperClient(TaskCompletionSource<LaunchHelperOutcome> pending) : ILaunchHelperClient
	{
		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			return pending.Task;
		}
	}

	// Never completes until the test releases it, so a dismissal that AWAITS the cancel-to-clipboard
	// write can be told apart from one that raced past it - and, more to the point here, so the
	// write can still be PENDING while a second dismissal is driven. This is the seam the defect
	// needs: `WriteClipboardForTests` is `Action<string>`, synchronous by construction, so it
	// finishes inside the first call and cannot express the window at all. Mirrors
	// PickerUiThreadResponsivenessTests.PendingClipboardWrite.
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

		// Releases the held write. Called only after the assertions on the pending state have been
		// made, so the pending window this test is about is never accidentally closed under it.
		public void Complete()
		{
			_ = _completion.TrySetResult();
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
