namespace BroChoos.Core.Abstractions;

// D-130: the picker's F key is the "system" (fallback) choice, opening the URL
// through the operating system's own URL association instead of a detected
// candidate. It is deliberately separate from IBrowserLauncher: no candidate
// executable is resolved and no argv is built; the URL is handed to the shell
// association as-is, so there is nothing to reparse.
//
// The single-instance guard breaks the theoretical recursion when BroChoos
// itself is the registered handler: the new instance forwards the URL back to
// the running picker over the existing pipe instead of nesting pickers.
public interface ISystemFallbackLauncher
{
	// Same result shape as IBrowserLauncher so the picker renders a failure of
	// either surface identically. TargetId is a fixed token ("system"), never
	// a browser id.
	LaunchResult Launch(string url);
}
