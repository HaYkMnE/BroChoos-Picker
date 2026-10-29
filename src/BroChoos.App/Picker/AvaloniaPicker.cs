using BroChoos.App.Audit;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Routing;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Browsers;
using Microsoft.Extensions.Logging;

namespace BroChoos.App.Picker;

// IPicker implementation registered in Program's container. Like the scaffold
// probe: Main is async and cannot be [STAThread], and Windows UI work (OLE
// clipboard, activation) requires an STA apartment, so the whole Avalonia
// lifetime of one picker run gets its own dedicated STA thread; the join carries
// the session's exit code back to UrlRunner. ShowAsync is synchronous by
// contract: UrlRunner awaits it, so this run of the process IS the window.
internal sealed class AvaloniaPicker(
	ILoggerFactory loggerFactory,
	ILaunchHelperClient launchHelperClient,
	IStorageHelperClient storageHelperClient,
	INetworkHelperClient networkHelperClient,
	ISystemFallbackLauncher fallbackLauncher,
	IActivationHistoryStoreFactory activationHistoryStoreFactory,
	IRulesStoreFactory rulesStoreFactory,
	ForwardedUrlRouter router,
	ISettingsStoreFactory settingsStoreFactory,
	Platform.Windows.Icons.IProgramIconSourceFactory iconSourceFactory) : IPicker
{
	private readonly Platform.Windows.Icons.IProgramIconSourceFactory _iconSourceFactory = iconSourceFactory;
	private readonly ISettingsStoreFactory _settingsStoreFactory = settingsStoreFactory;
	private readonly ILoggerFactory _loggerFactory = loggerFactory;
	private readonly ILaunchHelperClient _launchHelperClient = launchHelperClient;
	private readonly IStorageHelperClient _storageHelperClient = storageHelperClient;
	private readonly INetworkHelperClient _networkHelperClient = networkHelperClient;
	private readonly ISystemFallbackLauncher _fallbackLauncher = fallbackLauncher;
	private readonly IActivationHistoryStoreFactory _activationHistoryStoreFactory = activationHistoryStoreFactory;
	private readonly IRulesStoreFactory _rulesStoreFactory = rulesStoreFactory;
	private readonly ForwardedUrlRouter _router = router;
	private readonly ShortLinkExpansionOptions _shortLinkExpansionOptions = new(Enabled: false);
	private int _cleanupAuditDiscardDone;

	// The options and writer this instance passes to every session. Read-only; internal so tests
	// can see exactly what the start-up discard is handed.
	internal TrackingCleanupOptions TrackingCleanupOptions { get; } = new(Enabled: false);

	internal LocalTrackingCleanupAuditWriter TrackingCleanupAuditWriter { get; } = new();

	// Test seam only: replaces the disk step so no test touches the real %LocalAppData% file.
	// Production never sets it, so the discard runs against TrackingCleanupAuditWriter.
	internal ICleanupAuditDiscarder? CleanupAuditDiscarderOverride { get; init; }

	internal ICleanupAuditDiscarder CleanupAuditDiscarder => CleanupAuditDiscarderOverride ?? TrackingCleanupAuditWriter;

	public Task<int> ShowAsync(PickerRequest request, CancellationToken cancellationToken)
	{
		// The cancellation token is accepted on the contract; the minimal shell has
		// no cancellable work - the user cancelling with Esc is the designed exit.
		_ = cancellationToken;

		RunPreUiStartup();

		IActivationHistoryStore activationHistoryStore = _activationHistoryStoreFactory.Create(request.Activation.PortableDataPath);

		// W7.2: the rules document is read once per run. Its store provisions the storage
		// root (StorageRootResolver's constructor creates directories), which is why the
		// factory - not the store - is what the container holds.
		IRulesStore rulesStore = _rulesStoreFactory.Create(request.Activation.PortableDataPath);

		// W6.2: manual/portable admission is composed here with a REAL platform gate and NO
		// selector. The selector is the file-picker surface, which is a window concern this
		// class does not own, so passing null is the honest state and the coordinator reports
		// "not available yet" for Add manually rather than pretending to have a dialog.
		PickerSession session = new(
			request,
			_launchHelperClient,
			new FirstRunConsentActionCoordinator(
				_storageHelperClient,
				manualBrowserSelector: null,
				manualBrowserAdmissionGate: new ManualBrowserAdmitter(new SystemEnvironmentProbe())),
			_fallbackLauncher,
			_router,
			_loggerFactory.CreateLogger<PickerSession>(),
			TrackingCleanupOptions,
			TrackingCleanupAuditWriter,
			_shortLinkExpansionOptions,
			_networkHelperClient,
			activationHistoryStore,
			rulesStore)
		{
			PickerLayout = ReadPickerLayout(request.Activation.PortableDataPath),
			IconLoader = new Icons.BrowserIconLoader(
				_iconSourceFactory.Create(request.Activation.PortableDataPath),
				_loggerFactory.CreateLogger<Icons.BrowserIconLoader>()),
		};

		int exitCode = 0;

		Thread uiThread = new(() => exitCode = session.Run())
		{
			Name = "BroChoos-Picker-UI",
			IsBackground = false,
		};

		uiThread.SetApartmentState(ApartmentState.STA);
		uiThread.Start();
		uiThread.Join();

		return Task.FromResult(exitCode);
	}

	// Everything ShowAsync does before it composes the session and starts the UI thread. Kept as one
	// method so tests can run exactly the production start-up steps without starting Avalonia.
	internal void RunPreUiStartup()
	{
		DiscardDisabledCleanupAuditOnce();
	}

	// Earlier internal builds left a host per session in cleanup_events.jsonl; with cleanup off this
	// build never writes or trims it, so it is removed once per process, here where the writer is
	// composed and before the UI thread starts. Fail-open: a failure is logged by exception type
	// only (ExceptionType is an allowlisted problem-log field) and the picker carries on.
	private void DiscardDisabledCleanupAuditOnce()
	{
		// The logger is created only if the discard failed, never on the normal path.
		DiscardDisabledCleanupAuditOnce(
			ref _cleanupAuditDiscardDone,
			TrackingCleanupOptions.Enabled,
			CleanupAuditDiscarder,
			() => _loggerFactory.CreateLogger<AvaloniaPicker>());
	}

	// The testable body of the step above: no Avalonia, no UI thread. `guard` is the per-picker flag
	// (AvaloniaPicker is a container singleton, so once per picker is once per process). The guard is
	// taken BEFORE the enabled check, exactly as before, so a later call never retries either way.
	internal static void DiscardDisabledCleanupAuditOnce(ref int guard, bool cleanupEnabled, ICleanupAuditDiscarder discarder, Func<ILogger> loggerOnFailure)
	{
		if (Interlocked.Exchange(ref guard, 1) != 0)
		{
			return;
		}

		CleanupAuditDiscardOutcome outcome = discarder.DiscardWhenDisabled(cleanupEnabled, out string? failureType);
		if (outcome == CleanupAuditDiscardOutcome.Failed)
		{
			loggerOnFailure().LogWarning("Disabled cleanup audit file could not be removed ({ExceptionType}); continuing", failureType);
		}
	}

	// One small settings.json read through the same store Settings writes, on this thread, before
	// the UI thread starts - the same place the history and rules files are already read. Fails
	// open to Horizontal: a missing, damaged or unreadable file never stops the picker.
	private SettingsPickerLayout ReadPickerLayout(string? portableDataPath)
	{
		try
		{
			SettingsPickerLayout layout = _settingsStoreFactory.Create(portableDataPath).LoadOrDefault().PickerLayout;
			return layout == SettingsPickerLayout.Vertical ? SettingsPickerLayout.Vertical : SettingsPickerLayout.Horizontal;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
		{
			_loggerFactory.CreateLogger<AvaloniaPicker>().LogWarning("Picker layout setting could not be read; using the horizontal default");
			return SettingsPickerLayout.Horizontal;
		}
	}
}
