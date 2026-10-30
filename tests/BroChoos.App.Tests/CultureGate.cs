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
// clears holder and poison together. A waiter that recorded its epoch while the gate was free and
// then lost the race to a holder that hangs does not poison (the epoch moved during its wait); the
// next waiter, or the console-capture per-test bound, does.
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
			heldBy = Holder;
			if (_epoch != epoch || heldBy is null || (holder is not null && !string.Equals(heldBy, holder, StringComparison.Ordinal)))
			{
				return false;
			}

			PoisonedBy ??= heldBy;
			return string.Equals(PoisonedBy, heldBy, StringComparison.Ordinal);
		}
	}

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

		// Recorded BEFORE the wait: on timeout the gate is poisoned only if this same acquisition
		// held it for the whole wait (see CultureGateState).
		long epochBeforeWait = CultureGate.State.Epoch;
		if (!CultureGate.Gate.Wait(CultureGate.Timeout))
		{
			bool poisoned = CultureGate.State.TryPoisonWhileHeldBy(epochBeforeWait, holder: null, out string? heldBy);
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
