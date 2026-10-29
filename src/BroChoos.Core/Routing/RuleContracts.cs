namespace BroChoos.Core.Routing;

public enum RuleMatchType
{
	HostEquals,
	HostSuffix,
	PathPrefix,
	PathContains,
	QueryContains,
	UrlContains,
	SourceAppEquals,
}

public sealed record RuleCondition(RuleMatchType MatchType, string Value);

public sealed record RoutingTargetRef(string BrowserId, string? ProfileId);

public sealed record RoutingRule(
	string RuleId,
	int OrderIndex,
	bool Enabled,
	RoutingTargetRef Target,
	IReadOnlyList<RuleCondition> Conditions);

public sealed record RuleSetDocument(long Generation, IReadOnlyList<RoutingRule> Rules);

public sealed record RoutingTarget(
	string Url,
	string Host,
	string Path,
	string? Query);

public sealed record RoutingSuggestion(
	string RuleId,
	RoutingTargetRef Target,
	string Reason);

public sealed record RoutingDecision(
	long Generation,
	RoutingSuggestion? Suggestion)
{
	public bool HasSuggestion => Suggestion is not null;
}

public sealed record RuleValidationError(
	string Code,
	string RuleId,
	string Message);

public sealed record RuleValidationResult(
	bool IsValid,
	IReadOnlyList<RuleValidationError> Errors);
