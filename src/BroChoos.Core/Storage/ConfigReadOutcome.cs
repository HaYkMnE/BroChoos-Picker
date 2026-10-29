namespace BroChoos.Core.Storage;

// WHY a store's LoadOrDefault returned what it returned. The fail-open read path hands back
// defaults for every unusable file state, which keeps the link working but used to hide the
// difference between "nothing saved yet" (normal) and "the saved file is broken" (the user
// lost history or rules and should be told). This is that difference, as data.
public enum ConfigReadOutcome
{
	// The document was read and bound completely; the returned value IS the file.
	Loaded,

	// No file: first run or never saved. Normal, and never reported to the user.
	Missing,

	// The file exists but could not be opened (locked, access denied). Transient; the file
	// itself is not known to be broken, so this is not reported as damage either.
	OpenFailed,

	// The file was opened but its envelope or body did not parse (or did not bind).
	// Defaults are in use; the file is left on disk untouched.
	Corrupt,

	// The file parsed but its schema is OLDER than this build and no migration exists.
	// Defaults are in use; the file is left on disk untouched, and no repair may write over
	// it (D-070 hard stop).
	Incompatible,

	// The envelope parsed but is not a legal one (a negative schemaVersion/schemaRevision).
	// The planner hard-stops it (MainFileEnvelopeInvalidReadOnly): no repair, no quarantine,
	// no write. Defaults are in use; the user has to move the file aside.
	InvalidEnvelope,

	// The file was saved by a NEWER BroChoos (newer version, or newer revision whose extra
	// fields this build would drop). Defaults are in use; the file must never be rewritten by
	// this build, so a repair leaves it alone.
	Newer,

	// The file is over the observer's read ceiling, so it was never read. Not "corrupt" for
	// the repair planner (nothing is known about its bytes), but the user's settings are
	// still not in use, so the picker reports it like damage.
	TooLarge,
}

// A fail-open read together with the reason for its value.
public sealed record ConfigReadResult<T>(T Value, ConfigReadOutcome Outcome);

public static class ConfigReadOutcomes
{
	// The single mapping from an observer result to an outcome, shared by every store so
	// "broken" means the same thing for history and rules.
	public static ConfigReadOutcome Classify(ConfigFileObservation observation, bool documentBound)
	{
		ArgumentNullException.ThrowIfNull(observation);

		if (observation.Access == ConfigFileAccess.Absent)
		{
			return ConfigReadOutcome.Missing;
		}

		if (observation.Access != ConfigFileAccess.Opened)
		{
			return ConfigReadOutcome.OpenFailed;
		}

		if (observation.Envelope != EnvelopeParse.Parsed || observation.Body != DocumentBodyParse.Parsed)
		{
			return ConfigReadOutcome.Corrupt;
		}

		if (observation.Schema is SchemaCompatibility.NewerVersionReadOnly or SchemaCompatibility.NewerRevisionReadKnownOnly)
		{
			return ConfigReadOutcome.Newer;
		}

		if (observation.Schema == SchemaCompatibility.Invalid)
		{
			return ConfigReadOutcome.InvalidEnvelope;
		}

		if (observation.Schema != SchemaCompatibility.CompletelyCompatible)
		{
			return ConfigReadOutcome.Incompatible;
		}

		// Parsed and compatible but not bound (a required field missing) is a broken file too.
		return documentBound ? ConfigReadOutcome.Loaded : ConfigReadOutcome.Corrupt;
	}

	// True for the outcomes the user is told about: the saved file exists and was not used.
	public static bool IsDamaged(ConfigReadOutcome outcome)
	{
		return outcome is ConfigReadOutcome.Corrupt or ConfigReadOutcome.Incompatible or ConfigReadOutcome.InvalidEnvelope
			or ConfigReadOutcome.Newer or ConfigReadOutcome.TooLarge;
	}
}
