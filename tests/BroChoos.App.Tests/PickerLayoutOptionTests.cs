using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BroChoos.App.Localization;
using BroChoos.App.Picker;
using BroChoos.App.Settings;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Routing;
using BroChoos.Core.Snapshots;
using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// Phase 3 of the picker layout spec: the Horizontal/Vertical setting, layout-aware arrows, and
// the profile line on a tile.
[Collection(AvaloniaUiCollection.Name)]
public sealed class PickerLayoutOptionTests
{
	[Theory]
	[InlineData(false, Key.Right, true)]
	[InlineData(false, Key.Left, false)]
	[InlineData(true, Key.Down, true)]
	[InlineData(true, Key.Up, false)]
	public void Arrows_AlongTheLayout_MoveFocus(bool vertical, Key key, bool next)
	{
		PickerKey expected = next ? PickerKey.FocusNext : PickerKey.FocusPrevious;
		_ = PickerWindow.MapKey(key, KeyModifiers.None, vertical).Should().Be(new PickerKeyAction(expected, 0));
		_ = PickerWindow.IsOffAxisArrow(key, KeyModifiers.None, vertical).Should().BeFalse();
	}

	[Theory]
	[InlineData(false, Key.Up)]
	[InlineData(false, Key.Down)]
	[InlineData(true, Key.Left)]
	[InlineData(true, Key.Right)]
	public void Arrows_AcrossTheLayout_AreSwallowedAndDoNothing(bool vertical, Key key)
	{
		_ = PickerWindow.MapKey(key, KeyModifiers.None, vertical).Should().BeNull();
		_ = PickerWindow.IsOffAxisArrow(key, KeyModifiers.None, vertical).Should().BeTrue(
			"the window consumes it, so native directional focus never moves behind the picker");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void OtherKeys_AreTheSameInBothLayouts(bool vertical)
	{
		_ = PickerWindow.MapKey(Key.D3, KeyModifiers.None, vertical).Should().Be(new PickerKeyAction(PickerKey.Digit, 3));
		_ = PickerWindow.MapKey(Key.Enter, KeyModifiers.None, vertical).Should().Be(new PickerKeyAction(PickerKey.Enter, 0));
		_ = PickerWindow.MapKey(Key.Escape, KeyModifiers.None, vertical).Should().Be(new PickerKeyAction(PickerKey.Escape, 0));
		_ = PickerWindow.MapKey(Key.F, KeyModifiers.None, vertical).Should().Be(new PickerKeyAction(PickerKey.SystemFallback, 0));
		_ = PickerWindow.MapKey(Key.F1, KeyModifiers.None, vertical).Should().Be(new PickerKeyAction(PickerKey.ToggleShortcuts, 0));
	}

	// NARROW SCREENS: floor((workArea - 24) / 96), clamped to 3..8.
	[Theory]
	[InlineData(1280, 8)]
	[InlineData(1536, 8)]
	[InlineData(819, 8)]
	[InlineData(682, 6)]
	[InlineData(500, 4)]
	[InlineData(300, 3)]
	public void TilesPerRow_FollowTheWorkAreaWidth(double workAreaDip, int expected)
	{
		_ = PickerWindow.TilesPerRowFor(workAreaDip).Should().Be(expected);
	}

	[Fact]
	public void TilesPerRow_WithNoWorkArea_IsEight()
	{
		_ = PickerWindow.TilesPerRowFor(null).Should().Be(8);
		_ = PickerWindow.TilesPerRowFor(double.NaN).Should().Be(8);
	}

	// The reported case: nine tiles on a 1024 px work area at 150 % (682 DIP). Eight per row
	// would be 792 DIP = 1188 px and run off the screen. Balanced rows make it 5+4: 504 DIP = 756 px.
	[Fact]
	public void NineTiles_OnA1024pxScreenAt150_WrapToTwoRows_AndTheWindowFits()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, 9).Select(i =>
			new BrowserCandidate($"browser-{i}", $"Browser {i}", $@"C:\Browsers\b{i}.exe", "stable", "known-path"))];
		PickerViewModel vm = new("https://example.com/", new BrowserSnapshot(candidates, 9, DateTimeOffset.UtcNow));
		PickerWindow window = new(vm, _ => { }, _ => { }) { WorkAreaHeightDipOverride = 2000, WorkAreaWidthDipOverride = 682 };
		try
		{
			window.ApplyMeasuredHeight();
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			_ = window.Width.Should().Be(504);
			ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")!;
			List<double> rows = [.. Enumerable.Range(0, 9).Select(index =>
				Math.Round(cards.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Button>().First().TranslatePoint(default, window)!.Value.Y))];
			_ = rows.Distinct().Count().Should().Be(2);
			_ = rows.Count(y => y == rows[0]).Should().Be(5);

			CursorWorkArea screen = new(0, 0, 1024, 728);
			CursorPlacementResult placement = CursorPlacement.ComputeCentered(window.Width, window.Height, 1.5, screen);
			_ = placement.WindowWidthPx.Should().Be(756);
			_ = placement.X.Should().BeGreaterThanOrEqualTo(0);
			_ = (placement.X + placement.WindowWidthPx).Should().BeLessThanOrEqualTo(1024);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	[Fact]
	public void Width_FollowsTheLayout()
	{
		_ = PickerWindow.WidthForTileCount(0).Should().Be(460, "the first-run screen keeps the width its buttons need");
		_ = PickerWindow.WidthForTileCount(3).Should().Be(360);
		_ = PickerWindow.WidthForTileCount(5).Should().Be(504);
		_ = PickerWindow.WidthForTileCount(8).Should().Be(792);
		_ = PickerWindow.WidthForTileCount(9).Should().Be(504, "nine tiles are balanced 5+4");
		_ = PickerWindow.WidthForTileCount(16).Should().Be(792);
		_ = PickerWindow.WidthForTileCount(9, vertical: true).Should().Be(360);
	}

	[Fact]
	public void VerticalWindow_StacksTheTilesInOneColumn_AndHorizontalInOneRow()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		(double verticalSpread, double verticalRows) = TileGeometry(vertical: true);
		(double horizontalSpread, double horizontalRows) = TileGeometry(vertical: false);

		_ = verticalSpread.Should().BeApproximately(0, 0.5, "every tile shares one X in the column");
		_ = verticalRows.Should().Be(3);
		_ = horizontalRows.Should().Be(1, "three tiles sit on one row");
		_ = horizontalSpread.Should().BeGreaterThan(0);
	}

	[Fact]
	public void ProfileName_IsShown_OnlyWhenTheBrowserHasSeveralProfiles()
	{
		using EnglishCultureScope english = new();
		ChoiceCardViewModel several = Card(new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\c.exe", "stable", "known-path")
		{
			ProfileIds = ["Profile 2", "Default"],
			ProfileNames = ["Work", "Anna"],
		});
		ChoiceCardViewModel single = Card(new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\c.exe", "stable", "known-path")
		{
			ProfileIds = ["Default"],
			ProfileNames = ["Person 1"],
		});
		ChoiceCardViewModel unnamed = Card(new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\c.exe", "stable", "known-path")
		{
			ProfileIds = ["Profile 2", "Default"],
			ProfileNames = ["", "Anna"],
		});

		_ = several.ProfileName.Should().Be("Work", "the tile names the profile it launches: the first id");
		_ = several.AccessibleName.Should().Be(LocalizationService.GetString("Card.AccessibleName.Profile", "Google Chrome", "Work"));
		_ = single.HasProfile.Should().BeFalse("one profile is not worth a line");
		_ = single.AccessibleName.Should().Be("Google Chrome");
		_ = unnamed.HasProfile.Should().BeFalse("no visible name, no line - never an internal directory id");
	}

	[Fact]
	public void SettingsViewModel_SavesAndReloadsThePickerLayout()
	{
		using EnglishCultureScope english = new();
		MemorySettingsStore store = new();
		SettingsViewModel vm = new(store);

		_ = vm.SelectedPickerLayoutIndex.Should().Be(0, "Horizontal is the default");
		vm.SelectedPickerLayoutIndex = 1;

		_ = store.Document.PickerLayout.Should().Be(SettingsPickerLayout.Vertical);
		_ = vm.PickerLayoutSaveStatusText.Should().Be(LocalizationService.GetString("Settings.Depth.Saved"));
		_ = new SettingsViewModel(store).PickerLayout.Should().Be(SettingsPickerLayout.Vertical);

		// The other preference survives: one document, two fields.
		_ = vm.SetControlDepth(SettingsControlDepth.Expert)!.Status.Should().Be(MutationCommitStatus.Committed);
		_ = store.Document.PickerLayout.Should().Be(SettingsPickerLayout.Vertical);
		_ = store.Document.ControlDepth.Should().Be(SettingsControlDepth.Expert);
	}

	[Fact]
	public void SettingsWindow_ShowsThePickerLayoutEditor_InAppearance()
	{
		string xaml = File.ReadAllText(Path.Combine(Root(), "src", "BroChoos.App", "Settings", "SettingsWindow.axaml"));

		_ = xaml.Should().Contain("IsVisible=\"{Binding IsAppearanceSelected}\"")
			.And.Contain("x:Name=\"PickerLayoutEditor\"")
			.And.Contain("SelectedIndex=\"{Binding SelectedPickerLayoutIndex, Mode=TwoWay}\"")
			.And.Contain("AutomationProperties.Name=\"{Binding PickerLayoutEditorLabelText}\"");
	}

	// PRIVACY: profile names can be personal names. Outside the tile view model (and the readers
	// and DTOs that carry them there), no source file may even mention them, so no log call,
	// problem-log entry or diagnostics projection can pick them up by accident.
	[Fact]
	public void ProfileNames_AreReferencedOnlyOnTheirPathToTheTile()
	{
		string[] allowed =
		[
			@"src\BroChoos.Core\Abstractions\BrowserCandidate.cs",
			@"src\BroChoos.Core\Privacy\ProfileNameSanitizer.cs",
			@"src\BroChoos.Core\Storage\BrowserDefinition.cs",
			@"src\BroChoos.Platform.Windows\Browsers\ChromiumProfileReader.cs",
			@"src\BroChoos.Platform.Windows\Browsers\WindowsBrowserDetector.cs",
			@"src\BroChoos.Platform.Windows\Browsers\ManualBrowserAdmitter.cs",
			@"src\BroChoos.Platform.Windows\Storage\PostConsentDiscoveryPublisher.cs",
			@"src\BroChoos.Platform.Windows\Storage\BrowserCatalogPersistenceService.cs",
			@"src\BroChoos.App\Picker\ChoiceCardViewModel.cs",
		];
		string root = Root();

		List<string> offenders = [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
			.Where(file => !file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) && !file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
			.Select(file => Path.GetRelativePath(root, file))
			.Where(file => !allowed.Contains(file, StringComparer.OrdinalIgnoreCase))
			.Where(file => File.ReadAllText(Path.Combine(root, file)).Contains("ProfileName", StringComparison.Ordinal))];

		_ = offenders.Should().BeEmpty("profile names are UI-only personal data");
	}

	// ONE TILE PER CHROMIUM PROFILE: tiles, slots, names and the nine-tile cap.
	[Fact]
	public void ChromiumProfiles_AreTiles_NamedBrowserAndProfile_AndTheCapCountsTiles()
	{
		using EnglishCultureScope english = new();
		BrowserCandidate chrome = new("chrome-stable", "Google Chrome", @"C:\c.exe", "stable", "known-path")
		{
			ProfileIds = ["Profile 2", "Default"],
			ProfileNames = ["Work", "Anna"],
		};
		BrowserCandidate[] others = [.. Enumerable.Range(1, 8).Select(i => new BrowserCandidate($"browser-{i}", $"Browser {i}", $@"C:\b{i}.exe", "stable", "known-path"))];
		PickerViewModel vm = new("https://example.com/", new BrowserSnapshot([chrome, .. others], 9, DateTimeOffset.UtcNow));

		_ = vm.Cards.Should().HaveCount(9, "every browser gets a tile first, then the spare slot goes to Chrome's second profile");
		_ = vm.Cards.Select(c => c.Slot).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9);
		_ = vm.Cards[0].AccessibleName.Should().Be(LocalizationService.GetString("Card.AccessibleName.ProfileMore", "Google Chrome", "Work", LocalizationService.GetPlural("Card.MoreProfiles", 1)));
		_ = vm.Cards[0].TooltipText.Should().Contain(vm.Cards[0].AccessibleName, "the tooltip names the hidden profiles too");
		_ = vm.Cards[0].HiddenProfileCount.Should().Be(1, "eight other browsers take the slots before Chrome's second profile");
		_ = vm.Cards[0].HiddenProfileCountText.Should().Be(LocalizationService.GetString("Card.ProfileMore", 1)).And.Be("+1");
		_ = vm.Cards[0].KeyHelpText.Should().Be(LocalizationService.GetString("Card.HelpText", 1), "the count is in the name already; the help text does not repeat it");
		_ = vm.Cards.Select(c => c.BrowserId).Should().Equal(["chrome-stable", .. others.Select(o => o.BrowserId)], "digit N follows the new order");
	}

	[Fact]
	public void SpareSlots_GoToProfileTiles_RightAfterTheBrowsersFirstTile()
	{
		using EnglishCultureScope english = new();
		BrowserCandidate chrome = new("chrome-stable", "Google Chrome", @"C:\c.exe", "stable", "known-path")
		{
			ProfileIds = ["Profile 2", "Default"],
			ProfileNames = ["Work", "Anna"],
		};
		BrowserCandidate edge = new("edge-stable", "Microsoft Edge", @"C:\e.exe", "stable", "known-path");
		PickerViewModel vm = new("https://example.com/", new BrowserSnapshot([chrome, edge], 2, DateTimeOffset.UtcNow));

		_ = vm.Cards.Select(c => (c.Slot, c.BrowserId, c.LaunchProfileId)).Should().Equal(
			(1, "chrome-stable", "Profile 2"),
			(2, "chrome-stable", "Default"),
			(3, "edge-stable", null));
		_ = vm.Cards[1].AccessibleName.Should().Be(LocalizationService.GetString("Card.AccessibleName.Profile", "Google Chrome", "Anna"));
		_ = vm.Cards.Should().OnlyContain(c => c.HiddenProfileCount == 0);
	}

	// REVIEW MED: Firefox launches with NO profile argument (LaunchHelperArguments), so it opens
	// its own default profile. Naming the first profiles.ini entry on the tile would be false.
	[Fact]
	public void FirefoxWithTwoProfiles_ShowsNoProfileLine_AndIsNamedByTheBrowserOnly()
	{
		ChoiceCardViewModel firefox = Card(new BrowserCandidate("firefox-stable", "Mozilla Firefox", @"C:\f.exe", "stable", "known-path")
		{
			ProfileIds = ["dev", "default-release"],
			ProfileNames = ["dev", "default-release"],
		});
		ChoiceCardViewModel manual = Card(new BrowserCandidate("manual-1", "My Browser", @"C:\m.exe", "manual", "manual")
		{
			ProfileIds = ["Profile 2", "Default"],
			ProfileNames = ["Work", "Anna"],
		});

		_ = firefox.HasProfile.Should().BeFalse();
		_ = firefox.AccessibleName.Should().Be("Mozilla Firefox");
		_ = manual.HasProfile.Should().BeFalse("no profile switch is passed to an unknown browser either");
	}

	// REVIEW LOW 5 + 8: the digit is in the tooltip and the help text, not on the tile; the name
	// stays short and the tooltip no longer duplicates it word for word.
	[Fact]
	public void Tile_TooltipAndHelpText_NameTheDigit_TheAccessibleNameDoesNot()
	{
		using EnglishCultureScope english = new();
		ChoiceCardViewModel card = new(
			new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\c.exe", "stable", "known-path"), 2, 2, RankingReasonCode.AllChoices, null);

		_ = card.AccessibleName.Should().Be("Google Chrome");
		_ = card.TooltipText.Should().Be(LocalizationService.GetString("Card.Tooltip", "Google Chrome", 2)).And.Contain("2").And.NotBe(card.AccessibleName);
		_ = card.KeyHelpText.Should().Be(LocalizationService.GetString("Card.HelpText", 2));

		string axaml = File.ReadAllText(Path.Combine(Root(), "src", "BroChoos.App", "Picker", "PickerWindow.axaml"));
		_ = axaml.Should().Contain("AutomationProperties.HelpText=\"{Binding KeyHelpText}\"").And.Contain("ToolTip.Tip=\"{Binding TooltipText}\"");
	}

	// OWNER RULE: rows are balanced, never filled-then-spilled.
	[Theory]
	[InlineData(1, 8, new[] { 1 })]
	[InlineData(8, 8, new[] { 8 })]
	[InlineData(9, 8, new[] { 5, 4 })]
	[InlineData(10, 8, new[] { 5, 5 })]
	[InlineData(12, 8, new[] { 6, 6 })]
	[InlineData(16, 8, new[] { 8, 8 })]
	[InlineData(17, 8, new[] { 6, 6, 5 })]
	[InlineData(9, 6, new[] { 5, 4 })]
	[InlineData(7, 6, new[] { 4, 3 })]
	public void RowSizes_AreBalanced_WithTheExtraTilesOnTop(int count, int maxPerRow, int[] expected)
	{
		_ = BalancedRowsPanel.RowSizes(count, maxPerRow).Should().Equal(expected);
	}

	// REVIEW MED: the "+N" count is its own run and never trims, the profile name gets the full
	// inner width, and the screen-reader name says how many profiles are hidden.
	[Fact]
	public void HiddenProfileCount_IsVisibleUntrimmed_AndInTheAccessibleName()
	{
		using EnglishCultureScope english = new();
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		BrowserCandidate chrome = new("chrome-stable", "Google Chrome", @"C:\c.exe", "stable", "known-path")
		{
			ProfileIds = ["Default", "Profile 1", "Profile 2", "Profile 3"],
			ProfileNames = ["Personal", "Work - Design team", "Testing", "Travel"],
		};
		BrowserCandidate[] others = [.. Enumerable.Range(1, 7).Select(i => new BrowserCandidate($"browser-{i}", $"Browser {i}", $@"C:\b{i}.exe", "stable", "known-path"))];
		PickerViewModel vm = new("https://example.com/", new BrowserSnapshot([chrome, .. others], 8, DateTimeOffset.UtcNow));
		int index = vm.Cards.ToList().FindIndex(c => c.HiddenProfileCount > 0);
		_ = index.Should().Be(1, "Chrome gets one spare slot; its second tile carries the count");
		_ = vm.Cards[index].HiddenProfileCount.Should().Be(2);

		PickerWindow window = new(vm, _ => { }, _ => { }) { WorkAreaHeightDipOverride = 2000, WorkAreaWidthDipOverride = 1280 };
		try
		{
			window.ApplyMeasuredHeight();
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();

			Control container = window.FindControl<ItemsControl>("CardsItems")!.ContainerFromIndex(index)!;
			TextBlock more = container.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TileProfileMore");
			TextBlock name = container.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TileProfile");
			_ = more.IsEffectivelyVisible.Should().BeTrue();
			_ = more.Text.Should().Be("+2");
			_ = more.TextLayout.TextLines.Should().OnlyContain(line => !line.HasCollapsed, "the count is never trimmed");
			_ = more.Bounds.Width.Should().BeGreaterThanOrEqualTo(more.TextLayout.WidthIncludingTrailingWhitespace - 0.5);
			_ = name.Bounds.Width.Should().BeGreaterThan(45, "the name trims against the full 70 DIP line, not a 40 DIP box");
			_ = (name.Bounds.Width + more.Bounds.Width + more.Margin.Left).Should().BeLessThanOrEqualTo(70.5);

			Button tile = container.GetVisualDescendants().OfType<Button>().First();
			string accessible = Avalonia.Automation.AutomationProperties.GetName(tile) ?? string.Empty;
			_ = accessible.Should().Be(LocalizationService.GetString("Card.AccessibleName.ProfileMore", "Google Chrome", "Work - Design team", LocalizationService.GetPlural("Card.MoreProfiles", 2))).And.Contain("2");
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// A hidden child takes no slot: five children, the second hidden, two per row -> 2+2, not 2+2+1,
	// and the visible ones close up in reading order with no gap where the hidden one was.
	[Fact]
	public void BalancedRowsPanel_SkipsHiddenChildren_WhenCountingAndArranging()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		BalancedRowsPanel panel = new() { MaxPerRow = 2 };
		Border[] children = [.. Enumerable.Range(0, 5).Select(_ => new Border { Width = 10, Height = 10 })];
		children[1].IsVisible = false;
		panel.Children.AddRange(children);

		panel.Measure(Size.Infinity);
		_ = panel.DesiredSize.Should().Be(new Size(20, 20));

		panel.Arrange(new Rect(0, 0, 20, 20));
		_ = children[0].Bounds.Position.Should().Be(new Point(0, 0));
		_ = children[2].Bounds.Position.Should().Be(new Point(10, 0));
		_ = children[3].Bounds.Position.Should().Be(new Point(0, 10));
		_ = children[4].Bounds.Position.Should().Be(new Point(10, 10));
	}

	// Measured on a real layout: the number of tiles on each row, each row centred on the window,
	// and digit order row by row, left to right.
	[Theory]
	[InlineData(1, 1280, new[] { 1 })]
	[InlineData(2, 1280, new[] { 2 })]
	[InlineData(3, 1280, new[] { 3 })]
	[InlineData(9, 1280, new[] { 5, 4 })]
	[InlineData(16, 1280, new[] { 8, 8 })]
	[InlineData(17, 1280, new[] { 6, 6, 5 })]
	[InlineData(9, 682, new[] { 5, 4 })]
	public void Rows_AreBalanced_EachCentredOnTheWindow_InReadingOrder(int count, double workAreaWidthDip, int[] expectedRows)
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, count).Select(i =>
			new BrowserCandidate($"browser-{i}", $"Browser {i}", $@"C:\Browsers\b{i}.exe", "stable", "known-path"))];
		PickerViewModel vm = new("https://example.com/", new BrowserSnapshot(candidates, count, DateTimeOffset.UtcNow));
		PickerWindow window = new(vm, _ => { }, _ => { }) { WorkAreaHeightDipOverride = 2000, WorkAreaWidthDipOverride = workAreaWidthDip };
		try
		{
			window.ApplyMeasuredHeight();
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")!;
			int shown = vm.Cards.Count;
			List<Rect> rects = [.. Enumerable.Range(0, shown).Select(index =>
			{
				Button b = cards.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Button>().First();
				return new Rect(b.TranslatePoint(default, window)!.Value, b.Bounds.Size);
			})];
			List<IGrouping<double, Rect>> rows = [.. rects.GroupBy(r => Math.Round(r.Y)).OrderBy(g => g.Key)];

			int[] expected = shown == count ? expectedRows : [.. BalancedRowsPanel.RowSizes(shown, PickerWindow.TilesPerRowFor(workAreaWidthDip))];
			_ = rows.Select(g => g.Count()).Should().Equal(expected);
			foreach (IGrouping<double, Rect> row in rows)
			{
				double centre = (row.Min(r => r.X) + row.Max(r => r.Right)) / 2;
				_ = centre.Should().BeApproximately(window.ClientSize.Width / 2, 1.0, "every row is centred on the window");
			}

			// Reading order: each tile is right of the previous one on its row, or on a lower row.
			for (int i = 1; i < rects.Count; i++)
			{
				bool sameRow = Math.Abs(rects[i].Y - rects[i - 1].Y) < 0.5;
				_ = (sameRow ? rects[i].X > rects[i - 1].X : rects[i].Y > rects[i - 1].Y).Should().BeTrue($"tile {i + 1} follows tile {i} in reading order");
			}

			_ = window.Width.Should().Be(PickerWindow.WidthForTileCount(shown, false, workAreaWidthDip), "the window follows the widest balanced row");
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// REVIEW LOW 3: the last row is CENTRED (WrapPanel ItemsAlignment=Center), measured on a
	// real layout: the lone ninth tile's centre is the window's centre.
	[Theory]
	[InlineData(1280, 9)]
	[InlineData(682, 9)]
	[InlineData(682, 8)]
	public void LastRow_IsCentred(double workAreaWidthDip, int count)
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, count).Select(i =>
			new BrowserCandidate($"browser-{i}", $"Browser {i}", $@"C:\Browsers\b{i}.exe", "stable", "known-path"))];
		PickerViewModel vm = new("https://example.com/", new BrowserSnapshot(candidates, count, DateTimeOffset.UtcNow));
		PickerWindow window = new(vm, _ => { }, _ => { }) { WorkAreaHeightDipOverride = 2000, WorkAreaWidthDipOverride = workAreaWidthDip };
		try
		{
			window.ApplyMeasuredHeight();
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")!;
			List<Rect> rects = [.. Enumerable.Range(0, count).Select(index =>
			{
				Button b = cards.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Button>().First();
				return new Rect(b.TranslatePoint(default, window)!.Value, b.Bounds.Size);
			})];
			double lastRowY = rects.Max(r => r.Y);
			List<Rect> lastRow = [.. rects.Where(r => Math.Abs(r.Y - lastRowY) < 0.5)];
			double rowCentre = (lastRow.Min(r => r.X) + lastRow.Max(r => r.Right)) / 2;

			_ = rowCentre.Should().BeApproximately(window.Width / 2, 1.0, "the last row sits under the middle of the window");
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// REVIEW C5: focus resting on the status area moves to the first tile when the message clears.
	[Fact]
	public void FocusedStatusArea_HandsFocusToTheFirstTile_WhenItsMessageClears()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		BrowserCandidate[] candidates = [new("edge-stable", "Microsoft Edge", @"C:\e.exe", "stable", "known-path")];
		PickerViewModel vm = new("https://example.com/", new BrowserSnapshot(candidates, 1, DateTimeOffset.UtcNow));
		PickerWindow window = new(vm, _ => { }, _ => { }) { WorkAreaHeightDipOverride = 2000 };
		try
		{
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();
			vm.ShowLaunchFailure("Microsoft Edge", "launch.start-failed");
			StackPanel status = window.FindControl<StackPanel>("StatusArea")!;
			_ = status.Focus().Should().BeTrue();

			vm.ClearStatusForReadyChoices();
			Dispatcher.UIThread.RunJobs();

			_ = window.FocusManager?.GetFocusedElement().Should().BeOfType<Button>().Which.DataContext.Should().BeSameAs(vm.Cards[0]);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// REVIEW C2: the scroll floor uses the taller tile when a profile line is shown.
	[Fact]
	public void ScrollFloor_UsesTheProfileTileHeight_WhenAnyTileShowsAProfile()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		BrowserCandidate chrome = new("chrome-stable", "Google Chrome", @"C:\c.exe", "stable", "known-path")
		{
			ProfileIds = ["Profile 2", "Default"],
			ProfileNames = ["Work", "Anna"],
		};
		PickerWindow window = new(new PickerViewModel("https://example.com/", new BrowserSnapshot([chrome], 1, DateTimeOffset.UtcNow)), _ => { }, _ => { });
		try
		{
			_ = window.TryFindResource("bc.tile.heightWithProfile", out object? tall).Should().BeTrue();
			_ = window.MinimumScrolledListDip().Should().Be(2 * ((double)tall! + 8));
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// REVIEW LOW 9: with no message the status area is hidden from the screen reader; it joins the
	// automation tree as soon as a message appears.
	[Fact]
	public void EmptyStatusArea_IsHiddenFromScreenReaders_UntilAMessageAppears()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		BrowserCandidate[] candidates = [new("edge-stable", "Microsoft Edge", @"C:\e.exe", "stable", "known-path")];
		PickerViewModel vm = new("https://example.com/", new BrowserSnapshot(candidates, 1, DateTimeOffset.UtcNow));
		PickerWindow window = new(vm, _ => { }, _ => { });
		try
		{
			StackPanel status = window.FindControl<StackPanel>("StatusArea")!;
			_ = Avalonia.Automation.AutomationProperties.GetAccessibilityView(status).Should().Be(Avalonia.Automation.AccessibilityView.Raw);
			_ = status.Focusable.Should().BeFalse("an empty status area is not a focus stop with no name");

			vm.ShowLaunchFailure("Microsoft Edge", "launch.start-failed");

			_ = Avalonia.Automation.AutomationProperties.GetAccessibilityView(status).Should().Be(Avalonia.Automation.AccessibilityView.Control);
			_ = status.Focusable.Should().BeTrue("with a message the status area is a focus landing spot");
			_ = Avalonia.Automation.AutomationProperties.GetName(status).Should().Be(vm.StatusMessage).And.NotBeNullOrWhiteSpace();
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static (double XSpread, double Rows) TileGeometry(bool vertical)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, 3).Select(i =>
			new BrowserCandidate($"browser-{i}", $"Browser {i}", $@"C:\Browsers\b{i}.exe", "stable", "known-path"))];
		PickerViewModel vm = new("https://example.com/", new BrowserSnapshot(candidates, 3, DateTimeOffset.UtcNow))
		{
			IsVerticalLayout = vertical,
		};
		PickerWindow window = new(vm, _ => { }, _ => { }) { WorkAreaHeightDipOverride = 2000 };
		try
		{
			window.ApplyMeasuredHeight();
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")!;
			List<Point> origins = [.. Enumerable.Range(0, 3).Select(index =>
			{
				Button button = cards.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Button>().First();
				return button.TranslatePoint(default, window)!.Value;
			})];

			return (origins.Max(p => p.X) - origins.Min(p => p.X), origins.Select(p => Math.Round(p.Y)).Distinct().Count());
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// LocalizationService.CurrentCulture is process-wide. [CultureIsolated] on this class's
	// collection holds the global culture gate for every test (see CultureGate), so the console-capture
	// collection cannot run while the value below is captured, pinned and restored. Tests that
	// compare against English text pin English for their duration and restore the value they
	// captured, which nothing else can have changed in between.
	private sealed class EnglishCultureScope : IDisposable
	{
		private readonly System.Globalization.CultureInfo _previous = LocalizationService.CurrentCulture;

		public EnglishCultureScope()
		{
			LocalizationService.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en");
		}

		public void Dispose()
		{
			LocalizationService.CurrentCulture = _previous;
		}
	}

	private static ChoiceCardViewModel Card(BrowserCandidate candidate)
	{
		return new ChoiceCardViewModel(candidate, 1, 1, RankingReasonCode.AllChoices, null);
	}

	private static string Root()
	{
		DirectoryInfo? current = new(AppContext.BaseDirectory);
		while (current is not null && !File.Exists(Path.Combine(current.FullName, "BroChoos.sln")))
		{
			current = current.Parent;
		}

		return current?.FullName ?? throw new InvalidOperationException("BroChoos.sln not found");
	}

	private sealed class MemorySettingsStore : ISettingsStore
	{
		private bool _absent = true;

		public SettingsDocument Document { get; private set; } = new();

		public SettingsDocument LoadOrDefault()
		{
			return Document;
		}

		public ExpectedDocumentGeneration ObserveExpectedGeneration()
		{
			return _absent ? ExpectedDocumentGeneration.Absent : ExpectedDocumentGeneration.Match(Document.Generation);
		}

		public MutationCommitResult Save(SettingsDocument document, ExpectedDocumentGeneration expectedGeneration)
		{
			bool matches = expectedGeneration.ExpectsDocument
				? !_absent && expectedGeneration.Value == Document.Generation
				: _absent;
			if (!matches)
			{
				return MutationCommitResult.Conflict(_absent ? null : Document.Generation);
			}

			Document = document with { Generation = Document.Generation + 1 };
			_absent = false;
			return MutationCommitResult.Committed(Document.Generation, Document.Generation - 1);
		}
	}
}
