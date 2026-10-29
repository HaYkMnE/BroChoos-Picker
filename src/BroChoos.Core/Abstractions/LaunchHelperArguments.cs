namespace BroChoos.Core.Abstractions;

// Which command-line dialect a launch target speaks. Derived from the BrowserId the
// LaunchHelperRequest already carries, so the request shape (and the App that builds it)
// does not change.
public enum BrowserLaunchFamily
{
	// Manual/portable entries and any id the detector does not emit. No browser-specific
	// switch is passed: the executable is user-picked and its dialect is not established.
	Unknown,

	// edge-*, chrome-*, brave-* — the detector's Chromium channel ids.
	Chromium,

	// firefox-* — the detector's Gecko channel ids.
	Gecko,
}

// F4: the launch helper used to append "--profile-directory=<id>" for every browser. That
// is a Chromium switch; with Firefox in the catalog the launch became
// "firefox.exe --profile-directory=default-release <url>". The argv is now built per family.
//
// GECKO GETS NO PROFILE ARGUMENT, deliberately. Checked against Mozilla's sources:
//   * "-P <name>" selects a profile by its profiles.ini Name, case-sensitive
//     (wiki.mozilla.org/Firefox/CommandLineOptions). When the name is not found,
//     nsToolkitProfileService::SelectStartupProfile returns NS_ERROR_SHOW_PROFILE_MANAGER,
//     i.e. the Profile Manager opens INSTEAD of the URL.
//   * "-no-remote" is no longer honoured: nsAppRunner.cpp strips it as "now unsupported".
//   * Remoting is per profile path (nsAppRunner.cpp: "Use the full profile path to identify
//     the profile"): "-P X <url>" while X is running hands the URL to that instance; while
//     a DIFFERENT profile is running it starts a second instance with X.
//   * Since Firefox 67 each install has its own default profile ([Install<hash>] Default=
//     in profiles.ini). The catalog's Firefox ProfileIds are the Name= values in file order,
//     with no default marker, and the App launches with the first one.
// So "-P <first name>" would often open a profile other than the one the user is using,
// start a second Firefox next to the running one, or show the Profile Manager. Passing
// nothing lets Firefox pick the install's own default and remote the URL into the running
// instance, which is the behaviour of clicking a link. Choosing a non-default Firefox
// profile needs the install->default mapping and is out of scope here.
public static class LaunchHelperArguments
{
	public static BrowserLaunchFamily ResolveFamily(string browserId)
	{
		ArgumentNullException.ThrowIfNull(browserId);

		return browserId.StartsWith("edge-", StringComparison.OrdinalIgnoreCase)
			|| browserId.StartsWith("chrome-", StringComparison.OrdinalIgnoreCase)
			|| browserId.StartsWith("brave-", StringComparison.OrdinalIgnoreCase)
			? BrowserLaunchFamily.Chromium
			: browserId.StartsWith("firefox-", StringComparison.OrdinalIgnoreCase)
			? BrowserLaunchFamily.Gecko
			: BrowserLaunchFamily.Unknown;
	}

	// Typed refusal for a private launch of a target with no known private switch. It travels
	// through the helper's existing Failed outcome (LaunchHelperOutcome.ErrorCode), the same
	// channel as launch.target-missing / launch.recursion-guard.
	public const string ErrorPrivateUnsupported = "launch.private-unsupported";

	// The ArgumentList for ProcessStartInfo. Structured argv (never one joined string), so a
	// profile directory containing spaces stays one token.
	//
	// PRIVATE MODE IS PER FAMILY (the App does not request it today; this is latent until it
	// does, and must be right before then):
	//   * chrome-*, brave-*  "--incognito"
	//   * edge-*             "--inprivate" (Edge's switch; it does not document --incognito)
	//   * firefox-*          "-private-window <url>": BrowserContentHandler.sys.mjs reads it
	//                        with cmdLine.handleFlagWithParam("private-window"), i.e. the URL is
	//                        the flag's SEPARATE next argument (no "="), and opens that URL in a
	//                        private window ("--private-window [<url>]" in its helpInfo). The
	//                        URL is therefore NOT repeated as a positional argument, which would
	//                        load it a second time in a normal window.
	//   * anything else      refused with ErrorPrivateUnsupported: opening a NORMAL window
	//                        when the user asked for a private one is the failure that must
	//                        not happen silently.
	public static bool TryBuild(LaunchHelperRequest request, out IReadOnlyList<string> arguments, out string? errorCode)
	{
		ArgumentNullException.ThrowIfNull(request);

		BrowserLaunchFamily family = ResolveFamily(request.BrowserId);
		string? privateSwitch = null;
		if (request.IsPrivate)
		{
			privateSwitch = family switch
			{
				BrowserLaunchFamily.Chromium => IsEdge(request.BrowserId) ? "--inprivate" : "--incognito",
				BrowserLaunchFamily.Gecko => "-private-window",
				BrowserLaunchFamily.Unknown => null,
				_ => null,
			};
			if (privateSwitch is null)
			{
				arguments = [];
				errorCode = ErrorPrivateUnsupported;
				return false;
			}
		}

		List<string> args = [];
		if (family == BrowserLaunchFamily.Chromium && !string.IsNullOrWhiteSpace(request.ProfileId))
		{
			args.Add($"--profile-directory={request.ProfileId}");
		}

		if (privateSwitch is not null)
		{
			args.Add(privateSwitch);
		}

		// For Gecko private this is the -private-window parameter; otherwise the positional URL.
		args.Add(request.Url);
		arguments = args;
		errorCode = null;
		return true;
	}

	public static IReadOnlyList<string> Build(LaunchHelperRequest request)
	{
		return TryBuild(request, out IReadOnlyList<string> arguments, out string? errorCode)
			? arguments
			: throw new InvalidOperationException($"Launch arguments refused ({errorCode}).");
	}

	private static bool IsEdge(string browserId)
	{
		return browserId.StartsWith("edge-", StringComparison.OrdinalIgnoreCase);
	}
}
