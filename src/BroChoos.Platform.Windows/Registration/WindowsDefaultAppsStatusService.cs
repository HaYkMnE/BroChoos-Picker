using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using Microsoft.Extensions.Logging;

namespace BroChoos.Platform.Windows.Registration;

// D-268/D-274/D-496: the read-only three-step status for http, https and .url.
//   Registered — the full D-187 capability declaration is in place (verified
//                against the actual expected value set, piece by piece);
//   Selected   — the CURRENT UserChoice ProgId, read as a snapshot. The value
//                is HKCU-writable and therefore untrusted: it is compared and
//                displayed, NEVER executed and never used as any kind of path
//                (it can never reach Process.Start through this code);
//   Handled    — reported honestly: 0A-4 has no activation history sink, so the
//                note says "no activation recorded yet" instead of inventing one.
public sealed class WindowsDefaultAppsStatusService(
	ILogger<WindowsDefaultAppsStatusService> logger,
	IEnvironmentProbe environment,
	IRegistryCapabilityStore store) : IDefaultAppsStatusService
{
	internal const string NoActivationRecordedNote = "no activation recorded yet";

	private readonly ILogger<WindowsDefaultAppsStatusService> _logger = logger;
	private readonly IEnvironmentProbe _environment = environment;
	private readonly IRegistryCapabilityStore _store = store;

	public IReadOnlyList<DefaultAppsEntryStatus> GetStatus()
	{
		// The open command is rebuilt from the same gated exe path the
		// registration service wrote, so verification compares against the true
		// declaration rather than a copy of the registry's own claim.
		string? exe = _environment.CurrentProcessPath;
		string? normalizedExe = exe is null ? null : PathSafety.NormalizeLocalExecutablePath(exe);
		if (normalizedExe is null && _logger.IsEnabled(LogLevel.Warning))
		{
			_logger.LogWarning("The running executable path could not be normalized; capability verification reports not-registered");
		}

		string openCommand = normalizedExe is null ? string.Empty : $"\"{normalizedExe}\" \"%1\"";

		List<DefaultAppsEntryStatus> entries = [];

		foreach (DefaultAppsTargetShape shape in CapabilityShape.Targets)
		{
			bool registered = normalizedExe is not null
				&& CapabilityShape.IsTargetRegistered(_store, shape, openCommand);

			string? selectedProgId = ReadSelectedProgId(shape);

			entries.Add(new DefaultAppsEntryStatus(
				shape.Target,
				shape.Scheme,
				shape.ProgId,
				registered,
				selectedProgId,
				NoActivationRecordedNote));
		}

		return entries;
	}

	// D-268 step 2, READ-ONLY. The UserChoice key is opened through the same
	// store the writer uses, but ReadKey is the one member the D-187 guard does
	// not guard: reading the user's own pick is legitimate, writing it is not.
	// A missing/unreadable key degrades to "no choice recorded", never to a key
	// creation attempt.
	private string? ReadSelectedProgId(DefaultAppsTargetShape shape)
	{
		RegistryKeyState state = _store.ReadKey(shape.UserChoiceKeyPath);
		if (!state.Exists)
		{
			return null;
		}

		if (state.ReadFailed)
		{
			_logger.LogWarning("The UserChoice key for {Scheme} could not be read; reporting no selection", shape.Scheme);
			return null;
		}

		foreach (RegistryValueState value in state.Values)
		{
			if (value.Name.Equals("ProgId", StringComparison.OrdinalIgnoreCase))
			{
				return value.Data;
			}
		}

		return null;
	}
}
