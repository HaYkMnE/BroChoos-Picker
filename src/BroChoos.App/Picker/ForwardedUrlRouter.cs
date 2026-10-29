using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Platform.Windows.SingleInstance;
using Microsoft.Extensions.Logging;

namespace BroChoos.App.Picker;

// Process-wide hub between WindowsSingleInstanceGuard (owns the named pipe, knows
// nothing about UI) and the running picker session. Program attaches it to the
// guard right after the container is built, before the primary role can start,
// so no forwarded URL arrives without someone answering it (spike item 12: the
// forward must reach the UI, never be lost silently). This router is the ONLY
// owner of the no-session fallback (0A-3 review finding A1; the guard's own echo
// branch is gone): with no active session it echoes the forward to the console
// and buffers it, so forwards stay observable before a picker session is active.
internal sealed class ForwardedUrlRouter(ILogger<ForwardedUrlRouter> logger)
{
	private readonly ILogger<ForwardedUrlRouter> _logger = logger;
	private readonly Lock _gate = new();
	private IPickerForwardTarget? _session;
	private WindowsSingleInstanceGuard? _attachedGuard;
	private readonly Queue<ActivationRequest> _pendingRequests = new();
	private bool _delivering;

	// Idempotent by contract (0A-3 review finding A3): Program attaches once per
	// process, but a repeat Attach to the SAME guard instance must not subscribe a
	// second handler - that double-delivered every forwarded URL. Attaching a
	// different instance replaces the old subscription so there is one at a time.
	public void Attach(ISingleInstanceGuard guard)
	{
		if (guard is not WindowsSingleInstanceGuard windowsGuard)
		{
			return;
		}

		lock (_gate)
		{
			if (ReferenceEquals(_attachedGuard, windowsGuard))
			{
				return;
			}

			_attachedGuard?.UrlReceived -= OnUrlReceived;
			_attachedGuard = windowsGuard;
			windowsGuard.UrlReceived += OnUrlReceived;
		}
	}

	public void ActivateSession(IPickerForwardTarget target)
	{
		int buffered;
		lock (_gate)
		{
			_session = target;
			buffered = _pendingRequests.Count;
		}

		if (buffered > 0 && _logger.IsEnabled(LogLevel.Information))
		{
			_logger.LogInformation("Delivering {Count} forwarded URLs that arrived before the picker window was up", buffered);
		}

		DeliverPending();
	}

	public void DeactivateSession(IPickerForwardTarget target)
	{
		lock (_gate)
		{
			if (ReferenceEquals(_session, target))
			{
				_session = null;
			}
		}
	}

	// Internal (not private) so tests can drive arrivals deterministically without a pipe.
	internal void OnUrlReceived(object? sender, ActivationRequest request)
	{
		bool hasSession;
		lock (_gate)
		{
			// EVERY arrival goes through the one FIFO queue, even with an active session:
			// a direct hand-off here could overtake buffered URLs that an ActivateSession on
			// another thread is still delivering (observed [b, a] instead of [a, b]).
			_pendingRequests.Enqueue(request);
			hasSession = _session is not null;
		}

		if (!hasSession)
		{
			// No picker session yet (or none any more): keep
			// observability for local dev without emitting the sensitive URL payload.
			Console.WriteLine("[primary received] forwarded activation buffered");
			return;
		}

		_logger.LogInformation("Routing forwarded URL to the active picker session");
		DeliverPending();
	}

	// Single-drainer FIFO delivery. Whoever finds no drain in progress becomes the drainer;
	// everyone else only enqueues and returns, so URLs are delivered exactly once, in arrival
	// order, one at a time. Delivery runs OUTSIDE _gate, so a target that calls back into the
	// router (re-entrant forward, DeactivateSession) can neither deadlock nor reorder: its URL
	// is queued and this loop delivers it after the ones already queued. The session is re-read
	// per item: a deactivated session stops receiving and the rest stays buffered for the next.
	// The "queue empty -> stop draining" decision is made under _gate together with clearing
	// the flag, so an enqueue racing the drainer's exit is never stranded.
	private void DeliverPending()
	{
		lock (_gate)
		{
			if (_delivering)
			{
				return;
			}

			_delivering = true;
		}

		bool released = false;
		try
		{
			while (true)
			{
				IPickerForwardTarget target;
				ActivationRequest? request;
				lock (_gate)
				{
					if (_session is null || !_pendingRequests.TryDequeue(out request))
					{
						_delivering = false;
						released = true;
						return;
					}

					target = _session;
				}

				try
				{
					target.OnForwardedActivation(request);
				}
				catch (Exception ex)
				{
					// One failing delivery must not lose the URLs queued behind it, nor fault the
					// guard's drain loop that raised the event. URL-free, like every log here.
					_logger.LogError(ex, "Forwarded activation delivery to the picker session threw; continuing with the next");
				}
			}
		}
		finally
		{
			// Backstop for an exception escaping outside the per-item catch: never leave the
			// router believing a drain is in progress, which would buffer forever.
			if (!released)
			{
				lock (_gate)
				{
					_delivering = false;
				}
			}
		}
	}
}
