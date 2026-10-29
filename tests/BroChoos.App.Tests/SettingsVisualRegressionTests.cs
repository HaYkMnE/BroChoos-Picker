using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BroChoos.App.Picker;
using BroChoos.App.Settings;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// Regressions found by the visual audit (VisualSnapshotTests). Each test renders the REAL
// Settings window on the shared bootstrap and fails on the defect as it was measured:
//   1. High Contrast: the Settings window background resolved to a TRANSLUCENT brush
//      (bc.bg.subtle -> SystemControlBackgroundBaseLowBrush, alpha 0x33), so a top-level
//      window painted 20 % black over nothing.
//   2. Home dashboard values were single-line and silently cut at the tile edge; the English
//      status lost "...not saved", the Armenian one lost half the sentence.
//   3. Home action row was a horizontal StackPanel: in Armenian the fourth button ran past the
//      window's right edge (measured 928 + 214 > 1120 DIP) and was clipped.
// AvaloniaUiCollection, not ConsoleCaptureCollection: the dispatcher is thread-affine and owned
// by the thread that first ran PickerXamlProbe, which is this collection's (MEASURED: from the
// other collection every test failed with "Call from invalid thread").
[Collection(AvaloniaUiCollection.Name)]
public sealed class SettingsVisualRegressionTests
{
	[Fact]
	public void HighContrast_settings_window_background_is_opaque()
	{
		Measure("en", PickerThemeVariants.HighContrast, 1120, window =>
		{
			IBrush? background = window.Background;
			_ = background.Should().BeAssignableTo<ISolidColorBrush>();
			ISolidColorBrush solid = (ISolidColorBrush)background!;
			_ = (solid.Color.A * solid.Opacity).Should().BeGreaterThanOrEqualTo(254.5,
				"a top-level window background has nothing beneath it to blend with");
		});
	}

	[Theory]
	[InlineData("en")]
	[InlineData("ru")]
	[InlineData("hy")]
	public void Home_dashboard_values_are_not_truncated(string lang)
	{
		Measure(lang, Avalonia.Styling.ThemeVariant.Light, 960, window =>
		{
			SettingsViewModel vm = (SettingsViewModel)window.DataContext!;
			string[] values = [vm.DashboardStatusText, vm.DashboardBrowserCountText, vm.DashboardProfileCountText, vm.DashboardHealthText];
			foreach (TextBlock tb in window.GetVisualDescendants().OfType<TextBlock>().Where(t => values.Contains(t.Text)))
			{
				double available = tb.Bounds.Width - tb.Padding.Left - tb.Padding.Right + 0.75;
				_ = tb.TextLayout.TextLines.Max(l => l.WidthIncludingTrailingWhitespace).Should().BeLessThanOrEqualTo(available, $"'{tb.Text}' must fit its tile");
				_ = tb.TextLayout.Height.Should().BeLessThanOrEqualTo(tb.Bounds.Height + 0.75, $"'{tb.Text}' must not be clipped vertically");
			}
		});
	}

	[Theory]
	[InlineData("en", 960)]
	[InlineData("ru", 960)]
	[InlineData("hy", 960)]
	[InlineData("hy", 1120)]
	public void Home_action_buttons_stay_inside_the_window(string lang, double width)
	{
		Measure(lang, Avalonia.Styling.ThemeVariant.Light, width, window =>
		{
			SettingsViewModel vm = (SettingsViewModel)window.DataContext!;
			string[] labels = [vm.CheckWindowsSettingsButtonText, vm.OpenBrowsersButtonText, vm.ChangeDepthButtonText, vm.ExportSettingsButtonText];
			List<Button> buttons = [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Content is string s && labels.Contains(s))];
			_ = buttons.Should().HaveCount(4);
			foreach (Button b in buttons)
			{
				Point origin = b.TranslatePoint(default, window)!.Value;
				_ = (origin.X + b.Bounds.Width).Should().BeLessThanOrEqualTo(window.ClientSize.Width + 0.5, $"'{b.Content}' at {width} DIP");
			}
		});
	}

	private static void Measure(string lang, Avalonia.Styling.ThemeVariant variant, double width, Action<SettingsWindow> assert)
	{
		_ = PickerXamlProbe.Run().Failure.Should().BeNull();
		// The view model is asked for its language directly, so the process-wide
		// LocalizationService.CurrentCulture is never touched and this UI-collection class cannot
		// disturb a console-capture class that runs at the same time.
		SettingsViewModel vm = new(culture: CultureInfo.GetCultureInfo(lang));

		SettingsWindow window = new(vm)
		{
			RequestedThemeVariant = variant,
			Width = width,
			ShowActivated = false,
			Topmost = false,
			Opacity = 0,
			Position = new PixelPoint(-32000, -32000),
		};
		try
		{
			window.Show();
			for (int i = 0; i < 4; i++)
			{
				Dispatcher.UIThread.RunJobs();
				window.UpdateLayout();
			}

			assert(window);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}
}
