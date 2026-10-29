namespace BroChoos.Core.Ingress;

public enum SourceConfidence
{
	Unknown = 0,
	Probable = 1,
	Confirmed = 2,
}

public sealed record SourceEvidence
{
	public const int MaxEvidenceLength = 256;

	public SourceConfidence Confidence { get; }

	public string DisplayProjection { get; }

	private SourceEvidence(SourceConfidence confidence, string displayProjection)
	{
		Confidence = confidence;
		DisplayProjection = displayProjection;
	}

	public static SourceEvidence Confirmed(string evidence)
	{
		return Create(SourceConfidence.Confirmed, evidence);
	}

	public static SourceEvidence Probable(string evidence)
	{
		return Create(SourceConfidence.Probable, evidence);
	}

	public static SourceEvidence Unknown(string? evidence = null)
	{
		return Create(SourceConfidence.Unknown, evidence ?? "unknown");
	}

	public static SourceEvidence Create(SourceConfidence confidence, string evidence)
	{
		if (string.IsNullOrWhiteSpace(evidence))
		{
			throw new ArgumentException("Evidence cannot be empty.", nameof(evidence));
		}

		if (evidence.Length > MaxEvidenceLength)
		{
			throw new ArgumentException($"Evidence exceeds the {MaxEvidenceLength}-character limit.", nameof(evidence));
		}

		string compact = evidence.Trim();
		return new SourceEvidence(confidence, BuildDisplayProjection(compact));
	}

	private static string BuildDisplayProjection(string compact)
	{
		if (LooksSensitive(compact))
		{
			return "redacted";
		}

		int slash = compact.LastIndexOf('/');
		int backslash = compact.LastIndexOf('\\');
		int separator = Math.Max(slash, backslash);
		string tail = separator >= 0 && separator < compact.Length - 1
			? compact[(separator + 1)..]
			: compact;

		return LooksSensitive(tail) ? "redacted" : tail;
	}

	private static bool LooksSensitive(string value)
	{
		return value.Contains('?', StringComparison.Ordinal)
			|| value.Contains('&', StringComparison.Ordinal)
			|| value.Contains('=', StringComparison.Ordinal)
			|| value.Contains("://", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("token", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("secret", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("auth", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("bearer", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("session", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("password", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("cookie", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("apikey", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("api-key", StringComparison.OrdinalIgnoreCase);
	}
}
