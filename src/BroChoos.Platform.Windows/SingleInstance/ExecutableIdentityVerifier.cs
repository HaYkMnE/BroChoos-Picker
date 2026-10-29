namespace BroChoos.Platform.Windows.SingleInstance;

public sealed record ExecutableIdentityCheckResult(bool Success, IpcPeerAuthenticationFailure? Failure)
{
	public static ExecutableIdentityCheckResult Passed { get; } = new(true, null);

	public static ExecutableIdentityCheckResult Fail(IpcPeerAuthenticationErrorCode code, string reason)
	{
		return new ExecutableIdentityCheckResult(false, new IpcPeerAuthenticationFailure(code, reason));
	}
}

public sealed record SignerIdentityCheckResult(bool Success, IpcPeerAuthenticationFailure? Failure)
{
	public static SignerIdentityCheckResult Passed { get; } = new(true, null);

	public static SignerIdentityCheckResult Fail(IpcPeerAuthenticationErrorCode code, string reason)
	{
		return new SignerIdentityCheckResult(false, new IpcPeerAuthenticationFailure(code, reason));
	}
}

public sealed class ExecutableIdentityVerifier
{
	public static ExecutableIdentityCheckResult VerifyCanonicalIdentity(string localExecutablePath, string peerExecutablePath)
	{
		if (string.IsNullOrWhiteSpace(localExecutablePath) || string.IsNullOrWhiteSpace(peerExecutablePath))
		{
			return ExecutableIdentityCheckResult.Fail(IpcPeerAuthenticationErrorCode.ExecutableIdentityAmbiguous, "ipc.identity.missing");
		}

		string localCanonical;
		string peerCanonical;
		try
		{
			localCanonical = ResolveCanonicalPath(localExecutablePath);
			peerCanonical = ResolveCanonicalPath(peerExecutablePath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
		{
			return ExecutableIdentityCheckResult.Fail(IpcPeerAuthenticationErrorCode.ExecutableIdentityAmbiguous, "ipc.identity.ambiguous");
		}

		return !string.Equals(localCanonical, peerCanonical, StringComparison.OrdinalIgnoreCase)
			? ExecutableIdentityCheckResult.Fail(IpcPeerAuthenticationErrorCode.ExecutableIdentityMismatch, "ipc.identity.path-mismatch")
			: ExecutableIdentityCheckResult.Passed;
	}

	public static SignerIdentityCheckResult VerifySignerIdentity(IpcTrustPolicy policy, string? localSignerIdentity, string? peerSignerIdentity)
	{
		ArgumentNullException.ThrowIfNull(policy);

		return string.IsNullOrWhiteSpace(localSignerIdentity) || string.IsNullOrWhiteSpace(peerSignerIdentity)
			? SignerIdentityCheckResult.Fail(IpcPeerAuthenticationErrorCode.SignerIdentityAmbiguous, "ipc.signer.identity-missing")
			: !string.Equals(localSignerIdentity, peerSignerIdentity, StringComparison.Ordinal)
			? SignerIdentityCheckResult.Fail(IpcPeerAuthenticationErrorCode.SignerIdentityMismatch, "ipc.signer.identity-mismatch")
			: SignerIdentityCheckResult.Passed;
	}

	// Shared with PeerProcessImageVerifier (F-2b): the OS-reported image path and the
	// expected executable must be canonicalized the SAME way on both sides, or the
	// comparison would be testing the two call sites' formatting rather than identity.
	internal static string ResolveCanonicalPath(string path)
	{
		string full = Path.GetFullPath(path);
		FileSystemInfo? target = File.ResolveLinkTarget(full, returnFinalTarget: true);
		return target?.FullName ?? full;
	}
}
