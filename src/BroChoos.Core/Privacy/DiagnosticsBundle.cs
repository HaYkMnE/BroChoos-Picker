namespace BroChoos.Core.Privacy;

// W10.5 (independent diagnostic-implementation portion).
//
// WHAT THIS IS. The local, previewable, redacted-by-default diagnostics bundle the plan
// requires, expressed as a PURE projection: the caller supplies already-observed facts
// (booleans, counts, a build id, a schema version) and this type decides what may leave
// the machine. It performs NO I/O and opens NO socket, which is what makes "local and
// manually sent, no telemetry" a structural property rather than a promise.
//
// WHY AN ALLOWLIST AND NOT A SCRUB (D-075 / D-097). A subtractive scrub — strip
// `C:\Users\...`, strip URLs, strip tokens — can only remove the shapes its author
// remembered, and every new field is a new chance to leak. Here the OUTPUT record IS the
// allowlist: a value the caller passes but the projector does not have a field for simply
// never enters the bundle, so an added input cannot leak by accident. The candidate
// members below are deliberately accepted-and-dropped so that this property is testable
// and so that a future integrator has an obvious, safe place to hand a value that is not
// yet cleared for export.
//
// WHAT THIS IS NOT. It is NOT the release-trust surface. Publisher identity, official
// source URL, release channel, checksums, signature instructions, release notes, known
// issues, security disclosure and support path are final trust-document content and are
// gated on actual W10.1-W10.4/W10.6 behavior and evidence; none of them is invented here.
// The one build-identity fact this type carries is the VERIFICATION STATUS of the running
// artifact, and it is reported as Unavailable until release signing exists, because
// reporting Verified without an observed signature would be fabrication.

// How far the running artifact's authenticity has actually been established. Unavailable
// is not an error and not a failure: it is the truthful answer for a build that has no
// signing/update pipeline yet (W10.3/W10.4). The point of the enum is that there is no
// way to express "verified" without having observed a verification.
public enum ArtifactVerificationStatus
{
	// This build cannot verify its own artifact: no signing/update pipeline exists yet
	// (W10.3/W10.4 own it). Never rendered as a value or a PASS.
	Unavailable,

	// A verification ran and PASSED.
	Verified,

	// A verification ran and FAILED. Distinct from Unavailable on purpose: "we checked and
	// it did not match" is a different, more serious fact than "we cannot check here".
	Failed,
}

// The exact facts a caller may supply. Only the non-`Candidate` members become bundle
// fields; the `Candidate*` members exist so that a value with no cleared home is handed
// to the boundary and DROPPED there, which is a testable no-leak path rather than a
// convention. `Empty` is the honest, minimal input: nothing observed, nothing exported.
public sealed record DiagnosticsBundleInput
{
	public required DateTimeOffset ProducedAtUtc { get; init; }

	// A human/runtime build identity token. This is a LABEL the caller derives, not a
	// path, not a URL and not a user value. It is never a filesystem location.
	public required string BuildId { get; init; }

	public required bool StorageRootReachable { get; init; }

	public required bool BackupsDirectoryPresent { get; init; }

	public required bool LogsDirectoryPresent { get; init; }

	public required int SchemaVersion { get; init; }

	public required int SchemaRevision { get; init; }

	// True only when a real verification actually ran and produced an observation. When
	// false the status is Unavailable regardless of what the caller hoped.
	public required bool ArtifactVerificationObserved { get; init; }

	// The outcome of the observed verification. Meaningful only when
	// ArtifactVerificationObserved is true. Defaults to false so an observed verification
	// with no explicit success is reported as Failed, never as Verified.
	public bool ArtifactVerificationSucceeded { get; init; }

	// ---- Values with NO cleared home: accepted at the boundary and dropped. ----

	public string? CandidateRootPath { get; init; }

	public string? CandidateSourcePath { get; init; }

	public string? CandidateUrl { get; init; }

	public string? CandidateClipboard { get; init; }

	public string? CandidateAccount { get; init; }

	public string? CandidateProfile { get; init; }

	public string? CandidateToken { get; init; }

	public string? CandidateHistory { get; init; }

	public string? CandidateAddress { get; init; }

	public static DiagnosticsBundleInput Empty => new()
	{
		ProducedAtUtc = DateTimeOffset.UnixEpoch,
		BuildId = "unknown",
		StorageRootReachable = false,
		BackupsDirectoryPresent = false,
		LogsDirectoryPresent = false,
		SchemaVersion = 0,
		SchemaRevision = 0,
		ArtifactVerificationObserved = false,
	};
}

// THE ALLOWLIST, AS A SHAPE. Every property here is a reviewed, exportable field. Adding
// one is a deliberate privacy decision, and DiagnosticBundlePrivacyTests fails until the
// serializer's allowlist is extended to match — so the change cannot be silent.
public sealed record DiagnosticsBundle
{
	public required string Artifact { get; init; }

	public required DateTimeOffset ProducedAtUtc { get; init; }

	public required string BuildId { get; init; }

	public required bool StorageRootReachable { get; init; }

	public required bool BackupsDirectoryPresent { get; init; }

	public required bool LogsDirectoryPresent { get; init; }

	public required int SchemaVersion { get; init; }

	public required int SchemaRevision { get; init; }

	public required ArtifactVerificationStatus ArtifactVerificationStatus { get; init; }

	public required bool ContainsUserContent { get; init; }
}

public static class DiagnosticsBundleProjector
{
	// Fixed so a support workflow and the serializer's allowlist can name the artifact
	// without either holding a path: the artifact is identified by its KIND, never by
	// where it happens to sit on one machine.
	public const string ArtifactKind = "brochoos-diagnostics";

	public static DiagnosticsBundle Project(DiagnosticsBundleInput input)
	{
		ArgumentNullException.ThrowIfNull(input);

		// The Candidate* members are intentionally NOT read. That is the whole point: an
		// unclassified value is dropped at the boundary, so it cannot ride into the bundle
		// through a field somebody adds later without extending the allowlist.
		return new DiagnosticsBundle
		{
			Artifact = ArtifactKind,
			ProducedAtUtc = input.ProducedAtUtc,
			BuildId = input.BuildId,
			StorageRootReachable = input.StorageRootReachable,
			BackupsDirectoryPresent = input.BackupsDirectoryPresent,
			LogsDirectoryPresent = input.LogsDirectoryPresent,
			SchemaVersion = input.SchemaVersion,
			SchemaRevision = input.SchemaRevision,

			// A status, not a value: Unavailable until a verification actually observed
			// something. Verified requires an observed AND successful verification; an
			// observed failure maps to Failed. No branch yields Verified otherwise.
			ArtifactVerificationStatus = !input.ArtifactVerificationObserved
				? ArtifactVerificationStatus.Unavailable
				: input.ArtifactVerificationSucceeded
					? ArtifactVerificationStatus.Verified
					: ArtifactVerificationStatus.Failed,

			// Constant and explicit, so a reader (and a test) can see the bundle is
			// declared to hold no user content rather than inferring it from field names.
			ContainsUserContent = false,
		};
	}
}
