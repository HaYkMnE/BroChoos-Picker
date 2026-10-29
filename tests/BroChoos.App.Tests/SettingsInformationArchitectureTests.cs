using System.Globalization;
using System.Text.Json;
using BroChoos.App.Localization;
using BroChoos.App.Settings;
using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// LocalizationService.CurrentCulture is process-wide static state; serialized with the other culture-touching classes.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class SettingsInformationArchitectureTests
{
	[Fact]
	public void FrozenInformationArchitecture_HasExact12SectionsInAuthorityOrder()
	{
		string[] orderedIds = [.. SettingsViewModel.OrderedSections.Select(section => section.SectionId)];
		_ = orderedIds.Should().Equal(
			"home",
			"control-depth",
			"browsers-profiles",
			"what-brochoos-opens",
			"rules-suggestions",
			"privacy-network",
			"appearance",
			"hotkeys",
			"language",
			"updates",
			"maintenance",
			"advanced");
	}

	[Fact]
	public void HomeDashboard_UsesConcreteLocalizationValues_NotKeyFallbacks_AndRuntimeCultureWiring()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			SettingsViewModel enVm = new();

			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("ru");
			SettingsViewModel ruVm = new();

			(string Key, string EnValue, string RuValue)[] assertions =
			[
				("Settings.Dashboard.Status", enVm.DashboardStatusText, ruVm.DashboardStatusText),
				("Settings.Dashboard.Depth", enVm.DashboardDepthText, ruVm.DashboardDepthText),
				("Settings.Dashboard.BrowserCount", enVm.DashboardBrowserCountText, ruVm.DashboardBrowserCountText),
				("Settings.Dashboard.ProfileCount", enVm.DashboardProfileCountText, ruVm.DashboardProfileCountText),
				("Settings.Dashboard.Coverage", enVm.DashboardCoverageText, ruVm.DashboardCoverageText),
				("Settings.Dashboard.Privacy", enVm.DashboardPrivacyText, ruVm.DashboardPrivacyText),
				("Settings.Dashboard.Health", enVm.DashboardHealthText, ruVm.DashboardHealthText),
				("Settings.Dashboard.Update", enVm.DashboardUpdateText, ruVm.DashboardUpdateText),
			];

			foreach ((string key, string enValue, string ruValue) in assertions)
			{
				_ = enValue.Should().NotBeNullOrWhiteSpace($"English localization for '{key}' must be populated");
				_ = ruValue.Should().NotBeNullOrWhiteSpace($"Russian localization for '{key}' must be populated");
				_ = enValue.Should().NotBe(key, $"'{key}' missing localization must not pass via key fallback");
				_ = ruValue.Should().NotBe(key, $"'{key}' missing localization must not pass via key fallback");
				_ = ruValue.Should().NotBe(enValue, $"'{key}' must be wired to runtime culture, not always fallback to English");
			}
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	// The overview shows only what the window can know: no sample numbers, an honest "unknown"
	// for counts it cannot read, the store state, and the real (loaded) control depth.
	[Fact]
	public void HomeDashboard_ShowsNoInventedNumbers_AndReflectsRealState()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			SettingsViewModel vm = new();

			_ = vm.DashboardBrowserCountText.Should().NotContain("1 detected").And.StartWith("Unknown");
			_ = vm.DashboardProfileCountText.Should().NotContain("0 detected").And.StartWith("Unknown");
			_ = vm.DashboardHealthText.Should().NotBe("No active issues");
			_ = vm.DashboardStatusText.Should().Be(LocalizationService.GetString("Settings.Dashboard.Status"), "no store composed means changes are not saved");

			_ = vm.SetControlDepth(SettingsControlDepth.Expert);
			_ = vm.DashboardDepthText.Should().Be(LocalizationService.GetString("Settings.Depth.Expert"), "the depth tile follows the real preference");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public void OrderedSections_DoesNotExposeMutableArrayBacking()
	{
		IReadOnlyList<SettingsSection> orderedSections = SettingsViewModel.OrderedSections;

		_ = orderedSections.Should().NotBeOfType<SettingsSection[]>("W8.1 IA must not expose raw mutable array");
		_ = (orderedSections is IList<SettingsSection>).Should().BeTrue("runtime view must support list semantics without mutation");

		IList<SettingsSection> mutableList = (IList<SettingsSection>)orderedSections;
		Action mutate = () => mutableList[0] = new SettingsSection("mutated", 1, "mutated", "mutated", "mutated", true);
		_ = mutate.Should().Throw<NotSupportedException>("W8.1 IA must be frozen/immutable to callers");
	}

	[Fact]
	public void SettingsWindow_W8_1ShellText_IsBoundToViewModel_NotHardcodedLiterals()
	{
		string settingsWindowPath = Path.Combine(FindRepositoryRoot(), "src", "BroChoos.App", "Settings", "SettingsWindow.axaml");
		string xaml = File.ReadAllText(settingsWindowPath);

		_ = xaml.Should().Contain("Title=\"{Binding WindowTitle}\"", "window title must come from VM/localization");
		_ = xaml.Should().Contain("Watermark=\"{Binding SearchWatermarkText}\"", "search watermark must come from VM/localization");
		_ = xaml.Should().Contain("Text=\"{Binding SearchExamplesText}\"", "search helper text must come from VM/localization");
		_ = xaml.Should().Contain("Text=\"{Binding SelectedSection.DisplayLabel}\"", "hero title must come from IA/VM binding");
		_ = xaml.Should().Contain("Text=\"{Binding DashboardStatusText}\"", "dashboard text must come from localized VM value");
		_ = xaml.Should().Contain("Content=\"{Binding CheckWindowsSettingsButtonText}\"", "button text must come from VM/localization");

		string[] forbiddenHardcoded =
		[
			"Title=\"BroChoos Settings\"",
			"Watermark=\"Search settings, aliases, and values\"",
			"Text=\"Search examples: incognito, UserChoice, trackers\"",
			"Text=\"Home\"",
			"Text=\"Status dashboard\"",
			"Content=\"Check Windows settings\""
		];

		foreach (string literal in forbiddenHardcoded)
		{
			_ = xaml.Should().NotContain(literal, $"W8.1 settings shell text literal must be replaced by binding/resource: {literal}");
		}
	}

	[Fact]
	public void SelectingSections_DoesNotHideAnySection_AndRejectsUnknownIds()
	{
		SettingsViewModel vm = new();
		int baselineCount = SettingsViewModel.OrderedSections.Count;

		foreach (SettingsSection section in SettingsViewModel.OrderedSections)
		{
			_ = vm.SelectSection(section.SectionId).Should().BeTrue();
			_ = vm.SelectedSection.Should().NotBeNull();
			_ = vm.SelectedSection!.SectionId.Should().Be(section.SectionId);
			_ = SettingsViewModel.OrderedSections.Count.Should().Be(baselineCount);
		}

		_ = vm.SelectSection("does-not-exist").Should().BeFalse();
		_ = SettingsViewModel.OrderedSections.Count.Should().Be(12);
	}

	[Fact]
	public void SearchAliases_ContainAuthorityExamples_AndMapToExpectedSections()
	{
		string aliasesPath = Path.Combine(FindRepositoryRoot(), "src", "BroChoos.App", "Settings", "search-aliases.json");
		_ = File.Exists(aliasesPath).Should().BeTrue();

		string json = File.ReadAllText(aliasesPath);
		Dictionary<string, string>? aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
		_ = aliases.Should().NotBeNull();
		_ = aliases!["incognito"].Should().Be("privacy-network");
		_ = aliases["инкогнито"].Should().Be("privacy-network");
		_ = aliases["userchoice"].Should().Be("what-brochoos-opens");
		_ = aliases["trackers"].Should().Be("privacy-network");
		_ = aliases["regex"].Should().Be("rules-suggestions");
	}

	// W8.1: the window RENDERS a search box, so the box must SEARCH. This drives the real
	// command the TextBox binds and asserts it selects the matching section via an alias,
	// and that an unmatched query selects nothing and says so instead of silently doing
	// nothing or jumping to a "closest" section.
	[Fact]
	public void Search_SelectsTheMatchingSectionByAlias_AndReportsNoMatchTruthfully()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			SettingsViewModel vm = new();

			// An alias from the shipped table, resolved through the loaded embedded resource.
			string? byAlias = vm.Search("incognito");
			_ = byAlias.Should().Be(SettingsViewModel.SectionIds.PrivacyNetwork);
			_ = vm.SelectedSection.SectionId.Should().Be(SettingsViewModel.SectionIds.PrivacyNetwork);
			_ = vm.SearchStatusText.Should().BeEmpty();

			// A query with no alias/label match selects nothing and reports that plainly.
			string? noMatch = vm.Search("zzzz-not-a-section");
			_ = noMatch.Should().BeNull();
			_ = vm.SelectedSection.SectionId.Should().Be(SettingsViewModel.SectionIds.PrivacyNetwork, "an unmatched search must not move the selection");
			_ = vm.SearchStatusText.Should().NotBeNullOrWhiteSpace("a no-match search must report the outcome, not fail silently");

			// Every search result must still be one of the 12 sections and must never remove
			// any section from the frozen list (no hiding).
			_ = SettingsViewModel.OrderedSections.Should().HaveCount(12);
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			string candidate = Path.Combine(directory.FullName, "BroChoos.sln");
			if (File.Exists(candidate))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new InvalidOperationException("Repository root with BroChoos.sln was not found.");
	}
}
