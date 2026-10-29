using System.Globalization;
using BroChoos.App.Picker;
using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

public sealed class PickerContractTests
{
	[Fact]
	public void Enter_IsIgnored_UntilFocusedCardWasVisibleFor100ms()
	{
		MutableTimeProvider time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));
		PickerViewModel vm = NewViewModel(time);

		_ = vm.FocusSlot(1);
		vm.SetFocusFromControl(0);

		_ = vm.CanActivateFocusedCard().Should().BeFalse();

		time.Advance(TimeSpan.FromMilliseconds(99));
		_ = vm.CanActivateFocusedCard().Should().BeFalse();

		time.Advance(TimeSpan.FromMilliseconds(1));
		_ = vm.CanActivateFocusedCard().Should().BeTrue();
	}

	[Fact]
	public void Escape_DismissesPicker_WhenNoVisibleDetailsLayerExists()
	{
		PickerViewModel vm = NewViewModel(TimeProvider.System);
		_ = vm.FocusSlot(1);
		vm.SetFocusFromControl(0);

		_ = vm.IsDetailsOpen.Should().BeFalse();
		_ = vm.HandleEscape().Should().Be(EscapeHandlingResult.DismissPicker);
	}

	[Fact]
	public async Task Session_EnterGuard_IsEnforcedThroughActivateFocusedPath_BeforeSafeThreshold()
	{
		FakeLaunchHelperClient launch = new();
		PickerRequest request = NewRequest();
		PickerSession session = new(
			request,
			launch,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);

		int exit = await session.ShowForTestsAsync([
			new PickerKeyAction(PickerKey.Enter, 0),
			new PickerKeyAction(PickerKey.Escape, 0),
		]);

		_ = exit.Should().Be(ExitCodes.Success);
		_ = launch.LaunchCalls.Should().Be(0);
		_ = session.EnterIgnoredBeforeSafeCountForTests.Should().Be(1);
	}

	[Fact]
	public async Task Session_EscapeAndWindowClose_AreProcessedOnce_NoDoubleCloseRace()
	{
		FakeLaunchHelperClient launch = new();
		PickerRequest request = NewRequest();
		PickerSession session = new(
			request,
			launch,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);

		int exit = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);
		session.SimulateWindowClosedForTests();
		session.SimulateWindowClosedForTests();

		_ = exit.Should().Be(ExitCodes.Success);
		_ = launch.LaunchCalls.Should().Be(0);
		_ = session.CompleteCountForTests.Should().Be(1);
	}

	[Fact]
	public async Task BlockedFirstRunClick_DoesNotCreateHiddenDetailsState_AndNeedsSingleEscape()
	{
		FakeLaunchHelperClient launch = new();
		PickerRequest request = NewRequest(FirstRunConsentContract.Required);
		PickerSession session = new(
			request,
			launch,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);

		session.ClickCardForTests(1);

		int exit = await session.ShowForTestsAsync([
			new PickerKeyAction(PickerKey.Escape, 0),
		]);

		_ = exit.Should().Be(ExitCodes.Success);
		_ = launch.LaunchCalls.Should().Be(0);
		_ = session.IsDetailsOpenForTests.Should().BeFalse();
		_ = session.CompleteCountForTests.Should().Be(1);
	}

	[Fact]
	public async Task Session_UnknownLaunch_ShowsRecoveryState_WithoutAutoRetry()
	{
		FakeLaunchHelperClient launch = new(new LaunchHelperOutcome(
			LaunchStatus.Unknown,
			"edge-stable",
			"launch.browser-exit-race",
			"helper-launch",
			"launch.error.unknown"));
		PickerRequest request = NewRequest();
		PickerSession session = new(
			request,
			launch,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		_ = launch.LaunchCalls.Should().Be(1);
		_ = session.CompleteCountForTests.Should().Be(0);
	}

	[Fact]
	public async Task Session_UnknownLaunch_RetriesOnlyOnSecondExplicitEnter()
	{
		FakeLaunchHelperClient launch = new(new LaunchHelperOutcome(
			LaunchStatus.Unknown,
			"edge-stable",
			"launch.browser-exit-race",
			"helper-launch",
			"launch.error.unknown"));
		PickerSession session = new(
			NewRequest(),
			launch,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);

		session.ClickCardForTests(1);
		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		_ = launch.LaunchCalls.Should().Be(2);
		_ = session.CompleteCountForTests.Should().Be(0);
	}

	[Fact]
	public async Task Session_LaunchRequest_UsesDetectedProfileId_NotChannel()
	{
		FakeLaunchHelperClient launch = new();
		PickerRequest request = NewRequestWithCandidate(new BrowserCandidate(
			"edge-stable",
			"Microsoft Edge",
			@"C:\Edge\msedge.exe",
			"stable",
			"known-path")
		{
			ProfileIds = ["Profile 7"],
		});

		PickerSession session = new(
			request,
			launch,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		_ = launch.LastRequest.Should().NotBeNull();
		_ = launch.LastRequest!.ProfileId.Should().Be("Profile 7");
		_ = launch.LastRequest.ProfileId.Should().NotBe("stable");
	}

	[Fact]
	public async Task Session_LaunchRequest_ProfileIdIsNull_WhenNoProfilesDetected()
	{
		FakeLaunchHelperClient launch = new();
		PickerRequest request = NewRequestWithCandidate(new BrowserCandidate(
			"edge-stable",
			"Microsoft Edge",
			@"C:\Edge\msedge.exe",
			"stable",
			"known-path")
		{
			ProfileIds = null,
		});

		PickerSession session = new(
			request,
			launch,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);

		session.ClickCardForTests(1);
		_ = await session.ShowForTestsAsync([]);

		_ = launch.LastRequest.Should().NotBeNull();
		_ = launch.LastRequest!.ProfileId.Should().BeNull();
	}

	private static PickerViewModel NewViewModel(TimeProvider time)
	{
		BrowserCandidate[] candidates = [new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")];
		BrowserSnapshot snapshot = new(candidates, 0, DateTimeOffset.UtcNow);
		return new PickerViewModel("https://example.com/", snapshot, time);
	}

	private static PickerRequest NewRequest(FirstRunConsentContract? firstRun = null)
	{
		BrowserCandidate[] candidates = [new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")];
		return NewRequestWithCandidates(candidates, firstRun);
	}

	private static PickerRequest NewRequestWithCandidate(BrowserCandidate candidate, FirstRunConsentContract? firstRun = null)
	{
		return NewRequestWithCandidates([candidate], firstRun);
	}

	private static PickerRequest NewRequestWithCandidates(BrowserCandidate[] candidates, FirstRunConsentContract? firstRun = null)
	{
		BrowserSnapshot snapshot = new(candidates, 1, DateTimeOffset.UtcNow);
		ActivationRequest activation = new(
			"https://example.com/",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		return new PickerRequest(activation, snapshot, firstRun ?? FirstRunConsentContract.None);
	}

	private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	private sealed class FakeLaunchHelperClient(LaunchHelperOutcome? outcome = null) : ILaunchHelperClient
	{
		private readonly LaunchHelperOutcome _outcome = outcome ?? new LaunchHelperOutcome(LaunchStatus.Success, "edge-stable", null, null, null);

		public int LaunchCalls { get; private set; }
		public LaunchHelperRequest? LastRequest { get; private set; }

		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			LaunchCalls++;
			LastRequest = request;
			return Task.FromResult(_outcome with { TargetId = request.BrowserId });
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
