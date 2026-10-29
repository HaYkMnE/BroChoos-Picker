using BroChoos.Core.Routing;
using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Storage;

// W7.1: the rules document is a generation-bearing config document, so it must obey the
// same envelope and additive-revision discipline every other W3 document obeys. These are
// the Core-side facts (shape, generation carrier, projection into the evaluator's
// RuleSetDocument). The persistence/CAS facts live in the platform store tests.
public sealed class RulesDocumentSerializationTests
{
	[Fact]
	public void RulesDocument_CarriesSchemaEnvelopeAndGeneration()
	{
		RulesDocument document = new(
			[
				new RoutingRule("work-domains", 1, true, new RoutingTargetRef("chrome-stable", "Profile 1"),
					[new RuleCondition(RuleMatchType.HostEquals, "docs.example.com")]),
			]);

		_ = document.SchemaVersion.Should().Be(SchemaVersioning.Version);
		_ = document.SchemaRevision.Should().Be(SchemaVersioning.Revision);
		_ = document.Generation.Should().Be(0);
		_ = document.Rules.Should().ContainSingle();
	}

	[Fact]
	public void RulesDocument_ProjectsToRuleSetDocument_PreservingGenerationAndRules()
	{
		RoutingRule rule = new(
			"r1", 7, true, new RoutingTargetRef("edge-stable", null),
			[new RuleCondition(RuleMatchType.HostSuffix, "example.com")]);
		RulesDocument document = new([rule]) { Generation = 42 };

		RuleSetDocument ruleSet = document.ToRuleSet();

		_ = ruleSet.Generation.Should().Be(42);
		_ = ruleSet.Rules.Should().ContainSingle();
		_ = ruleSet.Rules[0].RuleId.Should().Be("r1");
		_ = ruleSet.Rules[0].OrderIndex.Should().Be(7);
	}

	[Fact]
	public void RulesDocument_EmptyRules_IsAValidDocument()
	{
		RulesDocument document = new([]);

		_ = document.ToRuleSet().Rules.Should().BeEmpty();
	}
}
