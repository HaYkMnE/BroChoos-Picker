using Xunit;

namespace BroChoos.Core.Tests;

// Console.SetOut/SetError mutate process-wide state, and xUnit runs test classes in
// parallel by default. Every class that captures console output must join this
// collection so those redirections cannot race with each other.
[CollectionDefinition(Name)]
public sealed class ConsoleCaptureCollection
{
	public const string Name = "console-capture";
}
