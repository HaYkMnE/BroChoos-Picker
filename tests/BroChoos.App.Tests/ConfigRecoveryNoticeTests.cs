using System.Diagnostics;
using BroChoos.App.Localization;
using BroChoos.App.Picker;
using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Routing;
using BroChoos.Core.Snapshots;
using BroChoos.Core.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.App.Tests;

// The picker's config-recovery notice: history.json or rules.json was unreadable, defaults are
// in use, the link still opens, and one quiet line offers "Repair settings" (two presses), which
// runs `--safe-mode --safe-mode-action repair-settings` hidden and reports the real exit code.
// No test here starts a real process: the runner is a fake. Reads localized text, so it joins
// the culture-serializing collection.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class ConfigRecoveryNoticeTests
{
	// ---- detection and the link path -----------------------------------------------------

	[Fact]
	public void BrokenHistory_ShowsNotice_AndTheCardStillLaunches()
	{
		FakeLaunchHelperClient launch = new();
		RecordingLogger logger = new();
		PickerSession session = NewSession(launch, logger, history: ConfigReadOutcome.Corrupt, rules: ConfigReadOutcome.Loaded);

		session.ClickCardForTests(1);

		_ = session.IsConfigNoticeVisibleForTests.Should().BeTrue();
		_ = launch.LaunchCalls.Should().Be(1, "a broken settings file must never cost the link");
		_ = launch.LastRequest!.Url.Should().Be("https://example.com/");
		_ = logger.Warnings.Should().ContainSingle(w => w.Contains(PickerSession.HistoryResetCode, StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(ConfigReadOutcome.Incompatible)]
	[InlineData(ConfigReadOutcome.Newer)]
	[InlineData(ConfigReadOutcome.Corrupt)]
	[InlineData(ConfigReadOutcome.TooLarge)]
	public void DamagedRules_ShowNotice_AndLogTheRulesCode(ConfigReadOutcome rules)
	{
		RecordingLogger logger = new();
		PickerSession session = NewSession(new FakeLaunchHelperClient(), logger, history: ConfigReadOutcome.Loaded, rules);

		session.InitializeForTests();

		_ = session.IsConfigNoticeVisibleForTests.Should().BeTrue();
		_ = logger.Warnings.Should().ContainSingle(w => w.Contains(PickerSession.RulesResetCode, StringComparison.Ordinal));
	}

	// First run: no files yet. A locked file is transient, not damage. Neither is reported.
	[Theory]
	[InlineData(ConfigReadOutcome.Missing, ConfigReadOutcome.Missing)]
	[InlineData(ConfigReadOutcome.Loaded, ConfigReadOutcome.Loaded)]
	[InlineData(ConfigReadOutcome.OpenFailed, ConfigReadOutcome.Missing)]
	public void MissingOrHealthyFiles_ShowNoNotice_AndLogNothing(ConfigReadOutcome history, ConfigReadOutcome rules)
	{
		RecordingLogger logger = new();
		PickerSession session = NewSession(new FakeLaunchHelperClient(), logger, history, rules);

		session.InitializeForTests();

		_ = session.IsConfigNoticeVisibleForTests.Should().BeFalse();
		_ = logger.Warnings.Should().BeEmpty();
	}

	// PRIORITY: a launch error owns the status line and is shown in full; the notice is a
	// separate row that neither hides it nor is wiped by it.
	[Fact]
	public void LaunchError_WithNotice_ShowsBoth_AndTheErrorIsNotReplaced()
	{
		FakeLaunchHelperClient launch = new(new LaunchHelperOutcome(LaunchStatus.Failed, "edge-stable", "launch.start-failed", "start", "launch.error.failed"));
		PickerSession session = NewSession(launch, new RecordingLogger(), history: ConfigReadOutcome.Corrupt, rules: ConfigReadOutcome.Loaded);

		session.ClickCardForTests(1);

		_ = session.IsErrorStatusVisibleForTests.Should().BeTrue();
		_ = session.StatusMessageForTests.Should().Contain(LocalizationService.GetString("Error.launch.start-failed"));
		_ = session.StatusMessageForTests.Should().NotContain(LocalizationService.GetString("ConfigNotice.Text"));
		_ = session.IsConfigNoticeVisibleForTests.Should().BeTrue("the notice stays visible next to the error");
	}

	// ---- Repair settings: confirmation ---------------------------------------------------

	[Fact]
	public async Task FirstPress_OnlyArms_AndStartsNothing()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeOk);
		PickerSession session = SessionWithRunner(runner, out _);

		await session.PressRepairForTestsAsync();

		_ = runner.Started.Should().BeEmpty("one press must never repair");
		_ = session.RepairStateForTests.Should().Be(ConfigRepairState.Armed);
		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.RepairArmed"));
		_ = session.IsConfigNoticeVisibleForTests.Should().BeTrue();
	}

	[Fact]
	public async Task SecondPress_RunsRepairSettingsHidden_WithStructuredArguments()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeOk);
		PickerSession session = SessionWithRunner(runner, out _);

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		ProcessStartInfo started = runner.Started.Should().ContainSingle().Subject;
		_ = started.FileName.Should().Be(@"C:\Apps\BroChoos\BroChoos.exe");
		_ = started.UseShellExecute.Should().BeFalse();
		_ = started.CreateNoWindow.Should().BeTrue("the child must not flash a console or take focus");
		_ = started.WindowStyle.Should().Be(ProcessWindowStyle.Hidden);
		_ = started.Arguments.Should().BeEmpty("arguments go through ArgumentList, never a command string");
		_ = started.ArgumentList.Should().Equal("--safe-mode", "--safe-mode-action", "repair-settings");
		_ = runner.Timeouts.Should().ContainSingle().Which.Should().Be(SafeModeLauncher.Timeout);
	}

	[Fact]
	public void PortableDataRoot_IsPassedAsSeparateTokens()
	{
		SafeModeLauncher launcher = new(new FakeRecoveryRunner(ExitCodes.SafeModeOk), static () => @"D:\Portable\BroChoos.exe");

		ProcessStartInfo info = launcher.BuildStartInfo("repair-settings", @"D:\Portable\Data with space")!;

		_ = info.ArgumentList.Should().Equal("--safe-mode", "--safe-mode-action", "repair-settings", "--portable-data", @"D:\Portable\Data with space");
		_ = info.CreateNoWindow.Should().BeTrue();
	}

	[Fact]
	public async Task ArmExpires_AfterTheConfirmWindow_AndTheLatePressReArms()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeOk);
		PickerSession session = SessionWithRunner(runner, out _);
		ManualTimeProvider time = new(DateTimeOffset.UnixEpoch);
		session.UseTimeProviderForTests(time);

		await session.PressRepairForTestsAsync();
		time.Advance(PickerSession.RepairConfirmWindow + TimeSpan.FromMilliseconds(1));
		await session.PressRepairForTestsAsync();

		_ = runner.Started.Should().BeEmpty("an expired arm must not turn a later press into a repair");
		_ = session.RepairStateForTests.Should().Be(ConfigRepairState.Armed);
	}

	[Fact]
	public async Task APickerKeyBetweenPresses_Disarms()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeOk);
		PickerSession session = SessionWithRunner(runner, out _);

		await session.PressRepairForTestsAsync();
		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.UseOriginal, 0)]);
		await session.PressRepairForTestsAsync();

		_ = runner.Started.Should().BeEmpty();
		_ = session.RepairStateForTests.Should().Be(ConfigRepairState.Armed, "the press after the key only re-arms");
	}

	// ---- Repair settings: double-click guard ---------------------------------------------

	[Fact]
	public async Task PressesWhileRunning_AreRefused_AndStartNoSecondChild()
	{
		FakeRecoveryRunner runner = new(exitCode: null, hold: true);
		PickerSession session = SessionWithRunner(runner, out _);

		await session.PressRepairForTestsAsync();
		Task run = session.PressRepairForTestsAsync();

		_ = session.RepairStateForTests.Should().Be(ConfigRepairState.Running);
		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.Repairing"));
		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();
		_ = runner.Started.Should().ContainSingle("a double click must not start a second recovery run");

		runner.Release(ExitCodes.SafeModeOk);
		await run;
		_ = session.RepairStateForTests.Should().Be(ConfigRepairState.Idle);
	}

	[Fact]
	public async Task CardsStillLaunch_WhileTheRepairRuns()
	{
		FakeLaunchHelperClient launch = new();
		FakeRecoveryRunner runner = new(exitCode: null, hold: true);
		PickerSession session = SessionWithRunner(runner, out _, launch);

		await session.PressRepairForTestsAsync();
		Task run = session.PressRepairForTestsAsync();
		session.ClickCardForTests(1);

		_ = launch.LaunchCalls.Should().Be(1, "the repair never blocks the link");
		runner.Release(ExitCodes.SafeModeOk);
		await run;
	}

	// ---- Repair settings: exit code -> status ----------------------------------------------

	[Fact]
	public async Task Exit10_IsRepaired_AndTheNoticeIsHidden_AndCannotRunAgain()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeOk);
		PickerSession session = SessionWithRunner(runner, out RecordingLogger logger);

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		_ = session.IsConfigNoticeVisibleForTests.Should().BeFalse("the files are readable again");
		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.RepairDone"));
		_ = session.IsErrorStatusVisibleForTests.Should().BeFalse();
		_ = logger.Warnings.Should().NotContain(w => w.Contains("config.settings.", StringComparison.Ordinal));

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();
		_ = runner.Started.Should().ContainSingle("a hidden notice offers nothing more to press");
	}

	// EXIT 11 SAYS ONLY "NOT EVERYTHING WAS FIXED". After it, both stores are re-read and the
	// message is chosen from what is really on disk. Each case: what history.json reads as
	// AFTER the repair -> the key shown, whether it is an error, and the file name in it.
	public static TheoryData<ConfigReadOutcome, string, bool> Exit11Cases => new()
	{
		{ ConfigReadOutcome.Newer, "Status.RepairKeptNewer", false },
		{ ConfigReadOutcome.Incompatible, "Status.RepairKeptOlder", false },
		{ ConfigReadOutcome.InvalidEnvelope, "Status.RepairInvalidEnvelope", false },
		{ ConfigReadOutcome.TooLarge, "Status.RepairTooLarge", false },
		{ ConfigReadOutcome.Corrupt, "Status.RepairStillBroken", true },
		{ ConfigReadOutcome.Missing, "Status.RepairStillBroken", true },
	};

	[Theory]
	[MemberData(nameof(Exit11Cases))]
	public async Task Exit11_MessageComesFromTheReReadOutcome_AndNamesOnlyTheFile(ConfigReadOutcome afterRepair, string key, bool isError)
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeFailed);
		OutcomeHistoryStore history = new(ConfigReadOutcome.Corrupt);
		PickerSession session = SessionWithRunner(runner, out RecordingLogger logger, history: history);
		runner.OnRun = () => history.Outcome = afterRepair;

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString(key, "history.json"));
		_ = session.StatusMessageForTests.Should().Contain("history.json").And.NotContain(@"\", "only the file name, never a path");
		_ = session.IsErrorStatusVisibleForTests.Should().Be(isError);
		_ = session.IsConfigNoticeVisibleForTests.Should().BeTrue("the file is still not usable");
		_ = logger.Warnings.Should().ContainSingle(w => w.Contains(PickerSession.RepairIncompleteCode, StringComparison.Ordinal));
		_ = session.RepairStateForTests.Should().Be(ConfigRepairState.Idle, "the user may try again");
	}

	[Fact]
	public async Task Exit11_RulesFileTooLarge_IsNamedAsRulesJson()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeFailed);
		OutcomeRulesStore rules = new(ConfigReadOutcome.TooLarge);
		PickerSession session = SessionWithRunner(runner, out _, history: new OutcomeHistoryStore(ConfigReadOutcome.Loaded), rules: rules);

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.RepairTooLarge", "rules.json"));
	}

	// Too large outranks "kept for a newer version": moving the file is the one thing the
	// user can do, so that is what the line asks for.
	[Fact]
	public async Task Exit11_WithTwoDamagedFiles_ShowsTheOneTheUserCanFix()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeFailed);
		PickerSession session = SessionWithRunner(
			runner,
			out _,
			history: new OutcomeHistoryStore(ConfigReadOutcome.Newer),
			rules: new OutcomeRulesStore(ConfigReadOutcome.TooLarge));

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.RepairTooLarge", "rules.json"));
	}

	// Exit 11, but by the time the stores are re-read both load (another writer fixed them):
	// that is a success, and the notice goes.
	[Fact]
	public async Task Exit11_ButBothFilesNowLoad_IsReportedAsRepaired()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeFailed);
		OutcomeHistoryStore history = new(ConfigReadOutcome.Corrupt);
		PickerSession session = SessionWithRunner(runner, out _, history: history);
		runner.OnRun = () => history.Outcome = ConfigReadOutcome.Loaded;

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		_ = session.IsConfigNoticeVisibleForTests.Should().BeFalse();
		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.RepairDone"));
	}

	// MISSING IS REMAINING DAMAGE ONLY FOR A FILE THAT WAS DAMAGED AT OPEN. Here rules.json
	// never existed (the usual case: nothing saves rules yet) and history.json is a newer
	// BroChoos's file, so the line names history.json and the "newer" reason - not a
	// "rules.json could not be repaired" error.
	[Fact]
	public async Task Exit11_HistoryNewer_RulesMissingAtOpen_NamesHistoryWithTheNewerReason()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeFailed);
		PickerSession session = SessionWithRunner(
			runner,
			out _,
			history: new OutcomeHistoryStore(ConfigReadOutcome.Newer),
			rules: new OutcomeRulesStore(ConfigReadOutcome.Missing));

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.RepairKeptNewer", "history.json"));
		_ = session.StatusMessageForTests.Should().NotContain("rules.json");
		_ = session.IsErrorStatusVisibleForTests.Should().BeFalse();
	}

	// The race: exit 11, but both files load by the time they are re-read (rules was never
	// there). Repaired, not an error - the missing rules file is not damage.
	[Fact]
	public async Task Exit11_BothReadableAfterARace_WithRulesNeverPresent_IsReportedAsRepaired()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeFailed);
		OutcomeHistoryStore history = new(ConfigReadOutcome.Corrupt);
		PickerSession session = SessionWithRunner(runner, out _, history: history, rules: new OutcomeRulesStore(ConfigReadOutcome.Missing));
		runner.OnRun = () => history.Outcome = ConfigReadOutcome.Loaded;

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		_ = session.IsErrorStatusVisibleForTests.Should().BeFalse();
		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.RepairDone"));
		_ = session.IsConfigNoticeVisibleForTests.Should().BeFalse();
	}

	// The real "moved out, not replaced" case: rules.json WAS damaged at open and is Missing
	// after the repair, so the error names rules.json.
	[Fact]
	public async Task Exit11_RulesDamagedAtOpen_MissingAfter_ErrorNamesRulesJson()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeFailed);
		OutcomeRulesStore rules = new(ConfigReadOutcome.Corrupt);
		PickerSession session = SessionWithRunner(runner, out _, history: new OutcomeHistoryStore(ConfigReadOutcome.Loaded), rules: rules);
		runner.OnRun = () => rules.Outcome = ConfigReadOutcome.Missing;

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.RepairStillBroken", "rules.json"));
		_ = session.IsErrorStatusVisibleForTests.Should().BeTrue();
	}

	// A file damaged at open whose damage changed kind keeps being reported (by its NEW kind),
	// while a file that was fine at open and is fine now is never mentioned.
	[Fact]
	public async Task Exit11_HistoryLoadedAtOpenAndNow_IsNeverNamed()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeFailed);
		PickerSession session = SessionWithRunner(
			runner,
			out _,
			history: new OutcomeHistoryStore(ConfigReadOutcome.Loaded),
			rules: new OutcomeRulesStore(ConfigReadOutcome.Incompatible));

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.RepairKeptOlder", "rules.json"));
		_ = session.StatusMessageForTests.Should().NotContain("history.json");
	}

	// ---- the confirm label is disarmed by a timer ----------------------------------------

	[Fact]
	public async Task ArmedLabel_IsDisarmedByTheTimer_WhenTheWindowCloses()
	{
		PickerSession session = SessionWithRunner(new FakeRecoveryRunner(ExitCodes.SafeModeOk), out _);
		FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
		session.UseTimeProviderForTests(time);
		session.PostToUi = static action => action();

		await session.PressRepairForTestsAsync();
		_ = session.RepairStateForTests.Should().Be(ConfigRepairState.Armed);

		time.Advance(PickerSession.RepairConfirmWindow);

		_ = session.RepairStateForTests.Should().Be(ConfigRepairState.Idle, "the label must not keep saying Confirm repair");
		_ = session.StatusMessageForTests.Should().BeEmpty("the armed hint is cleared with it");
	}

	// A stale timer from an EARLIER arm must not disarm a later one.
	[Fact]
	public async Task AnOldTimer_DoesNotDisarmANewerArm()
	{
		PickerSession session = SessionWithRunner(new FakeRecoveryRunner(ExitCodes.SafeModeOk), out _);
		FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
		session.UseTimeProviderForTests(time);
		session.PostToUi = static action => action();

		await session.PressRepairForTestsAsync();
		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.UseOriginal, 0)]);
		time.Advance(TimeSpan.FromSeconds(2));
		await session.PressRepairForTestsAsync();
		time.Advance(TimeSpan.FromSeconds(2.5));

		_ = session.RepairStateForTests.Should().Be(ConfigRepairState.Armed, "only 2.5 s of the second arm have passed");
	}

	public static TheoryData<int?, bool, string> FailureCases => new()
	{
		{ null, false, "timeout" },
		{ ExitCodes.GeneralError, false, "unexpected-exit" },
		{ ExitCodes.InvalidUrl, false, "unexpected-exit" },
		{ null, true, "Win32Exception" },
	};

	[Theory]
	[MemberData(nameof(FailureCases))]
	public async Task TimeoutStartFailureOrUnexpectedExit_IsAnErrorLine_WithTheManualCommand(int? exitCode, bool throwOnStart, string step)
	{
		FakeRecoveryRunner runner = new(exitCode, throwOnStart: throwOnStart);
		PickerSession session = SessionWithRunner(runner, out RecordingLogger logger);

		await session.PressRepairForTestsAsync();
		await session.PressRepairForTestsAsync();

		_ = session.IsErrorStatusVisibleForTests.Should().BeTrue();
		_ = session.StatusMessageForTests.Should().Be(LocalizationService.GetString("Status.RepairFailed"));
		_ = session.StatusMessageForTests.Should().Contain("BroChoos.exe --safe-mode --safe-mode-action repair-settings");
		_ = session.IsConfigNoticeVisibleForTests.Should().BeTrue();
		_ = logger.Warnings.Should().ContainSingle(w => w.Contains(PickerSession.RepairFailedCode, StringComparison.Ordinal) && w.Contains(step, StringComparison.Ordinal));
		_ = session.RepairStateForTests.Should().Be(ConfigRepairState.Idle);
	}

	[Fact]
	public async Task UnknownExecutablePath_StartsNothing_AndFails()
	{
		FakeRecoveryRunner runner = new(ExitCodes.SafeModeOk);
		SafeModeLauncher launcher = new(runner, static () => null);

		(SettingsRepairResult result, string? detail) = await launcher.RunAsync("repair-settings", null, CancellationToken.None);

		_ = result.Should().Be(SettingsRepairResult.Failed);
		_ = detail.Should().Be("no-executable");
		_ = runner.Started.Should().BeEmpty();
		_ = launcher.BuildStartInfo("not-an-action", null).Should().BeNull("only known Safe Mode actions are ever started");
	}

	[Fact]
	public void AllLogCodes_AreOnTheProblemLogAllowlist()
	{
		_ = Logging.ProblemLog.KnownErrorCodes.Should().Contain(
			[PickerSession.HistoryResetCode, PickerSession.RulesResetCode, PickerSession.RepairFailedCode, PickerSession.RepairIncompleteCode]);
	}

	// ---- screen reader -------------------------------------------------------------------

	[Fact]
	public void Announcement_IsEmptyAtFirstFrame_ThenSetOnce()
	{
		PickerViewModel vm = new("https://example.com/", new BrowserSnapshot([], 0, DateTimeOffset.UtcNow));
		vm.ShowConfigNotice();
		List<string> changes = [];
		vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);

		_ = vm.ConfigNoticeAnnouncement.Should().BeEmpty("a live region announces a text CHANGE, so the first frame carries none");

		vm.AnnounceConfigNotice();
		vm.AnnounceConfigNotice();

		_ = vm.ConfigNoticeAnnouncement.Should().Be(vm.ConfigNoticeText);
		_ = changes.Count(c => c == nameof(PickerViewModel.ConfigNoticeAnnouncement)).Should().Be(1, "announced once");
	}

	// ---- translations --------------------------------------------------------------------

	[Theory]
	[InlineData("ru")]
	[InlineData("hy")]
	public void NoticeStrings_AreTranslated(string culture)
	{
		System.Globalization.CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			string[] keys =
			[
				"ConfigNotice.Text", "ConfigNotice.Repair", "ConfigNotice.RepairConfirm", "ConfigNotice.Repairing",
				"Status.RepairArmed", "Status.Repairing", "Status.RepairDone",
				"Status.RepairKeptNewer", "Status.RepairKeptOlder", "Status.RepairInvalidEnvelope", "Status.RepairTooLarge", "Status.RepairStillBroken",
				"Status.RepairFailed",
			];
			LocalizationService.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en");
			string[] english = [.. keys.Select(LocalizationService.GetString)];
			LocalizationService.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
			string[] translated = [.. keys.Select(LocalizationService.GetString)];

			for (int i = 0; i < keys.Length; i++)
			{
				_ = translated[i].Should().NotBeNullOrWhiteSpace();
				_ = translated[i].Should().NotBe(english[i], $"{culture} '{keys[i]}' must be translated");
			}

			_ = translated[^1].Should().Contain("BroChoos.exe --safe-mode --safe-mode-action repair-settings", "the manual command is never translated");
			_ = LocalizationService.GetString("Status.RepairStillBroken", "rules.json")
				.Should().Contain("rules.json").And.Contain("BroChoos.exe --safe-mode --safe-mode-action repair-settings");
			_ = LocalizationService.GetString("Status.RepairTooLarge", "history.json").Should().Contain("history.json");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	// ---- helpers -------------------------------------------------------------------------

	private static PickerSession SessionWithRunner(
		FakeRecoveryRunner runner,
		out RecordingLogger logger,
		FakeLaunchHelperClient? launch = null,
		OutcomeHistoryStore? history = null,
		OutcomeRulesStore? rules = null)
	{
		logger = new RecordingLogger();
		PickerSession session = NewSession(
			launch ?? new FakeLaunchHelperClient(),
			logger,
			history ?? new OutcomeHistoryStore(ConfigReadOutcome.Corrupt),
			rules ?? new OutcomeRulesStore(ConfigReadOutcome.Loaded));
		session.UseSafeModeLauncherForTests(new SafeModeLauncher(runner, static () => @"C:\Apps\BroChoos\BroChoos.exe"));
		return session;
	}

	private static PickerSession NewSession(FakeLaunchHelperClient launch, RecordingLogger logger, ConfigReadOutcome history, ConfigReadOutcome rules)
	{
		return NewSession(launch, logger, new OutcomeHistoryStore(history), new OutcomeRulesStore(rules));
	}

	private static PickerSession NewSession(FakeLaunchHelperClient launch, RecordingLogger logger, OutcomeHistoryStore history, OutcomeRulesStore rules)
	{
		BrowserSnapshot snapshot = new([new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")], 3, DateTimeOffset.UtcNow);

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
			new PickerRequest(activation, snapshot, FirstRunConsentContract.None),
			launch,
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(Microsoft.Extensions.Logging.Abstractions.NullLogger<ForwardedUrlRouter>.Instance),
			logger,
			trackingCleanupOptions: null,
			trackingCleanupAuditWriter: null,
			shortLinkExpansionOptions: null,
			networkHelperClient: null,
			activationHistoryStore: history,
			rulesStore: rules);
	}

	// Records every start; returns a fixed exit code, throws, or holds until Release.
	private sealed class FakeRecoveryRunner(int? exitCode, bool hold = false, bool throwOnStart = false) : IRecoveryProcessRunner
	{
		private readonly TaskCompletionSource<int?> _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public List<ProcessStartInfo> Started { get; } = [];

		public List<TimeSpan> Timeouts { get; } = [];

		// What the "child" did to the files, applied before it exits (a store re-read sees it).
		public Action? OnRun { get; set; }

		public Task<int?> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
		{
			Started.Add(startInfo);
			Timeouts.Add(timeout);
			OnRun?.Invoke();
			return throwOnStart
				? throw new System.ComponentModel.Win32Exception(2)
				: hold ? _held.Task : Task.FromResult(exitCode);
		}

		public void Release(int code)
		{
			_ = _held.TrySetResult(code);
		}
	}

	private class ManualTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public virtual void Advance(TimeSpan delta)
		{
			_now = _now.Add(delta);
		}
	}

	// Clock plus one-shot timers that fire when Advance passes their due time, on the calling
	// thread - so a test drives the disarm timer without waiting.
	private sealed class FakeTimeProvider(DateTimeOffset now) : ManualTimeProvider(now)
	{
		private readonly List<FakeTimer> _timers = [];

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			FakeTimer timer = new(callback, state, GetUtcNow() + dueTime);
			_timers.Add(timer);
			return timer;
		}

		public override void Advance(TimeSpan delta)
		{
			base.Advance(delta);
			foreach (FakeTimer timer in _timers.ToArray())
			{
				timer.FireIfDue(GetUtcNow());
			}
		}

		private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
		{
			private bool _done;

			public void FireIfDue(DateTimeOffset now)
			{
				if (!_done && now >= due)
				{
					_done = true;
					callback(state);
				}
			}

			public bool Change(TimeSpan dueTime, TimeSpan period)
			{
				return false;
			}

			public void Dispose()
			{
				_done = true;
			}

			public ValueTask DisposeAsync()
			{
				_done = true;
				return ValueTask.CompletedTask;
			}
		}
	}

	private sealed class OutcomeHistoryStore(ConfigReadOutcome outcome) : IActivationHistoryStore
	{
		public ConfigReadOutcome Outcome { get; set; } = outcome;

		private static readonly ActivationHistoryDocument Default = new(new ActivationHistorySettingsContract(), []);

		public ActivationHistoryDocument LoadOrDefault()
		{
			return Default;
		}

		public ConfigReadResult<ActivationHistoryDocument> LoadWithOutcome()
		{
			return new(Default, Outcome);
		}

		public ActivationHistoryMutationResult Record(ActivationHistoryRecordRequest request)
		{
			return new(false, MutationCommitResult.Conflict(observedGeneration: null), null, null);
		}

		public ActivationHistoryMutationResult ClearConfirmed(ExpectedDocumentGeneration expectedGeneration)
		{
			return new(false, MutationCommitResult.Conflict(observedGeneration: null), null, null);
		}
	}

	private sealed class OutcomeRulesStore(ConfigReadOutcome outcome) : IRulesStore
	{
		public ConfigReadOutcome Outcome { get; set; } = outcome;

		public RuleSetDocument LoadOrDefault()
		{
			return new RuleSetDocument(0, []);
		}

		public ConfigReadResult<RuleSetDocument> LoadWithOutcome()
		{
			return new(LoadOrDefault(), Outcome);
		}

		public MutationCommitResult Save(RulesDocument document, ExpectedDocumentGeneration expectedGeneration)
		{
			return MutationCommitResult.Conflict(observedGeneration: null);
		}
	}

	private sealed class RecordingLogger : ILogger<PickerSession>
	{
		public List<string> Warnings { get; } = [];

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (logLevel == LogLevel.Warning)
			{
				Warnings.Add(formatter(state, exception));
			}
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
