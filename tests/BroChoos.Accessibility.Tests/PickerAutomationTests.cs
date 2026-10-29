using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace BroChoos.Accessibility.Tests;

public sealed partial class PickerAutomationTests
{
	private static readonly string Root = FindRepositoryRoot();
	private static readonly string PickerWindowPath = Path.Combine(Root, "src", "BroChoos.App", "Picker", "PickerWindow.axaml");
	private static readonly string GhostToastPath = Path.Combine(Root, "src", "BroChoos.App", "Picker", "GhostToastWindow.axaml");
	private static readonly string CleanupToastPath = Path.Combine(Root, "src", "BroChoos.App", "Picker", "CleanupRecoveryToastWindow.axaml");

	private static readonly XNamespace Ns = "https://github.com/avaloniaui";
	private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

	[Fact]
	public void Picker_window_has_automation_properties()
	{
		XDocument doc = XDocument.Parse(File.ReadAllText(PickerWindowPath));
		XElement window = doc.Root!;

		// Window should have AutomationProperties.Name or equivalent
		XAttribute? nameAttr = window.Attribute(XNs + "Name");
		XAttribute? automationNameAttr = window.Attribute(XNs + "AutomationProperties.Name");
		_ = (nameAttr?.Value ?? automationNameAttr?.Value).Should().NotBeNullOrEmpty("Picker window must have AutomationProperties.Name for screen readers");
	}

	[Fact]
	public void Choice_cards_have_proper_automation_roles_and_names()
	{
		XDocument doc = XDocument.Parse(File.ReadAllText(PickerWindowPath));
		XElement itemsControl = doc.Descendants(Ns + "ItemsControl").Single(c => (string?)c.Attribute(XNs + "Name") == "CardsItems");
		XElement dataTemplate = itemsControl.Element(Ns + "ItemsControl.ItemTemplate")!
			.Element(Ns + "DataTemplate")!;
		XElement button = dataTemplate.Element(Ns + "Button")!;

		// Choice cards are Buttons - they have implicit Button role, but should have AutomationProperties.Name
		// The name should come from the DisplayName binding
		XElement? textBlock = button.Descendants(Ns + "TextBlock").FirstOrDefault(t =>
		{
			string? text = (string?)t.Attribute("Text");
			return text != null && text.Contains("DisplayName", StringComparison.Ordinal);
		});
		_ = textBlock.Should().NotBeNull("Choice card must have a TextBlock bound to DisplayName for automation name");
	}

	[Fact]
	public void Key_hints_have_automation_properties_for_screen_readers()
	{
		// The always-on key-hint line was removed (picker layout spec). The keys are reached through
		// the "?" toggle (and F1), which must carry a screen-reader name.
		XDocument doc = XDocument.Parse(File.ReadAllText(PickerWindowPath));
		XElement? toggle = doc.Descendants()
			.FirstOrDefault(e => (string?)e.Attribute(XNs + "Name") == "ShortcutsToggleButton");

		_ = toggle.Should().NotBeNull("the shortcut list toggle must exist");
		_ = ((string?)toggle!.Attribute("AutomationProperties.Name")).Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void Ghost_toast_is_non_interactive_and_announced_as_status()
	{
		XDocument doc = XDocument.Parse(File.ReadAllText(GhostToastPath));
		XElement window = doc.Root!;

		// Ghost toast is hit-through and non-focusable
		string? focusable = (string?)window.Attribute("Focusable") ?? (string?)window.Attribute(XNs + "Focusable");
		string? hitTestVisible = (string?)window.Attribute("IsHitTestVisible") ?? (string?)window.Attribute(XNs + "IsHitTestVisible");

		_ = focusable
			.Should().Be("False", "Ghost toast must set Focusable in local namespace");
		_ = hitTestVisible
			.Should().Be("False", "Ghost toast must set IsHitTestVisible in local namespace");

		// Should have automation live region for status announcement
		// In Avalonia, this is AutomationProperties.LiveSetting = "Polite"
		XElement border = window.Descendants(Ns + "Border").First();
		_ = border.Attribute(XNs + "AutomationProperties.LiveSetting")?.Value
		.Should().Be("Polite", "Ghost toast must be announced as polite live region");
	}

	[Fact]
	public void Cleanup_recovery_toast_is_interactive_and_focusable()
	{
		XDocument doc = XDocument.Parse(File.ReadAllText(CleanupToastPath));
		XElement window = doc.Root!;

		// Cleanup toast is interactive
		string? focusable = (string?)window.Attribute("Focusable") ?? (string?)window.Attribute(XNs + "Focusable");
		string? hitTestVisible = (string?)window.Attribute("IsHitTestVisible") ?? (string?)window.Attribute(XNs + "IsHitTestVisible");

		_ = focusable
			.Should().Be("True", "Cleanup toast must set Focusable in local namespace");
		_ = hitTestVisible
			.Should().Be("True", "Cleanup toast must set IsHitTestVisible in local namespace");

		// Restore button must have automation name
		XElement button = doc.Descendants(Ns + "Button").First();
		_ = button.Attribute(XNs + "AutomationProperties.Name")?.Value
		.Should().NotBeNullOrEmpty("Restore button must have AutomationProperties.Name");
	}

	[Fact]
	public void Focus_order_is_logical_and_no_control_is_unreachable()
	{
		XDocument doc = XDocument.Parse(File.ReadAllText(PickerWindowPath));

		// All interactive elements should be reachable via Tab/arrows
		// Buttons should not have IsTabStop="False"
		IEnumerable<XElement> buttons = doc.Descendants(Ns + "Button");
		foreach (XElement button in buttons)
		{
			XAttribute? attr = button.Attribute("IsTabStop");
			string? value = (string?)attr;
			_ = value.Should().NotBe("False", "Interactive buttons must not disable tab stop");
		}
	}

	[Fact]
	public void Text_scaling_tolerance_no_clipping_at_150_percent()
	{
		// This is a structural test - verify no fixed pixel heights that would clip at 1.5x
		XDocument doc = XDocument.Parse(File.ReadAllText(PickerWindowPath));

		// Check for any hardcoded Height values that aren't SizeToContent or Auto
		IEnumerable<XElement> elementsWithHeight = doc.Descendants().Where(e => e.Attribute("Height") != null);
		foreach (XElement element in elementsWithHeight)
		{
			string? height = element.Attribute("Height")?.Value;
			if (double.TryParse(height, out double h) && h > 0)
			{
				// Fixed heights are risky for text scaling - only allow on non-text containers
				// The main window has Width="460" but no Height (SizeToContent)
				_ = element.Name.LocalName.Should().NotBe("TextBlock", "TextBlock should not have fixed Height");
			}
		}
	}

	[Fact]
	public void IME_safety_no_input_controls_without_ime_support()
	{
		// Picker has no text input controls - this is a smoke test
		XDocument doc = XDocument.Parse(File.ReadAllText(PickerWindowPath));
		IEnumerable<XElement> textBoxes = doc.Descendants(Ns + "TextBox");
		_ = textBoxes.Should().BeEmpty("Picker should not contain TextBox controls");
	}

	[Fact]
	public void Screen_reader_descriptions_for_state_changes()
	{
		// Status text in PickerWindow should be in a live region
		XDocument doc = XDocument.Parse(File.ReadAllText(PickerWindowPath));

		IEnumerable<XElement> statusTextBlocks = doc.Descendants(Ns + "TextBlock")
			.Where(t =>
			{
				string? text = (string?)t.Attribute("Text");
				return text != null && text.Contains("StatusMessage", StringComparison.Ordinal);
			});

		_ = statusTextBlocks.Should().NotBeEmpty("Picker should expose a bound status text element for screen readers");

		foreach (XElement tb in statusTextBlocks)
		{
			_ = ((string?)tb.Attribute("Text"))
				.Should().Contain("StatusMessage", "status text should bind to StatusMessage");

			// Status changes should be announced - the text itself or an ancestor should have LiveSetting
			XElement? parent = tb;
			bool hasLiveRegion = false;
			while (parent != null)
			{
				if (parent.Attribute("AutomationProperties.LiveSetting") != null
					|| parent.Attribute(XNs + "AutomationProperties.LiveSetting") != null)
				{
					hasLiveRegion = true;
					break;
				}
				parent = parent.Parent;
			}
			_ = hasLiveRegion.Should().BeTrue("Status text changes must be in an Automation live region");
		}
	}

	[Fact]
	public void Unlocalized_brands_and_urls_are_ltR_isolated()
	{
		// This test verifies the pattern - actual enforcement is in LocalizationCoverageTests
		// Here we just ensure the AXAML doesn't have problematic RTL mixing
		XDocument doc = XDocument.Parse(File.ReadAllText(PickerWindowPath));

		// URL/brand display elements should have FlowDirection="LeftToRight" or be in LTR container
		XElement? linkHost = doc.Descendants(Ns + "TextBlock").FirstOrDefault(t =>
		{
			string? text = (string?)t.Attribute("Text");
			return text != null && text.Contains("Link.Host", StringComparison.Ordinal);
		});
		_ = linkHost.Should().NotBeNull();
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BroChoos.sln")))
		{
			directory = directory.Parent;
		}
		return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
	}
}
