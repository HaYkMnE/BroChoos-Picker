using BroChoos.Core.Privacy;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Browsers;

namespace BroChoos.Platform.Windows.Storage;

public sealed class ActivationHistoryStoreFactory(
	IEnvironmentProbe environment,
	IStorageFileSystem storageFileSystem,
	IStorageReadFileSystem storageReadFileSystem) : IActivationHistoryStoreFactory
{
	private readonly IEnvironmentProbe _environment = environment;
	private readonly IStorageFileSystem _storageFileSystem = storageFileSystem;
	private readonly IStorageReadFileSystem _storageReadFileSystem = storageReadFileSystem;

	public IActivationHistoryStore Create(string? portableDataPath)
	{
		StorageRootResolver roots = new(_environment, _storageFileSystem, portableDataPath);
		ConfigFileObserver observer = new(_storageReadFileSystem);
		GenerationMutationCoordinator coordinator = new(_storageReadFileSystem);
		return new ActivationHistoryStore(roots, observer, coordinator);
	}
}

public sealed class ActivationHistoryStore(
	IStorageRootResolver storageRoot,
	ConfigFileObserver observer,
	GenerationMutationCoordinator coordinator) : IActivationHistoryStore
{
	private readonly IStorageRootResolver _storageRoot = storageRoot;
	private readonly ConfigFileObserver _observer = observer;
	private readonly GenerationMutationCoordinator _coordinator = coordinator;
	private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(5);

	public ActivationHistoryDocument LoadOrDefault()
	{
		return LoadWithOutcome().Value;
	}

	// Same decision as before (the whole document or the default); the outcome only names
	// which branch was taken, so the picker can tell a broken file from a first run.
	public ConfigReadResult<ActivationHistoryDocument> LoadWithOutcome()
	{
		(ConfigReadOutcome outcome, ActivationHistoryDocument? document) =
			_observer.ObserveReadOutcome<ActivationHistoryDocument>(DocumentPath());
		return new(document ?? DefaultDocument(), outcome);
	}

	public ActivationHistoryMutationResult Record(ActivationHistoryRecordRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		string path = DocumentPath();

		if (!request.Settings.Enabled)
		{
			return CommitSettingsOnly(path, request.ExpectedGeneration, new ActivationHistorySettingsContract(Enabled: false, SkipNextLink: false));
		}

		if (request.Settings.SkipNextLink)
		{
			return CommitSettingsOnly(path, request.ExpectedGeneration, new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: true));
		}

		if (request.CandidateRecord is null)
		{
			ActivationHistoryDocument currentState = LoadForCommit(path, out _);
			ActivationHistorySettingsContract settings = currentState.Settings.SkipNextLink
				? new ActivationHistorySettingsContract(currentState.Settings.Enabled, SkipNextLink: false)
				: currentState.Settings;

			return CommitSettingsOnly(path, request.ExpectedGeneration, settings);
		}

		long? committedGeneration = null;
		ActivationHistoryDocument? committedDocument = null;

		ActivationHistoryDocument current = LoadForCommit(path, out _);
		List<ActivationHistoryRecord> entries = [.. current.Entries, request.CandidateRecord];
		ActivationHistoryDocument next = new(
			new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: false),
			entries);

		MutationCommitResult commit = _coordinator.Commit(
			path,
			next,
			request.ExpectedGeneration,
			CommitTimeout,
			Path.Combine(_storageRoot.Root, "backups"),
			(committed, doc) =>
			{
				committedGeneration = committed;
				committedDocument = doc;
			});

		return commit.Status == MutationCommitStatus.Committed
			? new ActivationHistoryMutationResult(true, commit, committedGeneration, committedDocument)
			: new ActivationHistoryMutationResult(false, commit, null, null);
	}

	public ActivationHistoryMutationResult ClearConfirmed(ExpectedDocumentGeneration expectedGeneration)
	{
		string path = DocumentPath();
		ActivationHistoryDocument current = LoadForCommit(path, out _);

		ActivationHistoryDocument next = new(
			new ActivationHistorySettingsContract(current.Settings.Enabled, SkipNextLink: false),
			[]);

		long? committedGeneration = null;
		ActivationHistoryDocument? committedDocument = null;

		MutationCommitResult commit = _coordinator.Commit(
			path,
			next,
			expectedGeneration,
			CommitTimeout,
			Path.Combine(_storageRoot.Root, "backups"),
			(committed, doc) =>
			{
				committedGeneration = committed;
				committedDocument = doc;
			});

		return commit.Status == MutationCommitStatus.Committed
			? new ActivationHistoryMutationResult(true, commit, committedGeneration, committedDocument)
			: new ActivationHistoryMutationResult(false, commit, null, null);
	}

	private ActivationHistoryMutationResult CommitSettingsOnly(
		string path,
		ExpectedDocumentGeneration expected,
		ActivationHistorySettingsContract settings)
	{
		ActivationHistoryDocument current = LoadForCommit(path, out _);
		ActivationHistoryDocument next = current with { Settings = settings };

		long? committedGeneration = null;
		ActivationHistoryDocument? committedDocument = null;

		MutationCommitResult commit = _coordinator.Commit(
			path,
			next,
			expected,
			CommitTimeout,
			Path.Combine(_storageRoot.Root, "backups"),
			(committed, doc) =>
			{
				committedGeneration = committed;
				committedDocument = doc;
			});

		return commit.Status == MutationCommitStatus.Committed
			? new ActivationHistoryMutationResult(true, commit, committedGeneration, committedDocument)
			: new ActivationHistoryMutationResult(false, commit, null, null);
	}

	private ActivationHistoryDocument LoadForCommit(string path, out long observedGeneration)
	{
		(ConfigFileObservation observation, ActivationHistoryDocument? document) = _observer.ObserveDocument<ActivationHistoryDocument>(path);
		if (observation.Access == ConfigFileAccess.Opened
			&& observation.Envelope == EnvelopeParse.Parsed
			&& observation.Body == DocumentBodyParse.Parsed
			&& observation.Schema == SchemaCompatibility.CompletelyCompatible
			&& document is not null)
		{
			observedGeneration = document.Generation;
			return document;
		}

		observedGeneration = 0;
		return DefaultDocument();
	}

	private string DocumentPath()
	{
		return Path.Combine(_storageRoot.Root, "history.json");
	}

	private static ActivationHistoryDocument DefaultDocument()
	{
		return new ActivationHistoryDocument(new ActivationHistorySettingsContract(), []);
	}
}
