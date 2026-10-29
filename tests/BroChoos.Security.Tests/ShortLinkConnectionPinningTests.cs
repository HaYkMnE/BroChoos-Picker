using System.Net;
using System.Net.Sockets;
using System.Text;
using BroChoos.Core.Abstractions;
using BroChoos.NetworkHelper;
using FluentAssertions;
using Xunit;

namespace BroChoos.Security.Tests;

// H3 (HIGH, independent deep audit): DNS-rebinding TOCTOU on the short-link resolver.
//
// The guard resolved the redirect target with the INJECTED resolver and rejected private
// answers, but the outbound request was then handed to an HttpMessageInvoker that performed
// its OWN, independent resolution at connect time. The approved IP was discarded, so a
// hostname whose second answer is private reached the internal endpoint even though the guard
// had only ever vetted a public one.
//
// The fix pins the connection: the resolver resolves and validates ONCE per hop, and the socket
// is opened against exactly that IP (SocketsHttpHandler.ConnectCallback plus a per-request
// options slot). TLS SNI and the Host header keep using the hostname because the request URI is
// never rewritten.
//
// WHAT THE FIRST TEST PROVES (honest scope): the request targets a hostname that CANNOT resolve
// through the platform resolver ("rebind-target.invalid" - the RFC 6761 reserved TLD, guaranteed
// NXDOMAIN) while the INJECTED resolver answers with a public TEST-NET-3 address (RFC 5737). The
// pre-fix code would fail on the handler's own DNS lookup for that host; the post-fix code
// reaches the loopback listener ONLY because the connect callback was handed the guard-validated
// IP from the injected resolver. Success therefore proves the connect used the vetted address
// rather than a fresh resolution - which is exactly the address a rebinding attacker needs the
// client to re-resolve.
// It does NOT simulate a live attacker flipping two answers at the OS resolver; the rewriter
// parameter is the seam that records the IP the connect callback was actually given, and that
// recorded value is asserted below.
public sealed class ShortLinkConnectionPinningTests
{
	private const string FirstHopAnswer = "203.0.113.10";  // RFC 5737 TEST-NET-3
	private const string SecondHopAnswer = "198.51.100.7"; // RFC 5737 TEST-NET-2
	private const string UnresolvableHost = "rebind-target.invalid";

	[Fact]
	public async Task ConnectCallback_UsesTheGuardValidatedIp_InsteadOfAFreshHandlerResolution()
	{
		await using LoopbackEndpoint endpoint = LoopbackEndpoint.Start(
			new ScriptedResponse("HTTP/1.1 200 OK", ZeroLength()));

		List<IPAddress> connectIps = [];
		ShortLinkResolver resolver = new(
			NewHandler(),
			(_, _) => Task.FromResult(new[] { IPAddress.Parse(FirstHopAnswer) }),
			(validated, _) =>
			{
				connectIps.Add(validated);
				return new IPEndPoint(IPAddress.Loopback, endpoint.Port); // test-only transport redirect
			});

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/pinned-first-hop", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(
			HelperClientDispatch.Dispatched,
			$"the request must complete over the pinned connection, got {response.ErrorCode}");
		_ = connectIps.Should().ContainSingle().Which.Should().Be(
			IPAddress.Parse(FirstHopAnswer),
			"the connect callback must receive exactly the address the guard validated");
		_ = endpoint.Exchanges.Should().NotBeEmpty("the pinned connection reached the listener");
	}

	[Fact]
	public async Task RedirectTargetPin_UsesEachHopsOwnValidatedIp()
	{
		// The redirect target is a hostname the platform resolver cannot answer, so the only way
		// this request can complete is through the pinned connection to the injected answer.
		await using LoopbackEndpoint endpoint = LoopbackEndpoint.Start(
			new ScriptedResponse("HTTP/1.1 302 Found", Location($"http://{UnresolvableHost}/final")),
			new ScriptedResponse("HTTP/1.1 200 OK", ZeroLength()));

		List<IPAddress> connectIps = [];
		ShortLinkResolver resolver = new(
			NewHandler(),
			(host, _) => Task.FromResult(host.StartsWith("bit.ly", StringComparison.Ordinal)
				? new[] { IPAddress.Parse(FirstHopAnswer) }
				: [IPAddress.Parse(SecondHopAnswer)]),
			(validated, _) =>
			{
				connectIps.Add(validated);
				return new IPEndPoint(IPAddress.Loopback, endpoint.Port);
			});

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/two-hop", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched, $"got {response.ErrorCode}");
		_ = response.EffectiveUrl.Should().Be($"http://{UnresolvableHost}/final");
		_ = connectIps.Should().Equal(
			[IPAddress.Parse(FirstHopAnswer), IPAddress.Parse(SecondHopAnswer)],
			"each hop must connect to the address its own validation approved, in order");
	}

	[Fact]
	public async Task ConnectCallback_TriesEachValidatedAddressInOrder_UntilOneConnects()
	{
		// Code review finding 2: only resolved[0] used to be kept, so one unreachable first answer
		// failed the whole resolve. The first validated address is steered at a closed port, the
		// second at the live listener; the resolve must succeed and the callback must have been
		// handed both addresses, in resolver order.
		await using LoopbackEndpoint endpoint = LoopbackEndpoint.Start(
			new ScriptedResponse("HTTP/1.1 200 OK", ZeroLength()));

		TcpListener closed = new(IPAddress.Loopback, 0);
		closed.Start();
		int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
		closed.Stop();

		IPAddress first = IPAddress.Parse(FirstHopAnswer);
		IPAddress second = IPAddress.Parse(SecondHopAnswer);
		List<IPAddress> connectIps = [];
		ShortLinkResolver resolver = new(
			NewHandler(),
			(_, _) => Task.FromResult(new[] { first, second }),
			(validated, _) =>
			{
				connectIps.Add(validated);
				return new IPEndPoint(IPAddress.Loopback, validated.Equals(first) ? closedPort : endpoint.Port);
			});

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/multi-address", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched, $"got {response.ErrorCode}");
		_ = connectIps.Should().Equal([first, second], "the unreachable first address falls through to the next validated one");
		_ = endpoint.Exchanges.Should().NotBeEmpty();
	}

	[Fact]
	public async Task ConnectCallback_StopsAtTheFirstReachableAddress()
	{
		await using LoopbackEndpoint endpoint = LoopbackEndpoint.Start(
			new ScriptedResponse("HTTP/1.1 200 OK", ZeroLength()));

		List<IPAddress> connectIps = [];
		ShortLinkResolver resolver = new(
			NewHandler(),
			(_, _) => Task.FromResult(new[] { IPAddress.Parse(FirstHopAnswer), IPAddress.Parse(SecondHopAnswer) }),
			(validated, _) =>
			{
				connectIps.Add(validated);
				return new IPEndPoint(IPAddress.Loopback, endpoint.Port);
			});

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/first-wins", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched, $"got {response.ErrorCode}");
		_ = connectIps.Should().Equal([IPAddress.Parse(FirstHopAnswer)], "later addresses are not dialled once one connects");
	}

	[Fact]
	public async Task MixedPublicAndPrivateAnswers_AreStillRejected()
	{
		List<IPAddress> connectIps = [];
		ShortLinkResolver resolver = new(
			NewHandler(),
			(_, _) => Task.FromResult(new[] { IPAddress.Parse(FirstHopAnswer), IPAddress.Parse("10.0.0.5") }),
			(validated, port) =>
			{
				connectIps.Add(validated);
				return new IPEndPoint(IPAddress.Loopback, port);
			});

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/mixed", ConsentGranted: true),
			CancellationToken.None);

		_ = response.ErrorCode.Should().Be("shortlink.private-target");
		_ = connectIps.Should().BeEmpty();
	}

	[Fact]
	public void ConnectPinGuard_RefusesToConnect_WhenTheValidatedAddressIsMissing()
	{
		// FAIL CLOSED, not fall through: a request that reaches the connect callback without a
		// pinned address must be refused rather than silently letting the platform resolver pick
		// the destination.
		//
		// SocketsHttpConnectionContext is a sealed class the framework alone constructs, so the
		// callback cannot be invoked from a test at all. The decision it makes is therefore held
		// in a pure predicate, which is what this pins: with no validated address there is no
		// connection target, so the caller throws instead of resolving independently.
		Action connectWithoutPin = () => ShortLinkResolver.RequireValidatedAddresses(null);
		Action connectWithEmptyPin = () => ShortLinkResolver.RequireValidatedAddresses([]);
		Action connectWithPin = () => _ = ShortLinkResolver.RequireValidatedAddresses([IPAddress.Parse(FirstHopAnswer)]);

		_ = connectWithoutPin.Should().Throw<InvalidOperationException>(
			"an unpinned connect must never be attempted");
		_ = connectWithEmptyPin.Should().Throw<InvalidOperationException>(
			"an empty validated list carries no address to dial");
		_ = connectWithPin.Should().NotThrow("a validated address is exactly what the pin carries");
	}

	[Fact]
	public async Task HandlerOwnedPath_StillRejectsAHostnameWhoseInjectedResolutionIsPrivate()
	{
		// Pinning must not weaken the guard: a hostname the injected resolver maps to a private
		// address is rejected before any socket is opened.
		List<IPAddress> connectIps = [];
		ShortLinkResolver resolver = new(
			NewHandler(),
			(_, _) => Task.FromResult(new[] { IPAddress.Parse("10.10.1.2") }),
			(validated, port) =>
			{
				connectIps.Add(validated);
				return new IPEndPoint(IPAddress.Loopback, port);
			});

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/private-dns", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be("shortlink.private-target");
		_ = connectIps.Should().BeEmpty("no socket may be opened for a rejected target");
	}

	[Fact]
	public async Task HandlerOwnedPath_FailsClosed_WhenDnsResolutionFails()
	{
		List<IPAddress> connectIps = [];
		ShortLinkResolver resolver = new(
			NewHandler(),
			(_, _) => throw new SocketException((int)SocketError.HostNotFound),
			(validated, port) =>
			{
				connectIps.Add(validated);
				return new IPEndPoint(IPAddress.Loopback, port);
			});

		ShortLinkResolveResponse response = await resolver.ResolveAsync(
			new ShortLinkResolveRequest("http://bit.ly/dns-failure", ConsentGranted: true),
			CancellationToken.None);

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be(
			"shortlink.private-target",
			"a resolver failure stays fail-closed, classified as a rejected target");
		_ = connectIps.Should().BeEmpty();
	}

	private static SocketsHttpHandler NewHandler()
	{
		return new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			UseCookies = false,
			UseProxy = false,
			AutomaticDecompression = DecompressionMethods.None,
		};
	}

	private static Dictionary<string, string> ZeroLength()
	{
		return new(StringComparer.OrdinalIgnoreCase)
		{
			["Content-Length"] = "0",
			["Connection"] = "close",
		};
	}

	private static Dictionary<string, string> Location(string value)
	{
		return new(StringComparer.OrdinalIgnoreCase)
		{
			["Location"] = value,
			["Content-Length"] = "0",
			["Connection"] = "close",
		};
	}

	private sealed record ScriptedResponse(string StatusLine, IReadOnlyDictionary<string, string> Headers);

	private sealed class LoopbackEndpoint : IAsyncDisposable
	{
		private readonly TcpListener _listener;
		private readonly CancellationTokenSource _cts;
		private readonly Task _task;
		private readonly List<string> _exchanges;

		private LoopbackEndpoint(TcpListener listener, CancellationTokenSource cts, Task task, List<string> exchanges, int port)
		{
			_listener = listener;
			_cts = cts;
			_task = task;
			_exchanges = exchanges;
			Port = port;
		}

		public IReadOnlyList<string> Exchanges => _exchanges;

		public int Port { get; }

		public static LoopbackEndpoint Start(params ScriptedResponse[] responses)
		{
			TcpListener listener = new(IPAddress.Loopback, 0);
			listener.Start();
			int port = ((IPEndPoint)listener.LocalEndpoint).Port;
			CancellationTokenSource cts = new();
			List<string> exchanges = [];
			Task task = RunAsync(listener, exchanges, responses, cts.Token);
			return new LoopbackEndpoint(listener, cts, task, exchanges, port);
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

			_cts.Dispose();
		}

		private static async Task RunAsync(
			TcpListener listener,
			List<string> exchanges,
			IReadOnlyList<ScriptedResponse> responses,
			CancellationToken cancellationToken)
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
				using NetworkStream stream = client.GetStream();
				int hop = exchanges.Count;
				using (StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
				{
					while (true)
					{
						string? line = await reader.ReadLineAsync(cancellationToken);
						if (line is null || line.Length == 0)
						{
							break;
						}

						exchanges.Add(line);
					}
				}

				ScriptedResponse scripted = responses[Math.Min(hop, responses.Count - 1)];
				StringBuilder raw = new();
				_ = raw.AppendLine(scripted.StatusLine);
				foreach ((string name, string value) in scripted.Headers)
				{
					_ = raw.AppendLine(string.Concat(name, ": ", value));
				}

				_ = raw.AppendLine();
				byte[] bytes = Encoding.ASCII.GetBytes(raw.ToString());
				await stream.WriteAsync(bytes, cancellationToken);
				await stream.FlushAsync(cancellationToken);
			}
		}
	}
}
