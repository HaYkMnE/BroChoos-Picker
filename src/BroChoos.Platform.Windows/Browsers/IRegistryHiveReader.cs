using Microsoft.Win32;

namespace BroChoos.Platform.Windows.Browsers;

// Minimal READ-ONLY seam over the Win32 registry. It exists so the D-181 tier 2/3
// traversal can be tested against a described hive shape (over-long key names,
// non-string values, HKLM/HKCU precedence) instead of mutating the real machine
// registry. There is deliberately no write member anywhere on this surface: 0A-2
// never writes to the registry.
internal interface IRegistryKeyReader : IDisposable
{
	IReadOnlyList<string> GetSubKeyNames();

	IReadOnlyList<string> GetValueNames();

	// Verbatim value. REG_EXPAND_SZ is never expanded — see RegistryBrowserSource
	// for why expansion would be an injection vector.
	object? GetValue(string? name);

	IRegistryKeyReader? OpenSubKey(string path);
}

internal interface IRegistryHiveReader
{
	IRegistryKeyReader? OpenSubKey(string path);
}

// Thin pass-through to a real RegistryKey. Holds no logic of its own so the seam
// cannot drift from the behaviour of the underlying API.
internal sealed class RegistryKeyReader(RegistryKey key) : IRegistryKeyReader
{
	private readonly RegistryKey _key = key;

	public IReadOnlyList<string> GetSubKeyNames()
	{
		return _key.GetSubKeyNames();
	}

	public IReadOnlyList<string> GetValueNames()
	{
		return _key.GetValueNames();
	}

	public object? GetValue(string? name)
	{
		return _key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
	}

	public IRegistryKeyReader? OpenSubKey(string path)
	{
		RegistryKey? subKey = _key.OpenSubKey(path);
		return subKey is null ? null : new RegistryKeyReader(subKey);
	}

	public void Dispose()
	{
		_key.Dispose();
	}
}

// A hive root (HKLM/HKCU). The hive handle itself is process-owned and not disposed
// here; only the subkeys this type hands out are.
internal sealed class RegistryHiveReader(RegistryKey hive) : IRegistryHiveReader
{
	private readonly RegistryKey _hive = hive;

	public IRegistryKeyReader? OpenSubKey(string path)
	{
		RegistryKey? subKey = _hive.OpenSubKey(path);
		return subKey is null ? null : new RegistryKeyReader(subKey);
	}
}
