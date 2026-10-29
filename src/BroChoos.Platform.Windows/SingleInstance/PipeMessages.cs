using BroChoos.Core.Ipc;
using BroChoos.Core.Ingress;

namespace BroChoos.Platform.Windows.SingleInstance;

internal static class PipeMessages
{
	public static byte[] EncodeActivationRequest(ActivationRequest request)
	{
		return ActivationProtocol.EncodeRequest(request);
	}

	public static bool TryParseActivationRequest(ReadOnlySpan<byte> frame, DateTimeOffset nowUtc, out ActivationRequest? request, out string reasonCode)
	{
		request = null;

		if (!ActivationProtocol.TryDecodeRequest(frame, nowUtc, out ActivationProtocolRequest? decoded, out string errorCode))
		{
			reasonCode = errorCode;
			return false;
		}

		request = new ActivationRequest(
			decoded!.NormalizedUrl,
			arrivalMonotonicStamp: 0,
			ActivationTriggerContext.ForwardedFromSecondary,
			SourceEvidence.Probable("local-named-pipe"),
			ActivationModifiers.None,
			decoded.RequestId,
			decoded.Version,
			decoded.ExpiresAtUtc);

		reasonCode = string.Empty;
		return true;
	}

	public static byte[] EncodeActivationAck(Guid requestId, ActivationAckStatus status)
	{
		return ActivationProtocol.EncodeAck(requestId, status);
	}

	public static bool TryParseActivationAck(ReadOnlySpan<byte> frame, Guid expectedRequestId, out ActivationProtocolAck? ack, out string reasonCode)
	{
		return ActivationProtocol.TryDecodeAck(frame, expectedRequestId, out ack, out reasonCode);
	}
}
