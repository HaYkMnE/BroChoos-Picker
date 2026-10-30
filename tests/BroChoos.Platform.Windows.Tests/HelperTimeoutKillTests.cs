using System.Diagnostics;
using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Network;
using BroChoos.Platform.Windows.Tests.TestSupport;
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
//   1. an expired budget kills the child it abandons (the stub NEVER exits on its own while
//      its gate is closed, so a pass can only be caused by the kill);
//   2. the write the child would have made afterwards never happens, even once the test opens
//      the gate that would let a surviving child perform it;
//   3. a caller cancellation kills the child too, without changing the typed outcome;
//   4. normal completion still returns the child's REAL exit code - the kill must fire only on
//      the abandoned paths.
//
// The stub is a COPY of cmd.exe with a unique file name, so GetProcessesByName sees only THIS
// test's child and never an unrelated cmd.exe on the machine.
public sealed class HelperTimeoutKillTests
{
	// How long the kill may take to remove the child from the process list. This is NOT raced
	// against the child's own life: the gated stub never exits by itself before its gate opens
	// (its self-exit cap is ~30 s, three times this window), so an un-killed child is still running when this
	// window closes no matter how long the window is. It only bounds how long a FAILING run waits.
	private static readonly TimeSpan LivenessWindow = TimeSpan.FromSeconds(10);

	// THE CI FLAKE THIS DESIGN REMOVES (runs 40975699410 and 41033263986 on windows-2022). The
	// stub used to be `ping -n 3` (~2.09 s) followed by the late write, racing a 300 ms budget.
	// The budget's expiry, the OperationCanceledException continuation and the kill all run on
	// the thread pool; on a loaded 2-vCPU runner they landed after the stub had already finished
	// its whole life. The kill then saw HasExited and did nothing, the runner still reported 124,
	// and the test observed late.txt ("found True"), or the child's own exit 0 when the expiry
	// lost the race entirely. The late write is now GATED on a release file the test creates
	// only AFTER the runner has returned, so it can only happen in a child that survived the
	// kill. Scheduling latency can delay the test, but it cannot change the outcome.
	//
	// The started.txt precondition is still established through the runner's
	// beforeBudgetStartsForTests seam, before the budget exists.
	[Fact]
	public async Task LaunchRunner_ExpiredBudget_KillsTheChild_SoNoLateLaunchRequestIsActedOn()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		int killedPid = 0;
		LaunchHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromMilliseconds(300),
			observer: pid => killedPid = pid,
			forcedGenericFailure: null,
			beforeBudgetStartsForTests: stub.WaitUntilStartedAsync);

		LaunchHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue("the child did start; only the WAIT was abandoned");
		_ = result.ExitCode.Should().Be(124, "124 is the typed timeout marker the client classifies on");
		_ = stub.Started.Should().BeTrue("the stub must have begun work before the budget expired, or the late write would be untestable");
		AssertTheRunnerKilledTheStub(stub, killedPid);

		_ = (await stub.WaitForExitAsync(LivenessWindow))
			.Should()
			.BeTrue("the launch runner killed the child it abandoned; a still-running helper could still open a browser after the timeout was reported");

		await stub.ReleaseTheLateWriteAsync();
		_ = stub.LateMarkerExists.Should().BeFalse("the abandoned child was killed, so the work it would have done after the timeout must not happen");
	}

	// The seam's CONTRACT, pinned directly: time spent in it is not charged to the budget. A
	// seam that takes 3x the budget, followed by a child that exits at once, must still yield
	// the child's real exit code. Were the budget's token source created before the seam,
	// this run would report the 124 timeout instead.
	//
	// The seam also waits until the child has EXITED, so the budget only ever covers work on an
	// already-finished child. Before, `cmd /c exit 5` was still starting while the 300 ms budget
	// ran, and under CPU load it lost that race and reported 124 (reproduced locally under
	// stress). The mutation this test exists for still fails it: a budget created before the
	// seam is already expired when the runner first uses its token.
	//
	// SIDE EFFECT, stated so nobody mistakes it for the path under test: because the child has
	// already exited when the budget starts, the runner's stdin write lands on a CLOSED pipe and
	// the run normally concludes through the generic catch's exited-child branch (HasExited true
	// -> ReadCompletedResultAsync), not through the plain WaitForExitAsync path. That branch also
	// returns the real exit code, so the test still distinguishes "seam not charged" (5 / 6)
	// from "seam charged" (124), which is the only thing it pins. The closed-pipe path has its
	// own tests (ChildExitsBeforeTheWrite below).
	[Fact]
	public async Task Runners_BeforeBudgetSeam_IsNotChargedToTheBudget()
	{
		using FastExitStub launchStub = FastExitStub.Create(CommandInterpreter());
		using FastExitStub networkStub = FastExitStub.Create(CommandInterpreter());

		LaunchHelperProcessRunner launch = new(() => launchStub.ExecutablePath, TimeSpan.FromMilliseconds(300), null, null, launchStub.SlowSeamUntilExitedAsync);
		NetworkHelperProcessRunner network = new(() => networkStub.ExecutablePath, TimeSpan.FromMilliseconds(300), null, null, networkStub.SlowSeamUntilExitedAsync);

		LaunchHelperProcessResult launchResult = await launch.RunAsync("/c exit 5", "{}", CancellationToken.None);
		NetworkHelperProcessResult networkResult = await network.RunAsync("/c exit 6", "{}", CancellationToken.None);

		_ = launchResult.ExitCode.Should().Be(5, "the seam's 900 ms must not consume the 300 ms launch budget");
		_ = networkResult.ExitCode.Should().Be(6, "the seam's 900 ms must not consume the 300 ms network budget");
	}

	// The cancellation fires only once started.txt exists (CancelOnceStartedAsync), not on a
	// fixed timer that could fire before the stub had begun, so the kill is always tested
	// against a child that is genuinely running its gated wait.
	[Fact]
	public async Task LaunchRunner_CallerCancellation_KillsTheChild_WithoutChangingTheTypedOutcome()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		int killedPid = 0;
		LaunchHelperProcessRunner runner = new(() => stub.ExecutablePath, TimeSpan.FromSeconds(30), pid => killedPid = pid, forcedGenericFailure: null);

		using CancellationTokenSource cancellation = new();
		Task cancelOnceStarted = CancelOnceStartedAsync(stub, cancellation);
		LaunchHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", cancellation.Token);
		await cancelOnceStarted;

		_ = result.Started.Should().BeFalse("a caller cancellation is not a diagnosis about the helper and keeps the existing shape");
		_ = result.ExitCode.Should().Be(-1);
		_ = result.StandardError.Should().Contain("helper-process-start-exception");
		_ = stub.Started.Should().BeTrue("the stub must have begun work before the caller cancelled, or the kill would be tested against a child that never ran");
		AssertTheRunnerKilledTheStub(stub, killedPid);

		_ = (await stub.WaitForExitAsync(LivenessWindow))
			.Should()
			.BeTrue("the cancelled wait abandoned the child, so the child must be killed for the same reason as a timeout");

		await stub.ReleaseTheLateWriteAsync();
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
		int killedPid = 0;
		NetworkHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromMilliseconds(300),
			observer: pid => killedPid = pid,
			forcedGenericFailure: null,
			beforeBudgetStartsForTests: stub.WaitUntilStartedAsync);

		NetworkHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue("the child did start; only the WAIT was abandoned");
		_ = result.ExitCode.Should().Be(124, "124 is the typed timeout marker the client classifies on");
		_ = stub.Started.Should().BeTrue("the stub must have begun work before the budget expired, or the late write would be untestable");
		AssertTheRunnerKilledTheStub(stub, killedPid);

		_ = (await stub.WaitForExitAsync(LivenessWindow))
			.Should()
			.BeTrue("the network runner killed the child it abandoned; a still-running helper could still complete its resolution afterwards");

		await stub.ReleaseTheLateWriteAsync();
		_ = stub.LateMarkerExists.Should().BeFalse("the abandoned child was killed, so the work it would have done after the timeout must not happen");
	}

	[Fact]
	public async Task NetworkRunner_CallerCancellation_KillsTheChild_WithoutChangingTheTypedOutcome()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		int killedPid = 0;
		NetworkHelperProcessRunner runner = new(() => stub.ExecutablePath, TimeSpan.FromSeconds(30), pid => killedPid = pid, forcedGenericFailure: null);

		using CancellationTokenSource cancellation = new();
		Task cancelOnceStarted = CancelOnceStartedAsync(stub, cancellation);
		NetworkHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", cancellation.Token);
		await cancelOnceStarted;

		_ = result.Started.Should().BeFalse("a caller cancellation is not a diagnosis about the helper and keeps the existing shape");
		_ = result.ExitCode.Should().Be(-1);
		_ = result.StandardError.Should().Contain("helper-process-start-exception");
		_ = stub.Started.Should().BeTrue("the stub must have begun work before the caller cancelled, or the kill would be tested against a child that never ran");
		AssertTheRunnerKilledTheStub(stub, killedPid);

		_ = (await stub.WaitForExitAsync(LivenessWindow))
			.Should()
			.BeTrue("the cancelled wait abandoned the child, so the child must be killed for the same reason as a timeout");

		await stub.ReleaseTheLateWriteAsync();
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

	// THE POSITIVE CONTROL FOR THE GATE. The kill tests assert "still alive -> killed" and "no
	// late.txt after release"; both would pass vacuously if the stub died at once or could never
	// write late.txt. Run with NO runner, the stub must stay alive while the gate is closed, and
	// once release.txt exists it must write late.txt and exit on its own.
	[Fact]
	public async Task Stub_GateIsOpenOnlyAfterRelease_StaysAliveUntilReleased_ThenWritesTheLateMarkerAndExits()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		using Process child = stub.StartDirectly();

		await stub.WaitUntilStartedAsync(CancellationToken.None);
		_ = stub.Started.Should().BeTrue("the stub script must run and touch started.txt");
		_ = stub.ObservedPid.Should().Be(child.Id, "the uniquely named image the tests watch is the process that was started");

		await Task.Delay(TimeSpan.FromSeconds(1));
		_ = child.HasExited.Should().BeFalse("with the gate closed the stub must still be running, or the kill tests' liveness check would pass without a kill");
		_ = stub.LateMarkerExists.Should().BeFalse("the late write must wait for release.txt");

		await File.WriteAllTextAsync(Path.Combine(stub.DirectoryPath, "release.txt"), "release");

		using CancellationTokenSource bound = new(TimeSpan.FromSeconds(15));
		bool exited;
		try
		{
			await child.WaitForExitAsync(bound.Token);
			exited = true;
		}
		catch (OperationCanceledException)
		{
			exited = false;
		}

		_ = exited.Should().BeTrue("a released stub must exit on its own within the bound");
		_ = child.ExitCode.Should().Be(0, "exit 0 is the released path; 3 would mean the 30-poll cap ran out first");
		_ = stub.LateMarkerExists.Should().BeTrue("a released, un-killed stub must perform the late write, so its absence in the kill tests means something");
	}

	// Cancels the caller's token only once the stub has written started.txt (bounded at 10 s by
	// WaitUntilStartedAsync), instead of on a fixed timer that could fire before the stub began.
	private static async Task CancelOnceStartedAsync(StubHelper stub, CancellationTokenSource cancellation)
	{
		await stub.WaitUntilStartedAsync(CancellationToken.None);
		await cancellation.CancelAsync();
	}

	// The kill is proven through the runner's own kill observer, which fires only right before
	// Process.Kill on a child that has not exited. A non-zero pid means the runner attempted the
	// kill on a live child, and it must be THIS stub's pid.
	//
	// ObservedPid is read while the stub is provably alive and gated (started.txt exists and only
	// release.txt, which no test has written yet, lets it finish), and its image name is unique to
	// this test, so exactly one process matches. A 0 therefore means the stub never started or the
	// lookup saw zero or several matches; either way the comparison below could not be made, and
	// skipping it would let a kill of the wrong process pass. It fails instead.
	private static void AssertTheRunnerKilledTheStub(StubHelper stub, int killedPid)
	{
		_ = killedPid.Should().NotBe(0, "the runner must have attempted the kill on the live child it abandoned; a child that merely exited on its own would leave this 0");
		_ = stub.ObservedPid.Should().NotBe(
			0,
			"the stub's pid must have been observed while it was alive and gated, otherwise the test cannot prove the runner killed THIS stub and no other process");
		_ = killedPid.Should().Be(stub.ObservedPid, "the runner must kill this test's stub and no other process");
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

		// A beforeBudgetStartsForTests seam: 900 ms (3x the 300 ms test budget), then waits,
		// bounded at 10 s, until this stub's uniquely named child is gone. A child that never
		// exits makes the runner hit its budget, and the test's exit-code assertion reports it.
		public async Task SlowSeamUntilExitedAsync(CancellationToken cancellationToken)
		{
			await Task.Delay(900, cancellationToken);

			string processName = Path.GetFileNameWithoutExtension(ExecutablePath);
			DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
			while (IsRunning(processName) && DateTimeOffset.UtcNow < deadline)
			{
				await Task.Delay(20, cancellationToken);
			}
		}

		private static bool IsRunning(string processName)
		{
			Process[] running = Process.GetProcessesByName(processName);
			foreach (Process process in running)
			{
				process.Dispose();
			}

			return running.Length > 0;
		}

		// A survivor from this image (verified by full path) is killed first, then the folder is
		// deleted with one short retry: a child that exits milliseconds before the delete can
		// still hold its image briefly. A failed cleanup must not fail a test that proved its point.
		public void Dispose()
		{
			TestTempCleanup.KillSurvivors(ExecutablePath);
			TestTempCleanup.DeleteDirectory(DirectoryPath);
		}
	}

	// A REAL child process that is uniquely identifiable and whose late write is GATED: it
	// touches started.txt, then waits for a release.txt that only the test creates, and only
	// after that writes late.txt. Built from a copy of cmd.exe because the runners start their
	// child from a path, and a copied image is the one way to make "is THIS child still
	// running?" answerable without matching an unrelated cmd.exe.
	//
	// The stub's lifetime is a contract of the tests: it never exits on its own before the gate
	// opens (apart from its ~30 s safety cap), so the liveness check can only pass through the
	// kill, and a late.txt after the release can only come from a child that survived the kill.
	// `Started` guards against a stub that never began; GateIsOpenOnlyAfterRelease proves the
	// gate itself works, so the "no late.txt" assertions cannot pass vacuously.
	private sealed class StubHelper : IDisposable
	{
		private const string StartedMarkerFileName = "started.txt";
		private const string LateMarkerFileName = "late.txt";
		private const string ReleaseMarkerFileName = "release.txt";
		private const string ScriptFileName = "gate.cmd";

		// A surviving child polls for release.txt about once a second (`ping -n 2`), so this
		// covers more than two polls. It only delays the check; it is not raced against the kill.
		private static readonly TimeSpan ReleasedChildReactionBound = TimeSpan.FromMilliseconds(2500);

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

		// The pid of this stub's child, read when started.txt was first observed (0 if never).
		public int ObservedPid { get; private set; }

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

			// THE GATE, as a batch FILE rather than a quoted one-liner: the earlier inline form
			// nested quotes and parentheses inside the single ArgumentList argument, and the
			// trailing quote that argument gets made cmd reject the whole line (exit 1 at once,
			// not even started.txt). In a file nothing is nested, and cmd runs it in-process,
			// so the copied image is still the process the tests look for (ping is its child,
			// which the runner's tree kill also ends).
			//
			// After started.txt the script polls (about once a second, via ping) for release.txt
			// and writes late.txt ONLY once it exists. The loop is capped at 30 polls (~30 s) so a
			// stub a failed kill leaves behind does not linger, and at the cap it exits 3 WITHOUT
			// the late write. `exit 0` ends cmd itself, so the write happens at most once.
			// %~dp0 anchors every marker to the stub's own directory.
			string script = string.Join(
				"\r\n",
				"@echo off",
				$"echo started> \"%~dp0{StartedMarkerFileName}\"",
				"for /l %%i in (1,1,30) do (",
				$"\tif exist \"%~dp0{ReleaseMarkerFileName}\" (",
				$"\t\techo late> \"%~dp0{LateMarkerFileName}\"",
				"\t\texit 0",
				"\t)",
				"\tping -n 2 127.0.0.1 >nul",
				")",
				"exit 3",
				string.Empty);
			File.WriteAllText(Path.Combine(directoryPath, ScriptFileName), script);

			// ONE argument (ArgumentList), exactly as the runners do it. It has no space, so
			// ArgumentList passes it unquoted and cmd sees no quote at all (cmd accepts the
			// command directly after /c). The script resolves against the runner's
			// WorkingDirectory, which is the stub's own directory. The explicit `.\` makes that a
			// relative PATH rather than a bare name, so cmd never searches the current directory
			// implicitly and the stub still runs when NoDefaultCurrentDirectoryInExePath is set.
			string command = $"/c.\\{ScriptFileName}";

			return new StubHelper(directoryPath, executablePath, processName, command);
		}

		// Waits for started.txt, bounded at 10 s, and records the child's pid. Used as the
		// runner's beforeBudgetStartsForTests delegate and, in the cancellation tests, before
		// the caller cancels. It returns either way; a stub that never started then fails the
		// test's own `stub.Started` assertion with its message, instead of hanging.
		public async Task WaitUntilStartedAsync(CancellationToken cancellationToken)
		{
			DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
			while (!Started && DateTimeOffset.UtcNow < deadline)
			{
				await Task.Delay(20, cancellationToken);
			}

			if (Started)
			{
				Process[] running = Process.GetProcessesByName(ProcessName);
				try
				{
					ObservedPid = running.Length == 1 ? running[0].Id : 0;
				}
				finally
				{
					foreach (Process process in running)
					{
						process.Dispose();
					}
				}
			}
		}

		// Starts the stub directly, with no runner, the same way the runners do.
		public Process StartDirectly()
		{
			ProcessStartInfo startInfo = new()
			{
				FileName = ExecutablePath,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = DirectoryPath,
			};
			startInfo.ArgumentList.Add(Command);
			return Process.Start(startInfo) ?? throw new InvalidOperationException("the stub did not start");
		}

		// Opens the gate, then gives a SURVIVING child more than two of its polls to act on
		// it. A killed child cannot act, so late.txt stays absent; a child the kill missed
		// writes it within about one poll, so the check that follows catches it.
		public async Task ReleaseTheLateWriteAsync()
		{
			await File.WriteAllTextAsync(Path.Combine(DirectoryPath, ReleaseMarkerFileName), "release");
			await Task.Delay(ReleasedChildReactionBound);
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
			Process[] running = Process.GetProcessesByName(ProcessName);
			foreach (Process process in running)
			{
				process.Dispose();
			}

			return running.Length > 0;
		}

		// Cleanup that runs however the test ended. A stub the kill missed (a FAILING run) is
		// reaped here, so it does not live out its 30 s cap; then the temp directory goes, with
		// one short retry for an image a just-killed child still holds. A failed cleanup never
		// fails a test that has already proved its point.
		//
		// The reap acts only on a process whose handle is pinned and whose image is this stub's
		// own full path, so a reused pid can never be killed (TestTempCleanup.KillSurvivors).
		public void Dispose()
		{
			TestTempCleanup.KillSurvivors(ExecutablePath);
			TestTempCleanup.DeleteDirectory(DirectoryPath);
		}
	}
}
