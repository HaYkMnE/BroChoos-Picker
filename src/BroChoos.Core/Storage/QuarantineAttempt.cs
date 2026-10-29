namespace BroChoos.Core.Storage;

// Owner decision 3, extended: the state of quarantine for ONE file within one
// load cycle. The executor re-observes after quarantining, which is what makes a
// plain RestoreFromBackup / CreateDefaultsAndPersist reachable without a second
// quarantine-move.
public enum QuarantineAttempt
{
	// No quarantine has been attempted for this file in this load cycle.
	NotAttempted,

	// The original has already been preserved in backups/corrupted. Owner decision
	// 1 route (b) is satisfied, so a write no longer needs a fresh quarantine.
	Succeeded,

	// A quarantine was attempted and failed (the target directory is not
	// pre-provisioned; the disk can be full). Fails CLOSED to read-only — this is
	// explicitly NOT a retry signal (D-089).
	Failed
}
