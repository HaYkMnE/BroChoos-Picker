using System.Reflection;
using FluentAssertions;
using Xunit;
using Xunit.Sdk;

namespace BroChoos.App.Tests;

// ONE GLOBAL LOCK FOR LocalizationService.CurrentCulture, HELD PER TEST.
//
// The culture is a process-wide static. The classes that read or switch it live in two xUnit
// collections, console-capture and avalonia-ui, and xUnit runs different collections in
// parallel. Collection membership alone therefore cannot stop a switch in one collection from
// landing between another collection's capture and its assertion, or a restore from putting
// back a value someone else set.
//
// [CultureIsolated] sits on BOTH collection definitions, so xUnit applies it to every test in
// both collections: Before takes the gate, After releases it. Every reader and every writer is
// covered, including tests that only compare against GetString and files this change does not
// edit, and any value a test captures and restores is captured and restored while no test in
// the other collection can run. The guard facts below fail if the attribute ever stops being
// applied.
//
// Before and After run on the test's own thread, so the Avalonia Dispatcher keeps its thread
// affinity. SemaphoreSlim has no thread affinity, so an async test that resumes on another
// thread still releases correctly. The gate is not re-entrant: tests must not take it again.
//
// WHAT WAS TRIED FIRST, AND MEASURED TO FAIL. Merging the two collections moved the Avalonia
// classes onto a thread that did not own the dispatcher (54 "Call from invalid thread"
// failures). Holding the gate for a whole collection (a collection fixture) reordered the
// Avalonia classes behind the other collection and failed 18-24 of them with "dispatcher
// processing is suspended". A per-test gate leaves both collections' scheduling as it was.
internal static class CultureGate
{
	internal static readonly SemaphoreSlim Gate = new(1, 1);

	// Bounded: a test that hangs while holding the gate makes the next test fail with a message
	// that names the holder, instead of the whole run waiting silently until the CI job is cancelled.
	internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

	// FAIL FAST AFTER THE FIRST TIMEOUT. A holder that hangs never releases, so without this every
	// later gated test would wait its own full Timeout in turn, and three of them already run past
	// the test job's 30-minute limit. The first waiter that times out records the holder it saw
	// here (so does the console-capture per-test bound in AvaloniaUiThread.cs when it fires);
	// every later waiter throws at once with that name instead of waiting again.
	//
	// The holder, the acquisition epoch and the poison live in one CultureGateState, under one lock
	// (see there for the exact guarantee). Tests drive a private instance of the same type.
	internal static readonly CultureGateState State = new();

	internal static string? PoisonedBy => State.PoisonedBy;

	internal static string? Holder => State.Holder;

	// True only inside a test that took the gate ITSELF. The gate's count alone cannot say
	// that: it also reads as taken while a test of the OTHER collection holds it.
	private static readonly AsyncLocal<bool> HeldByCurrentTestFlag = new();

	internal static bool HeldByCurrentTest
	{
		get => HeldByCurrentTestFlag.Value;
		set => HeldByCurrentTestFlag.Value = value;
	}
}

// WHO HOLDS THE GATE, WHICH ACQUISITION IT IS, AND WHETHER IT IS POISONED: one lock for all three.
//
// Every acquisition gets a new epoch (Take). A poison names an ACQUISITION, not a test name: the
// caller passes the epoch it observed, and the poison is set only if that same acquisition still
// holds the gate at that instant (TryPoisonWhileHeldBy). A waiter records the epoch BEFORE it
// starts waiting, so on timeout it poisons only if one acquisition held the gate for its whole
// wait. A holder that released near the end of the wait, followed by another test that took the
// gate (SemaphoreSlim is not FIFO), therefore can no longer get the new holder poisoned, and two
// rows of one Theory (same Type.Method name) are told apart by their epochs.
//
// INVARIANT, kept under _lock: PoisonedBy is non-null ONLY while Holder equals it, during the
// acquisition that was poisoned. Take clears the poison for the next acquisition, and Release
// clears holder and poison together.
//
// A WAITER THAT STARTED WHILE NOBODY WAS RECORDED AS HOLDER (BeginWait saw Holder == null) also
// accepts the NEXT acquisition, epoch + 1. That is safe because every Gate.Wait success is
// followed at once by Take (TakeGate is the only acquisition path), and State.Release runs right
// before Gate.Release. So while Holder is null at epoch e the gate is free, or its holder is
// between those two release calls, or the next acquirer is between Gate.Wait and Take, and in
// every case the next acquisition is e + 1. Epoch e + 1 therefore held the gate for the whole wait
// apart from a window of a few instructions in program order (in wall time that window also
// includes any OS scheduling delay of the thread running them), and a waiter that lost the race
// to a holder that hangs names it at once instead of leaving the poison to the next waiter, a
// further CultureGate.Timeout (10 minutes) later. Epoch e + 2 or later means the gate changed
// hands during the wait: refused.
//
// WHAT PINS THIS. BroChoos.Architecture.Tests/CultureGateInvariantTests parses every source of
// this project (and every file its MSBuild <Compile Include> items name) with Roslyn, once per CI
// configuration (Debug and Release), and fails unless, on the syntax tree, in each: Gate outside
// CultureGate is used only as Gate.CurrentCount, Gate.Wait(..) or Gate.Release(); Wait and Release
// are called only inside CultureIsolatedAttribute, exactly 2 Wait and 1 Release; each Wait is the
// whole condition of an if, either "if (Gate.Wait(..))" whose block starts with TakeGate(..), or
// "if (!Gate.Wait(..))" with no else, whose block ends in throw and whose next statement is
// TakeGate(..); the attribute declares exactly one TakeGate and one After; the Release takes no
// arguments and is a direct statement of After's body, right after CultureGate.State.Release()
// (written out in full, not as a bare State); and CultureGate.State.Take / State.Release are
// invoked once each, Take as a top-level statement of TakeGate and Release in After. A using
// static or alias of CultureGate, in source or as an MSBuild <Using> item, also fails it. Comments
// and string literals are not code to it; reflection is out of its scope.
internal sealed class CultureGateState
{
	private readonly Lock _lock = new();
	private long _epoch;

	// Read through Volatile for the unlocked diagnostic reads; written only under _lock.
	internal string? Holder
	{
		get => Volatile.Read(ref field);
		private set => Volatile.Write(ref field, value);
	}

	internal string? PoisonedBy
	{
		get => Volatile.Read(ref field);
		private set => Volatile.Write(ref field, value);
	}

	// The epoch of the latest acquisition (0 before the first one).
	internal long Epoch
	{
		get
		{
			lock (_lock)
			{
				return _epoch;
			}
		}
	}

	// Called by the test that has just acquired the gate, on every acquisition path. Returns the
	// new acquisition's epoch.
	internal long Take(string holder)
	{
		lock (_lock)
		{
			Holder = holder;
			PoisonedBy = null;
			return ++_epoch;
		}
	}

	// Called by the holder right before it releases the semaphore.
	internal void Release()
	{
		lock (_lock)
		{
			Holder = null;
			PoisonedBy = null;
		}
	}

	// Poisons the gate only if acquisition `epoch` still holds it right now (and, when `holder` is
	// given, under that name), atomically with that check. `heldBy` is the holder seen under the
	// lock. Returns whether the gate is poisoned in the name of that acquisition afterwards.
	internal bool TryPoisonWhileHeldBy(long epoch, string? holder, out string? heldBy)
	{
		lock (_lock)
		{
			return TryPoisonLocked(_epoch == epoch, holder, out heldBy);
		}
	}

	// Caller holds _lock and has decided whether the current acquisition is the one to name.
	private bool TryPoisonLocked(bool currentAcquisitionMatches, string? holder, out string? heldBy)
	{
		heldBy = Holder;
		if (!currentAcquisitionMatches || heldBy is null || (holder is not null && !string.Equals(heldBy, holder, StringComparison.Ordinal)))
		{
			return false;
		}

		PoisonedBy ??= heldBy;
		return string.Equals(PoisonedBy, heldBy, StringComparison.Ordinal);
	}

	// Recorded by a waiter BEFORE it waits: the latest epoch and whether a holder was recorded,
	// read together under the lock.
	internal WaitStart BeginWait()
	{
		lock (_lock)
		{
			return new WaitStart(_epoch, Holder is null);
		}
	}

	// Called by a waiter that timed out. Poisons only if the acquisition that holds the gate now
	// held it for the whole wait: the epoch is unchanged, or the wait started with no holder
	// recorded and this is the very next acquisition (see the header).
	internal bool TryPoisonAfterWait(WaitStart start, out string? heldBy)
	{
		lock (_lock)
		{
			bool sameAcquisition = _epoch == start.Epoch || (start.HolderWasNull && _epoch == start.Epoch + 1);
			return TryPoisonLocked(sameAcquisition, holder: null, out heldBy);
		}
	}

	internal readonly record struct WaitStart(long Epoch, bool HolderWasNull);

	// True when the gate is poisoned and still held by the acquisition the poison names.
	internal bool IsPoisonedAndStillHeld(out string? poisonedBy)
	{
		lock (_lock)
		{
			poisonedBy = PoisonedBy;
			return poisonedBy is not null && string.Equals(Holder, poisonedBy, StringComparison.Ordinal);
		}
	}
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class CultureIsolatedAttribute : BeforeAfterTestAttribute
{
	public override void Before(MethodInfo methodUnderTest)
	{
		if (CultureGate.PoisonedBy is not null)
		{
			// RECOVERY CHECK FIRST. The poisoned holder clears the poison when it releases
			// (CultureGateState.Release), so this is a defensive path: Wait(0) takes the gate if it
			// is free right now, and TakeGate then clears any poison. A test that hit the original
			// timeout has already failed and named the slow holder, so the incident stays in the
			// results.
			if (CultureGate.Gate.Wait(0))
			{
				TakeGate(methodUnderTest);
				return;
			}

			// Fail fast only if the poison is still true at this instant: the gate is still held
			// by the acquisition it names. Otherwise this test simply waits like any other.
			if (CultureGate.State.IsPoisonedAndStillHeld(out string? poisonedBy))
			{
				throw new TimeoutException(
					$"CultureGate is poisoned: it is still held by {poisonedBy}, which already ran past a bound (a waiter's {CultureGate.Timeout.TotalMinutes} minutes, or the console-capture per-test bound). Failing at once instead of waiting again.");
			}
		}

		// Recorded BEFORE the wait: on timeout the gate is poisoned only if the acquisition that
		// holds it now held it for the whole wait (see CultureGateState).
		CultureGateState.WaitStart waitStart = CultureGate.State.BeginWait();
		if (!CultureGate.Gate.Wait(CultureGate.Timeout))
		{
			bool poisoned = CultureGate.State.TryPoisonAfterWait(waitStart, out string? heldBy);
			throw new TimeoutException(poisoned
				? $"CultureGate was not released within {CultureGate.Timeout.TotalMinutes} minutes; it is held by {heldBy}, which held it for the whole wait."
				: $"CultureGate was not acquired within {CultureGate.Timeout.TotalMinutes} minutes; it changed hands during the wait, so no holder is blamed (now held by {heldBy ?? "nobody"}).");
		}

		TakeGate(methodUnderTest);
	}

	// Called only after this test has acquired the gate, on every acquisition path. Taking it
	// starts a new epoch and clears any poison in the same step (CultureGateState.Take), and the
	// console-capture per-test bound starts counting from here (ConsoleCaptureTestBound.Clock).
	private static void TakeGate(MethodInfo methodUnderTest)
	{
		string holder = methodUnderTest.DeclaringType?.FullName + "." + methodUnderTest.Name;
		long epoch = CultureGate.State.Take(holder);
		CultureGate.HeldByCurrentTest = true;
		ConsoleCaptureTestBound.Clock?.MarkHolding(holder, epoch);
	}

	public override void After(MethodInfo methodUnderTest)
	{
		CultureGate.HeldByCurrentTest = false;
		ConsoleCaptureTestBound.Clock?.MarkNotHolding();
		CultureGate.State.Release();
		_ = CultureGate.Gate.Release();
	}
}

// GUARDS: the mechanism is only real if xUnit actually applies the collection-level attribute
// to EACH collection. They assert the per-test flag, not the gate's count, so a collection
// that lost its [CultureIsolated] fails here even while the other collection holds the gate.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class CultureGateConsoleCaptureGuardTests
{
	[Fact]
	public void TestsInTheConsoleCaptureCollection_RunHoldingTheCultureGate()
	{
		_ = CultureGate.HeldByCurrentTest.Should().BeTrue("[CultureIsolated] on ConsoleCaptureCollection must take the gate for every test in it");
		_ = CultureGate.Gate.CurrentCount.Should().Be(0);
	}

	// L-1 pin: the gate can only be poisoned in the name of the acquisition that holds it. This
	// test holds the gate, so a poison attempt for any other name, or for an earlier acquisition,
	// must be refused and leave no poison behind. (The positive case on the REAL gate is not
	// exercised here: it would fail the next waiter at once. CultureGateStateTests covers it on a
	// private instance.)
	[Fact]
	public void PoisonAttempt_ForATestThatDoesNotHoldTheGate_IsRefused()
	{
		long current = CultureGate.State.Epoch;
		_ = CultureGate.Holder.Should().Be(typeof(CultureGateConsoleCaptureGuardTests).FullName + "." + nameof(PoisonAttempt_ForATestThatDoesNotHoldTheGate_IsRefused));
		_ = CultureGate.State.TryPoisonWhileHeldBy(current, "Some.Other.Test.ThatHung", out _).Should().BeFalse("the gate is held by this test, not by the named one");
		_ = CultureGate.State.TryPoisonWhileHeldBy(current - 1, holder: null, out _).Should().BeFalse("an earlier acquisition no longer holds the gate");
		_ = CultureGate.PoisonedBy.Should().BeNull("taking the gate cleared any poison, and a refused attempt must not set one");
	}
}

// L-1 / N-2: the epoch rules, driven step by step on a private CultureGateState, so nothing here
// depends on timing and the real gate is never touched.
public sealed class CultureGateStateTests
{
	[Fact]
	public void WaiterTimeout_AfterTheGateChangedHands_RefusesThePoison_AndDoesNotBlameTheNewHolder()
	{
		CultureGateState state = new();

		long epochA = state.Take("Tests.A");
		_ = epochA.Should().Be(1);

		// The waiter records the epoch before it starts waiting.
		long seenByWaiter = state.Epoch;
		_ = seenByWaiter.Should().Be(epochA);

		// A releases near the end of the wait, and B (not the waiter) takes the gate.
		state.Release();
		long epochB = state.Take("Tests.B");
		_ = epochB.Should().Be(2);

		// The waiter "times out": the gate changed hands during its wait, so it must not poison.
		_ = state.TryPoisonWhileHeldBy(seenByWaiter, holder: null, out string? heldBy).Should().BeFalse("acquisition 1 no longer holds the gate");
		_ = heldBy.Should().Be("Tests.B");
		_ = state.PoisonedBy.Should().BeNull("B took the gate after the wait began and must not be reported");
		_ = state.IsPoisonedAndStillHeld(out string? poisonedBy).Should().BeFalse();
		_ = poisonedBy.Should().BeNull();
	}

	[Fact]
	public void WaiterTimeout_WhileOneAcquisitionHeldTheWholeWait_PoisonsInItsName()
	{
		CultureGateState state = new();
		_ = state.Take("Tests.A");
		long seenByWaiter = state.Epoch;

		_ = state.TryPoisonWhileHeldBy(seenByWaiter, holder: null, out string? heldBy).Should().BeTrue();
		_ = heldBy.Should().Be("Tests.A");
		_ = state.IsPoisonedAndStillHeld(out string? poisonedBy).Should().BeTrue();
		_ = poisonedBy.Should().Be("Tests.A");
	}

	// Two rows of one Theory share the name Type.Method; the epoch still tells them apart.
	[Fact]
	public void SameNamedLaterAcquisition_IsNotPoisonedByATimeoutAgainstTheEarlierOne()
	{
		CultureGateState state = new();
		long row1 = state.Take("Tests.Theory");
		state.Release();
		_ = state.Take("Tests.Theory");

		_ = state.TryPoisonWhileHeldBy(row1, "Tests.Theory", out _).Should().BeFalse("row 2 is a different acquisition with the same name");
		_ = state.PoisonedBy.Should().BeNull();
	}

	// The waiter's own path (BeginWait / TryPoisonAfterWait), for the review scenario: the wait
	// started while A held the gate, so the next acquisition (B) must not be accepted.
	[Fact]
	public void WaitStartedUnderAHolder_TimeoutAfterAHandOver_IsRefused()
	{
		CultureGateState state = new();
		_ = state.Take("Tests.A");
		CultureGateState.WaitStart start = state.BeginWait();
		_ = start.Should().Be(new CultureGateState.WaitStart(1, HolderWasNull: false));

		state.Release();
		_ = state.Take("Tests.B");

		_ = state.TryPoisonAfterWait(start, out string? heldBy).Should().BeFalse("a holder was recorded at the start, so only that acquisition may be named");
		_ = heldBy.Should().Be("Tests.B");
		_ = state.PoisonedBy.Should().BeNull();
	}

	// Accepted-limit fix: the wait started with no holder recorded (epoch e), and the very next
	// acquisition (e + 1) won the race and then held the gate for the rest of the wait.
	[Fact]
	public void WaitStartedWithNoHolder_TimeoutUnderTheNextAcquisition_PoisonsInItsName()
	{
		CultureGateState state = new();
		_ = state.Take("Tests.Earlier");
		state.Release();
		CultureGateState.WaitStart start = state.BeginWait();
		_ = start.Should().Be(new CultureGateState.WaitStart(1, HolderWasNull: true));

		_ = state.Take("Tests.Hung").Should().Be(2);

		_ = state.TryPoisonAfterWait(start, out string? heldBy).Should().BeTrue("epoch e + 1 held the gate for the whole wait");
		_ = heldBy.Should().Be("Tests.Hung");
		_ = state.IsPoisonedAndStillHeld(out string? poisonedBy).Should().BeTrue();
		_ = poisonedBy.Should().Be("Tests.Hung");
	}

	// The e + 1 allowance goes no further: two hand-overs during the wait are refused.
	[Fact]
	public void WaitStartedWithNoHolder_TimeoutAfterTwoAcquisitions_IsRefused()
	{
		CultureGateState state = new();
		CultureGateState.WaitStart start = state.BeginWait();
		_ = start.Should().Be(new CultureGateState.WaitStart(0, HolderWasNull: true));

		_ = state.Take("Tests.First");
		state.Release();
		_ = state.Take("Tests.Second").Should().Be(2);

		_ = state.TryPoisonAfterWait(start, out string? heldBy).Should().BeFalse("the gate changed hands during the wait");
		_ = heldBy.Should().Be("Tests.Second");
		_ = state.PoisonedBy.Should().BeNull("Second took the gate late in the wait and must not be reported");
	}

	// A free gate at timeout (the next acquirer has not taken it yet) names nobody.
	[Fact]
	public void WaitStartedWithNoHolder_TimeoutWhileStillFree_IsRefused()
	{
		CultureGateState state = new();
		CultureGateState.WaitStart start = state.BeginWait();

		_ = state.TryPoisonAfterWait(start, out string? heldBy).Should().BeFalse();
		_ = heldBy.Should().BeNull();
		_ = state.PoisonedBy.Should().BeNull();
	}

	// The e + 1 allowance names only a HOLDER: e + 1 took the gate and released it again during
	// the wait, so at timeout the gate is free and nobody may be poisoned.
	[Fact]
	public void WaitStartedWithNoHolder_NextAcquisitionTookAndReleased_FreeAtTimeout_IsRefused()
	{
		CultureGateState state = new();
		CultureGateState.WaitStart start = state.BeginWait();
		_ = start.Should().Be(new CultureGateState.WaitStart(0, HolderWasNull: true));

		_ = state.Take("Tests.Passing").Should().Be(start.Epoch + 1);
		state.Release();

		_ = state.TryPoisonAfterWait(start, out string? heldBy).Should().BeFalse("the gate is free at timeout, so there is no holder to name");
		_ = heldBy.Should().BeNull();
		_ = state.PoisonedBy.Should().BeNull();
		_ = state.IsPoisonedAndStillHeld(out string? poisonedBy).Should().BeFalse();
		_ = poisonedBy.Should().BeNull();
	}

	// N-2: the invariant "PoisonedBy is non-null only while Holder equals it" survives a release.
	[Fact]
	public void PoisonedHolder_Releasing_ClearsThePoisonWithTheHolder()
	{
		CultureGateState state = new();
		long epoch = state.Take("Tests.A");
		_ = state.TryPoisonWhileHeldBy(epoch, "Tests.A", out _).Should().BeTrue();

		state.Release();

		_ = state.Holder.Should().BeNull();
		_ = state.PoisonedBy.Should().BeNull("the poison names an acquisition that has ended");
		_ = state.IsPoisonedAndStillHeld(out _).Should().BeFalse();
	}
}

[Collection(AvaloniaUiCollection.Name)]
public sealed class CultureGateAvaloniaUiGuardTests
{
	[Fact]
	public void TestsInTheAvaloniaUiCollection_RunHoldingTheCultureGate()
	{
		_ = CultureGate.HeldByCurrentTest.Should().BeTrue("[CultureIsolated] on AvaloniaUiCollection must take the gate for every test in it");
		_ = CultureGate.Gate.CurrentCount.Should().Be(0);
	}
}
