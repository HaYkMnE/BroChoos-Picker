using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// F2: THE e + 1 RULE IN CultureGateState RESTS ON HOW THE GATE IS TAKEN AND RELEASED. Every
// successful CultureGate.Gate.Wait must be followed at once by TakeGate (State.Take), and the only
// Gate.Release must sit in [CultureIsolated] After, right after State.Release. This scans the
// sources of this test project (comment lines skipped) and fails if a new acquisition or release
// path appears anywhere else or in another shape.
public sealed partial class CultureGateInvariantTests
{
	private const string GateFile = "CultureGate.cs";

	[Fact]
	public void GateWait_IsOnlyCalledInCultureGate_AndEverySuccessGoesStraightToTakeGate()
	{
		List<string> violations = [];
		int waits = 0;
		foreach ((string file, string[] lines) in Sources())
		{
			for (int i = 0; i < lines.Length; i++)
			{
				if (IsComment(lines[i]) || !WaitCall().IsMatch(lines[i]))
				{
					continue;
				}

				waits++;
				string where = $"{file}:{i + 1}";
				if (!string.Equals(Path.GetFileName(file), GateFile, StringComparison.Ordinal))
				{
					violations.Add($"{where}: the gate is waited on outside {GateFile}");
					continue;
				}

				string? next = StatementOnSuccess(lines, i);
				if (next is null || !next.StartsWith("TakeGate(", StringComparison.Ordinal))
				{
					violations.Add($"{where}: a successful wait is not followed at once by TakeGate (next statement: {next ?? "not recognised"})");
				}
			}
		}

		_ = waits.Should().BeGreaterThan(0, "the scan must see the real acquisition paths, or it proves nothing");
		_ = violations.Should().BeEmpty("every acquisition must record itself in CultureGateState right after Gate.Wait (the e + 1 rule depends on it)");
	}

	[Fact]
	public void GateRelease_IsOnlyCalledInAfter_RightAfterStateRelease()
	{
		List<string> violations = [];
		int releases = 0;
		foreach ((string file, string[] lines) in Sources())
		{
			for (int i = 0; i < lines.Length; i++)
			{
				if (IsComment(lines[i]) || !ReleaseCall().IsMatch(lines[i]))
				{
					continue;
				}

				releases++;
				string where = $"{file}:{i + 1}";
				if (!string.Equals(Path.GetFileName(file), GateFile, StringComparison.Ordinal))
				{
					violations.Add($"{where}: the gate is released outside {GateFile}");
					continue;
				}

				string? method = EnclosingMethod(lines, i);
				if (method is null || !method.Contains("void After(", StringComparison.Ordinal))
				{
					violations.Add($"{where}: the gate is released outside [CultureIsolated] After (in: {method ?? "unknown"})");
				}

				string? previous = PreviousStatement(lines, i);
				if (!string.Equals(previous, "CultureGate.State.Release();", StringComparison.Ordinal))
				{
					violations.Add($"{where}: Gate.Release is not preceded at once by CultureGate.State.Release() (previous statement: {previous ?? "none"})");
				}
			}
		}

		_ = releases.Should().Be(1, "After is the single release path");
		_ = violations.Should().BeEmpty("the holder must be cleared in CultureGateState right before the semaphore is released (the e + 1 rule depends on it)");
	}

	// `if (...Gate.Wait(...))` succeeds into its block; `if (!...Gate.Wait(...))` fails into its
	// block and succeeds after it. Any other shape is not recognised and returns null.
	private static string? StatementOnSuccess(string[] lines, int waitLine)
	{
		string call = lines[waitLine].Trim();
		if (!call.StartsWith("if (", StringComparison.Ordinal) || waitLine + 1 >= lines.Length || lines[waitLine + 1].Trim() != "{")
		{
			return null;
		}

		bool negated = call.StartsWith("if (!", StringComparison.Ordinal);
		int open = waitLine + 1;
		int close = MatchingBrace(lines, open);
		return close < 0
			? null
			: negated
				? NextStatement(lines, close + 1, lines.Length)
				: NextStatement(lines, open + 1, close);
	}

	private static int MatchingBrace(string[] lines, int open)
	{
		int depth = 0;
		for (int i = open; i < lines.Length; i++)
		{
			string trimmed = lines[i].Trim();
			if (trimmed == "{")
			{
				depth++;
			}
			else if (trimmed == "}")
			{
				depth--;
				if (depth == 0)
				{
					return i;
				}
			}
		}

		return -1;
	}

	private static string? NextStatement(string[] lines, int from, int to)
	{
		for (int i = from; i < to; i++)
		{
			string trimmed = lines[i].Trim();
			if (trimmed.Length > 0 && trimmed != "{" && trimmed != "}" && !IsComment(trimmed))
			{
				return trimmed;
			}
		}

		return null;
	}

	private static string? PreviousStatement(string[] lines, int line)
	{
		for (int i = line - 1; i >= 0; i--)
		{
			string trimmed = lines[i].Trim();
			if (trimmed.Length > 0 && !IsComment(trimmed))
			{
				return trimmed;
			}
		}

		return null;
	}

	private static string? EnclosingMethod(string[] lines, int line)
	{
		for (int i = line - 1; i >= 0; i--)
		{
			if (MethodSignature().IsMatch(lines[i]))
			{
				return lines[i].Trim();
			}
		}

		return null;
	}

	private static bool IsComment(string line)
	{
		return line.TrimStart().StartsWith("//", StringComparison.Ordinal);
	}

	private static IEnumerable<(string File, string[] Lines)> Sources()
	{
		string project = Path.Combine(Root(), "tests", "BroChoos.App.Tests");
		char sep = Path.DirectorySeparatorChar;
		return Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase) && !path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase))
			.Order(StringComparer.Ordinal)
			.Select(path => (Path.GetRelativePath(project, path), File.ReadAllLines(path)));
	}

	private static string Root()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BroChoos.sln")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
	}

	[GeneratedRegex(@"\bGate\.Wait\(")]
	private static partial Regex WaitCall();

	[GeneratedRegex(@"\bGate\.Release\(")]
	private static partial Regex ReleaseCall();

	[GeneratedRegex(@"^\s*(public|private|internal|protected)\b[^=;]*\([^;]*\)\s*$")]
	private static partial Regex MethodSignature();
}
