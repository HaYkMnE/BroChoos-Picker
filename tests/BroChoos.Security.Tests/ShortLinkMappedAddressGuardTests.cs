using System.Net;
using BroChoos.Core.Abstractions;
using BroChoos.NetworkHelper;
using FluentAssertions;
using Xunit;

namespace BroChoos.Security.Tests;

// H2 (HIGH, independent deep audit): the SSRF guard classified an IPv4-mapped IPv6 literal
// by its V6 family only, so "::ffff:10.0.0.1" was NOT private - and Uri.Host preserves the
// bracketed form, so a redirect to http://[::ffff:10.0.0.1]/ passed shortlink.private-target
// and reached the internal host. Verified on .NET 10 before the fix (see the class comment on
// ShortLinkHelperTests for the probe): all four mapped literals classified NOT private.
//
// The same hole existed on the DNS path, where a resolver can hand back a mapped address.
public sealed class ShortLinkMappedAddressGuardTests
{
	public static TheoryData<string> MappedPrivateLiterals =>
	[
		"::ffff:10.0.0.1",        // RFC1918 10/8
		"::ffff:192.168.1.1",     // RFC1918 192.168/16
		"::ffff:172.16.5.5",      // RFC1918 172.16/12
		"::ffff:169.254.1.1",     // link-local 169.254/16
		"::ffff:127.0.0.1",       // loopback 127/8
		"::ffff:0.0.0.0",         // "this network" 0/8
	];

	[Theory]
	[MemberData(nameof(MappedPrivateLiterals))]
	public async Task MappedIpv4Literal_InARedirect_IsRejectedAsAPrivateTarget(string mappedLiteral)
	{
		FakeSequenceHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.Found)
			{
				Headers = { Location = new Uri($"http://[{mappedLiteral}]/internal") },
			});

		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/mapped", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be(
			"shortlink.private-target",
			"an IPv4-mapped IPv6 literal must be unwrapped and classified by the IPv4 address it carries");
		_ = response.EffectiveUrl.Should().Be("http://bit.ly/mapped", "a rejected resolution falls back to the original URL");
	}

	[Theory]
	[MemberData(nameof(MappedPrivateLiterals))]
	public async Task MappedIpv4Literal_FromTheDnsResolver_IsRejectedAsAPrivateTarget(string mappedLiteral)
	{
		// The literal itself is NOT parseable as a bare host here (it is not bracketed), so the
		// resolver is consulted - which is exactly the second hole in the audit finding.
		FakeSequenceHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.Found)
			{
				Headers = { Location = new Uri("http://internal-host.example/x") },
			});

		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse(mappedLiteral) }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/mapped-dns", ConsentGranted: true),
			CancellationToken.None);

		_ = response.ErrorCode.Should().Be("shortlink.private-target");
	}

	// Negative control: the guard must keep letting a genuinely PUBLIC address through, so the
	// unwrapping fix cannot pass by rejecting every IPv6 literal.
	[Fact]
	public async Task MappedPublicIpv4Literal_IsStillResolved()
	{
		FakeSequenceHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.Found)
			{
				Headers = { Location = new Uri("http://[::ffff:93.184.216.34]/final") },
			},
			new HttpResponseMessage(HttpStatusCode.OK));

		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/public", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
		_ = response.ErrorCode.Should().BeNull();
		_ = response.EffectiveUrl.Should().Be("http://[::ffff:93.184.216.34]/final");
	}

	// A bare (unbracketed) IPv6 literal in the request URL is still rejected by the allowlist,
	// so this pins that the mapped-literal unwrapping did not open the request path.
	[Fact]
	public async Task MappedIpv4Literal_AsTheRequestHost_IsStillRejectedByTheAllowlist()
	{
		FakeSequenceHandler handler = new(new HttpResponseMessage(HttpStatusCode.OK));
		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(Array.Empty<IPAddress>()));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://[::ffff:10.0.0.1]/x", ConsentGranted: true),
			CancellationToken.None);

		_ = response.ErrorCode.Should().Be("shortlink.host-not-allowlisted");
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
