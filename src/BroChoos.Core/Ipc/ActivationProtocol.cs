using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BroChoos.Core.Ingress;

namespace BroChoos.Core.Ipc;

public enum ActivationAckStatus
{
	Accepted = 0,
	Rejected = 1,
}

public sealed record ActivationProtocolRequest(
	Guid RequestId,
	DateTimeOffset ExpiresAtUtc,
	string NormalizedUrl,
	int Version);

public sealed record ActivationProtocolAck(
	Guid RequestId,
	ActivationAckStatus Status,
	int Version);

public static class ActivationProtocol
{
	public const int ProtocolVersion = 1;
	public const int LengthPrefixBytes = 4;
	public const int MaxFrameBytes = 8 * 1024;
	public const int RequestFieldCount = 5;
	public const int AckFieldCount = 4;

	private const string RequestKind = "REQ";
	private const string AckKind = "ACK";

	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	public static byte[] EncodeRequest(ActivationRequest request)
	{
		string urlBase64 = Convert.ToBase64String(StrictUtf8.GetBytes(request.NormalizedUrl));
		string payload = string.Join(
			"|",
			RequestKind,
			ProtocolVersion.ToString(CultureInfo.InvariantCulture),
			request.RequestId.ToString("N"),
			request.ExpiresAtUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
			urlBase64);

		return EncodeFrame(payload);
	}

	public static bool TryDecodeRequest(ReadOnlySpan<byte> frame, DateTimeOffset nowUtc, out ActivationProtocolRequest? request, out string errorCode)
	{
		request = null;

		if (!TryDecodeFrame(frame, out string payload, out errorCode))
		{
			return false;
		}

		string[] fields = payload.Split('|', StringSplitOptions.None);
		if (fields.Length != RequestFieldCount)
		{
			errorCode = "request.field-count";
			return false;
		}

		if (!fields[0].Equals(RequestKind, StringComparison.Ordinal))
		{
			errorCode = "request.kind";
			return false;
		}

		if (!TryParseVersion(fields[1], out int version, out errorCode))
		{
			return false;
		}

		if (!Guid.TryParseExact(fields[2], "N", out Guid requestId) || requestId == Guid.Empty)
		{
			errorCode = "request.id";
			return false;
		}

		if (!long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long expiresUnixMs))
		{
			errorCode = "request.expiry";
			return false;
		}

		DateTimeOffset expiresAtUtc;
		try
		{
			expiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(expiresUnixMs);
		}
		catch (ArgumentOutOfRangeException)
		{
			errorCode = "request.expiry";
			return false;
		}

		if (expiresAtUtc <= nowUtc)
		{
			errorCode = "request.expired";
			return false;
		}

		if (!TryDecodeBase64Utf8(fields[4], out string normalizedUrl, out errorCode))
		{
			return false;
		}

		if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? uri) || uri.AbsoluteUri.Length > ActivationRequest.MaxUrlLength)
		{
			errorCode = "request.url";
			return false;
		}

		request = new ActivationProtocolRequest(requestId, expiresAtUtc, uri.AbsoluteUri, version);
		errorCode = string.Empty;
		return true;
	}

	public static byte[] EncodeAck(Guid requestId, ActivationAckStatus status)
	{
		if (requestId == Guid.Empty)
		{
			throw new ArgumentException("Request ID must be non-empty.", nameof(requestId));
		}

		string payload = string.Join(
			"|",
			AckKind,
			ProtocolVersion.ToString(CultureInfo.InvariantCulture),
			requestId.ToString("N"),
			status.ToString());

		return EncodeFrame(payload);
	}

	public static bool TryDecodeAck(ReadOnlySpan<byte> frame, Guid expectedRequestId, out ActivationProtocolAck? ack, out string errorCode)
	{
		ack = null;

		if (!TryDecodeFrame(frame, out string payload, out errorCode))
		{
			return false;
		}

		string[] fields = payload.Split('|', StringSplitOptions.None);
		if (fields.Length != AckFieldCount)
		{
			errorCode = "ack.field-count";
			return false;
		}

		if (!fields[0].Equals(AckKind, StringComparison.Ordinal))
		{
			errorCode = "ack.kind";
			return false;
		}

		if (!TryParseVersion(fields[1], out int version, out errorCode))
		{
			return false;
		}

		if (!Guid.TryParseExact(fields[2], "N", out Guid requestId) || requestId == Guid.Empty)
		{
			errorCode = "ack.id";
			return false;
		}

		if (requestId != expectedRequestId)
		{
			errorCode = "ack.request-mismatch";
			return false;
		}

		if (!TryParseAckStatus(fields[3], out ActivationAckStatus status))
		{
			errorCode = "ack.status";
			return false;
		}

		ack = new ActivationProtocolAck(requestId, status, version);
		errorCode = string.Empty;
		return true;
	}

	private static byte[] EncodeFrame(string payload)
	{
		byte[] payloadBytes = StrictUtf8.GetBytes(payload);
		int frameLength = LengthPrefixBytes + payloadBytes.Length;

		if (frameLength > MaxFrameBytes)
		{
			throw new ArgumentException($"Activation frame exceeds the {MaxFrameBytes}-byte limit.", nameof(payload));
		}

		byte[] frame = new byte[frameLength];
		BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, LengthPrefixBytes), payloadBytes.Length);
		payloadBytes.CopyTo(frame.AsSpan(LengthPrefixBytes));
		return frame;
	}

	private static bool TryDecodeFrame(ReadOnlySpan<byte> frame, out string payload, out string errorCode)
	{
		payload = string.Empty;

		if (frame.Length < LengthPrefixBytes)
		{
			errorCode = "frame.truncated";
			return false;
		}

		if (frame.Length > MaxFrameBytes)
		{
			errorCode = "frame.oversized";
			return false;
		}

		int payloadLength = BinaryPrimitives.ReadInt32BigEndian(frame[..LengthPrefixBytes]);
		if (payloadLength is <= 0 or > (MaxFrameBytes - LengthPrefixBytes))
		{
			errorCode = "frame.length";
			return false;
		}

		int expectedFrameLength = LengthPrefixBytes + payloadLength;
		if (frame.Length < expectedFrameLength)
		{
			errorCode = "frame.truncated";
			return false;
		}

		if (frame.Length > expectedFrameLength)
		{
			errorCode = "frame.trailing";
			return false;
		}

		try
		{
			payload = StrictUtf8.GetString(frame.Slice(LengthPrefixBytes, payloadLength));
		}
		catch (DecoderFallbackException)
		{
			errorCode = "frame.utf8";
			return false;
		}

		errorCode = string.Empty;
		return true;
	}

	private static bool TryParseVersion(string value, out int version, out string errorCode)
	{
		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
		{
			errorCode = "frame.version";
			return false;
		}

		if (version != ProtocolVersion)
		{
			errorCode = "frame.version";
			return false;
		}

		errorCode = string.Empty;
		return true;
	}

	private static bool TryDecodeBase64Utf8(string base64, out string value, out string errorCode)
	{
		value = string.Empty;

		byte[] bytes;
		try
		{
			bytes = Convert.FromBase64String(base64);
		}
		catch (FormatException)
		{
			errorCode = "request.url";
			return false;
		}

		try
		{
			value = StrictUtf8.GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			errorCode = "frame.utf8";
			return false;
		}

		errorCode = string.Empty;
		return true;
	}

	private static bool TryParseAckStatus(string token, out ActivationAckStatus status)
	{
		if (token.Equals(nameof(ActivationAckStatus.Accepted), StringComparison.Ordinal))
		{
			status = ActivationAckStatus.Accepted;
			return true;
		}

		if (token.Equals(nameof(ActivationAckStatus.Rejected), StringComparison.Ordinal))
		{
			status = ActivationAckStatus.Rejected;
			return true;
		}

		status = default;
		return false;
	}
}
