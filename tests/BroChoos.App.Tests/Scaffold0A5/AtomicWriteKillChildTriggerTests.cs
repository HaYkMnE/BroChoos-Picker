using BroChoos.App.Scaffold0A5;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests.Scaffold0A5;

// THE TRIGGER GATE, pinned. This is the RUNTIME half of a HIGH-severity fix; the compile-time
// half (the scaffold's absence from a Release assembly) cannot be asserted from inside a Debug
// test run and is verified by searching the built Release assembly for the trigger string.
//
// WHAT WAS WRONG. The trigger test was Array.Exists(args, ...) - the sentinel ANYWHERE in the
// argument vector - and the hook that called it in Program was unconditional. This application
// is a Windows URL handler: the OS passes URLs as command-line arguments through a registration
// whose open command is "<exe>" "%1". A URL carrying a literal unencoded quote ends the shell's
// quoted region at that quote, so every following token becomes its own ARGUMENT - and the same
// breakout can deliver --dest, --run-id and --revision, making the child's real config write
// fully caller-controlled. Measured with the OS's own CommandLineToArgvW on .NET 10, the URL
//
//   https://example.com/" --internal-0a5-atomic-write-child "
//
// yielded argc=3 and REACHED the mode, while the correctly percent-encoded %22 form did not.
//
// WHAT THESE TESTS PIN. Two independent conditions, either of which alone defeats that shape:
// the trigger must be args[0] (a URL occupies args[0], so an injected token never is), and a
// corroborating environment variable must be set (a URL cannot set one). Neither is a claim
// that a LOCAL process able to set the environment and choose args[0] is refused - such a
// process can already write the user's config directly. The property is narrower and is the one
// that matters: CONTENT cannot cross into argument position and reach a write.
public sealed class AtomicWriteKillChildTriggerTests
{
	[Fact]
	public void IsTriggered_TriggerFirstAndEnvironmentSet_IsRecognised()
	{
		// The shape the harness itself uses: tools\atomic-kill-test.ps1 places the trigger
		// first in every one of its three argument vectors and sets the variable for the
		// children it starts. If this test fails, the D-v3.5-493 harness is broken.
		WithTriggerEnvironment(AtomicWriteKillChild.TriggerEnvironmentValue, () =>
			_ = AtomicWriteKillChild
				.IsTriggered([AtomicWriteKillChild.TriggerArgument, "--dest", @"C:\x\app.json"])
				.Should().BeTrue("this is exactly how the kill-test harness invokes the child"));
	}

	// THE INJECTION SHAPE, refused. The trigger is present but NOT first, because a URL
	// occupies args[0] and a quote-breakout appends its tokens after it. Array.Exists accepted
	// precisely this.
	[Fact]
	public void IsTriggered_TriggerPresentButNotFirst_IsRefused()
	{
		WithTriggerEnvironment(AtomicWriteKillChild.TriggerEnvironmentValue, () =>
			_ = AtomicWriteKillChild
				.IsTriggered(["https://example.com/", AtomicWriteKillChild.TriggerArgument, "--dest", @"C:\x\app.json"])
				.Should().BeFalse("a URL occupies args[0], so a token injected by a quote breakout can never be first - which is what makes the position test a gate rather than a formality"));
	}

	// THE GATE A URL CANNOT PASS, on its own. Even with the trigger correctly first - i.e. even
	// for a caller that fully controls the argument vector - the mode stays shut without the
	// environment variable, because a URL cannot set one.
	[Fact]
	public void IsTriggered_EnvironmentVariableAbsent_IsRefused()
	{
		WithTriggerEnvironment(null, () =>
			_ = AtomicWriteKillChild
				.IsTriggered([AtomicWriteKillChild.TriggerArgument])
				.Should().BeFalse("the environment variable is the condition no URL can satisfy, so it must be required and not merely consulted"));
	}

	// An exact ordinal value match, so a variable that merely EXISTS does not open the gate.
	// Pinned because "is the variable set?" is the easy, weaker thing to write.
	[Fact]
	public void IsTriggered_EnvironmentVariableSetToSomethingElse_IsRefused()
	{
		WithTriggerEnvironment("0", () =>
			_ = AtomicWriteKillChild
				.IsTriggered([AtomicWriteKillChild.TriggerArgument])
				.Should().BeFalse("presence is not the test; the value must match exactly"));
	}

	[Fact]
	public void IsTriggered_NoArguments_IsRefusedWithoutThrowing()
	{
		// The normal no-argument launch. Reads args[0] only after a length check, so this must
		// be an ordinary false rather than an IndexOutOfRangeException on the app's own
		// startup path.
		WithTriggerEnvironment(AtomicWriteKillChild.TriggerEnvironmentValue, () =>
			_ = AtomicWriteKillChild.IsTriggered([]).Should().BeFalse());
	}

	// Sets the process-wide variable, runs the assertion, and RESTORES the previous value in a
	// finally. Restored rather than cleared: a developer may have it set in their shell, and a
	// test must not silently change the environment the rest of the run sees. Only this class
	// touches this variable, so no other test can race with it.
	private static void WithTriggerEnvironment(string? value, Action assertion)
	{
		string? previous = Environment.GetEnvironmentVariable(AtomicWriteKillChild.TriggerEnvironmentVariable);
		Environment.SetEnvironmentVariable(AtomicWriteKillChild.TriggerEnvironmentVariable, value);

		try
		{
			assertion();
		}
		finally
		{
			Environment.SetEnvironmentVariable(AtomicWriteKillChild.TriggerEnvironmentVariable, previous);
		}
	}
}
