using System.Diagnostics;
using System.Text.Json;
using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Network;

namespace BroChoos.Platform.Windows.Browsers;

public interface ILaunchHelperProcessRunner
{
	Task<LaunchHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken);
}

public sealed record LaunchHelperProcessResult(bool Started, int ExitCode, string StandardOutput, string StandardError);

public sealed class LaunchHelperClient(ILaunchHelperProcessRunner processRunner) : ILaunchHelperClient
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private const string HelperTimeoutMarker = "helper-process-timeout";

	private readonly ILaunchHelperProcessRunner _processRunner = processRunner;

	// W6.3 typed-outcome classification contract (accepted; Gate 0B criterion 0B-1):
	//  - helper never started            -> Failed  (launch.helper-not-found: nothing was launched)
	//  - typed outcome in stdout         -> that outcome, trusted as-is
	//  - timeout marker / exit 124       -> Timeout (launch.helper-timeout)
	//  - started, exit != 0              -> Unknown (launch.helper-nonzero-exit)
	//  - started, empty stdout           -> Unknown (launch.helper-empty-response)
	//  - started, unparseable stdout     -> Unknown (launch.helper-malformed-response)
	// A started-but-unclassifiable helper may have already opened the browser, so claiming
	// Failed would be a false result; Unknown drives the explicit Try again/Copy/Cancel
	// recovery in PickerSession with zero automatic retry. Pinned by
	// LaunchOutcomeMatrixTests and LaunchHelperBoundaryTests.
	public async Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
	{
		string payload = JsonSerializer.Serialize(request, JsonOptions);
		LaunchHelperProcessResult processResult = await _processRunner.RunAsync("launch", payload, cancellationToken);

		if (!processResult.Started)
		{
			return Fail(request.BrowserId, "launch.helper-not-found");
		}

		LaunchHelperOutcome? parsed = TryParseOutcome(processResult.StandardOutput);
		return parsed
			?? (IsTimeout(processResult)
			? Timeout(request.BrowserId, "launch.helper-timeout")
			: processResult.ExitCode != 0
			? Unknown(request.BrowserId, "launch.helper-nonzero-exit")
			: string.IsNullOrWhiteSpace(processResult.StandardOutput)
			? Unknown(request.BrowserId, "launch.helper-empty-response")
			: Unknown(request.BrowserId, "launch.helper-malformed-response"));
	}

	private static LaunchHelperOutcome? TryParseOutcome(string response)
	{
		if (string.IsNullOrWhiteSpace(response))
		{
			return null;
		}

		try
		{
			LaunchHelperOutcome? outcome = JsonSerializer.Deserialize<LaunchHelperOutcome>(response, JsonOptions);
			return outcome is null || string.IsNullOrWhiteSpace(outcome.TargetId) ? null : outcome;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static bool IsTimeout(LaunchHelperProcessResult processResult)
	{
		return processResult.ExitCode == 124
			|| processResult.StandardError.Contains(HelperTimeoutMarker, StringComparison.OrdinalIgnoreCase);
	}

	private static LaunchHelperOutcome Fail(string targetId, string errorCode)
	{
		return new LaunchHelperOutcome(
			LaunchStatus.Failed,
			targetId,
			errorCode,
			"helper-process",
			"launch.error.failed");
	}

	private static LaunchHelperOutcome Timeout(string targetId, string errorCode)
	{
		return new LaunchHelperOutcome(
			LaunchStatus.Timeout,
			targetId,
			errorCode,
			"helper-timeout",
			"launch.error.timeout");
	}

	private static LaunchHelperOutcome Unknown(string targetId, string errorCode)
	{
		return new LaunchHelperOutcome(
			LaunchStatus.Unknown,
			targetId,
			errorCode,
			"helper-process",
			"launch.error.unknown");
	}
}

public sealed class LaunchHelperProcessRunner : ILaunchHelperProcessRunner
{
	// PARITY WITH StorageHelperProcessRunner, and the finding this closes. Disposing a
	// Process handle does NOT terminate the child, so abandoning the wait used to leave the
	// helper running with the launch request already in its stdin - MEASURED: an orphan could
	// still open a browser AFTER launch.helper-timeout was reported to the user. The kill in
	// the two abandoned paths below is what makes the abandonment real.
	//
	// The budget is a VALUE rather than an inline literal so the test seam below can pin the
	// production default, exactly as StorageHelperProcessRunner does. The two-argument
	// constructor is the TEST seam (a short budget plus a real child process); optional
	// parameters are deliberately NOT used, because the container would resolve them to
	// null/default and silently drop the budget.
	internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

	private readonly Func<string?> _resolveHelperPath;

	internal TimeSpan Timeout { get; }

	// D-13/D-41/D-42 KILL-AUDIT SEAM, and deliberately per-INSTANCE rather than static. Every
	// abandoned path must reap the child it abandons, and the generic (non-cancellation) path is
	// the one that used to leak. That path is hard to reach with a real child - MEASURED: a
	// deliberately broken stdin pipe only surfaces the exception AFTER the child has already
	// exited - so a real-process test cannot DISTINGUISH "killed here" from "gone anyway", and
	// the run is made deterministic instead.
	//
	// INSTANCE state, not static: a static hook would be shared mutable state across
	// concurrently running tests, which is a defect in the test infrastructure that could fail
	// an unrelated test. These fields are set only through the internal constructor used by
	// tests; production goes through the parameterless one and gets the inert defaults.
	private readonly Action<int> _killObserver;

	private readonly Exception? _forcedGenericFailureForTests;

	// TEST SEAM: awaited after Process.Start and BEFORE the budget's token source exists, so
	// a test can wait for its stub child to reach a known state without that wait being
	// charged to the budget. Without it, a 300 ms budget raced the stub's own startup under
	// load (the "stub must have begun work" flake). Null in production: no extra await, and
	// the budget starts exactly where it always did. Not linked to the budget or the caller's
	// token, so a slow seam can only delay the test, never time out a production launch.
	private readonly Func<CancellationToken, Task>? _beforeBudgetStartsForTests;

	public LaunchHelperProcessRunner()
		: this(ResolveHelperPath, DefaultTimeout, observer: null, forcedGenericFailure: null)
	{
	}

	internal LaunchHelperProcessRunner(Func<string?> resolveHelperPath, TimeSpan timeout)
		: this(resolveHelperPath, timeout, observer: null, forcedGenericFailure: null)
	{
	}

	internal LaunchHelperProcessRunner(
		Func<string?> resolveHelperPath,
		TimeSpan timeout,
		Action<int>? observer,
		Exception? forcedGenericFailure,
		Func<CancellationToken, Task>? beforeBudgetStartsForTests = null)
	{
		_resolveHelperPath = resolveHelperPath;
		Timeout = timeout;
		_killObserver = observer ?? (static _ => { });
		_forcedGenericFailureForTests = forcedGenericFailure;
		_beforeBudgetStartsForTests = beforeBudgetStartsForTests;
	}

	public async Task<LaunchHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
	{
		string? helperPath = _resolveHelperPath();
		if (helperPath is null)
		{
			return new LaunchHelperProcessResult(false, -1, string.Empty, "helper-path-not-found");
		}

		ProcessStartInfo startInfo = new()
		{
			FileName = helperPath,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = Path.GetDirectoryName(helperPath) ?? AppContext.BaseDirectory,
		};
		startInfo.ArgumentList.Add(command);

		// Hoisted out of the try so the catch clauses can reach it: a `using` declaration
		// inside the try is out of scope in a catch, and disposing alone never terminates
		// the child.
		Process? process = null;

		// Hoisted for the same reason: the generic catch must be able to observe what the
		// output reads did for a child that exited on its own. Null until the child starts.
		Task<string>? stdoutTask = null;
		Task<string>? stderrTask = null;

		try
		{
			process = Process.Start(startInfo);
			if (process is null)
			{
				return new LaunchHelperProcessResult(false, -1, string.Empty, "helper-process-not-started");
			}

			if (_beforeBudgetStartsForTests is { } beforeBudget)
			{
				await beforeBudget(CancellationToken.None);
			}

			using CancellationTokenSource timeoutCts = new(Timeout);
			using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
			CancellationToken linkedToken = linkedCts.Token;

			// D-13/D-41/D-42 test hook: throw a NON-cancellation failure AFTER the child is
			// started, so the generic catch is exercised deterministically. Null in production.
			if (_forcedGenericFailureForTests is { } forced)
			{
				throw forced;
			}

			stdoutTask = process.StandardOutput.ReadToEndAsync(linkedToken);
			stderrTask = process.StandardError.ReadToEndAsync(linkedToken);

			await process.StandardInput.WriteAsync(requestPayload.AsMemory(), linkedToken);
			process.StandardInput.Close();

			await process.WaitForExitAsync(linkedToken);
			string stdout = await stdoutTask;
			string stderr = await stderrTask;

			return new LaunchHelperProcessResult(true, process.ExitCode, stdout, stderr);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// The kill runs before the result is built and never alters it: Started stays true
			// (the process did start) and 124 is the marker the client classifies on.
			KillAbandonedProcess(process);
			return new LaunchHelperProcessResult(true, 124, string.Empty, "helper-process-timeout");
		}
		catch (OperationCanceledException)
		{
			// The caller cancelled, so this wait is abandoned too and the child is killed for
			// the same reason. The typed outcome stays EXACTLY what the generic catch below
			// returns: the kill is an implementation detail of abandonment, not a new
			// diagnosis about the helper.
			KillAbandonedProcess(process);
			return new LaunchHelperProcessResult(false, -1, string.Empty, "helper-process-start-exception");
		}
		catch
		{
			// THE THIRD ABANDONED PATH, and the one that used to leak. Process.Start has
			// already run by the time any exception can reach here, so a non-cancellation
			// failure (a broken stdin pipe, an I/O error while reading the child's output,
			// an internal failure while awaiting the exit) abandons a STARTED child exactly
			// as the two OperationCanceledException branches do. The kill is therefore the
			// same one, for the same reason: an abandoned but started process must not be
			// leaked. Without it this path returned the start-exception result while the
			// launch helper - or the browser it starts - kept running.
			//
			// AND THE CLASSIFICATION, which is the OTHER half. The most COMMON way to arrive
			// here is not a failure at all: a helper that exits FAST closes its end of the
			// stdin pipe, and the parent's next write then throws IOException ("The pipe is
			// being closed.", ERROR_BROKEN_PIPE). MEASURED against real child processes:
			// 36/144 launch runs took exactly that path, and in EVERY run the exit code was
			// the correct one (wrongExit = 0). Reporting Started=false for those runs made
			// the client say LaunchStatus.Failed / launch.helper-not-found - telling the user
			// the launch failed when the helper had in fact done its job.
			//
			// See HelperProcessAbandonment for the decision rule and the no-hang argument.
			if (process is { HasExited: true })
			{
				// The child was ALREADY GONE when the failure surfaced, so its real result is
				// the truthful outcome. The drain is sized by THIS runner's budget, so a
				// healthy-but-slow helper is not truncated by an unrelated constant (NEW-2).
				(int exitCode, string stdout, string stderr) = await HelperProcessAbandonment.ReadCompletedResultAsync(process, stdoutTask, stderrTask, Timeout).ConfigureAwait(false);

				// NO KILL HERE, AND ON THIS PATH IT IS THE WHOLE POINT (NEW-3 REVERTED). The
				// parent has already exited, so what a tree kill would target is a DESCENDANT -
				// and on the launch path that descendant is the BROWSER the helper was asked to
				// start. A surviving browser is the DESIRED outcome of a successful launch, so
				// reaping the tree here reproduces the exact "the browser disappeared" symptom
				// the design avoids. The kill is also unsafe on its own terms:
				//   * the parent's numeric PID may already have been REUSED (live PID reuse was
				//     observed during the audit). Process.Kill(entireProcessTree: true) re-opens
				//     the process BY NUMBER and its walk StartTime-guards the CHILDREN but not
				//     the ROOT, so it can select an unrelated process and kill it and its tree.
				//   * passing the runner's own budget (NEW-2) stretched the window between the
				//     HasExited observation and the kill to as much as the drain window, widening
				//     that hazard.
				// The orphan protection that matters lives on the still-running path below, in
				// KillAbandonedProcess: it is SAFE because it only ever runs while the child is
				// still alive, so the process it targets can be trusted to be the child it
				// appears to be. NO safe IN-PLACE mechanism exists to reap descendants of an
				// already-exited parent without risking an unrelated PID or a wanted browser, so
				// none is attempted here. A per-invocation Windows Job Object with
				// KILL_ON_JOB_CLOSE would be the safe design (handle-based, so no PID reuse), but
				// it is deferred as its own task and is NOT implemented here. See the NEW-3
				// REVERT note in HelperProcessAbandonment.
				return new LaunchHelperProcessResult(true, exitCode, stdout, stderr);
			}

			// The child is STILL RUNNING, so this is a genuine unexpected failure that
			// abandons a started process. The kill is the orphan fix (D-13 / D-41 / D-42)
			// and MUST remain.
			//
			// It shares the cancelled branch's result SHAPE (Started false, exit -1, the
			// start-exception marker) and nothing else: the kill never alters the report, and
			// KillAbandonedProcess swallows its own failures by contract.
			KillAbandonedProcess(process);
			return new LaunchHelperProcessResult(false, -1, string.Empty, "helper-process-start-exception");
		}
		finally
		{
			process?.Dispose();
		}
	}

	// Kills the child the runner is about to abandon. The whole TREE is killed because the
	// launch helper's job is to start a browser, and an orphaned browser would be exactly as
	// invisible to the user as an orphaned helper.
	//
	// Guarded and swallowed BY CONTRACT: a child that exited between the abandonment and this
	// call is not an error, and a kill that fails (access denied, exit race) must never
	// replace the typed outcome the caller has already been promised with an exception.
	private void KillAbandonedProcess(Process? process)
	{
		try
		{
			if (process is null || process.HasExited)
			{
				return;
			}

			_killObserver(process.Id);
			process.Kill(entireProcessTree: true);
		}
		catch (Exception)
		{
			// Best effort by contract: the abandonment has already been reported and a failed
			// kill must not change that report.
		}
	}

	private static string? ResolveHelperPath()
	{
		string fileName = "BroChoos.LaunchHelper.exe";
		string? processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
		if (!string.IsNullOrWhiteSpace(processDirectory))
		{
			string candidate = Path.Combine(processDirectory, fileName);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		string appBaseCandidate = Path.Combine(AppContext.BaseDirectory, fileName);
		return File.Exists(appBaseCandidate) ? appBaseCandidate : null;
	}
}
