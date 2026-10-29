using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

public sealed class ShortLinkConsentPickerFlowTests
{
	[Fact]
	public async Task ShortLinkResolution_DoesNotRunWithoutOneShotConsent()
	{
		FakeLaunchHelperClient launchClient = new();
		RecordingNetworkHelperClient networkClient = new("https://example.org/resolved");
		PickerSession session = NewSession("https://bit.ly/start", launchClient, networkClient);

		await session.ResolveShortLinkInBackgroundForTestsAsync();
		session.ClickCardForTests(1);

		_ = networkClient.Requests.Should().BeEmpty();
		_ = launchClient.LastRequest.Should().NotBeNull();
		_ = launchClient.LastRequest!.Url.Should().Be("https://bit.ly/start");
	}

	[Fact]
	public async Task ShortLinkResolution_RunsWithExplicitOneShotConsent()
	{
		FakeLaunchHelperClient launchClient = new();
		RecordingNetworkHelperClient networkClient = new("https://example.org/resolved");
		PickerSession session = NewSession("https://bit.ly/start", launchClient, networkClient);

		session.ArmShortLinkConsentForNextAttemptForTests();
		await session.ResolveShortLinkInBackgroundForTestsAsync();
		session.ClickCardForTests(1);

		_ = networkClient.Requests.Should().ContainSingle();
		_ = networkClient.Requests[0].ConsentGranted.Should().BeTrue();
		_ = launchClient.LastRequest.Should().NotBeNull();
		_ = launchClient.LastRequest!.Url.Should().Be("https://example.org/resolved");
	}

	[Fact]
	public async Task ShortLinkResolution_OneShotConsentIsConsumed_AndNotReused()
	{
		FakeLaunchHelperClient launchClient = new();
		RecordingNetworkHelperClient networkClient = new("https://example.org/resolved");
		PickerSession session = NewSession("https://bit.ly/start", launchClient, networkClient);

		session.ArmShortLinkConsentForNextAttemptForTests();
		await session.ResolveShortLinkInBackgroundForTestsAsync();
		await session.ResolveShortLinkInBackgroundForTestsAsync();
		session.ClickCardForTests(1);

		_ = networkClient.Requests.Should().ContainSingle("consent is one-shot and must be re-armed for each attempt");
		_ = launchClient.LastRequest.Should().NotBeNull();
		_ = launchClient.LastRequest!.Url.Should().Be("https://example.org/resolved");
	}

	private static PickerSession NewSession(string url, FakeLaunchHelperClient launchClient, RecordingNetworkHelperClient networkClient)
	{
		BrowserSnapshot snapshot = new([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
		], 1, DateTimeOffset.UtcNow);

		ActivationRequest activation = new(
			url,
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		return new PickerSession(
			new PickerRequest(activation, snapshot, FirstRunConsentContract.None),
			launchClient,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance,
			shortLinkExpansionOptions: new ShortLinkExpansionOptions(Enabled: true),
			networkHelperClient: networkClient);
	}

	private sealed class FakeLaunchHelperClient : ILaunchHelperClient
	{
		public LaunchHelperRequest? LastRequest { get; private set; }

		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null));
		}
	}

	private sealed class RecordingNetworkHelperClient(string resolvedUrl) : INetworkHelperClient
	{
		private readonly string _resolvedUrl = resolvedUrl;

		public List<ShortLinkResolveRequest> Requests { get; } = [];

		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Rejected);
		}

		public Task<ShortLinkResolveResponse> ResolveShortLinkAsync(ShortLinkResolveRequest request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(new ShortLinkResolveResponse(
				HelperClientDispatch.Dispatched,
				request.Url,
				_resolvedUrl,
				ErrorCode: null,
				FromCache: false));
		}
	}

	private sealed class FakeStorageHelperClient : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, null, null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.publish-not-used-by-this-test"));
		}
	}

	private sealed class FakeSystemFallbackLauncher : ISystemFallbackLauncher
	{
		public LaunchResult Launch(string url)
		{
			return new LaunchResult(LaunchStatus.Success, "system", null, null, null);
		}
	}
}
