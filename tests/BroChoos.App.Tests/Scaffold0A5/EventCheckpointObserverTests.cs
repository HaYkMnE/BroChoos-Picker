using BroChoos.App.Scaffold0A5;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

// Matches the folder (tests\BroChoos.App.Tests\Scaffold0A5), which is what IDE0130 asks for and
// what every other file in this suite already does. It also keeps the throwaway scaffold's tests
// in a namespace that disappears with the scaffold rather than sitting in the root test namespace.
namespace BroChoos.App.Tests.Scaffold0A5;

// 0A-5 SCAFFOLD COVERAGE - throwaway, goes away with the scaffold it guards.
//
// EventCheckpointObserver is the piece the D-v3.5-493 kill-process harness rests on, and a
// silent break in it does not look like a failure: the harness would stop parking, the parent
// would kill the child at some unrelated point, and the assertions could still pass - a kill
// test passing for the wrong reason. These tests pin the properties that make that impossible
// to miss.
//
// Deliberately fast: a park that is meant to expire is bounded at a few hundred milliseconds,
// and a park that is meant to be released is released by a signal, so the whole class costs
// well under a second. The long bounds below are hang guards only and are never waited out.
//
// DEBUG-ONLY NOW, AND THE COMMENT THIS REPLACES WAS THE REASONING BEHIND A SECURITY DEFECT.
// It read: "NOT #if DEBUG - the scaffold compiles in Release too, so gating these would leave
// it unverified there." The premise was true and was the problem: the scaffold DID compile in
// Release, including a mode that performs a real config write to an argument-supplied path in
// an application the OS hands URLs to as command-line arguments.
//
// This whole FOLDER is now excluded from every non-Debug build by a configuration-scoped
// Compile Remove in BroChoos.App.Tests.csproj, mirroring the one in BroChoos.App.csproj that
// removes the scaffold itself. The gate is in the project file rather than as an #if in this
// file for two reasons: the types under test do not EXIST in Release, so even the using
// directives above would be CS0246 errors there, and keeping the app's gate and its tests'
// gate as the same construct means they cannot drift apart.
//
// NOTHING IS LEFT UNVERIFIED BY THAT, which is the question the old comment was right to ask.
// The property under test is the observer's park/release/expire behaviour, and it is verified
// in the configuration where the observer EXISTS and is used - Debug, which is also the
// configuration the D-v3.5-493 harness builds and runs (tools\atomic-kill-test.ps1). There is
// no Release build of this type for a Release test to cover.
//
// NO BLOCKING TASK WAITS anywhere below (xUnit1031). Task.Wait/.Result on a test thread can
// deadlock depending on the runner's synchronization context, and a test suite that pins a
// concurrency contract must not itself contain the pattern it would be diagnosing. The timed
// "did this complete / is it still parked" question is asked with Task.WhenAny against a
// Task.Delay instead, which is the same observation without occupying the thread.
public sealed class EventCheckpointObserverTests
{
	// The cap for a park the test intends to RELEASE. It must be out of reach of any scheduling
	// delay, because the observer's own WaitOne is a kernel wait that keeps time while the thread
	// pool does not: CI run 41048551150 (test-debug) failed when the old 5 s cap expired and
	// OnStage returned while the 200 ms Task.Delay that was meant to observe "still parked" had
	// not yet completed on a starved pool. Ten minutes is far above HangGuard, so a released park
	// can only end because proceed was set, and an expiry here is a real failure.
	private static readonly TimeSpan ReleasablePark = TimeSpan.FromMinutes(10);

	// The bound on waits that are EXPECTED to succeed. It only turns a hang into a failure, so it
	// is generous: on a loaded machine it costs nothing, because the awaited thing finishes first.
	// Kept below ReleasablePark so a hang is reported by the test, not by a park expiry.
	private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

	// Short on purpose: this one is MEANT to expire.
	private static readonly TimeSpan ExpiringPark = TimeSpan.FromMilliseconds(250);

	[Fact]
	public async Task OnStage_ArmedStage_SignalsReachedThenBlocksUntilProceedIsSet()
	{
		string runId = NewRunId();
		List<AtomicWriteStage> expired = [];

		using EventCheckpointObserver observer = new(
			runId,
			[AtomicWriteStage.BeforePublish],
			ReleasablePark,
			expired.Add);

		using EventWaitHandle reached = OpenGate(EventCheckpointObserver.ReachedEventName(runId, AtomicWriteStage.BeforePublish));
		using EventWaitHandle proceed = OpenGate(EventCheckpointObserver.ProceedEventName(runId, AtomicWriteStage.BeforePublish));

		Task park = Task.Run(() => observer.OnStage(AtomicWriteStage.BeforePublish));

		try
		{
			// The signal must arrive while the call is STILL INSIDE OnStage: the harness asserts
			// against the destination at this instant, so "reached" has to mean "parked here now".
			// The kernel wait runs on a pool thread and is awaited, so the test thread is never
			// blocked (the rule in the class header); HangGuard only turns a hang into a failure.
			_ = (await Task.Run(() => reached.WaitOne(HangGuard))).Should().BeTrue("an armed stage must signal reached");

			// "Still parked" is decided by the observer's state, not by a race: proceed is not set and
			// the park cap is far out of reach, so the only way OnStage can have returned is a defect.
			// IsCompleted is only a fast first check: it catches an observer that had ALREADY returned.
			// The 200 ms negative wait is what detects an early return: it is EXPECTED to time out and
			// gives a broken observer the time to return early, and a slow scheduler can only lengthen it.
			_ = park.IsCompleted.Should().BeFalse("the observer must still be parked, not returned");
			_ = (await CompletedWithin(park, TimeSpan.FromMilliseconds(200)))
				.Should().BeFalse("the observer must still be parked, not returned");
			_ = observer.ExpiredStages.Should().BeEmpty("a park that is still waiting has not expired");

			_ = proceed.Set();

			_ = (await CompletedWithin(park, HangGuard)).Should().BeTrue("setting proceed must release the park");
			_ = expired.Should().BeEmpty("a released park is not an expired one");
			_ = observer.ExpiredStages.Should().BeEmpty();
		}
		finally
		{
			// A failed assertion above must not leave OnStage parked on a pool thread for the
			// 10-minute ReleasablePark. Setting an already-set manual-reset event is harmless.
			_ = proceed.Set();
		}
	}

	[Fact]
	public async Task OnStage_UnarmedStage_ReturnsImmediatelyAndNeverParks()
	{
		string runId = NewRunId();
		List<AtomicWriteStage> expired = [];

		using EventCheckpointObserver observer = new(
			runId,
			[AtomicWriteStage.AfterVerify],
			ExpiringPark,
			expired.Add);

		// Every stage the write path reaches EXCEPT the single armed one. If an unarmed stage
		// parked, the write would stall six times over and the harness would time out.
		AtomicWriteStage[] unarmed =
		[
			AtomicWriteStage.Validated,
			AtomicWriteStage.TempFlushed,
			AtomicWriteStage.LockAcquired,
			AtomicWriteStage.BeforePublish,
			AtomicWriteStage.AfterPublish,
			AtomicWriteStage.AfterPrune,
		];

		// The time is measured INSIDE the task, around the six calls only. The earlier version
		// timed the await from outside, so thread-pool scheduling delay counted against the
		// 500 ms budget, and under the parallel suite that failed once (observed flake).
		TimeSpan loopElapsed = TimeSpan.Zero;
		Task all = Task.Run(() =>
		{
			System.Diagnostics.Stopwatch loop = System.Diagnostics.Stopwatch.StartNew();
			foreach (AtomicWriteStage stage in unarmed)
			{
				observer.OnStage(stage);
			}

			loopElapsed = loop.Elapsed;
		});

		// The completion bound only guards against a hang; it is far above six parks
		// (6 x ExpiringPark = 1.5 s) so it never decides the result on a loaded machine.
		_ = (await CompletedWithin(all, HangGuard)).Should().BeTrue("the calls must return, not hang");

		// Same 500 ms bound as before, now applied to the calls alone: generously above the cost
		// of six hash lookups, so this fails if unarmed stages start waiting.
		_ = loopElapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "unarmed stages must return immediately");
		_ = expired.Should().BeEmpty("an unarmed stage never parks, so it can never expire");
	}

	[Fact]
	public async Task OnStage_ProceedNeverSet_ExpiresAndReturnsInsteadOfHangingForever()
	{
		string runId = NewRunId();
		List<AtomicWriteStage> expired = [];

		using EventCheckpointObserver observer = new(
			runId,
			[AtomicWriteStage.LockAcquired],
			ExpiringPark,
			expired.Add);

		// The Stopwatch wraps ONLY the OnStage call, inside the task, so thread-pool start-up delay
		// is not charged to it. A regression that ignored the configured 250 ms timeout (for example
		// a fallback to a long default) would still complete within HangGuard, so this is the
		// assertion that catches it.
		TimeSpan onStageElapsed = TimeSpan.Zero;
		Task park = Task.Run(() =>
		{
			System.Diagnostics.Stopwatch call = System.Diagnostics.Stopwatch.StartNew();
			observer.OnStage(AtomicWriteStage.LockAcquired);
			onStageElapsed = call.Elapsed;
		});

		// The whole point: nobody ever sets proceed. The observer must free itself on its own
		// timeout - from LockAcquired onward the park is inside the per-file write mutex, so an
		// unbounded wait would wedge every other writer of the file behind a dead parent.
		// HangGuard, not a tight bound: Task.Run itself can start late on a starved pool, so a
		// short wall-clock budget here would measure the scheduler. Nobody can set proceed, so the
		// task can only complete through the observer's own expiry, and expired proves it did.
		_ = (await CompletedWithin(park, HangGuard))
			.Should().BeTrue("the park must be bounded and expire on its own");

		_ = onStageElapsed.Should().BeLessThan(
			ExpiringPark + TimeSpan.FromSeconds(30),
			"the park must expire on the CONFIGURED timeout, not on some longer bound");
		_ = expired.Should().ContainSingle().Which.Should().Be(AtomicWriteStage.LockAcquired);
		_ = observer.ExpiredStages.Should().ContainSingle().Which.Should().Be(AtomicWriteStage.LockAcquired);
	}

	// The no-throw contract, pinned at the ONE place the observer could break it.
	//
	// IAtomicWriteObserver states implementations MUST NOT throw, and AtomicJsonFileStore calls
	// OnStage with NO try/catch by explicit design. The park-expiry callback is caller-supplied,
	// so if it threw and the exception escaped OnStage, the write path would classify it as a
	// WRITE failure - and a HARNESS fault would be reported as a defect in the code under test.
	// That is the single most misleading outcome this scaffold can produce, because the symptom
	// names the store while the cause is in the test rig. Nothing pinned it before.
	[Fact]
	public async Task OnStage_OnParkExpiredThrows_DoesNotEscape()
	{
		string runId = NewRunId();
		List<string> markers = [];

		using EventCheckpointObserver observer = new(
			runId,
			[AtomicWriteStage.AfterPublish],
			ExpiringPark,
			static _ => throw new InvalidOperationException("a broken park-expiry callback"),
			markers.Add);

		// Nobody sets proceed, so the park expires and the throwing callback runs. Wrapped in a
		// Task so the assertion is about the exception ESCAPING OnStage, not about this test
		// thread: an escape would surface as a faulted task here.
		// Same in-task Stopwatch around OnStage alone as in the test above. It is read only when
		// the task did not fault, so an escaping exception is still reported by IsFaulted.
		TimeSpan onStageElapsed = TimeSpan.Zero;
		Task park = Task.Run(() =>
		{
			System.Diagnostics.Stopwatch call = System.Diagnostics.Stopwatch.StartNew();
			observer.OnStage(AtomicWriteStage.AfterPublish);
			onStageElapsed = call.Elapsed;
		});

		Task winner = await Task.WhenAny(park, Task.Delay(HangGuard));

		_ = ReferenceEquals(winner, park).Should().BeTrue("the park must still expire and return, even when the callback throws");
		_ = park.IsFaulted.Should().BeFalse("a throwing park-expiry callback must NOT escape OnStage - the write path has no try/catch around it");
		_ = onStageElapsed.Should().BeLessThan(
			ExpiringPark + TimeSpan.FromSeconds(30),
			"the park must expire on the CONFIGURED timeout, not on some longer bound");

		// The fault is contained, NOT hidden: the expiry is still recorded, and the containment
		// is announced through the marker sink so a harness fault stays diagnosable.
		_ = observer.ExpiredStages.Should().ContainSingle().Which.Should().Be(AtomicWriteStage.AfterPublish);
		_ = markers.Should().Contain(marker => marker.Contains("KILLCHILD_PARK_EXPIRED_CALLBACK_FAILED", StringComparison.Ordinal));
	}

	[Fact]
	public void Constructor_NonPositiveParkTimeout_IsRejected()
	{
		// A zero or negative bound would make a park meaningless (it would expire before the
		// parent could ever observe the checkpoint), which is a harness bug worth failing loudly
		// at construction rather than diagnosing from a confusing run.
		Action zero = () => new EventCheckpointObserver(NewRunId(), [], TimeSpan.Zero, static _ => { }).Dispose();

		_ = zero.Should().Throw<ArgumentOutOfRangeException>();
	}

	// The event names the PowerShell parent rebuilds independently, pinned to their literal
	// format. The parent cannot call this method - it is a different language and a different
	// process - so it composes the same string by hand. A rename or a separator change here still
	// compiles, still passes every other test in this class, and then fails in the harness as a
	// child that never appears to reach its stage: a ~30s bounded wait per case followed by a FAIL
	// that points at the write path rather than at the name. Cheap to pin, expensive to diagnose.
	[Fact]
	public void EventNames_MatchTheLiteralFormatThePowerShellParentRebuilds()
	{
		_ = EventCheckpointObserver.ReachedEventName("run42", AtomicWriteStage.BeforePublish)
			.Should().Be(@"Local\BroChoos_KillTest_run42_BeforePublish_reached");

		_ = EventCheckpointObserver.ProceedEventName("run42", AtomicWriteStage.BeforePublish)
			.Should().Be(@"Local\BroChoos_KillTest_run42_BeforePublish_proceed");
	}

	// Non-blocking "did it finish in time?". Returns false on timeout instead of throwing, so a
	// caller can assert BOTH directions - "still parked" and "released" - with one helper.
	private static async Task<bool> CompletedWithin(Task task, TimeSpan timeout)
	{
		Task winner = await Task.WhenAny(task, Task.Delay(timeout));
		if (!ReferenceEquals(winner, task))
		{
			return false;
		}

		// Awaited so a faulted task surfaces its exception here rather than as an unobserved
		// one later; a park that threw is a failure, not a completion.
		await task;
		return true;
	}

	// A fresh id per test, for the same reason the harness mints one per run: event names are
	// machine-wide within the session, so a latch left set by a previous test would be consumed
	// by the next one and it would observe a park that never happened.
	private static string NewRunId()
	{
		return $"test{Guid.NewGuid():N}";
	}

	private static EventWaitHandle OpenGate(string name)
	{
		// Same name, same kernel object: the observer's constructor already created it, so this
		// opens the existing one rather than racing to create a second.
		return new EventWaitHandle(initialState: false, EventResetMode.ManualReset, name);
	}
}
