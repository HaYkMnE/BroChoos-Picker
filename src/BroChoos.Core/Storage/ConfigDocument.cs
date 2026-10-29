namespace BroChoos.Core.Storage;

// D-064: every root config document carries the schema envelope at the top
// level, modeled here as shared required properties:
//
//   { "schemaVersion": 1, "schemaRevision": 0 }
//
// schemaVersion is the major breaking version; schemaRevision is the
// minor/additive revision. Newer-version safety and migration rules are decided
// by SchemaCompatibilityEvaluator, not by this type.
public abstract record ConfigDocument
{
	public int SchemaVersion { get; init; } = SchemaVersioning.Version;

	public int SchemaRevision { get; init; } = SchemaVersioning.Revision;

	// Monotonic durable generation for compare-and-swap writes. Legacy documents that
	// predate generation carry no field on disk and are treated as generation 0 by
	// the mutation coordinator/backbone.
	public long Generation { get; init; }
}
