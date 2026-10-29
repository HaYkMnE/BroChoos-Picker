using System.Globalization;
using BroChoos.App.Localization;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Routing;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// These tests switch LocalizationService.CurrentCulture, which is process-wide static state.
// They live apart from PickerLayoutOptionTests (AvaloniaUiCollection) so that
// ConsoleCaptureCollection serializes them with every other culture-touching class.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class PickerLayoutCultureTests
{
	// REVIEW LOW: the hidden-profile phrase agrees with its number (en 1/many; ru 1/2-4/5+).
	[Theory]
	[InlineData("en", 1, "1 more profile not shown")]
	[InlineData("en", 2, "2 more profiles not shown")]
	[InlineData("en", 5, "5 more profiles not shown")]
	[InlineData("ru", 1, "ещё 1 профиль не показан")]
	[InlineData("ru", 2, "ещё 2 профиля не показаны")]
	[InlineData("ru", 5, "ещё 5 профилей не показано")]
	[InlineData("ru", 11, "ещё 11 профилей не показано")]
	[InlineData("ru", 21, "ещё 21 профиль не показан")]
	[InlineData("ru", 22, "ещё 22 профиля не показаны")]
	public void MoreProfilesPhrase_UsesThePluralTheNumberNeeds(string culture, int count, string expected)
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo(culture);
			_ = LocalizationService.GetPlural("Card.MoreProfiles", count).Should().Be(expected);
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	// REVIEW LOW: the count is said once (in the name), the help text is the plain key, and the
	// tooltip names the key in words, so "…not shown: 2 — 2" can no longer happen.
	[Theory]
	[InlineData("en", "Google Chrome, Work, 2 more profiles not shown", "Google Chrome, Work, 2 more profiles not shown — key 2", "Key 2")]
	[InlineData("ru", "Google Chrome, Work, ещё 2 профиля не показаны", "Google Chrome, Work, ещё 2 профиля не показаны — клавиша 2", "Клавиша 2")]
	[InlineData("hy", "Google Chrome, Work, ևս 2 պրոֆիլ ցուցադրված չէ", "Google Chrome, Work, ևս 2 պրոֆիլ ցուցադրված չէ — ստեղն 2", "Ստեղն 2")]
	public void HiddenProfiles_AreSaidOnce_AndTheTooltipNamesTheKey(string culture, string name, string tooltip, string help)
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo(culture);
			BrowserCandidate chrome = new("chrome-stable", "Google Chrome", @"C:\c.exe", "stable", "known-path")
			{
				ProfileIds = ["Profile 2", "Default", "Profile 3"],
				ProfileNames = ["Work", "Anna", "Test"],
			};
			ChoiceCardViewModel card = new(chrome, 2, 2, RankingReasonCode.AllChoices, null, "Profile 2", 2);

			_ = card.AccessibleName.Should().Be(name);
			_ = card.TooltipText.Should().Be(tooltip);
			_ = card.KeyHelpText.Should().Be(help);
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}
}
