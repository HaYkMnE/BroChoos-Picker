using System.Diagnostics;
using BroChoos.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BroChoos.Platform.Windows.Browsers;

// D-130 system/fallback surface for the picker's F key. The URL is handed to the
// Windows URL association (UseShellExecute=true) as FileName: no command line is
// ever built, no Arguments string and no ArgumentList entry is populated, so the
// URL cannot be reparsed as a shell fragment (the D-183/D-199/D-310 invariants
// are kept even on this path). BroChoos-as-handler recursion cannot nest pickers:
// a forwarded URL re-enters the already-running primary through the single
// instance pipe (see ISystemFallbackLauncher).
//
// Like WindowsBrowserLauncher: no exception escapes Launch, every outcome is a
// LaunchResult, and log lines stay URL-free by construction (privacy rule).
public sealed class SystemFallbackLauncher(
	ILogger<SystemFallbackLauncher> logger,
	IProcessStarter processStarter) : ISystemFallbackLauncher
{
	internal const string SystemTargetId = "system";
	internal const string ErrorInvalidUrl = "systemfallback.invalid-url";
	internal const string ErrorStartFailed = "systemfallback.start-failed";
	internal const string CategoryConfiguration = "configuration";
	internal const string CategoryProcessStart = "process-start";
	internal const string MessageKeyFailed = "launch.error.failed";

	private readonly ILogger<SystemFallbackLauncher> _logger = logger;
	private readonly IProcessStarter _processStarter = processStarter;

	public LaunchResult Launch(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return Fail(ErrorInvalidUrl, CategoryConfiguration, "System fallback launch was requested without a URL");
		}

		ProcessStartInfo startInfo = BuildStartInfo(url);

		try
		{
			if (_processStarter.Start(startInfo))
			{
				_logger.LogInformation("System fallback launched the URL with the OS association");
				return LaunchResult.Succeeded(SystemTargetId);
			}

			return Fail(ErrorStartFailed, CategoryProcessStart, "The operating system did not return a process for the shell association");
		}
		catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
		{
			// The message is logged without the URL; the exception text describes the OS error only.
			_logger.LogError(ex, "System fallback launch failed ({ErrorCode})", ErrorStartFailed);
			return LaunchResult.Failed(SystemTargetId, ErrorStartFailed, CategoryProcessStart, MessageKeyFailed);
		}
	}

	// The exact shape pinned by tests: FileName carries the URL, shell resolution
	// does the rest. Nothing else is populated on purpose.
	internal static ProcessStartInfo BuildStartInfo(string url)
	{
		return new ProcessStartInfo
		{
			FileName = url,
			UseShellExecute = true,
		};
	}

	private LaunchResult Fail(string errorCode, string category, string logMessage)
	{
		_logger.LogError("System fallback launch rejected ({ErrorCode}): {Reason}", errorCode, logMessage);
		return LaunchResult.Failed(SystemTargetId, errorCode, category, MessageKeyFailed);
	}
}
