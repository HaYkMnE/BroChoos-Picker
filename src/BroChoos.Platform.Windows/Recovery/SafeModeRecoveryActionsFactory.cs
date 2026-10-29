using BroChoos.Core.Abstractions;
using BroChoos.Core.Runners;
using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace BroChoos.Platform.Windows.Recovery;

// W8.2: the DI-SAFE way for Core to obtain the acting recovery side. This exists
// for exactly the reason IBrowserCatalogPersistenceFactory exists, and the reason
// is measured rather than stylistic: Program builds the container and resolves
// Bootstrap BEFORE Bootstrap.RunAsync parses the command line, so anything
// reachable from that graph is constructed while --safe-mode and --portable-data
// are still unknown. Constructing StorageRootResolver IS provisioning (its
// constructor creates Root/backups/logs/temp), so a registered resolver would
// create four directories on a command line that must never mutate anything.
//
// The factory's own constructor therefore performs ZERO I/O: it holds only
// already-registered, stateless seams and composes the resolver inside Create,
// where the parsed --portable-data value is finally known.
//
// IT IMPLEMENTS THE CORE-OWNED PROVIDER DIRECTLY. Core cannot reference this
// assembly, so the Core runner consumes ISafeModeRecoveryActionsProvider; rather
// than registering an adapter that only forwards, this type IS that provider.
// One interface would have been enough if Core could name the assembly, and the
// extra name exists purely for the direction of the dependency.
public interface ISafeModeRecoveryActionsFactory : ISafeModeRecoveryActionsProvider
{
}

public sealed class SafeModeRecoveryActionsFactory(
	ILogger<SafeModeRecoveryActions> logger,
	IEnvironmentProbe environment,
	IStorageFileSystem provisioning,
	ConfigFileObserver observer,
	TimeProvider timeProvider,
	IStorageRepairFileSystem? repairFileSystem = null,
	ICapabilityRegistrationService? registration = null,
	IBrowserDetector? browserDetector = null,
	IDataFolderOpener? dataFolderOpener = null) : ISafeModeRecoveryActionsFactory
{
	private readonly ILogger<SafeModeRecoveryActions> _logger = logger;
	private readonly IEnvironmentProbe _environment = environment;
	private readonly IStorageFileSystem _provisioning = provisioning;
	private readonly ConfigFileObserver _observer = observer;
	private readonly TimeProvider _timeProvider = timeProvider;
	private readonly IStorageRepairFileSystem _repairFileSystem = repairFileSystem ?? new SystemStorageRepairFileSystem();
	private readonly ICapabilityRegistrationService? _registration = registration;
	private readonly IBrowserDetector? _browserDetector = browserDetector;
	private readonly IDataFolderOpener? _dataFolderOpener = dataFolderOpener;

	public ISafeModeRecoveryActions Create(string? portableDataArgument)
	{
		// The provisioning moment, and the ONLY one this type owns. The resolver is
		// built here — never held as a field — so a second call with a different
		// argument resolves a different root instead of silently reusing the first.
		StorageRootResolver storageRoot = new(_environment, _provisioning, portableDataArgument);

		ConfigRepairExecutor repairExecutor = new(
			_observer,
			_repairFileSystem,
			_provisioning,
			new AtomicConfigDocumentWriter(storageRoot.Backups),
			_timeProvider,
			new InMemoryConfigRepairLog());

		return new SafeModeRecoveryActions(
			_logger,
			storageRoot,
			repairExecutor,
			_registration,
			_browserDetector,
			_dataFolderOpener);
	}

	// READ-ONLY DESCRIBE, AND IT MUST NOT PROVISION. A bare --safe-mode reports the
	// contract through this member; composing the resolver here would create the
	// storage tree on a command line that must touch nothing, which is precisely the
	// property ServiceCompositionTests pins. The answers are therefore derived from
	// the COMPOSITION alone — whether the seam an action needs was registered — and
	// never from a resolved root.
	public SafeModeActionOutcome Describe(string actionId)
	{
		ArgumentNullException.ThrowIfNull(actionId);

		return actionId switch
		{
			SafeModeActionIds.DiagnosticsExport or SafeModeActionIds.RestoreBackup or SafeModeActionIds.ResetDefaults or SafeModeActionIds.RepairSettings =>
				Ready(actionId, "available once the data folder is prepared; write access is checked when the action runs"),
			SafeModeActionIds.OpenDataFolder => _dataFolderOpener is null
				? Missing(actionId, "opening a folder requires the Windows shell, which is not available in this composition")
				: Ready(actionId, "open the data folder in the shell"),
			SafeModeActionIds.RegistrationRepair => _registration is null
				? Missing(actionId, "capability registration is not available in this composition")
				: Ready(actionId, "re-declare BroChoos handler capabilities (never writes UserChoice)"),
			SafeModeActionIds.SyntheticLinkProbe => _browserDetector is null
				? Missing(actionId, "browser discovery is not available in this composition")
				: Ready(actionId, "observe browser discovery without launching anything"),
			_ => Missing(actionId, "this build does not implement the requested recovery action"),
		};
	}

	private static SafeModeActionOutcome Ready(string actionId, string capability)
	{
		// Ready, NOT Completed. Describe performs no work, so claiming Completed here
		// would state that an observable effect was produced when none was — the exact
		// untruth this slice exists to remove. Ready says what is true: the route is
		// offered and will run when selected.
		return new SafeModeActionOutcome(actionId, SafeModeActionStatus.Ready, $"ready: {capability}");
	}

	private static SafeModeActionOutcome Missing(string actionId, string reason)
	{
		return new SafeModeActionOutcome(actionId, SafeModeActionStatus.Unavailable, reason);
	}
}

// The production shell implementation for opening the data folder. `explorer.exe`
// is started with UseShellExecute=false and the directory as ONE ArgumentList
// element, matching the repository's structured-argument rule (D-183/D-199): no
// shell string is composed and no user value is concatenated. The default-apps
// guide's protocol-URI exception does NOT apply here — this starts a real
// executable.
public sealed class ExplorerDataFolderOpener(
	ILogger<ExplorerDataFolderOpener> logger,
	IProcessStarter processStarter) : IDataFolderOpener
{
	private const string ExplorerExecutable = "explorer.exe";

	private readonly ILogger<ExplorerDataFolderOpener> _logger = logger;
	private readonly IProcessStarter _processStarter = processStarter;

	public bool TryOpen(string directoryPath)
	{
		System.Diagnostics.ProcessStartInfo startInfo = new()
		{
			FileName = ExplorerExecutable, // compile-time constant, never user input
			UseShellExecute = false,
			ArgumentList = { directoryPath }, // the folder as ONE structured argument
		};

		try
		{
			return _processStarter.Start(startInfo);
		}
		catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
		{
			// Fail-open: the recovery surface still reports the outcome; an unopenable
			// shell never crashes recovery.
			_logger.LogWarning(failure, "The data folder could not be opened by the shell");
			return false;
		}
	}
}
