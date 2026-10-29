using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BroChoos.App.Settings;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// W8.1: THE TEST THAT PROVES THE SETTINGS SHELL IS ACTUALLY WIRED, not merely present.
//
// The audit finding this file answers was precise: the frozen 12-section sidebar rendered
// <Button>s that bound Content only, `SelectSection` was a plain method, and the content
// area could not have reacted even if the buttons had been wired. A source-text assertion
// (which SettingsInformationArchitectureTests already makes) cannot tell "this command
// exists" from "pressing this button changes the section", so this file runs the REAL
// window: it constructs SettingsWindow, finds the real nav buttons, and drives the same
// ICommand the button binds.
//
// BOOTSTRAP IS SHARED, DELIBERATELY. An Avalonia Application and its Dispatcher are
// process-wide singletons and the dispatcher is THREAD-AFFINE; a second bootstrap from
// another test class would fail with "Call from invalid thread" rather than start a second
// UI thread (MEASURED, and recorded in PickerXamlLoadTests). So this probe reuses
// PickerXamlProbe's bootstrap instead of configuring its own.
internal static class SettingsShellProbe
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
			_ = PickerXamlProbe.Run();
		}
		catch (Exception failure)
		{
			return Result.Failed($"Avalonia setup failed: {failure.GetType().FullName}: {failure.Message}");
		}

		try
		{
			return Measure();
		}
		catch (Exception failure)
		{
			return Result.Failed($"{failure.GetType().FullName}: {failure.Message}");
		}
	}

	private static Result Measure()
	{
		// F-B: a REAL store on a temp root, so this probe can prove the editor actually
		// PERSISTS, not merely that a control exists. (It used to construct the view model
		// with a null store, which made the "saves on selection" test vacuous.)
		string dataRoot = Path.Combine(Path.GetTempPath(), "brochoos-settings-window-" + Guid.NewGuid().ToString("N")[..12]);
		_ = Directory.CreateDirectory(Path.Combine(dataRoot, "backups"));
		SettingsStore store = new(
			new FixedStorageRoot(dataRoot),
			new ConfigFileObserver(new SystemStorageReadFileSystem()),
			new GenerationMutationCoordinator(new SystemStorageReadFileSystem()));

		SettingsViewModel viewModel = new(store);
		SettingsWindow window = new(viewModel)
		{
			// The visual tree is only realized once the window is shown and laid out, so the
			// window is shown OFFSCREEN (the same technique PickerFocusProbe uses): opacity 0,
			// far off the virtual desktop, no activation. Nothing is ever visible to a user.
			ShowActivated = false,
			Topmost = false,
			Opacity = 0,
			Position = new PixelPoint(-32000, -32000),
		};
		window.Show();
		Dispatcher.UIThread.RunJobs();

		// The window must actually be the settings shell, not a bare Window that happens to
		// construct: the initial section is Home and its dashboard branch is visible.
		string initialSection = viewModel.SelectedSection.SectionId;
		bool homeVisibleAtStart = viewModel.IsHomeSelected;

		// Every sidebar nav button must bind the NAV COMMAND, not just Content. This reads
		// the realized visual tree, so it fails if the XAML regresses to Content-only.
		int navButtonsWithNavCommand = 0;
		int navButtonsTotal = 0;
		bool hasBrowsersButton = false;
		bool hasPrivacyButton = false;
		HashSet<string> coveredSectionIds = new(StringComparer.Ordinal);

		foreach (Button button in window.GetVisualDescendants().OfType<Button>())
		{
			if (button.Command is not null && button.CommandParameter is string parameter)
			{
				navButtonsTotal++;
				if (ReferenceEquals(button.Command, viewModel.SelectSectionCommand))
				{
					navButtonsWithNavCommand++;
					_ = coveredSectionIds.Add(parameter);
				}

				if (string.Equals(parameter, SettingsViewModel.SectionIds.BrowsersProfiles, StringComparison.Ordinal))
				{
					hasBrowsersButton = true;
				}

				if (string.Equals(parameter, SettingsViewModel.SectionIds.PrivacyNetwork, StringComparison.Ordinal))
				{
					hasPrivacyButton = true;
				}
			}
		}

		// Every frozen section id must be reachable from a real control, not just present in
		// the section list: a section with no way to select it is the audit's "shell as data".
		string[] missingSectionIds = [.. SettingsViewModel.OrderedSections
			.Select(section => section.SectionId)
			.Where(id => !coveredSectionIds.Contains(id))];

		// Drive the section change through the SAME command the sidebar button holds. This
		// is the behaviour the audit said did not exist.
		bool privacyExecuted = viewModel.SelectSectionCommand.CanExecute(SettingsViewModel.SectionIds.PrivacyNetwork);
		viewModel.SelectSectionCommand.Execute(SettingsViewModel.SectionIds.PrivacyNetwork);
		Dispatcher.UIThread.RunJobs();

		string sectionAfterNav = viewModel.SelectedSection.SectionId;
		bool privacyVisibleAfterNav = viewModel.IsPrivacyNetworkSelected;
		bool homeHiddenAfterNav = !viewModel.IsHomeSelected;

		// A consequential action must arm an ACTION-SPECIFIC confirmation, not a generic one.
		bool confirmationRequested = viewModel.RequestConsequentialAction(SettingsViewModel.ConsequentialActionIds.ExportSettings);
		string confirmationMessage = viewModel.ConfirmationMessage ?? string.Empty;
		bool confirmationPending = viewModel.IsConsequentialConfirmationPending;
		viewModel.CancelConsequentialAction();
		bool confirmationClearedAfterCancel = !viewModel.IsConsequentialConfirmationPending;

		// The search box the window renders must be wired to the view model's search, not a
		// decorative TextBox: type the alias, run the command the box binds, and the section
		// changes. A TextBox with no KeyBinding and no working command is the fake affordance
		// the plan's negative clause forbids.
		IReadOnlyList<TextBox> searchBoxes = [.. window.GetVisualDescendants().OfType<TextBox>()];
		TextBox? searchBox = searchBoxes.Count > 0 ? searchBoxes[0] : null;
		searchBox?.SetCurrentValue(TextBox.TextProperty, "incognito");

		bool hasSearchBox = searchBoxes.Count > 0;
		bool searchBoxBoundToViewModel = searchBox?.DataContext is SettingsViewModel;
		bool searchBoxHasCommandBinding = searchBox?.KeyBindings.Count > 0;

		viewModel.SearchCommand.Execute(viewModel.SearchQuery);
		Dispatcher.UIThread.RunJobs();

		string sectionAfterSearch = viewModel.SelectedSection.SectionId;
		string searchStatusAfterSearch = viewModel.SearchStatusText;

		// F2: the CONTROL-DEPTH section must offer a REAL editor, not just a label. Switch to
		// the section, find the ComboBox, and drive it the way a user would (select a value),
		// proving the control is bound to the save path rather than decorative.
		_ = viewModel.SelectSection(SettingsViewModel.SectionIds.ControlDepth);
		Dispatcher.UIThread.RunJobs();

		IReadOnlyList<ComboBox> comboBoxes = [.. window.GetVisualDescendants().OfType<ComboBox>()];
		ComboBox? depthEditor = comboBoxes.Count > 0 ? comboBoxes[0] : null;
		bool hasDepthEditor = comboBoxes.Count > 0;
		bool depthEditorBoundToViewModel = depthEditor?.DataContext is SettingsViewModel;
		int depthEditorItemCount = depthEditor?.ItemCount ?? 0;

		int expertIndex = 0;
		for (int index = 0; index < SettingsViewModel.ControlDepthChoices.Count; index++)
		{
			if (SettingsViewModel.ControlDepthChoices[index] == SettingsControlDepth.Expert)
			{
				expertIndex = index;
			}
		}

		depthEditor?.SetCurrentValue(SelectingItemsControl.SelectedIndexProperty, expertIndex);

		Dispatcher.UIThread.RunJobs();

		bool depthEditorChangedSelection = viewModel.ControlDepth == SettingsControlDepth.Expert;
		string depthSaveStatus = viewModel.ControlDepthSaveStatusText;

		// F-B: read the value back from DISK, through a fresh observation, so "saves on
		// selection" is a persistence claim and not just a property change.
		string settingsJsonPath = Path.Combine(dataRoot, "settings.json");
		bool settingsFileCreated = File.Exists(settingsJsonPath);
		SettingsDocument reloaded = store.LoadOrDefault();
		bool depthPersistedToDisk = reloaded.ControlDepth == SettingsControlDepth.Expert
			&& reloaded.Generation == 1;

		window.Close();
		Dispatcher.UIThread.RunJobs();

		DeleteQuietly(dataRoot);

		return new Result(
			Failure: null,
			InitialSection: initialSection,
			HomeVisibleAtStart: homeVisibleAtStart,
			NavButtonsTotal: navButtonsTotal,
			NavButtonsWithNavCommand: navButtonsWithNavCommand,
			HasBrowsersButton: hasBrowsersButton,
			HasPrivacyButton: hasPrivacyButton,
			MissingSectionIds: missingSectionIds,
			PrivacyCanExecute: privacyExecuted,
			SectionAfterNav: sectionAfterNav,
			PrivacyVisibleAfterNav: privacyVisibleAfterNav,
			HomeHiddenAfterNav: homeHiddenAfterNav,
			ConfirmationRequested: confirmationRequested,
			ConfirmationMessage: confirmationMessage,
			ConfirmationPending: confirmationPending,
			ConfirmationClearedAfterCancel: confirmationClearedAfterCancel,
			HasSearchBox: hasSearchBox,
			SearchBoxBoundToViewModel: searchBoxBoundToViewModel,
			SearchBoxHasCommandBinding: searchBoxHasCommandBinding,
			SectionAfterSearch: sectionAfterSearch,
			SearchStatusAfterSearch: searchStatusAfterSearch,
			HasDepthEditor: hasDepthEditor,
			DepthEditorBoundToViewModel: depthEditorBoundToViewModel,
			DepthEditorItemCount: depthEditorItemCount,
			DepthEditorChangedSelection: depthEditorChangedSelection,
			DepthSaveStatus: depthSaveStatus,
			SettingsFileCreated: settingsFileCreated,
			DepthPersistedToDisk: depthPersistedToDisk);
	}

	private static void DeleteQuietly(string root)
	{
		try
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	// A minimal root resolver over a temp directory, matching the pattern the storage test
	// projects use. The real SettingsStoreFactory resolves the user's data root, which a test
	// must not touch, so the probe composes the same three storage types over a temp root.
	private sealed class FixedStorageRoot(string root) : IStorageRootResolver
	{
		public string Root { get; } = root;
		public string Backups { get; } = Path.Combine(root, "backups");
		public string Logs { get; } = Path.Combine(root, "logs");
		public string Temp { get; } = Path.Combine(root, "temp");
	}

	internal sealed record Result(
		string? Failure,
		string InitialSection,
		bool HomeVisibleAtStart,
		int NavButtonsTotal,
		int NavButtonsWithNavCommand,
		bool HasBrowsersButton,
		bool HasPrivacyButton,
		IReadOnlyList<string> MissingSectionIds,
		bool PrivacyCanExecute,
		string SectionAfterNav,
		bool PrivacyVisibleAfterNav,
		bool HomeHiddenAfterNav,
		bool ConfirmationRequested,
		string ConfirmationMessage,
		bool ConfirmationPending,
		bool ConfirmationClearedAfterCancel,
		bool HasSearchBox,
		bool SearchBoxBoundToViewModel,
		bool SearchBoxHasCommandBinding,
		string SectionAfterSearch,
		string SearchStatusAfterSearch,
		bool HasDepthEditor,
		bool DepthEditorBoundToViewModel,
		int DepthEditorItemCount,
		bool DepthEditorChangedSelection,
		string DepthSaveStatus,
		bool SettingsFileCreated,
		bool DepthPersistedToDisk)
	{
		internal static Result Failed(string failure)
		{
			return new Result(
				failure,
				InitialSection: string.Empty,
				HomeVisibleAtStart: false,
				NavButtonsTotal: 0,
				NavButtonsWithNavCommand: 0,
				HasBrowsersButton: false,
				HasPrivacyButton: false,
				MissingSectionIds: [],
				PrivacyCanExecute: false,
				SectionAfterNav: string.Empty,
				PrivacyVisibleAfterNav: false,
				HomeHiddenAfterNav: false,
				ConfirmationRequested: false,
				ConfirmationMessage: string.Empty,
				ConfirmationPending: false,
				ConfirmationClearedAfterCancel: false,
				HasSearchBox: false,
				SearchBoxBoundToViewModel: false,
				SearchBoxHasCommandBinding: false,
				SectionAfterSearch: string.Empty,
				SearchStatusAfterSearch: string.Empty,
				HasDepthEditor: false,
				DepthEditorBoundToViewModel: false,
				DepthEditorItemCount: 0,
				DepthEditorChangedSelection: false,
				DepthSaveStatus: string.Empty,
				SettingsFileCreated: false,
				DepthPersistedToDisk: false);
		}
	}
}

[Collection(AvaloniaUiCollection.Name)]
public sealed class SettingsWindowLoadTests
{
	[Fact]
	public void SettingsWindow_Constructs_AndOpensOnTheHomeDashboard()
	{
		SettingsShellProbe.Result result = SettingsShellProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.InitialSection.Should().Be(SettingsViewModel.SectionIds.Home);
		_ = result.HomeVisibleAtStart.Should().BeTrue();
	}

	[Fact]
	public void EverySidebarNavButton_BindsTheNavCommand_NotContentAlone()
	{
		SettingsShellProbe.Result result = SettingsShellProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.MissingSectionIds.Should().BeEmpty("every frozen section id must be reachable from a real control");
		_ = result.NavButtonsTotal.Should().BeGreaterThanOrEqualTo(12, "each of the 12 frozen sections has a nav button carrying its id");
		_ = result.NavButtonsWithNavCommand.Should().Be(result.NavButtonsTotal, "every command-bearing button must bind SelectSectionCommand, not Content alone");
		_ = result.HasBrowsersButton.Should().BeTrue();
		_ = result.HasPrivacyButton.Should().BeTrue();
	}

	// THE AUDIT'S CENTRAL CLAIM, REVERSED AND PINNED: pressing a nav button changes which
	// section the content area shows.
	[Fact]
	public void NavCommand_ChangesTheVisibleSection()
	{
		SettingsShellProbe.Result result = SettingsShellProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.PrivacyCanExecute.Should().BeTrue();
		_ = result.SectionAfterNav.Should().Be(SettingsViewModel.SectionIds.PrivacyNetwork);
		_ = result.PrivacyVisibleAfterNav.Should().BeTrue("the Privacy content branch must become visible");
		_ = result.HomeHiddenAfterNav.Should().BeTrue("the Home content branch must stop being visible");
	}

	[Fact]
	public void ConsequentialAction_RequiresAnActionSpecificConfirmation()
	{
		SettingsShellProbe.Result result = SettingsShellProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.ConfirmationRequested.Should().BeTrue();
		_ = result.ConfirmationPending.Should().BeTrue();
		_ = result.ConfirmationMessage.Should().NotBeNullOrWhiteSpace("a named action carries its own consequence sentence");
		_ = result.ConfirmationMessage.Should().NotBe("Are you sure?", "the plan's negative clause forbids a generic confirmation");
		_ = result.ConfirmationClearedAfterCancel.Should().BeTrue();
	}

	// The search box the window RENDERS must be a real, wired control: a TextBox bound to
	// the view model with a command binding, whose query selects the matching section. An
	// unwired search box is the fake affordance the plan's negative clause forbids.
	[Fact]
	public void SearchBox_IsBoundAndSelectsTheMatchingSection()
	{
		SettingsShellProbe.Result result = SettingsShellProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.HasSearchBox.Should().BeTrue("the shell renders a search box");
		_ = result.SearchBoxBoundToViewModel.Should().BeTrue("the search box must be bound to the view model");
		_ = result.SearchBoxHasCommandBinding.Should().BeTrue("the search box must bind a command (Enter), not just hold text");
		_ = result.SectionAfterSearch.Should().Be(SettingsViewModel.SectionIds.PrivacyNetwork, "searching the alias 'incognito' selects Privacy & network");
		_ = result.SearchStatusAfterSearch.Should().BeEmpty("a successful search reports no error");
	}

	// F2 + F-B: the CONTROL-DEPTH section must offer a REAL editor, and selecting a value
	// must actually PERSIST it (this probe uses a real store on a temp root, so the claim is
	// persistence, not merely a property change).
	[Fact]
	public void ControlDepthSection_RendersAWiredEditor_ThatSavesOnSelection()
	{
		SettingsShellProbe.Result result = SettingsShellProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.HasDepthEditor.Should().BeTrue("the Control-depth section must render an editor, not just a label");
		_ = result.DepthEditorBoundToViewModel.Should().BeTrue("the editor must be bound to the view model");
		_ = result.DepthEditorItemCount.Should().Be(3, "the editor offers exactly the three control depths");
		_ = result.DepthEditorChangedSelection.Should().BeTrue("selecting a value must change the preference through the model");
		_ = result.SettingsFileCreated.Should().BeTrue("a real save must create settings.json");
		_ = result.DepthPersistedToDisk.Should().BeTrue("the selected depth must round-trip to disk at generation 1");
		_ = result.DepthSaveStatus.Should().NotBeNullOrWhiteSpace("the editor must state truthfully whether the change was saved");
	}
}
