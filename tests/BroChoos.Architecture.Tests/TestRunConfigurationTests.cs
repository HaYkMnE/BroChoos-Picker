using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BroChoos.Architecture.Tests;

// GUARD FOR THE TEST RUN'S FAILURE SIGNAL.
//
// BroChoos.App.Tests reports some failures as xUnit cleanup failures, not test results: an
// exception that escaped a work item on the avalonia-ui test thread, a flush of that thread that
// timed out, a collection that timed out (AvaloniaUiThread.cs). The VSTest adapter sends those to
// VSTest as ERROR messages, and they fail the run and the dotnet test exit code only while the
// VSTest setting RunConfiguration.TreatTestAdapterErrorsAsWarnings keeps its default, false
// (https://learn.microsoft.com/visualstudio/test/configure-unit-tests-by-using-a-dot-runsettings-file).
// Turned on, the same failures become warnings and CI goes green with them. This fails if any
// runsettings, MSBuild file or CI workflow in the repository turns it on.
public sealed partial class TestRunConfigurationTests
{
	private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", ".vs", "node_modules", "artifacts"];

	[Fact]
	public void NoRepositoryFile_TreatsTestAdapterErrorsAsWarnings()
	{
		string root = GetRepositoryRoot();
		List<string> scanned = [.. EnumerateRunConfigurationFiles(root)];

		// Not vacuous: the scan must reach the files that can carry the setting.
		_ = scanned.Should().Contain(Path.Combine(root, "Directory.Build.props"));
		_ = scanned.Should().Contain(Path.Combine(root, "tests", "BroChoos.App.Tests", "BroChoos.App.Tests.csproj"));
		_ = scanned.Should().Contain(path => path.StartsWith(Path.Combine(root, ".github", "workflows"), StringComparison.OrdinalIgnoreCase));

		List<string> offenders = [.. scanned
			.Where(path => TurnsAdapterErrorsIntoWarnings(File.ReadAllText(path)))
			.Select(path => Path.GetRelativePath(root, path))];

		_ = offenders.Should().BeEmpty(
			"cleanup failures from the App.Tests test framework fail the run only while TreatTestAdapterErrorsAsWarnings stays false");
	}

	[Theory]
	[InlineData("<RunConfiguration><TreatTestAdapterErrorsAsWarnings>true</TreatTestAdapterErrorsAsWarnings></RunConfiguration>", true)]
	[InlineData("<TreatTestAdapterErrorsAsWarnings> True </TreatTestAdapterErrorsAsWarnings>", true)]
	[InlineData("dotnet test -- RunConfiguration.TreatTestAdapterErrorsAsWarnings=true", true)]
	[InlineData("<TreatAdapterErrorsAsWarnings>true</TreatAdapterErrorsAsWarnings>", true)]
	[InlineData("<TreatTestAdapterErrorsAsWarnings>false</TreatTestAdapterErrorsAsWarnings>", false)]
	[InlineData("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", false)]
	public void TheMatcher_RecognisesTheSettingInEveryFormItCanTake(string text, bool expected)
	{
		_ = TurnsAdapterErrorsIntoWarnings(text).Should().Be(expected);
	}

	private static bool TurnsAdapterErrorsIntoWarnings(string text)
	{
		return AdapterErrorsAsWarningsTrue().IsMatch(text);
	}

	// The element form (runsettings, MSBuild) and the "Name=value" form (dotnet test -- arguments).
	[GeneratedRegex(@"Treat(?:Test)?AdapterErrorsAsWarnings\s*(?:>|=)\s*[""']?\s*true", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex AdapterErrorsAsWarningsTrue();

	private static IEnumerable<string> EnumerateRunConfigurationFiles(string root)
	{
		EnumerationOptions options = new() { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
		Stack<string> pending = new([root]);
		while (pending.Count > 0)
		{
			string directory = pending.Pop();
			foreach (string child in Directory.EnumerateDirectories(directory, "*", options))
			{
				if (!SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
				{
					pending.Push(child);
				}
			}

			foreach (string file in Directory.EnumerateFiles(directory, "*", options))
			{
				string name = Path.GetFileName(file);
				string extension = Path.GetExtension(file);
				bool isWorkflow = (extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
					&& file.StartsWith(Path.Combine(root, ".github") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
				if (isWorkflow
					|| extension.Equals(".runsettings", StringComparison.OrdinalIgnoreCase)
					|| extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
					|| extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
					|| extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
					|| name.Equals("global.json", StringComparison.OrdinalIgnoreCase))
				{
					yield return file;
				}
			}
		}
	}

	private static string GetRepositoryRoot()
	{
		string? overrideRoot = Environment.GetEnvironmentVariable("BROCHOOS_ARCH_SOURCE_ROOT");
		if (!string.IsNullOrWhiteSpace(overrideRoot))
		{
			string fullOverridePath = Path.GetFullPath(overrideRoot);
			return !File.Exists(Path.Combine(fullOverridePath, "BroChoos.sln"))
				? throw new InvalidOperationException($"BROCHOOS_ARCH_SOURCE_ROOT must point to a folder containing BroChoos.sln: {fullOverridePath}")
				: fullOverridePath;
		}

		DirectoryInfo? current = new(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "BroChoos.sln")))
			{
				return current.FullName;
			}

			current = current.Parent;
		}

		throw new InvalidOperationException("Could not locate BroChoos.sln from test execution directory.");
	}
}
