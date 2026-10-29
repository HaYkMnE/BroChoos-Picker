using BroChoos.Core.Storage;

namespace BroChoos.Core.Routing;

public sealed class RuleCache
{
	private readonly Lock _sync = new();
	private RuleSetDocument _snapshot = new(0, []);

	public RuleSetDocument GetSnapshot()
	{
		lock (_sync)
		{
			return _snapshot;
		}
	}

	public MutationCommitResult ReloadAfterCommit(RuleSetDocument next, long committedGeneration)
	{
		ArgumentNullException.ThrowIfNull(next);

		lock (_sync)
		{
			long priorGeneration = _snapshot.Generation;

			// THE DOCUMENT BEING INSTALLED IS WHAT THE CACHE WILL HOLD, SO ITS OWN GENERATION
			// IS THE ONE THAT MUST BE STRICTLY NEWER — not the caller's committedGeneration.
			// The guard used to compare committedGeneration against the snapshot and then
			// store `next`, whose Generation is a separate value: the two could disagree, and
			// when they did the snapshot moved BACKWARDS, leaving the cache holding rules
			// older than one it had already committed while every decision evaluated against
			// it looked current. `<=` rather than `<` because a repeated generation is not
			// idempotent here: two different rule sets would both claim the same generation,
			// and every generation-keyed decision downstream would be ambiguous about which
			// one it saw. Both a regression and a duplicate are a Conflict.
			if (next.Generation <= priorGeneration)
			{
				return MutationCommitResult.Conflict(priorGeneration);
			}

			// Two sources of truth for one event is what produced the defect above, so they
			// must at least agree: if the caller says generation X was committed while the
			// document claims Y, the cache cannot know what it holds, and storing either one
			// would report a generation that half the system does not believe. This is a
			// caller-consistency failure, not a newer document, so it is a Conflict too.
			if (committedGeneration != next.Generation)
			{
				return MutationCommitResult.Conflict(priorGeneration);
			}

			RuleValidationResult validation = RuleEvaluator.Validate(next);
			if (!validation.IsValid)
			{
				return MutationCommitResult.Conflict(priorGeneration);
			}

			_snapshot = next with { Rules = [.. next.Rules] };

			// next.Generation, NOT the caller's committedGeneration: after the checks above
			// they are equal, and reporting the value actually stored is what keeps this
			// result true even if those checks are ever weakened.
			return MutationCommitResult.Committed(next.Generation, priorGeneration);
		}
	}
}
