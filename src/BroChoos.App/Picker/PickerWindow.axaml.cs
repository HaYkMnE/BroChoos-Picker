using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace BroChoos.App.Picker;

internal partial class PickerWindow : Window
{
	// Fixed DIP WIDTH: the placement math (pre-show, D-149/D2) must know the exact size
	// the window WILL have, so SizeToContent stays Manual and the height is derived from
	// the card count before the window is shown.
	//
	// THE HEIGHT IS NOW MEASURED, NOT EXTRAPOLATED. It used to be a linear formula over
	// hardcoded constants (170 + n*62 + spacing), and those constants had drifted 15-78 DIP
	// ABOVE the real layout, so the window was systematically too tall and the empty-space
	// error grew with the card count. The formula could not be kept honest because it
	// restated the layout by hand: every token, padding or font change silently invalidated
	// it, and nothing failed when it did.
	//
	// The replacement measures the ACTUAL content at the fixed width. This is possible
	// before Show because Avalonia performs layout on demand: Measure() on the content root
	// runs the same measure pass the window would run, with no windowing backend involved
	// (MEASURED: pre-show content measurement matches the shown client height exactly, and
	// it needs no HWND, so it is safe in the pre-show placement window). Only the chrome
	// height - which is zero here, SystemDecorations="None" - would have to be added back
	// if decorations were ever enabled.
	private const double WidthDip = 460;

	// TILE ROW WIDTH (docs/design/PICKER-LAYOUT-SPEC.md). The window is as wide as its WIDEST tile
	// row: window padding (12 + 12) plus one 96 DIP slot (88 tile + 2 x 4 margin) per tile. Rows are
	// balanced (BalancedRowsPanel: 9 -> 5+4), so the widest row is ceil(n / rows) tiles, not the
	// per-row maximum. The floor keeps the first-run screen, status lines and the config notice
	// readable with one to three browsers.
	internal const int MaxTilesPerRow = 8;
	internal const double TileSlotDip = 96;
	internal const double WindowHorizontalPaddingDip = 24;
	internal const double MinimumWidthDip = 360;
	internal const double FirstRunWidthDip = 460;

	// Every tile grows by the profile line when any tile shows one (PickerWindow.axaml).
	private void ApplyProfileRowClass()
	{
		CardsItems.Classes.Set("withProfiles", _vm.Cards.Any(card => card.HasProfile));
	}

	internal static double WidthForTileCount(int tileCount, bool vertical = false, double? workAreaWidthDip = null)
	{
		// No tiles = the first-run screen, whose two side-by-side buttons need the old 460 DIP
		// (Russian "Add manually" measured 126 DIP of text in a 121 DIP button at 360).
		if (tileCount <= 0)
		{
			return FirstRunWidthDip;
		}

		// Vertical: one tile per row, so the window is the 360 DIP floor. Horizontal: the widest
		// balanced row.
		int perRow = vertical
			? Math.Min(Math.Max(tileCount, 0), 1)
			: BalancedRowsPanel.RowSizes(tileCount, TilesPerRowFor(workAreaWidthDip))[0];
		return Math.Max(MinimumWidthDip, WindowHorizontalPaddingDip + (perRow * TileSlotDip));
	}

	// Hands the per-row maximum for this work area to the realized tile panel. The ItemsPanel is a
	// template, so the panel instance only exists once the ItemsControl has built it; both the
	// pre-show measurement and every width change call this.
	private void ApplyTilesPerRow()
	{
		if (CardsItems.ItemsPanelRoot is BalancedRowsPanel panel)
		{
			panel.MaxPerRow = TilesPerRowFor(_workAreaWidthDip);
		}
	}

	// NARROW SCREENS: as many 96 DIP slots as the work area holds after the window padding, 3 to 8.
	// 1024 px at 150 % is 682 DIP -> 6 per row (600 DIP = 900 px), so a row never runs off screen.
	internal const int MinTilesPerRow = 3;

	internal static int TilesPerRowFor(double? workAreaWidthDip)
	{
		return workAreaWidthDip is { } width && double.IsFinite(width) && width > 0
			? Math.Clamp((int)Math.Floor((width - WindowHorizontalPaddingDip) / TileSlotDip), MinTilesPerRow, MaxTilesPerRow)
			: MaxTilesPerRow;
	}

	// TEST SEAM: a work-area width in DIP that replaces the monitor query. Null in production.
	internal double? WorkAreaWidthDipOverride { get; set; }

	private double? _workAreaWidthDip;

	// PROGRAM ICONS (picker layout spec). Null in the off-screen probes: the tiles then keep their
	// letter plates, which is also the permanent state when an icon cannot be read.
	internal Icons.BrowserIconLoader? IconLoader { get; set; }

	private int _iconSizePx = (int)Icons.BrowserIconLoader.IconDip;

	// Starts one background load per tile and returns at once. Each icon is applied on the UI
	// thread when it arrives; the plate and the image share one 48 DIP box, so the swap never
	// changes the measured height or moves a tile.
	internal void RequestIcons(int sizePx)
	{
		_iconSizePx = sizePx;
		RequestIconsForCards();
	}

	private void RequestIconsForCards()
	{
		if (IconLoader is not { } loader)
		{
			return;
		}

		foreach (ChoiceCardViewModel card in _vm.Cards)
		{
			if (!card.HasIcon)
			{
				_ = ApplyIconWhenReadyAsync(loader, card, _iconSizePx);
			}
		}
	}

	private static async Task ApplyIconWhenReadyAsync(Icons.BrowserIconLoader loader, ChoiceCardViewModel card, int sizePx)
	{
		Avalonia.Media.IImage? icon = await loader.LoadAsync(card.Candidate.ExePath, sizePx).ConfigureAwait(false);
		if (icon is not null)
		{
			Dispatcher.UIThread.Post(() => card.Icon = icon);
		}
	}

	// A floor for pathological content (a very short status line at a tiny font) so the
	// window is never handed a degenerate height by the measurement.
	private const double MinimumHeightDip = 1;

	// Sub-pixel slack: a re-pin below this is layout noise, not a real content change, and
	// acting on it would resize the window on every layout pass.
	private const double HeightEpsilonDip = 0.5;

	// The pseudo-class the visible focus treatment hangs off (PickerWindow.axaml). Applying it
	// is how the FOCUSED geometry is obtained without restating the padding and border numbers
	// by hand - the same reason the height is measured rather than extrapolated.
	private const string FocusVisiblePseudoClass = ":focus-visible";

	// The card the launch contract focuses at first frame (slot 1 -> index 0; see
	// PickerSession.OnWindowOpened). Focus is applied on EVERY launch, so the pre-show height
	// must already be the FOCUSED height or the window visibly resizes at first frame.
	private const int FirstFrameFocusIndex = 0;

	// Width perturbation used to force a genuine re-measure after the focus reservation is
	// applied. A pseudo-class change invalidates the CONTROL's style but NOT the parent's
	// cached measurement, so re-measuring at the same constraint returns the stale height
	// (MEASURED: 238 before and after the ring was applied). Measuring once at a different
	// constraint and once at the real one is what drops that cache. The perturbation is
	// sub-pixel on purpose: it must be far too small to change any real layout outcome, only
	// the cache identity.
	private const double CacheProbeWidthEpsilonDip = 0.5;

	// When the tile list has to scroll, it keeps at least two tile rows (tile height plus its top
	// and bottom margin, twice), so a scrolled list still shows the focused tile and a hint that
	// more follow. On a work area too short even for that, the window overflows rather than hiding
	// every tile. The height is read from the same tokens the layout uses (MinimumScrolledListDip).
	private const int MinimumScrolledCardRows = 2;
	private const string TileHeightTokenKey = "bc.tile.height";
	private const string TileHeightWithProfileTokenKey = "bc.tile.heightWithProfile";
	private const string TileMarginTokenKey = "bc.tile.margin";

	// Only used if the tokens cannot be resolved (never expected: PickerTokens.axaml is merged).
	private const double MinimumScrolledListFallbackDip = 228;

	// The work-area height the card list is capped against, captured pre-show (see
	// FitCardListToWorkArea). Null when no monitor could be read: no cap, the old behaviour.
	private double? _workAreaLimitDip;
	private bool _refitPosted;

	private readonly PickerViewModel _vm;
	private readonly Action<PickerKeyAction> _onKey;
	private readonly Action<ChoiceCardViewModel> _onCardClicked;
	private readonly Action<Core.Abstractions.FirstRunAction>? _onFirstRunAction;
	private readonly Action? _onDismiss;
	private readonly Action? _onRepairSettings;
	private bool _isPinningHeight;

	// True while the pre-show measurement's focus reservation is still standing (see
	// ReserveFirstFrameFocus). Tracked so the reservation can be withdrawn if focus never
	// actually lands, and so it is applied exactly once.
	private bool _firstFrameFocusReserved;

	public PickerWindow(
		PickerViewModel viewModel,
		Action<PickerKeyAction> onKey,
		Action<ChoiceCardViewModel> onCardClicked,
		Action<Core.Abstractions.FirstRunAction>? onFirstRunAction = null,
		Action? onDismiss = null,
		Action? onRepairSettings = null)
	{
		_vm = viewModel;
		_onKey = onKey;
		_onCardClicked = onCardClicked;
		_onFirstRunAction = onFirstRunAction;
		_onDismiss = onDismiss;
		_onRepairSettings = onRepairSettings;

		DataContext = viewModel;
		InitializeComponent();

		// Vertical layout (Settings > Appearance): the same tiles in one centred column.
		if (viewModel.IsVerticalLayout)
		{
			CardsItems.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel
			{
				Orientation = Orientation.Vertical,
				HorizontalAlignment = HorizontalAlignment.Center,
			});
		}

		Width = WidthForTileCount(viewModel.Cards.Count, viewModel.IsVerticalLayout);
		ApplyProfileRowClass();

		// The status area is a programmatic focus landing spot only (see OnFirstRunPanelCollapsed);
		// Tab never stops on it.
		StatusArea.IsTabStop = false;
		UpdateStatusAreaAutomation();

		// Height is settled HERE, before any caller can place or show the window, so the
		// pre-show placement math always reads a real measured value.
		ApplyMeasuredHeight();

		// ...and then KEPT settled. The status line is a live region that appears, wraps
		// and clears AFTER the window is up (copied, launch failed, history cleared), and
		// the card list can be replaced by a later snapshot. A height fixed at
		// construction would clip both. The old card-count formula hid this by
		// over-reserving space permanently; a measurement must instead follow the
		// measurement, which is what this subscription does.
		if (Content is Control content)
		{
			content.LayoutUpdated += OnContentLayoutUpdated;
		}

		// TUNNELLING + handledEventsToo (locked spike lesson): with a bubbling
		// handler a focused Button swallows Enter before the window ever sees it.
		// The handler only marks picker hotkeys handled; Tab and everything else
		// fall through to native focus/typing behaviour (per-key policy).
		AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

		// Window-level and bubbling-with-eventsToo so it sees focus landing on ANY card,
		// whichever route moved it there. It exists solely to release the pre-show
		// measurement's focus reservation (see OnAnyGotFocus); it never touches focus itself.
		AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);

		// Remembers whether the config notice's repair button was the last control focused,
		// so focus can be rescued when that button goes disabled or hidden (see
		// MoveFocusOffUnusableRepairButton).
		AddHandler(GotFocusEvent, TrackRepairButtonFocus, RoutingStrategies.Bubble, handledEventsToo: true);

		// The other half of the same contract (finding N1): focus leaving the card list has to
		// drop the VM's rendered-focus proof, or Enter stays armed against a card that is no
		// longer visibly focused. Bubbling-with-eventsToo for the same reason as GotFocus: the
		// Button class handler marks nothing handled today, but the guard must not depend on that.
		AddHandler(LostFocusEvent, OnAnyLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);

		// FINDING F1-a (MEDIUM): REACTIVATION HAS TO PUT THE RING BACK.
		//
		// WindowBase.HandleActivated raises Activated and THEN calls FocusManager.SetFocusScope,
		// whose restore path is Focus(focused) with NavigationMethod.Unspecified - so real focus
		// lands on the card again but :focus-visible is deliberately NOT set (the framework sets
		// it only for Tab/Directional). The claim had already been dropped when the window lost
		// OS focus (finding N1), and the restore raises GotFocus with Unspecified - which the
		// window's own gate correctly treats as ringless - so nothing re-armed it. MEASURED
		// one-card: focusOnCardAfterReactivate=True, ringAfterReactivate=False,
		// claimAfterReactivate=False, and neither a digit re-press nor a Tab-shaped refocus could
		// recover it (Focus() on the already-focused element raises no GotFocus), leaving only
		// Esc. The handler POSTS rather than acting inline: Activated is raised BEFORE
		// SetFocusScope in the same call, so an inline re-assert would be overwritten by the
		// restore that follows it.
		Activated += OnWindowActivated;

		// W5.2 DISMISSAL CONTRACT (spec 3.3): "Click outside, Alt+Tab/focus loss, Win+D/desktop
		// switch, sleep, and lock dismiss the Picker."
		//
		// Deactivated is the single OS signal that covers the pointer/Alt+Tab/desktop-switch cases:
		// clicking another window or the desktop, Alt+Tabbing away, and Win+D all take activation
		// away from this window, which is exactly what raises Deactivated. Sleep and a session lock
		// do not guarantee an event before the system suspends or the session is locked, so they are
		// covered only as far as the platform reports a deactivation; a guaranteed pre-sleep/pre-lock
		// dismissal needs platform session hooks (WM_POWERBROADCAST / WTS_SESSION_CHANGE) and is
		// documented as remaining work rather than faked here.
		//
		// The handler is deliberately STATELESS: it reports every Deactivated and lets PickerSession
		// own the one-shot/terminal policy, because the session is the only place that knows whether a
		// launch is in flight or the run already completed.
		Deactivated += OnWindowDeactivated;

		// FINDING F1-b (MEDIUM): the Unknown-recovery status line advertises "Try again (Enter)"
		// while Enter is disarmed - the click that produced it is a pointer press (no ring,
		// proof dropped by B/N2). The view model raises this when that status is shown; the
		// window turns it into a visibly focused, armed card. See OnLaunchUnknownRecoveryShown.
		viewModel.LaunchUnknownRecoveryShown += OnLaunchUnknownRecoveryShown;

		// A snapshot replacement (first-run discovery) rebuilds the card list, and the
		// containers that carried real focus and its ring die with the old one - so focus has
		// to be re-established on the new list. The VM invalidates its rendered-focus proof
		// unconditionally on a replacement (no invisible claim, D-131); this subscription is
		// the other half, the one that puts real focus and the ring back.
		viewModel.PropertyChanged += OnViewModelPropertyChanged;
	}

	// FINDING F1-a, the reactivation half. Posted at Loaded so the re-assert is the LAST writer:
	// by then the framework's own SetFocusScope restore has run, and the card it left focused
	// (with no ring) is the one this re-asserts.
	private void OnWindowActivated(object? sender, EventArgs e)
	{
		Dispatcher.UIThread.Post(
			() =>
			{
				if (!IsVisible)
				{
					return;
				}

				if (_vm.Cards.Count == 0)
				{
					// First-run screen: with focus nowhere, put it back on the primary action.
					if (_vm.IsFirstRunPromptVisible && FocusManager?.GetFocusedElement() is null)
					{
						FocusFirstRunPrimary();
					}

					return;
				}

				// The restored focus names the card to re-assert. If no card holds it and focus is
				// nowhere, the VM's index is the only intent left, so restore it rather than leave
				// the picker focus-less. Focus on another control (a first-run button, the "?"
				// toggle) is left where the user put it.
				int slot = FocusedCardSlot();
				if (slot < 0)
				{
					if (FocusManager?.GetFocusedElement() is not null || _vm.FocusedIndex < 0)
					{
						return;
					}

					slot = _vm.FocusedIndex;
				}

				PickerLog.Marker(string.Create(CultureInfo.InvariantCulture, $"REACTIVATED reassert slot={slot + 1}"));
				FocusCard(slot);
			},
			DispatcherPriority.Loaded);
	}

	// W5.2 DISMISSAL CONTRACT, the OS half. The window has lost activation - click-outside on
	// another window/the desktop, Alt+Tab, Win+D, or (as far as the platform reports it) a lock.
	// The window does not decide anything here: it forwards the event to the session, which owns
	// the cancellation path (cancel-to-clipboard + Success exit) and the terminal guard, exactly
	// as it does for the second Esc. A null callback (the existing three-argument construction used
	// by the off-screen probes) is a no-op, so this handler cannot break a window built without it.
	private void OnWindowDeactivated(object? sender, EventArgs e)
	{
		PickerLog.Marker("WINDOW_DEACTIVATED");
		_onDismiss?.Invoke();
	}

	// FINDING F1-b, the recovery half: the status line promises Enter will retry, so the card the
	// recovery is about must end up visibly focused with the claim armed. The slot comes from the
	// caller that launched the card; -1 means the caller did not know it, and the VM's intent (or
	// the picker's resting state) is the documented fallback.
	private void OnLaunchUnknownRecoveryShown(int slot)
	{
		Dispatcher.UIThread.Post(
			() =>
			{
				if (!IsVisible || _vm.Cards.Count == 0)
				{
					return;
				}

				int target = slot >= 0 && slot < _vm.Cards.Count
					? slot
					: _vm.FocusedIndex >= 0 ? _vm.FocusedIndex : FirstFrameFocusIndex;

				PickerLog.Marker(string.Create(CultureInfo.InvariantCulture, $"LAUNCH_UNKNOWN reassert slot={target + 1}"));
				FocusCard(target);
			},
			DispatcherPriority.Loaded);
	}

	// The slot of the card that really holds focus, or -1. The destination is read from the focus
	// manager for the same reason OnAnyLostFocus reads it: it is the only authority on where
	// focus actually went.
	private int FocusedCardSlot()
	{
		return FocusManager?.GetFocusedElement() is Control { DataContext: ChoiceCardViewModel card }
			? card.Slot - 1
			: -1;
	}

	// Only the card-list replacement matters here: every other property change is display
	// state that never destroys a focused container. Focus is re-asserted at Loaded priority
	// because the ItemsControl regenerates containers during the layout pass this change
	// invalidates - focusing before that pass would race the realization and fail.
	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(PickerViewModel.StatusMessage))
		{
			UpdateStatusAreaAutomation();
		}

		if (e.PropertyName == nameof(PickerViewModel.IsDetailsOpen))
		{
			OnShortcutListToggled();
			return;
		}

		if (e.PropertyName == nameof(PickerViewModel.IsFirstRunPromptVisible) && !_vm.IsFirstRunPromptVisible)
		{
			OnFirstRunPanelCollapsed();
			return;
		}

		// The repair button goes disabled ("Repairing…") or the whole notice hides after a
		// successful repair. If it held focus, focus would be left on a control that cannot act
		// or is gone - so it lands on the status area, which is announcing the result.
		if (e.PropertyName is nameof(PickerViewModel.IsRepairRunning) or nameof(PickerViewModel.IsConfigNoticeVisible))
		{
			MoveFocusOffUnusableRepairButton();
			return;
		}

		if (e.PropertyName != nameof(PickerViewModel.Cards))
		{
			return;
		}

		// A replaced list (first-run discovery) can change the tile count, so the row width follows.
		Width = WidthForTileCount(_vm.Cards.Count, _vm.IsVerticalLayout, _workAreaWidthDip);
		ApplyTilesPerRow();
		ApplyProfileRowClass();
		RequestIconsForCards();

		Dispatcher.UIThread.Post(
			() =>
			{
				if (_vm.Cards.Count == 0)
				{
					return;
				}

				// The VM's index is the re-assert intent; with focus nowhere (it named no card
				// before the replacement) the picker's documented resting state is card 1.
				int target = _vm.FocusedIndex >= 0 ? _vm.FocusedIndex : FirstFrameFocusIndex;
				FocusCard(target);
			},
			DispatcherPriority.Loaded);
	}

	// An EMPTY status area is hidden from the screen reader (an unnamed group is noise) and is not
	// focusable (a focus stop with no name is a dead end); it rejoins the tree and becomes a focus
	// landing spot the moment a message appears, which is also when its live region speaks.
	private void UpdateStatusAreaAutomation()
	{
		bool hasMessage = !string.IsNullOrEmpty(_vm.StatusMessage);

		// The status area is the focus landing spot after Skip or a repair. If it HOLDS focus when
		// its message clears, focus moves to the first tile (or the window when there is none)
		// BEFORE it stops being focusable, so focus is never left on an element that is gone.
		if (!hasMessage && ReferenceEquals(FocusManager?.GetFocusedElement(), StatusArea))
		{
			if (_vm.Cards.Count > 0)
			{
				FocusCard(0);
			}
			else
			{
				_ = Focus();
			}
		}

		StatusArea.Focusable = hasMessage;
		Avalonia.Automation.AutomationProperties.SetAccessibilityView(
			StatusArea,
			hasMessage ? Avalonia.Automation.AccessibilityView.Control : Avalonia.Automation.AccessibilityView.Raw);
	}

	// Re-pins the window height to the content's measured height after any layout pass that
	// changed it. Terminates by construction: once Height equals the measurement the
	// epsilon test is false, so the layout pass this assignment triggers does not resize
	// again (verified: one resize per real content change, none while idle).
	private void OnContentLayoutUpdated(object? sender, EventArgs e)
	{
		if (_isPinningHeight || !IsVisible)
		{
			return;
		}

		double measured = MeasureContentHeightDip();
		if (Math.Abs(measured - Height) <= HeightEpsilonDip)
		{
			return;
		}

		// Content that grew past the work area after Show (a wrapped status line under nine
		// cards) is refitted by shrinking the list again - POSTED, because the refit drops the
		// layout cache and must not run inside this pass. The plain re-pin below still runs, so
		// the window is never left shorter than its content in the meantime.
		// The same refit also LIFTS a cap that is no longer needed (the status line cleared, the
		// shortcut list closed), so the list never scrolls while the screen has room for it.
		if (_workAreaLimitDip is { } limit
			&& (measured > limit + HeightEpsilonDip || double.IsFinite(CardsScroller.MaxHeight))
			&& !_refitPosted)
		{
			_refitPosted = true;
			Dispatcher.UIThread.Post(
				() =>
				{
					_refitPosted = false;
					FitCardListToWorkArea();
				},
				DispatcherPriority.Background);
		}

		_isPinningHeight = true;
		try
		{
			Height = measured;
		}
		finally
		{
			_isPinningHeight = false;
		}
	}

	// Measures the REAL content at the fixed width and adopts it as the window height.
	// Replaces the old card-count formula, which restated the layout by hand and had
	// drifted away from it; nothing here needs updating when a token or a font changes.
	//
	// PRE-SHOW ONLY (constructor and the session's pre-show re-pin). The live re-pin uses the
	// plain measurement instead: this one drops the layout cache, which must never happen from
	// inside a layout pass.
	internal void ApplyMeasuredHeight()
	{
		// The row width depends on the work area of the monitor the window opens on (narrow
		// screens get fewer tiles per row), so it is settled before anything is measured.
		(double? workAreaWidthDip, double? workAreaHeightDip) = CursorMonitorWorkAreaDip();
		_workAreaWidthDip = WorkAreaWidthDipOverride ?? workAreaWidthDip;
		Width = WidthForTileCount(_vm.Cards.Count, _vm.IsVerticalLayout, _workAreaWidthDip);
		// The card list sits in a ScrollViewer. Before Show, its presenter has not created its
		// child yet, so a measure would see an EMPTY list and hand the placement a height ~650 DIP
		// short for nine cards (MEASURED: 204 instead of 853). Building the presenter's child here
		// makes the pre-show measurement see the real list again.
		// MEASURED pre-show: the presenter exists but its Content template binding has not been
		// evaluated (Content=null, attached=False), so the presenter is pointed at the list it
		// would bind to anyway - the same object, so the template binding agrees once it runs.
		CardsScroller.ApplyTemplate();
		if (CardsScroller.Presenter is Avalonia.Controls.Presenters.ContentPresenter presenter)
		{
			presenter.Content ??= CardsScroller.Content;
			presenter.UpdateChild();
		}

		// First measure realizes the item containers, so the card the first frame will focus
		// actually exists to be reserved.
		_ = MeasureContentHeightDip();

		// That measure also built the tile panel, so it can now be told the per-row maximum for
		// this work area; the cache-dropping measure below then sees the balanced rows.
		ApplyTilesPerRow();

		// The height must be the one the window RESTS at, and the first frame always focuses
		// card 1 (PickerSession.OnWindowOpened). Measuring the unfocused geometry therefore
		// measured a size this window never rests at: :focus-visible shrinks the focused card's
		// padding 8->3 and grows its border 1->2, so the window was handed a height 8 DIP too
		// tall and visibly SHRANK when focus landed (MEASURED 238 -> 230). The reservation
		// below puts the measurement in the focused geometry instead.
		ReserveFirstFrameFocus();
		Height = MeasureContentHeightWithCacheDropped();

		// Visual audit: nine cards measured 853 DIP, taller than the usable height of a 1080p
		// screen at 125-150 %. The work area is read here, from the same monitor and DPI the
		// session's placement uses (the one under the cursor), so the height the session reads
		// back right after this call already fits.
		_workAreaLimitDip = WorkAreaHeightDipOverride ?? workAreaHeightDip;
		FitCardListToWorkArea();
	}

	// TEST SEAM: a work-area height in DIP that replaces the monitor query, so a test can prove
	// the cap on any desktop. Null in production.
	internal double? WorkAreaHeightDipOverride { get; set; }

	// Shrinks the CARD LIST (its ScrollViewer's MaxHeight), never the window's own Height, until
	// the measured window fits the work area. Capping the viewer keeps the window's measurement
	// and its layout the same thing, so the live re-pin (OnContentLayoutUpdated) still converges.
	//
	// PRE-SHOW, or POSTED from the live re-pin - never inline in a layout pass: it drops the
	// layout cache the same way ApplyMeasuredHeight does.
	private void FitCardListToWorkArea()
	{
		if (_workAreaLimitDip is not { } limit || !double.IsFinite(limit) || limit <= 0)
		{
			return;
		}

		// Start from the uncapped list, so a cap left by an earlier, longer state never outlives it.
		CardsScroller.MaxHeight = double.PositiveInfinity;
		double natural = MeasureContentHeightWithCacheDropped();
		if (natural <= limit + HeightEpsilonDip)
		{
			Height = natural;
			return;
		}

		double listNatural = CardsScroller.DesiredSize.Height;
		double floor = Math.Min(listNatural, MinimumScrolledListDip());
		CardsScroller.MaxHeight = Math.Max(floor, listNatural - (natural - limit));
		Height = MeasureContentHeightWithCacheDropped();
	}

	// Two tile rows, from the live token values.
	internal double MinimumScrolledListDip()
	{
		// The taller tile when any tile shows a profile line (withProfiles grows every tile).
		string heightKey = CardsItems.Classes.Contains("withProfiles") ? TileHeightWithProfileTokenKey : TileHeightTokenKey;
		return this.TryFindResource(heightKey, out object? height) && height is double heightDip
			&& this.TryFindResource(TileMarginTokenKey, out object? margin) && margin is Thickness marginDip
			? MinimumScrolledCardRows * (heightDip + marginDip.Top + marginDip.Bottom)
			: MinimumScrolledListFallbackDip;
	}

	// The usable height of the monitor under the cursor, in DIP of THAT monitor's effective DPI -
	// the same three calls, in the same order, as PickerSession.AssignPlacementBeforeShow, so the
	// window is capped against exactly the rectangle it will then be clamped into. Floored so the
	// session's rounding to physical pixels can never push the window one pixel past the edge.
	//
	// KNOWN RARE RACE: the cursor is read here and again in the session's placement, a few
	// microseconds apart. If it crosses onto another monitor between the two reads, the height cap
	// and the placement use different screens; the placement clamp still keeps the window on its
	// screen, only the cap may be wrong for that one launch. Left as is because sharing one read
	// would change the constructor/placement contract for a sub-millisecond window.
	private static (double? WidthDip, double? HeightDip) CursorMonitorWorkAreaDip()
	{
		if (!NativeMethods.GetCursorPos(out NativeMethods.Point cursor))
		{
			return (null, null);
		}

		IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
		NativeMethods.MonitorInfo info = new() { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
		if (!NativeMethods.GetMonitorInfo(monitor, ref info))
		{
			return (null, null);
		}

		double scale = NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MonitorDpiType.EffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0
			? dpiX / 96.0
			: 1.0;

		return (Math.Floor((info.rcWork.Right - info.rcWork.Left) / scale), Math.Floor((info.rcWork.Bottom - info.rcWork.Top) / scale));
	}

	// Applies the visible-focus treatment to the card the first frame will focus, so the
	// measurement is taken in the geometry the window actually rests in. The pseudo-class is
	// read from the same style the real focus path activates, so the reserved geometry cannot
	// drift from the real one when a token changes - which is the whole reason this file
	// measures instead of restating the layout.
	//
	// THE RESERVATION IS LEFT APPLIED, NOT ROLLED BACK. Rolling it back re-invalidates the
	// layout, and the window grows straight back to the unfocused height on the first pass
	// after Show (MEASURED: 230 -> 238 -> 230) - the reported defect, inverted. Left applied,
	// the height is 230 before Show, after Show and after the real focus lands (MEASURED).
	//
	// It is not a second owner of the focus ring: the framework clears the bit as soon as
	// focus moves to another control (MEASURED), the real focus path sets it anyway, and a
	// window-level GotFocus handler releases the reservation whenever focus lands anywhere
	// else (see OnAnyGotFocus). A card that is not focused therefore never keeps a ring.
	private void ReserveFirstFrameFocus()
	{
		if (_firstFrameFocusReserved)
		{
			return;
		}

		Button? button = FirstFrameFocusButton();
		if (button is null)
		{
			return;
		}

		((IPseudoClasses)button.Classes).Set(FocusVisiblePseudoClass, true);
		_firstFrameFocusReserved = true;
	}

	private void WithdrawFirstFrameFocusReservation()
	{
		if (!_firstFrameFocusReserved)
		{
			return;
		}

		_firstFrameFocusReserved = false;
		Button? button = FirstFrameFocusButton();

		// Never take the ring off a card that is REALLY focused. The reservation and the real
		// focus treatment are the same pseudo-class, so an unconditional clear here would
		// delete a legitimate focus ring whenever real focus had already landed.
		if (button is not null && ReferenceEquals(FocusManager?.GetFocusedElement(), button))
		{
			return;
		}

		if (button is not null)
		{
			((IPseudoClasses)button.Classes).Set(FocusVisiblePseudoClass, false);
		}
	}

	// THE RESERVATION IS ONLY HONEST WHILE THE RESERVED CARD IS WHERE FOCUS IS GOING.
	//
	// The framework clears :focus-visible from the element that HELD focus, so a bit set
	// manually on a card that never held it survives when focus lands elsewhere - MEASURED:
	// focusing card 2 directly left card 1's reserved ring in place, so TWO cards showed the
	// focused treatment and the height collapsed to 222 (8 DIP short of the content, i.e.
	// clipping). Every path that can move focus somewhere other than the reserved card has to
	// release the reservation first, and a window-level GotFocus handler is the one place that
	// covers all of them - digits, arrows, Tab and pointer alike.
	private void OnAnyGotFocus(object? sender, GotFocusEventArgs e)
	{
		if (!_firstFrameFocusReserved)
		{
			return;
		}

		// Focus landing ON the reserved card is the case the reservation exists for: the real
		// focus treatment takes over and the ring is no longer a claim, it is the state.
		if (ReferenceEquals(e.Source, FirstFrameFocusButton()))
		{
			_firstFrameFocusReserved = false;
			return;
		}

		WithdrawFirstFrameFocusReservation();
	}

	private Button? FirstFrameFocusButton()
	{
		return CardsItems.ContainerFromIndex(FirstFrameFocusIndex) is Control container
			? container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			: null;
	}

	// The measurement the pre-show placement depends on. Measure() runs a real layout pass
	// on the content root; no windowing backend, HWND or Show() is involved, so this is
	// safe while the window is still hidden (which is the whole point - the size must be
	// known BEFORE Show so the window never flashes at a default position).
	//
	// ONE measure at the real constraint, deliberately. This is also the measurement the
	// live re-pin runs from a layout pass (OnContentLayoutUpdated), where a second measure at
	// a DIFFERENT constraint would invalidate layout again and the pass would never settle
	// (MEASURED: "Infinite layout loop detected"). The cache-dropping variant that the
	// pre-show path needs is therefore a separate method, never this one.
	internal double MeasureContentHeightDip()
	{
		if (Content is not Control content)
		{
			return MinimumHeightDip;
		}

		// The constraint comes from the window's own Width (declared in XAML), not from a
		// second hardcoded copy of it: two sources for one number is how the old constants
		// drifted. The constant is only the fallback for a non-finite Width.
		double constraintWidth = double.IsFinite(Width) && Width > 0 ? Width : WidthDip;

		content.Measure(new Size(constraintWidth, double.PositiveInfinity));
		double measured = content.DesiredSize.Height;

		// SystemDecorations="None" means there is no chrome to add. If decorations are ever
		// enabled this is the one place that has to account for them.
		return double.IsFinite(measured) && measured > MinimumHeightDip ? measured : MinimumHeightDip;
	}

	// The same measurement with the layout cache dropped first. A style change on a DESCENDANT
	// (the reserved focus ring, for instance) invalidates that control's measure but NOT the
	// root's cached one, so a plain re-measure at the same constraint hands back the height
	// from BEFORE the change (MEASURED: 238 before and after the ring was applied). Measuring
	// once at a perturbed constraint and once at the real one forces the pass that reads the
	// current styles.
	//
	// PRE-SHOW ONLY. Called from a layout pass this would re-invalidate layout on every pass
	// and never settle, which is why it is not folded into MeasureContentHeightDip.
	private double MeasureContentHeightWithCacheDropped()
	{
		if (Content is not Control content)
		{
			return MinimumHeightDip;
		}

		double constraintWidth = double.IsFinite(Width) && Width > 0 ? Width : WidthDip;
		content.Measure(new Size(constraintWidth + CacheProbeWidthEpsilonDip, double.PositiveInfinity));

		return MeasureContentHeightDip();
	}

	// Moves real focus to the card's Button, which reports back through GotFocus
	// so the VM's FocusedIndex always names the VISIBLY focused card (D-131).
	//
	// THE ALREADY-FOCUSED CASE IS HANDLED EXPLICITLY (findings A and the stale-index hole).
	// Focus() on the element that ALREADY holds focus returns true but raises NO GotFocus
	// (KeyboardDevice.SetFocusedElement is a no-op for an unchanged element), so neither the ring
	// nor the rendered-focus proof is re-established by the framework. Every path that lands here
	// with focus already on the target therefore used to end with a ringless, unarmed card:
	//
	//   - the reactivation re-assert (F1-a), where the framework's own restore leaves the card
	//     focused with NavigationMethod.Unspecified, i.e. without the ring;
	//   - a digit re-press after a POINTER moved focus (the stale-index hole). FocusSlot's
	//     already-focused guard keys on FocusedIndex, but the pointer path disarms the proof
	//     WITHOUT updating the index (ClearFocusFromControl keeps the intent), so the ordinary
	//     path was taken, the proof was cleared, and Focus() was a no-op. MEASURED:
	//     card2StillFocused=True, ringedAfterRepress=0, vmIndex=1, claimAfterRepress=False.
	//
	// The ring is applied through the SAME pseudo-class the real focus treatment uses (the
	// pre-show reservation sets it the same way), and the claim is re-armed only AFTER the ring
	// is on - never before - so the no-ring => no-claim invariant holds.
	internal void FocusCard(int zeroBasedIndex)
	{
		if (zeroBasedIndex < 0)
		{
			return;
		}

		Control? container = CardsItems.ContainerFromIndex(zeroBasedIndex);
		Button? button = container?.GetVisualDescendants().OfType<Button>().FirstOrDefault();

		if (button is not null && ReferenceEquals(FocusManager?.GetFocusedElement(), button))
		{
			ApplyVisibleFocusToFocusedCard(button, zeroBasedIndex);
			return;
		}

		if (FocusButton(button) != true)
		{
			// Realization race (first frames): retry once after layout.
			int slot = zeroBasedIndex;
			Dispatcher.UIThread.Post(
				() =>
				{
					Control? retryContainer = CardsItems.ContainerFromIndex(slot);
					bool focused = FocusButton(retryContainer?.GetVisualDescendants().OfType<Button>().FirstOrDefault()) == true;

					// The pre-show reservation drew a ring for a focus that never arrived.
					// It has to come off here: nothing else will clear it, because the
					// framework only clears the bit when focus MOVES and it never landed.
					// A ring on an unfocused card is exactly the invisible-claim defect
					// D-131 exists to prevent - just inverted.
					//
					// Independent of the VM-claim rule below: that rule is about Enter's
					// target, this one is about what the user can SEE, and the two can
					// legitimately disagree (a later key may have moved the claim).
					if (!focused && slot == FirstFrameFocusIndex)
					{
						WithdrawFirstFrameFocusReservation();
					}

					// D-131 final-failure rule (0A-3 review finding A2): real focus
					// never landed, so the VM must not keep a claim Enter could act
					// on INVISIBLY - drop it and Enter degrades to ENTER_IGNORED.
					// Only clear when the VM still names THIS slot: a later key may
					// have legitimately moved (and confirmed) the claim elsewhere.
					if (!focused && _vm.FocusedIndex == slot)
					{
						PickerLog.Marker($"FOCUS_FAILED slot={slot + 1} -> marked focus cleared (Enter degrades to no-op)");
						_vm.SetFocusFromControl(-1);
					}
				},
				DispatcherPriority.Loaded);
		}
	}

	// The already-focused counterpart of FocusButton: the element already holds real focus, so the
	// framework will not raise GotFocus for it again. The visible-focus treatment and the VM's
	// rendered-focus proof are therefore applied by hand, in that order - the ring is on before the
	// claim is armed, which is what keeps "no ring => no claim" true (D-131).
	private void ApplyVisibleFocusToFocusedCard(Button button, int zeroBasedIndex)
	{
		// The pre-show reservation drew this card's ring for a focus that has now really arrived:
		// it is the state, not a claim, so the flag must not survive to clear it later. (For any
		// other card the reservation was already withdrawn when focus landed - OnAnyGotFocus.)
		if (zeroBasedIndex == FirstFrameFocusIndex)
		{
			_firstFrameFocusReserved = false;
		}

		((IPseudoClasses)button.Classes).Set(FocusVisiblePseudoClass, true);
		// No GotFocus follows on this path, so OnChoiceCardGotFocus cannot scroll it into view.
		button.BringIntoView();
		_vm.ReassertFocusFromControl(zeroBasedIndex);
		PickerLog.Marker(string.Create(CultureInfo.InvariantCulture, $"FOCUS_SLOT {zeroBasedIndex + 1}"));
	}

	// D-139: the focused card must be VISIBLY focused. The visible treatment is the
	// :focus-visible style, and Avalonia only sets that pseudo-class for keyboard-shaped
	// focus - Tab and directional navigation. A bare Focus() passes
	// NavigationMethod.Unspecified, which deliberately does NOT set :focus-visible (the
	// framework cannot know a mouse was not involved), so startup focus, digits 1-9 and
	// the arrow keys all moved real focus while leaving the ring invisible.
	//
	// NavigationMethod.Tab is the honest argument for these callers: every one of them is
	// keyboard- or programmatic-navigation, never a pointer. It is also exactly what the
	// native Tab path passes, so both paths produce the same visible state and neither
	// can drift from the other.
	private static bool? FocusButton(Button? button)
	{
		return button?.Focus(NavigationMethod.Tab, KeyModifiers.None);
	}

	private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
	{
		// The arrows follow the layout: Left/Right in the row, Up/Down in the column. The other
		// pair is consumed and does nothing, as every arrow was consumed before, so native
		// directional navigation never moves focus behind the picker's back.
		if (IsOffAxisArrow(e.Key, e.KeyModifiers, _vm.IsVerticalLayout))
		{
			e.Handled = true;
			return;
		}

		PickerKeyAction? action = MapKey(e.Key, e.KeyModifiers, _vm.IsVerticalLayout);
		if (action is null)
		{
			return;
		}

		// Enter on a focused NON-card button (first-run actions, the "?" toggle) belongs to that
		// button: the picker's Enter means "launch the focused card", and with no card focused it
		// would be consumed as a no-op, leaving the keyboard user unable to press the button.
		if (action.Value.Kind == PickerKey.Enter
			&& FocusManager?.GetFocusedElement() is Button { DataContext: not ChoiceCardViewModel })
		{
			return;
		}

		// Picker hotkeys beat focused-control handling (spike: Enter stolen by a
		// focused Button was the observed defect).
		e.Handled = true;
		_onKey(action.Value);
	}

	internal static bool IsOffAxisArrow(Key key, KeyModifiers modifiers, bool vertical)
	{
		return modifiers == KeyModifiers.None
			&& (vertical ? key is Key.Left or Key.Right : key is Key.Up or Key.Down);
	}

	internal static PickerKeyAction? MapKey(Key key, KeyModifiers modifiers, bool vertical)
	{
		// Tuple switch: an enum-member switch on Avalonia's Key would trip the
		// populate-switch style rule without handling all ~200 members.
		(PickerKey Kind, int Slot)? mapping = (key, modifiers) switch
		{
			(Key.D1 or Key.NumPad1, KeyModifiers.None) => (PickerKey.Digit, 1),
			(Key.D2 or Key.NumPad2, KeyModifiers.None) => (PickerKey.Digit, 2),
			(Key.D3 or Key.NumPad3, KeyModifiers.None) => (PickerKey.Digit, 3),
			(Key.D4 or Key.NumPad4, KeyModifiers.None) => (PickerKey.Digit, 4),
			(Key.D5 or Key.NumPad5, KeyModifiers.None) => (PickerKey.Digit, 5),
			(Key.D6 or Key.NumPad6, KeyModifiers.None) => (PickerKey.Digit, 6),
			(Key.D7 or Key.NumPad7, KeyModifiers.None) => (PickerKey.Digit, 7),
			(Key.D8 or Key.NumPad8, KeyModifiers.None) => (PickerKey.Digit, 8),
			(Key.D9 or Key.NumPad9, KeyModifiers.None) => (PickerKey.Digit, 9),
			(Key.Enter, KeyModifiers.None) => (PickerKey.Enter, 0),
			(Key.Escape, KeyModifiers.None) => (PickerKey.Escape, 0),
			(Key.F, KeyModifiers.None) => (PickerKey.SystemFallback, 0),
			(Key.C, KeyModifiers.Control) => (PickerKey.Copy, 0),
			(Key.U, KeyModifiers.Control) => (PickerKey.UseOriginal, 0),
			(Key.H, KeyModifiers.Control) => (PickerKey.SkipHistoryOnce, 0),
			(Key.Back, KeyModifiers.Control) => (PickerKey.ClearHistoryConfirmed, 0),
			(Key.F1, KeyModifiers.None) => (PickerKey.ToggleShortcuts, 0),
			(Key.Right, KeyModifiers.None) when !vertical => (PickerKey.FocusNext, 0),
			(Key.Left, KeyModifiers.None) when !vertical => (PickerKey.FocusPrevious, 0),
			(Key.Down, KeyModifiers.None) when vertical => (PickerKey.FocusNext, 0),
			(Key.Up, KeyModifiers.None) when vertical => (PickerKey.FocusPrevious, 0),
			_ => null,
		};

		return mapping is { } m ? new PickerKeyAction(m.Kind, m.Slot) : null;
	}

	private void OnChoiceCardClick(object? sender, RoutedEventArgs e)
	{
		// NO ARMING HERE, DELIBERATELY (finding N2). The click IS the action - it launches the
		// card below - so arming the Enter claim as well would leave a loaded weapon pointed at a
		// card the user never visibly focused: a pointer-focused card carries no :focus-visible
		// ring, so the claim would be invisible (MEASURED pre-fix: ringsAfter=0, claim=True).
		// Enter's target is owned by the focus paths alone.
		if (sender is Control { DataContext: ChoiceCardViewModel card })
		{
			_onCardClicked(card);
		}
	}

	// FINDING B (MEDIUM): THE CLAIM MAY ONLY BE ARMED BY FOCUS THAT IS VISIBLE.
	//
	// Avalonia sets :focus-visible only for keyboard-shaped navigation (NavigationMethod.Tab or
	// Directional - InputElement.OnGotFocus, 11.3.18), so a pointer press focuses a card while
	// leaving it WITHOUT a ring. Arming on any GotFocus therefore made the pointer-focused card
	// Enter's target with nothing on screen saying so - the invisible claim D-131 forbids.
	//
	// TWO BRANCHES, AND THE SECOND ONE MATTERS. Gating alone (arm only when ringed) still leaves
	// the claim armed from the PREVIOUS card: startup focuses card 1 through the keyboard, then a
	// mouse press on card 2 clears card 1's ring and arms nothing - but the old claim still names
	// card 1, whose ring just vanished (MEASURED: ringFirst=False, ringSecond=False, claimLive=True).
	// So a ringless focus report DISARMS the proof as well. The index is kept (ClearFocusFromControl
	// does not touch it): it is the re-assert intent, not the proof.
	private void OnChoiceCardGotFocus(object? sender, GotFocusEventArgs e)
	{
		if (sender is not Control { DataContext: ChoiceCardViewModel card } control)
		{
			return;
		}

		// When the list is capped (FitCardListToWorkArea) the focused card may sit below the fold:
		// digits, arrows and Tab all land here, so this one call keeps every one of them visible.
		// A no-op when the list does not scroll.
		control.BringIntoView();

		if (!control.Classes.Contains(FocusVisiblePseudoClass))
		{
			_vm.ClearFocusFromControl();
			return;
		}

		_vm.SetFocusFromControl(card.Slot - 1);
		PickerLog.Marker(string.Create(CultureInfo.InvariantCulture, $"FOCUS_SLOT {card.Slot}"));
	}

	// FINDING N1 (HIGH): FOCUS LEAVING THE CARD LIST MUST DISARM THE CLAIM.
	//
	// Nothing anywhere in the repo handled LostFocus, so the claim outlived the visible focus: once
	// focus moved off the cards entirely (window deactivation, focus cleared, another control
	// taking it), FocusedIndex and the rendered-focus proof both survived while no card carried a
	// ring - and Enter launched a browser invisibly (MEASURED: ringedCards=0, claim=True).
	//
	// WHERE FOCUS WENT, NOT THAT IT LEFT. KeyboardDevice.SetFocusedElement assigns the new focused
	// element BEFORE raising LostFocus (11.3.18, verified in source), so reading the focus manager
	// here reports the DESTINATION. That is what keeps the ordinary card-to-card walk - Tab, digits,
	// arrows - from disarming: focus landing on another card is not "leaving the list", and the new
	// card re-arms through its own GotFocus.
	//
	// THE SOURCE IS DELIBERATELY NOT INSPECTED. The event's Source is the element that LOST focus -
	// i.e. a card in every case this handler cares about - so keying on it would make the handler a
	// no-op. Only the destination answers the question that matters: is a card still focused?
	private void OnAnyLostFocus(object? sender, RoutedEventArgs e)
	{
		if (FocusManager?.GetFocusedElement() is Control { DataContext: ChoiceCardViewModel })
		{
			return;
		}

		_vm.ClearFocusFromControl();
	}

	private void OnFindBrowsersClick(object? sender, RoutedEventArgs e)
	{
		_onFirstRunAction?.Invoke(Core.Abstractions.FirstRunAction.FindBrowsers);
	}

	private void OnAddManuallyClick(object? sender, RoutedEventArgs e)
	{
		_onFirstRunAction?.Invoke(Core.Abstractions.FirstRunAction.AddManually);
	}

	private void OnSkipClick(object? sender, RoutedEventArgs e)
	{
		_onFirstRunAction?.Invoke(Core.Abstractions.FirstRunAction.Skip);
	}

	// The config-recovery notice's action. Enter/Space on the focused button reach here through
	// the button itself (OnKeyDownTunnel leaves Enter to a focused non-card button).
	private void OnRepairSettingsClick(object? sender, RoutedEventArgs e)
	{
		_onRepairSettings?.Invoke();
	}

	// SHORTCUT LIST FOCUS. Opening moves keyboard focus onto the panel (the screen reader reads
	// its name and the full list from its help text); closing - Esc, F1 or "?" - puts focus back
	// on whatever held it before, so a card's ring and Enter claim come back with it.
	private IInputElement? _focusBeforeShortcuts;

	private void OnShortcutListToggled()
	{
		Dispatcher.UIThread.Post(
			() =>
			{
				if (!IsVisible)
				{
					return;
				}

				if (_vm.IsDetailsOpen)
				{
					IInputElement? current = FocusManager?.GetFocusedElement();
					_focusBeforeShortcuts = ReferenceEquals(current, ShortcutPanel) ? _focusBeforeShortcuts : current;
					_ = ShortcutPanel.Focus(NavigationMethod.Tab, KeyModifiers.None);
					return;
				}

				IInputElement? restore = _focusBeforeShortcuts;
				_focusBeforeShortcuts = null;
				if (restore is Control { DataContext: ChoiceCardViewModel card })
				{
					FocusCard(card.Slot - 1);
				}
				else if (restore is InputElement element && element.IsEffectivelyVisible)
				{
					_ = element.Focus(NavigationMethod.Tab, KeyModifiers.None);
				}
				else if (_vm.Cards.Count > 0)
				{
					FocusCard(_vm.FocusedIndex >= 0 ? _vm.FocusedIndex : FirstFrameFocusIndex);
				}
				else if (_vm.IsFirstRunPromptVisible)
				{
					FocusFirstRunPrimary();
				}
			},
			DispatcherPriority.Loaded);
	}

	// The first-run panel collapsed (Skip, or a search that produced cards). If focus was on one
	// of its buttons it would be left on a hidden control - nowhere, for keyboard and screen
	// reader alike. Cards re-take focus through the Cards change; with no cards (Skip), focus
	// lands on the status area, whose name is the "Scan skipped..." line just shown.
	private void OnFirstRunPanelCollapsed()
	{
		Dispatcher.UIThread.Post(
			() =>
			{
				if (!IsVisible || _vm.Cards.Count > 0)
				{
					return;
				}

				// Lost = nothing focused, or the focused control is no longer visible (it sat in
				// the collapsed panel).
				IInputElement? focused = FocusManager?.GetFocusedElement();
				bool focusLost = focused is not Control control || !control.IsEffectivelyVisible;
				if (focusLost)
				{
					_ = StatusArea.Focus(NavigationMethod.Tab, KeyModifiers.None);
				}
			},
			DispatcherPriority.Loaded);
	}

	// Same landing pattern as after Skip (OnFirstRunPanelCollapsed). Checked synchronously,
	// BEFORE layout runs: the button is still the focused element at this point, and only it
	// is moved, so focus that is anywhere else is never disturbed.
	private void MoveFocusOffUnusableRepairButton()
	{
		bool unusable = _vm.IsRepairRunning || !_vm.IsConfigNoticeVisible;
		if (!_repairButtonHeldFocus || !unusable || !IsVisible)
		{
			return;
		}

		// The IsEnabled/IsVisible bindings may run before this handler, and Avalonia then
		// clears focus off the now-unusable button - so "is it focused NOW" is not the test.
		// The flag records that it was the last focused control; focus is moved only if it has
		// not already gone somewhere real (a card, another button) in between.
		IInputElement? focused = FocusManager?.GetFocusedElement();
		bool focusIsLost = focused is null
			|| ReferenceEquals(focused, RepairSettingsButton)
			|| focused is Control { IsEffectivelyVisible: false }
			|| focused is InputElement { IsEffectivelyEnabled: false };
		if (focusIsLost)
		{
			_repairButtonHeldFocus = false;
			_ = StatusArea.Focus(NavigationMethod.Tab, KeyModifiers.None);
		}
	}

	// True while the repair button is the most recent control to receive focus.
	private bool _repairButtonHeldFocus;

	private void TrackRepairButtonFocus(object? sender, GotFocusEventArgs e)
	{
		_repairButtonHeldFocus = ReferenceEquals(e.Source, RepairSettingsButton);
	}

	// The "?" button is the pointer twin of F1; both go through the session's key path so
	// the details layer has one owner.
	private void OnShortcutsToggleClick(object? sender, RoutedEventArgs e)
	{
		_onKey(new PickerKeyAction(PickerKey.ToggleShortcuts, 0));
	}

	// First frame of the first-run screen: Find browsers takes focus with a visible ring
	// (NavigationMethod.Tab, for the same reason FocusButton uses it).
	internal void FocusFirstRunPrimary()
	{
		if (!FindBrowsersButton.IsEffectivelyVisible)
		{
			return;
		}

		if (!FindBrowsersButton.Focus(NavigationMethod.Tab, KeyModifiers.None))
		{
			Dispatcher.UIThread.Post(
				() => _ = FindBrowsersButton.Focus(NavigationMethod.Tab, KeyModifiers.None),
				DispatcherPriority.Loaded);
		}
	}
}
