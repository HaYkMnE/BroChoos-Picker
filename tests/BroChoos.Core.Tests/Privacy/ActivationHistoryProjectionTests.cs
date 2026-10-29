using System.Globalization;
using BroChoos.Core.Ingress;
using BroChoos.Core.Privacy;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Privacy;

public sealed class ActivationHistoryProjectionTests
{
	[Fact]
	public void Project_AllowlistOnly_UsesMainDomainAndConfirmedSourceOnly()
	{
		ActivationHistoryRecord? record = ActivationHistoryProjection.Project(new ActivationHistoryProjectionInput(
			DateTimeOffset.Parse("2026-01-01T12:00:00Z", CultureInfo.InvariantCulture),
			"https://sub.example.co.uk/path?a=1#frag",
			"edge-stable",
			"Profile 7",
			SourceEvidence.Confirmed(@"C:\\Program Files\\Google\\Chrome\\chrome.exe"),
			"work-domains"));

		_ = record.Should().NotBeNull();
		_ = record!.MainDomain.Should().Be("example.co.uk");
		_ = record.BrowserId.Should().Be("edge-stable");
		_ = record.ProfileId.Should().Be("Profile 7");
		_ = record.ConfirmedSourceApp.Should().Be("chrome.exe");
		_ = record.MatchedRuleId.Should().Be("work-domains");
	}

	[Fact]
	public void Project_ProbableOrUnknownSource_DoesNotPersistSource()
	{
		ActivationHistoryRecord? probable = ActivationHistoryProjection.Project(new ActivationHistoryProjectionInput(
			DateTimeOffset.UtcNow,
			"https://example.com/",
			"edge-stable",
			null,
			SourceEvidence.Probable("firefox.exe"),
			null));

		ActivationHistoryRecord? unknown = ActivationHistoryProjection.Project(new ActivationHistoryProjectionInput(
			DateTimeOffset.UtcNow,
			"https://example.com/",
			"edge-stable",
			null,
			SourceEvidence.Unknown("mystery"),
			null));

		_ = probable!.ConfirmedSourceApp.Should().BeNull();
		_ = unknown!.ConfirmedSourceApp.Should().BeNull();
	}

	[Theory]
	[InlineData("http://localhost:8080/test")]
	[InlineData("http://127.0.0.1/test")]
	[InlineData("http://10.1.2.3/test")]
	[InlineData("http://172.16.1.20/test")]
	[InlineData("http://192.168.0.10/test")]
	[InlineData("http://169.254.1.2/test")]
	[InlineData("http://[::1]/test")]
	[InlineData("http://[fd00::1]/test")]
	[InlineData("http://[fe80::1]/test")]
	[InlineData("https://device.local/test")]
	[InlineData("https://service.internal/test")]
	[InlineData("https://app.localhost/test")]
	[InlineData("https://APP.LOCALHOST/test")]
	[InlineData("http://[::ffff:127.0.0.1]/test")]
	[InlineData("http://[::ffff:10.1.2.3]/test")]
	[InlineData("http://[::ffff:192.168.0.10]/test")]
	[InlineData("http://[::ffff:172.16.1.20]/test")]
	[InlineData("http://[::ffff:169.254.1.2]/test")]
	[InlineData("http://0.0.0.0/test")]
	[InlineData("http://0.1.2.3/test")]
	[InlineData("http://100.64.0.1/test")]
	[InlineData("http://100.127.255.254/test")]
	[InlineData("http://[::ffff:100.64.0.1]/test")]
	[InlineData("http://[64:ff9b::7f00:1]/test")]
	[InlineData("http://[64:ff9b::a01:203]/test")]
	[InlineData("http://[64:ff9b::c0a8:1]/test")]
	[InlineData("http://[2002:7f00:1::1]/test")]
	[InlineData("http://[2002:a01:203::1]/test")]
	[InlineData("http://[2002:c0a8:1::]/test")]
	public void Project_PrivateLocalInternal_ReturnsNull(string url)
	{
		ActivationHistoryRecord? record = ActivationHistoryProjection.Project(new ActivationHistoryProjectionInput(
			DateTimeOffset.UtcNow,
			url,
			"edge-stable",
			null,
			SourceEvidence.Unknown(),
			null));

		_ = record.Should().BeNull();
	}

	[Theory]
	[InlineData("https://shop.example.co.in/x", "example.co.in")]
	[InlineData("https://a.b.example.com.cn/x", "example.com.cn")]
	[InlineData("https://www.example.com/x", "example.com")]
	[InlineData("http://[::ffff:8.8.8.8]/x", "[::ffff:8.8.8.8]")]
	[InlineData("http://100.63.255.255/x", "100.63.255.255")]
	[InlineData("http://100.128.0.1/x", "100.128.0.1")]
	[InlineData("http://8.8.8.8/x", "8.8.8.8")]
	[InlineData("http://[64:ff9b::808:808]/x", "[64:ff9b::808:808]")]
	[InlineData("http://[2002:808:808::1]/x", "[2002:808:808::1]")]
	public void Project_MainDomain_ResolvesSuffixesAndPublicMappedAddresses(string url, string expected)
	{
		ActivationHistoryRecord? record = ActivationHistoryProjection.Project(new ActivationHistoryProjectionInput(
			DateTimeOffset.UtcNow,
			url,
			"edge-stable",
			null,
			SourceEvidence.Unknown(),
			null));

		_ = record.Should().NotBeNull();
		_ = record!.MainDomain.Should().Be(expected);
	}
}
