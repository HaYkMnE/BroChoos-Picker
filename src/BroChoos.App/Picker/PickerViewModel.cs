using System.ComponentModel;
using BroChoos.App.Localization;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Routing;
using BroChoos.Core.Snapshots;
using BroChoos.Core.Storage;

namespace BroChoos.App.Picker;

internal enum PickerStatusKind
{
	None,
	Info,
	Error,
}

internal enum FirstRunStatusKind
{
	None,
	Prompt,
}

// The config-recovery notice's "Repair settings" action: press once to arm, press again within
// the confirm window to run, and it refuses further presses while the child runs.
internal enum ConfigRepairState
{
	Idle,
	Armed,
	Running,
}

// UI-thread-owned view state for the picker window. Pure CLR: no Avalonia types,
// so unit tests drive it without a UI thread or a real window. D-131 lives here:
// activation is possible only while a card is visibly focused (FocusedIndex >= 0,
// kept in sync from real focus events and key navigation) - never on an invisible
// default.
//
// W7.2: the view model is where ranking becomes VISIBLE. Cards are built from
// RankingEngine.Rank rather than from the snapshot's raw order, and every card carries a
// localized reason. Ranking only reorders; it grants no activation authority, so the
// explicit-choice and no-auto-open invariants are untouched — Enter still acts only on the
// visibly focused card.
internal sealed class PickerViewModel(
	string url,
	BrowserSnapshot snapshot,
	TimeProvider? timeProvider = null,
	PickerRankingInputs? ranking = null) : INotifyPropertyChanged
{
	// D-130: the keyboard contract is digits 1-9, so a run never shows more than
	// nine choice cards (deterministic cap; candidates arrive in detector order).
	private const int MaxChoiceSlots = 9;

	private PickerStatusKind _statusKind;
	private FirstRunStatusKind _firstRunStatusKind;
	private TimeProvider TimeProvider { get; } = timeProvider ?? TimeProvider.System;
	private DateTimeOffset _focusRenderedAt = DateTimeOffset.MinValue;

	public event PropertyChangedEventHandler? PropertyChanged;

	// FINDING F1-b: raised by ShowLaunchUnknownRecovery with the slot the recovery is about
	// (-1 when the caller does not know it). The window owns focus, so it is the only place that
	// can turn "the UI promises an Enter retry" into a visibly focused, armed card. Kept as a
	// plain CLR signal: this view model stays Avalonia-free (unit tests drive it without a UI).
	internal event Action<int>? LaunchUnknownRecoveryShown;

	public string Url { get; private set; } = url;

	// Settings > Appearance > Picker layout, read once at picker start. Set before the window is
	// built (the window chooses its tile panel and width from it in its constructor).
	public bool IsVerticalLayout { get; set; }

	public BrowserSnapshot Snapshot { get; private set; } = snapshot;

	// The ranking inputs for THIS run. Replaceable through ApplySnapshot so a first-run
	// discovery that changes the candidate list can be re-ranked against the same rules
	// rather than silently falling back to detector order.
	private PickerRankingInputs Ranking { get; set; } = ranking ?? PickerRankingInputs.None;

	public IReadOnlyList<ChoiceCardViewModel> Cards { get; private set; } = BuildCards(snapshot.Candidates, ranking ?? PickerRankingInputs.None);

	public LinkCardDisplay Link { get; private set; } = UrlDisplayFormatter.Format(url);

	// 0-based index of the visibly focused card; -1 when no card is focused.
	public int FocusedIndex { get; private set; } = -1;

	public string StatusText { get; private set; } = string.Empty;
	public string StatusMessage => StatusText;

	public string FirstRunPromptText { get; private set; } = string.Empty;

	public bool IsFirstRunPromptVisible => _firstRunStatusKind == FirstRunStatusKind.Prompt;

	public bool IsInfoStatusVisible => _statusKind == PickerStatusKind.Info;

	public bool IsErrorStatusVisible => _statusKind == PickerStatusKind.Error;

	public bool IsActionButtonsVisible => IsFirstRunPromptVisible;

	// CONFIG RECOVERY NOTICE. A saved settings file (history, rules) was found broken or in a
	// schema this build cannot read, so defaults are in use for this run. It is its OWN row,
	// not the status line: the status line is replaced by every launch failure, copy or hint,
	// and the notice must neither hide a launch error nor be wiped by one. Quiet by design
	// (secondary text, no error colour, no focus steal); it stays for the whole session.
	public bool IsConfigNoticeVisible { get; private set; }

	// The visible sentence, set before the window is measured so the row is in the pre-show height.
	public string ConfigNoticeText { get; private set; } = string.Empty;

	// The row's accessible NAME. Empty until the window has opened, then set once to the notice
	// sentence: the row is a polite live region, and a live region announces a TEXT change, not
	// a visibility flip - so this one change is what makes a screen reader read it once.
	public string ConfigNoticeAnnouncement { get; private set; } = string.Empty;

	public ConfigRepairState RepairState { get; private set; }

	public bool IsRepairRunning => RepairState == ConfigRepairState.Running;

	public string RepairButtonText => LocalizationService.GetString(RepairState switch
	{
		ConfigRepairState.Armed => "ConfigNotice.RepairConfirm",
		ConfigRepairState.Running => "ConfigNotice.Repairing",
		ConfigRepairState.Idle => "ConfigNotice.Repair",
		_ => "ConfigNotice.Repair",
	});

	public void ShowConfigNotice()
	{
		if (IsConfigNoticeVisible)
		{
			return;
		}

		ConfigNoticeText = LocalizationService.GetString("ConfigNotice.Text");
		IsConfigNoticeVisible = true;
		RaiseChanged(nameof(ConfigNoticeText));
		RaiseChanged(nameof(IsConfigNoticeVisible));
	}

	public void AnnounceConfigNotice()
	{
		if (!IsConfigNoticeVisible || ConfigNoticeAnnouncement.Length > 0)
		{
			return;
		}

		ConfigNoticeAnnouncement = ConfigNoticeText;
		RaiseChanged(nameof(ConfigNoticeAnnouncement));
	}

	// First press: nothing runs yet, the line says what the second press will do.
	public void ArmRepair()
	{
		SetRepairState(ConfigRepairState.Armed);
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.RepairArmed"));
	}

	// An expired or interrupted arm. Only the armed hint is cleared; any newer status stays.
	public void DisarmRepair()
	{
		if (RepairState != ConfigRepairState.Armed)
		{
			return;
		}

		SetRepairState(ConfigRepairState.Idle);
		if (string.Equals(StatusText, LocalizationService.GetString("Status.RepairArmed"), StringComparison.Ordinal))
		{
			ClearStatus();
		}
	}

	// The status line is set BEFORE the state change: the window moves focus off the repair
	// button onto the status area in response to that change, and the status area is only
	// focusable while it carries a message.
	public void BeginRepair()
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.Repairing"));
		SetRepairState(ConfigRepairState.Running);
	}

	// The settings files are readable again: the notice has nothing left to say, so it goes.
	// Status first, for the same focus reason as BeginRepair.
	public void ShowRepairSucceeded()
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.RepairDone"));
		SetRepairState(ConfigRepairState.Idle);
		IsConfigNoticeVisible = false;
		RaiseChanged(nameof(IsConfigNoticeVisible));
	}

	// The repair ran (exit 11) but a file is still not usable. The line says WHY, from the
	// re-read outcome: a file another BroChoos version saved is left alone on purpose (info);
	// one too large to read must be moved by the user (info, with its file name); one still
	// broken or gone after the attempt is an error naming the manual command. The notice stays.
	public void ShowRepairIncomplete(ConfigReadOutcome remaining, string? fileName)
	{
		SetRepairState(ConfigRepairState.Idle);
		string name = fileName ?? string.Empty;
		(PickerStatusKind kind, string text) = remaining switch
		{
			ConfigReadOutcome.Newer => (PickerStatusKind.Info, LocalizationService.GetString("Status.RepairKeptNewer", name)),
			ConfigReadOutcome.Incompatible => (PickerStatusKind.Info, LocalizationService.GetString("Status.RepairKeptOlder", name)),
			ConfigReadOutcome.InvalidEnvelope => (PickerStatusKind.Info, LocalizationService.GetString("Status.RepairInvalidEnvelope", name)),
			ConfigReadOutcome.TooLarge => (PickerStatusKind.Info, LocalizationService.GetString("Status.RepairTooLarge", name)),
			ConfigReadOutcome.Corrupt or ConfigReadOutcome.Missing => (PickerStatusKind.Error, LocalizationService.GetString("Status.RepairStillBroken", name)),
			ConfigReadOutcome.Loaded or ConfigReadOutcome.OpenFailed => (PickerStatusKind.Error, LocalizationService.GetString("Status.RepairFailed")),
			_ => (PickerStatusKind.Error, LocalizationService.GetString("Status.RepairFailed")),
		};
		ShowStatus(kind, text);
	}

	public void ShowRepairFailed()
	{
		SetRepairState(ConfigRepairState.Idle);
		ShowStatus(PickerStatusKind.Error, LocalizationService.GetString("Status.RepairFailed"));
	}

	private void SetRepairState(ConfigRepairState state)
	{
		if (RepairState == state)
		{
			return;
		}

		RepairState = state;
		RaiseChanged(nameof(RepairState));
		RaiseChanged(nameof(IsRepairRunning));
		RaiseChanged(nameof(RepairButtonText));
	}

	// The "details layer" Esc closes first. Its content is the full keyboard-shortcut list
	// (F1 or the "?" button toggles it); the bottom bar always shows only the compact line, so
	// the long localized list never has to fit the 460 DIP width on one line.
	public bool IsDetailsOpen { get; private set; }

	// True while a first-run action (browser search) is running. The buttons stay ENABLED on
	// purpose: disabling the focused button would throw focus off it. Re-entry is refused by
	// the session instead, and this flag only drives the "Looking for browsers..." line.
	public bool IsFirstRunBusy { get; private set; }

	// The busy line's TEXT, empty when idle. The text itself changes (not only its visibility),
	// so the polite live region it sits in has a real change to announce.
	public string FirstRunBusyText { get; private set; } = string.Empty;

	// "Add manually" is shown only when a file picker is actually composed. Without one the
	// action can only fail, so offering it would be a dead end (the production picker composes
	// none yet - AvaloniaPicker passes manualBrowserSelector: null).
	public bool IsAddManuallyAvailable { get; private set; }

	public void SetAddManuallyAvailable(bool available)
	{
		if (IsAddManuallyAvailable == available)
		{
			return;
		}

		IsAddManuallyAvailable = available;
		RaiseChanged(nameof(IsAddManuallyAvailable));
	}

	// The whole shortcut list as one sentence per row, for the panel's screen-reader help text:
	// the rows are plain TextBlocks, so without this the list would be read as a name only.
	public string ShortcutListAccessibleText => string.Join(
		" ",
		ShortcutRows.Select(row => row.Action.Length == 0 ? row.Keys : $"{row.Keys}: {row.Action}."));

	// One row per shortcut, parsed from the localized "keys<TAB>action" lines, so translators
	// own both halves and the panel renders as an aligned two-column list.
	public IReadOnlyList<ShortcutHintRow> ShortcutRows { get; } = BuildShortcutRows();

	public static string FindBrowsersText => LocalizationService.GetString("FirstRun.FindBrowsers");
	public static string AddManuallyText => LocalizationService.GetString("FirstRun.AddManually");
	public static string SkipText => LocalizationService.GetString("FirstRun.Skip");
	public static string FirstRunTitleText => LocalizationService.GetString("FirstRun.Title");
	public static string ShortcutsToggleText => LocalizationService.GetString("Picker.Shortcuts.Toggle");

	// W6.2: which admission path the next "Add manually" will run. Default is Custom because
	// it is the weaker claim: a custom admission reports NO profiles (the install layout is
	// unknown), so nothing about a portable data folder is assumed until the user says so.
	public ManualBrowserKind AddManuallyKind { get; set; } = ManualBrowserKind.Custom;

	// D-131: Enter fires only on the visibly focused card; with focus nowhere,
	// this returns null and the window consumes the key without doing anything.
	public ChoiceCardViewModel? GetFocusedActivationTarget()
	{
		return FocusedIndex >= 0 && FocusedIndex < Cards.Count ? Cards[FocusedIndex] : null;
	}

	public bool CanActivateFocusedCard()
	{
		return GetFocusedActivationTarget() is not null && _focusRenderedAt != DateTimeOffset.MinValue
			&& (TimeProvider.GetUtcNow() - _focusRenderedAt) >= TimeSpan.FromMilliseconds(100);
	}

	// Digits 1-9 focus the corresponding card (D-130); out-of-range is a no-op.
	public bool FocusSlot(int slot)
	{
		if (slot < 1 || slot > Cards.Count)
		{
			return false;
		}

		// ALREADY-FOCUSED IS A NO-OP (HIGH). Re-pressing the digit of the card that is
		// already focused must not clear the rendered-focus proof: the window then calls
		// Focus() on the card that ALREADY holds focus, and Avalonia's
		// KeyboardDevice.SetFocusedElement is a no-op for an unchanged element - no GotFocus
		// is raised, so the VM's SetFocusFromControl never runs and the proof is never
		// re-armed. The card stays visibly ringed (the focus really is there) while
		// CanActivateFocusedCard() returns false for the rest of the session: Enter is dead
		// permanently, and nothing on screen says so.
		if (slot - 1 == FocusedIndex)
		{
			return true;
		}

		FocusedIndex = slot - 1;
		_focusRenderedAt = DateTimeOffset.MinValue;
		ClearStatus();
		return true;
	}

	// Arrows walk the card list, clamped at both ends; from "focus nowhere" the
	// first move lands on card 1.
	public bool MoveFocus(int delta)
	{
		if (Cards.Count == 0)
		{
			return false;
		}

		int next = FocusedIndex < 0 ? 0 : Math.Clamp(FocusedIndex + delta, 0, Cards.Count - 1);
		if (next == FocusedIndex)
		{
			return false;
		}

		FocusedIndex = next;
		_focusRenderedAt = DateTimeOffset.MinValue;
		ClearStatus();
		return true;
	}

	// Real control focus (pointer, Tab, programmatic): the window reports the slot
	// it observed so FocusedIndex always names the VISIBLY focused card. The
	// special report -1 is the window's final-focus-failure signal (Focus() false
	// even after the realization-race retry): the claim must be dropped so Enter
	// can never act on a card the user cannot SEE focused (D-131, finding A2).
	public void SetFocusFromControl(int zeroBasedIndex)
	{
		if (zeroBasedIndex == -1)
		{
			FocusedIndex = -1;
			_focusRenderedAt = DateTimeOffset.MinValue;
			return;
		}

		if (zeroBasedIndex >= 0 && zeroBasedIndex < Cards.Count)
		{
			FocusedIndex = zeroBasedIndex;
			_focusRenderedAt = TimeProvider.GetUtcNow();
		}
	}

	// THE RE-ASSERT PATH (findings F1-a, F1-b and the stale-index hole). Real focus is ALREADY on
	// the card and the window has just made it visible, so this is deliberately NOT the same thing
	// as SetFocusFromControl's real focus report: the anti-double-tap clock must not restart, or a
	// digit re-press that was already armed would fall back inside the 100 ms gate and Enter would
	// be ignored for a moment - the A case the repress probe pins. The index always follows the
	// re-asserted card; the timestamp is armed only when the proof is dead, which is exactly when
	// there is nothing to preserve (deactivation, a ringless pointer focus, a cleared proof).
	public void ReassertFocusFromControl(int zeroBasedIndex)
	{
		if (zeroBasedIndex < 0 || zeroBasedIndex >= Cards.Count)
		{
			return;
		}

		FocusedIndex = zeroBasedIndex;
		if (_focusRenderedAt == DateTimeOffset.MinValue)
		{
			_focusRenderedAt = TimeProvider.GetUtcNow();
		}
	}

	// THE RENDERED-FOCUS PROOF, DROPPED WITHOUT FORGETTING THE INTENT (findings N1/B). Focus left
	// the card list - or landed on a card that carries no visible ring - so Enter must be a no-op
	// until a real focus report re-arms it. FocusedIndex deliberately survives: it is the
	// re-assert intent the window reads after a snapshot replacement (ApplySnapshot), so clearing
	// it here would make a later focus move start from nowhere.
	//
	// Only the proof is cleared, never the index, and that asymmetry is the whole point: the proof
	// answers "is there visible focus RIGHT NOW", the index answers "which card was intended".
	public void ClearFocusFromControl()
	{
		_focusRenderedAt = DateTimeOffset.MinValue;
	}

	public void ToggleDetails()
	{
		IsDetailsOpen = !IsDetailsOpen;
		RaiseChanged(nameof(IsDetailsOpen));
		AnnounceDetailsState();
	}

	// The status the "shortcuts are shown" line replaced, put back when the list closes.
	private (PickerStatusKind Kind, string Text)? _statusBeforeDetails;

	// Opening the list says so on the polite status line (which IS a live region), so the change
	// is announced even if a screen reader does not follow the focus move. Whatever that line
	// was showing - a launch failure, the Unknown-recovery prompt, "copied" - is saved first and
	// restored on close, so opening the list never loses an error or its recovery hint. If
	// something else replaced the announcement while the list was open, that newer status wins
	// and is left alone.
	private void AnnounceDetailsState()
	{
		string opened = LocalizationService.GetString("Picker.Shortcuts.Opened");
		if (IsDetailsOpen)
		{
			_statusBeforeDetails = (_statusKind, StatusText);
			ShowStatus(PickerStatusKind.Info, opened);
			return;
		}

		(PickerStatusKind Kind, string Text)? saved = _statusBeforeDetails;
		_statusBeforeDetails = null;
		if (!string.Equals(StatusText, opened, StringComparison.Ordinal))
		{
			return;
		}

		if (saved is { } previous)
		{
			ShowStatus(previous.Kind, previous.Text);
		}
		else
		{
			ClearStatus();
		}
	}

	public void SetFirstRunBusy(bool busy)
	{
		if (IsFirstRunBusy == busy)
		{
			return;
		}

		IsFirstRunBusy = busy;
		FirstRunBusyText = busy ? LocalizationService.GetString("FirstRun.Searching") : string.Empty;
		RaiseChanged(nameof(IsFirstRunBusy));
		RaiseChanged(nameof(FirstRunBusyText));
	}

	public EscapeHandlingResult HandleEscape()
	{
		if (IsDetailsOpen)
		{
			IsDetailsOpen = false;
			RaiseChanged(nameof(IsDetailsOpen));
			AnnounceDetailsState();
			return EscapeHandlingResult.CloseDetails;
		}

		return EscapeHandlingResult.DismissPicker;
	}

	// A forwarded URL replaces the run's subject: the link card re-renders and any
	// stale failure/copy state clears. Cards stay: detection is process state, and
	// re-detecting on a forward is a later-milestone decision.
	public void UpdateUrl(string url)
	{
		Url = url;
		Link = UrlDisplayFormatter.Format(url);
		ClearStatus();
		RaiseChanged(nameof(Url));
		RaiseChanged(nameof(Link));
	}

	// Fail-open on launch failure: the window stays up with a visible state, and
	// another card, F, Ctrl+C and Esc all remain available.
	//
	// 6d: a KNOWN code gets a short localized phrase ("Error.<code>") with the raw code kept in
	// brackets for diagnostics; an UNKNOWN code falls back to the raw code alone, so a new code
	// can never render as a missing-key string.
	public void ShowLaunchFailure(string targetName, string? errorCode)
	{
		// A null code has no raw form to show, so it gets the localized "unknown" word.
		string code = errorCode ?? LocalizationService.GetString("Status.UnknownCode");
		string? phrase = errorCode is null ? null : LocalizationService.TryGetString("Error." + code);
		ShowStatus(
			PickerStatusKind.Error,
			phrase is null
				? LocalizationService.GetString("Status.LaunchFailure", targetName, code)
				: LocalizationService.GetString("Status.LaunchFailureWithReason", targetName, phrase, code));
	}

	// A first-run ACTION (adding a browser, browser search) failed. Not a launch, so the line does
	// not say "Could not open": "<action> failed: <phrase> (<code>)". Unknown codes keep the raw code.
	public void ShowActionFailure(string actionName, string? errorCode)
	{
		string code = errorCode ?? LocalizationService.GetString("Status.UnknownCode");
		string phrase = (errorCode is null ? null : LocalizationService.TryGetString("Error." + code))
			?? LocalizationService.GetString("Status.UnknownCode");
		ShowStatus(PickerStatusKind.Error, LocalizationService.GetString("Status.ActionFailed", actionName, phrase, code));
	}

	// 6d: skipping the first-run scan is a choice, so it is an INFO line, never a launch failure.
	public void ShowFirstRunSkipped()
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.FirstRunSkipped"));
	}

	public void ShowLaunchUnknownRecovery(string targetName, int recoverySlot = -1)
	{
		ShowStatus(
			PickerStatusKind.Error,
			LocalizationService.GetString("Status.LaunchUnknownRecovery", targetName));

		// FINDING F1-b (MEDIUM): the status line above promises "Try again (Enter), Copy link
		// (Ctrl+C), or Cancel (Esc)", but Enter is only armed by a real focus report - and the
		// click that led here was a POINTER press, which focuses the card with no ring and
		// disarms the rendered-focus proof (findings B/N2). Nothing re-armed it, so the
		// advertised retry was dead (MEASURED: claimAfterPointerPress=False,
		// enterWouldRetry=False, ringedAfter=0). This signal is the window's cue to re-assert the
		// ring and the claim on the card the recovery is about.
		//
		// THE SLOT TRAVELS WITH THE SIGNAL, deliberately. The pointer path keeps FocusedIndex as
		// the re-assert INTENT and drops only the proof (ClearFocusFromControl), so the index
		// still names whatever was intended BEFORE the pointer moved focus - it cannot answer
		// "which card is this recovery about". The caller that launched the card can, and does.
		LaunchUnknownRecoveryShown?.Invoke(recoverySlot);
	}

	// R3-1/R4-1: a link forwarded during a failed launch is held. The note is appended as a second
	// sentence to the EXISTING status line (the StatusMessage the window already renders), so no
	// new visual element is needed and the failure text with its retry hint stays first. With no
	// status showing, it appears on the info line.
	public void ShowForwardWaiting()
	{
		string note = LocalizationService.GetString("Status.ForwardWaiting");
		if (StatusText.Contains(note, StringComparison.Ordinal))
		{
			return;
		}

		PickerStatusKind kind = _statusKind == PickerStatusKind.None ? PickerStatusKind.Info : _statusKind;
		ShowStatus(kind, StatusText.Length == 0 ? note : StatusText + " " + note);
	}

	public void ShowCopied()
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.Copied"));
	}

	// W6.2: a manually admitted browser really did enter the catalog, and the picker says so
	// with the name the admission produced. Shown as INFO rather than an error: this is the
	// success path of "Add manually", and reusing the error channel for it would make a
	// successful add indistinguishable from a refusal in a screenshot.
	public void ShowManualAddSucceeded(string displayName)
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.ManualAddSucceeded", displayName));
	}

	public void ShowCopyFailed()
	{
		ShowStatus(PickerStatusKind.Error, LocalizationService.GetString("Status.CopyFailed"));
	}

	public void ShowUseOriginalArmed()
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.UseOriginalArmed"));
	}

	public void ShowUseOriginalUnavailable()
	{
		ShowStatus(PickerStatusKind.Error, LocalizationService.GetString("Status.UseOriginalUnavailable"));
	}

	public void ShowActivationHistorySkipArmed()
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.HistorySkipArmed"));
	}

	public void ShowActivationHistoryDisabled()
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.HistoryDisabled"));
	}

	public void ShowActivationHistoryCleared()
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.HistoryCleared"));
	}

	// First Ctrl+Backspace: nothing is cleared yet, the line says how to confirm.
	public void ShowActivationHistoryClearArmed()
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("Status.HistoryClearArmed"));
	}

	// A search that ran but found nothing: the prompt stays up so the user can add a browser
	// manually, and the line names the one way out that always works (F).
	public void ShowFirstRunNoneFound()
	{
		ShowStatus(PickerStatusKind.Info, LocalizationService.GetString("FirstRun.NoneFound"));
	}

	public void ShowActivationHistoryClearFailed()
	{
		ShowStatus(PickerStatusKind.Error, LocalizationService.GetString("Status.HistoryClearFailed"));
	}

	public void ShowFirstRunPrompt()
	{
		ShowFirstRunPrompt(LocalizationService.GetString("FirstRun.Prompt"));
	}

	// Replaces the run's snapshot wholesale (first-run discovery). The card list is rebuilt,
	// so the containers that carried real focus and its ring are destroyed with the old one:
	// the rendered-focus proof is invalidated UNCONDITIONALLY here - not only when the list
	// shrank - because a same-size or grown replacement also leaves the claim pointing at a
	// card that is no longer on screen (the invisible-claim class D-131 forbids, reachable
	// through the first-run consent action).
	//
	// FocusedIndex is kept as the re-assert INTENT, clamped to the new list (or -1 when it is
	// empty), and the window re-establishes real focus from it; only that focus report re-arms
	// the claim, exactly as it does after FocusSlot/MoveFocus.
	public void ApplySnapshot(BrowserSnapshot snapshot, PickerRankingInputs? ranking = null)
	{
		Snapshot = snapshot;
		if (ranking is not null)
		{
			Ranking = ranking;
		}

		Cards = BuildCards(snapshot.Candidates, Ranking);

		// Dropped BEFORE the property change is raised: the window's Cards handler reads
		// FocusedIndex to decide where focus is re-asserted, so the clamp must be final by then.
		_focusRenderedAt = DateTimeOffset.MinValue;

		if (Cards.Count == 0)
		{
			FocusedIndex = -1;
		}
		else if (FocusedIndex >= Cards.Count)
		{
			FocusedIndex = Cards.Count - 1;
		}

		RaiseChanged(nameof(Snapshot));
		RaiseChanged(nameof(Cards));
	}

	public void ClearStatusForReadyChoices()
	{
		ClearStatus();
	}

	public void ClearFirstRunPrompt()
	{
		ShowFirstRunPrompt(string.Empty, visible: false);
	}

	private void ClearStatus()
	{
		ShowStatus(PickerStatusKind.None, string.Empty);
	}

	private void ShowStatus(PickerStatusKind kind, string text)
	{
		_statusKind = kind;
		StatusText = text;
		RaiseChanged(nameof(StatusText));
		RaiseChanged(nameof(StatusMessage));
		RaiseChanged(nameof(IsInfoStatusVisible));
		RaiseChanged(nameof(IsErrorStatusVisible));
	}

	private void ShowFirstRunPrompt(string text, bool visible = true)
	{
		_firstRunStatusKind = visible ? FirstRunStatusKind.Prompt : FirstRunStatusKind.None;
		FirstRunPromptText = text;
		RaiseChanged(nameof(FirstRunPromptText));
		RaiseChanged(nameof(IsFirstRunPromptVisible));
		RaiseChanged(nameof(IsActionButtonsVisible));
	}

	private static List<ChoiceCardViewModel> BuildCards(
		IReadOnlyList<BrowserCandidate> candidates,
		PickerRankingInputs ranking)
	{
		// RANK BEFORE THE CAP, and the order matters. The cap is a keyboard limit (digits
		// 1-9), so it must select the NINE BEST VISIBLE cards rather than nine arbitrary
		// ones that a later reorder would have promoted. The Slot below is the VISIBLE
		// position (what a digit names, D-130); PinnedSlot is the candidate's position in
		// the detector's list, which RankingEngine preserves across reorders.
		IReadOnlyList<RankedChoice> ranked = RankingEngine.ExpandChromiumProfiles(
			RankingEngine.Rank(
				candidates,
				ranking.Decision,
				ranking.SourceConfidence,
				ranking.ProbablePreferredBrowserId,
				ranking.RecentSessionBrowserIds),
			ranking.Decision);

		// The nine-slot cap counts TILES (one per Chromium profile), in ranked order.
		List<ChoiceCardViewModel> cards = [];
		foreach (RankedChoice choice in ranked)
		{
			if (cards.Count == MaxChoiceSlots)
			{
				break;
			}

			cards.Add(new ChoiceCardViewModel(
				choice.Candidate,
				cards.Count + 1,
				choice.PinnedSlot,
				choice.ReasonCode,
				choice.MatchedRuleId,
				choice.LaunchProfileId,
				choice.HiddenProfileCount));
		}

		return cards;
	}

	private static List<ShortcutHintRow> BuildShortcutRows()
	{
		List<ShortcutHintRow> rows = [];
		foreach (string line in LocalizationService.GetString("picker.select.hints").Split('\n'))
		{
			int tab = line.IndexOf('\t', StringComparison.Ordinal);
			rows.Add(tab < 0
				? new ShortcutHintRow(line, string.Empty)
				: new ShortcutHintRow(line[..tab], line[(tab + 1)..]));
		}

		return rows;
	}

	private void RaiseChanged(string propertyName)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}

// One line of the shortcut panel: the key chord and what it does, both localized.
internal sealed record ShortcutHintRow(string Keys, string Action);
