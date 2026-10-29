using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using BroChoos.App.Localization;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Routing;

namespace BroChoos.App.Picker;

// D-125 zone 2: one card per detected candidate, carrying its identity (browser id +
// display name) and its keyboard slot (1-9, shown as the shortcut hint).
//
// W7.2 adds the VISIBLE reason. Three fields carry it, and the split is deliberate:
//
//   Slot        the 1-based position in the VISIBLE list. This is what the digit keys
//               name (D-130), so it must follow the ranked order.
//   PinnedSlot  the 1-based position in the DETECTOR's list, which ranking never changes.
//               It is the card's stable identity across reorders: a caller that must
//               re-assert focus on the SAME browser after a snapshot replacement keys on
//               this, not on the display position.
//   ReasonCode  the machine-readable vocabulary item, so tests never assert on prose.
//   ReasonLabel the localized, human-readable explanation shown to the user.
//
// THE MATCHED-RULE LABEL NAMES THE RULE. The plan's acceptance gate is "every non-default
// ordering has a user-visible explanation", and a rule id the user can find in their rules
// file is an explanation; "Recommended" would not be, and is forbidden by spec 3.7. The
// generic fallback exists only for the defensive case where a suggestion carries no id,
// because formatting a null into "{0}" would be a lie about which rule matched.
internal sealed class ChoiceCardViewModel(
	BrowserCandidate candidate,
	int slot,
	int pinnedSlot,
	RankingReasonCode reasonCode,
	string? matchedRuleId,
	string? launchProfileId = null,
	int hiddenProfileCount = 0) : INotifyPropertyChanged
{
	// Profiles of this browser that did not fit the nine slots (set on its LAST tile only).
	public int HiddenProfileCount { get; } = Math.Max(0, hiddenProfileCount);

	// The Chromium profile directory THIS tile launches (--profile-directory), and the one
	// history records. Set per tile by RankingEngine.ExpandChromiumProfiles; a card built
	// without it falls back to the browser's first profile id, the rule before per-profile tiles.
	public string? LaunchProfileId { get; } = launchProfileId
		?? candidate.ProfileIds?.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

	public event PropertyChangedEventHandler? PropertyChanged;

	// The browser's program icon, null until BrowserIconLoader delivers it (and for good when it
	// cannot be read). SET ON THE UI THREAD ONLY: the tile's Image binds to it.
	public IImage? Icon
	{
		get;
		set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
		}
	}

	public bool HasIcon => Icon is not null;

	public BrowserCandidate Candidate { get; } = candidate;

	public int Slot { get; } = slot;

	public int PinnedSlot { get; } = pinnedSlot;

	public string ShortcutHint { get; } = slot.ToString(CultureInfo.InvariantCulture);

	public string DisplayName => Candidate.DisplayName;

	public string BrowserId => Candidate.BrowserId;

	public RankingReasonCode ReasonCode { get; } = reasonCode;

	public string? MatchedRuleId { get; } = matchedRuleId;

	// Resolved once, at construction. The culture is fixed for the lifetime of one picker
	// run, and a card is rebuilt whenever the candidate list is, so there is no stale-label
	// window to close.
	public string ReasonLabel { get; } = BuildReasonLabel(reasonCode, matchedRuleId);

	// The placeholder glyph on the tile's icon plate (and the fallback when no program icon can
	// be read): the first letter or digit of the display name, upper-cased.
	public string Initial { get; } = BuildInitial(candidate.DisplayName);

	// The visible name of the profile this tile LAUNCHES (LaunchProfileId), shown only when the
	// browser has more than one profile: with a single profile the line would be noise.
	// CHROMIUM ONLY: LaunchHelperArguments passes a profile switch to Chromium browsers alone;
	// Firefox and manual entries open their own default, so naming a profile there would be false.
	// PERSONAL DATA: UI only, never logged.
	public string? ProfileName => BuildProfileName(Candidate, LaunchProfileId);

	public bool HasProfile => ProfileName is not null || HiddenProfileCount > 0;

	// The muted second line is TWO runs (PickerWindow.axaml): the profile name, which may trim,
	// then "+N" when N more profiles of this browser did not fit, which never trims. One
	// combined string trimmed the count away first.
	public bool HasHiddenProfiles => HiddenProfileCount > 0;

	public string? HiddenProfileCountText => HasHiddenProfiles
		? LocalizationService.GetString("Card.ProfileMore", HiddenProfileCount)
		: null;

	// Screen-reader name for the tile's button (AutomationProperties.Name): what the tile shows -
	// the browser name, the profile when one is shown, and the hidden-profile count in words.
	public string AccessibleName => (ProfileName, HasHiddenProfiles) switch
	{
		(null, false) => LocalizationService.GetString("Card.AccessibleName", DisplayName),
		(null, true) => LocalizationService.GetString("Card.AccessibleName.More", DisplayName, MoreProfilesText),
		(_, false) => LocalizationService.GetString("Card.AccessibleName.Profile", DisplayName, ProfileName),
		(_, true) => LocalizationService.GetString("Card.AccessibleName.ProfileMore", DisplayName, ProfileName, MoreProfilesText),
	};

	// "2 more profiles not shown", with the plural form the language needs.
	private string MoreProfilesText => LocalizationService.GetPlural("Card.MoreProfiles", HiddenProfileCount);

	// The digit is not drawn on the tile (owner decision); it is discoverable in the tooltip and
	// announced as help text after the name, so the name itself stays short. Setting help text
	// also stops the screen reader reading the tooltip as a second copy of the name. The hidden
	// profile count is in the name already, so the help text never repeats it.
	public string TooltipText => LocalizationService.GetString("Card.Tooltip", AccessibleName, Slot);

	public string KeyHelpText => LocalizationService.GetString("Card.HelpText", Slot);

	private static string? BuildProfileName(BrowserCandidate candidate, string? launchProfileId)
	{
		if (LaunchHelperArguments.ResolveFamily(candidate.BrowserId) != BrowserLaunchFamily.Chromium
			|| candidate.ProfileIds is not { Count: > 1 } ids
			|| candidate.ProfileNames is not { } names
			|| launchProfileId is null)
		{
			return null;
		}

		int index = -1;
		for (int i = 0; i < ids.Count; i++)
		{
			if (string.Equals(ids[i], launchProfileId, StringComparison.Ordinal))
			{
				index = i;
				break;
			}
		}

		return index >= 0 && index < names.Count && !string.IsNullOrWhiteSpace(names[index]) ? names[index].Trim() : null;
	}

	private static string BuildInitial(string displayName)
	{
		foreach (char c in displayName)
		{
			if (char.IsLetterOrDigit(c))
			{
				return char.ToUpperInvariant(c).ToString();
			}
		}

		return "?";
	}

	private static string BuildReasonLabel(RankingReasonCode code, string? matchedRuleId)
	{
		return code switch
		{
			RankingReasonCode.MatchedRuleSuggestion when !string.IsNullOrWhiteSpace(matchedRuleId) =>
				LocalizationService.GetString("Suggestion.Reason.MatchedRule", matchedRuleId),
			RankingReasonCode.MatchedRuleSuggestion =>
				LocalizationService.GetString("Suggestion.Reason.MatchedRuleGeneric"),
			RankingReasonCode.RecentSession => LocalizationService.GetString("Suggestion.Reason.RecentSession"),
			RankingReasonCode.ProbableSourceOrdering => LocalizationService.GetString("Suggestion.Reason.ProbableSource"),
			RankingReasonCode.AllChoices => LocalizationService.GetString("Suggestion.Reason.AllChoices"),
			_ => LocalizationService.GetString("Suggestion.Reason.AllChoices"),
		};
	}
}
