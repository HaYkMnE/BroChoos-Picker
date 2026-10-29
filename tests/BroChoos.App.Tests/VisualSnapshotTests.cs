using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BroChoos.App.Localization;
using BroChoos.App.Picker;
using BroChoos.App.Settings;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BroChoos.App.Tests;

// OPT-IN VISUAL AUDIT. Skipped unless BROCHOOS_VISUAL_SNAPSHOTS=1. Renders every picker state
// (3 themes x 3 languages) and the Settings home (3 themes x 3 languages) to PNG, and writes a
// text report of text fit, colour contrast, Tab order and automation names next to them.
//
// It reuses PickerXamlProbe's bootstrap (Win32 platform + Skia, windows shown off-screen and
// never activated), because an Avalonia Application is process-wide and cannot be configured
// twice; Avalonia.Headless is not referenced by this repo. Output goes to
// BROCHOOS_VISUAL_SNAPSHOTS_DIR, default %TEMP%\brochoos-visual-check.
public sealed class VisualSnapshotFactAttribute : FactAttribute
{
	public VisualSnapshotFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("BROCHOOS_VISUAL_SNAPSHOTS") != "1")
		{
			Skip = "Opt-in visual audit: set BROCHOOS_VISUAL_SNAPSHOTS=1 to render PNGs.";
		}
	}
}

[Collection(AvaloniaUiCollection.Name)]
public sealed class VisualSnapshotTests(ITestOutputHelper output)
{
	private static readonly string OutDir =
		Environment.GetEnvironmentVariable("BROCHOOS_VISUAL_SNAPSHOTS_DIR") is { Length: > 0 } dir
			? dir
			: Path.Combine(Path.GetTempPath(), "brochoos-visual-check");

	private static readonly (string Name, ThemeVariant Variant)[] Themes =
	[
		("light", ThemeVariant.Light),
		("dark", ThemeVariant.Dark),
		("hc", PickerThemeVariants.HighContrast),
	];

	private static readonly string[] Languages = ["en", "ru", "hy"];

	private static readonly string[] BrowserNames =
	[
		"Microsoft Edge", "Google Chrome", "Mozilla Firefox", "Brave", "Opera",
		"Vivaldi", "Firefox Developer Edition", "Tor Browser", "Chromium",
	];

	// Tile layout (docs/design/PICKER-LAYOUT-SPEC.md). Cards9Capped: nine tiles on a short work area
	// so the second row scrolls; Cards9Narrow: nine tiles on a 1024 px work area at 150 % (682 DIP).
	private enum PickerState
	{
		Cards3, Cards5, Cards9, Cards9Narrow, Cards9Capped, Cards9HorizontalCapped, Vertical3, Profiles3, ProfilesOverflow, Icons3,
		FirstRun, FirstRunFailed, LaunchFailed, ErrorWaiting, RepairNotice, ShortcutsOpen,
	}

	// Every other state renders against a work area tall enough never to cap, so the images do
	// not depend on the monitor of the machine that runs the audit.
	private const double TallWorkAreaDip = 2000;
	// Cards9Capped: nine tiles in the VERTICAL layout on 1080p at 150 % (688 DIP), the case that
	// scrolls now that the scroll floor is two full tile rows.
	private const double ShortWorkAreaDip = 688;

	// Widths in DIP: 1920 px at 150 % (8 tiles per row) and 1024 px at 150 %.
	private const double WideWorkAreaDip = 1280;
	private const double NarrowWorkAreaDip = 682;

	// Cards9HorizontalCapped: the HORIZONTAL layout capped. At 5+4 the list is exactly the
	// two-row scroll floor and can never scroll, so the work area is made narrow enough for
	// 3 per row (3+3+3, a 342 DIP list) and short enough (340 DIP) that the list must scroll.
	// Synthetic on purpose: it exercises BalancedRowsPanel inside the capped ScrollViewer.
	private const double TinyWorkAreaWidthDip = 400;
	private const double TinyWorkAreaHeightDip = 340;

	// Physical work areas the placement is checked against, with the scale each is used at.
	private static readonly (string Name, CursorWorkArea Area, double Scale)[] Screens =
	[
		("1920x1040@100", new CursorWorkArea(0, 0, 1920, 1040), 1.0),
		("1920x1040@125", new CursorWorkArea(0, 0, 1920, 1040), 1.25),
		("1920x1040@150", new CursorWorkArea(0, 0, 1920, 1040), 1.5),
		("1024x728@150", new CursorWorkArea(0, 0, 1024, 728), 1.5),
	];

	[VisualSnapshotFact]
	public void Render_all_states_and_write_audit_report()
	{
		PickerXamlProbe.ProbeResult setup = PickerXamlProbe.Run();
		_ = setup.Failure.Should().BeNull();
		_ = Directory.CreateDirectory(OutDir);

		StringBuilder report = new();
		List<string> problems = [];
		CultureInfo savedCulture = LocalizationService.CurrentCulture;

		try
		{
			foreach (string lang in Languages)
			{
				LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo(lang);
				foreach ((string themeName, ThemeVariant variant) in Themes)
				{
					foreach (PickerState state in Enum.GetValues<PickerState>())
					{
						RenderPicker(state, lang, themeName, variant, report, problems);
					}

					RenderSettings(lang, themeName, variant, report, problems, appearance: false);
					RenderSettings(lang, themeName, variant, report, problems, appearance: true);
				}
			}

			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			WriteContrastTable(report, problems);
		}
		finally
		{
			LocalizationService.CurrentCulture = savedCulture;
		}

		_ = report.Insert(0, "PROBLEMS (" + problems.Count + ")\n" + string.Join("\n", problems) + "\n\n");
		File.WriteAllText(Path.Combine(OutDir, "report.txt"), report.ToString(), Encoding.UTF8);
		output.WriteLine($"report: {Path.Combine(OutDir, "report.txt")}, problems: {problems.Count}");
	}

	// ---------------------------------------------------------------- picker

	private static void RenderPicker(PickerState state, string lang, string themeName, ThemeVariant variant, StringBuilder report, List<string> problems)
	{
		int cardCount = state switch
		{
			PickerState.Cards9 or PickerState.Cards9Capped or PickerState.Cards9Narrow or PickerState.Cards9HorizontalCapped => 9,
			PickerState.ProfilesOverflow => 8,
			PickerState.Cards5 => 5,
			PickerState.FirstRun or PickerState.FirstRunFailed => 0,
			PickerState.Cards3 or PickerState.Vertical3 or PickerState.Profiles3 or PickerState.Icons3
				or PickerState.LaunchFailed or PickerState.ErrorWaiting or PickerState.RepairNotice or PickerState.ShortcutsOpen => 3,
			_ => 3,
		};
		PickerViewModel vm = new("https://www.example.com/articles/2026/10/a-long-readable-path?utm_source=newsletter&id=42", Snapshot(cardCount, state))
		{
			IsVerticalLayout = state is PickerState.Vertical3 or PickerState.Cards9Capped,
		};
		if (cardCount == 0)
		{
			vm.SetAddManuallyAvailable(true);
			vm.ShowFirstRunPrompt();
		}

		if (state == PickerState.RepairNotice)
		{
			vm.ShowConfigNotice();
		}

		double workWidth = state == PickerState.Cards9Narrow ? NarrowWorkAreaDip
			: state == PickerState.Cards9HorizontalCapped ? TinyWorkAreaWidthDip
			: WideWorkAreaDip;
		double workHeight = state == PickerState.Cards9Capped ? ShortWorkAreaDip
			: state == PickerState.Cards9HorizontalCapped ? TinyWorkAreaHeightDip
			: TallWorkAreaDip;
		PickerWindow window = new(vm, _ => { }, _ => { })
		{
			RequestedThemeVariant = variant,
			WorkAreaHeightDipOverride = workHeight,
			WorkAreaWidthDipOverride = workWidth,
		};
		// The constructor measured against the real monitor; re-measure against the override.
		window.ApplyMeasuredHeight();

		if (state == PickerState.Icons3)
		{
			LoadRealIcons(vm);
		}

		string id = $"picker-{Slug(state)}-{lang}-{themeName}";
		try
		{
			ShowOffScreen(window);

			switch (state)
			{
				case PickerState.FirstRun:
					window.FocusFirstRunPrimary();
					break;
				case PickerState.FirstRunFailed:
					vm.ShowActionFailure(LocalizationService.GetString("Status.Target.AvailableBrowsers"), "consent.discovery.failed");
					window.FocusFirstRunPrimary();
					break;
				case PickerState.ShortcutsOpen:
					window.FocusCard(0);
					Settle(window);
					vm.ToggleDetails();
					break;
				case PickerState.LaunchFailed:
					window.FocusCard(0);
					Settle(window);
					vm.ShowLaunchFailure("Google Chrome", "launch.start-failed");
					break;
				case PickerState.ErrorWaiting:
					window.FocusCard(0);
					Settle(window);
					vm.ShowLaunchFailure("Google Chrome", "launch.start-failed");
					vm.ShowForwardWaiting();
					break;
				case PickerState.Cards9Capped:
				case PickerState.Cards9HorizontalCapped:
					_ = vm.FocusSlot(9);
					window.FocusCard(vm.FocusedIndex);
					break;
				case PickerState.Cards3:
				case PickerState.Cards5:
				case PickerState.Cards9:
				case PickerState.Cards9Narrow:
				case PickerState.Vertical3:
				case PickerState.Profiles3:
				case PickerState.ProfilesOverflow:
				case PickerState.Icons3:
				case PickerState.RepairNotice:
				default:
					window.FocusCard(0);
					break;
			}

			Settle(window);
			string png = Capture(window, id);

			_ = report.AppendLine(CultureInfo.InvariantCulture, $"=== {id}  ({png})");
			CheckTiles(window, id, workWidth, report, problems);
			Control content = (Control)window.Content!;
			double desired = content.DesiredSize.Height;
			double client = window.ClientSize.Height;
			_ = report.AppendLine(CultureInfo.InvariantCulture, $"  window {window.ClientSize.Width:0}x{client:0} DIP, content desired height {desired:0.#}");
			if (desired > client + 0.5)
			{
				problems.Add($"[HIGH] {id}: content {desired:0.#} DIP taller than window {client:0.#} DIP (clipped)");
			}
			else if (client - desired > 24)
			{
				problems.Add($"[LOW] {id}: window {client - desired:0} DIP taller than content (empty band)");
			}

			IInputElement? focused = window.FocusManager?.GetFocusedElement();
			bool ring = focused is Control fc && fc.Classes.Contains(":focus-visible");
			_ = report.AppendLine(CultureInfo.InvariantCulture, $"  focused: {Describe(focused)}  ring(:focus-visible)={ring}");
			if (focused is Button && !ring)
			{
				problems.Add($"[MEDIUM] {id}: focused button has no visible focus ring");
			}

			CheckTextFit(window, id, problems);
			if (lang == "en" || state is PickerState.FirstRun or PickerState.ShortcutsOpen)
			{
				ListAutomation(window, id, report, problems);
			}

			WalkTabOrder(window, id, report, problems);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// ---------------------------------------------------------------- settings

	private static void RenderSettings(string lang, string themeName, ThemeVariant variant, StringBuilder report, List<string> problems, bool appearance)
	{
		SettingsViewModel vm = new();
		if (appearance)
		{
			vm.SelectSectionCommand.Execute(SettingsViewModel.SectionIds.Appearance);
		}

		SettingsWindow window = new(vm) { RequestedThemeVariant = variant };
		string id = $"settings-{(appearance ? "appearance" : "home")}-{lang}-{themeName}";
		try
		{
			ShowOffScreen(window);
			Settle(window);
			string png = Capture(window, id);
			_ = report.AppendLine(CultureInfo.InvariantCulture, $"=== {id}  ({png})");
			_ = report.AppendLine(CultureInfo.InvariantCulture, $"  window {window.ClientSize.Width:0}x{window.ClientSize.Height:0} DIP");
			CheckTextFit(window, id, problems);
			if (lang == "en")
			{
				ListAutomation(window, id, report, problems);
			}

			WalkTabOrder(window, id, report, problems);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	// ---------------------------------------------------------------- checks

	// Tiles: how many rows, that every tile sits inside the window, and where the window lands
	// on each reference screen (centred, inside the work area, nothing off the edge).
	private static void CheckTiles(PickerWindow window, string id, double workWidthDip, StringBuilder report, List<string> problems)
	{
		Rect client = new(window.ClientSize);
		List<Button> tiles = [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("choiceCard") && b.IsEffectivelyVisible)];
		if (tiles.Count > 0)
		{
			List<Rect> rects = [.. tiles.Select(t => new Rect(t.TranslatePoint(default, window) ?? default, t.Bounds.Size))];
			int rows = rects.Select(r => Math.Round(r.Y)).Distinct().Count();
			int perRow = rects.GroupBy(r => Math.Round(r.Y)).Max(g => g.Count());
			_ = report.AppendLine(CultureInfo.InvariantCulture, $"  tiles {tiles.Count}: {rows} row(s), up to {perRow} per row, tile {rects[0].Width:0}x{rects[0].Height:0} DIP");
			foreach (Rect r in rects.Where(r => r.X < -0.5 || r.Right > client.Right + 0.5))
			{
				problems.Add($"[HIGH] {id}: tile at x {r.X:0} runs past the window edge ({client.Width:0} DIP)");
			}
		}

		foreach ((string name, CursorWorkArea area, double scale) in Screens)
		{
			// The window's DIP width was chosen for ONE work-area width; check it only on screens of
			// that width class, so a wide-screen layout is not judged against the narrow screen.
			bool narrowScreen = area.Width / scale < 900;
			if (narrowScreen != (workWidthDip < 900))
			{
				continue;
			}

			CursorPlacementResult p = CursorPlacement.ComputeCentered(window.Width, window.Height, scale, area);
			bool inside = p.X >= area.X && p.Y >= area.Y && p.X + p.WindowWidthPx <= area.X + area.Width && p.Y + p.WindowHeightPx <= area.Y + area.Height;
			_ = report.AppendLine(CultureInfo.InvariantCulture, $"  on {name}: {p.WindowWidthPx}x{p.WindowHeightPx} px at ({p.X},{p.Y}) {(inside ? "inside" : "OUTSIDE")}");
			if (!inside)
			{
				problems.Add($"[HIGH] {id}: window {p.WindowWidthPx}x{p.WindowHeightPx} px does not fit the {name} work area");
			}
		}
	}

	// A label "does not fit" when its layout collapsed a line (ellipsis), when the laid-out text
	// is larger than the box the parent gave it, or when the box itself sticks out of the
	// enclosing button or the window client area.
	private static void CheckTextFit(Window window, string id, List<string> problems)
	{
		Rect client = new(window.ClientSize);
		foreach (TextBlock tb in window.GetVisualDescendants().OfType<TextBlock>())
		{
			if (!tb.IsEffectivelyVisible || string.IsNullOrEmpty(tb.Text) || tb.Bounds.Width <= 0)
			{
				continue;
			}

			TextLayout layout = tb.TextLayout;
			bool collapsed = layout.TextLines.Any(line => line.HasCollapsed);
			bool tooTall = layout.Height > tb.Bounds.Height - tb.Padding.Top - tb.Padding.Bottom + 0.75;
			bool tooWide = layout.WidthIncludingTrailingWhitespace > tb.Bounds.Width - tb.Padding.Left - tb.Padding.Right + 0.75
				&& tb.TextWrapping == TextWrapping.NoWrap && tb.TextTrimming == TextTrimming.None;

			// Vertical overflow inside a ScrollViewer is scrollable, not clipped; horizontal scrolling
			// is disabled everywhere in these windows, so horizontal overflow always counts.
			Point? origin = tb.TranslatePoint(default, window);
			bool inScroller = tb.FindAncestorOfType<ScrollViewer>() is not null;
			bool outsideWindow = origin is { } o
				&& (o.X < client.Left - 0.5 || o.X + tb.Bounds.Width > client.Right + 0.5
					|| (!inScroller && (o.Y < -0.5 || o.Y + tb.Bounds.Height > client.Bottom + 0.5)));

			Button? host = tb.FindAncestorOfType<Button>();
			bool outsideButton = false;
			if (host is not null && tb.TranslatePoint(default, host) is { } inHost)
			{
				outsideButton = !new Rect(host.Bounds.Size).Inflate(0.5).Contains(new Rect(inHost, tb.Bounds.Size));
			}

			bool wrappedButton = host is not null && layout.TextLines.Count > 1 && !host.Classes.Contains("choiceCard");

			if (collapsed || tooTall || tooWide || outsideWindow || outsideButton || wrappedButton)
			{
				string flags = string.Join(",", new[]
				{
					collapsed ? "ellipsis" : null, tooTall ? "clipped-height" : null, tooWide ? "clipped-width" : null,
					outsideWindow ? "outside-window" : null, outsideButton ? "outside-button" : null,
					wrappedButton ? $"button-label-wraps({layout.TextLines.Count} lines)" : null,
				}.Where(f => f is not null));
				string sev = outsideWindow || outsideButton || tooTall ? "HIGH" : wrappedButton ? "MEDIUM" : "LOW";
				// The width the label was ALLOWED (its MaxWidth, and the host button's inner width), so a
				// trimmed label shows whether padding or the text itself is the limit.
				string room = host is null ? string.Empty
					: $", maxWidth {tb.MaxWidth:0.#}, host inner {host.Bounds.Width - host.Padding.Left - host.Padding.Right - host.BorderThickness.Left - host.BorderThickness.Right:0.#}";
				problems.Add($"[{sev}] {id}: text '{Trim(tb.Text)}' {flags} (at {origin?.X:0},{origin?.Y:0}, box {tb.Bounds.Width:0.#}x{tb.Bounds.Height:0.#}, text {layout.WidthIncludingTrailingWhitespace:0.#}x{layout.Height:0.#}{room})");
			}
		}
	}

	private static void WalkTabOrder(Window window, string id, StringBuilder report, List<string> problems)
	{
		List<Control> reachable = [.. window.GetVisualDescendants().OfType<Control>()
			.Where(c => c.Focusable && c.IsTabStop && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)];

		IInputElement? start = window.FocusManager?.GetFocusedElement() ?? reachable.FirstOrDefault();
		List<string> order = [];
		HashSet<IInputElement> seen = [];
		IInputElement? current = start;
		bool cycled = false;
		for (int step = 0; step < 80 && current is not null; step++)
		{
			IInputElement? next = KeyboardNavigationHandler.GetNext(current, NavigationDirection.Next);
			if (next is null)
			{
				break;
			}

			if (!seen.Add(next))
			{
				cycled = ReferenceEquals(next, start) || seen.Contains(next);
				break;
			}

			order.Add(Describe(next));
			current = next;
		}

		_ = report.AppendLine(CultureInfo.InvariantCulture, $"  tab order from {Describe(start)}: {string.Join(" -> ", order)}{(cycled ? " -> (cycles)" : " -> (STOPS)")}");
		if (!cycled)
		{
			problems.Add($"[HIGH] {id}: Tab traversal did not cycle (possible trap/dead end)");
		}

		List<string> unreached = [.. reachable.Where(c => !seen.Contains(c) && !ReferenceEquals(c, start)).Select(Describe)];
		if (unreached.Count > 0)
		{
			problems.Add($"[HIGH] {id}: not reachable by Tab: {string.Join("; ", unreached)}");
		}
	}

	private static void ListAutomation(Window window, string id, StringBuilder report, List<string> problems)
	{
		_ = report.AppendLine("  automation (focusable, visible):");
		foreach (Control c in window.GetVisualDescendants().OfType<Control>().Where(c => c.Focusable && c.IsEffectivelyVisible))
		{
			AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(c);
			string name = peer.GetName() ?? string.Empty;
			_ = report.AppendLine(CultureInfo.InvariantCulture, $"    {peer.GetAutomationControlType(),-10} tab={c.IsTabStop,-5} name='{Trim(name)}'  [{c.GetType().Name}{(c.Name is { } n ? "#" + n : string.Empty)}]");
			if (string.IsNullOrWhiteSpace(name))
			{
				problems.Add($"[HIGH] {id}: focusable {c.GetType().Name}{(c.Name is { } nn ? "#" + nn : string.Empty)} has no automation name");
			}
		}
	}

	// ---------------------------------------------------------------- contrast

	private static readonly (string Fg, string Bg, double Min, string Use)[] ContrastPairs =
	[
		("bc.text.primary", "bc.bg.base", 4.5, "window text"),
		("bc.text.primary", "bc.bg.card", 4.5, "card title"),
		("bc.text.primary", "bc.bg.subtle", 4.5, "card title, hover"),
		("bc.text.primary", "bc.focus.inner", 4.5, "card title, focused"),
		("bc.text.secondary", "bc.bg.base", 4.5, "hints, '?' key"),
		("bc.text.secondary", "bc.bg.card", 4.5, "muted card text"),
		("bc.text.secondary", "bc.bg.subtle", 4.5, "muted, hover"),
		("bc.text.secondary", "bc.focus.inner", 4.5, "profile line / letter plate, focused tile"),
		("bc.text.secondary", "bc.bg.card", 4.5, "letter on the icon plate"),
		("bc.accent.onAccent", "bc.accent.default", 4.5, "primary button text"),
		("bc.accent.onAccent", "bc.accent.hover", 4.5, "primary hover"),
		("bc.accent.onAccent", "bc.accent.pressed", 4.5, "primary pressed"),
		("bc.accent.default", "bc.bg.base", 4.5, "info status text (12px)"),
		("bc.status.danger.onSurface", "bc.bg.base", 4.5, "error status text (12px)"),
		("bc.status.warning", "bc.bg.base", 4.5, "Settings warning text"),
		("bc.status.warning", "bc.bg.card", 4.5, "Settings warning on card"),
		("bc.text.disabled", "bc.bg.card", 4.5, "disabled text (WCAG-exempt)"),
		("bc.focus.outer", "bc.bg.base", 3.0, "focus ring vs window"),
		("bc.focus.outer", "bc.bg.card", 3.0, "focus ring vs card/panel"),
		("bc.focus.outer", "bc.focus.inner", 3.0, "focus ring vs focused fill"),
		// The primary's focus ring is two-tone: the inner token borders the fill, the outer token
		// is spread outside it over the panel (PickerWindow.axaml, Button.primary:focus-visible).
		// (outer vs inner is the "focus ring vs focused fill" row above.)
		("bc.focus.inner", "bc.accent.default", 3.0, "primary ring inner vs fill"),
		("bc.border.strong", "bc.bg.base", 3.0, "secondary button outline"),
		("bc.accent.default", "bc.bg.card", 3.0, "accent on card (first-run)"),
		("bc.border.strong", "bc.bg.base", 3.0, "'?' key outline"),
	];

	private static void WriteContrastTable(StringBuilder report, List<string> problems)
	{
		PickerWindow window = new(new PickerViewModel("https://example.com/", Snapshot(1, PickerState.Cards3)), _ => { }, _ => { });
		_ = report.AppendLine().AppendLine("CONTRAST (WCAG 2.x ratio; 4.5 text, 3.0 UI parts)");
		foreach ((string themeName, ThemeVariant variant) in Themes)
		{
			foreach ((string fg, string bg, double min, string use) in ContrastPairs)
			{
				// Translucent tokens (the HC Fluent aliases) are composited the way they are painted:
				// the background over the opaque window base, then the foreground over that.
				Color? baseColor = Resolve(window, "bc.bg.base", variant);
				Color? b = Resolve(window, bg, variant) is { } rawB && baseColor is { } bb ? Over(rawB, Over(bb, Colors.White)) : null;
				Color? f = Resolve(window, fg, variant) is { } rawF && b is { } bv ? Over(rawF, bv) : null;
				if (f is null || b is null)
				{
					problems.Add($"[HIGH] contrast {themeName}: {fg} or {bg} did not resolve");
					_ = report.AppendLine(CultureInfo.InvariantCulture, $"  {themeName,-5} {fg} on {bg}: UNRESOLVED");
					continue;
				}

				double ratio = Ratio(f.Value, b.Value);
				bool pass = ratio >= min;
				_ = report.AppendLine(CultureInfo.InvariantCulture, $"  {themeName,-5} {fg,-28} {Hex(f.Value)} on {bg,-18} {Hex(b.Value)}  {ratio,5:0.00}:1  need {min}  {(pass ? "PASS" : "FAIL")}  {use}");
				if (!pass && !fg.Contains("disabled", StringComparison.Ordinal))
				{
					problems.Add($"[{(min >= 4.5 ? "HIGH" : "MEDIUM")}] contrast {themeName}: {fg} on {bg} = {ratio:0.00}:1 < {min} ({use})");
				}
			}
		}

		window.Close();
	}

	private static Color? Resolve(Window window, string key, ThemeVariant variant)
	{
		return !window.TryGetResource(key, variant, out object? value)
			? null
			: value switch
			{
				Color c => c,
				ISolidColorBrush s => s.Color,
				_ => null,
			};
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

	internal static double Ratio(Color a, Color b)
	{
		double la = Luminance(a), lb = Luminance(b);
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
	}

	private static double Luminance(Color c)
	{
		static double Lin(byte v)
		{
			double s = v / 255d;
			return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
		}

		return (0.2126 * Lin(c.R)) + (0.7152 * Lin(c.G)) + (0.0722 * Lin(c.B));
	}

	// ---------------------------------------------------------------- helpers

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

	private static string Capture(Window window, string id)
	{
		const double scale = 2;
		Size size = window.ClientSize;
		window.Opacity = 1; // the window is at -32000,-32000 and never activated; opacity only matters to the renderer
		using RenderTargetBitmap bitmap = new(
			new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)),
			new Vector(96 * scale, 96 * scale));
		bitmap.Render(window);
		window.Opacity = 0;
		string path = Path.Combine(OutDir, id + ".png");
		bitmap.Save(path);
		return Path.GetFileName(path);
	}

	private static string Describe(IInputElement? element)
	{
		if (element is not Control c)
		{
			return "(none)";
		}

		string name = ControlAutomationPeer.CreatePeerForElement(c).GetName() ?? string.Empty;
		return $"{c.GetType().Name}{(c.Name is { } n ? "#" + n : string.Empty)}'{Trim(name, 28)}'";
	}

	private static string Trim(string text, int max = 60)
	{
		string flat = text.Replace('\n', ' ').Replace('\t', ' ');
		return flat.Length <= max ? flat : flat[..max] + "…";
	}

	private static string Hex(Color c)
	{
		return string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
	}

	private static string Slug(PickerState state)
	{
		return state switch
		{
			PickerState.Cards3 => "cards3",
			PickerState.Cards5 => "cards5",
			PickerState.Cards9 => "cards9",
			PickerState.Cards9Narrow => "cards9-narrow",
			PickerState.Cards9Capped => "cards9-capped",
			PickerState.Vertical3 => "vertical3",
			PickerState.Profiles3 => "profiles3",
			PickerState.ProfilesOverflow => "profiles-overflow",
			PickerState.Cards9HorizontalCapped => "cards9-hcapped",
			PickerState.Icons3 => "icons3",
			PickerState.FirstRun => "firstrun",
			PickerState.FirstRunFailed => "firstrun-failed",
			PickerState.LaunchFailed => "launch-failed",
			PickerState.ErrorWaiting => "error-waiting",
			PickerState.RepairNotice => "repair-notice",
			PickerState.ShortcutsOpen => "shortcuts",
			_ => "unknown",
		};
	}

	private static BrowserSnapshot Snapshot(int count, PickerState state)
	{
		BrowserCandidate[] candidates = [.. BrowserNames.Take(count).Select((name, i) =>
			new BrowserCandidate($"browser-{i + 1}", name, ExeFor(state, i), "stable", "known-path"))];

		// Made-up profile names (one long, to exercise trimming); never a real person's data.
		// Real Chromium ids (LaunchHelperArguments.ResolveFamily), or no profile tile ever renders.
		// Profiles3: 3 browsers -> Edge 2 + Chrome 3 + Firefox 1 = 6 tiles, all fit.
		// ProfilesOverflow: 8 browsers take 8 of the 9 slots, so Chrome (4 profiles) gets ONE extra
		// tile and its last tile shows "+2".
		if (state is PickerState.Profiles3 or PickerState.ProfilesOverflow)
		{
			candidates[0] = candidates[0] with { BrowserId = "edge-stable", ProfileIds = ["Default", "Profile 1"], ProfileNames = ["Personal", "Work"] };
			candidates[1] = state == PickerState.Profiles3
				? candidates[1] with { BrowserId = "chrome-stable", ProfileIds = ["Profile 2", "Default", "Profile 3"], ProfileNames = ["Work - Design team", "Personal", "Testing"] }
				: candidates[1] with { BrowserId = "chrome-stable", ProfileIds = ["Default", "Profile 1", "Profile 2", "Profile 3"], ProfileNames = ["Personal", "Work - Design team", "Testing", "Travel"] };
		}

		if (state == PickerState.ProfilesOverflow)
		{
			// Only Chrome may split here: Edge keeps one tile so the overflow lands on Chrome.
			candidates[0] = candidates[0] with { ProfileIds = ["Default"], ProfileNames = ["Personal"] };
		}

		return count == 0 ? BrowserSnapshot.Empty : new BrowserSnapshot(candidates, count, DateTimeOffset.UtcNow);
	}

	// Icons3: the first tile points at the real installed Edge (when present); the others at
	// paths that do not exist, so the same image shows a real icon next to the letter fallback.
	private static string ExeFor(PickerState state, int index)
	{
		string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
		return state == PickerState.Icons3 && index == 0 && File.Exists(edge) ? edge : $@"C:\Browsers\b{index + 1}.exe";
	}

	// Loads icons through the production loader and reader at 2x (the capture scale), then
	// applies them on this (UI) thread, which is what the window's own Post would do.
	private static void LoadRealIcons(PickerViewModel vm)
	{
		Icons.BrowserIconLoader loader = new(
			new Platform.Windows.Icons.ShellIconReader(),
			Microsoft.Extensions.Logging.Abstractions.NullLogger<Icons.BrowserIconLoader>.Instance);
		foreach (ChoiceCardViewModel card in vm.Cards)
		{
			card.Icon = loader.LoadAsync(card.Candidate.ExePath, 96).GetAwaiter().GetResult();
		}
	}
}
