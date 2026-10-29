using System.Text.Json;

namespace BroChoos.App.Settings;

// W8.1 — the search-alias table the Settings shell searches over.
//
// WHY THIS EXISTS AT ALL. The plan's W8.1 minimal behavior asks for a SEARCHABLE,
// localized information architecture and names `search-aliases.json` as a file. The file
// alone is not search: something has to load it and answer a query with a section. Before
// this type, the JSON shipped in the source tree but was never read by the product (it was
// not even copied to the output directory), so the search box the window renders could not
// have searched anything.
//
// FAIL OPEN. Aliases are a convenience over the frozen section list, never the source of
// truth for what a section IS: if the resource is missing or malformed the shell still
// works and search simply falls back to the section labels and ids. A broken alias table
// must never block Settings from opening (same fail-open direction as ISettingsStore).
internal static class SettingsSearchAliases
{
	// The logical resource name declared by BroChoos.App.csproj's EmbeddedResource item.
	private const string ResourceName = "BroChoos.App.Settings.search-aliases.json";

	private static readonly Lazy<IReadOnlyDictionary<string, string>> Aliases = new(Load, isThreadSafe: true);

	// alias (lower-cased) -> section id. The lookup is case-insensitive to match the way a
	// user types; the file itself is the authority on which aliases exist.
	public static IReadOnlyDictionary<string, string> Table => Aliases.Value;

	// Resolves a raw search query to a section id, or null when nothing matches.
	//
	// Matching is deliberately narrow and honest:
	//   1. an exact alias hit (case-insensitive), then
	//   2. a section id or localized label CONTAINING the query (case-insensitive).
	// It never returns a section "because it is closest": an unmatched query answers null,
	// so the shell can say plainly that nothing matched instead of jumping somewhere.
	public static string? Resolve(string? query, IReadOnlyList<SettingsSection> sections)
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			return null;
		}

		string normalized = query.Trim();

		if (Table.TryGetValue(normalized, out string? aliasTarget))
		{
			return aliasTarget;
		}

		foreach (SettingsSection section in sections)
		{
			if (section.SectionId.Contains(normalized, StringComparison.OrdinalIgnoreCase)
				|| section.DisplayLabel.Contains(normalized, StringComparison.OrdinalIgnoreCase)
				|| section.GroupLabel.Contains(normalized, StringComparison.OrdinalIgnoreCase))
			{
				return section.SectionId;
			}
		}

		return null;
	}

	private static Dictionary<string, string> Load()
	{
		try
		{
			using Stream? stream = typeof(SettingsSearchAliases).Assembly.GetManifestResourceStream(ResourceName);
			if (stream is null)
			{
				return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			}

			using StreamReader reader = new(stream);
			Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());

			return parsed is null
				? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				: new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
		}
		catch (JsonException)
		{
			// Malformed alias table: search still works over labels/ids. Never throw here -
			// this runs while the settings window is being constructed.
			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}
		catch (IOException)
		{
			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}
	}
}
