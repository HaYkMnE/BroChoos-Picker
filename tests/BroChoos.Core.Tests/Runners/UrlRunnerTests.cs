using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Runners;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.Core.Tests.Runners;

// Redirects Console.Out/Console.Error; see ConsoleCaptureCollection.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class UrlRunnerTests
{
	private const string Url = "https://example.com/";

	[Fact]
	public async Task RunAsync_PickerPath_SuccessReturnsPickerExitCodeWithoutAutoLaunch()
	{
		FakePicker picker = new() { ExitCode = ExitCodes.Success };
		TestRun run = await RunAsync(new FakeBrowserDetector([Candidate("edge-stable")]), picker: picker, noUi: false);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = run.StandardOutput.Should().BeEmpty();
		_ = run.StandardError.Should().BeEmpty();
		_ = picker.ShowCount.Should().Be(1);
	}

	[Fact]
	public async Task RunAsync_PickerPath_WithNoCandidates_StillShowsPickerWithEmptySnapshot()
	{
		FakePicker picker = new() { ExitCode = ExitCodes.Success };
		FakeBrowserDetector detector = new([]);

		TestRun run = await RunAsync(detector, picker: picker, noUi: false);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = run.StandardError.Should().BeEmpty();
		_ = detector.DetectCount.Should().Be(0);
		_ = picker.ShowCount.Should().Be(1);
		_ = picker.ReceivedRequest.Should().NotBeNull();
		_ = picker.ReceivedRequest!.Candidates.Should().BeEmpty();
		_ = picker.ReceivedRequest.Snapshot.Should().Be(Snapshots.BrowserSnapshot.Empty);
	}

	[Fact]
	public async Task RunAsync_PickerPath_PropagatesPickerErrorCode()
	{
		FakePicker picker = new() { ExitCode = ExitCodes.BrowserLaunchFailed };
		TestRun run = await RunAsync(new FakeBrowserDetector([Candidate("edge-stable")]), picker: picker, noUi: false);

		_ = run.ExitCode.Should().Be(ExitCodes.BrowserLaunchFailed);
		_ = picker.ShowCount.Should().Be(1);
	}

	[Fact]
	public async Task RunAsync_PickerPath_DoesNotDetectAndNeverLaunches()
	{
		FakeBrowserDetector detector = new([Candidate("edge-stable"), Candidate("chrome-stable")]);
		FakePicker picker = new() { ExitCode = ExitCodes.Success };

		TestRun run = await RunAsync(detector, picker: picker, noUi: false);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = detector.DetectCount.Should().Be(0);
		_ = picker.ShowCount.Should().Be(1);
	}

	[Fact]
	public async Task RunAsync_SecondaryInstance_DoesNotDetectOrLaunch()
	{
		FakeBrowserDetector detector = new([Candidate("edge-stable")]);

		TestRun run = await RunAsync(detector, SingleInstanceRole.Secondary);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = detector.DetectCount.Should().Be(0);
	}

	// The URL is the user's own input, so stdout may echo it; persistent log lines may not.
	[Fact]
	public async Task RunAsync_NoUiFailClosed_DoesNotWriteTheUrlToLogs()
	{
		ListLogger<UrlRunner> logger = new();

		_ = await RunAsync(new FakeBrowserDetector([Candidate("edge-stable")]), logger: logger, noUi: true);

		_ = logger.Entries.Should().NotBeEmpty();
		_ = logger.Entries.Should().OnlyContain(e => !e.Contains("example.com", StringComparison.Ordinal));
	}

	// Privacy also holds on a successful picker route.
	[Fact]
	public async Task RunAsync_PickerPath_Success_DoesNotWriteTheUrlToLogs()
	{
		ListLogger<UrlRunner> logger = new();
		FakePicker picker = new() { ExitCode = ExitCodes.Success };

		TestRun run = await RunAsync(new FakeBrowserDetector([Candidate("edge-stable")]), logger: logger, picker: picker, noUi: false);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = logger.Entries.Should().NotContain(e => e.Contains("example.com", StringComparison.Ordinal));
		_ = run.StandardOutput.Should().BeEmpty();
	}

	[Fact]
	public async Task RunAsync_SecondaryInstance_DoesNotWriteTheUrlToLogs()
	{
		ListLogger<UrlRunner> logger = new();

		_ = await RunAsync(new FakeBrowserDetector([Candidate("edge-stable")]), SingleInstanceRole.Secondary, logger);

		_ = logger.Entries.Should().NotBeEmpty();
		_ = logger.Entries.Should().OnlyContain(e => !e.Contains("example.com", StringComparison.Ordinal));
	}

	// (u) A guard that can neither acquire nor forward is a general error (exit 1), and
	// it must never reach detection or launching.
	[Fact]
	public async Task RunAsync_SingleInstanceGuardFailed_ReturnsGeneralErrorAndNeverLaunches()
	{
		FakeBrowserDetector detector = new([Candidate("edge-stable")]);
		ListLogger<UrlRunner> logger = new();

		TestRun run = await RunAsync(detector, SingleInstanceRole.Failed, logger);

		_ = run.ExitCode.Should().Be(ExitCodes.GeneralError);
		_ = detector.DetectCount.Should().Be(0);
		_ = logger.Entries.Should().OnlyContain(e => !e.Contains("example.com", StringComparison.Ordinal));
	}

	// ---- 0A-3 picker routing --------------------------------------------------

	// The default (no --no-ui) URL run must hand URL + candidates to the picker and
	// return its exit code; the runner itself never launches on this path.
	[Fact]
	public async Task RunAsync_PickerPath_PassesUrlAndImmutableEmptySnapshotToThePicker()
	{
		FakePicker picker = new() { ExitCode = 42 };
		FakeBrowserDetector detector = new([Candidate("edge-stable"), Candidate("chrome-stable")]);

		TestRun run = await RunAsync(
			detector,
			picker: picker,
			noUi: false);

		// 42 is a distinctive fake value: equality can only hold if the runner
		// returned whatever the picker produced.
		_ = run.ExitCode.Should().Be(42);
		_ = detector.DetectCount.Should().Be(0);
		_ = picker.ShowCount.Should().Be(1);
		_ = picker.ReceivedRequest.Should().NotBeNull();
		_ = picker.ReceivedRequest!.Url.Should().Be(Url);
		_ = picker.ReceivedRequest.Snapshot.Should().Be(Snapshots.BrowserSnapshot.Empty);
		_ = picker.ReceivedRequest.Candidates.Should().BeEmpty();
	}

	[Fact]
	public async Task RunAsync_PickerPath_DoesNotDetectAndUsesEmptySnapshot()
	{
		FakePicker picker = new() { ExitCode = ExitCodes.Success };
		FakeBrowserDetector detector = new([Candidate("edge-stable")]);

		TestRun run = await RunAsync(detector, picker: picker, noUi: false);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = detector.DetectCount.Should().Be(0);
		_ = picker.ShowCount.Should().Be(1);
		_ = picker.ReceivedRequest.Should().NotBeNull();
		_ = picker.ReceivedRequest!.Snapshot.Should().Be(Snapshots.BrowserSnapshot.Empty);
		_ = picker.ReceivedRequest.Candidates.Should().BeEmpty();
	}

	[Fact]
	public async Task RunAsync_NoUi_FailsClosedWithoutDiscoveryPickerOrLaunch()
	{
		FakePicker picker = new();
		FakeBrowserDetector detector = new([Candidate("edge-stable")]);

		TestRun run = await RunAsync(detector, picker: picker, noUi: true);

		_ = run.ExitCode.Should().Be(ExitCodes.GeneralError);
		_ = run.StandardError.Should().Contain("explicit browser choice");
		_ = detector.DetectCount.Should().Be(0);
		_ = picker.ShowCount.Should().Be(0);
	}

	// A --no-ui request is rejected before the guard runs, so it can never be forwarded to
	// a primary instance (which would be a Secondary "success" for a rejected request).
	[Theory]
	[InlineData(SingleInstanceRole.Primary)]
	[InlineData(SingleInstanceRole.Secondary)]
	[InlineData(SingleInstanceRole.Failed)]
	public async Task RunAsync_NoUi_IsRejectedBeforeAcquireOrForward(SingleInstanceRole role)
	{
		FakeSingleInstanceGuard guard = new(role);
		UrlRunner runner = new(new ListLogger<UrlRunner>(), guard, new FakeBrowserDetector([]), new FakePicker());

		TextWriter originalError = Console.Error;
		StringWriter stderr = new();
		int exitCode;
		try
		{
			Console.SetError(stderr);
			exitCode = await runner.RunAsync(BuildRequest(noUi: true), CancellationToken.None);
		}
		finally
		{
			Console.SetError(originalError);
		}

		_ = exitCode.Should().Be(ExitCodes.GeneralError);
		_ = guard.AcquireCount.Should().Be(0);
		_ = stderr.ToString().Should().Contain("explicit browser choice");
	}

	[Fact]
	public async Task RunAsync_SecondaryInstance_NeitherShowsPickerNorLaunches()
	{
		FakePicker picker = new();

		TestRun run = await RunAsync(new FakeBrowserDetector([Candidate("edge-stable")]), SingleInstanceRole.Secondary, picker: picker, noUi: false);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = picker.ShowCount.Should().Be(0);
	}

	private static BrowserCandidate Candidate(string browserId)
	{
		return browserId == "edge-stable"
			? new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
			: new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path");
	}

	private static async Task<TestRun> RunAsync(
		FakeBrowserDetector detector,
		SingleInstanceRole role = SingleInstanceRole.Primary,
		ListLogger<UrlRunner>? logger = null,
		FakePicker? picker = null,
		bool noUi = false)
	{
		UrlRunner runner = new(logger ?? new ListLogger<UrlRunner>(), new FakeSingleInstanceGuard(role), detector, picker ?? new FakePicker());

		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		StringWriter stdout = new();
		StringWriter stderr = new();

		try
		{
			Console.SetOut(stdout);
			Console.SetError(stderr);
			ActivationRequest request = BuildRequest(noUi);
			int exitCode = await runner.RunAsync(request, CancellationToken.None);
			return new TestRun(exitCode, stdout.ToString(), stderr.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	private sealed record TestRun(int ExitCode, string StandardOutput, string StandardError);

	private sealed class FakeBrowserDetector(IReadOnlyList<BrowserCandidate> candidates) : IBrowserDetector
	{
		private readonly IReadOnlyList<BrowserCandidate> _candidates = candidates;

		public int DetectCount { get; private set; }

		public IReadOnlyList<BrowserCandidate> Detect()
		{
			DetectCount++;
			return _candidates;
		}
	}

	private sealed class FakeSingleInstanceGuard(SingleInstanceRole role) : ISingleInstanceGuard
	{
		private readonly SingleInstanceRole _role = role;

		public int AcquireCount { get; private set; }

		public Task<SingleInstanceRole> AcquireOrForwardAsync(ActivationRequest request, CancellationToken cancellationToken)
		{
			AcquireCount++;
			return Task.FromResult(_role);
		}
	}

	// Records picker calls; never launches anything itself.
	private sealed class FakePicker : IPicker
	{
		public int ShowCount { get; private set; }

		public PickerRequest? ReceivedRequest { get; private set; }

		public int ExitCode { get; set; } = ExitCodes.Success;

		public Task<int> ShowAsync(PickerRequest request, CancellationToken cancellationToken)
		{
			ShowCount++;
			ReceivedRequest = request;
			return Task.FromResult(ExitCode);
		}
	}

	private static ActivationRequest BuildRequest(bool noUi)
	{
		ActivationModifiers modifiers = noUi ? ActivationModifiers.NoUi : ActivationModifiers.None;
		return new ActivationRequest(
			Url,
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Confirmed("test"),
			modifiers,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));
	}

	// Captures every level so log-redaction can be asserted, not just warnings.
	private sealed class ListLogger<T> : ILogger<T>
	{
		private readonly List<string> _entries = [];

		public IReadOnlyList<string> Entries => _entries;

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			_entries.Add(formatter(state, exception));
		}
	}
}
