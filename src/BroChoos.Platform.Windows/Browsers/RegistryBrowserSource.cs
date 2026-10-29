using Microsoft.Win32;

namespace BroChoos.Platform.Windows.Browsers;

// D-181 tiers 2 and 3, read-only:
//   tier 2 — HKLM/HKCU \Software\Clients\StartMenuInternet\<client>\shell\open\command
//   tier 3 — \Software\RegisteredApplications, which points at a client key that is
//            then resolved through the same StartMenuInternet layout.
// Registry access can fail for permission or hive-shape reasons; every tier is
// best-effort and degrades to "no registrations" instead of throwing (D-308/309).
public sealed class RegistryBrowserSource : IRegistryBrowserSource
{
	internal const string StartMenuInternetPath = @"Software\Clients\StartMenuInternet";
	internal const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
	internal const string SourceStartMenuInternet = "registry-startmenuinternet";
	internal const string SourceRegisteredApplications = "registry-registeredapplications";

	// Win32 registry key names are capped at 255 characters. RegistryKey.OpenSubKey
	// throws ArgumentException past that, so an over-long name is rejected before use
	// rather than being caught afterwards.
	internal const int MaxKeyNameLength = 255;

	private readonly IRegistryHiveReader _localMachine;
	private readonly IRegistryHiveReader _currentUser;

	public RegistryBrowserSource()
		: this(new RegistryHiveReader(Registry.LocalMachine), new RegistryHiveReader(Registry.CurrentUser))
	{
	}

	// Test seam: lets the traversal be exercised against a described hive shape
	// without touching the real machine registry.
	internal RegistryBrowserSource(IRegistryHiveReader localMachine, IRegistryHiveReader currentUser)
	{
		_localMachine = localMachine;
		_currentUser = currentUser;
	}

	public IReadOnlyList<RegistryBrowserRegistration> GetRegistrations()
	{
		List<RegistryBrowserRegistration> registrations = [];

		// HKLM before HKCU: machine-wide installs are the more authoritative tier.
		CollectStartMenuInternet(_localMachine, registrations);
		CollectStartMenuInternet(_currentUser, registrations);
		CollectRegisteredApplications(_localMachine, registrations);
		CollectRegisteredApplications(_currentUser, registrations);

		return registrations;
	}

	private static void CollectStartMenuInternet(IRegistryHiveReader hive, List<RegistryBrowserRegistration> sink)
	{
		try
		{
			using IRegistryKeyReader? root = hive.OpenSubKey(StartMenuInternetPath);
			if (root is null)
			{
				return;
			}

			foreach (string clientName in root.GetSubKeyNames())
			{
				string? command = ReadShellOpenCommand(root, clientName);
				if (!string.IsNullOrWhiteSpace(command))
				{
					sink.Add(new RegistryBrowserRegistration(clientName, command, SourceStartMenuInternet));
				}
			}
		}
		catch (Exception ex) when (IsRecoverableRegistryFailure(ex))
		{
			// A missing or locked-down hive is a normal machine state, not an error.
		}
	}

	private static void CollectRegisteredApplications(IRegistryHiveReader hive, List<RegistryBrowserRegistration> sink)
	{
		try
		{
			using IRegistryKeyReader? registered = hive.OpenSubKey(RegisteredApplicationsPath);
			using IRegistryKeyReader? clients = hive.OpenSubKey(StartMenuInternetPath);
			if (registered is null || clients is null)
			{
				return;
			}

			foreach (string valueName in registered.GetValueNames())
			{
				// The value points at a capabilities key; only StartMenuInternet-shaped
				// entries can be resolved to a launchable command in this milestone.
				// A non-string value (REG_DWORD, REG_BINARY, ...) fails the pattern
				// match and is skipped rather than coerced.
				if (registered.GetValue(valueName) is not string capabilitiesPath
					|| !capabilitiesPath.Contains("StartMenuInternet", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string? clientName = ExtractClientName(capabilitiesPath);
				if (clientName is null)
				{
					continue;
				}

				string? command = ReadShellOpenCommand(clients, clientName);
				if (!string.IsNullOrWhiteSpace(command))
				{
					sink.Add(new RegistryBrowserRegistration(clientName, command, SourceRegisteredApplications));
				}
			}
		}
		catch (Exception ex) when (IsRecoverableRegistryFailure(ex))
		{
			// Same best-effort contract as the StartMenuInternet tier.
		}
	}

	private static string? ReadShellOpenCommand(IRegistryKeyReader clientsRoot, string clientName)
	{
		using IRegistryKeyReader? command = clientsRoot.OpenSubKey($@"{clientName}\shell\open\command");

		// The value is read with RegistryValueOptions.DoNotExpandEnvironmentNames
		// (see RegistryKeyReader): a REG_EXPAND_SZ value is attacker-influenced input,
		// and expanding it would splice this process's environment into a path that
		// later becomes ProcessStartInfo.FileName. Nothing expands it afterwards
		// either — an unexpanded %VAR% simply fails the PathSafety gate.
		return command?.GetValue(null) as string;
	}

	// "Software\Clients\StartMenuInternet\Google Chrome\Capabilities" -> "Google Chrome".
	// Returns null for anything that cannot be used as a registry key name, so the
	// caller never hands an out-of-range name to OpenSubKey (which throws
	// ArgumentException above 255 chars — see MaxKeyNameLength).
	internal static string? ExtractClientName(string capabilitiesPath)
	{
		string[] segments = capabilitiesPath.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		for (int i = 0; i < segments.Length - 1; i++)
		{
			if (segments[i].Equals("StartMenuInternet", StringComparison.OrdinalIgnoreCase))
			{
				string clientName = segments[i + 1];
				return clientName.Length is > 0 and <= MaxKeyNameLength ? clientName : null;
			}
		}

		return null;
	}

	private static bool IsRecoverableRegistryFailure(Exception ex)
	{
		// ArgumentException (and its ArgumentNullException subtype) belong here:
		// RegistryKey.OpenSubKey throws ArgumentException for a key name longer than
		// 255 characters, and that name comes from an attacker-writable
		// HKCU\...\RegisteredApplications value. Letting it escape would turn a
		// best-effort registry read into Bootstrap's generic catch and exit 1,
		// breaking the documented 0/4 contract (D-308/D-309).
		return ex is System.Security.SecurityException
			or UnauthorizedAccessException
			or IOException
			or ObjectDisposedException
			or ArgumentException;
	}
}
