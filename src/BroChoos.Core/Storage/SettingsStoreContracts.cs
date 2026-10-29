namespace BroChoos.Core.Storage;

// W8.1 — the read/write surface for the persisted settings document. Mirrors
// IRulesStore/IRulesStoreFactory exactly (the other single-document store built on the
// W3 generation/CAS machinery): read (LoadOrDefault) plus one generation-checked write
// (Save). The settings UI consumes it through this Core contract; the App never sees the
// platform store.
//
// FAIL-OPEN ON READ: LoadOrDefault never throws for a state a settings file can really
// be in (absent, locked, corrupt, newer schema). The honest fallback is the document's
// own defaults, because losing settings must never block the settings window from opening
// (D-v3.5-083 fail-open). Only a broken collaborator escapes.
public interface ISettingsStore
{
	// Returns the persisted settings, or the default document when nothing usable could
	// be read. Never returns a partially-parsed document.
	SettingsDocument LoadOrDefault();

	// The expected-generation token a writer must send to Save so the coordinator's CAS
	// compares against what is ACTUALLY on disk: Absent when there is no file (a fresh
	// install), Match(generation) once there is one.
	//
	// WHY THIS IS PART OF THE CONTRACT rather than something the caller guesses from
	// LoadOrDefault: LoadOrDefault deliberately HIDES absence (it fails open to defaults),
	// so a caller cannot tell "generation 0 because empty" from "generation 0 because the
	// file does not exist". Sending Match(0) for an absent file makes the coordinator's
	// MatchesExpected return false (ExpectsDocument && Access==Absent), which would make the
	// FIRST save on a clean install Conflict forever. This method makes the distinction
	// explicit and observable at the storage boundary, where the observation really happens.
	ExpectedDocumentGeneration ObserveExpectedGeneration();

	// Commits against the exact observed generation (or the absent token), atomically.
	// A stale writer is refused rather than silently overwriting a newer document.
	MutationCommitResult Save(SettingsDocument document, ExpectedDocumentGeneration expectedGeneration);
}

// Deferred provisioning, for the same measured reason every storage factory in this
// codebase is deferred: composing the store resolves a StorageRootResolver, whose
// constructor CREATES directories. That must not happen at process start on a command
// line that may be --help, a rejected URL, or --safe-mode. This factory's own constructor
// does no I/O; Create is the moment the parsed storage root is known.
public interface ISettingsStoreFactory
{
	ISettingsStore Create(string? portableDataPath);
}
