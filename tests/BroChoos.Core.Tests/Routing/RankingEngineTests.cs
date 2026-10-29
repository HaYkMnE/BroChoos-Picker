using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Routing;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Routing;

public sealed class RankingEngineTests
{
	[Fact]
	public void Rank_NoSuggestionAndNoSignals_KeepsInputOrder_WithVisibleAllChoicesReason()
	{
		BrowserCandidate[] candidates =
		[
			new("edge-stable", "Microsoft Edge", @"C:\\Edge\\msedge.exe", "stable", "known-path"),
			new("chrome-stable", "Google Chrome", @"C:\\Chrome\\chrome.exe", "stable", "known-path"),
		];

		IReadOnlyList<RankedChoice> ranked = RankingEngine.Rank(
			candidates,
			new RoutingDecision(1, null),
			SourceConfidence.Unknown,
			null,
			null);

		_ = ranked.Select(c => c.Candidate.BrowserId).Should().Equal("edge-stable", "chrome-stable");
		_ = ranked.Select(c => c.ReasonCode).Should().Equal(RankingReasonCode.AllChoices, RankingReasonCode.AllChoices);
		_ = ranked.Select(c => c.ReasonLabel).Should().OnlyContain(label => label == "All choices");
	}

	[Fact]
	public void Rank_MatchedRuleSuggestion_IsPrimaryGroup()
	{
		BrowserCandidate[] candidates =
		[
			new("edge-stable", "Microsoft Edge", @"C:\\Edge\\msedge.exe", "stable", "known-path"),
			new("chrome-stable", "Google Chrome", @"C:\\Chrome\\chrome.exe", "stable", "known-path"),
		];
		RoutingDecision decision = new(
			5,
			new RoutingSuggestion(
				"work-domains",
				new RoutingTargetRef("chrome-stable", null),
				"Matched by work domain"));

		IReadOnlyList<RankedChoice> ranked = RankingEngine.Rank(candidates, decision, SourceConfidence.Unknown, null, null);

		_ = ranked[0].Candidate.BrowserId.Should().Be("chrome-stable");
		_ = ranked[0].ReasonCode.Should().Be(RankingReasonCode.MatchedRuleSuggestion);
		_ = ranked[0].ReasonLabel.Should().Contain("work-domains");
	}

	[Fact]
	public void Rank_ProbableSourceMayReorder_WithVisibleReasonOnly()
	{
		BrowserCandidate[] candidates =
		[
			new("edge-stable", "Microsoft Edge", @"C:\\Edge\\msedge.exe", "stable", "known-path"),
			new("chrome-stable", "Google Chrome", @"C:\\Chrome\\chrome.exe", "stable", "known-path"),
		];

		IReadOnlyList<RankedChoice> ranked = RankingEngine.Rank(
			candidates,
			new RoutingDecision(2, null),
			SourceConfidence.Probable,
			"chrome-stable",
			null);

		_ = ranked[0].Candidate.BrowserId.Should().Be("chrome-stable");
		_ = ranked[0].ReasonCode.Should().Be(RankingReasonCode.ProbableSourceOrdering);
		_ = ranked[0].ReasonLabel.Should().Contain("Likely from source app");
	}

	[Fact]
	public void Rank_UnknownSourceHasNoEffect_EvenWhenPreferredProvided()
	{
		BrowserCandidate[] candidates =
		[
			new("edge-stable", "Microsoft Edge", @"C:\\Edge\\msedge.exe", "stable", "known-path"),
			new("chrome-stable", "Google Chrome", @"C:\\Chrome\\chrome.exe", "stable", "known-path"),
		];

		IReadOnlyList<RankedChoice> ranked = RankingEngine.Rank(
			candidates,
			new RoutingDecision(2, null),
			SourceConfidence.Unknown,
			"chrome-stable",
			null);

		_ = ranked.Select(c => c.Candidate.BrowserId).Should().Equal("edge-stable", "chrome-stable");
		_ = ranked.All(c => c.ReasonCode == RankingReasonCode.AllChoices).Should().BeTrue();
	}

	// A SUGGESTION WHOSE TARGET IS NOT AMONG THE CANDIDATES MUST SIMPLY NOT MATCH, and the
	// defect this pins is subtle enough to survive review: FirstOrDefault over a list of
	// value tuples returns default((BrowserCandidate, int)) = (null, 0) when nothing matches,
	// and `is { } matched` is TRUE for a non-nullable value tuple — so the guard passed and a
	// phantom RankedChoice was emitted at slot 0 with a null Candidate. It ranked FIRST,
	// ahead of every real browser, and anything dereferencing it would throw.
	[Fact]
	public void Rank_SuggestionTargetAbsentFromCandidates_ProducesNoPhantomChoice()
	{
		BrowserCandidate[] candidates =
		[
			new("edge-stable", "Microsoft Edge", @"C:\\Edge\\msedge.exe", "stable", "known-path"),
			new("firefox-stable", "Mozilla Firefox", @"C:\\Firefox\\firefox.exe", "stable", "known-path"),
		];
		RoutingDecision decision = new(
			5,
			new RoutingSuggestion(
				"work-domains",
				new RoutingTargetRef("chrome-stable", null),
				"Matched by work domain"));

		IReadOnlyList<RankedChoice> ranked = RankingEngine.Rank(candidates, decision, SourceConfidence.Unknown, null, null);

		_ = ranked.Should().HaveCount(candidates.Length);
		_ = ranked.All(choice => choice.Candidate is not null).Should().BeTrue(
			"a suggestion targeting an absent browser must not produce a phantom choice with a null Candidate");
		_ = ranked.Select(choice => choice.Candidate.BrowserId).Should().Equal("edge-stable", "firefox-stable");
		_ = ranked.Should().OnlyContain(choice => choice.ReasonCode == RankingReasonCode.AllChoices);
	}

	// The same shape for the probable-source path, which used the identical guard.
	[Fact]
	public void Rank_ProbablePreferredBrowserAbsentFromCandidates_ProducesNoPhantomChoice()
	{
		BrowserCandidate[] candidates =
		[
			new("edge-stable", "Microsoft Edge", @"C:\\Edge\\msedge.exe", "stable", "known-path"),
		];

		IReadOnlyList<RankedChoice> ranked = RankingEngine.Rank(
			candidates,
			new RoutingDecision(2, null),
			SourceConfidence.Probable,
			"chrome-stable",
			null);

		_ = ranked.Should().HaveCount(1);
		_ = ranked.All(choice => choice.Candidate is not null).Should().BeTrue(
			"a preferred browser absent from the candidates must not produce a phantom choice with a null Candidate");
		_ = ranked[0].Candidate.BrowserId.Should().Be("edge-stable");
		_ = ranked[0].ReasonCode.Should().Be(RankingReasonCode.AllChoices);
	}

	[Fact]
	public void Rank_ReorderPreservesPinnedSlotIdentity()
	{
		BrowserCandidate[] candidates =
		[
			new("edge-stable", "Microsoft Edge", @"C:\\Edge\\msedge.exe", "stable", "known-path"),
			new("chrome-stable", "Google Chrome", @"C:\\Chrome\\chrome.exe", "stable", "known-path"),
			new("firefox-stable", "Mozilla Firefox", @"C:\\Firefox\\firefox.exe", "stable", "known-path"),
		];

		IReadOnlyList<RankedChoice> ranked = RankingEngine.Rank(
			candidates,
			new RoutingDecision(9, null),
			SourceConfidence.Probable,
			"firefox-stable",
			null);

		_ = ranked[0].Candidate.BrowserId.Should().Be("firefox-stable");
		_ = ranked[0].PinnedSlot.Should().Be(3);
		_ = ranked.Single(c => c.Candidate.BrowserId == "edge-stable").PinnedSlot.Should().Be(1);
		_ = ranked.Single(c => c.Candidate.BrowserId == "chrome-stable").PinnedSlot.Should().Be(2);
	}
}
