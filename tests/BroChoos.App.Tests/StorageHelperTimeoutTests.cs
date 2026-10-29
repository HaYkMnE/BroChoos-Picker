using System.Diagnostics;
using BroChoos.App.HelperClients;
using BroChoos.Core.Abstractions;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// H1 (HIGH, independent deep audit): the Picker UI thread invoked the storage helper
// SYNCHRONOUSLY and StorageHelperProcessRunner had NO invocation budget at all - the launch
// helper was bounded at 5 s and the network helper at 1.7 s, and the storage helper was the
// one unbounded path. A hung StorageHelper therefore blocked the picker indefinitely.
//
// WHAT THIS PINS, both halves:
//   1. the runner owns a real budget and turns an expired one into the SAME typed shape the
//      launch and network runners use (Started: true, exit 124, "helper-process-timeout"), so
//      the wait is abandoned instead of running forever;
//   2. the client maps that shape to a distinct error code, so the failure surfaces in the
//      status line rather than being folded into a generic non-zero exit.
//
// The runner test drives a REAL child process with a short budget, so the abandoned wait is
// observed rather than assumed: the child is asked to stay alive for seconds, the budget
// expires first, and exit code 124 can only come from the timeout branch (the child's own
// exit code is 0).
//
// THE CHILD IS KILLED BY THE RUNNER when the budget expires, and the tests below pin that.
// The earlier wording here said "the child self-terminates, so no orphan is left behind",
// which was FALSE: the runner disposed the Process HANDLE, which does not terminate the
// child, so the helper kept running with the request already in its stdin - measured alive
// 1 s after storage.helper-timeout and finishing a catalog write 2.64 s after the timeout was
// reported to the user.
public sealed class StorageHelperTimeoutTests
{
	// The budget the launch helper already uses. Pinned as a VALUE so a future edit cannot
	// quietly turn the storage helper back into the unbounded path this finding is about.
	[Fact]
	public void Runner_HasAFiveSecondBudget_ParityWithTheLaunchHelper()
	{
		_ = StorageHelperProcessRunner.DefaultTimeout.Should().Be(
			TimeSpan.FromSeconds(5),
			"the storage helper must carry the same invocation budget as the launch helper; it was the only helper with none");
	}

	// The budget must survive into the instance the CONTAINER builds, not just the constant.
	// Program.cs registers this type and the container resolves the parameterless
	// constructor (MEASURED: it does, even with an internal two-argument constructor whose
	// parameter types are unregistered), so a parameterless constructor that stopped passing
	// DefaultTimeout would silently restore the unbounded behaviour while this suite stayed
	// green on the constant alone.
	[Fact]
	public void Runner_ConstructedForTheContainer_CarriesTheDefaultBudget()
	{
		StorageHelperProcessRunner runner = new();

		_ = runner.Timeout.Should().Be(StorageHelperProcessRunner.DefaultTimeout);
	}

	[Fact]
	public async Task Client_MapsAnExpiredBudget_ToTheTypedStorageHelperTimeout()
	{
		StorageHelperClient client = new(new TimeoutRunner());

		StorageDiscoveryResponse response = await client.DiscoverAndPersistAsync(
			new StorageDiscoveryRequest(null),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be(
			"storage.helper-timeout",
			"an expired budget is a distinct, recoverable failure and must not be reported as a plain non-zero exit");
		_ = response.Snapshot.Should().BeNull();
	}

	// Negative control: the new timeout branch must not swallow a genuine non-zero exit.
	[Fact]
	public async Task Client_NonZeroExitWithoutTheTimeoutMarker_StaysANonZeroExit()
	{
		StorageHelperClient client = new(new FixedRunner(new StorageHelperProcessResult(true, 4, string.Empty, "boom")));

		StorageDiscoveryResponse response = await client.DiscoverAndPersistAsync(
			new StorageDiscoveryRequest(null),
			CancellationToken.None);

		_ = response.ErrorCode.Should().Be("storage.helper-nonzero-exit");
	}

	// The runner's own budget, over a real child process. cmd.exe is used as the helper
	// stand-in because it is the one executable guaranteed to exist on Windows and its
	// argument list is passed structurally, exactly as the runner does for the real helper.
	[Fact]
	public async Task Runner_ExpiresTheBudget_InsteadOfWaitingForAHungHelper()
	{
		string commandInterpreter = Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe";
		_ = File.Exists(commandInterpreter).Should().BeTrue($"the timeout probe needs a real child process; '{commandInterpreter}' was not found");

		StorageHelperProcessRunner runner = new(
			() => commandInterpreter,
			TimeSpan.FromMilliseconds(300));

		Stopwatch elapsed = Stopwatch.StartNew();
		StorageHelperProcessResult result = await runner.RunAsync("/c ping -n 3 127.0.0.1", "{}", CancellationToken.None);
		elapsed.Stop();

		_ = result.Started.Should().BeTrue("the helper process did start; only the WAIT expired");
		_ = result.ExitCode.Should().Be(
			124,
			"124 is the typed timeout exit code the launch and network runners already use");
		_ = result.StandardError.Should().Contain("helper-process-timeout");
		_ = elapsed.Elapsed.Should().BeLessThan(
			TimeSpan.FromSeconds(10),
			"the budget must end the wait rather than the child's own exit (which takes ~2 s here)");
	}

	// The caller's own cancellation must NOT be reported as a helper timeout: the `when`
	// guard on the catch is what keeps the two apart, and a caller that cancelled asked for
	// the wait to stop, not for a diagnosis about the helper.
	[Fact]
	public async Task Runner_CallerCancellation_IsNotReportedAsAHelperTimeout()
	{
		string commandInterpreter = Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe";
		StorageHelperProcessRunner runner = new(() => commandInterpreter, TimeSpan.FromSeconds(30));

		using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(250));
		StorageHelperProcessResult result = await runner.RunAsync("/c ping -n 3 127.0.0.1", "{}", cancellation.Token);

		_ = result.ExitCode.Should().NotBe(124, "the caller cancelled; that is not a diagnosis about the helper");
	}

	// THE ORPHAN, pinned against a REAL child. A reported timeout used to abandon only the
	// WAIT: disposing the Process handle does not terminate the child, so the helper stayed
	// alive with the request already in its stdin and could still write afterwards (MEASURED:
	// alive 1 s after storage.helper-timeout, catalog write completed 2.64 s after it).
	//
	// The stub is a COPY of cmd.exe with a unique file name, so its liveness is observable
	// through GetProcessesByName without matching any unrelated cmd.exe on the machine - the
	// probe only sees processes whose image name is this test's own. It writes `started.txt`
	// immediately and `late.txt` after ~6.1 s of work; the budget expires at 1.5 s.
	//
	// TWO INDEPENDENT ASSERTIONS, each of which the un-killed child fails:
	//  1. LIVENESS inside a 2 s window. The child's natural remaining life at the timeout is
	//     ~4.6 s, so a child that was not killed is still running when this window closes -
	//     the window is deliberately SHORTER than that life, which is what ties the pass to
	//     the kill rather than to the child eventually exiting on its own.
	//  2. THE LATE WRITE, observed after the child's whole natural lifetime has elapsed. This
	//     is the user-visible consequence: a write the user was told had failed.
	[Fact]
	public async Task Runner_ExpiredBudget_KillsTheChild_SoNoLateWriteHappens()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());

		// FLAKE FIX (observed 1 failure in 5 full runs: `stub.Started` was False). The budget
		// must cover cmd.exe starting AND writing started.txt; 500 ms is not enough when the
		// other test classes (which run in parallel with this one) are loading the machine.
		// 1.5 s absorbs that start-up latency, and the stub lives long enough (see StubHelper)
		// that the liveness window below is still shorter than its remaining life.
		StorageHelperProcessRunner runner = new(() => stub.ExecutablePath, TimeSpan.FromMilliseconds(1500));

		Stopwatch elapsed = Stopwatch.StartNew();
		StorageHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue("the child did start; only the WAIT was abandoned");
		_ = result.ExitCode.Should().Be(124, "the budget expired, which is the typed timeout marker");
		_ = stub.Started.Should().BeTrue("the stub must have begun work before the budget expired, or the late write would be untestable");

		// 1. The child must be DEAD once the typed outcome is returned. The 2 s window absorbs
		// kernel teardown latency (measured at ~50 ms) without reaching the child's own exit.
		_ = (await stub.WaitForExitAsync(TimeSpan.FromSeconds(2)))
			.Should()
			.BeTrue("the runner killed the child it abandoned; a child still running here is the orphan this test exists to catch");

		// 2. The late write must never appear, observed after the child's whole natural
		// lifetime has passed so the check cannot pass merely because the file is not due yet.
		await StubHelper.WaitOutTheNaturalLifetimeAsync(elapsed.Elapsed);
		_ = stub.LateMarkerExists.Should().BeFalse(
			"the abandoned child was killed, so the write it would have made after the timeout must not occur");
	}

	// The SAME proof for the caller-cancellation path, which abandons the wait for a
	// different reason but orphans the child identically. The typed outcome must stay what it
	// always was (Started false, exit -1) - the kill is not allowed to change the report.
	[Fact]
	public async Task Runner_CallerCancellation_KillsTheChild_WithoutChangingTheTypedOutcome()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());

		StorageHelperProcessRunner runner = new(() => stub.ExecutablePath, TimeSpan.FromSeconds(30));

		Stopwatch elapsed = Stopwatch.StartNew();
		using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));
		StorageHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", cancellation.Token);

		_ = result.Started.Should().BeFalse("a caller cancellation is not a diagnosis about the helper and keeps the existing shape");
		_ = result.ExitCode.Should().Be(-1);
		_ = result.StandardError.Should().Contain("helper-process-start-exception");

		_ = (await stub.WaitForExitAsync(TimeSpan.FromSeconds(2)))
			.Should()
			.BeTrue("the cancelled wait abandoned the child, so the child must be killed for the same reason as a timeout");

		await StubHelper.WaitOutTheNaturalLifetimeAsync(elapsed.Elapsed);
		_ = stub.LateMarkerExists.Should().BeFalse("a cancelled exchange must not leave a child that finishes the work afterwards");
	}

	// The kill must fire ONLY on the abandoned paths. A child that exits on its own still
	// reports its REAL exit code, because a regression that killed unconditionally would
	// turn every successful helper invocation into a timeout or a kill failure.
	[Fact]
	public async Task Runner_NormalCompletion_KeepsTheRealExitCode_AndIsNotKilled()
	{
		string commandInterpreter = CommandInterpreter();
		StorageHelperProcessRunner runner = new(() => commandInterpreter, TimeSpan.FromSeconds(30));

		StorageHelperProcessResult result = await runner.RunAsync("/c exit 7", "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue();
		_ = result.ExitCode.Should().Be(7, "the child's own exit code is the outcome; the kill must not touch the normal path");
		_ = result.StandardError.Should().NotContain("helper-process-timeout");
	}

	// THE THIRD ABANDONED PATH (D-13 / D-41 / D-42). The tests above cover the timeout and
	// caller-cancellation paths, both of which ALREADY killed. This one covers the GENERIC
	// catch: a NON-cancellation failure raised AFTER Process.Start has already run, which used
	// to return the start-exception result while the started child kept running - and for the
	// storage helper that means it could still complete a catalog write.
	//
	// WHY THIS IS A SEAM TEST AND NOT A REAL-PROCESS TEST, stated plainly. The generic catch is
	// REACHABLE with a real child (MEASURED: a child that closes its own stdin makes the
	// parent's next write throw IOException), but by the time that exception surfaces the child
	// has ALWAYS already exited - the write is buffered and the failure is only observed on a
	// later flush. A real-process test therefore cannot tell "the runner killed it" apart from
	// "it was gone anyway", so it could not fail on the pre-fix code. The run is made
	// deterministic by forcing the same exception type at the same point, and the kill is
	// observed through the KillObserver seam. The child is a real process, so the assertion is
	// about a real PID.
	[Fact]
	public async Task Runner_GenericFailureAfterStart_KillsTheStartedChild()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		int killedPid = 0;
		StorageHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromSeconds(30),
			pid => killedPid = pid,
			new IOException("forced non-cancellation failure after start"));

		StorageHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", CancellationToken.None);

		_ = result.Started.Should().BeFalse("the generic path keeps the shape it always had: Started false, exit -1");
		_ = result.ExitCode.Should().Be(-1);
		_ = result.StandardError.Should().Contain("helper-process-start-exception");

		_ = killedPid.Should().NotBe(
			0,
			"the generic catch must reap the child Process.Start already launched - without this a helper holding the discovery request is leaked");
	}

	// The forced-failure seam must NOT leak into production behaviour: with no forced failure
	// and no observer set, the runner still behaves exactly as before on the normal path. This
	// is the guard against the seam itself becoming the defect.
	[Fact]
	public async Task Runner_WithoutForcedFailure_StillReportsTheRealExitCode()
	{
		StorageHelperProcessRunner runner = new(() => CommandInterpreter(), TimeSpan.FromSeconds(30));

		StorageHelperProcessResult result = await runner.RunAsync("/c exit 17", "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue();
		_ = result.ExitCode.Should().Be(17, "the seam must be inert unless a test sets it");
	}

	// THE PIPE-CLOSE RACE (HIGH regression introduced by the kill fix above). A helper that
	// exits FAST - before the parent has finished writing the request to its stdin - makes the
	// parent's next write throw System.IO.IOException ("The pipe is being closed." /
	// ERROR_BROKEN_PIPE). That is a NORMAL-SUCCESSFUL-RUN artifact: the child RAN and did its
	// work. The generic catch, however, classified EVERY non-cancellation exception as
	// Started=false / "helper-process-start-exception", so the client mapped the run to
	// storage.helper-not-found and the user was told a helper that had in fact run had FAILED.
	//
	// MEASURED before the fix (256 real child runs): 76, 128 and 110 runs misreported, with
	// wrongExit = 0 in every run - the exit code was never wrong, only the CLASSIFICATION was.
	//
	// The child is FORCED to exit before the write (Environment.Exit(0) before its own
	// Console.In.ReadToEnd()), so the write is guaranteed to arrive at a closed pipe - the
	// natural race, made deterministic. It is a COPY of cmd.exe with a unique image name, so
	// the test owns a real PID and can prove the child is gone.
	[Fact]
	public async Task Runner_ChildExitsBeforeTheWrite_ReportsTheRealResult_NotAStartFailure()
	{
		using FastExitStub stub = FastExitStub.Create(CommandInterpreter());
		int killedPid = 0;
		StorageHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromSeconds(30),
			pid => killedPid = pid,
			forcedGenericFailure: null);

		StorageHelperProcessResult result = await runner.RunAsync(
			"/c exit 0",
			new string('x', 5_000_000),
			CancellationToken.None);

		_ = result.Started.Should().BeTrue(
			"the helper exited on its own before the write reached it; the run concluded normally and must not be reported as a start failure");
		_ = result.ExitCode.Should().Be(
			0,
			"the child's ACTUAL exit code is the truthful outcome - this is the value that was wrong before the fix, and wrongExit was 0 in every MEASURED run");
		_ = result.StandardError.Should().NotContain(
			"helper-process-start-exception",
			"the start-exception marker means 'the runner never started the helper', which is false here");
		// THE EXITED-CHILD BRANCH ATTEMPTS NO KILL (NEW-3 REVERTED). This assertion was
		// `killedPid != 0` while the exited-child branch reaped the tree; but the observer then
		// fired unconditionally BEFORE the Kill, so disabling the Kill left it green - the reap
		// was effectively untested (N-1). It is now the negative form: the observer fires ONLY
		// where a kill is ATTEMPTED ON A LIVE PROCESS - it sits AFTER the `HasExited` guard in
		// KillAbandonedProcess - so `killedPid == 0` pins that no DIRECT tree-kill was
		// introduced on the exited path (a kill routed through KillAbandonedProcess there would
		// be a no-op the observer cannot see). It is a guard against re-introducing that
		// BYPASSING reap, not a general "no kill happened" oracle.
		// A tree kill on an already-exited parent re-opens the process by numeric PID (PID-reuse
		// hazard) and can terminate an unrelated process; the still-running path still kills,
		// because there the child is genuinely alive and the kill is safe.
		_ = killedPid.Should().Be(
			0,
			"the exited-child branch must NOT reap the tree: a stale-PID tree kill can terminate an unrelated process; the safe kill lives on the still-running path");

		// Both halves of the pipe-close shape: either the child exited before the pipe broke
		// (IOException) or it was already reaped by the time the write observed the closure.
		// Either way the outcome above must be the real result, and the two markers below
		// distinguish "started child, output collected" from "started child, output unreadable".
		_ = (result.StandardError.Length == 0
			|| result.StandardError.Contains("helper-process-output-unreadable", StringComparison.Ordinal))
			.Should()
			.BeTrue("output that can still be read is returned; output that cannot is reported with the dedicated marker, never as a start failure");
	}

	// The SAME race for the CALLER-CANCELLATION path is deliberately NOT changed here: a
	// cancelled call is a decision by the caller, not an artifact of the child's speed, and
	// the existing test (Runner_CallerCancellation_KillsTheChild_WithoutChangingTheTypedOutcome)
	// pins that the kill still fires and the typed outcome is preserved.

	private static string CommandInterpreter()
	{
		string commandInterpreter = Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe";
		_ = File.Exists(commandInterpreter).Should().BeTrue($"the timeout probes need a real child process; '{commandInterpreter}' was not found");
		return commandInterpreter;
	}

	// A REAL child process that is uniquely identifiable and writes a marker AFTER the window
	// in which the runner's budget expires. Built from a copy of cmd.exe because the runner
	// starts its child from a path, and a copied image is the one way to make "is THIS child
	// still running?" answerable without matching an unrelated cmd.exe.
	//
	// The stub's own lifetime is a contract of the test, not an incidental detail: the liveness
	// window (2 s) and the late-write window are both chosen against it, and `Started` guards
	// against the stub finishing before the budget expires - if that ever happened the test
	// would fail loudly rather than pass vacuously.
	private sealed class StubHelper : IDisposable
	{
		private const string StartedMarkerFileName = "started.txt";
		private const string LateMarkerFileName = "late.txt";

		// `ping -n 7` waits six one-second intervals, ~6.1 s. The declared lifetime is the
		// conservative UPPER bound (7 s), so "the late write is due by now" cannot be claimed
		// before the stub could possibly have written it. It was `-n 5` / 5 s with a 500 ms
		// budget; the longer life keeps >= 4.5 s of it remaining after the 1.5 s budget, so the
		// 2 s liveness window stays well inside the child's natural life.
		private static readonly TimeSpan NaturalLifetime = TimeSpan.FromSeconds(7);

		private StubHelper(string directoryPath, string executablePath, string processName, string command)
		{
			DirectoryPath = directoryPath;
			ExecutablePath = executablePath;
			ProcessName = processName;
			Command = command;
		}

		public string DirectoryPath { get; }

		public string ExecutablePath { get; }

		public string ProcessName { get; }

		public string Command { get; }

		public bool Started => File.Exists(Path.Combine(DirectoryPath, StartedMarkerFileName));

		public bool LateMarkerExists => File.Exists(Path.Combine(DirectoryPath, LateMarkerFileName));

		public static StubHelper Create(string commandInterpreter)
		{
			// Under the test process's temp directory, never the repository: a test that wrote
			// into the working tree would pollute it (P8).
			string directoryPath = Path.Combine(Path.GetTempPath(), "brochoos-storage-orphan-" + Guid.NewGuid().ToString("N")[..12]);
			_ = Directory.CreateDirectory(directoryPath);

			// A unique image name makes GetProcessesByName precise. cmd.exe is copied rather
			// than reimplemented because the runner starts its child from a real path.
			string processName = "brochoos-stub-" + Guid.NewGuid().ToString("N")[..12];
			string executablePath = Path.Combine(directoryPath, processName + ".exe");
			File.Copy(commandInterpreter, executablePath);

			// Passed as ONE argument (ArgumentList), exactly as the runner does it, so the
			// stub's invocation shape cannot differ from the real helper's. Relative marker
			// paths resolve against the runner's WorkingDirectory, which is the stub's own
			// directory because that is where the executable lives.
			string command = $"/c echo started> {StartedMarkerFileName} & ping -n 7 127.0.0.1 >nul & echo late> {LateMarkerFileName}";

			return new StubHelper(directoryPath, executablePath, processName, command);
		}

		// Waits until the stub's whole natural lifetime has elapsed SINCE IT STARTED, measured
		// against the caller's stopwatch. Checking the late marker earlier would be able to
		// pass simply because the write was not due yet; this makes the check conclusive.
		public static async Task WaitOutTheNaturalLifetimeAsync(TimeSpan alreadyElapsed)
		{
			TimeSpan remaining = NaturalLifetime - alreadyElapsed;
			if (remaining > TimeSpan.Zero)
			{
				await Task.Delay(remaining);
			}
		}

		// Bounded: returns whether the child is gone within the window, so a failure reports
		// itself rather than hanging the suite.
		public async Task<bool> WaitForExitAsync(TimeSpan timeout)
		{
			DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

			while (DateTimeOffset.UtcNow < deadline)
			{
				if (!IsRunning())
				{
					return true;
				}

				await Task.Delay(50);
			}

			return !IsRunning();
		}

		private bool IsRunning()
		{
			return Process.GetProcessesByName(ProcessName).Length > 0;
		}

		// The executable is deleted on dispose, but a killed child's image can be briefly
		// locked; a failed delete must not fail the test that already proved its point.
		public void Dispose()
		{
			try
			{
				if (Directory.Exists(DirectoryPath))
				{
					Directory.Delete(DirectoryPath, recursive: true);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	// A REAL child that EXITS IMMEDIATELY, before it ever reads its stdin, so the parent's
	// write lands on a closed pipe every time instead of only ~13% of the time. `exit 0` runs
	// before cmd would ever read stdin, so the process is gone in milliseconds while the
	// parent is still streaming the request onto the pipe.
	//
	// WHY A COPY OF cmd.exe: the runner starts its child from a PATH, and a uniquely named
	// image is the one way to answer "is THIS test's child still running?" without matching an
	// unrelated cmd.exe on the machine - the same technique the orphan stubs in this file and
	// in HelperTimeoutKillTests use.
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

		// The executable is deleted on dispose, but a child that exits milliseconds before the
		// delete can still hold its image briefly; a failed delete must not fail a test that
		// already proved its point.
		public void Dispose()
		{
			try
			{
				if (Directory.Exists(DirectoryPath))
				{
					Directory.Delete(DirectoryPath, recursive: true);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	private sealed class TimeoutRunner : IStorageHelperProcessRunner
	{
		public Task<StorageHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
		{
			_ = command;
			_ = requestPayload;
			_ = cancellationToken;
			return Task.FromResult(new StorageHelperProcessResult(true, 124, string.Empty, "helper-process-timeout"));
		}
	}

	private sealed class FixedRunner(StorageHelperProcessResult result) : IStorageHelperProcessRunner
	{
		private readonly StorageHelperProcessResult _result = result;

		public Task<StorageHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
		{
			_ = command;
			_ = requestPayload;
			_ = cancellationToken;
			return Task.FromResult(_result);
		}
	}
}
