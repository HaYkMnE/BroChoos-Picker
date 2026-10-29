using BroChoos.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BroChoos.Core.Runners;

// 0A-4 default-apps command surface (--register / --unregister /
// --verify-defaults / --open-default-apps). All four actions are read-mostly and
// strictly per-user (HKCU only, D-273). None of them ever writes UserChoice
// (D-187): registering declares capability, the USER picks the default in
// Windows Settings.
//
// Exit codes: success 0, failure ExitCodes.GeneralError (1). No new numeric
// codes: the four actions are operator flows whose failures are one-off and
// fully explained on the console, so the existing contract stays intact.
//
// Console output here is user-facing dev output (same class as UrlRunner's
// "Opening in ..." line); persistent log lines carry fixed tokens only.
public sealed class DefaultsRunner(
	ILogger<DefaultsRunner> logger,
	ICapabilityRegistrationService registration,
	IDefaultAppsStatusService status,
	IDefaultAppsGuide guide)
{
	// The UserChoice ProgId rendered on the console comes from a registry value
	// another (HKCU-writable) process could have shaped, so it is capped and
	// scrubbed of control characters before display.
	private const int MaxDisplayedProgIdLength = 64;

	private readonly ILogger<DefaultsRunner> _logger = logger;
	private readonly ICapabilityRegistrationService _registration = registration;
	private readonly IDefaultAppsStatusService _status = status;
	private readonly IDefaultAppsGuide _guide = guide;

	public int Register()
	{
		CapabilityRegistrationResult result = _registration.RegisterAll();

		if (result.BackupFilePath is not null)
		{
			// User-facing console value (never logged): the operator needs the real
			// path to restore from. D-291 keeps it out of system_changes.log.
			Console.WriteLine($"Backup of the pre-write state: {result.BackupFilePath}");
		}

		if (!result.Succeeded)
		{
			Console.Error.WriteLine($"Registration failed: {result.ErrorMessage}");
			if (result.BackupFilePath is not null)
			{
				Console.Error.WriteLine("A backup was taken before any write; partial state can be restored from it.");
			}

			_logger.LogError("Capability registration failed");
			return ExitCodes.GeneralError;
		}

		// Per-type Registered status AFTER writing, from a fresh verification read
		// (not from what the write pass intended to write).
		PrintStatusTable(_status.GetStatus());
		Console.WriteLine("Capabilities declared for http, https and .url (per-user, HKCU only).");
		Console.WriteLine("The default is NOT changed: pick BroChoos in Windows Settings > Apps > Default apps (or run --open-default-apps).");
		_logger.LogInformation("Capabilities registered; default left to the user's choice");
		return ExitCodes.Success;
	}

	public int Unregister()
	{
		CapabilityRegistrationResult result = _registration.UnregisterAll();

		if (result.BackupFilePath is not null)
		{
			Console.WriteLine($"Backup of the pre-removal state: {result.BackupFilePath}");
		}

		foreach (string removed in result.RemovedKeyPaths)
		{
			// Fixed product-owned HKCU paths: no user data, safe to print and to log.
			Console.WriteLine($"Removed: {removed}");
		}

		if (!result.Succeeded)
		{
			Console.Error.WriteLine($"Unregister failed: {result.ErrorMessage}");
			_logger.LogError("Capability unregistration failed");
			return ExitCodes.GeneralError;
		}

		PrintStatusTable(_status.GetStatus());
		Console.WriteLine("BroChoos handler capabilities removed for this user.");
		Console.WriteLine($"Restore path: re-run --register, or restore the backed-up state manually from {result.BackupFilePath}");
		if (_logger.IsEnabled(LogLevel.Information))
		{
			_logger.LogInformation("Capabilities unregistered ({RemovedCount} registry item(s))", result.RemovedKeyPaths.Count);
		}

		return ExitCodes.Success;
	}

	public int VerifyDefaults()
	{
		IReadOnlyList<DefaultAppsEntryStatus> entries = _status.GetStatus();
		PrintStatusTable(entries);

		int registeredCount = entries.Count(e => e.Registered);
		if (registeredCount == 0)
		{
			Console.WriteLine("BroChoos is not registered as a handler candidate. Run --register to declare capabilities.");
		}

		// A status report is never an error state: "not registered" is a valid
		// answer, so verification itself returns 0 (step 2 stays an honest
		// READ-ONLY snapshot of the user's choice).
		return ExitCodes.Success;
	}

	public int OpenDefaultAppsGuide()
	{
		// Output may be redirected; UTF-8 is attempted so the Russian guide text
		// renders on a real console, and the attempt is best-effort on purpose.
		try
		{
			Console.OutputEncoding = System.Text.Encoding.UTF8;
		}
		catch (IOException)
		{
			// A redirected/specialized console rejected the switch; the guide text
			// still prints, worst case with substitution glyphs.
		}

		// User-facing guide text in Russian (spec, D-187/D-274): the user — and
		// only the user — picks the default in Windows Settings.
		Console.WriteLine("Откройте страницу «Приложения по умолчанию» (она открывается отдельным окном):");
		Console.WriteLine("  1. В списке найдите «BroChoos» (можно воспользоваться поиском).");
		Console.WriteLine("  2. Назначьте BroChoos обработчиком по умолчанию для протоколов HTTP, HTTPS и файлов .URL.");
		Console.WriteLine("BroChoos никогда не переключает выбор сам: «Приложение по умолчанию» выбираете только вы (BroChoos does not write UserChoice).");

		bool opened = _guide.TryOpenDefaultAppsPage();
		if (!opened)
		{
			Console.Error.WriteLine("The Settings page could not be opened automatically; open it manually: Start > Settings > Apps > Default apps.");
			_logger.LogWarning("The Default apps settings page could not be opened; continuing with the text guide only");
		}

		// After the user returns (their action in Settings), verification re-runs.
		// With redirected input there is nobody to wait for: skip the pause so
		// scripts and tests stay non-interactive.
		if (!Console.IsInputRedirected)
		{
			Console.Write("Когда закончите выбор в параметрах, нажмите Enter здесь для повторной проверки статуса...");
			_ = Console.ReadLine();
		}

		Console.WriteLine();
		Console.WriteLine("Status after your choice:");
		PrintStatusTable(_status.GetStatus());
		return ExitCodes.Success;
	}

	private void PrintStatusTable(IReadOnlyList<DefaultAppsEntryStatus> entries)
	{
		Console.WriteLine("Default Apps status (three-step model, read-only):");
		Console.WriteLine("  target | registered | selected (UserChoice ProgId) | handled");

		foreach (DefaultAppsEntryStatus entry in entries)
		{
			Console.WriteLine(
				"  {0,-6} | {1,-10} | {2,-28} | {3}",
				entry.Scheme,
				entry.Registered ? "yes" : "no",
				DescribeSelection(entry),
				entry.HandledNote);
		}
	}

	// Renders step 2 of D-268. Persistent logs get only the fixed token via the
	// logger: the raw ProgId value is console-only and scrubbed/capped.
	private string DescribeSelection(DefaultAppsEntryStatus entry)
	{
		string selection = entry.SelectedIsBroChoos switch
		{
			null => "(no choice recorded)",
			true => $"BroChoos ({entry.ExpectedProgId})",
			false => $"other app ({Scrub(entry.SelectedProgId)})",
		};

		if (_logger.IsEnabled(LogLevel.Information))
		{
			string token = entry.SelectedIsBroChoos switch
			{
				null => "none",
				true => "ours",
				false => "other",
			};
			_logger.LogInformation("Default Apps status {Scheme}: registered={Registered} selection={Selection}", entry.Scheme, entry.Registered, token);
		}

		return selection;
	}

	private static string Scrub(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return "(empty)";
		}

		string cleaned = string.Concat(raw.Select(c => char.IsControl(c) ? '?' : c));
		return cleaned.Length > MaxDisplayedProgIdLength
			? cleaned[..MaxDisplayedProgIdLength] + "..."
			: cleaned;
	}
}
