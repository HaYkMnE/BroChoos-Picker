using System.Diagnostics;

namespace BroChoos.Platform.Windows.Network;

// THE PIPE-CLOSE RACE, shared by all three helper process runners.
//
// Every runner has the same shape: start the child, write the request to its stdin, close
// stdin, then WaitForExitAsync and read stdout/stderr. A child that exits FAST - before the
// parent finishes writing - closes its end of the stdin pipe, and the parent's next write
// throws IOException ("The pipe is being closed.", ERROR_BROKEN_PIPE). That is a
// NORMAL-SUCCESSFUL-RUN artifact, not a start failure: the child RAN and did its work.
//
// The generic catch used to classify EVERY non-cancellation exception as
// "Started=false, helper-process-start-exception", and the clients then mapped that to
// storage.helper-not-found / LaunchStatus.Failed / network.helper-not-found. MEASURED against
// real child processes: storage 76/256, 128/256, 110/256; launch 36/144; network 75/144 normal
// completions were misreported, with wrongExit = 0 in every run - the exit code was never
// wrong, only the CLASSIFICATION was.
//
// THE DECISION RULE this type implements, and it is the ONLY rule that can be truthful here:
//
//   child ALREADY EXITED  -> the run may have SUCCEEDED; the write failure is benign. Report
//                            the child's ACTUAL result: Started = true and its real ExitCode,
//                            with whatever stdout/stderr can still be collected. NO KILL IS
//                            ATTEMPTED ON THIS PATH (see the NEW-3 REVERT note below).
//   child STILL RUNNING   -> a genuine unexpected failure that abandons a STARTED child. The
//                            caller keeps the existing behaviour: KillAbandonedProcess + the
//                            failure tuple. The kill is the orphan fix and MUST remain.
//
// NEW-3 REVERT - WHY THE EXITED-CHILD PATH DOES NOT REAP THE TREE. An earlier revision called
// a ReapExitedProcessTree sibling here, on the theory that the parent exiting does not prove
// the descendants it started are gone. The theory is true, but the CURE IS WORSE THAN THE
// DISEASE, and it was independently refuted:
//   * A tree kill on an ALREADY-EXITED parent re-opens the process BY NUMERIC PID. Live PID
//     REUSE was observed during the audit, and Process.Kill(entireProcessTree: true) guards
//     each CHILD match with StartTime but NOT the walk ROOT - so the walk can select an
//     unrelated new process and terminate it and its descendants.
//   * On the launch path a descendant is not an orphan at all: it is the BROWSER the helper was
//     asked to start, and a surviving browser is the DESIRED outcome of a successful launch.
//     Reaping it reproduces the exact "the browser disappeared" symptom the design avoids.
//   * The window in which that hazard operates is not small: passing the runner's own budget
//     (NEW-2) stretched the gap between the HasExited observation and the kill to as much as the
//     drain window (up to DrainCeiling).
// The orphan protection that actually matters - and that is SAFE, because it runs only while
// the child is still alive and is therefore the identity it appears to be - lives on the
// still-running path, in KillAbandonedProcess. NO safe IN-PLACE mechanism exists to reap
// descendants of an already-exited parent here: a direct-only kill is a no-op with the same
// stale-PID hazard, and a manual creator-PID walk would have to re-derive the very root-guard
// the runtime omits, in a new, racier, untested mechanism.
//
// THE SAFE DESIGN THAT DOES EXIST, AND WHY IT IS DEFERRED. A per-invocation Windows JOB OBJECT
// with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE is PID-reuse-safe by construction: the job holds
// kernel HANDLES to its processes rather than numeric PIDs, so reaping it can never select an
// unrelated process the way a by-number tree kill can. It is also safe for the launch path: with
// JOB_OBJECT_LIMIT_BREAKAWAY_OK unset, the browser cannot break out of the job, so a wanted
// browser is never killed - in fact the browser is a job member, so closing the job when the
// helper exits would kill it, which is why KILL_ON_JOB_CLOSE is not a drop-in fit for the launch
// helper and the assignment policy per helper has to be its own decision. It is therefore a
// genuine, deferred option - NOT the same thing as "no mechanism exists", and NOT in scope here.
//
// REACHABILITY, MEASURED PER HELPER (from the audit) - what a descendant COULD be, and so how
// much a tree kill would even have to protect:
//   * network helper: spawns NO descendant at all (it resolves over sockets/DNS) -> there is
//     nothing to reap on this path;
//   * launch helper: its descendant IS the wanted browser -> it must NOT be reaped;
//   * storage helper: a late-write tail was measured as bounded - roughly <= 1.5 s, one write
//     attempt, with a 1500 ms lock floor -> a narrow real tail, and the only path where a
//     mechanism could matter at all.
// The limitation is therefore documented and accepted for now, with the Job Object recorded as
// the deferred safe design rather than mis-stated as impossible.
//
// WHY THIS CANNOT HANG, stated as the sequencing it relies on:
//
//   The catch that reaches here has ALREADY left the `await` that failed, so no runner task is
//   sitting on this continuation. The only open work is the two ReadToEndAsync tasks, started
//   BEFORE the write. When the child has exited and nothing else holds the pipe, both streams
//   reach end-of-file and those tasks COMPLETE on their own - reading from a closed pipe
//   returns EOF rather than blocking - so awaiting them cannot block indefinitely. Two
//   independent guards make that a guarantee rather than a hope:
//     1. HasExited is checked FIRST. Awaiting the reads is only ever attempted for a child that
//        has already exited, which is exactly the state in which the streams are normally at
//        EOF.
//     2. The await is BOUNDED by the runner's OWN REMAINING BUDGET, capped by a named ceiling
//        (see DrainCeiling / ResolveDrainWindow). If a stream outlives the child - a descendant
//        inherit the pipe write handle and hold it open, the one real way EOF never arrives -
//        the budget expires and the reads are abandoned. Abandoning a read is safe here for
//        the same reason it is in the timeout path: the child is gone and its Process handle
//        is disposed by the caller's finally, and a read that is never awaited cannot deadlock
//        the caller.
//   A read that does not complete inside the budget is reported with an explicit
//   "helper-process-output-unreadable" marker instead of being silently reported as an empty
//   string, so the outcome is honest about which half it did and did not observe.
//
// THE DRAIN IS SIZED BY THE RUNNER'S OWN BUDGET, NOT BY A BLIND CONSTANT (NEW-2). The window
// used to be a hardcoded 2 s, chosen with no relation to any helper's declared budget (launch
// and storage 5 s, network 1.7 s; the child-side network budget is 1500 ms in
// ShortLinkResolver.cs). A healthy child whose output arrived after ~3.5 s was therefore
// reported with the unreadable marker, and the clients mapped that marker to a spurious
// *-empty-response / *-malformed-response failure for a run that had in fact SUCCEEDED. The
// fix is to pass the runner's budget in: DrainOutput waits up to min(budget, DrainCeiling), so
// the drain is always coherent with how long the helper itself is allowed to run, and a
// healthy-but-slow child is never truncated by an unrelated guess.
public static class HelperProcessAbandonment
{
	// The UPPER BOUND on any single drain, independent of the runner's budget. It exists so a
	// caller that somehow hands in a very large budget still cannot turn this diagnostic path
	// into a long stall, and it is named rather than inline so the policy is greppable.
	//
	// The ceiling is deliberately generous relative to every real helper budget: the largest
	// one is 5 s, so the ceiling never binds in production and the drain is governed by the
	// budget, which is the point of NEW-2.
	public static readonly TimeSpan DrainCeiling = TimeSpan.FromSeconds(10);

	public const string OutputUnreadableMarker = "helper-process-output-unreadable";

	// Collects the result of a child that has ALREADY EXITED (the benign half of the rule).
	// Returns Started = true and the REAL exit code, which is the truthful outcome the clients
	// classify on. Never throws: this runs on a diagnostic path where a fresh exception would
	// replace the outcome the caller is promised, and the caller's own finally still disposes
	// the Process handle.
	//
	// `drainBudget` is the RUNNER'S OWN invocation budget. The reads are awaited for at most
	// that long, capped by DrainCeiling, so the drain is sized by how long the helper was
	// allowed to live rather than by an unrelated constant (NEW-2). A non-positive budget falls
	// back to the ceiling: refusing to drain at all would defeat the purpose of the branch.
	//
	// BOTH streams share ONE deadline (the resolved window), not one window each: the drain as
	// a whole never exceeds the window. This overload blocks the calling thread for up to that
	// window; async callers use ReadCompletedResultAsync.
	public static (int ExitCode, string StandardOutput, string StandardError) ReadCompletedResult(
		Process process,
		Task<string>? stdoutTask,
		Task<string>? stderrTask,
		TimeSpan drainBudget)
	{
		int exitCode = ReadExitCode(process);
		TimeSpan window = ResolveDrainWindow(drainBudget);
		Task[] pending = PendingReads(stdoutTask, stderrTask);
		if (pending.Length > 0)
		{
			try
			{
				_ = Task.WaitAll(pending, window);
			}
			catch (AggregateException)
			{
				// A read faulted (most often the same pipe closure that produced the failure);
				// WaitAll has by now waited for the others, and Collect reports the marker.
			}
		}

		return (exitCode, Collect(stdoutTask), Collect(stderrTask));
	}

	// The non-blocking form of ReadCompletedResult for the runners' async catch paths: the
	// shared deadline is awaited, so no thread is held for the drain.
	public static async Task<(int ExitCode, string StandardOutput, string StandardError)> ReadCompletedResultAsync(
		Process process,
		Task<string>? stdoutTask,
		Task<string>? stderrTask,
		TimeSpan drainBudget)
	{
		int exitCode = ReadExitCode(process);
		TimeSpan window = ResolveDrainWindow(drainBudget);
		Task[] pending = PendingReads(stdoutTask, stderrTask);
		if (pending.Length > 0)
		{
			try
			{
				await Task.WhenAll(pending).WaitAsync(window).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				// The shared window expired; unfinished reads are abandoned and reported with
				// the marker by Collect.
			}
			catch (Exception)
			{
				// A read faulted (most often the same pipe closure that produced the failure);
				// WhenAll completes only after every read finished, and Collect reports the
				// marker for the faulted one. Never throws: this is a diagnostic path.
			}
		}

		return (exitCode, Collect(stdoutTask), Collect(stderrTask));
	}

	private static int ReadExitCode(Process process)
	{
		try
		{
			return process.ExitCode;
		}
		catch (InvalidOperationException)
		{
			// The handle stopped being associated with a process between the HasExited check
			// and here; the child did run, so Started stays true and -1 records that the exact
			// code could not be read.
			return -1;
		}
	}

	private static Task[] PendingReads(Task<string>? stdoutTask, Task<string>? stderrTask)
	{
		List<Task> reads = [];
		if (stdoutTask is not null)
		{
			reads.Add(stdoutTask);
		}

		if (stderrTask is not null)
		{
			reads.Add(stderrTask);
		}

		return [.. reads];
	}

	// The window this drain may use: the runner's budget when it is a usable value,
	// otherwise the ceiling. The rule is TOTAL and stated in full so a future budget change
	// is never silently clamped by a comparison that only happens to work:
	//
	//   budget <= 0                     -> ceiling (refusing to drain would defeat the branch)
	//   0 < budget <= DrainCeiling       -> budget  (the runner's own budget governs)
	//   budget > DrainCeiling            -> ceiling (the hard upper bound always wins)
	//
	// The EQUALITY case (budget == DrainCeiling) is deliberately admitted as a VALID budget
	// rather than falling through to the "else": the returned value is the same either way,
	// but writing it as `<=` makes the boundary legible instead of hiding it behind a strict
	// `<` that reads as if equality were an error case.
	private static TimeSpan ResolveDrainWindow(TimeSpan drainBudget)
	{
		return drainBudget > TimeSpan.Zero && drainBudget <= DrainCeiling ? drainBudget : DrainCeiling;
	}

	// Reads the already-settled state of a read task. A task that faulted (the pipe broke
	// mid-read), was cancelled, is missing, or is still pending after the shared window yields
	// the explicit marker rather than an empty string, so "no output" and "output we could not
	// read" stay distinguishable. A fault is observed so it never surfaces as unobserved.
	private static string Collect(Task<string>? readTask)
	{
		if (readTask is null)
		{
			return OutputUnreadableMarker;
		}

		if (readTask.IsFaulted)
		{
			_ = readTask.Exception;
			return OutputUnreadableMarker;
		}

		return readTask.Status == TaskStatus.RanToCompletion ? readTask.Result : OutputUnreadableMarker;
	}
}
