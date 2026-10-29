namespace BroChoos.Core;

// THE PUBLIC EXIT-CODE CONTRACT. Every code a shipped BroChoos.exe can return is
// listed below; nothing else is contractual.
//
// THAT FIRST SENTENCE IS ABSOLUTE, SO WHAT IT RESTS ON IS RECORDED HERE. It was
// written while a SECOND ungated scaffold hook still stood above the 0A-5 one in
// Program: the 0A-3 Avalonia probe. AvaloniaProbeRunner.Run() returns whatever
// Avalonia's StartWithClassicDesktopLifetime returns — the desktop lifetime's own
// exit code, set by whoever calls Shutdown(int), which for this scaffold's
// self-closing window is 0 but is in no way drawn from or bounded by this file. So
// while that hook was unconditional the sentence was FALSE OF THE SHIPPED EXE for a
// reason nobody had enumerated: not a code missing from the list, but a code this
// list has no way to constrain.
//
// It is TRUE NOW rather than softened, and the fix was structural rather than
// editorial: the 0A-3 hook and both Scaffold0A3 sources are Debug-only by the same
// three-part gate as Scaffold0A5 (a configuration-scoped Compile Remove in
// BroChoos.App.csproj, #if DEBUG wrapping each scaffold source whole, and #if DEBUG
// on the call site in Program together with its using). A shipped binary therefore
// cannot reach either scaffold, so no scaffold's return value can escape through it.
// Both hooks were gated by the same change, which is why one sentence covers them.
//
// 70 / 71 / 72 ARE DELIBERATELY ABSENT, and this note exists because for one commit
// they were reachable. The 0A-5 kill-test scaffold's child mode
// (BroChoos.App.Scaffold0A5.AtomicWriteKillChild) returns 70 on an expired park, 71 on
// a failed write and 72 on bad arguments, and the hook that reached it in Program was
// UNCONDITIONAL — so the shipped Release exe really could return codes that appear
// nowhere in this file. That was a defect in the hook, not a gap in this list.
//
// The scaffold is now excluded from every non-Debug build (a configuration-scoped
// Compile Remove in BroChoos.App.csproj, plus #if DEBUG in each Scaffold0A5 source and
// on the call site in Program), so those three codes cannot occur in a shipped build at
// all. They are therefore NOT added here: this file documents what a released binary
// promises its callers, and adding a code that no released binary can return would
// enlarge the public contract to describe a developer-only build — inviting a future
// reader to treat a throwaway harness's exit codes as something to preserve. They stay
// documented where they are produced.
public static class ExitCodes
{
	public const int Success = 0;
	public const int GeneralError = 1;
	public const int NoArgs = 2;
	public const int InvalidUrl = 3;
	public const int NoBrowserDetected = 4;
	public const int BrowserLaunchFailed = 5;
	public const int SafeModeOk = 10;
	public const int SafeModeFailed = 11;
	public const int ConfigCorruption = 99;
}
