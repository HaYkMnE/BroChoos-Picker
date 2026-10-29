namespace BroChoos.Core.Storage;

// D-089 / owner decision 4: the in-process budget for retrying the SAME repair
// path. GATED by count AND by elapsed time — a pre-attempt gate, not a deadline; see
// the latency paragraph below before inferring anything about total duration.
//
// In-process rather than persisted: the locked words are "stop retrying same
// repair loop", which describes an in-process loop, and persisting the counter in
// app.json was rejected because app.json is itself one of the files whose read can
// fail — the counter would be unreadable in exactly the scenario it bounds.
//
// Time-bounded to REDUCE total repair latency, NOT to cap it. D-493 caps picker-open
// at 2000 ms; this type does not enforce that cap. Evaluate is a PRE-ATTEMPT GATE: it
// decides whether to START another attempt and measures nothing about what an attempt
// costs once authorised. So attempt 3 may legitimately begin at 1499 ms against the
// 1500 ms default and then run for an unbounded duration — a slow disk, an SMB share,
// an antivirus scan mid-read. Refusing further attempts earlier is a real reduction;
// it is not a guarantee, and reading it as one is how the 2000 ms figure below gets
// mistaken for something this file delivers. What IS bounded, and what remains
// uncounted, is recorded on ConfigRepairExecutor (Platform.Windows), which owns the
// retry loop and derives its write-lock timeout from what is LEFT of this budget.
//
// Immutable: RecordAttempt returns a new instance, so the type stays a pure value and
// a test can hold both the before and after states.
//
// Time is injected. TimeProvider.GetTimestamp/GetElapsedTime is used rather than
// the ambient static wall clock, both because the DI container already registers
// TimeProvider and because GetElapsedTime is monotonic — a wall-clock jump cannot
// make the budget look spent or unspent. Elsewhere in the solution the ambient
// clock is read directly; that is a known wart, not a pattern copied here.
//
// This type answers only "may I attempt again?". What to do once the answer is no
// belongs to the executor loop that owns the retry: D-089 requires it to mark the
// file corrupted, move the corrupted copy to backups/corrupted, create a safe
// default and surface a next-launch message — which is the planner's
// QuarantineThenCreateDefaults instruction plus a 0B surface. The budget is kept
// separate from ConfigLoadPlanner deliberately: the planner is a per-observation
// function with no memory, and folding a counter into it would give it state.
public sealed class RepairAttemptBudget
{
	// D-089: "if the same recovery path fails 3 times — stop retrying".
	public const int MaxAttempts = 3;

	// Core-side default. The VALUE sits under the D-493 2000 ms picker-open cap,
	// leaving nominal headroom for the rest of startup — but it is the instant after
	// which no NEW attempt opens, not a ceiling on when repair finishes. A caller may
	// pass a tighter budget; this is a Core default and not a locked number.
	public static readonly TimeSpan DefaultTimeBudget = TimeSpan.FromMilliseconds(1500);

	private readonly TimeProvider timeProvider;
	private readonly long startedAtTimestamp;

	private RepairAttemptBudget(
		TimeProvider timeProvider,
		long startedAtTimestamp,
		TimeSpan timeBudget,
		int attemptsUsed)
	{
		this.timeProvider = timeProvider;
		this.startedAtTimestamp = startedAtTimestamp;
		TimeBudget = timeBudget;
		AttemptsUsed = attemptsUsed;
	}

	// How many attempts at this repair path have already been made.
	public int AttemptsUsed { get; }

	public TimeSpan TimeBudget { get; }

	// Attempts still available by COUNT alone. Never negative.
	public int AttemptsRemaining => Math.Max(0, MaxAttempts - AttemptsUsed);

	// Monotonic time since the budget started.
	public TimeSpan Elapsed => timeProvider.GetElapsedTime(startedAtTimestamp);

	// Opens a budget and stamps the monotonic start instant.
	public static RepairAttemptBudget Start(TimeProvider timeProvider, TimeSpan? timeBudget = null)
	{
		ArgumentNullException.ThrowIfNull(timeProvider);

		TimeSpan budget = timeBudget ?? DefaultTimeBudget;

		// TimeSpan is not INumberBase, so ArgumentOutOfRangeException.ThrowIfNegativeOrZero
		// does not apply here; the check is explicit.
		return budget <= TimeSpan.Zero
			? throw new ArgumentOutOfRangeException(
				nameof(timeBudget),
				budget,
				"A repair time budget must be positive.")
			: new RepairAttemptBudget(timeProvider, timeProvider.GetTimestamp(), budget, 0);
	}

	// Whether another attempt at the same repair path is permitted, measured
	// against the injected clock.
	public RepairAttemptPolicy Evaluate()
	{
		return Evaluate(Elapsed);
	}

	// Pure overload: decides against a caller-supplied elapsed value, with no clock
	// involved at all. Count is checked first so that an exhausted count reports as
	// such even when the time budget is also spent — the count is the D-089 rule, and
	// the elapsed check sits on top of it as a latency reduction, not as enforcement
	// of the D-493 cap (which this type does not deliver; see the header).
	public RepairAttemptPolicy Evaluate(TimeSpan elapsed)
	{
		return AttemptsUsed >= MaxAttempts
			? RepairAttemptPolicy.DeniedAttemptsExhausted
			: elapsed >= TimeBudget
			? RepairAttemptPolicy.DeniedTimeBudgetExhausted
			: RepairAttemptPolicy.Permitted;
	}

	// Books one attempt against the budget, returning the advanced value. The
	// counter saturates at MaxAttempts so repeated calls cannot overflow it.
	public RepairAttemptBudget RecordAttempt()
	{
		return new(
			timeProvider,
			startedAtTimestamp,
			TimeBudget,
			Math.Min(MaxAttempts, AttemptsUsed + 1));
	}
}
