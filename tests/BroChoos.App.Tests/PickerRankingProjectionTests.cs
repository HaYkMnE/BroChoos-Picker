using BroChoos.App.Localization;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Routing;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using System.Globalization;
using Xunit;

namespace BroChoos.App.Tests;

// W7.2: the ranking must be VISIBLE. A reordered card carries a truthful, human-readable
// reason, and the reason comes from the rule/evaluator vocabulary — never an opaque
// "Recommended". These are App-level facts: the VM projection and the localized labels.
// LocalizationService.CurrentCulture is process-wide static state; serialized with the other culture-touching classes.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class PickerRankingProjectionTests
{
	private const string Url = "https://docs.example.com/download";

	[Fact]
	public void Cards_WithoutRanking_KeepInputOrder_AndEveryCardExplainsAllChoices()
	{
		PickerViewModel vm = NewViewModel();

		_ = vm.Cards.Select(c => c.BrowserId).Should().Equal("edge-stable", "chrome-stable");
		_ = vm.Cards.Should().OnlyContain(c => c.ReasonCode == RankingReasonCode.AllChoices);
		_ = vm.Cards.Should().OnlyContain(c => c.ReasonLabel == LocalizationService.GetString("Suggestion.Reason.AllChoices"));
	}

	[Fact]
	public void Cards_WithMatchedRule_ReorderAndShowTheRuleIdAsTheReason()
	{
		RoutingDecision decision = new(
			3,
			new RoutingSuggestion(
				"work-domains",
				new RoutingTargetRef("chrome-stable", null),
				"Matched rule 'work-domains' by orderIndex 1."));

		PickerViewModel vm = NewViewModel(new PickerRankingInputs(
			decision,
			SourceConfidence.Unknown,
			ProbablePreferredBrowserId: null,
			RecentSessionBrowserIds: null));

		_ = vm.Cards[0].BrowserId.Should().Be("chrome-stable");
		_ = vm.Cards[0].ReasonCode.Should().Be(RankingReasonCode.MatchedRuleSuggestion);
		_ = vm.Cards[0].MatchedRuleId.Should().Be("work-domains");
		_ = vm.Cards[0].ReasonLabel.Should().Contain("work-domains");
		_ = vm.Cards[0].ReasonLabel.Should().Be(LocalizationService.GetString("Suggestion.Reason.MatchedRule", "work-domains"));
	}

	[Fact]
	public void Cards_KeepOneBasedVisibleSlots_AndCarryThePinnedIdentitySeparately()
	{
		RoutingDecision decision = new(
			3,
			new RoutingSuggestion("work-domains", new RoutingTargetRef("chrome-stable", null), "..."));

		PickerViewModel vm = NewViewModel(new PickerRankingInputs(decision, SourceConfidence.Unknown, null, null));

		// D-130: the digit always names the Nth VISIBLE card, so the shortcut hint follows
		// the visible order even though the pinned identity records where the card started.
		_ = vm.Cards.Select(c => c.Slot).Should().Equal(1, 2);
		_ = vm.Cards.Select(c => c.ShortcutHint).Should().Equal("1", "2");
		_ = vm.Cards.Single(c => c.BrowserId == "chrome-stable").PinnedSlot.Should().Be(2);
		_ = vm.Cards.Single(c => c.BrowserId == "edge-stable").PinnedSlot.Should().Be(1);
	}

	[Fact]
	public void Cards_ProbableSource_ReordersWithAVisibleReason_AndUnknownDoesNot()
	{
		PickerViewModel ranked = NewViewModel(new PickerRankingInputs(
			new RoutingDecision(1, null),
			SourceConfidence.Probable,
			"chrome-stable",
			null));
		PickerViewModel unknown = NewViewModel(new PickerRankingInputs(
			new RoutingDecision(1, null),
			SourceConfidence.Unknown,
			"chrome-stable",
			null));

		_ = ranked.Cards[0].BrowserId.Should().Be("chrome-stable");
		_ = ranked.Cards[0].ReasonCode.Should().Be(RankingReasonCode.ProbableSourceOrdering);
		_ = ranked.Cards[0].ReasonLabel.Should().Be(LocalizationService.GetString("Suggestion.Reason.ProbableSource"));

		_ = unknown.Cards.Select(c => c.BrowserId).Should().Equal("edge-stable", "chrome-stable");
		_ = unknown.Cards.Should().OnlyContain(c => c.ReasonCode == RankingReasonCode.AllChoices);
	}

	[Fact]
	public void ReasonVocabulary_IsLocalizedInEveryShippedPack_AndNeverSaysRecommended()
	{
		CultureInfo original = LocalizationService.CurrentCulture;
		try
		{
			string[] keys =
			[
				"Suggestion.Reason.MatchedRule",
				"Suggestion.Reason.MatchedRuleGeneric",
				"Suggestion.Reason.RecentSession",
				"Suggestion.Reason.ProbableSource",
				"Suggestion.Reason.AllChoices",
			];

			foreach (string culture in new[] { "en", "ru", "hy" })
			{
				LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo(culture);
				foreach (string key in keys)
				{
					string value = LocalizationService.GetString(key);
					_ = value.Should().NotBe(key, $"'{key}' must be translated in '{culture}'");
					_ = value.Should().NotContainEquivalentOf("recommended", $"the ranking vocabulary must never label an opaque result 'Recommended' ('{culture}')");
				}
			}
		}
		finally
		{
			LocalizationService.CurrentCulture = original;
		}
	}

	private static PickerViewModel NewViewModel(PickerRankingInputs? ranking = null)
	{
		BrowserCandidate[] candidates =
		[
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path"),
			new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path"),
		];

		return new PickerViewModel(Url, new BrowserSnapshot(candidates, 0, DateTimeOffset.UtcNow), timeProvider: null, ranking);
	}
}
