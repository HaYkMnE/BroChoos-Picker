using BroChoos.App.Localization;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// W6.2 manual/portable admission — BEHAVIOUR, not plumbing.
//
// WHAT THESE ASSERT, and why each one is worth a test rather than a comment:
//
//   * an admitted browser actually ENTERS THE CATALOG (the published snapshot the picker
//     shows contains it), rather than the flow stopping at a status line;
//   * an invalid path is REFUSED, with the reason the admission gate produced, and nothing
//     is published — a refusal that quietly published would be the worst of both;
//   * the custom/portable flags on the admitted candidate are the ones the user asked for,
//     because those flags are what the catalog persists and what the card surface reads;
//   * a manual add NEVER becomes a machine scan. That is the W3.4 consent guarantee, and it
//     is the one thing this flow could break invisibly: the publication request and the
//     discovery request are different calls, and these tests pin which one is used.
// Reads localized status strings, so it is serialized with the culture-switching classes.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class ManualBrowserValidationTests
{
	private static readonly string PortablePath = Path.Combine(
		Path.GetTempPath(),
		"not-a-real-browser.exe");

	[Fact]
	public async Task HandleAddManuallyAction_WithoutSelector_DoesNotDispatchDiscoveryAndSaysSo()
	{
		FakeStorageHelperClient storage = new();
		FirstRunConsentActionCoordinator coordinator = new(storage);
		PickerViewModel vm = NewViewModel();
		vm.ShowFirstRunPrompt();

		StorageDiscoveryResponse? result = await coordinator.HandleAsync(FirstRunAction.AddManually, vm, null, CancellationToken.None);

		_ = result.Should().BeNull();
		_ = storage.DispatchCount.Should().Be(0);
		_ = storage.DiscoverCount.Should().Be(0);
		_ = storage.PublishCount.Should().Be(0);
		// The status line is built from the localized "Could not open {0} ({1})" template, so
		// the CODE is the stable, localization-independent fact to assert. The template text
		// itself is covered by the localization suite, not here.
		_ = vm.StatusText.Should().Contain(LocalizationService.GetString("Status.Target.ManualAdd"));
		_ = vm.StatusText.Should().Contain("manual-browser.selector-unavailable");
		_ = vm.IsErrorStatusVisible.Should().BeTrue();

		// The stub's implementation-state code is gone: the message must not claim that
		// admission itself is unimplemented, because it is implemented and only the picker
		// surface is missing.
		_ = vm.StatusText.Should().NotContain("not-yet-implemented");
	}

	[Fact]
	public async Task HandleAddManuallyAction_AdmittedBrowser_EntersTheCatalogAndKeepsItsPortableFlag()
	{
		FakeStorageHelperClient storage = new();
		RecordingSelector selector = new(PortablePath, displayName: null);
		FirstRunConsentActionCoordinator coordinator = new(storage, selector, new PassingAdmitter());
		PickerViewModel vm = NewViewModel();
		vm.ShowFirstRunPrompt();
		vm.AddManuallyKind = ManualBrowserKind.Portable;

		StorageDiscoveryResponse? result = await coordinator.HandleAsync(FirstRunAction.AddManually, vm, null, CancellationToken.None);

		_ = result.Should().NotBeNull();
		_ = storage.PublishCount.Should().Be(1);
		_ = storage.DiscoverCount.Should().Be(0, "a manual add must never become the machine scan the user declined");
		_ = storage.LastPublication.Should().NotBeNull();

		BrowserCandidate published = storage.LastPublication!.Candidates.Should().ContainSingle().Subject;
		_ = published.BrowserId.Should().Be("portable-demo");
		_ = published.IsPortableBrowser.Should().BeTrue();
		_ = published.IsCustomBrowser.Should().BeFalse();
		_ = published.ExePath.Should().Be(PortablePath);

		// The admitted browser is VISIBLE: the snapshot handed back was applied to the view
		// model, so it is a card the user can select.
		_ = vm.Cards.Should().ContainSingle(card => card.BrowserId == "portable-demo");
		_ = vm.IsFirstRunPromptVisible.Should().BeFalse();
		_ = vm.StatusText.Should().Contain("Portable demo");
	}

	[Fact]
	public async Task HandleAddManuallyAction_CustomAdmission_SetsCustomFlagAndNoPortableFlag()
	{
		FakeStorageHelperClient storage = new();
		RecordingSelector selector = new(PortablePath, "My Browser");
		FirstRunConsentActionCoordinator coordinator = new(storage, selector, new PassingAdmitter());
		PickerViewModel vm = NewViewModel();
		vm.ShowFirstRunPrompt();

		_ = await coordinator.HandleAsync(FirstRunAction.AddManually, vm, null, CancellationToken.None);

		BrowserCandidate published = storage.LastPublication!.Candidates.Should().ContainSingle().Subject;
		_ = published.IsCustomBrowser.Should().BeTrue();
		_ = published.IsPortableBrowser.Should().BeFalse();
		_ = published.DisplayName.Should().Be("My Browser");
	}

	[Fact]
	public async Task HandleAddManuallyAction_RefusedPath_ShowsTheAdmissionReasonAndPublishesNothing()
	{
		FakeStorageHelperClient storage = new();
		RecordingSelector selector = new(@"\\hostile\share\msedge.exe", displayName: null);
		FirstRunConsentActionCoordinator coordinator = new(storage, selector, new RefusingAdmitter(ManualBrowserAdmissionCodes.PathRejected));
		PickerViewModel vm = NewViewModel();
		vm.ShowFirstRunPrompt();

		StorageDiscoveryResponse? result = await coordinator.HandleAsync(FirstRunAction.AddManually, vm, null, CancellationToken.None);

		_ = result.Should().BeNull();
		_ = storage.PublishCount.Should().Be(0);
		_ = storage.DiscoverCount.Should().Be(0);
		_ = vm.StatusText.Should().Contain("manual-browser.path-rejected");
		_ = vm.IsErrorStatusVisible.Should().BeTrue();
		// The prompt survives a refusal, so the other two actions are still reachable.
		_ = vm.IsFirstRunPromptVisible.Should().BeTrue();
	}

	[Fact]
	public async Task HandleAddManuallyAction_CancelledSelection_ShowsNoErrorAndKeepsThePrompt()
	{
		FakeStorageHelperClient storage = new();
		RecordingSelector selector = new(exePath: null, displayName: null);
		FirstRunConsentActionCoordinator coordinator = new(storage, selector, new PassingAdmitter());
		PickerViewModel vm = NewViewModel();
		vm.ShowFirstRunPrompt();

		StorageDiscoveryResponse? result = await coordinator.HandleAsync(FirstRunAction.AddManually, vm, null, CancellationToken.None);

		_ = result.Should().BeNull();
		_ = storage.PublishCount.Should().Be(0);
		_ = vm.IsErrorStatusVisible.Should().BeFalse("closing the picker is a cancel, not a failure");
		_ = vm.IsFirstRunPromptVisible.Should().BeTrue();
	}

	[Fact]
	public async Task HandleAddManuallyAction_PublicationRejected_ShowsTheStorageReasonAndKeepsThePrompt()
	{
		FakeStorageHelperClient storage = new() { RejectPublication = true };
		RecordingSelector selector = new(PortablePath, displayName: null);
		FirstRunConsentActionCoordinator coordinator = new(storage, selector, new PassingAdmitter());
		PickerViewModel vm = NewViewModel();
		vm.ShowFirstRunPrompt();

		StorageDiscoveryResponse? result = await coordinator.HandleAsync(FirstRunAction.AddManually, vm, null, CancellationToken.None);

		_ = result.Should().NotBeNull();
		_ = result!.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = vm.StatusText.Should().Contain("storage.catalog-readonly");
		_ = vm.IsFirstRunPromptVisible.Should().BeTrue();

		// Coordinator call site after a rejected publication: the ACTION template naming the
		// localized "Adding a browser" target, never the launch wording.
		_ = vm.StatusText.Should().StartWith(LocalizationService.GetString("Status.Target.ManualAdd"));
		_ = vm.StatusText.Should().NotContain("Could not open");
	}

	// Coordinator call site after a refused admission: same action template, with the gate's
	// localized phrase and raw code.
	[Fact]
	public async Task HandleAddManuallyAction_RefusedPath_UsesTheLocalizedActionTemplate()
	{
		FakeStorageHelperClient storage = new();
		RecordingSelector selector = new(@"\\hostile\share\msedge.exe", displayName: null);
		FirstRunConsentActionCoordinator coordinator = new(storage, selector, new RefusingAdmitter(ManualBrowserAdmissionCodes.PathRejected));
		PickerViewModel vm = NewViewModel();

		_ = await coordinator.HandleAsync(FirstRunAction.AddManually, vm, null, CancellationToken.None);

		_ = vm.StatusText.Should().Be(LocalizationService.GetString(
			"Status.ActionFailed",
			LocalizationService.GetString("Status.Target.ManualAdd"),
			LocalizationService.GetString("Error.manual-browser.path-rejected"),
			"manual-browser.path-rejected"));
	}

	private static PickerViewModel NewViewModel()
	{
		BrowserCandidate[] candidates =
		[
			new("edge-stable", "Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "stable", "known-path"),
		];
		return new PickerViewModel("https://example.com/", new BrowserSnapshot(candidates, 0, DateTimeOffset.UtcNow));
	}

	private sealed class RecordingSelector(string? exePath, string? displayName) : IManualBrowserSelector
	{
		public bool TrySelectPath(ManualBrowserKind kind, out string? path, out string? name)
		{
			path = exePath;
			name = displayName;
			return exePath is not null;
		}
	}

	// Stands in for the real platform admission gate. The App tests care about the WIRING —
	// which request is sent, what is published, what the surface says — while the gate's own
	// behavior is pinned in BroChoos.Platform.Windows.Tests by ManualBrowserAdmissionTests,
	// on real files and real path shapes.
	//
	// MEASURED EDITOR/TOOLING HAZARD, recorded because it cost two build cycles: five
	// consecutive edits to THIS EXACT IDENTIFIER (the interface name after the colon) were
	// silently not applied — the tool reported success and a read-back via PowerShell
	// `Get-Content` even showed the new text, while the file on disk and the compiler still
	// had the old token. The same edits applied without error when the target string was
	// changed to something unambiguous. Nothing about the repository is at fault; the lesson
	// is to verify an edit of this line with `Select-String` on the raw file before building.
	private sealed class PassingAdmitter : IManualBrowserAdmissionGate
	{
		public ManualBrowserAdmissionResult Admit(ManualBrowserAdmissionRequest request)
		{
			BrowserCandidate candidate = new(
				request.Kind == ManualBrowserKind.Portable ? "portable-demo" : "custom-demo",
				string.IsNullOrWhiteSpace(request.DisplayName) ? "Portable demo" : request.DisplayName,
				request.ExePath,
				request.Kind == ManualBrowserKind.Portable ? "portable" : "custom",
				request.Kind == ManualBrowserKind.Portable ? "portable" : "manual")
			{
				IsCustomBrowser = request.Kind == ManualBrowserKind.Custom,
				IsPortableBrowser = request.Kind == ManualBrowserKind.Portable,
			};

			return ManualBrowserAdmissionResult.Accepted(candidate);
		}
	}

	private sealed class RefusingAdmitter(string code) : IManualBrowserAdmissionGate
	{
		public ManualBrowserAdmissionResult Admit(ManualBrowserAdmissionRequest request)
		{
			return ManualBrowserAdmissionResult.Refused(code);
		}
	}

	private sealed class FakeStorageHelperClient : IStorageHelperClient
	{
		public int DispatchCount { get; private set; }

		public int DiscoverCount { get; private set; }

		public int PublishCount { get; private set; }

		public bool RejectPublication { get; init; }

		public StorageCandidatePublicationRequest? LastPublication { get; private set; }

		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			DispatchCount++;
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			DiscoverCount++;
			BrowserSnapshot snapshot = new(
				[
					new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "stable", "known-path"),
				],
				5,
				DateTimeOffset.UtcNow);
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, snapshot, null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			PublishCount++;
			LastPublication = request;

			if (RejectPublication)
			{
				return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.catalog-readonly"));
			}

			BrowserSnapshot snapshot = new(request.Candidates, 6, DateTimeOffset.UtcNow);
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, snapshot, null));
		}
	}
}
