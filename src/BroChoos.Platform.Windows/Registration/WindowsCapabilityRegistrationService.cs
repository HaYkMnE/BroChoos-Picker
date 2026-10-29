using System.Text.Json;
using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using Microsoft.Extensions.Logging;

namespace BroChoos.Platform.Windows.Registration;

// D-187/D-268/D-273/D-274/D-291/D-299: the HKCU capability declaration flow.
//
// Order of operations (both directions): snapshot -> backup file FIRST -> only
// then registry writes/deletes -> verification through CapabilityShape -> one
// JSONL line per action appended to system_changes.log (D-291). A missing
// backup sink refuses the run with NO registry mutation.
//
// system_changes.log schema (locked by D-291): one JSON object per line with
// exactly four fields — ts (UTC ISO-8601), action (fixed token), result
// (0 = ok, 1 = failed), path (redacted: HKCU-relative product-owned paths or
// "backups\<file>"; never a full filesystem path, never a URL, never PII).
public sealed class WindowsCapabilityRegistrationService(
	ILogger<WindowsCapabilityRegistrationService> logger,
	IEnvironmentProbe environment,
	IRegistryCapabilityStore store,
	IRegistrationArtifacts artifacts,
	TimeProvider clock) : ICapabilityRegistrationService
{
	internal const string ActionBackup = "registration.backup";
	internal const string ActionWrite = "registration.write";
	internal const string ActionDelete = "registration.delete";
	internal const string ActionVerify = "registration.verify";

	private static readonly JsonSerializerOptions BackupJsonOptions = new() { WriteIndented = true };

	private readonly ILogger<WindowsCapabilityRegistrationService> _logger = logger;
	private readonly IEnvironmentProbe _environment = environment;
	private readonly IRegistryCapabilityStore _store = store;
	private readonly IRegistrationArtifacts _artifacts = artifacts;
	private readonly TimeProvider _clock = clock;

	// Test seam: the real seam shape comes from DI in production.
	internal WindowsCapabilityRegistrationService(
		ILogger<WindowsCapabilityRegistrationService> logger,
		IEnvironmentProbe environment,
		IRegistryCapabilityStore store,
		IRegistrationArtifacts artifacts)
		: this(logger, environment, store, artifacts, TimeProvider.System)
	{
	}

	public CapabilityRegistrationResult RegisterAll()
	{
		// Fail-closed preconditions: no backup sink or no trustworthy exe path
		// means NO registry mutation whatsoever. The exe goes through the same
		// PathSafety gate as any launch target because it becomes a shell
		// command string Windows will later resolve.
		if (!_artifacts.IsAvailable)
		{
			return Fail("LocalAppData is unavailable; refusing to modify the registry without a backup sink.");
		}

		string? normalizedExe = _environment.CurrentProcessPath is { } exe
			? PathSafety.NormalizeLocalExecutablePath(exe)
			: null;
		if (normalizedExe is null)
		{
			return Fail("The running executable path could not be normalized to a local drive-letter path.");
		}

		string openCommand = $"\"{normalizedExe}\" \"%1\"";
		string applicationIcon = $"{normalizedExe},0";
		DateTimeOffset now = _clock.GetUtcNow();
		List<string> logLines = [];

		string? backupPath = TakeBackup("register", now, openCommand, logLines);
		if (backupPath is null)
		{
			AppendLogLinesBestEffort(logLines);
			return Fail("The pre-write backup could not be written; the registry was not touched.");
		}

		string? failure = null;
		foreach (CapabilityWriteOperation operation in CapabilityShape.BuildWriteOperations(openCommand, applicationIcon))
		{
			try
			{
				_store.SetValue(operation.Path, operation.Name, operation.Data);
				logLines.Add(BuildLogLine(ActionWrite, 0, Hkcu(operation.Path), now));
			}
			catch (Exception ex) when (IsRecoverableFailure(ex))
			{
				failure = "A capability key could not be written (permissions or hive state).";
				logLines.Add(BuildLogLine(ActionWrite, 1, Hkcu(operation.Path), now));
				_logger.LogError(ex, "Capability registration failed mid-write; a partial capability shape may exist and the backup holds the pre-write state");
				break;
			}
		}

		IReadOnlyList<RegisteredTypeOutcome> outcomes = VerifyTargets(openCommand, failure);
		bool verified = failure is null && outcomes.All(outcome => outcome.Registered);
		logLines.Add(BuildLogLine(ActionVerify, verified ? 0 : 1, @"HKCU\Software\Classes", now));
		AppendLogLinesBestEffort(logLines);

		return new CapabilityRegistrationResult(
			verified,
			outcomes,
			[],
			backupPath,
			verified
				? null
				: failure ?? "Post-write verification did not find the full capability shape; the pre-write state is in the backup.");
	}

	public CapabilityRegistrationResult UnregisterAll()
	{
		// D-299: backup BEFORE any removal, then remove ONLY BroChoos-owned
		// pieces, then verify through the three-step model. The exe path is not
		// needed: removal is keyed by the fixed product-owned paths, never by
		// registry CONTENT (a forged value under one of our keys is removed as
		// confidently as a genuine one — it lives under a BroChoos-owned path).
		if (!_artifacts.IsAvailable)
		{
			return Fail("LocalAppData is unavailable; refusing to modify the registry without a backup sink.");
		}

		DateTimeOffset now = _clock.GetUtcNow();
		List<string> logLines = [];

		string? backupPath = TakeBackup("unregister", now, null, logLines);
		if (backupPath is null)
		{
			AppendLogLinesBestEffort(logLines);
			return Fail("The pre-removal backup could not be written; the registry was not touched.");
		}

		List<string> removed = [];
		string? failure = null;

		foreach (RemovalOperation removal in BuildRemovalPlan())
		{
			try
			{
				bool actuallyRemoved = removal.Execute();
				logLines.Add(BuildLogLine(ActionDelete, 0, Hkcu(removal.Path), now));
				if (actuallyRemoved)
				{
					removed.Add(Hkcu(removal.Path) + removal.Suffix);
				}
			}
			catch (Exception ex) when (IsRecoverableFailure(ex))
			{
				failure = "A capability key could not be removed (permissions or hive state).";
				logLines.Add(BuildLogLine(ActionDelete, 1, Hkcu(removal.Path), now));
				_logger.LogError(ex, "Capability unregistration failed mid-delete; the backup holds the pre-removal state");
				break;
			}
		}

		// Verification against an empty expected state: after unregister every
		// target must report NOT registered (any exe path passes because a
		// missing shape matches nothing, which is itself the expectation).
		List<RegisteredTypeOutcome> outcomes = [];
		foreach (DefaultAppsTargetShape shape in CapabilityShape.Targets)
		{
			bool stillRegistered = CapabilityShape.IsTargetRegistered(_store, shape, string.Empty);
			outcomes.Add(new RegisteredTypeOutcome(shape.Target, shape.Scheme, stillRegistered,
				stillRegistered && failure is null ? "Capability keys are still present after removal." : null));
		}

		bool verified = failure is null && outcomes.All(outcome => !outcome.Registered);
		logLines.Add(BuildLogLine(ActionVerify, verified ? 0 : 1, @"HKCU\Software\Classes", now));
		AppendLogLinesBestEffort(logLines);

		return new CapabilityRegistrationResult(
			verified,
			outcomes,
			removed,
			backupPath,
			verified
				? null
				: failure ?? "Some BroChoos capability keys are still present after removal (see the backup).");
	}

	// -----------------------------------------------------------------------

	private string? TakeBackup(string operation, DateTimeOffset now, string? openCommand, List<string> logLines)
	{
		// D-299 base name stays recognizable ("registration-{utc-timestamp}");
		// the -{4 hex} suffix makes two runs inside the SAME millisecond produce
		// two distinct files instead of silently overwriting the older pre-state
		// (Windows rtc granularity is coarse and tests pin a fixed clock; a pure
		// timestamp name is not unique enough). 4 hex chars = 65536 slots.
		string suffix = Guid.NewGuid().ToString("N")[..4];
		string fileName = $"registration-{now.UtcDateTime:yyyyMMdd'T'HHmmssfff'Z'}-{suffix}.json";
		try
		{
			string fullPath = _artifacts.WriteBackupFile(fileName, BuildBackupJson(operation, now, openCommand));
			// Redacted by construction (D-291): only the relative location, never
			// the %LocalAppData% prefix (which would carry the user name).
			logLines.Add(BuildLogLine(ActionBackup, 0, "backups\\" + fileName, now));
			return fullPath;
		}
		catch (Exception ex) when (IsRecoverableFailure(ex))
		{
			logLines.Add(BuildLogLine(ActionBackup, 1, "backups\\" + fileName, now));
			_logger.LogError(ex, "The registration backup could not be written");
			return null;
		}
	}

	private string BuildBackupJson(string operation, DateTimeOffset takenUtc, string? openCommand)
	{
		List<object> keys = [];

		foreach (string path in CapabilityShape.SnapshotPaths())
		{
			RegistryKeyState state = _store.ReadKey(path);
			keys.Add(new
			{
				path = "HKCU\\" + path,
				exists = state.Exists,
				readFailed = state.ReadFailed,
				values = state.Values.Select(value => new
				{
					name = value.Name.Length == 0 ? "(Default)" : value.Name,
					kind = value.Kind,
					data = value.Data,
				}).ToList(),
				subKeyNames = state.SubKeyNames,
			});
		}

		var payload = new
		{
			version = 1,
			operation,
			takenUtc = takenUtc.UtcDateTime,
			hive = "HKCU (current user only; no HKLM, D-273)",
			declaredOpenCommand = openCommand,
			keys,
		};

		return JsonSerializer.Serialize(payload, BackupJsonOptions);
	}

	private List<RegisteredTypeOutcome> VerifyTargets(string openCommand, string? priorFailure)
	{
		List<RegisteredTypeOutcome> outcomes = [];

		foreach (DefaultAppsTargetShape shape in CapabilityShape.Targets)
		{
			bool registered = CapabilityShape.IsTargetRegistered(_store, shape, openCommand);
			outcomes.Add(new RegisteredTypeOutcome(shape.Target, shape.Scheme, registered,
				registered || priorFailure is not null ? priorFailure : "The declared capability shape is incomplete after writing."));
		}

		return outcomes;
	}

	private sealed record RemovalOperation(string Path, string Suffix, Func<bool> Execute);

	// Removes ONLY BroChoos-owned pieces (D-299): the whole BroChoos-named trees
	// go (they cannot contain foreign content by construction), while shared
	// scheme keys lose ONLY the BroChoos-owned verb — the "URL Protocol" value
	// and the default value of shell\open\command — and every key then
	// disappears ONLY when it has nothing else left: DeleteSubKeyIfEmpty walks
	// up command -> open -> shell -> scheme root and stops at the first key
	// that still holds a foreign subkey or value. A DeleteSubKeyTree on the
	// shared <scheme>\shell subtree is emphatically NOT in this plan: it would
	// destroy foreign verbs/values parked under a key BroChoos does not own.
	private List<RemovalOperation> BuildRemovalPlan()
	{
		List<RemovalOperation> plan =
		[
			new RemovalOperation(CapabilityShape.RegisteredApplicationsPath, " (value \"BroChoos\")",
				() => _store.DeleteValue(CapabilityShape.RegisteredApplicationsPath, CapabilityShape.RegisteredApplicationsValueName)),
			new RemovalOperation(CapabilityShape.AppClientKeyPath, " (key tree)",
				() => _store.DeleteSubKeyTree(CapabilityShape.AppClientKeyPath)),
			new RemovalOperation(@"Software\Classes\" + CapabilityShape.ProgIdUrl, " (key tree)",
				() => _store.DeleteSubKeyTree(@"Software\Classes\" + CapabilityShape.ProgIdUrl)),
			new RemovalOperation(@"Software\Classes\" + CapabilityShape.ProgIdUrlFile, " (key tree)",
				() => _store.DeleteSubKeyTree(@"Software\Classes\" + CapabilityShape.ProgIdUrlFile)),
		];

		foreach (DefaultAppsTargetShape shape in CapabilityShape.Targets)
		{
			string commandKeyPath = shape.SchemeKeyPath + @"\shell\open\command";
			plan.Add(new RemovalOperation(shape.SchemeKeyPath, " (value \"URL Protocol\")",
				() => _store.DeleteValue(shape.SchemeKeyPath, "URL Protocol")));
			plan.Add(new RemovalOperation(commandKeyPath, " (default value)",
				() => _store.DeleteValue(commandKeyPath, string.Empty)));
			plan.Add(new RemovalOperation(commandKeyPath, " (empty key)",
				() => _store.DeleteSubKeyIfEmpty(commandKeyPath)));
			plan.Add(new RemovalOperation(shape.SchemeKeyPath + @"\shell\open", " (empty key)",
				() => _store.DeleteSubKeyIfEmpty(shape.SchemeKeyPath + @"\shell\open")));
			plan.Add(new RemovalOperation(shape.SchemeKeyPath + @"\shell", " (empty key)",
				() => _store.DeleteSubKeyIfEmpty(shape.SchemeKeyPath + @"\shell")));
			plan.Add(new RemovalOperation(shape.SchemeKeyPath, " (empty key)",
				() => _store.DeleteSubKeyIfEmpty(shape.SchemeKeyPath)));
		}

		return plan;
	}

	private void AppendLogLinesBestEffort(List<string> logLines)
	{
		try
		{
			_artifacts.AppendLogLines(logLines);
		}
		catch (Exception ex) when (IsRecoverableFailure(ex))
		{
			// The registry actions ALREADY happened; a failed audit trail must not
			// hide that fact. The console still shows the full outcome (backup
			// path, per-type status), so the operator is not blind.
			_logger.LogWarning(ex, "system_changes.log could not be appended; the registry actions above DID still happen");
		}
	}

	private static string BuildLogLine(string action, int result, string path, DateTimeOffset timestamp)
	{
		// Locked D-291 schema: exactly ts / action / result / path. The path
		// value is a product-owned HKCU-relative path (or "backups\<file>"),
		// which contains no URLs, no full filesystem paths and no PII.
		// DateTime with Kind=Utc serializes with the trailing Z.
		return JsonSerializer.Serialize(new
		{
			ts = timestamp.UtcDateTime,
			action,
			result,
			path,
		});
	}

	private static string Hkcu(string relativePath)
	{
		return "HKCU\\" + relativePath;
	}

	private static CapabilityRegistrationResult Fail(string reason)
	{
		return new CapabilityRegistrationResult(false, [], [], null, reason);
	}

	private static bool IsRecoverableFailure(Exception ex)
	{
		// Same policy as WindowsBrowserLauncher: every failure becomes an honest
		// result except the two that mean the process itself is no longer sane.
		return ex is not OutOfMemoryException and not StackOverflowException;
	}
}
