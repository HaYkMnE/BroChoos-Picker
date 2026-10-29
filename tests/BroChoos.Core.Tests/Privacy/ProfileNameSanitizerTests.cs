using BroChoos.Core.Privacy;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Privacy;

public sealed class ProfileNameSanitizerTests
{
	[Theory]
	[InlineData(null, "")]
	[InlineData("", "")]
	[InlineData("  Work  ", "Work")]
	[InlineData("Wo\u0007rk\n", "Work")]
	[InlineData("\u202Egnp.exe", "gnp.exe")]
	[InlineData("A\u200Eb\u2066c\u2069\u061C", "Abc")]
	public void Sanitize_TrimsAndRemovesControlAndBidiCharacters(string? input, string expected)
	{
		_ = ProfileNameSanitizer.Sanitize(input).Should().Be(expected);
	}

	[Theory]
	[InlineData("Wo\u200Brk", "Work")]
	[InlineData("Wo\u2060rk", "Work")]
	[InlineData("\uFEFFWork", "Work")]
	[InlineData("Work\u2028Home\u2029", "WorkHome")]
	public void Sanitize_RemovesInvisibleSeparators(string input, string expected)
	{
		_ = ProfileNameSanitizer.Sanitize(input).Should().Be(expected);
	}

	[Fact]
	public void Sanitize_KeepsZeroWidthJoinerAndNonJoiner()
	{
		// Family emoji (ZWJ sequence) and a Persian word with ZWNJ must survive unchanged.
		string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
		string persian = "\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645";

		_ = ProfileNameSanitizer.Sanitize(family).Should().Be(family);
		_ = ProfileNameSanitizer.Sanitize(persian).Should().Be(persian);
	}

	[Fact]
	public void Sanitize_CutsLongNamesTo64()
	{
		_ = ProfileNameSanitizer.Sanitize(new string('x', 100)).Should().HaveLength(64);
	}

	[Fact]
	public void Sanitize_NeverSplitsASurrogatePair()
	{
		// 63 letters + one emoji (2 UTF-16 units) = 65: the cut must drop the whole emoji.
		string result = ProfileNameSanitizer.Sanitize(new string('x', 63) + "\U0001F600");

		_ = result.Should().Be(new string('x', 63));
		_ = char.IsHighSurrogate(result[^1]).Should().BeFalse();
	}
}
