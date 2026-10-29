using BroChoos.Core.Abstractions;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

public sealed class BrowserCatalogPersistenceServiceTests
{
	[Fact]
	public void Persist_MapsKnownCandidatesUsingOneInjectedScanInstant()
	{
		DateTimeOffset scanInstant = new(2026, 8, 31, 12, 34, 56, TimeSpan.Zero);
		FakeStorage storage = new();
		CapturingWriterFactory writers = new();
		BrowserCatalogPersistenceService service = Build(storage, writers, scanInstant);
		BrowserCandidate[] candidates =
		[
			new("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path"),
			new("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "registry"),
		];

		BrowserCatalogPersistenceResult result = service.Persist(candidates);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.Persisted);
		_ = result.CommittedGeneration.Should().Be(1);
		_ = writers.Document.Should().NotBeNull();
		_ = writers.Document!.Browsers.Should().Equal(
			new BrowserDefinition("edge-stable", "Microsoft Edge", "Microsoft", "stable", @"C:\Edge\msedge.exe", "known-path", scanInstant),
			new BrowserDefinition("chrome-stable", "Google Chrome", "Google", "stable", @"C:\Chrome\chrome.exe", "registry", scanInstant));
	}

	[Fact]
	public void Persist_ReplacesMatchingIdsInDetectorOrderThenKeepsUnmatchedExistingOrder()
	{
		FakeStorage storage = new();
		storage.AddBrowsers(
			new BrowserDefinition("custom-first", "Custom First", "Custom", "stable", "custom-1.exe", "manual"),
			new BrowserDefinition("edge-stable", "Old Edge", "Microsoft", "stable", "old-edge.exe", "registry"),
			new BrowserDefinition("custom-second", "Custom Second", "Custom", "stable", "custom-2.exe", "manual"));
		CapturingWriterFactory writers = new();
		BrowserCatalogPersistenceService service = Build(storage, writers, DateTimeOffset.UnixEpoch);

		_ = service.Persist(
		[
			new("chrome-stable", "Chrome", "chrome.exe", "stable", "known-path"),
			new("edge-stable", "New Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = writers.Document!.Browsers.Select(browser => browser.BrowserId).Should().Equal(
			"chrome-stable",
			"edge-stable",
			"custom-first",
			"custom-second");
		_ = writers.Document.Browsers[1].DisplayName.Should().Be("New Edge");
	}

	[Fact]
	public void Persist_KnownIdsAreCaseInsensitiveFirstOccurrenceWinsAndCanonicalIdsAreWritten()
	{
		FakeStorage storage = new();
		storage.AddBrowsers(
			new BrowserDefinition("EDGE-STABLE", "Old Edge", "Microsoft", "stable", "old-edge.exe", "manual"),
			new BrowserDefinition("custom", "Custom", "Vendor", "stable", "custom.exe", "manual"));
		CapturingWriterFactory writers = new();
		CountingTimeProvider time = new();
		BrowserCatalogPersistenceService service = new(
			new FixedStorageRoot("root"),
			new ConfigFileObserver(storage),
			writers,
			time);

		BrowserCatalogPersistenceResult result = service.Persist(
		[
			new("EDGE-STABLE", "First Edge", "first-edge.exe", "stable", "known-path"),
			new("edge-stable", "Second Edge", "second-edge.exe", "stable", "registry"),
			new("Chrome-Stable", "Chrome", "chrome.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
		_ = time.AccessCount.Should().Be(1);
		_ = writers.Document!.Browsers.Select(browser => browser.BrowserId).Should().Equal(
			"edge-stable",
			"chrome-stable",
			"custom");
		_ = writers.Document.Browsers[0].DisplayName.Should().Be("First Edge");
	}

	[Fact]
	public void Persist_EmptyDetectionDoesNotWriteAndReturnsRecoverableFailure()
	{
		FakeStorage storage = new();
		storage.AddRaw(/*lang=json,strict*/ "{\"keep\":\"byte-identical\"}");
		byte[] bytesBefore = storage.GetBrowsersBytes();
		storage.ResetAccessCount();
		CapturingWriterFactory writers = new();
		CountingTimeProvider time = new();
		BrowserCatalogPersistenceService service = new(
			new FixedStorageRoot("root"),
			new ConfigFileObserver(storage),
			writers,
			time);

		BrowserCatalogPersistenceResult result = service.Persist([]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.RecoverableFailure);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.ScanEmpty);
		_ = writers.Document.Should().BeNull();
		_ = time.AccessCount.Should().Be(0);
		_ = time.TimestampAccessCount.Should().Be(0);
		_ = storage.AccessCount.Should().Be(0);
		_ = storage.GetBrowsersBytes().Should().Equal(bytesBefore);
	}

	[Fact]
	public void Persist_NewerSameMajorRevisionDoesNotWriteAndReturnsReadOnly()
	{
		FakeStorage storage = new();
		storage.AddRaw(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":1,\"browsers\":[],\"futureField\":\"keep\"}");
		byte[] bytesBefore = storage.GetBrowsersBytes();
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.ReadOnly);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.DestinationNewerRevisionReadOnly);
		_ = writers.Document.Should().BeNull();
		_ = storage.GetBrowsersBytes().Should().Equal(bytesBefore);
	}

	[Fact]
	public void Persist_NewerSchemaDoesNotWriteAndReturnsReadOnly()
	{
		FakeStorage storage = new();
		storage.AddRaw(/*lang=json,strict*/ "{\"schemaVersion\":99,\"schemaRevision\":0,\"browsers\":[]}");
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.ReadOnly);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.DestinationNewerSchemaReadOnly);
		_ = writers.Document.Should().BeNull();
	}

	[Theory]
	[InlineData(-1, 0, BrowserCatalogPersistenceReason.DestinationEnvelopeInvalidReadOnly)]
	[InlineData(0, 0, BrowserCatalogPersistenceReason.DestinationOlderSchemaMigrationUnavailableReadOnly)]
	public void Persist_OtherUnsupportedSchemaStatesDoNotWriteAndReturnPreciseReadOnlyReason(
		int schemaVersion,
		int schemaRevision,
		BrowserCatalogPersistenceReason expectedReason)
	{
		FakeStorage storage = new();
		storage.AddRaw($$"""{"schemaVersion":{{schemaVersion}},"schemaRevision":{{schemaRevision}},"browsers":[]}""");
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.ReadOnly);
		_ = result.Reason.Should().Be(expectedReason);
		_ = writers.Document.Should().BeNull();
	}

	[Theory]
	[InlineData("not-json", false, BrowserCatalogPersistenceReason.DestinationRequiresRepair)]
	[InlineData(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}", false, BrowserCatalogPersistenceReason.DestinationRequiresRepair)]
	[InlineData(/*lang=json,strict*/ "{\"unread\":true}", true, BrowserCatalogPersistenceReason.DestinationOpenFailed)]
	public void Persist_WhenExistingCatalogRequiresRepairOrCannotOpen_DoesNotWriteAndReturnsPreciseReason(
		string existingJson,
		bool openFails,
		BrowserCatalogPersistenceReason expectedReason)
	{
		FakeStorage storage = new();
		storage.AddRaw(existingJson);
		storage.OpenFailure = openFails ? new IOException("sharing violation") : null;
		byte[] bytesBefore = storage.GetBrowsersBytes();
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.RecoverableFailure);
		_ = result.Reason.Should().Be(expectedReason);
		_ = writers.Document.Should().BeNull();
		_ = storage.GetBrowsersBytes().Should().Equal(bytesBefore);
	}

	[Fact]
	public void Persist_WriterIoFailureReturnsRecoverableFailureAndPreservesExistingBytes()
	{
		FakeStorage storage = new();
		storage.AddBrowsers(new BrowserDefinition("custom", "Custom", "Vendor", "stable", "custom.exe", "manual"));
		byte[] bytesBefore = storage.GetBrowsersBytes();
		CapturingWriterFactory writers = new() { Failure = new IOException("disk unavailable") };

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.RecoverableFailure);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.WriterFailed);
		_ = result.DestinationModified.Should().BeNull();
		_ = result.Restored.Should().BeNull();
		_ = storage.GetBrowsersBytes().Should().Equal(bytesBefore);
	}

	[Theory]
	[InlineData(WriterFailureKind.Io, false, null)]
	[InlineData(WriterFailureKind.Io, true, false)]
	[InlineData(WriterFailureKind.Io, true, true)]
	[InlineData(WriterFailureKind.UnauthorizedAccess, false, null)]
	[InlineData(WriterFailureKind.UnauthorizedAccess, true, false)]
	[InlineData(WriterFailureKind.UnauthorizedAccess, true, true)]
	public void Persist_ClassifiedWriterFailureRetainsMutationAndRestoreOutcomeWithoutPath(
		WriterFailureKind failureKind,
		bool destinationModified,
		bool? restored)
	{
		FakeStorage storage = new();
		Exception failure = failureKind switch
		{
			WriterFailureKind.Io => new IOException("safe message"),
			WriterFailureKind.UnauthorizedAccess => new UnauthorizedAccessException("safe message"),
			_ => throw new InvalidOperationException($"Unsupported writer failure kind: {failureKind}"),
		};
		failure.Data["destinationModified"] = destinationModified;
		failure.Data["path"] = @"C:\Users\secret\browsers.json";
		if (restored is not null)
		{
			failure.Data["restored"] = restored.Value;
		}
		CapturingWriterFactory writers = new() { Failure = failure };

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.RecoverableFailure);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.WriterFailed);
		_ = result.DestinationModified.Should().Be(destinationModified);
		_ = result.Restored.Should().Be(restored);
		_ = result.ToString().Should().NotContain("secret");
	}

	[Fact]
	public void Persist_OnlyUnknownCandidatesDoesNotRewriteExistingCatalog()
	{
		BrowserDefinition existing = new(
			"custom-browser",
			"User Name",
			"User Vendor",
			"custom",
			"user-path.exe",
			"manual");
		FakeStorage storage = new();
		storage.AddBrowsers(existing);
		byte[] bytesBefore = storage.GetBrowsersBytes();
		CapturingWriterFactory writers = new();
		CountingTimeProvider time = new();
		BrowserCatalogPersistenceService service = new(
			new FixedStorageRoot("root"),
			new ConfigFileObserver(storage),
			writers,
			time);

		BrowserCatalogPersistenceResult result = service.Persist(
		[
			new("custom-browser", "Detected Name", "detected-path.exe", "stable", "registry"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.RecoverableFailure);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.NoRecognizedBrowsers);
		_ = writers.Document.Should().BeNull();
		_ = storage.AccessCount.Should().Be(0);
		_ = storage.OpenReadCount.Should().Be(0);
		_ = time.AccessCount.Should().Be(0);
		_ = time.TimestampAccessCount.Should().Be(0);
		_ = storage.GetBrowsersBytes().Should().Equal(bytesBefore);
	}

	[Fact]
	public void Persist_OnlyUnknownCandidatesDoesNotCreateAnEmptyCatalog()
	{
		FakeStorage storage = new();
		CapturingWriterFactory writers = new();
		CountingTimeProvider time = new();
		BrowserCatalogPersistenceService service = new(
			new FixedStorageRoot("root"),
			new ConfigFileObserver(storage),
			writers,
			time);

		BrowserCatalogPersistenceResult result = service.Persist(
		[
			new("custom-browser", "Detected Name", "detected-path.exe", "stable", "registry"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.RecoverableFailure);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.NoRecognizedBrowsers);
		_ = writers.Document.Should().BeNull();
		_ = storage.AccessCount.Should().Be(0);
		_ = storage.OpenReadCount.Should().Be(0);
		_ = time.AccessCount.Should().Be(0);
		_ = time.TimestampAccessCount.Should().Be(0);
	}

	// F1: the detector emits three Firefox channels (WindowsBrowserDetector.FirefoxId,
	// FirefoxDevId, FirefoxNightlyId). Before the fix the allowlist stopped at Brave, so a
	// Firefox-only machine persisted nothing and the publish failed NoRecognizedBrowsers.
	[Theory]
	[InlineData("firefox-stable")]
	[InlineData("firefox-developer")]
	[InlineData("FIREFOX-NIGHTLY")]
	public void Persist_DetectedFirefoxChannels_ArePersistedWithTheMozillaVendor(string browserId)
	{
		FakeStorage storage = new();
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new(browserId, "Firefox", @"C:\Firefox\firefox.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
		BrowserDefinition persisted = writers.Document!.Browsers.Should().ContainSingle().Subject;
		_ = persisted.BrowserId.Should().Be(browserId.ToLowerInvariant());
		_ = persisted.Vendor.Should().Be("Mozilla");
	}

	// F1: ManualBrowserAdmitter produces custom-* / portable-* ids with matching provenance
	// (detection source "manual"/"portable" and exactly one of the two flags). Those are
	// admitted browsers and must persist, keeping their flags and the user's display name.
	[Fact]
	public void Persist_AdmittedManualAndPortableCandidates_ArePersistedWithTheirFlags()
	{
		FakeStorage storage = new();
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("custom-myownbrowser-0123abcd", "My Own Browser", @"C:\Tools\MyOwnBrowser.exe", "custom", "manual")
			{
				IsCustomBrowser = true,
			},
			new("Portable-Chromium-Chrome-89abcdef", "Portable chrome", @"D:\Apps\Chrome\chrome.exe", "portable", "portable")
			{
				IsPortableBrowser = true,
			},
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
		_ = writers.Document!.Browsers.Should().Equal(
			new BrowserDefinition("custom-myownbrowser-0123abcd", "My Own Browser", "Custom", "custom", @"C:\Tools\MyOwnBrowser.exe", "manual", DateTimeOffset.UnixEpoch, IsCustomBrowser: true),
			new BrowserDefinition("portable-chromium-chrome-89abcdef", "Portable chrome", "Portable", "portable", @"D:\Apps\Chrome\chrome.exe", "portable", DateTimeOffset.UnixEpoch, IsPortableBrowser: true));
	}

	// The prefix alone is not provenance: a custom-/portable- id that did not come out of the
	// admission gate (wrong detection source or flags) is still an unknown id and is dropped.
	[Theory]
	[InlineData("custom-browser", "registry", false, false)]
	[InlineData("custom-browser", "manual", false, false)]
	[InlineData("custom-browser", "manual", true, true)]
	[InlineData("custom-", "manual", true, false)]
	[InlineData("portable-browser", "portable", true, false)]
	[InlineData("portable-browser", "manual", false, true)]
	[InlineData("portable-", "portable", false, true)]
	public void Persist_ManualPrefixWithoutAdmissionProvenance_IsNotRecognized(
		string browserId,
		string detectionSource,
		bool isCustom,
		bool isPortable)
	{
		FakeStorage storage = new();
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new(browserId, "Name", "path.exe", "custom", detectionSource)
			{
				IsCustomBrowser = isCustom,
				IsPortableBrowser = isPortable,
			},
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.RecoverableFailure);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.NoRecognizedBrowsers);
		_ = writers.Document.Should().BeNull();
	}

	[Fact]
	public void Persist_ReconcilesTheExactDocumentValidatedByTheObserverWithoutASecondRead()
	{
		FakeStorage storage = new();
		storage.AddBrowsers(new BrowserDefinition("observed", "Observed", "Vendor", "stable", "observed.exe", "manual"));
		storage.SecondOpenBytes = FakeStorage.SerializeBrowsers(
			new BrowserDefinition("raced", "Raced", "Vendor", "stable", "raced.exe", "manual"));
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
		_ = storage.OpenReadCount.Should().Be(1);
		_ = writers.Document!.Browsers.Select(browser => browser.BrowserId).Should().Equal("edge-stable", "observed");
	}

	[Fact]
	public void Persist_AbsentCatalog_UsesOnlyTheClearanceGuardedCreateRoute()
	{
		FakeStorage storage = new();
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
		_ = result.CommittedGeneration.Should().Be(1);
		_ = writers.CreateCallCount.Should().Be(1);
		_ = writers.ReconcileCallCount.Should().Be(0);
	}

	[Fact]
	public void Persist_CompatibleCatalog_UsesOnlyTheClearanceGuardedReconcileRoute()
	{
		FakeStorage storage = new();
		storage.AddBrowsers(new BrowserDefinition("custom", "Custom", "Vendor", "stable", "custom.exe", "manual"));
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
		_ = result.CommittedGeneration.Should().Be(1);
		_ = writers.CreateCallCount.Should().Be(0);
		_ = writers.ReconcileCallCount.Should().Be(1);
	}

	[Fact]
	public void Persist_AbsentCatalogWithoutCreateClearance_ReturnsRedactedReadOnlyWithoutCreatingWriter()
	{
		FakeStorage storage = new();
		CapturingWriterFactory writers = new();
		BrowserCatalogPersistenceService service = new(
			new FixedStorageRoot("root"),
			new ConfigFileObserver(storage),
			writers,
			new FixedTimeProvider(DateTimeOffset.UnixEpoch),
			new RefusingClearanceEvaluator(refuseCreate: true));

		BrowserCatalogPersistenceResult result = service.Persist(
		[
			new("edge-stable", "Edge", @"C:\Users\secret\edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.ReadOnly);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.DestinationClearanceRefusedReadOnly);
		_ = result.ToString().Should().NotContain("secret");
		_ = writers.FactoryCreateCallCount.Should().Be(0);
		_ = writers.CreateCallCount.Should().Be(0);
		_ = writers.ReconcileCallCount.Should().Be(0);
	}

	[Fact]
	public void Persist_CompatibleCatalogWithoutReconcileClearance_ReturnsRedactedReadOnlyWithoutCreatingWriterOrChangingBytes()
	{
		FakeStorage storage = new();
		storage.AddBrowsers(new BrowserDefinition("custom", "Custom", "Vendor", "stable", "custom.exe", "manual"));
		byte[] bytesBefore = storage.GetBrowsersBytes();
		CapturingWriterFactory writers = new();
		BrowserCatalogPersistenceService service = new(
			new FixedStorageRoot("root"),
			new ConfigFileObserver(storage),
			writers,
			new FixedTimeProvider(DateTimeOffset.UnixEpoch),
			new RefusingClearanceEvaluator(refuseReconcile: true));

		BrowserCatalogPersistenceResult result = service.Persist(
		[
			new("edge-stable", "Edge", @"C:\Users\secret\edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.ReadOnly);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.DestinationClearanceRefusedReadOnly);
		_ = result.ToString().Should().NotContain("secret");
		_ = writers.FactoryCreateCallCount.Should().Be(0);
		_ = writers.CreateCallCount.Should().Be(0);
		_ = writers.ReconcileCallCount.Should().Be(0);
		_ = storage.GetBrowsersBytes().Should().Equal(bytesBefore);
	}

	[Fact]
	public void GetNonWritableReason_ClearanceRefusedReadOnly_MapsToDestinationClearanceRefusedReadOnly()
	{
		BrowserCatalogPersistenceReason result = BrowserCatalogPersistenceService.GetNonWritableReason(
			ConfigLoadReason.ClearanceRefusedReadOnly);

		_ = result.Should().Be(BrowserCatalogPersistenceReason.DestinationClearanceRefusedReadOnly);
	}

	[Fact]
	public void GetNonWritableReason_UndefinedFutureReason_ThrowsInvalidOperationException()
	{
		Action act = () => BrowserCatalogPersistenceService.GetNonWritableReason((ConfigLoadReason)int.MaxValue);

		_ = act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Unsupported non-writable config load reason*");
	}

	[Fact]
	public void Persist_ObserverClassifiedReadFailureReturnsRecoverableWithoutThrowing()
	{
		FakeStorage storage = new();
		storage.AddBrowsers(new BrowserDefinition("observed", "Observed", "Vendor", "stable", "observed.exe", "manual"));
		storage.OpenFailureNumber = 1;
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult? result = null;
		Action act = () => result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = act.Should().NotThrow();
		_ = result!.Status.Should().Be(BrowserCatalogPersistenceStatus.RecoverableFailure);
		_ = result.Reason.Should().Be(BrowserCatalogPersistenceReason.DestinationOpenFailed);
		_ = storage.OpenReadCount.Should().Be(1);
		_ = writers.Document.Should().BeNull();
	}

	[Fact]
	public void Persist_UsesResolvedPathsAndBoundedTimeoutWithoutMutatingCandidates()
	{
		FakeStorage storage = new();
		CapturingWriterFactory writers = new();
		List<BrowserCandidate> candidates =
		[
			new("chrome-stable", "Chrome", "chrome.exe", "stable", "known-path"),
			new("edge-stable", "Edge", "edge.exe", "stable", "registry"),
		];
		BrowserCandidate[] before = [.. candidates];

		_ = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(candidates);

		_ = writers.BackupsDirectory.Should().Be(Path.Combine("root", "backups"));
		_ = writers.DestinationPath.Should().Be(Path.Combine("root", "browsers.json"));
		_ = writers.WriteLockTimeout.Should().Be(TimeSpan.FromMilliseconds(1500));
		_ = candidates.Should().Equal(before);
	}

	// 0A-5 debt 15: the constructor takes NO read seam, and this test IS that arity.
	//
	// The parameter that used to sit here was assigned to no field and read by nothing —
	// the service reads through its observer (ObserveDocument), which is the type that
	// holds IStorageReadFileSystem. The only cost of the dead parameter was that every
	// caller had to supply a seam that was discarded, and that its null check made it look
	// load-bearing. Re-adding it breaks the build on this call, which is the point: an
	// arity is not something a value assertion can pin.
	[Fact]
	public void Constructor_TakesNoReadSeam_BecauseTheServiceReadsThroughTheObserver()
	{
		FakeStorage storage = new();
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceService service = new(
			new FixedStorageRoot("root"),
			new ConfigFileObserver(storage),
			writers,
			new FixedTimeProvider(DateTimeOffset.UnixEpoch));

		BrowserCatalogPersistenceResult result = service.Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
		_ = result.CommittedGeneration.Should().Be(1);
		_ = writers.Document!.Browsers.Select(browser => browser.BrowserId).Should().Equal("edge-stable");
	}

	[Fact]
	public void Persist_CompatibleCatalogIncrementsCommittedGenerationFromObservedGeneration()
	{
		FakeStorage storage = new();
		storage.AddRaw(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0,\"generation\":7,\"browsers\":[]}");
		CapturingWriterFactory writers = new();

		BrowserCatalogPersistenceResult result = Build(storage, writers, DateTimeOffset.UnixEpoch).Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
		_ = result.CommittedGeneration.Should().Be(8);
		_ = writers.LastExpectedGeneration.Should().NotBeNull();
		_ = writers.LastExpectedGeneration!.Value.ExpectsDocument.Should().BeTrue();
		_ = writers.LastExpectedGeneration!.Value.Value.Should().Be(7);
	}

	// 0A-5 debt 16: the write-lock timeout is DERIVED from what is left of this Persist's
	// budget, not a constant handed to a parameter whose whole contract says it cannot be
	// one (IConfigDocumentWriter.cs:8-13).
	//
	// The clock is advanced FROM INSIDE THE READ SEAM, so the advance is real elapsed time
	// during the observation rather than a value poked in from outside — the observation is
	// exactly the cost the derivation has to subtract. Deterministic: the fake clock's
	// timestamp only moves when the seam is called, so the expectations are exact rather
	// than ranges.
	//
	// The last four rows are the floor, and the floor is not cosmetic. The unfloored
	// derivation lands in one of THREE bands, all of which Mutex.WaitOne treats differently
	// and none of which is a bounded wait — the measured reasoning is on
	// BrowserCatalogPersistenceService.MinimumWriteLockTimeout, and the rows below are one
	// per band:
	//
	//   * 1500 ms spent leaves exactly zero: WaitOne would POLL without waiting.
	//   * 1501.5 ms leaves -1.5 ms, which truncates to -1 and is WaitOne's INFINITE wait.
	//     This is the band a reader is most likely to miss, because -1.5 ms does not
	//     compare equal to Timeout.InfiniteTimeSpan and hangs anyway.
	//   * 3000 ms leaves -1500 ms, which would throw ArgumentOutOfRangeException.
	//
	// Every one of them must come out as the floor instead, which is what makes an
	// overrunning Persist a bounded failure rather than a hang or a defect. The cost is a
	// double so the -1.5 ms row can exist at all: the hazard band is 1 ms wide and integer
	// milliseconds step straight over it.
	//
	// VERIFIED BY MUTATION, not by passing: returning `remaining` unfloored fails the 1500,
	// 1501.5 and 3000 ms rows and leaves 400 and 1450 passing, with the 1501.5 row reporting
	// "Expected 50ms, but found -1ms and 500.0µs" — the hazard value itself. The 1450 ms row
	// leaves exactly 50 ms and returns through the REMAINING branch, so it documents the
	// boundary rather than pinning the comparison: swapping `<` for `<=` keeps it passing.
	[Theory]
	[InlineData(400d, 1100)]
	[InlineData(1450d, 50)]
	[InlineData(1500d, 50)]
	[InlineData(1501.5d, 50)]
	[InlineData(3000d, 50)]
	public void Persist_DerivesTheWriteLockTimeoutFromTheRemainingBudgetFlooredAt50Ms(
		double observationCostMilliseconds,
		int expectedTimeoutMilliseconds)
	{
		FakeStorage storage = new();
		storage.AddBrowsers(new BrowserDefinition("custom", "Custom", "Vendor", "stable", "custom.exe", "manual"));
		AdvancingTimeProvider time = new(DateTimeOffset.UnixEpoch);
		storage.OnOpenRead = () => time.Advance(TimeSpan.FromMilliseconds(observationCostMilliseconds));
		CapturingWriterFactory writers = new();
		BrowserCatalogPersistenceService service = new(
			new FixedStorageRoot("root"),
			new ConfigFileObserver(storage),
			writers,
			time);

		BrowserCatalogPersistenceResult result = service.Persist(
		[
			new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
		]);

		_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
		_ = writers.WriteLockTimeout.Should().Be(TimeSpan.FromMilliseconds(expectedTimeoutMilliseconds));
	}

	[Fact]
	public void Persist_WithRealAtomicWriterCreatesCamelCaseJsonWithoutTempResidue()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-browser-catalog-" + Guid.NewGuid().ToString("N")[..12]);
		string backups = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(backups);
		try
		{
			SystemStorageReadFileSystem fileSystem = new();
			BrowserCatalogPersistenceService service = new(
				new FixedStorageRoot(root),
				new ConfigFileObserver(fileSystem),
				new AtomicConfigDocumentWriterFactory(),
				new FixedTimeProvider(DateTimeOffset.UnixEpoch));

			BrowserCatalogPersistenceResult result = service.Persist(
			[
				new("edge-stable", "Edge", "edge.exe", "stable", "known-path"),
			]);

			string destination = Path.Combine(root, "browsers.json");
			_ = result.Status.Should().Be(BrowserCatalogPersistenceStatus.Persisted);
			using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(destination));
			_ = json.RootElement.TryGetProperty("schemaVersion", out _).Should().BeTrue();
			_ = json.RootElement.TryGetProperty("SchemaVersion", out _).Should().BeFalse();
			_ = json.RootElement.GetProperty("browsers")[0].TryGetProperty("browserId", out _).Should().BeTrue();
			_ = Directory.GetFiles(root, "*.tmp").Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static BrowserCatalogPersistenceService Build(
		FakeStorage storage,
		CapturingWriterFactory writers,
		DateTimeOffset now)
	{
		return new(
			new FixedStorageRoot("root"),
			new ConfigFileObserver(storage),
			writers,
			new FixedTimeProvider(now));
	}

	private sealed class FixedStorageRoot(string root) : IStorageRootResolver
	{
		public string Root { get; } = root;

		public string Backups { get; } = Path.Combine(root, "backups");

		public string Logs { get; } = Path.Combine(root, "logs");

		public string Temp { get; } = Path.Combine(root, "temp");
	}

	// Frozen in BOTH clocks: GetUtcNow for the scan instant, and the MONOTONIC pair
	// (GetTimestamp/TimestampFrequency) the write-lock derivation reads. Without the
	// monotonic half the base class would hand back real Stopwatch ticks, so "how much
	// budget is left" would drift with machine speed and no exact expectation would hold.
	private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow()
		{
			return now;
		}

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override long GetTimestamp()
		{
			return 0;
		}
	}

	// A monotonic clock that moves ONLY when a test moves it, so elapsed time inside one
	// Persist is exact. GetUtcNow is frozen: the scan instant is a separate concern and
	// tests that count its reads must stay unaffected.
	private sealed class AdvancingTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private long _timestamp;

		public override DateTimeOffset GetUtcNow()
		{
			return now;
		}

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override long GetTimestamp()
		{
			return _timestamp;
		}

		public void Advance(TimeSpan elapsed)
		{
			_timestamp += elapsed.Ticks;
		}
	}

	private sealed class CountingTimeProvider : TimeProvider
	{
		public int AccessCount { get; private set; }
		public int TimestampAccessCount { get; private set; }

		public override DateTimeOffset GetUtcNow()
		{
			AccessCount++;
			return DateTimeOffset.UnixEpoch;
		}

		public override long GetTimestamp()
		{
			TimestampAccessCount++;
			return 0;
		}
	}

	public enum WriterFailureKind
	{
		Io,
		UnauthorizedAccess,
	}

	private sealed class RefusingClearanceEvaluator(
		bool refuseCreate = false,
		bool refuseReconcile = false) : IBrowserCatalogClearanceEvaluator
	{
		public PersistClearance? TryClearCreate(ConfigFileObservation observation)
		{
			return refuseCreate ? null : PersistClearance.TryClear(observation);
		}

		public ReconcileClearance? TryClearReconcile(
			ConfigFileObservation observation,
			ConfigLoadDecision decision)
		{
			return refuseReconcile ? null : ReconcileClearance.TryClear(observation, decision);
		}
	}

	private sealed class CapturingWriterFactory : IConfigDocumentWriterFactory, IBrowserCatalogDocumentWriter
	{
		public BrowsersDocument? Document { get; private set; }
		public Exception? Failure { get; init; }
		public string? BackupsDirectory { get; private set; }
		public string? DestinationPath { get; private set; }
		public TimeSpan? WriteLockTimeout { get; private set; }
		public int FactoryCreateCallCount { get; private set; }
		public int CreateCallCount { get; private set; }
		public int ReconcileCallCount { get; private set; }
		public ExpectedDocumentGeneration? LastExpectedGeneration { get; private set; }

		public IBrowserCatalogDocumentWriter Create(string backupsDirectory)
		{
			FactoryCreateCallCount++;
			BackupsDirectory = backupsDirectory;
			return this;
		}

		public void CreateAbsent(
			string destinationPath,
			BrowsersDocument document,
			ExpectedDocumentGeneration expectedGeneration,
			PersistClearance clearance,
			TimeSpan writeLockTimeout,
			Action<long, BrowsersDocument> onCommitted)
		{
			ArgumentNullException.ThrowIfNull(clearance);
			CreateCallCount++;
			_ = expectedGeneration.ExpectsDocument.Should().BeFalse();
			_ = expectedGeneration.Value.Should().Be(0);
			LastExpectedGeneration = expectedGeneration;
			Capture(destinationPath, document, writeLockTimeout);
			onCommitted(1, document with { Generation = 1 });
		}

		public void ReconcileCompatible(
			string destinationPath,
			BrowsersDocument document,
			ExpectedDocumentGeneration expectedGeneration,
			ReconcileClearance clearance,
			TimeSpan writeLockTimeout,
			Action<long, BrowsersDocument> onCommitted)
		{
			ArgumentNullException.ThrowIfNull(clearance);
			ReconcileCallCount++;
			_ = expectedGeneration.ExpectsDocument.Should().BeTrue();
			LastExpectedGeneration = expectedGeneration;
			Capture(destinationPath, document, writeLockTimeout);
			onCommitted(checked(expectedGeneration.Value + 1), document with { Generation = checked(expectedGeneration.Value + 1) });
		}

		private void Capture(string destinationPath, BrowsersDocument document, TimeSpan writeLockTimeout)
		{
			DestinationPath = destinationPath;
			Document = document;
			WriteLockTimeout = writeLockTimeout;
			if (Failure is not null)
			{
				throw Failure;
			}
		}
	}

	private sealed class FakeStorage : IStorageReadFileSystem
	{
		private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
		private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
		{
			PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
		};

		public int AccessCount { get; private set; }
		public int OpenReadCount { get; private set; }
		public Exception? OpenFailure { get; set; }
		public int? OpenFailureNumber { get; set; }
		public byte[]? SecondOpenBytes { get; set; }

		// Lets a test charge REAL elapsed time to the observation, which is the cost the
		// write-lock derivation has to subtract. Null for every other test, so nothing
		// else changes behaviour.
		public Action? OnOpenRead { get; set; }

		public void AddBrowsers(params BrowserDefinition[] browsers)
		{
			_files[Path.Combine("root", "browsers.json")] = SerializeBrowsers(browsers);
		}

		public static byte[] SerializeBrowsers(params BrowserDefinition[] browsers)
		{
			BrowsersDocument document = new(browsers);
			using MemoryStream stream = new();
			System.Text.Json.JsonSerializer.Serialize(stream, document, SerializerOptions);
			return stream.ToArray();
		}

		public void AddRaw(string json)
		{
			_files[Path.Combine("root", "browsers.json")] = System.Text.Encoding.UTF8.GetBytes(json);
		}

		public byte[] GetBrowsersBytes()
		{
			return [.. _files[Path.Combine("root", "browsers.json")]];
		}

		public void ResetAccessCount()
		{
			AccessCount = 0;
		}

		public bool FileExists(string path)
		{
			AccessCount++;
			return _files.ContainsKey(path);
		}

		public bool DirectoryExists(string path)
		{
			AccessCount++;
			return false;
		}

		public Stream OpenRead(string path)
		{
			AccessCount++;
			OpenReadCount++;
			OnOpenRead?.Invoke();
			if (OpenFailure is not null || OpenFailureNumber == OpenReadCount)
			{
				throw OpenFailure ?? new IOException("classified race");
			}

			byte[] bytes = OpenReadCount == 2 && SecondOpenBytes is not null
				? SecondOpenBytes
				: _files[path];
			return new MemoryStream(bytes, writable: false);
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			AccessCount++;
			return [];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			AccessCount++;
			return DateTime.UnixEpoch;
		}

		public long GetLength(string path)
		{
			AccessCount++;
			return _files[path].Length;
		}
	}
}
