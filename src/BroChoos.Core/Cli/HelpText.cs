namespace BroChoos.Core.Cli;

public static class HelpText
{
	public static string Render()
	{
		return """
		BroChoos Picker — choose which browser and profile opens each link

		Usage:
		  BroChoos.exe <URL>          Show the Picker for a URL
		  BroChoos.exe --no-ui <URL>  Typed compatibility input: fails closed and opens nothing (explicit choice required)
		  BroChoos.exe --safe-mode    Start in recovery mode
		  BroChoos.exe --safe-mode --safe-mode-action <id>
		                              Run one recovery action and exit (id: diagnostics-export,
		                              restore-backup, reset-defaults, open-data-folder,
		                              registration-repair, synthetic-link-probe, repair-settings)
		  BroChoos.exe --settings     Open the Settings shell and exit
		  BroChoos.exe --help         Show this help

		Default Apps capabilities (per-user, HKCU only; never writes UserChoice):
		  BroChoos.exe --register          Declare http/https/.url handler capabilities
		  BroChoos.exe --unregister        Remove only BroChoos-owned capability keys (backup first)
		  BroChoos.exe --verify-defaults   Three-step status: registered / selected / handled
		  BroChoos.exe --open-default-apps Guide + open Windows Settings: Default apps

		Data location (the path is a separate token, never joined to the option):
		  BroChoos.exe --portable-data <path>  Use <path> as the data root instead of the default location

		Development build. Shows the Picker; the Picker discovers browsers only after first-run consent.
		Registering declares capabilities only; the default is always picked by the user
		in Windows Settings.
		""";
	}
}
