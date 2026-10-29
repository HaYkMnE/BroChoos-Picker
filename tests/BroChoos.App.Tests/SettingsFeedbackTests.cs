using System.Globalization;
using BroChoos.App.Localization;
using BroChoos.App.Settings;
using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// Feedback the Settings window owes the user when something did not happen:
//   - a refused save must make the ComboBox show the value that is actually stored;
//   - a confirmed action this build cannot perform must say so;
//   - a view model asked for a language must use it without touching the process-wide culture.
// Reads localized text, so it joins the culture-serializing collection.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class SettingsFeedbackTests
{
	// A store that refuses every save, as a stale generation or a corrupt file would.
	private sealed class RefusingSettingsStore : ISettingsStore
	{
		public SettingsDocument LoadOrDefault()
		{
			return new SettingsDocument();
		}

		public ExpectedDocumentGeneration ObserveExpectedGeneration()
		{
			return ExpectedDocumentGeneration.Absent;
		}

		public MutationCommitResult Save(SettingsDocument document, ExpectedDocumentGeneration expectedGeneration)
		{
			return MutationCommitResult.Conflict(observedGeneration: null);
		}
	}

	private static List<string?> CollectChanges(SettingsViewModel vm)
	{
		List<string?> changes = [];
		vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
		return changes;
	}

	// --- Finding 1: a refused save must pull the ComboBox back to the stored value ---

	[Fact]
	public void RefusedControlDepthSave_NotifiesTheComboBoxSelection_AndKeepsTheStoredValue()
	{
		SettingsViewModel vm = new(new RefusingSettingsStore());
		List<string?> changes = CollectChanges(vm);

		vm.SelectedControlDepthIndex = SettingsViewModel.ControlDepthChoices.ToList().IndexOf(SettingsControlDepth.Expert);

		_ = vm.ControlDepth.Should().Be(SettingsControlDepth.Standard, "the refused save left the stored depth unchanged");
		_ = vm.ControlDepthSaveStatusText.Should().Be(LocalizationService.GetString("Settings.Depth.NotSaved"));
		_ = changes.Should().Contain(nameof(SettingsViewModel.SelectedControlDepthIndex),
			"without this the ComboBox keeps showing the refused choice");
		_ = vm.SelectedControlDepthIndex.Should().Be(0, "the index read back is the actual depth, Standard");
	}

	[Fact]
	public void RefusedPickerLayoutSave_NotifiesTheComboBoxSelection_AndKeepsTheStoredValue()
	{
		SettingsViewModel vm = new(new RefusingSettingsStore());
		List<string?> changes = CollectChanges(vm);

		vm.SelectedPickerLayoutIndex = 1;

		_ = vm.PickerLayout.Should().Be(SettingsPickerLayout.Horizontal);
		_ = vm.PickerLayoutSaveStatusText.Should().Be(LocalizationService.GetString("Settings.Depth.NotSaved"));
		_ = changes.Should().Contain(nameof(SettingsViewModel.SelectedPickerLayoutIndex));
		_ = vm.SelectedPickerLayoutIndex.Should().Be(0);
	}

	// --- Finding 2: a confirmed action that cannot run must say so ---

	[Fact]
	public void ConfirmingAnAction_ExposesTheUnavailableReason_AsObservableText()
	{
		SettingsViewModel vm = new();
		List<string?> changes = CollectChanges(vm);
		_ = vm.ConsequentialActionResultText.Should().BeEmpty("nothing was confirmed yet");

		_ = vm.RequestConsequentialAction(SettingsViewModel.ConsequentialActionIds.ExportSettings).Should().BeTrue();
		_ = vm.ConfirmConsequentialActionCommand.CanExecute(null).Should().BeTrue();
		vm.ConfirmConsequentialActionCommand.Execute(null);

		_ = vm.ConsequentialActionResultText.Should().Be(LocalizationService.GetString("Settings.Consequential.Unavailable"));
		_ = changes.Should().Contain(nameof(SettingsViewModel.ConsequentialActionResultText));
		_ = vm.IsConsequentialConfirmationPending.Should().BeFalse("the confirmation closes once answered");
	}

	[Fact]
	public void RequestingAnotherAction_ClearsTheOldResult()
	{
		SettingsViewModel vm = new();
		_ = vm.RequestConsequentialAction(SettingsViewModel.ConsequentialActionIds.ExportSettings);
		vm.ConfirmConsequentialActionCommand.Execute(null);
		_ = vm.ConsequentialActionResultText.Should().NotBeEmpty();

		_ = vm.RequestConsequentialAction(SettingsViewModel.ConsequentialActionIds.CheckWindowsSettings);

		_ = vm.ConsequentialActionResultText.Should().BeEmpty("a stale outcome must not sit next to a new question");
	}

	[Fact]
	public void CancellingAnAction_ShowsNoResult()
	{
		SettingsViewModel vm = new();
		_ = vm.RequestConsequentialAction(SettingsViewModel.ConsequentialActionIds.ExportSettings);

		vm.CancelConsequentialActionCommand.Execute(null);

		_ = vm.ConsequentialActionResultText.Should().BeEmpty("cancelling is not an unavailable action");
	}

	[Fact]
	public void SettingsWindow_ShowsTheActionResult_InAVisibleLiveRegion()
	{
		string xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "BroChoos.App", "Settings", "SettingsWindow.axaml"));

		_ = xaml.Should().Contain("Text=\"{Binding ConsequentialActionResultText}\"");
		int start = xaml.IndexOf("Text=\"{Binding ConsequentialActionResultText}\"", StringComparison.Ordinal);
		string element = xaml[start..xaml.IndexOf("/>", start, StringComparison.Ordinal)];
		_ = element.Should().Contain("AutomationProperties.LiveSetting=\"Polite\"");
		_ = element.Should().Contain("IsVisible=\"{Binding ConsequentialActionResultText");
	}

	// --- Finding 4: a requested culture is used directly ---

	[Fact]
	public void RequestedCulture_IsUsedByTheViewModel_WithoutChangingTheProcessCulture()
	{
		CultureInfo before = LocalizationService.CurrentCulture;
		CultureInfo ru = CultureInfo.GetCultureInfo("ru");
		CultureInfo en = CultureInfo.GetCultureInfo("en");

		SettingsViewModel ruVm = new(culture: ru);
		SettingsViewModel enVm = new(culture: en);

		_ = LocalizationService.CurrentCulture.Should().BeSameAs(before, "the view model must not switch the process-wide culture");
		_ = ruVm.WindowTitle.Should().Be(LocalizationService.GetStringFor("Settings.Window.Title", ru));
		_ = enVm.WindowTitle.Should().Be(LocalizationService.GetStringFor("Settings.Window.Title", en));
		_ = ruVm.WindowTitle.Should().NotBe(enVm.WindowTitle);
		_ = ruVm.DashboardStatusText.Should().Be(LocalizationService.GetStringFor("Settings.Dashboard.Status", ru));
		_ = ruVm.ControlDepthChoiceTexts[0].Should().Be(LocalizationService.GetStringFor("Settings.Depth.Standard", ru));
		_ = ruVm.PickerLayoutChoiceTexts[1].Should().Be(LocalizationService.GetStringFor("Settings.Appearance.PickerLayout.Vertical", ru));
	}

	[Fact]
	public void RequestedCulture_AlsoCoversTextsResolvedAfterConstruction()
	{
		CultureInfo ru = CultureInfo.GetCultureInfo("ru");
		SettingsViewModel vm = new(new RefusingSettingsStore(), culture: ru) { SelectedControlDepthIndex = 2 };

		_ = vm.ControlDepthSaveStatusText.Should().Be(LocalizationService.GetStringFor("Settings.Depth.NotSaved", ru));
	}

	// The section table (nav labels, descriptions, search, SelectSection) used to be built once for the
	// whole process, so a view model asked for another language still showed the process language there.
	[Theory]
	[InlineData("ru")]
	[InlineData("hy")]
	public void RequestedCulture_CoversNavLabelsSectionDescriptionsAndSearch(string language)
	{
		CultureInfo before = LocalizationService.CurrentCulture;
		CultureInfo requested = CultureInfo.GetCultureInfo(language);
		SettingsViewModel vm = new(culture: requested);

		string[] navTexts =
		[
			vm.NavHomeText, vm.NavControlDepthText, vm.NavBrowsersProfilesText, vm.NavWhatBroChoosOpensText,
			vm.NavRulesSuggestionsText, vm.NavPrivacyNetworkText, vm.NavAppearanceText, vm.NavHotkeysText,
			vm.NavLanguageText, vm.NavUpdatesText, vm.NavMaintenanceText, vm.NavAdvancedText,
		];
		string[] labelKeys =
		[
			"Settings.Section.Home", "Settings.Section.ControlDepth", "Settings.Section.BrowsersProfiles",
			"Settings.Section.WhatBroChoosOpens", "Settings.Section.RulesSuggestions", "Settings.Section.PrivacyNetwork",
			"Settings.Section.Appearance", "Settings.Section.Hotkeys", "Settings.Section.Language",
			"Settings.Section.Updates", "Settings.Section.Maintenance", "Settings.Section.Advanced",
		];
		for (int index = 0; index < labelKeys.Length; index++)
		{
			string label = LocalizationService.GetStringFor(labelKeys[index], requested);
			_ = label.Should().NotBe(labelKeys[index], "the key must exist in the pack");
			_ = navTexts[index].Should().Be($"{index + 1} {label}", $"nav button {index + 1} must be in {language}");
		}

		_ = vm.SelectedSectionDescription.Should().Be(LocalizationService.GetStringFor("Settings.Section.Home.Description", requested));
		_ = vm.SelectSection(SettingsViewModel.SectionIds.PrivacyNetwork).Should().BeTrue();
		_ = vm.SelectedSectionDescription.Should().Be(LocalizationService.GetStringFor("Settings.Section.PrivacyNetwork.Description", requested));
		_ = vm.SelectedSection.DisplayLabel.Should().Be(LocalizationService.GetStringFor("Settings.Section.PrivacyNetwork", requested));

		// Search matches the labels the user SEES, so a label in the requested language finds its section.
		_ = vm.SelectSection(SettingsViewModel.SectionIds.Home).Should().BeTrue();
		string? found = vm.Search(LocalizationService.GetStringFor("Settings.Section.Appearance", requested));
		_ = found.Should().Be(SettingsViewModel.SectionIds.Appearance);
		_ = vm.SelectedSection.SectionId.Should().Be(SettingsViewModel.SectionIds.Appearance);

		_ = LocalizationService.CurrentCulture.Should().BeSameAs(before, "asking for a language must not switch the process culture");
	}

	[Fact]
	public void TwoViewModels_InDifferentCultures_DoNotShareSectionTexts()
	{
		SettingsViewModel ru = new(culture: CultureInfo.GetCultureInfo("ru"));
		SettingsViewModel hy = new(culture: CultureInfo.GetCultureInfo("hy"));

		_ = ru.NavHomeText.Should().NotBe(hy.NavHomeText);
		_ = ru.SelectedSectionDescription.Should().NotBe(hy.SelectedSectionDescription);
		_ = ru.SelectedSection.SectionId.Should().Be(hy.SelectedSection.SectionId, "ids and order are language-neutral");
	}

	[Fact]
	public void NoRequestedCulture_KeepsTheCurrentCultureBehaviour()
	{
		SettingsViewModel vm = new();

		_ = vm.WindowTitle.Should().Be(LocalizationService.GetString("Settings.Window.Title"));
	}

	private static string RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "BroChoos.sln")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new InvalidOperationException("BroChoos.sln not found above " + AppContext.BaseDirectory);
	}
}
