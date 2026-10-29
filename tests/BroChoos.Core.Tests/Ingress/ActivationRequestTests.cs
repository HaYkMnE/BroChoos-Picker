using BroChoos.Core.Ingress;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Ingress;

public sealed class ActivationRequestTests
{
	[Fact]
	public void Constructor_RejectsNonAbsoluteUrl()
	{
		Action act = () => _ = new ActivationRequest(
			"not-a-url",
			42,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(1));

		_ = act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Constructor_RejectsOversizedUrl()
	{
		string oversized = "https://example.com/" + new string('a', ActivationRequest.MaxUrlLength + 1);

		Action act = () => _ = new ActivationRequest(
			oversized,
			42,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Confirmed("shell"),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(1));

		_ = act.Should().Throw<ArgumentException>();
	}

	// THE CAP IS ABOUT THE VALUE THAT GETS STORED, NOT THE VALUE THE CALLER PASSED IN, and
	// these boundaries are where the difference is visible. Uri.AbsoluteUri percent-escapes
	// non-ASCII, so a Cyrillic path character leaves the constructor as six characters and a
	// CJK one as nine. The check used to run on the INPUT string, so a URL whose input was
	// comfortably under 2048 could store an AbsoluteUri over 2048 — and the IPC decoder,
	// which checks the same cap on the DECODED value, then rejected the frame the encoder had
	// just produced. The URL was dropped on its second activation, silently.
	//
	// The accepted counts below are the largest that still fit: 20 + 6n <= 2048 for Cyrillic
	// (n = 338, landing on exactly 2048) and 20 + 9n <= 2048 for CJK (n = 225). One more
	// character crosses the cap in both cases.
	[Theory]
	[InlineData('\u041F', 338)] // Cyrillic: each character becomes %D0%9F (6 characters).
	[InlineData('\u4E2D', 225)] // CJK: each character becomes %E4%B8%AD (9 characters).
	public void Constructor_AcceptsNonAsciiUrl_WhenEscapedValueIsStillWithinCap(char filler, int acceptedCount)
	{
		ActivationRequest request = BuildRequest("https://example.com/" + new string(filler, acceptedCount));

		_ = request.NormalizedUrl.Length.Should().BeLessThanOrEqualTo(ActivationRequest.MaxUrlLength);
	}

	[Theory]
	[InlineData('\u041F', 339)] // One Cyrillic character past the accepted count.
	[InlineData('\u4E2D', 226)] // One CJK character past the accepted count.
	public void Constructor_RejectsNonAsciiUrl_WhenEscapingPushesStoredValueOverCap(char filler, int rejectedCount)
	{
		string input = "https://example.com/" + new string(filler, rejectedCount);

		// The input itself is far below the cap: only the stored, escaped value crosses it.
		// This is the assertion that would have caught the original defect.
		_ = input.Length.Should().BeLessThan(ActivationRequest.MaxUrlLength);

		Action act = () => _ = BuildRequest(input);

		_ = act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Constructor_PreservesImmutableValuesAndStableRequestId()
	{
		Guid id = Guid.NewGuid();
		DateTimeOffset expiry = DateTimeOffset.UtcNow.AddMinutes(2);

		ActivationRequest request = new(
			"https://example.com/path",
			123_456,
			ActivationTriggerContext.ForwardedFromSecondary,
			SourceEvidence.Probable("forwarded from local peer"),
			ActivationModifiers.NoUi,
			id,
			1,
			expiry);

		_ = request.RequestId.Should().Be(id);
		_ = request.NormalizedUrl.Should().Be("https://example.com/path");
		_ = request.ArrivalMonotonicStamp.Should().Be(123_456);
		_ = request.TriggerContext.Should().Be(ActivationTriggerContext.ForwardedFromSecondary);
		_ = request.Modifiers.Should().Be(ActivationModifiers.NoUi);
		_ = request.ProtocolVersion.Should().Be(1);
		_ = request.ExpiresAtUtc.Should().Be(expiry);
	}

	[Theory]
	[InlineData(SourceConfidence.Confirmed)]
	[InlineData(SourceConfidence.Probable)]
	[InlineData(SourceConfidence.Unknown)]
	public void SourceEvidence_SupportsAllConfidenceStates(SourceConfidence confidence)
	{
		SourceEvidence evidence = SourceEvidence.Create(confidence, "local shell");

		_ = evidence.Confidence.Should().Be(confidence);
	}

	[Fact]
	public void SourceEvidence_DisplayProjection_DoesNotExposeFullPath()
	{
		SourceEvidence evidence = SourceEvidence.Create(SourceConfidence.Probable, @"C:\\Users\\testuser\\AppData\\Local\\brochoos\\sender.exe");

		_ = evidence.DisplayProjection.Should().Be("sender.exe");
		_ = evidence.DisplayProjection.Should().NotContain(@"C:\\Users\\testuser");
	}

	[Theory]
	[InlineData("https://example.com/callback?token=abc")]
	[InlineData("query=token&value=abc")]
	[InlineData("auth=bearer abc")]
	[InlineData("session=xyz")]
	public void SourceEvidence_DisplayProjection_RedactsSensitiveNonPathValues(string raw)
	{
		SourceEvidence evidence = SourceEvidence.Create(SourceConfidence.Probable, raw);

		_ = evidence.DisplayProjection.Should().Be("redacted");
	}

	[Fact]
	public void SourceEvidence_DisplayProjection_KeptForSafeHumanLabel()
	{
		SourceEvidence evidence = SourceEvidence.Create(SourceConfidence.Confirmed, "local shell");

		_ = evidence.DisplayProjection.Should().Be("local shell");
	}

	private static ActivationRequest BuildRequest(string url)
	{
		return new ActivationRequest(
			url,
			42,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Confirmed("shell"),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(1));
	}
}
