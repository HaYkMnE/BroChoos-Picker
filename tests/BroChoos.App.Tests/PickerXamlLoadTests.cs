using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;
using BroChoos.App.Audit;
using BroChoos.App.Picker;
using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Routing;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace BroChoos.App.Tests;

// THE TEST THAT WOULD HAVE CAUGHT THE LAUNCH-KILLING DEFECT, and the reason it is shaped
// this way rather than as another source-text assertion.
//
// WHAT WENT WRONG. PickerTokens.axaml keyed its High Contrast dictionary with
// x:Key="HighContrast". ThemeDictionaries is keyed by ThemeVariant, and the only thing that
// turns that attribute into one is Avalonia's ThemeVariantTypeConverter, which accepts the
// BUILT-IN variants only and throws NotSupportedException for anything else. The throw
// happened during compiled-XAML population of PickerTokens.axaml - i.e. inside
// PickerWindow's constructor, reached from PickerSession.OpenWindow() - so the exception
// escaped to the top of the STA UI thread and killed the process on EVERY launch
// (exit 0xE0434352). The product's only UI never opened once.
//
// WHY NOTHING EXISTING SAW IT. Every guard in the repo at that time read the .axaml as
// TEXT or as XML: VisualTokenTests asserted that a dictionary with x:Key="HighContrast"
// was present, which was true of the broken file and equally true of a fixed one. A
// source-level check cannot distinguish "this key is a string that a type converter will
// reject at load time" from "this key is a valid ThemeVariant", and it cannot see a token
// whose declared type does not match its consumer at all. Only RUNNING the XAML compiler's
// output can.
//
// WHAT THIS DOES INSTEAD: it builds a real Avalonia application (no window is ever shown
// and no message loop is started) and constructs the REAL PickerWindow. That single call
// exercises the whole chain that used to die - the ResourceInclude of PickerTokens.axaml,
// the ThemeDictionaries keys, every DynamicResource consumer in the window and every
// Setter value in its styles - so any recurrence of the theme-variant, alias-shape or
// token-type class of defect fails here, in a test, instead of on a user's desktop.
//
// NO DEDICATED THREAD IS USED, DELIBERATELY. An earlier version of this probe ran the work
// on its own STA thread from inside the probe type's static initializer, and it hung for the
// full two-minute timeout: a static method of a type whose static initializer is still
// running BLOCKS on every other thread, because that thread waits on the CLR type-init lock.
// MEASURED in the sandbox: SetupWithoutStarting plus PickerWindow construction and resource
// resolution all complete on the test host's own MTA thread (under a second), so the extra
// thread bought nothing and cost a deadlock. The probe therefore runs inline, and its state
// is produced by a method that returns a value rather than by a static constructor.
internal static class PickerXamlProbe
{
	// Colour roles the High Contrast overlay is required to supply. Named individually
	// rather than counted so a role dropped in a future edit fails by name.
	private static readonly string[] HighContrastColorRoles =
	[
		"bc.bg.base", "bc.bg.card", "bc.bg.subtle",
		"bc.text.primary", "bc.text.secondary", "bc.text.disabled",
		"bc.accent.default", "bc.accent.hover", "bc.accent.pressed", "bc.accent.onAccent",
		"bc.border.subtle", "bc.border.strong",
		"bc.status.success", "bc.status.success.onSurface",
		"bc.status.warning", "bc.status.warning.onSurface",
		"bc.status.danger", "bc.status.danger.onSurface",
		"bc.scrim", "bc.focus.outer", "bc.focus.inner",
	];

	private static readonly Lock Gate = new();

	private static ProbeResult? _result;

	// Idempotent: an Avalonia Application is process-wide state and can only be configured
	// once per process, so the work happens on the first call and is cached for every later
	// test in the same run.
	internal static ProbeResult Run()
	{
		lock (Gate)
		{
			return _result ??= Execute();
		}
	}

	private static ProbeResult Execute()
	{
		try
		{
			StartAvalonia();
		}
		catch (Exception failure)
		{
			return new ProbeResult(
				$"Avalonia setup failed: {failure.GetType().FullName}: {failure.Message}",
				MeasuredHeightDip: 0,
				MissingRoles: HighContrastColorRoles,
				RoleTypes: new Dictionary<string, string>(StringComparer.Ordinal));
		}

		try
		{
			PickerWindow window = new(NewViewModel(), _ => { }, _ => { });

			// The constructor already measured and adopted the height; read it back so the
			// height contract is asserted on a real value rather than a constant.
			double measuredHeight = window.Height;

			Dictionary<string, string> roleTypes = new(StringComparer.Ordinal);
			List<string> missingRoles = [];

			foreach (string role in HighContrastColorRoles)
			{
				if (!window.TryGetResource(role, PickerThemeVariants.HighContrast, out object? value) || value is null)
				{
					missingRoles.Add(role);
					continue;
				}

				roleTypes[role] = value.GetType().FullName ?? value.GetType().Name;

				// The consumer contract: a colour role is handed to a Brush property, so it
				// must BE an IBrush. The old DynamicResource-as-value shape resolved to a
				// DynamicResourceExtension and threw InvalidCastException at the consumer.
				if (value is not IBrush)
				{
					return new ProbeResult(
						$"High Contrast role '{role}' resolved to {value.GetType().FullName}, which is not an IBrush. "
						+ "A colour role must hold a brush (use <StaticResource .../> aliases, not <DynamicResource .../>).",
						measuredHeight,
						missingRoles,
						roleTypes);
				}
			}

			return new ProbeResult(null, measuredHeight, missingRoles, roleTypes);
		}
		catch (Exception failure)
		{
			return new ProbeResult(
				$"{failure.GetType().FullName}: {failure.Message}",
				MeasuredHeightDip: 0,
				MissingRoles: HighContrastColorRoles,
				RoleTypes: new Dictionary<string, string>(StringComparer.Ordinal));
		}
	}

	// The windowing backend is needed even though no window is shown: PickerWindow derives
	// from Window, and the platform services must be in place before one can be constructed.
	// SetupWithoutStarting wires the services WITHOUT running a message loop, which is what
	// makes this usable inside a test host.
	private static void StartAvalonia()
	{
		_ = AppBuilder.Configure<ProbeApplication>()
			.UsePlatformDetect()
			.SetupWithoutStarting();
	}

	private static PickerViewModel NewViewModel()
	{
		BrowserCandidate[] candidates =
		[
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path"),
			new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path"),
		];

		return new PickerViewModel("https://example.com/", new BrowserSnapshot(candidates, 1, DateTimeOffset.UtcNow));
	}

	// A minimal Application: FluentTheme only, no localization writes and no session. The
	// window under test must not depend on anything PickerApp does beyond the theme, and
	// this keeps the probe honest about that - a missing localization key resolves to an
	// empty string rather than failing, which is the product's own fail-open behaviour.
	private sealed class ProbeApplication : Application
	{
		public override void Initialize()
		{
			Styles.Add(new FluentTheme());
		}
	}

	internal sealed record ProbeResult(
		string? Failure,
		double MeasuredHeightDip,
		IReadOnlyList<string> MissingRoles,
		IReadOnlyDictionary<string, string> RoleTypes);
}

// THE FOCUS BEHAVIOUR PROBE. It shares PickerXamlProbe's Avalonia bootstrap - an Application
// and its Dispatcher are process-wide singletons, and the dispatcher is THREAD-AFFINE, so a
// second bootstrap attempt from another test class would fail with "Call from invalid thread"
// rather than build a second UI thread (MEASURED). It therefore lives in this file, and is
// driven by the test above rather than constructing anything of its own.
//
// It does NOT touch Console: the PickerLog markers the window emits would otherwise race with
// the console-capturing collection, which exists precisely because Console redirection is
// process-wide and xUnit runs classes in parallel.
internal static class PickerFocusProbe
{
	// The focus treatment's own geometry delta, derived from the styles that declare it rather
	// than restated as a number here. Per edge, the inset is (padding + border): the focused
	// style SHRINKS padding 8 -> 3 while GROWING border 1 -> 2, so each edge loses 4 DIP and the
	// card loses 4 on top and 4 on the bottom. MEASURED against the real window: 238 unfocused,
	// 230 focused - exactly this 8.
	//
	// Derived from the four pairs below so a token change moves the expectation with the style
	// instead of silently invalidating the assertion.
	internal const double FocusRingDeltaDip = RestingPaddingTop + RestingBorderTop - FocusedPaddingTop - FocusedBorderTop
		+ RestingPaddingBottom + RestingBorderBottom - FocusedPaddingBottom - FocusedBorderBottom;

	private const double RestingPaddingTop = 8;
	private const double RestingPaddingBottom = 8;
	private const double RestingBorderTop = 1;
	private const double RestingBorderBottom = 1;
	// Tile layout: the focused style shrinks padding 8 -> 7 while growing the border 1 -> 2, so the
	// inset per edge is unchanged and the delta is 0 - focusing a tile never moves or resizes it.
	private const double FocusedPaddingTop = 7;
	private const double FocusedPaddingBottom = 7;
	private const double FocusedBorderTop = 2;
	private const double FocusedBorderBottom = 2;

	private const string FocusVisiblePseudoClass = ":focus-visible";

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
		PickerWindow window = new(NewViewModel(), _ => { }, _ => { });

		// 1. The height the window adopts at construction, before anything is shown. This is the
		//    value PickerSession's pre-show placement reads.
		double heightAtConstruction = window.Height;

		// 2. The unfocused geometry, taken from a SECOND untouched window so nothing below has to
		//    mutate the window under test. This is the height the first window would have adopted
		//    without the reservation, so comparing the two is what proves the reservation does
		//    real work instead of happening to agree with an unfocused measurement.
		//
		//    The reservation is withdrawn from this throwaway window first (it is applied at
		//    construction by design), and the constraint is perturbed before reading: a style
		//    change on a DESCENDANT does not invalidate the content root's cached measurement, so
		//    a plain re-measure would hand back the reserved height and the comparison would be
		//    vacuous (MEASURED).
		PickerWindow unfocusedWindow = new(NewViewModel(), _ => { }, _ => { });
		((IPseudoClasses)CardButton(unfocusedWindow, 0).Classes).Set(FocusVisiblePseudoClass, false);
		Control unfocusedContent = (Control)unfocusedWindow.Content!;
		unfocusedContent.Measure(new Size(unfocusedWindow.Width + 0.5, double.PositiveInfinity));
		double heightUnfocused = unfocusedWindow.MeasureContentHeightDip();
		unfocusedWindow.Close();

		bool ringVisible = false;
		bool focusedElementIsCard = false;
		int ringedAfterDirectMove = 0;

		try
		{
			// 3. Show the window where nobody can see it: off-screen, fully transparent and
			//    non-activating. Show() is REQUIRED - Focus() returns false before it, because no
			//    focus manager is attached to the tree yet (MEASURED), so the production focus
			//    path cannot be exercised on a hidden window at all.
			window.ShowActivated = false;
			window.Topmost = false;
			window.Opacity = 0;
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			// 4. THE DIRECT-TO-SECOND-CARD PATH, driven while the pre-show reservation is still
			//    standing. This is what a digit key does when the first-frame focus never landed
			//    (the reservation is only released by focus arriving somewhere), and it is the
			//    path that exposed the two-ring defect: the framework clears :focus-visible from
			//    the element that HELD focus, so a reservation on card 1 survives focus landing on
			//    card 2 (MEASURED before the fix: 2 ringed cards and a 222 DIP height).
			window.FocusCard(1);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();

			for (int index = 0; index < 2; index++)
			{
				if (CardButton(window, index).Classes.Contains(FocusVisiblePseudoClass))
				{
					ringedAfterDirectMove++;
				}
			}

			// 5. Now the ordinary first-frame path on the same window: focus card 1, then walk to
			//    card 2 the way an arrow or digit key does. Exactly one card may stay ringed.
			window.FocusCard(0);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();

			Button card = CardButton(window, 0);
			ringVisible = card.Classes.Contains(FocusVisiblePseudoClass);
			focusedElementIsCard = ReferenceEquals(window.FocusManager?.GetFocusedElement(), card);

			double heightAfterFocus = window.Height;

			window.FocusCard(1);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();

			int ringedCards = 0;
			for (int index = 0; index < 2; index++)
			{
				if (CardButton(window, index).Classes.Contains(FocusVisiblePseudoClass))
				{
					ringedCards++;
				}
			}

			return new Result(
				Failure: null,
				HeightAtConstruction: heightAtConstruction,
				HeightWithUnfocusedGeometry: heightUnfocused,
				HeightAfterFirstFrameFocus: heightAfterFocus,
				RingVisibleAfterProgrammaticFocus: ringVisible,
				FocusedElementIsCard: focusedElementIsCard,
				RingedCardCountAfterFocusMovedToSecondCard: ringedCards,
				RingedCardCountAfterFocusMovedDirectlyWhileReserved: ringedAfterDirectMove);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the measurement no longer builds the containers.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private static PickerViewModel NewViewModel()
	{
		BrowserCandidate[] candidates =
		[
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path"),
			new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path"),
		];

		return new PickerViewModel("https://example.com/", new BrowserSnapshot(candidates, 1, DateTimeOffset.UtcNow));
	}

	internal sealed record Result(
		string? Failure,
		double HeightAtConstruction,
		double HeightWithUnfocusedGeometry,
		double HeightAfterFirstFrameFocus,
		bool RingVisibleAfterProgrammaticFocus,
		bool FocusedElementIsCard,
		int RingedCardCountAfterFocusMovedToSecondCard,
		int RingedCardCountAfterFocusMovedDirectlyWhileReserved)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, 0, 0, 0, false, false, 0, 0);
		}
	}
}

// THE SNAPSHOT-REPLACEMENT FOCUS PROBE (latent D-131 corner). ApplySnapshot replaces the
// whole card collection: the ItemsControl destroys the containers, so real focus and the
// :focus-visible ring die with them. The VM's rendered-focus proof used to survive any
// replacement that did not SHRINK the list, so the claim outlived the visible focus and
// Enter could activate a card with no focus anywhere - the invisible claim D-131 forbids,
// reachable through the first-run consent action.
//
// It shares PickerXamlProbe's bootstrap for the same reason PickerFocusProbe does: one
// Application per process, and its dispatcher is thread-affine (MEASURED: a second
// bootstrap from another class fails with "Call from invalid thread" instead of starting
// a second UI thread).
//
// THE CLOCK IS INJECTED so both halves of the invariant are observable deterministically.
// The claim is only live 100 ms after a real focus report, so without an advanceable clock
// the pre-replacement sanity check would fail for the wrong reason (the anti-double-tap
// delay) and the post-replacement check could not fail on the defect at all.
internal static class PickerSnapshotFocusProbe
{
	private const string FocusVisiblePseudoClass = ":focus-visible";

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
		AdvanceableTimeProvider time = new(DateTimeOffset.UnixEpoch);
		PickerViewModel vm = new("https://example.com/", Snapshot(2), time);
		PickerWindow window = new(vm, _ => { }, _ => { });

		try
		{
			// Show where nobody can see it, for the same reason PickerFocusProbe does: Focus()
			// returns false before Show because no focus manager is attached yet, so the real
			// focus path cannot be exercised on a hidden window at all.
			window.ShowActivated = false;
			window.Topmost = false;
			window.Opacity = 0;
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			// 1. The production path (PickerSession.OnWindowOpened / digit key): focus slot 2,
			//    NOT the first card. A re-assert that blindly focused card 1 cannot pass this
			//    probe, and the card the VM names is index 1 for the replacement below.
			_ = vm.FocusSlot(2);
			window.FocusCard(vm.FocusedIndex);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();

			// 2. Let the 100 ms visibility window elapse so the claim is genuinely live before
			//    the replacement; otherwise the post-replacement assertion below could pass
			//    merely because the anti-double-tap delay had not elapsed.
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimLiveBeforeReplacement = vm.CanActivateFocusedCard();
			int indexBeforeReplacement = vm.FocusedIndex;

			// 3. THE REPLACEMENT. A GROWN list is one of the two shapes that used to keep the
			//    stale proof standing (the shrink was already handled by the clamp).
			vm.ApplySnapshot(Snapshot(3));

			// 4. THE NO-INVISIBLE-CLAIM HALF, read synchronously: no layout pass has run since
			//    the swap, so no container of the new list can be focused yet, and Enter must
			//    already be a no-op.
			bool claimAfterReplacement = vm.CanActivateFocusedCard();

			// 5. THE RE-ASSERT HALF, after the posted focus job and the layout it waits for.
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();

			Button reasserted = CardButton(window, indexBeforeReplacement);
			bool reassertedIsFocused = ReferenceEquals(window.FocusManager?.GetFocusedElement(), reasserted);
			bool reassertedHasRing = reasserted.Classes.Contains(FocusVisiblePseudoClass);

			// 6. The claim is live again only through the real focus report, on the card that
			//    is visibly focused.
			time.Advance(TimeSpan.FromMilliseconds(100));
			bool claimAfterReassert = vm.CanActivateFocusedCard();
			bool targetIsReasserted = ReferenceEquals(vm.GetFocusedActivationTarget(), vm.Cards[indexBeforeReplacement]);

			// 7. A shrinking replacement clamps the index and re-asserts on the clamped card.
			vm.ApplySnapshot(Snapshot(1));
			int shrinkIndex = vm.FocusedIndex;
			bool claimAfterShrink = vm.CanActivateFocusedCard();
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			bool shrinkFocusOnFirstCard = ReferenceEquals(window.FocusManager?.GetFocusedElement(), CardButton(window, 0));
			bool shrinkRingOnFirstCard = CardButton(window, 0).Classes.Contains(FocusVisiblePseudoClass);

			// 8. THE FIRST-RUN SHAPE, which is the production sequence this whole path exists
			//    for: the picker opens with NO cards (nothing detected yet), the user consents to
			//    a scan, and the discovered list arrives as a replacement. With the VM naming no
			//    card at all, the re-assert has to fall back to the first card - the branch a
			//    probe that only ever replaced a focused list would never reach.
			vm.ApplySnapshot(BrowserSnapshot.Empty);
			int emptyIndex = vm.FocusedIndex;
			bool emptyTargetIsNull = vm.GetFocusedActivationTarget() is null;
			bool emptyClaim = vm.CanActivateFocusedCard();

			vm.ApplySnapshot(Snapshot(2));
			bool claimAfterEmptyReplacement = vm.CanActivateFocusedCard();
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			bool fallbackFocusOnFirstCard = ReferenceEquals(window.FocusManager?.GetFocusedElement(), CardButton(window, 0));
			bool fallbackRingOnFirstCard = CardButton(window, 0).Classes.Contains(FocusVisiblePseudoClass);
			time.Advance(TimeSpan.FromMilliseconds(100));
			bool claimAfterFallbackReassert = vm.CanActivateFocusedCard();

			return new Result(
				Failure: null,
				ClaimLiveBeforeReplacement: claimLiveBeforeReplacement,
				ClaimAfterReplacement: claimAfterReplacement,
				ReassertedCardIsFocused: reassertedIsFocused,
				ReassertedCardHasRing: reassertedHasRing,
				ClaimAfterReassert: claimAfterReassert,
				TargetIsReassertedCard: targetIsReasserted,
				ShrinkIndex: shrinkIndex,
				ClaimAfterShrink: claimAfterShrink,
				ShrinkFocusOnFirstCard: shrinkFocusOnFirstCard,
				ShrinkRingOnFirstCard: shrinkRingOnFirstCard,
				EmptyIndex: emptyIndex,
				EmptyTargetIsNull: emptyTargetIsNull,
				EmptyClaim: emptyClaim,
				ClaimAfterEmptyReplacement: claimAfterEmptyReplacement,
				FallbackFocusOnFirstCard: fallbackFocusOnFirstCard,
				FallbackRingOnFirstCard: fallbackRingOnFirstCard,
				ClaimAfterFallbackReassert: claimAfterFallbackReassert);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the replacement's containers are missing.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private static BrowserSnapshot Snapshot(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(index =>
			new BrowserCandidate(
				$"browser-{index}",
				$"Browser {index}",
				$@"C:\Browsers\b{index}.exe",
				"stable",
				"known-path"))];

		return new BrowserSnapshot(candidates, candidateCount, DateTimeOffset.UtcNow);
	}

	// The picker's anti-double-tap rule reads the wall clock, so the probe has to own it:
	// with TimeProvider.System the claim would be asserted in the first 100 ms after the
	// focus report and could not be shown to be live at all.
	private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	internal sealed record Result(
		string? Failure,
		bool ClaimLiveBeforeReplacement,
		bool ClaimAfterReplacement,
		bool ReassertedCardIsFocused,
		bool ReassertedCardHasRing,
		bool ClaimAfterReassert,
		bool TargetIsReassertedCard,
		int ShrinkIndex,
		bool ClaimAfterShrink,
		bool ShrinkFocusOnFirstCard,
		bool ShrinkRingOnFirstCard,
		int EmptyIndex,
		bool EmptyTargetIsNull,
		bool EmptyClaim,
		bool ClaimAfterEmptyReplacement,
		bool FallbackFocusOnFirstCard,
		bool FallbackRingOnFirstCard,
		bool ClaimAfterFallbackReassert)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, false, true, false, false, false, false, -1, true, false, false, 0, false, true, true, false, false, false);
		}
	}
}

// THE ALREADY-FOCUSED-DIGIT GUARD (HIGH). Digits 1-9 focus the card with that slot (D-130),
// and re-pressing the digit of the ALREADY-focused card is an ordinary thing to do: the user
// taps "1" again because the first press looked like it did nothing, or simply to re-affirm
// the choice. Pre-fix that re-press was a silent trap.
//
// WHAT WENT WRONG. FocusSlot unconditionally reset the rendered-focus proof
// (_focusRenderedAt = MinValue) and the window then called Focus() on the card that ALREADY
// held focus. Avalonia's KeyboardDevice.SetFocusedElement is a no-op when the element is
// unchanged, so no GotFocus was raised, so the VM's SetFocusFromControl never ran and the
// proof was never re-armed. The card stayed visibly ringed - the focus really was there -
// while CanActivateFocusedCard() returned false for the rest of the session: Enter was dead,
// permanently, and nothing on screen said so.
//
// WHY ONLY A REAL WINDOW CAN SEE IT. FocusSlot returning true, FocusedIndex staying put and
// the ring staying on the card are IDENTICAL pre- and post-fix; the difference lives in
// whether a real focus report still follows. So this probe drives the real focus path on a
// real window and shares PickerXamlProbe's bootstrap for the same reason PickerFocusProbe
// does (one Application per process, thread-affine dispatcher).
//
// THE CLOCK IS INJECTED for the same reason PickerSnapshotFocusProbe injects it: the claim
// is only live 100 ms after a real focus report, so the "was the claim live before the
// re-press" sanity check needs a clock the probe controls.
internal static class PickerRepressDigitProbe
{
	private const string FocusVisiblePseudoClass = ":focus-visible";

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
		AdvanceableTimeProvider time = new(DateTimeOffset.UnixEpoch);
		PickerViewModel vm = new("https://example.com/", Snapshot(2), time);
		PickerWindow window = new(vm, _ => { }, _ => { });

		try
		{
			// Show where nobody can see it, for the same reason PickerFocusProbe does: Focus()
			// returns false before Show because no focus manager is attached yet, so the real
			// focus path cannot be exercised on a hidden window at all.
			window.ShowActivated = false;
			window.Topmost = false;
			window.Opacity = 0;
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			// 1. The ordinary first-frame path: focus slot 1 and let real focus land, so the
			//    VM's proof is armed by a real GotFocus report.
			_ = vm.FocusSlot(1);
			window.FocusCard(vm.FocusedIndex);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();

			// 2. Let the 100 ms window elapse: the claim must be genuinely live before the
			//    re-press, or "the claim survived" could pass for the wrong reason.
			time.Advance(TimeSpan.FromMilliseconds(100));
			bool claimLiveBeforeRepress = vm.CanActivateFocusedCard();

			// 3. THE RE-PRESS, exactly as PickerSession routes a digit key: FocusSlot, then
			//    FocusCard on the slot it reports.
			bool accepted = vm.FocusSlot(1);
			if (accepted)
			{
				window.FocusCard(vm.FocusedIndex);
			}

			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();

			bool claimAfterRepress = vm.CanActivateFocusedCard();
			bool focusedElementIsStillFirstCard = ReferenceEquals(window.FocusManager?.GetFocusedElement(), CardButton(window, 0));

			int ringedAfterRepress = 0;
			for (int index = 0; index < 2; index++)
			{
				if (CardButton(window, index).Classes.Contains(FocusVisiblePseudoClass))
				{
					ringedAfterRepress++;
				}
			}

			// 4. Time passing must not be what fixes it: pre-fix the proof is MinValue, and no
			//    later instant revives it without a new focus report.
			time.Advance(TimeSpan.FromMilliseconds(200));
			bool claimAfterClockAdvance = vm.CanActivateFocusedCard();

			return new Result(
				Failure: null,
				ClaimLiveBeforeRepress: claimLiveBeforeRepress,
				RepressAccepted: accepted,
				ClaimAfterRepress: claimAfterRepress,
				ClaimAfterClockAdvance: claimAfterClockAdvance,
				FocusedElementIsStillFirstCard: focusedElementIsStillFirstCard,
				RingedCardCountAfterRepress: ringedAfterRepress);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the measurement no longer builds the containers.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private static BrowserSnapshot Snapshot(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(index =>
			new BrowserCandidate(
				$"browser-{index}",
				$"Browser {index}",
				$@"C:\Browsers\b{index}.exe",
				"stable",
				"known-path"))];

		return new BrowserSnapshot(candidates, candidateCount, DateTimeOffset.UtcNow);
	}

	// The picker's anti-double-tap rule reads the wall clock, so the probe has to own it: with
	// TimeProvider.System the claim could not be shown to be live before the re-press at all.
	private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	internal sealed record Result(
		string? Failure,
		bool ClaimLiveBeforeRepress,
		bool RepressAccepted,
		bool ClaimAfterRepress,
		bool ClaimAfterClockAdvance,
		bool FocusedElementIsStillFirstCard,
		int RingedCardCountAfterRepress)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, false, false, false, false, false, 0);
		}
	}
}

// THE FIRST-RUN CONSENT GUARD ON THE KEYBOARD PATH (MEDIUM, finding N3). The click path
// (PickerSession.OnCardClicked) refuses to launch a card while the first-run consent contract
// says consent is required - it re-shows the prompt instead. The keyboard path
// (ActivateFocused, reached by Enter) had no such guard, so Enter launched a browser the mouse
// could not: one activation route honoured the consent gate and the other bypassed it.
//
// WHAT THIS PINS. Enter must be a no-op while consent is required, exactly like the click, and
// the session must stay up on its prompt (the guard's recovery path is the prompt, not an
// exit). NO WINDOW IS INVOLVED - the session is driven through its own test entry points, as
// PickerContractTests does - so no Avalonia bootstrap is needed here.
//
// TWO FALSE PASSES ARE CLOSED EXPLICITLY, because both would produce a zero launch count on
// the unfixed code:
//   1. the 100 ms visible-focus window: Enter arriving before it elapses is ignored by the
//      anti-double-tap gate, so the probe waits the window out and asserts the key was not
//      ignored for that reason (EnterIgnoredBeforeSafeCountForTests == 0);
//   2. a session that had already completed: every key is then a no-op, so the probe asserts
//      the session was still running when Enter arrived (CompleteCountForTests == 0).
//
// THE CONTROL SESSION IS THE OTHER HALF OF THE SAME ARGUMENT: the same harness, the same wait,
// the same Enter, with consent NOT required, really does launch - so the zero in the guarded
// session is attributable to the consent guard and not to a harness that cannot launch at all.
//
// THE SESSION IS DRIVEN ON A DEDICATED THREAD, deliberately, for the reason
// PickerUiThreadResponsivenessTests records: the Avalonia XAML probes may have left a
// SynchronizationContext installed on the thread that runs the tests, and ShowForTestsAsync
// awaits Task.Yield() between actions - a continuation posted to that context would never run
// while the caller blocks on the result. A fresh Thread starts with
// SynchronizationContext.Current == null (guaranteed), so the continuations run on the pool.
internal static class PickerFirstRunEnterProbe
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
			return Measure();
		}
		catch (Exception failure)
		{
			return Result.Failed($"{failure.GetType().FullName}: {failure.Message}");
		}
	}

	private static Result Measure()
	{
		SessionOutcome guarded = DriveSession(FirstRunConsentContract.Required);
		SessionOutcome control = DriveSession(FirstRunConsentContract.None);

		return new Result(
			Failure: guarded.Failure ?? control.Failure,
			GuardedLaunchCalls: guarded.LaunchCalls,
			GuardedEnterIgnoredBeforeSafe: guarded.EnterIgnoredBeforeSafe,
			GuardedCompleteCountAfterEnter: guarded.CompleteCountAfterEnter,
			GuardedExitAfterEnter: guarded.ExitAfterEnter,
			GuardedExitAfterEscape: guarded.ExitAfterEscape,
			ControlLaunchCalls: control.LaunchCalls,
			ControlExitAfterEnter: control.ExitAfterEnter);
	}

	// THE PHASES ARE SEPARATED DELIBERATELY, because the Enter phase has to be read on its own:
	//   1. priming - initialize the session, which starts the 100 ms visible-focus window;
	//   2. Enter   - read launch count and terminal state BEFORE any dismissal, so "no launch"
	//                can be told apart from "the session had already finished, so every key was
	//                a no-op anyway";
	//   3. Escape  - the guarded session's own way out must still work.
	private static SessionOutcome DriveSession(FirstRunConsentContract consent)
	{
		FakeLaunchHelperClient launch = new();
		PickerSession session = NewSession(launch, consent);

		string? failure = null;
		int launchCallsAfterEnter = int.MinValue;
		int completeCountAfterEnter = int.MinValue;
		int exitAfterEnter = int.MinValue;
		int exitAfterEscape = int.MinValue;

		Thread thread = new(() =>
		{
			try
			{
				// Phase 1: initialize the session. This is where the VM focuses slot 1 and the
				// 100 ms visible-focus window starts. The returned code is not asserted on: a
				// session left running returns GeneralError by design, and the point of this
				// phase is that the session is left RUNNING for phase 2.
				_ = session.ShowForTestsAsync([]).GetAwaiter().GetResult();

				// Wait the anti-double-tap window out, so the Enter below is not ignored for
				// THAT reason: the consent guard has to be the only thing left that can stop
				// the launch, or the assertion could not tell the two apart.
				Thread.Sleep(200);

				// Phase 2: the keyboard path, exactly as the Enter key routes it.
				exitAfterEnter = session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Enter, 0)]).GetAwaiter().GetResult();
				launchCallsAfterEnter = launch.LaunchCalls;
				completeCountAfterEnter = session.CompleteCountForTests;

				// Phase 3: dismissal must still work whatever phase 2 decided.
				exitAfterEscape = session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]).GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				failure = $"{ex.GetType().FullName}: {ex.Message}";
			}
		})
		{ IsBackground = true };

		thread.Start();

		// A deadlock detector, not a performance budget: the work above is three session calls
		// and one 200 ms wait.
		bool finished = thread.Join(TimeSpan.FromSeconds(15));

		return new SessionOutcome(
			failure ?? (finished ? null : "the session-driving thread did not finish within 15 s"),
			launchCallsAfterEnter,
			session.EnterIgnoredBeforeSafeCountForTests,
			completeCountAfterEnter,
			exitAfterEnter,
			exitAfterEscape);
	}

	private static PickerSession NewSession(ILaunchHelperClient launch, FirstRunConsentContract firstRun)
	{
		BrowserSnapshot snapshot = new(
			[new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")],
			1,
			DateTimeOffset.UtcNow);

		ActivationRequest activation = new(
			"https://example.com/",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		// The audit writer is injected as inert for the reason PickerUiThreadResponsivenessTests
		// records: the default writer acquires a PROCESS-GLOBAL named mutex and rewrites the
		// user's real %LocalAppData%\BroChoos\logs file on every session start, and xUnit runs
		// classes in parallel.
		return new PickerSession(
			new PickerRequest(activation, snapshot, firstRun),
			launch,
			new FirstRunConsentActionCoordinator(new ImmediateStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance,
			trackingCleanupOptions: null,
			trackingCleanupAuditWriter: new InertAuditWriter(),
			shortLinkExpansionOptions: null,
			networkHelperClient: null,
			activationHistoryStore: null);
	}

	private sealed record SessionOutcome(
		string? Failure,
		int LaunchCalls,
		int EnterIgnoredBeforeSafe,
		int CompleteCountAfterEnter,
		int ExitAfterEnter,
		int ExitAfterEscape);

	private sealed class FakeLaunchHelperClient : ILaunchHelperClient
	{
		public int LaunchCalls { get; private set; }

		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			LaunchCalls++;
			return Task.FromResult(new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null));
		}
	}

	private sealed class FakeSystemFallbackLauncher : ISystemFallbackLauncher
	{
		public LaunchResult Launch(string url)
		{
			_ = url;
			return new LaunchResult(LaunchStatus.Success, "system", null, null, null);
		}
	}

	private sealed class ImmediateStorageHelperClient : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			_ = command;
			_ = cancellationToken;
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, null, null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.publish-not-used-by-this-probe"));
		}
	}

	private sealed class InertAuditWriter : ITrackingCleanupAuditWriter
	{
		public void Write(TrackingCleanupAuditEvent cleanupEvent)
		{
			_ = cleanupEvent;
		}
	}

	internal sealed record Result(
		string? Failure,
		int GuardedLaunchCalls,
		int GuardedEnterIgnoredBeforeSafe,
		int GuardedCompleteCountAfterEnter,
		int GuardedExitAfterEnter,
		int GuardedExitAfterEscape,
		int ControlLaunchCalls,
		int ControlExitAfterEnter)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, -1, -1, -1, -1, -1, -1, -1);
		}
	}
}

// FINDING F1-a (MEDIUM): REACTIVATION LEFT THE CARD FOCUSED BUT RINGLESS, AND THE ONE-CARD
// PICKER COULD NOT RECOVER AT ALL.
//
// WHAT WENT WRONG. WindowBase.HandleActivated raises Activated and THEN calls
// FocusManager.SetFocusScope, whose restore path is Focus(focused) with
// NavigationMethod.Unspecified. Real focus therefore lands on the card again, but
// :focus-visible is deliberately NOT set (Avalonia sets it only for Tab/Directional), so the
// card holds focus with no ring - while the claim had already been dropped when the window lost
// OS focus (finding N1). MEASURED one-card: focusOnCardAfterReactivate=True,
// ringAfterReactivate=False, claimAfterReactivate=False, and neither a digit re-press nor a
// Tab-shaped refocus could recover it, because Focus() on the element that already holds focus
// is a framework no-op (KeyboardDevice.SetFocusedElement returns early for an unchanged
// element) - no GotFocus, so nothing re-arms the proof. Only Esc worked. The two-card picker
// recovered through a digit on the OTHER card, which is what proved the one-card verdict was a
// missing path rather than an unreachable state.
//
// WHY A REAL WINDOW IS REQUIRED. The whole defect lives in what the framework does to a real
// focus manager; FocusSlot/FocusCard return values are identical before and after the fix. The
// probe drives the production sequence through the handlers the platform delegates call:
// WindowBase wires IWindowBaseImpl.Deactivated/Activated to its own private handlers and
// TopLevel wires IWindowBaseImpl.LostFocus to PlatformImpl_LostFocus - and the delegates that
// carry them are non-client-accessible, so the probe invokes those handlers directly. They ARE
// the code a real deactivate/reactivate runs; only the delegate wrapper is bypassed.
internal static class PickerPlatformCallbacks
{
	internal static void Deactivate(PickerWindow window)
	{
		Invoke(window, "HandleDeactivated");
		Invoke(window, "PlatformImpl_LostFocus");
	}

	internal static void Activate(PickerWindow window)
	{
		Invoke(window, "HandleActivated");
	}

	private static void Invoke(PickerWindow window, string methodName)
	{
		for (Type? type = window.GetType(); type is not null; type = type.BaseType)
		{
			MethodInfo? method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (method is not null)
			{
				_ = method.Invoke(window, null);
				return;
			}
		}

		throw new InvalidOperationException(
			$"{methodName} was not found on PickerWindow or its base types; the framework's windowing contract changed.");
	}
}

internal static class PickerReactivationProbe
{
	private const string FocusVisiblePseudoClass = ":focus-visible";

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
		AdvanceableTimeProvider oneCardTime = new(DateTimeOffset.UnixEpoch);
		PickerViewModel oneCardVm = new("https://example.com/", Snapshot(1), oneCardTime);
		PickerWindow oneCard = new(oneCardVm, _ => { }, _ => { });

		try
		{
			ShowOffScreen(oneCard);

			// The production first-frame path: focus slot 1 and let the real report arm the claim.
			_ = oneCardVm.FocusSlot(1);
			oneCard.FocusCard(oneCardVm.FocusedIndex);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			oneCardTime.Advance(TimeSpan.FromMilliseconds(100));

			bool claimBeforeDeactivate = oneCardVm.CanActivateFocusedCard();
			bool ringBeforeDeactivate = Ringed(oneCard, 0);

			// 1. THE OS DEACTIVATION, through the handlers the platform delegates call:
			//    Deactivated sets IsActive=false, LostFocus is what clears the focused element.
			PickerPlatformCallbacks.Deactivate(oneCard);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			oneCardTime.Advance(TimeSpan.FromMilliseconds(100));

			bool claimAfterDeactivate = oneCardVm.CanActivateFocusedCard();
			int ringsAfterDeactivate = CountRinged(oneCard, 1);
			int indexAfterDeactivate = oneCardVm.FocusedIndex;

			// 2. THE OS REACTIVATION. HandleActivated restores focus to the card the focus scope
			//    remembers - with NavigationMethod.Unspecified, i.e. WITHOUT the ring.
			PickerPlatformCallbacks.Activate(oneCard);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			oneCardTime.Advance(TimeSpan.FromMilliseconds(100));

			bool focusOnCardAfterReactivate = ReferenceEquals(oneCard.FocusManager?.GetFocusedElement(), CardButton(oneCard, 0));
			bool ringAfterReactivate = Ringed(oneCard, 0);
			bool claimAfterReactivate = oneCardVm.CanActivateFocusedCard();
			bool targetIsCardAfterReactivate = ReferenceEquals(oneCardVm.GetFocusedActivationTarget(), oneCardVm.Cards[0]);

			// 3. THE PREVIOUSLY UNRECOVERABLE PATH: re-pressing the digit of the card that already
			//    holds focus. Pre-fix this was accepted by the VM and then did nothing at the window
			//    (Focus() no-op), leaving the claim dead for the rest of the session.
			bool digitRepressAccepted = oneCardVm.FocusSlot(1);
			if (digitRepressAccepted)
			{
				oneCard.FocusCard(oneCardVm.FocusedIndex);
			}

			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			oneCardTime.Advance(TimeSpan.FromMilliseconds(100));

			bool ringAfterDigitRepress = Ringed(oneCard, 0);
			bool claimAfterDigitRepress = oneCardVm.CanActivateFocusedCard();

			// 4. The other refocus shape the audit tried: FocusCard on the already-focused card
			//    ("Tab-shaped refocus"). It must be a real recovery too, not a silent no-op.
			oneCard.FocusCard(0);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			oneCardTime.Advance(TimeSpan.FromMilliseconds(100));

			bool tabRefocusLanded = ReferenceEquals(oneCard.FocusManager?.GetFocusedElement(), CardButton(oneCard, 0));
			bool ringAfterTabRefocus = Ringed(oneCard, 0);
			bool claimAfterTabRefocus = oneCardVm.CanActivateFocusedCard();

			// 5. THE TWO-CARD CONTROL: the case that used to recover through a digit on the OTHER
			//    card must keep working, and the ordinary card-to-card move must stay untouched.
			AdvanceableTimeProvider twoCardTime = new(DateTimeOffset.UnixEpoch);
			PickerViewModel twoCardVm = new("https://example.com/", Snapshot(2), twoCardTime);
			PickerWindow twoCard = new(twoCardVm, _ => { }, _ => { });

			try
			{
				ShowOffScreen(twoCard);
				_ = twoCardVm.FocusSlot(1);
				twoCard.FocusCard(twoCardVm.FocusedIndex);
				Dispatcher.UIThread.RunJobs();
				Dispatcher.UIThread.RunJobs();
				twoCardTime.Advance(TimeSpan.FromMilliseconds(100));

				PickerPlatformCallbacks.Deactivate(twoCard);
				Dispatcher.UIThread.RunJobs();
				Dispatcher.UIThread.RunJobs();

				PickerPlatformCallbacks.Activate(twoCard);
				Dispatcher.UIThread.RunJobs();
				Dispatcher.UIThread.RunJobs();
				twoCardTime.Advance(TimeSpan.FromMilliseconds(100));

				bool twoCardFocusOnFirst = ReferenceEquals(twoCard.FocusManager?.GetFocusedElement(), CardButton(twoCard, 0));
				bool twoCardRingAfterReactivate = Ringed(twoCard, 0);
				bool twoCardClaimAfterReactivate = twoCardVm.CanActivateFocusedCard();

				_ = twoCardVm.FocusSlot(2);
				twoCard.FocusCard(twoCardVm.FocusedIndex);
				Dispatcher.UIThread.RunJobs();
				Dispatcher.UIThread.RunJobs();
				twoCardTime.Advance(TimeSpan.FromMilliseconds(100));

				bool twoCardDigit2Ring = Ringed(twoCard, 1);
				bool twoCardDigit2Claim = twoCardVm.CanActivateFocusedCard();
				bool twoCardTargetIsSecond = ReferenceEquals(twoCardVm.GetFocusedActivationTarget(), twoCardVm.Cards[1]);

				return new Result(
					Failure: null,
					ClaimBeforeDeactivate: claimBeforeDeactivate,
					RingBeforeDeactivate: ringBeforeDeactivate,
					ClaimAfterDeactivate: claimAfterDeactivate,
					RingsAfterDeactivate: ringsAfterDeactivate,
					IndexAfterDeactivate: indexAfterDeactivate,
					FocusOnCardAfterReactivate: focusOnCardAfterReactivate,
					RingAfterReactivate: ringAfterReactivate,
					ClaimAfterReactivate: claimAfterReactivate,
					TargetIsCardAfterReactivate: targetIsCardAfterReactivate,
					DigitRepressAccepted: digitRepressAccepted,
					RingAfterDigitRepress: ringAfterDigitRepress,
					ClaimAfterDigitRepress: claimAfterDigitRepress,
					TabRefocusLanded: tabRefocusLanded,
					RingAfterTabRefocus: ringAfterTabRefocus,
					ClaimAfterTabRefocus: claimAfterTabRefocus,
					TwoCardFocusOnFirst: twoCardFocusOnFirst,
					TwoCardRingAfterReactivate: twoCardRingAfterReactivate,
					TwoCardClaimAfterReactivate: twoCardClaimAfterReactivate,
					TwoCardDigit2Ring: twoCardDigit2Ring,
					TwoCardDigit2Claim: twoCardDigit2Claim,
					TwoCardTargetIsSecond: twoCardTargetIsSecond);
			}
			finally
			{
				twoCard.Close();
				Dispatcher.UIThread.RunJobs();
			}
		}
		finally
		{
			oneCard.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static void ShowOffScreen(PickerWindow window)
	{
		window.ShowActivated = false;
		window.Topmost = false;
		window.Opacity = 0;
		window.Position = new PixelPoint(-32000, -32000);
		window.Show();
		Dispatcher.UIThread.RunJobs();
	}

	private static bool Ringed(PickerWindow window, int index)
	{
		return CardButton(window, index).Classes.Contains(FocusVisiblePseudoClass);
	}

	private static int CountRinged(PickerWindow window, int cardCount)
	{
		int ringed = 0;
		for (int index = 0; index < cardCount; index++)
		{
			if (Ringed(window, index))
			{
				ringed++;
			}
		}

		return ringed;
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the measurement no longer builds the containers.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private static BrowserSnapshot Snapshot(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(index =>
			new BrowserCandidate(
				$"browser-{index}",
				$"Browser {index}",
				$@"C:\Browsers\b{index}.exe",
				"stable",
				"known-path"))];

		return new BrowserSnapshot(candidates, candidateCount, DateTimeOffset.UtcNow);
	}

	private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	internal sealed record Result(
		string? Failure,
		bool ClaimBeforeDeactivate,
		bool RingBeforeDeactivate,
		bool ClaimAfterDeactivate,
		int RingsAfterDeactivate,
		int IndexAfterDeactivate,
		bool FocusOnCardAfterReactivate,
		bool RingAfterReactivate,
		bool ClaimAfterReactivate,
		bool TargetIsCardAfterReactivate,
		bool DigitRepressAccepted,
		bool RingAfterDigitRepress,
		bool ClaimAfterDigitRepress,
		bool TabRefocusLanded,
		bool RingAfterTabRefocus,
		bool ClaimAfterTabRefocus,
		bool TwoCardFocusOnFirst,
		bool TwoCardRingAfterReactivate,
		bool TwoCardClaimAfterReactivate,
		bool TwoCardDigit2Ring,
		bool TwoCardDigit2Claim,
		bool TwoCardTargetIsSecond)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, false, false, true, -1, -1, false, false, true, true, false, false, true, false, false, true, false, false, true, false, true, true);
		}
	}
}

// FINDING F1-b (MEDIUM): THE ADVERTISED ENTER RETRY AFTER AN UNKNOWN OUTCOME WAS DEAD.
//
// The recovery status line promises "Try again (Enter), Copy link (Ctrl+C), or Cancel (Esc)"
// (Status.LaunchUnknownRecovery). But the launch that produced it was started by a CLICK, and a
// click is a pointer press: it focuses the card with NO ring, so OnChoiceCardGotFocus disarms
// the rendered-focus proof (findings B/N2 - correctly, no invisible claim) and nothing re-armed
// it. Enter was therefore a no-op while the UI said it would retry. MEASURED:
// claimAfterPointerPress=False, ringedAfterPointerPress=False, clickReported=True,
// enterWouldRetry=False, ringedAfter=0.
//
// WHAT THIS PINS. The session's Unknown branch shows the recovery status and names the slot the
// recovery is about (the card the launch was for - the card the pointer focused). The window
// must turn that into a VISIBLE focus target: ring on that card plus a live claim, which is
// exactly the state Enter needs. The probe also records the pre-recovery state, so the
// assertion cannot pass because some unrelated path armed the claim.
internal static class PickerUnknownRetryProbe
{
	private const string FocusVisiblePseudoClass = ":focus-visible";

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
		AdvanceableTimeProvider time = new(DateTimeOffset.UnixEpoch);
		PickerViewModel vm = new("https://example.com/", Snapshot(2), time);
		int clickedCards = 0;
		PickerWindow window = new(vm, _ => { }, _ => clickedCards++);

		try
		{
			ShowOffScreen(window);

			// 1. The picker starts armed from the keyboard (production first-frame path).
			_ = vm.FocusSlot(1);
			window.FocusCard(vm.FocusedIndex);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimLiveBeforeClick = vm.CanActivateFocusedCard();

			// 2. THE CLICK, exactly as a mouse produces it: the pointer press moves focus to the
			//    clicked card WITHOUT a ring, which disarms the claim (B/N2), and the Click routed
			//    event reaches the window's own handler. This is the state the launch starts from.
			_ = CardButton(window, 1).Focus(NavigationMethod.Pointer, KeyModifiers.None);
			Dispatcher.UIThread.RunJobs();
			CardButton(window, 1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimAfterPointerPress = vm.CanActivateFocusedCard();
			int ringsAfterPointerPress = CountRinged(window);
			bool clickReported = clickedCards == 1;
			bool focusedCardIsClicked = ReferenceEquals(window.FocusManager?.GetFocusedElement(), CardButton(window, 1));

			// 3. THE UNKNOWN OUTCOME. The session shows the recovery status and hands the window the
			//    slot the recovery is about: the card the launch was for. The VM's own index cannot
			//    supply it - the pointer path drops the proof but keeps the intent (card 1 here), so
			//    the slot travels with the call.
			vm.ShowLaunchUnknownRecovery("Browser 2", 1);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool ringOnClickedCardAfterRecovery = Ringed(window, 1);
			int ringsAfterRecovery = CountRinged(window);
			bool claimAfterRecovery = vm.CanActivateFocusedCard();
			bool targetIsClickedCard = ReferenceEquals(vm.GetFocusedActivationTarget(), vm.Cards[1]);
			bool statusShown = vm.IsErrorStatusVisible;
			bool focusedCardIsStillClicked = ReferenceEquals(window.FocusManager?.GetFocusedElement(), CardButton(window, 1));

			return new Result(
				Failure: null,
				ClaimLiveBeforeClick: claimLiveBeforeClick,
				ClaimAfterPointerPress: claimAfterPointerPress,
				RingsAfterPointerPress: ringsAfterPointerPress,
				ClickReported: clickReported,
				FocusedCardIsClicked: focusedCardIsClicked,
				RingOnClickedCardAfterRecovery: ringOnClickedCardAfterRecovery,
				RingsAfterRecovery: ringsAfterRecovery,
				ClaimAfterRecovery: claimAfterRecovery,
				TargetIsClickedCard: targetIsClickedCard,
				StatusShown: statusShown,
				FocusedCardIsStillClicked: focusedCardIsStillClicked);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static void ShowOffScreen(PickerWindow window)
	{
		window.ShowActivated = false;
		window.Topmost = false;
		window.Opacity = 0;
		window.Position = new PixelPoint(-32000, -32000);
		window.Show();
		Dispatcher.UIThread.RunJobs();
	}

	private static bool Ringed(PickerWindow window, int index)
	{
		return CardButton(window, index).Classes.Contains(FocusVisiblePseudoClass);
	}

	private static int CountRinged(PickerWindow window)
	{
		int ringed = 0;
		for (int index = 0; index < 2; index++)
		{
			if (Ringed(window, index))
			{
				ringed++;
			}
		}

		return ringed;
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the measurement no longer builds the containers.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private static BrowserSnapshot Snapshot(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(index =>
			new BrowserCandidate(
				$"browser-{index}",
				$"Browser {index}",
				$@"C:\Browsers\b{index}.exe",
				"stable",
				"known-path"))];

		return new BrowserSnapshot(candidates, candidateCount, DateTimeOffset.UtcNow);
	}

	private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	internal sealed record Result(
		string? Failure,
		bool ClaimLiveBeforeClick,
		bool ClaimAfterPointerPress,
		int RingsAfterPointerPress,
		bool ClickReported,
		bool FocusedCardIsClicked,
		bool RingOnClickedCardAfterRecovery,
		int RingsAfterRecovery,
		bool ClaimAfterRecovery,
		bool TargetIsClickedCard,
		bool StatusShown,
		bool FocusedCardIsStillClicked)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, false, true, -1, false, false, false, -1, false, false, false, false);
		}
	}
}

// FINDING F-A1 (HIGH): THE SESSION-SIDE HALF OF THE UNKNOWN-RECOVERY FIX HAD NO TEST AT ALL.
//
// The Unknown-recovery fix is two halves. The WINDOW half - turning the recovery signal into a
// VISIBLY focused, armed card - is pinned by PickerUnknownRetryProbe above. But that probe calls
// vm.ShowLaunchUnknownRecovery("Browser 2", 1) DIRECTLY, so it cannot fail when PickerSession hands
// the window the WRONG slot: the slot argument is the half the session owns, and nothing exercised it.
//
// MEASURED (F-A1, two independent checks): changing PickerSession's call from `card.Slot - 1` to
// `card.Slot` left the whole suite green (112/112) while the real behaviour retried the WRONG card -
// the raw slot is one past the clicked card's zero-based index, the window rejects it as out of range,
// and its documented fallback lands on the view model's stale index (card 1).
//
// WHAT THIS PINS, THROUGH THE REAL SESSION AND A REAL WINDOW. The click is driven by
// PickerSession.ClickCardForTests (the session-level harness every Unknown-path test uses), the
// outcome comes from a fake helper client, and the recovery signal the session raises is consumed by
// a REAL PickerWindow bound to the SAME view model - the production wiring, minus the OS. The
// scenario is the hazardous one the finding names: card 2 is clicked while the VM's index is stale at
// 0, so a wrong slot argument cannot hide behind a coincidentally-correct index.
//
// THE OBSERVABLES ARE THE WINDOW'S END STATE, NOT A NEW SEAM. The slot the session handed over is
// recorded by subscribing to the view model's own recovery signal; what the window then DID with it is
// read from real focus and the VM index after the dispatcher has run the posted re-assert. The console
// marker (LAUNCH_UNKNOWN reassert slot=N) is deliberately NOT captured: this file's probes do not
// touch Console, because Console redirection is process-wide and would race the console-capturing
// collection (see the note at the top of the focus probe).
internal static class PickerSessionUnknownRetryProbe
{
	private const string FocusVisiblePseudoClass = ":focus-visible";

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
		FakeUnknownLaunchHelperClient launch = new();
		PickerSession session = NewSession(launch);

		// Phase 1: initialize the session so its view model exists. The returned code is not asserted
		// on - a session left running returns GeneralError by design - and no action is passed, so this
		// call never awaits and cannot be stranded by a SynchronizationContext the Avalonia probes left
		// on this thread.
		_ = session.ShowForTestsAsync([]).GetAwaiter().GetResult();

		// The session exposes no view-model handle by design (its tests observe session-level effects),
		// so the probe reads the private field the same way PickerPlatformCallbacks reads private
		// framework methods: reflection, confined to the test assembly.
		PickerViewModel vm = SessionViewModel(session);

		int recordedSlot = int.MinValue;
		vm.LaunchUnknownRecoveryShown += slot => recordedSlot = slot;

		// The REAL window, on the session's OWN view model: it subscribes to the recovery signal in its
		// constructor, exactly as production does, so the re-assert under test is the window's own.
		PickerWindow window = new(vm, _ => { }, _ => { });

		try
		{
			ShowOffScreen(window);

			// 2. The production first-frame path: focus card 1 from the keyboard, so the index the
			//    recovery must NOT trust is a legitimate intent before the pointer moves focus.
			_ = vm.FocusSlot(1);
			window.FocusCard(vm.FocusedIndex);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();

			int indexBeforeClick = vm.FocusedIndex;

			// 3. The pointer press on card 2, exactly as a mouse produces it: focus moves to the clicked
			//    card WITHOUT a ring, the rendered-focus proof is dropped, and the index deliberately
			//    keeps naming card 1 - the stale index this finding is about.
			_ = CardButton(window, 1).Focus(NavigationMethod.Pointer, KeyModifiers.None);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();

			bool clickedCardFocused = ReferenceEquals(window.FocusManager?.GetFocusedElement(), CardButton(window, 1));
			int indexAfterPointer = vm.FocusedIndex;

			// 4. THE CLICK, through the session's own handler. The fake helper completes synchronously,
			//    so the Unknown outcome has been handled and the recovery signal has already been raised
			//    by the time this returns; the window handles it at Loaded priority, hence the pump.
			session.ClickCardForTests(2);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();

			// The claim is time-gated on the session's own TimeProvider.System (the session builds its
			// view model without a test clock), so the advertised-retry check waits the gate out.
			Thread.Sleep(150);

			return new Result(
				Failure: null,
				IndexBeforeClick: indexBeforeClick,
				IndexAfterPointer: indexAfterPointer,
				ClickedCardFocused: clickedCardFocused,
				LaunchCalls: launch.LaunchCalls,
				RecordedSlot: recordedSlot,
				IndexAfterRecovery: vm.FocusedIndex,
				RingOnClickedCard: Ringed(window, 1),
				RingOnFirstCard: Ringed(window, 0),
				TargetIsClickedCard: ReferenceEquals(vm.GetFocusedActivationTarget(), vm.Cards[1]),
				ClaimArmed: vm.CanActivateFocusedCard(),
				StatusShown: vm.IsErrorStatusVisible);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static PickerViewModel SessionViewModel(PickerSession session)
	{
		FieldInfo? field = typeof(PickerSession).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic);
		return field?.GetValue(session) as PickerViewModel
			?? throw new InvalidOperationException(
				"PickerSession._vm was not found (or was not initialized); the session's test-visible shape changed.");
	}

	private static PickerSession NewSession(ILaunchHelperClient launch)
	{
		BrowserSnapshot snapshot = new(
			[
				new BrowserCandidate("browser-1", "Browser 1", @"C:\Browsers\b1.exe", "stable", "known-path"),
				new BrowserCandidate("browser-2", "Browser 2", @"C:\Browsers\b2.exe", "stable", "known-path"),
			],
			2,
			DateTimeOffset.UtcNow);

		ActivationRequest activation = new(
			"https://example.com/",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		// The audit writer is injected as inert for the reason PickerFirstRunEnterProbe records: the
		// default writer acquires a PROCESS-GLOBAL named mutex and rewrites the user's real
		// %LocalAppData%\BroChoos\logs file on every session start, and xUnit runs classes in parallel.
		return new PickerSession(
			new PickerRequest(activation, snapshot, FirstRunConsentContract.None),
			launch,
			new FirstRunConsentActionCoordinator(new ImmediateStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance,
			trackingCleanupOptions: null,
			trackingCleanupAuditWriter: new InertAuditWriter(),
			shortLinkExpansionOptions: null,
			networkHelperClient: null,
			activationHistoryStore: null);
	}

	private static void ShowOffScreen(PickerWindow window)
	{
		window.ShowActivated = false;
		window.Topmost = false;
		window.Opacity = 0;
		window.Position = new PixelPoint(-32000, -32000);
		window.Show();
		Dispatcher.UIThread.RunJobs();
	}

	private static bool Ringed(PickerWindow window, int index)
	{
		return CardButton(window, index).Classes.Contains(FocusVisiblePseudoClass);
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the measurement no longer builds the containers.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private sealed class FakeUnknownLaunchHelperClient : ILaunchHelperClient
	{
		public int LaunchCalls { get; private set; }

		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			LaunchCalls++;
			return Task.FromResult(new LaunchHelperOutcome(
				LaunchStatus.Unknown,
				request.BrowserId,
				"launch.browser-exit-race",
				"helper-launch",
				"launch.error.unknown"));
		}
	}

	private sealed class FakeSystemFallbackLauncher : ISystemFallbackLauncher
	{
		public LaunchResult Launch(string url)
		{
			_ = url;
			return new LaunchResult(LaunchStatus.Success, "system", null, null, null);
		}
	}

	private sealed class ImmediateStorageHelperClient : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			_ = command;
			_ = cancellationToken;
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, null, null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.publish-not-used-by-this-probe"));
		}
	}

	private sealed class InertAuditWriter : ITrackingCleanupAuditWriter
	{
		public void Write(TrackingCleanupAuditEvent cleanupEvent)
		{
			_ = cleanupEvent;
		}
	}

	internal sealed record Result(
		string? Failure,
		int IndexBeforeClick,
		int IndexAfterPointer,
		bool ClickedCardFocused,
		int LaunchCalls,
		int RecordedSlot,
		int IndexAfterRecovery,
		bool RingOnClickedCard,
		bool RingOnFirstCard,
		bool TargetIsClickedCard,
		bool ClaimArmed,
		bool StatusShown)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, -1, -1, false, -1, int.MinValue, -1, false, true, false, false, false);
		}
	}
}

// THE STALE-INDEX HOLE (HIGH): POINTER FOCUS, THEN THE DIGIT OF THE SAME CARD, KILLED ENTER.
//
// PickerViewModel.FocusSlot treats "the slot I am asked for is the slot I already name" as a
// no-op - a fix for the already-focused re-press (finding A). But FocusedIndex is NOT the
// focused element: the pointer path (B/N2) deliberately disarms the rendered-focus proof while
// leaving the index naming the card that was intended BEFORE the pointer moved focus
// (ClearFocusFromControl keeps the intent, drops only the proof). So after the pointer focuses
// card 2, FocusedIndex still says 0; pressing "2" takes the ORDINARY path, clears the proof,
// and the window calls Focus() on the card that already holds focus - a framework no-op, so no
// GotFocus and no re-arm. MEASURED: card2StillFocused=True, ringedAfterRepress=0, vmIndex=1,
// claimAfterRepress=False. The card the user asked for is focused and ringless, and Enter is
// dead - the same dead-key state as A, reached through the pointer.
//
// WHAT THIS PINS: after pointer-focusing card 2, pressing "2" must leave card 2 VISIBLY focused
// (ring) and Enter armed, with the VM's index naming card 2.
internal static class PickerStaleIndexRepressProbe
{
	private const string FocusVisiblePseudoClass = ":focus-visible";

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
		AdvanceableTimeProvider time = new(DateTimeOffset.UnixEpoch);
		PickerViewModel vm = new("https://example.com/", Snapshot(2), time);
		PickerWindow window = new(vm, _ => { }, _ => { });

		try
		{
			ShowOffScreen(window);

			// 1. Arm from the keyboard so there is a claim to lose.
			_ = vm.FocusSlot(1);
			window.FocusCard(vm.FocusedIndex);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimLiveBeforePointer = vm.CanActivateFocusedCard();

			// 2. THE POINTER PRESS on the OTHER card. It disarms the claim (B/N2) and leaves the
			//    VM's index pointing at card 1 - the premise this finding rests on, so the probe
			//    measures it rather than assuming it.
			_ = CardButton(window, 1).Focus(NavigationMethod.Pointer, KeyModifiers.None);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimAfterPointer = vm.CanActivateFocusedCard();
			int indexAfterPointer = vm.FocusedIndex;
			bool card2FocusedAfterPointer = ReferenceEquals(window.FocusManager?.GetFocusedElement(), CardButton(window, 1));
			int ringsAfterPointer = CountRinged(window);

			// 3. THE DIGIT RE-PRESS of the card the pointer focused, routed exactly as
			//    PickerSession routes it: FocusSlot, then FocusCard on the index it reports.
			bool accepted = vm.FocusSlot(2);
			if (accepted)
			{
				window.FocusCard(vm.FocusedIndex);
			}

			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			int indexAfterRepress = vm.FocusedIndex;
			bool card2StillFocused = ReferenceEquals(window.FocusManager?.GetFocusedElement(), CardButton(window, 1));
			bool ringOnCard2AfterRepress = Ringed(window, 1);
			int ringsAfterRepress = CountRinged(window);
			bool claimAfterRepress = vm.CanActivateFocusedCard();
			bool targetIsCard2 = ReferenceEquals(vm.GetFocusedActivationTarget(), vm.Cards[1]);

			return new Result(
				Failure: null,
				ClaimLiveBeforePointer: claimLiveBeforePointer,
				ClaimAfterPointer: claimAfterPointer,
				IndexAfterPointer: indexAfterPointer,
				Card2FocusedAfterPointer: card2FocusedAfterPointer,
				RingsAfterPointer: ringsAfterPointer,
				RepressAccepted: accepted,
				IndexAfterRepress: indexAfterRepress,
				Card2StillFocused: card2StillFocused,
				RingOnCard2AfterRepress: ringOnCard2AfterRepress,
				RingsAfterRepress: ringsAfterRepress,
				ClaimAfterRepress: claimAfterRepress,
				TargetIsCard2: targetIsCard2);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static void ShowOffScreen(PickerWindow window)
	{
		window.ShowActivated = false;
		window.Topmost = false;
		window.Opacity = 0;
		window.Position = new PixelPoint(-32000, -32000);
		window.Show();
		Dispatcher.UIThread.RunJobs();
	}

	private static bool Ringed(PickerWindow window, int index)
	{
		return CardButton(window, index).Classes.Contains(FocusVisiblePseudoClass);
	}

	private static int CountRinged(PickerWindow window)
	{
		int ringed = 0;
		for (int index = 0; index < 2; index++)
		{
			if (Ringed(window, index))
			{
				ringed++;
			}
		}

		return ringed;
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the measurement no longer builds the containers.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private static BrowserSnapshot Snapshot(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(index =>
			new BrowserCandidate(
				$"browser-{index}",
				$"Browser {index}",
				$@"C:\Browsers\b{index}.exe",
				"stable",
				"known-path"))];

		return new BrowserSnapshot(candidates, candidateCount, DateTimeOffset.UtcNow);
	}

	private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	internal sealed record Result(
		string? Failure,
		bool ClaimLiveBeforePointer,
		bool ClaimAfterPointer,
		int IndexAfterPointer,
		bool Card2FocusedAfterPointer,
		int RingsAfterPointer,
		bool RepressAccepted,
		int IndexAfterRepress,
		bool Card2StillFocused,
		bool RingOnCard2AfterRepress,
		int RingsAfterRepress,
		bool ClaimAfterRepress,
		bool TargetIsCard2)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, false, true, -1, false, -1, false, -1, false, false, -1, true, false);
		}
	}
}

// THE NO-RING => NO-CLAIM INVARIANT (D-131), re-asserted against every disarm route the picker
// has, plus the arming direction that the three F1 fixes must never break.
//
// The fixes all add new places where focus and the claim are re-established, and every one of
// them could over-arm: a claim that outlives its ring is the invisible-claim defect D-131
// exists to prevent. This probe therefore walks the routes that drop focus - focus cleared
// outright (the N1 path), and a ringless pointer focus on another card (the B/N2 path) - and
// requires ZERO ringed cards and a DEAD claim in each, while the index survives as the intent.
// The positive control at the end arms through a real Tab-shaped focus move and requires the
// ring and the claim together, so "always dead" cannot pass this probe either.
internal static class PickerNoRingNoClaimProbe
{
	private const string FocusVisiblePseudoClass = ":focus-visible";

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
		AdvanceableTimeProvider time = new(DateTimeOffset.UnixEpoch);
		PickerViewModel vm = new("https://example.com/", Snapshot(2), time);
		PickerWindow window = new(vm, _ => { }, _ => { });

		try
		{
			ShowOffScreen(window);

			_ = vm.FocusSlot(1);
			window.FocusCard(vm.FocusedIndex);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimLiveBeforeDisarm = vm.CanActivateFocusedCard();

			// ROUTE 1: focus cleared outright - no card is focused, so no ring may exist and the
			// claim must be dead while the index survives as the re-assert intent.
			window.FocusManager!.ClearFocus();
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimAfterClearFocus = vm.CanActivateFocusedCard();
			int ringsAfterClearFocus = CountRinged(window);
			int indexAfterClearFocus = vm.FocusedIndex;

			// ROUTE 2: the pointer focuses the OTHER card with no ring - focus IS on a card, but
			// not visibly, so the claim must still be dead.
			_ = CardButton(window, 1).Focus(NavigationMethod.Pointer, KeyModifiers.None);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimAfterRinglessFocus = vm.CanActivateFocusedCard();
			int ringsAfterRinglessFocus = CountRinged(window);

			// POSITIVE CONTROL: a real Tab-shaped focus move onto card 1 must restore BOTH the ring
			// and the claim, so the probe cannot pass by keeping the picker permanently dead.
			window.FocusCard(0);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool ringAfterTabRefocus = Ringed(window, 0);
			int ringsAfterTabRefocus = CountRinged(window);
			bool claimAfterTabRefocus = vm.CanActivateFocusedCard();

			return new Result(
				Failure: null,
				ClaimLiveBeforeDisarm: claimLiveBeforeDisarm,
				ClaimAfterClearFocus: claimAfterClearFocus,
				RingsAfterClearFocus: ringsAfterClearFocus,
				IndexAfterClearFocus: indexAfterClearFocus,
				ClaimAfterRinglessFocus: claimAfterRinglessFocus,
				RingsAfterRinglessFocus: ringsAfterRinglessFocus,
				RingAfterTabRefocus: ringAfterTabRefocus,
				RingsAfterTabRefocus: ringsAfterTabRefocus,
				ClaimAfterTabRefocus: claimAfterTabRefocus);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static void ShowOffScreen(PickerWindow window)
	{
		window.ShowActivated = false;
		window.Topmost = false;
		window.Opacity = 0;
		window.Position = new PixelPoint(-32000, -32000);
		window.Show();
		Dispatcher.UIThread.RunJobs();
	}

	private static bool Ringed(PickerWindow window, int index)
	{
		return CardButton(window, index).Classes.Contains(FocusVisiblePseudoClass);
	}

	private static int CountRinged(PickerWindow window)
	{
		int ringed = 0;
		for (int index = 0; index < 2; index++)
		{
			if (Ringed(window, index))
			{
				ringed++;
			}
		}

		return ringed;
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the measurement no longer builds the containers.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private static BrowserSnapshot Snapshot(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(index =>
			new BrowserCandidate(
				$"browser-{index}",
				$"Browser {index}",
				$@"C:\Browsers\b{index}.exe",
				"stable",
				"known-path"))];

		return new BrowserSnapshot(candidates, candidateCount, DateTimeOffset.UtcNow);
	}

	private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	internal sealed record Result(
		string? Failure,
		bool ClaimLiveBeforeDisarm,
		bool ClaimAfterClearFocus,
		int RingsAfterClearFocus,
		int IndexAfterClearFocus,
		bool ClaimAfterRinglessFocus,
		int RingsAfterRinglessFocus,
		bool RingAfterTabRefocus,
		int RingsAfterTabRefocus,
		bool ClaimAfterTabRefocus)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, false, true, -1, -1, true, -1, false, -1, false);
		}
	}
}

[Collection(AvaloniaUiCollection.Name)]
public sealed class PickerXamlLoadTests
{
	[Fact]
	public void Picker_window_constructs_from_compiled_XAML()
	{
		PickerXamlProbe.ProbeResult result = PickerXamlProbe.Run();

		_ = result.Failure.Should().BeNull(
			"PickerWindow's constructor loads PickerTokens.axaml through compiled XAML; a theme-variant, "
			+ "alias-shape or token-type defect there escapes PickerSession.OpenWindow() and kills the process "
			+ "on every launch, which is exactly what happened before this test existed");
	}

	[Fact]
	public void High_contrast_overlay_resolves_every_colour_role_to_a_brush()
	{
		PickerXamlProbe.ProbeResult result = PickerXamlProbe.Run();

		_ = result.MissingRoles.Should().BeEmpty(
			"every colour role must resolve under the High Contrast variant; an alias whose source key does not "
			+ "exist in the installed Fluent theme throws KeyNotFoundException at resolution time, i.e. only once "
			+ "the OS high-contrast setting is switched on");

		_ = result.RoleTypes.Should().OnlyContain(pair => pair.Value == "Avalonia.Media.SolidColorBrush");
	}

	[Fact]
	public void High_contrast_variant_used_by_the_app_is_the_one_the_token_dictionary_is_keyed_by()
	{
		// The dictionary key and the requested variant must be the SAME ThemeVariant instance.
		// A custom variant cannot be spelled as a string key in XAML at all (the framework's
		// converter rejects it), so if these two ever diverge the HC overlay becomes
		// unreachable - silently, because a variant with no matching dictionary just falls
		// back to its parent.
		OsAccessibilitySettings highContrast = new(HighContrast: true, ReducedMotion: false);

		_ = highContrast.ThemeVariant(platformSettings: null)
			.Should().BeSameAs(PickerThemeVariants.HighContrast);

		// And the shared instance must not be one of the built-ins, which would collide with
		// the Light/Dark/Default dictionaries already carrying those keys.
		_ = PickerThemeVariants.HighContrast.Should().NotBeSameAs(ThemeVariant.Light);
		_ = PickerThemeVariants.HighContrast.Should().NotBeSameAs(ThemeVariant.Dark);
		_ = PickerThemeVariants.HighContrast.Should().NotBeSameAs(ThemeVariant.Default);
	}

	[Fact]
	public void Window_height_is_the_measured_content_height_not_a_card_count_extrapolation()
	{
		PickerXamlProbe.ProbeResult result = PickerXamlProbe.Run();

		// Two cards, no status line. The old formula produced 170 + 2*62 + 8 = 302 DIP, which
		// was ~60 DIP taller than the real layout. The measurement must land well below that
		// and must be a real positive value.
		_ = result.MeasuredHeightDip.Should().BeGreaterThan(0);
		_ = result.MeasuredHeightDip.Should().BeLessThan(302,
			"the card-count formula over-reserved space; the measured height must reflect the real layout");
	}

	// THE FOCUS-RING GUARD (GAP-1). The D-139 fix - FocusButton passing NavigationMethod.Tab
	// instead of Unspecified - was invisible to the whole suite: reverting it broke nothing.
	//
	// WHY IT CAN BE TESTED AT ALL. A focus ring cannot be observed on a window that was never
	// shown (MEASURED: Focus() returns false pre-Show because no focus manager is attached yet),
	// so this drives the REAL production path on a window that IS shown - off-screen, opacity 0,
	// non-activating, never topmost - and reads the pseudo-class the :focus-visible style hangs
	// off. Nothing is rendered to the user and the window is closed in a finally.
	//
	// WHY IT CATCHES THE REVERT. NavigationMethod is not a hint the framework may ignore: it is
	// what decides whether :focus-visible is set. MEASURED on the real card: focus lands either
	// way (Focus() returns true), but with Unspecified the pseudo-class is NOT set - so the
	// assertion below fails on the reverted call site, not on some incidental difference.
	//
	// IT ALSO PINS THE MEASUREMENT CONTRACT (NEW-4). The same window is asserted to hold ONE
	// height from construction through first-frame focus, because focus is applied on every
	// launch and the pre-show height must already be the focused height. Reverting that fix
	// (measuring the unfocused geometry) makes this window grow 8 DIP at first frame.
	[Fact]
	public void Programmatic_focus_shows_the_visible_ring_and_the_window_never_resizes_at_first_frame()
	{
		PickerFocusProbe.Result result = PickerFocusProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.RingVisibleAfterProgrammaticFocus.Should().BeTrue(
			"FocusCard is the keyboard/programmatic navigation path, so it must produce the same "
			+ ":focus-visible state native Tab does; passing NavigationMethod.Unspecified leaves the "
			+ "ring invisible and is the D-139 defect this asserts against");

		_ = result.FocusedElementIsCard.Should().BeTrue("focus must really land on the card, not just set a pseudo-class");

		_ = result.HeightAtConstruction.Should().Be(result.HeightAfterFirstFrameFocus,
			"the first frame always focuses card 1, so the pre-show height must already be the FOCUSED "
			+ "height; otherwise the window visibly shrinks on every launch");

		_ = result.HeightAtConstruction.Should().Be(result.HeightWithUnfocusedGeometry - PickerFocusProbe.FocusRingDeltaDip,
			"the focused geometry must differ from the unfocused one by exactly the focus treatment's "
			+ "padding/border delta; if this fails the reservation stopped tracking the real style");

		// THE ONE-RING INVARIANT, and the reason the pre-show reservation needs a release path at
		// all. The reservation sets the SAME pseudo-class the real focus treatment does, and the
		// framework only clears that bit from the element that HELD focus - so a reservation left
		// standing while focus lands on a DIFFERENT card would show two rings at once and collapse
		// the height (MEASURED before the fix: 2 ringed cards, 222 DIP). This drives focus to the
		// second card while the reservation is still standing, which is the path a digit key takes.
		_ = result.RingedCardCountAfterFocusMovedDirectlyWhileReserved.Should().Be(1,
			"exactly one card may show the focused treatment; a stale pre-show reservation would "
			+ "leave two, and the height would collapse below the content it is measuring");

		_ = result.RingedCardCountAfterFocusMovedToSecondCard.Should().Be(1,
			"moving focus off the first card must leave exactly one ringed card, not zero and not two");
	}

	// THE SNAPSHOT-REPLACEMENT FOCUS GUARD (latent D-131 corner, found by the level-1 audit).
	// ApplySnapshot replaces the card collection; the containers that carried real focus and
	// the :focus-visible ring are destroyed with it. The VM's rendered-focus proof used to
	// survive any replacement that did not shrink the list, so the window ended up with
	// focus nowhere while Enter still had a target - the invisible claim D-131 forbids,
	// reachable from the first-run consent action (FirstRunConsentActionCoordinator).
	//
	// WHAT THIS PINS, both halves:
	//   1. the claim is dead the moment the replacement happens (read synchronously, before
	//      any layout pass could focus a new container), so Enter can never act invisibly;
	//   2. real focus and the ring are re-asserted on the card the VM still names - or on the
	//      first card when it names none - and the claim only comes back through that real
	//      focus report.
	[Fact]
	public void Snapshot_replacement_never_leaves_a_claim_without_visible_focus()
	{
		PickerSnapshotFocusProbe.Result result = PickerSnapshotFocusProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.ClaimLiveBeforeReplacement.Should().BeTrue(
			"the probe must start from a genuinely dangerous state - a live claim - or the "
			+ "assertion below would pass for the wrong reason (the 100 ms visibility window)");

		_ = result.ClaimAfterReplacement.Should().BeFalse(
			"replacing the cards destroys the container that carried the visible focus, so Enter "
			+ "must be a no-op until focus is re-established; a surviving claim is the invisible "
			+ "claim D-131 forbids (measured pre-fix: canActivate=True with focus nowhere)");

		_ = result.ReassertedCardIsFocused.Should().BeTrue(
			"the window owns real focus, so it must re-assert it on the card the VM still names "
			+ "after the replacement, not leave the picker with focus nowhere");

		_ = result.ReassertedCardHasRing.Should().BeTrue(
			"the re-asserted card must carry :focus-visible, or the choice is not visibly focused "
			+ "(D-139/D-131)");

		_ = result.ClaimAfterReassert.Should().BeTrue(
			"once the re-asserted card is really focused, the claim must come back - the fix must "
			+ "not trade an invisible claim for a dead keyboard");

		_ = result.TargetIsReassertedCard.Should().BeTrue(
			"Enter must act on the card that is visibly focused after the replacement");

		_ = result.ShrinkIndex.Should().Be(0, "a shrinking replacement clamps the index to the last card");
		_ = result.ClaimAfterShrink.Should().BeFalse("the clamp also invalidates the rendered-focus proof");
		_ = result.ShrinkFocusOnFirstCard.Should().BeTrue("the clamped card is the one focus is re-asserted on");
		_ = result.ShrinkRingOnFirstCard.Should().BeTrue("and it carries the ring");

		_ = result.EmptyIndex.Should().Be(-1, "an empty replacement leaves no focused card");
		_ = result.EmptyTargetIsNull.Should().BeTrue("with no cards there is no activation target at all");
		_ = result.EmptyClaim.Should().BeFalse("and therefore no claim");

		// THE FIRST-RUN SHAPE: an empty snapshot replaced by the discovered list, with the VM
		// naming no card. The re-assert must fall back to the first card rather than leaving
		// the picker unfocused, and the claim must come back only through that real focus.
		_ = result.ClaimAfterEmptyReplacement.Should().BeFalse(
			"the discovered list arrives as a replacement, so Enter must be a no-op until its "
			+ "first card is visibly focused - the first-run path is the one this defect lives on");

		_ = result.FallbackFocusOnFirstCard.Should().BeTrue(
			"with no card named, the re-assert must fall back to card 1 (the picker's documented "
			+ "resting state), not leave focus nowhere");

		_ = result.FallbackRingOnFirstCard.Should().BeTrue("and the fallback card must carry the ring");

		_ = result.ClaimAfterFallbackReassert.Should().BeTrue(
			"after the fallback focus lands, Enter must work again - the first-run user must not "
			+ "be left with a dead keyboard");
	}

	// THE ALREADY-FOCUSED-DIGIT GUARD (HIGH). Re-pressing the digit of the card that is already
	// focused used to clear the VM's rendered-focus proof without anything re-arming it: the
	// window called Focus() on the card that already held focus, Avalonia's
	// KeyboardDevice.SetFocusedElement is a no-op for an unchanged element, so no GotFocus was
	// raised and the proof stayed dead. The card remained visibly ringed while Enter was
	// permanently ignored - the user sees a focused card that cannot be activated.
	//
	// WHAT THIS PINS, in the order the defect unfolds:
	//   1. the claim really is live before the re-press (so the assertion below cannot pass
	//      because the 100 ms window had not elapsed);
	//   2. the re-press is still accepted as a navigation (FocusSlot returns true - the card IS
	//      the focused one, so this is not an out-of-range no-op) and focus stays on that card;
	//   3. the claim is STILL live afterwards, so Enter still works;
	//   4. no amount of waiting revives it if the report never came, which is why the probe also
	//      advances the clock - the pre-fix state is dead, not merely delayed.
	[Fact]
	public void Repressing_the_focused_card_digit_leaves_Enter_armed()
	{
		PickerRepressDigitProbe.Result result = PickerRepressDigitProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.ClaimLiveBeforeRepress.Should().BeTrue(
			"the probe must start from a live claim, or the re-press assertion below would pass "
			+ "for the wrong reason (the 100 ms anti-double-tap window)");

		_ = result.RepressAccepted.Should().BeTrue(
			"re-pressing the digit of the focused card is a valid navigation (the slot is in "
			+ "range), so FocusSlot must still report it as handled");

		_ = result.FocusedElementIsStillFirstCard.Should().BeTrue(
			"the re-press must not move real focus off the card it names");

		_ = result.ClaimAfterRepress.Should().BeTrue(
			"the card is still visibly focused, so Enter must still be armed: clearing the "
			+ "rendered-focus proof on a re-press of the SAME card cannot be re-armed by a real "
			+ "focus report (Focus() on an already-focused element raises no GotFocus), so the "
			+ "picker would be left with a ringed card and a permanently dead Enter key");

		_ = result.ClaimAfterClockAdvance.Should().BeTrue(
			"the pre-fix state is permanently dead, not merely delayed: once the proof is cleared "
			+ "with no focus report to re-arm it, no later instant revives it");

		_ = result.RingedCardCountAfterRepress.Should().Be(1,
			"exactly one card may show the focused treatment after the re-press");
	}

	// THE FIRST-RUN CONSENT GUARD ON THE KEYBOARD PATH (MEDIUM, finding N3). The click path
	// refuses to launch a card while consent is required (it re-shows the prompt); the keyboard
	// path did not, so Enter bypassed the consent gate the mouse honours.
	//
	// THE FALSE PASSES ARE CLOSED IN THE PROBE, and the assertions restate them: Enter is not
	// ignored by the anti-double-tap gate (the probe waits the 100 ms out and asserts the
	// before-safe counter is 0), and the session is still running when Enter arrives (so "no
	// launch" cannot mean "the session was already over"). The CONTROL session - same harness,
	// same wait, same Enter, consent NOT required - must launch, which is what makes the zero
	// in the guarded session attributable to the guard rather than to a harness that cannot
	// launch at all.
	[Fact]
	public void Enter_is_blocked_by_the_first_run_consent_guard_like_a_card_click()
	{
		PickerFirstRunEnterProbe.Result result = PickerFirstRunEnterProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.ControlLaunchCalls.Should().Be(1,
			"the control session (consent not required) proves the harness can launch on Enter; "
			+ "without it the guarded session's zero could be an artefact of the probe");

		_ = result.ControlExitAfterEnter.Should().Be(ExitCodes.Success,
			"the control session's launch succeeds, so the session completes exactly as it did "
			+ "before this guard existed");

		_ = result.GuardedEnterIgnoredBeforeSafe.Should().Be(0,
			"Enter must not have been ignored by the 100 ms visible-focus gate: that would "
			+ "produce the same zero launch count for an unrelated reason");

		_ = result.GuardedLaunchCalls.Should().Be(0,
			"the keyboard path must honour the first-run consent gate exactly as the click path "
			+ "does: launching before consent is the bypass this finding is about");

		_ = result.GuardedCompleteCountAfterEnter.Should().Be(0,
			"the guard's recovery is the consent prompt, so the session must stay up - an exit "
			+ "here would be a different behaviour from the click path");

		_ = result.GuardedExitAfterEscape.Should().Be(ExitCodes.Success,
			"the guarded session must still dismiss normally afterwards: the fix blocks the "
			+ "launch, not the user's way out");
	}

	// NEW-1 (0A-5 audit finding), the BEHAVIOURAL half of the pin. PickerTokens.axaml is merged
	// into Window.Resources, which Avalonia searches BEFORE Application.Resources - so while the
	// five runtime-owned motion keys were also declared there, the OS accessibility setting was
	// silently overridden: with reduced motion ON, a real DynamicResource consumer still resolved
	// the static "animations enabled" value.
	//
	// The source-level half lives in BroChoos.Accessibility.Tests (which can read the token file);
	// this half proves the RESOLUTION, which needs a live Application and a real consumer - which
	// is exactly what this assembly has and that one does not.
	[Fact]
	public void Runtime_owned_tokens_resolve_to_the_application_value_not_a_window_level_declaration()
	{
		PickerTokenProbe.Result result = PickerTokenProbe.Run();

		_ = result.Failure.Should().BeNull();

		// The application's write is what a consumer must see, for both a double-valued and a
		// boolean-valued token.
		_ = result.ResolvedMotionFast.Should().Be(0d,
			"the application wrote bc.motion.fast = 0 for reduced motion; a declaration in the window's "
			+ "merged token dictionary would shadow it (the defect this pins)");
		_ = result.ResolvedReducedMotion.Should().Be(true,
			"the application wrote bc.os.reducedMotion = true; the window must not shadow it either");
		_ = result.ResolvedTransitionsEnabled.Should().Be(false,
			"bc.motion.transitions.enabled is written by the application as the inverse of reduced motion");

		// And the window's own dictionary must not carry the key at all - the shadow is what made
		// this fail before, so its absence is asserted directly rather than inferred.
		_ = result.WindowDeclaresMotionFast.Should().BeFalse(
			"PickerTokens.axaml must not declare a runtime-owned key: any declaration in a dictionary "
			+ "the window merges wins over Application.Resources");
	}

	// FINDING B (MEDIUM): POINTER-SHAPED FOCUS MUST NOT ARM A CLAIM WITH NO RING.
	//
	// OnChoiceCardGotFocus armed the claim for ANY GotFocus, but Avalonia sets :focus-visible only
	// for keyboard-shaped navigation (NavigationMethod.Tab / Directional). A pointer press focuses
	// the card with NO ring, so the card became Enter's target while nothing on screen said so -
	// the invisible claim D-131 forbids, reachable by the mouse.
	//
	// THE TEST HAS TWO HALVES AND THE SECOND IS THE DANGEROUS ONE:
	//   1. cold start - a pointer press on an unarmed picker must leave Enter a no-op;
	//   2. the stale-claim path - the picker is ALREADY armed from the keyboard (startup focuses
	//      card 1 through the real FocusCard path) and the user then presses the mouse on ANOTHER
	//      card. Gating alone does not help here: the claim from step 1 is still live and still
	//      names card 1, whose ring the framework has just cleared - so Enter would launch a card
	//      whose ring is gone. Only disarming the claim on ringless focus closes this.
	[Fact]
	public void Pointer_shaped_focus_does_not_arm_an_invisible_claim()
	{
		PickerPointerFocusProbe.Result result = PickerPointerFocusProbe.Run();

		_ = result.Failure.Should().BeNull();

		// Half 1: cold start, pointer press, no keyboard focus has ever landed.
		_ = result.ClaimAfterColdPointerFocus.Should().BeFalse(
			"a pointer press focuses the card WITHOUT :focus-visible (Avalonia sets that pseudo-class "
			+ "only for Tab/Directional navigation), so the card must not become Enter's target: "
			+ "MEASURED pre-fix ringFirst=False, ringSecond=False, claimLive=True");

		// Half 2: the keyboard armed the claim, then the pointer moved focus to another card.
		_ = result.ClaimLiveFromKeyboardBeforePointer.Should().BeTrue(
			"the probe must start half 2 from a genuinely live claim, or the assertion below could "
			+ "pass because the 100 ms visible-focus window had not elapsed");

		_ = result.ClaimAfterPointerMovedFocusOffTheArmedCard.Should().BeFalse(
			"once the pointer moves focus, the armed card's ring is cleared by the framework and the "
			+ "newly focused card has no ring either: no visible focus means NO live claim, so Enter "
			+ "must be a no-op rather than launch the card the user no longer sees focused");

		// The ring facts the fix rests on, asserted rather than assumed.
		_ = result.RingedCardsAfterPointerFocus.Should().Be(0,
			"a pointer-focused card carries no :focus-visible ring - this is the framework fact the "
			+ "whole finding is about, so the probe pins it rather than trusting it");
	}

	// FINDING N1 (HIGH): FOCUS LEAVING THE CARD LIST MUST DISARM THE CLAIM.
	//
	// No LostFocus handler existed anywhere in the repo (0 matches), so the claim survived focus
	// moving off the card list entirely: MEASURED ringedCards=0, claim=True. Enter then launched a
	// browser while nothing on the picker was focused - the invisible claim again.
	//
	// THE OVER-CORRECTION IS PINNED TOO, and it is the reason the handler must look at where focus
	// WENT rather than just fire on any LostFocus: focus moving BETWEEN cards is the ordinary
	// Tab/arrow/digit walk and must never disarm (the new card re-arms through its own GotFocus).
	[Fact]
	public void Focus_leaving_the_card_list_disarms_the_claim_without_forgetting_the_intent()
	{
		PickerFocusLeftProbe.Result result = PickerFocusLeftProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.ClaimLiveBeforeFocusLeft.Should().BeTrue(
			"the probe must start from a genuinely live claim, or the assertion below could pass "
			+ "because the 100 ms window had not elapsed");

		_ = result.ClaimAfterFocusLeft.Should().BeFalse(
			"with focus cleared there is no ring on any card, so Enter must be a no-op: a surviving "
			+ "claim is the invisible claim D-131 forbids (MEASURED pre-fix ringedCards=0, claim=True)");

		_ = result.FocusedIndexAfterFocusLeft.Should().Be(0,
			"the disarm must clear only the rendered-focus PROOF, not the index: the index is the "
			+ "re-assert intent, so forgetting it would make a later focus move start from nowhere");

		_ = result.ClaimAfterFocusMovedBetweenCards.Should().BeTrue(
			"focus moving between cards is the ordinary Tab/arrow walk and must NOT disarm: the new "
			+ "card carries the ring and re-arms the claim through its own focus report");
	}

	// FINDING N2 (MEDIUM): THE CLICK ITSELF IS THE ACTION, SO IT MUST NOT ARM ENTER.
	//
	// OnChoiceCardClick called SetFocusFromControl unconditionally. On a pointer-focused card that
	// is an invisible claim (same shape as B), and it is worse than redundant: the click already
	// launched the browser, so arming Enter leaves a loaded weapon pointed at a card the user did
	// not visibly focus. MEASURED pre-fix ringsAfter=0, claim=True.
	//
	// NO FOCUS IS INVOLVED AT ALL IN THIS PROBE, deliberately: the click is driven directly, so the
	// assertion isolates the click path and cannot pass or fail because of the focus path.
	[Fact]
	public void Clicking_a_card_does_not_arm_enter()
	{
		PickerClickProbe.Result result = PickerClickProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.FocusedElementBeforeClick.Should().BeFalse(
			"the probe deliberately clicks with NO focus on any card, so the assertion below is "
			+ "about the click path alone");

		_ = result.ClaimAfterClick.Should().BeFalse(
			"the click is itself the action; arming Enter on a card that carries no ring is the "
			+ "invisible claim D-131 forbids (MEASURED pre-fix ringsAfter=0, claim=True)");

		_ = result.ClickedCardWasReported.Should().BeTrue(
			"the click must still reach the session's card handler - the fix removes the arming, "
			+ "not the click");
	}

	// FINDING F1-a (MEDIUM): REACTIVATION MUST RESTORE THE RING AND THE CLAIM - INCLUDING THE
	// ONE-CARD PICKER, WHICH HAD NO RECOVERY AT ALL.
	//
	// MEASURED pre-fix (one card): claimAfterDeactivate=False, focusOnCardAfterReactivate=True,
	// ringAfterReactivate=False, claimAfterReactivate=False; a digit re-press was accepted but
	// left claimAfterDigitRepress=False, arrows moved nothing (clamped), and a Tab-shaped refocus
	// was accepted while raising no GotFocus - so the claim stayed False and only Esc worked.
	//
	// WHAT THIS PINS, in the order the defect unfolds:
	//   1. the picker is genuinely armed and ringed before the deactivation;
	//   2. deactivation (the framework's own Deactivated/LostFocus callbacks) drops the claim and
	//      leaves no ring anywhere - the no-ring => no-claim invariant, and the index survives as
	//      the re-assert intent;
	//   3. reactivation puts BOTH back on the card the scope restored focus to, so Enter works;
	//   4. the previously unrecoverable re-press and Tab-shaped refocus recover too;
	//   5. the two-card control keeps working, including the ordinary card-to-card move.
	[Fact]
	public void Reactivation_restores_the_ring_and_the_claim_including_the_one_card_picker()
	{
		PickerReactivationProbe.Result result = PickerReactivationProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.ClaimBeforeDeactivate.Should().BeTrue(
			"the probe must start from a genuinely armed, ringed picker, or the reactivation "
			+ "assertions below could pass because nothing was armed to begin with");
		_ = result.RingBeforeDeactivate.Should().BeTrue("and the armed card must carry the ring");

		_ = result.ClaimAfterDeactivate.Should().BeFalse(
			"losing OS focus clears the focused element, so no card carries a ring and Enter must "
			+ "be a no-op - the invisible claim D-131 forbids");
		_ = result.RingsAfterDeactivate.Should().Be(0, "the deactivation must leave no ringed card");
		_ = result.IndexAfterDeactivate.Should().Be(0,
			"only the proof is dropped, never the intent: the index is what the re-assert reads");

		// THE ONE-CARD CASE, the state that had no recovery path at all.
		_ = result.FocusOnCardAfterReactivate.Should().BeTrue(
			"the framework's own activation restore lands real focus on the card the focus scope "
			+ "remembers - this is why the defect is a RINGLESS focused card, not focus nowhere");
		_ = result.RingAfterReactivate.Should().BeTrue(
			"after reactivation the restored card must carry :focus-visible again, or the focused "
			+ "card is invisible (MEASURED pre-fix ringAfterReactivate=False)");
		_ = result.ClaimAfterReactivate.Should().BeTrue(
			"the claim must be re-armed with the ring, or Enter is dead while a card is focused "
			+ "(MEASURED pre-fix claimAfterReactivate=False)");
		_ = result.TargetIsCardAfterReactivate.Should().BeTrue(
			"Enter must act on the card that is visibly focused after reactivation");

		_ = result.DigitRepressAccepted.Should().BeTrue(
			"re-pressing the digit of the focused card is a valid navigation");
		_ = result.RingAfterDigitRepress.Should().BeTrue(
			"the one-card recovery path: the re-press must leave the ring standing");
		_ = result.ClaimAfterDigitRepress.Should().BeTrue(
			"and the claim live - pre-fix this was the measured dead end (only Esc worked)");

		_ = result.TabRefocusLanded.Should().BeTrue("the refocus must leave real focus on the card");
		_ = result.RingAfterTabRefocus.Should().BeTrue("and the ring on it");
		_ = result.ClaimAfterTabRefocus.Should().BeTrue(
			"a Tab-shaped refocus of the focused card must re-arm Enter rather than being a "
			+ "silent no-op (pre-fix: accepted=True, no GotFocus, claim stayed False)");

		_ = result.TwoCardFocusOnFirst.Should().BeTrue("the two-card control restores focus the same way");
		_ = result.TwoCardRingAfterReactivate.Should().BeTrue("and the ring");
		_ = result.TwoCardClaimAfterReactivate.Should().BeTrue("and the claim");
		_ = result.TwoCardDigit2Ring.Should().BeTrue(
			"the ordinary card-to-card move after reactivation must still work");
		_ = result.TwoCardDigit2Claim.Should().BeTrue("and must arm the claim on the newly focused card");
		_ = result.TwoCardTargetIsSecond.Should().BeTrue("with Enter acting on that card");
	}

	// FINDING F1-b (MEDIUM): THE ADVERTISED ENTER RETRY AFTER AN UNKNOWN LAUNCH MUST BE REAL.
	//
	// The recovery status line promises "Try again (Enter), Copy link (Ctrl+C), or Cancel (Esc)"
	// (Status.LaunchUnknownRecovery), but the click that produced it is a pointer press: it
	// focuses the card with no ring, which correctly disarms the claim (B/N2), and nothing
	// re-armed it. MEASURED pre-fix: claimBefore=True, claimAfterPointerPress=False,
	// ringedAfterPointerPress=False, clickReported=True, enterWouldRetry=False, ringedAfter=0.
	//
	// WHAT THIS PINS: the pre-recovery state really is the dangerous one (so the assertion below
	// is about the fix, not about an already-armed picker), the click really reaches the handler,
	// and showing the Unknown recovery for the clicked slot leaves that card RINGED and Enter
	// ARMED - the two things the advertised retry needs.
	[Fact]
	public void Unknown_launch_recovery_arms_the_advertised_enter_retry_on_the_clicked_card()
	{
		PickerUnknownRetryProbe.Result result = PickerUnknownRetryProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.ClaimLiveBeforeClick.Should().BeTrue(
			"the probe must start armed, or the pre-recovery assertion below could pass because "
			+ "nothing was ever armed");
		_ = result.FocusedCardIsClicked.Should().BeTrue(
			"a pointer press focuses the clicked card, which is the state the launch starts from");

		_ = result.ClaimAfterPointerPress.Should().BeFalse(
			"the pointer press focuses the card WITHOUT a ring, so the claim must be dropped "
			+ "(B/N2) - this is the state the recovery has to rescue");
		_ = result.RingsAfterPointerPress.Should().Be(0,
			"and no card may be ringed after a pointer press - the framework fact the finding rests on");
		_ = result.ClickReported.Should().BeTrue("the click must still reach the session's card handler");

		_ = result.StatusShown.Should().BeTrue("the Unknown recovery status must be shown");
		_ = result.FocusedCardIsStillClicked.Should().BeTrue(
			"the recovery must not move focus off the card the launch was for");
		_ = result.RingOnClickedCardAfterRecovery.Should().BeTrue(
			"the advertised Enter retry needs a VISIBLY focused card: the recovery must put the ring "
			+ "back on the clicked card (MEASURED pre-fix ringedAfter=0)");
		_ = result.RingsAfterRecovery.Should().Be(1, "exactly one card may be ringed");
		_ = result.ClaimAfterRecovery.Should().BeTrue(
			"and the claim must be live, or Enter is a lie (MEASURED pre-fix enterWouldRetry=False)");
		_ = result.TargetIsClickedCard.Should().BeTrue(
			"Enter must retry the card the recovery is about");
	}

	// FINDING F-A1 (HIGH): THE SESSION-SIDE HALF OF THE UNKNOWN-RECOVERY FIX.
	//
	// The probe above pins the WINDOW half of the fix but calls the view model directly, so it cannot
	// see which slot PickerSession hands over. This test drives the real session: a click on card 2,
	// an Unknown outcome from the launch helper, and the real window bound to the same view model. A
	// wrong slot argument (the off-by-one mutation `card.Slot - 1` -> `card.Slot`) is rejected by the
	// window as out of range and its fallback lands on the stale index (card 1), which is exactly what
	// the assertions below refuse. MEASURED with that mutation: RecordedSlot=2, IndexAfterRecovery=0,
	// RingOnClickedCard=False, RingOnFirstCard=True, TargetIsClickedCard=False.
	[Fact]
	public void Session_Unknown_launch_reasserts_the_clicked_card_not_the_stale_index()
	{
		PickerSessionUnknownRetryProbe.Result result = PickerSessionUnknownRetryProbe.Run();

		_ = result.Failure.Should().BeNull();

		// The PREMISE the finding rests on, measured rather than assumed: the session focuses card 1 at
		// first frame, the pointer press moves focus to card 2, and the index deliberately still names
		// card 1 - so an implementation that trusted the index could not land on the clicked card.
		_ = result.IndexBeforeClick.Should().Be(0, "the session's first frame focuses card 1");
		_ = result.ClickedCardFocused.Should().BeTrue("the pointer press must focus the clicked card");
		_ = result.IndexAfterPointer.Should().Be(0,
			"THE HAZARD: the pointer path keeps FocusedIndex as the intent and drops only the proof, so "
			+ "after the pointer focuses card 2 the index still names card 1");

		// The session really took the Unknown path for the click, and it handed the window the CLICKED
		// card's slot - the argument under test.
		_ = result.LaunchCalls.Should().Be(1, "the click must have started exactly one launch");
		_ = result.StatusShown.Should().BeTrue("the Unknown outcome must have shown the recovery status");
		_ = result.RecordedSlot.Should().Be(1,
			"the session must name the CLICKED card's zero-based index (card 2 -> 1); the raw slot would "
			+ "be 2, which the window rejects as out of range");

		// And the window's re-assert landed there: real focus, the ring, the VM index, Enter's target.
		_ = result.IndexAfterRecovery.Should().Be(1,
			"the recovery must move the index to the clicked card; MEASURED with the slotless mutation "
			+ "it stayed at 0, i.e. the retry would have relaunched card 1");
		_ = result.RingOnClickedCard.Should().BeTrue(
			"the advertised Enter retry needs a VISIBLY focused card, so the clicked card must be ringed");
		_ = result.RingOnFirstCard.Should().BeFalse(
			"and the stale card must not be left ringed - exactly one card may carry the focus ring");
		_ = result.TargetIsClickedCard.Should().BeTrue("Enter must retry the card the user clicked");
		_ = result.ClaimArmed.Should().BeTrue("and the claim must be live, or the advertised retry is a lie");
	}

	// FINDING F-A2 (DOCUMENTATION): THE ALREADY-FOCUSED BRANCH OF PickerWindow.FocusCard IS SHARED.
	//
	// REVERTING THAT ONE BRANCH FAILS THE THREE TESTS BELOW - all three share it:
	//
	//   1. Reactivation_restores_the_ring_and_the_claim_including_the_one_card_picker
	//      (PickerReactivationProbe: the OS-reactivation re-assert and the Tab-shaped refocus);
	//   2. Unknown_launch_recovery_arms_the_advertised_enter_retry_on_the_clicked_card
	//      (PickerUnknownRetryProbe: the recovery re-assert of the pointer-focused card);
	//   3. Digit_repress_of_a_pointer_focused_card_leaves_it_ringed_and_enter_armed
	//      (PickerStaleIndexRepressProbe: the digit re-press of a pointer-focused card).
	//
	// All three land on FocusCard with real focus ALREADY on the target, where Focus() is a framework
	// no-op (no GotFocus), so the branch that applies the ring and calls ReassertFocusFromControl by
	// hand is the only thing arming the claim.
	//
	// MEASURED (sandbox): forcing that branch false fails exactly those three PLUS the session-side
	// test added below (Session_Unknown_launch_reasserts_the_clicked_card_not_the_stale_index), which
	// reaches the same branch through the Unknown-recovery re-assert - 4 failures, not 3. The count
	// grew with the new test rather than contradicting the finding: the three named tests remain the
	// ones that pinned the branch before it existed.
	//
	// THE STALE-INDEX HOLE (HIGH): POINTER FOCUS FOLLOWED BY THE DIGIT OF THE SAME CARD.
	//
	// The already-focused guard in FocusSlot keys on FocusedIndex, but the pointer path disarms
	// the proof WITHOUT updating the index - so after the pointer focuses card 2 the VM still
	// names card 1, pressing "2" takes the ordinary path, and Focus() on the already-focused card
	// is a framework no-op: no GotFocus, no re-arm. MEASURED pre-fix: card2StillFocused=True,
	// ringedAfterRepress=0, vmIndex=1, claimAfterRepress=False.
	//
	// WHAT THIS PINS: the premise (the index really is stale and the claim really is dead after
	// the pointer press), and then the fix - the digit must leave card 2 ringed and Enter armed,
	// with the index naming card 2.
	[Fact]
	public void Digit_repress_of_a_pointer_focused_card_leaves_it_ringed_and_enter_armed()
	{
		PickerStaleIndexRepressProbe.Result result = PickerStaleIndexRepressProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.ClaimLiveBeforePointer.Should().BeTrue(
			"the probe must start armed, or the assertions below could pass for the wrong reason");

		_ = result.ClaimAfterPointer.Should().BeFalse(
			"the ringless pointer focus must disarm the claim (B/N2)");
		_ = result.RingsAfterPointer.Should().Be(0, "and leave no ring");
		_ = result.Card2FocusedAfterPointer.Should().BeTrue("while real focus is on card 2");
		_ = result.IndexAfterPointer.Should().Be(0,
			"THE PREMISE: the index still names card 1 - the pointer path keeps the intent and drops "
			+ "only the proof, which is what makes the digit take the ordinary path");

		_ = result.RepressAccepted.Should().BeTrue("pressing 2 is a valid navigation");
		_ = result.IndexAfterRepress.Should().Be(1, "the VM must now name card 2");
		_ = result.Card2StillFocused.Should().BeTrue("focus must stay on the card the digit named");
		_ = result.RingOnCard2AfterRepress.Should().BeTrue(
			"the digit must make card 2 VISIBLY focused; pre-fix Focus() was a no-op and the card "
			+ "stayed ringless (MEASURED ringedAfterRepress=0)");
		_ = result.RingsAfterRepress.Should().Be(1, "exactly one card may be ringed");
		_ = result.ClaimAfterRepress.Should().BeTrue(
			"and Enter must be armed on it (MEASURED pre-fix claimAfterRepress=False)");
		_ = result.TargetIsCard2.Should().BeTrue("Enter must act on card 2");
	}

	// THE NO-RING => NO-CLAIM INVARIANT (D-131), re-asserted across the disarm routes and the
	// arming direction the F1 fixes must not break. Every fix adds a place where focus and the
	// claim are re-established, and each could over-arm; this probe requires zero rings and a dead
	// claim after focus is cleared outright and after a ringless pointer focus, while the positive
	// control requires the ring and the claim to come back together through a real Tab-shaped move.
	[Fact]
	public void No_ring_means_no_claim_across_every_disarm_route()
	{
		PickerNoRingNoClaimProbe.Result result = PickerNoRingNoClaimProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.ClaimLiveBeforeDisarm.Should().BeTrue(
			"the probe must start armed, or the disarm assertions below could pass vacuously");

		_ = result.ClaimAfterClearFocus.Should().BeFalse(
			"with focus cleared there is no ring on any card, so Enter must be a no-op");
		_ = result.RingsAfterClearFocus.Should().Be(0, "and no card may be ringed");
		_ = result.IndexAfterClearFocus.Should().Be(0,
			"the disarm clears only the proof: the index is the re-assert intent");

		_ = result.ClaimAfterRinglessFocus.Should().BeFalse(
			"a pointer-focused card carries no ring, so the claim must stay dead even though real "
			+ "focus is on a card");
		_ = result.RingsAfterRinglessFocus.Should().Be(0, "and no card may be ringed");

		_ = result.RingAfterTabRefocus.Should().BeTrue(
			"the positive control: a real Tab-shaped focus move must restore the ring");
		_ = result.RingsAfterTabRefocus.Should().Be(1, "exactly one ringed card");
		_ = result.ClaimAfterTabRefocus.Should().BeTrue(
			"and the claim with it - so 'always dead' cannot pass this probe either");
	}

	// W5.2 DISMISSAL CONTRACT (spec 3.3): "Click outside, Alt+Tab/focus loss, Win+D/desktop
	// switch, sleep, and lock dismiss the Picker."
	//
	// THE WINDOW-LEVEL HALF of the dismissal contract. It lives in THIS class, not in
	// PickerDismissalContractTests, because it constructs a real Window and drives the shared,
	// process-wide Avalonia Application - and that dispatcher is THREAD-AFFINE: a probe driven from
	// another test class running in parallel fails with "Call from invalid thread" (MEASURED),
	// exactly the hazard PickerFocusProbe's header documents. Keeping every real-window probe under
	// this one class is what makes the run deterministic.
	[Fact]
	public void Window_deactivation_invokes_the_dismiss_callback()
	{
		PickerDismissalProbe.Result result = PickerDismissalProbe.Run();

		_ = result.Failure.Should().BeNull();

		_ = result.DismissCallsBeforeDeactivate.Should().Be(0,
			"the picker must not dismiss itself merely by being constructed and shown off-screen");
		_ = result.DismissCallsAfterDeactivate.Should().Be(1,
			"the platform raises Deactivated for click-outside / Alt+Tab / Win+D / lock, and the window "
			+ "must translate it into exactly one dismissal callback (the same real Deactivated event "
			+ "PickerPlatformCallbacks.Deactivate drives)");
		_ = result.DismissCallsAfterSecondDeactivate.Should().Be(2,
			"the handler is a plain event handler; re-entrancy/one-shot policy belongs to the session, "
			+ "which is already terminal-guarded");
	}

	// Backward compatibility: every existing probe constructs the window with three arguments
	// (onKey, onCardClicked, and an optional onFirstRunAction). A window built WITHOUT a dismissal
	// callback must survive a deactivation without throwing, or the whole off-screen probe suite
	// above would break the moment the handler was added.
	[Fact]
	public void Window_deactivation_without_a_dismiss_callback_is_safe()
	{
		PickerDismissalProbe.Result result = PickerDismissalProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.SafeWithoutCallback.Should().BeTrue(
			"a window constructed without a dismissal callback must not dereference null when it "
			+ "deactivates - the existing three-argument construction is used by every off-screen probe");
	}

	// W7.2: THE REASON MUST ACTUALLY BE ON SCREEN. The view-model test proves the card
	// CARRIES the reason; this proves the WINDOW renders it, which is the difference between
	// "the ranking exists" and "the user can see why a browser is suggested". It is a real
	// WINDOW assertion rather than a source-text one for the same reason the XAML probe is:
	// a TextBlock with a broken binding compiles and passes every textual check.
	[Fact]
	public void Ranked_card_does_not_draw_the_reason_on_the_tile()
	{
		// Tile layout (owner decision): a tile shows the icon and the name only. The ranking reason
		// is still computed (PickerRankingProjectionTests pin it) but is no longer drawn.
		PickerReasonRenderProbe.Result result = PickerReasonRenderProbe.Run();

		_ = result.Failure.Should().BeNull();
		_ = result.ReasonTexts.Should().HaveCount(2);
		_ = result.ReasonTexts.Should().OnlyContain(text => string.IsNullOrEmpty(text),
			"no tile may carry a reason line");
	}
}

// THE RUNTIME-TOKEN PRECEDENCE PROBE (NEW-1). It shares PickerXamlProbe's Avalonia bootstrap for
// the same reason PickerFocusProbe does: one Application per process, and its dispatcher is
// thread-affine (MEASURED: a second bootstrap from another class fails with "Call from invalid
// thread" instead of starting a second UI thread).
//
// HOW IT OBSERVES PRECEDENCE. TryGetResource is NOT a valid instrument here: it does not fall
// through to Application.Resources at all (MEASURED: a key present only at application level
// returns false from a window), so it cannot tell "shadowed" from "absent". The instrument is a
// real DynamicResource binding on a control inside the window's own tree - the same lookup a
// production consumer performs - read back through a typed property.
//
// The application's own writes are made and then RESTORED in a finally: Application.Resources is
// process-wide state shared with every other test in this assembly.
internal static class PickerTokenProbe
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
		Application application = Application.Current
			?? throw new InvalidOperationException("No Application is configured; the probe must run after PickerXamlProbe.");

		PickerWindow window = new(NewViewModel(), _ => { }, _ => { });

		// The instrument: three bindings for the three value TYPES the application writes
		// (double, double and boolean), so a shadow of any of them is observable.
		//
		// Each property field is qualified by the type that DECLARES it, not by TextBlock:
		// OpacityProperty and IsVisibleProperty are Visual's, TagProperty is Control's, while
		// FontSizeProperty really is TextBlock's. Accessing a static through a derived type is
		// IDE0002 ("simplify member access"); the declaring type is what the analyzer wants and
		// what the compiler binds to anyway.
		TextBlock instrument = new();
		_ = instrument.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("bc.motion.fast"));
		_ = instrument.Bind(Visual.OpacityProperty, new DynamicResourceExtension("bc.motion.slow"));
		_ = instrument.Bind(Control.TagProperty, new DynamicResourceExtension("bc.os.reducedMotion"));

		// A second control for the boolean transitions flag, read through IsVisible so the value
		// is typed rather than boxed.
		TextBlock transitionsInstrument = new();
		_ = transitionsInstrument.Bind(Visual.IsVisibleProperty, new DynamicResourceExtension("bc.motion.transitions.enabled"));

		Dictionary<string, object?> previous = new(StringComparer.Ordinal);
		foreach (string key in RuntimeOwnedKeys)
		{
			previous[key] = application.Resources.ContainsKey(key) ? application.Resources[key] : null;
		}

		try
		{
			// The application's runtime write, exactly as PickerApp.ApplyPlatformAccessibility
			// performs it for a user who has reduced motion switched on.
			application.Resources["bc.motion.fast"] = 0d;
			application.Resources["bc.motion.slow"] = 0d;
			application.Resources["bc.os.reducedMotion"] = true;
			application.Resources["bc.motion.transitions.enabled"] = false;

			// The instrument has to be inside the window's own tree, or it would resolve against
			// a different resource scope and the precedence question would not be asked.
			StackPanel host = (StackPanel)((Border)window.Content!).Child!;
			host.Children.Add(instrument);
			host.Children.Add(transitionsInstrument);

			Dispatcher.UIThread.RunJobs();

			bool windowDeclares = window.TryGetResource("bc.motion.fast", null, out _);

			return new Result(
				Failure: null,
				ResolvedMotionFast: instrument.FontSize,
				ResolvedReducedMotion: instrument.Tag is true,
				ResolvedTransitionsEnabled: transitionsInstrument.IsVisible,
				WindowDeclaresMotionFast: windowDeclares);
		}
		finally
		{
			foreach (KeyValuePair<string, object?> entry in previous)
			{
				if (entry.Value is null)
				{
					_ = application.Resources.Remove(entry.Key);
				}
				else
				{
					application.Resources[entry.Key] = entry.Value;
				}
			}

			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static readonly string[] RuntimeOwnedKeys =
	[
		"bc.motion.fast", "bc.motion.slow", "bc.os.reducedMotion", "bc.motion.transitions.enabled",
	];

	private static PickerViewModel NewViewModel()
	{
		BrowserCandidate[] candidates =
		[
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path"),
			new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path"),
		];

		return new PickerViewModel("https://example.com/", new BrowserSnapshot(candidates, 1, DateTimeOffset.UtcNow));
	}

	internal sealed record Result(
		string? Failure,
		double ResolvedMotionFast,
		bool ResolvedReducedMotion,
		bool ResolvedTransitionsEnabled,
		bool WindowDeclaresMotionFast)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, -1, false, true, true);
		}
	}
}

// THE POINTER-FOCUS PROBE (finding B). It drives the REAL window on an off-screen shown window,
// for the reason every focus probe in this file does: Focus() returns false before Show because no
// focus manager is attached to the tree yet, so the production focus path cannot be exercised on a
// hidden window at all.
//
// THE POINTER IS MODELLED BY NavigationMethod, WHICH IS WHAT ACTUALLY DECIDES THE RING. A real
// mouse press reaches Focus(NavigationMethod.Pointer) through FocusManager.OnPreviewPointerPressed
// (Avalonia 11.3.18, verified in source), and InputElement.OnGotFocus sets :focus-visible only for
// Tab and Directional. Calling Focus(NavigationMethod.Pointer) therefore reproduces exactly the
// framework state a mouse press produces - focus landed, no ring - without needing a synthetic
// pointer event or a hit-testable off-screen surface.
internal static class PickerPointerFocusProbe
{
	private const string FocusVisiblePseudoClass = ":focus-visible";

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
		AdvanceableTimeProvider time = new(DateTimeOffset.UnixEpoch);
		PickerViewModel vm = new("https://example.com/", Snapshot(2), time);
		PickerWindow window = new(vm, _ => { }, _ => { });

		try
		{
			window.ShowActivated = false;
			window.Topmost = false;
			window.Opacity = 0;
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			// HALF 1 - COLD START. No keyboard focus has ever landed, so no claim exists yet; the
			// pointer press must not create one.
			bool pointerFocused = CardButton(window, 1).Focus(NavigationMethod.Pointer, KeyModifiers.None);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimAfterColdPointerFocus = vm.CanActivateFocusedCard();
			int ringedAfterPointerFocus = CountRingedCards(window);

			// HALF 2 - THE STALE-CLAIM PATH. The keyboard arms the claim on card 1 (the production
			// startup path), the 100 ms window elapses so the claim is genuinely live, and only THEN
			// does the pointer move focus to card 2. This is the state the audit measured, and the
			// state a gate-only fix would leave armed.
			_ = vm.FocusSlot(1);
			window.FocusCard(vm.FocusedIndex);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimLiveFromKeyboardBeforePointer = vm.CanActivateFocusedCard();

			_ = CardButton(window, 1).Focus(NavigationMethod.Pointer, KeyModifiers.None);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimAfterPointerMovedFocusOffTheArmedCard = vm.CanActivateFocusedCard();

			return new Result(
				Failure: null,
				PointerFocusLanded: pointerFocused,
				ClaimAfterColdPointerFocus: claimAfterColdPointerFocus,
				RingedCardsAfterPointerFocus: ringedAfterPointerFocus,
				ClaimLiveFromKeyboardBeforePointer: claimLiveFromKeyboardBeforePointer,
				ClaimAfterPointerMovedFocusOffTheArmedCard: claimAfterPointerMovedFocusOffTheArmedCard);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static int CountRingedCards(PickerWindow window)
	{
		int ringed = 0;
		for (int index = 0; index < 2; index++)
		{
			if (CardButton(window, index).Classes.Contains(FocusVisiblePseudoClass))
			{
				ringed++;
			}
		}

		return ringed;
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the measurement no longer builds the containers.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private static BrowserSnapshot Snapshot(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(index =>
			new BrowserCandidate(
				$"browser-{index}",
				$"Browser {index}",
				$@"C:\Browsers\b{index}.exe",
				"stable",
				"known-path"))];

		return new BrowserSnapshot(candidates, candidateCount, DateTimeOffset.UtcNow);
	}

	private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	internal sealed record Result(
		string? Failure,
		bool PointerFocusLanded,
		bool ClaimAfterColdPointerFocus,
		int RingedCardsAfterPointerFocus,
		bool ClaimLiveFromKeyboardBeforePointer,
		bool ClaimAfterPointerMovedFocusOffTheArmedCard)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, false, true, -1, false, true);
		}
	}
}

// THE FOCUS-LEFT PROBE (finding N1). Focus is cleared through the real FocusManager, which is the
// same call Avalonia makes when the window itself is deactivated or focus is removed from the tree
// (TopLevel.PlatformImpl_LostFocus -> SetFocusedElement(null)) - so this drives the production
// sequence, not a synthetic one.
//
// THE OVER-CORRECTION IS MEASURED IN THE SAME RUN: after the clear, focus is moved back onto a card
// through FocusCard and the claim must be live again. A handler that disarmed too eagerly (or that
// disarmed on every LostFocus, including card-to-card moves) fails that half.
internal static class PickerFocusLeftProbe
{
	private const string FocusVisiblePseudoClass = ":focus-visible";

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
		AdvanceableTimeProvider time = new(DateTimeOffset.UnixEpoch);
		PickerViewModel vm = new("https://example.com/", Snapshot(2), time);
		PickerWindow window = new(vm, _ => { }, _ => { });

		try
		{
			window.ShowActivated = false;
			window.Topmost = false;
			window.Opacity = 0;
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			// 1. Arm through the production keyboard path and let the 100 ms window elapse.
			_ = vm.FocusSlot(1);
			window.FocusCard(vm.FocusedIndex);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimLiveBeforeFocusLeft = vm.CanActivateFocusedCard();

			// 2. THE DEFECT: focus leaves the card list entirely. ClearFocus is the framework's own
			//    way of doing that, and it raises LostFocus with the focused element already null.
			window.FocusManager!.ClearFocus();
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimAfterFocusLeft = vm.CanActivateFocusedCard();
			int focusedIndexAfterFocusLeft = vm.FocusedIndex;
			int ringedAfterFocusLeft = CountRingedCards(window);

			// 3. THE OVER-CORRECTION HALF, part one: focus moving BETWEEN cards must not disarm.
			_ = vm.FocusSlot(1);
			window.FocusCard(vm.FocusedIndex);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			window.FocusCard(1);
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			bool claimAfterFocusMovedBetweenCards = vm.CanActivateFocusedCard();

			return new Result(
				Failure: null,
				ClaimLiveBeforeFocusLeft: claimLiveBeforeFocusLeft,
				ClaimAfterFocusLeft: claimAfterFocusLeft,
				FocusedIndexAfterFocusLeft: focusedIndexAfterFocusLeft,
				RingedCardsAfterFocusLeft: ringedAfterFocusLeft,
				ClaimAfterFocusMovedBetweenCards: claimAfterFocusMovedBetweenCards);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static int CountRingedCards(PickerWindow window)
	{
		int ringed = 0;
		for (int index = 0; index < 2; index++)
		{
			if (CardButton(window, index).Classes.Contains(FocusVisiblePseudoClass))
			{
				ringed++;
			}
		}

		return ringed;
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the measurement no longer builds the containers.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private static BrowserSnapshot Snapshot(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(index =>
			new BrowserCandidate(
				$"browser-{index}",
				$"Browser {index}",
				$@"C:\Browsers\b{index}.exe",
				"stable",
				"known-path"))];

		return new BrowserSnapshot(candidates, candidateCount, DateTimeOffset.UtcNow);
	}

	private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	internal sealed record Result(
		string? Failure,
		bool ClaimLiveBeforeFocusLeft,
		bool ClaimAfterFocusLeft,
		int FocusedIndexAfterFocusLeft,
		int RingedCardsAfterFocusLeft,
		bool ClaimAfterFocusMovedBetweenCards)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, false, true, -1, -1, false);
		}
	}
}

// THE CLICK PROBE (finding N2). It clicks through the window's OWN Click handler by raising the
// real Click routed event on the real card Button - the same event the XAML wires to
// OnChoiceCardClick - so the probe exercises the production click path rather than calling the VM.
//
// NO FOCUS IS INVOLVED. Nothing is focused when the click arrives, so the probe isolates the click
// path: on the unfixed code the click alone arms the claim, which is what makes the assertion
// attributable to this finding and not to the focus path (B) or the focus-left path (N1).
internal static class PickerClickProbe
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
		AdvanceableTimeProvider time = new(DateTimeOffset.UnixEpoch);
		PickerViewModel vm = new("https://example.com/", Snapshot(2), time);
		int clickedCards = 0;
		PickerWindow window = new(vm, _ => { }, _ => clickedCards++);

		try
		{
			window.ShowActivated = false;
			window.Topmost = false;
			window.Opacity = 0;
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			// The claim is armed by the click path itself if it is armed at all: no focus has
			// landed, so nothing else in the window can have armed it.
			bool focusedElementBeforeClick = window.FocusManager?.GetFocusedElement() is not null;

			CardButton(window, 1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Dispatcher.UIThread.RunJobs();
			Dispatcher.UIThread.RunJobs();
			time.Advance(TimeSpan.FromMilliseconds(100));

			return new Result(
				Failure: null,
				FocusedElementBeforeClick: focusedElementBeforeClick,
				ClaimAfterClick: vm.CanActivateFocusedCard(),
				ClickedCardWasReported: clickedCards == 1);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static Button CardButton(PickerWindow window, int index)
	{
		ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
			?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

		Control container = cards.ContainerFromIndex(index)
			?? throw new InvalidOperationException($"Card {index} was not realized; the measurement no longer builds the containers.");

		return container.GetVisualDescendants().OfType<Button>().FirstOrDefault()
			?? throw new InvalidOperationException($"Card {index} contains no Button.");
	}

	private static BrowserSnapshot Snapshot(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(index =>
			new BrowserCandidate(
				$"browser-{index}",
				$"Browser {index}",
				$@"C:\Browsers\b{index}.exe",
				"stable",
				"known-path"))];

		return new BrowserSnapshot(candidates, candidateCount, DateTimeOffset.UtcNow);
	}

	private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	internal sealed record Result(
		string? Failure,
		bool FocusedElementBeforeClick,
		bool ClaimAfterClick,
		bool ClickedCardWasReported)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, false, true, false);
		}
	}
}

// W5.2 DISMISSAL CONTRACT (spec 3.3: "Click outside, Alt+Tab/focus loss, Win+D/desktop switch,
// sleep, and lock dismiss the Picker").
//
// WHY A REAL WINDOW, AND WHY THE PLATFORM CALLBACKS. The dismissal hangs off the window's
// Deactivated event, which is exactly what the platform raises when the window loses activation
// by pointer (click-outside on another window/app), by Alt+Tab, by Win+D / show-desktop, and in
// practice by a lock request. The event is raised by WindowBase.HandleDeactivated, which the
// platform delegate IWindowBaseImpl.Deactivated calls; that delegate is not client-accessible,
// so the probe invokes the SAME private handler through PickerPlatformCallbacks.Deactivate -
// the same seam PickerReactivationProbe uses for the reactivation half. Only the delegate
// wrapper is bypassed; the handler that runs is the production code.
internal static class PickerDismissalProbe
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
		int dismissCalls = 0;
		PickerViewModel vm = new("https://example.com/", Snapshot(2));
		PickerWindow window = new(vm, _ => { }, _ => { }, onFirstRunAction: null, onDismiss: () => dismissCalls++);

		try
		{
			ShowOffScreen(window);

			int beforeDeactivate = dismissCalls;

			// 1. THE OS DEACTIVATION. This raises the window's real Deactivated event through the
			//    handler WindowBase wires to the platform delegate.
			PickerPlatformCallbacks.Deactivate(window);
			Dispatcher.UIThread.RunJobs();

			int afterDeactivate = dismissCalls;

			// 2. A SECOND deactivation. The window handler is deliberately stateless (a plain event
			//    handler); the one-shot/terminal policy belongs to PickerSession, which ignores a
			//    dismissal after it has completed. The probe pins that the window itself reports every
			//    Deactivated rather than dropping later ones, so the session gets to decide.
			PickerPlatformCallbacks.Deactivate(window);
			Dispatcher.UIThread.RunJobs();

			int afterSecondDeactivate = dismissCalls;

			// 3. THE BACKWARD-COMPATIBILITY HALF: a window with NO dismissal callback (every existing
			//    probe constructs it with three arguments) must survive a deactivation. A throw here
			//    would break the whole off-screen probe suite.
			bool safeWithoutCallback = SafeWithoutCallback();

			return new Result(
				Failure: null,
				DismissCallsBeforeDeactivate: beforeDeactivate,
				DismissCallsAfterDeactivate: afterDeactivate,
				DismissCallsAfterSecondDeactivate: afterSecondDeactivate,
				SafeWithoutCallback: safeWithoutCallback);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static bool SafeWithoutCallback()
	{
		PickerWindow window = new(NewViewModel(), _ => { }, _ => { });

		try
		{
			ShowOffScreen(window);
			PickerPlatformCallbacks.Deactivate(window);
			Dispatcher.UIThread.RunJobs();
			return true;
		}
		catch (InvalidOperationException)
		{
			// The framework's BaseDeactivated. NullReferenceException from a missing callback would
			// surface as a generic Exception, which is NOT caught here on purpose: the probe must
			// fail loudly if the window dereferences a null dismissal callback.
			throw;
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static void ShowOffScreen(PickerWindow window)
	{
		window.ShowActivated = false;
		window.Topmost = false;
		window.Opacity = 0;
		window.Position = new PixelPoint(-32000, -32000);
		window.Show();
		Dispatcher.UIThread.RunJobs();
	}

	private static PickerViewModel NewViewModel()
	{
		return new PickerViewModel("https://example.com/", Snapshot(2));
	}

	private static BrowserSnapshot Snapshot(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(index =>
			new BrowserCandidate(
				$"browser-{index}",
				$"Browser {index}",
				$@"C:\Browsers\b{index}.exe",
				"stable",
				"known-path"))];

		return new BrowserSnapshot(candidates, candidateCount, DateTimeOffset.UtcNow);
	}

	internal sealed record Result(
		string? Failure,
		int DismissCallsBeforeDeactivate,
		int DismissCallsAfterDeactivate,
		int DismissCallsAfterSecondDeactivate,
		bool SafeWithoutCallback)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, -1, -1, -1, false);
		}
	}
}

// THE REASON-RENDER PROBE (W7.2). It shows the REAL window off-screen with a Ranked VM
// whose rules suggest chrome-stable, then reads every TextBlock in the realized card
// containers. The point is the BINDING, not the string: a card whose ReasonLabel is
// correct but whose template has no consumer would pass every view-model test and still
// show the user nothing.
internal static class PickerReasonRenderProbe
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
		RoutingDecision decision = new(
			3,
			new RoutingSuggestion(
				"work-domains",
				new RoutingTargetRef("chrome-stable", null),
				"Matched rule 'work-domains' by orderIndex 1."));

		PickerRankingInputs ranking = new(
			decision,
			SourceConfidence.Unknown,
			ProbablePreferredBrowserId: null,
			RecentSessionBrowserIds: null);

		BrowserCandidate[] candidates =
		[
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path"),
			new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path"),
		];

		PickerViewModel vm = new(
			"https://docs.example.com/download",
			new BrowserSnapshot(candidates, 2, DateTimeOffset.UtcNow),
			timeProvider: null,
			ranking);

		PickerWindow window = new(vm, _ => { }, _ => { });

		try
		{
			window.ShowActivated = false;
			window.Topmost = false;
			window.Opacity = 0;
			window.Position = new PixelPoint(-32000, -32000);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			ItemsControl cards = window.FindControl<ItemsControl>("CardsItems")
				?? throw new InvalidOperationException("CardsItems was not found; the window's XAML changed shape.");

			List<string> reasonTexts = [];
			for (int index = 0; index < vm.Cards.Count; index++)
			{
				Control container = cards.ContainerFromIndex(index)
					?? throw new InvalidOperationException($"Card {index} was not realized.");

				// The reason is the LAST TextBlock in the card's StackPanel, and it is
				// identified by matching the card's own ReasonLabel - so the probe cannot
				// pass by accidentally reading the browser-id line.
				string expected = vm.Cards[index].ReasonLabel;
				TextBlock? reason = container.GetVisualDescendants()
					.OfType<TextBlock>()
					.FirstOrDefault(block => block.Text == expected);

				reasonTexts.Add(reason?.Text ?? string.Empty);
			}

			return new Result(Failure: null, ReasonTexts: reasonTexts);
		}
		finally
		{
			window.Close();
			Dispatcher.UIThread.RunJobs();
		}
	}

	internal sealed record Result(
		string? Failure,
		IReadOnlyList<string> ReasonTexts)
	{
		internal static Result Failed(string failure)
		{
			return new Result(failure, []);
		}
	}
}
