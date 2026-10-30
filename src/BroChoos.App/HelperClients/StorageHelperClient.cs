using System.Diagnostics;
using System.Text.Json;
using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Network;

namespace BroChoos.App.HelperClients;

internal interface IStorageHelperProcessRunner
{
	Task<StorageHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken);
}

internal sealed record StorageHelperProcessResult(bool Started, int ExitCode, string StandardOutput, string StandardError);

internal sealed class StorageHelperClient(IStorageHelperProcessRunner processRunner) : IStorageHelperClient
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private const string HelperTimeoutMarker = "helper-process-timeout";

	private readonly IStorageHelperProcessRunner _processRunner = processRunner;

	public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return command.CommandName == "first-run.find-browsers"
			? Task.FromResult(HelperClientDispatch.Dispatched)
			: Task.FromResult(HelperClientDispatch.Rejected);
	}

	public async Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
	{
		string payload = JsonSerializer.Serialize(request, JsonOptions);
		return await RunHelperAsync("discover-persist", payload, cancellationToken);
	}

	// W6.2: publish candidates the CALLER validated. Different helper command, same typed
	// failure vocabulary and the same 5 s budget — the helper's publication path takes the
	// candidates it is handed and reconciles them with the catalog, so nothing here needs a
	// second request shape for the answer.
	public async Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
	{
		string payload = JsonSerializer.Serialize(request, JsonOptions);
		return await RunHelperAsync("publish-candidates", payload, cancellationToken);
	}

	private async Task<StorageDiscoveryResponse> RunHelperAsync(string command, string payload, CancellationToken cancellationToken)
	{
		StorageHelperProcessResult processResult = await _processRunner.RunAsync(command, payload, cancellationToken);

		if (!processResult.Started)
		{
			return new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-not-found");
		}

		// The runner's own budget expiring is a DISTINCT failure, and it is classified BEFORE
		// the non-zero-exit branch below for the same reason LaunchHelperClient classifies its
		// timeout first: exit 124 is what the runner returns when the WAIT was abandoned, so
		// folding it into "the helper exited non-zero" would report the wrong cause. The
		// storage helper's own exit codes are 0 and 2-5 (src\BroChoos.StorageHelper\Program.cs),
		// so 124 is unambiguous. Without this branch a hung helper surfaced as a generic
		// non-zero exit and the fix that gave the runner its budget would be invisible to the
		// user (H1).
		if (IsTimeout(processResult))
		{
			return new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-timeout");
		}

		if (processResult.ExitCode != 0)
		{
			return new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-nonzero-exit");
		}

		if (string.IsNullOrWhiteSpace(processResult.StandardOutput))
		{
			return new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-empty-response");
		}

		try
		{
			StorageDiscoveryResponse? response = JsonSerializer.Deserialize<StorageDiscoveryResponse>(processResult.StandardOutput, JsonOptions);
			return response is null
				? new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-malformed-response")
				: response;
		}
		catch (JsonException)
		{
			return new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-malformed-response");
		}
	}

	// Same marker and exit code as LaunchHelperClient.IsTimeout / NetworkHelperProcessRunner,
	// so one convention covers all three helpers.
	private static bool IsTimeout(StorageHelperProcessResult processResult)
	{
		return processResult.ExitCode == 124
			|| processResult.StandardError.Contains(HelperTimeoutMarker, StringComparison.OrdinalIgnoreCase);
	}
}

internal sealed class StorageHelperProcessRunner : IStorageHelperProcessRunner
{
	// PARITY WITH THE OTHER TWO HELPERS, and the finding this closes. The launch helper is
	// bounded at 5 s (LaunchHelperProcessRunner) and the network helper at 1.7 s
	// (NetworkHelperProcessRunner); this runner had NO budget at all, so WaitForExitAsync
	// waited forever. Because the picker invoked the storage helper from its UI thread, a hung
	// StorageHelper.exe blocked the picker indefinitely (H1) - and a helper process can hang
	// for reasons the caller cannot control (a blocked scan, a stuck child, a debugger).
	//
	// The DI constructor keeps production behaviour identical (the real path probe and the
	// 5 s budget). The two-argument constructor is the TEST seam: the timeout test needs a
	// short budget and a real child process to prove the wait is abandoned. Optional
	// parameters are deliberately NOT used here - the container would resolve them to
	// null/default and silently drop the budget (MEASURED: with a parameterless constructor
	// present the container selects it, so the default budget is what production gets).
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

	private readonly Func<string?> _resolveHelperPath;

	// The budget this runner actually holds. Internal rather than private so the tests can
	// assert the DI-resolved runner carries DefaultTimeout: a parameterless constructor that
	// stopped passing it would silently restore the unbounded behaviour while a test on the
	// constant alone stayed green - which is the exact defect this fixes.
	internal TimeSpan Timeout { get; }

	// D-13/D-41/D-42 KILL-AUDIT SEAM, and deliberately per-INSTANCE rather than static. Every
	// abandoned path must reap the child it abandons, and the generic (non-cancellation) path is
	// the one that used to leak. That path is hard to reach with a real child - MEASURED: a
	// deliberately broken stdin pipe only surfaces the exception AFTER the child has already
	// exited - so a real-process test cannot DISTINGUISH "killed here" from "gone anyway", and
	// the run is made deterministic instead.
	//
	// INSTANCE state, not static: BroChoos.App.Tests does not disable parallel test execution
	// (unlike the Core and Platform.Windows test assemblies), so a static hook would be shared
	// mutable state across concurrently running tests - a defect in the test infrastructure that
	// could fail an unrelated test. These fields are set only through the internal constructor
	// used by tests; production goes through the parameterless one and gets the inert defaults.
	private readonly Action<int> _killObserver;

	private readonly Exception? _forcedGenericFailureForTests;

	// TEST SEAM, the same one LaunchHelperProcessRunner and NetworkHelperProcessRunner carry:
	// awaited after Process.Start and BEFORE the budget's token source exists, so a test can
	// wait for its stub child to reach a known state without that wait being charged to the
	// budget. Null in production: no extra await, and the budget starts where it always did.
	private readonly Func<CancellationToken, Task>? _beforeBudgetStartsForTests;

	public StorageHelperProcessRunner()
		: this(ResolveHelperPath, DefaultTimeout, observer: null, forcedGenericFailure: null)
	{
	}

	internal StorageHelperProcessRunner(Func<string?> resolveHelperPath, TimeSpan timeout)
		: this(resolveHelperPath, timeout, observer: null, forcedGenericFailure: null)
	{
	}

	internal StorageHelperProcessRunner(
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

	public async Task<StorageHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
	{
		string? helperPath = _resolveHelperPath();
		if (helperPath is null)
		{
			return new StorageHelperProcessResult(false, -1, string.Empty, "helper-path-not-found");
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

		// The child handle is HOISTED OUT of the try ON PURPOSE: a `using` declaration inside
		// the try is out of scope in a catch clause, and disposing a Process handle does NOT
		// terminate the child. That combination is the orphan this fixes - MEASURED: after
		// storage.helper-timeout was reported, the child was still alive 1 s later and
		// completed a catalog write 2.64 s AFTER the timeout. The kill in the two abandoned
		// paths below is what makes the abandonment real; the normal path is untouched.
		Process? process = null;

		// The two output-read tasks are HOISTED OUT of the try for the same reason the
		// Process handle is: the generic catch must be able to observe what they did (a
		// child that exited on its own may still have readable output). They are null until
		// the child is started and the reads begin, which is what the null-conditional
		// handling in the catch relies on.
		Task<string>? stdoutTask = null;
		Task<string>? stderrTask = null;

		try
		{
			process = Process.Start(startInfo);
			if (process is null)
			{
				return new StorageHelperProcessResult(false, -1, string.Empty, "helper-process-not-started");
			}

			if (_beforeBudgetStartsForTests is { } beforeBudget)
			{
				await beforeBudget(CancellationToken.None);
			}

			// The budget covers the WHOLE exchange, not just the exit: a helper that stalls
			// before it ever reads its stdin would otherwise leave ReadToEndAsync pending
			// forever even though WaitForExitAsync had been given a deadline.
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

			return new StorageHelperProcessResult(true, process.ExitCode, stdout, stderr);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// Typed timeout, identical to the launch and network runners: Started stays true
			// (the process did start) and 124 is the marker the client classifies on. The
			// `when` guard is what keeps a CALLER cancellation from being reported as a helper
			// timeout. The kill runs BEFORE the result is built and never alters it.
			KillAbandonedProcess(process);
			return new StorageHelperProcessResult(true, 124, string.Empty, "helper-process-timeout");
		}
		catch (OperationCanceledException)
		{
			// The caller cancelled, so the wait is abandoned here too and the child is killed
			// for the same reason as above. The typed outcome stays EXACTLY what it was
			// (Started false, exit -1, the start-exception marker): the kill is an
			// implementation detail of abandonment, not a new diagnosis about the helper.
			KillAbandonedProcess(process);
			return new StorageHelperProcessResult(false, -1, string.Empty, "helper-process-start-exception");
		}
		catch
		{
			// THE THIRD ABANDONED PATH, and the one that used to leak. Process.Start has
			// already run by the time any exception can reach here, so a non-cancellation
			// failure (a broken stdin pipe, an I/O error while reading the child's output,
			// an internal failure while awaiting the exit) abandons a STARTED child exactly
			// as the two OperationCanceledException branches do. The kill is therefore the
			// same one, for the same reason: an abandoned but started process must not be
			// leaked. Without it this path returned the start-exception result while a
			// helper that had already received the discovery request kept running - and for
			// the storage helper that means it could still complete a catalog write.
			//
			// AND THE CLASSIFICATION, which is the OTHER half and is why this catch is no
			// longer a flat "start failure". The most COMMON way to arrive here is not a
			// failure at all: a helper that exits FAST closes its end of the stdin pipe, and
			// the parent's next write then throws IOException ("The pipe is being closed.",
			// ERROR_BROKEN_PIPE). MEASURED against real child processes: 76/256, 128/256 and
			// 110/256 storage runs took exactly that path, and in EVERY run the child's exit
			// code was the correct one (wrongExit = 0). Reporting Started=false for those
			// runs told the user a helper that HAD RUN AND DONE ITS WORK had failed
			// (storage.helper-not-found) - a false result, and the regression this closes.
			//
			// See HelperProcessAbandonment for the decision rule and the no-hang argument.
			if (process is { HasExited: true })
			{
				// The child was ALREADY GONE when the failure surfaced, so its real result is
				// the truthful outcome. The drain is sized by THIS runner's budget, so a
				// healthy-but-slow helper is not truncated by an unrelated constant (NEW-2).
				(int exitCode, string stdout, string stderr) = HelperProcessAbandonment.ReadCompletedResult(process, stdoutTask, stderrTask, Timeout);

				// NO KILL HERE, AND THAT IS DELIBERATE (NEW-3 REVERTED). The parent has already
				// exited, so there is nothing of the HELPER left to reap; what a tree kill would
				// target is a DESCENDANT, and doing that from this branch is unsafe:
				//   * the parent's numeric PID may already have been REUSED (live PID reuse was
				//     observed during the audit). Process.Kill(entireProcessTree: true) re-opens
				//     the process BY NUMBER and its walk StartTime-guards the CHILDREN but not
				//     the ROOT, so it can select an unrelated process and kill it and its tree.
				//   * a descendant here is not necessarily an orphan: on the launch path it is
				//     the BROWSER the helper was asked to start, and a surviving browser is the
				//     DESIRED outcome of a successful launch (see LaunchHelperClient).
				// The orphan protection that matters - and that is SAFE, because it only runs
				// while the child is still alive and can therefore be trusted to be the child
				// it appears to be - lives on the still-running path below, in
				// KillAbandonedProcess. NO safe IN-PLACE mechanism exists to reap descendants of
				// an already-exited parent without risking an unrelated PID or a wanted browser,
				// so none is attempted here. A per-invocation Windows Job Object with
				// KILL_ON_JOB_CLOSE would be the safe design (handle-based, so no PID reuse;
				// breakaway unset, so no wanted browser escapes the assignment decision), but it
				// is deferred as its own task and is NOT implemented here. See the NEW-3 REVERT
				// note in HelperProcessAbandonment.
				return new StorageHelperProcessResult(true, exitCode, stdout, stderr);
			}

			// The child is STILL RUNNING, so this is a genuine unexpected failure that
			// abandons a started process. The kill is the orphan fix (D-13 / D-41 / D-42)
			// and MUST remain; the typed shape is unchanged (Started false, exit -1, the
			// start-exception marker, the same shape the cancelled branch returns) because
			// the kill is an implementation detail of abandonment, not a new diagnosis.
			KillAbandonedProcess(process);
			return new StorageHelperProcessResult(false, -1, string.Empty, "helper-process-start-exception");
		}
		finally
		{
			process?.Dispose();
		}
	}

	// Kills the child the runner is about to abandon, so a reported timeout (or a caller
	// cancellation) can no longer be followed by a write the user was told had failed. The
	// whole TREE is killed because these helpers can start children of their own (the launch
	// helper starts a browser, the storage helper a scan host) and an orphaned grandchild
	// would be just as invisible.
	//
	// Guarded and swallowed BY CONTRACT: a child that exited between the abandonment and
	// this call is not an error, and a kill that fails (access denied, exit race) must never
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
		string fileName = "BroChoos.StorageHelper.exe";
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
