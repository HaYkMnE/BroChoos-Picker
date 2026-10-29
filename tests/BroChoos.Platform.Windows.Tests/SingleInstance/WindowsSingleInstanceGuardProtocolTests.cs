using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Platform.Windows.SingleInstance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.SingleInstance;

public sealed class WindowsSingleInstanceGuardProtocolTests
{
	private const string SharedMutexSkipReason = SharedGuardMutex.SkipReason;

	[SkippableFact]
	public async Task DuplicateRequestId_IsRejectedOnSecondForward_AndOnlyFirstIsEnqueued()
	{
		using PrimaryHost primary = new(new ApprovedPolicyProvider());
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue();
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary, "the mutex was free before this host was created, so the first acquirer must be the primary");

		Guid duplicateRequestId = Guid.NewGuid();
		ActivationRequest first = BuildRequest("https://example.com/dup/1", duplicateRequestId, DateTimeOffset.UtcNow.AddMinutes(5));
		ActivationRequest second = BuildRequest("https://example.com/dup/2", duplicateRequestId, DateTimeOffset.UtcNow.AddMinutes(5));

		using WindowsSingleInstanceGuard secondaryA = NewGuard(new ApprovedPolicyProvider());
		using WindowsSingleInstanceGuard secondaryB = NewGuard(new ApprovedPolicyProvider());

		SingleInstanceRole firstRole = await secondaryA.AcquireOrForwardAsync(first, CancellationToken.None);
		SingleInstanceRole secondRole = await secondaryB.AcquireOrForwardAsync(second, CancellationToken.None);

		_ = firstRole.Should().Be(SingleInstanceRole.Secondary);
		_ = secondRole.Should().Be(SingleInstanceRole.Failed);
		bool delivered = await WaitForAsync(() => primary.Received.Count == 1);
		_ = delivered.Should().BeTrue();
		_ = primary.Received.Single().RequestId.Should().Be(duplicateRequestId);
	}

	[SkippableFact]
	public async Task MalformedRequestFrame_IsRejectedAndNeverEnqueued()
	{
		using PrimaryHost primary = new(new ApprovedPolicyProvider());
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue();
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary, "the mutex was free before this host was created, so the first acquirer must be the primary");

		await using NamedPipeClientStream client = await ConnectRawClientAsync();
		await SendPeerIdentityAsync(client, primary.IssueSecret());
		await client.WriteAsync(new byte[] { 0x00, 0x00, 0x00, 0x02, 0xC3, 0x28 });
		await client.FlushAsync();

		await Task.Delay(300);
		_ = primary.Received.Should().BeEmpty();
	}

	[SkippableFact]
	public async Task StalledReadAfterIdentity_TimesOutAndNeverEnqueues()
	{
		using PrimaryHost primary = new(new ApprovedPolicyProvider());
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue();
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary, "the mutex was free before this host was created, so the first acquirer must be the primary");

		await using NamedPipeClientStream client = await ConnectRawClientAsync();
		await SendPeerIdentityAsync(client, primary.IssueSecret());
		await Task.Delay(2600);

		_ = primary.Received.Should().BeEmpty();
	}

	[SkippableFact]
	public async Task AbruptDisconnectBeforeIdentity_NeverEnqueues()
	{
		using PrimaryHost primary = new(new ApprovedPolicyProvider());
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue();
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary, "the mutex was free before this host was created, so the first acquirer must be the primary");

		await using (NamedPipeClientStream client = await ConnectRawClientAsync())
		{
		}

		await Task.Delay(300);
		_ = primary.Received.Should().BeEmpty();
	}

	[SkippableFact]
	public async Task UnavailableTrustProvider_FailsBeforeEnqueue_AndSecondaryTimesOutOnAck()
	{
		using PrimaryHost primary = new(new UnavailablePolicyProvider());
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue();
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary, "the mutex was free before this host was created, so the first acquirer must be the primary");

		using WindowsSingleInstanceGuard secondary = NewGuard(new ApprovedPolicyProvider());
		SingleInstanceRole role = await secondary.AcquireOrForwardAsync(BuildRequest("https://example.com/unavailable", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);

		_ = role.Should().Be(SingleInstanceRole.Failed);
		_ = primary.Received.Should().BeEmpty();
	}

	[SkippableFact]
	public async Task RevokedTrustProvider_FailsBeforeEnqueue_AndSecondaryTimesOutOnAck()
	{
		using PrimaryHost primary = new(new RevokedPolicyProvider());
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue();
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary, "the mutex was free before this host was created, so the first acquirer must be the primary");

		using WindowsSingleInstanceGuard secondary = NewGuard(new ApprovedPolicyProvider());
		SingleInstanceRole role = await secondary.AcquireOrForwardAsync(BuildRequest("https://example.com/revoked", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);

		_ = role.Should().Be(SingleInstanceRole.Failed);
		_ = primary.Received.Should().BeEmpty();
	}

	[SkippableFact]
	public async Task StalledAckFromPrimary_ReturnsFailed()
	{
		string mutexName = SharedGuardMutex.Name;
		string pipeName = BuildPipeName();
		using Mutex mutex = new(initiallyOwned: true, mutexName, out bool created);

		// `created` false means the named mutex already existed, i.e. it is demonstrably held
		// by another process; any other outcome means this test owns it and must go on.
		Skip.IfNot(created, SharedMutexSkipReason);

		using CancellationTokenSource serverCts = new();
		Task server = Task.Run(async () =>
		{
			await using NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await pipe.WaitForConnectionAsync(serverCts.Token);

			byte[] peerHeader = new byte[4];
			await ReadExactAsync(pipe, peerHeader, serverCts.Token);
			int peerLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(peerHeader);
			byte[] peerPayload = new byte[peerLength];
			await ReadExactAsync(pipe, peerPayload, serverCts.Token);

			byte[] reqHeader = new byte[4];
			await ReadExactAsync(pipe, reqHeader, serverCts.Token);
			int reqLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(reqHeader);
			byte[] reqPayload = new byte[reqLength];
			await ReadExactAsync(pipe, reqPayload, serverCts.Token);

			await Task.Delay(2600, serverCts.Token);
		});

		using WindowsSingleInstanceGuard secondary = NewGuard(new ApprovedPolicyProvider());
		SingleInstanceRole role = SingleInstanceRole.Primary;
		Thread forwardThread = new(() =>
		{
			role = secondary.AcquireOrForwardAsync(BuildRequest("https://example.com/stalled-ack", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None).GetAwaiter().GetResult();
		})
		{ IsBackground = true };
		forwardThread.Start();
		_ = forwardThread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

		_ = role.Should().Be(SingleInstanceRole.Failed);
		serverCts.Cancel();
		try { await server; } catch (OperationCanceledException) { }
	}

	private static WindowsSingleInstanceGuard NewGuard(IIpcTrustPolicyProvider provider)
	{
		OneShotSecretValidator oneShot = new();
		IpcPeerAuthenticator authenticator = new(oneShot, provider);
		return new WindowsSingleInstanceGuard(NullLogger<WindowsSingleInstanceGuard>.Instance, authenticator, oneShot);
	}

	private static ActivationRequest BuildRequest(string url, Guid requestId, DateTimeOffset expiresAtUtc)
	{
		return new ActivationRequest(
			url,
			1,
			ActivationTriggerContext.ForwardedFromSecondary,
			SourceEvidence.Probable("test"),
			ActivationModifiers.None,
			requestId,
			1,
			expiresAtUtc);
	}

	private static async Task<NamedPipeClientStream> ConnectRawClientAsync()
	{
		NamedPipeClientStream client = new(".", BuildPipeName(), PipeDirection.InOut, PipeOptions.Asynchronous);
		await client.ConnectAsync(2000);
		return client;
	}

	private static async Task SendPeerIdentityAsync(NamedPipeClientStream client, string oneShotSecret)
	{
		string executablePath = Environment.ProcessPath ?? string.Empty;
		string signer = "CN=BroChoos Dev Runner";
		string payload = string.Join(
			"|",
			"PEER",
			Convert.ToBase64String(Encoding.UTF8.GetBytes(executablePath)),
			Convert.ToBase64String(Encoding.UTF8.GetBytes(signer)),
			Convert.ToBase64String(Encoding.UTF8.GetBytes(oneShotSecret)));

		byte[] body = Encoding.UTF8.GetBytes(payload);
		byte[] frame = new byte[4 + body.Length];
		System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), body.Length);
		body.CopyTo(frame.AsSpan(4));

		await client.WriteAsync(frame);
		await client.FlushAsync();
	}

	private static string BuildPipeName()
	{
		string sid = WindowsIdentity.GetCurrent().User?.Value ?? "anon";
		return $"BroChoos.Pipe.{sid}";
	}

	private static async Task<bool> WaitForAsync(Func<bool> condition)
	{
		const int timeoutMs = 10_000;
		int waited = 0;
		while (!condition())
		{
			if (waited >= timeoutMs)
			{
				return false;
			}

			await Task.Delay(50);
			waited += 50;
		}

		return true;
	}

	private static async Task ReadExactAsync(PipeStream stream, byte[] buffer, CancellationToken cancellationToken)
	{
		int offset = 0;
		while (offset < buffer.Length)
		{
			int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
			if (read == 0)
			{
				throw new EndOfStreamException();
			}

			offset += read;
		}
	}

	private sealed class PrimaryHost : IDisposable
	{
		private readonly ManualResetEventSlim _acquired = new();
		private readonly ManualResetEventSlim _stop = new();
		private readonly Thread _thread;

		internal PrimaryHost(IIpcTrustPolicyProvider provider)
		{
			// Probe BEFORE the guard exists and BEFORE its thread starts: skip only when the
			// mutex is demonstrably held by another process. When it is free, the first
			// acquirer must be Primary and the test asserts that instead of skipping.
			Skip.If(SharedGuardMutex.IsHeldElsewhere(), SharedMutexSkipReason);

			OneShotSecretValidator oneShot = new();
			OneShot = oneShot;
			IpcPeerAuthenticator auth = new(oneShot, provider);
			Guard = new WindowsSingleInstanceGuard(NullLogger<WindowsSingleInstanceGuard>.Instance, auth, oneShot);
			Guard.UrlReceived += (_, activation) => Received.Add(activation);

			_thread = new Thread(Run) { IsBackground = true };
			_thread.Start();
		}

		internal WindowsSingleInstanceGuard Guard { get; }
		internal List<ActivationRequest> Received { get; } = [];
		internal OneShotSecretValidator OneShot { get; }

		internal SingleInstanceRole Role { get; private set; } = SingleInstanceRole.Failed;

		internal string IssueSecret()
		{
			return OneShot.Issue();
		}

		internal bool WaitForPrimary(TimeSpan timeout)
		{
			return _acquired.Wait(timeout);
		}

		private void Run()
		{
			Role = Guard.AcquireOrForwardAsync(BuildRequest("https://primary.example/", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None).GetAwaiter().GetResult();
			_acquired.Set();
			_stop.Wait();
			Guard.Dispose();
		}

		public void Dispose()
		{
			_stop.Set();
			_ = _thread.Join(TimeSpan.FromSeconds(10));
			_acquired.Dispose();
			_stop.Dispose();
		}
	}

	private sealed class ApprovedPolicyProvider : IIpcTrustPolicyProvider
	{
		public bool TryGetPolicy(out IpcTrustPolicy? policy, out IpcPeerAuthenticationFailure? failure)
		{
			policy = new IpcTrustPolicy(
				IpcTrustPolicyGate.RequiredSubjectId,
				IpcTrustPolicyGate.RequiredScope,
				"approved",
				"PASS",
				"x509-chain",
				false,
				"DH-2026-09-11",
				"owner@brochoos",
				DateTimeOffset.UtcNow);
			failure = null;
			return true;
		}
	}

	private sealed class UnavailablePolicyProvider : IIpcTrustPolicyProvider
	{
		public bool TryGetPolicy(out IpcTrustPolicy? policy, out IpcPeerAuthenticationFailure? failure)
		{
			policy = null;
			failure = new IpcPeerAuthenticationFailure(IpcPeerAuthenticationErrorCode.TrustAnchorUnavailable, "ipc.trust-anchor.unavailable");
			return false;
		}
	}

	private sealed class RevokedPolicyProvider : IIpcTrustPolicyProvider
	{
		public bool TryGetPolicy(out IpcTrustPolicy? policy, out IpcPeerAuthenticationFailure? failure)
		{
			policy = null;
			failure = new IpcPeerAuthenticationFailure(IpcPeerAuthenticationErrorCode.TrustAnchorRevoked, "ipc.trust-anchor.revoked");
			return false;
		}
	}
}
