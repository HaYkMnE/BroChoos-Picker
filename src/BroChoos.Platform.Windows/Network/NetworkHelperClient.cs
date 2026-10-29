using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BroChoos.Core.Abstractions;

namespace BroChoos.Platform.Windows.Network;

public interface INetworkHelperProcessRunner
{
	Task<NetworkHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken);
}

public sealed record NetworkHelperProcessResult(bool Started, int ExitCode, string StandardOutput, string StandardError);

public sealed class NetworkHelperClient(INetworkHelperProcessRunner processRunner) : INetworkHelperClient
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	// THE SESSION CACHE IS RAM-ONLY AND BOUNDED, and both halves are deliberate.
	//
	// IT IS RAM-ONLY. Nothing about a resolved short link is written to disk: the URL a user
	// activated is exactly the kind of value the privacy surface must not persist, and a
	// cache that outlived the process would be a tracking record, not a cache.
	//
	// IT IS BOUNDED (N2). A ConcurrentDictionary with no cap grows for as long as the
	// URL-handler process lives, one entry per distinct (url, consent) pair the user ever
	// activates, and nothing ever removes one. 256 is chosen as the cap because the value is
	// a per-process convenience for repeat activations of the SAME link, not an archive: a
	// single process resolving more than 256 DISTINCT short links is a burst the cap exists to
	// absorb, and at roughly a few hundred bytes per entry the resident cost stays trivially
	// small. The number is a NAME rather than an inline literal so the policy is greppable and
	// the test pins the exact bound instead of a magic number.
	//
	// EVICTION IS OLDEST-FIRST (FIFO), matching the two bounded queues in this assembly
	// (BoundedActivationQueue, FullscreenSuppressionQueue): the codebase already expresses
	// "bounded backlog" as a queue plus a dictionary, and a full LRU would add bookkeeping to
	// a cache whose miss cost is one helper invocation. Only a key that was actually inserted
	// is enqueued, so the order queue cannot outgrow the dictionary on repeated lookups.
	internal const int SessionCacheCapacity = 256;

	private static readonly ConcurrentDictionary<ShortLinkCacheKey, ShortLinkResolveResponse> SessionCache = new();
	private static readonly ConcurrentQueue<ShortLinkCacheKey> SessionCacheOrder = new();

	internal static int SessionCacheCount => SessionCache.Count;

	private readonly INetworkHelperProcessRunner _processRunner = processRunner;

	public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
	{
		_ = command;
		_ = cancellationToken;
		return Task.FromResult(HelperClientDispatch.Rejected);
	}

	public async Task<ShortLinkResolveResponse> ResolveShortLinkAsync(ShortLinkResolveRequest request, CancellationToken cancellationToken)
	{
		if (!request.ConsentGranted)
		{
			return Reject(request.Url, "shortlink.consent-required");
		}

		ShortLinkCacheKey cacheKey = new(request.Url, request.ConsentGranted);
		if (SessionCache.TryGetValue(cacheKey, out ShortLinkResolveResponse? cached))
		{
			return cached with { FromCache = true };
		}

		string payload = JsonSerializer.Serialize(request, JsonOptions);
		NetworkHelperProcessResult processResult = await _processRunner.RunAsync("resolve-short-link", payload, cancellationToken);

		ShortLinkResolveResponse response;
		if (!processResult.Started)
		{
			response = Reject(request.Url, "network.helper-not-found");
		}
		else if (processResult.ExitCode != 0)
		{
			response = Reject(request.Url, "network.helper-nonzero-exit");
		}
		else if (string.IsNullOrWhiteSpace(processResult.StandardOutput))
		{
			response = Reject(request.Url, "network.helper-empty-response");
		}
		else
		{
			try
			{
				response = JsonSerializer.Deserialize<ShortLinkResolveResponse>(processResult.StandardOutput, JsonOptions)
					?? Reject(request.Url, "network.helper-malformed-response");
			}
			catch (JsonException)
			{
				response = Reject(request.Url, "network.helper-malformed-response");
			}
		}

		// CACHE ONLY A REAL RESOLUTION (defect #38). Before this predicate the write was
		// unconditional, so the FIRST transient failure was cached for the rest of the process:
		// "network.helper-not-found" (the helper package not yet deployed), a one-off nonzero
		// exit, an empty stdout or a malformed payload all became a permanent, FromCache = true
		// answer and the link could never resolve again until restart. The predicate is derived
		// from what a reject IS rather than from where the branch happens to be written:
		// a resolution is cacheable only when the helper actually dispatched it (Dispatch ==
		// Dispatched) AND it carries no error code. The ErrorCode half is belt-and-braces
		// against a helper that emits a contradictory payload (Dispatched with an ErrorCode, or
		// Rejected with none); anything ambiguous is simply not cached, which costs one extra
		// helper run and never pins a wrong answer.
		if (IsCacheable(response))
		{
			Remember(cacheKey, response with { FromCache = false });
		}

		return response;
	}

	private static bool IsCacheable(ShortLinkResolveResponse response)
	{
		return response.Dispatch == HelperClientDispatch.Dispatched && response.ErrorCode is null;
	}

	private static void Remember(ShortLinkCacheKey cacheKey, ShortLinkResolveResponse response)
	{
		// TryAdd is false when the key is already resident, and in that case NOTHING is
		// enqueued: the order queue records insertions, so a hit must not inflate it.
		if (!SessionCache.TryAdd(cacheKey, response))
		{
			return;
		}

		SessionCacheOrder.Enqueue(cacheKey);

		// The eviction loop, not a single dequeue: a key can already have been removed by a
		// concurrent Trim, so one dequeue does not guarantee one removal. Looping until the
		// dictionary is back at the cap keeps the bound honest; the queue-empty guard stops a
		// transient race (an insert that has added to the dictionary but not yet to the queue)
		// from spinning.
		while (SessionCache.Count > SessionCacheCapacity && SessionCacheOrder.TryDequeue(out ShortLinkCacheKey? oldest))
		{
			_ = SessionCache.TryRemove(oldest, out _);
		}
	}

	private sealed record ShortLinkCacheKey(string Url, bool ConsentGranted);

	private static ShortLinkResolveResponse Reject(string url, string errorCode)
	{
		return new ShortLinkResolveResponse(HelperClientDispatch.Rejected, url, url, errorCode, FromCache: false);
	}
}

public sealed class NetworkHelperProcessRunner : INetworkHelperProcessRunner
{
	// PARITY WITH THE OTHER TWO RUNNERS, and the finding this closes. Disposing a Process
	// handle does NOT terminate the child, so abandoning the wait left the helper running
	// with the resolve request already in its stdin - a network helper that was reported as
	// timed out could still finish its work afterwards. The kill in the two abandoned paths
	// below is what makes the abandonment real.
	//
	// The budget is a VALUE rather than an inline literal so the test seam below can pin the
	// production default, exactly as StorageHelperProcessRunner does. The two-argument
	// constructor is the TEST seam (a short budget plus a real child process); optional
	// parameters are deliberately NOT used, because the container would resolve them to
	// null/default and silently drop the budget.
	internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1700);

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

	// TEST SEAM, identical to LaunchHelperProcessRunner's: awaited after Process.Start and
	// BEFORE the budget's token source exists, so a test's wait for its stub is never
	// charged to the budget. Null in production; behaviour unchanged.
	private readonly Func<CancellationToken, Task>? _beforeBudgetStartsForTests;

	public NetworkHelperProcessRunner()
		: this(ResolveHelperPath, DefaultTimeout, observer: null, forcedGenericFailure: null)
	{
	}

	internal NetworkHelperProcessRunner(Func<string?> resolveHelperPath, TimeSpan timeout)
		: this(resolveHelperPath, timeout, observer: null, forcedGenericFailure: null)
	{
	}

	internal NetworkHelperProcessRunner(
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

	public async Task<NetworkHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
	{
		string? helperPath = _resolveHelperPath();
		if (helperPath is null)
		{
			return new NetworkHelperProcessResult(false, -1, string.Empty, "helper-path-not-found");
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
				return new NetworkHelperProcessResult(false, -1, string.Empty, "helper-process-not-started");
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

			return new NetworkHelperProcessResult(true, process.ExitCode, stdout, stderr);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// The kill runs before the result is built and never alters it: Started stays true
			// (the process did start) and 124 is the marker the client classifies on.
			KillAbandonedProcess(process);
			return new NetworkHelperProcessResult(true, 124, string.Empty, "helper-process-timeout");
		}
		catch (OperationCanceledException)
		{
			// The caller cancelled, so this wait is abandoned too and the child is killed for
			// the same reason. The typed outcome stays EXACTLY what the generic catch below
			// returns: the kill is an implementation detail of abandonment, not a new
			// diagnosis about the helper.
			KillAbandonedProcess(process);
			return new NetworkHelperProcessResult(false, -1, string.Empty, "helper-process-start-exception");
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
			// helper that had already received the resolve request kept running.
			//
			// AND THE CLASSIFICATION, which is the OTHER half. The most COMMON way to arrive
			// here is not a failure at all: a helper that exits FAST closes its end of the
			// stdin pipe, and the parent's next write then throws IOException ("The pipe is
			// being closed.", ERROR_BROKEN_PIPE). MEASURED against real child processes:
			// 75/144 network runs took exactly that path, and in EVERY run the exit code was
			// the correct one (wrongExit = 0). Reporting Started=false for those runs made
			// the client say network.helper-not-found for a resolution that had happened.
			//
			// See HelperProcessAbandonment for the decision rule and the no-hang argument.
			if (process is { HasExited: true })
			{
				// The child was ALREADY GONE when the failure surfaced, so its real result is
				// the truthful outcome. The drain is sized by THIS runner's budget, so a
				// healthy-but-slow helper is not truncated by an unrelated constant (NEW-2).
				(int exitCode, string stdout, string stderr) = await HelperProcessAbandonment.ReadCompletedResultAsync(process, stdoutTask, stderrTask, Timeout).ConfigureAwait(false);

				// NO KILL HERE, AND THAT IS DELIBERATE (NEW-3 REVERTED). The parent has already
				// exited, so there is nothing of the HELPER left to reap; what a tree kill would
				// target is a DESCENDANT, and doing that from this branch is unsafe:
				//   * the parent's numeric PID may already have been REUSED (live PID reuse was
				//     observed during the audit). Process.Kill(entireProcessTree: true) re-opens
				//     the process BY NUMBER and its walk StartTime-guards the CHILDREN but not
				//     the ROOT, so it can select an unrelated process and kill it and its tree.
				//   * a descendant here is not necessarily an orphan: reaping is at best
				//     unnecessary for the network helper and, on the launch path, actively
				//     harmful (see LaunchHelperClient).
				// The orphan protection that matters - and that is SAFE, because it only runs
				// while the child is still alive and can therefore be trusted to be the child
				// it appears to be - lives on the still-running path below, in
				// KillAbandonedProcess. NO safe IN-PLACE mechanism exists to reap descendants of
				// an already-exited parent without risking an unrelated PID or a wanted browser,
				// so none is attempted here - and on this path there is nothing to reap anyway:
				// the network helper resolves over sockets/DNS and spawns NO descendant. A
				// per-invocation Windows Job Object with KILL_ON_JOB_CLOSE would be the safe
				// design (handle-based, so no PID reuse), but it is deferred as its own task and
				// is NOT implemented here. See the NEW-3 REVERT note in HelperProcessAbandonment.
				return new NetworkHelperProcessResult(true, exitCode, stdout, stderr);
			}

			// The child is STILL RUNNING, so this is a genuine unexpected failure that
			// abandons a started process. The kill is the orphan fix (D-13 / D-41 / D-42)
			// and MUST remain.
			//
			// It shares the cancelled branch's result SHAPE (Started false, exit -1, the
			// start-exception marker) and nothing else: the kill never alters the report, and
			// KillAbandonedProcess swallows its own failures by contract.
			KillAbandonedProcess(process);
			return new NetworkHelperProcessResult(false, -1, string.Empty, "helper-process-start-exception");
		}
		finally
		{
			process?.Dispose();
		}
	}

	// Kills the child the runner is about to abandon. The whole TREE is killed because the
	// network helper may spawn its own resolution children, and an orphaned grandchild would
	// be exactly as invisible to the user as an orphaned helper.
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
		string fileName = "BroChoos.NetworkHelper.exe";
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
