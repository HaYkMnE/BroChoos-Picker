using System.Diagnostics;
using System.Globalization;
using System.Text;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Runners;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace BroChoos.Platform.Windows.Recovery;

// W8.2: the ACTING side of Safe Mode recovery. Every member here does something
// observable; a route this build cannot perform returns Unavailable with a
// truthful, user-readable reason rather than claiming availability.
//
// WHY THIS TYPE HOLDS A RECORDED SET OF SEAMS RATHER THAN DOING I/O DIRECTLY.
// The recovery routes touch storage roots, the repair executor, the capability
// registration service, the detector and the shell. Each of those already exists
// behind a seam in this repository, so recovery reuses them instead of inventing
// a second filesystem or registry path with its own safety rules. That is also
// what makes the routes testable without a real registry or a real browser.
//
// PRIVACY (D-075 / D-097). No reason string composed here carries a filesystem
// path, a URL, a registry value or an exception message. Failure reasons name a
// TYPE. Diagnostics are written REDACTED by construction: the artifact records
// counts, booleans and the schema verdict only, never a path.
//
// SAFETY. Every action is fail-open: a failure is reported as Failed, never
// thrown out of the recovery surface. `registration-repair` re-DECLARES
// capabilities (RegisterAll) and never writes UserChoice — that is the existing
// service's invariant, and recovery does not get a second, weaker registry path.
public sealed class SafeModeRecoveryActions : ISafeModeRecoveryActions
{
	// The diagnostics artifact name. Fixed so repeated runs are idempotent in the
	// directory listing and a support workflow knows exactly what to look for.
	internal const string DiagnosticsFileName = "safe-mode-diagnostics.json";

	// A bounded write-lock wait for a recovery write. Recovery runs while the user
	// is watching and is not on the picker's D-v3.5-493 path, but it must still not
	// park forever behind a foreign handle; the store fails fast on contention.
	// The reset route now delegates to ConfigRepairExecutor, which owns the wait: a
	// fixed cap on the per-file lock (PromotionWriteLockTimeout, 5 s), with the
	// RepairAttemptBudget started only after the lock is acquired and bounding the
	// repair work that follows. So recovery carries no second timeout constant that
	// could drift from the one the executor actually uses.

	private readonly ILogger<SafeModeRecoveryActions> _logger;
	private readonly IStorageRootResolver _storageRoot;
	private readonly ConfigRepairExecutor _repairExecutor;
	private readonly ICapabilityRegistrationService? _registration;
	private readonly IBrowserDetector? _browserDetector;
	private readonly IDataFolderOpener? _dataFolderOpener;

	public SafeModeRecoveryActions(
		ILogger<SafeModeRecoveryActions> logger,
		IStorageRootResolver storageRoot,
		ConfigRepairExecutor repairExecutor,
		ICapabilityRegistrationService? registration = null,
		IBrowserDetector? browserDetector = null,
		IDataFolderOpener? dataFolderOpener = null)
	{
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentNullException.ThrowIfNull(storageRoot);
		ArgumentNullException.ThrowIfNull(repairExecutor);

		_logger = logger;
		_storageRoot = storageRoot;
		_repairExecutor = repairExecutor;
		_registration = registration;
		_browserDetector = browserDetector;
		_dataFolderOpener = dataFolderOpener;
	}

	public SafeModeActionOutcome Perform(string actionId)
	{
		ArgumentNullException.ThrowIfNull(actionId);

		return actionId switch
		{
			SafeModeActionIds.DiagnosticsExport => ExportDiagnostics(),
			SafeModeActionIds.RestoreBackup => RestoreBackup(),
			SafeModeActionIds.ResetDefaults => ResetDefaults(),
			SafeModeActionIds.OpenDataFolder => OpenDataFolder(),
			SafeModeActionIds.RegistrationRepair => RepairRegistration(),
			SafeModeActionIds.SyntheticLinkProbe => ProbeSyntheticLink(),
			SafeModeActionIds.RepairSettings => RepairSettings(),
			_ => new SafeModeActionOutcome(actionId, SafeModeActionStatus.Unavailable, "this build does not implement the requested recovery action"),
		};
	}

	// 1. A REAL artifact on disk. Written to <root>\diagnostics\, which
	// StorageRootResolver deliberately does not pre-provision (it is subsystem-owned
	// and created on action, D-074), so the route also proves the storage root is
	// usable — which is itself the fact a broken deployment most needs.
	private SafeModeActionOutcome ExportDiagnostics()
	{
		try
		{
			string diagnosticsDirectory = Path.Combine(_storageRoot.Root, "diagnostics");
			_ = Directory.CreateDirectory(diagnosticsDirectory);
			string destination = Path.Combine(diagnosticsDirectory, DiagnosticsFileName);

			// Redacted by construction: no path, no URL, no registry value, no user
			// name. Counts and booleans only, so the artifact is safe to hand a
			// reviewer and safe to attach to a support request.
			string artifact = string.Concat(
				"{\n",
				"  \"artifact\": \"safe-mode-diagnostics\",\n",
				"  \"schemaVersion\": ",
				SchemaVersioning.Version.ToString(CultureInfo.InvariantCulture),
				",\n",
				"  \"schemaRevision\": ",
				SchemaVersioning.Revision.ToString(CultureInfo.InvariantCulture),
				",\n",
				"  \"storageRootReachable\": true,\n",
				"  \"backupsDirectoryPresent\": ",
				Directory.Exists(_storageRoot.Backups) ? "true" : "false",
				",\n",
				"  \"logsDirectoryPresent\": ",
				Directory.Exists(_storageRoot.Logs) ? "true" : "false",
				",\n",
				"  \"timestampUtc\": \"",
				DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
				"\"\n",
				"}\n");

			File.WriteAllText(destination, artifact, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

			return new SafeModeActionOutcome(
				SafeModeActionIds.DiagnosticsExport,
				SafeModeActionStatus.Completed,
				"redacted diagnostics written to the data folder's diagnostics directory");
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			_logger.LogWarning(failure, "Safe mode diagnostics export failed");
			return new SafeModeActionOutcome(
				SafeModeActionIds.DiagnosticsExport,
				SafeModeActionStatus.Failed,
				$"diagnostics could not be written ({failure.GetType().Name}; details suppressed)");
		}
	}

	// 2. The EXISTING restore machinery, not a second copy path: ConfigRepairExecutor
	// quarantines a corrupt original before promoting the newest compatible backup,
	// and refuses a newer-schema backup outright. Recovery inherits those properties
	// by calling the same executor.
	//
	// EVERY ARM BELOW IS A REACHABLE OUTCOME OF ConfigRepairExecutor.Execute, READ OFF
	// THE EXECUTOR, and the wording distinguishes them because the route this action
	// promises — a RESTORE — only happened on ONE of them:
	//
	//   (a) BackupPromoted            Promote, ConfigRepairExecutor.cs:673-680.
	//                                 A byte copy replaced app.json with the backup.
	//   (b) DefaultsPersisted         PersistDefaults, :751-758. The destination was
	//                                 CORRUPT and NO backup was promotable, so the
	//                                 corrupt original was QUARANTINED and FRESH
	//                                 DEFAULTS were written. BackupPromoted is false and
	//                                 DestinationUsable is true, so this arm used to be
	//                                 reported as "already readable; no backup needed to
	//                                 be restored" — false in all three parts, and the
	//                                 defect this arm exists to remove. RestoreFromBackup
	//                                 promised a restore and performed NONE, so the
	//                                 honest status is Failed, not Completed.
	//   (c) !DestinationUsable        Degraded/UseInMemoryDefaults (:425-432, :786-793)
	//                                 or a hard stop (:532-542): nothing was written and
	//                                 the session runs on in-memory defaults.
	//   (d) UseLoadedDocument         the non-mutating arm of Perform (:532-542) with a
	//                                 SOUND destination. Here "already readable" is
	//                                 literally true and the action is a no-op, so it is
	//                                 the ONE case that may say so — and it is separated
	//                                 from (b) by DefaultsPersisted rather than by
	//                                 DestinationUsable, which is true in both.
	//
	// (b) and (d) are told apart ONLY by DefaultsPersisted, which is why that member is
	// read here rather than DestinationUsable alone: the two are indistinguishable from
	// (BackupPromoted, DestinationUsable) and that ambiguity WAS the defect.
	private SafeModeActionOutcome RestoreBackup()
	{
		try
		{
			string destination = Path.Combine(_storageRoot.Root, "app.json");
			ConfigRepairOutcome outcome = _repairExecutor.Execute(destination, _storageRoot.Backups, new AppDocument());

			if (outcome.BackupPromoted)
			{
				return new SafeModeActionOutcome(
					SafeModeActionIds.RestoreBackup,
					SafeModeActionStatus.Completed,
					"the newest compatible configuration backup was restored");
			}

			// (b) A repair happened, but it was a repair to defaults, NOT a restore. Say
			// which bytes moved: the corrupt original is in backups\corrupted and fresh
			// defaults now sit at the destination.
			if (outcome.DefaultsPersisted)
			{
				return new SafeModeActionOutcome(
					SafeModeActionIds.RestoreBackup,
					SafeModeActionStatus.Failed,
					outcome.OriginalPreserved
						? "no usable configuration backup could be restored; the unreadable configuration was preserved and safe defaults were written instead"
						: "no usable configuration backup could be restored; safe defaults were written instead");
			}

			// (d) The only truthful "already readable" case: the planner used the loaded
			// document and wrote nothing, so there was genuinely nothing to restore.
			if (outcome.DestinationUsable)
			{
				return new SafeModeActionOutcome(
					SafeModeActionIds.RestoreBackup,
					SafeModeActionStatus.Completed,
					"the configuration was already readable; no backup needed to be restored");
			}

			// (c) Nothing usable was restored, and nothing readable remains: if a repair
			// was attempted it did not produce a usable destination, so the session runs
			// on in-memory defaults.
			return new SafeModeActionOutcome(
				SafeModeActionIds.RestoreBackup,
				SafeModeActionStatus.Failed,
				"no usable configuration backup could be restored and the destination remains unusable");
		}
		catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
		{
			_logger.LogWarning(failure, "Safe mode restore-backup failed");
			return new SafeModeActionOutcome(
				SafeModeActionIds.RestoreBackup,
				SafeModeActionStatus.Failed,
				$"the restore could not be completed ({failure.GetType().Name}; details suppressed)");
		}
	}

	// 3. Hardcoded safe defaults written through the SAME atomic writer the ordinary
	// path uses. This is the "reset defaults" route.
	//
	// WHY THIS GOES THROUGH ConfigRepairExecutor RATHER THAN THE ATOMIC WRITER DIRECTLY,
	// which is a truthfulness fix rather than a refactor. The route used to construct an
	// AtomicConfigDocumentWriter inline and then assert "the previous file was backed up
	// first" UNCONDITIONALLY. That claim is FALSE whenever app.json is absent: the store
	// takes the File.Move branch and mints no backup at all. The executor reports the
	// clearance route it really took (NoExistingFileToProtect vs
	// OriginalPreservedBeforeAnyWrite), so this route can state which case actually
	// happened instead of asserting the backup case every time. The call is also safe on
	// a file that exists: an openable app.json with a compatible envelope plans
	// UseLoadedDocument, which MUTATES NOTHING — so a reset seeded a fresh default the
	// moment the file could not be read, and a present, readable document is left alone.
	private SafeModeActionOutcome ResetDefaults()
	{
		try
		{
			string destination = Path.Combine(_storageRoot.Root, "app.json");

			ConfigRepairOutcome outcome = _repairExecutor.Execute(destination, _storageRoot.Backups, new AppDocument());

			// A compatible backup was promoted instead of defaults (the repair route restores
			// the newest compatible backup before it falls back to defaults). That is a
			// completed repair with a usable destination, and it must be reported as such:
			// DefaultsPersisted is false here, so the branch below would otherwise misreport it
			// as "already readable" or as a failure.
			if (outcome.BackupPromoted)
			{
				return new SafeModeActionOutcome(
					SafeModeActionIds.ResetDefaults,
					SafeModeActionStatus.Completed,
					"a compatible configuration backup was restored instead of writing defaults");
			}

			// A present, readable file yields a non-mutating decision. Reporting a false
			// success there would be precisely the defect this slice removes, so say what
			// happened: nothing needed to be reset.
			if (!outcome.DefaultsPersisted)
			{
				return outcome.DestinationUsable
					? new SafeModeActionOutcome(
						SafeModeActionIds.ResetDefaults,
						SafeModeActionStatus.Completed,
						"the configuration was already readable; defaults did not need to be written")
					: new SafeModeActionOutcome(
						SafeModeActionIds.ResetDefaults,
						SafeModeActionStatus.Failed,
						"defaults could not be written and the existing configuration is not usable");
			}

			// TRUTHFUL BY CASE, never by assertion. The two routes differ in exactly what
			// the user needs to know: whether their previous bytes still exist. The
			// executor reports OriginalPreserved for a quarantined original and NOTHING
			// for an absent file, which is the case the old wording got wrong: an absent
			// app.json takes the File.Move branch in AtomicJsonFileStore and mints NO
			// backup, so "the previous file was backed up first" was false there.
			return new SafeModeActionOutcome(
				SafeModeActionIds.ResetDefaults,
				SafeModeActionStatus.Completed,
				outcome.OriginalPreserved
					? "safe defaults were written through the atomic writer; the previous file was preserved first"
					: "safe defaults were written through the atomic writer; no previous configuration existed");
		}
		catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
		{
			_logger.LogWarning(failure, "Safe mode reset-defaults failed");
			return new SafeModeActionOutcome(
				SafeModeActionIds.ResetDefaults,
				SafeModeActionStatus.Failed,
				$"defaults could not be written ({failure.GetType().Name}; details suppressed)");
		}
	}

	// 7. The picker's documents (history.json, rules.json), each through the SAME executor
	// as restore-backup, so the ordering invariant (preserve before write) and the
	// newer-schema hard stop are inherited, not restated. Per file (D-071), then summarised:
	//
	//   * absent              left absent - a first run is not damage, and nothing is written;
	//   * readable            UseLoadedDocument, nothing written;
	//   * unreadable          quarantined to backups\corrupted, then the newest compatible
	//                         backup is promoted, or defaults are written;
	//   * newer/older schema  hard stop: nothing written, reported Failed (only a matching
	//                         BroChoos build may touch it).
	//
	// Completed only when every file ends up usable. The reason is a fixed sentence per
	// shape, with counts only - no path, no file content.
	//
	// "USABLE" IS THE PICKER'S VERDICT, not the planner's. Each file is judged before and after
	// with the same check the picker's stores use (ConfigFileObserver.ObserveReadOutcome), so a
	// repair can never report "already readable" for a file the picker still rejects. Routing:
	//
	//   Loaded / Missing          nothing to do;
	//   Newer / Incompatible      never written by this build - counted as kept, action Failed;
	//   Corrupt / TooLarge /      the executor (with the oversized route on): preserve first,
	//   OpenFailed                then restore a backup or write defaults.
	//
	// After the executor, only a file the picker would now LOAD counts as repaired. Anything
	// else - still corrupt, moved but not replaced, a lock that never came free - is unusable.
	private SafeModeActionOutcome RepairSettings()
	{
		try
		{
			int restored = 0;
			int reset = 0;
			int kept = 0;
			int unusable = 0;

			// CONCRETE document types, one call each: the observer rejects an abstract
			// TDocument as a caller bug, so a loop over ConfigDocument would not work.
			Tally(RepairOne("history.json", new ActivationHistoryDocument(new ActivationHistorySettingsContract(), [])));
			Tally(RepairOne("rules.json", new RulesDocument([])));

			void Tally(SettingsFileRepair result)
			{
				switch (result)
				{
					case SettingsFileRepair.Restored:
						restored++;
						break;
					case SettingsFileRepair.Reset:
						reset++;
						break;
					case SettingsFileRepair.KeptForOtherVersion:
						kept++;
						break;
					case SettingsFileRepair.Unusable:
						unusable++;
						break;
					case SettingsFileRepair.NothingToDo:
					default:
						break;
				}
			}

			string counts = string.Create(
				CultureInfo.InvariantCulture,
				$"restored={restored} reset={reset} kept={kept} unusable={unusable}");

			return unusable + kept > 0
				? new SafeModeActionOutcome(
					SafeModeActionIds.RepairSettings,
					SafeModeActionStatus.Failed,
					$"some picker settings could not be repaired ({counts})")
				: new SafeModeActionOutcome(
					SafeModeActionIds.RepairSettings,
					SafeModeActionStatus.Completed,
					restored + reset == 0
						? $"the picker settings were already readable; nothing was changed ({counts})"
						: $"the picker settings were repaired; unreadable files were preserved first ({counts})");
		}
		catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
		{
			_logger.LogWarning(failure, "Safe mode repair-settings failed");
			return new SafeModeActionOutcome(
				SafeModeActionIds.RepairSettings,
				SafeModeActionStatus.Failed,
				$"the picker settings could not be repaired ({failure.GetType().Name}; details suppressed)");
		}
	}

	private SettingsFileRepair RepairOne<TDocument>(string fileName, TDocument defaults)
		where TDocument : ConfigDocument
	{
		string destination = Path.Combine(_storageRoot.Root, fileName);

		switch (_repairExecutor.ObserveReadOutcome<TDocument>(destination))
		{
			case ConfigReadOutcome.Loaded:
			case ConfigReadOutcome.Missing:
				return SettingsFileRepair.NothingToDo;
			// The planner hard-stops all three (D-066 / D-070): this build never writes them.
			case ConfigReadOutcome.Newer:
			case ConfigReadOutcome.Incompatible:
			case ConfigReadOutcome.InvalidEnvelope:
				return SettingsFileRepair.KeptForOtherVersion;
			case ConfigReadOutcome.Corrupt:
			case ConfigReadOutcome.TooLarge:
			case ConfigReadOutcome.OpenFailed:
			default:
				break;
		}

		ConfigRepairOutcome outcome = _repairExecutor.Execute(destination, _storageRoot.Backups, defaults, quarantineOversized: true);

		// Judged again with the picker's own check: only a file the picker now loads is fixed.
		return _repairExecutor.ObserveReadOutcome<TDocument>(destination) != ConfigReadOutcome.Loaded
			? SettingsFileRepair.Unusable
			: outcome.BackupPromoted ? SettingsFileRepair.Restored : SettingsFileRepair.Reset;
	}

	private enum SettingsFileRepair
	{
		NothingToDo,
		Restored,
		Reset,
		KeptForOtherVersion,
		Unusable,
	}

	// 4. Opens the data folder through a shell seam. Unavailable when no seam was
	// composed, because "we cannot do this here" is the honest answer and a fake
	// success would be precisely the defect this slice removes.
	private SafeModeActionOutcome OpenDataFolder()
	{
		if (_dataFolderOpener is null)
		{
			return new SafeModeActionOutcome(
				SafeModeActionIds.OpenDataFolder,
				SafeModeActionStatus.Unavailable,
				"opening a folder requires the Windows shell, which is not available in this composition");
		}

		try
		{
			return _dataFolderOpener.TryOpen(_storageRoot.Root)
				? new SafeModeActionOutcome(SafeModeActionIds.OpenDataFolder, SafeModeActionStatus.Completed, "the data folder was opened")
				: new SafeModeActionOutcome(SafeModeActionIds.OpenDataFolder, SafeModeActionStatus.Failed, "the shell declined to open the data folder");
		}
		catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
		{
			_logger.LogWarning(failure, "Safe mode open-data-folder failed");
			return new SafeModeActionOutcome(
				SafeModeActionIds.OpenDataFolder,
				SafeModeActionStatus.Failed,
				$"the data folder could not be opened ({failure.GetType().Name}; details suppressed)");
		}
	}

	// 5. Re-declares the per-user capability keys through the EXISTING registration
	// service. That service takes a fresh pre-write backup and appends one line per
	// action to system_changes.log, and it never writes UserChoice — recovery gains
	// no second registry path and no weaker invariant.
	//
	// THE REASON IS BUILT FROM THE RESULT, NEVER ASSERTED. The service refuses a run
	// with NO registry mutation at WindowsCapabilityRegistrationService.cs:55-58 (no
	// backup sink) and :73-78 (the backup write itself failed), and BOTH of those
	// refusals return BackupFilePath = null (Fail, :338-341). The old failure text
	// claimed "a pre-write backup was taken" unconditionally, which is false on exactly
	// those refusals — and a refusal is the ONLY picture a failed run ever shows the
	// user. So the two things the report may state are read off the result:
	//
	//   * WHETHER a backup exists is decided by BackupFilePath being non-null, and
	//     nothing else. A null path means the registry was untouched and NO backup was
	//     minted, so no arm may mention a backup.
	//   * WHAT went wrong is ErrorMessage — the service's OWN refusal reason. When it is
	//     present it is the honest answer and is passed through; when it is absent (a
	//     defensive case; the service always supplies one on failure) the report says
	//     only what it can support.
	//
	// D-075 / D-097. ErrorMessage is a refusal SENTENCE, but BackupFilePath is a FULL
	// filesystem path embedding the Windows account name, so that value NEVER reaches
	// the reason: only the boolean fact that a backup exists is rendered. ErrorMessage
	// is the service's own curated user-facing text and names no path (it is also the
	// console value D-299 already shows the operator), which is why it may be passed
	// through where BackupFilePath may not.
	private SafeModeActionOutcome RepairRegistration()
	{
		if (_registration is null)
		{
			return new SafeModeActionOutcome(
				SafeModeActionIds.RegistrationRepair,
				SafeModeActionStatus.Unavailable,
				"capability registration is not available in this composition");
		}

		try
		{
			CapabilityRegistrationResult result = _registration.RegisterAll();

			if (result.Succeeded)
			{
				// Success text stays silent about a backup UNLESS one was really taken: a
				// run can succeed by merely VERIFYING a pre-existing shape, in which case
				// claiming a snapshot would be the same untruth in the other direction.
				return result.BackupFilePath is null
					? new SafeModeActionOutcome(
						SafeModeActionIds.RegistrationRepair,
						SafeModeActionStatus.Completed,
						"BroChoos handler capabilities were re-declared (the default is still the user's choice)")
					: new SafeModeActionOutcome(
						SafeModeActionIds.RegistrationRepair,
						SafeModeActionStatus.Completed,
						"BroChoos handler capabilities were re-declared (the default is still the user's choice); the pre-write state was backed up first");
			}

			string refusal = result.ErrorMessage is { Length: > 0 } reason
				? reason
				: "capability registration did not succeed";

			return new SafeModeActionOutcome(
				SafeModeActionIds.RegistrationRepair,
				SafeModeActionStatus.Failed,
				result.BackupFilePath is null
					? refusal
					: $"{refusal} The pre-write state was backed up first.");
		}
		catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
		{
			_logger.LogWarning(failure, "Safe mode registration-repair failed");
			return new SafeModeActionOutcome(
				SafeModeActionIds.RegistrationRepair,
				SafeModeActionStatus.Failed,
				$"capability registration failed ({failure.GetType().Name}; details suppressed)");
		}
	}

	// 6. A synthetic link probe over the REAL detector. It observes only; it opens
	// nothing, sends nothing, and reports the count of candidates the detector found.
	// The probe exists so a user can confirm the discovery half works at all before
	// blaming their configuration.
	private SafeModeActionOutcome ProbeSyntheticLink()
	{
		if (_browserDetector is null)
		{
			return new SafeModeActionOutcome(
				SafeModeActionIds.SyntheticLinkProbe,
				SafeModeActionStatus.Unavailable,
				"browser discovery is not available in this composition");
		}

		try
		{
			long started = Stopwatch.GetTimestamp();
			IReadOnlyList<BrowserCandidate> candidates = _browserDetector.Detect();
			double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			int unresolved = candidates.Count(candidate => candidate.HasUnresolvedProfiles);

			return new SafeModeActionOutcome(
				SafeModeActionIds.SyntheticLinkProbe,
				SafeModeActionStatus.Completed,
				string.Concat(
					"discovery probe observed ",
					candidates.Count.ToString(CultureInfo.InvariantCulture),
					" candidate(s), ",
					unresolved.ToString(CultureInfo.InvariantCulture),
					" with unresolved profiles, in ",
					elapsedMs.ToString("F0", CultureInfo.InvariantCulture),
					" ms; nothing was launched"));
		}
		catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
		{
			_logger.LogWarning(failure, "Safe mode synthetic-link-probe failed");
			return new SafeModeActionOutcome(
				SafeModeActionIds.SyntheticLinkProbe,
				SafeModeActionStatus.Failed,
				$"the discovery probe could not run ({failure.GetType().Name}; details suppressed)");
		}
	}
}

// The shell seam for opening the data folder. Kept separate from IProcessStarter
// because this is an EXPLORER invocation of a directory path — a different shape
// from the browser/launch helper (UseShellExecute=false) and from the single
// sanctioned protocol-URI exception in WindowsDefaultAppsGuide. A caller that
// cannot offer a shell passes null and gets an honest Unavailable.
public interface IDataFolderOpener
{
	bool TryOpen(string directoryPath);
}
