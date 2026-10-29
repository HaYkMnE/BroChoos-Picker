using Xunit;

namespace BroChoos.App.Tests;

// Console.SetOut/SetError mutate process-wide state, and xUnit runs test classes in
// parallel by default. Every class that captures console output must join this
// collection so those redirections cannot race with each other (same pattern as
// BroChoos.Core.Tests; collection definitions are per-assembly, hence the copy).
// It additionally serializes the ForwardedUrlRouterTests: the guard's Global mutex
// name is per-user, so two primary acquisitions at the same time would collide.
// It also serializes every class that reads or switches the static
// LocalizationService.CurrentCulture: a parallel culture switch made a label built in one
// culture be compared against GetString evaluated in another (observed flake). The
// avalonia-ui collection runs in parallel with this one and also uses the culture, so both
// definitions carry [CultureIsolated], which takes the global CultureGate around every test.
[CollectionDefinition(Name)]
[CultureIsolated]
public sealed class ConsoleCaptureCollection
{
	public const string Name = "console-capture";
}
