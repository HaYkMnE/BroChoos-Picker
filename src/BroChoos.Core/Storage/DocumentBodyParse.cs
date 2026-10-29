namespace BroChoos.Core.Storage;

// Owner decision 2: a write is licensed only on a WHOLE-DOCUMENT parse. The
// envelope parsing is NOT enough — a file whose schemaVersion/schemaRevision read
// cleanly but whose body is garbage must never be plainly overwritten. This enum
// is the observation the executor reports for everything BELOW the envelope.
public enum DocumentBodyParse
{
	// The whole document deserialized, envelope and body.
	Parsed,

	// The envelope read, but deserializing the full document failed. Routes to
	// quarantine-first, never to a plain overwrite.
	Unparseable
}
