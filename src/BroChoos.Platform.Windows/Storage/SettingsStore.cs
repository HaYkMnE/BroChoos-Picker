using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Browsers;

namespace BroChoos.Platform.Windows.Storage;

// W8.1 — the settings-document store, built on the EXISTING W3 generation/CAS machinery
// and nothing else. Read is ConfigFileObserver (the same strict observer every other
// config document uses); write is GenerationMutationCoordinator (mutex + observe + CAS
// check + monotonic generation increment + AtomicJsonFileStore, which is where the atomic
// replace and per-generation backup live).
//
// WHY NOT A NEW STORAGE MECHANISM. The spec requires settings auto-save to use the shared
// mutation protocol (spec 2.5, D-v3.5-036/063/065/071/076/078). Reinventing a writer here
// would duplicate the CAS, backup and retention semantics three other documents already
// depend on. This type adds only the document-specific parts: the file name and the
// fail-open default.
//
// THE SHAPE MIRRORS RulesStore AND ActivationHistoryStore. `Create` resolves the storage
// root, and THAT IS THE PROVISIONING MOMENT — see ISettingsStoreFactory's comment.
public sealed class SettingsStoreFactory(
	IEnvironmentProbe environment,
	IStorageFileSystem storageFileSystem,
	IStorageReadFileSystem storageReadFileSystem) : ISettingsStoreFactory
{
	private readonly IEnvironmentProbe _environment = environment;
	private readonly IStorageFileSystem _storageFileSystem = storageFileSystem;
	private readonly IStorageReadFileSystem _storageReadFileSystem = storageReadFileSystem;

	public ISettingsStore Create(string? portableDataPath)
	{
		StorageRootResolver roots = new(_environment, _storageFileSystem, portableDataPath);
		ConfigFileObserver observer = new(_storageReadFileSystem);
		GenerationMutationCoordinator coordinator = new(_storageReadFileSystem);
		return new SettingsStore(roots, observer, coordinator);
	}
}

public sealed class SettingsStore(
	IStorageRootResolver storageRoot,
	ConfigFileObserver observer,
	GenerationMutationCoordinator coordinator) : ISettingsStore
{
	// The same 5 s commit budget the other stores use. Every wait inside Commit is bounded
	// by it rather than by the 10 s AtomicJsonFileStore default.
	private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(5);

	private readonly IStorageRootResolver _storageRoot = storageRoot;
	private readonly ConfigFileObserver _observer = observer;
	private readonly GenerationMutationCoordinator _coordinator = coordinator;

	public SettingsDocument LoadOrDefault()
	{
		(ConfigFileObservation observation, SettingsDocument? document) =
			_observer.ObserveDocument<SettingsDocument>(DocumentPath());

		// FAIL OPEN, AND THE WHOLE DOCUMENT OR NOTHING — the ordinary config-file states
		// (absent, locked, corrupt, newer schema) all yield the defaults. Settings are
		// preferences, so a broken file must never block the settings window (D-v3.5-083).
		return observation.Access == ConfigFileAccess.Opened
			&& observation.Envelope == EnvelopeParse.Parsed
			&& observation.Body == DocumentBodyParse.Parsed
			&& observation.Schema == SchemaCompatibility.CompletelyCompatible
			&& document is not null
			? document
			: new SettingsDocument();
	}

	// The CAS token for the observed state. This mirrors the coordinator's own reading of
	// the file (ConfigFileObserver over the same path) so the token describes what a commit
	// will actually compare against - not a guess.
	//
	// THE ABSENT CASE IS THE POINT. A missing settings.json must be expected as Absent, NOT
	// Match(0): the coordinator's MatchesExpected is `ExpectsDocument && Access==Absent =>
	// false`, so Match(0) would refuse the first save on a clean install forever.
	//
	// THE PRESENT-BUT-UNREADABLE CASE MUST NOT BE MATCHABLE (F-A). A file that exists but is
	// corrupt (unparseable body/envelope) or could not be opened has a generation we cannot
	// know; we must not return a token that a commit would accept. Returning Match(0) here
	// was the defect: the coordinator treated the unparseable body's generation as 0 and
	// therefore MATCHED it, so a plain save replaced the corrupt bytes. We now return the
	// token for the generation we could actually read (0 when unreadable) ONLY when the
	// document is genuinely parsed; for every other present state the coordinator's own
	// shape-based refusal (MatchesExpected returns false for OpenFailed/Unparseable) refuses
	// the write, which is the correct fail-closed direction (repair/quarantine overwrites
	// through its own writer, not through this CAS path).
	public ExpectedDocumentGeneration ObserveExpectedGeneration()
	{
		(ConfigFileObservation observation, SettingsDocument? document) =
			_observer.ObserveDocument<SettingsDocument>(DocumentPath());

		if (observation.Access == ConfigFileAccess.Absent)
		{
			return ExpectedDocumentGeneration.Absent;
		}

		// For a present file we report the generation only when the whole document parsed.
		// Otherwise we still return a Match token (there is no third vocabulary item), but
		// MatchesExpected refuses it on shape, so the value here is never trusted for an
		// unreadable/corrupt destination.
		bool wholeDocumentParsed = observation.Access == ConfigFileAccess.Opened
			&& observation.Envelope == EnvelopeParse.Parsed
			&& observation.Body == DocumentBodyParse.Parsed;

		return ExpectedDocumentGeneration.Match(wholeDocumentParsed ? document?.Generation ?? 0 : 0);
	}

	public MutationCommitResult Save(SettingsDocument document, ExpectedDocumentGeneration expectedGeneration)
	{
		ArgumentNullException.ThrowIfNull(document);

		// A save with no listener is still a save: the coordinator reports the committed
		// generation in its result, and this store chooses no cache to publish into.
		return _coordinator.Commit(
			DocumentPath(),
			document,
			expectedGeneration,
			CommitTimeout,
			Path.Combine(_storageRoot.Root, "backups"),
			static (_, _) => { });
	}

	private string DocumentPath()
	{
		return Path.Combine(_storageRoot.Root, "settings.json");
	}
}
