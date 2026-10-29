// THE WHOLE FILE IS DEBUG-ONLY, and the #if wraps the using, the namespace and the type
// declaration rather than only the members. That is the point: it is a SECOND, INDEPENDENT gate
// beside the configuration-scoped Compile Remove in BroChoos.App.csproj, so deleting either one
// leaves the other standing. Mirrors what Scaffold0A5 already does, deliberately - one construct
// to understand for both scaffolds instead of two.
//
// WHY A THROWAWAY PROBE NEEDED GATING AT ALL. This application is a Windows URL handler: the OS
// hands it URLs as command-line arguments through a registration whose open command is
// "<exe>" "%1" (see WindowsCapabilityRegistrationService). Ungated, the shipped Release exe
// accepted an undocumented argument in ANY position and started a UI lifetime on it. Milder than
// the 0A-5 case - Run() takes no arguments and writes no config, so there is no
// argument-supplied write path - and still an argument a released binary should not answer to.
#if DEBUG
using Avalonia;

namespace BroChoos.App.Scaffold0A3;

/// <summary>
/// 0A-3 SCAFFOLD - THROWAWAY. Starts the Avalonia desktop lifetime so the framework can be
/// observed running inside this solution. Not part of the public CLI contract, and after the
/// gating that is true OF THE SHIPPED BINARY rather than merely of the documentation: this type
/// does not exist in a non-Debug build, so <see cref="TriggerArgument"/> is absent from the
/// Release assembly altogether. In a Debug build <c>Program</c> intercepts the trigger before
/// the argument string reaches <c>CliParser</c>, so the 0A-2 contract is untouched there too.
/// </summary>
internal static class AvaloniaProbeRunner
{
	/// <summary>
	/// Deliberately verbose and namespaced so it cannot collide with a real option and is
	/// obviously not a user-facing switch. It is absent from HelpText on purpose.
	/// </summary>
	public const string TriggerArgument = "--internal-0a3-avalonia-probe";

	public static bool IsTriggered(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		// THE TRIGGER MUST BE args[0], NOT MERELY PRESENT. This was Array.Exists over every
		// argument, so ANY position matched - and the comment justifying that reproduced the
		// fallacy NOT DOCUMENTED IS NOT NOT REACHABLE. A URL handler is invoked as
		// "<exe>" "%1", so a URL arrives as args[0]; requiring position 0 means a forwarded URL
		// can never be mistaken for the trigger no matter what it contains, because a string
		// that IS the trigger is not a URL.
		//
		// The empty case is guarded explicitly rather than left to indexing: args.Length == 0 is
		// an ordinary invocation (the exe double-clicked), not an error, and args[0] on it would
		// be an IndexOutOfRangeException from a method whose job is to answer a question.
		//
		// Ordinal, case-sensitive: an internal trigger takes no spelling liberties, unlike
		// the public options which are matched case-insensitively by CliParser.
		return args.Length > 0 && string.Equals(args[0], TriggerArgument, StringComparison.Ordinal);
	}

	/// <summary>
	/// Runs the Avalonia lifetime on a dedicated STA thread and returns its exit code.
	/// </summary>
	public static int Run()
	{
		int exitCode = 0;

		// Main is async, so it cannot carry [STAThread]; Windows UI work (OLE clipboard,
		// shell dialogs) expects an STA apartment, so the lifetime gets its own STA thread.
		Thread uiThread = new(() => exitCode = RunLifetime())
		{
			Name = "BroChoos-0A3-Avalonia-Probe",
			IsBackground = false,
		};

		uiThread.SetApartmentState(ApartmentState.STA);
		uiThread.Start();
		uiThread.Join();

		return exitCode;
	}

	private static int RunLifetime()
	{
		Console.WriteLine("[0A-3 probe] AVALONIA_START apartment=" + Thread.CurrentThread.GetApartmentState());

		int exitCode = AppBuilder.Configure<AvaloniaProbeApp>()
			.UsePlatformDetect()
			.StartWithClassicDesktopLifetime([]);

		Console.WriteLine($"[0A-3 probe] AVALONIA_EXIT code={exitCode}");
		return exitCode;
	}
}
#endif
