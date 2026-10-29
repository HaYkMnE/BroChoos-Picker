using System.Globalization;
using BroChoos.App.Audit;
using BroChoos.App.Localization;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

// Round-4 audit follow-ups. Joins the console collection: R4-3 redirects Console.Out (where
// PickerLog.Marker writes), and R4-1 switches the static LocalizationService.CurrentCulture.
[Collection(ConsoleCaptureCollection.Name)]
public sealed partial class PickerAuditRound4Tests
{
	// R4-1: the "another link is waiting" note reaches the status line the window ALREADY renders
	// (StatusMessage), after the failure text, in the current language.
	[Theory]
	[InlineData("en", "Another link is waiting.")]
	[InlineData("ru", "Ожидает ещё одна ссылка.")]
	[InlineData("hy", "Սպասում է ևս մեկ հղում։")]
	public async Task HeldForward_AfterFailedLaunch_IsAppendedToTheRenderedStatusLine(string culture, string note)
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo(culture);
			TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
			PickerSession session = NewSession(new PendingLaunchHelperClient(pending));

			session.ClickCardForTests(1);
			Forward(session, "https://second.example/b");
			pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Failed, "edge-stable", "launch.start-failed", "launch", "launch.error.failed"));
			_ = await WaitUntilAsync(() => session.StatusMessageForTests.EndsWith(note, StringComparison.Ordinal));

			string rendered = session.StatusMessageForTests;
			_ = rendered.Should().Contain("launch.start-failed", "the failure for link A stays on the line");
			_ = rendered.Should().EndWith(note, "the waiting note is the second sentence of the rendered status");
			_ = session.IsErrorStatusVisibleForTests.Should().BeTrue("the line the window binds for errors is the one shown");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	// R4-1: placeholder parity for the two strings this round touches.
	[Fact]
	public void TouchedStrings_KeepPlaceholderParity_AcrossLanguages()
	{
		foreach (string key in new[] { "Status.ForwardWaiting", "Card.AccessibleName" })
		{
			string en = LocalizationService.PackFor("en")![key];
			foreach (string culture in new[] { "ru", "hy" })
			{
				string value = LocalizationService.PackFor(culture)![key];
				_ = Placeholders(value).Should().Equal(Placeholders(en), $"{culture}:{key}");
			}
		}

		_ = LocalizationService.PackFor("ru")!["Status.ForwardWaiting"].Should().NotBe(LocalizationService.PackFor("en")!["Status.ForwardWaiting"]);
		_ = LocalizationService.PackFor("hy")!["Status.ForwardWaiting"].Should().NotBe(LocalizationService.PackFor("en")!["Status.ForwardWaiting"]);
	}

	// R4-2: F succeeding after a failed launch must not drop the forward held during that launch.
	[Fact]
	public async Task FallbackSuccess_WithAHeldForward_AppliesItAndKeepsThePickerOpen()
	{
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession session = NewSession(new PendingLaunchHelperClient(pending));

		session.ClickCardForTests(1);
		Forward(session, "https://second.example/b");
		pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Failed, "edge-stable", "launch.start-failed", "launch", "launch.error.failed"));
		_ = await WaitUntilAsync(() => session.IsErrorStatusVisibleForTests);

		session.PressSystemFallbackForTests();

		_ = session.CompleteCountForTests.Should().Be(0, "completing would lose the held forward");
		_ = session.EffectiveUrlForTests.Should().Be("https://second.example/b");
		_ = session.FullscreenQueueCountForTests.Should().Be(0);
	}

	[Fact]
	public void FallbackSuccess_WithNothingHeld_StillCompletes()
	{
		PickerSession session = NewSession(new PendingLaunchHelperClient(new TaskCompletionSource<LaunchHelperOutcome>()));

		session.PressSystemFallbackForTests();

		_ = session.CompleteCountForTests.Should().Be(1);
	}

	// R4-3: the marker (console) path of the fail-open catches carries the exception TYPE only.
	[Fact]
	public async Task SwallowedFailureMarker_HasTheTypeName_AndNoMessageOrUrl()
	{
		TextWriter original = Console.Out;
		using StringWriter capture = new();
		try
		{
			Console.SetOut(capture);
			PickerSession session = NewSession(new PendingLaunchHelperClient(new TaskCompletionSource<LaunchHelperOutcome>()));
			session.ConfigureClipboardHooksForTests(() => throw new InvalidOperationException("secret-message https://example.com/secret-path"), null);

			await session.CaptureClipboardAtOpenForTestsAsync();
		}
		finally
		{
			Console.SetOut(original);
		}

		string output = capture.ToString();
		_ = output.Should().Contain("CLIPBOARD_READ_FAILED type=InvalidOperationException");
		_ = output.Should().NotContain("secret-message");
		_ = output.Should().NotContain("secret-path");
	}

	// R4-4: a forward dequeued by a drain that then throws is counted as lost, not just the ones
	// still queued.
	[Fact]
	public async Task DrainThrowsAfterDequeue_CountsTheDequeuedForwardAsLost()
	{
		TextWriter original = Console.Out;
		using StringWriter capture = new();
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession session = NewSession(new PendingLaunchHelperClient(pending));
		session.ClickCardForTests(1);
		Forward(session, "https://second.example/b");
		session.DrainHeldForwardsForTests = () => throw new InvalidOperationException("boom");
		try
		{
			Console.SetOut(capture);
			pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Success, "edge-stable", null, null, null));
			_ = await WaitUntilAsync(() => session.CompleteCountForTests == 1);
		}
		finally
		{
			Console.SetOut(original);
		}

		_ = session.CompleteCountForTests.Should().Be(1);
		_ = capture.ToString().Should().Contain("FWD_URL_LOST_ON_DRAIN_FAILURE count=1");
	}

	[System.Text.RegularExpressions.GeneratedRegex(@"\{\d+\}")]
	private static partial System.Text.RegularExpressions.Regex PlaceholderPattern();

	private static List<string> Placeholders(string value)
	{
		return [.. PlaceholderPattern().Matches(value).Select(m => m.Value).Order(StringComparer.Ordinal)];
	}

	private static void Forward(PickerSession session, string url)
	{
		session.PostDrainForTests = action => action();
		session.OnForwardedActivation(new ActivationRequest(
			url,
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5)));
	}

	private static async Task<bool> WaitUntilAsync(Func<bool> condition)
	{
		System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
		while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(10))
		{
			await Task.Delay(10);
		}

		return condition();
	}

	private static PickerSession NewSession(ILaunchHelperClient launch)
	{
		BrowserSnapshot snapshot = new([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
		], 1, DateTimeOffset.UtcNow);

		ActivationRequest activation = new(
			"https://first.example/a",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		return new PickerSession(
			new PickerRequest(activation, snapshot, FirstRunConsentContract.None),
			launch,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new SuccessFallback(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance,
			null,
			new NullAuditWriter());
	}

	private sealed class PendingLaunchHelperClient(TaskCompletionSource<LaunchHelperOutcome> pending) : ILaunchHelperClient
	{
		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			return pending.Task;
		}
	}

	private sealed class SuccessFallback : ISystemFallbackLauncher
	{
		public LaunchResult Launch(string url)
		{
			return new LaunchResult(LaunchStatus.Success, "system", null, null, null);
		}
	}

	private sealed class NullAuditWriter : ITrackingCleanupAuditWriter
	{
		public void Write(TrackingCleanupAuditEvent cleanupEvent)
		{
			_ = cleanupEvent;
		}
	}

	private sealed class FakeStorageHelperClient : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, null, null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.publish-not-used-by-this-test"));
		}
	}
}
