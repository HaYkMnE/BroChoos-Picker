using System.Globalization;

namespace BroChoos.Core.Privacy;

// A minimal RFC 4291 section 2.2 text parser for IPv6 literals, so Core can inspect address
// bytes without System.Net (banned in Core by ForbiddenApiTests to stay platform-neutral).
// Accepts: eight 1-4 digit hex groups, one optional "::" compression, an optional trailing
// dotted IPv4 (counts as two groups) and an optional "%zone" suffix, which is ignored.
// Inputs here come from Uri.Host, which has already validated the literal; the parser is
// still strict on its own and is checked against IPAddress.TryParse in the Core tests.
//
// IT IS NOT A BYTE-FOR-BYTE CLONE OF IPAddress.TryParse. Every input this parser accepts is
// accepted by .NET with the same bytes, but it rejects some inputs .NET accepts. Measured on
// .NET 10.0.14 and pinned in Ipv6LiteralTests:
// - Zone: this parser rejects an empty zone ("fe80::1%") and a zone containing '%', ']' or
//   whitespace. .NET also rejects ']', but accepts the other three.
// - Dotted IPv4: this parser rejects any multi-digit octet with a leading zero. .NET does the
//   same in the first three octets ("::045.1.2.3"), and for any octet starting "00", but in the
//   LAST octet it accepts a single leading zero and reads the rest as decimal: "::1.2.3.045" is
//   45 and the four-digit "::1.2.3.0255" is 255. That rule depends on the octet's position, so
//   it is not copied here.
internal static class Ipv6Literal
{
	public static bool TryParse(string text, out byte[] bytes)
	{
		bytes = new byte[16];
		int zone = text.IndexOf('%', StringComparison.Ordinal);
		if (zone >= 0 && !IsValidZone(text.AsSpan(zone + 1)))
		{
			return false;
		}

		string address = zone >= 0 ? text[..zone] : text;

		int gap = address.IndexOf("::", StringComparison.Ordinal);
		if (gap >= 0 && address.IndexOf("::", gap + 1, StringComparison.Ordinal) >= 0)
		{
			return false;
		}

		List<byte> head = [];
		List<byte> tail = [];
		bool parsed = gap < 0
			? TryParseGroups(address, head, allowIpv4: true)
			: TryParseGroups(address[..gap], head, allowIpv4: false)
				&& TryParseGroups(address[(gap + 2)..], tail, allowIpv4: true);
		if (!parsed)
		{
			return false;
		}

		int total = head.Count + tail.Count;
		if (gap < 0 ? total != 16 : total > 14)
		{
			return false;
		}

		head.CopyTo(bytes, 0);
		tail.CopyTo(bytes, 16 - tail.Count);
		return true;
	}

	private static bool TryParseGroups(string part, List<byte> output, bool allowIpv4)
	{
		if (part.Length == 0)
		{
			return true;
		}

		string[] groups = part.Split(':');
		for (int i = 0; i < groups.Length; i++)
		{
			string group = groups[i];
			if (allowIpv4 && i == groups.Length - 1 && group.Contains('.', StringComparison.Ordinal))
			{
				string[] octets = group.Split('.');
				if (octets.Length != 4)
				{
					return false;
				}

				foreach (string octet in octets)
				{
					if (octet.Length is 0 or > 3
						|| (octet.Length > 1 && octet[0] == '0')
						|| !byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out byte value))
					{
						return false;
					}

					output.Add(value);
				}

				continue;
			}

			if (group.Length is 0 or > 4
				|| !ushort.TryParse(group, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort word))
			{
				return false;
			}

			output.Add((byte)(word >> 8));
			output.Add((byte)word);
		}

		return true;
	}

	private static bool IsValidZone(ReadOnlySpan<char> zone)
	{
		if (zone.IsEmpty)
		{
			return false;
		}

		foreach (char c in zone)
		{
			if (c is ']' or '%' || char.IsWhiteSpace(c))
			{
				return false;
			}
		}

		return true;
	}
}
