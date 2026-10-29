using System.Diagnostics;
using BroChoos.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BroChoos.Platform.Windows.Browsers;

// D-183 launch contract: a structured ProcessStartInfo whose ArgumentList carries the
// URL as its own single item, UseShellExecute=false, and no cmd.exe/powershell in the
// chain. D-199/D-310: because the URL is a distinct argv entry it can never be reparsed
// as an option or a shell fragment. D-304: the target executable must never be BroChoos
// itself, which would make the process re-enter its own handler forever.
// D-308/D-309: every failure becomes a LaunchResult; no exception escapes Launch.
public sealed class WindowsBrowserLauncher(
	ILogger<WindowsBrowserLauncher> logger,
	IEnvironmentProbe environment,
	IProcessStarter processStarter) : IBrowserLauncher
{
	internal const string ErrorRecursionGuard = "launch.recursion-guard";
	internal const string ErrorTargetMissing = "launch.target-missing";
	internal const string ErrorInvalidTarget = "launch.invalid-target";
	internal const string ErrorStartFailed = "launch.start-failed";

	// A failure the launch path was NOT designed for. Still a LaunchResult (D-308/309)
	// and still exit 5, but the code says "our bug or an unmodelled platform state",
	// not "the OS refused to start the browser" — telling the two apart is what makes
	// the catch-all diagnosable instead of a place where real defects go to hide.
	internal const string ErrorInternal = "launch.internal-error";

	internal const string CategoryProcessStart = "process-start";
	internal const string CategoryInternal = "internal";
	internal const string MessageKeyFailed = "launch.error.failed";

	private readonly ILogger<WindowsBrowserLauncher> _logger = logger;
	private readonly IEnvironmentProbe _environment = environment;
	private readonly IProcessStarter _processStarter = processStarter;

	public LaunchResult Launch(BrowserCandidate target, string url)
	{
		// The one deliberate throw: a null target is a CALLER bug, not a runtime launch
		// failure, and it must stay outside the try so it cannot be laundered into a
		// tidy LaunchResult. Everything after this line is inside the no-escape region.
		ArgumentNullException.ThrowIfNull(target);

		// EVERY pre-check lives inside the try (D-308/D-309). The recursion guard and its
		// path resolution touch the environment seam, Path.GetFullPath and
		// File.ResolveLinkTarget — each of which can throw something this file does not
		// model. Their own inner catch filters are ALLOWLISTS, and an allowlist is exactly
		// what previously broke the no-escape contract here (see IsRecoverableLaunchFailure):
		// anything the allowlist did not name escaped as exit 1 instead of becoming a
		// LaunchResult. Wrapping the pre-checks makes the inverted filter below the single
		// backstop, so an unmodelled throw from the guard path is a Failed result (exit 5).
		try
		{
			if (string.IsNullOrWhiteSpace(target.ExePath))
			{
				return Fail(target.BrowserId, ErrorInvalidTarget, "configuration", "Launch target has no executable path");
			}

			if (string.IsNullOrWhiteSpace(url))
			{
				return Fail(target.BrowserId, ErrorInvalidTarget, "configuration", "Launch was requested without a URL");
			}

			// D-304 recursion guard, before any other check: refusing to start ourselves
			// matters more than reporting a missing file.
			if (IsSelf(target.ExePath))
			{
				return Fail(target.BrowserId, ErrorRecursionGuard, "recursion-guard", "Refusing to launch BroChoos as its own browser target");
			}

			if (!_environment.FileExists(target.ExePath))
			{
				return Fail(target.BrowserId, ErrorTargetMissing, "target-missing", "Launch target executable no longer exists");
			}

			ProcessStartInfo startInfo = BuildStartInfo(target.ExePath, url);

			return _processStarter.Start(startInfo)
				? Succeed(target.BrowserId)
				: Fail(target.BrowserId, ErrorStartFailed, CategoryProcessStart, "The operating system did not return a browser process");
		}
		catch (Exception ex) when (IsRecoverableLaunchFailure(ex))
		{
			// The message is logged without the URL; the exception text describes the
			// executable and the OS error only.
			bool operational = IsOperationalLaunchFailure(ex);
			string errorCode = operational ? ErrorStartFailed : ErrorInternal;
			string category = operational ? CategoryProcessStart : CategoryInternal;

			_logger.LogError(ex, "Browser launch failed for {BrowserId} ({ErrorCode})", target.BrowserId, errorCode);
			return LaunchResult.Failed(target.BrowserId, errorCode, category, MessageKeyFailed);
		}
	}

	// Inverted policy (D-308/D-309): the contract is "no exception escapes Launch", so
	// the filter names the few exceptions that MUST escape instead of trying to list
	// every failure a process start can produce. An allowlist silently broke the
	// contract for ArgumentException/ObjectDisposedException/NotSupportedException and
	// OperationCanceledException, which surfaced as exit 1 rather than LaunchResult/5.
	// OutOfMemoryException stays uncaught: the process is no longer sane and reporting a
	// tidy launch failure would be a lie. StackOverflowException is named for the
	// READER only — it is DOCUMENTARY: since .NET Core the runtime fails fast on stack
	// overflow and the exception cannot be caught by any filter, so this clause never
	// fires. It is kept so the intent ("neither of these two is recoverable") survives
	// the next edit rather than looking like an oversight.
	private static bool IsRecoverableLaunchFailure(Exception ex)
	{
		return ex is not OutOfMemoryException and not StackOverflowException;
	}

	// The failures this launch path actually models: the OS or the platform refusing,
	// mis-stating or losing the start request. Anything outside this set reached the
	// catch-all for a reason nobody designed for, so it must not be reported with the
	// same code as a routine "browser would not start".
	private static bool IsOperationalLaunchFailure(Exception ex)
	{
		return ex is System.ComponentModel.Win32Exception
			or InvalidOperationException
			or IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException
			or PlatformNotSupportedException
			or ArgumentException
			or ObjectDisposedException
			or NotSupportedException
			or TimeoutException;
	}

	// The exact argv shape asserted by the tests: argument 0 is the URL and nothing else.
	internal static ProcessStartInfo BuildStartInfo(string exePath, string url)
	{
		ProcessStartInfo startInfo = new()
		{
			FileName = exePath,
			// No shell, no verb, no command string: the URL cannot be reparsed.
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
		};

		startInfo.ArgumentList.Add(url);

		return startInfo;
	}

	private bool IsSelf(string exePath)
	{
		string? self = _environment.CurrentProcessPath;
		if (string.IsNullOrWhiteSpace(self))
		{
			// Fail-closed: with no identity for the running process, "is this me?"
			// cannot be answered, and D-304 makes an infinite re-entry loop the worse
			// outcome than refusing one launch.
			return true;
		}

		try
		{
			// Compare normalized full paths so "..\BroChoos.exe" cannot slip past, and
			// resolve symlinks/junctions on BOTH sides so a link pointing at BroChoos
			// is not mistaken for a different executable.
			return string.Equals(ResolveFinalPath(self), ResolveFinalPath(exePath), StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			// Fail-closed: an unnormalizable path cannot be proven DIFFERENT from this
			// process, so the launch is refused. The previous plain-string fallback was
			// fail-open — it let an unnormalizable self-path through while its comment
			// claimed safety.
			return true;
		}
	}

	// Final target of any symlink/junction chain, or the normalized path when the file
	// is not a link (or cannot be inspected — a missing file is handled by the caller's
	// FileExists check, not here).
	private static string ResolveFinalPath(string path)
	{
		string fullPath = Path.GetFullPath(path);

		try
		{
			return File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			// ResolveLinkTarget throws for a missing path; the normalized path is still
			// the best available identity for the comparison.
			return fullPath;
		}
	}

	private LaunchResult Succeed(string browserId)
	{
		if (_logger.IsEnabled(LogLevel.Information))
		{
			_logger.LogInformation("Browser launched: {BrowserId}", browserId);
		}

		return LaunchResult.Succeeded(browserId);
	}

	private LaunchResult Fail(string browserId, string errorCode, string category, string logMessage)
	{
		// Log lines stay URL-free by construction (privacy rule): only ids and codes.
		_logger.LogError("Browser launch rejected for {BrowserId} ({ErrorCode}): {Reason}", browserId, errorCode, logMessage);
		return LaunchResult.Failed(browserId, errorCode, category, MessageKeyFailed);
	}
}
