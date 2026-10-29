using BroChoos.Core.Abstractions;
using BroChoos.Core.Storage;

namespace BroChoos.Platform.Windows.Storage;

public enum BrowserCatalogPersistenceStatus
{
	Persisted,
	RecoverableFailure,
	ReadOnly
}

public enum BrowserCatalogPersistenceReason
{
	Persisted = 1,
	ScanEmpty,
	NoRecognizedBrowsers,
	DestinationOpenFailed,
	DestinationRequiresRepair,
	DestinationNewerRevisionReadOnly,
	DestinationNewerSchemaReadOnly,
	DestinationEnvelopeInvalidReadOnly,
	DestinationOlderSchemaMigrationUnavailableReadOnly,
	WriterFailed,
	DestinationClearanceRefusedReadOnly
}

public sealed record BrowserCatalogPersistenceResult(
	BrowserCatalogPersistenceStatus Status,
	BrowserCatalogPersistenceReason Reason,
	bool? DestinationModified = null,
	bool? Restored = null,
	long? CommittedGeneration = null,
	IReadOnlyList<BrowserDefinition>? PersistedBrowsers = null);

public interface IConfigDocumentWriterFactory
{
	IBrowserCatalogDocumentWriter Create(string backupsDirectory);
}

internal interface IBrowserCatalogClearanceEvaluator
{
	PersistClearance? TryClearCreate(ConfigFileObservation observation);

	ReconcileClearance? TryClearReconcile(
		ConfigFileObservation observation,
		ConfigLoadDecision decision);
}

internal sealed class BrowserCatalogClearanceEvaluator : IBrowserCatalogClearanceEvaluator
{
	public PersistClearance? TryClearCreate(ConfigFileObservation observation)
	{
		return PersistClearance.TryClear(observation);
	}

	public ReconcileClearance? TryClearReconcile(
		ConfigFileObservation observation,
		ConfigLoadDecision decision)
	{
		return ReconcileClearance.TryClear(observation, decision);
	}
}

public sealed class AtomicConfigDocumentWriterFactory : IConfigDocumentWriterFactory
{
	public IBrowserCatalogDocumentWriter Create(string backupsDirectory)
	{
		return new AtomicBrowserCatalogDocumentWriter(backupsDirectory);
	}
}

public sealed class BrowserCatalogPersistenceService
{
	// Lock-wait budget accounting uses RepairAttemptBudget's shared default. A monotonic
	// timestamp starts before pre-write work, so elapsed observation, planning, and
	// reconciliation are subtracted from the lock-wait allowance. Persist makes at most
	// one write attempt, with no retry; caught write failures return RecoverableFailure.
	// The minimum lock-wait floor and its rationale are documented separately below.
	private static readonly TimeSpan PersistTimeBudget = RepairAttemptBudget.DefaultTimeBudget;

	// Same floor and value as ConfigRepairExecutor.MinimumWriteLockTimeout. An exhausted
	// budget must still attempt the write with a bounded wait. Mutex.WaitOne(TimeSpan)
	// converts TotalMilliseconds to a long by truncating toward zero, then rejects converted
	// values below -1 or above Int32.MaxValue. The lower-bound bands relevant here are:
	//
	//   * (-1, 1) ms converts to 0 by truncation toward zero, including positive
	//     sub-millisecond values, and produces a non-blocking poll.
	//   * (-2, -1] ms converts to -1, an infinite timeout. The wait ends when the mutex
	//     becomes available; abandonment ends it by throwing AbandonedMutexException.
	//   * Values at or below -2 ms convert below -1 and throw before waiting.
	//
	// Converted values above Int32.MaxValue also throw. The 50 ms floor makes each
	// zero-converted wait, infinite wait, and lower-bound throw unreachable. Positive values
	// below 50 ms are floored; values at least 50 ms remain as is.
	private static readonly TimeSpan MinimumWriteLockTimeout = TimeSpan.FromMilliseconds(50);

	private readonly IStorageRootResolver _storageRoot;
	private readonly ConfigFileObserver _observer;

	private readonly IConfigDocumentWriterFactory _writerFactory;
	private readonly IBrowserCatalogClearanceEvaluator _clearanceEvaluator;

	private readonly TimeProvider _timeProvider;

	public BrowserCatalogPersistenceService(
		IStorageRootResolver storageRoot,
		ConfigFileObserver observer,
		IConfigDocumentWriterFactory writerFactory,
		TimeProvider timeProvider)
		: this(
			storageRoot,
			observer,
			writerFactory,
			timeProvider,
			new BrowserCatalogClearanceEvaluator())
	{
	}

	internal BrowserCatalogPersistenceService(
		IStorageRootResolver storageRoot,
		ConfigFileObserver observer,
		IConfigDocumentWriterFactory writerFactory,
		TimeProvider timeProvider,
		IBrowserCatalogClearanceEvaluator clearanceEvaluator)
	{
		ArgumentNullException.ThrowIfNull(storageRoot);
		ArgumentNullException.ThrowIfNull(observer);
		ArgumentNullException.ThrowIfNull(writerFactory);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(clearanceEvaluator);

		_storageRoot = storageRoot;
		_observer = observer;
		_writerFactory = writerFactory;
		_timeProvider = timeProvider;
		_clearanceEvaluator = clearanceEvaluator;
	}

	public BrowserCatalogPersistenceResult Persist(IReadOnlyList<BrowserCandidate> candidates)
	{
		ArgumentNullException.ThrowIfNull(candidates);

		// Empty input, and all-unknown input below, never reaches the destination. Persisted
		// would claim a write and ReadOnly would blame an uninspected destination, leaving
		// RecoverableFailure as the only current status. That collapse prevents callers from
		// distinguishing no usable input from a failed write; this is a known limitation.
		if (candidates.Count == 0)
		{
			return new BrowserCatalogPersistenceResult(
				BrowserCatalogPersistenceStatus.RecoverableFailure,
				BrowserCatalogPersistenceReason.ScanEmpty);
		}

		List<(BrowserCandidate Candidate, string CanonicalId, string Vendor)> knownCandidates = [];
		HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
		foreach (BrowserCandidate candidate in candidates)
		{
			// BrowserCandidate has no vendor, and this service has neither a logger nor a
			// per-candidate result channel. Passing an unknown id through would require inventing
			// a vendor and could replace an existing same-id row during reconciliation; rejecting
			// the whole call would also discard recognized candidates. Dropping only the unknown
			// candidate preserves the recognized ones.
			//
			// The canonical literals provide stable persisted keys independent of caller spelling
			// or case.
			(string CanonicalId, string Vendor)? identity = Recognize(candidate);
			// Because this public method may receive repeated canonical ids from any caller,
			// possibly with distinct paths, first-occurrence-wins preserves caller order without
			// inventing a last-wins ranking or field-merge policy. Each persisted row remains
			// sourced from one candidate, keeping its source and path provenance coherent.
			if (identity is { } known && seenIds.Add(known.CanonicalId))
			{
				knownCandidates.Add((candidate, known.CanonicalId, known.Vendor));
			}
		}
		// All-unknown input stops before destination observation rather than rewriting an
		// existing catalog with no newly recognized information.
		if (knownCandidates.Count == 0)
		{
			return new BrowserCatalogPersistenceResult(
				BrowserCatalogPersistenceStatus.RecoverableFailure,
				BrowserCatalogPersistenceReason.NoRecognizedBrowsers);
		}

		// The budget opens only once usable input exists, so the no-write early exits acquire
		// neither wall-clock nor monotonic time. From here, everything spent before the write —
		// the observation's read, the plan, and reconciliation — is charged against it.
		long startedAtTimestamp = _timeProvider.GetTimestamp();

		DateTimeOffset scanInstant = _timeProvider.GetUtcNow();
		List<BrowserDefinition> browsers = [.. knownCandidates
			.Select(known => new BrowserDefinition(
				known.CanonicalId,
				known.Candidate.DisplayName,
				known.Vendor,
				known.Candidate.Channel,
				known.Candidate.ExePath,
				known.Candidate.DetectionSource,
				scanInstant,
				known.Candidate.ProfileIds,
				known.Candidate.HasUnresolvedProfiles,
				known.Candidate.IsCustomBrowser,
				known.Candidate.IsPortableBrowser,
				known.Candidate.ProfileNames))];
		string destinationPath = Path.Combine(_storageRoot.Root, "browsers.json");
		(ConfigFileObservation observation, BrowsersDocument? existing) =
			_observer.ObserveDocument<BrowsersDocument>(destinationPath);
		ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);
		bool absent = observation.Access == ConfigFileAccess.Absent
			&& decision.Action == ConfigLoadAction.CreateDefaultsAndPersist;
		bool currentLoaded = decision.Action == ConfigLoadAction.UseLoadedDocument
			&& observation.Schema == SchemaCompatibility.CompletelyCompatible;
		if (!absent && !currentLoaded)
		{
			BrowserCatalogPersistenceStatus status = decision.Action == ConfigLoadAction.HardStopReadOnly
				|| decision.Action == ConfigLoadAction.QuarantineFailedReadOnly
				|| (decision.Action == ConfigLoadAction.UseLoadedDocument
					&& observation.Schema == SchemaCompatibility.NewerRevisionReadKnownOnly)
				? BrowserCatalogPersistenceStatus.ReadOnly
				: BrowserCatalogPersistenceStatus.RecoverableFailure;
			BrowserCatalogPersistenceReason reason = GetNonWritableReason(decision.Reason);
			return new BrowserCatalogPersistenceResult(status, reason);
		}

		if (currentLoaded)
		{
			HashSet<string> detectedIds = browsers.Select(browser => browser.BrowserId).ToHashSet(StringComparer.OrdinalIgnoreCase);
			browsers.AddRange(existing!.Browsers.Where(browser => !detectedIds.Contains(browser.BrowserId)));
		}

		ExpectedDocumentGeneration expectedGeneration = absent
			? ExpectedDocumentGeneration.Absent
			: ExpectedDocumentGeneration.Match(existing!.Generation);
		BrowsersDocument document = new(browsers);
		TimeSpan writeLockTimeout = RemainingWriteLockTimeout(startedAtTimestamp);
		PersistClearance? createClearance = absent ? _clearanceEvaluator.TryClearCreate(observation) : null;
		ReconcileClearance? reconcileClearance = currentLoaded
			? _clearanceEvaluator.TryClearReconcile(observation, decision)
			: null;
		if ((absent && createClearance is null) || (currentLoaded && reconcileClearance is null))
		{
			return new BrowserCatalogPersistenceResult(
				BrowserCatalogPersistenceStatus.ReadOnly,
				BrowserCatalogPersistenceReason.DestinationClearanceRefusedReadOnly);
		}

		try
		{
			IBrowserCatalogDocumentWriter writer = _writerFactory.Create(_storageRoot.Backups);
			long? committedGeneration = null;
			IReadOnlyList<BrowserDefinition>? committedBrowsers = null;
			if (absent)
			{
				writer.CreateAbsent(destinationPath, document, expectedGeneration, createClearance!, writeLockTimeout, (committed, committedDocument) =>
				{
					committedGeneration = committed;
					committedBrowsers = committedDocument.Browsers;
				});
			}
			else
			{
				writer.ReconcileCompatible(destinationPath, document, expectedGeneration, reconcileClearance!, writeLockTimeout, (committed, committedDocument) =>
				{
					committedGeneration = committed;
					committedBrowsers = committedDocument.Browsers;
				});
			}

			return committedGeneration is null || committedBrowsers is null
				? new BrowserCatalogPersistenceResult(
					BrowserCatalogPersistenceStatus.RecoverableFailure,
					BrowserCatalogPersistenceReason.WriterFailed)
				: new BrowserCatalogPersistenceResult(
				BrowserCatalogPersistenceStatus.Persisted,
				BrowserCatalogPersistenceReason.Persisted,
				CommittedGeneration: committedGeneration,
				PersistedBrowsers: committedBrowsers);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			return new BrowserCatalogPersistenceResult(
				BrowserCatalogPersistenceStatus.RecoverableFailure,
				BrowserCatalogPersistenceReason.WriterFailed,
				ReadBooleanClassification(failure, "destinationModified"),
				ReadBooleanClassification(failure, "restored"));
		}

		throw new InvalidOperationException("Unreachable: persist success path must return inside the write try/catch.");
	}

	// The ids the product actually admits, and nothing else:
	//
	//   * the detector's fixed channel ids (WindowsBrowserDetector.*Id), vendor by family;
	//   * ManualBrowserAdmitter's "custom-<basis>" / "portable-<basis>" ids. These are open
	//     families, so the prefix alone is NOT accepted: the candidate must also carry the
	//     admitter's provenance (detection source "manual" with only IsCustomBrowser, or
	//     "portable" with only IsPortableBrowser). A registry/known-path candidate that
	//     merely spells a custom- id is still unknown and still dropped.
	//
	// Vendor for a manual entry is "Custom"/"Portable" rather than a guessed browser
	// vendor: the user picked an arbitrary executable, and the admitter does not establish
	// who made it. Vendor is display/provenance metadata only — the snapshot handed to the
	// picker (PostConsentDiscoveryPublisher.ToSnapshotCandidates) does not carry it, and
	// launch, ranking and rules key on BrowserId.
	private static (string CanonicalId, string Vendor)? Recognize(BrowserCandidate candidate)
	{
		string id = candidate.BrowserId.ToLowerInvariant();
		(string CanonicalId, string Vendor)? known = id switch
		{
			"edge-stable" => ("edge-stable", "Microsoft"),
			"edge-beta" => ("edge-beta", "Microsoft"),
			"edge-dev" => ("edge-dev", "Microsoft"),
			"edge-canary" => ("edge-canary", "Microsoft"),
			"chrome-stable" => ("chrome-stable", "Google"),
			"chrome-beta" => ("chrome-beta", "Google"),
			"chrome-dev" => ("chrome-dev", "Google"),
			"chrome-canary" => ("chrome-canary", "Google"),
			"brave-stable" => ("brave-stable", "Brave"),
			"brave-beta" => ("brave-beta", "Brave"),
			"brave-nightly" => ("brave-nightly", "Brave"),
			"firefox-stable" => ("firefox-stable", "Mozilla"),
			"firefox-developer" => ("firefox-developer", "Mozilla"),
			"firefox-nightly" => ("firefox-nightly", "Mozilla"),
			_ => null,
		};
		if (known is not null)
		{
			return known;
		}

		if (id.Length > CustomIdPrefix.Length
			&& id.StartsWith(CustomIdPrefix, StringComparison.Ordinal)
			&& candidate.DetectionSource.Equals("manual", StringComparison.OrdinalIgnoreCase)
			&& candidate.IsCustomBrowser
			&& !candidate.IsPortableBrowser)
		{
			return (id, "Custom");
		}

		return id.Length > PortableIdPrefix.Length
			&& id.StartsWith(PortableIdPrefix, StringComparison.Ordinal)
			&& candidate.DetectionSource.Equals("portable", StringComparison.OrdinalIgnoreCase)
			&& candidate.IsPortableBrowser
			&& !candidate.IsCustomBrowser
			? (id, "Portable")
			: null;
	}

	private const string CustomIdPrefix = "custom-";
	private const string PortableIdPrefix = "portable-";

	// What is LEFT of this Persist's budget, floored. Derived at the moment of the write
	// rather than fixed anywhere, which is what IConfigDocumentWriter's contract demands
	// and the same shape ConfigRepairExecutor.RemainingWriteLockTimeout uses.
	private TimeSpan RemainingWriteLockTimeout(long startedAtTimestamp)
	{
		TimeSpan remaining = PersistTimeBudget - _timeProvider.GetElapsedTime(startedAtTimestamp);
		return remaining < MinimumWriteLockTimeout ? MinimumWriteLockTimeout : remaining;
	}

	internal static BrowserCatalogPersistenceReason GetNonWritableReason(ConfigLoadReason reason)
	{
		return reason switch
		{
			ConfigLoadReason.OpenFailedDefaultsInMemoryOnly =>
				BrowserCatalogPersistenceReason.DestinationOpenFailed,
			ConfigLoadReason.DocumentLoadedNewerRevisionKnownFieldsOnly =>
				BrowserCatalogPersistenceReason.DestinationNewerRevisionReadOnly,
			ConfigLoadReason.MainFileNewerSchemaReadOnly =>
				BrowserCatalogPersistenceReason.DestinationNewerSchemaReadOnly,
			ConfigLoadReason.MainFileEnvelopeInvalidReadOnly =>
				BrowserCatalogPersistenceReason.DestinationEnvelopeInvalidReadOnly,
			ConfigLoadReason.MainFileOlderSchemaMigrationUnavailableReadOnly =>
				BrowserCatalogPersistenceReason.DestinationOlderSchemaMigrationUnavailableReadOnly,
			ConfigLoadReason.BodyUnparseableQuarantineThenRestore or
			ConfigLoadReason.BodyUnparseableQuarantineThenDefaults or
			ConfigLoadReason.EnvelopeUnparseableQuarantineThenRestore or
			ConfigLoadReason.EnvelopeUnparseableQuarantineThenDefaults or
			ConfigLoadReason.OriginalAlreadyPreservedRestoreFromBackup or
			ConfigLoadReason.OriginalAlreadyPreservedCreateDefaults =>
				BrowserCatalogPersistenceReason.DestinationRequiresRepair,
			ConfigLoadReason.ClearanceRefusedReadOnly =>
				BrowserCatalogPersistenceReason.DestinationClearanceRefusedReadOnly,
			ConfigLoadReason.FileAbsentDefaultsCreated or
			ConfigLoadReason.DocumentLoadedSchemaCompatible or
			ConfigLoadReason.BackupNewerSchemaNotPromotable or
			ConfigLoadReason.QuarantineFailedReadOnlyNoRetry =>
				throw new InvalidOperationException($"Unsupported non-writable config load reason: {reason}"),
			_ => throw new InvalidOperationException($"Unsupported non-writable config load reason: {reason}"),
		};
	}

	private static bool? ReadBooleanClassification(Exception failure, string key)
	{
		return failure.Data[key] is bool value ? value : null;
	}
}
