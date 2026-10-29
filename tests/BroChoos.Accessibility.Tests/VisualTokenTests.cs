using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace BroChoos.Accessibility.Tests;

public sealed partial class VisualTokenTests
{
	private static readonly string Root = FindRepositoryRoot();
	private static readonly string TokensText = File.ReadAllText(Path.Combine(Root, "src", "BroChoos.App", "Picker", "PickerTokens.axaml"));
	private static readonly string PickerText = File.ReadAllText(Path.Combine(Root, "src", "BroChoos.App", "Picker", "PickerWindow.axaml"));
	private static readonly string PickerAppText = File.ReadAllText(Path.Combine(Root, "src", "BroChoos.App", "Picker", "PickerApp.cs"));
	private static readonly string AccessibilityText = File.ReadAllText(Path.Combine(Root, "src", "BroChoos.App", "Picker", "OsAccessibilitySettings.cs"));

	private static readonly XNamespace Ns = "https://github.com/avaloniaui";
	private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XDocument Tokens = XDocument.Parse(TokensText);
	private static readonly XDocument Picker = XDocument.Parse(PickerText);

	[Fact]
	public void Token_dictionary_contains_the_approved_inventory()
	{
		HashSet<string> keys = [.. Tokens.Descendants().Attributes(XNs + "Key").Select(attribute => attribute.Value)];
		string[] required =
		[
			"bc.font.ui", "bc.type.caption", "bc.type.body", "bc.type.bodyStrong", "bc.type.subtitle", "bc.type.title",
			"bc.space.0", "bc.space.1", "bc.space.2", "bc.space.3", "bc.space.4", "bc.space.5", "bc.space.6", "bc.space.7", "bc.space.8",
			"bc.pad.0", "bc.pad.1", "bc.pad.2", "bc.pad.3", "bc.pad.4", "bc.pad.5", "bc.pad.6", "bc.pad.7", "bc.pad.8",
			"bc.target.min", "bc.target.cardRow", "bc.radius.none", "bc.radius.control", "bc.radius.card", "bc.radius.window",
			"bc.elevation.card", "bc.elevation.flyout", "bc.elevation.dialog", "bc.material.solid", "bc.material.blur",
			"bc.bg.base", "bc.bg.card", "bc.bg.subtle", "bc.text.primary", "bc.text.secondary", "bc.text.disabled",
			"bc.accent.default", "bc.accent.hover", "bc.accent.pressed", "bc.accent.onAccent", "bc.border.subtle", "bc.border.strong",
			"bc.status.success", "bc.status.success.onSurface", "bc.status.warning", "bc.status.warning.onSurface", "bc.status.danger", "bc.status.danger.onSurface",
			"bc.scrim", "bc.focus.outer", "bc.focus.inner", "bc.motion.reduced", "bc.dwell.ghost", "bc.dwell.recovery"
		];

		foreach (string token in required)
		{
			_ = keys.Should().Contain(token, "W5.1 token inventory must expose all approved bc.* keys");
		}

		// bc.motion.fast/.normal/.slow, bc.motion.transitions.enabled and bc.os.reducedMotion are
		// deliberately NOT in this inventory: they are runtime-owned (see
		// Runtime_owned_motion_tokens_are_not_declared_in_the_token_dictionary).

		_ = ThemeDictionary("Light").Should().NotBeNull();
		_ = ThemeDictionary("Dark").Should().NotBeNull();
		_ = ThemeDictionary("Default").Should().NotBeNull();

		// The High Contrast dictionary is keyed by an x:Static reference to a code-defined
		// ThemeVariant, NOT by a string. A string key cannot work: ThemeDictionaries is keyed
		// by ThemeVariant and Avalonia's converter accepts the built-in variants only, which is
		// exactly what killed every launch (NotSupportedException, exit 0xE0434352).
		_ = TokensText.Should().Contain("x:Key=\"{x:Static p:PickerThemeVariants.HighContrast}\"");
	}

	// NEW-9 (0A-5 audit finding). bc.pad.* and bc.space.* are two families carrying the SAME
	// numeric steps in two types, because a DynamicResource is handed to its target property
	// unconverted and Border.Padding needs a Thickness while StackPanel.Spacing needs a double.
	// The file's own comment says "bc.pad.N mirrors bc.space.N exactly; keep them in step", and
	// until now only the TYPE half of that contract was asserted - the two could drift apart
	// numerically with nothing failing. A drifted pair is a silent visual inconsistency: the
	// same named step would mean two different insets depending on which property consumed it.
	[Fact]
	public void Padding_and_spacing_families_stay_numerically_in_step()
	{
		foreach (string step in new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8" })
		{
			double space = double.Parse(ValueForRootKey($"bc.space.{step}"), CultureInfo.InvariantCulture);
			double pad = ParseThicknessUniform(ValueForRootKey($"bc.pad.{step}"));

			_ = pad.Should().Be(space,
				$"bc.pad.{step} must mirror bc.space.{step} exactly: the families exist only because the "
				+ "consumer properties need different types, not because the steps are different");
		}
	}

	// A Thickness token is written either as one number ("12", meaning all four edges) or as
	// four comma-separated ones. The token file uses the uniform form for this family; anything
	// else is a shape this assertion cannot compare step-by-step, so it fails rather than
	// silently passing.
	private static double ParseThicknessUniform(string value)
	{
		string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
		_ = parts.Should().HaveCount(1,
			$"the bc.pad.* family is written as a uniform Thickness ('{value}' is not)");

		return double.Parse(parts[0], CultureInfo.InvariantCulture);
	}

	// THE TOKEN-TYPE CONTRACT, asserted at the source level because that is where the fix has
	// to be made. A DynamicResource hands its value to the target property UNCONVERTED, so a
	// token's declared type must already BE the property's type. These four mismatches each
	// threw at window construction: FontFamily, CornerRadius, Thickness and the brush roles.
	[Fact]
	public void Token_declared_types_match_their_consumers()
	{
		_ = DeclaredElement("bc.font.ui").Should().Be("FontFamily",
			"Window.FontFamily is a FontFamily; an x:String token threw InvalidCastException");
		_ = DeclaredElement("bc.radius.card").Should().Be("CornerRadius");
		_ = DeclaredElement("bc.radius.control").Should().Be("CornerRadius");
		_ = DeclaredElement("bc.radius.none").Should().Be("CornerRadius");
		_ = DeclaredElement("bc.radius.window").Should().Be("CornerRadius");

		// Spacing stays a double (StackPanel.Spacing cannot take a Thickness), so the inset
		// role has its own Thickness-typed family. Both must exist and stay in step.
		foreach (string step in new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8" })
		{
			_ = DeclaredElement($"bc.pad.{step}").Should().Be("Thickness", "Border.Padding is a Thickness");
			_ = DeclaredElement($"bc.space.{step}").Should().Be("Double", "StackPanel.Spacing is a double");
		}
	}

	// F2: the High Contrast overlay must ALIAS the platform brushes with <StaticResource/>,
	// which copies the brush into this dictionary. The <DynamicResource/> shape stores a
	// DynamicResourceExtension instead, and the property store throws InvalidCastException
	// the moment a consumer binds it to a Brush property.
	[Fact]
	public void High_contrast_overlay_uses_static_resource_aliases_not_dynamic_resource_values()
	{
		XElement highContrast = ThemeDictionaryByStaticKey();

		IEnumerable<XElement> dynamicValues = highContrast.Elements().Where(element => element.Name == Ns + "DynamicResource");
		_ = dynamicValues.Should().BeEmpty(
			"a DynamicResource used as a dictionary VALUE stores an extension, not a brush, and throws at the consumer");

		IEnumerable<XElement> aliases = highContrast.Elements().Where(element => element.Name == Ns + "StaticResource");
		_ = aliases.Should().NotBeEmpty("the High Contrast overlay is a set of platform-brush aliases");
		_ = aliases.Should().OnlyContain(alias => alias.Attribute("ResourceKey") != null);
	}

	[Fact]
	public void Picker_uses_tokenized_values_and_preserves_accessible_target_floor()
	{
		_ = HexRegex().Matches(PickerText).Should().BeEmpty("Picker visual shell should consume bc.* resources, not literal colors");
		_ = OffScaleRegex().Matches(PickerText).Should().BeEmpty("Picker shell must stay on approved type/spacing/radius scale");

		IEnumerable<XElement> setters = Picker.Descendants(Ns + "Setter");
		_ = setters.Should().Contain(setter =>
			(string?)setter.Attribute("Property") == "MinHeight"
			&& (string?)setter.Attribute("Value") == "48");

		IEnumerable<XElement> choiceButtons = Picker.Descendants(Ns + "Button")
			.Where(button => (string?)button.Attribute("Classes") == "choiceCard");

		_ = choiceButtons.Should().OnlyContain(button =>
			(double?)button.Attribute("MinWidth") >= 44
			&& (double?)button.Attribute("MinHeight") >= 44);

		_ = PickerText.Should().Contain("DynamicResource bc.font.ui");
	}

	[Fact]
	public void Light_theme_roles_meet_required_contrast_thresholds()
	{
		Dictionary<string, string> light = ThemeColorMap("Light");

		_ = Contrast(light["bc.text.primary"], light["bc.bg.base"]).Should().BeGreaterThanOrEqualTo(4.5);
		_ = Contrast(light["bc.text.secondary"], light["bc.bg.base"]).Should().BeGreaterThanOrEqualTo(4.5);
		_ = Contrast(light["bc.accent.default"], light["bc.bg.base"]).Should().BeGreaterThanOrEqualTo(3.0);
		_ = Contrast(light["bc.border.strong"], light["bc.bg.base"]).Should().BeGreaterThanOrEqualTo(3.0);
	}

	[Fact]
	public void Tokens_include_high_contrast_overlay_and_reduced_motion_contract_values()
	{
		XElement highContrast = ThemeDictionaryByStaticKey();
		HashSet<string> hcKeys = [.. highContrast.Descendants().Attributes(XNs + "Key").Select(attribute => attribute.Value)];

		_ = hcKeys.Should().Contain("bc.focus.outer");
		_ = hcKeys.Should().Contain("bc.focus.inner");

		// bc.motion.reduced is the one motion value the token file still owns: it is the
		// "no motion" constant, identical whatever the OS setting is, so there is nothing for
		// the runtime to override. The other five motion keys are RUNTIME-OWNED and must NOT
		// appear here - see Runtime_owned_motion_tokens_are_not_declared_in_the_token_dictionary.
		_ = ValueForRootKey("bc.motion.reduced").Should().Be("0");

		_ = TokensText.Should().NotMatchRegex("(?i)retention|activation.{0,20}(days|entries|cap)");
	}

	// NEW-1 (0A-5 audit finding). The five keys PickerApp writes from the live OS setting were
	// ALSO declared in PickerTokens.axaml, and the token file is merged into Window.Resources.
	// A window-level dictionary is searched BEFORE the application one, so the declaration won:
	// with reduced motion switched ON, a DynamicResource consumer still resolved 90 instead of
	// the 0 the application had written. The defect was invisible on a default desktop, because
	// the declared values WERE the "animations enabled" defaults - only the users the setting
	// exists for ever saw it.
	//
	// The guard is source-level by necessity: the failure is dictionary PRECEDENCE, which needs
	// a live Application and a shown window to observe, and this test assembly has neither (it
	// reads the sources). The behavioural half of the pin lives in BroChoos.App.Tests, which
	// does own an Avalonia application - see PickerXamlLoadTests.
	[Fact]
	public void Runtime_owned_motion_tokens_are_not_declared_in_the_token_dictionary()
	{
		string[] runtimeOwned =
		[
			"bc.motion.fast", "bc.motion.normal", "bc.motion.slow",
			"bc.motion.transitions.enabled", "bc.os.reducedMotion",
		];

		HashSet<string> declared = [.. Tokens.Descendants().Attributes(XNs + "Key").Select(attribute => attribute.Value)];

		foreach (string token in runtimeOwned)
		{
			_ = declared.Should().NotContain(token,
				$"'{token}' is written into Application.Resources from the live OS accessibility setting, and any "
				+ "declaration in this file shadows that write (the token dictionary is merged into Window.Resources, "
				+ "which is searched before the application dictionary)");

			_ = TokensText.Should().NotContain($"\"{token}\"",
				$"'{token}' must not be declared anywhere in the token file, including a theme dictionary");
		}

		// ...and the application must still own them, or removing the declarations would have
		// dropped the keys entirely rather than handing them to their real owner.
		_ = PickerAppText.Should().Contain("Resources[\"bc.os.reducedMotion\"] = settings.ReducedMotion;");
		_ = PickerAppText.Should().Contain("Resources[\"bc.motion.transitions.enabled\"]");
		_ = PickerAppText.Should().Contain("Resources[\"bc.motion.fast\"]");
		_ = PickerAppText.Should().Contain("Resources[\"bc.motion.normal\"]");
		_ = PickerAppText.Should().Contain("Resources[\"bc.motion.slow\"]");
	}

	[Fact]
	public void Application_activates_platform_theme_and_reduced_motion_contract()
	{
		_ = PickerAppText.Should().Contain("Resources[\"bc.os.reducedMotion\"] = settings.ReducedMotion;");
		_ = PickerAppText.Should().Contain("Resources[\"bc.motion.transitions.enabled\"]");
		_ = PickerAppText.Should().Contain("Resources[\"bc.motion.fast\"]");
		_ = PickerAppText.Should().Contain("Resources[\"bc.motion.normal\"]");
		_ = PickerAppText.Should().Contain("Resources[\"bc.motion.slow\"]");
		_ = AccessibilityText.Should().Contain("SystemParametersInfo").And.Contain("ContrastPreference").And.Contain("GetColorValues");
	}

	[Fact]
	public void Picker_focus_styles_consume_both_rings_without_replacing_button_semantics()
	{
		IEnumerable<XElement> focusStyles = Picker.Descendants(Ns + "Style")
			.Where(style => ((string?)style.Attribute("Selector"))?.Contains(":focus-visible", StringComparison.Ordinal) == true);

		_ = focusStyles.Should().NotBeEmpty();
		IEnumerable<XElement> focusSetters = focusStyles.SelectMany(style => style.Elements(Ns + "Setter"));
		bool hasOuter = focusSetters.Any(setter =>
			(string?)setter.Attribute("Property") == "BorderBrush"
			&& ((string?)setter.Attribute("Value") ?? string.Empty).Contains("bc.focus.outer", StringComparison.Ordinal));
		bool hasInner = focusSetters.Any(setter =>
			(string?)setter.Attribute("Property") == "Background"
			&& ((string?)setter.Attribute("Value") ?? string.Empty).Contains("bc.focus.inner", StringComparison.Ordinal));

		_ = hasOuter.Should().BeTrue();
		_ = hasInner.Should().BeTrue();

		_ = PickerText.Should().NotContain("IsTabStop=\"False\"");
	}

	private static XElement ThemeDictionary(string key)
	{
		return Tokens.Descendants(Ns + "ResourceDictionary")
			.Single(element => (string?)element.Attribute(XNs + "Key") == key);
	}

	// The High Contrast dictionary, found by its x:Static key. It cannot be looked up by a
	// plain string key because a custom ThemeVariant is not expressible as one - see the
	// architectural note in PickerThemeVariants.
	private static XElement ThemeDictionaryByStaticKey()
	{
		return Tokens.Descendants(Ns + "ResourceDictionary")
			.Single(element => ((string?)element.Attribute(XNs + "Key") ?? string.Empty)
				.Contains("PickerThemeVariants.HighContrast", StringComparison.Ordinal));
	}

	// The XAML element name that DECLARES a root-level token, which is its type in XAML.
	// "x:Double" reports as "Double", "CornerRadius" as "CornerRadius", and so on.
	private static string DeclaredElement(string key)
	{
		XElement element = Tokens.Root!
			.Elements()
			.Single(node => (string?)node.Attribute(XNs + "Key") == key);
		return element.Name.LocalName;
	}

	private static Dictionary<string, string> ThemeColorMap(string key)
	{
		return ThemeDictionary(key)
			.Elements(Ns + "Color")
			.ToDictionary(
				element => (string)element.Attribute(XNs + "Key")!,
				element => element.Value,
				StringComparer.Ordinal);
	}

	private static string ValueForRootKey(string key)
	{
		XElement element = Tokens.Root!
			.Elements()
			.Single(node => (string?)node.Attribute(XNs + "Key") == key);
		return element.Value;
	}

	private static double Contrast(string foreground, string background)
	{
		static double Channel(string hex, int offset)
		{
			double value = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
			return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
		}

		static double L(string hex)
		{
			return (0.2126 * Channel(hex, 1)) + (0.7152 * Channel(hex, 3)) + (0.0722 * Channel(hex, 5));
		}

		double a = L(foreground);
		double b = L(background);
		return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
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

	[GeneratedRegex("#[0-9A-Fa-f]{6,8}")]
	private static partial Regex HexRegex();

	[GeneratedRegex("(?:FontSize|Padding|Spacing|CornerRadius)=\"(?:1|3|6|10|11|14|17)\"")]
	private static partial Regex OffScaleRegex();
}
