using Microsoft.Win32;

namespace BroChoos.Platform.Windows.Registration;

// One observed registry value. Name is "" for the key's default (unnamed) value.
// Kind is a coarse hand-readable classifier for backup JSON; Data holds the full
// string for REG_SZ and an invariant number for DWORD/QWORD, and is null for
// kinds the milestone never writes (multi/binary/unknown stay
// restore-by-hand-with-regedit material).
public sealed record RegistryValueState(string Name, string Kind, string? Data);

// One observed registry key. ReadFailed is true when the key could not be opened
// even read-only (permissions): Exists=false then means "not observable", which
// the snapshot marks explicitly so a backup never pretends to fidelity it does
// not have.
public sealed record RegistryKeyState(bool Exists, bool ReadFailed, IReadOnlyList<RegistryValueState> Values, IReadOnlyList<string> SubKeyNames)
{
	internal static RegistryKeyState Missing { get; } = new(false, false, [], []);
}

// HKCU-relative registry seam for capability declaration (D-273: per-user only,
// no HKLM anywhere). All paths are relative to HKEY_CURRENT_USER.
//
// THE HARD INVARIANT (D-187): every WRITE member of every implementation must
// refuse a path containing a UserChoice segment — BroChoos never writes where
// Windows stores the user's own default-app pick. Every DELETE member additionally
// refuses any path outside the BroChoos-owned registration roots: a segment check
// alone misses a tree delete aimed at an ANCESTOR of a UserChoice key. ReadKey
// deliberately does NOT guard: the status service legitimately READS the current
// UserChoice ProgId as a snapshot (D-268 step 2). Writes and deletes are the
// forbidden verbs.
public interface IRegistryCapabilityStore
{
	RegistryKeyState ReadKey(string path);

	void SetValue(string path, string? name, string data);

	// True when the value existed and was deleted. A missing key is "false",
	// not an error.
	bool DeleteValue(string path, string name);

	// True when the key existed and was deleted.
	bool DeleteSubKeyTree(string path);

	// True only when the key existed, had no subkeys, no named values and no
	// default value, and was then deleted. Used for shared keys (the scheme
	// keys) where foreign content must survive (D-299: only BroChoos-owned
	// pieces are removed).
	bool DeleteSubKeyIfEmpty(string path);
}

// The D-187 hard invariant as a single choke point. A match is ANY path segment
// equal to "UserChoice", case-insensitive: HKCU\Software\Microsoft\Windows\
// Shell\Associations\UrlAssociations\http\UserChoice is the classic URL target,
// FileExts\.url\UserChoice its file-extension sibling, and no future sibling is
// allowed to slip through a narrower rule.
public static class UserChoiceWriteGuard
{
	public static void ThrowIfUserChoicePath(string path)
	{
		ArgumentNullException.ThrowIfNull(path);

		foreach (string segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
		{
			if (segment.Equals("UserChoice", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					"Writes and deletes under a UserChoice key are forbidden (D-187): the default is the user's own pick, made in Windows Settings.");
			}
		}
	}

	// The delete-side allowlist. DESIGN DEFENSE (allowlist over ancestor-prefix
	// detection): a segment check alone misses the destructive case — a
	// DeleteSubKeyTree on an ANCESTOR of a UserChoice key (say
	// ...\UrlAssociations\http) contains no "UserChoice" segment yet would wipe
	// the user's own pick underneath. Enumerating every ancestor of every current
	// AND future UserChoice location is a fragile reverse-lookup that must be
	// maintained in lockstep with Windows internals, so the destructive verbs are
	// instead restricted to the fixed product-owned registration roots below —
	// exactly the set the D-299 removal plan is allowed to touch and nothing
	// else. Writes keep the segment rule only: registration legitimately creates
	// brand-new keys under shared roots, and every declared write path is a
	// compile-time constant anyway.
	private static readonly string[] ProductOwnedDeleteRoots =
	[
		@"Software\Classes\http",
		@"Software\Classes\https",
		@"Software\Classes\.url",
		@"Software\Classes\BroChoosURL",
		@"Software\Classes\BroChoosURL.File",
		@"Software\Clients\StartMenuInternet\BroChoos",
		@"Software\RegisteredApplications",
	];

	// Throws unless the path lies AT one of the product-owned delete roots or
	// strictly UNDERNEATH it. Runs after ThrowIfUserChoicePath in every delete
	// member so a genuine UserChoice path keeps its precise D-187 message.
	public static void ThrowIfDeletePathIsNotProductOwned(string path)
	{
		ArgumentNullException.ThrowIfNull(path);

		foreach (string root in ProductOwnedDeleteRoots)
		{
			// Registry key names are case-insensitive; equality covers the root
			// itself, the prefix check covers anything strictly underneath.
			if (path.Equals(root, StringComparison.OrdinalIgnoreCase)
				|| path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}

		throw new InvalidOperationException(
			"Registry deletes are allowlisted to the BroChoos-owned registration roots (D-187/D-299): a delete outside them is refused before any hive access.");
	}
}

// Real HKCU implementation. The guards (UserChoice segment, then the delete
// allowlist) run as the FIRST statements of every write/delete member so no
// refactoring can route around them. Reads never throw for a missing hive/key;
// recoverable access failures degrade to ReadFailed instead of taking the status
// report or the backup pass down.
public sealed class HkcuRegistryCapabilityStore : IRegistryCapabilityStore
{
	public RegistryKeyState ReadKey(string path)
	{
		ArgumentNullException.ThrowIfNull(path);

		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: false);
			if (key is null)
			{
				return RegistryKeyState.Missing;
			}

			List<RegistryValueState> values = [];

			foreach (string name in key.GetValueNames())
			{
				object? data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
				values.Add(new RegistryValueState(name, ClassifyKind(data), RenderData(data)));
			}

			// GetValueNames omits the default (unnamed) value; probe it explicitly.
			object? defaultData = key.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
			if (defaultData is not null)
			{
				values.Add(new RegistryValueState(string.Empty, ClassifyKind(defaultData), RenderData(defaultData)));
			}

			return new RegistryKeyState(true, false, values, key.GetSubKeyNames());
		}
		catch (Exception ex) when (IsRecoverableRegistryFailure(ex))
		{
			return new RegistryKeyState(false, true, [], []);
		}
	}

	public void SetValue(string path, string? name, string data)
	{
		UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
		ArgumentException.ThrowIfNullOrEmpty(path);

		using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, writable: true)
			?? throw new IOException("The registry refused to create or open a capability key (HKCU-relative path withheld from logs by policy).");

		key.SetValue(name ?? string.Empty, data, RegistryValueKind.String);
	}

	public bool DeleteValue(string path, string name)
	{
		UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
		UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(name);

		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: true);
		if (key is null)
		{
			return false;
		}

		object? existing = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
		if (existing is null)
		{
			return false;
		}

		key.DeleteValue(name, throwOnMissingValue: false);
		return true;
	}

	public bool DeleteSubKeyTree(string path)
	{
		UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
		UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);
		ArgumentException.ThrowIfNullOrEmpty(path);

		using RegistryKey? probe = Registry.CurrentUser.OpenSubKey(path, writable: false);
		if (probe is null)
		{
			return false;
		}

		Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
		return true;
	}

	public bool DeleteSubKeyIfEmpty(string path)
	{
		UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
		UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);
		ArgumentException.ThrowIfNullOrEmpty(path);

		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: false);
		if (key is null)
		{
			return false;
		}

		// "Empty" includes the default value: a shared key that only carries some
		// other owner's unnamed value is NOT ours to remove.
		bool hasForeignContent = key.SubKeyCount != 0
			|| key.ValueCount != 0
			|| key.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null;
		if (hasForeignContent)
		{
			return false;
		}

		Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
		return true;
	}

	// Coarse classifier for snapshot JSON. Kept hand-rolled instead of
	// RegistryValueKind so the backup records what the data LOOKS like rather
	// than trusting an enum over values we did not write.
	private static string ClassifyKind(object? data)
	{
		return data switch
		{
			string => "String",
			int => "DWord",
			long => "QWord",
			string[] => "MultiString",
			byte[] => "Binary",
			null => "None",
			_ => "Unknown",
		};
	}

	private static string? RenderData(object? data)
	{
		return data switch
		{
			string text => text,
			int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
			long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
			_ => null,
		};
	}

	private static bool IsRecoverableRegistryFailure(Exception ex)
	{
		// Same failure family RegistryBrowserSource treats as a normal machine
		// state; a locked-down or vanishing key degrades the report, it never
		// escapes to Bootstrap.
		return ex is System.Security.SecurityException
			or UnauthorizedAccessException
			or IOException
			or ObjectDisposedException
			or ArgumentException;
	}
}
