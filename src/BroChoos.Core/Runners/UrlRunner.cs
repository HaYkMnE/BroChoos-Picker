using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Snapshots;
using Microsoft.Extensions.Logging;

namespace BroChoos.Core.Runners;

public sealed class UrlRunner(
	ILogger<UrlRunner> logger,
	ISingleInstanceGuard guard,
	IBrowserDetector browserDetector,
	IPicker picker)
{
	private readonly ILogger<UrlRunner> _logger = logger;
	private readonly ISingleInstanceGuard _guard = guard;
	private readonly IBrowserDetector _browserDetector = browserDetector;
	private readonly IPicker _picker = picker;

	public async Task<int> RunAsync(ActivationRequest request, CancellationToken cancellationToken)
	{
		// W1.1 fail-closed contract: --no-ui never performs discovery or launch.
		// Explicit choice is required; auto-selecting a browser is not allowed. Checked
		// BEFORE acquiring/forwarding so a rejected request is never handed to a
		// secondary instance's primary.
		if (request.Modifiers.HasFlag(ActivationModifiers.NoUi))
		{
			Console.Error.WriteLine("No-UI activation requires an explicit browser choice; nothing was opened.");
			_logger.LogError("No-UI activation rejected: explicit browser choice required");
			return ExitCodes.GeneralError;
		}

		SingleInstanceRole role = await _guard.AcquireOrForwardAsync(request, cancellationToken);

		switch (role)
		{
			case SingleInstanceRole.Primary:
				return await ShowPickerAsync(request, cancellationToken);

			case SingleInstanceRole.Secondary:
				Console.WriteLine("[secondary] Forwarded URL to primary instance");
				_logger.LogInformation("Secondary instance forwarded URL");
				return ExitCodes.Success;

			case SingleInstanceRole.Failed:
				_logger.LogError("Could not acquire or forward to primary");
				return ExitCodes.GeneralError;

			default:
				return ExitCodes.GeneralError;
		}
	}

	// Picker path: startup receives an immutable/default snapshot and must not block on
	// detection/discovery before first frame. Discovery integration is deferred to the
	// post-consent helper flow (W3), so this path always starts from BrowserSnapshot.Empty.
	private async Task<int> ShowPickerAsync(ActivationRequest request, CancellationToken cancellationToken)
	{
		BrowserSnapshot snapshot = BuildSnapshot();
		FirstRunConsentContract firstRunConsent = snapshot.IsEmpty
			? FirstRunConsentContract.Required
			: FirstRunConsentContract.None;

		return await _picker.ShowAsync(new PickerRequest(request, snapshot, firstRunConsent), cancellationToken);
	}


	// W2.3: first frame is rendered from immutable/default data only.
	private BrowserSnapshot BuildSnapshot()
	{
		_ = _browserDetector; // intentionally deferred; see W3 discovery integration.
		_logger.LogInformation("Using immutable empty browser snapshot on startup; detection/discovery is deferred");
		return BrowserSnapshot.Empty;
	}
}
