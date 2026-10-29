using BroChoos.Core.Ingress;

namespace BroChoos.Platform.Windows.SingleInstance;

internal sealed class BoundedActivationQueue
{
	private readonly int _capacity;
	private readonly Queue<ActivationRequest> _queue;
	private readonly Dictionary<Guid, DateTimeOffset> _seenRequestIds;

	public BoundedActivationQueue(int capacity)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

		_capacity = capacity;
		_queue = new Queue<ActivationRequest>(capacity);
		_seenRequestIds = [];
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
				reasonCode = "ipc.request.expired";
				return false;
			}

			if (_seenRequestIds.ContainsKey(request.RequestId))
			{
				reasonCode = "ipc.request.duplicate";
				return false;
			}

			if (_queue.Count >= _capacity)
			{
				reasonCode = "ipc.queue.full";
				return false;
			}

			_queue.Enqueue(request);
			_seenRequestIds[request.RequestId] = request.ExpiresAtUtc;
			reasonCode = string.Empty;
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
		if (_seenRequestIds.Count == 0)
		{
			return;
		}

		List<Guid>? expired = null;
		foreach ((Guid requestId, DateTimeOffset expiry) in _seenRequestIds)
		{
			if (expiry <= nowUtc)
			{
				expired ??= [];
				expired.Add(requestId);
			}
		}

		if (expired is null)
		{
			return;
		}

		foreach (Guid requestId in expired)
		{
			_ = _seenRequestIds.Remove(requestId);
		}
	}
}
