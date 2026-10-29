using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using BroChoos.App.Picker;
using BroChoos.App.Settings;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// The REAL windows, built on the shared Avalonia bootstrap (PickerXamlProbe): the first-run
// panel renders with its three buttons, in order, with screen-reader names, and takes
// initial focus; the shortcut panel is collapsed by default; the Settings window resolves
// its colours from the theme tokens (so Dark differs from Light) and names its search box
// and dropdown. Everything is run once and cached, like the other probes.
internal static class FirstRunAndSettingsProbe
{
	private static readonly Lock Gate = new();
	private static Result? _result;

	internal static Result Run()
	{
		lock (Gate)
		{
			return _result ??= Execute();
		}
	}

	private static Result Execute()
	{
		try
		{
			string? setup = PickerXamlProbe.Run().Failure;
			return setup is not null ? Result.Failed("Avalonia setup failed: " + setup) : Measure();
		}
		catch (Exception failure)
		{
			return Result.Failed($"{failure.GetType().FullName}: {failure.Message}");
		}
	}

	private static Result Measure()
	{
		PickerViewModel vm = new("https://example.com/", BrowserSnapshot.Empty);
		vm.ShowFirstRunPrompt();
		PickerWindow window = new(vm, _ => { }, _ => { });

		bool panelVisible;
		int[] tabOrder;
		string[] names;
		bool primaryFocused;
		bool primaryRing;
		bool shortcutsHiddenByDefault;
		bool shortcutsVisibleAfterToggle;
		bool compactHintTrims;
		bool addManuallyHidden;
		bool toggleCollapsedAtStart;
		bool focusMovedToShortcuts;
		bool toggleExpandedWhenOpen;
		bool openAnnounced;
		bool escClosedShortcuts;
		bool focusRestoredAfterClose;
		bool focusOnStatusAfterSkip;
		string statusAreaName;
		bool shortcutPanelIsControlGroup;

		try
		{
			window.ShowActivated = false;
			window.Topmost = false;
			window.Opacity = 0;
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			Border panel = window.FindControl<Border>("FirstRunPanel")!;
			Button find = window.FindControl<Button>("FindBrowsersButton")!;
			Button add = window.FindControl<Button>("AddManuallyButton")!;
			Button skip = window.FindControl<Button>("SkipButton")!;

			panelVisible = panel.IsEffectivelyVisible;
			tabOrder = [find.TabIndex, add.TabIndex, skip.TabIndex];
			names = [
				AutomationProperties.GetName(find) ?? string.Empty,
				AutomationProperties.GetName(add) ?? string.Empty,
				AutomationProperties.GetName(skip) ?? string.Empty,
			];

			window.FocusFirstRunPrimary();
			Dispatcher.UIThread.RunJobs();
			primaryFocused = ReferenceEquals(window.FocusManager?.GetFocusedElement(), find);
			primaryRing = find.Classes.Contains(":focus-visible");

			addManuallyHidden = !add.IsEffectivelyVisible;

			Border shortcuts = window.FindControl<Border>("ShortcutPanel")!;
			ExpandableToggleButton toggle = window.FindControl<ExpandableToggleButton>("ShortcutsToggleButton")!;
			IExpandCollapseProvider toggleState = (IExpandCollapseProvider)ControlAutomationPeer.CreatePeerForElement(toggle);
			shortcutsHiddenByDefault = !shortcuts.IsEffectivelyVisible;
			toggleCollapsedAtStart = toggleState.ExpandCollapseState == ExpandCollapseState.Collapsed;

			vm.ToggleDetails();
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			shortcutsVisibleAfterToggle = shortcuts.IsEffectivelyVisible;
			focusMovedToShortcuts = ReferenceEquals(window.FocusManager?.GetFocusedElement(), shortcuts);
			toggleExpandedWhenOpen = toggleState.ExpandCollapseState == ExpandCollapseState.Expanded;
			openAnnounced = vm.IsInfoStatusVisible && vm.StatusMessage.Length > 0;

			_ = vm.HandleEscape();
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			escClosedShortcuts = !shortcuts.IsEffectivelyVisible;
			focusRestoredAfterClose = ReferenceEquals(window.FocusManager?.GetFocusedElement(), find);

			// Skip, as the coordinator performs it: the panel collapses with focus on its button.
			window.FocusFirstRunPrimary();
			Dispatcher.UIThread.RunJobs();
			vm.ClearFirstRunPrompt();
			vm.ShowFirstRunSkipped();
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			StackPanel statusArea = window.FindControl<StackPanel>("StatusArea")!;
			focusOnStatusAfterSkip = ReferenceEquals(window.FocusManager?.GetFocusedElement(), statusArea);
			statusAreaName = AutomationProperties.GetName(statusArea) ?? string.Empty;
			shortcutPanelIsControlGroup = AutomationProperties.GetAccessibilityView(shortcuts) == AccessibilityView.Control
				&& AutomationProperties.GetControlTypeOverride(shortcuts) == AutomationControlType.Group;

			// The always-on key-hint line was removed (picker layout spec); the one remaining calm line
			// above the tiles is the link host, and it must trim, never wrap.
			TextBlock compact = window.FindControl<TextBlock>("LinkHostLine")!;
			compactHintTrims = compact.TextWrapping == TextWrapping.NoWrap && compact.TextTrimming != TextTrimming.None;
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}

		SettingsWindow settings = new(new SettingsViewModel());
		string searchName = AutomationProperties.GetName(settings.FindControl<TextBox>("SettingsSearchBox")!) ?? string.Empty;
		string depthName = AutomationProperties.GetName(settings.FindControl<ComboBox>("ControlDepthEditor")!) ?? string.Empty;
		string? lightBg = ResolveColor(settings, "bc.bg.base", ThemeVariant.Light);
		string? darkBg = ResolveColor(settings, "bc.bg.base", ThemeVariant.Dark);
		bool hcResolves = settings.TryGetResource("bc.bg.base", PickerThemeVariants.HighContrast, out object? hc) && hc is not null;

		return new Result(
			null,
			panelVisible,
			tabOrder,
			names,
			primaryFocused,
			primaryRing,
			shortcutsHiddenByDefault,
			shortcutsVisibleAfterToggle,
			compactHintTrims,
			searchName,
			depthName,
			lightBg,
			darkBg,
			hcResolves,
			new ShortcutA11y(
				addManuallyHidden,
				toggleCollapsedAtStart,
				focusMovedToShortcuts,
				toggleExpandedWhenOpen,
				openAnnounced,
				escClosedShortcuts,
				focusRestoredAfterClose,
				focusOnStatusAfterSkip,
				statusAreaName,
				shortcutPanelIsControlGroup));
	}

	internal sealed record ShortcutA11y(
		bool AddManuallyHidden,
		bool ToggleCollapsedAtStart,
		bool FocusMovedToShortcuts,
		bool ToggleExpandedWhenOpen,
		bool OpenAnnounced,
		bool EscClosedShortcuts,
		bool FocusRestoredAfterClose,
		bool FocusOnStatusAfterSkip,
		string StatusAreaName,
		bool ShortcutPanelIsControlGroup);

	private static string? ResolveColor(Control control, string key, ThemeVariant variant)
	{
		return control.TryGetResource(key, variant, out object? value) ? value?.ToString() : null;
	}

	internal sealed record Result(
		string? Failure,
		bool PanelVisible,
		IReadOnlyList<int> TabOrder,
		IReadOnlyList<string> ButtonNames,
		bool PrimaryFocused,
		bool PrimaryRing,
		bool ShortcutsHiddenByDefault,
		bool ShortcutsVisibleAfterToggle,
		bool CompactHintTrims,
		string SearchName,
		string DepthName,
		string? LightBg,
		string? DarkBg,
		bool HighContrastResolves,
		ShortcutA11y Shortcuts)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, false, [], [], false, false, false, false, false, string.Empty, string.Empty, null, null, false,
				new ShortcutA11y(false, false, false, false, false, false, false, false, string.Empty, false));
		}
	}
}

[Collection(AvaloniaUiCollection.Name)]
public sealed partial class FirstRunWindowTests
{
	[System.Text.RegularExpressions.GeneratedRegex("\"#[0-9A-Fa-f]{6,8}\"")]
	private static partial System.Text.RegularExpressions.Regex LiteralColour();

	[Fact]
	public void FirstRunPanel_ShowsThreeNamedButtons_InPrimaryFirstTabOrder()
	{
		FirstRunAndSettingsProbe.Result result = FirstRunAndSettingsProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.PanelVisible.Should().BeTrue("with no browsers known the window must show the first-run screen");
		_ = result.TabOrder.Should().Equal(0, 1, 2);
		_ = result.ButtonNames.Should().OnlyContain(name => name.Length > 0, "every first-run button needs a screen-reader name");
		_ = result.ButtonNames.Should().OnlyHaveUniqueItems();
	}

	[Fact]
	public void FirstRunPanel_HidesAddManually_WhenNoFilePickerIsComposed()
	{
		FirstRunAndSettingsProbe.Result result = FirstRunAndSettingsProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.Shortcuts.AddManuallyHidden.Should().BeTrue("a button that can only fail must not be offered");
	}

	[Fact]
	public void ShortcutList_IsReachableByScreenReaders_AndEscClosesItFirst()
	{
		FirstRunAndSettingsProbe.ShortcutA11y a11y = FirstRunAndSettingsProbe.Run().Shortcuts;

		_ = a11y.ToggleCollapsedAtStart.Should().BeTrue("the toggle exposes Collapsed while the list is closed");
		_ = a11y.FocusMovedToShortcuts.Should().BeTrue("opening the list moves focus into it");
		_ = a11y.ToggleExpandedWhenOpen.Should().BeTrue("the toggle exposes Expanded while the list is open");
		_ = a11y.OpenAnnounced.Should().BeTrue("opening is announced on the polite status line");
		_ = a11y.EscClosedShortcuts.Should().BeTrue("Esc closes the list before it dismisses the picker");
		_ = a11y.FocusRestoredAfterClose.Should().BeTrue("closing returns focus to where it was");
		_ = a11y.ShortcutPanelIsControlGroup.Should().BeTrue("the panel is in the Control view as a Group, so focus on it is read");
	}

	[Fact]
	public void Skip_MovesFocusToTheStatusLine_InsteadOfLeavingItOnAHiddenButton()
	{
		FirstRunAndSettingsProbe.ShortcutA11y a11y = FirstRunAndSettingsProbe.Run().Shortcuts;

		_ = a11y.FocusOnStatusAfterSkip.Should().BeTrue();
		_ = a11y.StatusAreaName.Should().NotBeNullOrWhiteSpace("the status area is read by its current message");
	}

	[Fact]
	public void FirstRunPanel_FindBrowsers_TakesInitialFocus_WithAVisibleRing()
	{
		FirstRunAndSettingsProbe.Result result = FirstRunAndSettingsProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.PrimaryFocused.Should().BeTrue();
		_ = result.PrimaryRing.Should().BeTrue("keyboard users must see where focus is");
	}

	[Fact]
	public void ShortcutHints_AreCompactByDefault_AndTheFullListToggles()
	{
		FirstRunAndSettingsProbe.Result result = FirstRunAndSettingsProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.CompactHintTrims.Should().BeTrue("the link line must trim on one line, never wrap");
		_ = result.ShortcutsHiddenByDefault.Should().BeTrue();
		_ = result.ShortcutsVisibleAfterToggle.Should().BeTrue();
	}

	[Fact]
	public void SettingsWindow_FollowsTheThemeTokens_AndNamesItsInputs()
	{
		FirstRunAndSettingsProbe.Result result = FirstRunAndSettingsProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.SearchName.Should().NotBeNullOrWhiteSpace();
		_ = result.DepthName.Should().NotBeNullOrWhiteSpace();
		_ = result.LightBg.Should().NotBeNull();
		_ = result.DarkBg.Should().NotBeNull();
		_ = result.DarkBg.Should().NotBe(result.LightBg, "dark mode must resolve a different surface colour");
		_ = result.HighContrastResolves.Should().BeTrue();
	}

	[Fact]
	public void SettingsWindow_HasNoLiteralColours()
	{
		string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "BroChoos.App", "Settings", "SettingsWindow.axaml"));

		_ = LiteralColour().IsMatch(xaml).Should().BeFalse(
			"every colour must come from the theme tokens so dark mode and high contrast apply");
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BroChoos.sln")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
	}
}
