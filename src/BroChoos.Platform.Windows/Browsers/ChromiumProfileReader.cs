using System.Text.Json;

namespace BroChoos.Platform.Windows.Browsers;

internal sealed record ChromiumProfileReadResult(
	IReadOnlyList<string> ProfileIds,
	bool HasUnresolvedProfiles)
{
	// profile.info_cache.<dir>.name, index-aligned with ProfileIds ("" when absent). The
	// user-visible profile name - personal data; this reader holds no logger on purpose.
	public IReadOnlyList<string> ProfileNames { get; init; } = [];
}

internal interface IChromiumProfileFileSystem
{
	bool FileExists(string path);

	byte[] ReadAllBytes(string path);
}

internal sealed class ChromiumProfileFileSystem : IChromiumProfileFileSystem
{
	public bool FileExists(string path)
	{
		return File.Exists(path);
	}

	public byte[] ReadAllBytes(string path)
	{
		return File.ReadAllBytes(path);
	}
}

internal sealed class ChromiumProfileReader
{
	private const int MaxLocalStateBytes = 2 * 1024 * 1024;

	private readonly IChromiumProfileFileSystem _fileSystem;

	public ChromiumProfileReader()
		: this(new ChromiumProfileFileSystem())
	{
	}

	internal ChromiumProfileReader(IChromiumProfileFileSystem fileSystem)
	{
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
	}

	public ChromiumProfileReadResult TryRead(string localStatePath)
	{
		if (string.IsNullOrWhiteSpace(localStatePath) || !_fileSystem.FileExists(localStatePath))
		{
			return new ChromiumProfileReadResult([], true);
		}

		try
		{
			byte[] payload = _fileSystem.ReadAllBytes(localStatePath);
			if (payload.Length is 0 or > MaxLocalStateBytes)
			{
				return new ChromiumProfileReadResult([], true);
			}

			using JsonDocument json = JsonDocument.Parse(payload);
			// A non-object root or "profile" (array, string, number, null...) is a malformed
			// shape: TryGetProperty would throw InvalidOperationException, which is not caught
			// below and would interrupt detection.
			if (json.RootElement.ValueKind != JsonValueKind.Object
				|| !json.RootElement.TryGetProperty("profile", out JsonElement profile)
				|| profile.ValueKind != JsonValueKind.Object
				|| !profile.TryGetProperty("info_cache", out JsonElement infoCache)
				|| infoCache.ValueKind != JsonValueKind.Object)
			{
				return new ChromiumProfileReadResult([], false);
			}

			List<string> ids = [];
			List<string> names = [];
			HashSet<string> dedupe = new(StringComparer.Ordinal);
			foreach (JsonProperty property in infoCache.EnumerateObject())
			{
				if (!string.IsNullOrWhiteSpace(property.Name) && dedupe.Add(property.Name))
				{
					ids.Add(property.Name);
					names.Add(ReadProfileName(property.Value));
				}
			}

			// The launcher uses the FIRST id. Chromium records the profile the user last used
			// as "profile.last_used" (pref kProfileLastUsed, "Directory of the last profile
			// used", chrome/common/pref_names.h); it is moved to the front so a launch opens
			// that profile rather than whichever info_cache key happens to be first. Only an
			// exact (ordinal) match against a listed profile counts: a stale or malformed
			// value is ignored, never turned into an id.
			if (profile.TryGetProperty("last_used", out JsonElement lastUsed)
				&& lastUsed.ValueKind == JsonValueKind.String
				&& lastUsed.GetString() is { Length: > 0 } lastUsedId)
			{
				int index = ids.IndexOf(lastUsedId);
				if (index > 0)
				{
					string lastUsedName = names[index];
					ids.RemoveAt(index);
					names.RemoveAt(index);
					ids.Insert(0, lastUsedId);
					names.Insert(0, lastUsedName);
				}
			}

			return new ChromiumProfileReadResult(ids, false) { ProfileNames = names };
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
		{
			return new ChromiumProfileReadResult([], true);
		}
	}

	private static string ReadProfileName(JsonElement entry)
	{
		return entry.ValueKind != JsonValueKind.Object
			|| !entry.TryGetProperty("name", out JsonElement name)
			|| name.ValueKind != JsonValueKind.String
			|| name.GetString() is not { } text
			? string.Empty
			: Core.Privacy.ProfileNameSanitizer.Sanitize(text);
	}
}
