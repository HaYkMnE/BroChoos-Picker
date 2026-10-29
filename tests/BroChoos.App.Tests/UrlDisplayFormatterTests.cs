using BroChoos.App.Picker;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// D-125 zone 1: host + short readable URL; long query strings collapse by default.
// The formatter is pure string math on an already-validated absolute URL, so no
// UI thread and no window are involved anywhere below.
public sealed class UrlDisplayFormatterTests
{
	[Fact]
	public void Format_ShortUrl_KeptWholeWithHost()
	{
		LinkCardDisplay display = UrlDisplayFormatter.Format("https://example.com/");

		_ = display.Host.Should().Be("example.com");
		_ = display.ShortUrl.Should().Be("https://example.com/");
		_ = display.IsCollapsed.Should().BeFalse();
	}

	[Fact]
	public void Format_ShortQuery_KeptWhenItFits()
	{
		LinkCardDisplay display = UrlDisplayFormatter.Format("https://example.com/s?q=1");

		_ = display.ShortUrl.Should().Be("https://example.com/s?q=1");
		_ = display.IsCollapsed.Should().BeFalse();
	}

	// The collapse is the D-125 requirement, and the query tail must actually be
	// gone from the card: an ellipsis is no privacy boundary if it follows it.
	[Fact]
	public void Format_LongQuery_CollapsedBehindPlaceholder()
	{
		string url = "https://example.com/search?query=abcdefghijklmnopqrstuvwxyz0123456789&tracking=utm_abcdefgh";

		LinkCardDisplay display = UrlDisplayFormatter.Format(url);

		_ = display.Host.Should().Be("example.com");
		_ = display.ShortUrl.Should().Be("https://example.com/search?…");
		_ = display.IsCollapsed.Should().BeTrue();
		_ = display.ShortUrl.Should().NotContain("tracking");
		_ = display.ShortUrl.Should().NotContain("utm_abcdefgh");
	}

	[Fact]
	public void Format_VeryLongPath_Truncated()
	{
		string url = "https://example.com/" + new string('a', 200) + "/end";

		LinkCardDisplay display = UrlDisplayFormatter.Format(url);

		// Exact bound (strengthened from a loose <=65): 63 kept chars + one ellipsis,
		// pinned content so an off-by-one in the truncation cannot stay green.
		_ = display.ShortUrl.Should().Be("https://example.com/" + new string('a', 43) + "…");
		_ = display.ShortUrl.Length.Should().Be(64);
		_ = display.IsCollapsed.Should().BeTrue();
	}

	// Defensive only: real input is validated absolute http(s) before this point.
	[Fact]
	public void Format_NonAbsolute_FallsBackToRawText()
	{
		LinkCardDisplay display = UrlDisplayFormatter.Format("not-a-uri");

		_ = display.Host.Should().Be("not-a-uri");
		_ = display.ShortUrl.Should().Be("not-a-uri");
		_ = display.IsCollapsed.Should().BeFalse();
	}
}
