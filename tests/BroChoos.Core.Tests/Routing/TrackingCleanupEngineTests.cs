using BroChoos.Core.Routing;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Routing;

public sealed class TrackingCleanupEngineTests
{
	[Fact]
	public void Cleanup_DefaultOff_LeavesUrlUnchanged()
	{
		const string url = "https://example.com/path?utm_source=newsletter#frag";

		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url);

		_ = result.Changed.Should().BeFalse();
		_ = result.EffectiveUrl.Should().Be(url);
		_ = result.ReasonCode.Should().Be("cleanup.disabled");
	}

	[Fact]
	public void Cleanup_RemovesKnownTrackingQueryKeys_AndPreservesPathFragmentAndEncoding()
	{
		const string url = "https://example.com/a%2Fb?utm_source=news&x=1&gclid=abc+123#section-1";

		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

		_ = result.Changed.Should().BeTrue();
		_ = result.EffectiveUrl.Should().Be("https://example.com/a%2Fb?x=1#section-1");
		_ = result.RemovedQueryParameterKeys.Should().Contain(["utm_source", "gclid"]);
		_ = result.CanUseOriginalForThisLaunch.Should().BeTrue();
	}

	[Fact]
	public void Cleanup_PreservesHardProtectedAndAffiliateKeysByDefault()
	{
		const string url = "https://shop.example.com/checkout?code=abc123&state=xyz&ref=partner&utm_campaign=sale";

		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

		_ = result.Changed.Should().BeTrue();
		_ = result.EffectiveUrl.Should().Be("https://shop.example.com/checkout?code=abc123&state=xyz&ref=partner");
		_ = result.RemovedQueryParameterKeys.Should().Equal("utm_campaign");
	}

	[Fact]
	public void Cleanup_BypassesKnownSsoOrOAuthEndpoints()
	{
		const string url = "https://accounts.google.com/o/oauth2/v2/auth?utm_source=mail&state=abc";

		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

		_ = result.Changed.Should().BeFalse();
		_ = result.IsBypassed.Should().BeTrue();
		_ = result.ReasonCode.Should().Be("cleanup.sso-bypass");
		_ = result.EffectiveUrl.Should().Be(url);
	}

	[Theory]
	[InlineData("mailto:test@example.com")]
	[InlineData("ftp://example.com/file?utm_source=x")]
	public void Cleanup_NonHttpSchemes_AreLeftUnchanged(string url)
	{
		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

		_ = result.Changed.Should().BeFalse();
		_ = result.ReasonCode.Should().Be("cleanup.non-http");
	}

	[Fact]
	public void Cleanup_MalformedPercentEncoding_FailsUnchanged()
	{
		const string url = "https://example.com/path?utm_source=a%ZZ&x=1";

		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

		_ = result.Changed.Should().BeFalse();
		_ = result.ReasonCode.Should().Be("cleanup.malformed");
		_ = result.EffectiveUrl.Should().Be(url);
	}

	[Fact]
	public void Cleanup_OverLimitInput_IsLeftUnchanged()
	{
		string longUrl = "https://example.com/?x=" + new string('a', 50);

		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(
			longUrl,
			new TrackingCleanupOptions(Enabled: true, MaxInputLength: 32));

		_ = result.Changed.Should().BeFalse();
		_ = result.ReasonCode.Should().Be("cleanup.over-limit");
	}

	// A host suffix must match a whole host-label boundary. "okta.com" admits "okta.com" and
	// "acme.okta.com" but must NOT admit "evilokta.com" or "notokta.com": those are
	// attacker-registrable lookalikes that would otherwise inherit a FREE SSO/OAuth bypass and
	// keep their tracked parameters (`utm_*`, `gclid`, ...) uncleaned — the dangerous direction.
	[Theory]
	[InlineData("https://evilokta.com/?utm_source=x")]
	[InlineData("https://notokta.com/?utm_source=x")]
	[InlineData("https://evilokta.com/?gclid=TRACKER")]
	public void Cleanup_HostSuffixLookalike_DoesNotGetFreeSsoBypass(string url)
	{
		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

		_ = result.IsBypassed.Should().BeFalse();
		_ = result.Changed.Should().BeTrue();
	}

	// "accounts.google.com" names an exact host, not a label a lookalike may end with.
	[Theory]
	[InlineData("https://notaccounts.google.com/?utm_source=x")]
	[InlineData("https://evilaccounts.google.com/?utm_source=x")]
	[InlineData("https://accounts.google.com.evil.example/?utm_source=x")]
	public void Cleanup_GoogleSsoLookalike_DoesNotGetFreeSsoBypass(string url)
	{
		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

		_ = result.IsBypassed.Should().BeFalse();
		_ = result.Changed.Should().BeTrue();
	}

	// Positive controls: legitimate hosts keep behaving exactly as today (bypass OR subdomain).
	[Theory]
	[InlineData("https://accounts.google.com/o/oauth2/v2/auth?utm_source=mail&state=abc")]
	[InlineData("https://okta.com/login?utm_source=mail")]
	[InlineData("https://acme.okta.com/login?utm_source=mail")]
	[InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?utm_source=mail")]
	[InlineData("https://acme.auth0.com/authorize?utm_source=mail")]
	public void Cleanup_LegitimateSsoHosts_StillBypass(string url)
	{
		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

		_ = result.IsBypassed.Should().BeTrue();
		_ = result.ReasonCode.Should().Be("cleanup.sso-bypass");
		_ = result.Changed.Should().BeFalse();
	}

	// The domain packs match a whole host-label suffix too: "facebook.com" admits "facebook.com"
	// and "www.facebook.com" but must NOT admit "evil-facebook.com" / "notfacebook.com", which
	// would otherwise wrongly inherit "meta-click-ids" and have their "fbclid" scrubbed.
	[Theory]
	[InlineData("https://evil-facebook.com/?fbclid=TRACKER")]
	[InlineData("https://notfacebook.com/?fbclid=TRACKER")]
	[InlineData("https://evilbing.com/?msclkid=TRACKER")]
	public void Cleanup_DomainPackLookalike_DoesNotInheritPack(string url)
	{
		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

		_ = result.AppliedPackId.Should().Be("global-known-tracking");
		_ = result.RemovedQueryParameterKeys.Should().HaveCount(1);
	}

	// Positive controls: legitimate pack hosts still resolve to their own pack id.
	[Theory]
	[InlineData("https://facebook.com/?fbclid=TRACKER", "meta-click-ids", "https://facebook.com/")]
	[InlineData("https://www.facebook.com/some/path?fbclid=abc123&x=1", "meta-click-ids", "https://www.facebook.com/some/path?x=1")]
	[InlineData("https://bing.com/search?q=hi&msclkid=abc", "microsoft-click-ids", "https://bing.com/search?q=hi")]
	public void Cleanup_LegitimateDomainPackHosts_StillApplyOwnPack(string url, string expectedPackId, string expectedUrl)
	{
		TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

		_ = result.Changed.Should().BeTrue();
		_ = result.AppliedPackId.Should().Be(expectedPackId);
		_ = result.EffectiveUrl.Should().Be(expectedUrl);
	}

	[Fact]
	public void Cleanup_AppliesEveryShippedDomainPack_WithExactFixtureProof()
	{
		foreach (string packId in TrackingCleanupEngine.GetShippedDomainPackIds())
		{
			(string url, string expected) = packId switch
			{
				"meta-click-ids" => ("https://www.facebook.com/some/path?fbclid=abc123&x=1", "https://www.facebook.com/some/path?x=1"),
				"microsoft-click-ids" => ("https://www.bing.com/search?q=hi&msclkid=abc", "https://www.bing.com/search?q=hi"),
				_ => throw new Xunit.Sdk.XunitException($"Missing exact fixture for pack '{packId}'.")
			};

			TrackingCleanupResult result = TrackingCleanupEngine.Cleanup(url, new TrackingCleanupOptions(Enabled: true));

			_ = result.Changed.Should().BeTrue($"pack {packId} must be test-gated by an exact fixture");
			_ = result.AppliedPackId.Should().Be(packId);
			_ = result.EffectiveUrl.Should().Be(expected);
		}
	}
}
