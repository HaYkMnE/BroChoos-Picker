using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Ipc;
using BroChoos.Platform.Windows.SingleInstance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.SingleInstance;

// F-2 (2A, HIGH): the forwarded-URL path had NO scheme validation. The pipe decoder
// accepts any absolute URI (ActivationProtocol checks length and absoluteness only), the
// guard enqueued it, and the picker's F key hands the value to the shell through
// ProcessStartInfo { FileName = url, UseShellExecute = true } — so
// file:///C:/Windows/System32/calc.exe, javascript:, ms-settings: and vbscript: all
// reached ShellExecute. The CLI path has always been scheme-validated (CliParser through
// IUrlValidator, http/https only); the forwarded path was the gap, and it is the one a
// second process can reach.
//
// These tests run the REAL pipe: a production primary guard (the same shape
// WindowsSingleInstanceGuardProtocolTests uses) plus a raw pipe client that sends a valid
// peer identity and a WELL-FORMED activation frame carrying the probe URL. The frame is
// asserted to parse BEFORE it is sent, so a rejection observed below can only come from
// the URL gate — never from the frame parser, and never from the peer authentication.
public sealed class WindowsSingleInstanceGuardUrlSchemeTests
{
	[SkippableTheory]
	[InlineData("file:///C:/Windows/System32/calc.exe", "file")]
	[InlineData("javascript:alert(1)", "javascript")]
	[InlineData("ms-settings:defaultapps", "ms-settings")]
	[InlineData("vbscript:msgbox(1)", "vbscript")]
	public async Task ForwardedNonWebScheme_IsRejectedBeforeEnqueue_AndNeverReachesTheQueue(string probeUrl, string expectedScheme)
	{
		Guid requestId = Guid.NewGuid();
		byte[] frame = PipeMessages.EncodeActivationRequest(BuildRequest(probeUrl, requestId, DateTimeOffset.UtcNow.AddMinutes(5)));

		// PRECONDITION, asserted rather than assumed: the probe is a well-formed activation
		// frame whose hostile scheme survives the decode. This is the runtime-proven shape
		// of the defect — the URL used to be enqueued, not refused — and it is what makes
		// the Rejected ack below evidence about the URL gate specifically.
		_ = PipeMessages.TryParseActivationRequest(frame, DateTimeOffset.UtcNow, out ActivationRequest? decoded, out string parseReason)
			.Should().BeTrue($"the probe must reach the URL gate; the frame parser said '{parseReason}'");
		_ = new Uri(decoded!.NormalizedUrl).Scheme.Should().Be(expectedScheme);

		using PrimaryHost primary = new(new ApprovedPolicyProvider());
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue("local mutex work; 10 s is generous");
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary);

		await using NamedPipeClientStream client = await ConnectRawClientAsync();
		await SendPeerIdentityAsync(client, primary.IssueSecret());
		await client.WriteAsync(frame);
		await client.FlushAsync();

		ActivationAckStatus ack = await ReadAckStatusAsync(client, requestId);

		_ = ack.Should().Be(ActivationAckStatus.Rejected, "a forwarded non-web URL must be refused fail-closed, not buffered");
		await Task.Delay(250);
		_ = primary.Received.Should().BeEmpty("a refused URL must never be enqueued for the picker to act on");

		// Observability is part of the fix: the refusal is a named marker, and the log line
		// stays URL-free (the scheme is the only detail carried, and it is bounded).
		_ = primary.Logger.Entries.Should().ContainSingle(entry => entry.Contains("ipc.forwarded-url.rejected", StringComparison.Ordinal));
		_ = primary.Logger.Entries.Should().Contain(entry => entry.Contains($"scheme {expectedScheme}", StringComparison.Ordinal));
		_ = primary.Logger.Entries.Should().NotContain(entry => entry.Contains(probeUrl, StringComparison.Ordinal));
	}

	[SkippableFact]
	public async Task ForwardedHttpsUrl_IsStillAcceptedAndDelivered()
	{
		const string Url = "https://example.com/ok";
		Guid requestId = Guid.NewGuid();
		byte[] frame = PipeMessages.EncodeActivationRequest(BuildRequest(Url, requestId, DateTimeOffset.UtcNow.AddMinutes(5)));

		using PrimaryHost primary = new(new ApprovedPolicyProvider());
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue("local mutex work; 10 s is generous");
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary);

		await using NamedPipeClientStream client = await ConnectRawClientAsync();
		await SendPeerIdentityAsync(client, primary.IssueSecret());
		await client.WriteAsync(frame);
		await client.FlushAsync();

		ActivationAckStatus ack = await ReadAckStatusAsync(client, requestId);

		_ = ack.Should().Be(ActivationAckStatus.Accepted, "the gate must refuse non-web schemes, not every forwarded URL");
		bool delivered = await WaitForAsync(() => primary.Received.Count == 1);
		_ = delivered.Should().BeTrue("an accepted forwarded URL must still reach the picker handoff");
		_ = primary.Received.Single().NormalizedUrl.Should().Be(Url);
		_ = primary.Logger.Entries.Should().NotContain(entry => entry.Contains("ipc.forwarded-url.rejected", StringComparison.Ordinal));
	}

	// F6: the drain loop invoked UrlReceived with no try/catch, so ONE throwing subscriber
	// faulted the drain task and every later forward was acked Accepted but never delivered.
	// The loop must survive the throw, log it without the URL, and keep delivering.
	[SkippableFact]
	public async Task ThrowingSubscriber_DoesNotStopLaterForwardsFromBeingDelivered()
	{
		const string FirstUrl = "https://example.com/first-secret-path";
		const string SecondUrl = "https://example.com/second";

		using PrimaryHost primary = new(new ApprovedPolicyProvider());
		int throws = 0;
		primary.Guard.UrlReceived += (_, activation) =>
		{
			if (activation.NormalizedUrl == FirstUrl)
			{
				_ = Interlocked.Increment(ref throws);
				throw new InvalidOperationException("subscriber failure " + activation.NormalizedUrl);
			}
		};
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue("local mutex work; 10 s is generous");
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary);

		_ = (await ForwardAsync(primary, FirstUrl)).Should().Be(ActivationAckStatus.Accepted);
		_ = (await WaitForAsync(() => Volatile.Read(ref throws) == 1)).Should().BeTrue();
		_ = (await ForwardAsync(primary, SecondUrl)).Should().Be(ActivationAckStatus.Accepted);

		bool delivered = await WaitForAsync(() => primary.Received.Any(activation => activation.NormalizedUrl == SecondUrl));
		_ = delivered.Should().BeTrue("a subscriber exception must not kill the drain loop");
		_ = primary.Logger.Entries.Should().Contain(entry => entry.Contains("ipc.forwarded-url.subscriber-failed", StringComparison.Ordinal));
		_ = primary.Logger.Entries.Should().NotContain(entry => entry.Contains("first-secret-path", StringComparison.Ordinal));
	}

	// F6, per-subscriber isolation: with TWO subscribers and the FIRST one throwing, the
	// SECOND must still receive that same activation. The test above cannot tell a
	// per-handler catch from one that stops at the first throwing handler, because its
	// throwing subscriber is attached after the recording one.
	[SkippableFact]
	public async Task FirstSubscriberThrows_SecondSubscriberStillReceivesTheSameActivation()
	{
		const string Url = "https://example.com/two-subscribers";
		int throws = 0;
		using PrimaryHost primary = new(new ApprovedPolicyProvider(), (_, _) =>
		{
			_ = Interlocked.Increment(ref throws);
			throw new InvalidOperationException("first subscriber failure");
		});
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue("local mutex work; 10 s is generous");
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary);

		_ = (await ForwardAsync(primary, Url)).Should().Be(ActivationAckStatus.Accepted);

		bool delivered = await WaitForAsync(() => primary.Received.Any(activation => activation.NormalizedUrl == Url));
		_ = delivered.Should().BeTrue("a throwing first subscriber must not stop the second one");
		_ = Volatile.Read(ref throws).Should().Be(1);
		_ = primary.Logger.Entries.Should().ContainSingle(entry => entry.Contains("ipc.forwarded-url.subscriber-failed", StringComparison.Ordinal));
	}

	private static async Task<ActivationAckStatus> ForwardAsync(PrimaryHost primary, string url)
	{
		Guid requestId = Guid.NewGuid();
		byte[] frame = PipeMessages.EncodeActivationRequest(BuildRequest(url, requestId, DateTimeOffset.UtcNow.AddMinutes(5)));
		await using NamedPipeClientStream client = await ConnectRawClientAsync();
		await SendPeerIdentityAsync(client, primary.IssueSecret());
		await client.WriteAsync(frame);
		await client.FlushAsync();
		return await ReadAckStatusAsync(client, requestId);
	}

	// The DI half of the fix. The guard's validator travels through an OPTIONAL
	// constructor parameter (hand-built guards in tests pass three arguments), so
	// resolving the guard alone cannot show that the container supplied the registered
	// validator instead of letting the parameter default. Identity is what proves it: the
	// fallback constructs its own DefaultUrlValidator instance, so reference equality with
	// the registered singleton is only possible when the container injected it.
	[Fact]
	public void ComposedGuard_ReceivesTheUrlValidatorRegisteredInTheContainer()
	{
		ServiceCollection services = new();
		_ = services.AddLogging();
		_ = services.AddWindowsPlatformServices();
		using ServiceProvider provider = services.BuildServiceProvider();

		WindowsSingleInstanceGuard guard = provider.GetRequiredService<ISingleInstanceGuard>()
			.Should().BeOfType<WindowsSingleInstanceGuard>().Subject;

		_ = guard.UrlValidatorForComposition().Should().BeSameAs(provider.GetRequiredService<IUrlValidator>());
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
			ActivationProtocol.ProtocolVersion,
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

	// Reads the guard's typed answer. A timeout keeps a broken guard from hanging the
	// suite: the assertion below must fail, not the test run.
	private static async Task<ActivationAckStatus> ReadAckStatusAsync(NamedPipeClientStream client, Guid expectedRequestId)
	{
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

		byte[] lengthBuffer = new byte[4];
		await ReadExactAsync(client, lengthBuffer, cts.Token);
		int payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
		byte[] frame = new byte[4 + payloadLength];
		lengthBuffer.CopyTo(frame, 0);
		await ReadExactAsync(client, frame.AsMemory(4, payloadLength), cts.Token);

		_ = PipeMessages.TryParseActivationAck(frame, expectedRequestId, out ActivationProtocolAck? ack, out string reason)
			.Should().BeTrue($"the guard must answer with a well-formed ack; the parser said '{reason}'");

		return ack!.Status;
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
		await ReadExactAsync(stream, buffer.AsMemory(0, buffer.Length), cancellationToken);
	}

	private static async Task ReadExactAsync(PipeStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
	{
		int offset = 0;
		while (offset < buffer.Length)
		{
			int read = await stream.ReadAsync(buffer[offset..], cancellationToken);
			if (read == 0)
			{
				throw new EndOfStreamException();
			}

			offset += read;
		}
	}

	// Owns the primary side on a dedicated thread for the test's whole duration: the
	// mutex is acquired AND released on the same (owner) thread, which is what keeps the
	// Global mutex from staying stuck-owned for the next test in the suite.
	private sealed class PrimaryHost : IDisposable
	{
		private readonly ManualResetEventSlim _acquired = new();
		private readonly ManualResetEventSlim _stop = new();
		private readonly Thread _thread;

		// firstSubscriber, when given, is attached BEFORE the recording handler, so a test can
		// put a throwing subscriber ahead of the one that observes delivery.
		internal PrimaryHost(IIpcTrustPolicyProvider provider, EventHandler<ActivationRequest>? firstSubscriber = null)
		{
			// Probe BEFORE the guard exists: skip only when the per-user mutex is demonstrably
			// held by another process. When it is free the first acquirer must be Primary, and
			// the tests assert exactly that instead of skipping.
			Skip.If(SharedGuardMutex.IsHeldElsewhere(), SharedGuardMutex.SkipReason);

			OneShotSecretValidator oneShot = new();
			OneShot = oneShot;
			IpcPeerAuthenticator auth = new(oneShot, provider);
			Guard = new WindowsSingleInstanceGuard(Logger, auth, oneShot);
			if (firstSubscriber is not null)
			{
				Guard.UrlReceived += firstSubscriber;
			}

			Guard.UrlReceived += (_, activation) => Received.Add(activation);

			_thread = new Thread(Run) { IsBackground = true };
			_thread.Start();
		}

		internal WindowsSingleInstanceGuard Guard { get; }

		internal List<ActivationRequest> Received { get; } = [];

		internal ListLogger<WindowsSingleInstanceGuard> Logger { get; } = new();

		internal SingleInstanceRole Role { get; private set; } = SingleInstanceRole.Failed;

		internal OneShotSecretValidator OneShot { get; }

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
			Role = Guard.AcquireOrForwardAsync(
				BuildRequest("https://primary.example/", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)),
				CancellationToken.None).GetAwaiter().GetResult();
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

	// Captures every level so the refusal marker and the URL-free property can be
	// asserted. Locked because the guard logs from its own server-loop thread.
	private sealed class ListLogger<T> : ILogger<T>
	{
		private readonly List<string> _entries = [];
		private readonly Lock _gate = new();

		internal IReadOnlyList<string> Entries
		{
			get
			{
				lock (_gate)
				{
					return [.. _entries];
				}
			}
		}

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			lock (_gate)
			{
				_entries.Add(formatter(state, exception));
			}
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
}
