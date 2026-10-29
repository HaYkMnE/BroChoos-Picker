using BroChoos.Core.Routing;

namespace BroChoos.Core.Storage;

// W7.1: the read/write surface for the persisted rules document. Deliberately small and
// split into read (LoadOrDefault) and one generation-checked write (Save), so the picker
// can consume rules without any launch or process authority — this interface grants none.
//
// FAIL-OPEN ON READ: LoadOrDefault never throws for a state a rules file can really be
// in (absent, locked, corrupt, newer schema). Rules are optional suggestions, so the
// honest fallback is "no rules, generation 0", and a broken collaborator — not a file
// state — is the only thing that escapes.
public interface IRulesStore
{
	// Returns the persisted rules, or an empty generation-0 set when nothing usable could
	// be read. Never returns a partially-parsed document: either the whole document bound
	// or the default is returned.
	RuleSetDocument LoadOrDefault();

	// The same fail-open read, plus WHY the value is what it is (loaded, missing, corrupt,
	// incompatible), so the picker can tell a broken file from a first run. The default
	// body serves stores that have no file behind them (null objects, test fakes).
	ConfigReadResult<RuleSetDocument> LoadWithOutcome()
	{
		return new(LoadOrDefault(), ConfigReadOutcome.Loaded);
	}

	// Commits against the exact observed generation (or the absent token), atomically.
	// The caller supplies the expected generation so a stale writer is refused rather
	// than silently overwriting a newer document.
	MutationCommitResult Save(RulesDocument document, ExpectedDocumentGeneration expectedGeneration);
}

// Deferred provisioning, for the same measured reason every storage factory in this
// codebase is deferred: composing the store resolves a StorageRootResolver, whose
// constructor CREATES directories. That must not happen at process start on a command
// line that may be --help, a rejected URL, or --safe-mode. This factory's own
// constructor does no I/O; Create is the moment the parsed storage root is known.
public interface IRulesStoreFactory
{
	IRulesStore Create(string? portableDataPath);
}
