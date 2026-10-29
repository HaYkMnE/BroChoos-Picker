using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Ipc;
using BroChoos.Platform.Windows.Urls;
using Microsoft.Extensions.Logging;

namespace BroChoos.Platform.Windows.SingleInstance;

public sealed class WindowsSingleInstanceGuard : ISingleInstanceGuard, IDisposable
{
	private const int ConnectTimeoutMs = 2000;
	private const int ReadTimeoutMs = 2000;
	private const int ValidationTimeoutMs = 2000;
	private const int EnqueueTimeoutMs = 2000;
	private const int AckTimeoutMs = 2000;
	private const int QueueCapacity = 5;
	private const int MaxExecutablePathBytes = 1024;
	private const int MaxSignerIdentityBytes = 512;
	private const int MaxSecretBytes = 256;
	private const int MaxPeerIdentityFrameBytes = MaxExecutablePathBytes + MaxSignerIdentityBytes + MaxSecretBytes + 256;
	private const int MaxLoggedSchemeChars = 32;
	private readonly ILogger<WindowsSingleInstanceGuard> _logger;
	private readonly IUrlValidator _urlValidator;
	private readonly IpcPeerAuthenticator? _ipcPeerAuthenticator;
	private readonly OneShotSecretValidator? _oneShotSecretValidator;
	private readonly BoundedActivationQueue _queue = new(QueueCapacity);
	private readonly string _mutexName;
	private readonly string _pipeName;
	private readonly string _localExecutablePath;
	private readonly string _localSignerIdentity;
	private Mutex? _mutex;
	private bool _ownsMutex;
	private CancellationTokenSource? _serverCts;
	private Task? _serverLoop;
	private Task? _drainLoop;

	// Raised on the pipe-server loop thread for every forwarded URL; the 0A-3
	// handoff point for the picker UI. Subscribe before the primary role starts
	// (Program wires it right after the container is built), so no forwarded URL
	// arrives without someone answering it.
	public event EventHandler<ActivationRequest>? UrlReceived;

	public WindowsSingleInstanceGuard(
		ILogger<WindowsSingleInstanceGuard> logger,
		IpcPeerAuthenticator? ipcPeerAuthenticator = null,
		OneShotSecretValidator? oneShotSecretValidator = null,
		IUrlValidator? urlValidator = null)
	{
		_logger = logger;
		_ipcPeerAuthenticator = ipcPeerAuthenticator;
		_oneShotSecretValidator = oneShotSecretValidator;
		// F-2 (2A): the forwarded-URL scheme gate. The container injects the registered
		// IUrlValidator; hand-built guards (the protocol and router tests construct this
		// type with three arguments) fall back to the SAME http/https-only rule — never to
		// an accept-all. The fallback is strict on purpose: a dropped registration must
		// fail closed, and a test seam that silently disabled the gate would be the defect
		// wearing a constructor parameter.
		_urlValidator = urlValidator ?? new DefaultUrlValidator();
		string sid = GetUserSidForName();
		// Global scope: the same user in two sessions must converge to ONE primary.
		_mutexName = $@"Global\BroChoos.SingleInstance.{sid}";
		_pipeName = $"BroChoos.Pipe.{sid}";
		_localExecutablePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? string.Empty;
		_localSignerIdentity = "CN=BroChoos Dev Runner";
	}

	// F-2 (2A): observable seam for the composition test. The container must INJECT the
	// registered validator rather than let the constructor fall back, and reference
	// equality with the container's IUrlValidator is the only way to tell those apart —
	// the fallback is a different instance of the same type.
	internal IUrlValidator UrlValidatorForComposition()
	{
		return _urlValidator;
	}

	internal IpcPeerAuthenticationResult ValidatePeerForW1_3Seam(
		string localExecutablePath,
		string peerExecutablePath,
		string? localSignerIdentity,
		string? peerSignerIdentity,
		string? oneShotSecret)
	{
		return _ipcPeerAuthenticator is null
			? IpcPeerAuthenticationResult.Fail(IpcPeerAuthenticationErrorCode.TrustAnchorUnavailable, "ipc.trust-anchor.unavailable")
			: _ipcPeerAuthenticator.Authenticate(localExecutablePath, peerExecutablePath, localSignerIdentity, peerSignerIdentity, oneShotSecret);
	}

	public async Task<SingleInstanceRole> AcquireOrForwardAsync(ActivationRequest request, CancellationToken cancellationToken)
	{
		_mutex = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);

		if (createdNew)
		{
			StartAsPrimary(cancellationToken);
			return SingleInstanceRole.Primary;
		}

		// The mutex already exists, but the primary may have exited: take ownership
		// when the mutex is free, and AbandonedMutexException also grants ownership
		// when the previous holder died without releasing it.
		if (TryTakeOwnership())
		{
			StartAsPrimary(cancellationToken);
			return SingleInstanceRole.Primary;
		}

		_mutex.Dispose();
		_mutex = null;

		try
		{
			await using NamedPipeClientStream client = new(
				".",
				_pipeName,
				PipeDirection.InOut,
				PipeOptions.Asynchronous);

			await client.ConnectAsync(ConnectTimeoutMs, cancellationToken);

			if (!await SendPeerIdentityAsync(client, cancellationToken))
			{
				return SingleInstanceRole.Failed;
			}

			byte[] requestFrame = PipeMessages.EncodeActivationRequest(request);
			using CancellationTokenSource writeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			writeTimeoutCts.CancelAfter(ReadTimeoutMs);

			try
			{
				await client.WriteAsync(requestFrame, writeTimeoutCts.Token);
				await client.FlushAsync(writeTimeoutCts.Token);
			}
			catch (OperationCanceledException) when (writeTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				_logger.LogWarning("Forward to primary timed out during request write");
				return SingleInstanceRole.Failed;
			}

			byte[] ackFrame;
			try
			{
				ackFrame = await ReadBoundedFrameAsync(client, AckTimeoutMs, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				_logger.LogWarning("Forward to primary timed out waiting for ack");
				return SingleInstanceRole.Failed;
			}

			if (!PipeMessages.TryParseActivationAck(ackFrame, request.RequestId, out ActivationProtocolAck? ack, out string ackError))
			{
				_logger.LogWarning("Forward to primary received invalid ack ({ReasonCode})", ackError);
				return SingleInstanceRole.Failed;
			}

			if (ack!.Status != ActivationAckStatus.Accepted)
			{
				_logger.LogWarning("Forward to primary rejected activation ({ReasonCode})", "ipc.request.rejected");
				return SingleInstanceRole.Failed;
			}

			return SingleInstanceRole.Secondary;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Forward to primary failed");
			return SingleInstanceRole.Failed;
		}
	}

	private bool TryTakeOwnership()
	{
		try
		{
			return _mutex!.WaitOne(0);
		}
		catch (AbandonedMutexException)
		{
			// The wait still grants ownership even though the previous holder abandoned the mutex.
			return true;
		}
	}

	private void StartAsPrimary(CancellationToken cancellationToken)
	{
		_ownsMutex = true;
		_serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_serverLoop = Task.Run(() => RunPipeServerAsync(_serverCts.Token), _serverCts.Token);
		_drainLoop = Task.Run(() => DrainQueueAsync(_serverCts.Token), _serverCts.Token);
	}

	private async Task RunPipeServerAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await using NamedPipeServerStream server = new(
					_pipeName,
					PipeDirection.InOut,
					NamedPipeServerStream.MaxAllowedServerInstances,
					PipeTransmissionMode.Byte,
					PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

				await server.WaitForConnectionAsync(cancellationToken);
				ActivationAckStatus ackStatus = await HandleAcceptedConnectionAsync(server, cancellationToken);
				if (ackStatus == ActivationAckStatus.Accepted)
				{
					_logger.LogInformation("Received forwarded activation from secondary instance");
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Pipe server iteration failed");
			}
		}
	}

	private async Task<ActivationAckStatus> HandleAcceptedConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
	{
		if (!await TryValidatePeerAsync(server, cancellationToken))
		{
			await TryWriteAckAsync(server, Guid.Empty, ActivationAckStatus.Rejected, cancellationToken);
			return ActivationAckStatus.Rejected;
		}

		byte[] requestFrame;
		try
		{
			requestFrame = await ReadBoundedFrameAsync(server, ReadTimeoutMs, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			await TryWriteAckAsync(server, Guid.Empty, ActivationAckStatus.Rejected, cancellationToken);
			return ActivationAckStatus.Rejected;
		}
		catch (Exception)
		{
			await TryWriteAckAsync(server, Guid.Empty, ActivationAckStatus.Rejected, cancellationToken);
			return ActivationAckStatus.Rejected;
		}

		DateTimeOffset now = DateTimeOffset.UtcNow;
		if (!PipeMessages.TryParseActivationRequest(requestFrame, now, out ActivationRequest? activation, out string parseReason))
		{
			_logger.LogWarning("Rejected activation frame ({ReasonCode})", parseReason);
			await TryWriteAckAsync(server, ExtractRequestId(requestFrame), ActivationAckStatus.Rejected, cancellationToken);
			return ActivationAckStatus.Rejected;
		}

		if (activation is null)
		{
			await TryWriteAckAsync(server, Guid.Empty, ActivationAckStatus.Rejected, cancellationToken);
			return ActivationAckStatus.Rejected;
		}

		// F-2 (2A) SCHEME GATE, and the position is the point: this runs BEFORE the
		// enqueue, so a refused URL is never buffered, never drained, never handed to the
		// picker, and never reaches the picker's F key — the one place a forwarded value
		// becomes ProcessStartInfo { FileName = url, UseShellExecute = true }.
		//
		// WHY THE FRAME DECODER IS NOT ENOUGH. ActivationProtocol.TryDecodeRequest accepts
		// any ABSOLUTE URI (length plus Uri.TryCreate only), which is exactly the shape of
		// the four probes that were runtime-proven to reach ShellExecute: file:///…calc.exe,
		// javascript:, ms-settings: and vbscript:. The CLI path has always been validated
		// (CliParser through IUrlValidator, http/https only); the pipe path — the one a
		// second process can reach — was not.
		//
		// FAIL-CLOSED, and the log line carries the SCHEME ONLY: the URL may contain user
		// data and persistent lines stay URL-free by convention. The scheme is attacker-
		// supplied text, so it is truncated as well as non-payload — a scheme can legally
		// run to hundreds of characters.
		if (!_urlValidator.IsAcceptable(activation.NormalizedUrl, out _))
		{
			_logger.LogWarning(
				"Rejected activation before enqueue ({ReasonCode}, scheme {Scheme})",
				"ipc.forwarded-url.rejected",
				DescribeSchemeForLog(activation.NormalizedUrl));
			await TryWriteAckAsync(server, activation.RequestId, ActivationAckStatus.Rejected, cancellationToken);
			return ActivationAckStatus.Rejected;
		}

		using CancellationTokenSource enqueueTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		enqueueTimeoutCts.CancelAfter(EnqueueTimeoutMs);

		if (!_queue.TryEnqueue(activation, now, out string queueReason))
		{
			_logger.LogWarning("Rejected activation enqueue ({ReasonCode})", queueReason);
			await TryWriteAckAsync(server, activation.RequestId, ActivationAckStatus.Rejected, enqueueTimeoutCts.Token);
			return ActivationAckStatus.Rejected;
		}

		await TryWriteAckAsync(server, activation.RequestId, ActivationAckStatus.Accepted, enqueueTimeoutCts.Token);
		return ActivationAckStatus.Accepted;
	}

	private async Task<bool> TryValidatePeerAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
	{
		if (_ipcPeerAuthenticator is null || _oneShotSecretValidator is null)
		{
			_logger.LogWarning("Rejected activation before enqueue ({ReasonCode})", "ipc.trust-anchor.unavailable");
			return false;
		}

		// F-2b: THE OS-LEVEL ANCHOR, and the position is the point. Everything below this
		// line is the peer's OWN account of itself; this is the only check the peer cannot
		// author. It runs first, before the frame is even read, so an impostor's claimed
		// path and signer never reach the frame-level comparisons at all.
		//
		// Fail-closed with no fallback: if the OS cannot name the connecting process's
		// image, the connection is refused. The frame-declared path is NEVER substituted
		// for the missing OS answer — that substitution would restore exactly the
		// self-reported trust this closes.
		IpcPeerAuthenticationResult image = PeerProcessImageVerifier.VerifyClientProcessImage(server.SafePipeHandle, _localExecutablePath);
		if (!image.Success)
		{
			_logger.LogWarning("Rejected activation before enqueue ({ReasonCode})", image.Failure!.RedactedReason);
			return false;
		}

		using CancellationTokenSource validationTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		validationTimeoutCts.CancelAfter(ValidationTimeoutMs);

		byte[] frame;
		try
		{
			frame = await ReadPeerIdentityFrameAsync(server, validationTimeoutCts.Token);
		}
		catch (OperationCanceledException)
		{
			_logger.LogWarning("Rejected activation before enqueue ({ReasonCode})", "ipc.validation.timeout");
			return false;
		}
		catch (Exception)
		{
			_logger.LogWarning("Rejected activation before enqueue ({ReasonCode})", "ipc.validation.read-failed");
			return false;
		}

		if (!TryParsePeerIdentityFrame(frame, out string peerExecutablePath, out string peerSignerIdentity, out string oneShotSecret, out string parseReason))
		{
			_logger.LogWarning("Rejected activation before enqueue ({ReasonCode})", parseReason);
			return false;
		}

		IpcPeerAuthenticationResult identity = _ipcPeerAuthenticator.ValidateTrustAndIdentity(
			_localExecutablePath,
			peerExecutablePath,
			_localSignerIdentity,
			peerSignerIdentity);

		if (!identity.Success)
		{
			_logger.LogWarning("Rejected activation before enqueue ({ReasonCode})", identity.Failure!.RedactedReason);
			return false;
		}

		IpcPeerAuthenticationResult secret = _oneShotSecretValidator.Consume(oneShotSecret);
		if (!secret.Success)
		{
			_logger.LogWarning("Rejected activation before enqueue ({ReasonCode})", secret.Failure!.RedactedReason);
			return false;
		}

		return true;
	}

	private async Task DrainQueueAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			ActivationRequest[] drained = DrainAllPending();
			foreach (ActivationRequest activation in drained)
			{
				// A subscriber exception must not fault this loop: every later forward has
				// already been acked Accepted to its secondary, so a dead drain would drop
				// them silently. The log carries the exception TYPE only — never the URL,
				// and never the message or stack, which a subscriber may have built from it.
				// Per subscriber, so one throwing handler cannot skip the others either.
				EventHandler<ActivationRequest>? handlers = UrlReceived;
				if (handlers is null)
				{
					continue;
				}

				foreach (EventHandler<ActivationRequest> handler in handlers.GetInvocationList().Cast<EventHandler<ActivationRequest>>())
				{
					try
					{
						handler(this, activation);
					}
					catch (Exception ex)
					{
						_logger.LogWarning(
							"Forwarded activation handler failed ({ReasonCode}, {ExceptionType})",
							"ipc.forwarded-url.subscriber-failed",
							ex.GetType().Name);
					}
				}
			}

			try
			{
				await Task.Delay(25, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}

	private ActivationRequest[] DrainAllPending()
	{
		List<ActivationRequest> drained = [];
		while (_queue.TryDequeue(out ActivationRequest? activation) && activation is not null)
		{
			drained.Add(activation);
		}

		return [.. drained];
	}

	private async Task<bool> SendPeerIdentityAsync(NamedPipeClientStream client, CancellationToken cancellationToken)
	{
		if (_oneShotSecretValidator is null)
		{
			_logger.LogWarning("Forward to primary failed before enqueue ({ReasonCode})", "ipc.secret.missing");
			return false;
		}

		string secret = _oneShotSecretValidator.Issue();
		byte[] frame = BuildPeerIdentityFrame(_localExecutablePath, _localSignerIdentity, secret);

		using CancellationTokenSource writeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		writeTimeoutCts.CancelAfter(ValidationTimeoutMs);

		try
		{
			await client.WriteAsync(frame, writeTimeoutCts.Token);
			await client.FlushAsync(writeTimeoutCts.Token);
			return true;
		}
		catch (OperationCanceledException)
		{
			_logger.LogWarning("Forward to primary timed out during identity write");
			return false;
		}
	}

	private static async Task<byte[]> ReadBoundedFrameAsync(PipeStream stream, int timeoutMs, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeoutMs);

		byte[] lengthBuffer = new byte[ActivationProtocol.LengthPrefixBytes];
		await ReadExactAsync(stream, lengthBuffer, timeoutCts.Token);

		int payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
		if (payloadLength is <= 0 or > (ActivationProtocol.MaxFrameBytes - ActivationProtocol.LengthPrefixBytes))
		{
			throw new InvalidDataException("ipc.frame.length");
		}

		byte[] frame = new byte[ActivationProtocol.LengthPrefixBytes + payloadLength];
		lengthBuffer.CopyTo(frame, 0);
		await ReadExactAsync(stream, frame.AsMemory(ActivationProtocol.LengthPrefixBytes, payloadLength), timeoutCts.Token);
		return frame;
	}

	private static async Task<byte[]> ReadPeerIdentityFrameAsync(PipeStream stream, CancellationToken cancellationToken)
	{
		byte[] lengthBuffer = new byte[4];
		await ReadExactAsync(stream, lengthBuffer, cancellationToken);
		int payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
		if (payloadLength is <= 0 or > MaxPeerIdentityFrameBytes)
		{
			throw new InvalidDataException("ipc.peer.frame.length");
		}

		byte[] frame = new byte[4 + payloadLength];
		lengthBuffer.CopyTo(frame, 0);
		await ReadExactAsync(stream, frame.AsMemory(4, payloadLength), cancellationToken);
		return frame;
	}

	private static async Task ReadExactAsync(PipeStream stream, byte[] target, CancellationToken cancellationToken)
	{
		await ReadExactAsync(stream, target.AsMemory(0, target.Length), cancellationToken);
	}

	private static async Task ReadExactAsync(PipeStream stream, Memory<byte> target, CancellationToken cancellationToken)
	{
		int offset = 0;
		while (offset < target.Length)
		{
			int read = await stream.ReadAsync(target[offset..], cancellationToken);
			if (read == 0)
			{
				throw new EndOfStreamException("ipc.frame.truncated");
			}

			offset += read;
		}
	}

	private static async Task TryWriteAckAsync(PipeStream stream, Guid requestId, ActivationAckStatus status, CancellationToken cancellationToken)
	{
		if (requestId == Guid.Empty)
		{
			return;
		}

		byte[] ack = PipeMessages.EncodeActivationAck(requestId, status);
		try
		{
			await stream.WriteAsync(ack, cancellationToken);
			await stream.FlushAsync(cancellationToken);
		}
		catch
		{
			// best effort, caller already has typed outcome
		}
	}

	private static byte[] BuildPeerIdentityFrame(string executablePath, string signerIdentity, string oneShotSecret)
	{
		string payload = string.Join(
			"|",
			"PEER",
			Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(executablePath)),
			Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(signerIdentity)),
			Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(oneShotSecret)));

		byte[] body = System.Text.Encoding.UTF8.GetBytes(payload);
		if (body.Length > MaxPeerIdentityFrameBytes)
		{
			throw new InvalidDataException("ipc.peer.frame.oversized");
		}

		byte[] frame = new byte[4 + body.Length];
		System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), body.Length);
		body.CopyTo(frame.AsSpan(4));
		return frame;
	}

	private static bool TryParsePeerIdentityFrame(byte[] frame, out string executablePath, out string signerIdentity, out string oneShotSecret, out string reasonCode)
	{
		executablePath = string.Empty;
		signerIdentity = string.Empty;
		oneShotSecret = string.Empty;

		if (frame.Length < 5)
		{
			reasonCode = "ipc.peer.frame.truncated";
			return false;
		}

		int payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(0, 4));
		if (payloadLength <= 0 || payloadLength != frame.Length - 4)
		{
			reasonCode = "ipc.peer.frame.length";
			return false;
		}

		string payload;
		try
		{
			payload = System.Text.Encoding.UTF8.GetString(frame, 4, payloadLength);
		}
		catch
		{
			reasonCode = "ipc.peer.frame.utf8";
			return false;
		}

		string[] parts = payload.Split('|', StringSplitOptions.None);
		if (parts.Length != 4 || !parts[0].Equals("PEER", StringComparison.Ordinal))
		{
			reasonCode = "ipc.peer.frame.fields";
			return false;
		}

		if (!TryDecodeBase64Utf8(parts[1], out executablePath)
			|| !TryDecodeBase64Utf8(parts[2], out signerIdentity)
			|| !TryDecodeBase64Utf8(parts[3], out oneShotSecret))
		{
			reasonCode = "ipc.peer.frame.base64";
			return false;
		}

		reasonCode = string.Empty;
		return true;
	}

	private static bool TryDecodeBase64Utf8(string token, out string value)
	{
		value = string.Empty;
		try
		{
			byte[] bytes = Convert.FromBase64String(token);
			value = System.Text.Encoding.UTF8.GetString(bytes);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static Guid ExtractRequestId(ReadOnlySpan<byte> requestFrame)
	{
		return ActivationProtocol.TryDecodeRequest(requestFrame, DateTimeOffset.UnixEpoch, out ActivationProtocolRequest? request, out _)
			? request!.RequestId
			: Guid.Empty;
	}

	// F-2 (2A): the scheme alone, truncated, for the refusal marker. Never the URL: the
	// value may carry user data (persistent lines stay URL-free), and the scheme is
	// attacker-supplied text that Uri accepts at arbitrary length, so it is bounded here
	// rather than trusted to be short.
	private static string DescribeSchemeForLog(string url)
	{
		string scheme = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Scheme : string.Empty;
		return scheme.Length <= MaxLoggedSchemeChars ? scheme : scheme[..MaxLoggedSchemeChars];
	}

	private static string GetUserSidForName()
	{
		// Raw SID text: hyphens are legal in both mutex and pipe names (spec uses the literal {userSID}).
		return WindowsIdentity.GetCurrent().User?.Value ?? "anon";
	}

	public void Dispose()
	{
		_serverCts?.Cancel();

		try
		{
			_ = (_serverLoop?.Wait(TimeSpan.FromMilliseconds(500)));
		}
		catch (AggregateException)
		{
			// Best-effort shutdown only.
		}

		try
		{
			_ = (_drainLoop?.Wait(TimeSpan.FromMilliseconds(500)));
		}
		catch (AggregateException)
		{
			// Best-effort shutdown only.
		}

		if (_ownsMutex && _mutex is not null)
		{
			try
			{
				_mutex.ReleaseMutex();
			}
			catch (ApplicationException)
			{
				// Mutex was not owned during shutdown; ignore.
			}
		}

		_mutex?.Dispose();
		_serverCts?.Dispose();
	}
}
