namespace BroChoos.Core.Abstractions;

// Per-target outcome of a register/unregister run. Registered reports the actual
// post-operation verification state (D-268 step 1), never the intended state.
public sealed record RegisteredTypeOutcome(
	DefaultAppsTarget Target,
	string Scheme,
	bool Registered,
	string? Error);

// Result of one register/unregister run. BackupFilePath is the FULL path of the
// pre-operation snapshot so the operator can restore manually (D-299): it is a
// user-facing console value only and never lands in a persistent log line or in
// system_changes.log (D-291 redaction). RemovedKeyPaths lists what an unregister
// actually deleted (empty for a register run).
public sealed record CapabilityRegistrationResult(
	bool Succeeded,
	IReadOnlyList<RegisteredTypeOutcome> Outcomes,
	IReadOnlyList<string> RemovedKeyPaths,
	string? BackupFilePath,
	string? ErrorMessage);
