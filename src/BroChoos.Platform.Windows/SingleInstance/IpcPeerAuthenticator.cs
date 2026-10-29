namespace BroChoos.Platform.Windows.SingleInstance;

public sealed class IpcPeerAuthenticator(
	OneShotSecretValidator oneShotSecretValidator,
	IIpcTrustPolicyProvider trustPolicyProvider)
{
	private readonly OneShotSecretValidator _oneShotSecretValidator = oneShotSecretValidator;
	private readonly IIpcTrustPolicyProvider _trustPolicyProvider = trustPolicyProvider;

	public IpcPeerAuthenticationResult Authenticate(
		string localExecutablePath,
		string peerExecutablePath,
		string? localSignerIdentity,
		string? peerSignerIdentity,
		string? oneShotSecret)
	{
		IpcPeerAuthenticationResult identity = ValidateTrustAndIdentity(localExecutablePath, peerExecutablePath, localSignerIdentity, peerSignerIdentity);
		return !identity.Success ? identity : _oneShotSecretValidator.Consume(oneShotSecret);
	}

	public IpcPeerAuthenticationResult ValidateTrustAndIdentity(
		string localExecutablePath,
		string peerExecutablePath,
		string? localSignerIdentity,
		string? peerSignerIdentity)
	{
		IpcPeerAuthenticationResult trust = IpcTrustPolicyGate.ResolveProductionPolicy(_trustPolicyProvider, out IpcTrustPolicy? policy);
		if (!trust.Success)
		{
			return trust;
		}

		ExecutableIdentityCheckResult identity = ExecutableIdentityVerifier.VerifyCanonicalIdentity(localExecutablePath, peerExecutablePath);
		if (!identity.Success)
		{
			return new IpcPeerAuthenticationResult(false, identity.Failure);
		}

		SignerIdentityCheckResult signer = ExecutableIdentityVerifier.VerifySignerIdentity(policy!, localSignerIdentity, peerSignerIdentity);
		return !signer.Success ? new IpcPeerAuthenticationResult(false, signer.Failure) : IpcPeerAuthenticationResult.Passed;
	}
}
