using System.Text;

namespace BroChoos.Core.Privacy;

// One cleaning rule for browser profile display names (Chromium info_cache name, Firefox
// profiles.ini Name=) before they reach a tile. These names are free text the user or another
// program wrote, so they are made safe to draw: control characters and bidi overrides (which can
// flip how the rest of the line reads) are removed, whitespace is trimmed, and the result is cut
// to MaxLength without splitting a surrogate pair. Personal data: this helper never logs.
public static class ProfileNameSanitizer
{
	public const int MaxLength = 64;

	public static string Sanitize(string? name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return string.Empty;
		}

		StringBuilder kept = new(name.Length);
		foreach (char c in name)
		{
			if (!char.IsControl(c) && !IsBidiControl(c))
			{
				_ = kept.Append(c);
			}
		}

		string text = kept.ToString().Trim();
		if (text.Length <= MaxLength)
		{
			return text;
		}

		int cut = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
		return text[..cut].TrimEnd();
	}

	// U+061C ALM, U+200E/U+200F LRM/RLM, U+202A-U+202E embeddings/overrides, U+2066-U+2069 isolates,
	// and invisible separators/spaces: U+200B ZWSP, U+2060 WJ, U+FEFF BOM, U+2028/U+2029 line and
	// paragraph separators. ZWJ/ZWNJ (U+200D/U+200C) are KEPT: they shape emoji and many scripts.
	private static bool IsBidiControl(char c)
	{
		return c is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')
			or '\u200B' or '\u2060' or '\uFEFF' or '\u2028' or '\u2029';
	}
}
