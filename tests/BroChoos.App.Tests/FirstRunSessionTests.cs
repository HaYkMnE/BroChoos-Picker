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

// The first-run screen at session level: the prompt is up when no browsers are known, the
// gate blocks card launches until the user acts, a successful search satisfies the gate so
// the discovered cards actually launch, and the outcomes that leave nothing to choose keep
// the prompt (and the gate) up. Session-level, no window: the window half is in
// PickerFirstRunWindowTests.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class FirstRunSessionTests
{
	[Fact]
	public void EmptyFirstRun_ShowsThePrompt_AndTheGateIsPending()
	{
		PickerSession session = NewSession(new FakeLaunch(), new DiscoveringStorage(withResults: true));

		session.InitializeForTests();

		_ = session.IsFirstRunPromptVisibleForTests.Should().BeTrue();
		_ = session.IsFirstRunConsentPendingForTests.Should().BeTrue();
		_ = session.CardCountForTests.Should().Be(0);
	}

	[Fact]
	public async Task FindBrowsers_WithResults_ClearsThePrompt_AndTheDiscoveredCardLaunches()
	{
		FakeLaunch launch = new();
		PickerSession session = NewSession(launch, new DiscoveringStorage(withResults: true));

		session.FirstRunActionForTests(FirstRunAction.FindBrowsers);
		_ = await WaitUntilAsync(() => !session.IsFirstRunConsentPendingForTests);

		_ = session.CardCountForTests.Should().Be(1);
		_ = session.IsFirstRunPromptVisibleForTests.Should().BeFalse();
		_ = session.IsFirstRunConsentPendingForTests.Should().BeFalse(
			"a successful search is the user's consent; the immutable request must not keep blocking the new cards");

		session.ClickCardForTests(1);
		_ = await WaitUntilAsync(() => launch.Calls > 0);

		_ = launch.Calls.Should().Be(1, "the discovered card must be clickable after Find browsers succeeds");
	}

	[Fact]
	public async Task FindBrowsers_WithNoResults_KeepsThePrompt_AndSaysWhatToDoNext()
	{
		PickerSession session = NewSession(new FakeLaunch(), new DiscoveringStorage(withResults: false));

		session.FirstRunActionForTests(FirstRunAction.FindBrowsers);
		_ = await WaitUntilAsync(() => session.StatusMessageForTests.Length > 0);

		_ = session.IsFirstRunPromptVisibleForTests.Should().BeTrue("an empty result leaves nothing to choose, so the actions must stay");
		_ = session.IsFirstRunConsentPendingForTests.Should().BeTrue();
		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("FirstRun.NoneFound"));
	}

	[Fact]
	public async Task FindBrowsers_WhenTheSearchFails_KeepsThePrompt_SoTheUserCanRetry()
	{
		PickerSession session = NewSession(new FakeLaunch(), new FailingStorage());

		session.FirstRunActionForTests(FirstRunAction.FindBrowsers);
		_ = await WaitUntilAsync(() => session.IsErrorStatusVisibleForTests);

		_ = session.IsFirstRunPromptVisibleForTests.Should().BeTrue();
		_ = session.IsFirstRunConsentPendingForTests.Should().BeTrue();
	}

	[Fact]
	public async Task Skip_CollapsesThePanel_ButKeepsTheGatePending()
	{
		PickerSession session = NewSession(new FakeLaunch(), new DiscoveringStorage(withResults: true));

		session.FirstRunActionForTests(FirstRunAction.Skip);
		_ = await WaitUntilAsync(() => session.StatusMessageForTests.Length > 0);

		_ = session.IsFirstRunPromptVisibleForTests.Should().BeFalse();
		_ = session.IsFirstRunConsentPendingForTests.Should().BeTrue();
		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.FirstRunSkipped"));
	}

	[Fact]
	public void AddManually_IsHidden_AndRefused_WhenNoFilePickerIsComposed()
	{
		DiscoveringStorage storage = new(withResults: true);
		PickerSession session = NewSession(new FakeLaunch(), storage);

		session.FirstRunActionForTests(FirstRunAction.AddManually);

		_ = session.IsAddManuallyAvailableForTests.Should().BeFalse();
		_ = session.StatusMessageForTests.Should().BeEmpty("a refused action must not paint the 'file picker is not available' failure");
		_ = session.IsFirstRunPromptVisibleForTests.Should().BeTrue();
	}

	// The texts must not send the user to an action that is not on screen.
	[Theory]
	[InlineData("en", "add ")]
	[InlineData("ru", "вручную")]
	[InlineData("hy", "ձեռքով")]
	public void PromptAndNoneFoundTexts_DoNotAdviseAddingABrowserManually(string culture, string addWord)
	{
		IReadOnlyDictionary<string, string> pack = LocalizationService.PackFor(culture)!;

		_ = pack["FirstRun.Prompt"].Should().NotContainEquivalentOf(addWord);
		_ = pack["FirstRun.NoneFound"].Should().NotContainEquivalentOf(addWord);
	}

	[Fact]
	public async Task SearchStart_SetsTheBusyText_SoTheLiveRegionHasSomethingToAnnounce()
	{
		TaskCompletionSource<StorageDiscoveryResponse> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession session = NewSession(new FakeLaunch(), new PendingStorage(pending));

		session.FirstRunActionForTests(FirstRunAction.FindBrowsers);
		_ = await WaitUntilAsync(() => session.FirstRunBusyTextForTests.Length > 0);
		_ = session.FirstRunBusyTextForTests.Should().Be(LocalizationService.GetString("FirstRun.Searching"));

		pending.SetResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-timeout"));
		_ = await WaitUntilAsync(() => session.FirstRunBusyTextForTests.Length == 0);
		_ = session.FirstRunBusyTextForTests.Should().BeEmpty();
	}

	private sealed class PendingStorage(TaskCompletionSource<StorageDiscoveryResponse> pending) : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			_ = command;
			_ = cancellationToken;
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return pending.Task;
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.publish-not-used-by-this-test"));
		}
	}

	private static async Task<bool> WaitUntilAsync(Func<bool> condition)
	{
		for (int attempt = 0; attempt < 200 && !condition(); attempt++)
		{
			await Task.Delay(10);
		}

		return condition();
	}

	private static PickerSession NewSession(ILaunchHelperClient launch, IStorageHelperClient storage)
	{
		ActivationRequest activation = new(
			"https://example.com/",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		return new PickerSession(
			new PickerRequest(activation, BrowserSnapshot.Empty, FirstRunConsentContract.Required),
			launch,
			new FirstRunConsentActionCoordinator(storage),
			new FakeFallback(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance,
			trackingCleanupOptions: null,
			trackingCleanupAuditWriter: new InertAuditWriter(),
			shortLinkExpansionOptions: null,
			networkHelperClient: null,
			activationHistoryStore: null);
	}

	private sealed class FakeLaunch : ILaunchHelperClient
	{
		public int Calls { get; private set; }

		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			Calls++;
			return Task.FromResult(new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null));
		}
	}

	private sealed class DiscoveringStorage(bool withResults) : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			_ = command;
			_ = cancellationToken;
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			BrowserCandidate[] candidates = withResults
				? [new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")]
				: [];
			return Task.FromResult(new StorageDiscoveryResponse(
				HelperClientDispatch.Dispatched,
				new BrowserSnapshot(candidates, 2, DateTimeOffset.UtcNow),
				null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.publish-not-used-by-this-test"));
		}
	}

	private sealed class FailingStorage : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			_ = command;
			_ = cancellationToken;
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-timeout"));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.publish-not-used-by-this-test"));
		}
	}

	private sealed class FakeFallback : ISystemFallbackLauncher
	{
		public LaunchResult Launch(string url)
		{
			_ = url;
			return new LaunchResult(LaunchStatus.Success, "system", null, null, null);
		}
	}

	private sealed class InertAuditWriter : ITrackingCleanupAuditWriter
	{
		public void Write(TrackingCleanupAuditEvent cleanupEvent)
		{
			_ = cleanupEvent;
		}
	}
}
