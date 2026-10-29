using System.Text.Json;
using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Xunit;

namespace BroChoos.Security.Tests;

public sealed class LaunchHelperBoundaryTests
{
	private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
	[Fact]
	public async Task LaunchHelperClient_UsesHelperProcessContract_AndParsesTypedOutcome()
	{
		LaunchHelperRequest request = BuildRequest();
		RecordingRunner runner = new(new LaunchHelperProcessResult(
			Started: true,
			ExitCode: 0,
			StandardOutput: JsonSerializer.Serialize(new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null)),
			StandardError: string.Empty));
		LaunchHelperClient client = new(runner);

		LaunchHelperOutcome outcome = await client.LaunchAsync(request, CancellationToken.None);

		_ = runner.LastCommand.Should().Be("launch");
		_ = runner.LastRequestPayload.Should().NotBeNullOrWhiteSpace();
		LaunchHelperRequest? payload = JsonSerializer.Deserialize<LaunchHelperRequest>(runner.LastRequestPayload!, WebJsonOptions);
		_ = payload.Should().NotBeNull();
		_ = payload!.BrowserId.Should().Be(request.BrowserId);
		_ = payload.ExePath.Should().Be(request.ExePath);
		_ = payload.Url.Should().Be(request.Url);
		_ = outcome.Status.Should().Be(LaunchStatus.Success);
		_ = outcome.TargetId.Should().Be(request.BrowserId);
	}

	[Fact]
	public async Task LaunchHelperClient_HelperProcessNotFound_FailsClosed()
	{
		LaunchHelperRequest request = BuildRequest();
		LaunchHelperClient client = new(new RecordingRunner(new LaunchHelperProcessResult(false, -1, string.Empty, "missing")));

		LaunchHelperOutcome outcome = await client.LaunchAsync(request, CancellationToken.None);

		_ = outcome.Status.Should().Be(LaunchStatus.Failed);
		_ = outcome.TargetId.Should().Be(request.BrowserId);
		_ = outcome.ErrorCode.Should().Be("launch.helper-not-found");
	}

	// W6.3 supersedes the W2.2-era Failed expectation for a helper that STARTED but
	// could not be classified. The browser may already be open, so a definitive Failed
	// would be a false claim; the accepted typed-outcome contract maps this to Unknown
	// (explicit Try again/Copy/Cancel recovery in PickerSession, zero automatic retry).
	// Failed stays reserved for the helper never starting (launch.helper-not-found).
	// Contract pinned on the other side by LaunchOutcomeMatrixTests.HelperClient_NonZeroWithoutTypedPayload_IsUnknown.
	[Fact]
	public async Task LaunchHelperClient_NonZeroExit_FailsClosed()
	{
		LaunchHelperRequest request = BuildRequest();
		LaunchHelperClient client = new(new RecordingRunner(new LaunchHelperProcessResult(true, 7, "", "boom")));

		LaunchHelperOutcome outcome = await client.LaunchAsync(request, CancellationToken.None);

		_ = outcome.Status.Should().Be(LaunchStatus.Unknown);
		_ = outcome.TargetId.Should().Be(request.BrowserId);
		_ = outcome.ErrorCode.Should().Be("launch.helper-nonzero-exit");
		_ = outcome.UserVisibleMessageKey.Should().Be("launch.error.unknown");
	}

	// Same W6.3 contract as NonZeroExit above: an unparseable helper response is an
	// unconfirmed outcome, never a definitive failure.
	[Fact]
	public async Task LaunchHelperClient_MalformedResponse_FailsClosed()
	{
		LaunchHelperRequest request = BuildRequest();
		LaunchHelperClient client = new(new RecordingRunner(new LaunchHelperProcessResult(true, 0, "{not-json", "")));

		LaunchHelperOutcome outcome = await client.LaunchAsync(request, CancellationToken.None);

		_ = outcome.Status.Should().Be(LaunchStatus.Unknown);
		_ = outcome.TargetId.Should().Be(request.BrowserId);
		_ = outcome.ErrorCode.Should().Be("launch.helper-malformed-response");
		_ = outcome.UserVisibleMessageKey.Should().Be("launch.error.unknown");
	}

	private static LaunchHelperRequest BuildRequest()
	{
		return new LaunchHelperRequest(
			"edge-stable",
			@"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
			"https://example.com/",
			IsPrivate: false,
			ProfileId: "stable");
	}

	// NOTE (D-13 / D-41 / D-42): the runner-level kill on the GENERIC catch path is pinned in
	// BroChoos.Platform.Windows.Tests/HelperTimeoutKillTests.cs and in
	// BroChoos.App.Tests/StorageHelperTimeoutTests.cs, where the process-runner suites and the
	// KillObserver seam live. It is deliberately NOT duplicated here: this suite is the
	// client-boundary contract (what the caller is TOLD), and the kill is an implementation
	// detail of abandonment that the client contract must not be able to observe. The boundary
	// tests above are the negative control for that property - they pin the reported outcome
	// shape that the fix is required NOT to change.

	private sealed class RecordingRunner(LaunchHelperProcessResult result) : ILaunchHelperProcessRunner
	{
		private readonly LaunchHelperProcessResult _result = result;

		public string? LastCommand { get; private set; }
		public string? LastRequestPayload { get; private set; }

		public Task<LaunchHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			LastCommand = command;
			LastRequestPayload = requestPayload;
			return Task.FromResult(_result);
		}
	}
}
