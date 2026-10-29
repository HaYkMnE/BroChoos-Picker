namespace BroChoos.Core.Storage;

// The DECISION-facing justification for touching a byte on the read path: exactly
// the routes a ConfigLoadDecision can actually carry, and no others. Every mutating
// decision carries one, so a later logging layer can state WHY a write was
// permitted without inventing wording.
//
// Deliberately only TWO values. This enum is split from RepairClearanceBasis so
// that an exhaustive switch over Decision.Clearance in the logging slice is
// exhaustive over REALITY: there is no member here that cannot be observed on a
// decision, so no dead arm and no 0B string can be written for an outcome that
// never occurs. The token-internal basis on which a repair cleared its schema check
// is a different question with a different type — see RepairClearanceBasis.
//
// "Newer" in that other type means a newer major schemaVersion (D-066) or an
// envelope that is not legal at all. A newer schemaRevision is a separate, weaker
// case: it never licenses a bare overwrite, but it does admit quarantine-FIRST
// repair, because the quarantine preserves the original before anything is written.
public enum ClearanceRoute
{
	// There is no file, therefore no newer-version configuration to destroy.
	// D-085: "create missing default config on first run".
	NoExistingFileToProtect,

	// Route (b): the original has been preserved in backups/corrupted, so whatever
	// its schema was, it still exists and nothing can be destroyed by writing. This
	// is the route EVERY quarantine-first decision reports, because in a
	// quarantine-first instruction it is the preservation — not the envelope — that
	// licenses the write.
	OriginalPreservedBeforeAnyWrite
}
