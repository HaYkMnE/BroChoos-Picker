using BroChoos.Core.Abstractions;

namespace BroChoos.App.HelperClients;

// W2.1-only app-side helper-client placeholders.
// They establish the DI/reference boundary without migrating launch behavior.
internal sealed class NoOpStorageHelperClient : IStorageHelperClient
{
	public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
	{
		return Task.FromResult(HelperClientDispatch.Rejected);
	}

	public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
	{
		return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-client-unavailable"));
	}

	public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
	{
		return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-client-unavailable"));
	}
}

internal sealed class NoOpLaunchHelperClient : ILaunchHelperClient
{
	public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(new LaunchHelperOutcome(
			LaunchStatus.Failed,
			request.BrowserId,
			"launch.helper-client-unavailable",
			"helper-client",
			"launch.error.failed"));
	}
}

internal sealed class NoOpNetworkHelperClient : INetworkHelperClient
{
	public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
	{
		return Task.FromResult(HelperClientDispatch.Rejected);
	}

	public Task<ShortLinkResolveResponse> ResolveShortLinkAsync(ShortLinkResolveRequest request, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(new ShortLinkResolveResponse(
			HelperClientDispatch.Rejected,
			request.Url,
			request.Url,
			"network.helper-client-unavailable",
			FromCache: false));
	}
}

internal sealed class NoOpUpdateHelperClient : IUpdateHelperClient
{
	public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
	{
		return Task.FromResult(HelperClientDispatch.Rejected);
	}
}
