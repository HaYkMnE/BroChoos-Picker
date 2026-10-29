using BroChoos.App.Logging;
using BroChoos.App.Picker;
using BroChoos.App.HelperClients;
using BroChoos.App.Settings;
#if DEBUG
using BroChoos.App.Scaffold0A3;
using BroChoos.App.Scaffold0A5;
#endif
using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Cli;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BroChoos.App;

internal static class Program
{
	public static async Task<int> Main(string[] args)
	{
		// Local problem log (option A): installs the crash handlers first so a startup crash
		// is recorded too. Always %LocalAppData%\BroChoos\logs\problems.log, like the other
		// durable logs, also in portable mode. Nothing is created on disk until a write.
		ProblemLogWriter problemLog = ProblemLog.Install(new ProblemLogOptions(ProblemLogOptions.DefaultFilePath));

		PickerLog.Marker("PROCESS_START");

		// 0A-3 SCAFFOLD: the internal Avalonia probe trigger, DEBUG-ONLY, and the #if is the
		// point of this block rather than a detail of it.
		//
		// WHAT THE OLD COMMENT HERE CLAIMED, AND WHY IT WAS THE SAME FALLACY THE 0A-5 BLOCK
		// BELOW RECORDS. It said the public CLI contract was "byte-for-byte untouched by the
		// scaffold" because "the trigger is absent from HelpText and unknown to CliParser".
		// Both halves of that supporting fact were true; the conclusion did not follow. NOT
		// DOCUMENTED IS NOT NOT REACHABLE: the hook was UNCONDITIONAL, so it compiled into the
		// Release output, and the shipped exe answered to an undocumented argument in ANY
		// position - Array.Exists over the whole array, not a position test.
		//
		// MILDER THAN THE 0A-5 CASE, AND SAID SO PLAINLY RATHER THAN LEVELLED UP. Run() takes
		// no arguments and performs no config write, so there is no argument-supplied write
		// path here and nothing bypasses the safe-mode ordering. What was actually wrong is
		// narrower: a released binary accepted an argument that is not in its contract, and
		// started a UI lifetime on it. That is enough, because this app is a Windows URL
		// handler - the OS hands it URLs as command-line arguments via an open command of the
		// form "<exe>" "%1" - so every accepted argument is attack surface reachable by
		// anything that can ask the shell to open a URL.
		//
		// WHAT IS TRUE NOW. In a Release build neither this block nor the type it calls exists
		// (Scaffold0A3 is removed from compilation - see BroChoos.App.csproj), so the trigger
		// string is absent from the assembly and the contract is byte-for-byte unchanged AS
		// SHIPPED rather than merely as documented. That also settles the absolute claim in
		// BroChoos.Core.ExitCodes: whatever Avalonia's lifetime returns from Run(), no shipped
		// exe can return it. In Debug the probe stays reachable, and IsTriggered now requires
		// the trigger to be args[0] - so a forwarded URL cannot be mistaken for it.
		//
		// THE using FOR Scaffold0A3 IS GATED THE SAME WAY, deliberately: that pairing is what
		// makes a half-deletion a loud compile error instead of a silent regression.
#if DEBUG
		if (AvaloniaProbeRunner.IsTriggered(args))
		{
			return AvaloniaProbeRunner.Run();
		}
#endif

		// 0A-5 SCAFFOLD: the D-v3.5-493 kill-test child. DEBUG-ONLY, and the #if is the
		// point of this block rather than a detail of it.
		//
		// WHAT WAS WRONG BEFORE, stated plainly because the old comment here claimed the
		// opposite. This hook was UNCONDITIONAL, so it compiled into the Release output, and
		// the sentence it used to carry - that the public CLI contract "stays byte-for-byte
		// unchanged" - was false of the shipped exe: that exe accepted an argument it had
		// never accepted before and could return 70, 71 or 72, none of which appear in
		// BroChoos.Core.ExitCodes. "Absent from HelpText and unknown to CliParser" was true
		// and beside the point: NOT DOCUMENTED IS NOT NOT REACHABLE.
		//
		// WHY THAT WAS A SECURITY MATTER AND NOT UNTIDINESS. This app is a Windows URL
		// handler - the OS passes URLs as command-line arguments via an open command of the
		// form "<exe>" "%1" - and the child mode performs a REAL config write to a path taken
		// from those arguments. Worse, it ran HERE: before ConfigureServices, before the
		// single-instance guard, and before Bootstrap, which is where --safe-mode is honoured.
		// A write therefore happened on a path where safe mode was never consulted, inverting
		// the rule that safe mode is honoured before any config access.
		//
		// WHAT IS TRUE NOW. In a Release build this block does not exist and neither does the
		// type it calls (Scaffold0A5 is removed from compilation - see BroChoos.App.csproj),
		// so the trigger string is absent from the assembly and the public CLI contract is
		// byte-for-byte unchanged AS SHIPPED rather than merely as documented. In a Debug
		// build the mode is still reachable for the harness, and it is gated twice more
		// inside IsTriggered: the trigger must be args[0], and a corroborating environment
		// variable must be set - which a URL cannot do.
		//
		// Returns without building the service graph on purpose - the child needs one storage
		// type, not the whole app.
#if DEBUG
		if (AtomicWriteKillChild.IsTriggered(args))
		{
			return AtomicWriteKillChild.Run(args);
		}
#endif

		ServiceCollection services = new();
		ConfigureServices(services, problemLog);

		// Validate every registration eagerly so a broken graph fails fast at startup.
		await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

		// W8.1: --settings opens the Settings shell. It is a UI-ONLY mode that Core's
		// Bootstrap cannot serve (Core is UI-free by architecture), so it is routed here,
		// in the App, BEFORE Bootstrap runs — the same place the DEBUG scaffolds are
		// routed, but unconditionally and as a DOCUMENTED mode (CliParser recognizes it and
		// HelpText lists it). The flag is parsed by the single CliParser so the CLI contract
		// has one source of truth; a malformed command line is left to Bootstrap so its
		// existing exit-code mapping and value-free error handling are not duplicated.
		if (IsSettingsRequested(provider, args))
		{
			// The store is created ONLY on this path, so no other command line provisions
			// the storage root (StorageRootResolver's constructor creates directories).
			ParsedArgs parsedForSettings = provider.GetRequiredService<CliParser>().Parse(args);
			ISettingsStore settingsStore = provider
				.GetRequiredService<ISettingsStoreFactory>()
				.Create(parsedForSettings.PortableDataPath);

			return SettingsLauncher.Run(settingsStore, parsedForSettings.PortableDataPath);
		}

		// 0A-3: wire forwarded URLs to the picker session router BEFORE any run, so a
		// second instance reaching the pipe while the picker is up is handed to the
		// window (real handoff) instead of only being echoed. The guard stays UI-free:
		// it raises an event; the router decides where a forwarded URL goes.
		ForwardedUrlRouter router = provider.GetRequiredService<ForwardedUrlRouter>();
		router.Attach(provider.GetRequiredService<ISingleInstanceGuard>());

		Bootstrap bootstrap = provider.GetRequiredService<Bootstrap>();

		return await bootstrap.RunAsync(args, CancellationToken.None);
	}

	// W8.1. True only when the command line requests the Settings shell AND parses
	// cleanly. A malformed command line is NOT a settings request: it returns false here
	// so Bootstrap performs its usual parse and maps the failure to the existing exit
	// codes with the existing value-free error handling (no duplicated mapping).
	//
	// The parse happens twice on the settings path (here and again in Main) — deliberately,
	// because Bootstrap's Parse on the normal path must still own the failure mapping, and
	// hoisting a single parse out of Bootstrap would be a refactor outside W8.1's scope.
	// The cost is one cheap, side-effect-free parse.
	private static bool IsSettingsRequested(ServiceProvider provider, string[] args)
	{
		try
		{
			return provider.GetRequiredService<CliParser>().Parse(args).Settings;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	private static void ConfigureServices(IServiceCollection services, ProblemLogWriter problemLog)
	{
		_ = services.AddLogging(builder =>
		{
			_ = builder.AddSimpleConsole(options =>
			{
				options.SingleLine = true;
				options.IncludeScopes = false;
				options.TimestampFormat = "HH:mm:ss.fff ";
			});

			// Local-only file sink: Warning+ and allowlisted Information context steps, with
			// templates, codes and exception TYPE names only (ProblemLog.cs).
			_ = builder.AddProvider(new ProblemLogLoggerProvider(problemLog));

			_ = builder.SetMinimumLevel(LogLevel.Information);
		});

		_ = services.AddCoreServices();
		_ = services.AddWindowsPlatformServices();

		// 0A-3 picker stack (App-only): the router is process-wide; the picker runs
		// the whole Avalonia lifetime of one URL run on its own STA thread.
		_ = services.AddSingleton<ForwardedUrlRouter>();
		_ = services.AddSingleton<IPicker, AvaloniaPicker>();

		_ = services.AddSingleton<IStorageHelperProcessRunner, StorageHelperProcessRunner>();
		_ = services.AddSingleton<IStorageHelperClient, StorageHelperClient>();
		_ = services.AddSingleton<IUpdateHelperClient, NoOpUpdateHelperClient>();
	}
}
