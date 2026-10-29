using System.Diagnostics;
using BroChoos.App.Audit;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Routing;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

// Session state-handling contracts:
//   1. a window deactivation DURING an in-flight launch (the new browser taking focus) is not a
//      user cancel: no cancel-to-clipboard, no terminal transition; the launch outcome decides;
//   2. F (system fallback) honours the same in-flight guard as a card launch;
//   3. a forwarded URL replaces every per-link launch input (short-link resolution, cleanup
//      state, the armed use-original one-shot), so the next launch opens the NEW link.
public sealed class PickerSessionStateTests
{
	private static readonly TimeSpan EffectBudget = TimeSpan.FromSeconds(10);

	[Fact]
	public async Task DeactivationDuringLaunch_ThenSuccess_CompletesWithoutClipboardCopy()
	{
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int writes = 0;
		PickerSession session = NewSession("https://example.com/", new PendingLaunchHelperClient(pending));
		session.ConfigureClipboardHooksForTests(null, _ => writes++);

		session.ClickCardForTests(1);
		_ = session.IsLaunchInFlightForTests.Should().BeTrue();

		_ = await session.SimulateExternalDismissalForTestsAsync();

		_ = writes.Should().Be(0, "the launched browser taking focus is not a user cancel");
		_ = session.CompleteCountForTests.Should().Be(0, "the deactivation must not end the session while the launch is in flight");

		pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Success, "edge-stable", null, null, null));

		_ = (await WaitUntilAsync(() => session.CompleteCountForTests == 1, EffectBudget)).Should().BeTrue("a successful launch ends the session");
		_ = writes.Should().Be(0, "a successful launch must not copy the link");
		_ = session.LastClipboardWriteForTests.Should().BeNull();
	}

	[Fact]
	public async Task DeactivationDuringLaunch_ThenFailure_ShowsErrorAndKeepsWindowOpen()
	{
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int writes = 0;
		PickerSession session = NewSession("https://example.com/", new PendingLaunchHelperClient(pending));
		session.ConfigureClipboardHooksForTests(null, _ => writes++);

		session.ClickCardForTests(1);
		_ = await session.SimulateExternalDismissalForTestsAsync();

		pending.SetResult(new LaunchHelperOutcome(LaunchStatus.Failed, "edge-stable", "launch.start-failed", "launch", "launch.error.failed"));

		_ = (await WaitUntilAsync(() => session.IsErrorStatusVisibleForTests, EffectBudget)).Should().BeTrue("the failure must be shown to the user");
		_ = session.CompleteCountForTests.Should().Be(0, "a failed launch keeps the picker open");
		_ = session.IsLaunchInFlightForTests.Should().BeFalse("the in-flight window is over, so the user can pick another card");
		_ = writes.Should().Be(0);
	}

	[Fact]
	public void SystemFallback_DuringInFlightLaunch_IsIgnored()
	{
		TaskCompletionSource<LaunchHelperOutcome> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		RecordingFallbackLauncher fallback = new();
		PickerSession session = NewSession("https://example.com/", new PendingLaunchHelperClient(pending), fallback: fallback);

		session.ClickCardForTests(1);
		session.PressSystemFallbackForTests();

		_ = fallback.Urls.Should().BeEmpty("F must not open a second browser while a launch is in flight");
		_ = session.CompleteCountForTests.Should().Be(0);
		_ = pending.TrySetResult(new LaunchHelperOutcome(LaunchStatus.Failed, "edge-stable", "x", "launch", null));
	}

	[Fact]
	public void SystemFallback_WithNoLaunchInFlight_StillLaunches()
	{
		RecordingFallbackLauncher fallback = new();
		PickerSession session = NewSession("https://example.com/", new RecordingLaunchHelperClient(), fallback: fallback);

		session.PressSystemFallbackForTests();

		_ = fallback.Urls.Should().Equal("https://example.com/");
		_ = session.CompleteCountForTests.Should().Be(1);
	}

	[Fact]
	public async Task ForwardedUrl_ClearsArmedUseOriginal_AndLaunchesTheNewLink()
	{
		RecordingLaunchHelperClient launch = new();
		PickerSession session = NewSession(
			"https://first.example/a?utm_source=news&k=1",
			launch,
			cleanup: new TrackingCleanupOptions(Enabled: true));

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.UseOriginal, 0)]);
		Forward(session, "https://second.example/b");
		session.ClickCardForTests(1);

		_ = launch.LastRequest.Should().NotBeNull();
		_ = launch.LastRequest!.Url.Should().Be("https://second.example/b", "the armed use-original belonged to the previous link");
	}

	[Fact]
	public void ForwardedUrl_RecomputesCleanupForTheNewLink()
	{
		RecordingLaunchHelperClient launch = new();
		PickerSession session = NewSession(
			"https://first.example/a?utm_source=news&k=1",
			launch,
			cleanup: new TrackingCleanupOptions(Enabled: true));
		session.InitializeForTests();

		Forward(session, "https://second.example/b?utm_source=mail&z=2");
		session.ClickCardForTests(1);

		_ = launch.LastRequest!.Url.Should().Be("https://second.example/b?z=2", "cleanup is applied to the new link, not the old one");
		_ = session.EffectiveUrlForTests.Should().Be("https://second.example/b?z=2");
	}

	[Fact]
	public async Task ForwardedUrl_DropsThePreviousShortLinkResolution()
	{
		RecordingLaunchHelperClient launch = new();
		PickerSession session = NewSession(
			"https://bit.ly/start",
			launch,
			network: new ResolvingNetworkHelperClient("https://resolved.example/old"),
			shortLinks: new ShortLinkExpansionOptions(Enabled: true));

		session.ArmShortLinkConsentForNextAttemptForTests();
		await session.ResolveShortLinkInBackgroundForTestsAsync();
		Forward(session, "https://second.example/new");
		session.ClickCardForTests(1);

		_ = launch.LastRequest!.Url.Should().Be("https://second.example/new", "the short-link resolution belonged to the previous link");
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

	private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan budget)
	{
		Stopwatch elapsed = Stopwatch.StartNew();
		while (!condition() && elapsed.Elapsed < budget)
		{
			await Task.Delay(10);
		}

		return condition();
	}

	private static PickerSession NewSession(
		string url,
		ILaunchHelperClient launch,
		ISystemFallbackLauncher? fallback = null,
		TrackingCleanupOptions? cleanup = null,
		INetworkHelperClient? network = null,
		ShortLinkExpansionOptions? shortLinks = null)
	{
		BrowserSnapshot snapshot = new([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
		], 1, DateTimeOffset.UtcNow);

		ActivationRequest activation = new(
			url,
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
			fallback ?? new RecordingFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance,
			cleanup,
			new NullAuditWriter(),
			shortLinks,
			network);
	}

	private sealed class PendingLaunchHelperClient(TaskCompletionSource<LaunchHelperOutcome> pending) : ILaunchHelperClient
	{
		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			return pending.Task;
		}
	}

	private sealed class RecordingLaunchHelperClient : ILaunchHelperClient
	{
		public LaunchHelperRequest? LastRequest { get; private set; }

		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null));
		}
	}

	private sealed class RecordingFallbackLauncher : ISystemFallbackLauncher
	{
		public List<string> Urls { get; } = [];

		public LaunchResult Launch(string url)
		{
			Urls.Add(url);
			return new LaunchResult(LaunchStatus.Success, "system", null, null, null);
		}
	}

	private sealed class ResolvingNetworkHelperClient(string resolvedUrl) : INetworkHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Rejected);
		}

		public Task<ShortLinkResolveResponse> ResolveShortLinkAsync(ShortLinkResolveRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new ShortLinkResolveResponse(HelperClientDispatch.Dispatched, request.Url, resolvedUrl, ErrorCode: null, FromCache: false));
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
