using BroChoos.Core.Routing;
using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Routing;

public sealed class RuleCacheTests
{
	[Fact]
	public void Reload_ValidRules_IsAtomicAndImmutable()
	{
		RuleCache cache = new();
		RuleSetDocument first = new(1, [new RoutingRule("r1", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);
		RuleSetDocument second = new(2, [new RoutingRule("r2", 1, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "example.com")])]);

		MutationCommitResult firstCommit = cache.ReloadAfterCommit(first, 1);
		RuleSetDocument snapshotAfterFirst = cache.GetSnapshot();
		MutationCommitResult secondCommit = cache.ReloadAfterCommit(second, 2);
		RuleSetDocument snapshotAfterSecond = cache.GetSnapshot();

		_ = firstCommit.Status.Should().Be(MutationCommitStatus.Committed);
		_ = secondCommit.Status.Should().Be(MutationCommitStatus.Committed);
		_ = snapshotAfterFirst.Generation.Should().Be(1);
		_ = snapshotAfterSecond.Generation.Should().Be(2);
		_ = snapshotAfterFirst.Rules[0].RuleId.Should().Be("r1");
		_ = snapshotAfterSecond.Rules[0].RuleId.Should().Be("r2");
	}

	[Fact]
	public void Reload_InvalidRules_PreservesPreviousSnapshot()
	{
		RuleCache cache = new();
		RuleSetDocument good = new(3, [new RoutingRule("r-good", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);
		RuleSetDocument bad = new(4,
		[
			new RoutingRule("r-a", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
			new RoutingRule("r-b", 1, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
		]);

		_ = cache.ReloadAfterCommit(good, 3);
		MutationCommitResult badCommit = cache.ReloadAfterCommit(bad, 4);
		RuleSetDocument snapshot = cache.GetSnapshot();

		_ = badCommit.Status.Should().Be(MutationCommitStatus.Conflict);
		_ = snapshot.Generation.Should().Be(3);
		_ = snapshot.Rules[0].RuleId.Should().Be("r-good");
	}

	[Fact]
	public void Reload_OlderCommittedGeneration_IsIgnoredAsConflict()
	{
		RuleCache cache = new();
		RuleSetDocument v5 = new(5, [new RoutingRule("r5", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);
		RuleSetDocument v4 = new(4, [new RoutingRule("r4", 1, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);

		_ = cache.ReloadAfterCommit(v5, 5);
		MutationCommitResult staleCommit = cache.ReloadAfterCommit(v4, 4);
		RuleSetDocument snapshot = cache.GetSnapshot();

		_ = staleCommit.Status.Should().Be(MutationCommitStatus.Conflict);
		_ = snapshot.Generation.Should().Be(5);
		_ = snapshot.Rules[0].RuleId.Should().Be("r5");
	}

	// THE GUARD COMPARED THE WRONG GENERATION. It tested committedGeneration against the
	// SNAPSHOT's generation but then stored `next`, whose own Generation is a separate value
	// the caller supplies — so the snapshot could move BACKWARDS: the cache ends up holding a
	// document older than one it already committed, and every decision evaluated against it
	// is based on superseded rules. The three tests below pin the three ways that happened.
	[Fact]
	public void Reload_NextDocumentGenerationOlderThanCommitted_IsRejectedAsConflict()
	{
		RuleCache cache = new();
		RuleSetDocument newer = new(7, [new RoutingRule("r7", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);
		// The caller reports generation 7 as committed — the generation the cache already
		// holds — but hands over a document that claims generation 6. The old guard compared
		// only the caller's number against the snapshot, saw 7 >= 7 and stored the document,
		// moving the cache backwards; the document's own generation was never consulted.
		RuleSetDocument older = new(6, [new RoutingRule("r6", 1, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);

		_ = cache.ReloadAfterCommit(newer, 7);
		MutationCommitResult regression = cache.ReloadAfterCommit(older, 7);
		RuleSetDocument snapshot = cache.GetSnapshot();

		_ = regression.Status.Should().Be(MutationCommitStatus.Conflict);
		_ = snapshot.Generation.Should().Be(7);
		_ = snapshot.Rules[0].RuleId.Should().Be("r7");
	}

	[Fact]
	public void Reload_NextDocumentSupersededByNewerSnapshot_IsRejectedAsConflict()
	{
		RuleCache cache = new();
		RuleSetDocument first = new(1, [new RoutingRule("r1", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);
		RuleSetDocument third = new(3, [new RoutingRule("r3", 1, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);
		// A reload prepared against generation 2 arrives after generation 3 was committed. Its
		// committedGeneration (3) equals the snapshot's 3, so the old guard admitted it and the
		// cache fell from 3 back to 2 — the superseded rules silently became current.
		RuleSetDocument superseded = new(2, [new RoutingRule("r2", 1, true, new RoutingTargetRef("firefox-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);

		_ = cache.ReloadAfterCommit(first, 1);
		_ = cache.ReloadAfterCommit(third, 3);
		MutationCommitResult lateCommit = cache.ReloadAfterCommit(superseded, 3);
		RuleSetDocument snapshot = cache.GetSnapshot();

		_ = lateCommit.Status.Should().Be(MutationCommitStatus.Conflict);
		_ = snapshot.Generation.Should().Be(3);
		_ = snapshot.Rules[0].RuleId.Should().Be("r3");
	}

	// Committing generation 3 twice is not idempotent here: the second commit carries no new
	// information, and accepting it would let two different rule sets claim the same
	// generation, which makes every generation-based decision downstream ambiguous. A
	// duplicate is therefore a Conflict, and the first document survives.
	[Fact]
	public void Reload_SameGenerationCommittedTwice_IsRejectedAsConflict_AndKeepsFirstDocument()
	{
		RuleCache cache = new();
		RuleSetDocument first = new(4, [new RoutingRule("r-first", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);
		RuleSetDocument second = new(4, [new RoutingRule("r-second", 1, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")])]);

		MutationCommitResult firstCommit = cache.ReloadAfterCommit(first, 4);
		MutationCommitResult duplicateCommit = cache.ReloadAfterCommit(second, 4);
		RuleSetDocument snapshot = cache.GetSnapshot();

		_ = firstCommit.Status.Should().Be(MutationCommitStatus.Committed);
		_ = duplicateCommit.Status.Should().Be(MutationCommitStatus.Conflict);
		_ = snapshot.Generation.Should().Be(4);
		_ = snapshot.Rules[0].RuleId.Should().Be("r-first");
	}
}
