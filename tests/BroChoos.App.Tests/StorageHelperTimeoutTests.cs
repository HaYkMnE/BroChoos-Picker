using System.Diagnostics;
using BroChoos.App.HelperClients;
using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Tests.TestSupport;
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
	// How long the kill may take to remove the child from the process list. NOT raced against
	// the child's own life: the gated stub never exits before its gate opens (its self-exit cap
	// is ~30 s, three times this window). It only bounds how long a FAILING run waits.
	private static readonly TimeSpan LivenessWindow = TimeSpan.FromSeconds(10);

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
	// through GetProcessesByName without matching any unrelated cmd.exe on the machine. It
	// writes `started.txt` and then waits on a GATE: it writes `late.txt` only once the test
	// creates `release.txt`, which the test does only AFTER the runner has returned (see
	// StubHelper). The earlier stub was a timed `ping -n 7`, so every window here was a race
	// against its natural life; with the gate, scheduling delay can slow the test but cannot
	// change the outcome.
	//
	// THREE INDEPENDENT PROOFS, each of which an un-killed child fails:
	//  1. the runner's kill observer fired for THIS stub's pid (observed while it was gated);
	//  2. LIVENESS: the child is gone within LivenessWindow. The gated stub never exits on its
	//     own before release (its safety cap is ~30 s), so only the kill can end it in time;
	//  3. THE LATE WRITE: after the gate is opened, no late.txt appears. A surviving child would
	//     write it within one poll. This is the user-visible consequence: a write the user was
	//     told had failed.
	[Fact]
	public async Task Runner_ExpiredBudget_KillsTheChild_SoNoLateWriteHappens()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		int killedPid = 0;

		// started.txt is awaited through the beforeBudgetStartsForTests seam, BEFORE the budget
		// exists, so the 300 ms budget can no longer race the stub's own start-up (the old
		// "`stub.Started` was False" flake, which a 1.5 s budget only made rarer).
		StorageHelperProcessRunner runner = new(
			() => stub.ExecutablePath,
			TimeSpan.FromMilliseconds(300),
			observer: pid => killedPid = pid,
			forcedGenericFailure: null,
			beforeBudgetStartsForTests: stub.WaitUntilStartedAsync);

		StorageHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", CancellationToken.None);

		_ = result.Started.Should().BeTrue("the child did start; only the WAIT was abandoned");
		_ = result.ExitCode.Should().Be(124, "the budget expired, which is the typed timeout marker");
		_ = stub.Started.Should().BeTrue("the stub must have begun work before the budget expired, or the late write would be untestable");
		AssertTheRunnerKilledTheStub(stub, killedPid);

		// 2. The child must be DEAD once the typed outcome is returned.
		_ = (await stub.WaitForExitAsync(LivenessWindow))
			.Should()
			.BeTrue("the runner killed the child it abandoned; a child still running here is the orphan this test exists to catch");

		// 3. Open the gate a survivor would act on; the late write must never appear.
		await stub.ReleaseTheLateWriteAsync();
		_ = stub.LateMarkerExists.Should().BeFalse(
			"the abandoned child was killed, so the write it would have made after the timeout must not occur");
	}

	// The SAME proof for the caller-cancellation path, which abandons the wait for a
	// different reason but orphans the child identically. The typed outcome must stay what it
	// always was (Started false, exit -1) - the kill is not allowed to change the report.
	//
	// The cancellation fires once started.txt exists, not on a fixed 500 ms timer that could
	// fire before the stub had begun, so the kill is always tested against a running child.
	[Fact]
	public async Task Runner_CallerCancellation_KillsTheChild_WithoutChangingTheTypedOutcome()
	{
		using StubHelper stub = StubHelper.Create(CommandInterpreter());
		int killedPid = 0;

		StorageHelperProcessRunner runner = new(() => stub.ExecutablePath, TimeSpan.FromSeconds(30), pid => killedPid = pid, forcedGenericFailure: null);

		using CancellationTokenSource cancellation = new();
		Task cancelOnceStarted = CancelOnceStartedAsync(stub, cancellation);
		StorageHelperProcessResult result = await runner.RunAsync(stub.Command, "{}", cancellation.Token);
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
		_ = stub.LateMarkerExists.Should().BeFalse("a cancelled exchange must not leave a child that finishes the work afterwards");
	}

	// THE POSITIVE CONTROL FOR THE GATE. The kill tests assert "killed" and "no late.txt after
	// release"; both would pass vacuously if the stub died at once or could never write late.txt.
	// Run with NO runner, the stub must stay alive while the gate is closed, and once release.txt
	// exists it must write late.txt and exit on its own with 0.
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

	// Cancels the caller's token only once the stub has written started.txt (bounded at 10 s by
	// WaitUntilStartedAsync), instead of on a fixed timer that could fire before the stub began.
	private static async Task CancelOnceStartedAsync(StubHelper stub, CancellationTokenSource cancellation)
	{
		await stub.WaitUntilStartedAsync(CancellationToken.None);
		await cancellation.CancelAsync();
	}

	// The kill is proven through the runner's own kill observer, which fires only right before
	// Process.Kill on a child that has not exited, and it must be THIS stub's pid. ObservedPid is
	// read while the stub is alive and gated and its image name is unique, so a 0 means the stub
	// never started or the lookup was ambiguous; the comparison could not be made, so it fails.
	private static void AssertTheRunnerKilledTheStub(StubHelper stub, int killedPid)
	{
		_ = killedPid.Should().NotBe(0, "the runner must have attempted the kill on the live child it abandoned; a child that merely exited on its own would leave this 0");
		_ = stub.ObservedPid.Should().NotBe(
			0,
			"the stub's pid must have been observed while it was alive and gated, otherwise the test cannot prove the runner killed THIS stub and no other process");
		_ = killedPid.Should().Be(stub.ObservedPid, "the runner must kill this test's stub and no other process");
	}

	// A REAL child process that is uniquely identifiable and whose late write is GATED: it
	// touches started.txt, then waits for a release.txt that only the test creates, and only
	// after that writes late.txt. The same design as HelperTimeoutKillTests.StubHelper (the
	// launch and network runners' kill tests). Built from a copy of cmd.exe because the runner
	// starts its child from a path, and a copied image is the one way to make "is THIS child
	// still running?" answerable without matching an unrelated cmd.exe.
	//
	// The stub never exits on its own before the gate opens (apart from its ~30 s safety cap),
	// so the liveness check can only pass through the kill, and a late.txt after the release can
	// only come from a child that survived the kill. `Started` guards against a stub that never
	// began; Stub_GateIsOpenOnlyAfterRelease proves the gate works, so the "no late.txt"
	// assertions cannot pass vacuously.
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
			// Under the test process's temp directory, never the repository: a test that wrote
			// into the working tree would pollute it (P8).
			string directoryPath = Path.Combine(Path.GetTempPath(), "brochoos-storage-orphan-" + Guid.NewGuid().ToString("N")[..12]);
			_ = Directory.CreateDirectory(directoryPath);

			// A unique image name makes GetProcessesByName precise. cmd.exe is copied rather
			// than reimplemented because the runner starts its child from a real path.
			string processName = "brochoos-stub-" + Guid.NewGuid().ToString("N")[..12];
			string executablePath = Path.Combine(directoryPath, processName + ".exe");
			File.Copy(commandInterpreter, executablePath);

			// THE GATE, as a batch FILE: nothing is nested inside the single ArgumentList
			// argument, and cmd runs the file in-process, so the copied image is still the process
			// the tests look for (ping is its child, which the runner's tree kill also ends).
			// After started.txt it polls (about once a second) for release.txt and writes late.txt
			// ONLY once it exists. The loop is capped at 30 polls (~30 s) and at the cap it exits 3
			// WITHOUT the late write. %~dp0 anchors every marker to the stub's own directory.
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

			// ONE argument (ArgumentList), exactly as the runner does it, with no space, so cmd
			// sees no quote at all. The script resolves against the runner's WorkingDirectory
			// (the stub's own directory). The explicit `.\` makes it a relative PATH rather than a
			// bare name, so it still runs when NoDefaultCurrentDirectoryInExePath is set.
			string command = $"/c.\\{ScriptFileName}";

			return new StubHelper(directoryPath, executablePath, processName, command);
		}

		// Waits for started.txt, bounded at 10 s, and records the child's pid. It returns either
		// way; a stub that never started then fails the test's own `stub.Started` assertion.
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

		// Starts the stub directly, with no runner, the same way the runner does.
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

		// Opens the gate, then gives a SURVIVING child more than two of its polls to act on it.
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

		// Cleanup that runs however the test ended. The old Dispose only deleted the folder, so a
		// live stub (a failing run, or the generic-failure test, whose kill is observed but not
		// awaited) kept its image locked, the folder leaked, and late.txt could be written after
		// the test. A survivor is now killed first, verified by its pinned handle and full image
		// path (TestTempCleanup.KillSurvivors), then the folder goes with one short retry.
		public void Dispose()
		{
			TestTempCleanup.KillSurvivors(ExecutablePath);
			TestTempCleanup.DeleteDirectory(DirectoryPath);
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

		// A survivor from this image (verified by its full path) is killed first, then the folder
		// is deleted with one short retry: a child that exits milliseconds before the delete can
		// still hold its image briefly. A failed cleanup must not fail a test that proved its point.
		public void Dispose()
		{
			TestTempCleanup.KillSurvivors(ExecutablePath);
			TestTempCleanup.DeleteDirectory(DirectoryPath);
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
