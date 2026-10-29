using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BroChoos.App.Localization;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// Regressions from the second visual-audit round, each driven on the REAL picker window:
//   1. Nine cards measured 853 DIP - taller than a 1080p work area at 125-150 % - and the card
//      list could not scroll. The window now caps the list to the work area and scrolls the
//      focused card into view.
//   2. Dark theme: the white focus ring on the #77B7F0 primary fill was 2.14:1. The primary now
//      gets a two-tone ring (inner token as the border, outer token as a 2 DIP spread).
//   3. The "?" key-cap outline was bc.border.subtle: 1.32:1 Light, 1.50:1 Dark.
[Collection(AvaloniaUiCollection.Name)]
public sealed class PickerVisualRegressionTests
{
	private static readonly (string Name, ThemeVariant Variant)[] Themes =
	[
		("light", ThemeVariant.Light),
		("dark", ThemeVariant.Dark),
		("hc", PickerThemeVariants.HighContrast),
	];

	// 1080p at 150 %: a 1032 px work area (1080 minus a 48 px taskbar) / 1.5.
	// The scroll floor is now two full tile rows (228 DIP), so the horizontal layout (at most two or
	// three rows) never needs to scroll on a real screen. The VERTICAL layout with nine tiles (about
	// 1060 DIP) is the case that does, so this test drives that one on the real 1080p@150 % height.
	private const double ShortWorkAreaDip = 688;

	[Fact]
	public void Nine_cards_fit_a_short_work_area_and_the_ninth_scrolls_into_view_on_focus()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		PickerViewModel vm = new("https://example.com/", Snapshot(9)) { IsVerticalLayout = true };
		PickerWindow window = new(vm, _ => { }, _ => { }) { WorkAreaHeightDipOverride = ShortWorkAreaDip };
		window.ApplyMeasuredHeight();

		try
		{
			_ = window.Height.Should().BeLessThanOrEqualTo(ShortWorkAreaDip, "the window must fit the work area it opens on");
			ScrollViewer scroller = window.FindControl<ScrollViewer>("CardsScroller")!;
			_ = double.IsFinite(scroller.MaxHeight).Should().BeTrue(
				$"the card list, not the window, absorbs the overflow (window {window.Height:0.#}, list {scroller.DesiredSize.Height:0.#} DIP)");

			// The pre-show measurement must see the real list: an empty ScrollViewer presenter
			// once handed placement 204 DIP for nine cards.
			_ = window.FindControl<ItemsControl>("CardsItems")!.ContainerFromIndex(0).Should().NotBeNull("the cards are realized before Show");

			ShowOffScreen(window);
			_ = window.ClientSize.Height.Should().BeLessThanOrEqualTo(ShortWorkAreaDip + 0.5);
			_ = ((Control)window.Content!).DesiredSize.Height.Should().BeLessThanOrEqualTo(window.ClientSize.Height + 0.5, "nothing is clipped at the window edge");

			// The keyboard contract: digit 9 focuses the ninth card, which starts below the fold.
			_ = vm.FocusSlot(9).Should().BeTrue();
			window.FocusCard(vm.FocusedIndex);
			Settle(window);

			Button ninth = CardButton(window, 8);
			_ = ReferenceEquals(window.FocusManager?.GetFocusedElement(), ninth).Should().BeTrue();
			_ = ninth.Classes.Contains(":focus-visible").Should().BeTrue();
			_ = scroller.Offset.Y.Should().BeGreaterThan(0, "focusing the ninth card must scroll the list");
			Rect inViewport = new(ninth.TranslatePoint(default, scroller)!.Value, ninth.Bounds.Size);
			_ = inViewport.Top.Should().BeGreaterThanOrEqualTo(-0.5);
			_ = inViewport.Bottom.Should().BeLessThanOrEqualTo(scroller.Viewport.Height + 0.5, "the focused card is fully visible");

			// And back to the first card: the list scrolls up again.
			window.FocusCard(0);
			Settle(window);
			_ = scroller.Offset.Y.Should().BeApproximately(0, 0.5);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// The config-recovery notice on the REAL window: part of the pre-show height (no grow after
	// open), card 1 keeps first focus, Tab reaches the action after the cards, the action has
	// its accessible name, and clicking it reaches the session callback.
	[Fact]
	public void Config_notice_row_is_measured_reachable_by_tab_and_invokes_safe_mode()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		PickerViewModel plain = new("https://example.com/", Snapshot(2));
		PickerWindow without = new(plain, _ => { }, _ => { });
		double heightWithout = without.Height;
		without.Close();

		PickerViewModel vm = new("https://example.com/", Snapshot(2));
		vm.ShowConfigNotice();
		int repairCalls = 0;
		PickerWindow window = new(vm, _ => { }, _ => { }, onRepairSettings: () => repairCalls++);

		try
		{
			_ = window.Height.Should().BeGreaterThan(heightWithout, "the notice row is in the pre-show measurement");

			ShowOffScreen(window);
			Border notice = window.FindControl<Border>("ConfigNotice")!;
			Button action = window.FindControl<Button>("RepairSettingsButton")!;
			_ = notice.IsEffectivelyVisible.Should().BeTrue();
			_ = Avalonia.Automation.AutomationProperties.GetLiveSetting(notice).Should().Be(Avalonia.Automation.AutomationLiveSetting.Polite);
			_ = Avalonia.Automation.AutomationProperties.GetName(action).Should().Be(LocalizationService.GetString("ConfigNotice.Repair"));
			_ = Avalonia.Automation.AutomationProperties.GetHelpText(action).Should().BeNullOrEmpty("the sentence must not be read twice");
			_ = Avalonia.Automation.AutomationProperties.GetAccessibilityView(window.FindControl<TextBlock>("ConfigNoticeTextBlock")!)
				.Should().Be(Avalonia.Automation.AccessibilityView.Raw, "the row's name already carries the sentence");

			// Announced once, after open: the row's name goes from empty to the sentence.
			_ = Avalonia.Automation.AutomationProperties.GetName(notice).Should().BeNullOrEmpty();
			vm.AnnounceConfigNotice();
			Settle(window);
			_ = Avalonia.Automation.AutomationProperties.GetName(notice).Should().Be(vm.ConfigNoticeText);

			window.FocusCard(1);
			Settle(window);
			_ = ReferenceEquals(KeyboardNavigationHandler.GetNext(CardButton(window, 1), NavigationDirection.Next), action)
				.Should().BeTrue("Tab from the last card lands on Repair settings");

			action.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
			_ = repairCalls.Should().Be(1);

			// Armed: the label becomes the confirm wording; running: disabled, so no double click.
			vm.ArmRepair();
			Settle(window);
			_ = action.Content.Should().Be(LocalizationService.GetString("ConfigNotice.RepairConfirm"));

			// FOCUS: a disabled focused button is a dead end, so focus moves to the status area,
			// which is announcing "Repairing…" (the same landing as after Skip).
			_ = action.Focus(NavigationMethod.Tab, KeyModifiers.None).Should().BeTrue();
			Settle(window);
			vm.BeginRepair();
			Settle(window);
			_ = action.IsEnabled.Should().BeFalse("a running repair cannot be started again");
			StackPanel statusArea = window.FindControl<StackPanel>("StatusArea")!;
			_ = ReferenceEquals(window.FocusManager?.GetFocusedElement(), statusArea).Should().BeTrue("focus left the disabled button");

			vm.ShowRepairSucceeded();
			Settle(window);
			_ = notice.IsVisible.Should().BeFalse("after a successful repair the notice goes");
			_ = ReferenceEquals(window.FocusManager?.GetFocusedElement(), statusArea).Should().BeTrue();
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// The hidden-after-success path on its own: the button itself held focus (not the
	// running state), the notice hides, focus lands on the status area - never nowhere.
	[Fact]
	public void Hiding_the_notice_moves_focus_from_the_repair_button_to_the_status_area()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		PickerViewModel vm = new("https://example.com/", Snapshot(1));
		vm.ShowConfigNotice();
		PickerWindow window = new(vm, _ => { }, _ => { }, onRepairSettings: () => { });

		try
		{
			ShowOffScreen(window);
			Button action = window.FindControl<Button>("RepairSettingsButton")!;
			_ = action.Focus(NavigationMethod.Tab, KeyModifiers.None).Should().BeTrue();
			Settle(window);

			vm.ShowRepairSucceeded();
			Settle(window);

			_ = ReferenceEquals(window.FocusManager?.GetFocusedElement(), window.FindControl<StackPanel>("StatusArea")).Should().BeTrue();
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// Focus anywhere else is never disturbed by a repair state change.
	[Fact]
	public void Repair_state_changes_leave_focus_on_a_card_alone()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		PickerViewModel vm = new("https://example.com/", Snapshot(2));
		vm.ShowConfigNotice();
		PickerWindow window = new(vm, _ => { }, _ => { }, onRepairSettings: () => { });

		try
		{
			ShowOffScreen(window);
			window.FocusCard(0);
			Settle(window);
			Button card = CardButton(window, 0);

			vm.BeginRepair();
			Settle(window);
			vm.ShowRepairSucceeded();
			Settle(window);

			_ = ReferenceEquals(window.FocusManager?.GetFocusedElement(), card).Should().BeTrue();
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	[Fact]
	public void Scrolled_list_floor_is_two_tile_rows_computed_from_the_tokens()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		PickerWindow window = new(new PickerViewModel("https://example.com/", Snapshot(1)), _ => { }, _ => { });

		try
		{
			_ = window.TryFindResource("bc.tile.height", out object? height).Should().BeTrue();
			_ = window.TryFindResource("bc.tile.margin", out object? margin).Should().BeTrue();
			Thickness m = (Thickness)margin!;
			_ = window.MinimumScrolledListDip().Should().Be(2 * ((double)height! + m.Top + m.Bottom));
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	[Fact]
	public void Nine_cards_are_not_capped_when_the_work_area_is_tall_enough()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		PickerWindow window = new(new PickerViewModel("https://example.com/", Snapshot(9)), _ => { }, _ => { })
		{
			WorkAreaHeightDipOverride = 2000,
		};
		window.ApplyMeasuredHeight();

		try
		{
			ScrollViewer scroller = window.FindControl<ScrollViewer>("CardsScroller")!;
			_ = double.IsPositiveInfinity(scroller.MaxHeight).Should().BeTrue("a list that fits must not scroll");
			_ = window.Height.Should().BeApproximately(window.MeasureContentHeightDip(), 0.5);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	[Fact]
	public void Primary_button_focus_ring_is_two_tone_and_meets_3_to_1_in_every_theme()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		foreach ((string name, ThemeVariant variant) in Themes)
		{
			PickerViewModel vm = new("https://example.com/", BrowserSnapshot.Empty);
			vm.ShowFirstRunPrompt();
			PickerWindow window = new(vm, _ => { }, _ => { }) { RequestedThemeVariant = variant };
			try
			{
				ShowOffScreen(window);
				window.FocusFirstRunPrimary();
				Settle(window);

				Button primary = window.FindControl<Button>("FindBrowsersButton")!;
				_ = primary.Classes.Contains(":focus-visible").Should().BeTrue(name);
				ContentPresenter presenter = primary.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

				Color card = Opaque(window, "bc.bg.card", variant);
				Color fill = Over(BrushColor(presenter.Background), card);
				Color inner = Over(BrushColor(presenter.BorderBrush), card);
				_ = presenter.BoxShadow.Count.Should().BeGreaterThan(0, $"{name}: the outer ring is drawn as a spread");
				BoxShadow outerShadow = presenter.BoxShadow[0];
				_ = outerShadow.Spread.Should().BeGreaterThanOrEqualTo(2, name);
				_ = outerShadow.Color.Should().Be(Opaque(window, "bc.focus.outer", variant), $"{name}: the spread uses the outer focus token");

				_ = VisualSnapshotTests.Ratio(inner, fill).Should().BeGreaterThanOrEqualTo(3.0, $"{name}: inner ring vs primary fill");
				_ = VisualSnapshotTests.Ratio(outerShadow.Color, card).Should().BeGreaterThanOrEqualTo(3.0, $"{name}: outer ring vs the panel");
			}
			finally
			{
				window.Close();
				Dispatcher.UIThread.RunJobs();
			}
		}
	}

	[Fact]
	public void Shortcut_toggle_outline_meets_3_to_1_in_every_theme()
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		foreach ((string name, ThemeVariant variant) in Themes)
		{
			PickerWindow window = new(new PickerViewModel("https://example.com/", Snapshot(1)), _ => { }, _ => { })
			{
				RequestedThemeVariant = variant,
			};
			try
			{
				ShowOffScreen(window);
				Control toggle = window.FindControl<Control>("ShortcutsToggleButton")!;
				Color background = Opaque(window, "bc.bg.base", variant);
				Color outline = Over(BrushColor(((ExpandableToggleButton)toggle).BorderBrush), background);
				_ = VisualSnapshotTests.Ratio(outline, background).Should().BeGreaterThanOrEqualTo(3.0, $"{name}: '?' outline vs window");
			}
			finally
			{
				window.Close();
				Dispatcher.UIThread.RunJobs();
			}
		}
	}

	private static Color BrushColor(IBrush? brush)
	{
		ISolidColorBrush solid = brush.Should().BeAssignableTo<ISolidColorBrush>().Subject;
		return Color.FromArgb((byte)Math.Round(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B);
	}

	// A token as it is painted: composited over the opaque window base (and that over white),
	// because the High Contrast aliases are translucent Fluent brushes.
	private static Color Opaque(Window window, string key, ThemeVariant variant)
	{
		Color Raw(string k)
		{
			_ = window.TryGetResource(k, variant, out object? value).Should().BeTrue(k);
			return value switch
			{
				Color c => c,
				ISolidColorBrush s => BrushColor(s),
				_ => throw new InvalidOperationException($"{k} is {value?.GetType().Name}"),
			};
		}

		Color baseColor = Over(Raw("bc.bg.base"), Colors.White);
		return key == "bc.bg.base" ? baseColor : Over(Raw(key), baseColor);
	}

	private static Color Over(Color top, Color under)
	{
		double a = top.A / 255d;
		byte Mix(byte t, byte u)
		{
			return (byte)Math.Round((t * a) + (u * (1 - a)));
		}

		return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
	}

	private static void ShowOffScreen(Window window)
	{
		window.ShowActivated = false;
		window.Topmost = false;
		window.Opacity = 0;
		window.Position = new PixelPoint(-32000, -32000);
		window.Show();
		Settle(window);
	}

	private static void Settle(Window window)
	{
		for (int i = 0; i < 6; i++)
		{
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
		}
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")!;
		return cards.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Button>().First();
	}

	private static BrowserSnapshot Snapshot(int count)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, count).Select(i =>
			new BrowserCandidate($"browser-{i}", $"Browser {i}", $@"C:\Browsers\b{i}.exe", "stable", "known-path"))];
		return new BrowserSnapshot(candidates, count, DateTimeOffset.UtcNow);
	}
}
