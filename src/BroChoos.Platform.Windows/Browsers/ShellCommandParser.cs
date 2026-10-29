namespace BroChoos.Platform.Windows.Browsers;

// D-199/D-310: registry "shell\open\command" values are untrusted vendor input.
// They are parsed to recover the executable path ONLY — the command string is never
// executed, never passed to a shell, and never concatenated with the user's URL.
internal static class ShellCommandParser
{
	// Nullable input by contract: a registry value can be absent or of a non-string
	// type, and that is a normal machine state rather than a programmer error.
	public static string? ExtractExecutablePath(string? shellOpenCommand)
	{
		return TryExtractExecutablePath(shellOpenCommand, PathSafety.SystemVolumeProbe, out _);
	}

	// Same parse and same gate, plus the REASON the value was refused so the caller can
	// leave a diagnostic trail, and an explicit volume probe so one detection run can
	// share a cache. Behaviour is identical to the overload above.
	internal static string? TryExtractExecutablePath(
		string? shellOpenCommand,
		IDriveTypeProbe driveTypeProbe,
		out PathRejectionReason reason)
	{
		if (string.IsNullOrWhiteSpace(shellOpenCommand))
		{
			reason = PathRejectionReason.Empty;
			return null;
		}

		string trimmed = shellOpenCommand.Trim();

		// Quoted form: "C:\...\msedge.exe" --single-argument %1
		string candidate;
		if (trimmed[0] == '"')
		{
			int closing = trimmed.IndexOf('"', 1);
			if (closing <= 1)
			{
				// Unterminated or empty quoted segment: not a parseable command.
				reason = PathRejectionReason.MalformedCommand;
				return null;
			}

			candidate = trimmed[1..closing];
		}
		else
		{
			// Unquoted form: take the first token, which by this registry convention
			// is the executable. A path with spaces is only valid when quoted.
			int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
			candidate = space < 0 ? trimmed : trimmed[..space];
		}

		candidate = candidate.Trim();

		if (candidate.Length == 0)
		{
			reason = PathRejectionReason.MalformedCommand;
			return null;
		}

		if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			reason = PathRejectionReason.NotAnExecutable;
			return null;
		}

		// Reject anything that is not a local drive-letter path: this value later becomes
		// ProcessStartInfo.FileName. IsPathFullyQualified is NOT enough on its own — it
		// also accepts UNC (\\server\share\x.exe) and device paths (\\.\, \\?\), so the
		// shared PathSafety gate decides. Relative and malformed paths fail there too.
		return PathSafety.TryNormalize(candidate, driveTypeProbe, out reason);
	}
}
