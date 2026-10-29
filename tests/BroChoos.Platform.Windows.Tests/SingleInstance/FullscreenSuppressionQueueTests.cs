using BroChoos.Core.Ingress;
using BroChoos.Platform.Windows.SingleInstance;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.SingleInstance;

public sealed class FullscreenSuppressionQueueTests
{
	[Fact]
	public void Enqueue_FourthRequest_DropsOldest_AndReturnsOnlyRedactedReasonCode()
	{
		FullscreenSuppressionQueue queue = new();
		DateTimeOffset now = DateTimeOffset.UtcNow;

		ActivationRequest r1 = BuildRequest("https://example.com/1", Guid.NewGuid(), now.AddMinutes(1));
		ActivationRequest r2 = BuildRequest("https://example.com/2", Guid.NewGuid(), now.AddMinutes(1));
		ActivationRequest r3 = BuildRequest("https://example.com/3", Guid.NewGuid(), now.AddMinutes(1));
		ActivationRequest r4 = BuildRequest("https://example.com/4", Guid.NewGuid(), now.AddMinutes(1));

		_ = queue.TryEnqueue(r1, now, out string reason1).Should().BeTrue();
		_ = queue.TryEnqueue(r2, now, out string reason2).Should().BeTrue();
		_ = queue.TryEnqueue(r3, now, out string reason3).Should().BeTrue();
		_ = queue.TryEnqueue(r4, now, out string reason4).Should().BeTrue();

		_ = reason1.Should().BeEmpty();
		_ = reason2.Should().BeEmpty();
		_ = reason3.Should().BeEmpty();
		_ = reason4.Should().Be("fullscreen.queue.drop-oldest");

		_ = queue.TryDequeue(out ActivationRequest? first).Should().BeTrue();
		_ = queue.TryDequeue(out ActivationRequest? second).Should().BeTrue();
		_ = queue.TryDequeue(out ActivationRequest? third).Should().BeTrue();

		_ = first!.RequestId.Should().Be(r2.RequestId);
		_ = second!.RequestId.Should().Be(r3.RequestId);
		_ = third!.RequestId.Should().Be(r4.RequestId);
	}

	[Fact]
	public void Enqueue_ExpiredRequest_IsRejectedWithoutDropAndNoPayloadLeakInReason()
	{
		FullscreenSuppressionQueue queue = new();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		ActivationRequest expired = BuildRequest("https://example.com/expired?token=secret", Guid.NewGuid(), now.AddSeconds(-1));

		bool ok = queue.TryEnqueue(expired, now, out string reason);

		_ = ok.Should().BeFalse();
		_ = reason.Should().Be("fullscreen.queue.expired");
		_ = reason.Should().NotContain("https://", "reason must be redacted code only");
		_ = queue.Count.Should().Be(0);
	}

	[Fact]
	public void Enqueue_TransientExpiry_PrunesBeforeOverflowDecision()
	{
		FullscreenSuppressionQueue queue = new();
		DateTimeOffset now = DateTimeOffset.UtcNow;

		_ = queue.TryEnqueue(BuildRequest("https://example.com/a", Guid.NewGuid(), now.AddSeconds(1)), now, out _).Should().BeTrue();
		_ = queue.TryEnqueue(BuildRequest("https://example.com/b", Guid.NewGuid(), now.AddSeconds(1)), now, out _).Should().BeTrue();
		_ = queue.TryEnqueue(BuildRequest("https://example.com/c", Guid.NewGuid(), now.AddSeconds(1)), now, out _).Should().BeTrue();

		bool afterExpiry = queue.TryEnqueue(
			BuildRequest("https://example.com/d", Guid.NewGuid(), now.AddSeconds(10)),
			now.AddSeconds(2),
			out string reason);

		_ = afterExpiry.Should().BeTrue();
		_ = reason.Should().BeEmpty();
		_ = queue.Count.Should().Be(1);
	}

	[Fact]
	public void PolicyIsolation_ConcurrentQueueStaysCapacityFive_AndFullscreenQueueStaysCapacityThree()
	{
		BoundedActivationQueue concurrentQueue = new(5);
		FullscreenSuppressionQueue fullscreenQueue = FullscreenSuppressionQueue.CreateDefault();
		DateTimeOffset now = DateTimeOffset.UtcNow;

		// The two backlog policies are distinct NAMED instances, not one shared cap: the
		// concurrent-click queue is five (D-v3.5-329), the fullscreen-suppressed queue is three
		// (D-v3.5-150), and the spec forbids merging them. Pinning the names here is what the
		// W1.5 acceptance gate asks a reviewer to confirm.
		_ = FullscreenSuppressionQueue.Capacity.Should().Be(3);

		for (int i = 0; i < 5; i++)
		{
			_ = concurrentQueue.TryEnqueue(BuildRequest($"https://example.com/concurrent/{i}", Guid.NewGuid(), now.AddMinutes(1)), now, out _).Should().BeTrue();
		}

		_ = concurrentQueue.TryEnqueue(BuildRequest("https://example.com/concurrent/overflow", Guid.NewGuid(), now.AddMinutes(1)), now, out string concurrentReason).Should().BeFalse();
		_ = concurrentReason.Should().Be("ipc.queue.full");

		_ = fullscreenQueue.TryEnqueue(BuildRequest("https://example.com/fullscreen/1", Guid.NewGuid(), now.AddMinutes(1)), now, out _).Should().BeTrue();
		_ = fullscreenQueue.TryEnqueue(BuildRequest("https://example.com/fullscreen/2", Guid.NewGuid(), now.AddMinutes(1)), now, out _).Should().BeTrue();
		_ = fullscreenQueue.TryEnqueue(BuildRequest("https://example.com/fullscreen/3", Guid.NewGuid(), now.AddMinutes(1)), now, out _).Should().BeTrue();
		_ = fullscreenQueue.TryEnqueue(BuildRequest("https://example.com/fullscreen/4", Guid.NewGuid(), now.AddMinutes(1)), now, out string fullscreenReason).Should().BeTrue();
		_ = fullscreenReason.Should().Be("fullscreen.queue.drop-oldest");
	}

	[Fact]
	public void NoPersistenceAcrossRestart_NewQueueStartsEmptyAndDoesNotRememberOldRequestIds()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		Guid requestId = Guid.NewGuid();

		FullscreenSuppressionQueue firstProcessQueue = new();
		_ = firstProcessQueue.TryEnqueue(BuildRequest("https://example.com/p1", requestId, now.AddMinutes(1)), now, out _).Should().BeTrue();

		FullscreenSuppressionQueue restartedProcessQueue = new();
		_ = restartedProcessQueue.Count.Should().Be(0);
		_ = restartedProcessQueue.TryEnqueue(BuildRequest("https://example.com/p2", requestId, now.AddMinutes(1)), now, out string reason).Should().BeTrue();
		_ = reason.Should().BeEmpty();
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
