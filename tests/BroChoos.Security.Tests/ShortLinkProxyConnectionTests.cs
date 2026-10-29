using System.Net;
using System.Net.Sockets;
using System.Text;
using BroChoos.Core.Abstractions;
using BroChoos.NetworkHelper;
using FluentAssertions;
using Xunit;

namespace BroChoos.Security.Tests;

// REGRESSION (proxied users): the DNS-rebinding pin installed a ConnectCallback that connected to
// the guard-validated TARGET address on whatever port the callback was handed. When a proxy is in
// use, SocketsHttpHandler hands the callback the PROXY endpoint, so the old code dialled
// <target-ip>:<proxy-port> - a SYN nobody answers - and every proxied resolve ended in
// shortlink.timeout (observed in the field: SynSent to 67.199.248.10:40528, 40528 being the local
// proxy port).
//
// These tests drive the PINNED (SocketsHttpHandler) ctor through a real in-process loopback proxy
// and assert that the socket reaches the proxy itself and that the proxy receives the request.
// The endpoint rewriter is the production identity (validated, port) => validated:port, wrapped
// only to RECORD whether the pin was applied; on the proxy path it must not be consulted at all.
public sealed class ShortLinkProxyConnectionTests
{
	private const string TargetAnswer = "203.0.113.10"; // RFC 5737 TEST-NET-3, never the proxy
	private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task HttpRequest_ThroughProxy_ConnectsToTheProxy_NotToTheTargetIpOnTheProxyPort()
	{
		await using LoopbackProxy proxy = LoopbackProxy.Start(
			"HTTP/1.1 302 Found\r\nLocation: http://example.org/final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
			"HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

		List<IPEndPoint> pinnedConnects = [];
		using ShortLinkResolver resolver = NewProxiedResolver(proxy, pinnedConnects);
		using CancellationTokenSource cts = new(ResolveBudget);

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/via-proxy", ConsentGranted: true),
			cts.Token);

		_ = response.ErrorCode.Should().BeNull("a proxied resolve must reach the proxy instead of timing out");
		_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
		_ = response.EffectiveUrl.Should().Be("http://example.org/final");
		_ = proxy.RequestLines.Should().Equal(
			["HEAD http://bit.ly/via-proxy HTTP/1.1", "HEAD http://example.org/final HTTP/1.1"],
			"the proxy itself must receive each hop as an absolute-URI request");
		_ = pinnedConnects.Should().BeEmpty(
			"the target-IP pin must never be applied to the proxy connection (it would dial <target-ip>:<proxy-port>)");
	}

	[Fact]
	public async Task HttpsRequest_ThroughProxy_SendsConnectToTheProxy()
	{
		// The proxy refuses the tunnel; the point is only that the CONNECT reached the proxy port.
		await using LoopbackProxy proxy = LoopbackProxy.Start(
			"HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

		List<IPEndPoint> pinnedConnects = [];
		using ShortLinkResolver resolver = NewProxiedResolver(proxy, pinnedConnects);
		using CancellationTokenSource cts = new(ResolveBudget);

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("https://bit.ly/tunnel", ConsentGranted: true),
			cts.Token);

		_ = proxy.RequestLines.Should().ContainSingle($"the CONNECT must reach the proxy (resolve ended with {response.ErrorCode})")
			.Which.Should().Be("CONNECT bit.ly:443 HTTP/1.1");
		_ = response.ErrorCode.Should().Be("shortlink.network-error", "a refused tunnel is a network error, not a timeout");
		_ = pinnedConnects.Should().BeEmpty();
	}

	[Fact]
	public async Task ProxyPath_StillRejectsAPrivateRedirectTarget_BeforeTheNextHopLeaves()
	{
		// The pre-request guard is the control on the proxy path; it must keep running per hop.
		await using LoopbackProxy proxy = LoopbackProxy.Start(
			"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1/internal\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
			"HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

		List<IPEndPoint> pinnedConnects = [];
		using ShortLinkResolver resolver = NewProxiedResolver(proxy, pinnedConnects);
		using CancellationTokenSource cts = new(ResolveBudget);

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/private-via-proxy", ConsentGranted: true),
			cts.Token);

		_ = response.ErrorCode.Should().Be("shortlink.private-target");
		_ = proxy.RequestLines.Should().Equal(
			["HEAD http://bit.ly/private-via-proxy HTTP/1.1"],
			"the private redirect target must be rejected before a second request is sent to the proxy");
		_ = pinnedConnects.Should().BeEmpty();
	}

	private static ShortLinkResolver NewProxiedResolver(LoopbackProxy proxy, List<IPEndPoint> pinnedConnects)
	{
		SocketsHttpHandler handler = new()
		{
			AllowAutoRedirect = false,
			UseCookies = false,
			UseProxy = true,
			Proxy = new WebProxy(proxy.Uri),
			AutomaticDecompression = DecompressionMethods.None,
		};

		return new ShortLinkResolver(
			handler,
			(_, _) => Task.FromResult(new[] { IPAddress.Parse(TargetAnswer) }),
			(validated, port) =>
			{
				// Production identity, only recorded: this is the endpoint the pin would dial.
				IPEndPoint pinned = new(validated, port);
				lock (pinnedConnects)
				{
					pinnedConnects.Add(pinned);
				}

				return pinned;
			});
	}

	// A minimal forward proxy stub: accepts one connection per scripted response, records the
	// request line it received, replies with the scripted raw response and closes.
	private sealed class LoopbackProxy : IAsyncDisposable
	{
		private readonly TcpListener _listener;
		private readonly CancellationTokenSource _cts;
		private readonly Task _task;
		private readonly List<string> _requestLines;

		private LoopbackProxy(TcpListener listener, CancellationTokenSource cts, Task task, List<string> requestLines)
		{
			_listener = listener;
			_cts = cts;
			_task = task;
			_requestLines = requestLines;
			Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
		}

		public Uri Uri { get; }

		public IReadOnlyList<string> RequestLines
		{
			get
			{
				lock (_requestLines)
				{
					return [.. _requestLines];
				}
			}
		}

		public static LoopbackProxy Start(params string[] rawResponses)
		{
			TcpListener listener = new(IPAddress.Loopback, 0);
			listener.Start();
			CancellationTokenSource cts = new();
			List<string> requestLines = [];
			Task task = RunAsync(listener, requestLines, rawResponses, cts.Token);
			return new LoopbackProxy(listener, cts, task, requestLines);
		}

		public async ValueTask DisposeAsync()
		{
			_cts.Cancel();
			_listener.Stop();
			try
			{
				await _task;
			}
			catch (OperationCanceledException) when (_cts.IsCancellationRequested)
			{
			}
			catch (SocketException) when (_cts.IsCancellationRequested)
			{
			}
			catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
			{
			}

			_cts.Dispose();
		}

		private static async Task RunAsync(
			TcpListener listener,
			List<string> requestLines,
			string[] rawResponses,
			CancellationToken cancellationToken)
		{
			for (int i = 0; i < rawResponses.Length && !cancellationToken.IsCancellationRequested; i++)
			{
				using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
				using NetworkStream stream = client.GetStream();
				using (StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
				{
					string? requestLine = await reader.ReadLineAsync(cancellationToken);
					lock (requestLines)
					{
						requestLines.Add(requestLine ?? string.Empty);
					}

					while (true)
					{
						string? line = await reader.ReadLineAsync(cancellationToken);
						if (line is null || line.Length == 0)
						{
							break;
						}
					}
				}

				byte[] bytes = Encoding.ASCII.GetBytes(rawResponses[i]);
				await stream.WriteAsync(bytes, cancellationToken);
				await stream.FlushAsync(cancellationToken);
			}
		}
	}
}
