namespace BroChoos.Core.Ingress;

public enum ActivationTriggerContext
{
	CommandLine = 0,
	ForwardedFromSecondary = 1,
}

[Flags]
public enum ActivationModifiers
{
	None = 0,
	NoUi = 1,
}

public sealed record ActivationRequest
{
	public const int MaxUrlLength = 2048;

	public string NormalizedUrl { get; }

	public long ArrivalMonotonicStamp { get; }

	public ActivationTriggerContext TriggerContext { get; }

	public SourceEvidence SourceEvidence { get; }

	public ActivationModifiers Modifiers { get; }

	public Guid RequestId { get; }

	public int ProtocolVersion { get; }

	public DateTimeOffset ExpiresAtUtc { get; }

	public string? PortableDataPath { get; }

	public ActivationRequest(
		string normalizedUrl,
		long arrivalMonotonicStamp,
		ActivationTriggerContext triggerContext,
		SourceEvidence sourceEvidence,
		ActivationModifiers modifiers,
		Guid requestId,
		int protocolVersion,
		DateTimeOffset expiresAtUtc,
		string? portableDataPath = null)
	{
		if (string.IsNullOrWhiteSpace(normalizedUrl))
		{
			throw new ArgumentException("URL cannot be empty.", nameof(normalizedUrl));
		}

		if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? uri))
		{
			throw new ArgumentException("URL must be absolute and normalized.", nameof(normalizedUrl));
		}

		// THE CAP APPLIES TO THE VALUE THIS TYPE STORES, NOT TO THE CALLER'S INPUT STRING.
		// NormalizedUrl is uri.AbsoluteUri, and AbsoluteUri percent-escapes non-ASCII: one
		// Cyrillic path character leaves as six characters and one CJK character as nine. The
		// check used to run on the input, so a URL that fit the cap on the way in could store
		// an escaped value far over it — and ActivationProtocol.TryDecodeRequest, which checks
		// the cap on the decoded value, then refused the frame ActivationProtocol.EncodeRequest
		// had just produced from this very instance. Non-ASCII URLs were accepted on first
		// activation and silently dropped on the second, which arrives over IPC.
		//
		// Measuring the stored value here is what makes the encoder and decoder agree: both
		// now test the same string, so anything this constructor accepts can be encoded and
		// decoded again.
		string storedUrl = uri.AbsoluteUri;

		if (storedUrl.Length > MaxUrlLength)
		{
			throw new ArgumentException($"URL exceeds the {MaxUrlLength}-character limit after normalization.", nameof(normalizedUrl));
		}

		ArgumentOutOfRangeException.ThrowIfNegative(arrivalMonotonicStamp);

		ArgumentNullException.ThrowIfNull(sourceEvidence);

		if (requestId == Guid.Empty)
		{
			throw new ArgumentException("Request ID must be non-empty.", nameof(requestId));
		}

		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(protocolVersion);

		NormalizedUrl = storedUrl;
		ArrivalMonotonicStamp = arrivalMonotonicStamp;
		TriggerContext = triggerContext;
		SourceEvidence = sourceEvidence;
		Modifiers = modifiers;
		RequestId = requestId;
		ProtocolVersion = protocolVersion;
		ExpiresAtUtc = expiresAtUtc;
		PortableDataPath = portableDataPath;
	}
}
