using BroChoos.Core.Ingress;

namespace BroChoos.Core.Privacy;

public sealed record ActivationHistoryProjectionInput(
	DateTimeOffset TimestampUtc,
	string Url,
	string BrowserId,
	string? ProfileId,
	SourceEvidence SourceEvidence,
	string? MatchedRuleId);

public sealed record ActivationHistoryRecord(
	DateTimeOffset TimestampUtc,
	string MainDomain,
	string BrowserId,
	string? ProfileId,
	string? ConfirmedSourceApp,
	string? MatchedRuleId);

public static class ActivationHistoryProjection
{
	private static readonly HashSet<string> TwoLevelSuffixes =
	[
		"co.uk",
		"org.uk",
		"ac.uk",
		"gov.uk",
		"me.uk",
		"net.uk",
		"ltd.uk",
		"plc.uk",
		"com.au",
		"net.au",
		"org.au",
		"edu.au",
		"gov.au",
		"co.jp",
		"or.jp",
		"ne.jp",
		"ac.jp",
		"go.jp",
		"co.nz",
		"org.nz",
		"net.nz",
		"ac.nz",
		"govt.nz",
		"com.br",
		"net.br",
		"org.br",
		"gov.br",
		"co.in",
		"net.in",
		"org.in",
		"firm.in",
		"gen.in",
		"ind.in",
		"ac.in",
		"edu.in",
		"gov.in",
		"res.in",
		"nic.in",
		"com.cn",
		"net.cn",
		"org.cn",
		"gov.cn",
		"edu.cn",
		"com.hk",
		"com.tw",
		"com.sg",
		"com.my",
		"com.mx",
		"com.ar",
		"com.co",
		"com.pe",
		"com.tr",
		"com.ua",
		"com.pl",
		"com.vn",
		"com.ph",
		"com.pk",
		"com.bd",
		"com.eg",
		"com.sa",
		"com.ng",
		"co.za",
		"co.ke",
		"co.kr",
		"or.kr",
		"co.id",
		"co.il",
		"co.th",
	];

	public static ActivationHistoryRecord? Project(ActivationHistoryProjectionInput input)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentException.ThrowIfNullOrWhiteSpace(input.Url);
		ArgumentException.ThrowIfNullOrWhiteSpace(input.BrowserId);

		if (!Uri.TryCreate(input.Url, UriKind.Absolute, out Uri? uri))
		{
			return null;
		}

		string host = uri.Host.Trim().ToLowerInvariant();
		if (string.IsNullOrWhiteSpace(host) || IsPrivateLocalOrInternal(host))
		{
			return null;
		}

		string mainDomain = GetMainDomain(host);
		string? confirmedSource = input.SourceEvidence.Confidence == SourceConfidence.Confirmed
			? input.SourceEvidence.DisplayProjection
			: null;

		return new ActivationHistoryRecord(
			input.TimestampUtc,
			mainDomain,
			input.BrowserId,
			input.ProfileId,
			confirmedSource,
			input.MatchedRuleId);
	}

	private static string GetMainDomain(string host)
	{
		if (LooksLikeIpLiteral(host))
		{
			return host;
		}

		string[] labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
		if (labels.Length <= 2)
		{
			return host;
		}

		string twoLevelSuffix = $"{labels[^2]}.{labels[^1]}";
		return TwoLevelSuffixes.Contains(twoLevelSuffix) && labels.Length >= 3
			? $"{labels[^3]}.{twoLevelSuffix}"
			: $"{labels[^2]}.{labels[^1]}";
	}

	private static bool IsPrivateLocalOrInternal(string host)
	{
		if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (TryParseIpv4(host, out byte b1, out byte b2, out _, out _))
		{
			return IsPrivateIpv4(b1, b2);
		}

		if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
		{
			host = host[1..^1];
		}

		if (host.Contains(':', StringComparison.Ordinal))
		{
			if (Ipv6Literal.TryParse(host, out byte[] address) && HasPrivateEmbeddedIpv4(address))
			{
				return true;
			}

			string lower = host.ToLowerInvariant();
			return lower == "::1"
				|| lower.StartsWith("fc", StringComparison.Ordinal)
				|| lower.StartsWith("fd", StringComparison.Ordinal)
				|| lower.StartsWith("fe8", StringComparison.Ordinal)
				|| lower.StartsWith("fe9", StringComparison.Ordinal)
				|| lower.StartsWith("fea", StringComparison.Ordinal)
				|| lower.StartsWith("feb", StringComparison.Ordinal);
		}

		return false;
	}

	// An IPv6 literal can carry an IPv4 address that the IPv4 rule must still judge:
	// IPv4-mapped (::ffff:a.b.c.d), NAT64 (64:ff9b::/96, IPv4 in the last 4 bytes) and
	// 6to4 (2002::/16, IPv4 in bytes 2..5). Anything else is not inspected here.
	private static bool HasPrivateEmbeddedIpv4(byte[] bytes)
	{
		bool isMapped = bytes.AsSpan(0, 10).IndexOfAnyExcept((byte)0) < 0 && bytes[10] == 0xff && bytes[11] == 0xff;
		if (isMapped)
		{
			return IsPrivateIpv4(bytes[12], bytes[13]);
		}

		bool isNat64 = bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
			&& bytes.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0;
		return isNat64
			? IsPrivateIpv4(bytes[12], bytes[13])
			: bytes[0] == 0x20 && bytes[1] == 0x02 && IsPrivateIpv4(bytes[2], bytes[3]);
	}

	// Loopback, RFC 1918, link-local, "this network" (0.0.0.0/8) and CGNAT (100.64.0.0/10).
	private static bool IsPrivateIpv4(byte b1, byte b2)
	{
		return b1 == 0
			|| (b1 == 100 && b2 is >= 64 and <= 127)
			|| b1 == 10
			|| b1 == 127
			|| (b1 == 192 && b2 == 168)
			|| (b1 == 172 && b2 is >= 16 and <= 31)
			|| (b1 == 169 && b2 == 254);
	}

	private static bool LooksLikeIpLiteral(string host)
	{
		if (TryParseIpv4(host, out _, out _, out _, out _))
		{
			return true;
		}

		if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
		{
			host = host[1..^1];
		}

		return host.Contains(':', StringComparison.Ordinal);
	}

	private static bool TryParseIpv4(string host, out byte a, out byte b, out byte c, out byte d)
	{
		a = b = c = d = 0;
		string[] segments = host.Split('.', StringSplitOptions.None);
		return segments.Length == 4
			&& byte.TryParse(segments[0], out a)
			&& byte.TryParse(segments[1], out b)
			&& byte.TryParse(segments[2], out c)
			&& byte.TryParse(segments[3], out d);
	}
}
