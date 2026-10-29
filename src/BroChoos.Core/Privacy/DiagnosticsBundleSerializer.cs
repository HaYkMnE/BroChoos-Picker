using System.Globalization;
using System.Text;

namespace BroChoos.Core.Privacy;

// W10.5: the deterministic serializer for the redacted diagnostics bundle.
//
// It walks an EXPLICIT field list rather than reflecting over the record. Reflection
// would make serialization automatic — including for a field nobody reviewed — which is
// the opposite of what an allowlist is for. The cost of the explicit list is that adding
// a field requires two edits; DiagnosticBundlePrivacyTests makes the second edit
// mandatory by comparing the declared bundle shape against AllowlistedFieldNames.
//
// Determinism matters for a support workflow: the same input must produce byte-identical
// output so a preview can be compared against an export, and so a reviewer can diff two
// runs. Nothing here reads the clock, the filesystem, the environment or the machine
// name — every value arrives already observed.
public static class DiagnosticsBundleSerializer
{
	// THE ALLOWLIST. Ordered as it is written, and the single source of truth for which
	// top-level fields may exist. Extend this deliberately alongside the bundle record.
	public static readonly string[] AllowlistedFieldNames =
	[
		"artifact",
		"producedAtUtc",
		"buildId",
		"storageRootReachable",
		"backupsDirectoryPresent",
		"logsDirectoryPresent",
		"schemaVersion",
		"schemaRevision",
		"artifactVerificationStatus",
		"containsUserContent",
	];

	public static string Serialize(DiagnosticsBundle bundle)
	{
		ArgumentNullException.ThrowIfNull(bundle);

		StringBuilder builder = new();
		_ = builder.Append("{\n");
		AppendString(builder, "artifact", bundle.Artifact, last: false);
		AppendString(
			builder,
			"producedAtUtc",
			bundle.ProducedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
			last: false);
		AppendString(builder, "buildId", bundle.BuildId, last: false);
		AppendBool(builder, "storageRootReachable", bundle.StorageRootReachable, last: false);
		AppendBool(builder, "backupsDirectoryPresent", bundle.BackupsDirectoryPresent, last: false);
		AppendBool(builder, "logsDirectoryPresent", bundle.LogsDirectoryPresent, last: false);
		AppendInt(builder, "schemaVersion", bundle.SchemaVersion, last: false);
		AppendInt(builder, "schemaRevision", bundle.SchemaRevision, last: false);
		AppendString(builder, "artifactVerificationStatus", ToWire(bundle.ArtifactVerificationStatus), last: false);
		AppendBool(builder, "containsUserContent", bundle.ContainsUserContent, last: true);
		_ = builder.Append("}\n");

		return builder.ToString();
	}

	// The wire vocabulary is deliberately NOT the enum's name. C# member names change with
	// refactors; a support format is a contract, so the strings are pinned here and a
	// rename cannot silently alter what a reviewer reads.
	private static string ToWire(ArtifactVerificationStatus status)
	{
		return status switch
		{
			ArtifactVerificationStatus.Verified => "verified",
			ArtifactVerificationStatus.Failed => "failed",
			ArtifactVerificationStatus.Unavailable => "unavailable",
			_ => "unavailable",
		};
	}

	private static void AppendString(StringBuilder builder, string name, string value, bool last)
	{
		_ = builder.Append("  \"").Append(name).Append("\": \"").Append(Escape(value)).Append('"').Append(last ? "\n" : ",\n");
	}

	private static void AppendBool(StringBuilder builder, string name, bool value, bool last)
	{
		_ = builder.Append("  \"").Append(name).Append("\": ").Append(value ? "true" : "false").Append(last ? "\n" : ",\n");
	}

	private static void AppendInt(StringBuilder builder, string name, int value, bool last)
	{
		_ = builder.Append("  \"").Append(name).Append("\": ")
			.Append(value.ToString(CultureInfo.InvariantCulture)).Append(last ? "\n" : ",\n");
	}

	// Minimal JSON string escaping. The allowlisted string fields are a build label, an
	// artifact kind and a fixed status token — never free-form user text — so this only
	// has to be correct, not clever.
	private static string Escape(string value)
	{
		StringBuilder escaped = new(value.Length);
		for (int index = 0; index < value.Length; index++)
		{
			char character = value[index];

			// A valid surrogate pair is kept as-is; a LONE surrogate cannot be encoded as
			// UTF-8, so it is written as a \uXXXX escape to keep the output valid JSON.
			if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
			{
				_ = escaped.Append(character).Append(value[index + 1]);
				index++;
				continue;
			}

			_ = character switch
			{
				_ when char.IsSurrogate(character) => escaped.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)),
				'\\' => escaped.Append("\\\\"),
				'"' => escaped.Append("\\\""),
				'\n' => escaped.Append("\\n"),
				'\r' => escaped.Append("\\r"),
				'\t' => escaped.Append("\\t"),
				'\b' => escaped.Append("\\b"),
				'\f' => escaped.Append("\\f"),
				< ' ' => escaped.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)),
				_ => escaped.Append(character),
			};
		}

		return escaped.ToString();
	}
}
