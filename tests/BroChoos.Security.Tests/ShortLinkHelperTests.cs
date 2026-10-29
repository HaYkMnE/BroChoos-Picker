using System.Net;
using BroChoos.Core.Abstractions;
using BroChoos.NetworkHelper;
using BroChoos.Platform.Windows.Network;
using FluentAssertions;
using Xunit;

namespace BroChoos.Security.Tests;

public sealed class ShortLinkHelperTests
{
	private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

	[Fact]
	public async Task ConsentRequired_WhenOptInIsFalse_ReturnsOriginalFallback()
	{
		ShortLinkResolver resolver = NewResolver(new ThrowingHandler(), (_, _) => Task.FromResult(Array.Empty<IPAddress>()));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("https://bit.ly/example", ConsentGranted: false),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.EffectiveUrl.Should().Be("https://bit.ly/example");
		_ = response.ErrorCode.Should().Be("shortlink.consent-required");
	}

	[Fact]
	public async Task AllowlistRejectsUnknownHost()
	{
		ShortLinkResolver resolver = NewResolver(new ThrowingHandler(), (_, _) => Task.FromResult(Array.Empty<IPAddress>()));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("https://example.com/x", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be("shortlink.host-not-allowlisted");
		_ = response.EffectiveUrl.Should().Be("https://example.com/x");
	}

	[Fact]
	public async Task HeadManualRedirect_ResolvesFinalUrl_WithoutAutoRedirect()
	{
		FakeSequenceHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.MovedPermanently)
			{
				Headers = { Location = new Uri("https://example.org/final") },
			},
			new HttpResponseMessage(HttpStatusCode.OK));

		ShortLinkResolver resolver = NewResolver(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("https://bit.ly/path", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
		_ = response.ErrorCode.Should().BeNull();
		_ = response.EffectiveUrl.Should().Be("https://example.org/final");
		_ = handler.Requests.Should().HaveCount(2);
		_ = handler.Requests.All(req => req.Method == HttpMethod.Head).Should().BeTrue();
		_ = handler.Requests.All(req => req.Headers.Authorization is null).Should().BeTrue();
		_ = handler.Requests.All(req => req.Headers.Referrer is null).Should().BeTrue();
	}

	[Fact]
	public async Task Guards_BlockLoopPrivateAndDowngrade()
	{
		FakeSequenceHandler loopHandler = new(
			new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://bit.ly/a") } });
		ShortLinkResolver loopResolver = NewResolver(loopHandler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
		ShortLinkResolveResponse loop = await loopResolver.ResolveAsync(new ShortLinkResolveRequest("https://bit.ly/a", true), CancellationToken.None);
		_ = loop.ErrorCode.Should().Be("shortlink.redirect-loop");

		FakeSequenceHandler privateHandler = new(
			new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://intranet.internal/w") } });
		ShortLinkResolver privateResolver = NewResolver(privateHandler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.10.1.2") }));
		ShortLinkResolveResponse privateResult = await privateResolver.ResolveAsync(new ShortLinkResolveRequest("https://bit.ly/a", true), CancellationToken.None);
		_ = privateResult.ErrorCode.Should().Be("shortlink.private-target");

		FakeSequenceHandler downgradeHandler = new(
			new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://example.org/d") } });
		ShortLinkResolver downgradeResolver = NewResolver(downgradeHandler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
		ShortLinkResolveResponse downgrade = await downgradeResolver.ResolveAsync(new ShortLinkResolveRequest("https://bit.ly/a", true), CancellationToken.None);
		_ = downgrade.ErrorCode.Should().Be("shortlink.unsafe-downgrade");
	}

	[Fact]
	public async Task RedirectLimit_IsEnforcedAtFive()
	{
		List<HttpResponseMessage> chain = [];
		for (int i = 0; i < 6; i++)
		{
			chain.Add(new HttpResponseMessage(HttpStatusCode.Found)
			{
				Headers = { Location = new Uri($"https://example.org/r{i}") },
			});
		}

		ShortLinkResolver resolver = NewResolver(new FakeSequenceHandler([.. chain]), (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("https://bit.ly/start", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be("shortlink.redirect-limit");
	}

	[Fact]
	public async Task TimeoutBudget_UsesCancellationTokenAndFallsBackToOriginal()
	{
		// The first hop must be pinned too (the allowlisted host is rebindable like any other), so
		// the resolver now validates its address BEFORE the request is sent. This fixture keeps the
		// focus on the timeout behaviour by giving that validation a public answer.
		ShortLinkResolver resolver = NewResolver(
			new TimeoutHandler(),
			(_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("https://bit.ly/t", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be("shortlink.timeout");
		_ = response.EffectiveUrl.Should().Be("https://bit.ly/t");
	}

	[Fact]
	public async Task EmptyDnsAnswer_ForTheFirstHop_FailsClosedAsAPrivateTarget()
	{
		// Pinning the first hop moved one rejection earlier: a host with no answer can no longer be
		// sent, so it is refused as a rejected target instead of being handed to the transport to
		// resolve again. This pins the new, stricter ordering deliberately.
		ShortLinkResolver resolver = NewResolver(
			new ThrowingHandler(),
			(_, _) => Task.FromResult(Array.Empty<IPAddress>()));

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("https://bit.ly/no-answer", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be("shortlink.private-target");
	}

	[Fact]
	public async Task NetworkHelperClient_UsesRamOnlySessionCache()
	{
		RecordingProcessRunner process = new(new ShortLinkResolveResponse(
			HelperClientDispatch.Dispatched,
			"https://bit.ly/z",
			"https://example.org/resolved",
			ErrorCode: null,
			FromCache: false));
		NetworkHelperClient client = new(process);

		ShortLinkResolveResponse first = await client.ResolveShortLinkAsync(new ShortLinkResolveRequest("https://bit.ly/z", true), CancellationToken.None);
		ShortLinkResolveResponse second = await client.ResolveShortLinkAsync(new ShortLinkResolveRequest("https://bit.ly/z", true), CancellationToken.None);

		_ = process.CallCount.Should().Be(1);
		_ = first.FromCache.Should().BeFalse();
		_ = second.FromCache.Should().BeTrue();
		_ = second.EffectiveUrl.Should().Be("https://example.org/resolved");
	}

	[Fact]
	public async Task NetworkHelperClient_RejectedResolve_IsNotCached_AndTheNextCallRetries()
	{
		// DEFECT #38: the cache used to be written UNCONDITIONALLY, so one transient helper
		// failure (helper temporarily missing, a one-off bad exit, an empty/malformed stdout)
		// was remembered for the rest of the process and replayed as FromCache = true forever.
		// The predicate now caches only a dispatched, error-free resolution, so a reject must
		// NOT be served from cache on the second call: the runner has to run again.
		RejectingProcessRunner process = new();
		NetworkHelperClient client = new(process);
		ShortLinkResolveRequest request = new("https://bit.ly/transient", ConsentGranted: true);

		ShortLinkResolveResponse first = await client.ResolveShortLinkAsync(request, CancellationToken.None);
		ShortLinkResolveResponse second = await client.ResolveShortLinkAsync(request, CancellationToken.None);

		_ = process.CallCount.Should().Be(2, "a rejected resolution is transient and must be retried, not replayed from cache");
		_ = first.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = first.ErrorCode.Should().Be("network.helper-not-found");
		_ = first.FromCache.Should().BeFalse();
		_ = second.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = second.ErrorCode.Should().Be("network.helper-not-found");
		_ = second.FromCache.Should().BeFalse("the second call is a fresh rejection, not a cache hit");
	}

	[Fact]
	public async Task NetworkHelperClient_RejectThenSuccess_CachesOnlyTheSuccess()
	{
		// The complement of the test above, and the reason the fix must not swing too far:
		// the transient reject is retried, and the FIRST successful resolution after it is
		// cached normally, so the third call is a free cache hit.
		FlakyProcessRunner process = new(
			new NetworkHelperProcessResult(false, -1, string.Empty, "helper-path-not-found"),
			new NetworkHelperProcessResult(true, 0, SerializeResolved("https://bit.ly/flaky", "https://example.org/flaky-resolved"), string.Empty));
		NetworkHelperClient client = new(process);
		ShortLinkResolveRequest request = new("https://bit.ly/flaky", ConsentGranted: true);

		ShortLinkResolveResponse first = await client.ResolveShortLinkAsync(request, CancellationToken.None);
		ShortLinkResolveResponse second = await client.ResolveShortLinkAsync(request, CancellationToken.None);
		ShortLinkResolveResponse third = await client.ResolveShortLinkAsync(request, CancellationToken.None);

		_ = first.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = second.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
		_ = second.ErrorCode.Should().BeNull();
		_ = second.FromCache.Should().BeFalse();
		_ = third.FromCache.Should().BeTrue("the successful resolution is the one the cache may hold");
		_ = third.EffectiveUrl.Should().Be("https://example.org/flaky-resolved");
		_ = process.CallCount.Should().Be(2, "the reject retried once; the success is served from cache the third time");
	}

	[Fact]
	public async Task NetworkHelperClient_SessionCache_IsBounded_AndEvictsTheOldestKey()
	{
		// N2: the session cache was an unbounded static ConcurrentDictionary, so a long-lived
		// URL-handler process grew it without limit. The bound is asserted as an EXACT number
		// (not "it works") and the eviction order is pinned: once the cap is passed, the oldest
		// key is gone (a re-resolve misses and re-runs the helper) while the newest is a hit.
		ShortLinkResolveResponse response = new(
			HelperClientDispatch.Dispatched,
			"https://bit.ly/bounded",
			"https://example.org/bounded",
			ErrorCode: null,
			FromCache: false);
		RecordingProcessRunner process = new(response);
		NetworkHelperClient client = new(process);

		int overflow = 10;
		for (int i = 0; i < NetworkHelperClient.SessionCacheCapacity + overflow; i++)
		{
			_ = await client.ResolveShortLinkAsync(new ShortLinkResolveRequest($"https://bit.ly/bounded-{i}", true), CancellationToken.None);
		}

		_ = NetworkHelperClient.SessionCacheCount.Should().Be(
			NetworkHelperClient.SessionCacheCapacity,
			"the cache must never retain more than its named cap, however many distinct URLs are resolved");

		int callsBeforeOldestRetry = process.CallCount;
		ShortLinkResolveResponse oldest = await client.ResolveShortLinkAsync(new ShortLinkResolveRequest("https://bit.ly/bounded-0", true), CancellationToken.None);
		_ = oldest.FromCache.Should().BeFalse("bounded-0 was the first inserted and must have been evicted");
		_ = process.CallCount.Should().Be(callsBeforeOldestRetry + 1, "an evicted key re-runs the helper");

		ShortLinkResolveResponse newest = await client.ResolveShortLinkAsync(
			new ShortLinkResolveRequest($"https://bit.ly/bounded-{NetworkHelperClient.SessionCacheCapacity + overflow - 1}", true),
			CancellationToken.None);
		_ = newest.FromCache.Should().BeTrue("the newest key is still resident");
	}

	[Fact]
	public async Task NetworkHelperClient_CacheCannotBypassConsentGate()
	{
		RecordingProcessRunner process = new(new ShortLinkResolveResponse(
			HelperClientDispatch.Dispatched,
			"https://bit.ly/consent-safe-cache",
			"https://example.org/consent-safe-cache-resolved",
			ErrorCode: null,
			FromCache: false));
		NetworkHelperClient client = new(process);

		ShortLinkResolveResponse granted = await client.ResolveShortLinkAsync(
			new ShortLinkResolveRequest("https://bit.ly/consent-safe-cache", ConsentGranted: true),
			CancellationToken.None);
		ShortLinkResolveResponse denied = await client.ResolveShortLinkAsync(
			new ShortLinkResolveRequest("https://bit.ly/consent-safe-cache", ConsentGranted: false),
			CancellationToken.None);

		_ = process.CallCount.Should().Be(1, "consent=false should never dispatch and must not read the consent=true cache entry");
		_ = granted.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
		_ = denied.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = denied.ErrorCode.Should().Be("shortlink.consent-required");
		_ = denied.FromCache.Should().BeFalse();
		_ = denied.EffectiveUrl.Should().Be("https://bit.ly/consent-safe-cache");
	}

	private static ShortLinkResolver NewResolver(HttpMessageHandler handler, Func<string, CancellationToken, Task<IPAddress[]>> dns)
	{
		return new ShortLinkResolver(new HttpMessageInvoker(handler), dns);
	}

	private static string SerializeResolved(string originalUrl, string effectiveUrl)
	{
		return System.Text.Json.JsonSerializer.Serialize(
			new ShortLinkResolveResponse(HelperClientDispatch.Dispatched, originalUrl, effectiveUrl, ErrorCode: null, FromCache: false),
			JsonOptions);
	}

	private sealed class FakeSequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
	{
		private readonly Queue<HttpResponseMessage> _responses = new(responses);

		public List<HttpRequestMessage> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(_responses.Count == 0 ? new HttpResponseMessage(HttpStatusCode.OK) : _responses.Dequeue());
		}
	}

	private sealed class ThrowingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException();
		}
	}

	private sealed class TimeoutHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			throw new OperationCanceledException();
		}
	}

	private sealed class RecordingProcessRunner(ShortLinkResolveResponse response) : INetworkHelperProcessRunner
	{
		private readonly ShortLinkResolveResponse _response = response;

		public int CallCount { get; private set; }

		public Task<NetworkHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
		{
			CallCount++;
			string output = System.Text.Json.JsonSerializer.Serialize(_response, JsonOptions);
			return Task.FromResult(new NetworkHelperProcessResult(true, 0, output, string.Empty));
		}
	}

	// Always reports a helper that could not be started, i.e. the exact shape of the
	// "network.helper-not-found" reject. It exists so the retryability of a reject can be
	// asserted without a success ever entering the picture.
	private sealed class RejectingProcessRunner : INetworkHelperProcessRunner
	{
		public int CallCount { get; private set; }

		public Task<NetworkHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
		{
			CallCount++;
			return Task.FromResult(new NetworkHelperProcessResult(false, -1, string.Empty, "helper-path-not-found"));
		}
	}

	// Answers with a queued sequence of raw process results, so a test can express
	// "fails once, then succeeds" in the order the client actually observes.
	private sealed class FlakyProcessRunner(params NetworkHelperProcessResult[] results) : INetworkHelperProcessRunner
	{
		private readonly Queue<NetworkHelperProcessResult> _results = new(results);

		public int CallCount { get; private set; }

		public Task<NetworkHelperProcessResult> RunAsync(string command, string requestPayload, CancellationToken cancellationToken)
		{
			CallCount++;
			return Task.FromResult(_results.Count == 0 ? results[^1] : _results.Dequeue());
		}
	}
}
