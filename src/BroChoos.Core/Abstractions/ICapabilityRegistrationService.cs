namespace BroChoos.Core.Abstractions;

// D-187/D-274 (0A-4): BroChoos declares link-handler CAPABILITIES in HKCU only
// (D-273); the USER picks the default in Windows Settings. Neither member ever
// touches a UserChoice key — that is a hard invariant enforced by the write seam
// and pinned by tests, not a convention.
public interface ICapabilityRegistrationService
{
	// Idempotent: a re-run rewrites the same capability shape and reaches the
	// same end state. Before ANY registry write, the pre-write state of every
	// relevant key is snapshotted to a backups file; afterwards one JSONL line
	// per action is appended to system_changes.log (D-291/D-299).
	CapabilityRegistrationResult RegisterAll();

	// Destructive-confirmation-style flow (D-299): fresh backup first, then
	// removal of ONLY BroChoos-owned HKCU capability keys (foreign values in
	// shared keys are preserved), then verification through the D-268
	// three-step model, then a success message naming the restore path.
	CapabilityRegistrationResult UnregisterAll();
}
