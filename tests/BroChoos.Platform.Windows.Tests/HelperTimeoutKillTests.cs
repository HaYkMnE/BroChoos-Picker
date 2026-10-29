using System.Diagnostics;
using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Network;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests;

// THE ORPHAN ON THE OTHER TWO HELPERS. The finding was runtime-proven on the storage helper
// (see tests/BroChoos.App.Tests/StorageHelperTimeoutTests.cs): a reported timeout disposed the
// Process HANDLE, which does NOT terminate the child, so the helper stayed alive with the
// request already in its stdin and finished its work afterwards. The launch helper (5 s budget)
// and the network helper (1.7 s budget) had the identical shape, and the launch one is the
// worst of the three: the work it finishes afterwards is OPENING A BROWSER, after the user was
// told the launch timed out.
//
// WHAT THESE TESTS PIN, per runner and against a REAL child process:
//   1. an expired budget kills the child it abandons (liveness inside a window SHORTER than the
//      child's natural remaining life, so a pass is caused by the kill rather than by the child
//      eventually exiting on its own);
//   2. the write the child would have made afterwards never happens;
//   3. a caller cancellation kills the child too, without changing the typed outcome;
//   4. normal completion still returns the child's REAL exit code - the kill must fire only on
//      the abandoned paths.
//
// The stub is a COPY of cmd.exe with a unique file name, so GetProcessesByName sees only THIS
// test's child and never an unrelated cmd.exe on the machine.
public sealed class HelperTimeoutKillTests
{
	// MEASURED on this machine: cmd's `ping -n 3` busy-waits ~2.09 s, reproducibly within 20 ms
	// across runs. The declared bound is deliberately generous (2.5 s) because it is used to
	// decide when the late write is OVERDUE.
	private static readonly TimeSpan StubNaturalLifetimeBound = TimeSpan.FromMilliseconds(2500);

	// Shorter than the child's natural remaining life at the moment the runner returns
	// (~2.09 s - budget), so an un-killed child is still running when this window closes.
	private static readonly TimeSpan LivenessWindow = TimeSpan.FromMilliseconds(1300);

	// The two ExpiredBudget tests below used to flake (~1 in 10 under load): the 300 ms budget
	// started right after Process.Start, and a freshly copied cmd.exe sometimes had not yet
	// written started.txt, so the "stub must have begun work" precondition failed. The runner's
	// beforeBudgetStartsForTests seam now waits (bounded at 10 s) for started.txt BEFORE the
	// budget exists, so the precondition is established rather than raced.
	[Fact]
	public async Task LaunchRunner_ExpiredBudget_KillsTheChild_SoNoLateLaunchRequestIsActedOn()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		LaunchHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromMilliseconds(300),
			observer: null,
			forcedGenericFailure: null,
			beforeBudgetStartsForTests: stub.WaitUntilStartedAsync);

		LaunchHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue("the child did start; only the WAIT was abandoned");
		_ = result.ExitCode.Should().Be(124, "124 is the typed timeout marker the client classifies on");
		_ = stub.Started.Should().BeTrue("the stub must have begun work before the budget expired, or the late write would be untestable");

		_ = (await stub.WaitForExitAsync(LivenessWindow))
			.Should()
			.BeTrue("the launch runner killed the child it abandoned; a still-running helper could still open a browser after the timeout was reported");

		await stub.WaitOutTheNaturalLifetimeSinceStartAsync();
		_ = stub.LateMarkerExists.Should().BeFalse("the abandoned child was killed, so the work it would have done after the timeout must not happen");
	}

	// The seam's CONTRACT, pinned directly: time spent in it is not charged to the budget. A
	// seam that takes 3x the budget, followed by a child that exits at once, must still yield
	// the child's real exit code. Were the budget's token source created before the seam,
	// this run would report the 124 timeout instead.
	[Fact]
	public async Task Runners_BeforeBudgetSeam_IsNotChargedToTheBudget()
	{
		static async Task SlowSeam(CancellationToken cancellationToken)
		{
			await Task.Delay(900, cancellationToken);
		}

		LaunchHelperProcessRunner launch = new(() => CommandInterpreter(), TimeSpan.FromMilliseconds(300), null, null, SlowSeam);
		NetworkHelperProcessRunner network = new(() => CommandInterpreter(), TimeSpan.FromMilliseconds(300), null, null, SlowSeam);

		LaunchHelperProcessResult launchResult = await launch.RunAsync("/c exit 5", "{}", CancellationToken.None);
		NetworkHelperProcessResult networkResult = await network.RunAsync("/c exit 6", "{}", CancellationToken.None);

		_ = launchResult.ExitCode.Should().Be(5, "the seam's 900 ms must not consume the 300 ms launch budget");
		_ = networkResult.ExitCode.Should().Be(6, "the seam's 900 ms must not consume the 300 ms network budget");
	}

	[Fact]
	public async Task LaunchRunner_CallerCancellation_KillsTheChild_WithoutChangingTheTypedOutcome()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		LaunchHelperProcessRunner runner = new(() => stub.ExecutablePath, TimeSpan.FromSeconds(30));

		Stopwatch elapsed = Stopwatch.StartNew();
		using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(400));
		LaunchHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", cancellation.Token);

		_ = result.Started.Should().BeFalse("a caller cancellation is not a diagnosis about the helper and keeps the existing shape");
		_ = result.ExitCode.Should().Be(-1);
		_ = result.StandardError.Should().Contain("helper-process-start-exception");

		_ = (await stub.WaitForExitAsync(LivenessWindow))
			.Should()
			.BeTrue("the cancelled wait abandoned the child, so the child must be killed for the same reason as a timeout");

		await StubHelper.WaitOutTheNaturalLifetimeAsync(elapsed.Elapsed);
		_ = stub.LateMarkerExists.Should().BeFalse("a cancelled launch must not leave a helper that opens the browser afterwards");
	}

	[Fact]
	public async Task LaunchRunner_NormalCompletion_KeepsTheRealExitCode()
	{
		LaunchHelperProcessRunner runner = new(() => CommandInterpreter(), TimeSpan.FromSeconds(30));

		LaunchHelperProcessResult result = await runner.RunAsync("/c exit 9", "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue();
		_ = result.ExitCode.Should().Be(9, "the child's own exit code is the outcome; the kill must not touch the normal path");
		_ = result.StandardError.Should().NotContain("helper-process-timeout");
	}

	// The production budget must survive into the instance the CONTAINER builds, not just the
	// constant: the parameterless constructor is what the container resolves, so one that
	// stopped passing DefaultTimeout would silently restore a different budget while a test on
	// the constant alone stayed green.
	[Fact]
	public void LaunchRunner_ConstructedForTheContainer_CarriesTheFiveSecondBudget()
	{
		LaunchHelperProcessRunner runner = new();

		_ = runner.Timeout.Should().Be(TimeSpan.FromSeconds(5), "the launch helper's budget is 5 s and was the reference the other two helpers were brought to parity with");
		_ = LaunchHelperProcessRunner.DefaultTimeout.Should().Be(runner.Timeout);
	}

	[Fact]
	public async Task NetworkRunner_ExpiredBudget_KillsTheChild_SoNoLateResolveHappens()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		NetworkHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromMilliseconds(300),
			observer: null,
			forcedGenericFailure: null,
			beforeBudgetStartsForTests: stub.WaitUntilStartedAsync);

		NetworkHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue("the child did start; only the WAIT was abandoned");
		_ = result.ExitCode.Should().Be(124, "124 is the typed timeout marker the client classifies on");
		_ = stub.Started.Should().BeTrue("the stub must have begun work before the budget expired, or the late write would be untestable");

		_ = (await stub.WaitForExitAsync(LivenessWindow))
			.Should()
			.BeTrue("the network runner killed the child it abandoned; a still-running helper could still complete its resolution afterwards");

		await stub.WaitOutTheNaturalLifetimeSinceStartAsync();
		_ = stub.LateMarkerExists.Should().BeFalse("the abandoned child was killed, so the work it would have done after the timeout must not happen");
	}

	[Fact]
	public async Task NetworkRunner_CallerCancellation_KillsTheChild_WithoutChangingTheTypedOutcome()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		NetworkHelperProcessRunner runner = new(() => stub.ExecutablePath, TimeSpan.FromSeconds(30));

		Stopwatch elapsed = Stopwatch.StartNew();
		using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(400));
		NetworkHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", cancellation.Token);

		_ = result.Started.Should().BeFalse("a caller cancellation is not a diagnosis about the helper and keeps the existing shape");
		_ = result.ExitCode.Should().Be(-1);
		_ = result.StandardError.Should().Contain("helper-process-start-exception");

		_ = (await stub.WaitForExitAsync(LivenessWindow))
			.Should()
			.BeTrue("the cancelled wait abandoned the child, so the child must be killed for the same reason as a timeout");

		await StubHelper.WaitOutTheNaturalLifetimeAsync(elapsed.Elapsed);
		_ = stub.LateMarkerExists.Should().BeFalse("a cancelled resolve must not leave a helper that finishes the request afterwards");
	}

	[Fact]
	public async Task NetworkRunner_NormalCompletion_KeepsTheRealExitCode()
	{
		NetworkHelperProcessRunner runner = new(() => CommandInterpreter(), TimeSpan.FromSeconds(30));

		NetworkHelperProcessResult result = await runner.RunAsync("/c exit 11", "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue();
		_ = result.ExitCode.Should().Be(11, "the child's own exit code is the outcome; the kill must not touch the normal path");
		_ = result.StandardError.Should().NotContain("helper-process-timeout");
	}

	[Fact]
	public void NetworkRunner_ConstructedForTheContainer_CarriesTheOnePointSevenSecondBudget()
	{
		NetworkHelperProcessRunner runner = new();

		_ = runner.Timeout.Should().Be(TimeSpan.FromMilliseconds(1700), "the network helper's budget is 1.7 s and is deliberately the tightest of the three");
		_ = NetworkHelperProcessRunner.DefaultTimeout.Should().Be(runner.Timeout);
	}

	// THE THIRD ABANDONED PATH (D-13 / D-41 / D-42). The two tests above cover the paths that
	// were already killing. This one covers the GENERIC catch: a NON-cancellation failure
	// raised AFTER Process.Start has already run, which used to return the start-exception
	// result while the started child kept running.
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
	public async Task LaunchRunner_GenericFailureAfterStart_KillsTheStartedChild()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		int killedPid = 0;
		LaunchHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromSeconds(30),
			pid => killedPid = pid,
			new IOException("forced non-cancellation failure after start"));

		LaunchHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", CancellationToken.None);

		_ = result.Started.Should().BeFalse("the generic path keeps the shape it always had: Started false, exit -1");
		_ = result.ExitCode.Should().Be(-1);
		_ = result.StandardError.Should().Contain("helper-process-start-exception");

		_ = killedPid.Should().NotBe(
			0,
			"the generic catch must reap the child Process.Start already launched - without this the launch helper (or the browser it starts) is leaked");
	}

	// The same generic-path proof for the network runner.
	[Fact]
	public async Task NetworkRunner_GenericFailureAfterStart_KillsTheStartedChild()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		int killedPid = 0;
		NetworkHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromSeconds(30),
			pid => killedPid = pid,
			new IOException("forced non-cancellation failure after start"));

		NetworkHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", CancellationToken.None);

		_ = result.Started.Should().BeFalse("the generic path keeps the shape it always had: Started false, exit -1");
		_ = result.ExitCode.Should().Be(-1);
		_ = result.StandardError.Should().Contain("helper-process-start-exception");

		_ = killedPid.Should().NotBe(
			0,
			"the generic catch must reap the child Process.Start already launched - without this a helper holding the resolve request is leaked");
	}

	// The forced-failure seam must NOT leak into production behaviour: with no forced failure
	// and no observer set, the runner still behaves exactly as before on the normal path. This
	// is the guard against the seam itself becoming the defect.
	[Fact]
	public async Task LaunchRunner_WithoutForcedFailure_StillReportsTheRealExitCode()
	{
		LaunchHelperProcessRunner runner = new(() => CommandInterpreter(), TimeSpan.FromSeconds(30));

		LaunchHelperProcessResult result = await runner.RunAsync("/c exit 13", "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue();
		_ = result.ExitCode.Should().Be(13, "the seam must be inert unless a test sets it");
	}

	// THE PIPE-CLOSE RACE ON THE LAUNCH RUNNER (HIGH regression introduced by the kill fix).
	// A browser helper that exits FAST - before the parent has finished writing the launch
	// request - makes the parent's next write throw System.IO.IOException ("The pipe is being
	// closed."). That is a NORMAL-SUCCESSFUL-RUN artifact: the helper RAN. The generic catch
	// classified it as Started=false / "helper-process-start-exception", and the client then
	// mapped it to LaunchStatus.Failed (launch.helper-not-found) - telling the user the launch
	// failed when the helper had in fact done its job.
	//
	// MEASURED before the fix (144 real child runs on the launch runner): 36 misreported, and
	// wrongExit = 0 - the exit code was never wrong, only the classification was.
	//
	// The child is FORCED to exit before the write lands, so the race is deterministic rather
	// than ~13%, and the killed-PID assertion proves no kill fires for a child that ended on
	// its own (NEW-3 REVERTED: a tree kill on an exited parent is unsafe).
	[Fact]
	public async Task LaunchRunner_ChildExitsBeforeTheWrite_ReportsTheRealResult_NotAStartFailure()
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
		// hazard) and would kill the browser a successful launch left running; the timeout branch
		// still kills, because there the child is genuinely alive and the kill is safe.
		_ = killedPid.Should().Be(
			0,
			"the exited-child branch must NOT reap the tree: a stale-PID tree kill can terminate an unrelated process, and on the launch path it would kill the browser the launch just started");
	}

	// THE SAME RACE ON THE NETWORK RUNNER. MEASURED before the fix (144 real child runs):
	// 75 misreported, wrongExit = 0.
	[Fact]
	public async Task NetworkRunner_ChildExitsBeforeTheWrite_ReportsTheRealResult_NotAStartFailure()
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

		_ = result.Started.Should().BeTrue(
			"the helper exited on its own before the write reached it; the run concluded normally and must not be reported as a start failure");
		_ = result.ExitCode.Should().Be(
			0,
			"the child's ACTUAL exit code is the truthful outcome - this is the value that was wrong before the fix, and wrongExit was 0 in every MEASURED run");
		_ = result.StandardError.Should().NotContain(
			"helper-process-start-exception",
			"the start-exception marker means 'the runner never started the helper', which is false here");

		// THE EXITED-CHILD BRANCH ATTEMPTS NO KILL (NEW-3 REVERTED). Same correction as the
		// launch runner above: the observer fires ONLY where a kill is attempted on a LIVE
		// process, so `killedPid == 0` pins that no DIRECT tree-kill was introduced on this
		// path. The reap is unsafe because
		// a tree kill on an already-exited parent re-opens the process by numeric PID and can
		// terminate an unrelated process.
		_ = killedPid.Should().Be(
			0,
			"the exited-child branch must NOT reap the tree: a stale-PID tree kill can terminate an unrelated process; the safe kill lives on the still-running path");
	}

	private static string CommandInterpreter()
	{
		string commandInterpreter = Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe";
		_ = File.Exists(commandInterpreter).Should().BeTrue($"these probes need a real child process; '{commandInterpreter}' was not found");
		return commandInterpreter;
	}

	// A REAL child that EXITS IMMEDIATELY, before it ever reads its stdin, so the parent's
	// write lands on a closed pipe every time instead of only ~13% of the time. `exit 0` runs
	// before cmd would ever read stdin, so the process is gone in milliseconds while the
	// parent is still streaming the request onto the pipe.
	//
	// WHY A COPY OF cmd.exe: the runners start their child from a PATH, and a uniquely named
	// image is the one way to answer "is THIS test's child still running?" - the same technique
	// the orphan stub below uses.
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

		// A child that exits milliseconds before the delete can still hold its image briefly;
		// a failed delete must not fail a test that already proved its point.
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

	// A REAL child process that is uniquely identifiable and writes a marker AFTER the window in
	// which the runner's budget expires. Built from a copy of cmd.exe because the runners start
	// their child from a path, and a copied image is the one way to make "is THIS child still
	// running?" answerable without matching an unrelated cmd.exe.
	//
	// The stub's lifetime is a contract of the tests, not an incidental detail: the liveness
	// window and the late-write wait are both chosen against it, and `Started` guards against
	// the stub finishing before the budget expires - if that ever happened, the tests would fail
	// loudly rather than pass vacuously.
	private sealed class StubHelper : IDisposable
	{
		private const string StartedMarkerFileName = "started.txt";
		private const string LateMarkerFileName = "late.txt";

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
			// Under the test process's temp directory, never the repository (P8).
			string directoryPath = Path.Combine(Path.GetTempPath(), "brochoos-helper-orphan-" + Guid.NewGuid().ToString("N")[..12]);
			_ = Directory.CreateDirectory(directoryPath);

			string processName = "brochoos-stub-" + Guid.NewGuid().ToString("N")[..12];
			string executablePath = Path.Combine(directoryPath, processName + ".exe");
			File.Copy(commandInterpreter, executablePath);

			// ONE argument (ArgumentList), exactly as the runners do it. Relative marker paths
			// resolve against the runner's WorkingDirectory, which is the stub's own directory
			// because that is where the executable lives.
			string command = $"/c echo started> {StartedMarkerFileName} & ping -n 3 127.0.0.1 >nul & echo late> {LateMarkerFileName}";

			return new StubHelper(directoryPath, executablePath, processName, command);
		}

		// Waits until the stub's whole natural lifetime has elapsed SINCE IT STARTED, measured
		// against the caller's stopwatch. Checking the late marker earlier could pass merely
		// because the write was not due yet; this makes the check conclusive.
		public static async Task WaitOutTheNaturalLifetimeAsync(TimeSpan alreadyElapsed)
		{
			TimeSpan remaining = StubNaturalLifetimeBound - alreadyElapsed;
			if (remaining > TimeSpan.Zero)
			{
				await Task.Delay(remaining);
			}
		}

		// The runner's beforeBudgetStartsForTests delegate: waits for started.txt, bounded at
		// 10 s. It returns either way; a stub that never started then fails the test's own
		// `stub.Started` assertion with its message, instead of hanging.
		public async Task WaitUntilStartedAsync(CancellationToken cancellationToken)
		{
			DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
			while (!Started && DateTimeOffset.UtcNow < deadline)
			{
				await Task.Delay(20, cancellationToken);
			}

			_startObservedAt = Stopwatch.GetTimestamp();
		}

		private long? _startObservedAt;

		// The late-marker check is conclusive only once the stub's natural life has elapsed
		// SINCE THE STUB STARTED. With the seam a slow stub start is now absorbed instead of
		// failing, so the caller's stopwatch (started before Process.Start) can no longer stand
		// in for the stub's start: this waits from the observed started.txt instead.
		public async Task WaitOutTheNaturalLifetimeSinceStartAsync()
		{
			long startedAt = _startObservedAt ?? throw new InvalidOperationException("WaitUntilStartedAsync was not run.");
			TimeSpan remaining = StubNaturalLifetimeBound - Stopwatch.GetElapsedTime(startedAt);
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

		// A killed child's image can be briefly locked; a failed delete must not fail a test
		// that has already proved its point.
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
}
