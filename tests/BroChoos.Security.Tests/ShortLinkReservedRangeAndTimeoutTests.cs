using System.Net;
using BroChoos.Core.Abstractions;
using BroChoos.NetworkHelper;
using FluentAssertions;
using Xunit;

namespace BroChoos.Security.Tests;

// Code review findings 1 and 3 on the short-link resolver.
//
// Finding 1: the IPv4 classifier missed 100.64.0.0/10 (CGNAT), 192.0.0.0/24 (IETF protocol
// assignments), 198.18.0.0/15 (benchmarking) and 224.0.0.0-255.255.255.255 (multicast, reserved,
// broadcast). Each range is pinned at its first and last address, plus the address just outside.
//
// Finding 3: a DNS lookup that is cut short by the timeout token used to come back as
// shortlink.private-target, because the lookup swallows every exception. The token is now checked
// first so the caller sees shortlink.timeout.
public sealed class ShortLinkReservedRangeAndTimeoutTests
{
	public static TheoryData<string, bool> Boundaries => new()
	{
		// 100.64.0.0/10 shared address space
		{ "100.63.255.255", false },
		{ "100.64.0.0", true },
		{ "100.127.255.255", true },
		{ "100.128.0.0", false },

		// 192.0.0.0/24 IETF protocol assignments
		{ "191.255.255.255", false },
		{ "192.0.0.0", true },
		{ "192.0.0.255", true },
		{ "192.0.1.0", false },

		// 198.18.0.0/15 benchmarking
		{ "198.17.255.255", false },
		{ "198.18.0.0", true },
		{ "198.19.255.255", true },
		{ "198.20.0.0", false },

		// 224.0.0.0 - 255.255.255.255 multicast, reserved, broadcast
		{ "223.255.255.255", false },
		{ "224.0.0.0", true },
		{ "239.255.255.255", true },
		{ "240.0.0.0", true },
		{ "255.255.255.254", true },
		{ "255.255.255.255", true },

		// the pre-existing ranges keep their boundaries
		{ "9.255.255.255", false },
		{ "10.0.0.0", true },
		{ "172.15.255.255", false },
		{ "172.16.0.0", true },
		{ "172.31.255.255", true },
		{ "172.32.0.0", false },
		{ "192.167.255.255", false },
		{ "192.168.0.0", true },
		{ "169.253.255.255", false },
		{ "169.254.0.0", true },
		{ "126.255.255.255", false },
		{ "127.0.0.1", true },
		{ "0.0.0.0", true },
		{ "1.0.0.0", false },
	};

	[Theory]
	[MemberData(nameof(Boundaries))]
	public async Task DnsAnswer_IsClassifiedAtTheRangeBoundary(string address, bool expectedReserved)
	{
		ShortLinkResolver resolver = new(
			new HttpMessageInvoker(new OkHandler()),
			(_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/boundary", ConsentGranted: true),
			CancellationToken.None);

		if (expectedReserved)
		{
			_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
			_ = response.ErrorCode.Should().Be("shortlink.private-target", $"{address} is private or reserved");
		}
		else
		{
			_ = response.ErrorCode.Should().BeNull($"{address} is a public address");
			_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
		}
	}

	[Theory]
	[InlineData("100.64.0.1")]
	[InlineData("192.0.0.1")]
	[InlineData("198.18.0.1")]
	[InlineData("224.0.0.1")]
	[InlineData("255.255.255.255")]
	public async Task NewRanges_AreAlsoRejected_WhenCarriedInsideAMappedIpv6Literal(string embedded)
	{
		ShortLinkResolver resolver = new(
			new HttpMessageInvoker(new OkHandler()),
			(_, _) => Task.FromResult(new[] { IPAddress.Parse($"::ffff:{embedded}") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/mapped-reserved", ConsentGranted: true),
			CancellationToken.None);

		_ = response.ErrorCode.Should().Be("shortlink.private-target");
	}

	[Fact]
	public async Task FirstHop_WhenTheTimeoutFiresDuringDns_IsReportedAsTimeout()
	{
		using CancellationTokenSource cts = new();
		ShortLinkResolver resolver = new(
			new HttpMessageInvoker(new OkHandler()),
			(_, token) =>
			{
				cts.Cancel();
				token.ThrowIfCancellationRequested();
				return Task.FromResult(Array.Empty<IPAddress>());
			});

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/slow-dns", ConsentGranted: true),
			cts.Token);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be("shortlink.timeout");
	}

	[Fact]
	public async Task RedirectHop_WhenTheTimeoutFiresDuringDns_IsReportedAsTimeout()
	{
		using CancellationTokenSource cts = new();
		ShortLinkResolver resolver = new(
			new HttpMessageInvoker(new RedirectThenOkHandler("http://slow.example/next")),
			(host, token) =>
			{
				if (host.StartsWith("bit.ly", StringComparison.Ordinal))
				{
					return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
				}

				cts.Cancel();
				token.ThrowIfCancellationRequested();
				return Task.FromResult(Array.Empty<IPAddress>());
			});

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/slow-second-hop", ConsentGranted: true),
			cts.Token);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be("shortlink.timeout");
	}

	[Fact]
	public async Task DnsFailure_WithALiveToken_IsStillAPrivateTargetRejection()
	{
		ShortLinkResolver resolver = new(
			new HttpMessageInvoker(new OkHandler()),
			(_, _) => throw new InvalidOperationException("resolver failed"));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/dns-fail", ConsentGranted: true),
			CancellationToken.None);

		_ = response.ErrorCode.Should().Be("shortlink.private-target");
	}

	[Fact]
	public async Task PrivateRedirectTarget_WithALiveToken_IsStillAPrivateTargetRejection()
	{
		ShortLinkResolver resolver = new(
			new HttpMessageInvoker(new RedirectThenOkHandler("http://10.1.2.3/x")),
			(_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/private-hop", ConsentGranted: true),
			CancellationToken.None);

		_ = response.ErrorCode.Should().Be("shortlink.private-target");
	}

	private sealed class OkHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
		}
	}

	private sealed class RedirectThenOkHandler(string location) : HttpMessageHandler
	{
		private int _calls;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(Interlocked.Increment(ref _calls) == 1
				? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } }
				: new HttpResponseMessage(HttpStatusCode.OK));
		}
	}
}
