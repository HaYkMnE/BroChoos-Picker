using System.ComponentModel;
using System.Diagnostics;
using BroChoos.Core;
using BroChoos.Core.Runners;

namespace BroChoos.App.Picker;

// What the picker shows after a recovery run. Derived ONLY from the child's exit code (or the
// start/timeout failure), so the status line never claims more than the process reported.
internal enum SettingsRepairResult
{
	// Exit 10 (SafeModeOk): the selected action completed.
	Repaired,

	// Exit 11 (SafeModeFailed): the action ran and reported Failed/Unavailable (for example a
	// newer-schema file it must not touch). Nothing of the user's was lost.
	NotRepaired,

	// The child could not be started, did not finish in time, or returned an unexpected code.
	Failed,
}

// The process seam for a recovery child: start, then wait for its exit code. Kept separate from
// IProcessStarter (fire-and-forget) because this caller needs the exit code; tests replace it
// with a fake, so no test ever starts a real process.
internal interface IRecoveryProcessRunner
{
	// Returns the exit code, or null when the wait timed out (the child is then killed).
	// Throws Win32Exception / InvalidOperationException when the start itself fails.
	Task<int?> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class RecoveryProcessRunner : IRecoveryProcessRunner
{
	public async Task<int?> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
	{
		// Off the caller's thread: CreateProcess is usually quick, but an antivirus scan of
		// the image can stall it, and the caller is the picker's UI thread.
		using Process process = await Task.Run(() => Process.Start(startInfo), cancellationToken).ConfigureAwait(false)
			?? throw new InvalidOperationException("the recovery process did not start");

		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(timeout);
		try
		{
			await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
			return process.ExitCode;
		}
		catch (OperationCanceledException)
		{
			// A hung child must not outlive the attempt: it may hold a write lock.
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception killFailure) when (killFailure is InvalidOperationException or Win32Exception)
			{
			}

			return null;
		}
	}
}

// Runs ONE Safe Mode recovery action as a hidden child of the same executable and maps its exit
// code to a picker result. Used by the config-recovery notice's "Repair settings" action.
//
// SAFE BY CONSTRUCTION: the executable is the running image (Environment.ProcessPath), never a
// value from the link or a file; arguments are a structured ArgumentList; UseShellExecute is
// false and CreateNoWindow is true, so no shell, no console window and no focus change. The
// portable data root is passed on as its own token, so the child repairs the data the picker read.
//
// NEVER ON THE UI THREAD: the wait is awaited (WaitForExitAsync) with a timeout, so the picker
// stays responsive while the child runs.
internal sealed class SafeModeLauncher(IRecoveryProcessRunner runner, Func<string?> executablePath)
{
	internal const string SafeModeArgument = "--safe-mode";
	internal const string SafeModeActionArgument = "--safe-mode-action";
	internal const string PortableDataArgument = "--portable-data";

	// The repair touches at most two small files with a 1.5 s attempt budget each, plus process
	// start-up; 15 s is generous for a cold start on a slow disk without leaving the user
	// waiting indefinitely.
	internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

	private readonly IRecoveryProcessRunner _runner = runner;
	private readonly Func<string?> _executablePath = executablePath;

	public static SafeModeLauncher CreateDefault()
	{
		return new SafeModeLauncher(new RecoveryProcessRunner(), static () => Environment.ProcessPath);
	}

	// Null when the running image path is unknown: there is then nothing safe to start.
	public ProcessStartInfo? BuildStartInfo(string actionId, string? portableDataPath)
	{
		ArgumentNullException.ThrowIfNull(actionId);
		string? exe = _executablePath();
		if (string.IsNullOrWhiteSpace(exe) || !SafeModeActionIds.IsKnown(actionId))
		{
			return null;
		}

		ProcessStartInfo startInfo = new()
		{
			FileName = exe,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
		};
		startInfo.ArgumentList.Add(SafeModeArgument);
		startInfo.ArgumentList.Add(SafeModeActionArgument);
		startInfo.ArgumentList.Add(actionId);
		if (!string.IsNullOrWhiteSpace(portableDataPath))
		{
			startInfo.ArgumentList.Add(PortableDataArgument);
			startInfo.ArgumentList.Add(portableDataPath);
		}

		return startInfo;
	}

	// Never throws for a start, wait or exit-code problem; those become Failed. The exception
	// TYPE is reported for the log, never the message (it can carry a path with the user name).
	public async Task<(SettingsRepairResult Result, string? Detail)> RunAsync(string actionId, string? portableDataPath, CancellationToken cancellationToken)
	{
		ProcessStartInfo? startInfo = BuildStartInfo(actionId, portableDataPath);
		if (startInfo is null)
		{
			return (SettingsRepairResult.Failed, "no-executable");
		}

		try
		{
			int? exitCode = await _runner.RunAsync(startInfo, Timeout, cancellationToken).ConfigureAwait(true);
			return exitCode switch
			{
				null => (SettingsRepairResult.Failed, "timeout"),
				ExitCodes.SafeModeOk => (SettingsRepairResult.Repaired, null),
				ExitCodes.SafeModeFailed => (SettingsRepairResult.NotRepaired, null),
				_ => (SettingsRepairResult.Failed, "unexpected-exit"),
			};
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			return (SettingsRepairResult.Failed, ex.GetType().Name);
		}
	}
}
