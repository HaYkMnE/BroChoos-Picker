using BroChoos.Platform.Windows.Urls;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Urls;

public sealed class DefaultUrlValidatorTests
{
	[Theory]
	[InlineData("https://example.com")]
	[InlineData("http://example.com/path")]
	public void IsAcceptable_AcceptsHttpAndHttps(string url)
	{
		DefaultUrlValidator validator = new();

		bool result = validator.IsAcceptable(url, out string? normalized);

		_ = result.Should().BeTrue();
		_ = normalized.Should().NotBeNullOrWhiteSpace();
	}

	[Theory]
	[InlineData("javascript:alert(1)")]
	[InlineData("file:///C:/test.html")]
	[InlineData("not-a-url")]
	public void IsAcceptable_RejectsNonWebUrls(string url)
	{
		DefaultUrlValidator validator = new();

		bool result = validator.IsAcceptable(url, out _);

		_ = result.Should().BeFalse();
	}

	// 0A-5: the half of CliParser's dash rule that only the REAL validator can pin.
	// CliParser.LooksLikeOption deliberately does not treat a single-dash token as an
	// option, so "--portable-data -x" keeps "-x" as a data root; the same "-x" as a
	// POSITIONAL must still be refused. CliParserTests covers the value side with a
	// fake validator that accepts anything, so this is where the refusal is measured.
	// Nothing dash-leading can pass the Uri.TryCreate(Absolute) gate above: "-" is not
	// a legal scheme character, so even "-x:foo" — which LOOKS scheme-shaped — has no
	// scheme at all and never reaches the http/https check.
	[Theory]
	[InlineData("-x")]
	[InlineData("-")]
	[InlineData("-portable-data")]
	[InlineData("-h")]
	[InlineData("-x:foo")]
	public void IsAcceptable_RejectsDashLeadingTokens(string token)
	{
		DefaultUrlValidator validator = new();

		bool result = validator.IsAcceptable(token, out string normalized);

		_ = result.Should().BeFalse();
		_ = normalized.Should().BeEmpty();
	}
}
