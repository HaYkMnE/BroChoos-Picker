using System.Reflection;
using FluentAssertions;
using Xunit;
using AppProgram = BroChoos.App.Program;

namespace BroChoos.App.Tests;

// THE DEBUG-ONLY SCAFFOLD GATES, ASSERTED AGAINST THE BUILT ASSEMBLY INSTEAD OF A NUMBER
// IN A DOCUMENT.
//
// WHAT THIS REPLACES AND WHY. From 0A-2 to 0A-4 the gate was a documented test count:
// "Debug runs one test more than Release, so equal counts mean the #if broke." That canary
// worked, but it lived in prose in five files, it went stale the moment a second scaffold
// landed (the delta is no longer one), and a stale canary is worse than none — a reader who
// trusts it diagnoses a break that does not exist. The property the count was standing in
// for is asserted directly here, where the build and the runner enforce it and no document
// has to be kept in step.
//
// WHAT WOULD GO WRONG WITHOUT IT. Scaffold0A5 contains a mode that performs a REAL config
// write driven by its own command-line arguments, and Scaffold0A3 an Avalonia probe runner.
// Both are test harness machinery. If a configuration gate in BroChoos.App.csproj were
// deleted, renamed or made opt-in, that machinery would compile into a shipping build and
// nothing else in the suite would notice.
//
// THIS FILE MUST NOT ITSELF BE EXCLUDED, which is why it sits at the test project root
// rather than under Scaffold0A5\: BroChoos.App.Tests.csproj removes Scaffold0A5\**\*.cs in
// every non-Debug configuration, so a gate test placed there would vanish in exactly the
// configuration whose absence claim matters most.
//
// REFLECTION IS THE RIGHT TOOL HERE AND IS CONFINED TO THE TEST ASSEMBLY. "Is this type in
// this assembly" is not answerable any other way — naming the type in code would fail to
// COMPILE in Release, which is the state under test. This follows the precedent set by
// EveryCollectionMemberOnACoreDocument_IsAccountedFor: reflection asked where a wrong
// answer is a red test. It is emphatically not the reflection that was reversed out of the
// storage read path, which walked DTO members at runtime and could destroy a user's file.
public sealed class ScaffoldConfigurationGateTests
{
	// Every type the two configuration gates are meant to remove. Named as strings because
	// in Release these types DO NOT EXIST — a typeof would not compile there.
	//
	// A WRONG NAME HERE WOULD MAKE THE RELEASE HALF PASS FOR NOTHING, so it cannot be
	// trusted on inspection alone: the Debug half asserts each of these names RESOLVES,
	// which is what proves the strings match reality. The two halves check each other.
	private static readonly string[] GatedScaffoldTypes =
	[
		"BroChoos.App.Scaffold0A5.EventCheckpointObserver",
		"BroChoos.App.Scaffold0A5.AtomicWriteKillChild",
		"BroChoos.App.Scaffold0A3.AvaloniaProbeApp",
		"BroChoos.App.Scaffold0A3.AvaloniaProbeRunner",
	];

	// The assembly under test, reached through a type that is present in EVERY
	// configuration, so obtaining the handle can never be what fails.
	// ALIASED TO THE FULLY QUALIFIED NAME deliberately: Microsoft.NET.Test.Sdk can inject a
	// generated entry point into a test assembly, so an unqualified Program is a name this
	// file should not have to reason about. The alias keeps that binding without IDE0001.
	private static Assembly AppAssembly => typeof(AppProgram).Assembly;

	// The configuration the assembly under test was actually COMPILED in, read from that
	// same assembly. Deliberately not #if DEBUG in this file: a preprocessor directive here
	// would describe how the TEST was compiled and merely assume the app matches, whereas
	// AssemblyConfigurationAttribute is emitted into the app by its own build and cannot
	// disagree with itself. It also keeps the preprocessor out of the test sources, which is
	// the reason BroChoos.App.Tests.csproj gates its scaffold tests with a Compile Remove.
	private static string AppConfiguration =>
		AppAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? string.Empty;

	// ONE FACT, INVERTED BY CONFIGURATION, so neither outcome can pass vacuously: in Debug
	// every gated type must be PRESENT, in every other configuration every one must be
	// ABSENT. Split into two [Fact]s it would be a suite where one of the pair is always
	// silently not run, and a skipped assertion proves nothing.
	[Fact]
	public void GatedScaffoldTypes_ArePresentInDebugAndAbsentInEveryOtherConfiguration()
	{
		// PREMISES FIRST. Each of these three would otherwise let the real assertion below
		// pass for a reason that has nothing to do with the gates.
		_ = GatedScaffoldTypes.Should().NotBeEmpty(
			"an empty inventory would make the absence half trivially true");

		_ = AppConfiguration.Should().NotBeEmpty(
			"the configuration must be readable from the assembly, or this test cannot know which half to assert");

		_ = AppAssembly.GetType("BroChoos.App.Program", throwOnError: false).Should().NotBeNull(
			"a type present in every configuration must resolve, or the lookup mechanism itself is broken and every absence claim below is meaningless");

		string[] present = [.. GatedScaffoldTypes.Where(name => AppAssembly.GetType(name, throwOnError: false) is not null)];

		if (string.Equals(AppConfiguration, "Debug", StringComparison.Ordinal))
		{
			// The scaffold exists in Debug, so all of it must be here. This half is also
			// what validates the string inventory above.
			_ = present.Should().BeEquivalentTo(
				GatedScaffoldTypes,
				"the scaffold is compiled into Debug builds, so every gated type must resolve — a name missing here is either a deleted type or a typo in the inventory, and a typo would make the Release half of this test worthless");
		}
		else
		{
			// The direction that protects a shipping build: nothing from either scaffold
			// may survive into a non-Debug assembly.
			_ = present.Should().BeEmpty(
				FormattableString.Invariant(
					$"BroChoos.App was compiled as '{AppConfiguration}', where BroChoos.App.csproj removes Scaffold0A5\\**\\*.cs and Scaffold0A3\\**\\*.cs from compilation — a type surviving here means a configuration gate stopped working and test-harness code, including a mode that performs a real config write from its own arguments, is shipping"));
		}
	}
}
