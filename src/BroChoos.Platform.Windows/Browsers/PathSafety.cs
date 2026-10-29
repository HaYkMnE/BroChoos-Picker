namespace BroChoos.Platform.Windows.Browsers;

// Single gate for every executable path that can become ProcessStartInfo.FileName.
// Two independent sources feed it and BOTH are attacker-influenced:
//   * registry "shell\open\command" values (vendor/HKCU-writable, D-199/D-310)
//   * the %ProgramFiles%/%LocalAppData% known-path table, whose variables are
//     inherited from the parent process and can therefore be forged.
//
// WHAT THIS GATE GUARANTEES, exactly:
//   1. the normalized path is drive-letter rooted (^[A-Za-z]:\) — a LEXICAL check, and
//   2. that volume positively classifies as local (non-Network).
// UNC shares (\\server\share) and Win32 device paths (\\?\, \\.\) fail (1): verified on
// .NET 10 that Path.GetFullPath passes both through verbatim, so neither
// IsPathFullyQualified nor GetFullPath alone can be trusted here. A drive letter mapped
// to SMB (`net use Z: \\host\share`) passes (1) but fails (2).
//
// WHAT IT DOES NOT GUARANTEE: it is not proof of a local physical disk. A drive letter
// created with `subst Z: C:\somewhere` is a local folder mapping that Windows reports as
// DriveType.Fixed, indistinguishable here from a real volume, and stays accepted — which
// is acceptable because the target is still local. DriveType.Removable/CDRom/Ram are
// likewise accepted. The rule is fail-closed: only a positively non-Network
// classification is accepted, so Network, unclassifiable results (NoRootDirectory,
// Unknown) and probe failures are all refused. Existence is NOT checked here either;
// callers probe the filesystem separately.
internal static class PathSafety
{
	// Default seam implementation: the live volume table, created once so the no-probe
	// overload stays allocation-free on the common path.
	//
	// Also exposed for a caller that wants to WRAP it (see MemoizingDriveTypeProbe)
	// rather than replace it, so a per-run cache does not have to construct its own
	// notion of "the real probe" and drift from this one.
	internal static IDriveTypeProbe SystemVolumeProbe { get; } = new SystemDriveTypeProbe();

	// Returns the normalized path when it passes both guarantees above, else null.
	// Normalizing here also collapses trailing/duplicated separators and mixed
	// separators, so callers can use the result as a stable dedupe key.
	internal static string? NormalizeLocalExecutablePath(string? candidate)
	{
		return TryNormalize(candidate, SystemVolumeProbe, out _);
	}

	// Test seam overload: lets a volume be described as Network (or made to throw)
	// without requiring a real `net use` mapping on the machine running the suite.
	internal static string? NormalizeLocalExecutablePath(string? candidate, IDriveTypeProbe driveTypeProbe)
	{
		return TryNormalize(candidate, driveTypeProbe, out _);
	}

	// The same gate, plus the REASON a candidate was refused so callers can leave a
	// diagnostic trail. Without a reason, a systemic rejection — a forged %ProgramFiles%,
	// an install roamed onto SMB, a volume that stopped answering — surfaced only as
	// "no browser detected" and exit 4, indistinguishable in the log from a machine that
	// genuinely has no browser installed.
	//
	// The reason is a RETURN VALUE on purpose. This gate deliberately holds no ILogger:
	// every path reaching it is attacker-influenced, so giving it a logger would put the
	// decision to write an untrusted path into a log INSIDE the security gate. Redaction
	// stays the caller's job, at the caller's log level.
	internal static string? TryNormalize(string? candidate, IDriveTypeProbe driveTypeProbe, out PathRejectionReason reason)
	{
		if (string.IsNullOrWhiteSpace(candidate))
		{
			reason = PathRejectionReason.Empty;
			return null;
		}

		try
		{
			// The fully-qualified check MUST come first. Path.GetFullPath resolves a
			// relative path against the CURRENT DIRECTORY, so "msedge.exe" or
			// "..\..\msedge.exe" would otherwise come back drive-rooted and sail past
			// the drive-letter test — turning a rejected value into a PATH-style lookup
			// next to whatever directory the process happens to be running in.
			if (!Path.IsPathFullyQualified(candidate))
			{
				reason = PathRejectionReason.NotDriveRooted;
				return null;
			}

			string full = Path.GetFullPath(candidate);

			// Lexical root first, volume type second: the drive-letter check is what
			// makes full[0] safe to read as a drive letter at all.
			if (!IsDriveLetterRooted(full))
			{
				reason = PathRejectionReason.NotDriveRooted;
				return null;
			}

			reason = ClassifyVolume(full, driveTypeProbe);

			return reason == PathRejectionReason.None ? full : null;
		}
		catch (Exception ex) when (IsRecoverablePathFailure(ex))
		{
			// An over-long, malformed or null-byte-bearing path is simply not a
			// launch target; it degrades to "no candidate" instead of throwing.
			reason = PathRejectionReason.MalformedPath;
			return null;
		}
	}

	// Equivalent to the anchored pattern ^[A-Za-z]:\ — checked by character so no
	// regex engine is involved on this hot, security-relevant path. Anything that
	// starts with a separator (UNC or device) or lacks a drive root fails here.
	// This is PURELY LEXICAL: it says nothing about what the volume actually is.
	private static bool IsDriveLetterRooted(string fullPath)
	{
		return fullPath.Length >= 3
			&& char.IsAsciiLetter(fullPath[0])
			&& fullPath[1] == ':'
			&& fullPath[2] == '\\';
	}

	// FAIL-CLOSED: only a volume that positively reports a non-Network DriveType is
	// accepted. An unmapped letter reports NoRootDirectory and a probe failure tells
	// us nothing, so both are refused rather than assumed local — a launch target we
	// cannot classify is not a launch target.
	//
	// Returns None when the volume is acceptable, otherwise the reason it was refused.
	private static PathRejectionReason ClassifyVolume(string fullPath, IDriveTypeProbe driveTypeProbe)
	{
		// "C:\" — the volume root DriveInfo expects.
		string root = fullPath[..3];

		try
		{
			DriveType driveType = driveTypeProbe.GetDriveType(root);

			if (driveType is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram)
			{
				return PathRejectionReason.None;
			}

			// Network is called out separately from the merely unclassifiable letters:
			// a roamed/mapped install is an ordinary machine state worth recognising in
			// a diagnostic, while NoRootDirectory/Unknown mean the OS would not say.
			return driveType == DriveType.Network
				? PathRejectionReason.NetworkVolume
				: PathRejectionReason.UnclassifiableVolume;
		}
		catch (Exception ex) when (IsClassifiableDriveFailure(ex))
		{
			// Any probe failure means the volume could not be classified, and an
			// unclassifiable volume is refused. The filter is inverted on purpose (same
			// policy as WindowsBrowserLauncher): listing the failures DriveInfo can
			// produce — ArgumentException for a malformed root, IO/permission errors on a
			// sick or disconnected volume, plus whatever a future runtime adds — would
			// leave the gap fail-OPEN, which is the one outcome that must not happen on
			// a path heading for ProcessStartInfo.FileName.
			return PathRejectionReason.ProbeFailed;
		}
	}

	private static bool IsRecoverablePathFailure(Exception ex)
	{
		return ex is ArgumentException
			or NotSupportedException
			or PathTooLongException
			or IOException
			or System.Security.SecurityException;
	}

	// Everything except the two failures that mean the process itself is no longer sane.
	// OutOfMemoryException must keep propagating; StackOverflowException is named for the
	// reader only — since .NET Core it cannot be caught by any filter, so this clause is
	// documentary and never fires.
	private static bool IsClassifiableDriveFailure(Exception ex)
	{
		return ex is not OutOfMemoryException and not StackOverflowException;
	}
}

// Why a candidate was refused, for diagnostics only. Deliberately coarse: it names the
// GATE that refused, never the path, so a caller can log it verbatim without leaking a
// filesystem layout. It is NOT part of any public contract and must never be used to
// decide whether to launch — the only launch signal is a non-null normalized path.
internal enum PathRejectionReason
{
	// Not a rejection: the candidate passed both guarantees.
	None = 0,

	// Null, empty or whitespace. Callers skip these silently; a known-path template
	// whose variable is unset is an ordinary machine state, not an anomaly.
	Empty,

	// Failed the lexical root test: relative, drive-relative, UNC or a device path.
	NotDriveRooted,

	// Drive-letter rooted, but the volume reports DriveType.Network.
	NetworkVolume,

	// The volume answered with NoRootDirectory or Unknown — refused fail-closed.
	UnclassifiableVolume,

	// The volume probe threw, so the volume could not be classified at all.
	ProbeFailed,

	// Path.GetFullPath refused the string: over-long, null-byte-bearing or malformed.
	MalformedPath,

	// Produced by ShellCommandParser BEFORE the gate is reached: the registry value was
	// not a parseable "shell\open\command" (unterminated quote, empty after trimming).
	MalformedCommand,

	// Produced by ShellCommandParser BEFORE the gate is reached: the first token did
	// not end in ".exe", so it is not a launchable image at all.
	NotAnExecutable,
}

internal static class PathRejectionReasonExtensions
{
	// Stable, log-safe token per reason. Kept next to the enum so a new value cannot be
	// added without a reader noticing it needs a token; hyphen-lowercase to match the
	// existing detection-source strings ("known-path", "registry-startmenuinternet").
	internal static string ToToken(this PathRejectionReason reason)
	{
		return reason switch
		{
			PathRejectionReason.None => "accepted",
			PathRejectionReason.Empty => "empty",
			PathRejectionReason.NotDriveRooted => "not-drive-rooted",
			PathRejectionReason.NetworkVolume => "network-volume",
			PathRejectionReason.UnclassifiableVolume => "unclassifiable-volume",
			PathRejectionReason.ProbeFailed => "probe-failed",
			PathRejectionReason.MalformedPath => "malformed-path",
			PathRejectionReason.MalformedCommand => "malformed-command",
			PathRejectionReason.NotAnExecutable => "not-an-executable",
			_ => "unknown",
		};
	}
}

// Narrow seam over the volume table. Only the DriveType of a volume root is needed,
// so the interface stays one method rather than wrapping DriveInfo wholesale.
internal interface IDriveTypeProbe
{
	// root is a volume root in "C:\" form.
	DriveType GetDriveType(string root);
}

internal sealed class SystemDriveTypeProbe : IDriveTypeProbe
{
	public DriveType GetDriveType(string root)
	{
		return new DriveInfo(root).DriveType;
	}
}

// Caches one DriveType per volume root for the lifetime of a SINGLE detection run.
// One Detect() call gates 5 known-path templates plus every registry registration, and
// almost all of them sit on C:, so the uncached gate hit the volume table repeatedly
// (roughly once per candidate) for the same answer.
//
// Lifetime is deliberately per-run, NOT static: DriveType is live machine state (a
// mapping can appear or disappear between runs) and a process-wide cache would pin a
// stale answer for as long as BroChoos runs. Not thread-safe, and does not need to be —
// one instance belongs to one Detect() call on one thread.
//
// FAIL-CLOSED IS PRESERVED: a throwing probe is NOT cached. The exception propagates to
// ClassifyVolume, which converts it to ProbeFailed exactly as it does without the cache,
// so a sick volume keeps failing closed on every subsequent candidate instead of having
// its failure remembered as some accepted DriveType.
internal sealed class MemoizingDriveTypeProbe(IDriveTypeProbe inner) : IDriveTypeProbe
{
	private readonly IDriveTypeProbe _inner = inner;

	// Windows volume roots are case-insensitive: "c:\" and "C:\" are one volume.
	private readonly Dictionary<string, DriveType> _cache = new(StringComparer.OrdinalIgnoreCase);

	public DriveType GetDriveType(string root)
	{
		if (_cache.TryGetValue(root, out DriveType cached))
		{
			return cached;
		}

		// Only a value the inner probe actually RETURNED is cached; if it throws, nothing
		// is stored and the next candidate re-probes.
		DriveType probed = _inner.GetDriveType(root);
		_cache[root] = probed;

		return probed;
	}
}
