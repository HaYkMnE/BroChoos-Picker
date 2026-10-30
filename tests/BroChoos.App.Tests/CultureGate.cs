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
	// ONE LOCK FOR Holder AND PoisonedBy (StateLock). Every write to either happens under it, and
	// the invariant it keeps is: PoisonedBy is non-null ONLY while Holder equals it. Poisoning
	// checks the holder in the same critical section (TryPoisonWhileHeldBy), and taking the gate
	// sets the new holder and CLEARS the poison in one step (TakeGate), whichever path acquired
	// it. So the poisoned message can never name X while someone else legitimately holds the gate.
	internal static readonly Lock StateLock = new();

	internal static string? PoisonedBy
	{
		get => Volatile.Read(ref field);
		private set => Volatile.Write(ref field, value);
	}

	// The test currently holding the gate. The backing store is the compiler's private field
	// (C# 14 `field`), read through Volatile and written only under StateLock.
	internal static string? Holder
	{
		get => Volatile.Read(ref field);
		private set => Volatile.Write(ref field, value);
	}

	// Poisons the gate only if it is still held by `hungHolder` right now, atomically with that
	// check. Returns whether it is poisoned by `hungHolder` afterwards.
	internal static bool TryPoisonWhileHeldBy(string hungHolder)
	{
		lock (StateLock)
		{
			if (!string.Equals(Holder, hungHolder, StringComparison.Ordinal))
			{
				return false;
			}

			PoisonedBy ??= hungHolder;
			return string.Equals(PoisonedBy, hungHolder, StringComparison.Ordinal);
		}
	}

	// Called by the test that has just acquired the gate, on every acquisition path.
	internal static void SetHolderAndClearPoison(string holder)
	{
		lock (StateLock)
		{
			Holder = holder;
			PoisonedBy = null;
		}
	}

	internal static void ClearHolder()
	{
		lock (StateLock)
		{
			Holder = null;
		}
	}

	// True only inside a test that took the gate ITSELF. The gate's count alone cannot say
	// that: it also reads as taken while a test of the OTHER collection holds it.
	private static readonly AsyncLocal<bool> HeldByCurrentTestFlag = new();

	internal static bool HeldByCurrentTest
	{
		get => HeldByCurrentTestFlag.Value;
		set => HeldByCurrentTestFlag.Value = value;
	}
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class CultureIsolatedAttribute : BeforeAfterTestAttribute
{
	public override void Before(MethodInfo methodUnderTest)
	{
		if (CultureGate.PoisonedBy is not null)
		{
			// RECOVERY CHECK FIRST. A holder that was only very slow may have released since the
			// gate was poisoned. Wait(0) takes the gate if it is free right now, and TakeGate then
			// clears the poison: the reason for failing fast (a holder that never releases) no
			// longer exists. A test that hit the original timeout has already failed and named
			// the slow holder, so the incident stays in the results.
			if (CultureGate.Gate.Wait(0))
			{
				TakeGate(methodUnderTest);
				return;
			}

			// Fail fast only if the poison is still true at this instant: the gate is still held
			// by the test it names. If the holder changed (the poison was cleared by its taker),
			// this test simply waits for the gate like any other.
			lock (CultureGate.StateLock)
			{
				if (CultureGate.PoisonedBy is { } poisonedBy && string.Equals(CultureGate.Holder, poisonedBy, StringComparison.Ordinal))
				{
					throw new TimeoutException(
						$"CultureGate is poisoned: it is still held by {poisonedBy}, which already ran past a bound (a waiter's {CultureGate.Timeout.TotalMinutes} minutes, or the console-capture per-test bound). Failing at once instead of waiting again.");
				}
			}
		}

		if (!CultureGate.Gate.Wait(CultureGate.Timeout))
		{
			string? holder = CultureGate.Holder;
			_ = holder is not null && CultureGate.TryPoisonWhileHeldBy(holder);
			throw new TimeoutException(
				$"CultureGate was not released within {CultureGate.Timeout.TotalMinutes} minutes; it is held by {holder ?? "an unknown test"}.");
		}

		TakeGate(methodUnderTest);
	}

	// Called only after this test has acquired the gate, on every acquisition path. Setting the
	// holder clears any poison in the same step (see CultureGate.StateLock), and the console-capture
	// per-test bound starts counting from here (ConsoleCaptureTestBound.Clock).
	private static void TakeGate(MethodInfo methodUnderTest)
	{
		string holder = methodUnderTest.DeclaringType?.FullName + "." + methodUnderTest.Name;
		CultureGate.SetHolderAndClearPoison(holder);
		CultureGate.HeldByCurrentTest = true;
		ConsoleCaptureTestBound.Clock?.MarkHolding(holder);
	}

	public override void After(MethodInfo methodUnderTest)
	{
		CultureGate.HeldByCurrentTest = false;
		ConsoleCaptureTestBound.Clock?.MarkNotHolding();
		CultureGate.ClearHolder();
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

	// L-1 pin: the gate can only be poisoned in the name of the test that holds it. This test holds
	// the gate, so a poison attempt for any other name must be refused and leave no poison behind.
	// (The positive case is not exercised here: poisoning the gate this test holds would fail the
	// next waiter at once, which is the mechanism working, not something to provoke in a suite.)
	[Fact]
	public void PoisonAttempt_ForATestThatDoesNotHoldTheGate_IsRefused()
	{
		_ = CultureGate.Holder.Should().Be(typeof(CultureGateConsoleCaptureGuardTests).FullName + "." + nameof(PoisonAttempt_ForATestThatDoesNotHoldTheGate_IsRefused));
		_ = CultureGate.TryPoisonWhileHeldBy("Some.Other.Test.ThatHung").Should().BeFalse("the gate is held by this test, not by the named one");
		_ = CultureGate.PoisonedBy.Should().BeNull("taking the gate cleared any poison, and a refused attempt must not set one");
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
