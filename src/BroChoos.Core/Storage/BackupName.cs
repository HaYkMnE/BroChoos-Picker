namespace BroChoos.Core.Storage;

// The bare NAME of a candidate backup file — never a path.
//
// Core is platform-neutral and does no I/O (D-489 keeps every surface out of
// 0A-5), so it must never carry or compose a filesystem path. AtomicJsonFileStore
// already redacts its failure messages to the file NAME only; this type makes the
// same discipline structural on the read path: a decision can name a backup for
// the executor to resolve, but it cannot smuggle a directory, a drive or a
// traversal segment across the boundary.
public sealed record BackupName
{
	// Listed literally rather than taken from the framework's invalid-file-name-char
	// helper, because Core must not touch the filesystem namespace at all. The colon
	// covers drive qualifiers and alternate data streams.
	private static readonly char[] PathQualifiers = ['\\', '/', ':'];

	public BackupName(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException("A backup name must be a non-empty file name.", nameof(value));
		}

		if (value.IndexOfAny(PathQualifiers) >= 0)
		{
			throw new ArgumentException("A backup name must be a bare file name, not a path.", nameof(value));
		}

		if (value is "." or "..")
		{
			throw new ArgumentException("A backup name must not be a relative directory segment.", nameof(value));
		}

		Value = value;
	}

	public string Value { get; }

	public override string ToString()
	{
		return Value;
	}
}
