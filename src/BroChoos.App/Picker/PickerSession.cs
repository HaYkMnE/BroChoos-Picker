using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using BroChoos.App.Audit;
using BroChoos.App.Localization;
using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Privacy;
using BroChoos.Core.Routing;
using BroChoos.Core.Runners;
using BroChoos.Core.Snapshots;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.SingleInstance;
using Microsoft.Extensions.Logging;

namespace BroChoos.App.Picker;

// One picker run, end to end: window creation, pre-show placement, the bounded
// foreground ladder, key actions mapped onto the view model, launch / fallback /
// copy through the EXISTING 0A-2 services, and the forwarded-URL handoff. All UI
// state lives on the STA UI thread; the only caller from elsewhere is
// OnForwardedUrl (pipe-server thread), which marshals in through the dispatcher.
internal sealed class PickerSession(
	PickerRequest request,
	ILaunchHelperClient launchHelperClient,
	FirstRunConsentActionCoordinator firstRunConsentActionCoordinator,
	ISystemFallbackLauncher fallback,
	ForwardedUrlRouter router,
	ILogger<PickerSession> logger,
	TrackingCleanupOptions? trackingCleanupOptions = null,
	ITrackingCleanupAuditWriter? trackingCleanupAuditWriter = null,
	ShortLinkExpansionOptions? shortLinkExpansionOptions = null,
	INetworkHelperClient? networkHelperClient = null) : IPickerForwardTarget
{
	private readonly PickerRequest _request = request;
	private readonly ILaunchHelperClient _launchHelperClient = launchHelperClient;
	private readonly INetworkHelperClient _networkHelperClient = networkHelperClient ?? new NullNetworkHelperClient();
	private readonly FirstRunConsentActionCoordinator _firstRunConsentActionCoordinator = firstRunConsentActionCoordinator;
	private readonly ISystemFallbackLauncher _fallback = fallback;
	private readonly ForwardedUrlRouter _router = router;
	private readonly ILogger<PickerSession> _logger = logger;
	private readonly TrackingCleanupOptions _trackingCleanupOptions = trackingCleanupOptions ?? new TrackingCleanupOptions();
	private readonly ITrackingCleanupAuditWriter _trackingCleanupAuditWriter = trackingCleanupAuditWriter ?? new LocalTrackingCleanupAuditWriter();
	private readonly ShortLinkExpansionOptions _shortLinkExpansionOptions = shortLinkExpansionOptions ?? new ShortLinkExpansionOptions();
	private ActivationHistoryDocument _activationHistoryState = new(new ActivationHistorySettingsContract(), []);
	private readonly IActivationHistoryStore _activationHistoryStore = new NullActivationHistoryStore();

	// W1.5 (D-v3.5-150, spec 2.4): the FULLSCREEN-SUPPRESSED queue is the session's own named
	// policy instance. The plan's integration point is this session's forwarded-URL entry point
	// (OnForwardedActivation, the method the header comment calls OnForwardedUrl), so the queue
	// lives for exactly the lifetime of "the picker is showing" and dies with the session - which
	// is also why it cannot persist across a restart (W1.5 negative clause). It is deliberately a
	// SEPARATE instance from the guard's concurrent-click queue (capacity 5): the spec forbids
	// merging the caps, and routing a suppressed activation through the guard's queue would do
	// exactly that. The instance is non-null by construction, so the production path can never
	// silently run without it (the defect this wiring closes).
	private readonly FullscreenSuppressionQueue _fullscreenSuppressionQueue = FullscreenSuppressionQueue.CreateDefault();

	// W7.2: rules are loaded ONCE per session, and the decision they produce is data only.
	// The default (no store composed) is "no rules", so a session without persistence ranks
	// every card as AllChoices — the pre-ranking behaviour.
	private readonly RuleSetDocument _ruleSet = new(0, []);

	private BrowserSnapshot _snapshot = request.Snapshot;
	private TrackingCleanupResult _cleanupState = TrackingCleanupEngine.Cleanup(request.Url, trackingCleanupOptions ?? new TrackingCleanupOptions());
	private bool _useOriginalArmedForNextLaunch;
	private bool _shortLinkConsentArmedForNextAttempt;
	private string? _resolvedShortLinkUrl;

	// Bumped every time a forwarded link replaces the current one. A short-link resolution
	// captures it when it starts and drops its result if the link changed meanwhile.
	private int _linkGeneration;

	// True from the moment a launch starts until its outcome has been handled. Closes the
	// double-launch window the H1 fix would otherwise open (see LaunchCard). An auto-property
	// rather than a field because the tests read it through IsLaunchInFlightForTests, and a
	// field-plus-passthrough-property pair would be a needless second name for one value.
	internal bool IsLaunchInFlightForTests { get; private set; }

	// Counts requests to bring the picker back to the foreground after a launch (the launched
	// browser took focus). Counted before the HWND check, so tests without a window observe it.
	internal int ForegroundRequestCountForTests { get; private set; }

	// R3-4/R4-4 seam: runs inside the drain loop right after each dequeue, so a test can make the
	// drain throw with a forward already taken off the queue.
	internal Action? DrainHeldForwardsForTests { get; set; }

	// The exact text the window renders on its status line (PickerWindow binds StatusMessage).
	internal string StatusMessageForTests => _vm?.StatusMessage ?? string.Empty;

	internal bool IsForwardWaitingVisibleForTests => StatusMessageForTests.Contains(LocalizationService.GetString("Status.ForwardWaiting"), StringComparison.Ordinal);

	private IClassicDesktopStyleApplicationLifetime? _desktop;
	private PickerWindow? _window;
	private PickerViewModel? _vm;
	// volatile: written on the UI thread (Complete) and read by the fullscreen wait, which runs
	// off it (WaitForUserNotBusyThenForegroundAsync). volatile makes that cross-thread read see
	// the latest write; every other access is on the UI thread.
	private volatile bool _terminal;

	// DEFECT #36-c: THE GUARD FOR THE PENDING-WRITE WINDOW. `_terminal` is the terminal-STATE
	// source of truth and stays exactly as it was (set in Complete, read by the launch/first-run/
	// forwarded-URL paths), but it is only set AFTER the dismissal's awaited clipboard write
	// returns - so on its own it leaves a real window, whose length is the OS clipboard service's
	// to choose, in which a second dismissal is neither terminal nor guarded. A duplicate
	// `Deactivated` (PickerWindow's Deactivated handler forwards EVERY deactivation, and the class
	// comment on OnExternalDismissal records that a real one can arrive more than once) or a
	// repeat Escape arriving there re-entered and ran the cancel-to-clipboard write a SECOND time.
	//
	// This flag is claimed SYNCHRONOUSLY, before any await, so it closes exactly that window: the
	// first dismissal owns the side effects, and every later entry returns immediately - no second
	// write, no second toast, no second Complete (which is idempotent anyway).
	//
	// IT IS DELIBERATELY NEVER RELEASED, and that is what makes it deadlock-proof and
	// leak-proof rather than merely convenient: the ONLY callers are the two dismissal entry
	// points, and both are one-way terminal transitions - a dismissal is never legitimately
	// retried on a live session, so there is nothing to reopen. A TOO-STRICT guard would be a bug
	// (it would swallow a legitimate dismissal), so the claim sits after the same `_terminal`/
	// `_vm` pre-checks the old code used, and it cannot swallow anything the old code performed.
	// A throwing or skipped clipboard write still consumes the claim: TryCancelToClipboardOnDismissAsync
	// swallows its own failures, and the callers Complete unconditionally afterwards, so the
	// dismissal still completes with ExitCodes.Success - and if the write itself threw before the
	// claim were consumed, the flag would stay true and the session would simply never dismiss
	// again, which is the safe direction (one dismissal, not two writes).
	private bool _dismissalInProgress;

	// A deactivation that arrived while a launch was in flight. The launched browser taking focus
	// raises Deactivated, which is NOT a user cancel: it is only recorded here, and the launch
	// outcome decides what happens next (success ends the session, failure keeps it open).
	private bool _deactivatedDuringLaunch;

	// Set once a first-run action produced real cards (see HandleFirstRunActionAsync).
	internal bool IsFirstRunConsentSatisfiedForTests { get; private set; }

	// Two-press clear history: when the first Ctrl+Backspace armed it, or null.
	internal static readonly TimeSpan ClearHistoryConfirmWindow = TimeSpan.FromSeconds(4);
	private DateTimeOffset? _clearHistoryArmedAt;
	private TimeProvider _timeProvider = TimeProvider.System;

	// Fullscreen deferral: poll interval, the in-flight flag, and the seams the tests replace.
	internal static readonly TimeSpan FullscreenPollInterval = TimeSpan.FromSeconds(1);
	internal static readonly TimeSpan FullscreenMaxHold = TimeSpan.FromSeconds(30);
	private bool _foregroundDeferred;
	internal Func<bool> IsUserBusyForTests { get; set; } = UserNotificationState.IsUserBusy;
	internal Func<TimeSpan, Task> DelayForTests { get; set; } = static delay => Task.Delay(delay);
	internal int ForegroundDeferredCountForTests { get; private set; }

	internal void UseTimeProviderForTests(TimeProvider timeProvider)
	{
		_timeProvider = timeProvider;
	}

	internal bool IsFirstRunConsentPendingForTests => IsFirstRunConsentPending;

	internal bool IsFirstRunPromptVisibleForTests => _vm?.IsFirstRunPromptVisible == true;

	internal bool IsAddManuallyAvailableForTests => _vm?.IsAddManuallyAvailable == true;

	internal string FirstRunBusyTextForTests => _vm?.FirstRunBusyText ?? string.Empty;

	private uint _uiThreadId;
	private bool _ignoreWindowClosedOnce;
	private int _testExitCode = int.MinValue;
	private bool ClipboardReadAtOpenEnabled => _request.EffectiveClipboardOptions.ClipboardReadAtOpenEnabled;

	// The program-icon loader (composed by AvaloniaPicker). Null keeps the letter plates.
	internal Icons.BrowserIconLoader? IconLoader { get; init; }

	// Settings > Appearance > Picker layout (read by AvaloniaPicker before the UI thread starts).
	internal SettingsPickerLayout PickerLayout { get; init; } = SettingsPickerLayout.Horizontal;
	private bool CancelToClipboardEnabled => _request.EffectiveClipboardOptions.CancelToClipboardEnabled;

	internal PickerSession(
		PickerRequest request,
		ILaunchHelperClient launchHelperClient,
		FirstRunConsentActionCoordinator firstRunConsentActionCoordinator,
		ISystemFallbackLauncher fallback,
		ForwardedUrlRouter router,
		ILogger<PickerSession> logger,
		TrackingCleanupOptions? trackingCleanupOptions,
		ITrackingCleanupAuditWriter? trackingCleanupAuditWriter,
		ShortLinkExpansionOptions? shortLinkExpansionOptions,
		INetworkHelperClient? networkHelperClient,
		IActivationHistoryStore? activationHistoryStore,
		IRulesStore? rulesStore = null)
		: this(
			request,
			launchHelperClient,
			firstRunConsentActionCoordinator,
			fallback,
			router,
			logger,
			trackingCleanupOptions,
			trackingCleanupAuditWriter,
			shortLinkExpansionOptions,
			networkHelperClient)
	{
		if (activationHistoryStore is not null)
		{
			ConfigReadResult<ActivationHistoryDocument> history = activationHistoryStore.LoadWithOutcome();
			_activationHistoryState = history.Value;
			_activationHistoryStore = activationHistoryStore;
			_historyOutcomeAtOpen = history.Outcome;
			if (ConfigReadOutcomes.IsDamaged(history.Outcome))
			{
				_configNoticeRequired = true;
				_logger.LogWarning("Saved settings could not be read; defaults are in use ({ReasonCode})", HistoryResetCode);
			}
		}

		if (rulesStore is not null)
		{
			// FAIL OPEN: a rules file that is absent, locked or corrupt yields "no rules",
			// and the picker opens on detector order. Rules are suggestions; losing them
			// must never lose the link. Loaded once, so a mid-session file change cannot
			// reorder the cards under the user.
			ConfigReadResult<RuleSetDocument> rules = rulesStore.LoadWithOutcome();
			_ruleSet = rules.Value;
			_rulesStoreForRecheck = rulesStore;
			_rulesOutcomeAtOpen = rules.Outcome;
			if (ConfigReadOutcomes.IsDamaged(rules.Outcome))
			{
				_configNoticeRequired = true;
				_logger.LogWarning("Saved settings could not be read; defaults are in use ({ReasonCode})", RulesResetCode);
			}
		}
	}

	// Problem-log codes (ProblemLog.KnownErrorCodes). A MISSING file is a first run, not damage,
	// so only a corrupt or incompatible file produces them (ConfigReadOutcomes.IsDamaged).
	internal const string HistoryResetCode = "config.history.reset-defaults";
	internal const string RulesResetCode = "config.rules.reset-defaults";
	internal const string RepairFailedCode = "config.settings.repair-failed";
	internal const string RepairIncompleteCode = "config.settings.repair-incomplete";

	// Set at session start when history.json or rules.json was unusable; the picker then shows
	// the quiet recovery notice for the whole run. The link path is unchanged either way.
	private readonly bool _configNoticeRequired;

	// "Repair settings" is two presses, like Ctrl+Backspace for history: the first only arms,
	// a second press within the same confirm window runs it.
	internal static readonly TimeSpan RepairConfirmWindow = ClearHistoryConfirmWindow;
	private DateTimeOffset? _repairArmedAt;
	private Task _repairRun = Task.CompletedTask;
	private ITimer? _repairDisarmTimer;

	// Runs the disarm on the UI thread. Tests replace it with an inline call, because the
	// window-less test host has no dispatcher loop to drain a post.
	internal Action<Action> PostToUi { get; set; } = static action => Dispatcher.UIThread.Post(action);

	private SafeModeLauncher _safeModeLauncher = SafeModeLauncher.CreateDefault();

	internal bool IsConfigNoticeVisibleForTests => _vm?.IsConfigNoticeVisible == true;

	internal ConfigRepairState RepairStateForTests => _vm?.RepairState ?? ConfigRepairState.Idle;

	internal void UseSafeModeLauncherForTests(SafeModeLauncher launcher)
	{
		_safeModeLauncher = launcher;
	}

	// Drives the REAL button handler; the returned task is the repair run it started (or a
	// completed task when the press only armed or was refused).
	internal Task PressRepairForTestsAsync()
	{
		InitializeForTests();
		Task before = _repairRun;
		OnRepairPressed();
		return ReferenceEquals(before, _repairRun) ? Task.CompletedTask : _repairRun;
	}

	// The notice's action. Press 1 arms and says what press 2 does; press 2 within the window
	// runs `--safe-mode --safe-mode-action repair-settings` hidden and reports the real result.
	// Presses while it runs are refused (no second child). The link and the cards stay usable
	// throughout: nothing here waits on the UI thread.
	private void OnRepairPressed()
	{
		if (_terminal || _vm is null || !_vm.IsConfigNoticeVisible || _vm.IsRepairRunning)
		{
			return;
		}

		DateTimeOffset now = _timeProvider.GetUtcNow();
		bool confirmed = _vm.RepairState == ConfigRepairState.Armed
			&& _repairArmedAt is { } armedAt
			&& now - armedAt <= RepairConfirmWindow;

		if (!confirmed)
		{
			_repairArmedAt = now;
			_vm.ArmRepair();

			// The label must not keep saying "Confirm repair" after the window has closed: a
			// one-shot timer disarms it. It fires on the session's TimeProvider, so a test
			// advances time instead of waiting, and it checks the arm is still THIS one.
			DateTimeOffset thisArm = now;
			_repairDisarmTimer?.Dispose();
			_repairDisarmTimer = _timeProvider.CreateTimer(
				_ => PostToUi(() =>
				{
					if (_repairArmedAt == thisArm && !_terminal)
					{
						_repairArmedAt = null;
						_vm?.DisarmRepair();
					}
				}),
				state: null,
				RepairConfirmWindow,
				Timeout.InfiniteTimeSpan);
			return;
		}

		_repairDisarmTimer?.Dispose();
		_repairDisarmTimer = null;

		_repairArmedAt = null;
		_vm.BeginRepair();
		_repairRun = RunRepairAsync(_vm);
	}

	// The stores the session was composed with; null when composed without one (tests, or a
	// session with no persistence), which then never counts as damaged.
	private readonly IRulesStore? _rulesStoreForRecheck;

	// What each file read as when the picker opened. A session without a store records
	// Missing, which is never damage. Used after exit 11 to tell "the repair moved a broken
	// file out and could not replace it" (damaged at open, Missing now) from "there was never
	// a file" (Missing at open, Missing now - normal: nothing saves rules yet).
	private readonly ConfigReadOutcome _historyOutcomeAtOpen = ConfigReadOutcome.Missing;
	private readonly ConfigReadOutcome _rulesOutcomeAtOpen = ConfigReadOutcome.Missing;

	private readonly record struct RepairFollowUp(ConfigReadOutcome Outcome, string? FileName);

	// The damaged file that most needs the user's attention, by severity of the remedy:
	// one the user must move (too large, damaged envelope) > still broken or gone after the
	// repair (error) > a newer or older BroChoos's file (left alone on purpose). Loaded means
	// nothing remains. Only the FILE NAME is surfaced, never the data-folder path (it contains
	// the account name).
	private RepairFollowUp DescribeRemainingDamage()
	{
		List<(ConfigReadOutcome Outcome, string FileName)> found = [];
		Consider(SafeOutcome(() => _activationHistoryStore.LoadWithOutcome().Outcome), _historyOutcomeAtOpen, "history.json");
		if (_rulesStoreForRecheck is not null)
		{
			Consider(SafeOutcome(() => _rulesStoreForRecheck.LoadWithOutcome().Outcome), _rulesOutcomeAtOpen, "rules.json");
		}

		(ConfigReadOutcome Outcome, string FileName)? worst = found
			.OrderBy(f => Severity(f.Outcome))
			.Cast<(ConfigReadOutcome, string)?>()
			.FirstOrDefault();

		return worst is { } w ? new RepairFollowUp(w.Outcome, w.FileName) : new RepairFollowUp(ConfigReadOutcome.Loaded, null);

		// Damaged NOW always counts. Missing or locked now counts ONLY for a file that was
		// damaged at open: that is the repair having moved it out without a replacement.
		void Consider(ConfigReadOutcome now, ConfigReadOutcome atOpen, string fileName)
		{
			bool damagedNow = ConfigReadOutcomes.IsDamaged(now);
			bool goneSinceDamaged = now is ConfigReadOutcome.Missing or ConfigReadOutcome.OpenFailed
				&& ConfigReadOutcomes.IsDamaged(atOpen);
			if (damagedNow || goneSinceDamaged)
			{
				found.Add((now, fileName));
			}
		}

		static int Severity(ConfigReadOutcome outcome)
		{
			return outcome switch
			{
				ConfigReadOutcome.TooLarge or ConfigReadOutcome.InvalidEnvelope => 0,
				ConfigReadOutcome.Corrupt or ConfigReadOutcome.Missing or ConfigReadOutcome.OpenFailed => 1,
				ConfigReadOutcome.Newer or ConfigReadOutcome.Incompatible => 2,
				ConfigReadOutcome.Loaded => 3,
				_ => 3,
			};
		}
	}

	// A store that throws (it should not) is reported as still broken, never as fixed.
	private static ConfigReadOutcome SafeOutcome(Func<ConfigReadOutcome> read)
	{
		try
		{
			return read();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			return ConfigReadOutcome.OpenFailed;
		}
	}

	private async Task RunRepairAsync(PickerViewModel vm)
	{
		(SettingsRepairResult result, string? detail) = await _safeModeLauncher
			.RunAsync(SafeModeActionIds.RepairSettings, _request.Activation.PortableDataPath, CancellationToken.None)
			.ConfigureAwait(true);

		if (_terminal)
		{
			return;
		}

		switch (result)
		{
			case SettingsRepairResult.Repaired:
				PickerLog.Marker("SETTINGS_REPAIRED");
				// Re-read history so this session's next record matches the repaired file's
				// generation (otherwise the CAS refuses it). Rules are NOT reloaded: that would
				// reorder the cards under the user mid-session.
				_activationHistoryState = _activationHistoryStore.LoadOrDefault();
				vm.ShowRepairSucceeded();
				break;
			case SettingsRepairResult.NotRepaired:
				// Exit 11 says only "not everything was fixed". The REASON comes from what is
				// on disk now, read with the same check the notice used at open.
				RepairFollowUp followUp = DescribeRemainingDamage();
				_logger.LogWarning("Settings repair left some files unusable ({ReasonCode}, {Outcome})", RepairIncompleteCode, followUp.Outcome);
				if (followUp.Outcome == ConfigReadOutcome.Loaded)
				{
					// Nothing is damaged any more (another writer fixed it meanwhile).
					_activationHistoryState = _activationHistoryStore.LoadOrDefault();
					vm.ShowRepairSucceeded();
				}
				else
				{
					vm.ShowRepairIncomplete(followUp.Outcome, followUp.FileName);
				}

				break;
			case SettingsRepairResult.Failed:
			default:
				_logger.LogWarning("Settings repair could not run ({ErrorCode}, {Step})", RepairFailedCode, detail ?? "none");
				vm.ShowRepairFailed();
				break;
		}
	}

	private Func<Task<string?>>? ReadClipboardForTests { get; set; }
	private Action<string>? WriteClipboardForTests { get; set; }

	// DEFECT #36: the ASYNC WRITE, behind one seam. Production hands this the real
	// `(url) => clipboard.SetTextAsync(url)` over the window's IClipboard; a test swaps it to
	// supply a write that never completes, which is the only way to exercise the REAL production
	// wait without a real window. It cannot piggyback on `WriteClipboardForTests` above, because
	// that seam is `Action<string>` - synchronous by construction, so it cannot express a write
	// that is still PENDING, which is exactly the state the defect is about. Null in production,
	// where the clipboard is resolved from the window as before.
	internal Func<string, Task>? WriteClipboardAsyncForTests { get; set; }

	// W1.5: the dispatcher-post seam for the drain. Null in production (the real Post runs); set
	// by a test to drive the InvalidOperationException branch the real Post raises when no
	// dispatcher loop can accept the job.
	internal Action<Action>? PostDrainForTests { get; set; }

	// W1.5 (N1): the POST MECHANISM behind a single seam. It defaults to the real
	// Dispatcher.UIThread.Post and production never touches it; a test swaps it to CAPTURE the
	// action the production path hands to the mechanism. Both production and test therefore run
	// the SAME call site (`DispatchPostForTests(DrainFullscreenQueue)`), which is what makes the
	// test able to detect a mutation that drops that call - a boolean flag set before the call
	// could not. Swapping the mechanism is necessary because driving the shared, thread-affine
	// Dispatcher from this test assembly breaks the window tests (MEASURED).
	internal Action<Action> DispatchPostForTests { get; set; } = static action => Dispatcher.UIThread.Post(action);

	private Action? GhostToastShownForTests { get; set; }
	private Action? GhostToastClosedForTests { get; set; }
	private Action<bool>? CleanupToastShownForTests { get; set; }
	private Action? CleanupToastClosedForTests { get; set; }
	private DateTime _lastGhostToastAt = DateTime.MinValue;
	private bool _skipHistoryForNextLaunch;
	internal string EffectiveUrlForTests => _vm?.Url ?? string.Empty;

	// W1.5 observables: the count and last reason code let a test drive the REAL
	// OnForwardedActivation and observe cap/overflow/redaction end to end. The named capacity
	// itself lives on the queue type (FullscreenSuppressionQueue.Capacity = 3, D-v3.5-150), so no
	// magic number is written in App code.
	internal int FullscreenQueueCountForTests => _fullscreenSuppressionQueue.Count;

	internal string? LastFullscreenReasonCodeForTests { get; private set; }

	// W1.5 DELIVERY observable: the exact sequence of URLs the drain handed to the view model, in
	// delivery order. The VM keeps only the latest URL, so this list is what proves FIFO order,
	// no loss and no duplicate delivery. It is appended on the real delivery path only - inside
	// DrainFullscreenQueue, immediately after the VM update - so a broken drain fails the tests
	// that read it. Session-scoped and in-memory, exactly like the queue (never persisted).
	private readonly List<string> _deliveredUrls = [];

	internal IReadOnlyList<string> DeliveredUrlsForTests => _deliveredUrls;

	// Runs the whole Avalonia lifetime; called once on the dedicated STA thread.
	// The returned code comes from desktop.Shutdown(code) in Complete().
	public int Run()
	{
		_uiThreadId = NativeMethods.GetCurrentThreadId();
		PickerLog.Marker($"SESSION_START dpi_awareness={DpiAwareness.DescribeCurrentThread()}");

		return AppBuilder
			.Configure(() => new PickerApp(this))
			.UsePlatformDetect()
			.StartWithClassicDesktopLifetime([]);
	}

	internal void AttachDesktop(IClassicDesktopStyleApplicationLifetime desktop)
	{
		_desktop = desktop;
	}

	internal void OpenWindow()
	{
		PickerLog.Marker($"OPEN_WINDOW_START snapshot_empty={_snapshot.IsEmpty} candidates={_snapshot.Candidates.Count} first_run_required={_request.FirstRunConsent.IsRequired}");
		ApplyCleanupAtSessionStart(writeAuditInBackground: true);
		_vm = new PickerViewModel(_cleanupState.EffectiveUrl, _snapshot, timeProvider: null, BuildRankingInputs(_cleanupState.EffectiveUrl));

		_vm.SetAddManuallyAvailable(_firstRunConsentActionCoordinator.CanAddManually);

		// Before the window is built, like the first-run prompt, so the measured pre-show
		// height already includes the notice row.
		if (_configNoticeRequired)
		{
			_vm.ShowConfigNotice();
		}

		// The prompt is shown BEFORE the window is built and measured, so the pre-show height
		// already includes the first-run panel and the window does not grow after it appears.
		if (IsFirstRunConsentPending)
		{
			_vm.ShowFirstRunPrompt();
		}

		_vm.IsVerticalLayout = PickerLayout == SettingsPickerLayout.Vertical;
		_window = new PickerWindow(_vm, OnKeyAction, OnCardClicked, OnFirstRunAction, OnExternalDismissal, OnRepairPressed)
		{
			IconLoader = IconLoader,
		};
		_window.Opened += OnWindowOpened;
		_window.Closed += OnWindowClosed;

		AssignPlacementBeforeShow(_window, _vm.Cards.Count);

		_router.ActivateSession(this);

		// Position was assigned above: the window never flashes at a default
		// position (spike finding D2).
		_window.Show();

		PickerLog.Marker($"FIRST_FRAME_REQUESTED cards={_vm.Cards.Count}");
		PickerLog.Marker($"VM cards={_vm.Cards.Count}");
	}

	// D-149: near cursor on the active monitor, falling back to the primary
	// work-area origin when the cursor query itself fails. Everything is computed
	// BEFORE Show (D2), in the TARGET monitor's physical pixels (D1: the target
	// monitor's DPI converts the fixed DIP size, never the window's own
	// RenderScaling - the exact swap that broke the spike's clamp).
	private void AssignPlacementBeforeShow(PickerWindow window, int cardCount)
	{
		// The window measured its own content in its constructor (before it is placed or
		// shown), so this is the REAL height rather than a card-count extrapolation. The
		// card count is still reported: it is what the placement log is read against.
		_ = cardCount;
		window.ApplyMeasuredHeight();

		CursorWorkArea workArea = new(0, 0, 1920, 1040);
		double scale = 1.0;
		int cursorX = 0;
		int cursorY = 0;

		if (NativeMethods.GetCursorPos(out NativeMethods.Point cursor))
		{
			cursorX = cursor.X;
			cursorY = cursor.Y;

			IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);

			NativeMethods.MonitorInfo info = new() { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
			if (NativeMethods.GetMonitorInfo(monitor, ref info))
			{
				workArea = new CursorWorkArea(
					info.rcWork.Left,
					info.rcWork.Top,
					info.rcWork.Right - info.rcWork.Left,
					info.rcWork.Bottom - info.rcWork.Top);
			}

			if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MonitorDpiType.EffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
			{
				scale = dpiX / 96.0;
			}
		}
		else
		{
			_logger.LogWarning("GetCursorPos failed; falling back to the primary work-area origin");
		}

		// Centred on the monitor under the cursor (picker layout spec); the cursor only picks the monitor.
		CursorPlacementResult placement = CursorPlacement.ComputeCentered(window.Width, window.Height, scale, workArea);

		// Icons are read at that monitor's device-pixel size, in the background; the window does
		// not wait for them.
		window.RequestIcons(Icons.BrowserIconLoader.PixelSizeFor(scale));

		window.Position = new PixelPoint(placement.X, placement.Y);

		PickerLog.Marker(
			$"PLACEMENT cursor=({cursorX},{cursorY}) workarea=({workArea.X},{workArea.Y},{workArea.Width},{workArea.Height}) scale={scale:0.00} window_dip=({window.Width:0.#},{window.Height:0.#}) window_px=({placement.WindowWidthPx},{placement.WindowHeightPx}) pos=({placement.X},{placement.Y})");
	}

	private void OnWindowOpened(object? sender, EventArgs e)
	{
		if (_window is null)
		{
			return;
		}

		IntPtr hwnd = _window.TryGetPlatformHandle()?.Handle ?? 0;
		PickerLog.Marker($"FIRST_FRAME_RENDERED hwnd=0x{hwnd:X} pos=({_window.Position.X},{_window.Position.Y}) visible={_window.IsVisible}");

		// A borderless window gets no initial logical focus target (spike finding):
		// focus card 1 explicitly so the focus indicator is present from the start
		// (D-139). Posted at Loaded priority so item containers are realized first.
		Dispatcher.UIThread.Post(
			() =>
			{
				if (_terminal || _vm is null)
				{
					return;
				}

				if (_vm.FocusSlot(1))
				{
					_window.FocusCard(_vm.FocusedIndex);
				}
				else if (_vm.IsFirstRunPromptVisible)
				{
					// No cards yet: the first-run screen's primary action takes the first focus,
					// with a visible ring, so the screen is usable from the keyboard at once.
					_window.FocusFirstRunPrimary();
				}
			},
			DispatcherPriority.Loaded);

		// The notice was in the first frame; now announce it once (a live region reads a TEXT
		// change). Posted after the card-1 focus above, so the screen reader hears the focused
		// card first and the polite notice after it.
		Dispatcher.UIThread.Post(() => _vm?.AnnounceConfigNotice(), DispatcherPriority.Background);

		PickerLog.Marker("INTERACTIVE_READY");
		_ = ResolveShortLinkInBackgroundAsync();
		_ = ReadClipboardAtOpenAsync();
		_ = ActivateForegroundAsync(hwnd);

		// Show ghost toast on successful open
		_ = ShowGhostToastAsync();
	}

	private async Task ActivateForegroundAsync(IntPtr hwnd)
	{
		// A5: no HWND yet (handle realization race): skip the ladder AND the flash
		// fallback - both would target handle 0, i.e. nothing. The window is already
		// topmost-visible, which is the real losing fallback.
		if (!ForegroundActivator.IsAttemptable(hwnd))
		{
			_logger.LogWarning("Window handle unavailable (hwnd=0); skipping the foreground ladder and the flash fallback");
			PickerLog.Marker("ACTIVATION_SKIPPED hwnd=0");
			return;
		}

		ForegroundActivator.Outcome outcome = await ForegroundActivator.ActivateAsync(hwnd, _uiThreadId, CancellationToken.None);

		PickerLog.Marker($"ACTIVATION verified={outcome.Verified} attach_needed={outcome.AttachNeeded} timed_out={outcome.TimedOut} ms={outcome.Milliseconds:F0} first_err={outcome.FirstAttemptWin32Error}");

		if (!outcome.Verified)
		{
			_logger.LogWarning("Foreground escalation could not be verified; the picker stays topmost-visible as the fallback");
			ForegroundActivator.FlashAsFallback(hwnd);
		}
	}

	// Pipe-server thread entry point: ALL state changes marshal to the UI thread.
	public void OnForwardedActivation(ActivationRequest request)
	{
		if (!TrySuppressForwardedActivation(request))
		{
			// Expired/duplicate: the activation was not held, so there is nothing to deliver.
			return;
		}

		// Deliver from the queue on the UI thread, FIFO. The activation is HELD until this runs,
		// so a synchronous burst really fills the queue and the fourth really drops the oldest -
		// the cap is reachable on the production path, which is the point. Delivery then takes the
		// OLDEST held activation, not whichever arrival won a race.
		//
		// The post is GUARDED, but NOT for the reason an earlier comment claimed. A forwarded URL
		// can arrive before the picker's dispatcher loop is running (the URL handler may be
		// activated at any moment), and the old comment asserted that
		// `Dispatcher.UIThread.Post` "throws when no loop can accept the job". That is FALSE for
		// Avalonia 11.3.18 (VERIFIED against the pinned sources): `Post` calls `InvokeAsyncImpl`,
		// which - when shutdown has finished or no processing can be requested - marks the
		// operation Aborted and returns WITHOUT throwing. The activation is still safely HELD on
		// the queue and drains later, so the silent no-op is the same fail-open outcome the guard
		// wanted.
		//
		// The catch is kept because the guard is real for the tests' own dispatcher seam
		// (PostDrainForTests can be pointed at a throwing handoff) and because the pipe-server
		// thread must never die on an unexpected dispatcher failure: it is the defensive net, not
		// a claim about Post. The failure is reported as a redacted marker, never the URL.
		try
		{
			PostDrainToDispatcher();
		}
		catch (InvalidOperationException)
		{
			// The reason code reaches the same observable the queue uses, so the dispatcher-
			// unavailable path is testable and never mistaken for "the activation was dropped".
			LastFullscreenReasonCodeForTests = "fullscreen.queue.dispatcher-unavailable";
			PickerLog.Marker($"FWD_URL_QUEUED count={_fullscreenSuppressionQueue.Count} reason=dispatcher-unavailable");
		}
	}

	// The dispatcher handoff. Two independent test seams, because they answer two different
	// questions and conflating them hid a gap (reviewer finding N1):
	//
	//   * PostDrainForTests REPLACES the handoff, so a test can run the drain inline (FIFO/no-loss/
	//     pre-window) or make it throw (the dispatcher-unavailable guard). It never touches the
	//     process-wide Dispatcher.
	//   * DispatchPostForTests is the POST MECHANISM, defaulting to the real
	//     Dispatcher.UIThread.Post. A test swaps it to CAPTURE the action the production path hands
	//     to the mechanism. Both production and test run the SAME call site below, so the test
	//     detects a mutation that removes that call.
	private void PostDrainToDispatcher()
	{
		if (PostDrainForTests is { } hook)
		{
			hook(DrainFullscreenQueue);
			return;
		}

		DispatchPostForTests(DrainFullscreenQueue);
	}

	// W1.5 (D-v3.5-150): the SUPPRESSION step - the exact production code that routes a forwarded
	// activation through the session's named fullscreen queue. It enforces the distinct cap of
	// three, transient expiry, duplicate rejection, and drop-oldest overflow, and reports a
	// REDACTED reason code only (the queue never returns the URL or a source path). Split out from
	// OnForwardedActivation so the queue contract is exercisable without the UI-thread delivery
	// handoff (which posts to the process-wide Dispatcher and is not part of the queue contract).
	// Returns true when the activation was HELD (and should be delivered).
	internal bool TrySuppressForwardedActivation(ActivationRequest request)
	{
		bool accepted = _fullscreenSuppressionQueue.TryEnqueue(request, DateTimeOffset.UtcNow, out string reasonCode);
		LastFullscreenReasonCodeForTests = reasonCode;

		// A marker that ONLY a wired queue can emit: the reason code is a fixed vocabulary, never
		// the URL. This is the observable that distinguishes "the queue ran" from "the queue was
		// never constructed", which was the whole gap.
		PickerLog.Marker(accepted
			? $"FWD_URL_QUEUED count={_fullscreenSuppressionQueue.Count} reason={DescribeFullscreenReason(reasonCode)}"
			: $"FWD_URL_SUPPRESSED reason={DescribeFullscreenReason(reasonCode)}");

		return accepted;
	}

	private void DrainFullscreenQueue()
	{
		if (_terminal || _vm is null)
		{
			// The picker is done: the held activations are moot (and the queue is session-scoped,
			// so it is discarded with the session - never persisted across restart).
			if (_fullscreenSuppressionQueue.Count > 0)
			{
				_logger.LogWarning("Forwarded URLs arrived after the picker was done; they were not acted on");
				PickerLog.Marker("FWD_URL_IGNORED terminal");
			}

			return;
		}

		// A launch is in flight for the CURRENT link: changing the link under it would make the
		// outcome (and history, and the "Opening in" line) describe a link that was not launched.
		// The forward stays HELD in the queue; LaunchCardAsync drains it once the outcome is known.
		if (IsLaunchInFlightForTests)
		{
			PickerLog.Marker($"FWD_URL_HELD launch-in-flight count={_fullscreenSuppressionQueue.Count}");
			return;
		}

		while (_fullscreenSuppressionQueue.TryDequeue(out ActivationRequest? request) && request is not null)
		{
			// R3-4/R4-4 seam: fires AFTER the dequeue, so a test can model a forward that was taken
			// off the queue but never applied.
			DrainHeldForwardsForTests?.Invoke();

			// Every per-link launch input belongs to the PREVIOUS link: recompute cleanup for the new
			// one, drop the old short-link resolution and the armed use-original one-shot, and
			// re-rank the cards against the new link (LaunchCard prefers these values over _vm.Url).
			// The generation bump makes an in-flight short-link resolution for the previous link
			// drop its result.
			_linkGeneration++;
			_cleanupState = TrackingCleanupEngine.Cleanup(request.NormalizedUrl, _trackingCleanupOptions);
			_resolvedShortLinkUrl = null;
			_useOriginalArmedForNextLaunch = false;

			// A pending "press again to clear history" belonged to the screen the user was
			// looking at; a new link replaces it, so the arm is cancelled.
			_clearHistoryArmedAt = null;
			// Ctrl+H (skip history) deliberately survives a forward: privacy-safe, at worst one launch goes unrecorded.
			_vm.UpdateUrl(_cleanupState.EffectiveUrl);
			_vm.ApplySnapshot(_vm.Snapshot, BuildRankingInputs(_cleanupState.EffectiveUrl));
			_deliveredUrls.Add(request.NormalizedUrl);
			PickerLog.Marker($"FWD_URL_RECEIVED count={_deliveredUrls.Count}");

			RequestForegroundUnlessUserIsBusy();
		}
	}

	// FULLSCREEN POLICY. A forwarded link always UPDATES the picker (the link card shows the new
	// link at once), but it does not pull the picker to the foreground while the user is in an
	// exclusive fullscreen game or Windows presentation mode (UserNotificationState): stealing
	// keyboard focus there interrupts the game or the talk. The foreground request is held,
	// re-checked every FullscreenPollInterval, and made as soon as that ends - or after
	// FullscreenMaxHold at the latest, so a stuck or misreported state can never hold a
	// forwarded link forever. Only one wait runs at a time, and it ends with the session.
	private void RequestForegroundUnlessUserIsBusy()
	{
		if (!IsUserBusyForTests())
		{
			RequestForeground();
			return;
		}

		if (_foregroundDeferred)
		{
			return;
		}

		_foregroundDeferred = true;
		ForegroundDeferredCountForTests++;
		PickerLog.Marker("FWD_URL_FOREGROUND_DEFERRED user-busy");
		_ = WaitForUserNotBusyThenForegroundAsync();
	}

	private async Task WaitForUserNotBusyThenForegroundAsync()
	{
		// The wait runs off the UI thread (ConfigureAwait(false)); it only reads _terminal and
		// the busy seam. Every state change - clearing _foregroundDeferred, the foreground
		// request - is posted back to the UI thread through the same dispatcher seam.
		string reason = "ended";
		try
		{
			TimeSpan held = TimeSpan.Zero;
			while (!_terminal)
			{
				if (held >= FullscreenMaxHold)
				{
					reason = "max-hold";
					break;
				}

				await DelayForTests(FullscreenPollInterval).ConfigureAwait(false);
				held += FullscreenPollInterval;
				if (!IsUserBusyForTests())
				{
					break;
				}
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// A failed wait must not strand the link in the background: fall through and come
			// forward now, the same thing the picker did before this policy existed.
			LogSwallowed("FWD_URL_FOREGROUND_WAIT_FAILED", ex);
			reason = "wait-failed";
		}

		DispatchPostForTests(() =>
		{
			_foregroundDeferred = false;
			if (_terminal)
			{
				return;
			}

			PickerLog.Marker($"FWD_URL_FOREGROUND_RESUMED reason={reason}");
			RequestForeground();
		});
	}

	// Brings the picker back in front through the existing foreground ladder. Counted before the
	// HWND lookup so session tests (no window) can observe the request.
	private void RequestForeground()
	{
		ForegroundRequestCountForTests++;
		IntPtr hwnd = _window?.TryGetPlatformHandle()?.Handle ?? 0;
		if (hwnd != 0)
		{
			_ = ActivateForegroundAsync(hwnd);
		}
	}

	private static string DescribeFullscreenReason(string reasonCode)
	{
		return string.IsNullOrEmpty(reasonCode) ? "accepted" : reasonCode;
	}

	private void OnWindowClosed(object? sender, EventArgs e)
	{
		if (_ignoreWindowClosedOnce)
		{
			_ignoreWindowClosedOnce = false;
			return;
		}

		// Alt+F4 / OS close between actions = the cancel path (exit 0): user
		// cancellation is not a failure.
		if (!_terminal)
		{
			PickerLog.Marker("WINDOW_CLOSED_BY_OS -> cancel");
			Complete(ExitCodes.Success);
		}
	}

	// ---- key actions (D-130) ---------------------------------------------------

	private void OnKeyAction(PickerKeyAction action)
	{
		if (_terminal || _vm is null || _window is null)
		{
			return;
		}

		BeforeKeyAction(action);

		switch (action.Kind)
		{
			case PickerKey.Digit:
				PickerLog.Marker($"KEY D{action.Slot}");
				if (_vm.FocusSlot(action.Slot))
				{
					_window.FocusCard(_vm.FocusedIndex);
				}
				else
				{
					PickerLog.Marker($"DIGIT_IGNORED slot={action.Slot} cards={_vm.Cards.Count}");
				}

				break;

			case PickerKey.FocusNext:
			case PickerKey.FocusPrevious:
				MoveFocusBy(action.Kind == PickerKey.FocusNext ? 1 : -1);
				break;

			case PickerKey.Enter:
				ActivateFocused();
				break;

			case PickerKey.Escape:
				HandleEscape();
				break;

			case PickerKey.SystemFallback:
				LaunchSystemFallback();
				break;

			case PickerKey.Copy:
				_ = CopyUrlAsync();
				break;

			case PickerKey.UseOriginal:
				ArmUseOriginalForNextLaunch();
				break;

			case PickerKey.SkipHistoryOnce:
				ArmSkipHistoryForNextLaunch();
				break;

			case PickerKey.ClearHistoryConfirmed:
				HandleClearHistoryKey();
				break;

			case PickerKey.ToggleShortcuts:
				_vm.ToggleDetails();
				break;

			default:
				break;
		}
	}

	// Runs before every picker key, on the real path and in the test driver alike: any key other
	// than Ctrl+Backspace cancels a pending "press again to clear history", and ANY picker key
	// cancels an armed "Repair settings" (the confirm press is a button press, not a hotkey).
	private void BeforeKeyAction(PickerKeyAction action)
	{
		if (action.Kind != PickerKey.ClearHistoryConfirmed)
		{
			_clearHistoryArmedAt = null;
		}

		if (_repairArmedAt is not null)
		{
			_repairArmedAt = null;
			_vm?.DisarmRepair();
		}
	}

	private void MoveFocusBy(int delta)
	{
		PickerLog.Marker($"KEY ARROW delta={delta}");
		if (_vm!.MoveFocus(delta))
		{
			_window!.FocusCard(_vm.FocusedIndex);
		}
	}

	// `async void` is the correct shape for a synchronous UI event handler that must await
	// before its follow-up work: the picker only receives this from the key path, and the
	// awaited write must land BEFORE Complete(Success) shuts the session down (see
	// TryCancelToClipboardOnDismissAsync). Any failure is contained inside the awaited method,
	// so nothing can escape here.
	//
	// DEFECT #36-c: the dismiss branch claims the synchronous dismissal guard BEFORE it awaits.
	// Without it, a repeat Escape arriving while the first clipboard write is still pending
	// (i.e. before Complete has set `_terminal`) re-entered here and wrote the clipboard twice.
	private async void HandleEscape()
	{
		EscapeHandlingResult result = _vm!.HandleEscape();
		if (result == EscapeHandlingResult.CloseDetails)
		{
			PickerLog.Marker("KEY ESCAPE -> close details");
			return;
		}

		if (!TryClaimDismissal())
		{
			return;
		}

		PickerLog.Marker("KEY ESCAPE -> dismiss picker");
		await TryCancelToClipboardOnDismissAsync().ConfigureAwait(true);
		Complete(ExitCodes.Success);
	}

	// W5.2 DISMISSAL CONTRACT (spec 3.3): the OS-level dismissals - click-outside on another
	// window/the desktop, Alt+Tab, Win+D, and (as far as the platform reports it) sleep/lock -
	// all reach here through the window's Deactivated event (PickerWindow.OnWindowDeactivated).
	//
	// THE SAME CANCELLATION PATH AS THE SECOND ESC, deliberately: this is a user cancel, so it
	// runs cancel-to-clipboard and exits Success, exactly like HandleEscape's dismiss branch. It
	// does NOT call the view model's HandleEscape first, because there is no visible details step
	// for the user to consume when the window itself loses activation - an external dismissal
	// closes the WHOLE picker in one event (pinned by
	// PickerDismissalContractTests.Session_ExternalDismissal_IsSingleStageUnlikeEsc).
	//
	// IDEMPOTENT BY THE SYNCHRONOUS DISMISSAL GUARD (DEFECT #36-c). A real deactivate can be
	// delivered more than once, and a dismissal after a launch already completed must do
	// nothing. The `_terminal` pre-check below still covers the second case (a dead session),
	// but it CANNOT cover the first: `_terminal` is set only inside Complete, which runs only
	// AFTER the awaited clipboard write, so a duplicate Deactivated arriving while that write
	// is still pending used to slip past this check and run the cancel-to-clipboard write a
	// SECOND time. `TryClaimDismissal` claims a flag SYNCHRONOUSLY, before any await, so the
	// first dismissal owns the side effect and the duplicate is a no-op - no second write, no
	// second toast. The claim happens here, at the top, where the check that used to be the
	// only guard also sits.
	//
	// `async void`, like HandleEscape: the window's Deactivated handler calls this through an
	// `Action`, and the awaited write must complete BEFORE Complete(Success) shuts the session
	// down (a POSTED write would be aborted by that shutdown - see
	// TryCancelToClipboardOnDismissAsync). Complete() is itself idempotent, so even without the
	// claim a second dismissal completing could not double-complete; the claim is what keeps
	// the WRITE from running twice.
	private async void OnExternalDismissal()
	{
		if (_terminal || _vm is null)
		{
			return;
		}

		// The browser we just launched usually takes focus before the helper reports back.
		// That is not a cancel: no clipboard copy and no terminal transition here.
		if (IsLaunchInFlightForTests)
		{
			_deactivatedDuringLaunch = true;
			PickerLog.Marker("WINDOW_DEACTIVATED during-launch -> deferred");
			return;
		}

		if (!TryClaimDismissal())
		{
			return;
		}

		PickerLog.Marker("WINDOW_DEACTIVATED -> dismiss picker");
		await TryCancelToClipboardOnDismissAsync().ConfigureAwait(true);
		Complete(ExitCodes.Success);
	}

	// The synchronous one-shot claim shared by BOTH dismissal entry points (the Escape dismiss
	// branch above and OnExternalDismissal). It returns true exactly once per session, at the
	// moment the caller is about to start the dismissal side effects, and it must be called
	// BEFORE the first await - that is the whole point: it is the only guard that exists while
	// the clipboard write is still pending, because `_terminal` does not exist yet.
	//
	// Not thread-safe on purpose, and it does not need to be: both callers run on the UI thread
	// (the key path directly; the window's Deactivated handler from the same dispatcher), which
	// is the same thread affinity the rest of this class relies on.
	private bool TryClaimDismissal()
	{
		if (_dismissalInProgress)
		{
			return false;
		}

		_dismissalInProgress = true;
		return true;
	}

	// D-131: Enter acts ONLY on the visibly focused card; focus nowhere means the
	// key is consumed without doing anything, never an invisible default.
	private void ActivateFocused()
	{
		if (!_vm!.CanActivateFocusedCard())
		{
			EnterIgnoredBeforeSafeCountForTests++;
			PickerLog.Marker("ENTER_IGNORED focus-not-safe-yet");
			return;
		}

		// N3: the click path (OnCardClicked) refuses to launch a card while the first-run
		// consent contract requires consent - it re-shows the prompt instead. The keyboard
		// path reaches LaunchCard through here, so without this same guard Enter bypassed
		// the consent gate the mouse honours: one activation route consented, the other did
		// not. Placed BEFORE the target lookup and the launch so no activation work happens.
		// The gate reads the SESSION's consent state, not the immutable request: a successful
		// first-run action (search or manual add) satisfies it for the rest of the run.
		if (IsFirstRunConsentPending)
		{
			PickerLog.Marker("FIRST_RUN_GUARD blocked card launch before consent");
			_vm.ShowFirstRunPrompt();
			return;
		}

		ChoiceCardViewModel? target = _vm!.GetFocusedActivationTarget();
		if (target is null)
		{
			PickerLog.Marker("ENTER_IGNORED no-focused-card");
			return;
		}

		PickerLog.Marker($"KEY ENTER -> launch slot={target.Slot} target={target.BrowserId}");
		LaunchCard(target);
	}

	private void OnCardClicked(ChoiceCardViewModel card)
	{
		if (_terminal || _vm is null)
		{
			return;
		}

		if (IsFirstRunConsentPending)
		{
			PickerLog.Marker("FIRST_RUN_GUARD blocked card launch before consent");
			_vm.ShowFirstRunPrompt();
			return;
		}

		PickerLog.Marker($"CARD_CLICK slot={card.Slot} target={card.BrowserId}");
		LaunchCard(card);
	}

	// ---- actions ---------------------------------------------------------------

	// The ONLY launch surface: the existing IBrowserLauncher (0A-2), with its
	// recursion guard, structured argv and LaunchResult reporting. Fail-open on
	// failure: the window stays up with a visible state instead of crashing.
	private void LaunchCard(ChoiceCardViewModel card)
	{
		// RE-ENTRANCY GUARD, and it is a consequence of the H1 fix rather than an extra
		// feature. While the helper call blocked the UI thread, a second click was physically
		// impossible: the thread was inside the first call and could not dispatch another
		// key or pointer event. Handing the wait off restores input, so without this the
		// window would accept a second click during the in-flight launch and open a SECOND
		// browser - a behaviour change the fix must not introduce.
		//
		// It covers the IN-FLIGHT window only. Once the outcome has been handled (success,
		// or the Unknown/Failed recovery state) the flag is released, so the documented
		// "second explicit Enter retries after Unknown" contract still holds - pinned by
		// PickerContractTests.Session_UnknownLaunch_RetriesOnlyOnSecondExplicitEnter.
		if (IsLaunchInFlightForTests)
		{
			PickerLog.Marker($"LAUNCH_IGNORED already-in-flight target={card.BrowserId}");
			return;
		}

		IsLaunchInFlightForTests = true;

		PickerLog.Marker($"LAUNCH_REQUESTED target={card.BrowserId}");

		string baseUrl = _resolvedShortLinkUrl ?? _vm!.Url;

		string launchUrl = _useOriginalArmedForNextLaunch && _cleanupState.CanUseOriginalForThisLaunch
			? _cleanupState.OriginalUrl
			: baseUrl;

		if (_useOriginalArmedForNextLaunch && _cleanupState.CanUseOriginalForThisLaunch)
		{
			PickerLog.Marker("CLEANUP_USE_ORIGINAL_ONESHOT consumed=true");
		}

		_useOriginalArmedForNextLaunch = false;

		LaunchHelperRequest helperRequest = new(
			card.BrowserId,
			card.Candidate.ExePath,
			launchUrl,
			IsPrivate: false,
			ProfileId: card.LaunchProfileId);

		// H1: the helper call used to be awaited HERE, on the STA UI thread
		// (.GetAwaiter().GetResult()), and the launch helper carries a 5 s budget - so one
		// click could freeze the window for seconds, and a hung helper froze it forever. The
		// wait is handed off instead: the async method resumes on the UI thread (Avalonia's
		// dispatcher installs an AvaloniaSynchronizationContext for the whole desktop
		// lifetime, MEASURED), so every state change below still happens where it did, while
		// the window keeps processing input.
		LastLaunchTaskForTests = LaunchCardAsync(card, launchUrl, helperRequest);
	}

	// The task of the most recent card launch, so a test can wait for the REAL outcome handling to
	// finish instead of polling (and can see an exception that production would never observe,
	// because the launch is fire-and-forget).
	internal Task? LastLaunchTaskForTests { get; private set; }

	// The launch itself, off the caller's critical path. `async Task` rather than `async void`
	// so the failure contract is explicit: nothing escapes, because the client's own contract
	// is a typed outcome and the catch below is the backstop for a seam that breaks it.
	private async Task LaunchCardAsync(ChoiceCardViewModel card, string launchUrl, LaunchHelperRequest helperRequest)
	{
		LaunchHelperOutcome helperOutcome;
		try
		{
			helperOutcome = await _launchHelperClient.LaunchAsync(helperRequest, CancellationToken.None);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_logger.LogError(ex, "Picker launch threw for {BrowserId}", card.BrowserId);
			PickerLog.Marker($"LAUNCH_THREW target={card.BrowserId}");
			helperOutcome = new LaunchHelperOutcome(LaunchStatus.Failed, card.BrowserId, "launch.internal-error", "internal", "launch.error.failed");
		}

		// History records only a launch that actually succeeded: a failed, timed-out or unknown
		// outcome creates no entry and leaves the skip-next-link one-shot armed for the retry.
		// It runs before the window-closed check below because a launch that succeeded while the
		// window was being dismissed did open the browser, so it still belongs in the history.
		//
		// FAIL-OPEN. The browser is already open here, and this method runs as a fire-and-forget
		// task. A store that throws (the coordinator raises IOException on a lock timeout,
		// UnauthorizedAccessException on permissions) would fault the task before the in-flight flag
		// is released and before the session completes: the launch worked, yet the picker would stay
		// open and refuse every further launch. History is a convenience, so a failed write is
		// logged by type and the launch carries on.
		if (helperOutcome.Status == LaunchStatus.Success)
		{
			try
			{
				RecordActivationHistory(launchUrl, card);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				LogSwallowed("ACTIVATION_HISTORY_WRITE_FAILED", ex);
			}
		}

		// The window may have closed (Escape / OS close) while the helper was running: the
		// outcome is then moot and touching the VM would act on a dead session.
		PickerViewModel? vm = _vm;
		if (_terminal || vm is null)
		{
			return;
		}

		// The in-flight window is over, so the recovery states below can be acted on. On
		// Success the session completes anyway; on Failed the user may pick another card.
		IsLaunchInFlightForTests = false;
		bool deactivatedDuringLaunch = _deactivatedDuringLaunch;
		_deactivatedDuringLaunch = false;

		LaunchResult result = new(
			helperOutcome.Status,
			helperOutcome.TargetId,
			helperOutcome.ErrorCode,
			helperOutcome.FailureCategory,
			helperOutcome.UserVisibleMessageKey);

		if (result.Status == LaunchStatus.Success)
		{
			PickerLog.Marker($"LAUNCH_OK target={card.BrowserId} deactivated_during_launch={deactivatedDuringLaunch}");
			Console.WriteLine($"Opening in {card.DisplayName}: {launchUrl}");

			if (_logger.IsEnabled(LogLevel.Information))
			{
				_logger.LogInformation("Picker launched {BrowserId}", card.BrowserId);
			}

			CompleteOrKeepOpenForHeldForwards();
			return;
		}

		// The launch did not succeed, so the picker stays open. If the launched browser took focus
		// meanwhile (deferred deactivation), the picker has no focus: its keys do not work and an
		// outside click raises no new Deactivated. Bring it back.
		if (deactivatedDuringLaunch)
		{
			RequestForeground();
		}

		// R3-1: held forwards are NOT applied here. Draining would replace link A with link B, so
		// "Try again" would launch B. A stays; the forwards stay held (the next drain - a later
		// forward or the next launch outcome - applies them). The note is appended to the status
		// line AFTER the failure is painted (NoteHeldForwards, called at the end of each branch).
		if (result.Status is LaunchStatus.Unknown or LaunchStatus.Timeout)
		{
			PickerLog.Marker($"LAUNCH_UNKNOWN target={card.BrowserId} status={result.Status} code={result.ErrorCode}");
			_logger.LogWarning("Picker launch unknown for {BrowserId} ({ErrorCode})", card.BrowserId, result.ErrorCode);

			// FINDING F1-b: the status line promises "Try again (Enter), Copy link (Ctrl+C), or
			// Cancel (Esc)", and Enter only fires on a card whose focus is VISIBLE (D-131). The
			// click that led here was a pointer press, so the clicked card holds focus with no
			// ring and the claim was correctly dropped (B/N2) - the retry would be a lie unless
			// the card is re-asserted. The slot is passed with the status because the VM's own
			// index cannot supply it: the pointer path keeps the intent and drops only the proof,
			// so it may still name a different card.
			vm.ShowLaunchUnknownRecovery(card.DisplayName, card.Slot - 1);
			NoteHeldForwards(vm);
			return;
		}

		PickerLog.Marker($"LAUNCH_FAILED target={card.BrowserId} code={result.ErrorCode} deactivated_during_launch={deactivatedDuringLaunch}");
		_logger.LogError("Picker launch failed for {BrowserId} ({ErrorCode})", card.BrowserId, result.ErrorCode);
		vm.ShowLaunchFailure(card.DisplayName, result.ErrorCode);
		NoteHeldForwards(vm);
	}

	private void NoteHeldForwards(PickerViewModel vm)
	{
		if (_fullscreenSuppressionQueue.Count > 0)
		{
			PickerLog.Marker($"FWD_URL_HELD after-failed-launch count={_fullscreenSuppressionQueue.Count}");
			vm.ShowForwardWaiting();
		}
	}

	// The ONE success ending, shared by a card launch and the F fallback (R4-2). A link forwarded
	// DURING a launch (or while a failed launch was on screen) is still held. Completing would shut
	// the picker down and lose it silently (Complete ends the whole desktop lifetime, and the
	// router cannot start a second picker), so the picker stays open for the held link instead.
	private void CompleteOrKeepOpenForHeldForwards()
	{
		int held = _fullscreenSuppressionQueue.Count;
		if (held == 0)
		{
			Complete(ExitCodes.Success);
			return;
		}

		PickerLog.Marker($"LAUNCH_OK_KEEP_OPEN held_forwards={held}");
		if (held > 1)
		{
			// R3-3: the view model shows one link; the earlier held ones are superseded.
			PickerLog.Marker($"FWD_URL_COLLAPSED superseded={held - 1}");
		}

		int deliveredBefore = _deliveredUrls.Count;

		// R3-4: on the card path this runs inside a fire-and-forget task, so an exception here
		// would never be observed. Contain it: log the type only, and complete. R4-4: forwards
		// dequeued but not applied are counted in the loss log, not only those still queued.
		try
		{
			DrainFullscreenQueue();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			LogSwallowed("FWD_URL_DRAIN_FAILED", ex);
			int lost = held - (_deliveredUrls.Count - deliveredBefore);
			PickerLog.Marker($"FWD_URL_LOST_ON_DRAIN_FAILURE count={lost}");
			_logger.LogWarning("{Count} held forwarded links were not opened because applying them failed", lost);
			Complete(ExitCodes.Success);
			return;
		}

		if (_deliveredUrls.Count > deliveredBefore && !_terminal)
		{
			return;
		}

		Complete(ExitCodes.Success);
	}

	// W7.2: turn the run's URL and the loaded rules into the ranking inputs. Data only —
	// this evaluates rules and returns a decision; it launches nothing and grants nothing.
	//
	// SOURCE AND SESSION SIGNALS ARE DELIBERATELY ABSENT HERE. Probable-source ordering
	// needs a source-app→browser mapping, and the recent/session reason needs an allowed
	// history reader; neither contract exists in this slice (W7.5 owns the history side and
	// the source mapping is spike-gated by D-v3.5-376). Passing Unknown/null is the honest
	// state: the engine supports both signals and is tested for them, but the product does
	// not yet have a truthful value to give, and inventing one would be a hidden signal.
	private PickerRankingInputs BuildRankingInputs(string url)
	{
		RoutingTarget? target = RoutingTargetFactory.FromUrl(url);
		RoutingDecision decision = target is null
			? new RoutingDecision(_ruleSet.Generation, null)
			: RuleEvaluator.Evaluate(target, _ruleSet);

		return new PickerRankingInputs(
			decision,
			_request.Activation.SourceEvidence.Confidence,
			ProbablePreferredBrowserId: null,
			RecentSessionBrowserIds: null);
	}

	private void OnFirstRunAction(FirstRunAction action)
	{
		if (_terminal || _vm is null)
		{
			return;
		}

		// One action at a time: a second click while the search runs would dispatch a
		// second discovery.
		if (_vm.IsFirstRunBusy)
		{
			PickerLog.Marker($"FIRST_RUN_ACTION_IGNORED busy action={action}");
			return;
		}

		// The button is hidden when no file picker is composed; refuse the action too, so no
		// other route (automation Invoke on a stale peer) reaches the dead end.
		if (action == FirstRunAction.AddManually && !_vm.IsAddManuallyAvailable)
		{
			PickerLog.Marker("FIRST_RUN_ACTION_IGNORED add-manually-unavailable");
			return;
		}

		// H1: this used to await the storage helper HERE, on the STA UI thread, and the
		// storage helper was the one helper with NO invocation budget - so the first-run scan
		// could freeze the window indefinitely. The wait is handed off; the coordinator's
		// continuation resumes on the UI thread (MEASURED), so the VM updates below still land
		// where they did.
		_ = HandleFirstRunActionAsync(action);
	}

	private async Task HandleFirstRunActionAsync(FirstRunAction action)
	{
		PickerViewModel? vm = _vm;
		if (vm is null)
		{
			return;
		}

		StorageDiscoveryResponse? response;
		bool searching = action == FirstRunAction.FindBrowsers;
		if (searching)
		{
			vm.SetFirstRunBusy(true);
		}

		try
		{
			response = await _firstRunConsentActionCoordinator.HandleAsync(
				action,
				vm,
				_request.Activation.PortableDataPath,
				CancellationToken.None);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// The coordinator reports failures through the VM, so reaching here means a seam
			// broke its contract. Fail open: the window stays up, the failure is visible.
			_logger.LogError(ex, "First-run action {Action} threw", action);
			PickerLog.Marker($"FIRST_RUN_ACTION_THREW action={action}");
			vm.ShowActionFailure(LocalizationService.GetString("Status.Target.AvailableBrowsers"), "consent.discovery.failed");
			return;
		}
		finally
		{
			if (searching)
			{
				vm.SetFirstRunBusy(false);
			}
		}

		if (_terminal)
		{
			return;
		}

		if (response?.Snapshot is { } snapshot)
		{
			_snapshot = snapshot;
		}

		// CONSENT IS SATISFIED BY OUTCOME, NOT BY THE REQUEST. The request's FirstRunConsent is
		// immutable and stays Required for the whole run, so reading it in the activation guards
		// kept every freshly discovered card blocked after a successful search: the click
		// re-showed the prompt instead of launching. The prompt being cleared is the coordinator's
		// statement that the user acted and real cards now exist (a successful search with
		// results, or an admitted manual add); a failed search, an empty result, a cancelled
		// dialog and Skip all leave it up, so consent stays pending exactly in those cases.
		if (!vm.IsFirstRunPromptVisible && vm.Cards.Count > 0)
		{
			IsFirstRunConsentSatisfiedForTests = true;
			PickerLog.Marker($"FIRST_RUN_CONSENT_SATISFIED action={action} cards={vm.Cards.Count}");
		}
	}

	// The live first-run gate: required by the request AND not yet satisfied in this session.
	private bool IsFirstRunConsentPending => _request.FirstRunConsent.IsRequired && !IsFirstRunConsentSatisfiedForTests;

	private void LaunchSystemFallback()
	{
		// Same guard as LaunchCard: F during an in-flight card launch would open a second browser.
		if (IsLaunchInFlightForTests)
		{
			PickerLog.Marker("FALLBACK_IGNORED launch-in-flight");
			return;
		}

		PickerLog.Marker("KEY F -> system fallback");

		LaunchResult result = _fallback.Launch(_vm!.Url);

		if (result.Status == LaunchStatus.Success)
		{
			PickerLog.Marker("FALLBACK_OK");
			Console.WriteLine($"Opening with the system fallback: {_vm!.Url}");
			_logger.LogInformation("Picker launched the system fallback");
			CompleteOrKeepOpenForHeldForwards();
			return;
		}

		PickerLog.Marker($"FALLBACK_FAILED code={result.ErrorCode}");
		_logger.LogError("System fallback launch failed ({ErrorCode})", result.ErrorCode);
		_vm!.ShowLaunchFailure(LocalizationService.GetString("Status.Target.SystemFallback"), result.ErrorCode);
	}

	// Ctrl+C: WRITE the URL to the clipboard. This is one of the product's THREE clipboard
	// touchpoints, and the honest picture is that the product BOTH reads and writes:
	//
	//   - it READS the clipboard once per open (ReadClipboardAtOpenAsync, called from
	//     OnWindowOpened) to recover a URL the user copied, ON by default and disableable via
	//     PickerClipboardOptions.ClipboardReadAtOpenEnabled;
	//   - it WRITES on Ctrl+C (here) and on the cancel path (TryCancelToClipboardOnDismiss),
	//     the latter ON by default and disableable via CancelToClipboardEnabled.
	//
	// The value never leaves the picker: it is held in memory for the session only, is erased on
	// Complete, and is never written to logs, configuration, IPC, or history - which is exactly
	// what the settings copy says ("Clipboard URL read is local and transient: the value is held
	// in memory for one session only"). The earlier comment claiming the product "never reads the
	// user's clipboard" was false: it contradicted the read-at-open call above. The known residual
	// risk (spike item 14: a write can report success while the clipboard ends up empty) still
	// cannot be detected reliably, so the user gets the in-window confirmation plus the marker,
	// and the risk is documented.
	private async Task CopyUrlAsync()
	{
		if (!TryGetClipboard(out IClipboard? clipboard) && WriteClipboardForTests is null)
		{
			PickerLog.Marker("COPY_FAILED reason=no-clipboard-provider");
			_logger.LogWarning("Clipboard provider unavailable; copy failed");
			_vm!.ShowCopyFailed();
			return;
		}

		PickerLog.Marker("KEY CTRL+C -> clipboard write");

		try
		{
			if (WriteClipboardForTests is not null)
			{
				WriteClipboardForTests(_vm!.Url);
				LastClipboardWriteForTests = _vm.Url;
			}
			else
			{
				await clipboard!.SetTextAsync(_vm!.Url);
			}

			_vm!.ShowCopied();
			PickerLog.Marker("COPY clipboard-write ok");
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_logger.LogWarning(ex, "Clipboard write failed");
			_vm!.ShowCopyFailed();
			PickerLog.Marker("COPY_FAILED reason=write-failed");
		}
	}

	// Upper bound Complete() waits for the session-start audit write (see ApplyCleanupAtSessionStart).
	internal static readonly TimeSpan CleanupAuditDrainTimeout = TimeSpan.FromSeconds(1);

	// The bound THIS session's Complete() uses. Production never sets it, so it is always the 1 s
	// product value; a test that checks only THAT Complete waits raises it so machine load cannot
	// make the wait miss its window.
	internal TimeSpan CleanupAuditDrainTimeoutForTests { get; set; } = CleanupAuditDrainTimeout;

	// The session-start audit write when it runs in the background (null on the synchronous path).
	// Read by tests through this property; Complete() waits for it, bounded.
	internal Task? PendingCleanupAuditForTests { get; private set; }

	// Terminal transitions are idempotent: first one wins.
	private void Complete(int exitCode)
	{
		if (_terminal)
		{
			return;
		}

		_terminal = true;
		CompleteCountForTests++;
		_repairDisarmTimer?.Dispose();
		_repairDisarmTimer = null;
		_router.DeactivateSession(this);

		// R3-2: links forwarded during a launch and still held when the session ends (Esc, close)
		// are not opened. Not redesigned here; recorded without the URL.
		if (_fullscreenSuppressionQueue.Count > 0)
		{
			PickerLog.Marker($"FWD_URL_LOST_ON_COMPLETE count={_fullscreenSuppressionQueue.Count}");
			_logger.LogWarning("{Count} held forwarded links were not opened because the picker closed", _fullscreenSuppressionQueue.Count);
		}

		PickerLog.Marker($"WINDOW_CLOSED exit={exitCode}");

		SettleSkipHistoryForAnUnfinishedLaunch();

		// Before Shutdown: the process may exit right after it, taking a pending audit write along.
		WaitForCleanupAuditBounded();

		_ignoreWindowClosedOnce = true;
		_testExitCode = exitCode;
		ClipboardReadAtOpenValueForTests = null;
		_window?.Close();
		_desktop?.Shutdown(exitCode);
	}

	private async Task ReadClipboardAtOpenAsync()
	{
		if (!ClipboardReadAtOpenEnabled || ClipboardReadOnceForTests)
		{
			return;
		}

		ClipboardReadOnceForTests = true;

		try
		{
			string? text;
			if (ReadClipboardForTests is not null)
			{
				text = await ReadClipboardForTests();
			}
			else if (TryGetClipboard(out IClipboard? clipboard))
			{
				text = await clipboard!.TryGetTextAsync();
			}
			else
			{
				return;
			}

			// Prevent repopulation after completion (async race fix)
			if (_terminal)
			{
				return;
			}

			if (Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
				&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
			{
				ClipboardReadAtOpenValueForTests = text;
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// Clipboard-read is best-effort only in W5.3.
			LogSwallowed("CLIPBOARD_READ_FAILED", ex);
		}
	}

	private async Task ResolveShortLinkInBackgroundAsync()
	{
		if (!_shortLinkExpansionOptions.Enabled)
		{
			return;
		}

		if (!_shortLinkConsentArmedForNextAttempt)
		{
			return;
		}

		_shortLinkConsentArmedForNextAttempt = false;
		int startedForGeneration = _linkGeneration;

		if (!Uri.TryCreate(_vm?.Url ?? _request.Url, UriKind.Absolute, out Uri? candidate)
			|| !(candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps))
		{
			return;
		}

		ShortLinkResolveResponse response;
		try
		{
			response = await _networkHelperClient.ResolveShortLinkAsync(
				new ShortLinkResolveRequest(candidate.AbsoluteUri, ConsentGranted: true),
				CancellationToken.None);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// Fail-open: the link launches unexpanded. The exception TYPE only: its message may
			// carry the URL.
			LogSwallowed("SHORTLINK_RESOLVE_FAILED", ex);
			return;
		}

		if (_terminal)
		{
			return;
		}

		// A forward replaced the link while the helper was resolving: the result describes the
		// PREVIOUS link and must not redirect the new one.
		if (startedForGeneration != _linkGeneration)
		{
			PickerLog.Marker("SHORTLINK_RESULT_STALE dropped");
			return;
		}

		// Problem log: a network-helper failure is recorded as its typed code only (the
		// response's Url fields are never logged).
		if (!string.IsNullOrWhiteSpace(response.ErrorCode))
		{
			_logger.LogWarning("Short-link helper failed ({ErrorCode})", response.ErrorCode);
		}

		if (response.Dispatch == HelperClientDispatch.Dispatched
			&& string.IsNullOrWhiteSpace(response.ErrorCode)
			&& !string.IsNullOrWhiteSpace(response.EffectiveUrl)
			&& !string.Equals(response.EffectiveUrl, candidate.AbsoluteUri, StringComparison.Ordinal))
		{
			_resolvedShortLinkUrl = response.EffectiveUrl;
		}
	}

	private async Task ShowGhostToastAsync()
	{
		// Rate limit: 1 per 30 seconds
		DateTime now = DateTime.UtcNow;
		if (now - _lastGhostToastAt < GhostToastWindow.RateLimit)
		{
			return;
		}

		_lastGhostToastAt = now;

		GhostToastShownForTests?.Invoke();

		// Auto-close after lifetime
		await Task.Delay(GhostToastWindow.Lifetime);

		// Only close if not already closed by test
		if (!_terminal) // In production, the toast window would handle its own close
		{
			GhostToastClosedForTests?.Invoke();
		}
	}

	// DEFECT #36 (MED) AND ITS REGRESSION. Two constraints pull in opposite directions, and a
	// fix must satisfy BOTH:
	//
	//   (a) the callers must NOT park the UI thread on the clipboard. `SetTextAsync(url)
	//       .GetAwaiter().GetResult()` blocked for as long as the OS clipboard service took, and
	//       another process holding the clipboard open makes that wait unbounded - the same H1
	//       freeze the launch and first-run paths were fixed for, on a path the user hits
	//       constantly (Esc, click-outside, Alt+Tab, Win+D);
	//
	//   (b) the write must ACTUALLY COMPLETE before the session shuts down. Both callers call
	//       Complete(ExitCodes.Success) immediately afterwards, and Complete synchronously runs
	//       `_desktop?.Shutdown(exitCode)` - which in Avalonia 11.3.18 ends in
	//       Dispatcher.UIThread.InvokeShutdown() -> ShutdownImpl(), whose drain loop calls
	//       `operation.Abort()` on EVERY queued operation (VERIFIED against the 11.3.18 sources:
	//       Dispatcher.MainLoop.cs, ShutdownImpl). So a write merely POSTED here is guaranteed to
	//       be aborted before it ever runs: the app never pumps the queue between the post and
	//       the shutdown. That was the regression - cancel-to-clipboard silently no-opped in
	//       production while a seam-replacing test (DispatchWriteForTests = action => action())
	//       hid it, because the inline call bypassed the shutdown drain entirely.
	//
	// THE FIX: make this method `async Task` and AWAIT the write HERE, on the UI thread, BEFORE
	// returning. The two callers become `async void` (the standard shape for a synchronous UI
	// event handler; the window's dismiss hook is an `Action`, so this matches it) and call
	// Complete(ExitCodes.Success) only AFTER the awaited call returns - i.e. after the write has
	// finished. The write is therefore done BEFORE Shutdown, so there is nothing left for the
	// shutdown to abort. Nothing here blocks: `await` yields the UI thread while the clipboard
	// service works, so the window stays responsive (constraint (a) is met by NOT synchronously
	// waiting, not by deferring the write past the point of no return).
	//
	// WHY THIS SURVIVES SHUTDOWN, precisely: the ordering that matters is "write completes" ->
	// "Complete()" -> "Complete calls Shutdown". An `await` only returns once the awaited task
	// is done, so when the caller's next statement (Complete) runs, the write has already
	// happened on this thread. The shutdown drain finds an EMPTY (or at least write-free) queue;
	// there is no posted write for it to abort. Contrast the previous shape, where the write was
	// queued and the very next synchronous statement was the shutdown that cancels the queue.
	//
	// BEST-EFFORT AND NON-FATAL IS PRESERVED END TO END: the body is wrapped so an async
	// clipboard failure cannot escape to the UI thread's unhandled-exception path (which, from an
	// `async void` handler, would tear down the process). A failed or skipped write simply means
	// no toast and no copy; the dismissal still completes. The URL is read ONCE before the await,
	// so a write can never observe a later view-model state.
	private async Task TryCancelToClipboardOnDismissAsync()
	{
		if (!CancelToClipboardEnabled || _vm is null)
		{
			return;
		}

		if (ShouldSkipCancelToClipboard(_vm.Url))
		{
			return;
		}

		// Read the URL ONCE, before the await, so the write can never act on a later view-model
		// state (the view model is not supposed to change after a dismissal anyway, but the
		// contract should not rest on that).
		string url = _vm.Url;

		try
		{
			if (WriteClipboardForTests is not null)
			{
				WriteClipboardForTests(url);
			}
			else if (WriteClipboardAsyncForTests is { } asyncWrite)
			{
				await asyncWrite(url).ConfigureAwait(true);
			}
			else if (TryGetClipboard(out IClipboard? clipboard))
			{
				// ConfigureAwait(true): the continuation must resume on the UI thread, because
				// the resolved clipboard is thread-affine (the app is STA for exactly that
				// reason, see AvaloniaPicker) and ShowCleanupRecoveryToast touches UI state.
				await clipboard!.SetTextAsync(url).ConfigureAwait(true);
			}
			else
			{
				// No clipboard provider: nothing was written, so nothing to record and no toast.
				return;
			}

			LastClipboardWriteForTests = url;

			// Same ordering as before the fix: the toast is shown AFTER a successful write.
			ShowCleanupRecoveryToast();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// Cancel-to-clipboard is best-effort and non-fatal: a clipboard failure (or a
			// throwing test seam) must never fail the dismissal. Contained here so nothing can
			// escape into the `async void` callers' unhandled-exception path.
			LogSwallowed("CANCEL_TO_CLIPBOARD_FAILED", ex);
		}
	}

	// The fail-open catches log the exception TYPE only, never the exception object or message:
	// clipboard and short-link failures can carry the URL in their message.
	private void LogSwallowed(string marker, Exception ex)
	{
		string type = ex.GetType().Name;
		PickerLog.Marker($"{marker} type={type}");
		_logger.LogWarning("Best-effort picker step {Step} failed ({ExceptionType}); continuing", marker, type);
	}

	private void ShowCleanupRecoveryToast()
	{
		// Cleanup recovery toast is interactive (Focusable=true, IsHitTestVisible=true)
		CleanupToastShownForTests?.Invoke(true);

		// Auto-close after lifetime
		// The wait runs off the UI thread; the close callback is POSTED back to it (the toast is UI
		// state). DispatchPostForTests is the real Dispatcher.UIThread.Post in production.
		Action<Action> post = DispatchPostForTests;
		_ = Task.Run(async () =>
		{
			await Task.Delay(CleanupRecoveryToastWindow.Lifetime);
			post(() => CleanupToastClosedForTests?.Invoke());
		});
	}

	private bool TryGetClipboard(out IClipboard? clipboard)
	{
		clipboard = _window is null ? null : TopLevel.GetTopLevel(_window)?.Clipboard;
		return clipboard is not null;
	}

	internal static bool ShouldSkipCancelToClipboard(string url)
	{
		return url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
			|| url.StartsWith("\\\\", StringComparison.Ordinal)
			|| url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
			|| url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
			|| url.Contains("access_token", StringComparison.OrdinalIgnoreCase)
			|| url.Contains("token=", StringComparison.OrdinalIgnoreCase)
			|| url.Contains("password=", StringComparison.OrdinalIgnoreCase);
	}

	internal async Task<int> ShowForTestsAsync(IReadOnlyList<PickerKeyAction> actions)
	{
		InitializeForTests();

		foreach (PickerKeyAction action in actions)
		{
			// The SAME pre-dispatch step the real key path (OnKeyAction) runs.
			BeforeKeyAction(action);

			switch (action.Kind)
			{
				case PickerKey.Enter:
					ActivateFocused();
					break;
				case PickerKey.Escape:
					HandleEscape();

					// HandleEscape is `async void` now: its clipboard write sits between the
					// dismissal starting and Complete() running, so the loop must let the
					// awaiting continuation reach the terminal transition before it reads
					// _terminal below.
					await WaitForDismissalToSettleAsync();
					break;
				case PickerKey.Digit:
					break;
				case PickerKey.SystemFallback:
					break;
				case PickerKey.Copy:
					break;
				case PickerKey.UseOriginal:
					ArmUseOriginalForNextLaunch();
					break;
				case PickerKey.FocusNext:
					break;
				case PickerKey.FocusPrevious:
					break;
				case PickerKey.SkipHistoryOnce:
					ArmSkipHistoryForNextLaunch();
					break;
				case PickerKey.ClearHistoryConfirmed:
					HandleClearHistoryKey();
					break;
				case PickerKey.ToggleShortcuts:
					_vm!.ToggleDetails();
					break;
				default:
					break;
			}

			if (_terminal)
			{
				break;
			}

			await Task.Yield();
		}

		return !_terminal ? ExitCodes.GeneralError : _testExitCode == int.MinValue ? ExitCodes.Success : _testExitCode;
	}

	internal void ClickCardForTests(int slot)
	{
		InitializeForTests();
		if (_vm is null || slot < 1 || slot > _vm.Cards.Count)
		{
			return;
		}

		OnCardClicked(_vm.Cards[slot - 1]);
	}

	// Drives the REAL F-key handler (OnKeyAction needs a window, so it is called directly).
	internal void PressSystemFallbackForTests()
	{
		InitializeForTests();
		LaunchSystemFallback();
	}

	internal void SimulateWindowClosedForTests()
	{
		OnWindowClosed(this, EventArgs.Empty);
	}

	// Drives the SAME method the window's Deactivated handler invokes, so a session-level test can
	// assert the external-dismissal contract without a real window. Returns the exit code the
	// dismissal produced (Success), or the run's existing code if it was already terminal.
	//
	// `async Task<int>` rather than `int`: `OnExternalDismissal` is now `async void` and its
	// awaited clipboard write sits BETWEEN the dismissal starting and Complete() running, so a
	// synchronous return here could hand back a code before the dismissal had finished. Awaiting
	// keeps the test entry point an honest mirror of the production sequence, and lets a caller
	// observe the post-write state (which is what the regression test needs).
	internal async Task<int> SimulateExternalDismissalForTestsAsync()
	{
		InitializeForTests();

		// `await` here observes the async-void dismissal to the point where it is parked on the
		// awaited write. Because both the write and its continuation run on the caller's context
		// (there is no dispatcher loop in a test host, so the continuation runs inline on this
		// thread), waiting on the real state below is enough to see the completed dismissal.
		OnExternalDismissal();
		await WaitForDismissalToSettleAsync();

		return _testExitCode == int.MinValue ? ExitCodes.Success : _testExitCode;
	}

	// A bounded wait for the async-void dismissal to reach its terminal transition, used only by
	// the test entry point above. It observes the terminal flag rather than a fixed delay, so a
	// fast write returns immediately and a hung one fails loudly instead of hanging the suite.
	private async Task WaitForDismissalToSettleAsync()
	{
		for (int attempt = 0; attempt < 200 && !_terminal; attempt++)
		{
			await Task.Delay(10).ConfigureAwait(true);
		}
	}

	internal int CompleteCountForTests { get; private set; }

	internal int EnterIgnoredBeforeSafeCountForTests { get; private set; }

	internal bool IsDetailsOpenForTests => _vm?.IsDetailsOpen == true;

	// The view model is private by design (tests observe session-level effects), but the
	// late-outcome contract needs one VM-visible surface: whether a failure status was painted. A
	// late Failed outcome that incorrectly passed the `_terminal` re-check would call
	// `vm.ShowLaunchFailure` and show here, so this is the honest observable for F3-b.
	internal bool IsErrorStatusVisibleForTests => _vm?.IsErrorStatusVisible == true;

	internal int CardCountForTests => _vm?.Cards.Count ?? 0;

	// Drives the first-run consent action through the REAL handler, so a test can observe
	// whether the handler blocks its caller while the storage helper is still running.
	internal void FirstRunActionForTests(FirstRunAction action)
	{
		InitializeForTests();
		OnFirstRunAction(action);
	}

	internal bool ClipboardReadOnceForTests { get; private set; }

	internal string? ClipboardReadAtOpenValueForTests { get; private set; }

	internal string? LastClipboardWriteForTests { get; private set; }

	internal void ConfigureClipboardHooksForTests(Func<Task<string?>>? read, Action<string>? write)
	{
		ReadClipboardForTests = read;
		WriteClipboardForTests = write;
	}

	internal void ConfigureToastHooksForTests(
		Action? onGhostToast,
		Action? onGhostToastClosed,
		Action<bool>? onCleanupToast,
		Action? onCleanupToastClosed)
	{
		GhostToastShownForTests = onGhostToast;
		GhostToastClosedForTests = onGhostToastClosed;
		CleanupToastShownForTests = onCleanupToast;
		CleanupToastClosedForTests = onCleanupToastClosed;
	}

	internal async Task CaptureClipboardAtOpenForTestsAsync()
	{
		InitializeForTests();
		await ReadClipboardAtOpenAsync();
	}

	internal async Task SimulateWindowOpenForTestsAsync()
	{
		InitializeForTests();
		await ReadClipboardAtOpenAsync();
		await ShowGhostToastAsync();
	}

	internal void ArmShortLinkConsentForNextAttemptForTests()
	{
		InitializeForTests();
		_shortLinkConsentArmedForNextAttempt = true;
	}

	internal async Task ResolveShortLinkInBackgroundForTestsAsync()
	{
		InitializeForTests();
		await ResolveShortLinkInBackgroundAsync();
	}
	// W1.5: internal rather than private so the delivery tests can bring the session to its live
	// state (view model built) and then drive the REAL forwarded-URL path through the dispatcher,
	// instead of re-asserting the queue or the drain in isolation.
	internal void InitializeForTests()
	{
		if (_vm is not null)
		{
			return;
		}

		ApplyCleanupAtSessionStart();
		_vm = new PickerViewModel(_cleanupState.EffectiveUrl, _snapshot, TimeProvider.System, BuildRankingInputs(_cleanupState.EffectiveUrl));
		_window = null;
		_terminal = false;
		_testExitCode = int.MinValue;
		CompleteCountForTests = 0;
		EnterIgnoredBeforeSafeCountForTests = 0;
		_ignoreWindowClosedOnce = false;
		ClipboardReadAtOpenValueForTests = null;
		LastClipboardWriteForTests = null;
		ClipboardReadOnceForTests = false;
		_lastGhostToastAt = DateTime.MinValue;
		_useOriginalArmedForNextLaunch = false;
		_shortLinkConsentArmedForNextAttempt = false;
		_skipHistoryForNextLaunch = false;
		IsLaunchInFlightForTests = false;
		_dismissalInProgress = false;
		_deactivatedDuringLaunch = false;
		_clearHistoryArmedAt = null;
		_repairArmedAt = null;
		_repairRun = Task.CompletedTask;
		_vm.SetAddManuallyAvailable(_firstRunConsentActionCoordinator.CanAddManually);
		if (_configNoticeRequired)
		{
			_vm.ShowConfigNotice();
		}

		// Session tests must not depend on what THIS machine is doing (a game or a presentation
		// on the test host would hold the foreground request). Tests that exercise the policy
		// set the seam after initializing.
		IsUserBusyForTests = static () => false;

		if (IsFirstRunConsentPending)
		{
			_vm.ShowFirstRunPrompt();
		}

		_ = _vm.FocusSlot(1);
		_vm.SetFocusFromControl(0);
	}

	// writeAuditInBackground: the live OpenWindow path moves the audit write off the UI thread (the
	// writer can wait ~600 ms on file retries plus up to 5 s on its mutex BEFORE the window appears);
	// the test path keeps it synchronous so audit assertions stay deterministic. Both go through the
	// same fail-open WriteCleanupAuditFailOpen.
	private void ApplyCleanupAtSessionStart(bool writeAuditInBackground = false)
	{
		_cleanupState = TrackingCleanupEngine.Cleanup(_request.Url, _trackingCleanupOptions);
		_useOriginalArmedForNextLaunch = false;
		_shortLinkConsentArmedForNextAttempt = false;
		_skipHistoryForNextLaunch = false;

		// PRIVACY: the audit records what cleanup DID. With cleanup off nothing was cleaned, and an
		// event here would store the host of every opened link in cleanup_events.jsonl for 90 days,
		// a per-link host log the user never turned on.
		if (!_trackingCleanupOptions.Enabled)
		{
			return;
		}

		if (Uri.TryCreate(_request.Url, UriKind.Absolute, out Uri? uri))
		{
			TrackingCleanupAuditEvent cleanupEvent = new(
				DateTimeOffset.UtcNow,
				uri.Host,
				_cleanupState.Changed,
				_cleanupState.ReasonCode,
				_cleanupState.RemovedQueryParameterKeys,
				_cleanupState.AppliedPackId);

			if (writeAuditInBackground)
			{
				// Off the UI thread, but KEPT: Complete() waits for it (bounded) so the audit line
				// is not lost when the picker process exits right after the choice. The catch
				// inside WriteCleanupAuditFailOpen means the task can never fault.
				PendingCleanupAuditForTests = Task.Run(() => WriteCleanupAuditFailOpen(cleanupEvent));
			}
			else
			{
				WriteCleanupAuditFailOpen(cleanupEvent);
			}
		}
	}

	// Test seam for the live (background) branch: InitializeForTests keeps the synchronous one.
	internal void ApplyCleanupAtSessionStartInBackgroundForTests()
	{
		ApplyCleanupAtSessionStart(writeAuditInBackground: true);
	}

	// Called from Complete(), usually on the UI thread: waits at most CleanupAuditDrainTimeoutForTests (1 s in production)
	// for the background audit write, then lets the session end regardless. A timed Wait, never
	// an unbounded one; the write itself is fail-open, so the wait cannot throw its exception.
	private void WaitForCleanupAuditBounded()
	{
		Task? pending = PendingCleanupAuditForTests;
		if (pending is null || pending.IsCompleted)
		{
			return;
		}

		try
		{
			if (!pending.Wait(CleanupAuditDrainTimeoutForTests))
			{
				PickerLog.Marker("CLEANUP_AUDIT_PENDING_ON_COMPLETE timeout=true");
			}
		}
		catch (AggregateException ex)
		{
			LogSwallowed("CLEANUP_AUDIT_WAIT_FAILED", ex.InnerException ?? ex);
		}
	}

	// The audit is a diagnostic: it must never block or kill the user's link. The writer throws
	// IOException (sharing-violation retries exhausted, disk full), UnauthorizedAccessException or
	// TimeoutException (mutex wait); an escape from OpenWindow() would terminate the process on launch.
	private void WriteCleanupAuditFailOpen(TrackingCleanupAuditEvent cleanupEvent)
	{
		try
		{
			_trackingCleanupAuditWriter.Write(cleanupEvent);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			LogSwallowed("CLEANUP_AUDIT_WRITE_FAILED", ex);
		}
	}

	private void ArmUseOriginalForNextLaunch()
	{
		if (!_cleanupState.CanUseOriginalForThisLaunch)
		{
			_vm!.ShowUseOriginalUnavailable();
			return;
		}

		_useOriginalArmedForNextLaunch = true;
		_vm!.ShowUseOriginalArmed();
		PickerLog.Marker("CLEANUP_USE_ORIGINAL_ONESHOT armed=true");
	}

	private void ArmSkipHistoryForNextLaunch()
	{
		if (!_activationHistoryState.Settings.Enabled)
		{
			_vm!.ShowActivationHistoryDisabled();
			return;
		}

		ExpectedDocumentGeneration expected = _activationHistoryState.Generation == 0
			? ExpectedDocumentGeneration.Absent
			: ExpectedDocumentGeneration.Match(_activationHistoryState.Generation);

		ActivationHistoryMutationResult mutation = _activationHistoryStore.Record(new ActivationHistoryRecordRequest(
			expected,
			new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: true),
			CandidateRecord: null));

		if (mutation.Applied && mutation.Document is not null)
		{
			_activationHistoryState = mutation.Document;
			_skipHistoryForNextLaunch = true;
			_vm!.ShowActivationHistorySkipArmed();
		}
	}

	// CLEAR HISTORY IS A TWO-PRESS ACTION. Ctrl+Backspace is one slip away from an ordinary
	// Backspace, and clearing cannot be undone, so the first press only ARMS and says so on the
	// status line ("Press Ctrl+Backspace again..."). A second press within the window clears.
	// The arm expires on its own (no timer: the next press compares timestamps), and any other
	// picker key disarms it, so a stale arm can never turn a much later press into a clear.
	private void HandleClearHistoryKey()
	{
		DateTimeOffset now = _timeProvider.GetUtcNow();
		if (_clearHistoryArmedAt is { } armedAt && now - armedAt <= ClearHistoryConfirmWindow)
		{
			_clearHistoryArmedAt = null;
			PickerLog.Marker("HISTORY_CLEAR confirmed");
			ClearActivationHistoryConfirmed();
			return;
		}

		_clearHistoryArmedAt = now;
		PickerLog.Marker("HISTORY_CLEAR armed");
		_vm!.ShowActivationHistoryClearArmed();
	}

	private void ClearActivationHistoryConfirmed()
	{
		ExpectedDocumentGeneration expected = _activationHistoryState.Generation == 0
			? ExpectedDocumentGeneration.Absent
			: ExpectedDocumentGeneration.Match(_activationHistoryState.Generation);

		ActivationHistoryMutationResult mutation = _activationHistoryStore.ClearConfirmed(expected);
		if (mutation.Applied && mutation.Document is not null)
		{
			_activationHistoryState = mutation.Document;
			_skipHistoryForNextLaunch = false;
			_vm!.ShowActivationHistoryCleared();
			return;
		}

		_vm!.ShowActivationHistoryClearFailed();
	}

	// ESC OR WINDOW CLOSE WHILE A LAUNCH WAITS FOR ITS OUTCOME. History is written when the outcome
	// is Success, but the session is ending now: Shutdown aborts the dispatcher's queue, so the
	// outcome handler may never run, and a skip armed for this link would stay on disk and silently
	// swallow an unrelated later one. The outcome cannot be awaited here: this runs on the UI
	// thread, which is also the thread the outcome resumes on, so a wait could only time out.
	//
	// So the flag is cleared NOW, on disk, and remembered in memory as "the launch in flight is to
	// be skipped". If the outcome does arrive and is Success it takes the normal skip branch and
	// writes no entry; if it is a failure nothing is written. Trade-off: a launch that then FAILS
	// has used up the one-shot skip (the session is over, so there is no retry for it to wait for).
	// With no launch in flight nothing changes: the flag stays armed for the next link.
	private void SettleSkipHistoryForAnUnfinishedLaunch()
	{
		if (!IsLaunchInFlightForTests
			|| !_activationHistoryState.Settings.Enabled
			|| !(_skipHistoryForNextLaunch || _activationHistoryState.Settings.SkipNextLink))
		{
			return;
		}

		_skipHistoryForNextLaunch = true;

		try
		{
			ExpectedDocumentGeneration expected = _activationHistoryState.Generation == 0
				? ExpectedDocumentGeneration.Absent
				: ExpectedDocumentGeneration.Match(_activationHistoryState.Generation);

			ActivationHistoryMutationResult cleared = _activationHistoryStore.Record(new ActivationHistoryRecordRequest(
				expected,
				new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: false),
				CandidateRecord: null));

			if (cleared.Applied && cleared.Document is not null)
			{
				_activationHistoryState = cleared.Document;
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			LogSwallowed("ACTIVATION_HISTORY_SKIP_CLEAR_FAILED", ex);
		}
	}

	private void RecordActivationHistory(string launchUrl, ChoiceCardViewModel card)
	{
		if (!_activationHistoryState.Settings.Enabled)
		{
			return;
		}

		if (_skipHistoryForNextLaunch || _activationHistoryState.Settings.SkipNextLink)
		{
			_skipHistoryForNextLaunch = false;
			ExpectedDocumentGeneration expectedSkip = _activationHistoryState.Generation == 0
				? ExpectedDocumentGeneration.Absent
				: ExpectedDocumentGeneration.Match(_activationHistoryState.Generation);

			ActivationHistoryMutationResult skipCommit = _activationHistoryStore.Record(new ActivationHistoryRecordRequest(
				expectedSkip,
				new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: false),
				CandidateRecord: null));

			if (skipCommit.Applied && skipCommit.Document is not null)
			{
				_activationHistoryState = skipCommit.Document;
			}
			return;
		}

		// History records the profile the launch actually passed. Exactly mirrors LaunchHelperArguments: only Chromium ids get a profile switch, so only
		// they record a profile; Gecko and custom/unknown ids record none.
		string? historyProfileId = LaunchHelperArguments.ResolveFamily(card.BrowserId) == BrowserLaunchFamily.Chromium
			? card.LaunchProfileId
			: null;

		ActivationHistoryRecord? projected = ActivationHistoryProjection.Project(new ActivationHistoryProjectionInput(
			DateTimeOffset.UtcNow,
			launchUrl,
			card.BrowserId,
			historyProfileId,
			_request.Activation.SourceEvidence,
			MatchedRuleId: null));

		if (projected is null)
		{
			return;
		}

		ExpectedDocumentGeneration expected = _activationHistoryState.Generation == 0
			? ExpectedDocumentGeneration.Absent
			: ExpectedDocumentGeneration.Match(_activationHistoryState.Generation);

		ActivationHistoryMutationResult mutation = _activationHistoryStore.Record(new ActivationHistoryRecordRequest(
			expected,
			new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: false),
			projected));

		if (mutation.Applied && mutation.Document is not null)
		{
			_activationHistoryState = mutation.Document;
		}
	}

	private sealed class NullActivationHistoryStore : IActivationHistoryStore
	{
		private ActivationHistoryDocument _document = new(new ActivationHistorySettingsContract(), []);

		public ActivationHistoryDocument LoadOrDefault()
		{
			return _document;
		}

		public ActivationHistoryMutationResult Record(ActivationHistoryRecordRequest request)
		{
			if (!request.Settings.Enabled)
			{
				_document = _document with
				{
					Settings = new ActivationHistorySettingsContract(Enabled: false, SkipNextLink: false),
					Generation = _document.Generation + 1,
				};
				return Applied();
			}

			if (request.Settings.SkipNextLink)
			{
				_document = _document with
				{
					Settings = new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: true),
					Generation = _document.Generation + 1,
				};
				return Applied();
			}

			if (request.CandidateRecord is null)
			{
				_document = _document with
				{
					Settings = new ActivationHistorySettingsContract(_document.Settings.Enabled, SkipNextLink: false),
					Generation = _document.Generation + 1,
				};
				return Applied();
			}

			List<ActivationHistoryRecord> entries = [.. _document.Entries, request.CandidateRecord];
			_document = _document with
			{
				Entries = entries,
				Settings = new ActivationHistorySettingsContract(_document.Settings.Enabled, SkipNextLink: false),
				Generation = _document.Generation + 1,
			};
			return Applied();
		}

		public ActivationHistoryMutationResult ClearConfirmed(ExpectedDocumentGeneration expectedGeneration)
		{
			_ = expectedGeneration;
			_document = _document with
			{
				Entries = [],
				Settings = new ActivationHistorySettingsContract(_document.Settings.Enabled, SkipNextLink: false),
				Generation = _document.Generation + 1,
			};
			return Applied();
		}

		private ActivationHistoryMutationResult Applied()
		{
			return new ActivationHistoryMutationResult(
				Applied: true,
				MutationCommitResult.Committed(_document.Generation, Math.Max(0, _document.Generation - 1)),
				NewGeneration: _document.Generation,
				Document: _document);
		}
	}

	// W7.2's null object: a session composed without a rules store ranks every card as
	// AllChoices, which is the pre-ranking behaviour. It writes nothing — Save is a
	// conflict, because a store that holds no document cannot honestly claim to have
	// committed one.
	private sealed class NullRulesStore : IRulesStore
	{
		public RuleSetDocument LoadOrDefault()
		{
			return new RuleSetDocument(0, []);
		}

		public MutationCommitResult Save(RulesDocument document, ExpectedDocumentGeneration expectedGeneration)
		{
			return MutationCommitResult.Conflict(observedGeneration: null);
		}
	}

	private sealed class NullNetworkHelperClient : INetworkHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Rejected);
		}

		public Task<ShortLinkResolveResponse> ResolveShortLinkAsync(ShortLinkResolveRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new ShortLinkResolveResponse(
				HelperClientDispatch.Rejected,
				request.Url,
				request.Url,
				"network.helper-client-unavailable",
				FromCache: false));
		}
	}
}
