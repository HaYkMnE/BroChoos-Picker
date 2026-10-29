using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

public sealed class FirstRunConsentTests
{
	[Fact]
	public async Task HandleFindBrowsersAction_DispatchesExactlyOneConsentRequest()
	{
		FakeStorageHelperClient storage = new();
		FirstRunConsentActionCoordinator coordinator = new(storage);
		PickerViewModel vm = NewViewModel();
		vm.ShowFirstRunPrompt();

		StorageDiscoveryResponse? result = await coordinator.HandleAsync(FirstRunAction.FindBrowsers, vm, null, CancellationToken.None);

		_ = storage.DispatchCount.Should().Be(1);
		_ = storage.DiscoverCount.Should().Be(1);
		_ = storage.PublishCount.Should().Be(0, "consented discovery is the discovery call, never the manual-publication call");
		_ = storage.LastCommand.Should().NotBeNull();
		_ = storage.LastCommand!.CommandName.Should().Be("first-run.find-browsers");
		_ = result.Should().NotBeNull();
		_ = result!.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
		_ = vm.IsFirstRunPromptVisible.Should().BeFalse();
		_ = vm.Cards.Should().ContainSingle();
	}

	// W6.2 UPDATE. This test used to assert only that Add manually did no discovery. That
	// inverted into a weaker claim than the plan requires: "Add manually does nothing" is
	// satisfied both by a correct refusal and by an unwired stub. The behavior is now pinned
	// where it belongs, in ManualBrowserValidationTests (admission enters the catalog, invalid
	// paths are refused with their reason, custom/portable flags are set); what remains here is
	// the no-scan half, which is this class's subject.
	[Fact]
	public async Task HandleAddManuallyAction_DoesNotDispatchDiscoveryRequest()
	{
		FakeStorageHelperClient storage = new();
		FirstRunConsentActionCoordinator coordinator = new(storage);
		PickerViewModel vm = NewViewModel();
		vm.ShowFirstRunPrompt();

		StorageDiscoveryResponse? result = await coordinator.HandleAsync(FirstRunAction.AddManually, vm, null, CancellationToken.None);

		_ = storage.DispatchCount.Should().Be(0);
		_ = storage.DiscoverCount.Should().Be(0);
		_ = storage.PublishCount.Should().Be(0);
		_ = result.Should().BeNull();
		_ = vm.IsFirstRunPromptVisible.Should().BeTrue();
	}

	[Fact]
	public async Task HandleSkipAction_DoesNotDispatchDiscoveryRequest()
	{
		FakeStorageHelperClient storage = new();
		FirstRunConsentActionCoordinator coordinator = new(storage);
		PickerViewModel vm = NewViewModel();
		vm.ShowFirstRunPrompt();

		StorageDiscoveryResponse? result = await coordinator.HandleAsync(FirstRunAction.Skip, vm, null, CancellationToken.None);

		_ = storage.DispatchCount.Should().Be(0);
		_ = storage.DiscoverCount.Should().Be(0);
		_ = storage.PublishCount.Should().Be(0);
		_ = result.Should().BeNull();
		_ = vm.IsFirstRunPromptVisible.Should().BeFalse("Skip answers the prompt, so the panel collapses (consent stays pending in the session)");
	}

	[Fact]
	public void AddManually_IsOfferedOnlyWhenAFilePickerIsComposed()
	{
		_ = new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()).CanAddManually.Should().BeFalse(
			"the production picker composes no file picker, so the button would be a dead end");
	}

	private static PickerViewModel NewViewModel()
	{
		BrowserCandidate[] candidates =
		[
			new("edge-stable", "Microsoft Edge", @"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", "stable", "known-path"),
		];
		return new PickerViewModel("https://example.com/", new BrowserSnapshot(candidates, 0, DateTimeOffset.UtcNow));
	}

	private sealed class FakeStorageHelperClient : IStorageHelperClient
	{
		public int DispatchCount { get; private set; }

		public int DiscoverCount { get; private set; }

		public int PublishCount { get; private set; }

		public HelperCommand? LastCommand { get; private set; }

		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			DispatchCount++;
			LastCommand = command;
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			DiscoverCount++;
			BrowserSnapshot snapshot = new(
				[
					new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", "stable", "known-path"),
				],
				5,
				DateTimeOffset.UtcNow);
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, snapshot, null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			PublishCount++;
			BrowserSnapshot snapshot = new(request.Candidates, 6, DateTimeOffset.UtcNow);
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, snapshot, null));
		}
	}
}
