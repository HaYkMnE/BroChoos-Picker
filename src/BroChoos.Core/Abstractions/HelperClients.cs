using BroChoos.Core.Snapshots;

namespace BroChoos.Core.Abstractions;

// W2.1 helper-client boundary contracts only.
// W3.5 extends the storage helper contract with typed discovery/persistence responses.
public sealed record HelperCommand(string CommandName, string Payload);

public sealed record StorageDiscoveryRequest(string? PortableDataPath);

// W6.2: the manual-admission counterpart of discovery. The candidates here were NOT
// discovered — each one was named by the user and already validated by the platform
// admission gate — so this request must never be answered by scanning the machine.
public sealed record StorageCandidatePublicationRequest(
	IReadOnlyList<BrowserCandidate> Candidates,
	string? PortableDataPath);

public sealed record StorageDiscoveryResponse(
	HelperClientDispatch Dispatch,
	BrowserSnapshot? Snapshot,
	string? ErrorCode);

public sealed record LaunchHelperRequest(
	string BrowserId,
	string ExePath,
	string Url,
	bool IsPrivate,
	string? ProfileId);

public sealed record LaunchHelperOutcome(
	LaunchStatus Status,
	string TargetId,
	string? ErrorCode,
	string? FailureCategory,
	string? UserVisibleMessageKey)
{
	public static LaunchHelperOutcome FromLaunchResult(LaunchResult result)
	{
		return new LaunchHelperOutcome(
			result.Status,
			result.TargetId,
			result.ErrorCode,
			result.FailureCategory,
			result.UserVisibleMessageKey);
	}
}

public sealed record ShortLinkResolveRequest(string Url, bool ConsentGranted);

public sealed record ShortLinkResolveResponse(
	HelperClientDispatch Dispatch,
	string OriginalUrl,
	string EffectiveUrl,
	string? ErrorCode,
	bool FromCache);

public enum HelperClientDispatch
{
	Dispatched,
	Rejected,
}

public interface IStorageHelperClient
{
	Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken);

	Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken);

	// Publishes candidates the CALLER already validated (W6.2 manual/portable admission).
	// Separate from DiscoverAndPersistAsync on purpose: the two differ in the one way that
	// matters to the consent guarantee — discovery is authorized to look at the machine,
	// publication is not authorized to look at anything.
	Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken);
}

public interface ILaunchHelperClient
{
	Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken);
}

public interface INetworkHelperClient
{
	Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken);

	Task<ShortLinkResolveResponse> ResolveShortLinkAsync(ShortLinkResolveRequest request, CancellationToken cancellationToken);
}

public interface IUpdateHelperClient
{
	Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken);
}
