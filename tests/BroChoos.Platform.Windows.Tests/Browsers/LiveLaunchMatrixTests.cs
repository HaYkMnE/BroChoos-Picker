using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

// W6.3 LIVE four-browser opt-in test (plan `Task W6.3` GREEN: "four-browser
// controlled launch matrix"; Gate 0B criterion 0B-1).
//
// GATED. Normal test runs do NOT open browsers: every test here starts by
// requiring the explicit env var BROCHOOS_LIVE_LAUNCH_MATRIX=1. Without it the test
// SKIPS LOUDLY naming the opt-in — it never silently passes, and it never launches.
// With the opt-in set, each present family is launched through the product's real
// structured-argv path against a real browser; absent families are explicit
// NotAvailable rows, never fake passes.
//
// The live test intentionally does NOT assert per-family Success: this machine may
// lack a family, and a browser that is mid-update refusing to start is a REAL
// outcome, not a test failure. What it DOES pin is the honesty invariant: every
// declared family yields exactly one row; an absent family is NotAvailable with no
// launch attempt and no outcome; only present families are ever attempted.
public sealed class LiveLaunchMatrixTests
{
	private const string OptInVariable = "BROCHOOS_LIVE_LAUNCH_MATRIX";
	private const string ProbeUrl = "https://example.com/brochoos-w6-3-live-matrix";

	private static readonly (string Family, string DisplayName, string Channel, string[] Templates)[] DeclaredFamilies =
	[
		("chrome", "Google Chrome", "stable",
			[@"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
			 @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
			 @"%LocalAppData%\Google\Chrome\Application\chrome.exe"]),
		("edge", "Microsoft Edge", "stable",
			[@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
			 @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"]),
		("firefox", "Mozilla Firefox", "stable",
			[@"%ProgramFiles%\Mozilla Firefox\firefox.exe",
			 @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe"]),
		("brave", "Brave", "stable",
			[@"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe",
			 @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe",
			 @"%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe"]),
	];

	// Discovery is ALWAYS exercised (no browser opened): it is the probe that yields
	// the honest availability matrix this evidence record is built from. The row
	// invariants below are what make an "absent browser reported as passed" bug
	// impossible to hide.
	[Fact]
	public void Discovery_YieldsHonestMatrixRows_WithoutLaunchingAnything()
	{
		(LaunchMatrix matrix, Dictionary<string, string> discovered) = BuildAvailabilityMatrix();

		_ = matrix.Rows.Select(r => r.Family).Should().Equal("chrome", "edge", "firefox", "brave");

		foreach (LaunchMatrixRow row in matrix.Rows)
		{
			if (row.Availability == LaunchTargetAvailability.NotAvailable)
			{
				_ = row.Attempted.Should().BeFalse($"{row.Family} is absent and must not be attempted");
				_ = row.Outcome.Should().BeNull($"{row.Family} is absent and must have no outcome");
				_ = row.AbsenceReason.Should().Be(LaunchMatrixBuilder.AbsenceReasonNotAvailable);
			}
			else
			{
				_ = discovered.ContainsKey(row.Family).Should().BeTrue($"{row.Family} is available, so its executable was found");
			}
		}

		// MissingFamilies is derived from the same discovery pass: it must never name a
		// family whose executable was actually found, and IsComplete must agree with it.
		_ = matrix.IsComplete.Should().Be(matrix.MissingFamilies.Count == 0);
		foreach (LaunchMatrixRow row in matrix.Rows.Where(r => r.Availability == LaunchTargetAvailability.Available))
		{
			_ = matrix.MissingFamilies.Should().NotContain(row.Family, $"{row.Family} was discovered and cannot be listed as missing");
		}
	}

	// The actual live launches. Skipped unless the operator opted in explicitly.
	[SkippableFact]
	public async Task LiveMatrix_LaunchesOnlyPresentFamilies_AndNeverReportsAbsentAsPassed()
	{
		Skip.If(
			Environment.GetEnvironmentVariable(OptInVariable) != "1",
			$"live browser launches are opt-in; set {OptInVariable}=1 to open the real Chrome/Edge/Firefox/Brave windows this test drives");

		(LaunchMatrix availability, Dictionary<string, string> discovered) = BuildAvailabilityMatrix();
		_ = availability.IsComplete.Should().BeTrue(
			$"the live four-browser matrix requires all four families present; missing: {string.Join(", ", availability.MissingFamilies)}");

		Dictionary<string, LaunchStatus> outcomes = new(StringComparer.Ordinal);

		foreach ((string family, string displayName, string channel, _) in DeclaredFamilies)
		{
			string exePath = discovered[family];
			LaunchFamilyTarget target = new(family, displayName, channel, exePath, Version: null);

			LaunchOutcomeObservation observation = await LaunchOnceAsync(target, ProbeUrl);
			outcomes[family] = observation.Status;

			// One attempt per action (plan W6.3): the launcher is invoked EXACTLY once
			// for this family, and no automatic retry ever happens.
			_ = observation.Attempts.Should().Be(1, $"{family} must be attempted exactly once with no automatic retry");

			// The argv contract: URL is its own single argument, no shell, no command
			// string. A fallback result (e.g. a browser refusing to start) still proves
			// the argv shape that was requested.
			_ = observation.RequestedArgvLast.Should().Be(ProbeUrl, "the URL must be the final, distinct argv item");
			_ = observation.UsedShellExecute.Should().BeFalse("D-183: no shell may be involved");
		}

		LaunchMatrix matrix = LaunchMatrixBuilder.Build(new LaunchMatrixRequest(ProbeUrl, discovered.ToDictionary(
			kv => kv.Key,
			kv => new LaunchFamilyTarget(kv.Key, kv.Key, "stable", kv.Value, null),
			StringComparer.Ordinal), outcomes));

		// HONESTY INVARIANT, re-asserted on the live path: no absent family is ever
		// claimed as attempted or as an outcome.
		_ = matrix.Rows
			.Where(r => r.Availability == LaunchTargetAvailability.NotAvailable)
			.Should()
			.OnlyContain(r => !r.Attempted && r.Outcome == null);

		foreach (LaunchMatrixRow row in matrix.Rows.Where(r => r.Availability == LaunchTargetAvailability.Available))
		{
			_ = row.Outcome.Should().NotBeNull($"{row.Family} was attempted, so it carries a typed outcome");
		}
	}

	// One REAL launch through the product launcher shape. Kept as a helper so the
	// attempt count and argv shape are observable rather than guessed.
	private static Task<LaunchOutcomeObservation> LaunchOnceAsync(LaunchFamilyTarget target, string url)
	{
		RecordingProcessStarter starter = new();
		WindowsBrowserLauncher launcher = new(
			Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsBrowserLauncher>.Instance,
			new SystemEnvironmentProbe(),
			starter);

		BrowserCandidate candidate = new(target.Family, target.DisplayName, target.ExePath, target.Channel, "live-matrix");

		LaunchResult result = launcher.Launch(candidate, url);

		return Task.FromResult(new LaunchOutcomeObservation(
			Status: result.Status,
			Attempts: starter.Attempts,
			RequestedArgvLast: starter.Captured?.ArgumentList.Count > 0 ? starter.Captured.ArgumentList[^1] : null,
			UsedShellExecute: starter.Captured?.UseShellExecute ?? false));
	}

	private static (LaunchMatrix Matrix, Dictionary<string, string> Discovered) BuildAvailabilityMatrix()
	{
		Dictionary<string, string> discovered = new(StringComparer.Ordinal);

		foreach ((string family, _, _, string[] templates) in DeclaredFamilies)
		{
			foreach (string template in templates)
			{
				string? expanded = Expand(template);
				if (expanded is not null && File.Exists(expanded))
				{
					discovered[family] = expanded;
					break;
				}
			}
		}

		Dictionary<string, LaunchFamilyTarget> targets = new(StringComparer.Ordinal);
		foreach (KeyValuePair<string, string> pair in discovered)
		{
			targets[pair.Key] = new LaunchFamilyTarget(pair.Key, pair.Key, "stable", pair.Value, null);
		}

		return (LaunchMatrixBuilder.Build(new LaunchMatrixRequest(ProbeUrl, targets, null)), discovered);
	}

	private static string? Expand(string template)
	{
		string expanded = Environment.ExpandEnvironmentVariables(template);
		return string.Equals(expanded, template, StringComparison.Ordinal) ? null : expanded;
	}

	private sealed record LaunchOutcomeObservation(LaunchStatus Status, int Attempts, string? RequestedArgvLast, bool UsedShellExecute);

	// Real IProcessStarter that ALSO starts the process. Used only under the live
	// opt-in; normal runs never reach here.
	private sealed class RecordingProcessStarter : IProcessStarter
	{
		public int Attempts { get; private set; }

		public System.Diagnostics.ProcessStartInfo? Captured { get; private set; }

		public bool Start(System.Diagnostics.ProcessStartInfo startInfo)
		{
			Attempts++;
			Captured = startInfo;
			using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo);
			return process is not null;
		}
	}
}
