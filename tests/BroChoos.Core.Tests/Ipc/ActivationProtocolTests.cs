using BroChoos.Core.Ingress;
using BroChoos.Core.Ipc;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Ipc;

public sealed class ActivationProtocolTests
{
	[Fact]
	public void Request_RoundTrip_Succeeds()
	{
		ActivationRequest activation = BuildRequest();
		byte[] frame = ActivationProtocol.EncodeRequest(activation);

		bool ok = ActivationProtocol.TryDecodeRequest(frame, DateTimeOffset.UtcNow, out ActivationProtocolRequest? decoded, out string error);

		_ = ok.Should().BeTrue();
		_ = error.Should().BeEmpty();
		_ = decoded.Should().NotBeNull();
		_ = decoded!.RequestId.Should().Be(activation.RequestId);
		_ = decoded.NormalizedUrl.Should().Be(activation.NormalizedUrl);
		_ = decoded.Version.Should().Be(ActivationProtocol.ProtocolVersion);
	}

	[Fact]
	public void Ack_RoundTrip_And_RequestIdBinding_Succeeds()
	{
		Guid requestId = Guid.NewGuid();
		byte[] frame = ActivationProtocol.EncodeAck(requestId, ActivationAckStatus.Accepted);

		bool ok = ActivationProtocol.TryDecodeAck(frame, requestId, out ActivationProtocolAck? ack, out string error);

		_ = ok.Should().BeTrue();
		_ = error.Should().BeEmpty();
		_ = ack.Should().NotBeNull();
		_ = ack!.RequestId.Should().Be(requestId);
		_ = ack.Status.Should().Be(ActivationAckStatus.Accepted);
	}

	[Fact]
	public void DecodeRequest_TruncatedFrame_IsRejectedDeterministically()
	{
		byte[] frame = ActivationProtocol.EncodeRequest(BuildRequest());
		byte[] truncated = frame[..^1];

		bool ok = ActivationProtocol.TryDecodeRequest(truncated, DateTimeOffset.UtcNow, out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("frame.truncated");
	}

	[Fact]
	public void DecodeRequest_TrailingBytes_AreRejectedDeterministically()
	{
		byte[] frame = ActivationProtocol.EncodeRequest(BuildRequest());
		byte[] trailing = [.. frame, 0x00];

		bool ok = ActivationProtocol.TryDecodeRequest(trailing, DateTimeOffset.UtcNow, out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("frame.trailing");
	}

	[Fact]
	public void DecodeRequest_OversizedFrame_IsRejectedDeterministically()
	{
		byte[] oversized = new byte[ActivationProtocol.MaxFrameBytes + 1];

		bool ok = ActivationProtocol.TryDecodeRequest(oversized, DateTimeOffset.UtcNow, out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("frame.oversized");
	}

	[Fact]
	public void DecodeRequest_OverFieldCount_IsRejectedDeterministically()
	{
		byte[] frame = BuildRequestFramePayload("REQ|1|" + Guid.NewGuid().ToString("N") + "|4102444800000|aHR0cHM6Ly9leGFtcGxlLmNvbS8=|extra");

		bool ok = ActivationProtocol.TryDecodeRequest(frame, DateTimeOffset.UtcNow, out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("request.field-count");
	}

	[Fact]
	public void DecodeRequest_WrongVersion_IsRejectedDeterministically()
	{
		byte[] frame = BuildRequestFramePayload("REQ|999|" + Guid.NewGuid().ToString("N") + "|4102444800000|aHR0cHM6Ly9leGFtcGxlLmNvbS8=");

		bool ok = ActivationProtocol.TryDecodeRequest(frame, DateTimeOffset.UtcNow, out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("frame.version");
	}

	[Fact]
	public void DecodeRequest_MalformedUtf8_IsRejectedDeterministically()
	{
		byte[] frame = BuildMalformedUtf8Frame();

		bool ok = ActivationProtocol.TryDecodeRequest(frame, DateTimeOffset.UtcNow, out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("frame.utf8");
	}

	[Fact]
	public void DecodeAck_RequestIdMismatch_IsRejectedDeterministically()
	{
		Guid requestId = Guid.NewGuid();
		byte[] frame = ActivationProtocol.EncodeAck(requestId, ActivationAckStatus.Accepted);

		bool ok = ActivationProtocol.TryDecodeAck(frame, Guid.NewGuid(), out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("ack.request-mismatch");
	}

	[Fact]
	public void DecodeAck_NumericStatusToken_IsRejectedDeterministically()
	{
		Guid requestId = Guid.NewGuid();
		byte[] frame = BuildRequestFramePayload($"ACK|1|{requestId:N}|1");

		bool ok = ActivationProtocol.TryDecodeAck(frame, requestId, out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("ack.status");
	}

	[Fact]
	public void DecodeRequest_EmptyRequestId_IsRejectedDeterministically()
	{
		byte[] frame = BuildRequestFramePayload("REQ|1|00000000000000000000000000000000|4102444800000|aHR0cHM6Ly9leGFtcGxlLmNvbS8=");

		bool ok = ActivationProtocol.TryDecodeRequest(frame, DateTimeOffset.UtcNow, out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("request.id");
	}

	[Fact]
	public void DecodeRequest_InvalidRequestIdFormat_IsRejectedDeterministically()
	{
		byte[] frame = BuildRequestFramePayload("REQ|1|not-a-guid|4102444800000|aHR0cHM6Ly9leGFtcGxlLmNvbS8=");

		bool ok = ActivationProtocol.TryDecodeRequest(frame, DateTimeOffset.UtcNow, out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("request.id");
	}

	[Fact]
	public void DecodeRequest_ExpiredRequest_IsRejectedDeterministically()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		long expiredMs = now.AddMinutes(-1).ToUnixTimeMilliseconds();
		byte[] frame = BuildRequestFramePayload($"REQ|1|{Guid.NewGuid():N}|{expiredMs}|aHR0cHM6Ly9leGFtcGxlLmNvbS8=");

		bool ok = ActivationProtocol.TryDecodeRequest(frame, now, out _, out string error);

		_ = ok.Should().BeFalse();
		_ = error.Should().Be("request.expired");
	}

	// THE ENCODER AND THE DECODER MUST AGREE ON WHICH STRING THE LENGTH CAP APPLIES TO.
	// They did not: ActivationRequest checked the caller's input, ActivationProtocol checks
	// the decoded value, and Uri.AbsoluteUri percent-escapes non-ASCII between the two. A
	// Cyrillic URL accepted by the constructor therefore produced a frame the decoder
	// refused — accepted on first activation, silently dropped on the second. This test
	// drives the real pair at the largest URL the constructor accepts, so the two checks
	// cannot drift apart again without this failing.
	[Theory]
	[InlineData('\u041F', 338)] // Cyrillic: 6 escaped characters each, storing exactly 2048.
	[InlineData('\u4E2D', 225)] // CJK: 9 escaped characters each.
	public void Request_RoundTrip_Succeeds_AtNonAsciiLengthBoundary(char filler, int acceptedCount)
	{
		ActivationRequest activation = new(
			"https://example.com/" + new string(filler, acceptedCount),
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Confirmed("test"),
			ActivationModifiers.None,
			Guid.NewGuid(),
			ActivationProtocol.ProtocolVersion,
			DateTimeOffset.UtcNow.AddMinutes(5));

		_ = activation.NormalizedUrl.Length.Should().BeLessThanOrEqualTo(ActivationRequest.MaxUrlLength);

		byte[] frame = ActivationProtocol.EncodeRequest(activation);

		bool ok = ActivationProtocol.TryDecodeRequest(frame, DateTimeOffset.UtcNow, out ActivationProtocolRequest? decoded, out string error);

		_ = ok.Should().BeTrue(
			FormattableString.Invariant($"the decoder must accept the frame the encoder built from an accepted request; error was '{error}'"));
		_ = decoded.Should().NotBeNull();
		_ = decoded!.NormalizedUrl.Should().Be(activation.NormalizedUrl);
	}

	// THE INVARIANT THE TWO COMPONENTS MUST SHARE: a request the CONSTRUCTOR accepted must
	// survive the round trip. The constructor and the decoder each enforce the length cap, and
	// they must enforce it against the same string — the stored NormalizedUrl. When the
	// constructor measured the caller's input instead, a Cyrillic URL whose input was under
	// 2048 stored an escaped AbsoluteUri of 2054, and the decoder — measuring the stored value,
	// as it must — refused the frame the encoder had just produced from that very request. The
	// URL was accepted on first activation and silently dropped on the second, which arrives
	// over IPC. Either outcome is fine for a given URL; what may never happen is acceptance
	// followed by refusal, which is exactly what this test forbids.
	[Theory]
	[InlineData('\u041F', 339)] // One Cyrillic character past the largest accepted input.
	[InlineData('\u4E2D', 226)] // One CJK character past the largest accepted input.
	public void Request_AcceptedByConstructor_IsNeverRefusedByDecoder(char filler, int count)
	{
		ActivationRequest activation;
		try
		{
			activation = new ActivationRequest(
				"https://example.com/" + new string(filler, count),
				1,
				ActivationTriggerContext.CommandLine,
				SourceEvidence.Confirmed("test"),
				ActivationModifiers.None,
				Guid.NewGuid(),
				ActivationProtocol.ProtocolVersion,
				DateTimeOffset.UtcNow.AddMinutes(5));
		}
		catch (ArgumentException)
		{
			// Rejected up front. Correct for a URL over the cap, and nothing to round-trip.
			return;
		}

		byte[] frame = ActivationProtocol.EncodeRequest(activation);

		bool ok = ActivationProtocol.TryDecodeRequest(frame, DateTimeOffset.UtcNow, out ActivationProtocolRequest? decoded, out string error);

		_ = ok.Should().BeTrue(
			FormattableString.Invariant($"the constructor ACCEPTED this request, so the decoder must accept its frame; the decoder refused it with '{error}'"));
		_ = decoded.Should().NotBeNull();
		_ = decoded!.NormalizedUrl.Should().Be(activation.NormalizedUrl);
	}

	// The length cap applies to the canonical URL (Uri.AbsoluteUri), not to the raw decoded
	// text: a long raw URL whose dot segments collapse to a short canonical URL is accepted,
	// and a short raw non-ASCII URL that percent-escapes past the cap is refused.
	[Fact]
	public void DecodeRequest_CapIsAppliedToCanonicalUrl_NotRawText()
	{
		string rawUrl = "https://example.com/" + string.Concat(Enumerable.Repeat("a/../", 500)) + "x";
		_ = rawUrl.Length.Should().BeGreaterThan(ActivationRequest.MaxUrlLength);
		byte[] frame = BuildRequestFrameForUrl(rawUrl);

		bool ok = ActivationProtocol.TryDecodeRequest(frame, DateTimeOffset.UtcNow, out ActivationProtocolRequest? decoded, out string error);

		_ = ok.Should().BeTrue(FormattableString.Invariant($"canonical URL fits the cap; error was '{error}'"));
		_ = decoded!.NormalizedUrl.Should().Be("https://example.com/x");
	}

	[Fact]
	public void DecodeRequest_CanonicalUrlOverCap_IsRejectedWithUrlError()
	{
		string rawUrl = "https://example.com/" + new string('\u041F', 339);
		byte[] frame = BuildRequestFrameForUrl(rawUrl);

		bool ok = ActivationProtocol.TryDecodeRequest(frame, DateTimeOffset.UtcNow, out ActivationProtocolRequest? decoded, out string error);

		_ = ok.Should().BeFalse();
		_ = decoded.Should().BeNull();
		_ = error.Should().Be("request.url");
	}

	private static byte[] BuildRequestFrameForUrl(string rawUrl)
	{
		string payload = string.Join(
			"|",
			"REQ",
			"1",
			Guid.NewGuid().ToString("N"),
			DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
			Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(rawUrl)));
		return BuildRequestFramePayload(payload);
	}

	private static ActivationRequest BuildRequest()
	{
		return new ActivationRequest(
			"https://example.com/",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Confirmed("test"),
			ActivationModifiers.None,
			Guid.NewGuid(),
			ActivationProtocol.ProtocolVersion,
			DateTimeOffset.UtcNow.AddMinutes(5));
	}

	private static byte[] BuildRequestFramePayload(string payload)
	{
		byte[] body = System.Text.Encoding.UTF8.GetBytes(payload);
		byte[] frame = new byte[ActivationProtocol.LengthPrefixBytes + body.Length];
		System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, ActivationProtocol.LengthPrefixBytes), body.Length);
		body.CopyTo(frame.AsSpan(ActivationProtocol.LengthPrefixBytes));
		return frame;
	}

	private static byte[] BuildMalformedUtf8Frame()
	{
		byte[] body = [0xC3, 0x28];
		byte[] frame = new byte[ActivationProtocol.LengthPrefixBytes + body.Length];
		System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, ActivationProtocol.LengthPrefixBytes), body.Length);
		body.CopyTo(frame.AsSpan(ActivationProtocol.LengthPrefixBytes));
		return frame;
	}
}
