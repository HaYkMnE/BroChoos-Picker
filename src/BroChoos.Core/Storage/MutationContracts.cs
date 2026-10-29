namespace BroChoos.Core.Storage;

// W3.1 shared mutation contracts: every durable writer must commit against either
// the exact observed generation or an explicit "document absent" token.
public readonly record struct ExpectedDocumentGeneration
{
	private ExpectedDocumentGeneration(bool expectsDocument, long value)
	{
		if (expectsDocument && value < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(value), "Expected generation must be non-negative.");
		}

		ExpectsDocument = expectsDocument;
		Value = value;
	}

	public bool ExpectsDocument { get; }

	public long Value { get; }

	public static ExpectedDocumentGeneration Absent { get; } = new(false, 0);

	public static ExpectedDocumentGeneration Match(long observedGeneration)
	{
		return new ExpectedDocumentGeneration(true, observedGeneration);
	}
}

public enum MutationCommitStatus
{
	Committed,
	Conflict,
	NewerSchemaRefused,
}

public sealed record MutationCommitResult(
	MutationCommitStatus Status,
	long? CommittedGeneration,
	long? ObservedGeneration)
{
	public static MutationCommitResult Committed(long committedGeneration, long priorGeneration)
	{
		return new MutationCommitResult(MutationCommitStatus.Committed, committedGeneration, priorGeneration);
	}

	public static MutationCommitResult Conflict(long? observedGeneration)
	{
		return new MutationCommitResult(MutationCommitStatus.Conflict, null, observedGeneration);
	}

	public static MutationCommitResult NewerSchemaRefused(long observedGeneration)
	{
		return new MutationCommitResult(MutationCommitStatus.NewerSchemaRefused, null, observedGeneration);
	}
}
