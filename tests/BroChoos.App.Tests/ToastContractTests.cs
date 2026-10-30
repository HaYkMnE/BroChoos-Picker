using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

public sealed class ToastContractTests
{
	[Fact]
	public void GhostToast_HasDistinctLifetimeRateAndInteractivityContract()
	{
		_ = GhostToastWindow.Lifetime.Should().Be(TimeSpan.FromSeconds(3));
		_ = GhostToastWindow.RateLimit.Should().Be(TimeSpan.FromSeconds(30));

		string axaml = File.ReadAllText(Path.Combine(Root(), "src", "BroChoos.App", "Picker", "GhostToastWindow.axaml"));
		_ = axaml.Should().Contain("IsHitTestVisible=\"False\"");
		_ = axaml.Should().Contain("Focusable=\"False\"");
	}

	[Fact]
	public void CleanupRecoveryToast_HasDistinctInteractiveContract()
	{
		_ = CleanupRecoveryToastWindow.Lifetime.Should().Be(TimeSpan.FromSeconds(8));

		string axaml = File.ReadAllText(Path.Combine(Root(), "src", "BroChoos.App", "Picker", "CleanupRecoveryToastWindow.axaml"));
		_ = axaml.Should().Contain("IsHitTestVisible=\"True\"");
		_ = axaml.Should().Contain("Focusable=\"True\"");
	}

	[Fact]
	public void ToastContracts_AreNotMerged()
	{
		_ = GhostToastWindow.Lifetime.Should().NotBe(CleanupRecoveryToastWindow.Lifetime);
		_ = GhostToastWindow.RateLimit.Should().BeGreaterThan(CleanupRecoveryToastWindow.Lifetime);
	}

	[Fact]
	public async Task GhostToast_ShownOnOpen_RespectsLifetimeAndRateCap()
	{
		bool ghostToastShown = false;
		TaskCompletionSource ghostClosedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession session = NewSession();
		session.ConfigureToastHooksForTests(
			onGhostToast: () => ghostToastShown = true,
			onGhostToastClosed: () => _ = ghostClosedSignal.TrySetResult(),
			onCleanupToast: null,
			onCleanupToastClosed: null);

		await session.SimulateWindowOpenForTestsAsync();

		_ = ghostToastShown.Should().BeTrue("ghost toast should be shown on open");

		// Waits for the close signal itself, bounded by a hang guard, instead of a fixed
		// 3 s + 100 ms sleep. SimulateWindowOpenForTestsAsync already awaits the product's 3 s
		// lifetime, so the signal is normally set by now; the wait keeps the check independent of
		// that ordering, and only a toast that never closes reaches the 60 s guard.
		Task winner = await Task.WhenAny(ghostClosedSignal.Task, Task.Delay(TimeSpan.FromSeconds(60)));

		_ = ReferenceEquals(winner, ghostClosedSignal.Task).Should().BeTrue("ghost toast should auto-close after 3s lifetime");

		ghostToastShown = false;
		await session.SimulateWindowOpenForTestsAsync();

		_ = ghostToastShown.Should().BeFalse("ghost toast should be rate limited to 1 per 30s");
	}

	[Fact]
	public async Task CleanupRecoveryToast_ShownOnCancelToClipboard_IsInteractive()
	{
		bool cleanupToastShown = false;
		bool? isInteractive = null;
		string? written = null;
		System.Diagnostics.Stopwatch sinceShown = new();
		TimeSpan closedAfter = TimeSpan.Zero;
		TaskCompletionSource closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

		PickerSession session = NewSession();

		// The close is posted to the UI dispatcher, which no test host pumps: run it inline here.
		session.DispatchPostForTests = action => action();
		session.ConfigureClipboardHooksForTests(null, value => written = value);
		session.ConfigureToastHooksForTests(
			onGhostToast: null,
			onGhostToastClosed: null,
			onCleanupToast: interactive =>
			{
				cleanupToastShown = true;
				isInteractive = interactive;
				// Started here, BEFORE the product starts its lifetime wait, so the measured time
				// can only be longer than the product's own wait, never shorter.
				sinceShown.Start();
			},
			onCleanupToastClosed: () =>
			{
				closedAfter = sinceShown.Elapsed;
				_ = closedSignal.TrySetResult();
			});

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);

		_ = cleanupToastShown.Should().BeTrue("cleanup recovery toast should be shown on cancel-to-clipboard");
		_ = isInteractive.Should().BeTrue("cleanup recovery toast should be interactive");
		_ = written.Should().Be("https://example.com/");

		// CI run 41051054418 (test-debug, attempt 2) failed here when this was a fixed 8.1 s sleep:
		// the product's 8 s Task.Delay continuation runs on the thread pool, and on a loaded runner
		// it was more than 100 ms late. The test now waits for the close itself, bounded by a hang
		// guard far above the lifetime, and checks the lifetime separately: the close must not come
		// BEFORE 8 s. The 50 ms slack only absorbs timer-tick rounding between the product's timer
		// and this Stopwatch; it cannot hide a shorter lifetime.
		//
		// Both bounds below are measured from the show, and the lifetime they use is pinned to
		// 8 s first, so a change to the constant cannot move the window with it. The upper bound
		// (Lifetime + 10 s) fails a toast that closes on some other, much later timer; the 60 s
		// guard only turns a toast that never closes into a failure.
		_ = CleanupRecoveryToastWindow.Lifetime.Should().Be(TimeSpan.FromSeconds(8), "the cleanup recovery toast's lifetime contract is 8 s");

		Task winner = await Task.WhenAny(closedSignal.Task, Task.Delay(TimeSpan.FromSeconds(60)));

		_ = ReferenceEquals(winner, closedSignal.Task).Should().BeTrue("cleanup recovery toast should auto-close after 8s lifetime");
		_ = closedAfter.Should().BeGreaterThanOrEqualTo(
			CleanupRecoveryToastWindow.Lifetime - TimeSpan.FromMilliseconds(50),
			"the cleanup recovery toast must stay up for its whole 8 s lifetime before it auto-closes");
		_ = closedAfter.Should().BeLessThan(
			CleanupRecoveryToastWindow.Lifetime + TimeSpan.FromSeconds(10),
			"the auto-close must come from the 8 s lifetime, not from a later timer; 10 s of slack covers a loaded runner");
	}

	[Fact]
	public async Task CleanupRecoveryToast_NotShown_WhenCancelToClipboardSkipped()
	{
		bool cleanupToastShown = false;
		PickerSession session = NewSession("https://example.com/?access_token=secret");
		session.ConfigureClipboardHooksForTests(null, _ => { });
		session.ConfigureToastHooksForTests(
			onGhostToast: null,
			onGhostToastClosed: null,
			onCleanupToast: _ => cleanupToastShown = true,
			onCleanupToastClosed: null);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);

		_ = cleanupToastShown.Should().BeFalse("cleanup toast should not show when cancel-to-clipboard is skipped");
	}

	// The toast lifetime wait runs on a pool thread, but the close is UI state: it must be POSTED
	// through the dispatcher seam (Dispatcher.UIThread.Post in production), never run inline there.
	[Fact]
	public async Task CleanupRecoveryToast_CloseIsPostedToTheDispatcher_NotRunOnThePoolThread()
	{
		bool closed = false;
		List<Action> posted = [];
		TaskCompletionSource postedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PickerSession session = NewSession();
		session.DispatchPostForTests = action =>
		{
			lock (posted)
			{
				posted.Add(action);
			}

			_ = postedSignal.TrySetResult();
		};
		session.ConfigureClipboardHooksForTests(null, _ => { });
		session.ConfigureToastHooksForTests(null, null, _ => { }, () => closed = true);

		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);

		// CI-load flake (r13 validation, 1 of 20 runs): a fixed Lifetime + 500 ms sleep lost the race
		// with the product's 8 s Task.Delay continuation on a starved pool, and found nothing posted.
		// The test now waits for the post itself, bounded by a 60 s hang guard.
		Task winner = await Task.WhenAny(postedSignal.Task, Task.Delay(TimeSpan.FromSeconds(60)));

		_ = ReferenceEquals(winner, postedSignal.Task).Should().BeTrue("the cleanup toast's close must be posted to the dispatcher once its lifetime ends");
		_ = closed.Should().BeFalse("the close must not run on the pool thread");
		Action close;
		lock (posted)
		{
			_ = posted.Should().ContainSingle("the close is handed to the dispatcher");
			close = posted[0];
		}

		close();
		_ = closed.Should().BeTrue();
	}

	private static string Root()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BroChoos.sln")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
	}

	private static PickerSession NewSession(string url = "https://example.com/")
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
			new FakeLaunchHelperClient(),
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);
	}

	private sealed class FakeLaunchHelperClient : ILaunchHelperClient
	{
		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null));
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

	private sealed class FakeSystemFallbackLauncher : ISystemFallbackLauncher
	{
		public LaunchResult Launch(string url)
		{
			return new LaunchResult(LaunchStatus.Success, "system", null, null, null);
		}
	}
}
