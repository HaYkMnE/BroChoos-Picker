using BroChoos.Core.Ingress;
using BroChoos.Platform.Windows.SingleInstance;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.SingleInstance;

public sealed class BoundedActivationQueueTests
{
	[Fact]
	public void TryEnqueue_RejectsExpiredRequest()
	{
		BoundedActivationQueue queue = new(5);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		ActivationRequest request = BuildRequest("https://example.com/expired", Guid.NewGuid(), now.AddSeconds(-1));

		bool ok = queue.TryEnqueue(request, now, out string reason);

		_ = ok.Should().BeFalse();
		_ = reason.Should().Be("ipc.request.expired");
		_ = queue.Count.Should().Be(0);
	}

	[Fact]
	public void TryEnqueue_RejectsDuplicateRequestIdUntilExpiry()
	{
		BoundedActivationQueue queue = new(5);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		Guid requestId = Guid.NewGuid();

		bool first = queue.TryEnqueue(BuildRequest("https://example.com/1", requestId, now.AddMinutes(1)), now, out string firstReason);
		bool duplicate = queue.TryEnqueue(BuildRequest("https://example.com/2", requestId, now.AddMinutes(1)), now, out string duplicateReason);

		_ = first.Should().BeTrue();
		_ = firstReason.Should().BeEmpty();
		_ = duplicate.Should().BeFalse();
		_ = duplicateReason.Should().Be("ipc.request.duplicate");

		bool acceptedAfterExpiry = queue.TryEnqueue(BuildRequest("https://example.com/3", requestId, now.AddMinutes(2)), now.AddMinutes(1).AddSeconds(1), out string acceptedReason);
		_ = acceptedAfterExpiry.Should().BeTrue();
		_ = acceptedReason.Should().BeEmpty();
	}

	[Fact]
	public void TryEnqueue_EnforcesCapacityFive()
	{
		BoundedActivationQueue queue = new(5);
		DateTimeOffset now = DateTimeOffset.UtcNow;

		for (int i = 0; i < 5; i++)
		{
			bool ok = queue.TryEnqueue(BuildRequest($"https://example.com/{i}", Guid.NewGuid(), now.AddMinutes(1)), now, out string reason);
			_ = ok.Should().BeTrue();
			_ = reason.Should().BeEmpty();
		}

		bool overflow = queue.TryEnqueue(BuildRequest("https://example.com/overflow", Guid.NewGuid(), now.AddMinutes(1)), now, out string overflowReason);

		_ = overflow.Should().BeFalse();
		_ = overflowReason.Should().Be("ipc.queue.full");
		_ = queue.Count.Should().Be(5);
	}

	[Fact]
	public void TryDequeue_PreservesFifoOrder()
	{
		BoundedActivationQueue queue = new(5);
		DateTimeOffset now = DateTimeOffset.UtcNow;

		ActivationRequest r1 = BuildRequest("https://example.com/1", Guid.NewGuid(), now.AddMinutes(1));
		ActivationRequest r2 = BuildRequest("https://example.com/2", Guid.NewGuid(), now.AddMinutes(1));
		ActivationRequest r3 = BuildRequest("https://example.com/3", Guid.NewGuid(), now.AddMinutes(1));

		_ = queue.TryEnqueue(r1, now, out _).Should().BeTrue();
		_ = queue.TryEnqueue(r2, now, out _).Should().BeTrue();
		_ = queue.TryEnqueue(r3, now, out _).Should().BeTrue();

		_ = queue.TryDequeue(out ActivationRequest? first).Should().BeTrue();
		_ = queue.TryDequeue(out ActivationRequest? second).Should().BeTrue();
		_ = queue.TryDequeue(out ActivationRequest? third).Should().BeTrue();

		_ = first!.RequestId.Should().Be(r1.RequestId);
		_ = second!.RequestId.Should().Be(r2.RequestId);
		_ = third!.RequestId.Should().Be(r3.RequestId);
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
}
