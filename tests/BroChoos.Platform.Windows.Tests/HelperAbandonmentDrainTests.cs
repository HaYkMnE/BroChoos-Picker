using System.Diagnostics;
using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Network;
using BroChoos.Platform.Windows.Tests.TestSupport;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests;

// NEW-2 (MED) - the drain window used to truncate a slow-but-healthy child, on the shared
// `process is { HasExited: true }` branch of the three helper runners. PASSES: the drain is
// now sized by the runner's own budget (NEW-2), and these tests keep it that way.
//
// NEW-2: THE DRAIN WINDOW USED TO TRUNCATE A SLOW-BUT-HEALTHY CHILD.
//   HelperProcessAbandonment's drain window was a HARDCODED 2 s constant with no
//   relation to the runner's own budget (launch/storage 5 s, network 1.7 s; the
//   child-side network budget is 1500 ms in src\BroChoos.NetworkHelper\ShortLinkResolver.cs
//   line 510). A healthy child whose output landed after ~3.5 s was reported with the
//   "helper-process-output-unreadable" marker, and the clients mapped that marker to a
//   spurious *-empty-response / *-malformed-response FAILURE for a genuinely successful,
//   merely-slower run.
//
// NEW-3 WAS REVERTED - THE EXITED-CHILD BRANCH MUST NOT REAP THE TREE.
//   An earlier revision called a ReapExitedProcessTree sibling on the exited-child branch,
//   arguing that a parent exiting does not prove the descendants it started are gone. An
//   independent audit REFUTED that fix:
//     * a tree kill on an ALREADY-EXITED parent re-opens the process BY NUMERIC PID. Live
//       PID reuse was observed, and Process.Kill(entireProcessTree: true) StartTime-guards
//       each CHILD match but NOT the walk ROOT - so it can kill an unrelated process and its
//       descendants (N-2);
//     * on the launch path the descendant is the BROWSER the helper was asked to start, so
//       reaping it reproduces the "the browser disappeared" symptom the design avoids (N-3);
//     * passing the runner's own budget (NEW-2) stretched the window between the HasExited
//       observation and the kill, widening the hazard.
//   The protection that matters - SAFE, because it only runs while the child is still alive -
//   lives on the still-running path in KillAbandonedProcess. These tests now pin the
//   ABSENCE of a DIRECT tree-kill on the exited path through the observer seam (which fires ONLY
//   where a kill is attempted on a LIVE process), so a future re-introduction of the dangerous
//   BYPASSING reap fails loudly.
//   A real-descendant variant was attempted and DELIBERATELY DROPPED because on Windows it
//   cannot isolate this path precisely (see the note at the end of the test class).
public sealed class HelperAbandonmentDrainTests
{
	// The old hardcoded window. A child writing after this must STILL have its output
	// returned - that is the whole of NEW-2.
	private static readonly TimeSpan OldBlindWindow = TimeSpan.FromSeconds(2);

	// The child waits this long before writing, so its output lands well after the old
	// 2 s window and well inside the runner's own 5 s-class budget. MEASURED: the stub's
	// `ping -n 5` completes in ~4.09 s on this machine.
	private static readonly TimeSpan SlowWriteDelay = TimeSpan.FromSeconds(4);

	// The payload the delayed read resolves with. A distinctive token so a truncation to the
	// marker can never be mistaken for success.
	private const string SlowReadPayload = "SLOW-BUT-HEALTHY-OUTPUT";

	// NEW-2, AT THE RUNNER. The old window truncated a healthy-but-slow child's output. This
	// test drives the runner into the EXITED-CHILD branch with output that arrives AFTER the
	// old 2 s window, and requires the REAL payload to come back.
	//
	// Reaching that branch with a live-but-slow child is what needs explaining. The stub reads
	// its stdin and CLOSES its own stdin handle at once (like the real helpers, which return
	// before draining a large request), so the parent's in-flight write fails while the child
	// keeps working - the generic catch. The child then waits past the old window and writes.
	// It exits on its own, so by the time the runner has caught the failure HasExited is true
	// and the drain runs - which is exactly the path NEW-2 is about.
	[Fact]
	public async Task LaunchRunner_HealthyChildWritingAfterTheOldWindow_StillYieldsItsRealOutput()
	{
		using SlowOutputStub stub = SlowOutputStub.Create();
		LaunchHelperProcessRunner runner = new(() => stub.ExecutablePath, TimeSpan.FromSeconds(10));

		Stopwatch elapsed = Stopwatch.StartNew();
		LaunchHelperProcessResult result = await runner.RunAsync(
			stub.Command,
			new string('x', 5_000_000),
			CancellationToken.None);
		elapsed.Stop();

		_ = result.Started.Should().BeTrue("the helper ran to completion on its own; only the output was slow");
		_ = result.ExitCode.Should().Be(0, "the child exited 0 - a slow-but-healthy run is still a successful run");
		_ = result.StandardOutput.Should().NotBe(
			HelperProcessAbandonment.OutputUnreadableMarker,
			"the drain must wait for a healthy child's streams instead of giving up on a blind constant window");
		_ = result.StandardOutput.Should().Contain(
			SlowOutputStub.PayloadToken,
			"the child's REAL output must be returned - truncating it made the client report a spurious empty/malformed response");
		_ = result.StandardError.Should().NotContain(
			HelperProcessAbandonment.OutputUnreadableMarker,
			"stderr reached EOF together with stdout; neither may be reported unreadable");
		_ = elapsed.Elapsed.Should().BeGreaterThan(
			OldBlindWindow,
			"the child wrote after the old window, so a pass here cannot be explained by the child having finished early");
		_ = elapsed.Elapsed.Should().BeGreaterThan(
			SlowWriteDelay - TimeSpan.FromMilliseconds(500),
			"the stub's own contract is that the payload lands after this delay; if it did not, the test would be vacuous");
	}

	[Fact]
	public async Task NetworkRunner_HealthyChildWritingAfterTheOldWindow_StillYieldsItsRealOutput()
	{
		using SlowOutputStub stub = SlowOutputStub.Create();
		NetworkHelperProcessRunner runner = new(() => stub.ExecutablePath, TimeSpan.FromSeconds(10));

		NetworkHelperProcessResult result = await runner.RunAsync(
			stub.Command,
			new string('x', 5_000_000),
			CancellationToken.None);

		_ = result.Started.Should().BeTrue();
		_ = result.ExitCode.Should().Be(0);
		_ = result.StandardOutput.Should().Contain(
			SlowOutputStub.PayloadToken,
			"the network runner shares the drain rule, so it must be fixed by the same change - not by a second constant");
	}

	// THE INTERPRETATION SEAM. The runner-level tests above prove the runner now returns
	// real output. This one proves the CLIENT no longer turns a slow-but-healthy run into a
	// failure: with the old behaviour the marker reached the client, and the client mapped a
	// whitespace-free, non-JSON marker to a malformed-response rejection. Written against the
	// client so the NEW-2 CONSEQUENCE is pinned end to end, not just the runner's string.
	[Fact]
	public async Task NetworkClient_SlowButHealthyChild_IsNotRejectedForItsOutput()
	{
		const string Resolution =
			"{\"dispatch\":0,\"originalUrl\":\"https://example.test/\",\"effectiveUrl\":\"https://example.test/target\",\"errorCode\":null,\"fromCache\":false}";
		NetworkHelperClient client = new(new FixedNetworkRunner(
			new NetworkHelperProcessResult(true, 0, Resolution, string.Empty)));

		ShortLinkResolveResponse response = await client.ResolveShortLinkAsync(
			new ShortLinkResolveRequest("https://example.test/", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched, "a completed helper whose output arrived late is a success, not a failure");
		_ = response.ErrorCode.Should().BeNull("the old marker produced network.helper-malformed-response for a run that actually succeeded");
		_ = response.EffectiveUrl.Should().Be("https://example.test/target");
	}

	// NEW-3 REVERTED, AT THE RUNNER. The exited-child branch must NOT attempt a tree kill. The
	// child EXITS BY ITSELF before the write lands (the same FastExitStub the classification
	// test uses), so the run reaches the `HasExited: true` branch - and the runner must leave
	// it WITHOUT a kill.
	//
	// THE OBSERVER SEAM IS NEGATIVE HERE, AND THAT IS WHAT MAKES IT NON-TAUTOLOGICAL. The old
	// assertion was `killedPid != 0`, but `_killObserver(process.Id)` fired unconditionally,
	// IMMEDIATELY BEFORE the Kill - so disabling the Kill kept the assertion green and the reap
	// was effectively untested. This assertion is the other way round: the observer fires ONLY
	// where a kill is ATTEMPTED ON A LIVE PROCESS - it sits AFTER the `HasExited` guard in
	// KillAbandonedProcess - so `killedPid == 0` pins that NO DIRECT tree-kill was introduced on
	// the exited path. A kill routed through KillAbandonedProcess on that path would be a no-op
	// the observer cannot see, so this is a guard against re-introducing the BYPASSING reap the
	// removal was about, not a general "no kill happened" oracle. A re-introduced bypassing reap
	// makes it fail (the audit PROVED that by mutation); whether the uncommitted pre-fix
	// revision would have failed it is not decidable from git history. See the note after the
	// two tests below on why a seam-independent real-descendant variant was dropped.
	[Fact]
	public async Task LaunchRunner_ChildExitsBeforeTheWrite_DoesNotReapTheExitedTree_WithoutChangingTheResult()
	{
		using FastExitStub stub = FastExitStub.Create(CommandInterpreter());
		int killedPid = 0;
		LaunchHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromSeconds(30),
			pid => killedPid = pid,
			forcedGenericFailure: null);

		LaunchHelperProcessResult result = await runner.RunAsync(
			"/c exit 0",
			new string('x', 5_000_000),
			CancellationToken.None);

		// THE CLASSIFICATION HALF MUST BE UNTOUCHED.
		_ = result.Started.Should().BeTrue("the helper exited on its own; the classification fix must not regress");
		_ = result.ExitCode.Should().Be(0, "the child's real exit code is still the outcome");
		_ = result.StandardError.Should().NotContain("helper-process-start-exception");

		// THE SAFETY HALF. The observer records the PID the runner chose to kill; ZERO means
		// no DIRECT tree-kill was attempted on the exited path.
		_ = killedPid.Should().Be(
			0,
			"the exited-child branch must NOT reap the tree: a tree kill on an already-exited parent re-opens the process by numeric PID (PID-reuse hazard) and would kill the browser a successful launch left running");
	}

	[Fact]
	public async Task NetworkRunner_ChildExitsBeforeTheWrite_DoesNotReapTheExitedTree_WithoutChangingTheResult()
	{
		using FastExitStub stub = FastExitStub.Create(CommandInterpreter());
		int killedPid = 0;
		NetworkHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromSeconds(30),
			pid => killedPid = pid,
			forcedGenericFailure: null);

		NetworkHelperProcessResult result = await runner.RunAsync(
			"/c exit 0",
			new string('x', 5_000_000),
			CancellationToken.None);

		_ = result.Started.Should().BeTrue();
		_ = result.ExitCode.Should().Be(0);
		_ = killedPid.Should().Be(
			0,
			"the exited-child branch must NOT reap the tree: a tree kill on an already-exited parent re-opens the process by numeric PID and can kill an unrelated process (see the NEW-3 REVERT note in HelperProcessAbandonment)");
	}

	// THE SEAM-INDEPENDENT PIN IS DELIBERATELY ABSENT, and the reason is honest rather than
	// convenient. A test that observed a REAL surviving descendant would be the strongest
	// possible evidence, but it cannot be built precisely on Windows: a descendant started so
	// that it shares the parent's console (needed to make the parent its CREATOR PID, the link
	// the dangerous reap walks) also holds the parent's redirected stdout/stderr open, so the
	// run never reaches the exited-child branch - it takes the timeout branch instead. A
	// descendant spawned NOT to inherit handles (powershell Start-Process) gets clean EOF but
	// is no longer the parent's direct child, so a re-introduced reap does NOT reach it and the
	// test would be green for the wrong reason - exactly the tautology (N-1) this correction
	// removes. Such a test would therefore be WORSE than no test. The seam-negative assertions
	// above are precise for this path: the observer fires ONLY where a kill is attempted on a
	// LIVE process, so their failure is caused by the bypassing reap and nothing else.

	// THE DRAIN WINDOW IS DERIVED FROM THE RUNNER'S BUDGET, NOT FROM A BLIND CONSTANT (NEW-2).
	// Driven directly so the rule is pinned without depending on cmd's console timing: a read
	// task that completes AFTER the old 2 s window but well inside the runner's 5 s budget must
	// yield its REAL payload. On the old code this returned the unreadable marker.
	[Fact]
	public async Task ReadCompletedResult_ReadCompletesAfterTheOldWindow_WithinTheBudget_YieldsTheRealPayload()
	{
		(Process process, Task<string> slowButHealthy) = StartExitedChildWithSlowRead(TimeSpan.FromSeconds(3));
		try
		{
			Stopwatch elapsed = Stopwatch.StartNew();
			(int exitCode, string stdout, string _) = HelperProcessAbandonment.ReadCompletedResult(
				process,
				slowButHealthy,
				Task.FromResult(string.Empty),
				drainBudget: TimeSpan.FromSeconds(5));
			elapsed.Stop();

			_ = exitCode.Should().Be(0);
			_ = stdout.Should().Be(
				SlowReadPayload,
				"the output arrived after the old blind 2 s window but inside the runner's budget, so truncating it would misreport a successful run");
			_ = elapsed.Elapsed.Should().BeGreaterThan(
				OldBlindWindow,
				"the payload arrived later than the old window, so a pass cannot be explained by the old rule having sufficed");
		}
		finally
		{
			process.Dispose();
		}

		await Task.CompletedTask;
	}

	// THE UPPER BOUND STILL HOLDS. A read that never completes must be abandoned at the
	// CEILING even when the caller hands in a budget larger than it, so this path can never
	// become an unbounded stall. The ceiling applies PER STREAM, so the worst case for the two
	// streams is 2 x DrainCeiling; the assertion is written against that product so it stays
	// true if the stream set ever changes.
	[Fact]
	public void ReadCompletedResult_BudgetLargerThanTheCeiling_IsStillBoundedByTheCeiling()
	{
		_ = HelperProcessAbandonment.DrainCeiling.Should().Be(
			TimeSpan.FromSeconds(10),
			"the ceiling is the hard upper bound on any single stream drain, independent of the caller's budget");

		(Process process, Task<string> never) = StartExitedChildWithUnreadPipe();
		try
		{
			TimeSpan absoluteWorstCase = HelperProcessAbandonment.DrainCeiling + HelperProcessAbandonment.DrainCeiling;
			Stopwatch elapsed = Stopwatch.StartNew();
			(int _, string stdout, string stderr) = HelperProcessAbandonment.ReadCompletedResult(
				process,
				never,
				never,
				drainBudget: TimeSpan.FromMinutes(5));
			elapsed.Stop();

			_ = stdout.Should().Be(HelperProcessAbandonment.OutputUnreadableMarker);
			_ = stderr.Should().Be(HelperProcessAbandonment.OutputUnreadableMarker);
			_ = elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(HelperProcessAbandonment.DrainCeiling - TimeSpan.FromSeconds(1));
			_ = elapsed.Elapsed.Should().BeLessThan(absoluteWorstCase + TimeSpan.FromSeconds(5));
		}
		finally
		{
			process.Dispose();
		}
	}

	// ONE SHARED DEADLINE FOR BOTH STREAMS. Two reads that never complete must be abandoned
	// after ONE window, not one window each: with a 1.5 s budget the old per-stream drain took
	// about 3 s, the shared deadline about 1.5 s.
	[Fact]
	public void ReadCompletedResult_BothStreamsStalled_ShareOneDrainWindow()
	{
		(Process process, Task<string> never) = StartExitedChildWithUnreadPipe();
		try
		{
			Stopwatch elapsed = Stopwatch.StartNew();
			(int _, string stdout, string stderr) = HelperProcessAbandonment.ReadCompletedResult(
				process,
				never,
				never,
				drainBudget: TimeSpan.FromMilliseconds(1500));
			elapsed.Stop();

			_ = stdout.Should().Be(HelperProcessAbandonment.OutputUnreadableMarker);
			_ = stderr.Should().Be(HelperProcessAbandonment.OutputUnreadableMarker);
			_ = elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(2600), "the two streams share one 1.5 s deadline");
		}
		finally
		{
			process.Dispose();
		}
	}

	// The async form used by the runners' catch paths: same shared deadline, awaited, and a
	// stream that does deliver inside the window still yields its payload.
	[Fact]
	public async Task ReadCompletedResultAsync_OneStreamStalled_OneDelivered_SharesTheDeadlineAndKeepsThePayload()
	{
		(Process process, Task<string> never) = StartExitedChildWithUnreadPipe();
		try
		{
			Stopwatch elapsed = Stopwatch.StartNew();
			(int exitCode, string stdout, string stderr) = await HelperProcessAbandonment.ReadCompletedResultAsync(
				process,
				Task.FromResult("payload"),
				never,
				drainBudget: TimeSpan.FromMilliseconds(1500));
			elapsed.Stop();

			_ = exitCode.Should().Be(0);
			_ = stdout.Should().Be("payload");
			_ = stderr.Should().Be(HelperProcessAbandonment.OutputUnreadableMarker);
			_ = elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(2600));
		}
		finally
		{
			process.Dispose();
		}
	}

	// A delivered stream keeps its payload when the OTHER stream was cancelled or faulted.
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ReadCompletedResultAsync_OneStreamDelivers_OtherCancelledOrFaulted_KeepsThePayload(bool cancelled)
	{
		(Process process, Task<string> _) = StartExitedChildWithUnreadPipe();
		try
		{
			Task<string> failed = cancelled
				? Task.FromCanceled<string>(new CancellationToken(canceled: true))
				: Task.FromException<string>(new IOException("pipe broke"));
			(int _, string stdout, string stderr) = await HelperProcessAbandonment.ReadCompletedResultAsync(
				process,
				Task.FromResult("payload"),
				failed,
				drainBudget: TimeSpan.FromSeconds(1));

			_ = stdout.Should().Be("payload");
			_ = stderr.Should().Be(HelperProcessAbandonment.OutputUnreadableMarker);
		}
		finally
		{
			process.Dispose();
		}
	}

	// A cancelled or faulted read yields the explicit marker, never an empty string or a throw.
	[Fact]
	public async Task ReadCompletedResultAsync_CancelledAndFaultedReads_YieldTheMarker()
	{
		(Process process, Task<string> _) = StartExitedChildWithUnreadPipe();
		try
		{
			(int _, string stdout, string stderr) = await HelperProcessAbandonment.ReadCompletedResultAsync(
				process,
				Task.FromCanceled<string>(new CancellationToken(canceled: true)),
				Task.FromException<string>(new IOException("pipe broke")),
				drainBudget: TimeSpan.FromSeconds(1));

			_ = stdout.Should().Be(HelperProcessAbandonment.OutputUnreadableMarker);
			_ = stderr.Should().Be(HelperProcessAbandonment.OutputUnreadableMarker);
		}
		finally
		{
			process.Dispose();
		}
	}

	// THE EQUALITY CASE OF RESOLVE-DRAIN-WINDOW IS NOT OBSERVABLE BY VALUE, so no test is kept
	// for it. The rule is total:
	//   budget <= 0                 -> ceiling
	//   0 < budget <= DrainCeiling  -> budget
	//   budget >  DrainCeiling      -> ceiling
	// At budget == DrainCeiling BOTH the `<=` and the `<` forms return the SAME value (the budget
	// IS the ceiling), so the choice between them is provably indistinguishable from outside.
	// A mutation flipping ResolveDrainWindow's `<=` to `<` keeps every observable outcome
	// identical, which was confirmed by the audit (23/23 and 788/788 stayed green). An earlier
	// test here was named ..._BudgetEqualToTheCeiling_TreatsItAsAValidBudget and promised a
	// boundary pin it could not deliver: it could not fail on that reversal, so it was decorative.
	// It was DELETED rather than renamed-and-softened, because a renamed test would still sit on
	// the boundary and invite the same false confidence later.
	//
	// WHAT IS STILL PINNED, and it is the load-bearing half: the non-positive arm below IS
	// observable (a budget of 0 or less must fall back to the ceiling, not to a zero window), and
	// the ceiling arm is pinned by the large-budget test above. This is stated plainly rather than
	// hidden: the equality case is genuinely unobservable by value, so it is documented, not tested.

	// THE NON-POSITIVE REGIME (N-4). A budget of zero or less cannot govern a drain, so the
	// ceiling is used - refusing to drain at all would defeat the purpose of the branch, and a
	// negative window would make Wait() return immediately as if the read had failed. Both arms
	// are pinned against a read that completes only after a delay, so a wrongly-zero window would
	// return the unreadable marker instead of the payload.
	[Theory]
	[InlineData(0)]
	[InlineData(-5000)]
	public async Task ReadCompletedResult_NonPositiveBudget_FallsBackToTheCeiling(int budgetMilliseconds)
	{
		(Process process, Task<string> slowButHealthy) = StartExitedChildWithSlowRead(TimeSpan.FromSeconds(3));
		try
		{
			(int exitCode, string stdout, string _) = HelperProcessAbandonment.ReadCompletedResult(
				process,
				slowButHealthy,
				Task.FromResult(string.Empty),
				drainBudget: TimeSpan.FromMilliseconds(budgetMilliseconds));
			_ = exitCode.Should().Be(0);
			_ = stdout.Should().Be(
				SlowReadPayload,
				"a non-positive budget falls back to the ceiling rather than refusing to drain, so the delayed read still yields its real payload");
		}
		finally
		{
			process.Dispose();
		}

		await Task.CompletedTask;
	}

	private static string CommandInterpreter()
	{
		string commandInterpreter = Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe";
		_ = File.Exists(commandInterpreter).Should().BeTrue($"these probes need a real child process; '{commandInterpreter}' was not found");
		return commandInterpreter;
	}

	// A REAL, already-exited child plus a read task that completes only after `delay` - the
	// slow-but-healthy shape. The child is `cmd /c exit 0`; the delayed completion stands in
	// for output that arrives after the old blind window while the helper is still within its
	// own budget.
	private static (Process Process, Task<string> SlowRead) StartExitedChildWithSlowRead(TimeSpan delay)
	{
		ProcessStartInfo startInfo = new()
		{
			FileName = CommandInterpreter(),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		startInfo.ArgumentList.Add("/c exit 0");

		Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("the probe child did not start");
		process.StandardInput.Close();
		_ = process.WaitForExit(10000);

		Task<string> slowRead = Task.Delay(delay).ContinueWith(
			_ => SlowReadPayload,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
		return (process, slowRead);
	}

	// A REAL, already-exited child plus a read task that NEVER completes - the exact inputs
	// the drain must survive. The child is `cmd /c exit 0`; the returned Task is a TCS that
	// is deliberately never set, standing in for a read whose pipe is held open by a
	// descendant. This keeps the "unreadable" property deterministic instead of depending on
	// cmd's console arbitration.
	private static (Process Process, Task<string> Unreadable) StartExitedChildWithUnreadPipe()
	{
		ProcessStartInfo startInfo = new()
		{
			FileName = CommandInterpreter(),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		startInfo.ArgumentList.Add("/c exit 0");

		Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("the probe child did not start");
		process.StandardInput.Close();
		_ = process.WaitForExit(10000);

		TaskCompletionSource<string> neverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
		return (process, neverCompletes.Task);
	}

	// A REAL child that CLOSES ITS STDIN AT ONCE and then writes its payload only after
	// SlowWriteDelay, so the runner's write fails while the child is still working and the
	// output lands well beyond the old blind 2 s window. Built from a copy of cmd.exe because
	// the runners start their child from a path and a uniquely named image keeps the probe
	// precise.
	//
	// `set /p x= <nul` performs one stdin read from the NUL device and returns immediately
	// without consuming the inherited pipe; the child still holds the pipe open (it has not
	// exited), so the parent's multi-megabyte write is the side that trips. This mirrors the
	// real helpers, which answer and return before draining a large request.
	private sealed class SlowOutputStub : IDisposable
	{
		public const string PayloadToken = "SLOW-BUT-HEALTHY-OUTPUT";

		private SlowOutputStub(string directoryPath, string executablePath, string command)
		{
			DirectoryPath = directoryPath;
			ExecutablePath = executablePath;
			Command = command;
		}

		public string DirectoryPath { get; }

		public string ExecutablePath { get; }

		public string Command { get; }

		public static SlowOutputStub Create()
		{
			// Under the test process's temp directory, never the repository (P8).
			string directoryPath = Path.Combine(Path.GetTempPath(), "brochoos-slow-output-" + Guid.NewGuid().ToString("N")[..12]);
			_ = Directory.CreateDirectory(directoryPath);

			string processName = "brochoos-slow-" + Guid.NewGuid().ToString("N")[..12];
			string executablePath = Path.Combine(directoryPath, processName + ".exe");
			File.Copy(CommandInterpreter(), executablePath);

			// `ping -n 5` busy-waits ~4.09 s on this machine (measured), so the token is echoed
			// only after that - guaranteed to land beyond the old blind 2 s window with margin.
			string command = $"/c set /p ignored= <nul & ping -n 5 127.0.0.1 >nul & echo {PayloadToken}";

			return new SlowOutputStub(directoryPath, executablePath, command);
		}

		// A child still sleeping in its ping (a failing run, or one abandoned before the echo) is
		// killed first, verified by its full image path; then the folder goes, with one short
		// retry for an image the just-killed child still holds. Never fails the test.
		public void Dispose()
		{
			TestTempCleanup.KillSurvivors(ExecutablePath);
			TestTempCleanup.DeleteDirectory(DirectoryPath);
		}
	}

	// A REAL child that EXITS IMMEDIATELY, before it ever reads its stdin, so the parent's
	// write lands on a closed pipe every time instead of only ~13% of the time.
	private sealed class FastExitStub : IDisposable
	{
		private FastExitStub(string directoryPath, string executablePath)
		{
			DirectoryPath = directoryPath;
			ExecutablePath = executablePath;
		}

		public string DirectoryPath { get; }

		public string ExecutablePath { get; }

		public static FastExitStub Create(string commandInterpreter)
		{
			// Under the test process's temp directory, never the repository (P8).
			string directoryPath = Path.Combine(Path.GetTempPath(), "brochoos-fast-exit-" + Guid.NewGuid().ToString("N")[..12]);
			_ = Directory.CreateDirectory(directoryPath);

			string processName = "brochoos-fast-exit-" + Guid.NewGuid().ToString("N")[..12];
			string executablePath = Path.Combine(directoryPath, processName + ".exe");
			File.Copy(commandInterpreter, executablePath);

			return new FastExitStub(directoryPath, executablePath);
		}

		// Same cleanup as SlowOutputStub: kill a survivor verified by image path, then delete
		// with one short retry.
		public void Dispose()
		{
			TestTempCleanup.KillSurvivors(ExecutablePath);
			TestTempCleanup.DeleteDirectory(DirectoryPath);
		}
	}

	private sealed class FixedNetworkRunner(NetworkHelperProcessResult result) : INetworkHelperProcessRunner
	{
		private readonly NetworkHelperProcessResult _result = result;

		public Task<NetworkHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
		{
			_ = command;
			_ = requestPayload;
			_ = cancellationToken;
			return Task.FromResult(_result);
		}
	}
}
