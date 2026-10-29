namespace BroChoos.Core.Storage;

// D-089 / owner decision 4: the verdict of the in-process repair budget.
public enum RepairAttemptPolicy
{
	// Another attempt at the same repair path is allowed.
	Permitted,

	// D-089: the same repair path has already run MaxAttempts times. Stop
	// retrying — mark corrupted, quarantine, default, surface on next launch.
	DeniedAttemptsExhausted,

	// The elapsed-time budget is spent, measured BEFORE an attempt starts. This stops
	// repair from opening further attempts and so reduces total latency; it does not
	// cap it, and it does not enforce D-493's 2000 ms picker-open limit — an attempt
	// already authorised runs to completion uncounted. See RepairAttemptBudget.
	DeniedTimeBudgetExhausted
}
