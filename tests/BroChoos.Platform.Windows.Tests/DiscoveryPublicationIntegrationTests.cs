using BroChoos.Core.Abstractions;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests;

public sealed class DiscoveryPublicationIntegrationTests
{
	[Fact]
	public void Execute_PostConsentSuccess_PublishesSnapshotFromPersistedCommittedState()
	{
		FakeBrowserDetector detector = new([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\\detected\\edge.exe", "stable", "known-path"),
			new BrowserCandidate("custom-browser", "Custom Browser", @"C:\\detected\\custom.exe", "stable", "registry"),
		]);
		FakeDiscoveryStore store = new(new BrowserCatalogPersistenceResult(
			BrowserCatalogPersistenceStatus.Persisted,
			BrowserCatalogPersistenceReason.Persisted,
			CommittedGeneration: 8,
			PersistedBrowsers:
			[
				new BrowserDefinition("edge-stable", "Microsoft Edge", "Microsoft", "stable", @"C:\\persisted\\edge.exe", "known-path"),
				new BrowserDefinition("chrome-stable", "Google Chrome", "Google", "stable", @"C:\\persisted\\chrome.exe", "registry"),
			]));
		PostConsentDiscoveryPublisher publisher = new(detector, store, TimeProvider.System);

		StorageDiscoveryResponse response = publisher.Execute(new StorageDiscoveryRequest(null));

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
		_ = response.Snapshot.Should().NotBeNull();
		_ = response.Snapshot!.Generation.Should().Be(8);
		_ = response.Snapshot.Candidates.Select(candidate => candidate.BrowserId).Should().Equal("edge-stable", "chrome-stable");
		_ = response.Snapshot.Candidates.Select(candidate => candidate.ExePath).Should().Equal(@"C:\\persisted\\edge.exe", @"C:\\persisted\\chrome.exe");
		_ = detector.DetectCalls.Should().Be(1);
		_ = store.PersistCalls.Should().Be(1);
		_ = store.LastCandidates.Select(candidate => candidate.BrowserId).Should().Equal("edge-stable", "custom-browser");
	}

	[Fact]
	public void Execute_PersistenceFailure_DoesNotPublishSnapshot_AndReturnsRejected()
	{
		FakeBrowserDetector detector = new([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", "stable", "known-path"),
		]);
		FakeDiscoveryStore store = new(new BrowserCatalogPersistenceResult(
			BrowserCatalogPersistenceStatus.RecoverableFailure,
			BrowserCatalogPersistenceReason.WriterFailed));
		PostConsentDiscoveryPublisher publisher = new(detector, store, TimeProvider.System);

		StorageDiscoveryResponse response = publisher.Execute(new StorageDiscoveryRequest(null));

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.Snapshot.Should().BeNull();
		_ = detector.DetectCalls.Should().Be(1);
		_ = store.PersistCalls.Should().Be(1);
	}

	private sealed class FakeBrowserDetector(IReadOnlyList<BrowserCandidate> candidates) : IBrowserDetector
	{
		private readonly IReadOnlyList<BrowserCandidate> _candidates = candidates;

		public int DetectCalls { get; private set; }

		public IReadOnlyList<BrowserCandidate> Detect()
		{
			DetectCalls++;
			return _candidates;
		}
	}

	private sealed class FakeDiscoveryStore(BrowserCatalogPersistenceResult result) : IPostConsentDiscoveryStore
	{
		private readonly BrowserCatalogPersistenceResult _result = result;

		public int PersistCalls { get; private set; }

		public IReadOnlyList<BrowserCandidate> LastCandidates { get; private set; } = [];

		public BrowserCatalogPersistenceResult Persist(IReadOnlyList<BrowserCandidate> candidates, string? portableDataPath)
		{
			PersistCalls++;
			LastCandidates = candidates;
			return _result;
		}
	}

	[Fact]
	public void Execute_RealPersistenceBoundary_PublishesOnlyAfterCasCommittedGeneration()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-w3-5-discovery-" + Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(Path.Combine(root, "backups"));
		try
		{
			SeedBrowsersDocument(
				root,
				generation: 7,
				new BrowserDefinition("custom-existing", "Existing Browser", "Vendor", "stable", @"C:\\persisted\\custom.exe", "manual"));

			PersistingDiscoveryStore realStore = new(root);
			FakeBrowserDetector detector = new([
				new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\\detected\\edge.exe", "stable", "known-path"),
				new BrowserCandidate("unknown-browser", "Unknown", @"C:\\detected\\unknown.exe", "stable", "registry"),
			]);
			PostConsentDiscoveryPublisher publisher = new(detector, realStore, TimeProvider.System);

			StorageDiscoveryResponse response = publisher.Execute(new StorageDiscoveryRequest(null));

			_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
			_ = response.Snapshot.Should().NotBeNull();
			_ = response.Snapshot!.Generation.Should().Be(8, "CAS commit must increment from observed generation 7 to 8 before publication");
			_ = response.Snapshot.Candidates.Select(candidate => candidate.BrowserId).Should().Equal("edge-stable", "custom-existing");
			_ = response.Snapshot.Candidates.Select(candidate => candidate.ExePath).Should().Equal(@"C:\\detected\\edge.exe", @"C:\\persisted\\custom.exe");
			_ = realStore.PersistCalls.Should().Be(1);
			_ = detector.DetectCalls.Should().Be(1);
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	[Fact]
	public void Execute_RealPersistenceBoundary_ReadOnlyDestination_RejectsAndDoesNotPublish()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-w3-5-discovery-" + Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(Path.Combine(root, "backups"));
		try
		{
			File.WriteAllText(
				Path.Combine(root, "browsers.json"),
									 /*lang=json,strict*/
									 "{\"schemaVersion\":99,\"schemaRevision\":0,\"generation\":12,\"browsers\":[]}");

			PersistingDiscoveryStore realStore = new(root);
			FakeBrowserDetector detector = new([
				new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\\detected\\edge.exe", "stable", "known-path"),
			]);
			PostConsentDiscoveryPublisher publisher = new(detector, realStore, TimeProvider.System);

			StorageDiscoveryResponse response = publisher.Execute(new StorageDiscoveryRequest(null));

			_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
			_ = response.Snapshot.Should().BeNull();
			_ = response.ErrorCode.Should().Be("storage.persist.destination-newer-schema-read-only");
			_ = realStore.PersistCalls.Should().Be(1);
			_ = detector.DetectCalls.Should().Be(1);
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	// F1 END-TO-END: the W6.2 manual-add seam (PostConsentDiscoveryPublisher.Publish, the
	// method StorageHelper's RunPublication calls) over the REAL persistence service and the
	// REAL atomic writer. The manual candidate comes out of the REAL ManualBrowserAdmitter
	// over a real file, and the Firefox candidate carries the detector's real id. Before the
	// fix this returned Rejected/NoRecognizedBrowsers and wrote nothing.
	[Fact]
	public void Publish_RealAdmittedManualBrowserAndFirefox_PersistsBothAndPublishesThem()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-f1-publish-" + Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(Path.Combine(root, "backups"));
		try
		{
			string exePath = Path.Combine(root, "MyOwnBrowser.exe");
			File.WriteAllBytes(exePath, [0x4D, 0x5A, 0x00, 0x00]);
			ManualBrowserAdmissionResult admission = new Windows.Browsers.ManualBrowserAdmitter(
				new Windows.Browsers.SystemEnvironmentProbe())
				.Admit(new ManualBrowserAdmissionRequest(ManualBrowserKind.Custom, exePath, "My Own Browser"));
			_ = admission.Admitted.Should().BeTrue();
			BrowserCandidate manual = admission.Candidate!;
			BrowserCandidate firefox = new("firefox-stable", "Mozilla Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", "stable", "known-path");

			PersistingDiscoveryStore realStore = new(root);
			PostConsentDiscoveryPublisher publisher = new(new FakeBrowserDetector([]), realStore, TimeProvider.System);

			StorageDiscoveryResponse response = publisher.Publish(new StorageCandidatePublicationRequest([manual, firefox], null));

			_ = response.ErrorCode.Should().BeNull();
			_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
			_ = response.Snapshot!.Candidates.Select(candidate => candidate.BrowserId).Should().Equal(manual.BrowserId, "firefox-stable");
			_ = response.Snapshot.Candidates[0].IsCustomBrowser.Should().BeTrue();

			(ConfigFileObservation _, BrowsersDocument? persisted) = new ConfigFileObserver(new SystemStorageReadFileSystem())
				.ObserveDocument<BrowsersDocument>(Path.Combine(root, "browsers.json"));
			_ = persisted.Should().NotBeNull();
			_ = persisted!.Browsers.Select(browser => (browser.BrowserId, browser.Vendor, browser.ExePath, browser.IsCustomBrowser)).Should().Equal(
				(manual.BrowserId, "Custom", exePath, true),
				("firefox-stable", "Mozilla", @"C:\Program Files\Mozilla Firefox\firefox.exe", false));
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	private static void SeedBrowsersDocument(string root, long generation, params BrowserDefinition[] browsers)
	{
		string destinationPath = Path.Combine(root, "browsers.json");
		AtomicJsonFileStore store = new(Path.Combine(root, "backups"), TimeSpan.FromSeconds(2));
		store.Write(destinationPath, new BrowsersDocument(browsers) { Generation = generation });
	}

	private sealed class PersistingDiscoveryStore(string root) : IPostConsentDiscoveryStore
	{
		private readonly BrowserCatalogPersistenceService _service = new(
				new FixedStorageRoot(root),
				new ConfigFileObserver(new SystemStorageReadFileSystem()),
				new AtomicConfigDocumentWriterFactory(),
				TimeProvider.System);

		public int PersistCalls { get; private set; }

		public BrowserCatalogPersistenceResult Persist(IReadOnlyList<BrowserCandidate> candidates, string? portableDataPath)
		{
			PersistCalls++;
			return _service.Persist(candidates);
		}
	}

	private sealed class FixedStorageRoot(string root) : IStorageRootResolver
	{
		public string Root { get; } = root;

		public string Backups { get; } = Path.Combine(root, "backups");

		public string Logs { get; } = Path.Combine(root, "logs");

		public string Temp { get; } = Path.Combine(root, "temp");
	}
}
