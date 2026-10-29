using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

public sealed class ProtectedCommitTests
{
	[Theory]
	[InlineData(AtomicWriteStage.Validated, false)]
	[InlineData(AtomicWriteStage.TempFlushed, false)]
	[InlineData(AtomicWriteStage.LockAcquired, true)]
	[InlineData(AtomicWriteStage.BeforePublish, true)]
	[InlineData(AtomicWriteStage.AfterPublish, true)]
	[InlineData(AtomicWriteStage.AfterPrune, true)]
	[InlineData(AtomicWriteStage.AfterVerify, true)]
	public void IsProtectedCommitStage_MatchesTheW33ProtectedWindow(
		AtomicWriteStage stage,
		bool expected)
	{
		_ = AtomicJsonFileStore.IsProtectedCommitStage(stage).Should().Be(expected);
	}

	[Fact]
	public void ProtectedCommitStages_AreContiguousFromLockAcquiredThroughAfterVerify()
	{
		AtomicWriteStage[] expected =
		[
			AtomicWriteStage.LockAcquired,
			AtomicWriteStage.BeforePublish,
			AtomicWriteStage.AfterPublish,
			AtomicWriteStage.AfterPrune,
			AtomicWriteStage.AfterVerify,
		];

		_ = AtomicJsonFileStore.ProtectedCommitStages.Should().Equal(expected);
	}
}
