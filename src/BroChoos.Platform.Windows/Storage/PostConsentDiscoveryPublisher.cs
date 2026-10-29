using BroChoos.Core.Abstractions;
using BroChoos.Core.Snapshots;
using BroChoos.Core.Storage;

namespace BroChoos.Platform.Windows.Storage;

public interface IPostConsentDiscoveryStore
{
	BrowserCatalogPersistenceResult Persist(IReadOnlyList<BrowserCandidate> candidates, string? portableDataPath);
}

public sealed class PostConsentDiscoveryStore(IBrowserCatalogPersistenceFactory persistenceFactory) : IPostConsentDiscoveryStore
{
	private readonly IBrowserCatalogPersistenceFactory _persistenceFactory = persistenceFactory;

	public BrowserCatalogPersistenceResult Persist(IReadOnlyList<BrowserCandidate> candidates, string? portableDataPath)
	{
		BrowserCatalogPersistenceService service = _persistenceFactory.Create(portableDataPath);
		return service.Persist(candidates);
	}
}

// Stable wire codes for a rejected catalog persist. The response travels from the StorageHelper
// process to the App as JSON and is shown and logged as an error code, so it must not be an enum
// NAME (renaming a member would silently change the code). Every value here is on the problem
// log's code allowlist (ProblemLog.KnownErrorCodes); a test maps every enum member through this.
public static class BrowserCatalogPersistenceCodes
{
	public const string UnknownCode = "storage.persist.unknown";

	public static string ToErrorCode(BrowserCatalogPersistenceReason reason)
	{
		return reason switch
		{
			BrowserCatalogPersistenceReason.Persisted => "storage.persist.persisted",
			BrowserCatalogPersistenceReason.ScanEmpty => "storage.persist.scan-empty",
			BrowserCatalogPersistenceReason.NoRecognizedBrowsers => "storage.persist.no-recognized-browsers",
			BrowserCatalogPersistenceReason.DestinationOpenFailed => "storage.persist.destination-open-failed",
			BrowserCatalogPersistenceReason.DestinationRequiresRepair => "storage.persist.destination-requires-repair",
			BrowserCatalogPersistenceReason.DestinationNewerRevisionReadOnly => "storage.persist.destination-newer-revision-read-only",
			BrowserCatalogPersistenceReason.DestinationNewerSchemaReadOnly => "storage.persist.destination-newer-schema-read-only",
			BrowserCatalogPersistenceReason.DestinationEnvelopeInvalidReadOnly => "storage.persist.destination-envelope-invalid-read-only",
			BrowserCatalogPersistenceReason.DestinationOlderSchemaMigrationUnavailableReadOnly => "storage.persist.destination-older-schema-migration-unavailable-read-only",
			BrowserCatalogPersistenceReason.WriterFailed => "storage.persist.writer-failed",
			BrowserCatalogPersistenceReason.DestinationClearanceRefusedReadOnly => "storage.persist.destination-clearance-refused-read-only",
			_ => UnknownCode,
		};
	}
}

public sealed class PostConsentDiscoveryPublisher(
	IBrowserDetector browserDetector,
	IPostConsentDiscoveryStore discoveryStore,
	TimeProvider timeProvider)
{
	private readonly IBrowserDetector _browserDetector = browserDetector;
	private readonly IPostConsentDiscoveryStore _discoveryStore = discoveryStore;
	private readonly TimeProvider _timeProvider = timeProvider;

	public StorageDiscoveryResponse Execute(StorageDiscoveryRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		IReadOnlyList<BrowserCandidate> discovered = _browserDetector.Detect();
		return Persist(discovered, request.PortableDataPath);
	}

	// W6.2: the same persistence and snapshot construction as Execute, minus the detection.
	// Kept as one method so a manual add and a consented scan cannot drift apart in how they
	// commit a generation or shape a snapshot — the ONLY difference is where the candidate
	// list came from, and that difference is visible in the signature.
	public StorageDiscoveryResponse Publish(StorageCandidatePublicationRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		return Persist(request.Candidates, request.PortableDataPath);
	}

	private StorageDiscoveryResponse Persist(IReadOnlyList<BrowserCandidate> candidates, string? portableDataPath)
	{
		BrowserCatalogPersistenceResult persisted = _discoveryStore.Persist(candidates, portableDataPath);

		if (persisted.Status != BrowserCatalogPersistenceStatus.Persisted || persisted.CommittedGeneration is null)
		{
			return new StorageDiscoveryResponse(
				HelperClientDispatch.Rejected,
				null,
				BrowserCatalogPersistenceCodes.ToErrorCode(persisted.Reason));
		}

		IReadOnlyList<BrowserCandidate> persistedCandidates = ToSnapshotCandidates(persisted.PersistedBrowsers);
		BrowserSnapshot snapshot = new(persistedCandidates, persisted.CommittedGeneration.Value, _timeProvider.GetUtcNow());
		return new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, snapshot, null);
	}

	private static IReadOnlyList<BrowserCandidate> ToSnapshotCandidates(IReadOnlyList<BrowserDefinition>? persistedBrowsers)
	{
		return persistedBrowsers is null
			? []
			: [.. persistedBrowsers.Select(browser => new BrowserCandidate(
			browser.BrowserId,
			browser.DisplayName,
			browser.ExePath,
			browser.Channel,
			browser.DetectionSource)
			{
				ProfileIds = browser.ProfileIds,
				ProfileNames = browser.ProfileNames,
				HasUnresolvedProfiles = browser.HasUnresolvedProfiles,
				IsCustomBrowser = browser.IsCustomBrowser,
				IsPortableBrowser = browser.IsPortableBrowser,
			})];
	}
}
