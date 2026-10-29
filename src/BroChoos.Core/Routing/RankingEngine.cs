using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;

namespace BroChoos.Core.Routing;

public enum RankingReasonCode
{
	MatchedRuleSuggestion,
	RecentSession,
	ProbableSourceOrdering,
	AllChoices,
}

public sealed record RankedChoice(
	BrowserCandidate Candidate,
	int PinnedSlot,
	RankingReasonCode ReasonCode,
	string ReasonLabel,
	string? MatchedRuleId)
{
	// The profile this tile launches (Chromium --profile-directory). Set by
	// ExpandChromiumProfiles; null for a browser with no known profile.
	public string? LaunchProfileId { get; init; }

	// On a browser's last tile: how many of its profiles did not fit the nine slots.
	public int HiddenProfileCount { get; init; }
}

public static class RankingEngine
{
	public static IReadOnlyList<RankedChoice> Rank(
		IReadOnlyList<BrowserCandidate> candidates,
		RoutingDecision decision,
		SourceConfidence sourceConfidence,
		string? probablePreferredBrowserId,
		IReadOnlySet<string>? recentSessionBrowserIds)
	{
		List<(BrowserCandidate Candidate, int PinnedSlot)> indexed = [.. candidates.Select((candidate, index) => (candidate, index + 1))];
		List<RankedChoice> ranked = [];

		void AddChoice((BrowserCandidate Candidate, int PinnedSlot) item, RankingReasonCode code, string label, string? matchedRuleId = null)
		{
			ranked.Add(new RankedChoice(item.Candidate, item.PinnedSlot, code, label, matchedRuleId));
			_ = indexed.Remove(item);
		}

		if (decision.HasSuggestion)
		{
			// NOT FirstOrDefault ON THE TUPLE, and the difference is not stylistic. This list
			// holds value tuples, so FirstOrDefault returns default((BrowserCandidate, int)) =
			// (null, 0) when no element matches, and a null-check on that result is TRUE — a
			// non-nullable value tuple is never null. The guard would pass and emit a phantom
			// RankedChoice at slot 0 with a null Candidate, ranked ahead of every real browser.
			// An index is the check that actually distinguishes "found" from "not found".
			int suggestionIndex = indexed.FindIndex(i =>
				string.Equals(i.Candidate.BrowserId, decision.Suggestion!.Target.BrowserId, StringComparison.Ordinal));

			if (suggestionIndex >= 0)
			{
				AddChoice(
					indexed[suggestionIndex],
					RankingReasonCode.MatchedRuleSuggestion,
					$"Matched rule: {decision.Suggestion!.RuleId}",
					decision.Suggestion.RuleId);
			}
		}

		if (recentSessionBrowserIds is not null)
		{
			List<(BrowserCandidate Candidate, int PinnedSlot)> recent = [.. indexed.Where(i => recentSessionBrowserIds.Contains(i.Candidate.BrowserId))];
			foreach ((BrowserCandidate Candidate, int PinnedSlot) item in recent)
			{
				AddChoice(item, RankingReasonCode.RecentSession, "Recent choice");
			}
		}

		if (sourceConfidence == SourceConfidence.Probable && !string.IsNullOrWhiteSpace(probablePreferredBrowserId))
		{
			// Same defect, same fix as the suggestion branch above: an index distinguishes
			// "absent" from "present", a default value tuple does not.
			int probableIndex = indexed.FindIndex(i =>
				string.Equals(i.Candidate.BrowserId, probablePreferredBrowserId, StringComparison.Ordinal));

			if (probableIndex >= 0)
			{
				AddChoice(indexed[probableIndex], RankingReasonCode.ProbableSourceOrdering, "Likely from source app");
			}
		}

		foreach ((BrowserCandidate candidate, int pinnedSlot) in indexed)
		{
			ranked.Add(new RankedChoice(candidate, pinnedSlot, RankingReasonCode.AllChoices, "All choices", null));
		}

		return ranked;
	}

	// The picker's keyboard limit (digits 1-9).
	public const int MaxTiles = 9;

	// ONE TILE PER CHROMIUM PROFILE (owner decision), WITHOUT CROWDING OUT OTHER BROWSERS.
	// A Chromium browser (LaunchHelperArguments: the family that gets --profile-directory) with
	// two or more distinct profiles may get one tile per profile; every other browser is one tile
	// and launches with its first profile id (Firefox, custom and portable get no switch at all).
	//
	// SLOTS, in two passes:
	//   1. every browser, in rank order, gets ONE tile, up to maxTiles. More browsers than slots:
	//      the lowest-ranked browsers are cut, as before profiles existed.
	//   2. the slots left over go to extra profile tiles, browser by browser in rank order and
	//      profile by profile in the reader's order. A browser's extra tiles sit right after its
	//      first tile. Profiles that did not fit are counted on that browser's LAST tile
	//      (HiddenProfileCount), so the picker can say they exist.
	//
	// A BROWSER'S FIRST TILE is its last-used profile (the reader sorts it first), or the profile
	// a rule names when that rule matched this browser and the browser has that profile. A rule
	// with no profile matched the browser, so all its tiles keep the rule's reason; with a profile
	// rule, only the named tile does, and its other profiles are ordinary choices.
	public static IReadOnlyList<RankedChoice> ExpandChromiumProfiles(IReadOnlyList<RankedChoice> ranked, RoutingDecision decision, int maxTiles = MaxTiles)
	{
		string? ruleProfile = decision.Suggestion?.Target.ProfileId;
		List<(RankedChoice First, List<RankedChoice> Extras)> browsers = [];

		foreach (RankedChoice choice in ranked.Take(Math.Max(0, maxTiles)))
		{
			List<string> ids = choice.Candidate.ProfileIds is { } all
				? [.. all.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)]
				: [];
			bool split = ids.Count > 1
				&& LaunchHelperArguments.ResolveFamily(choice.Candidate.BrowserId) == BrowserLaunchFamily.Chromium;
			if (!split)
			{
				browsers.Add((choice with { LaunchProfileId = ids.Count > 0 ? ids[0] : null }, []));
				continue;
			}

			bool profileRule = choice.ReasonCode == RankingReasonCode.MatchedRuleSuggestion
				&& ruleProfile is not null
				&& ids.Contains(ruleProfile, StringComparer.Ordinal);
			string first = profileRule ? ruleProfile! : ids[0];
			RankedChoice extraTemplate = profileRule
				? choice with { ReasonCode = RankingReasonCode.AllChoices, ReasonLabel = "All choices", MatchedRuleId = null }
				: choice;

			browsers.Add((
				choice with { LaunchProfileId = first },
				[.. ids.Where(id => !string.Equals(id, first, StringComparison.Ordinal)).Select(id => extraTemplate with { LaunchProfileId = id })]));
		}

		int spare = Math.Max(0, maxTiles) - browsers.Count;
		List<RankedChoice> tiles = [];
		foreach ((RankedChoice firstTile, List<RankedChoice> extras) in browsers)
		{
			int shown = Math.Min(spare, extras.Count);
			spare -= shown;
			int hidden = extras.Count - shown;

			List<RankedChoice> group = [firstTile, .. extras.Take(shown)];
			if (hidden > 0)
			{
				group[^1] = group[^1] with { HiddenProfileCount = hidden };
			}

			tiles.AddRange(group);
		}

		return tiles;
	}
}
