using BroChoos.Core.Routing;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Browsers;

namespace BroChoos.Platform.Windows.Storage;

// W7.1: the rules-document store, built on the EXISTING W3 generation/CAS machinery and
// nothing else. Read is ConfigFileObserver (the same strict observer every other config
// document uses); write is GenerationMutationCoordinator (mutex + observe + CAS check +
// monotonic generation increment + AtomicJsonFileStore, which is where the atomic replace
// and per-generation backup live).
//
// WHY NOT A NEW STORAGE MECHANISM. The plan requires the rules document to persist "through
// the W3 protocol". Reinventing a writer here would duplicate the CAS, backup and
// retention semantics three other documents already depend on and would be the first
// place those semantics diverged. This type adds only the document-specific parts: the
// file name, the fail-open default, and the projection to the evaluator's RuleSetDocument.
//
// THE SHAPE MIRRORS ActivationHistoryStore (the other single-document store added on top
// of the coordinator), which is why it is registered the same deferred way and takes the
// same three collaborators. `Create` resolves the storage root, and THAT IS THE
// PROVISIONING MOMENT — see IRulesStoreFactory's comment.
public sealed class RulesStoreFactory(
	IEnvironmentProbe environment,
	IStorageFileSystem storageFileSystem,
	IStorageReadFileSystem storageReadFileSystem) : IRulesStoreFactory
{
	private readonly IEnvironmentProbe _environment = environment;
	private readonly IStorageFileSystem _storageFileSystem = storageFileSystem;
	private readonly IStorageReadFileSystem _storageReadFileSystem = storageReadFileSystem;

	public IRulesStore Create(string? portableDataPath)
	{
		StorageRootResolver roots = new(_environment, _storageFileSystem, portableDataPath);
		ConfigFileObserver observer = new(_storageReadFileSystem);
		GenerationMutationCoordinator coordinator = new(_storageReadFileSystem);
		return new RulesStore(roots, observer, coordinator);
	}
}

public sealed class RulesStore(
	IStorageRootResolver storageRoot,
	ConfigFileObserver observer,
	GenerationMutationCoordinator coordinator) : IRulesStore
{
	// The same 5 s commit budget ActivationHistoryStore uses. Every wait inside Commit is
	// bounded by it rather than by the 10 s AtomicJsonFileStore default, which is several
	// times the whole picker-open budget.
	private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(5);

	private readonly IStorageRootResolver _storageRoot = storageRoot;
	private readonly ConfigFileObserver _observer = observer;
	private readonly GenerationMutationCoordinator _coordinator = coordinator;

	public RuleSetDocument LoadOrDefault()
	{
		return LoadWithOutcome().Value;
	}

	public ConfigReadResult<RuleSetDocument> LoadWithOutcome()
	{
		(ConfigReadOutcome outcome, RulesDocument? document) =
			_observer.ObserveReadOutcome<RulesDocument>(DocumentPath());

		// FAIL OPEN, AND THE WHOLE DOCUMENT OR NOTHING. A partially-bound document is never
		// returned: the observer already refuses one (RespectRequiredConstructorParameters
		// makes every positional parameter mandatory), and the extra fields here are the
		// ordinary config-file states — absent, locked, corrupt, or a newer schema this
		// build may not write. In all of them the honest answer is "no rules", never an
		// exception on the picker's interactive path (D-v3.5-083).
		//
		// The outcome names which of those states it was; the returned value is unchanged.
		return new(document?.ToRuleSet() ?? DefaultRuleSet(), outcome);
	}

	public MutationCommitResult Save(RulesDocument document, ExpectedDocumentGeneration expectedGeneration)
	{
		ArgumentNullException.ThrowIfNull(document);

		// A save with no listener is still a save, so the callback is intentionally empty:
		// the coordinator reports the committed generation in its result, and this store
		// chooses no cache to publish into (RuleCache is Core's and the picker owns it).
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
		return Path.Combine(_storageRoot.Root, "rules.json");
	}

	private static RuleSetDocument DefaultRuleSet()
	{
		return new RuleSetDocument(0, []);
	}
}
