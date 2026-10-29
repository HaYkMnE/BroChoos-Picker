using BroChoos.Core.Ingress;

namespace BroChoos.Platform.Windows.SingleInstance;

// PUBLIC for the same reason its sibling WindowsSingleInstanceGuard is public: it is the
// composition seam the App must hold to own the fullscreen-suppressed policy instance, and it
// depends on NOTHING platform-specific (only BroChoos.Core.Ingress.ActivationRequest). Its entire
// surface is five members - Capacity, CreateDefault, Count, TryEnqueue, TryDequeue - so making it
// public opens exactly this type, whereas InternalsVisibleTo("BroChoos") would have opened EVERY
// internal in this assembly to the whole App. Least privilege favours the public type.
//
// CA1711 IS SUPPRESSED RATHER THAN OBEYED, and the reason is the plan's own vocabulary: D-v3.5-150
// and the spec (2.4) name this the "Fullscreen-suppressed Picker queue", every artifact and test
// refers to it by that name, and the internal type already carried it. The rule's concern (a type
// whose name implies a collection base) does not apply - this is a bounded backlog POLICY type,
// not a collection - and renaming it would break the plan/review references for no behavioural
// gain. Suppressing the naming rule on the type is narrower than an assembly-wide NoWarn.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Plan/spec name for the D-v3.5-150 fullscreen-suppressed Picker queue; a bounded policy type, not a collection.")]
public sealed class FullscreenSuppressionQueue
{
	// D-v3.5-150 (spec 2.4): the fullscreen-suppressed Picker queue holds at most THREE URLs; the
	// fourth drops the oldest with a redacted event. The cap is a NAME rather than a literal so the
	// two backlog policies stay distinct and no consumer writes a magic number: this is the named
	// policy instance the W1.5 acceptance gate asks the reviewer to confirm, separate from the
	// single-instance concurrent-click queue's capacity five (D-v3.5-329, QueueCapacity in
	// WindowsSingleInstanceGuard). The spec is explicit that the caps must not be merged.
	public const int Capacity = 3;

	private readonly Queue<ActivationRequest> _queue = new(Capacity);
	private readonly Dictionary<Guid, DateTimeOffset> _seenRequestIds = [];

	// The composition entry point: the picker session creates the policy instance through this
	// factory so the production path owns a real queue rather than constructing a type by hand.
	public static FullscreenSuppressionQueue CreateDefault()
	{
		return new FullscreenSuppressionQueue();
	}

	public int Count
	{
		get
		{
			lock (_queue)
			{
				return _queue.Count;
			}
		}
	}

	public bool TryEnqueue(ActivationRequest request, DateTimeOffset nowUtc, out string reasonCode)
	{
		ArgumentNullException.ThrowIfNull(request);

		lock (_queue)
		{
			PruneExpired(nowUtc);

			if (request.ExpiresAtUtc <= nowUtc)
			{
				reasonCode = "fullscreen.queue.expired";
				return false;
			}

			if (_seenRequestIds.ContainsKey(request.RequestId))
			{
				reasonCode = "fullscreen.queue.duplicate";
				return false;
			}

			reasonCode = string.Empty;
			if (_queue.Count >= Capacity)
			{
				ActivationRequest dropped = _queue.Dequeue();
				_ = _seenRequestIds.Remove(dropped.RequestId);
				reasonCode = "fullscreen.queue.drop-oldest";
			}

			_queue.Enqueue(request);
			_seenRequestIds[request.RequestId] = request.ExpiresAtUtc;
			return true;
		}
	}

	public bool TryDequeue(out ActivationRequest? request)
	{
		lock (_queue)
		{
			if (_queue.Count == 0)
			{
				request = null;
				return false;
			}

			request = _queue.Dequeue();
			return true;
		}
	}

	private void PruneExpired(DateTimeOffset nowUtc)
	{
		if (_queue.Count == 0)
		{
			return;
		}

		while (_queue.Count > 0 && _queue.Peek().ExpiresAtUtc <= nowUtc)
		{
			ActivationRequest expired = _queue.Dequeue();
			_ = _seenRequestIds.Remove(expired.RequestId);
		}

		if (_seenRequestIds.Count == 0)
		{
			return;
		}

		List<Guid>? staleIds = null;
		foreach ((Guid requestId, DateTimeOffset expiry) in _seenRequestIds)
		{
			if (expiry <= nowUtc)
			{
				staleIds ??= [];
				staleIds.Add(requestId);
			}
		}

		if (staleIds is null)
		{
			return;
		}

		foreach (Guid requestId in staleIds)
		{
			_ = _seenRequestIds.Remove(requestId);
		}
	}
}
