using System.Globalization;
using System.Text.Json;

namespace BroChoos.Platform.Windows.SingleInstance;

public enum IpcPeerAuthenticationErrorCode
{
	TrustAnchorUnavailable = 1,
	FixtureTrustProviderRejected = 2,
	ExecutableIdentityMismatch = 3,
	ExecutableIdentityAmbiguous = 4,
	SignerIdentityMismatch = 5,
	SignerIdentityAmbiguous = 6,
	MissingOneShotSecret = 7,
	ReplayedOneShotSecret = 8,
	TrustAnchorRevoked = 9,
	// 10 was UnissuedOneShotSecret, removed with the F-2a issued-set revert: issuance
	// and consumption happen in different processes, so "issued by this process" can
	// never be true for a real peer. The number is deliberately NOT reused — a stale
	// log or serialized frame carrying 10 must not silently acquire a new meaning.
	//
	// F-2b: the OS-level peer process-image anchor (PeerProcessImageVerifier).
	PeerProcessImageMismatch = 11,
	PeerProcessImageUnavailable = 12,
}

public sealed record IpcPeerAuthenticationFailure(IpcPeerAuthenticationErrorCode Code, string RedactedReason);

public sealed record IpcPeerAuthenticationResult(bool Success, IpcPeerAuthenticationFailure? Failure)
{
	public static IpcPeerAuthenticationResult Passed { get; } = new(true, null);

	public static IpcPeerAuthenticationResult Fail(IpcPeerAuthenticationErrorCode code, string redactedReason)
	{
		return new IpcPeerAuthenticationResult(false, new IpcPeerAuthenticationFailure(code, redactedReason));
	}
}

public sealed record IpcTrustPolicy(
	string SubjectId,
	string Scope,
	string Disposition,
	string ValidationStatus,
	string SignerIdentityModel,
	bool TestOnly,
	string AuthoritySource,
	string ApprovalIdentity,
	DateTimeOffset ApprovedAtUtc);

public interface IIpcTrustPolicyProvider
{
	bool TryGetPolicy(out IpcTrustPolicy? policy, out IpcPeerAuthenticationFailure? failure);
}

public sealed class BlockedUntilOwnerRulingIpcTrustPolicyProvider : IIpcTrustPolicyProvider
{
	public bool TryGetPolicy(out IpcTrustPolicy? policy, out IpcPeerAuthenticationFailure? failure)
	{
		policy = null;
		failure = new IpcPeerAuthenticationFailure(
			IpcPeerAuthenticationErrorCode.TrustAnchorUnavailable,
			"ipc.trust-anchor.unavailable");
		return false;
	}
}

public sealed class OwnerRulingFileIpcTrustPolicyProvider : IIpcTrustPolicyProvider
{
	private const string RecordRelativePath = "docs/decisions/owner-rulings/dev-runner-ipc-trust-anchor.json";

	public bool TryGetPolicy(out IpcTrustPolicy? policy, out IpcPeerAuthenticationFailure? failure)
	{
		policy = null;
		failure = null;

		string? repoRoot = FindRepositoryRoot();
		if (repoRoot is null)
		{
			failure = new IpcPeerAuthenticationFailure(IpcPeerAuthenticationErrorCode.TrustAnchorUnavailable, "ipc.trust-anchor.unavailable");
			return false;
		}

		string recordPath = Path.Combine(repoRoot, RecordRelativePath.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(recordPath))
		{
			failure = new IpcPeerAuthenticationFailure(IpcPeerAuthenticationErrorCode.TrustAnchorUnavailable, "ipc.trust-anchor.unavailable");
			return false;
		}

		try
		{
			using JsonDocument json = JsonDocument.Parse(File.ReadAllText(recordPath));
			JsonElement root = json.RootElement;

			string subjectId = ReadString(root, "subjectId");
			string scope = ReadString(root, "scope");
			string disposition = ReadNestedString(root, "resolutionState", "disposition");
			string validationStatus = ReadNestedString(root, "validation", "status");
			string signerIdentityModel = ReadString(root, "signerIdentityModel");
			string authoritySource = ReadNestedString(root, "authority", "source");
			string approvalIdentity = ReadNestedString(root, "authority", "approver");
			DateTimeOffset approvedAtUtc = DateTimeOffset.Parse(ReadNestedString(root, "authority", "approvedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

			bool testOnly = false;
			if (root.TryGetProperty("publicTrustMetadata", out JsonElement publicTrust)
				&& publicTrust.ValueKind == JsonValueKind.Object
				&& publicTrust.TryGetProperty("testOnly", out JsonElement testOnlyNode)
				&& (testOnlyNode.ValueKind == JsonValueKind.True || testOnlyNode.ValueKind == JsonValueKind.False))
			{
				testOnly = testOnlyNode.GetBoolean();
			}

			if (root.TryGetProperty("trustAnchorLifecycle", out JsonElement lifecycle)
				&& lifecycle.ValueKind == JsonValueKind.Object
				&& lifecycle.TryGetProperty("revocation", out JsonElement revocationNode)
				&& revocationNode.ValueKind == JsonValueKind.String)
			{
				string revocation = revocationNode.GetString() ?? string.Empty;
				if (revocation.Contains("revoked", StringComparison.OrdinalIgnoreCase)
					&& !revocation.Contains("not revoked", StringComparison.OrdinalIgnoreCase))
				{
					failure = new IpcPeerAuthenticationFailure(IpcPeerAuthenticationErrorCode.TrustAnchorRevoked, "ipc.trust-anchor.revoked");
					return false;
				}
			}

			policy = new IpcTrustPolicy(
				subjectId,
				scope,
				disposition,
				validationStatus,
				signerIdentityModel,
				testOnly,
				authoritySource,
				approvalIdentity,
				approvedAtUtc);

			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
		{
			failure = new IpcPeerAuthenticationFailure(IpcPeerAuthenticationErrorCode.TrustAnchorUnavailable, "ipc.trust-anchor.unavailable");
			return false;
		}
	}

	private static string? FindRepositoryRoot()
	{
		DirectoryInfo? current = new(AppContext.BaseDirectory);
		while (current is not null)
		{
			if ((Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
				&& Directory.Exists(Path.Combine(current.FullName, "docs")))
			{
				return current.FullName;
			}

			current = current.Parent;
		}

		return null;
	}

	private static string ReadString(JsonElement obj, string propertyName)
	{
		return !obj.TryGetProperty(propertyName, out JsonElement node) || node.ValueKind != JsonValueKind.String
			? throw new JsonException(propertyName)
			: node.GetString() ?? string.Empty;
	}

	private static string ReadNestedString(JsonElement obj, string nodeName, string propertyName)
	{
		return !obj.TryGetProperty(nodeName, out JsonElement nested) || nested.ValueKind != JsonValueKind.Object
			? throw new JsonException(nodeName)
			: ReadString(nested, propertyName);
	}
}

public static class IpcTrustPolicyGate
{
	public const string RequiredSubjectId = "DEV-RUNNER-IPC-TRUST-ANCHOR";
	public const string RequiredScope = "local Dev Runner IPC only";

	public static IpcPeerAuthenticationResult ResolveProductionPolicy(IIpcTrustPolicyProvider provider, out IpcTrustPolicy? policy)
	{
		ArgumentNullException.ThrowIfNull(provider);
		policy = null;

		if (!provider.TryGetPolicy(out IpcTrustPolicy? candidate, out IpcPeerAuthenticationFailure? failure))
		{
			return failure is null
				? IpcPeerAuthenticationResult.Fail(IpcPeerAuthenticationErrorCode.TrustAnchorUnavailable, "ipc.trust-anchor.unavailable")
				: new IpcPeerAuthenticationResult(false, failure);
		}

		if (candidate is null)
		{
			return IpcPeerAuthenticationResult.Fail(IpcPeerAuthenticationErrorCode.TrustAnchorUnavailable, "ipc.trust-anchor.unavailable");
		}

		if (candidate.TestOnly)
		{
			return IpcPeerAuthenticationResult.Fail(IpcPeerAuthenticationErrorCode.FixtureTrustProviderRejected, "ipc.trust-anchor.fixture-rejected");
		}

		if (!candidate.SubjectId.Equals(RequiredSubjectId, StringComparison.Ordinal)
			|| !candidate.Scope.Equals(RequiredScope, StringComparison.Ordinal)
			|| !candidate.Disposition.Equals("approved", StringComparison.OrdinalIgnoreCase)
			|| !candidate.ValidationStatus.Equals("PASS", StringComparison.OrdinalIgnoreCase))
		{
			return IpcPeerAuthenticationResult.Fail(IpcPeerAuthenticationErrorCode.TrustAnchorUnavailable, "ipc.trust-anchor.unavailable");
		}

		policy = candidate;
		return IpcPeerAuthenticationResult.Passed;
	}
}
