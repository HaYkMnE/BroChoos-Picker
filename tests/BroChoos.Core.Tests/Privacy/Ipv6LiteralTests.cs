using System.Net;
using BroChoos.Core.Privacy;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Privacy;

// Ipv6Literal replaces IPAddress.TryParse inside Core (System.Net is banned there). These
// tests pin it to the BCL parser, which is allowed in test assemblies, as the reference.
public sealed class Ipv6LiteralTests
{
	[Theory]
	[InlineData("::")]
	[InlineData("::1")]
	[InlineData("1::")]
	[InlineData("fe80::1")]
	[InlineData("fd00::1")]
	[InlineData("2001:db8::8a2e:370:7334")]
	[InlineData("2001:0db8:0000:0000:0000:ff00:0042:8329")]
	[InlineData("1:2:3:4:5:6:7:8")]
	[InlineData("1:2:3:4:5:6:7::")]
	[InlineData("::2:3:4:5:6:7:8")]
	[InlineData("::ffff:127.0.0.1")]
	[InlineData("::ffff:8.8.8.8")]
	[InlineData("::ffff:7f00:1")]
	[InlineData("64:ff9b::7f00:1")]
	[InlineData("64:ff9b::808:808")]
	[InlineData("64:ff9b::a00:1:1")]
	[InlineData("64:ff9b::192.168.0.1")]
	[InlineData("2002:7f00:1::1")]
	[InlineData("2002:c0a8:1::")]
	[InlineData("1:2:3:4:5:6:1.2.3.4")]
	[InlineData("ABCD:EF01::1")]
	[InlineData("fe80::1%3")]
	public void TryParse_ValidLiteral_MatchesSystemNetBytes(string text)
	{
		_ = IPAddress.TryParse(text, out IPAddress? reference).Should().BeTrue();

		bool parsed = Ipv6Literal.TryParse(text, out byte[] bytes);

		_ = parsed.Should().BeTrue();
		_ = bytes.Should().Equal(reference!.GetAddressBytes());
	}

	[Theory]
	[InlineData("")]
	[InlineData(":")]
	[InlineData(":::")]
	[InlineData("1::2::3")]
	[InlineData(":1::")]
	[InlineData("1:2:3:4:5:6:7")]
	[InlineData("1:2:3:4:5:6:7:8:9")]
	[InlineData("1:2:3:4:5:6:7:8::")]
	[InlineData("::1:2:3:4:5:6:7:8")]
	[InlineData("12345::1")]
	[InlineData("g::1")]
	[InlineData("+1::1")]
	[InlineData(" ::1")]
	[InlineData("::256.0.0.1")]
	[InlineData("::1.2.3")]
	[InlineData("1.2.3.4::")]
	[InlineData("::1.2.3.4:5")]
	public void TryParse_InvalidLiteral_ReturnsFalse(string text)
	{
		_ = Ipv6Literal.TryParse(text, out _).Should().BeFalse();
	}

	// ZONE. .NET rejects ']' in a zone; it accepts an empty zone, '%' and whitespace, which
	// Ipv6Literal rejects on purpose (stricter, see the class comment). The reference result is
	// asserted too, so a .NET change in either direction shows up here.
	[Theory]
	[InlineData("fe80::1%a]", false)]
	[InlineData("fe80::1%", true)]
	[InlineData("fe80::1%a%b", true)]
	[InlineData("fe80::1%a b", true)]
	[InlineData("fe80::1% 3", true)]
	[InlineData("fe80::1%3 ", true)]
	[InlineData("fe80::1%a\tb", true)]
	public void TryParse_BadZone_ReturnsFalse_AndPinsSystemNet(string text, bool systemNetAccepts)
	{
		_ = IPAddress.TryParse(text, out _).Should().Be(systemNetAccepts);

		_ = Ipv6Literal.TryParse(text, out _).Should().BeFalse();
	}

	// LEADING-ZERO OCTET, 2-3 DIGITS. .NET rejects it in the first three octets and accepts it
	// in the last one ("045" read as decimal 45); Ipv6Literal rejects it in every position.
	[Theory]
	[InlineData("::045.1.2.3", false)]
	[InlineData("::1.045.2.3", false)]
	[InlineData("::1.2.3.00", false)]
	[InlineData("::1.2.3.045", true)]
	[InlineData("::1.2.3.09", true)]
	public void TryParse_LeadingZeroOctet_ReturnsFalse_AndPinsSystemNet(string text, bool systemNetAccepts)
	{
		_ = IPAddress.TryParse(text, out _).Should().Be(systemNetAccepts);

		_ = Ipv6Literal.TryParse(text, out _).Should().BeFalse();
	}

	// FOUR-DIGIT OCTET WITH A LEADING ZERO. .NET accepts it only as the last octet and only
	// with a single leading zero ("0255" -> 255, "0045" rejected); the rule is position-dependent,
	// so Ipv6Literal does not copy it and rejects every four-digit octet.
	[Theory]
	[InlineData("::0255.1.2.3", false)]
	[InlineData("::1.2.0255.4", false)]
	[InlineData("::1.2.3.0045", false)]
	[InlineData("::1.2.3.0256", false)]
	[InlineData("::1.2.3.0255", true)]
	public void TryParse_FourDigitOctet_ReturnsFalse_AndPinsSystemNet(string text, bool systemNetAccepts)
	{
		_ = IPAddress.TryParse(text, out _).Should().Be(systemNetAccepts);

		_ = Ipv6Literal.TryParse(text, out _).Should().BeFalse();
	}

	[Theory]
	[InlineData("::1.2.3.0")]
	[InlineData("::0.0.0.0")]
	[InlineData("fe80::1%eth0")]
	public void TryParse_SingleZeroOctetOrPlainZone_StillMatchesSystemNetBytes(string text)
	{
		_ = IPAddress.TryParse(text, out IPAddress? reference).Should().BeTrue();

		_ = Ipv6Literal.TryParse(text, out byte[] bytes).Should().BeTrue();
		_ = bytes.Should().Equal(reference!.GetAddressBytes());
	}
}
