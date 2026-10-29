using System.Diagnostics;
using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

public sealed class WindowsBrowserLauncherTests
{
	private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
	private const string SelfPath = @"C:\Dev\BroChoos\BroChoos.exe";
	private const string Url = "https://example.com/";

	[Fact]
	public void Launch_BuildsExactArgumentList_WithUrlAsSingleArgument()
	{
		RecordingProcessStarter starter = new(started: true);

		LaunchResult result = CreateLauncher(starter).Launch(Candidate(EdgePath), Url);

		_ = result.Status.Should().Be(LaunchStatus.Success);
		_ = starter.Captured.Should().NotBeNull();
		_ = starter.Captured!.FileName.Should().Be(EdgePath);
		_ = starter.Captured.ArgumentList.Should().ContainSingle().And.Contain(Url);
		// D-183: no legacy command string may be populated alongside ArgumentList.
		_ = starter.Captured.Arguments.Should().BeEmpty();
		_ = starter.Captured.UseShellExecute.Should().BeFalse();
	}

	// (s) The old Verb assertion was vacuous: ProcessStartInfo.Verb returns string.Empty
	// whenever UseShellExecute is false, so it held no matter what the code did. These
	// assertions are the ones that actually pin the D-183 shape.
	[Fact]
	public void Launch_UsesNoShellAndNoLegacyCommandString()
	{
		RecordingProcessStarter starter = new(started: true);

		_ = CreateLauncher(starter).Launch(Candidate(EdgePath), Url);

		ProcessStartInfo captured = starter.Captured!;
		// No shell: this is what makes a verb meaningless and blocks cmd.exe reparsing.
		_ = captured.UseShellExecute.Should().BeFalse();
		// Structured argv only: a populated Arguments string would be reparsed by Win32.
		_ = captured.Arguments.Should().BeEmpty();
		_ = captured.ArgumentList.Should().HaveCount(1);
		_ = captured.ArgumentList[0].Should().Be(Url);
		// The executable is launched from its own directory, not the caller's.
		_ = captured.WorkingDirectory.Should().Be(Path.GetDirectoryName(EdgePath));
		_ = captured.FileName.Should().Be(EdgePath);
	}

	[Theory]
	[InlineData("https://example.com/?a=1&b=2")]
	[InlineData("https://example.com/path with space")]
	[InlineData("https://example.com/\" --headless")]
	[InlineData("https://example.com/&calc.exe")]
	public void Launch_PassesUrlVerbatim_WithoutQuotingOrSplitting(string url)
	{
		RecordingProcessStarter starter = new(started: true);

		_ = CreateLauncher(starter).Launch(Candidate(EdgePath), url);

		// A hostile-looking URL stays one opaque argv entry: nothing to reparse.
		_ = starter.Captured!.ArgumentList.Should().ContainSingle();
		_ = starter.Captured.ArgumentList[0].Should().Be(url);
	}

	[Fact]
	public void Launch_TargetIsBroChoosItself_IsRejectedByRecursionGuard()
	{
		RecordingProcessStarter starter = new(started: true);

		LaunchResult result = CreateLauncher(starter).Launch(Candidate(SelfPath), Url);

		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.FailureCategory.Should().Be("recursion-guard");
		_ = result.ErrorCode.Should().Be("launch.recursion-guard");
		_ = starter.StartCount.Should().Be(0);
	}

	[Fact]
	public void Launch_TargetIsSelfViaNonNormalizedPath_IsStillRejected()
	{
		RecordingProcessStarter starter = new(started: true);

		LaunchResult result = CreateLauncher(starter).Launch(Candidate(@"C:\Dev\BroChoos\..\BroChoos\BroChoos.exe"), Url);

		_ = result.FailureCategory.Should().Be("recursion-guard");
		_ = starter.StartCount.Should().Be(0);
	}

	[Fact]
	public void Launch_TargetExecutableMissing_ReturnsFailedResult()
	{
		RecordingProcessStarter starter = new(started: true);

		LaunchResult result = CreateLauncher(starter, existingFiles: []).Launch(Candidate(EdgePath), Url);

		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.FailureCategory.Should().Be("target-missing");
		_ = starter.StartCount.Should().Be(0);
	}

	[Fact]
	public void Launch_ProcessStartThrows_ReturnsFailedResultWithoutThrowing()
	{
		ThrowingProcessStarter starter = new(new System.ComponentModel.Win32Exception(2));

		LaunchResult result = CreateLauncher(starter).Launch(Candidate(EdgePath), Url);

		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.ErrorCode.Should().Be("launch.start-failed");
		_ = result.FailureCategory.Should().Be("process-start");
		_ = result.UserVisibleMessageKey.Should().Be("launch.error.failed");
		_ = result.TargetId.Should().Be("edge-stable");
	}

	[Fact]
	public void Launch_ProcessStartReturnsNoProcess_ReturnsFailedResult()
	{
		RecordingProcessStarter starter = new(started: false);

		LaunchResult result = CreateLauncher(starter).Launch(Candidate(EdgePath), Url);

		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.FailureCategory.Should().Be("process-start");
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Launch_EmptyUrl_IsRejectedAsInvalidTarget(string url)
	{
		RecordingProcessStarter starter = new(started: true);

		LaunchResult result = CreateLauncher(starter).Launch(Candidate(EdgePath), url);

		_ = result.ErrorCode.Should().Be("launch.invalid-target");
		_ = starter.StartCount.Should().Be(0);
	}

	// The first three escaped the old allowlist (F4) and surfaced as exit 1 instead of a
	// LaunchResult (exit 5); the rest were already covered and must stay covered. These
	// are the OPERATIONAL failures — the OS or the platform refusing the start request —
	// so they keep the "launch.start-failed"/"process-start" identity.
	// Instances rather than Types: ObjectDisposedException has no parameterless ctor.
	public static TheoryData<Exception> OperationalStartFailures =>
	[
		new ArgumentException("bad argument"),
		new ObjectDisposedException("Process"),
		new NotSupportedException("not supported"),
		new InvalidOperationException("invalid state"),
		new UnauthorizedAccessException("denied"),
		new IOException("io failure"),
		new PlatformNotSupportedException("wrong platform"),
		new TimeoutException("timed out"),
		new System.Security.SecurityException("blocked"),
		new System.ComponentModel.Win32Exception(2),
	];

	// (p) F4: the inverted policy turns every one of these into a Failed result.
	[Theory]
	[MemberData(nameof(OperationalStartFailures))]
	public void Launch_StarterThrowsOperationalFailure_ReturnsFailedResultWithoutEscaping(Exception exception)
	{
		ThrowingProcessStarter starter = new(exception);
		WindowsBrowserLauncher launcher = CreateLauncher(starter);

		// (F10) Invoked ONCE and asserted on the captured result. The old
		// NotThrow()-then-call-again shape ran the whole launch path twice, so a
		// side-effecting or non-deterministic second call could pass while the first
		// failed — and the "no escape" claim was only ever proven for one of the two.
		Func<LaunchResult> act = () => launcher.Launch(Candidate(EdgePath), Url);
		LaunchResult result = act.Should().NotThrow().Subject;

		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.ErrorCode.Should().Be("launch.start-failed");
		_ = result.FailureCategory.Should().Be("process-start");
		_ = result.UserVisibleMessageKey.Should().Be("launch.error.failed");
	}

	// (A7) Everything outside the modelled set is still caught — the D-308/309 contract
	// is unchanged and the exit code stays 5 — but it is reported as an INTERNAL error
	// so a genuine defect is not filed under "the OS would not start the browser".
	// OperationCanceledException is here deliberately: nothing in this launch path takes
	// a CancellationToken, so a cancellation arriving from Process.Start is unmodelled.
	// Runtime-reserved types (NullReferenceException, IndexOutOfRangeException) are
	// deliberately NOT constructed here: CA2201 forbids throwing them from user code, and
	// a custom type exercises the same branch — the classifier keys off "is this in the
	// modelled set", not off any specific type.
	public static TheoryData<Exception> NonOperationalStartFailures =>
	[
		new OperationCanceledException("cancelled"),
		new FormatException("bad format"),
		new KeyNotFoundException("missing key"),
		new InvalidCastException("bad cast"),
		new AggregateException("wrapped", new FormatException()),
		new UnmodelledStarterException("something nobody designed for"),
	];

	[Theory]
	[MemberData(nameof(NonOperationalStartFailures))]
	public void Launch_StarterThrowsNonOperationalFailure_IsReportedAsInternalError(Exception exception)
	{
		ThrowingProcessStarter starter = new(exception);
		WindowsBrowserLauncher launcher = CreateLauncher(starter);

		// (F10) Single invocation; see the operational-failure test above.
		Func<LaunchResult> act = () => launcher.Launch(Candidate(EdgePath), Url);
		LaunchResult result = act.Should().NotThrow().Subject;

		// Still a result, never an escape: the exit-5 contract is what must not regress.
		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.ErrorCode.Should().Be("launch.internal-error");
		_ = result.FailureCategory.Should().Be("internal");
		_ = result.UserVisibleMessageKey.Should().Be("launch.error.failed");
	}

	// OutOfMemoryException is the one failure that MUST escape: the process is no longer
	// sane and a tidy LaunchResult would be a lie. Pinned so the widened catch-all
	// cannot quietly start swallowing it.
	[Fact]
	public void Launch_StarterThrowsOutOfMemory_IsNotCaught()
	{
		// CA2201 (do not raise reserved exception types) is suppressed for this line only:
		// the assertion IS that this exact reserved type propagates, so there is no other
		// way to express it. The instance never leaves the fake starter.
#pragma warning disable CA2201
		ThrowingProcessStarter starter = new(new OutOfMemoryException("no memory"));
#pragma warning restore CA2201
		WindowsBrowserLauncher launcher = CreateLauncher(starter);

		Func<LaunchResult> act = () => launcher.Launch(Candidate(EdgePath), Url);

		_ = act.Should().Throw<OutOfMemoryException>();
	}

	// (F3) The recursion-guard PRE-CHECKS used to run outside the try, so a throw from
	// the environment seam or from path resolution escaped Launch as exit 1 instead of
	// becoming a LaunchResult (exit 5) — the exact no-escape hole the inverted filter
	// was introduced to close for the process-start path. The guard path's own catch
	// filters are allowlists, so only the outer inverted filter can be the backstop.
	//
	// CurrentProcessPath is read BEFORE IsSelf's inner try, and FileExists has no inner
	// try at all, so both are proof that the pre-check region is covered.
	public static TheoryData<GuardFailurePoint> UnmodelledGuardFailures =>
	[
		GuardFailurePoint.CurrentProcessPath,
		GuardFailurePoint.FileExists,
	];

	[Theory]
	[MemberData(nameof(UnmodelledGuardFailures))]
	public void Launch_GuardPreCheckThrowsUnmodelledException_ReturnsFailedResultWithoutEscaping(GuardFailurePoint failurePoint)
	{
		RecordingProcessStarter starter = new(started: true);
		WindowsBrowserLauncher launcher = new(
			NullLogger<WindowsBrowserLauncher>.Instance,
			new ThrowingEnvironmentProbe(failurePoint),
			starter);

		Func<LaunchResult> act = () => launcher.Launch(Candidate(EdgePath), Url);
		LaunchResult result = act.Should().NotThrow().Subject;

		// A pre-check failure is nobody's designed-for state, so it is an INTERNAL error
		// rather than "the OS would not start the browser" — but it is still a result,
		// still exit 5, and the browser was never started.
		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.ErrorCode.Should().Be("launch.internal-error");
		_ = result.FailureCategory.Should().Be("internal");
		_ = result.UserVisibleMessageKey.Should().Be("launch.error.failed");
		_ = result.TargetId.Should().Be("edge-stable");
		_ = starter.StartCount.Should().Be(0);
	}

	// The deliberate programmer-error guard stays a throw even though the pre-checks now
	// sit inside the try: ArgumentNullException.ThrowIfNull runs BEFORE it, so a null
	// target is still a caller bug and must not be laundered into a LaunchResult.
	[Fact]
	public void Launch_NullTarget_StillThrowsArgumentNullException()
	{
		WindowsBrowserLauncher launcher = CreateLauncher(new RecordingProcessStarter(started: true));

		Func<LaunchResult> act = () => launcher.Launch(null!, Url);

		_ = act.Should().Throw<ArgumentNullException>();
	}

	// (q) F8: when the running process has no usable identity the guard must fail
	// CLOSED. It previously returned false (fail-open) while its comment claimed safety.
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Launch_CurrentProcessPathIsUnknown_RefusesLaunchFailClosed(string? currentProcessPath)
	{
		RecordingProcessStarter starter = new(started: true);
		WindowsBrowserLauncher launcher = new(
			NullLogger<WindowsBrowserLauncher>.Instance,
			new FakeEnvironmentProbe([EdgePath], currentProcessPath),
			starter);

		LaunchResult result = launcher.Launch(Candidate(EdgePath), Url);

		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.FailureCategory.Should().Be("recursion-guard");
		_ = starter.StartCount.Should().Be(0);
	}

	[Fact]
	public void Launch_CurrentProcessPathCannotBeNormalized_RefusesLaunchFailClosed()
	{
		// A null character makes Path.GetFullPath throw ArgumentException, so the
		// process identity cannot be established and the launch must be refused.
		RecordingProcessStarter starter = new(started: true);
		WindowsBrowserLauncher launcher = new(
			NullLogger<WindowsBrowserLauncher>.Instance,
			new FakeEnvironmentProbe([EdgePath], "C:\\Dev\\Bro\0Choos.exe"),
			starter);

		LaunchResult result = launcher.Launch(Candidate(EdgePath), Url);

		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.FailureCategory.Should().Be("recursion-guard");
		_ = starter.StartCount.Should().Be(0);
	}

	// (r) An extreme or non-Latin URL is still exactly one opaque argv entry: no
	// truncation, no re-encoding, no splitting.
	[Fact]
	public void Launch_VeryLongUrl_IsPassedAsOneVerbatimArgument()
	{
		string longUrl = "https://example.com/?q=" + new string('a', 32_000);
		RecordingProcessStarter starter = new(started: true);

		LaunchResult result = CreateLauncher(starter).Launch(Candidate(EdgePath), longUrl);

		_ = result.Status.Should().Be(LaunchStatus.Success);
		_ = starter.Captured!.ArgumentList.Should().ContainSingle();
		_ = starter.Captured.ArgumentList[0].Should().Be(longUrl);
		_ = starter.Captured.ArgumentList[0].Length.Should().Be(longUrl.Length);
	}

	[Theory]
	// Mixed RTL/LTR, CJK, combining marks, emoji outside the BMP (surrogate pair).
	[InlineData("https://example.com/\u0645\u0631\u062D\u0628\u0627/path?q=\u05E9\u05DC\u05D5\u05DD")]
	[InlineData("https://example.com/\u4E2D\u6587\u30C6\u30B9\u30C8/\uD55C\uAD6D\uC5B4")]
	[InlineData("https://example.com/e\u0301\u0300\u0327/caf\u00E9")]
	[InlineData("https://example.com/\uD83D\uDE00\uD83C\uDF0D?q=\uD83E\uDD16")]
	[InlineData("https://\u0627\u0644\u0645\u062B\u0627\u0644.\u0625\u062E\u062A\u0628\u0627\u0631/path")]
	public void Launch_NonLatinUrl_IsPassedAsOneVerbatimArgument(string url)
	{
		RecordingProcessStarter starter = new(started: true);

		_ = CreateLauncher(starter).Launch(Candidate(EdgePath), url);

		_ = starter.Captured!.ArgumentList.Should().ContainSingle();
		// Ordinal comparison: no normalization form may be applied to the user's URL.
		_ = starter.Captured.ArgumentList[0].Should().Be(url);
		_ = string.Equals(starter.Captured.ArgumentList[0], url, StringComparison.Ordinal).Should().BeTrue();
	}

	private static BrowserCandidate Candidate(string exePath)
	{
		return new BrowserCandidate("edge-stable", "Microsoft Edge", exePath, "stable", "known-path");
	}

	private static WindowsBrowserLauncher CreateLauncher(IProcessStarter starter, IEnumerable<string>? existingFiles = null)
	{
		FakeEnvironmentProbe environment = new(existingFiles ?? [EdgePath, SelfPath]);
		return new WindowsBrowserLauncher(NullLogger<WindowsBrowserLauncher>.Instance, environment, starter);
	}

	private sealed class RecordingProcessStarter(bool started) : IProcessStarter
	{
		private readonly bool _started = started;

		public ProcessStartInfo? Captured { get; private set; }
		public int StartCount { get; private set; }

		public bool Start(ProcessStartInfo startInfo)
		{
			StartCount++;
			Captured = startInfo;
			return _started;
		}
	}

	private sealed class ThrowingProcessStarter(Exception exception) : IProcessStarter
	{
		private readonly Exception _exception = exception;

		public bool Start(ProcessStartInfo startInfo)
		{
			throw _exception;
		}
	}

	// Stands in for "an exception type the launch path was never designed for". A local
	// type rather than a framework one keeps the case honest: the classifier must decide
	// by ABSENCE from the modelled set, not by recognising a familiar name.
	private sealed class UnmodelledStarterException(string message) : Exception(message)
	{
	}

	// (F3) Which recursion-guard pre-check is made to fail. Both points are reached
	// BEFORE any process start and OUTSIDE the guard's own inner catch filters:
	// CurrentProcessPath is read before IsSelf's try, and FileExists has no try at all.
	// Public because it is the MemberData payload of a public theory.
	public enum GuardFailurePoint
	{
		CurrentProcessPath,
		FileExists,
	}

	// (F3) Makes one pre-check throw an exception the launch path does not model, so the
	// outer inverted filter is the only thing that can stop it escaping.
	private sealed class ThrowingEnvironmentProbe(GuardFailurePoint failurePoint) : IEnvironmentProbe
	{
		private readonly GuardFailurePoint _failurePoint = failurePoint;

		public string? CurrentProcessPath => _failurePoint == GuardFailurePoint.CurrentProcessPath
			? throw new UnmodelledStarterException("the environment seam failed in a way nobody designed for")
			: SelfPath;

		public string? GetEnvironmentVariable(string name)
		{
			return null;
		}

		public bool FileExists(string path)
		{
			return _failurePoint == GuardFailurePoint.FileExists
				? throw new UnmodelledStarterException("the filesystem probe failed in a way nobody designed for")
				: true;
		}
	}

	// currentProcessPath is configurable so the recursion guard can be tested when the
	// running process has no usable identity (F8 fail-closed behaviour).
	private sealed class FakeEnvironmentProbe(IEnumerable<string> existingFiles, string? currentProcessPath = SelfPath) : IEnvironmentProbe
	{
		private readonly HashSet<string> _existingFiles = new(existingFiles, StringComparer.OrdinalIgnoreCase);

		public string? CurrentProcessPath { get; } = currentProcessPath;

		public string? GetEnvironmentVariable(string name)
		{
			return null;
		}

		public bool FileExists(string path)
		{
			return _existingFiles.Contains(path);
		}
	}
}
