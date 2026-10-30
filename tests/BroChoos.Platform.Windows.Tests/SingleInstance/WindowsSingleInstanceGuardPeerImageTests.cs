using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Platform.Windows.SingleInstance;
using BroChoos.Platform.Windows.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.SingleInstance;

// F-2b END-TO-END: the pipe SERVER must refuse a connection from a process whose image is
// not the guard's own executable, and it must refuse it BEFORE the peer's identity frame
// is read or trusted.
//
// This is the production shape the unit tests cannot show: a real second process connects
// to the real named pipe and sends a PERFECTLY WELL-FORMED identity frame (correct claimed
// path, correct claimed signer, a first-seen one-shot secret). Every frame-level check
// would pass it — the process-image anchor is the only thing that can refuse it, which is
// exactly the property F-2b adds.
public sealed class WindowsSingleInstanceGuardPeerImageTests
{
	private const string ProbeUrl = "https://example.com/impostor";

	// SkippableFact, not Fact, and the reason is CROSS-ASSEMBLY contention rather than
	// doubt about the product. The guard's mutex and pipe names are per-user by design, so
	// this test needs to own them. Under `dotnet test BroChoos.sln` the App.Tests assembly
	// (ForwardedUrlRouterTests) drives guards over the SAME per-user names in parallel.
	// PrimaryHost probes the mutex BEFORE creating its guard (SharedGuardMutex) and skips only
	// when it already exists; when it is free, a non-Primary role is a real guard bug and the
	// test fails on it. Xunit.SkippableFact is already referenced by this project.
	[SkippableFact]
	public async Task ForwardFromADifferentImage_IsRejectedBeforeEnqueue_AndNeverDelivered()
	{
		using PrimaryHost primary = new(new ApprovedPolicyProvider());
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue("local mutex work; 10 s is generous");
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary, "the mutex was free before this host was created, so the first acquirer must be the primary");

		// The impostor: a REAL separate process (cmd.exe), not this test host. It connects
		// to the guard's pipe and speaks the protocol correctly.
		string? scriptPath = null;
		try
		{
			using Process impostor = StartImpostorClient(primary, out Task<int> impostorExit, out scriptPath);
			int exitCode = await impostorExit;

			_ = exitCode.Should().Be(2, "the guard must reject the connection (the client reports 2 for Rejected)");

			await Task.Delay(300);
			_ = primary.Received.Should().BeEmpty("a connection from a foreign image must never reach the queue");
			_ = primary.Logger.Entries.Should().Contain(
				entry => entry.Contains("ipc.peer.image-mismatch", StringComparison.Ordinal),
				"the refusal must be a named, observable marker");
		}
		finally
		{
			// The exit continuation deletes the script too, but only once the child exits; this
			// covers a failure before that point. Best effort: a missing file is not an error.
			if (scriptPath is not null)
			{
				TestTempCleanup.DeleteFile(scriptPath);
			}
		}
	}

	[SkippableFact]
	public async Task ForwardFromTheSameImage_IsStillAccepted_SoTheAnchorDoesNotBreakRealForwarding()
	{
		// The positive control at the pipe level. A real in-process forward (the same
		// executable, i.e. the same image) must still be accepted and delivered — this is
		// the property the reverted F-2a fix destroyed in production.
		using PrimaryHost primary = new(new ApprovedPolicyProvider());
		_ = primary.WaitForPrimary(TimeSpan.FromSeconds(10)).Should().BeTrue();
		_ = primary.Role.Should().Be(SingleInstanceRole.Primary, "the mutex was free before this host was created, so the first acquirer must be the primary");

		OneShotSecretValidator oneShot = new();
		IpcPeerAuthenticator authenticator = new(oneShot, new ApprovedPolicyProvider());
		using WindowsSingleInstanceGuard secondary = new(
			Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsSingleInstanceGuard>.Instance,
			authenticator,
			oneShot);

		SingleInstanceRole role = SingleInstanceRole.Failed;
		Thread forwarder = new(() =>
		{
			role = secondary.AcquireOrForwardAsync(
				BuildRequest(ProbeUrl, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)),
				CancellationToken.None).GetAwaiter().GetResult();
		})
		{ IsBackground = true };
		forwarder.Start();
		_ = forwarder.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

		_ = role.Should().Be(SingleInstanceRole.Secondary, "a same-image peer must still be able to forward");
		bool delivered = await WaitForAsync(() => primary.Received.Count == 1);
		_ = delivered.Should().BeTrue("the accepted forward must reach the picker handoff");
	}

	private static Process StartImpostorClient(PrimaryHost primary, out Task<int> exitCode, out string scriptPath)
	{
		// The child runs a script that connects to the guard's pipe, writes a valid
		// identity frame plus a valid activation request, and reports the ack status.
		// It uses PowerShell because the frame is a small amount of binary protocol and
		// this keeps the test free of a purpose-built helper executable.
		string script = BuildImpostorScript(PrimaryHost.PipeName, primary.IssueSecret());
		string path = Path.Combine(Path.GetTempPath(), $"brochoos-impostor-{Guid.NewGuid():N}.ps1");
		scriptPath = path;
		File.WriteAllText(path, script);

		ProcessStartInfo startInfo = new("powershell.exe")
		{
			Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		// A PowerShell 7 parent leaks its PSModulePath, and Windows PowerShell 5.1 then cannot autoload
		// its own modules (Microsoft.PowerShell.Security). Absent, 5.1 rebuilds its default path.
		_ = startInfo.Environment.Remove("PSModulePath");

		Process child = Process.Start(startInfo)!;
		exitCode = child.WaitForExitAsync().ContinueWith(_ =>
		{
			TestTempCleanup.DeleteFile(path);
			return child.ExitCode;
		}, TaskScheduler.Default);

		return child;
	}

	private static string BuildImpostorScript(string pipeName, string oneShotSecret)
	{
		string executablePath = Environment.ProcessPath ?? string.Empty;
		string signer = "CN=BroChoos Dev Runner";

		// The frame is built exactly as the guard builds it, so every frame-level check
		// passes. Only the process-image anchor can refuse this client.
		string peerPayload = string.Join(
			"|",
			"PEER",
			Convert.ToBase64String(Encoding.UTF8.GetBytes(executablePath)),
			Convert.ToBase64String(Encoding.UTF8.GetBytes(signer)),
			Convert.ToBase64String(Encoding.UTF8.GetBytes(oneShotSecret)));

		string requestPayload = string.Join(
			"|",
			"REQ",
			"1",
			Guid.NewGuid().ToString("N"),
			DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
			Convert.ToBase64String(Encoding.UTF8.GetBytes(ProbeUrl)));

		return $$"""
			$ErrorActionPreference = 'Stop'
			# Exit codes: 0 = Accepted, 2 = not accepted (Rejected ack OR the guard closed
			# the connection without one), 4 = an ack arrived but named neither status.
			$result = 2
			try {
				$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', '{{pipeName}}', [System.IO.Pipes.PipeDirection]::InOut)
				$pipe.Connect(5000)
				function Write-Frame([System.IO.Pipes.PipeStream]$p, [string]$payload) {
					$body = [System.Text.Encoding]::UTF8.GetBytes($payload)
					$len = [BitConverter]::GetBytes([int]$body.Length)
					[Array]::Reverse($len)
					$p.Write($len, 0, 4)
					$p.Write($body, 0, $body.Length)
					$p.Flush()
				}
				Write-Frame $pipe '{{peerPayload}}'
				# The guard rejects a foreign image BEFORE it reads the request frame, so it
				# has no request id to answer with and closes the connection. A failed write
				# or a short read therefore means 'not accepted', exactly like a Rejected ack.
				Write-Frame $pipe '{{requestPayload}}'
				$lenBuf = New-Object byte[] 4
				$read = 0
				while ($read -lt 4) { $n = $pipe.Read($lenBuf, $read, 4 - $read); if ($n -eq 0) { break }; $read += $n }
				if ($read -eq 4) {
					[Array]::Reverse($lenBuf)
					$payloadLen = [BitConverter]::ToInt32($lenBuf, 0)
					$payload = New-Object byte[] $payloadLen
					$read = 0
					while ($read -lt $payloadLen) { $n = $pipe.Read($payload, $read, $payloadLen - $read); if ($n -eq 0) { break }; $read += $n }
					$text = [System.Text.Encoding]::UTF8.GetString($payload, 0, $read)
					if ($text -like '*Accepted*') { $result = 0 } elseif ($text -notlike '*Rejected*') { $result = 4 }
				}
			} catch { $result = 2 }
			exit $result
			""";
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

	// Owns the primary side on a dedicated thread for the test's whole duration, exactly
	// like the protocol and URL-scheme suites: the mutex is acquired AND released on the
	// same owner thread, which keeps the Global mutex from staying stuck-owned.
	private sealed class PrimaryHost : IDisposable
	{
		private readonly ManualResetEventSlim _acquired = new();
		private readonly ManualResetEventSlim _stop = new();
		private readonly Thread _thread;

		internal PrimaryHost(IIpcTrustPolicyProvider provider)
		{
			// Probe BEFORE the guard exists: skip only when the per-user mutex is demonstrably
			// held by another process. When it is free the first acquirer must be Primary, and
			// the tests assert exactly that instead of skipping.
			Skip.If(SharedGuardMutex.IsHeldElsewhere(), SharedGuardMutex.SkipReason);

			OneShotSecretValidator oneShot = new();
			OneShot = oneShot;
			IpcPeerAuthenticator auth = new(oneShot, provider);
			Guard = new WindowsSingleInstanceGuard(Logger, auth, oneShot);
			Guard.UrlReceived += (_, activation) => Received.Add(activation);

			_thread = new Thread(Run) { IsBackground = true };
			_thread.Start();
		}

		internal WindowsSingleInstanceGuard Guard { get; }

		internal List<ActivationRequest> Received { get; } = [];

		internal ListLogger<WindowsSingleInstanceGuard> Logger { get; } = new();

		internal SingleInstanceRole Role { get; private set; } = SingleInstanceRole.Failed;

		internal OneShotSecretValidator OneShot { get; }

		internal static string PipeName
		{
			get
			{
				string sid = WindowsIdentity.GetCurrent().User?.Value ?? "anon";
				return $"BroChoos.Pipe.{sid}";
			}
		}

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

	// Captures every level so the refusal marker can be asserted. Locked because the guard
	// logs from its own server-loop thread.
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
