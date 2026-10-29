namespace BroChoos.Core.Routing;

public static class RuleEvaluator
{
	private static readonly HashSet<RuleMatchType> AllowedMatchTypes =
	[
		RuleMatchType.HostEquals,
		RuleMatchType.HostSuffix,
		RuleMatchType.PathPrefix,
		RuleMatchType.PathContains,
		RuleMatchType.QueryContains,
		RuleMatchType.UrlContains,
	];

	public static RuleValidationResult Validate(RuleSetDocument ruleSet)
	{
		List<RuleValidationError> errors = [];
		HashSet<int> orderIndexes = [];
		// Ordinal on purpose: RuleId is an opaque identifier, and nothing in the codebase
		// looks a rule up by id case-insensitively (it is only displayed and echoed back as
		// MatchedRuleId), so "R1" and "r1" are distinct ids and are not reported as duplicates.
		HashSet<string> ruleIds = new(StringComparer.Ordinal);

		foreach (RoutingRule rule in ruleSet.Rules)
		{
			if (string.IsNullOrWhiteSpace(rule.RuleId))
			{
				errors.Add(new RuleValidationError("rules.rule-id-required", rule.RuleId, "RuleId is required."));
			}
			else if (!ruleIds.Add(rule.RuleId))
			{
				errors.Add(new RuleValidationError("rules.duplicate-rule-id", rule.RuleId, "Duplicate RuleId is not allowed."));
			}

			if (rule.OrderIndex <= 0)
			{
				errors.Add(new RuleValidationError("rules.order-index-invalid", rule.RuleId, "OrderIndex must be positive."));
			}
			else if (!orderIndexes.Add(rule.OrderIndex))
			{
				errors.Add(new RuleValidationError("rules.duplicate-order-index", rule.RuleId, "Duplicate orderIndex is not allowed."));
			}

			if (rule.Conditions.Count == 0)
			{
				errors.Add(new RuleValidationError("rules.conditions-required", rule.RuleId, "At least one condition is required."));
			}

			if (string.IsNullOrWhiteSpace(rule.Target.BrowserId))
			{
				errors.Add(new RuleValidationError("rules.target-browser-required", rule.RuleId, "Target BrowserId is required."));
			}

			foreach (RuleCondition condition in rule.Conditions)
			{
				if (!AllowedMatchTypes.Contains(condition.MatchType))
				{
					errors.Add(new RuleValidationError("rules.match-type-not-allowed", rule.RuleId, $"Match type '{condition.MatchType}' is not allowed in Phase 0."));
				}

				if (string.IsNullOrWhiteSpace(condition.Value))
				{
					errors.Add(new RuleValidationError("rules.condition-value-required", rule.RuleId, "Condition value is required."));
				}
			}
		}

		return new RuleValidationResult(errors.Count == 0, errors);
	}

	public static RoutingDecision Evaluate(RoutingTarget target, RuleSetDocument ruleSet)
	{
		RuleValidationResult validation = Validate(ruleSet);
		if (!validation.IsValid)
		{
			return new RoutingDecision(ruleSet.Generation, null);
		}

		RoutingRule? matched = ruleSet.Rules
			.Where(r => r.Enabled)
			.OrderBy(r => r.OrderIndex)
			.FirstOrDefault(rule => RuleMatches(target, rule));

		if (matched is null)
		{
			return new RoutingDecision(ruleSet.Generation, null);
		}

		RoutingSuggestion suggestion = new(
			matched.RuleId,
			matched.Target,
			$"Matched rule '{matched.RuleId}' by orderIndex {matched.OrderIndex}.");

		return new RoutingDecision(ruleSet.Generation, suggestion);
	}

	private static bool RuleMatches(RoutingTarget target, RoutingRule rule)
	{
		foreach (RuleCondition condition in rule.Conditions)
		{
			if (!ConditionMatches(target, condition))
			{
				return false;
			}
		}

		return true;
	}

	private static bool ConditionMatches(RoutingTarget target, RuleCondition condition)
	{
		return condition.MatchType switch
		{
			RuleMatchType.HostEquals => string.Equals(target.Host, condition.Value, StringComparison.OrdinalIgnoreCase),
			RuleMatchType.HostSuffix => MatchesHostSuffix(target.Host, condition.Value),
			RuleMatchType.PathPrefix => target.Path.StartsWith(condition.Value, StringComparison.Ordinal),
			RuleMatchType.PathContains => target.Path.Contains(condition.Value, StringComparison.Ordinal),
			RuleMatchType.QueryContains => (target.Query ?? string.Empty).Contains(condition.Value, StringComparison.Ordinal),
			RuleMatchType.UrlContains => target.Url.Contains(condition.Value, StringComparison.Ordinal),
			RuleMatchType.SourceAppEquals => false,
			_ => false,
		};
	}

	// A suffix rule must match a whole host-label suffix, never a mere string suffix: "example.com"
	// admits "example.com" and "www.example.com" but must NOT admit "evil-example.com" or
	// "notexample.com" (attacker-registrable lookalikes that would otherwise hijack routing). The
	// boundary is the dot separating the label the condition names from the rest of the host.
	//
	// A leading dot in the configured value is normalized away: ".example.com" and "example.com"
	// denote the SAME host suffix, so both spellings behave identically instead of the dotted form
	// silently never matching (no host starts with "."). Comparison stays case-insensitive, matching
	// HostEquals and RoutingTargetFactory's lower-cased host.
	private static bool MatchesHostSuffix(string host, string suffix)
	{
		string normalized = suffix.TrimStart('.');
		return normalized.Length > 0
			&& (string.Equals(host, normalized, StringComparison.OrdinalIgnoreCase)
				|| host.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase));
	}
}
