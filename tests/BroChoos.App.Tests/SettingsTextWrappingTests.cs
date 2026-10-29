using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BroChoos.App.Settings;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// Every TextBlock in SettingsWindow sits in a ScrollViewer with horizontal scrolling disabled, so a
// line longer than the content column is simply cut off. ru and hy run about 1.5x the English
// width, and at 200% text scale even English overflows the 960 px minimum window. Rule: a TextBlock
// whose bound text reaches 40 * 14 / FontSize characters in any shipped language must wrap, and the
// wrap must sit where it has a finite width (not a horizontal StackPanel, not an Auto Grid column).
//
// Static rather than rendered: Avalonia.Headless is not referenced by this repo (see
// VisualSnapshotTests), and a measured check would depend on installed fonts and DPI. This test
// reads the real .axaml and resolves each binding against a real view model in en, ru and hy,
// including the status texts that only appear after a search, a save or a confirmed action.
[Collection(ConsoleCaptureCollection.Name)]
public sealed partial class SettingsTextWrappingTests
{
	private const int MaxUnwrappedLength = 40;

	private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

	[Fact]
	public void SettingsWindow_EveryLongLocalizedTextBlock_Wraps()
	{
		XDocument document = XDocument.Load(SettingsWindowPath());
		FontSizes fonts = new(document, LoadTokens());
		List<XElement> textBlocks = [.. document.Descendants()
			.Where(e => e.Name == Avalonia + "TextBlock" || e.Name == Avalonia + "SelectableTextBlock" || e.Name == Avalonia + "Label")];
		_ = textBlocks.Should().NotBeEmpty();

		Dictionary<string, int> longest = [];
		foreach (string culture in new[] { "en", "ru", "hy" })
		{
			foreach (SettingsViewModel vm in ViewModelsInEveryTextState(CultureInfo.GetCultureInfo(culture)))
			{
				foreach (XElement textBlock in textBlocks)
				{
					string path = BindingPath(textBlock);
					int length = Resolve(vm, path).Length;
					longest[path] = Math.Max(longest.GetValueOrDefault(path), length);
				}
			}
		}

		// The character budget shrinks as the font grows: 40 characters at 14 px, 23 at a 24 px heading.
		// A text whose length reaches the budget must wrap (>=), so a heading exactly at the limit counts.
		List<string> unwrapped = [.. textBlocks
			.Where(tb => longest[BindingPath(tb)] >= Threshold(fonts.Resolve(tb))
				&& (string?)tb.Attribute("TextWrapping") != "Wrap")
			.Select(tb => $"{BindingPath(tb)} (up to {longest[BindingPath(tb)]} chars, limit {Threshold(fonts.Resolve(tb))})")];

		// Wrapping needs a finite width. A horizontal StackPanel, and a Grid column sized Auto, both
		// measure their child with infinite width, so TextWrapping="Wrap" there never wraps.
		List<string> wrapDefeated = [.. textBlocks
			.Where(tb => (string?)tb.Attribute("TextWrapping") == "Wrap")
			.Select(tb => (Path: BindingPath(tb), Reason: WrapDefeatedBy(tb)))
			.Where(x => x.Reason is not null)
			.Select(x => $"{x.Path} ({x.Reason})")];

		_ = unwrapped.Should().BeEmpty("a long localized line is cut off unless it wraps");
		_ = wrapDefeated.Should().BeEmpty("wrapping has no effect without a finite width");
	}

	private const double BaseFontSize = 14;

	private static int Threshold(double fontSize)
	{
		return (int)Math.Floor(MaxUnwrappedLength * BaseFontSize / fontSize);
	}

	// Walks up from the TextBlock and names the first container that gives it unbounded width.
	private static string? WrapDefeatedBy(XElement textBlock)
	{
		XElement child = textBlock;
		foreach (XElement ancestor in textBlock.Ancestors())
		{
			if (ancestor.Name == Avalonia + "StackPanel" && (string?)ancestor.Attribute("Orientation") == "Horizontal")
			{
				return "inside a horizontal StackPanel";
			}

			if (ancestor.Name == Avalonia + "Grid")
			{
				int column = int.Parse((string?)child.Attribute("Grid.Column") ?? "0", CultureInfo.InvariantCulture);
				int span = int.Parse((string?)child.Attribute("Grid.ColumnSpan") ?? "1", CultureInfo.InvariantCulture);
				List<string> widths = ColumnWidths(ancestor);
				if (widths.Count > 0)
				{
					_ = (column + span).Should().BeLessThanOrEqualTo(widths.Count, $"Grid.Column {column} must exist in the Grid of '{BindingPath(textBlock)}'");
					// A span that includes a star column gets a finite width from it, so Auto plus * is
					// allowed. Only an Auto cell (or span) with no star column leaves the width unbounded.
					List<string> spanned = [.. widths.Skip(column).Take(span)];
					if (spanned.Any(w => w.Equals("Auto", StringComparison.OrdinalIgnoreCase))
						&& !spanned.Any(w => w.EndsWith('*')))
					{
						return span == 1
							? $"in Grid column {column} sized Auto"
							: $"in Grid columns {column}..{column + span - 1} sized Auto with no * column";
					}
				}
			}

			child = ancestor;
		}

		return null;
	}

	// ColumnDefinitions in either form: the attribute shorthand or <Grid.ColumnDefinitions> children.
	// No definitions means one star column, which bounds the width.
	private static List<string> ColumnWidths(XElement grid)
	{
		string? shorthand = (string?)grid.Attribute("ColumnDefinitions");
		if (shorthand is not null)
		{
			return [.. shorthand.Split(',', StringSplitOptions.TrimEntries)];
		}

		XElement? element = grid.Element(Avalonia + "Grid.ColumnDefinitions");
		return element is null
			? []
			: [.. element.Elements(Avalonia + "ColumnDefinition").Select(c => (string?)c.Attribute("Width") ?? "*")];
	}

	// The effective FontSize of a text element, resolved the way Avalonia would for this window:
	// local value, then a Style setter in this file whose selector matches the element's type and one
	// of its classes, then the nearest ancestor with a local FontSize (FontSize inherits), then the
	// Fluent Window theme default. A resource reference resolves through PickerTokens.axaml. Anything
	// the test cannot resolve FAILS it; there is no silent fallback to a guessed size.
	private sealed class FontSizes(XDocument document, Dictionary<string, double> tokens)
	{
		// Avalonia.Themes.Fluent 11.3 (Accents/BaseResources.xaml): ControlContentThemeFontSize = 14,
		// applied by the Window ControlTheme (Controls/Window.xaml). It is the only key resolved
		// outside this repo, and only for the window itself.
		private const double FluentWindowFontSize = 14;

		private readonly XDocument _document = Guarded(document);
		private readonly Dictionary<string, double> _tokens = tokens;

		// Fail closed on the two forms this resolver does not model: a FontSize setter in a Style whose
		// selector is anything but exactly "Type.class" (nested, pseudo-class, descendant, bare type),
		// and FontSize set as a property element (<TextBlock.FontSize>). Either would change sizes the
		// test reads, so the test refuses to guess.
		private static XDocument Guarded(XDocument document)
		{
			List<string> unmodelledSelectors = [.. document.Descendants(Avalonia + "Style")
				.Where(s => s.Elements(Avalonia + "Setter").Any(setter => (string?)setter.Attribute("Property") == "FontSize"))
				.Select(s => (string?)s.Attribute("Selector") ?? "<no selector>")
				.Where(selector => !TypeClassSelectorPattern().IsMatch(selector))];
			_ = unmodelledSelectors.Should().BeEmpty("a FontSize Style setter must use an exact 'Type.class' selector so the test can resolve it");

			List<string> propertyElements = [.. document.Descendants()
				.Where(e => e.Name.LocalName.EndsWith(".FontSize", StringComparison.Ordinal))
				.Select(e => e.Name.LocalName)];
			_ = propertyElements.Should().BeEmpty("FontSize must be set as an attribute or a Type.class Style setter, not as a property element");

			return document;
		}

		public double Resolve(XElement element)
		{
			string? local = (string?)element.Attribute("FontSize");
			if (local is not null)
			{
				return Value(local, element);
			}

			string? styled = StyleSetter(element);
			if (styled is not null)
			{
				return Value(styled, element);
			}

			foreach (XElement ancestor in element.Ancestors())
			{
				string? inherited = (string?)ancestor.Attribute("FontSize");
				if (inherited is not null)
				{
					return Value(inherited, element);
				}
			}

			return FluentWindowFontSize;
		}

		private string? StyleSetter(XElement element)
		{
			string[] classes = ((string?)element.Attribute("Classes") ?? string.Empty)
				.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			List<string> values = [.. _document.Descendants(Avalonia + "Style")
				.Where(s => classes.Any(c => (string?)s.Attribute("Selector") == $"{element.Name.LocalName}.{c}"))
				.SelectMany(s => s.Elements(Avalonia + "Setter"))
				.Where(s => (string?)s.Attribute("Property") == "FontSize")
				.Select(s => (string?)s.Attribute("Value") ?? string.Empty)];

			// Every class style is read so an unresolvable one fails even when another class wins.
			foreach (string value in values)
			{
				_ = Value(value, element);
			}

			return values.LastOrDefault();
		}

		private double Value(string raw, XElement element)
		{
			if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double literal))
			{
				return literal;
			}

			Match reference = ResourcePattern().Match(raw);
			_ = reference.Success.Should().BeTrue($"FontSize \"{raw}\" on '{BindingPath(element)}' must be a number or a resource reference");
			string key = reference.Groups[1].Value;
			_ = _tokens.Should().ContainKey(key, $"FontSize resource '{key}' on '{BindingPath(element)}' must be defined in PickerTokens.axaml");
			return _tokens[key];
		}
	}

	// Every x:Double in PickerTokens.axaml, the dictionary SettingsWindow merges.
	private static Dictionary<string, double> LoadTokens()
	{
		XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
		string path = Path.Combine(RepositoryRoot(), "src", "BroChoos.App", "Picker", "PickerTokens.axaml");
		return XDocument.Load(path).Descendants(x + "Double")
			.Where(d => d.Attribute(x + "Key") is not null)
			.GroupBy(d => (string)d.Attribute(x + "Key")!)
			.ToDictionary(g => g.Key, g => double.Parse(g.First().Value, NumberStyles.Float, CultureInfo.InvariantCulture));
	}

	// One view model per text state, so every status line is measured with real content.
	private static IEnumerable<SettingsViewModel> ViewModelsInEveryTextState(CultureInfo culture)
	{
		yield return new SettingsViewModel(culture: culture);

		// Search with no match, and an unsaved control depth / picker layout (no store composed).
		SettingsViewModel statuses = new(culture: culture);
		_ = statuses.Search("zz-no-such-section-zz");
		statuses.SelectedControlDepthIndex = 2;
		statuses.SelectedPickerLayoutIndex = 1;
		yield return statuses;

		// A pending confirmation, then the result of a confirmed action.
		SettingsViewModel pending = new(culture: culture);
		_ = pending.RequestConsequentialAction(SettingsViewModel.ConsequentialActionIds.ExportSettings);
		yield return pending;

		SettingsViewModel confirmed = new(culture: culture);
		_ = confirmed.RequestConsequentialAction(SettingsViewModel.ConsequentialActionIds.ExportSettings);
		confirmed.ConfirmConsequentialActionCommand.Execute(null);
		yield return confirmed;

		// Every section heading and description.
		foreach (string sectionId in SettingsViewModel.OrderedSections.Select(s => s.SectionId))
		{
			SettingsViewModel section = new(culture: culture);
			_ = section.SelectSection(sectionId);
			yield return section;
		}
	}

	private static string BindingPath(XElement textBlock)
	{
		string text = (string?)textBlock.Attribute("Text") ?? (string?)textBlock.Attribute("Content") ?? string.Empty;
		Match match = BindingPattern().Match(text);
		_ = match.Success.Should().BeTrue($"every Settings TextBlock is expected to bind its text, found Text=\"{text}\"");
		return match.Groups[1].Value;
	}

	// Resolves a dotted binding path. A path that does not exist fails, so a typo cannot pass silently.
	private static string Resolve(object source, string path)
	{
		object? current = source;
		foreach (string segment in path.Split('.'))
		{
			PropertyInfo? property = current?.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
			_ = property.Should().NotBeNull($"binding path '{path}' must exist on the view model");
			current = property!.GetValue(current);
		}

		return current as string ?? string.Empty;
	}

	private static string SettingsWindowPath()
	{
		return Path.Combine(RepositoryRoot(), "src", "BroChoos.App", "Settings", "SettingsWindow.axaml");
	}

	private static string RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "BroChoos.sln")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new InvalidOperationException("BroChoos.sln not found above " + AppContext.BaseDirectory);
	}

	[GeneratedRegex(@"^\{Binding ([\w.]+)\}$")]
	private static partial Regex BindingPattern();

	[GeneratedRegex(@"^\{(?:DynamicResource|StaticResource) ([\w.]+)\}$")]
	private static partial Regex ResourcePattern();

	[GeneratedRegex(@"^\w+\.[\w-]+$")]
	private static partial Regex TypeClassSelectorPattern();
}
