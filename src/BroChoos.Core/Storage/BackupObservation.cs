namespace BroChoos.Core.Storage;

// What the executor observed about a CANDIDATE backup, if it found one.
//
// D-085 attaches no schema precondition to its restore permission; D-066 forbids
// ever overwriting newer configuration. A backup minted by a newer app version is
// itself newer-schema, and promoting it converts a recoverable corrupt-file
// problem into a permanent read-only lockout — so the backup's OWN envelope is
// observed here and validated before promotion.
public sealed record BackupObservation
{
	private BackupObservation(
		BackupName name,
		EnvelopeParse envelope,
		DocumentBodyParse body,
		SchemaCompatibility? schema)
	{
		Name = name;
		Envelope = envelope;
		Body = body;
		Schema = schema;
	}

	// The bare file name, never a path (Core does no I/O and composes no paths).
	public BackupName Name { get; }

	public EnvelopeParse Envelope { get; }

	public DocumentBodyParse Body { get; }

	// null exactly when Envelope is Unparseable: no verdict is knowable.
	public SchemaCompatibility? Schema { get; }

	// The backup deserialized end to end and its envelope evaluated to `schema`.
	public static BackupObservation Loaded(BackupName name, SchemaCompatibility schema)
	{
		return new(name, EnvelopeParse.Parsed, DocumentBodyParse.Parsed, schema);
	}

	// The backup's envelope read, but its body is garbage — it is not promotable.
	public static BackupObservation BodyUnparseable(BackupName name, SchemaCompatibility schema)
	{
		return new(name, EnvelopeParse.Parsed, DocumentBodyParse.Unparseable, schema);
	}

	// The backup's own envelope did not parse — its schema is unknowable, so it is
	// not promotable either (promoting it could silently downgrade newer data).
	public static BackupObservation EnvelopeUnparseable(BackupName name)
	{
		return new(name, EnvelopeParse.Unparseable, DocumentBodyParse.Unparseable, null);
	}
}
