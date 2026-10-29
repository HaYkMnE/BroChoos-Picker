using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace BroChoos.Accessibility.Tests;

public sealed partial class LocalizationCoverageTests
{
	private static readonly string Root = FindRepositoryRoot();
	private static readonly string PickerWindowPath = Path.Combine(Root, "src", "BroChoos.App", "Picker", "PickerWindow.axaml");
	private static readonly string GhostToastPath = Path.Combine(Root, "src", "BroChoos.App", "Picker", "GhostToastWindow.axaml");
	private static readonly string CleanupToastPath = Path.Combine(Root, "src", "BroChoos.App", "Picker", "CleanupRecoveryToastWindow.axaml");
	private static readonly string PickerViewModelPath = Path.Combine(Root, "src", "BroChoos.App", "Picker", "PickerViewModel.cs");
	private static readonly string PickerAppPath = Path.Combine(Root, "src", "BroChoos.App", "Picker", "PickerApp.cs");

	private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

	[Fact]
	public void Localization_resource_files_exist_for_required_cultures()
	{
		string[] requiredPacks = ["en", "ru", "hy"];

		foreach (string pack in requiredPacks)
		{
			string path = Path.Combine(Root, "src", "BroChoos.App", "Localization", $"Strings.{pack}.axaml");
			_ = File.Exists(path).Should().BeTrue($"Localization resource file for '{pack}' must exist at {path}");
		}
	}

	[Fact]
	public void All_visible_picker_strings_use_resource_keys_not_hardcoded_english()
	{
		string pickerText = File.ReadAllText(PickerWindowPath);
		string ghostText = File.ReadAllText(GhostToastPath);
		string cleanupText = File.ReadAllText(CleanupToastPath);

		// These English strings should NOT appear as hardcoded Text="..." or Content="..." values
		// They should use DynamicResource or {x:Static} bindings to localization keys
		string[] forbiddenHardcoded =
		[
			"1-9 select", "Tab/arrows move", "Enter open", "Ctrl+C copy", "Ctrl+U use original once", "Ctrl+H skip history once", "Ctrl+Backspace clear history", "Esc cancel",
			"Opened",
			"Cleanup recovery", "Restore"
		];

		foreach (string forbidden in forbiddenHardcoded)
		{
			_ = pickerText.Should().NotContain($"Text=\"{forbidden}\"", $"PickerWindow must not hardcode '{forbidden}'");
			_ = ghostText.Should().NotContain($"Text=\"{forbidden}\"", $"GhostToastWindow must not hardcode '{forbidden}'");
			_ = cleanupText.Should().NotContain($"Text=\"{forbidden}\"", $"CleanupRecoveryToastWindow must not hardcode '{forbidden}'");
			_ = pickerText.Should().NotContain($"Content=\"{forbidden}\"", $"PickerWindow must not hardcode '{forbidden}' as Content");
			_ = ghostText.Should().NotContain($"Content=\"{forbidden}\"", $"GhostToastWindow must not hardcode '{forbidden}' as Content");
			_ = cleanupText.Should().NotContain($"Content=\"{forbidden}\"", $"CleanupRecoveryToastWindow must not hardcode '{forbidden}' as Content");
		}
	}

	[Fact]
	public void ViewModel_strings_exposed_via_localization_service_not_hardcoded()
	{
		// THE SCAN IS COMMENT-AWARE, deliberately. This assertion is about hardcoded UI
		// TEXT, and a comment is not UI text: W6.2 documents its admission decision in a
		// comment that quotes "Add manually", and the phrase also recurs in the success-path
		// comment. Reading the raw file made both comments indistinguishable from a string
		// literal, so the test failed while line 88 was correctly localizing the string
		// (LocalizationService.GetString("FirstRun.AddManually")). A guard that fires on
		// documentation is a false positive, and a false positive here is worse than no guard:
		// it pressures the next author to delete the explanation instead of keeping the code
		// honest. The literals inside real strings are still scanned - see StripComments.
		string viewModelText = StripComments(File.ReadAllText(PickerViewModelPath));

		string[] forbiddenInViewModel =
		[
			"Find browsers",
			"Add manually",
			"Skip",
			"Could not open",
			"Try another card",
			"F for system",
			"Ctrl+C to copy",
			"Esc to cancel",
			"URL copied to the clipboard",
			"Copy failed",
			"The URL is still visible",
			"Nothing has been scanned yet",
			"Choose Find browsers",
			"authorize discovery"
		];

		foreach (string forbidden in forbiddenInViewModel)
		{
			_ = viewModelText.Should().NotContain($"\"{forbidden}\"", $"PickerViewModel must not hardcode '{forbidden}'");
		}
	}

	// Removes // line comments and /* ... */ block comments while walking the text
	// CHARACTER BY CHARACTER, so a comment marker that sits inside a string literal is NOT
	// treated as the start of a comment. Line comments end at a newline; the newline itself
	// is kept so line structure survives. Block comments are replaced by a single space,
	// which cannot fuse two adjacent tokens into a new false match.
	// This is a scanner for source text, not a C# parser: it assumes no raw string
	// literals ("""...""") or verbatim strings (@"..."), which is true of the file it reads.
	private static string StripComments(string source)
	{
		System.Text.StringBuilder result = new(source.Length);
		bool inString = false;
		bool inChar = false;
		bool escaped = false;

		for (int i = 0; i < source.Length; i++)
		{
			char current = source[i];
			char next = i + 1 < source.Length ? source[i + 1] : '\0';

			if (inString || inChar)
			{
				_ = result.Append(current);
				if (escaped)
				{
					escaped = false;
				}
				else if (current == '\\')
				{
					escaped = true;
				}
				else if (inString && current == '"')
				{
					inString = false;
				}
				else if (inChar && current == '\'')
				{
					inChar = false;
				}

				continue;
			}

			if (current == '"')
			{
				inString = true;
				_ = result.Append(current);
			}
			else if (current == '\'')
			{
				inChar = true;
				_ = result.Append(current);
			}
			else if (current == '/' && next == '/')
			{
				while (i < source.Length && source[i] != '\n')
				{
					i++;
				}

				if (i < source.Length)
				{
					_ = result.Append(source[i]);
				}
			}
			else if (current == '/' && next == '*')
			{
				i += 2;
				while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
				{
					i++;
				}

				i++;
				_ = result.Append(' ');
			}
			else
			{
				_ = result.Append(current);
			}
		}

		return result.ToString();
	}

	[Fact]
	public void Localization_service_exists_and_exposes_get_string()
	{
		string servicePath = Path.Combine(Root, "src", "BroChoos.App", "Localization", "LocalizationService.cs");
		_ = File.Exists(servicePath).Should().BeTrue("LocalizationService.cs must exist");

		string serviceText = File.ReadAllText(servicePath);
		_ = serviceText.Should().Contain("GetString", "LocalizationService must expose a GetString method");
		_ = serviceText.Should().Contain("CurrentCulture", "LocalizationService must expose CurrentCulture property");
		_ = serviceText.Should().Contain("FallbackCulture", "LocalizationService must support fallback culture");
	}

	[Fact]
	public void Picker_and_toasts_do_not_hardwire_english_pack_and_use_runtime_localization_source()
	{
		string pickerText = File.ReadAllText(PickerWindowPath);
		string ghostText = File.ReadAllText(GhostToastPath);
		string cleanupText = File.ReadAllText(CleanupToastPath);
		string pickerAppText = File.ReadAllText(PickerAppPath);

		_ = pickerText.Should().NotContain("Strings.en.axaml", "Picker window must not hardwire English resource pack");
		_ = ghostText.Should().NotContain("Strings.en.axaml", "Ghost toast must not hardwire English resource pack");
		_ = cleanupText.Should().NotContain("Strings.en.axaml", "Cleanup toast must not hardwire English resource pack");

		_ = pickerAppText.Should().Contain("LocalizationService.GetString", "PickerApp should inject UI strings from runtime culture-driven localization");
		_ = pickerAppText.Should().Contain("picker.select.hints", "PickerApp should provide localized key hints string");
		_ = pickerAppText.Should().Contain("GhostToast.Opened", "PickerApp should provide localized ghost toast string");
		_ = pickerAppText.Should().Contain("CleanupToast.Title", "PickerApp should provide localized cleanup title string");
		_ = pickerAppText.Should().Contain("CleanupToast.RestoreButton", "PickerApp should provide localized restore button string");
	}

	[Fact]
	public void Resource_keys_have_parity_across_all_shipped_packs()
	{
		string enPath = Path.Combine(Root, "src", "BroChoos.App", "Localization", "Strings.en.axaml");
		string ruPath = Path.Combine(Root, "src", "BroChoos.App", "Localization", "Strings.ru.axaml");
		string hyPath = Path.Combine(Root, "src", "BroChoos.App", "Localization", "Strings.hy.axaml");

		_ = File.Exists(enPath).Should().BeTrue("English resource file must exist");
		_ = File.Exists(ruPath).Should().BeTrue("Russian resource file must exist");
		_ = File.Exists(hyPath).Should().BeTrue("Armenian resource file must exist");

		HashSet<string> enKeys = ExtractResourceKeys(enPath);
		HashSet<string> ruKeys = ExtractResourceKeys(ruPath);
		HashSet<string> hyKeys = ExtractResourceKeys(hyPath);

		// All three packs must have exactly the same keys for shipped content
		// Set membership, not enumeration order: only missing or extra keys fail.
		_ = ruKeys.Should().BeEquivalentTo(enKeys, "Russian pack must have identical keys to English");
		_ = hyKeys.Should().BeEquivalentTo(enKeys, "Armenian pack must have identical keys to English");
	}

	private static HashSet<string> ExtractResourceKeys(string path)
	{
		XDocument doc = XDocument.Parse(File.ReadAllText(path));
		return [.. doc.Descendants().Attributes(XNs + "Key").Select(a => a.Value)];
	}

	// W7.2: the ranking reason vocabulary is user-visible text, so it must exist in EVERY
	// shipped pack, not only English. The general parity test above compares the packs to
	// EACH OTHER; this one names the vocabulary explicitly so a pack that dropped a reason
	// key fails by name rather than merely staying internally consistent.
	[Fact]
	public void Ranking_reason_vocabulary_is_present_in_every_shipped_pack()
	{
		string[] reasonKeys =
		[
			"Suggestion.Reason.MatchedRule",
			"Suggestion.Reason.MatchedRuleGeneric",
			"Suggestion.Reason.RecentSession",
			"Suggestion.Reason.ProbableSource",
			"Suggestion.Reason.AllChoices",
		];

		foreach (string pack in new[] { "en", "ru", "hy" })
		{
			string path = Path.Combine(Root, "src", "BroChoos.App", "Localization", $"Strings.{pack}.axaml");
			HashSet<string> keys = ExtractResourceKeys(path);

			foreach (string key in reasonKeys)
			{
				_ = keys.Should().Contain(key, $"the ranking reason vocabulary must be localizable in '{pack}'");
			}
		}
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
