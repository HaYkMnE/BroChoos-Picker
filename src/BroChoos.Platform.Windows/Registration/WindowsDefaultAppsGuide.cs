using System.Diagnostics;
using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using Microsoft.Extensions.Logging;

namespace BroChoos.Platform.Windows.Registration;

// D-274 step "guide the user": opens the Windows "Default apps" settings page.
//
// LOCKED EXCEPTION (spec for 0A-4): ProcessStartInfo.UseShellExecute is FORBIDDEN
// everywhere else in this repo (D-183/D-199/D-310), and this is the single
// sanctioned exception — "ms-settings:defaultapps" is a PROTOCOL URI, not an
// executable path: there is no image to start, so only the Windows shell can
// resolve it, which requires UseShellExecute=true. The FileName value is a
// compile-time CONSTANT (never user input, never registry content, never
// concatenated), so the shell-reparse risk those decisions guard against does
// not apply. Arguments stays empty: nothing attacker-shapable is handed over.
public sealed class WindowsDefaultAppsGuide(
	ILogger<WindowsDefaultAppsGuide> logger,
	IProcessStarter processStarter) : IDefaultAppsGuide
{
	internal const string DefaultAppsUri = "ms-settings:defaultapps";

	private readonly ILogger<WindowsDefaultAppsGuide> _logger = logger;
	private readonly IProcessStarter _processStarter = processStarter;

	public bool TryOpenDefaultAppsPage()
	{
		ProcessStartInfo startInfo = new()
		{
			FileName = DefaultAppsUri, // constant protocol URI; see class remark
			UseShellExecute = true,    // single sanctioned exception
		};

		try
		{
			if (_processStarter.Start(startInfo))
			{
				_logger.LogInformation("Default apps settings page opened");
				return true;
			}

			_logger.LogWarning("The shell did not return a process for the Default apps page");
			return false;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
		{
			// Fail-open (D-274): the text guide and the verification re-run still
			// serve the user; a headless or locked-down shell never crashes the flow.
			_logger.LogWarning(ex, "The Default apps settings page could not be opened");
			return false;
		}
	}
}
