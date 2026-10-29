using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Storage;

// D-089 / owner decision 4: the in-process repair budget, bounded by BOTH count
// and elapsed time. Time is injected through TimeProvider, never the ambient static
// wall clock, so these tests are deterministic and do not sleep.
public sealed class RepairAttemptBudgetTests
{
	private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

	[Fact]
	public void Start_FreshBudget_PermitsAnAttempt()
	{
		RepairAttemptBudget budget = RepairAttemptBudget.Start(new ManualTimeProvider(), Budget);

		_ = budget.AttemptsUsed.Should().Be(0);
		_ = budget.AttemptsRemaining.Should().Be(3);
		_ = budget.Evaluate().Should().Be(RepairAttemptPolicy.Permitted);
	}

	[Fact]
	public void MaxAttempts_IsThree_PerD089()
	{
		_ = RepairAttemptBudget.MaxAttempts.Should().Be(3);
	}

	// The default sits under the D-493 2000 ms picker-open cap, so opening a further
	// repair attempt is refused before the cap is reached. That is all this pins: the
	// gate is checked BEFORE an attempt and does not measure the attempt, so this is
	// not evidence that three attempts stay inside the cap (see RepairAttemptBudget).
	[Fact]
	public void DefaultTimeBudget_StaysUnderThePickerOpenCap()
	{
		_ = RepairAttemptBudget.DefaultTimeBudget.Should().BeLessThan(TimeSpan.FromMilliseconds(2000));
	}

	[Fact]
	public void Evaluate_AfterTwoAttemptsWithinTime_StillPermitted()
	{
		RepairAttemptBudget budget = RepairAttemptBudget
			.Start(new ManualTimeProvider(), Budget)
			.RecordAttempt()
			.RecordAttempt();

		_ = budget.AttemptsUsed.Should().Be(2);
		_ = budget.AttemptsRemaining.Should().Be(1);
		_ = budget.Evaluate().Should().Be(RepairAttemptPolicy.Permitted);
	}

	// D-089: "if the same recovery path fails 3 times — stop retrying."
	[Fact]
	public void Evaluate_ThreeAttemptsUsed_IsExhaustedByCount()
	{
		RepairAttemptBudget budget = RepairAttemptBudget
			.Start(new ManualTimeProvider(), Budget)
			.RecordAttempt()
			.RecordAttempt()
			.RecordAttempt();

		_ = budget.AttemptsUsed.Should().Be(3);
		_ = budget.AttemptsRemaining.Should().Be(0);
		_ = budget.Evaluate().Should().Be(RepairAttemptPolicy.DeniedAttemptsExhausted);
	}

	[Fact]
	public void RecordAttempt_BeyondMax_SaturatesRatherThanOverflows()
	{
		RepairAttemptBudget budget = RepairAttemptBudget.Start(new ManualTimeProvider(), Budget);

		for (int i = 0; i < 10; i++)
		{
			budget = budget.RecordAttempt();
		}

		_ = budget.AttemptsUsed.Should().Be(3);
		_ = budget.Evaluate().Should().Be(RepairAttemptPolicy.DeniedAttemptsExhausted);
	}

	// Separately from the count: the elapsed-time pre-attempt gate. Attempts remain
	// available by count, and the budget refuses to START another attempt after the
	// elapsed budget; this does not itself enforce the D-493 picker-open target.
	[Fact]
	public void Evaluate_TimeBudgetSpentWithAttemptsRemaining_IsExhaustedByTime()
	{
		ManualTimeProvider time = new();
		RepairAttemptBudget budget = RepairAttemptBudget.Start(time, Budget).RecordAttempt();

		time.Advance(TimeSpan.FromMilliseconds(1500));

		_ = budget.AttemptsRemaining.Should().Be(2);
		_ = budget.Elapsed.Should().Be(TimeSpan.FromMilliseconds(1500));
		_ = budget.Evaluate().Should().Be(RepairAttemptPolicy.DeniedTimeBudgetExhausted);
	}

	[Fact]
	public void Evaluate_JustInsideTheTimeBudget_IsStillPermitted()
	{
		ManualTimeProvider time = new();
		RepairAttemptBudget budget = RepairAttemptBudget.Start(time, Budget);

		time.Advance(TimeSpan.FromMilliseconds(1499));

		_ = budget.Evaluate().Should().Be(RepairAttemptPolicy.Permitted);
	}

	// THE CLOCK DRIVEN BACKWARDS. Read the framing before trusting this test: the
	// production clock CANNOT do this. RepairAttemptBudget reads
	// TimeProvider.GetTimestamp/GetElapsedTime, which are monotonic, and there is no
	// DateTime anywhere in the layer — that is why a wall-clock jump is structurally
	// impossible here rather than merely untested. This test does not weaken that
	// claim and must NOT be read as support for non-monotonic clocks.
	//
	// What it does pin is the DIRECTION of failure if the type were ever rewritten
	// onto a jumpable clock: time moving backwards can only make the budget look
	// LESS spent, never more, so it fails open (D-083 never traps the user's link)
	// and never denies a repair that is genuinely still within budget. Over-permitting
	// stays bounded regardless, because the D-089 count rule caps attempts at 3
	// independently of any clock.
	//
	// Note the fake needed NO new capability and its shape is unchanged: Advance
	// already takes a TimeSpan, and a negative TimeSpan rewinds it.
	[Fact]
	public void Evaluate_ClockRewoundBackInsideTheBudget_FailsOpenRatherThanDenying()
	{
		ManualTimeProvider time = new();
		RepairAttemptBudget budget = RepairAttemptBudget.Start(time, Budget).RecordAttempt();

		time.Advance(TimeSpan.FromMilliseconds(1500));
		_ = budget.Evaluate().Should().Be(RepairAttemptPolicy.DeniedTimeBudgetExhausted);

		time.Advance(TimeSpan.FromMilliseconds(-1000));

		_ = budget.Elapsed.Should().BeLessThan(Budget);
		_ = budget.Evaluate().Should().Be(RepairAttemptPolicy.Permitted);
	}

	// The pure overload: the caller supplies elapsed and no clock is consulted.
	[Theory]
	[InlineData(0, RepairAttemptPolicy.Permitted)]
	[InlineData(1499, RepairAttemptPolicy.Permitted)]
	[InlineData(1500, RepairAttemptPolicy.DeniedTimeBudgetExhausted)]
	[InlineData(9000, RepairAttemptPolicy.DeniedTimeBudgetExhausted)]
	public void Evaluate_WithSuppliedElapsed_IgnoresTheClock(int elapsedMs, RepairAttemptPolicy expected)
	{
		ManualTimeProvider time = new();
		RepairAttemptBudget budget = RepairAttemptBudget.Start(time, Budget);

		// The clock is moved well past the budget; the supplied value must win.
		time.Advance(TimeSpan.FromHours(1));

		_ = budget.Evaluate(TimeSpan.FromMilliseconds(elapsedMs)).Should().Be(expected);
	}

	// The count is the D-089 rule; the elapsed check is a pre-attempt latency gate on
	// top of it, not enforcement of the D-493 picker-open target.
	// When both are spent, the count is reported, so a log says why per the locked rule.
	[Fact]
	public void Evaluate_BothBoundsSpent_ReportsTheCountRule()
	{
		ManualTimeProvider time = new();
		RepairAttemptBudget budget = RepairAttemptBudget
			.Start(time, Budget)
			.RecordAttempt()
			.RecordAttempt()
			.RecordAttempt();

		time.Advance(TimeSpan.FromMinutes(1));

		_ = budget.Evaluate().Should().Be(RepairAttemptPolicy.DeniedAttemptsExhausted);
	}

	// Immutable value: booking an attempt must not mutate the caller's instance.
	[Fact]
	public void RecordAttempt_LeavesTheOriginalBudgetUntouched()
	{
		RepairAttemptBudget original = RepairAttemptBudget.Start(new ManualTimeProvider(), Budget);

		RepairAttemptBudget advanced = original.RecordAttempt();

		_ = original.AttemptsUsed.Should().Be(0);
		_ = advanced.AttemptsUsed.Should().Be(1);
	}

	[Fact]
	public void Start_NullTimeProvider_IsRejected()
	{
		Action act = () => _ = RepairAttemptBudget.Start(null!, Budget);

		_ = act.Should().Throw<ArgumentNullException>();
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void Start_NonPositiveTimeBudget_IsRejected(int milliseconds)
	{
		Action act = () => _ = RepairAttemptBudget.Start(
			new ManualTimeProvider(),
			TimeSpan.FromMilliseconds(milliseconds));

		_ = act.Should().Throw<ArgumentOutOfRangeException>();
	}

	// A hand-driven TimeProvider, so the budget can be tested without sleeping and
	// without a new package. TimestampFrequency is pinned to TimeSpan.TicksPerSecond
	// rather than inherited: the base default is Stopwatch.Frequency, which is a
	// property of the host machine, and these assertions must not depend on it.
	private sealed class ManualTimeProvider : TimeProvider
	{
		private long timestamp;

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override long GetTimestamp()
		{
			return timestamp;
		}

		public void Advance(TimeSpan by)
		{
			timestamp += by.Ticks;
		}
	}
}
