using BroChoos.Core.Routing;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Routing;

// W7.2: the picker must turn the run's URL into the evaluator's RoutingTarget exactly once,
// and it must do so from the URL's own components — never by re-parsing a display string.
public sealed class RoutingTargetFactoryTests
{
	[Fact]
	public void FromUrl_SplitsHostPathAndQuery()
	{
		RoutingTarget? target = RoutingTargetFactory.FromUrl("https://Docs.Example.com/Path/Sub?x=1&y=2");

		_ = target.Should().NotBeNull();
		_ = target!.Host.Should().Be("docs.example.com", "the host is compared case-insensitively and is normalized once");
		_ = target.Path.Should().Be("/Path/Sub", "path matching is case-sensitive, so case must be preserved");
		_ = target.Query.Should().Be("x=1&y=2");
		_ = target.Url.Should().Be("https://Docs.Example.com/Path/Sub?x=1&y=2", "URL-contains matching operates on the original spelling");
	}

	[Fact]
	public void FromUrl_NoQuery_LeavesQueryNull()
	{
		RoutingTarget? target = RoutingTargetFactory.FromUrl("https://example.com/a");

		_ = target.Should().NotBeNull();
		_ = target!.Query.Should().BeNull();
		_ = target.Path.Should().Be("/a");
	}

	[Fact]
	public void FromUrl_EmptyPath_IsRoot()
	{
		RoutingTarget? target = RoutingTargetFactory.FromUrl("https://example.com");

		_ = target.Should().NotBeNull();
		_ = target!.Path.Should().Be("/");
	}

	[Theory]
	[InlineData("not a url")]
	[InlineData("file:///C:/secret.txt")]
	[InlineData("javascript:alert(1)")]
	[InlineData("")]
	public void FromUrl_NonHttpOrUnparseable_ReturnsNull(string url)
	{
		_ = RoutingTargetFactory.FromUrl(url).Should().BeNull();
	}
}
