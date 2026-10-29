using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows;
using BroChoos.Platform.Windows.SingleInstance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BroChoos.Security.Tests;

public sealed class IpcPeerAuthenticationTests
{
	[Fact]
	public void ResolveProductionPolicy_DefaultProvider_UsesOwnerRulingRecord()
	{
		ServiceCollection services = new();
		_ = services.AddLogging();
		_ = services.AddWindowsPlatformServices();
		using ServiceProvider provider = services.BuildServiceProvider();

		IIpcTrustPolicyProvider trustProvider = provider.GetRequiredService<IIpcTrustPolicyProvider>();
		_ = trustProvider.Should().BeOfType<OwnerRulingFileIpcTrustPolicyProvider>();

		IpcPeerAuthenticationResult result = IpcTrustPolicyGate.ResolveProductionPolicy(trustProvider, out IpcTrustPolicy? policy);

		_ = result.Success.Should().BeTrue();
		_ = result.Failure.Should().BeNull();
		_ = policy.Should().NotBeNull();
	}

	[Fact]
	public void ProductionComposition_GuardSeam_PassesWhenOwnerRulingAndIdentityAreValid()
	{
		ServiceCollection services = new();
		_ = services.AddLogging();
		_ = services.AddWindowsPlatformServices();
		using ServiceProvider provider = services.BuildServiceProvider();

		WindowsSingleInstanceGuard guard = provider.GetRequiredService<ISingleInstanceGuard>().Should().BeOfType<WindowsSingleInstanceGuard>().Subject;
		// The secret is issued through the container's own validator, which is exactly how
		// the guard's secondary role builds its identity frame.
		string secret = provider.GetRequiredService<OneShotSecretValidator>().Issue();
		string exe = CreateTempExecutablePath();

		try
		{
			IpcPeerAuthenticationResult result = guard.ValidatePeerForW1_3Seam(
				exe,
				exe,
				"CN=BroChoos Dev Runner",
				"CN=BroChoos Dev Runner",
				secret);

			_ = result.Success.Should().BeTrue();
			_ = result.Failure.Should().BeNull();
		}
		finally
		{
			DeleteIfExists(exe);
		}
	}

	[Fact]
	public void ResolveProductionPolicy_RejectsFixtureProvider()
	{
		IIpcTrustPolicyProvider fixtureProvider = new StaticPolicyProvider(new IpcTrustPolicy(
			IpcTrustPolicyGate.RequiredSubjectId,
			IpcTrustPolicyGate.RequiredScope,
			"approved",
			"PASS",
			"x509-chain",
			true,
			"DH-2026-09-11",
			"owner@example.invalid",
			DateTimeOffset.UtcNow));

		IpcPeerAuthenticationResult result = IpcTrustPolicyGate.ResolveProductionPolicy(fixtureProvider, out _);

		_ = result.Success.Should().BeFalse();
		_ = result.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.FixtureTrustProviderRejected);
	}

	[Fact]
	public void Authenticate_RejectsPathMismatch_WhenPolicyApprovedAndSecretIsPresent()
	{
		OneShotSecretValidator oneShot = new();
		IpcPeerAuthenticator authenticator = NewApprovedAuthenticator(oneShot);
		string secret = oneShot.Issue();
		string localExe = CreateTempExecutablePath();
		string peerExe = CreateTempExecutablePath();

		try
		{
			IpcPeerAuthenticationResult result = authenticator.Authenticate(
				localExe,
				peerExe,
				"CN=BroChoos Dev Runner",
				"CN=BroChoos Dev Runner",
				secret);

			_ = result.Success.Should().BeFalse();
			_ = result.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.ExecutableIdentityMismatch);
		}
		finally
		{
			DeleteIfExists(localExe);
			DeleteIfExists(peerExe);
		}
	}

	[Fact]
	public void Authenticate_RejectsMissingOneShotSecret()
	{
		IpcPeerAuthenticator authenticator = NewApprovedAuthenticator(new OneShotSecretValidator());
		string exe = CreateTempExecutablePath();

		try
		{
			IpcPeerAuthenticationResult result = authenticator.Authenticate(
				exe,
				exe,
				"CN=BroChoos Dev Runner",
				"CN=BroChoos Dev Runner",
				null);

			_ = result.Success.Should().BeFalse();
			_ = result.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.MissingOneShotSecret);
		}
		finally
		{
			DeleteIfExists(exe);
		}
	}

	[Fact]
	public void Consume_AcceptsAFirstSeenTokenThatThisProcessNeverIssued_AndRejectsItsReplay()
	{
		// THE CROSS-PROCESS CONTRACT, pinned as a regression test because the F-2a
		// "issued-set" fix violated it: in production the token is issued by the SECONDARY
		// process and consumed by the PRIMARY, so the consuming validator has never seen
		// it. A first-seen token MUST pass (it is a bearer value with anti-replay, not a
		// registered one), and the SAME token a second time must fail.
		OneShotSecretValidator consumer = new();

		IpcPeerAuthenticationResult first = consumer.Consume("token-issued-in-another-process");
		_ = first.Success.Should().BeTrue("the peer's token is issued in a different process, so 'first seen here' is the normal production shape");

		IpcPeerAuthenticationResult replayed = consumer.Consume("token-issued-in-another-process");
		_ = replayed.Success.Should().BeFalse("replay is the only thing the one-shot secret actually defends against");
		_ = replayed.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.ReplayedOneShotSecret);
	}

	[Fact]
	public void Consume_ReplaySetIsBounded_AndEvictsTheOldestToken()
	{
		// N3: the anti-replay set was an unbounded static ConcurrentDictionary, so a hostile
		// peer could grow it for the process lifetime (memory-pressure DoS). The set is now a
		// bounded FIFO; this pins BOTH halves of the trade: the count never exceeds the named
		// cap, and the eviction is what makes the cap real — the oldest token, once evicted,
		// becomes replayable again (the documented residual), while the newest is still refused.
		OneShotSecretValidator consumer = new();
		int total = OneShotSecretValidator.MaxConsumedTokens + 100;

		for (int i = 0; i < total; i++)
		{
			IpcPeerAuthenticationResult first = consumer.Consume($"token-{i}");
			_ = first.Success.Should().BeTrue("each token is distinct and first-seen here");
		}

		_ = OneShotSecretValidator.ConsumedCount.Should().Be(
			OneShotSecretValidator.MaxConsumedTokens,
			"the replay set must never retain more than its named cap, however many tokens are spent");

		IpcPeerAuthenticationResult oldestAfterEviction = consumer.Consume("token-0");
		_ = oldestAfterEviction.Success.Should().BeTrue("token-0 was evicted, so it is no longer remembered — the residual risk of any bounded replay set");

		IpcPeerAuthenticationResult newestReplay = consumer.Consume($"token-{total - 1}");
		_ = newestReplay.Success.Should().BeFalse("the newest token is still resident, so its replay is refused");
		_ = newestReplay.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.ReplayedOneShotSecret);
	}

	[Fact]
	public void Authenticate_RejectsSignerMismatch()
	{
		OneShotSecretValidator oneShot = new();
		IpcPeerAuthenticator authenticator = NewApprovedAuthenticator(oneShot);
		string secret = oneShot.Issue();
		string exe = CreateTempExecutablePath();

		try
		{
			IpcPeerAuthenticationResult result = authenticator.Authenticate(
				exe,
				exe,
				"CN=BroChoos Dev Runner",
				"CN=Different Signer",
				secret);

			_ = result.Success.Should().BeFalse();
			_ = result.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.SignerIdentityMismatch);
		}
		finally
		{
			DeleteIfExists(exe);
		}
	}

	[Fact]
	public void Authenticate_RejectsReplayedOneShotSecret()
	{
		OneShotSecretValidator oneShot = new();
		IpcPeerAuthenticator authenticator = NewApprovedAuthenticator(oneShot);
		string secret = oneShot.Issue();
		string exe = CreateTempExecutablePath();

		try
		{
			IpcPeerAuthenticationResult first = authenticator.Authenticate(
				exe,
				exe,
				"CN=BroChoos Dev Runner",
				"CN=BroChoos Dev Runner",
				secret);
			IpcPeerAuthenticationResult second = authenticator.Authenticate(
				exe,
				exe,
				"CN=BroChoos Dev Runner",
				"CN=BroChoos Dev Runner",
				secret);

			_ = first.Success.Should().BeTrue();
			_ = second.Success.Should().BeFalse();
			_ = second.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.ReplayedOneShotSecret);
		}
		finally
		{
			DeleteIfExists(exe);
		}
	}

	private static string CreateTempExecutablePath()
	{
		string path = Path.Combine(Path.GetTempPath(), $"brochoos-w13-{Guid.NewGuid():N}.exe");
		File.WriteAllText(path, "fixture");
		return path;
	}

	private static void DeleteIfExists(string path)
	{
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}

	private static IpcPeerAuthenticator NewApprovedAuthenticator(OneShotSecretValidator oneShotSecretValidator)
	{
		return new IpcPeerAuthenticator(
			oneShotSecretValidator,
			new StaticPolicyProvider(new IpcTrustPolicy(
				IpcTrustPolicyGate.RequiredSubjectId,
				IpcTrustPolicyGate.RequiredScope,
				"approved",
				"PASS",
				"x509-chain",
				false,
				"DH-2026-09-11",
				"owner@example.invalid",
				DateTimeOffset.UtcNow)));
	}

	private sealed class StaticPolicyProvider(IpcTrustPolicy policy) : IIpcTrustPolicyProvider
	{
		private readonly IpcTrustPolicy _policy = policy;

		public bool TryGetPolicy(out IpcTrustPolicy? policy, out IpcPeerAuthenticationFailure? failure)
		{
			policy = _policy;
			failure = null;
			return true;
		}
	}
}
