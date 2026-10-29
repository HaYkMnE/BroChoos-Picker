namespace BroChoos.Core.Storage;

// D-064/D-070: the single place that pins the schema the app reads and writes.
// 0A-5 recognizes exactly (1, 0). Changing either value is a conscious migration
// decision, never an incidental edit.
public static class SchemaVersioning
{
	// Major breaking version. A change to this number requires a migration.
	public const int Version = 1;

	// Minor/additive revision. Additive fields within the same Version bump only
	// this number (D-070).
	public const int Revision = 0;
}
