namespace BroChoos.App.Picker;

// The picker's key contract (D-130) as mapped by the window's TUNNELLING handler.
// Unmapped keys (Tab, anything else) fall through to native focus traversal and
// control behaviour: that is the per-key global/local policy from the spike
// (item 6) - picker hotkeys are global, everything else stays local.
internal enum PickerKey
{
	Digit,
	Enter,
	Escape,
	SystemFallback,
	Copy,
	UseOriginal,
	SkipHistoryOnce,
	// Ctrl+Backspace. The first press only arms; a second press within the confirm window
	// clears (PickerSession.HandleClearHistoryKey).
	ClearHistoryConfirmed,
	ToggleShortcuts,
	FocusNext,
	FocusPrevious,
}

// Slot is the 1-based digit for Kind=Digit and unused (0) otherwise.
internal readonly record struct PickerKeyAction(PickerKey Kind, int Slot);
