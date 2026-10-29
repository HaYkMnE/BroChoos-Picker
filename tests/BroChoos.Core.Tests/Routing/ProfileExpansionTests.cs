using BroChoos.Core.Abstractions;
using BroChoos.Core.Routing;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Routing;

// One tile per Chromium profile: order, rule matching (browser-only vs browser+profile), and
// the families that stay one tile.
public sealed class ProfileExpansionTests
{
	private static readonly BrowserCandidate Chrome = new("chrome-stable", "Google Chrome", @"C:\c.exe", "stable", "known-path")
	{
		ProfileIds = ["Profile 2", "Default", "Profile 3"],
	};

	private static readonly BrowserCandidate Edge = new("edge-stable", "Microsoft Edge", @"C:\e.exe", "stable", "known-path")
	{
		ProfileIds = ["Default"],
	};

	private static readonly BrowserCandidate Firefox = new("firefox-release", "Mozilla Firefox", @"C:\f.exe", "release", "known-path")
	{
		ProfileIds = ["dev", "default-release"],
	};

	[Fact]
	public void ChromiumProfiles_BecomeTiles_InReaderOrder_AtTheBrowsersPlace()
	{
		IReadOnlyList<RankedChoice> tiles = Expand(None(), Edge, Chrome, Firefox);

		_ = tiles.Select(t => (t.Candidate.BrowserId, t.LaunchProfileId)).Should().Equal(
			("edge-stable", "Default"),
			("chrome-stable", "Profile 2"),
			("chrome-stable", "Default"),
			("chrome-stable", "Profile 3"),
			("firefox-release", "dev"));
	}

	[Fact]
	public void Firefox_StaysOneTile_EvenWithSeveralProfiles()
	{
		_ = Expand(None(), Firefox).Should().ContainSingle();
	}

	[Fact]
	public void BrowserOnlyRule_PromotesEveryProfileTileOfThatBrowser()
	{
		IReadOnlyList<RankedChoice> tiles = Expand(Rule("chrome-stable", null), Edge, Chrome);

		_ = tiles.Take(3).Should().OnlyContain(t => t.Candidate.BrowserId == "chrome-stable" && t.ReasonCode == RankingReasonCode.MatchedRuleSuggestion);
		_ = tiles[3].Candidate.BrowserId.Should().Be("edge-stable");
	}

	[Fact]
	public void ProfileRule_PromotesOnlyThatTile_AndTheOtherProfilesBecomeOrdinaryChoices()
	{
		IReadOnlyList<RankedChoice> tiles = Expand(Rule("chrome-stable", "Profile 3"), Edge, Chrome);

		_ = tiles[0].LaunchProfileId.Should().Be("Profile 3", "the rule's profile is Chrome's first tile");
		_ = tiles[0].ReasonCode.Should().Be(RankingReasonCode.MatchedRuleSuggestion);
		_ = tiles.Select(t => (t.Candidate.BrowserId, t.LaunchProfileId)).Should().Equal(
			("chrome-stable", "Profile 3"),
			("chrome-stable", "Profile 2"),
			("chrome-stable", "Default"),
			("edge-stable", "Default"));
		_ = tiles.Skip(1).Should().OnlyContain(t => t.MatchedRuleId == null);
	}

	// REVIEW MED: profiles never push another browser off the picker.
	[Fact]
	public void EveryBrowserKeepsATile_WhenOneBrowserHasManyProfiles()
	{
		BrowserCandidate chrome9 = Chrome with { ProfileIds = [.. Enumerable.Range(1, 9).Select(i => $"Profile {i}")] };

		IReadOnlyList<RankedChoice> tiles = Expand(None(), Edge, chrome9, Firefox);

		_ = tiles.Should().HaveCount(9);
		_ = tiles.Select(t => t.Candidate.BrowserId).Should().Contain("firefox-release");
		_ = tiles.Select(t => (t.Candidate.BrowserId, t.LaunchProfileId)).Should().StartWith(
		[
			("edge-stable", "Default"),
			("chrome-stable", "Profile 1"),
			("chrome-stable", "Profile 2"),
		]);
		_ = tiles[^1].Candidate.BrowserId.Should().Be("firefox-release", "Chrome's extra tiles sit right after its first tile");
		_ = tiles.Count(t => t.Candidate.BrowserId == "chrome-stable").Should().Be(7);
		_ = tiles.Last(t => t.Candidate.BrowserId == "chrome-stable").HiddenProfileCount.Should().Be(2);
		_ = tiles.Where(t => t.Candidate.BrowserId != "chrome-stable").Should().OnlyContain(t => t.HiddenProfileCount == 0);
	}

	[Fact]
	public void NineBrowsersWithProfiles_GetNineBrowserTiles_AndNoProfileTiles()
	{
		BrowserCandidate[] nine = [.. Enumerable.Range(1, 9).Select(i => new BrowserCandidate($"chrome-c{i}", $"Chrome {i}", $@"C:\c{i}.exe", "stable", "known-path")
		{
			ProfileIds = ["Default", "Profile 1"],
		})];

		IReadOnlyList<RankedChoice> tiles = Expand(None(), nine);

		_ = tiles.Select(t => t.Candidate.BrowserId).Should().Equal(nine.Select(c => c.BrowserId));
		_ = tiles.Should().OnlyContain(t => t.LaunchProfileId == "Default" && t.HiddenProfileCount == 1);
	}

	[Fact]
	public void MoreThanNineBrowsers_CutsTheLowestRanked_AsBefore()
	{
		BrowserCandidate[] eleven = [.. Enumerable.Range(1, 11).Select(i => new BrowserCandidate($"browser-{i}", $"B{i}", $@"C:\b{i}.exe", "stable", "known-path"))];

		_ = Expand(None(), eleven).Select(t => t.Candidate.BrowserId).Should().Equal(eleven.Take(9).Select(c => c.BrowserId));
	}

	[Fact]
	public void DuplicateProfileIds_GiveOneTileEach()
	{
		BrowserCandidate dup = Chrome with { ProfileIds = ["Default", "Default", "Profile 1", "Profile 1"] };

		_ = Expand(None(), dup).Select(t => t.LaunchProfileId).Should().Equal("Default", "Profile 1");
	}

	[Fact]
	public void ProfileRule_ForAProfileTheBrowserDoesNotHave_ActsLikeABrowserOnlyRule()
	{
		IReadOnlyList<RankedChoice> tiles = Expand(Rule("chrome-stable", "Gone"), Chrome);

		_ = tiles.Should().HaveCount(3).And.OnlyContain(t => t.ReasonCode == RankingReasonCode.MatchedRuleSuggestion);
	}

	private static IReadOnlyList<RankedChoice> Expand(RoutingDecision decision, params BrowserCandidate[] candidates)
	{
		return RankingEngine.ExpandChromiumProfiles(
			RankingEngine.Rank(candidates, decision, Core.Ingress.SourceConfidence.Unknown, null, null),
			decision);
	}

	private static RoutingDecision None()
	{
		return new RoutingDecision(1, null);
	}

	private static RoutingDecision Rule(string browserId, string? profileId)
	{
		return new RoutingDecision(1, new RoutingSuggestion("r1", new RoutingTargetRef(browserId, profileId), "test"));
	}
}
