using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Storage;

// Pins the D-064/D-066/D-070 decision matrix: observed (schemaVersion,
// schemaRevision) versus the expected (1, 0).
public sealed class SchemaCompatibilityEvaluatorTests
{
	[Theory]
	[InlineData(1, 0, SchemaCompatibility.CompletelyCompatible)]
	[InlineData(1, 1, SchemaCompatibility.NewerRevisionReadKnownOnly)]
	[InlineData(0, 0, SchemaCompatibility.OlderVersionMigrationRequired)]
	[InlineData(2, 0, SchemaCompatibility.NewerVersionReadOnly)]
	[InlineData(1, -1, SchemaCompatibility.Invalid)]
	[InlineData(-1, 0, SchemaCompatibility.Invalid)]
	public void Evaluate_ObservedEnvelope_MapsToExpectedOutcome(int version, int revision, SchemaCompatibility expected)
	{
		_ = SchemaCompatibilityEvaluator.Evaluate(version, revision).Should().Be(expected);
	}

	// D-064: same version + any newer revision is the additive / read-known-only
	// case, however far the revision has moved.
	[Fact]
	public void Evaluate_SameVersionAnyNewerRevision_IsReadKnownOnly()
	{
		_ = SchemaCompatibilityEvaluator.Evaluate(1, 7).Should().Be(SchemaCompatibility.NewerRevisionReadKnownOnly);
	}

	// D-066: a newer major version wins regardless of revision — read-only, never
	// overwrite.
	[Fact]
	public void Evaluate_NewerVersionAnyRevision_IsReadOnly()
	{
		_ = SchemaCompatibilityEvaluator.Evaluate(2, 99).Should().Be(SchemaCompatibility.NewerVersionReadOnly);
	}

	// D-064: an older major version requires migration regardless of revision.
	[Fact]
	public void Evaluate_OlderVersionAnyRevision_IsMigrationRequired()
	{
		_ = SchemaCompatibilityEvaluator.Evaluate(0, 5).Should().Be(SchemaCompatibility.OlderVersionMigrationRequired);
	}

	[Fact]
	public void Evaluate_FromDocumentEnvelope_ReadsItsSchemaFields()
	{
		ConfigDocument current = new AppDocument();
		ConfigDocument newerRevision = new AppDocument { SchemaRevision = 1 };
		ConfigDocument olderVersion = new AppDocument { SchemaVersion = 0 };
		ConfigDocument newerVersion = new AppDocument { SchemaVersion = 2 };

		_ = SchemaCompatibilityEvaluator.Evaluate(current).Should().Be(SchemaCompatibility.CompletelyCompatible);
		_ = SchemaCompatibilityEvaluator.Evaluate(newerRevision).Should().Be(SchemaCompatibility.NewerRevisionReadKnownOnly);
		_ = SchemaCompatibilityEvaluator.Evaluate(olderVersion).Should().Be(SchemaCompatibility.OlderVersionMigrationRequired);
		_ = SchemaCompatibilityEvaluator.Evaluate(newerVersion).Should().Be(SchemaCompatibility.NewerVersionReadOnly);
	}
}
