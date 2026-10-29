using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

public sealed class LaunchOutcomeMatrixTests
{
	private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

	[Fact]
	public async Task HelperClient_NonZeroWithoutTypedPayload_IsUnknown()
	{
		LaunchHelperRequest request = BuildRequest();
		LaunchHelperClient client = new(new StaticRunner(new LaunchHelperProcessResult(
			Started: true,
			ExitCode: 6,
			StandardOutput: string.Empty,
			StandardError: "helper-crashed")));

		LaunchHelperOutcome outcome = await client.LaunchAsync(request, CancellationToken.None);

		_ = outcome.Status.Should().Be(LaunchStatus.Unknown);
		_ = outcome.ErrorCode.Should().Be("launch.helper-nonzero-exit");
		_ = outcome.UserVisibleMessageKey.Should().Be("launch.error.unknown");
	}

	[Fact]
	public async Task HelperClient_TimeoutMarker_IsTimeout()
	{
		LaunchHelperRequest request = BuildRequest();
		LaunchHelperClient client = new(new StaticRunner(new LaunchHelperProcessResult(
			Started: true,
			ExitCode: 124,
			StandardOutput: string.Empty,
			StandardError: "helper-process-timeout")));

		LaunchHelperOutcome outcome = await client.LaunchAsync(request, CancellationToken.None);

		_ = outcome.Status.Should().Be(LaunchStatus.Timeout);
		_ = outcome.ErrorCode.Should().Be("launch.helper-timeout");
		_ = outcome.UserVisibleMessageKey.Should().Be("launch.error.timeout");
	}

	[Fact]
	public async Task HelperClient_ValidTypedUnknownPayload_PreservedAsUnknown()
	{
		LaunchHelperRequest request = BuildRequest();
		LaunchHelperOutcome typedUnknown = new(
			LaunchStatus.Unknown,
			request.BrowserId,
			"launch.browser-exit-race",
			"helper-launch",
			"launch.error.unknown");
		string payload = System.Text.Json.JsonSerializer.Serialize(typedUnknown, JsonOptions);

		LaunchHelperClient client = new(new StaticRunner(new LaunchHelperProcessResult(
			Started: true,
			ExitCode: 6,
			StandardOutput: payload,
			StandardError: string.Empty)));

		LaunchHelperOutcome outcome = await client.LaunchAsync(request, CancellationToken.None);

		_ = outcome.Status.Should().Be(LaunchStatus.Unknown);
		_ = outcome.ErrorCode.Should().Be("launch.browser-exit-race");
		_ = outcome.TargetId.Should().Be(request.BrowserId);
	}

	private static LaunchHelperRequest BuildRequest()
	{
		return new LaunchHelperRequest(
			"edge-stable",
			@"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
			"https://example.com/",
			IsPrivate: true,
			ProfileId: "Profile 2");
	}

	private sealed class StaticRunner(LaunchHelperProcessResult result) : ILaunchHelperProcessRunner
	{
		private readonly LaunchHelperProcessResult _result = result;

		public Task<LaunchHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
		{
			_ = command;
			_ = requestPayload;
			_ = cancellationToken;
			return Task.FromResult(_result);
		}
	}
}
