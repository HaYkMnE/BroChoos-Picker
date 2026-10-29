using BroChoos.App.Localization;
using BroChoos.Core.Abstractions;

namespace BroChoos.App.Picker;

// W6.2 manual/portable admission, wired behind the W3.4 first-run "Add manually" action.
//
// WHAT CHANGED FROM THE STUB, exactly: this file used to answer the action with the
// placeholder error code "first-run.manual-add-not-yet-implemented". The placeholder is
// GONE. The action now runs a real admission: the surface supplies the path the user
// selected, ManualBrowserAdmitter validates it through the existing path/identity gates,
// and an admitted browser is PUBLISHED into the catalog through the same storage request
// consent-based discovery uses — so a manually added browser appears as a picker card.
//
// HONEST BOUNDARY, stated here rather than discovered later:
//
//   1. THE FILE-PICKER SURFACE IS NOT PART OF THIS TYPE. This is an App-layer coordinator
//      with no window, and a native open-file dialog needs one. The selector seam below is
//      therefore the contract the window implements, and when no selector is supplied
//      (which is every current construction: the surface has not been built yet) the action
//      says so in plain words — "Choosing a browser file is not available yet" — rather
//      than silently doing nothing or reporting a success that did not happen.
//
//   2. A REFUSED ADMISSION STILL DOES NOT DISPATCH DISCOVERY, and the first-run prompt stays
//      up so the user can pick another action. That is the W3.4 no-scan guarantee preserved.
//
//   3. WHEN A BROWSER IS ADMITTED, ONLY THE ADMITTED BROWSER IS PUBLISHED. This is
//      deliberately NOT the consent-based discovery call (which scans the machine): a manual
//      add must never silently become the scan the user declined.
internal sealed class FirstRunConsentActionCoordinator(
	IStorageHelperClient storageHelperClient,
	IManualBrowserSelector? manualBrowserSelector = null,
	IManualBrowserAdmissionGate? manualBrowserAdmissionGate = null)
{
	private static readonly HelperCommand FindBrowsersCommand = new("first-run.find-browsers", "{}");

	// True only when "Add manually" can actually do something: a file-picker surface AND an
	// admission gate are composed. The window hides the button otherwise.
	public bool CanAddManually => _manualBrowserSelector is not null && _manualBrowserAdmissionGate is not null;

	private readonly IStorageHelperClient _storageHelperClient = storageHelperClient;
	private readonly IManualBrowserSelector? _manualBrowserSelector = manualBrowserSelector;
	private readonly IManualBrowserAdmissionGate? _manualBrowserAdmissionGate = manualBrowserAdmissionGate;

	public async Task<StorageDiscoveryResponse?> HandleAsync(FirstRunAction action, PickerViewModel vm, string? portableDataPath, CancellationToken cancellationToken)
	{
		switch (action)
		{
			case FirstRunAction.FindBrowsers:
				_ = await _storageHelperClient.DispatchAsync(FindBrowsersCommand, cancellationToken);
				StorageDiscoveryResponse response = await _storageHelperClient.DiscoverAndPersistAsync(
					new StorageDiscoveryRequest(portableDataPath),
					cancellationToken);
				if (response.Dispatch == HelperClientDispatch.Dispatched && response.Snapshot is not null)
				{
					PickerLog.Marker($"FIRST_RUN_ACTION find-browsers snapshot-published generation={response.Snapshot.Generation}");
					vm.ApplySnapshot(response.Snapshot);

					// The prompt goes away only when there is something to choose. A search that
					// found nothing (or failed, below) keeps the buttons up: hiding them left an
					// empty window with no next step.
					if (vm.Cards.Count > 0)
					{
						vm.ClearStatusForReadyChoices();
						vm.ClearFirstRunPrompt();
					}
					else
					{
						vm.ShowFirstRunNoneFound();
					}
				}
				else
				{
					PickerLog.Marker($"FIRST_RUN_ACTION find-browsers publish-failed code={response.ErrorCode ?? "none"}");
					vm.ShowActionFailure(LocalizationService.GetString("Status.Target.AvailableBrowsers"), response.ErrorCode ?? "consent.discovery.failed");
				}

				return response;

			case FirstRunAction.AddManually:
				return await HandleAddManuallyAsync(vm, portableDataPath, cancellationToken);

			case FirstRunAction.Skip:
				PickerLog.Marker("FIRST_RUN_ACTION skip");

				// Skipping the scan is the user's choice, not a failure: its own info status. The
				// panel collapses (the user answered it), but nothing was found, so the session's
				// consent gate stays pending and the prompt returns on the next open.
				vm.ClearFirstRunPrompt();
				vm.ShowFirstRunSkipped();
				return null;

			default:
				return null;
		}
	}

	private async Task<StorageDiscoveryResponse?> HandleAddManuallyAsync(PickerViewModel vm, string? portableDataPath, CancellationToken cancellationToken)
	{
		ManualBrowserKind kind = _manualBrowserSelector is null
			? ManualBrowserKind.Custom
			: vm.AddManuallyKind;

		if (_manualBrowserSelector is null || _manualBrowserAdmissionGate is null)
		{
			// NO SURFACE, NO VALIDATOR, NO CLAIM. The old placeholder code is not reused
			// because it named an implementation state ("not yet implemented") that is no
			// longer true: admission IS implemented, and what is missing is the file-picker
			// control. The message says exactly that.
			PickerLog.Marker("FIRST_RUN_ACTION add-manually selector-unavailable");
			vm.ShowActionFailure(LocalizationService.GetString("Status.Target.ManualAdd"), "manual-browser.selector-unavailable");
			return null;
		}

		if (!_manualBrowserSelector.TrySelectPath(kind, out string? exePath, out string? displayName) || string.IsNullOrWhiteSpace(exePath))
		{
			// The user closed the dialog. That is a cancel, not a failure: no error line is
			// shown, and the prompt stays up so another action remains available.
			PickerLog.Marker("FIRST_RUN_ACTION add-manually selection-cancelled");
			return null;
		}

		ManualBrowserAdmissionResult admission = _manualBrowserAdmissionGate.Admit(
			new ManualBrowserAdmissionRequest(kind, exePath, displayName));

		if (!admission.Admitted || admission.Candidate is null)
		{
			PickerLog.Marker($"FIRST_RUN_ACTION add-manually refused code={admission.Code}");
			vm.ShowActionFailure(LocalizationService.GetString("Status.Target.ManualAdd"), admission.Code);
			return null;
		}

		PickerLog.Marker($"FIRST_RUN_ACTION add-manually admitted browser={admission.Candidate.BrowserId} custom={admission.Candidate.IsCustomBrowser} portable={admission.Candidate.IsPortableBrowser}");

		// The same helper request consent-based discovery uses. The helper's resolver
		// reconciles the published candidate with anything already catalogued, so a second
		// manual add does not replace the first. A rejection leaves the prompt up: nothing
		// was published, and the user is told why.
		StorageDiscoveryResponse publish = await _storageHelperClient.PublishCandidatesAsync(
			new StorageCandidatePublicationRequest([admission.Candidate], portableDataPath),
			cancellationToken);

		if (publish.Dispatch != HelperClientDispatch.Dispatched || publish.Snapshot is null)
		{
			PickerLog.Marker($"FIRST_RUN_ACTION add-manually publish-failed code={publish.ErrorCode ?? "none"}");
			vm.ShowActionFailure(LocalizationService.GetString("Status.Target.ManualAdd"), publish.ErrorCode ?? "manual-browser.publish-failed");
			return publish;
		}

		vm.ApplySnapshot(publish.Snapshot);
		vm.ClearStatusForReadyChoices();
		vm.ClearFirstRunPrompt();
		vm.ShowManualAddSucceeded(admission.Candidate.DisplayName);
		return publish;
	}
}
