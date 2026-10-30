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

	// The test currently holding the gate, for timeout messages only. The backing store is the
	// compiler's private field (C# 14 `field`), read and written only through Volatile.
	internal static string? Holder
	{
		get => Volatile.Read(ref field);
		set => Volatile.Write(ref field, value);
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
		if (!CultureGate.Gate.Wait(CultureGate.Timeout))
		{
			throw new TimeoutException(
				$"CultureGate was not released within {CultureGate.Timeout.TotalMinutes} minutes; it is held by {CultureGate.Holder ?? "an unknown test"}.");
		}

		CultureGate.Holder = methodUnderTest.DeclaringType?.FullName + "." + methodUnderTest.Name;
		CultureGate.HeldByCurrentTest = true;
	}

	public override void After(MethodInfo methodUnderTest)
	{
		CultureGate.HeldByCurrentTest = false;
		CultureGate.Holder = null;
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
