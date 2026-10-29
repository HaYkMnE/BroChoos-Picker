using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BroChoos.Core.Abstractions;

namespace BroChoos.NetworkHelper;

internal static class ShortLinkAllowlist
{
	private static readonly string[] AllowedHosts =
	[
		"bit.ly",
		"t.co",
		"tinyurl.com",
		"shorturl.at",
		"is.gd",
	];

	public static bool IsAllowedHost(string host)
	{
		if (string.IsNullOrWhiteSpace(host))
		{
			return false;
		}

		string lower = host.ToLowerInvariant();
		return AllowedHosts.Any(allowed => lower == allowed || lower.EndsWith($".{allowed}", StringComparison.Ordinal));
	}
}

public sealed class ShortLinkResolver : IDisposable
{
	private const int MaxRedirects = 5;

	// THE PER-REQUEST PIN SLOT. The address the guard validated travels with THIS request and is
	// read back by the connect callback, so the socket can only ever be opened against an address
	// that passed the private-target guard. It lives in HttpRequestOptions rather than in a
	// resolver field because the framework runs connect callbacks concurrently and a field would
	// let one request's validated address be used for another request's connection.
	private static readonly HttpRequestOptionsKey<IPAddress[]> ValidatedAddressOption = new("BroChoos.ShortLinkResolver.ValidatedAddress");

	private readonly HttpMessageInvoker _http;
	private readonly Func<string, CancellationToken, Task<IPAddress[]>> _dnsResolver;

	// TEST SEAM for the pin: given the validated address and the port the request would have used,
	// returns the ENDPOINT to actually connect to. Production passes nothing and connects to the
	// validated address on that same port; the tests use it to record which address the connect
	// callback was handed and to steer the transport at a loopback listener without touching the
	// DNS path under test.
	private readonly Func<IPAddress, int, IPEndPoint> _connectEndpointRewriter;

	// UNPINNED overload - INTENTIONALLY internal. An injected HttpMessageInvoker carries no handler
	// this type can reach (HttpMessageInvoker does not expose it), so no pin can be installed or
	// verified through it: the connection may be re-resolved by the transport after the guard has
	// already approved a different IP. That is the exact DNS-rebinding window this type exists to
	// close, so the overload is kept ONLY as an in-process test seam for callers that assert
	// PinsConnectionsToValidatedAddress == false. Making it internal turns "someone forgot to pin"
	// from a silent runtime flag into a compile error. Production uses the SocketsHttpHandler ctor.
	internal ShortLinkResolver(HttpMessageInvoker http, Func<string, CancellationToken, Task<IPAddress[]>> dnsResolver)
	{
		_http = http;
		_dnsResolver = dnsResolver;
		_connectEndpointRewriter = static (validated, port) => new IPEndPoint(validated, port);

		// An injected invoker carries no handler this type can reach (HttpMessageInvoker does not
		// expose it), so the resolver cannot install or verify a pin through this overload. The
		// boolean stays false and says exactly that, instead of implying a guarantee it cannot keep.
		PinsConnectionsToValidatedAddress = false;
	}

	public ShortLinkResolver(
		SocketsHttpHandler http,
		Func<string, CancellationToken, Task<IPAddress[]>> dnsResolver,
		Func<IPAddress, int, IPEndPoint>? connectEndpointRewriter = null)
	{
		// THE RESOLVER MUST OWN THE SOCKET LAYER, or the pin is a promise it cannot keep. A
		// caller-supplied connect callback would run instead of this one, so the guard's address
		// would again be discarded at connect time and the request would silently fall back to an
		// unpinned resolution - the exact defect this type exists to prevent. Refusing the handler
		// outright is the only answer that cannot be mistaken for success.
		if (http.ConnectCallback is not null)
		{
			throw new ArgumentException(
				"The handler already has a ConnectCallback, so this resolver cannot pin the connection to the validated address. Omit the callback; the pin is what prevents a DNS-rebinding race between the guard and the connect.",
				nameof(http));
		}

		_dnsResolver = dnsResolver;
		_connectEndpointRewriter = connectEndpointRewriter ?? ((validated, port) => new IPEndPoint(validated, port));
		http.ConnectCallback = ConnectPinnedAsync;
		_http = new HttpMessageInvoker(http);
		PinsConnectionsToValidatedAddress = true;
	}

	// True when this resolver owns the socket layer and therefore guarantees that every hop
	// connects to the address the guard approved. A resolver built over an opaque invoker still
	// validates every hop but CANNOT make that guarantee (the invoker may own its own handler and
	// its own DNS), so it reports false rather than implying a pin it does not control.
	public bool PinsConnectionsToValidatedAddress { get; }

	public async Task<ShortLinkResolveResponse> ResolveAsync(ShortLinkResolveRequest request, CancellationToken cancellationToken)
	{
		if (!request.ConsentGranted)
		{
			return Reject(request.Url, "shortlink.consent-required");
		}

		if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? original)
			|| !IsHttpOrHttps(original))
		{
			return Reject(request.Url, "shortlink.invalid-url");
		}

		if (!ShortLinkAllowlist.IsAllowedHost(original.Host))
		{
			return Reject(request.Url, "shortlink.host-not-allowlisted");
		}

		Uri current = original;
		HashSet<string> visited = new(StringComparer.Ordinal);
		_ = visited.Add(current.AbsoluteUri);

		// THE PIN IS RESOLVED ONCE PER HOP, BEFORE THE REQUEST LEAVES. The old shape validated a
		// redirect target and then let the handler resolve it AGAIN at connect time; a host whose
		// second answer is private passed the guard on its public answer and reached the internal
		// endpoint. Here the address that survives the guard is the address the connect callback
		// is given, so there is no second resolution left for an attacker to win.
		IPAddress[]? pinnedAddresses = await ResolveValidatedAddressesAsync(original, cancellationToken);
		if (pinnedAddresses is null)
		{
			return RejectUnvalidatedTarget(request.Url, cancellationToken);
		}

		for (int redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
		{
			using HttpRequestMessage message = new(HttpMethod.Head, current);
			message.Headers.Referrer = null;
			message.Headers.Authorization = null;
			message.Options.Set(ValidatedAddressOption, pinnedAddresses);

			HttpResponseMessage response;
			try
			{
				response = await _http.SendAsync(message, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				return Reject(request.Url, "shortlink.timeout");
			}
			catch
			{
				return Reject(request.Url, "shortlink.network-error");
			}

			using (response)
			{
				if (!IsRedirect(response.StatusCode))
				{
					return new ShortLinkResolveResponse(
						HelperClientDispatch.Dispatched,
						request.Url,
						current.AbsoluteUri,
						ErrorCode: null,
						FromCache: false);
				}

				if (redirectCount == MaxRedirects)
				{
					return Reject(request.Url, "shortlink.redirect-limit");
				}

				Uri? next = response.Headers.Location;
				if (next is null)
				{
					return Reject(request.Url, "shortlink.redirect-missing-location");
				}

				if (!next.IsAbsoluteUri)
				{
					next = new Uri(current, next);
				}

				if (!IsHttpOrHttps(next))
				{
					return Reject(request.Url, "shortlink.dangerous-scheme");
				}

				if (string.Equals(current.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(next.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
				{
					return Reject(request.Url, "shortlink.unsafe-downgrade");
				}

				if (!visited.Add(next.AbsoluteUri))
				{
					return Reject(request.Url, "shortlink.redirect-loop");
				}

				IPAddress[]? nextPinnedAddresses = await ResolveValidatedAddressesAsync(next, cancellationToken);
				if (nextPinnedAddresses is null)
				{
					return RejectUnvalidatedTarget(request.Url, cancellationToken);
				}

				pinnedAddresses = nextPinnedAddresses;
				current = next;
			}
		}

		return Reject(request.Url, "shortlink.redirect-limit");
	}

	// A null verdict normally means "private target". The DNS lookup swallows every exception
	// (fail closed), including the cancellation raised when the timeout fires mid-lookup, so the
	// token is consulted first: a timed-out lookup is reported as a timeout, not as a private target.
	private static ShortLinkResolveResponse RejectUnvalidatedTarget(string originalUrl, CancellationToken cancellationToken)
	{
		return Reject(
			originalUrl,
			cancellationToken.IsCancellationRequested ? "shortlink.timeout" : "shortlink.private-target");
	}

	// The guard's verdict AND the addresses to connect to, in one call: null means "reject this
	// target", any other value is the non-empty list of addresses that ALL passed the private-target
	// classification and are the only addresses the pinned connection may try, in order. Keeping the
	// two together is what makes the TOCTOU impossible - there is no seam between "this target is
	// acceptable" and "connect here".
	private async Task<IPAddress[]?> ResolveValidatedAddressesAsync(Uri target, CancellationToken cancellationToken)
	{
		if (target.IsLoopback)
		{
			return null;
		}

		string host = target.Host;
		if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		if (IPAddress.TryParse(host, out IPAddress? ipLiteral))
		{
			return IsPrivateLocalOrInternalIp(ipLiteral) ? null : [ipLiteral];
		}

		// A bracketed literal reaches here only if the framework stopped accepting the
		// bracket form (MEASURED on .NET 10: IPAddress.TryParse DOES accept "[::ffff:10.0.0.1]",
		// so this path is the belt to that braces). Retrying without the brackets keeps the
		// literal classification independent of that framework leniency; if the retry also
		// fails the DNS resolver below is consulted with an unparseable host and the guard
		// fails CLOSED (the catch returns null), never open.
		if (host.Length > 2 && host[0] == '[' && host[^1] == ']'
			&& IPAddress.TryParse(host[1..^1], out IPAddress? unbracketedLiteral))
		{
			return IsPrivateLocalOrInternalIp(unbracketedLiteral) ? null : [unbracketedLiteral];
		}

		IPAddress[] resolved;
		try
		{
			resolved = await _dnsResolver(host, cancellationToken);
		}
		catch
		{
			return null;
		}

		// SAME TWO RULES AS BEFORE, now returning the addresses instead of a boolean: an empty answer
		// or ANY private address in the answer rejects the host, otherwise the whole validated
		// answer becomes the pin and the connect tries each address in order. Rejecting on ANY
		// private answer is deliberately conservative - a resolver that mixes public and private
		// answers for one name is exactly what a rebinding attempt looks like.
		return resolved.Length == 0 || resolved.Any(IsPrivateLocalOrInternalIp) ? null : resolved;
	}

	private static bool IsPrivateLocalOrInternalIp(IPAddress ip)
	{
		if (IPAddress.IsLoopback(ip))
		{
			return true;
		}

		// EVERY FORM THAT CARRIES AN IPv4 ADDRESS IS CLASSIFIED BY THE IPv4 ADDRESS IT CARRIES,
		// through the single classifier below. Classifying these by the V6 rules alone is how the
		// private-target guard was bypassed twice:
		//
		//  - "::ffff:10.0.0.1" (IPv4-mapped) is not loopback, not link-local, not site-local and not
		//    fc00::/7, so it came back PUBLIC and a redirect to http://[::ffff:10.0.0.1]/ reached the
		//    internal host (MEASURED pre-fix on .NET 10 for ::ffff:10.0.0.1, ::ffff:192.168.1.1,
		//    ::ffff:172.16.5.5 and ::ffff:169.254.1.1).
		//  - the IPv6 TRANSITION PREFIXES do the same thing less obviously, because the embedded
		//    IPv4 does not look like an IPv4 to any V6 check at all:
		//      64:ff9b::/96   NAT64 well-known prefix (RFC 6052) -> [64:ff9b::7f00:1]   is 127.0.0.1
		//      2002::/16      6to4 (RFC 3056)                     -> [2002:7f00:1::]    is 127.0.0.1
		//      2001:0000::/32 Teredo (RFC 4380 / RFC 8200)        -> last 32 bits, complemented
		//    All three were MEASURED as NOT private before this unwrap existed.
		//
		// Uri.Host preserves the bracketed form, so the literal really does arrive here in that
		// shape - and this is also the DNS path's choke point, where a resolver can hand back a
		// mapped or transition address just as easily.
		if (TryUnwrapEmbeddedIpv4(ip, out IPAddress? embeddedIpv4))
		{
			return IsPrivateOrReservedIpv4(embeddedIpv4);
		}

		if (ip.AddressFamily == AddressFamily.InterNetwork)
		{
			return IsPrivateOrReservedIpv4(ip);
		}

		if (ip.AddressFamily == AddressFamily.InterNetworkV6)
		{
			if (ip.IsIPv6LinkLocal
				|| ip.IsIPv6SiteLocal
				|| ip.Equals(IPAddress.IPv6Loopback)
				|| ip.Equals(IPAddress.IPv6None))
			{
				return true;
			}

			byte[] bytes = ip.GetAddressBytes();
			return (bytes[0] & 0xFE) == 0xFC; // fc00::/7 unique local
		}

		return true;
	}

	// THE ONE IPv4 CLASSIFIER. RFC1918 (10/8, 172.16/12, 192.168/16), link-local (169.254/16),
	// loopback (127/8), "this network" (0/8), shared address space / CGNAT (100.64/10), IETF
	// protocol assignments (192.0.0/24), benchmarking (198.18/15), and everything from 224.0.0.0
	// up (multicast, reserved and broadcast). Both families reach it: IPv4 directly, and the
	// embedding forms through TryUnwrapEmbeddedIpv4, so there is exactly one definition of
	// "private IPv4" in this file.
	private static bool IsPrivateOrReservedIpv4(IPAddress ipv4)
	{
		byte[] bytes = ipv4.GetAddressBytes();
		return IsClassicPrivateIpv4(bytes)
			|| (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
			|| (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
			|| (bytes[0] == 198 && bytes[1] is 18 or 19)
			|| bytes[0] >= 224;
	}

	// The original range set. Kept separate because the Teredo unwrap below asks "is the DIRECT
	// 32 bits already a private address?" and must not treat the complement of a public address
	// (whose first octet is often >= 224) as such.
	private static bool IsClassicPrivateIpv4(byte[] bytes)
	{
		return bytes[0] == 10
			|| (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
			|| (bytes[0] == 192 && bytes[1] == 168)
			|| (bytes[0] == 169 && bytes[1] == 254)
			|| bytes[0] == 127
			|| bytes[0] == 0;
	}

	// Unwraps every IPv6 form whose payload is an IPv4 address. Returns false for anything that is
	// not such a form, so a legitimate global IPv6 address is untouched: this never blocks a
	// PREFIX, it only reads the IPv4 an address claims to carry and lets the classifier judge it.
	private static bool TryUnwrapEmbeddedIpv4(IPAddress ip, out IPAddress embeddedIpv4)
	{
		embeddedIpv4 = IPAddress.None;

		//::ffff:a.b.c.d - the family's own representation of an IPv4 address.
		if (ip.IsIPv4MappedToIPv6)
		{
			embeddedIpv4 = ip.MapToIPv4();
			return true;
		}

		if (ip.AddressFamily != AddressFamily.InterNetworkV6)
		{
			return false;
		}

		byte[] bytes = ip.GetAddressBytes();

		// 64:ff9b::/96 NAT64 well-known prefix (RFC 6052 section 2.2). The u-octet is reserved and
		// MUST be zero in a mapping this prefix describes; for a well-formed /96 mapping the whole
		// u-field (bytes 8..11) is zero, and the embedded IPv4 is the last 32 bits (bytes 12..15).
		// Checking the FULL u-field is not decoration: bytes 12..15 of an address that merely SHARES
		// the 96-bit prefix are not an embedded IPv4, so unwrapping them anyway would invent private
		// targets out of unrelated global addresses. Gating on byte 8 alone left that window open for
		// a sharing address whose bytes 9..11 were non-zero.
		if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B
			&& bytes[8] == 0x00 && bytes[9] == 0x00 && bytes[10] == 0x00 && bytes[11] == 0x00)
		{
			embeddedIpv4 = new IPAddress(bytes[12..16]);
			return true;
		}

		// 2002::/16 6to4 (RFC 3056 section 2): the embedded IPv4 immediately follows the prefix,
		// bytes 2..5.
		if (bytes[0] == 0x20 && bytes[1] == 0x02)
		{
			embeddedIpv4 = new IPAddress(bytes[2..6]);
			return true;
		}

		// 2001:0000::/32 Teredo (RFC 4380 section 4 / RFC 8200). The Teredo IPv4 IS the embedded
		// IPv4 and the Teredo host id holds its complement, so the embedded IPv4 - the address the
		// packet is ultimately for - is the last 32 bits inverted, exactly as the task specifies.
		// That inversion is only meaningful when the embedded IPv4 is itself PUBLIC: as a Teredo host
		// id it is then the complement of a public address. A private ADDRESS reported as a Teredo
		// literal arrives in the same prefix with a LEGAL host id, and complementing there would
		// misread a public IPv4 (for example 192.0.2.45) as private - over-blocking, which this
		// guard must not do. So the direct 32 bits win when they are already private, and only the
		// complemented form is consulted otherwise.
		if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
		{
			IPAddress direct = new(bytes[12..16]);
			IPAddress complemented = new(
			[
				(byte)~bytes[12],
				(byte)~bytes[13],
				(byte)~bytes[14],
				(byte)~bytes[15],
			]);

			embeddedIpv4 = IsClassicPrivateIpv4(direct.GetAddressBytes()) ? direct : complemented;
			return true;
		}

		return false;
	}

	// The pinned connect: the address is taken from the request's own validated slot, so a request
	// that somehow arrives without one is refused rather than allowed to fall through to the
	// platform resolver. The rewrite seam is production-inert (identity); the tests use it to
	// observe the address and to reach a loopback listener.
	private async ValueTask<Stream> ConnectPinnedAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
	{
		HttpRequestMessage initial = context.InitialRequestMessage;
		DnsEndPoint endpoint = context.DnsEndPoint;

		// PROXY HOP: when a proxy is in use, SocketsHttpHandler hands this callback the PROXY's
		// endpoint (host and port), not the request's target. Pinning that connection to the
		// target's validated IP dialled <target-ip>:<proxy-port>, a SYN nobody answers, so every
		// proxied resolve timed out. With a proxy the TARGET's DNS is performed by the proxy, so
		// connection pinning cannot apply; the pre-request hostname/address guard in ResolveAsync
		// (which runs before every hop, proxy or not) remains the control for the target. The proxy
		// itself is the configured endpoint and is dialled exactly as the default handler would.
		if (IsProxyConnection(initial, endpoint))
		{
			Socket proxySocket = new(SocketType.Stream, ProtocolType.Tcp)
			{
				NoDelay = true,
			};

			try
			{
				await proxySocket.ConnectAsync(endpoint, cancellationToken);
				return new NetworkStream(proxySocket, ownsSocket: true);
			}
			catch
			{
				proxySocket.Dispose();
				throw;
			}
		}

		IPAddress[] validated = RequireValidatedAddresses(
			initial.Options.TryGetValue(ValidatedAddressOption, out IPAddress[]? value) ? value : null);

		// Every address here passed the guard. They are tried in the resolver's order and the first
		// that connects wins; the last failure is rethrown when none does. Cancellation stops the walk.
		Exception? lastFailure = null;
		foreach (IPAddress address in validated)
		{
			cancellationToken.ThrowIfCancellationRequested();
			IPEndPoint connectEndpoint = _connectEndpointRewriter(address, endpoint.Port);

			Socket socket = new(connectEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
			{
				NoDelay = true,
			};

			try
			{
				await socket.ConnectAsync(connectEndpoint, cancellationToken);
				return new NetworkStream(socket, ownsSocket: true);
			}
			catch (Exception ex)
			{
				socket.Dispose();
				if (ex is OperationCanceledException)
				{
					throw;
				}

				lastFailure = ex;
			}
		}

		throw lastFailure ?? new InvalidOperationException("No validated address was available to connect to.");
	}

	// The two shapes in which the handler dials a proxy rather than the request's origin
	// (MEASURED on .NET 10 with a loopback proxy):
	//  - HTTPS target (tunnel): the callback receives the handler's OWN synthetic CONNECT message,
	//    whose RequestUri is the proxy and which carries none of this request's options. The
	//    resolver itself only ever sends HEAD, so a CONNECT here can only be that tunnel.
	//  - HTTP target (forward proxy): the callback receives OUR request (it carries the pin), but
	//    the endpoint is the proxy, so it differs from the request URI's host or port.
	// Anything else is the origin and is pinned. A pinned request without a URI cannot be
	// compared and is treated as the origin, so ambiguity resolves toward the pin; a non-CONNECT
	// request without a pin still reaches RequireValidatedAddresses and is refused.
	private static bool IsProxyConnection(HttpRequestMessage initial, DnsEndPoint endpoint)
	{
		return initial.Method == HttpMethod.Connect
			? IsSameEndpoint(endpoint, initial.RequestUri)
			: initial.Options.TryGetValue(ValidatedAddressOption, out _)
				&& initial.RequestUri is not null
				&& !IsSameEndpoint(endpoint, initial.RequestUri);
	}

	// Host comparison is case-insensitive against the IDN (punycode) host, with IPv6 brackets
	// trimmed on both sides; the port must match exactly.
	private static bool IsSameEndpoint(DnsEndPoint endpoint, Uri? uri)
	{
		return uri is not null
			&& endpoint.Port == uri.Port
			&& string.Equals(
				endpoint.Host.Trim('[', ']'),
				uri.IdnHost.Trim('[', ']'),
				StringComparison.OrdinalIgnoreCase);
	}

	// The fail-closed decision, held as a pure predicate so it is testable on its own:
	// SocketsHttpConnectionContext is a sealed class the framework alone constructs, so the
	// callback cannot be invoked from a test. No validated address means no connection.
	public static IPAddress[] RequireValidatedAddresses(IPAddress[]? validated)
	{
		return validated is { Length: > 0 } ? validated : throw new InvalidOperationException(
			"A short-link connection was attempted without a validated address. The connection is pinned to the address the private-target guard approved, and an unpinned connect is exactly the DNS-rebinding window this guard closes, so the request is refused.");
	}

	// The resolver owns the invoker it creates (and, on the pinned path, the handler whose
	// ConnectCallback it installed), so it is the owner that must release them. Callers that
	// injected their own invoker keep ownership of it; the field is the single disposable
	// resource and disposing a caller-elected invoker twice is defined as a no-op by
	// HttpMessageInvoker, so a shared ownership here cannot fail a caller.
	public void Dispose()
	{
		_http.Dispose();
	}

	private static bool IsHttpOrHttps(Uri uri)
	{
		return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsRedirect(HttpStatusCode statusCode)
	{
		int code = (int)statusCode;
		return code is >= 300 and < 400;
	}

	private static ShortLinkResolveResponse Reject(string originalUrl, string errorCode)
	{
		return new ShortLinkResolveResponse(
			HelperClientDispatch.Rejected,
			originalUrl,
			originalUrl,
			errorCode,
			FromCache: false);
	}
}

internal static class ShortLinkProgram
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	public static async Task<int> RunAsync(string[] args)
	{
		if (args.Length != 1 || !string.Equals(args[0], "resolve-short-link", StringComparison.Ordinal))
		{
			return 2;
		}

		string input = await Console.In.ReadToEndAsync();
		if (string.IsNullOrWhiteSpace(input))
		{
			return 2;
		}

		ShortLinkResolveRequest? request;
		try
		{
			request = JsonSerializer.Deserialize<ShortLinkResolveRequest>(input, JsonOptions);
		}
		catch (JsonException)
		{
			return 2;
		}

		if (request is null)
		{
			return 2;
		}

		using SocketsHttpHandler handler = BuildHandler();
		ShortLinkResolver resolver = new(handler, ResolveDnsAsync);

		using CancellationTokenSource timeoutCts = new(TimeSpan.FromMilliseconds(1500));
		ShortLinkResolveResponse response = await resolver.ResolveAsync(request, timeoutCts.Token);

		string output = JsonSerializer.Serialize(response, JsonOptions);
		await Console.Out.WriteAsync(output.AsMemory(), CancellationToken.None);
		return 0;
	}

	// THE PRODUCTION TRANSPORT IS THE ONE THAT CAN BE PINNED. HttpClientHandler cannot accept a
	// connect callback, so every request it made was free to resolve the host a second time at
	// connect time - the DNS-rebinding window Defect A describes. SocketsHttpHandler is the same
	// HTTP stack underneath with that seam exposed, so the switch is a capability change, not a
	// behaviour change; the resolver installs the callback itself (the guard-approved address
	// travels on each request) and the hardened flags below are carried over unchanged.
	private static SocketsHttpHandler BuildHandler()
	{
		return new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			UseCookies = false,
			UseProxy = true,
			AutomaticDecompression = DecompressionMethods.None,
		};
	}

	private static Task<IPAddress[]> ResolveDnsAsync(string host, CancellationToken cancellationToken)
	{
		return Dns.GetHostAddressesAsync(host, cancellationToken);
	}
}
