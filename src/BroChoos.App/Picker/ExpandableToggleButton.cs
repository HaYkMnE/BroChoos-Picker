using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace BroChoos.App.Picker;

// The "?" shortcut toggle. A plain Button exposes only Invoke to UI Automation, so a screen
// reader cannot tell whether the shortcut list it controls is open. This button adds the
// standard ExpandCollapse pattern (the same pattern ComboBox uses for its drop-down) and
// raises the state-change event when IsExpanded flips, so "expanded"/"collapsed" is announced.
//
// StyleKeyOverride keeps it a Button for styling: the Fluent Button template and the window's
// Button.hintToggle / Button:focus-visible selectors apply unchanged.
internal sealed class ExpandableToggleButton : Button
{
	public static readonly StyledProperty<bool> IsExpandedProperty =
		AvaloniaProperty.Register<ExpandableToggleButton, bool>(nameof(IsExpanded));

	public bool IsExpanded
	{
		get => GetValue(IsExpandedProperty);
		set => SetValue(IsExpandedProperty, value);
	}

	protected override Type StyleKeyOverride => typeof(Button);

	private Peer? _peer;

	// Expand/Collapse from automation runs the same Click path as a press.
	private void Activate()
	{
		OnClick();
	}

	protected override AutomationPeer OnCreateAutomationPeer()
	{
		return _peer = new Peer(this);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		if (change.Property == IsExpandedProperty)
		{
			_peer?.RaiseExpandCollapseChanged(change.GetOldValue<bool>(), change.GetNewValue<bool>());
		}
	}

	private sealed class Peer(ExpandableToggleButton owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
	{
		private ExpandableToggleButton Toggle => (ExpandableToggleButton)Owner;

		public ExpandCollapseState ExpandCollapseState =>
			Toggle.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

		public bool ShowsMenu => false;

		public void Expand()
		{
			if (!Toggle.IsExpanded)
			{
				Toggle.Activate();
			}
		}

		public void Collapse()
		{
			if (Toggle.IsExpanded)
			{
				Toggle.Activate();
			}
		}

		internal void RaiseExpandCollapseChanged(bool oldValue, bool newValue)
		{
			RaisePropertyChangedEvent(
				ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
				ToState(oldValue),
				ToState(newValue));
		}

		private static ExpandCollapseState ToState(bool expanded)
		{
			return expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
		}
	}
}
