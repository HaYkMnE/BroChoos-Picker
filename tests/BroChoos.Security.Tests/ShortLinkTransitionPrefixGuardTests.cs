using System.Net;
using BroChoos.Core.Abstractions;
using BroChoos.NetworkHelper;
using FluentAssertions;
using Xunit;

namespace BroChoos.Security.Tests;

// H4 (MEDIUM, independent deep audit): the private-target guard unwrapped ONLY ::ffff: mapped
// addresses, so the IPv6 transition prefixes that EMBED an IPv4 address were classified by the
// V6 rules alone. None of them is loopback/link-local/site-local/none/ULA, so all of these came
// back PUBLIC and a redirect to them reached the embedded private IPv4:
//   64:ff9b::/96  NAT64 well-known prefix (RFC 6052) -> [64:ff9b::7f00:1] is 127.0.0.1
//   2002::/16     6to4 (RFC 3056)                    -> [2002:7f00:1::]  is 127.0.0.1
//   2001:0000::/32 Teredo (RFC 4380/8200)           -> last 32 bits XOR 0xFFFFFFFF
//
// Extraction rules used by the fix (RFC 6052 / RFC 3056 / RFC 4380):
//   NAT64: the u-octet (byte 8) is reserved and MUST be zero; the IPv4 is bytes 12..15.
//   6to4:  the IPv4 is bytes 2..5.
//   Teredo: the IPv4 is the last 32 bits, bitwise-complemented (~).
// The embedded address is then classified by the ONE IPv4 rule, so there is a single definition
// of "private" for both families.
//
// Teredo has no row in the table below because it is the one transition form whose embedding does
// not describe an address by construction: a Teredo host id legally carries the COMPLEMENT of the
// Teredo IPv4, so a literal in this prefix can spell a private address in either direction
// depending on role. The guard's direction and its deliberate limit are pinned by
// TeredoLiteral_EmbeddingAPrivateIpv4_IsRejectedAsAPrivateTarget below.
public sealed class ShortLinkTransitionPrefixGuardTests
{
	// (literal, the private IPv4 it embeds, why)
	public static TheoryData<string, string, string> PrivateEmbeddingLiterals => new()
	{
		{ "64:ff9b::7f00:1", "127.0.0.1", "NAT64 embedding loopback 127/8" },
		{ "64:ff9b::a00:1", "10.0.0.1", "NAT64 embedding RFC1918 10/8" },
		{ "64:ff9b::c0a8:101", "192.168.1.1", "NAT64 embedding RFC1918 192.168/16" },
		{ "64:ff9b::a9fe:101", "169.254.1.1", "NAT64 embedding link-local 169.254/16" },
		{ "2002:7f00:1::", "127.0.0.1", "6to4 embedding loopback 127/8" },
		{ "2002:a00:1::", "10.0.0.1", "6to4 embedding RFC1918 10/8" },
		{ "2002:c0a8:101::", "192.168.1.1", "6to4 embedding RFC1918 192.168/16" },
	};

	// The same embedded IPv4 expressed through each transition form, so a fix that special-cases
	// only one prefix is caught.
	[Theory]
	[MemberData(nameof(PrivateEmbeddingLiterals))]
	public async Task TransitionPrefixLiteral_InARedirect_IsRejectedAsAPrivateTarget(string literal, string embeddedIpv4, string why)
	{
		FakeSequenceHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.Found)
			{
				Headers = { Location = new Uri($"http://[{literal}]/internal") },
			});

		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/transition-literal", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected, why);
		_ = response.ErrorCode.Should().Be(
			"shortlink.private-target",
			$"a transition-prefix literal must be unwrapped and classified by the IPv4 it carries ({embeddedIpv4})");
	}

	[Theory]
	[MemberData(nameof(PrivateEmbeddingLiterals))]
	public async Task TransitionPrefixLiteral_FromTheDnsResolver_IsRejectedAsAPrivateTarget(string literal, string embeddedIpv4, string why)
	{
		// The DNS path is the same choke point: a resolver can hand back a transition-prefix
		// address just as easily as it can hand back a mapped one.
		FakeSequenceHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.Found)
			{
				Headers = { Location = new Uri("http://internal-host.example/x") },
			});

		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse(literal) }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/transition-dns", ConsentGranted: true),
			CancellationToken.None);

		_ = response.ErrorCode.Should().Be(
			"shortlink.private-target",
			$"{why}; the answer embeds {embeddedIpv4}");
	}

	// The Teredo prefix itself is being used in its own transition role: the embedded IPv4 IS the
	// private target, so it is the direction that must not be missed. (Teredo's other direction -
	// a private ADDRESS mounted into a Teredo host id for a public IPv4 - is deliberately not
	// treated as private; complementing there would misread a public IPv4 as private and this
	// guard is required not to over-block. The IPv6 literal an attacker would actually send is the
	// bare private IPv6, which the V6 rules already reject.)
	[Fact]
	public async Task TeredoLiteral_EmbeddingAPrivateIpv4_IsRejectedAsAPrivateTarget()
	{
		// 2001:0000:4136:e378:8000:63bf:ffff:fec6 -> last 32 bits ffff:fec6, complemented 0.0.1.57
		FakeSequenceHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.Found)
			{
				Headers = { Location = new Uri("http://[2001:0000:4136:e378:8000:63bf:ffff:fec6]/internal") },
			});

		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/teredo-private", ConsentGranted: true),
			CancellationToken.None);

		_ = response.ErrorCode.Should().Be(
			"shortlink.private-target",
			"the Teredo embedded IPv4 is complemented per RFC 4380 before it is classified");
	}

	// NEGATIVE CONTROL: the unwrapping must not become a blanket rejection of these prefixes.
	// Each carries a genuinely public IPv4 (93.184.216.34 = example.com, TEST-NET example), so it
	// must still resolve - otherwise the fix would be "block all IPv6 transition forms", which
	// would break legitimate NAT64/6to4 users.
	public static TheoryData<string, string> PublicEmbeddingLiterals => new()
	{
		{ "64:ff9b::5db8:d822", "93.184.216.34" },
		{ "2002:5db8:d822::", "93.184.216.34" },
	};

	[Theory]
	[MemberData(nameof(PublicEmbeddingLiterals))]
	public async Task TransitionPrefixLiteral_WithAPublicEmbeddedIpv4_IsStillResolved(string literal, string embeddedIpv4)
	{
		_ = embeddedIpv4;
		FakeSequenceHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.Found)
			{
				Headers = { Location = new Uri($"http://[{literal}]/final") },
			},
			new HttpResponseMessage(HttpStatusCode.OK));

		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/transition-public", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
		_ = response.ErrorCode.Should().BeNull();
		_ = response.EffectiveUrl.Should().Be($"http://[{literal}]/final");
	}

	// RFC 6052 section 2.2: for a /96 mapping the whole four-octet field after the prefix (bits
	// 64..95, bytes 8..11) is zero, and the embedded IPv4 is the last 32 bits (bytes 12..15).
	// 64:ff9b::7f00:1 (0064:ff9b:0000:0000:0000:0000:7f00:0001) is a WELL-FORMED mapping: bytes
	// 8..11 are zero and bytes 12..15 are 127.0.0.1, so the guard must unwrap it and reject the
	// loopback target. This pins the mapping's shape, not where the colon groups fall in text.
	// (An earlier version of this test used 64:ff9b::7f00:1:1, whose bytes 12..15 are 00 01 00 01
	// = 0.1.0.1 — NOT 127.0.0.1, and whose bytes 10..11 are non-zero so it is not a /96 mapping at
	// all; it only appeared to pass because 0.1.0.1 starts with a zero byte. That was a wrong
	// fixture, not a real NAT64 case.)
	[Fact]
	public async Task Nat64Mapping_WithZeroUField_IsCaught_HoweverTheColonGroupsFall()
	{
		FakeSequenceHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.Found)
			{
				Headers = { Location = new Uri("http://[64:ff9b::7f00:1]/internal") },
			});

		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/nat64-u-field-zero", ConsentGranted: true),
			CancellationToken.None);

		_ = response.ErrorCode.Should().Be(
			"shortlink.private-target",
			"a zero u-field makes bytes 12..15 a valid embedded IPv4, which is 127.0.0.1 here");
	}

	// RFC 6052 section 2.2: an address that shares the 64:ff9b::/96 prefix but whose u-field
	// (bytes 8..11) is NOT all zero is not an IPv4-embedded mapping. Unwrapping bytes 12..15 of
	// such an address would invent a private target out of an unrelated global address. Here
	// 64:ff9b::a00:1:1 has bytes 10..11 = 0a 00 (non-zero u-field); its last 32 bits happen to be
	// 0.1.0.1 but this is NOT a NAT64 mapping, so the guard must NOT reject it as a private target.
	[Fact]
	public async Task Nat64ShapedAddress_WithNonZeroUField_IsNotTreatedAsAMapping()
	{
		FakeSequenceHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.Found)
			{
				Headers = { Location = new Uri("http://[64:ff9b::a00:1:1]/internal") },
			});

		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/nat64-nonzero-u-field", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(
			HelperClientDispatch.Dispatched,
			"a non-zero u-field means this is not an IPv4-embedded /96 mapping, so bytes 12..15 are not an embedded IPv4");
		_ = response.ErrorCode.Should().BeNull(
			"the address is not a private target, so resolution proceeds");
		_ = response.EffectiveUrl.Should().Be("http://[64:ff9b::a00:1:1]/internal");
	}

	private static ShortLinkResolver NewResolver(HttpMessageHandler handler, Func<string, CancellationToken, Task<IPAddress[]>> dns)
	{
		return new ShortLinkResolver(new HttpMessageInvoker(handler), dns);
	}

	private sealed class FakeSequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
	{
		private readonly Queue<HttpResponseMessage> _responses = new(responses);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(_responses.Count == 0 ? new HttpResponseMessage(HttpStatusCode.OK) : _responses.Dequeue());
		}
	}
}
