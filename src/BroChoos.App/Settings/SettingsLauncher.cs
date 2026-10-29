using Avalonia;
using BroChoos.Core.Storage;

namespace BroChoos.App.Settings;

// W8.1 — the production entry point that actually OPENS SettingsWindow.
//
// WHY A CLI ENTRY AND NOT A TRAY/MENU BUTTON. The spec and the implementation plan name no
// tray icon, no picker menu item, and no in-window Settings button for Phase 0 — the only
// entries they name anywhere are command-line modes (--safe-mode, --register,
// --verify-defaults, --open-default-apps). Inventing a visible affordance would be a design
// decision this task is explicitly told not to freelance. `--settings` is therefore the
// minimal truthful entry: it is a documented CLI mode like every other, it adds no visual
// novelty, and it opens the REAL window composed with the REAL settings store.
//
// WHY IT LIVES IN THE APP AND NOT IN Bootstrap. Core is UI-free by architecture (Program's
// comments and the App_References... architecture tests), so a Core runner cannot construct
// an Avalonia window. Program routes the parsed flag to this launcher instead, exactly the
// way it routes the DEBUG scaffolds — but unconditionally and as a documented mode. The
// flag is still parsed by the single CliParser so the CLI contract has one source of truth.
internal static class SettingsLauncher
{
	// Runs one whole Avalonia desktop lifetime for the settings window on its own STA
	// thread, mirroring AvaloniaPicker.ShowAsync: Main is async and cannot be [STAThread],
	// and Avalonia windowing requires an STA apartment. Returns the exit code Avalonia
	// hands back from the lifetime (Success for an ordinary close).
	public static int Run(ISettingsStore settingsStore, string? portableDataPath)
	{
		ArgumentNullException.ThrowIfNull(settingsStore);

		int exitCode = 0;

		Thread uiThread = new(() => exitCode = RunWindow(settingsStore, portableDataPath))
		{
			Name = "BroChoos-Settings-UI",
			IsBackground = false,
		};

		uiThread.SetApartmentState(ApartmentState.STA);
		uiThread.Start();
		uiThread.Join();

		return exitCode;
	}

	private static int RunWindow(ISettingsStore settingsStore, string? portableDataPath)
	{
		return AppBuilder
			.Configure(() => new SettingsApp(settingsStore, portableDataPath))
			.UsePlatformDetect()
			.StartWithClassicDesktopLifetime([]);
	}
}
