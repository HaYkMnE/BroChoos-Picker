namespace BroChoos.Core.Storage;

// TOKEN-INTERNAL. Why a RepairClearance's schema check cleared — evidence about the
// ENVELOPE, recorded on the token itself.
//
// This is NOT what a decision carries. A quarantine-first decision always reports
// ClearanceRoute.OriginalPreservedBeforeAnyWrite, because the preservation is what
// licenses the write; the basis below only records what the check found. The two
// questions were one enum once, which left a member that could never be observed on
// a decision and that a logging slice would have rendered a dead arm for. Splitting
// them is the type-level fix: neither enum now has a value its consumer cannot see.
public enum RepairClearanceBasis
{
	// The on-disk envelope was read and proved NOT newer-version. Licenses a
	// quarantine-bundled repair only; it is never a licence to overwrite in place,
	// because a bare overwrite of an existing file is not a decision this planner
	// reaches.
	EnvelopeProvenNotNewer,

	// The envelope did not parse, so no schema verdict can exist. The only legal
	// move is to preserve the original before anything is written — a quarantine
	// MOVE destroys nothing, so it is safe even for content that turns out to be
	// newer.
	EnvelopeUnknowableSoPreserveFirst
}
