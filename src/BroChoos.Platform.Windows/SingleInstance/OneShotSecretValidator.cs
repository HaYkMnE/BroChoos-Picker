using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace BroChoos.Platform.Windows.SingleInstance;

// THE ONE-SHOT SECRET IS AN ANTI-REPLAY MEASURE. IT IS NOT AN AUTHENTICATION ANCHOR.
//
// HISTORY, because this distinction is the whole reason for the file's current shape.
// A previous fix (F-2a) added a PROCESS-WIDE "issued" registry: Issue() recorded the
// token and Consume() rejected any token it had not recorded (ipc.secret.unissued).
// It was independently audited as BROKEN, and the audit's reasoning is preserved here
// so the defect is not reintroduced:
//
//   * ISSUANCE AND CONSUMPTION HAPPEN IN DIFFERENT PROCESSES. Issue() runs in the
//     SECONDARY process while it builds its peer-identity frame (the pipe CLIENT:
//     WindowsSingleInstanceGuard.SendPeerIdentityAsync). Consume() runs in the PRIMARY
//     process while it validates that frame (the pipe SERVER:
//     WindowsSingleInstanceGuard.TryValidatePeerAsync). The primary's registry can
//     therefore never contain a token the secondary issued, so every real forward
//     failed with ipc.secret.unissued and forwarding was dead in production.
//   * The tests only passed because they compose the primary and the secondary as two
//     objects in ONE process, where the static registry is shared. A cross-process
//     issued-set cannot be built without a protocol change (the primary would have to
//     learn the token before the secondary spends it), which is out of this scope.
//
// So the semantics are deliberately REPLAY-ONLY, with exactly three outcomes:
//   blank       -> MissingOneShotSecret  (ipc.secret.missing)
//   first seen  -> Passed
//   seen before -> ReplayedOneShotSecret (ipc.secret.replayed-or-unknown)
// The replay set stays process-wide because only the primary consumes: one process,
// many connections, and the set is the memory of tokens already spent.
//
// THE REAL AUTHENTICATION ANCHOR IS THE OS-LEVEL PEER PROCESS-IMAGE CHECK in
// PeerProcessImageVerifier (this assembly): the pipe server resolves the CONNECTING
// process's image path through GetNamedPipeClientProcessId + QueryFullProcessImageName
// and requires it to be this same executable. The frame-declared path and signer stay
// as defense-in-depth only. The owner ruling declares signerIdentityModel = x509-chain
// (docs/decisions/owner-rulings/dev-runner-ipc-trust-anchor.json).
// THE REPLAY SET IS BOUNDED (N3), AND THE BOUND IS THE LEAST-INVASIVE ONE THAT STOPS THE
// GROWTH. A ConcurrentDictionary that only ever TryAdds retains one entry per token ever
// consumed, for the life of the primary process, so a peer that keeps connecting grows it
// without limit (memory-pressure DoS). The header above explains why the set is process-wide;
// it does not explain why it is unbounded, and it never needed to be.
//
// WHAT WAS CHOSEN. A bounded FIFO: when the set is full the OLDEST token leaves, exactly as
// BoundedActivationQueue / FullscreenSuppressionQueue evict in this same assembly. Cap 4096.
// A TTL was considered and rejected as the PRIMARY bound: it needs a clock (a TimeProvider or
// an ambient UtcNow) threaded into a type that currently has none, and a pure TTL still allows
// an unbounded burst inside the window. A FIFO cap bounds the worst case with no new
// dependency and no new constructor parameter, which is what "least-invasive" has to mean for
// a type whose public surface is already a contract (Issue/Consume, both instance members).
//
// THE CORRECTNESS ARGUMENT, stated plainly, because this bound is a real trade:
//   * Every token seen within the last 4096 consumptions is still refused on replay, so the
//     guarantee is UNCHANGED for any token that could plausibly still be in flight. An IPC
//     connection is seconds long; 4096 consumptions is far beyond the retry horizon of a
//     single frame, so a REPLAY of an in-flight token never falls outside the window.
//   * 4096 base64 tokens is well under a megabyte resident, so the DoS channel is closed.
//   * The bound is per-PROCESS and the primary is the only consumer, so the number counts
//     activations, not connections across a fleet.
//
// RESIDUAL RISK, NOT HIDDEN: a token evicted after 4096 later consumptions becomes replayable
// again. A peer able to force that eviction is a same-user, same-image peer that has ALREADY
// passed the OS-level process-image check — the actual authentication anchor per the header
// above — and can act directly. The one-shot secret is anti-replay, not an authentication
// anchor, so the residual is the marginal ability to replay one old token in exchange for
// removing an unbounded memory channel. That is the intended direction of the trade.
public sealed class OneShotSecretValidator
{
	// Named so the policy is greppable and the test pins the exact bound rather than a literal.
	internal const int MaxConsumedTokens = 4096;

	private static readonly ConcurrentDictionary<string, byte> Consumed = new(StringComparer.Ordinal);
	private static readonly ConcurrentQueue<string> ConsumedOrder = new();

	internal static int ConsumedCount => Consumed.Count;

	public string Issue()
	{
		Span<byte> bytes = stackalloc byte[32];
		RandomNumberGenerator.Fill(bytes);
		return Convert.ToBase64String(bytes);
	}

	// CA1822 is suppressed rather than obeyed, and the reason is the type's contract: the
	// members must stay INSTANCE members because every caller (the guard, the authenticator,
	// DI registration, the tests) reaches them through an instance, and the replay set is
	// process-wide by design rather than per-instance. Converting them to static would be a
	// breaking public API change for no behavioral gain.
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance methods are the public contract; the replay set is process-wide by design.")]
	public IpcPeerAuthenticationResult Consume(string? token)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return IpcPeerAuthenticationResult.Fail(IpcPeerAuthenticationErrorCode.MissingOneShotSecret, "ipc.secret.missing");
		}

		if (!Consumed.TryAdd(token, 0))
		{
			return IpcPeerAuthenticationResult.Fail(IpcPeerAuthenticationErrorCode.ReplayedOneShotSecret, "ipc.secret.replayed-or-unknown");
		}

		// Only a token that was actually ADDED is enqueued, so the order queue cannot outgrow
		// the set when the same token arrives twice. Eviction is a loop because a concurrent
		// Trim may already have removed the token this call would dequeue; the queue-empty
		// guard ends the loop on a transient insert/queue race rather than spinning.
		ConsumedOrder.Enqueue(token);
		while (Consumed.Count > MaxConsumedTokens && ConsumedOrder.TryDequeue(out string? oldest))
		{
			_ = Consumed.TryRemove(oldest, out _);
		}

		return IpcPeerAuthenticationResult.Passed;
	}
}
