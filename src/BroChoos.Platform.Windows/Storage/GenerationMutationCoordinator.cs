using BroChoos.Core.Storage;

namespace BroChoos.Platform.Windows.Storage;

public sealed class GenerationMutationCoordinator
{
	private readonly IStorageReadFileSystem _readFileSystem;

	public GenerationMutationCoordinator(IStorageReadFileSystem readFileSystem)
	{
		ArgumentNullException.ThrowIfNull(readFileSystem);
		_readFileSystem = readFileSystem;
	}

	public MutationCommitResult Commit<TDocument>(
		string destinationPath,
		TDocument nextDocument,
		ExpectedDocumentGeneration expected,
		TimeSpan writeLockTimeout,
		string? backupsDirectory,
		Action<long, TDocument> onCommitted)
		where TDocument : ConfigDocument
	{
		ArgumentNullException.ThrowIfNull(destinationPath);
		ArgumentNullException.ThrowIfNull(nextDocument);
		ArgumentNullException.ThrowIfNull(onCommitted);

		using Mutex writeLock = new(initiallyOwned: false, AtomicJsonFileStore.BuildMutexName(destinationPath));
		bool acquired = false;
		try
		{
			try
			{
				acquired = writeLock.WaitOne(writeLockTimeout);
			}
			catch (AbandonedMutexException)
			{
				acquired = true;
			}

			if (!acquired)
			{
				IOException timedOut = new(FormattableString.Invariant(
					$"Timed out after {writeLockTimeout} waiting for the per-file config-write lock on '{Path.GetFileName(destinationPath)}'; the destination was not modified."));
				timedOut.Data["path"] = destinationPath;
				throw timedOut;
			}

			ConfigFileObserver observer = new(_readFileSystem);
			(ConfigFileObservation observation, TDocument? observedDocument) =
				observer.ObserveDocument<TDocument>(destinationPath);

			if (observation.Schema == SchemaCompatibility.NewerVersionReadOnly)
			{
				long observedGeneration = observedDocument?.Generation ?? 0;
				return MutationCommitResult.NewerSchemaRefused(observedGeneration);
			}

			if (!MatchesExpected(observation, observedDocument, expected, out long? observedGenerationForConflict))
			{
				return MutationCommitResult.Conflict(observedGenerationForConflict);
			}

			long priorGeneration = observedDocument?.Generation ?? 0;
			TDocument documentToCommit = nextDocument with { Generation = checked(priorGeneration + 1) };
			new AtomicJsonFileStore(backupsDirectory, writeLockTimeout).Write(destinationPath, documentToCommit);

			onCommitted(documentToCommit.Generation, documentToCommit);

			return MutationCommitResult.Committed(documentToCommit.Generation, priorGeneration);
		}
		finally
		{
			if (acquired)
			{
				writeLock.ReleaseMutex();
			}
		}
	}

	private static bool MatchesExpected(
		ConfigFileObservation observation,
		ConfigDocument? observedDocument,
		ExpectedDocumentGeneration expected,
		out long? observedGeneration)
	{
		// F-A: A DESTINATION THAT EXISTS BUT IS NOT A READABLE DOCUMENT MATCHES NOTHING.
		//
		// This is the CAS authority, so it is the one place that can refuse on the SHAPE of
		// what it observed rather than only on a generation number. If the destination is
		// present yet unparseable (unparseable body or envelope) or could not be read at all
		// (open-failed), we cannot know its generation - and `observedDocument?.Generation ??
		// 0` would INVENT a generation of 0, which then matched Match(0) and let the commit
		// replace the corrupt bytes. Overwriting such a file is the explicit
		// quarantine/repair route's job (ConfigRepairExecutor writes directly, not through
		// here); a plain generation-checked save must fail closed.
		//
		// This refusal is deliberately BEFORE the expected-token branches, so it holds for
		// EVERY token: Absent, Match(0), or any Match(n).
		if (observation.Access == ConfigFileAccess.OpenFailed
			|| observation.Body == DocumentBodyParse.Unparseable
			|| observation.Envelope == EnvelopeParse.Unparseable)
		{
			observedGeneration = observedDocument?.Generation;
			return false;
		}

		if (!expected.ExpectsDocument)
		{
			bool absent = observation.Access == ConfigFileAccess.Absent;
			observedGeneration = absent ? 0 : observedDocument?.Generation;
			return absent;
		}

		if (observation.Access == ConfigFileAccess.Absent)
		{
			observedGeneration = null;
			return false;
		}

		long currentGeneration = observedDocument?.Generation ?? 0;
		observedGeneration = currentGeneration;
		return currentGeneration == expected.Value;
	}
}
