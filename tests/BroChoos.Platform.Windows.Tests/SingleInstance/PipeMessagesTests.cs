using BroChoos.Platform.Windows.SingleInstance;
using BroChoos.Core.Ingress;
using BroChoos.Core.Ipc;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.SingleInstance;

public sealed class PipeMessagesTests
{
	[Fact]
	public void EncodeAndParseActivationRequest_RoundTrips()
	{
		ActivationRequest request = BuildRequest();
		byte[] frame = PipeMessages.EncodeActivationRequest(request);

		bool ok = PipeMessages.TryParseActivationRequest(frame, DateTimeOffset.UtcNow, out ActivationRequest? parsed, out string reasonCode);

		_ = ok.Should().BeTrue();
		_ = reasonCode.Should().BeEmpty();
		_ = parsed.Should().NotBeNull();
		_ = parsed!.NormalizedUrl.Should().Be(request.NormalizedUrl);
		_ = parsed.RequestId.Should().Be(request.RequestId);
	}

	[Fact]
	public void TryParseActivationRequest_InvalidFrame_ReturnsReasonCode()
	{
		byte[] malformedUtf8 = BuildMalformedUtf8Frame();

		bool ok = PipeMessages.TryParseActivationRequest(malformedUtf8, DateTimeOffset.UtcNow, out _, out string reasonCode);

		_ = ok.Should().BeFalse();
		_ = reasonCode.Should().Be("frame.utf8");
	}

	[Fact]
	public void EncodeAndParseAck_RoundTrips_And_IsBoundToExpectedRequestId()
	{
		Guid requestId = Guid.NewGuid();
		byte[] frame = PipeMessages.EncodeActivationAck(requestId, ActivationAckStatus.Accepted);

		bool ok = PipeMessages.TryParseActivationAck(frame, requestId, out ActivationProtocolAck? ack, out string reasonCode);

		_ = ok.Should().BeTrue();
		_ = reasonCode.Should().BeEmpty();
		_ = ack.Should().NotBeNull();
		_ = ack!.RequestId.Should().Be(requestId);
		_ = ack.Status.Should().Be(ActivationAckStatus.Accepted);
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

	private static byte[] BuildMalformedUtf8Frame()
	{
		byte[] body = [0xC3, 0x28];
		byte[] frame = new byte[ActivationProtocol.LengthPrefixBytes + body.Length];
		System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, ActivationProtocol.LengthPrefixBytes), body.Length);
		body.CopyTo(frame.AsSpan(ActivationProtocol.LengthPrefixBytes));
		return frame;
	}
}
