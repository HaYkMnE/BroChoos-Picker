using BroChoos.Core.Storage;

namespace BroChoos.Platform.Windows.Storage;

// The WRITE seam the repair executor persists a document through, and the ONE place
// the D-v3.5-493 deadline reaches the write side.
//
// WHY THE TIMEOUT IS A PARAMETER HERE AND NOT A CONSTRUCTOR ARGUMENT. It has to be
// derived PER ATTEMPT from the budget that is left, so it cannot be fixed when the
// writer is composed. AtomicJsonFileStore already accepts it as a constructor
// argument, so the adapter below builds one store per write; a store holds no state
// across writes (its backups directory and timeout are its only fields), so this
// costs an allocation and buys a bound that is otherwise enforced nowhere.
public interface IConfigDocumentWriter
{
	// Publishes one document. writeLockTimeout bounds the per-file write-lock wait,
	// which is the dominant blocking cost on this path — MEASURED, and it is the
	// reason this parameter exists at all: against a held lock the elapsed time
	// tracks this value (a 10 s wait cost ~10.2 s, a 400 ms wait ~415 ms, a 120 ms
	// wait ~126 ms), so the default 10 s alone is several times the whole repair
	// budget. Reproduce with a peer thread holding the mutex whose name
	// AtomicJsonFileStore.BuildMutexName derives from the destination path; a wait
	// that does NOT block means the name was reproduced wrongly.
	//
	// A timed-out wait publishes NOTHING (measured: the destination did not exist
	// afterwards), which is what makes shortening it safe rather than merely faster.
	void Write(string destinationPath, ConfigDocument document, TimeSpan writeLockTimeout);
}

// Browser-catalog writes are commands layered over the read planner rather than repair
// instructions. The two legal routes require different concrete evidence tokens, so this
// seam deliberately exposes no unguarded Write method.
public interface IBrowserCatalogDocumentWriter
{
	void CreateAbsent(
		string destinationPath,
		BrowsersDocument document,
		ExpectedDocumentGeneration expectedGeneration,
		PersistClearance clearance,
		TimeSpan writeLockTimeout,
		Action<long, BrowsersDocument> onCommitted);

	void ReconcileCompatible(
		string destinationPath,
		BrowsersDocument document,
		ExpectedDocumentGeneration expectedGeneration,
		ReconcileClearance clearance,
		TimeSpan writeLockTimeout,
		Action<long, BrowsersDocument> onCommitted);
}

// The production writer: AtomicJsonFileStore, constructed per write with the derived
// timeout. Deliberately a thin adapter — every atomicity, backup, retention and
// read-back property belongs to the store, which this slice does not modify.
public sealed class AtomicConfigDocumentWriter(string? backupsDirectory) : IConfigDocumentWriter
{
	private readonly string? _backupsDirectory = backupsDirectory;

	public void Write(string destinationPath, ConfigDocument document, TimeSpan writeLockTimeout)
	{
		new AtomicJsonFileStore(_backupsDirectory, writeLockTimeout).Write(destinationPath, document);
	}
}

// The production browser-catalog adapter has no raw write route. Both commands delegate
// to the same atomic store only after their required Core evidence has been supplied.
public sealed class AtomicBrowserCatalogDocumentWriter(string? backupsDirectory) : IBrowserCatalogDocumentWriter
{
	private readonly string? _backupsDirectory = backupsDirectory;
	private readonly GenerationMutationCoordinator _coordinator = new(new SystemStorageReadFileSystem());

	public void CreateAbsent(
		string destinationPath,
		BrowsersDocument document,
		ExpectedDocumentGeneration expectedGeneration,
		PersistClearance clearance,
		TimeSpan writeLockTimeout,
		Action<long, BrowsersDocument> onCommitted)
	{
		ArgumentNullException.ThrowIfNull(clearance);
		Write(destinationPath, document, expectedGeneration, writeLockTimeout, onCommitted);
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
		Write(destinationPath, document, expectedGeneration, writeLockTimeout, onCommitted);
	}

	private void Write(
		string destinationPath,
		BrowsersDocument document,
		ExpectedDocumentGeneration expectedGeneration,
		TimeSpan writeLockTimeout,
		Action<long, BrowsersDocument> onCommitted)
	{
		MutationCommitResult result = _coordinator.Commit(
			destinationPath,
			document,
			expectedGeneration,
			writeLockTimeout,
			_backupsDirectory,
			onCommitted);

		if (result.Status == MutationCommitStatus.Conflict)
		{
			throw new IOException("Write conflict: expected generation no longer matches the current destination generation.");
		}

		if (result.Status == MutationCommitStatus.NewerSchemaRefused)
		{
			throw new IOException("Write refused: destination is a newer schema and must remain read-only.");
		}
	}
}
