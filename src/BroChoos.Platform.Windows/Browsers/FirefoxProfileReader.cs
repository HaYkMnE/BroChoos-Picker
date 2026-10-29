namespace BroChoos.Platform.Windows.Browsers;

internal sealed record FirefoxProfileReadResult(
	IReadOnlyList<string> ProfileIds,
	bool HasUnresolvedProfiles);

internal interface IFirefoxProfileFileSystem
{
	bool FileExists(string path);

	string ReadAllText(string path);
}

internal sealed class FirefoxProfileFileSystem : IFirefoxProfileFileSystem
{
	public bool FileExists(string path)
	{
		return File.Exists(path);
	}

	public string ReadAllText(string path)
	{
		return File.ReadAllText(path);
	}
}

internal sealed class FirefoxProfileReader
{
	private readonly IFirefoxProfileFileSystem _fileSystem;

	public FirefoxProfileReader()
		: this(new FirefoxProfileFileSystem())
	{
	}

	internal FirefoxProfileReader(IFirefoxProfileFileSystem fileSystem)
	{
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
	}

	public FirefoxProfileReadResult TryRead(string profilesIniPath)
	{
		if (string.IsNullOrWhiteSpace(profilesIniPath) || !_fileSystem.FileExists(profilesIniPath))
		{
			return new FirefoxProfileReadResult([], true);
		}

		try
		{
			string content = _fileSystem.ReadAllText(profilesIniPath);
			return Parse(content);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return new FirefoxProfileReadResult([], true);
		}
	}

	internal static FirefoxProfileReadResult Parse(string content)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return new FirefoxProfileReadResult([], true);
		}

		List<string> ids = [];
		HashSet<string> seen = new(StringComparer.Ordinal);
		bool unresolved = false;

		bool inProfileSection = false;
		string? name = null;
		string? path = null;
		string? isRelative = null;

		void FinalizeCurrentProfile()
		{
			if (!inProfileSection)
			{
				return;
			}

			if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || (isRelative != "0" && isRelative != "1"))
			{
				unresolved = true;
				return;
			}

			if (isRelative == "1")
			{
				if (Path.IsPathRooted(path)
					|| path.StartsWith("..", StringComparison.Ordinal)
					|| path.Contains("\\..\\", StringComparison.Ordinal)
					|| path.Contains("/../", StringComparison.Ordinal))
				{
					unresolved = true;
					return;
				}
			}
			else
			{
				if (!Path.IsPathRooted(path)
					|| path.StartsWith("\\\\", StringComparison.Ordinal)
					|| path.StartsWith("\\\\?\\", StringComparison.Ordinal)
					|| path.StartsWith("\\\\.\\", StringComparison.Ordinal))
				{
					unresolved = true;
					return;
				}
			}

			if (seen.Add(name))
			{
				ids.Add(name);
			}
		}

		foreach (string rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
		{
			string line = rawLine.Trim();
			if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
			{
				continue;
			}

			if (line.StartsWith('[') && line.EndsWith(']'))
			{
				FinalizeCurrentProfile();

				string sectionName = line[1..^1].Trim();
				inProfileSection = sectionName.StartsWith("Profile", StringComparison.OrdinalIgnoreCase);
				name = null;
				path = null;
				isRelative = null;
				continue;
			}

			int separator = line.IndexOf('=');
			if (separator <= 0)
			{
				if (inProfileSection)
				{
					unresolved = true;
				}
				continue;
			}

			if (!inProfileSection)
			{
				continue;
			}

			string key = line[..separator].Trim();
			string value = line[(separator + 1)..].Trim();

			if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
			{
				name = value;
			}
			else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
			{
				path = value;
			}
			else if (key.Equals("IsRelative", StringComparison.OrdinalIgnoreCase))
			{
				isRelative = value;
			}
		}

		FinalizeCurrentProfile();

		if (ids.Count == 0)
		{
			unresolved = true;
		}

		return new FirefoxProfileReadResult(ids, unresolved);
	}
}
