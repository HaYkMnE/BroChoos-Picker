using BroChoos.Core.Routing;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Routing;

public sealed class RuleEvaluatorTests
{
	[Fact]
	public void Evaluate_NoRules_ReturnsNoSuggestion()
	{
		RoutingTarget target = new("https://docs.example.com/path?x=1", "docs.example.com", "/path", "x=1");
		RuleSetDocument rules = new(1, []);

		RoutingDecision decision = RuleEvaluator.Evaluate(target, rules);

		_ = decision.HasSuggestion.Should().BeFalse();
		_ = decision.Suggestion.Should().BeNull();
	}

	[Fact]
	public void Evaluate_EnabledRulesSortedByOrder_FirstMatchWins()
	{
		RoutingTarget target = new("https://docs.example.com/download", "docs.example.com", "/download", null);
		RuleSetDocument rules = new(
			7,
			[
				new RoutingRule("r-late", 50, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
				new RoutingRule("r-first", 10, true, new RoutingTargetRef("edge-stable", "Profile 2"), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
			]);

		RoutingDecision decision = RuleEvaluator.Evaluate(target, rules);

		_ = decision.HasSuggestion.Should().BeTrue();
		_ = decision.Suggestion!.Target.BrowserId.Should().Be("edge-stable");
		_ = decision.Suggestion.Target.ProfileId.Should().Be("Profile 2");
		_ = decision.Suggestion.RuleId.Should().Be("r-first");
		_ = decision.Generation.Should().Be(7);
	}

	[Theory]
	[InlineData("evil-example.com")]
	[InlineData("notexample.com")]
	public void Evaluate_HostSuffixWithoutDotBoundary_DoesNotMatch(string host)
	{
		RoutingTarget target = new($"https://{host}/", host, "/", null);
		RuleSetDocument rules = new(
			5,
			[
				new RoutingRule("r-suffix", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostSuffix, "example.com")]),
			]);

		RoutingDecision decision = RuleEvaluator.Evaluate(target, rules);

		_ = decision.HasSuggestion.Should().BeFalse();
	}

	[Theory]
	[InlineData("example.com")]
	[InlineData("www.example.com")]
	[InlineData("deep.docs.example.com")]
	[InlineData("EXAMPLE.COM")]
	[InlineData("WWW.Example.Com")]
	public void Evaluate_HostSuffixWithDotBoundary_Matches(string host)
	{
		RoutingTarget target = new($"https://{host}/", host, "/", null);
		RuleSetDocument rules = new(
			6,
			[
				new RoutingRule("r-suffix", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostSuffix, "example.com")]),
			]);

		RoutingDecision decision = RuleEvaluator.Evaluate(target, rules);

		_ = decision.HasSuggestion.Should().BeTrue();
		_ = decision.Suggestion!.RuleId.Should().Be("r-suffix");
	}

	[Fact]
	public void Evaluate_DisabledRule_IsIgnored()
	{
		RoutingTarget target = new("https://docs.example.com/", "docs.example.com", "/", null);
		RuleSetDocument rules = new(
			2,
			[
				new RoutingRule("r-disabled", 1, false, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
				new RoutingRule("r-enabled", 2, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
			]);

		RoutingDecision decision = RuleEvaluator.Evaluate(target, rules);

		_ = decision.HasSuggestion.Should().BeTrue();
		_ = decision.Suggestion!.Target.BrowserId.Should().Be("chrome-stable");
		_ = decision.Suggestion.RuleId.Should().Be("r-enabled");
	}

	[Fact]
	public void Evaluate_AndWithinRule_AllConditionsMustMatch()
	{
		RoutingTarget target = new("https://docs.example.com/path?a=1", "docs.example.com", "/path", "a=1");
		RuleSetDocument rules = new(
			3,
			[
				new RoutingRule("r-and", 1, true, new RoutingTargetRef("edge-stable", null),
				[
					new RuleCondition(RuleMatchType.HostEquals, "docs.example.com"),
					new RuleCondition(RuleMatchType.PathPrefix, "/admin"),
				]),
			]);

		RoutingDecision decision = RuleEvaluator.Evaluate(target, rules);

		_ = decision.HasSuggestion.Should().BeFalse();
	}

	[Fact]
	public void Validate_DuplicateOrderIndex_ReturnsConflict()
	{
		RuleSetDocument rules = new(
			1,
			[
				new RoutingRule("r-1", 5, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
				new RoutingRule("r-2", 5, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
			]);

		RuleValidationResult validation = RuleEvaluator.Validate(rules);

		_ = validation.IsValid.Should().BeFalse();
		_ = validation.Errors.Should().Contain(e => e.Code == "rules.duplicate-order-index");
	}

	[Fact]
	public void Validate_DuplicateRuleId_ReturnsError()
	{
		RuleSetDocument rules = new(
			1,
			[
				new RoutingRule("r-dup", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
				new RoutingRule("r-dup", 2, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
			]);

		RuleValidationResult validation = RuleEvaluator.Validate(rules);

		_ = validation.IsValid.Should().BeFalse();
		_ = validation.Errors.Should().ContainSingle(e => e.Code == "rules.duplicate-rule-id");
	}

	// RuleId is an opaque identifier compared ordinally; this pins that decision.
	[Fact]
	public void Validate_RuleIdsDifferingOnlyByCase_AreDistinct()
	{
		RuleSetDocument rules = new(
			1,
			[
				new RoutingRule("R1", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
				new RoutingRule("r1", 2, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
			]);

		RuleValidationResult validation = RuleEvaluator.Validate(rules);

		_ = validation.Errors.Should().NotContain(e => e.Code == "rules.duplicate-rule-id");
	}

	[Fact]
	public void Validate_BlankRuleIds_ReportRequiredNotDuplicate()
	{
		RuleSetDocument rules = new(
			1,
			[
				new RoutingRule(" ", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
				new RoutingRule(" ", 2, true, new RoutingTargetRef("chrome-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
			]);

		RuleValidationResult validation = RuleEvaluator.Validate(rules);

		_ = validation.Errors.Should().Contain(e => e.Code == "rules.rule-id-required");
		_ = validation.Errors.Should().NotContain(e => e.Code == "rules.duplicate-rule-id");
	}

	[Fact]
	public void Validate_SourceAppCondition_IsRejectedInPhase0()
	{
		RuleSetDocument rules = new(
			1,
			[
				new RoutingRule("r-src", 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.SourceAppEquals, "Discord")]),
			]);

		RuleValidationResult validation = RuleEvaluator.Validate(rules);

		_ = validation.IsValid.Should().BeFalse();
		_ = validation.Errors.Should().Contain(e => e.Code == "rules.match-type-not-allowed");
	}

	[Fact]
	public void Validate_BenchmarkHook_AcceptsFiftyRules()
	{
		List<RoutingRule> rules = [];
		for (int i = 0; i < 50; i++)
		{
			rules.Add(new RoutingRule($"r-{i}", i + 1, true, new RoutingTargetRef("edge-stable", null), [new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]));
		}

		RuleValidationResult validation = RuleEvaluator.Validate(new RuleSetDocument(12, rules));

		_ = validation.IsValid.Should().BeTrue();
		_ = validation.Errors.Should().BeEmpty();
	}
}
