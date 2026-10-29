using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

// W6.3 pure-matrix pins. The live four-browser launch itself is exercised by the
// opt-in live test (LiveLaunchMatrixTests); THIS file owns the deterministic
// classification logic: every declared family maps to exactly one row, an absent
// browser becomes NotAvailable and can never be reported as launched, and the
// per-row outcome vocabulary stays typed.
//
// Contract source: plan W6.3 "GREEN: Filtered tests and four-browser controlled
// launch matrix; expected one attempt per action, correct argv, typed results";
// spec 3.5/3.6 (Gate 0B families, typed LaunchResult) and spec 9.2 criterion 0B-1.
public sealed class LaunchMatrixBuilderTests
{
	private static readonly string[] RequiredFamilies = ["chrome", "edge", "firefox", "brave"];

	[Fact]
	public void Build_DeclaresExactlyTheFourRequiredFamilies_InSpecOrder()
	{
		LaunchMatrixRequest request = BuildRequest();

		LaunchMatrix matrix = LaunchMatrixBuilder.Build(request);

		_ = matrix.Rows.Select(r => r.Family).Should().Equal("chrome", "edge", "firefox", "brave");
		_ = matrix.Rows.Select(r => r.Family).Should().Equal(RequiredFamilies);
	}

	[Fact]
	public void Build_AbsentBrowser_IsNotAvailable_AndNeverLaunched()
	{
		// Only chrome exists; the other three must be honest NotAvailable rows.
		LaunchMatrixRequest request = BuildRequest(
			availableFamilies: ["chrome"],
			outcomes: new Dictionary<string, LaunchStatus>(StringComparer.Ordinal) { ["chrome"] = LaunchStatus.Success });

		LaunchMatrix matrix = LaunchMatrixBuilder.Build(request);

		LaunchMatrixRow edge = matrix.RowFor("edge");
		_ = edge.Availability.Should().Be(LaunchTargetAvailability.NotAvailable);
		_ = edge.Attempted.Should().BeFalse();
		_ = edge.Outcome.Should().BeNull();
		_ = edge.AbsenceReason.Should().Be("not-available-on-machine");

		// Only the present, recorded family is attempted; an absent family is never
		// reported as launched anywhere.
		_ = matrix.Rows.Where(r => r.Attempted).Select(r => r.Family).Should().Equal("chrome");
	}

	[Fact]
	public void Build_PresentBrowser_IsAttemptedAndCarriesTypedOutcome()
	{
		LaunchMatrixRequest request = BuildRequest(
			availableFamilies: ["chrome", "edge", "firefox", "brave"],
			outcomes: new Dictionary<string, LaunchStatus>(StringComparer.Ordinal)
			{
				["chrome"] = LaunchStatus.Success,
				["edge"] = LaunchStatus.Failed,
				["firefox"] = LaunchStatus.Timeout,
				["brave"] = LaunchStatus.Unknown,
			});

		LaunchMatrix matrix = LaunchMatrixBuilder.Build(request);

		_ = matrix.Rows.Should().OnlyContain(r => r.Availability == LaunchTargetAvailability.Available);
		_ = matrix.RowFor("chrome").Outcome.Should().Be(LaunchStatus.Success);
		_ = matrix.RowFor("edge").Outcome.Should().Be(LaunchStatus.Failed);
		_ = matrix.RowFor("firefox").Outcome.Should().Be(LaunchStatus.Timeout);
		_ = matrix.RowFor("brave").Outcome.Should().Be(LaunchStatus.Unknown);
		_ = matrix.Rows.Should().OnlyContain(r => r.Attempted);
	}

	[Fact]
	public void Build_PresentBrowserWithoutRecordedOutcome_IsUnknown_NotSuccess_AndNotCountedAsAttempted()
	{
		// A present browser whose outcome map carries no record: the matrix must not
		// invent an attempt, and the missing record must read as Unknown — never as a
		// silent Success.
		LaunchMatrixRequest request = BuildRequest(
			availableFamilies: ["chrome"],
			outcomes: new Dictionary<string, LaunchStatus>(StringComparer.Ordinal));

		LaunchMatrix matrix = LaunchMatrixBuilder.Build(request);

		LaunchMatrixRow chrome = matrix.RowFor("chrome");
		_ = chrome.Availability.Should().Be(LaunchTargetAvailability.Available);
		_ = chrome.Attempted.Should().BeFalse("no outcome was recorded, so no attempt is claimed");
		_ = chrome.Outcome.Should().Be(LaunchStatus.Unknown);
		_ = chrome.Outcome.Should().NotBe(LaunchStatus.Success);
		_ = matrix.AttemptedCount.Should().Be(0);
	}

	[Fact]
	public void Build_IsComplete_IsFalseWhenAnyRequiredFamilyIsNotAvailable()
	{
		LaunchMatrix partial = LaunchMatrixBuilder.Build(BuildRequest(availableFamilies: ["chrome", "edge"]));
		_ = partial.IsComplete.Should().BeFalse();
		_ = partial.MissingFamilies.Should().Equal("firefox", "brave");

		LaunchMatrix full = LaunchMatrixBuilder.Build(BuildRequest(
			availableFamilies: ["chrome", "edge", "firefox", "brave"]));
		_ = full.IsComplete.Should().BeTrue();
		_ = full.MissingFamilies.Should().BeEmpty();
	}

	[Fact]
	public void Build_CountsAreConsistent_WithRows()
	{
		LaunchMatrix matrix = LaunchMatrixBuilder.Build(BuildRequest(
			availableFamilies: ["chrome", "firefox"],
			outcomes: new Dictionary<string, LaunchStatus>(StringComparer.Ordinal)
			{
				["chrome"] = LaunchStatus.Success,
				["firefox"] = LaunchStatus.Failed,
			}));

		_ = matrix.AvailableCount.Should().Be(2);
		_ = matrix.NotAvailableCount.Should().Be(2);
		_ = matrix.AttemptedCount.Should().Be(matrix.Rows.Count(r => r.Attempted));
		_ = matrix.AttemptedCount.Should().Be(2);
	}

	[Fact]
	public void Build_AbsentRowsNeverSatisfyPerFamilyAcceptance()
	{
		// The acceptance gate (spec 9.2 #1) requires all four families to actually
		// launch. A machine with no brave must not be able to claim 0B-1 from this
		// matrix alone: the absent family is explicit, and the matrix is incomplete.
		LaunchMatrix matrix = LaunchMatrixBuilder.Build(BuildRequest(
			availableFamilies: ["chrome", "edge", "firefox"]));

		_ = matrix.IsComplete.Should().BeFalse();
		_ = matrix.RowFor("brave").Availability.Should().Be(LaunchTargetAvailability.NotAvailable);
		_ = matrix.RowFor("brave").Outcome.Should().BeNull();
		_ = matrix.MissingFamilies.Should().ContainSingle().Which.Should().Be("brave");
	}

	private static LaunchMatrixRequest BuildRequest(
		IReadOnlyCollection<string>? availableFamilies = null,
		IReadOnlyDictionary<string, LaunchStatus>? outcomes = null)
	{
		IReadOnlyCollection<string> available = availableFamilies ?? RequiredFamilies;

		Dictionary<string, LaunchFamilyTarget> targets = new(StringComparer.Ordinal)
		{
			["chrome"] = new("chrome", "Google Chrome", "stable", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "153.0.8010.50"),
			["edge"] = new("edge", "Microsoft Edge", "stable", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "153.0.4234.32"),
			["firefox"] = new("firefox", "Mozilla Firefox", "stable", @"C:\Program Files\Mozilla Firefox\firefox.exe", "154.0"),
			["brave"] = new("brave", "Brave", "stable", @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe", "153.1.95.102"),
		};

		foreach (string family in targets.Keys.ToArray())
		{
			if (!available.Contains(family, StringComparer.Ordinal))
			{
				_ = targets.Remove(family);
			}
		}

		return new LaunchMatrixRequest("https://example.com/", targets, outcomes);
	}
}
