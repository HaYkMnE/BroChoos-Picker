using System.Reflection;
using FluentAssertions;
using Xunit;

namespace BroChoos.Security.Tests;

// Code review finding 4: StorageHelper read all of stdin before it looked at the command, so an
// unknown command blocked on (and consumed) a payload it would never use. The command is now
// validated first; these tests replace Console.In with a reader that FAILS if it is touched.
[Collection(nameof(ConsoleInputCollection))]
public sealed class StorageHelperCommandValidationTests
{
	[Theory]
	[InlineData("bogus")]
	[InlineData("")]
	[InlineData("DISCOVER-PERSIST")]
	[InlineData("resolve-short-link")]
	public void UnknownCommand_ReturnsExitCode2_WithoutReadingStdin(string command)
	{
		int exit = RunMain([command]);

		_ = exit.Should().Be(2);
	}

	[Fact]
	public void WrongArgumentCount_ReturnsExitCode2_WithoutReadingStdin()
	{
		_ = RunMain([]).Should().Be(2);
		_ = RunMain(["discover-persist", "extra"]).Should().Be(2);
	}

	[Theory]
	[InlineData("discover-persist")]
	[InlineData("publish-candidates")]
	public void ValidCommand_ReadsStdin(string command)
	{
		// Malformed JSON makes the helper return before it touches any storage (exit 3), which
		// proves the payload was read for a recognised command without writing anything.
		TextReader original = Console.In;
		TextWriter originalOut = Console.Out;
		TrackingReader reader = new("{not json");
		try
		{
			Console.SetIn(reader);
			Console.SetOut(TextWriter.Null);
			int exit = InvokeMain([command]);

			_ = exit.Should().Be(3);
			_ = reader.WasRead.Should().BeTrue("a recognised command must still read its payload");
		}
		finally
		{
			Console.SetIn(original);
			Console.SetOut(originalOut);
		}
	}

	private static int RunMain(string[] args)
	{
		TextReader original = Console.In;
		TrackingReader reader = new(string.Empty);
		try
		{
			Console.SetIn(reader);
			int exit = InvokeMain(args);
			_ = reader.WasRead.Should().BeFalse("stdin must not be read before the command is accepted");
			return exit;
		}
		finally
		{
			Console.SetIn(original);
		}
	}

	private static int InvokeMain(string[] args)
	{
		Type program = Assembly.Load("BroChoos.StorageHelper").GetType("BroChoos.StorageHelper.Program", throwOnError: true)!;
		MethodInfo main = program.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
		return (int)main.Invoke(null, [args])!;
	}

	private sealed class TrackingReader(string content) : StringReader(content)
	{
		public bool WasRead { get; private set; }

		public override string ReadToEnd()
		{
			WasRead = true;
			return base.ReadToEnd();
		}

		public override int Read()
		{
			WasRead = true;
			return base.Read();
		}

		public override string? ReadLine()
		{
			WasRead = true;
			return base.ReadLine();
		}
	}
}

[CollectionDefinition(nameof(ConsoleInputCollection), DisableParallelization = true)]
public sealed class ConsoleInputCollection;
