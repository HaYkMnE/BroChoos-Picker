namespace BroChoos.Core.Storage;

// Everything the executor OBSERVED about one config file, with no I/O performed
// here and no document payload carried. D-071 makes atomicity per file, so this
// describes exactly one file; there is deliberately no trio-wide type.
//
// The named constructors are the enforcement point for "an observation cannot
// claim knowledge it could not have": there is no public constructor, so it is
// impossible to build an observation that reports OpenFailed yet also carries a
// schema verdict, or reports an unparseable envelope yet also carries one.
public sealed record ConfigFileObservation
{
	private ConfigFileObservation(
		ConfigFileAccess access,
		EnvelopeParse? envelope,
		DocumentBodyParse? body,
		SchemaCompatibility? schema,
		BackupObservation? backup,
		QuarantineAttempt quarantine)
	{
		Access = access;
		Envelope = envelope;
		Body = body;
		Schema = schema;
		Backup = backup;
		Quarantine = quarantine;
	}

	public ConfigFileAccess Access { get; }

	// null unless Access is Opened — bytes are a precondition for parsing.
	public EnvelopeParse? Envelope { get; }

	// null unless Access is Opened.
	public DocumentBodyParse? Body { get; }

	// null unless the envelope parsed. No verdict may be invented for an envelope
	// that could not be read (owner decision 1).
	public SchemaCompatibility? Schema { get; }

	// null when the executor found no candidate backup.
	public BackupObservation? Backup { get; }

	public QuarantineAttempt Quarantine { get; }

	// The file is not there. Nothing to protect, nothing to preserve.
	public static ConfigFileObservation Absent()
	{
		return new(ConfigFileAccess.Absent, null, null, null, null, QuarantineAttempt.NotAttempted);
	}

	// The file exists but would not open (lock, denied ACL). Distinct from Absent:
	// unreadable bytes may still be a newer version's, so nothing may be written.
	public static ConfigFileObservation OpenFailed()
	{
		return new(ConfigFileAccess.OpenFailed, null, null, null, null, QuarantineAttempt.NotAttempted);
	}

	// Bytes read, whole document deserialized, envelope evaluated to `schema`.
	public static ConfigFileObservation Loaded(
		SchemaCompatibility schema,
		BackupObservation? backup = null,
		QuarantineAttempt quarantine = QuarantineAttempt.NotAttempted)
	{
		return new(
			ConfigFileAccess.Opened,
			EnvelopeParse.Parsed,
			DocumentBodyParse.Parsed,
			schema,
			backup,
			quarantine);
	}

	// Owner decision 2: the envelope read and evaluated to `schema`, but the body
	// below it is garbage. A cleared envelope does NOT license overwriting this.
	public static ConfigFileObservation BodyUnparseable(
		SchemaCompatibility schema,
		BackupObservation? backup = null,
		QuarantineAttempt quarantine = QuarantineAttempt.NotAttempted)
	{
		return new(
			ConfigFileAccess.Opened,
			EnvelopeParse.Parsed,
			DocumentBodyParse.Unparseable,
			schema,
			backup,
			quarantine);
	}

	// The envelope itself did not parse, so the schema is UNKNOWABLE. Note the
	// signature takes no SchemaCompatibility at all — the illegal input "no
	// envelope, but here is a verdict anyway" cannot be expressed.
	public static ConfigFileObservation EnvelopeUnparseable(
		BackupObservation? backup = null,
		QuarantineAttempt quarantine = QuarantineAttempt.NotAttempted)
	{
		return new(
			ConfigFileAccess.Opened,
			EnvelopeParse.Unparseable,
			DocumentBodyParse.Unparseable,
			null,
			backup,
			quarantine);
	}

	// Same observation, with the quarantine state advanced. Lets the executor
	// re-plan after attempting a quarantine without rebuilding the observation.
	public ConfigFileObservation WithQuarantine(QuarantineAttempt quarantine)
	{
		return new(Access, Envelope, Body, Schema, Backup, quarantine);
	}

	// Same observation, with a candidate backup attached.
	public ConfigFileObservation WithBackup(BackupObservation? backup)
	{
		return new(Access, Envelope, Body, Schema, backup, Quarantine);
	}
}
