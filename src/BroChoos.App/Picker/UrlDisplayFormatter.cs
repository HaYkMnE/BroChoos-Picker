namespace BroChoos.App.Picker;

// The link card content (D-125 zone 1): the host plus a short readable URL with
// long query strings collapsed by default, so the card stays readable at a fixed
// window width. The full URL is always one Ctrl+C away (the clipboard is also
// read once at open to recover a copied URL, but that read never feeds this card).
internal sealed record LinkCardDisplay(string Host, string ShortUrl, bool IsCollapsed);

internal static class UrlDisplayFormatter
{
	private const int MaxShortLength = 64;

	public static LinkCardDisplay Format(string absoluteUrl)
	{
		// The URL reaching the picker is already validated and normalized by
		// CliParser through IUrlValidator (absolute http/https); the TryCreate here
		// is defensive only.
		if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out Uri? uri))
		{
			return new LinkCardDisplay(absoluteUrl, absoluteUrl, IsCollapsed: false);
		}

		string baseForm = uri.GetLeftPart(UriPartial.Path);
		string query = uri.Query;

		string full = string.Concat(baseForm, query);
		if (full.Length <= MaxShortLength)
		{
			return new LinkCardDisplay(uri.Host, full, IsCollapsed: false);
		}

		if (baseForm.Length <= MaxShortLength && query.Length > 0)
		{
			// Only the query was long: collapse it (D-125).
			return new LinkCardDisplay(uri.Host, string.Concat(baseForm, "?…"), IsCollapsed: true);
		}

		// Even the bare path is too long: truncate it; any query counts as collapsed.
		return new LinkCardDisplay(uri.Host, string.Concat(baseForm.AsSpan(0, MaxShortLength - 1), "…"), IsCollapsed: true);
	}
}
