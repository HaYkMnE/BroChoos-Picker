namespace BroChoos.Core.Storage;

// The outcome of parsing ONLY the schema envelope (schemaVersion/schemaRevision),
// separately from the body. Owner decision 1 requires the envelope to be read and
// evaluated BEFORE any repair decision; when it does not parse the schema is
// unknowable and route (b) — preserve the original first — applies.
public enum EnvelopeParse
{
	// schemaVersion/schemaRevision were read. A SchemaCompatibility verdict exists.
	Parsed,

	// The envelope did not parse. No schema verdict can exist, and none may be
	// invented.
	Unparseable
}
