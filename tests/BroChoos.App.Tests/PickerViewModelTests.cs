using BroChoos.App.Picker;
using BroChoos.App.Localization;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using System.Globalization;
using Xunit;

namespace BroChoos.App.Tests;

// The picker view-model is the unit-testable heart: candidate-to-card mapping
// (D-125) and the Enter-only-when-focused rule (D-131). No UI thread, no window:
// the VM is a pure CLR object the window merely binds to.
// LocalizationService.CurrentCulture is process-wide static state; serialized with the other culture-touching classes.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class PickerViewModelTests
{
	private const string Url = "https://example.com/";

	[Fact]
	public void Cards_MapEveryCandidate_WithOneBasedSlotHints()
	{
		PickerViewModel vm = New(2);

		_ = vm.Cards.Should().HaveCount(2);
		_ = vm.Cards.Select(c => c.Slot).Should().Equal(1, 2);
		_ = vm.Cards.Select(c => c.ShortcutHint).Should().Equal("1", "2");
		_ = vm.Cards.Select(c => c.DisplayName).Should().Equal("Microsoft Edge", "Google Chrome");
		_ = vm.Cards.Select(c => c.BrowserId).Should().Equal("edge-stable", "chrome-stable");
	}

	// Opening the shortcut list must not lose an error or its recovery hint: the status it
	// replaces comes back, with its kind, when the list closes (F1 toggle and Esc alike).
	[Fact]
	public void ShortcutList_RestoresTheErrorStatusItReplaced_WhenClosed()
	{
		PickerViewModel vm = New(1);
		vm.ShowLaunchFailure("Microsoft Edge", "launch.start-failed");
		string failure = vm.StatusMessage;

		vm.ToggleDetails();
		_ = vm.StatusMessage.Should().Be(LocalizationService.GetString("Picker.Shortcuts.Opened"));
		_ = vm.IsInfoStatusVisible.Should().BeTrue();

		vm.ToggleDetails();
		_ = vm.StatusMessage.Should().Be(failure);
		_ = vm.IsErrorStatusVisible.Should().BeTrue("the restored status keeps its error styling");

		vm.ToggleDetails();
		_ = vm.HandleEscape().Should().Be(EscapeHandlingResult.CloseDetails);
		_ = vm.StatusMessage.Should().Be(failure, "Esc restores it too");
	}

	[Fact]
	public void ShortcutList_KeepsANewerStatus_ThatArrivedWhileItWasOpen()
	{
		PickerViewModel vm = New(1);
		vm.ShowLaunchFailure("Microsoft Edge", "launch.start-failed");

		vm.ToggleDetails();
		vm.ShowCopied();
		vm.ToggleDetails();

		_ = vm.StatusMessage.Should().Be(LocalizationService.GetString("Status.Copied"));
	}

	[Fact]
	public void Cards_AreCappedAtNine_TheKeyboardHasNoSlotTen()
	{
		PickerViewModel vm = New(12);

		_ = vm.Cards.Should().HaveCount(9);
		_ = vm.Cards[^1].ShortcutHint.Should().Be("9");
	}

	// D-131: with focus nowhere there is NO activation target; an invisible
	// default must never fire.
	[Fact]
	public void GetFocusedActivationTarget_WithoutFocus_ReturnsNull()
	{
		PickerViewModel vm = New(2);

		_ = vm.FocusedIndex.Should().Be(-1);
		_ = vm.GetFocusedActivationTarget().Should().BeNull();
	}

	[Fact]
	public void FocusSlot_ValidSlot_MakesThatCardTheActivationTarget()
	{
		PickerViewModel vm = New(2);

		bool moved = vm.FocusSlot(2);

		_ = moved.Should().BeTrue();
		_ = vm.FocusedIndex.Should().Be(1);
		_ = vm.GetFocusedActivationTarget()!.BrowserId.Should().Be("chrome-stable");
	}

	[Fact]
	public void FocusSlot_OutOfRange_IsANoOp()
	{
		PickerViewModel vm = New(2);

		bool moved = vm.FocusSlot(3);

		_ = moved.Should().BeFalse();
		_ = vm.FocusedIndex.Should().Be(-1);
		_ = vm.GetFocusedActivationTarget().Should().BeNull();
	}

	[Fact]
	public void MoveFocus_FromNowhere_LandsOnTheFirstCard()
	{
		PickerViewModel vm = New(2);

		_ = vm.MoveFocus(1).Should().BeTrue();
		_ = vm.FocusedIndex.Should().Be(0);
	}

	[Fact]
	public void MoveFocus_IsClampedAtBothEnds()
	{
		PickerViewModel vm = New(2);
		_ = vm.FocusSlot(2);

		_ = vm.MoveFocus(1).Should().BeFalse();
		_ = vm.FocusedIndex.Should().Be(1);

		_ = vm.MoveFocus(-1).Should().BeTrue();
		_ = vm.FocusedIndex.Should().Be(0);
		_ = vm.MoveFocus(-1).Should().BeFalse();
		_ = vm.FocusedIndex.Should().Be(0);
	}

	// The window reports real control focus (pointer/Tab/programmatic), so the
	// VM's index always tracks the VISIBLY focused card.
	[Fact]
	public void SetFocusFromControl_SyncsWithRealFocus_AndIgnoresInvalidSlots()
	{
		PickerViewModel vm = New(2);

		vm.SetFocusFromControl(1);
		_ = vm.FocusedIndex.Should().Be(1);

		vm.SetFocusFromControl(7);
		_ = vm.FocusedIndex.Should().Be(1);
	}

	// D-131 / finding A2: the window's final-focus-failure report (-1) drops the
	// marked focus, so Enter can never act on a card that is not VISIBLY focused.
	[Fact]
	public void SetFocusFromControl_MinusOne_ClearsTheMarkedFocus_SoEnterIsIgnored()
	{
		PickerViewModel vm = New(2);
		_ = vm.FocusSlot(2);
		_ = vm.FocusedIndex.Should().Be(1);

		vm.SetFocusFromControl(-1);

		_ = vm.FocusedIndex.Should().Be(-1);
		_ = vm.GetFocusedActivationTarget().Should().BeNull();
	}

	[Fact]
	public void Navigation_ClearsAnyStatus()
	{
		PickerViewModel vm = New(2);
		vm.ShowLaunchFailure("Microsoft Edge", "launch.start-failed");
		_ = vm.IsErrorStatusVisible.Should().BeTrue();

		_ = vm.FocusSlot(1);

		_ = vm.IsErrorStatusVisible.Should().BeFalse();
		_ = vm.IsInfoStatusVisible.Should().BeFalse();
		_ = vm.StatusText.Should().BeEmpty();
	}

	[Fact]
	public void ShowLaunchFailure_ShowsTheFailOpenErrorState()
	{
		PickerViewModel vm = New(2);

		vm.ShowLaunchFailure("Microsoft Edge", "launch.start-failed");

		_ = vm.IsErrorStatusVisible.Should().BeTrue();
		_ = vm.IsInfoStatusVisible.Should().BeFalse();
		_ = vm.StatusText.Should().Contain("Microsoft Edge");
		_ = vm.StatusText.Should().Contain("launch.start-failed");
		_ = vm.StatusText.Should().Contain("Ctrl+C");
		_ = vm.StatusText.Should().Contain("Esc");
	}

	[Fact]
	public void ShowLaunchUnknownRecovery_ShowsExplicitTryAgainCopyCancelActions()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			PickerViewModel vm = New(2);
			vm.ShowLaunchUnknownRecovery("Microsoft Edge");

			_ = vm.IsErrorStatusVisible.Should().BeTrue();
			_ = vm.IsInfoStatusVisible.Should().BeFalse();
			_ = vm.StatusText.Should().Contain("Microsoft Edge");
			_ = vm.StatusText.Should().Contain("Try again");
			_ = vm.StatusText.Should().Contain("Copy link");
			_ = vm.StatusText.Should().Contain("Cancel");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public void ShowCopied_ShowsTheInfoState()
	{
		PickerViewModel vm = New(2);

		vm.ShowCopied();

		_ = vm.IsInfoStatusVisible.Should().BeTrue();
		_ = vm.IsErrorStatusVisible.Should().BeFalse();
	}

	[Fact]
	public void CopyFailure_RendersAnErrorState_NotACrash()
	{
		PickerViewModel vm = New(2);

		vm.ShowCopyFailed();

		_ = vm.IsErrorStatusVisible.Should().BeTrue();
	}

	// A forwarded URL replaces the subject of the run: link card re-renders and
	// stale state clears; the detected cards stay (detection is process state).
	[Fact]
	public void UpdateUrl_ReplacesSubject_AndClearsStatus()
	{
		PickerViewModel vm = New(2);
		_ = vm.FocusSlot(1);
		vm.ShowLaunchFailure("Microsoft Edge", "launch.start-failed");

		vm.UpdateUrl("https://other.example/page?q=verylongquerystringthatwillnotfitinthecard");

		_ = vm.Url.Should().Be("https://other.example/page?q=verylongquerystringthatwillnotfitinthecard");
		_ = vm.Link.Host.Should().Be("other.example");
		_ = vm.IsErrorStatusVisible.Should().BeFalse();
		// Card focus is preserved across the handoff (the cards did not change).
		_ = vm.FocusedIndex.Should().Be(0);
	}

	[Fact]
	public void PropertyChanged_FiresForDisplayedFields()
	{
		PickerViewModel vm = New(2);
		List<string> changed = [];
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

		vm.ShowCopied();

		_ = changed.Should().Contain(nameof(PickerViewModel.StatusText));
		_ = changed.Should().Contain(nameof(PickerViewModel.IsInfoStatusVisible));
		// Strengthened: the error-visibility notification must fire too; a dropped
		// IsErrorStatusVisible raise can no longer pass this test unnoticed.
		_ = changed.Should().Contain(nameof(PickerViewModel.IsErrorStatusVisible));
	}

	private static PickerViewModel New(int candidateCount)
	{
		BrowserCandidate[] candidates = [.. Enumerable.Range(1, candidateCount).Select(i =>
			i switch
			{
				1 => new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path"),
				2 => new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path"),
				_ => new BrowserCandidate($"browser-{i}", $"Browser {i}", $@"C:\B{i}\b.exe", "stable", "registry"),
			})];

		BrowserSnapshot snapshot = new(candidates, 0, DateTimeOffset.UtcNow);
		return new PickerViewModel(Url, snapshot);
	}
}
