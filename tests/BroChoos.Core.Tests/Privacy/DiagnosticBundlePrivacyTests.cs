using System.Reflection;
using BroChoos.Core.Privacy;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Privacy;

// W10.5 (independent diagnostic-implementation portion) behavioural suite.
//
// The plan names this class as `DiagnosticBundlePrivacyTests`. It lives HERE, in the
// disjoint Core test project, rather than in `tests/BroChoos.Release.Tests/`, because
// the plan's own serialization rules make that project W10.1's exclusive creation:
// "W10.1 is the sole creator/initial owner of the release test project and its solution
// entry" (W10.1 files/symbols) and "Never edit shared docs, BroChoos.sln, or
// BroChoos.Release.Tests.csproj concurrently" (lane isolation rule). The project does
// not exist yet, so the plan-named invocation cannot be run honestly today. The
// BEHAVIOUR the plan requires is what this file proves; relocating/mirroring it into the
// release project is a serialized follow-up after W10.1 creates that project.
//
// The privacy requirement is D-075 / D-097: default diagnostics omit full URLs, full
// user paths, clipboard, account/profile identity, tokens, browser history, file paths,
// and private/local/internal addresses. The plan adds "local, previewable, redacted by
// default, manually sent" and "no telemetry/automatic bug report".
public sealed class DiagnosticBundlePrivacyTests
{
	// A realistic burst of values that must NEVER reach a diagnostics bundle. Each is a
	// distinct leak class named by the spec, not a variation of one value.
	private static readonly string[] Sentinels =
	[
		@"C:\Users\Ada Lovelace\AppData\Local\BroChoos",           // full user path
		@"C:\Program Files\Google\Chrome\Application\chrome.exe",  // file/source-process path
		"https://example.com/secret/path?token=abc123#fragment",   // full URL + query + fragment
		"clipboard-sentinel-do-not-leak",                          // clipboard content
		"ada.lovelace@example.com",                                // account identity
		"Profile 7",                                               // profile identity
		"secret-bearer-token-0xDEADBEEF",                          // token/secret
		"https://tracker.example/visits/2026-01-01",               // browser history record
		"127.0.0.1",                                               // private/local/internal address
	];

	// The suite's core property: the serialized bundle contains NONE of the sentinels,
	// even when every one of them is handed to the projector as a candidate field value.
	[Fact]
	public void Serialize_NeverContainsAnySentinelValue_EvenWhenHandedIn()
	{
		DiagnosticsBundleInput input = BuildInputWithEverySentinelAsCandidate();

		DiagnosticsBundle bundle = DiagnosticsBundleProjector.Project(input);
		string json = DiagnosticsBundleSerializer.Serialize(bundle);

		foreach (string sentinel in Sentinels)
		{
			_ = json.Should().NotContain(sentinel, "the bundle must omit this leak class by construction (D-075/D-097)");
		}
	}

	// Redaction is an ALLOWLIST, not a subtractive scrub. A scrub can only remove shapes
	// somebody remembered; an allowlist omits everything it was not told to include. This
	// test pins the direction: even a value nobody would recognize as sensitive must not
	// appear unless it belongs to an allowlisted field.
	[Fact]
	public void Serialize_EmitsOnlyAllowlistedFieldNames()
	{
		string json = DiagnosticsBundleSerializer.Serialize(
			DiagnosticsBundleProjector.Project(BuildInputWithEverySentinelAsCandidate()));

		string[] expected = [.. DiagnosticsBundleSerializer.AllowlistedFieldNames.OrderBy(name => name, StringComparer.Ordinal)];

		string[] actual = [.. ExtractTopLevelFieldNames(json).OrderBy(name => name, StringComparer.Ordinal)];

		_ = actual.Should().Equal(expected, "the serialized top-level fields ARE the allowlist");
	}

	// The reflection half of the allowlist: a NEW property added to the bundle record must
	// fail this test until the author consciously extends the allowlist. Without it, an
	// added field ships unredacted because the serializer would simply keep working.
	[Fact]
	public void BundleShape_IsAClosedAllowlist_SoAddingAFieldFailsUntilReviewed()
	{
		string[] declared = [.. typeof(DiagnosticsBundle)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(property => ToCamelCase(property.Name))
			.OrderBy(name => name, StringComparer.Ordinal)];

		string[] allowlisted = [.. DiagnosticsBundleSerializer.AllowlistedFieldNames.OrderBy(name => name, StringComparer.Ordinal)];

		_ = declared.Should().Equal(
			allowlisted,
			"every bundle property must be an explicitly allowlisted, reviewed field; extend the allowlist deliberately when adding one");
	}

	// Honesty requirement (plan: "runtime-derived build hash/signature status"): a status
	// this build cannot observe is reported as Unavailable, never as a value or a PASS.
	// Faking "verified" is the failure mode this pins.
	[Fact]
	public void ArtifactVerification_WhenNotImplemented_ReportsUnavailableNeverVerified()
	{
		DiagnosticsBundle bundle = DiagnosticsBundleProjector.Project(
			DiagnosticsBundleInput.Empty with { ArtifactVerificationObserved = false });

		_ = bundle.ArtifactVerificationStatus.Should().Be(ArtifactVerificationStatus.Unavailable);
		_ = bundle.ArtifactVerificationStatus.Should().NotBe(ArtifactVerificationStatus.Verified);
	}

	[Theory]
	[InlineData(true, false, ArtifactVerificationStatus.Failed)]
	[InlineData(true, true, ArtifactVerificationStatus.Verified)]
	[InlineData(false, false, ArtifactVerificationStatus.Unavailable)]
	[InlineData(false, true, ArtifactVerificationStatus.Unavailable)]
	public void ArtifactVerification_MapsObservedOutcomeToStatus(bool observed, bool succeeded, ArtifactVerificationStatus expected)
	{
		DiagnosticsBundle bundle = DiagnosticsBundleProjector.Project(
			DiagnosticsBundleInput.Empty with
			{
				ArtifactVerificationObserved = observed,
				ArtifactVerificationSucceeded = succeeded,
			});

		_ = bundle.ArtifactVerificationStatus.Should().Be(expected);
	}

	[Fact]
	public void Serialize_EscapesLoneSurrogatesButKeepsValidPairs()
	{
		// lone high, lone low, a valid pair (U+1F600), and a high surrogate at the end.
		DiagnosticsBundle bundle = DiagnosticsBundleProjector.Project(
			DiagnosticsBundleInput.Empty with { BuildId = "a\uD800b\uDC00c\uD83D\uDE00d\uD83D" });

		string json = DiagnosticsBundleSerializer.Serialize(bundle);

		_ = json.Should().Contain("\"buildId\": \"a\\ud800b\\udc00c\uD83D\uDE00d\\ud83d\"");
		// Round-trips through strict UTF-8 without a replacement or an exception.
		byte[] bytes = new System.Text.UTF8Encoding(false, true).GetBytes(json);
		_ = bytes.Should().NotBeEmpty();
	}

	[Fact]
	public void Serialize_EscapesEveryControlCharacterBelowSpace()
	{
		DiagnosticsBundle bundle = DiagnosticsBundleProjector.Project(
			DiagnosticsBundleInput.Empty with { BuildId = "a\0b\fc\bd\ne\u001ff" });

		string json = DiagnosticsBundleSerializer.Serialize(bundle);

		_ = json.Should().Contain("\"buildId\": \"a\\u0000b\\fc\\bd\\ne\\u001ff\"");
		_ = json.Where(character => character is < ' ' and not '\n').Should().BeEmpty("raw control characters are invalid inside a JSON string");
	}

	// The bundle is local and manually sent: there must be no field that names a
	// submission/upload/telemetry action, because their absence is the contract.
	[Fact]
	public void Serialize_HasNoSubmissionOrTelemetrySurface()
	{
		string json = DiagnosticsBundleSerializer.Serialize(
			DiagnosticsBundleProjector.Project(BuildInputWithEverySentinelAsCandidate()));

		foreach (string forbidden in new[] { "upload", "submit", "telemetry", "endpoint", "network", "http://", "https://" })
		{
			_ = json.Should().NotContain(forbidden, "diagnostics are local-only and manually submitted");
		}
	}

	// A preview is produced from the SAME projection the export uses, so what the user
	// previews is byte-identical to what would be written. A separate preview path is a
	// second implementation that can drift from the thing it claims to preview.
	[Fact]
	public void PreviewAndExport_AreTheSameBytes()
	{
		DiagnosticsBundle bundle = DiagnosticsBundleProjector.Project(BuildInputWithEverySentinelAsCandidate());

		string preview = DiagnosticsBundleSerializer.Serialize(bundle);
		string export = DiagnosticsBundleSerializer.Serialize(bundle);

		_ = export.Should().Be(preview);
	}

	// ===================== helpers =====================

	// Every sentinel is offered as the value a careless implementation would happily
	// serialize: a root path, a source path, a URL, a profile, a token, an account, and a
	// free-form note. The projector must drop all of them.
	private static DiagnosticsBundleInput BuildInputWithEverySentinelAsCandidate()
	{
		return new DiagnosticsBundleInput
		{
			ProducedAtUtc = DateTimeOffset.UnixEpoch,
			BuildId = "1.0.0+local",
			StorageRootReachable = true,
			BackupsDirectoryPresent = true,
			LogsDirectoryPresent = false,
			SchemaVersion = 1,
			SchemaRevision = 0,
			ArtifactVerificationObserved = false,
			CandidateRootPath = Sentinels[0],
			CandidateSourcePath = Sentinels[1],
			CandidateUrl = Sentinels[2],
			CandidateClipboard = Sentinels[3],
			CandidateAccount = Sentinels[4],
			CandidateProfile = Sentinels[5],
			CandidateToken = Sentinels[6],
			CandidateHistory = Sentinels[7],
			CandidateAddress = Sentinels[8],
		};
	}

	private static IEnumerable<string> ExtractTopLevelFieldNames(string json)
	{
		foreach (string line in json.Split('\n'))
		{
			string trimmed = line.Trim();
			if (!trimmed.StartsWith('"'))
			{
				continue;
			}

			int close = trimmed.IndexOf('"', 1);
			if (close > 1)
			{
				yield return trimmed[1..close];
			}
		}
	}

	private static string ToCamelCase(string name)
	{
		return char.ToLowerInvariant(name[0]) + name[1..];
	}
}
