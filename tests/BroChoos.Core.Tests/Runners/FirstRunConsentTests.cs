using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Runners;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.Core.Tests.Runners;

public sealed class FirstRunConsentTests
{
	private const string Url = "https://example.com/";

	[Fact]
	public async Task RunAsync_FirstRunContract_NoUiPrimaryStillFailsClosedWithoutDetectionOrPicker()
	{
		FakeBrowserDetector detector = new([Candidate("edge-stable")]);
		FakePicker picker = new();
		UrlRunner runner = new(new ListLogger<UrlRunner>(), new FakeSingleInstanceGuard(SingleInstanceRole.Primary), detector, picker);

		int exit = await runner.RunAsync(BuildRequest(noUi: true), CancellationToken.None);

		_ = exit.Should().Be(ExitCodes.GeneralError);
		_ = detector.DetectCount.Should().Be(0);
		_ = picker.ShowCount.Should().Be(0);
	}

	[Fact]
	public async Task RunAsync_FirstRunContract_PrimaryPickerReceivesRequiredConsentWithoutDetection()
	{
		FakeBrowserDetector detector = new([Candidate("edge-stable")]);
		FakePicker picker = new() { ExitCode = ExitCodes.Success };
		UrlRunner runner = new(new ListLogger<UrlRunner>(), new FakeSingleInstanceGuard(SingleInstanceRole.Primary), detector, picker);

		int exit = await runner.RunAsync(BuildRequest(noUi: false), CancellationToken.None);

		_ = exit.Should().Be(ExitCodes.Success);
		_ = detector.DetectCount.Should().Be(0, "W3.4 no-scan contract forbids pre-consent discovery reads");
		_ = picker.ShowCount.Should().Be(1);
		_ = picker.ReceivedRequest.Should().NotBeNull();
		_ = picker.ReceivedRequest!.Snapshot.Should().Be(Snapshots.BrowserSnapshot.Empty,
			"W2.3 first-frame immutable snapshot path must stay intact");
		_ = picker.ReceivedRequest.FirstRunConsent.IsRequired.Should().BeTrue(
			"first-run must enter picker with explicit consent-required contract");
	}

	private static BrowserCandidate Candidate(string browserId)
	{
		return browserId == "edge-stable"
			? new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
			: new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path");
	}

	private static ActivationRequest BuildRequest(bool noUi)
	{
		ActivationModifiers modifiers = noUi ? ActivationModifiers.NoUi : ActivationModifiers.None;
		return new ActivationRequest(
			Url,
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Confirmed("test"),
			modifiers,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));
	}

	private sealed class FakeBrowserDetector(IReadOnlyList<BrowserCandidate> candidates) : IBrowserDetector
	{
		private readonly IReadOnlyList<BrowserCandidate> _candidates = candidates;

		public int DetectCount { get; private set; }

		public IReadOnlyList<BrowserCandidate> Detect()
		{
			DetectCount++;
			return _candidates;
		}
	}

	private sealed class FakePicker : IPicker
	{
		public int ShowCount { get; private set; }

		public PickerRequest? ReceivedRequest { get; private set; }

		public int ExitCode { get; set; } = ExitCodes.Success;

		public Task<int> ShowAsync(PickerRequest request, CancellationToken cancellationToken)
		{
			ShowCount++;
			ReceivedRequest = request;
			return Task.FromResult(ExitCode);
		}
	}

	private sealed class FakeSingleInstanceGuard(SingleInstanceRole role) : ISingleInstanceGuard
	{
		private readonly SingleInstanceRole _role = role;

		public Task<SingleInstanceRole> AcquireOrForwardAsync(ActivationRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(_role);
		}
	}

	private sealed class ListLogger<T> : ILogger<T>
	{
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
		}
	}
}
