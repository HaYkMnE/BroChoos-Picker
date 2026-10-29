using BroChoos.Core.Abstractions;

namespace BroChoos.Platform.Windows.Browsers;

// W6.2 manual/portable admission. This is the platform half of the flow whose request and
// answer shapes Core owns (ManualBrowserAdmissionRequest / ManualBrowserAdmissionResult).
//
// WHAT IT REUSES, deliberately, and what it does NOT invent:
//   * PathSafety  — the ONE gate for every path that can become ProcessStartInfo.FileName.
//     It is the same gate the known-path and registry tiers already go through, so a manual
//     path cannot be held to a weaker standard than a discovered one, and it cannot silently
//     become a second, drifting definition of "safe". UNC, device paths, relative paths,
//     network volumes and unclassifiable volumes are refused by it, with the reason reported.
//   * IEnvironmentProbe.FileExists / CurrentProcessPath — the same environment seam the
//     detector and launcher use. D-304's recursion guard ("never launch BroChoos as its own
//     browser target") is applied HERE too, at admission, because a candidate admitted with
//     BroChoos as its executable is one the user can select and the launcher can only then
//     refuse; refusing at admission is where the user can still be told why.
//   * ExecutableIdentityVerifier.ResolveCanonicalPath — the same canonicalization the IPC
//     peer-image and launcher comparisons use (symlink/junction resolved to its final
//     target), so "is this me?" and "is this the file the user picked?" cannot disagree
//     between this gate and the launcher's.
//   * Chromium/Firefox profile readers — the same profile-list reads the discovered tiers
//     use, pointed at the portable install's own data directory, so a portable candidate's
//     profile surface is the same shape as any other candidate's. A portable layout that
//     has no readable profile list yields zero profiles, which is the honest answer.
//
// WHAT IT DOES NOT DO: it never scans drives. The ONLY path input is the one the user
// picked. There is no enumeration, no directory walk, and no fallback search — a manual
// admission that could not validate the named file is refused, not "looked for elsewhere".
//
// NAMING NOTE: the class is named "Admitter" rather than "Validator" for the same reason the
// plan-named test class is ManualBrowserValidationTests. Two types named *Validator in the
// same solution (DefaultUrlValidator is the other) is a small but real reading hazard, and
// the type's output is an admitted candidate, not a boolean.
public sealed class ManualBrowserAdmitter(IEnvironmentProbe environment) : IManualBrowserAdmissionGate
{
	private static readonly char[] ExtensionSeparators = ['.'];

	// The ONLY extension a manual path may carry: a real Windows image. The registry and
	// known-path tiers reach ProcessStartInfo.FileName through ShellCommandParser, which
	// requires ".exe"; a user-picked path has no parser, so the equivalent requirement is
	// enforced here.
	//
	// .cmd/.bat/.com ARE DELIBERATELY ABSENT. They are shells, not browser images: launching
	// one hands the URL to a script interpreter, which is both a different program than the
	// user pointed at and an argument-injection surface the launch path was never designed
	// for (D-183's structured argv guarantees nothing once cmd.exe is the real target). A
	// user who wants a script wrapper must wrap it themselves and point at the wrapper's
	// real image.
	//
	// A path with NO extension is not refused here: File.Exists already answered for a real
	// file, and a portable build whose host has no extension is a legitimate shape.
	private static readonly string[] ExecutableExtensions = [".exe"];

	private readonly IEnvironmentProbe _environment = environment;
	private readonly ChromiumProfileReader _chromiumProfileReader = new();
	private readonly FirefoxProfileReader _firefoxProfileReader = new();

	public ManualBrowserAdmissionResult Admit(ManualBrowserAdmissionRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (string.IsNullOrWhiteSpace(request.ExePath))
		{
			return ManualBrowserAdmissionResult.Refused(ManualBrowserAdmissionCodes.EmptyPath);
		}

		// THE PATH GATE FIRST, and the order is load-bearing. Everything below asks questions
		// ABOUT the file (does it exist, is it me, what is its name), and every one of those
		// questions touches the filesystem — so the path has to be proven local and
		// drive-rooted before anything is allowed to resolve it. Running the existence probe
		// first would mean a UNC path had already been opened over SMB before the gate that
		// exists to refuse UNC paths ever ran.
		string? normalized = PathSafety.NormalizeLocalExecutablePath(request.ExePath);
		if (normalized is null)
		{
			return ManualBrowserAdmissionResult.Refused(ManualBrowserAdmissionCodes.PathRejected);
		}

		// A path that names a DIRECTORY is not a browser. Checked explicitly because
		// File.Exists is false for a directory too, and collapsing the two into "missing
		// file" would hide the case that is really a mis-pick rather than a deleted install.
		if (Directory.Exists(normalized) && !_environment.FileExists(normalized))
		{
			return ManualBrowserAdmissionResult.Refused(ManualBrowserAdmissionCodes.NotAFile);
		}

		if (!_environment.FileExists(normalized))
		{
			return ManualBrowserAdmissionResult.Refused(ManualBrowserAdmissionCodes.MissingFile);
		}

		string fileName = Path.GetFileName(normalized);
		if (HasRefusedExtension(fileName))
		{
			return ManualBrowserAdmissionResult.Refused(ManualBrowserAdmissionCodes.NotAnExecutable);
		}

		if (IsSelf(normalized))
		{
			return ManualBrowserAdmissionResult.Refused(ManualBrowserAdmissionCodes.SelfReference);
		}

		string channel = request.Kind == ManualBrowserKind.Portable ? "portable" : "custom";
		string detectionSource = request.Kind == ManualBrowserKind.Portable ? "portable" : "manual";
		string browserId = BuildBrowserId(request.Kind, normalized);
		string displayName = string.IsNullOrWhiteSpace(request.DisplayName)
			? BuildDisplayName(request.Kind, fileName)
			: request.DisplayName.Trim();

		BrowserCandidate candidate = ReadProfiles(browserId, displayName, normalized, channel, detectionSource);

		return ManualBrowserAdmissionResult.Accepted(candidate);
	}

	// A portable install keeps its profile inside the install folder; a custom install's
	// profile location is unknowable without the user saying so, so a custom admission
	// reports zero profiles rather than guessing at a vendor default that the user's layout
	// may not match. This is the "no invented data" rule applied to profile metadata.
	//
	// The profile list is read from the folder the user named — the ONLY directory this
	// class ever looks in. There is no drive walk, no registry lookup and no vendor default.
	private BrowserCandidate ReadProfiles(
		string browserId,
		string displayName,
		string exePath,
		string channel,
		string detectionSource)
	{
		string installDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
		string executableName = Path.GetFileName(exePath);
		bool isPortable = string.Equals(channel, "portable", StringComparison.Ordinal);

		if (executableName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase))
		{
			FirefoxProfileReadResult firefox = _firefoxProfileReader.TryRead(
				Path.Combine(installDirectory, "profiles.ini"));

			return new BrowserCandidate(browserId, displayName, exePath, channel, detectionSource)
			{
				ProfileIds = firefox.ProfileIds,
				ProfileNames = [.. firefox.ProfileIds.Select(Core.Privacy.ProfileNameSanitizer.Sanitize)],
				HasUnresolvedProfiles = firefox.HasUnresolvedProfiles,
				IsCustomBrowser = !isPortable,
				IsPortableBrowser = isPortable,
			};
		}

		ChromiumProfileReadResult chromium = _chromiumProfileReader.TryRead(
			Path.Combine(installDirectory, "Local State"));

		return new BrowserCandidate(browserId, displayName, exePath, channel, detectionSource)
		{
			ProfileIds = chromium.ProfileIds,
			ProfileNames = chromium.ProfileNames,
			HasUnresolvedProfiles = chromium.HasUnresolvedProfiles,
			IsCustomBrowser = !isPortable,
			IsPortableBrowser = isPortable,
		};
	}

	private static bool HasRefusedExtension(string fileName)
	{
		int lastDot = fileName.LastIndexOfAny(ExtensionSeparators);
		if (lastDot <= 0)
		{
			// No extension at all: File.Exists already answered for a real file, so this is
			// an executable with an unconventional (or absent) name, not a bad file kind.
			return false;
		}

		string extension = fileName[lastDot..];
		return !ExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
	}

	// D-304, applied at admission through the same seam and the same canonicalization the
	// launcher uses. Fail-closed: with no identity for the running process the question
	// cannot be answered, and admitting a target that MIGHT be BroChoos itself is worse
	// than refusing one admission the user can retry.
	private bool IsSelf(string exePath)
	{
		string? self = _environment.CurrentProcessPath;
		if (string.IsNullOrWhiteSpace(self))
		{
			return true;
		}

		try
		{
			string selfCanonical = SingleInstance.ExecutableIdentityVerifier.ResolveCanonicalPath(self);
			string candidateCanonical = SingleInstance.ExecutableIdentityVerifier.ResolveCanonicalPath(exePath);
			return string.Equals(selfCanonical, candidateCanonical, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
		{
			return true;
		}
	}

	// "<prefix>-<basis>-<hash8>". The stem keeps the id readable; the hash is the first 8 hex
	// chars of SHA-256 over the upper-cased, fully normalized path, so two installs that
	// share a file name (two portable chrome.exe folders) get distinct ids, while the same
	// install re-admitted — under any case or separator spelling, since Windows paths are
	// case-insensitive — collapses to the same id, which is what makes the catalog's same-id
	// reconciliation a dedupe. SHA-256 rather than string.GetHashCode: the id is persisted,
	// and GetHashCode is randomized per process.
	//
	// The caller passes PathSafety's normalized path; GetFullPath here only makes the
	// function total for the test vector and is a no-op on an already-normalized path.
	//
	// LINKS: the hash is taken over the path with every symlink/junction resolved (see
	// ResolveLinks), so the same install reached directly or through a junction gets ONE id.
	// The stem is still taken from the path the user picked, and the candidate's ExePath is
	// unchanged — only the identity is resolved.
	internal static string BuildBrowserId(ManualBrowserKind kind, string exePath)
	{
		string fullPath = Path.GetFullPath(exePath);
		string stem = Path.GetFileNameWithoutExtension(fullPath).ToLowerInvariant();
		string basis = stem is "chrome" or "msedge" or "brave" ? $"chromium-{stem}" : stem;
		string prefix = kind == ManualBrowserKind.Portable ? "portable" : "custom";
		byte[] digest = System.Security.Cryptography.SHA256.HashData(
			System.Text.Encoding.UTF8.GetBytes(ResolveLinks(fullPath).ToUpperInvariant()));
		return $"{prefix}-{basis}-{Convert.ToHexStringLower(digest)[..8]}";
	}

	// Resolves reparse points in the directory chain (each ancestor that is a junction or
	// directory symlink is replaced by its final target), then the file itself. Bounded:
	// a path has finitely many segments and ResolveLinkTarget(returnFinalTarget: true)
	// follows a chain to its end itself. The INPUT path is local and already gated, but a
	// directory or file SYMLINK on it may point at a UNC target (\\server\share\...), and
	// resolving it with returnFinalTarget: true then touches that remote path (SMB) to
	// follow the chain. This runs only after PathSafety accepted the path and File.Exists
	// already opened it through the same link, so it adds no new kind of remote access, but
	// it is not "local only". The resolved path is used for the id HASH only; it is never
	// launched and never re-gated. Any failure (missing target, access denied, unreachable
	// share, cyclic link) falls back to the unresolved path: the id then behaves as before,
	// which is a duplicate card at worst, never a refused or wrong launch.
	private static string ResolveLinks(string fullPath)
	{
		try
		{
			string? directory = Path.GetDirectoryName(fullPath);
			string fileName = Path.GetFileName(fullPath);
			string resolvedDirectory = directory is null ? string.Empty : ResolveDirectoryChain(directory);
			string candidate = Path.Combine(resolvedDirectory, fileName);
			FileSystemInfo? fileTarget = File.ResolveLinkTarget(candidate, returnFinalTarget: true);
			return fileTarget is null ? candidate : Path.GetFullPath(fileTarget.FullName);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return fullPath;
		}
	}

	private static string ResolveDirectoryChain(string directory)
	{
		string? root = Path.GetPathRoot(directory);
		if (string.IsNullOrEmpty(root))
		{
			return directory;
		}

		string current = root;
		foreach (string segment in directory[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current, segment);
			FileSystemInfo? target = Directory.ResolveLinkTarget(current, returnFinalTarget: true);
			if (target is not null)
			{
				current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
			}
		}

		return current;
	}

	private static string BuildDisplayName(ManualBrowserKind kind, string fileName)
	{
		string stem = Path.GetFileNameWithoutExtension(fileName);
		string prefix = kind == ManualBrowserKind.Portable ? "Portable" : "Custom";
		return $"{prefix} {stem}";
	}
}
