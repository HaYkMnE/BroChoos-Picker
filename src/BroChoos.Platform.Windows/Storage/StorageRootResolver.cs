using BroChoos.Platform.Windows.Browsers;

namespace BroChoos.Platform.Windows.Storage;

// Tiny focused filesystem seam over directory existence/creation. Kept separate
// from IEnvironmentProbe (ambient-OFF reads only, no writes) so directory
// PROVISIONING can be injected and recorded by tests without ever touching the
// real disk, and the browser-detection probes do not grow disk-writing members.
public interface IStorageFileSystem
{
	void CreateDirectory(string path);
}

internal interface IStorageDriveTypeProbe
{
	DriveType GetDriveType(string root);
}

internal sealed class SystemStorageDriveTypeProbe : IStorageDriveTypeProbe
{
	public DriveType GetDriveType(string root)
	{
		return new DriveInfo(root).DriveType;
	}
}

internal enum StoragePathEntry
{
	Missing = 0,
	Ordinary,
	ReparsePoint,
}

internal interface IStoragePathGate
{
	StoragePathEntry Inspect(string path);

	// The default fails closed for inspectors that cannot resolve reparse points.
	// Every inspector that can report ReparsePoint must provide one immediate hop.
	string? ReadImmediateReparseTarget(string path)
	{
		throw new InvalidOperationException("This path inspector cannot resolve reparse points.");
	}
}

internal sealed class SystemStoragePathGate : IStoragePathGate
{
	public StoragePathEntry Inspect(string path)
	{
		FileAttributes attributes;
		try
		{
			attributes = File.GetAttributes(path);
		}
		catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
		{
			return StoragePathEntry.Missing;
		}

		return (attributes & FileAttributes.ReparsePoint) != 0
			? StoragePathEntry.ReparsePoint
			: StoragePathEntry.Ordinary;
	}

	public string? ReadImmediateReparseTarget(string path)
	{
		FileAttributes attributes = File.GetAttributes(path);
		FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0
			? new DirectoryInfo(path)
			: new FileInfo(path);
		return entry.ResolveLinkTarget(false)?.FullName;
	}
}

public sealed class SystemStorageFileSystem : IStorageFileSystem
{
	public void CreateDirectory(string path)
	{
		_ = Directory.CreateDirectory(path);
	}
}

// D-072 storage-mode resolution, one level above the raw environment probes: it
// turns "which mode, and where is the root" into a resolved, eagerly-provisioned
// set of directories. Priority (highest first):
//   1. an explicit --portable-data <path> argument value — safety-gated and normalized;
//   2. a .portable marker file next to the RUNNING executable -> portable mode,
//      data under a BroChoosConfig\ subfolder of the exe's directory (D-060);
//   3. installed mode -> %LocalAppData%\BroChoos (D-060).
//
// Only Root/Backups/Logs/Temp are created eagerly. themes/, diagnostics/ and
// exports/ are deliberately NOT provisioned here: they belong to their owning
// subsystems and may carry lazy or on-action lifecycle rules (e.g. D-074 —
// exports/ never exists until a user action), so a fresh install's tree stays
// minimal.
public interface IStorageRootResolver
{
	string Root { get; }

	string Backups { get; }

	string Logs { get; }

	string Temp { get; }
}

public sealed class StorageRootResolver : IStorageRootResolver
{
	private static readonly IStorageDriveTypeProbe SystemDriveTypeProbe = new SystemStorageDriveTypeProbe();

	private static readonly IStoragePathGate SystemPathGate = new SystemStoragePathGate();

	// The create context of the CLI branch. It is stated as a const rather than left as
	// an inline literal because it is no longer just a message template: the catch filter
	// in NormalizeArgumentRoot and the default of CreateSafetyRejection below both key on
	// this exact string, so the wording an operator reads and the sentinel the filter
	// compares are the same fact by construction.
	private const string PortableDataContext = "--portable-data";

	private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"CON",
		"PRN",
		"AUX",
		"NUL",
		"CLOCK$",
		"CONIN$",
		"CONOUT$",
		"COM1",
		"COM2",
		"COM3",
		"COM4",
		"COM5",
		"COM6",
		"COM7",
		"COM8",
		"COM9",
		"COM¹",
		"COM²",
		"COM³",
		"LPT1",
		"LPT2",
		"LPT3",
		"LPT4",
		"LPT5",
		"LPT6",
		"LPT7",
		"LPT8",
		"LPT9",
		"LPT¹",
		"LPT²",
		"LPT³",
	};

	private const string PortableDataParameterName = "portableDataArgument";

	private const string PortableMarkerFileName = ".portable";

	private const string PortableDataFolderName = "BroChoosConfig";

	private const string InstalledFolderName = "BroChoos";

	// Each gate-controlled immediate hop consumes this operation-wide budget. The
	// framework is never asked to traverse intermediate links on the gate's behalf.
	internal const int MaxReparseResolutions = 64;

	// A separate shared inspection cap bounds filesystem work from long component
	// paths as well as from target+suffix rescans. It is deliberately larger than
	// the reparse cap so ordinary paths and useful suffixes retain ample headroom.
	internal const int MaxInspectedComponents = 1024;

	// The two failure legs this type owns are TRANSLATED, and the reason is PRIVACY
	// rather than tidiness. Measured on .NET 10, every path failure that can reach a
	// caller from here quotes the offending path in its own Message: Directory.CreateDirectory
	// produces IOException / DirectoryNotFoundException / PathTooLongException /
	// UnauthorizedAccessException, and all four name the full path; Path.GetFullPath
	// throws PathTooLongException naming the value. Bootstrap logs what it catches —
	// ex.Message for an ArgumentException (Bootstrap.cs:89) and the exception OBJECT
	// for anything else (Bootstrap.cs:94), which formats InnerException too — so an
	// untranslated failure prints the user's data root, and in installed mode that
	// string carries the Windows account name. D-075 / D-097 forbid exactly that.
	//
	// NOTHING IS CHAINED, deliberately: an InnerException would carry the untranslated
	// message straight back into the formatted output, which is the leak this prevents.
	// What IS kept is the failure's TYPE NAME, because a type name is static text and
	// it is the one detail an operator needs — it separates "denied" from "parent
	// missing" from "too long" without naming anything.
	//
	// WHERE THE TRANSLATION LEGS ARE, and why they are not one: the value leg lives in
	// NormalizeArgumentRoot, next to the only text a user supplied, so it can name
	// --portable-data truthfully; the disk leg is here, around the four
	// CreateDirectory calls; and the two env-derived roots get a DEPLOYMENT leg through
	// NormalizeEnvDerivedRoot. That third leg exists because ALL THREE branches are now
	// safety-gated: composing a candidate with only Path.GetDirectoryName and
	// Path.Combine is measured to be throw-free (none of them rejects an embedded NUL, a
	// 40 000-character value, an invalid character or an empty string on .NET 10), which
	// is why nothing used to be validated here — but "the composition does not throw" is
	// not the security property. A machine whose LocalAppData is a UNC share or whose
	// image sits behind a `\\?\` namespace used to be provisioned WITHOUT the lexical,
	// volume or reparse-chain checks the --portable-data branch has always applied. The
	// env branches now fail closed on exactly those checks instead of quietly writing
	// storage to a network or device-namespace location. Their messages stay anonymous
	// (no --portable-data is involved, so the type and suppression convention is shared
	// while the wording differs — see NormalizeEnvDerivedRoot) and the value is still
	// suppressed, because in installed mode a candidate carries the Windows account name.
	public StorageRootResolver(
		IEnvironmentProbe environment,
		IStorageFileSystem fileSystem,
		string? portableDataArgument = null)
		: this(environment, fileSystem, portableDataArgument, SystemDriveTypeProbe, SystemPathGate)
	{
	}

	internal StorageRootResolver(
		IEnvironmentProbe environment,
		IStorageFileSystem fileSystem,
		string? portableDataArgument,
		IStorageDriveTypeProbe driveTypeProbe,
		IStoragePathGate pathGate)
	{
		ArgumentNullException.ThrowIfNull(environment);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(driveTypeProbe);
		ArgumentNullException.ThrowIfNull(pathGate);

		// Fail-fast (not lazy-degrade like RegistrationArtifacts): the resolver must
		// eagerly provision 4 directories, and with no root there is nothing to fall
		// back to — throwing here happens before any provisioning, so no partial tree.
		// This message is composed here and names no path, so it needs no translation.
		Root = ResolveRoot(environment, portableDataArgument, driveTypeProbe, pathGate)
			?? throw new IOException("No storage root could be resolved (installed mode with no LocalAppData).");
		Backups = Path.Combine(Root, "backups");
		Logs = Path.Combine(Root, "logs");
		Temp = Path.Combine(Root, "temp");

		try
		{
			// Inspection and provisioning are separate filesystem operations. A path can
			// change between this safety gate and CreateDirectory; this reduces risk but
			// cannot provide atomic containment against a concurrent namespace change.
			// Eager provisioning: only these four exist on a fresh install. themes/,
			// diagnostics/ and exports/ are subsystem-owned and not created here.
			fileSystem.CreateDirectory(Root);
			fileSystem.CreateDirectory(Backups);
			fileSystem.CreateDirectory(Logs);
			fileSystem.CreateDirectory(Temp);
		}
		catch (Exception failure) when (IsTranslatable(failure))
		{
			// Whatever was created before the failure is LEFT IN PLACE. Deleting
			// directories to tidy up a failed provision is a destructive action on a
			// path the caller supplied, and no caller asked for it.
			//
			// IOException is the lane on purpose: it matches the no-root throw above,
			// so a caller that cannot get a usable root sees one exception type
			// whichever way it failed. UnauthorizedAccessException does NOT derive
			// from IOException, which is why the classifier is not `catch (IOException)`.
			throw new IOException(
				$"The storage root could not be provisioned ({failure.GetType().Name}; path suppressed)");
		}
	}

	public string Root { get; }

	public string Backups { get; }

	public string Logs { get; }

	public string Temp { get; }

	private static string? ResolveRoot(
		IEnvironmentProbe environment,
		string? portableDataArgument,
		IStorageDriveTypeProbe driveTypeProbe,
		IStoragePathGate pathGate)
	{
		// D-072: an explicit CLI argument always wins. Normalizing it is the only
		// step in this method that can fail, which is why it is the only one wrapped.
		if (!string.IsNullOrWhiteSpace(portableDataArgument))
		{
			return NormalizeArgumentRoot(portableDataArgument, driveTypeProbe, pathGate);
		}

		// Portable mode: a .portable marker next to the running image. The marker
		// is looked up next to the RUNNING executable, never the working directory,
		// so the mode follows the binary that is actually executing.
		string? processPath = environment.CurrentProcessPath;
		string? exeDirectory = processPath is null ? null : Path.GetDirectoryName(processPath);
		if (!string.IsNullOrEmpty(exeDirectory)
			&& environment.FileExists(Path.Combine(exeDirectory, PortableMarkerFileName)))
		{
			return NormalizeEnvDerivedRoot(
				Path.Combine(exeDirectory, PortableDataFolderName),
				"the .portable marker data root",
				driveTypeProbe,
				pathGate);
		}

		// Installed mode. If LocalAppData is unset there is no root to resolve.
		string? localAppData = environment.GetEnvironmentVariable("LocalAppData");
		return string.IsNullOrWhiteSpace(localAppData)
			? null
			: NormalizeEnvDerivedRoot(
				Path.Combine(localAppData, InstalledFolderName),
				"the installed data root",
				driveTypeProbe,
				pathGate);
	}

	// The VALUE leg for the two ENV-DERIVED roots, and the sequence it applies is
	// deliberately the same one NormalizeArgumentRoot applies to --portable-data: an
	// ordinary absolute drive path, no unsafe components before or after canonicalization,
	// no bare volume root, an allowed volume type, and an existing chain that is not a
	// reparse escape. Both branches used to be validated nowhere, and the reason it was
	// safe to say so — Path.GetDirectoryName and Path.Combine do not throw on .NET 10 —
	// is also the reason it was WRONG: string composition is not containment. A root
	// reached through a UNC share, a `\\?\` device namespace, an alternate data stream or
	// a reserved device name would have been provisioned unexamined.
	//
	// TWO PUBLISHED BEHAVIOURS CHANGED, stated explicitly because they are user-visible:
	//   * a reparse-point deployment now fails closed where it previously succeeded —
	//     a .portable image launched from inside a junction, or a LocalAppData redirected
	//     to a symlink/junction, can no longer yield a root. The gate below resolves ONE
	//     immediate hop and revalidates the target+suffix, and only fails when that hop
	//     is missing, malformed, cyclic or on a refused volume.
	//   * the failure TYPE changed from whatever Path.Combine would have raised (measured:
	//     ArgumentNullException, when ProcessPath has no directory part) to the
	//     privacy-preserving ArgumentException below, matching the CLI branch.
	// Fail-closed is the intended resolution for both: provisioning an unvalidated root
	// is the bug being fixed.
	//
	// THE MESSAGE IS ANONYMOUS ON PURPOSE. It shares the create-context parameter name and
	// the "type only, value suppressed, nothing chained" convention with
	// CreateSafetyRejection, but it may NOT claim --portable-data was rejected: this branch
	// runs precisely when no explicit argument was supplied, so naming the option would be
	// a falsehood in the one line an operator reads. The account holds for the parameter
	// name: it is the caller-facing create context, which is what Bootstrap logs.
	//
	// WHY ArgumentException RATHER THAN IOException: by design a deployment defect is not a
	// command-line error, and the honest type would be IOException (the disk leg's lane).
	// One behaviour is deliberately bought with that mismatch — ArgumentException is what
	// Bootstrap already maps to ExitCodes.InvalidUrl and logs message-only, which is the
	// privacy-preserving lane; an IOException would be logged as an exception OBJECT. The
	// cost is that a machine with no usable LocalAppData now exits InvalidUrl rather than
	// GeneralError, and an operator cannot tell why from the exit code alone. Making it an
	// IOException subclass instead is REJECTED, not overlooked: a message is the only place
	// the account could live in that variant, and Bootstrap's exception-object logging
	// formats it, which is the leak D-075 / D-097 forbid.
	private static string NormalizeEnvDerivedRoot(
		string candidate,
		string createContext,
		IStorageDriveTypeProbe driveTypeProbe,
		IStoragePathGate pathGate)
	{
		try
		{
			if (!IsOrdinaryDriveLetterRooted(candidate))
			{
				throw CreateSafetyRejection(createContext, "origin is not an ordinary absolute drive path");
			}

			ValidateComponents(candidate, createContext);
			string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
			if (!IsOrdinaryDriveLetterRooted(normalized))
			{
				throw CreateSafetyRejection(createContext, "canonical path is not ordinary drive-rooted storage");
			}

			if (normalized.Length == 3)
			{
				throw CreateSafetyRejection(createContext, "a bare volume root is unsafe for storage");
			}

			ValidateComponents(normalized, createContext);
			ValidateVolume(normalized, driveTypeProbe, createContext);
			TraversalBudget traversalBudget = new(createContext);
			ValidateExistingChain(
				normalized,
				driveTypeProbe,
				pathGate,
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				traversalBudget,
				requiredExistingSegments: 1);
			return normalized;
		}
		catch (ArgumentException malformed) when (malformed.ParamName != PortableDataParameterName)
		{
			throw CreateMalformedRejection(createContext, malformed.GetType().Name);
		}
		catch (PathTooLongException malformed)
		{
			throw CreateMalformedRejection(createContext, malformed.GetType().Name);
		}
	}

	// The VALUE leg of the translation described on the constructor. It lives here,
	// beside the normalization it guards, because this is the ONLY place in the type
	// that touches text a user typed — so it is the only place that can honestly name
	// --portable-data in a message.
	//
	// TWO FAILURES ARE TRANSLATED, for two different reasons, and the difference is
	// worth keeping straight:
	//   * PathTooLongException — the PRIVACY fix. Measured on .NET 10, GetFullPath
	//     quotes the whole offending value in its Message, and Bootstrap's
	//     `catch (Exception)` leg formats the exception object (Bootstrap.cs:94), so
	//     leaving it alone prints what the user typed.
	//   * ArgumentException (an embedded NUL, and the empty string the guard above
	//     already excludes) — the CONTRACT fix. It does NOT leak: .NET says only
	//     "Null character in path. (Parameter 'path')". What is wrong is `path`, which
	//     is GetFullPath's OWN parameter and not a parameter of any caller of this
	//     type. Re-pointing it at portableDataArgument is what makes the ParamName
	//     usable, and it is also the evidence that this leg ran.
	//
	// The 2-argument overload is deliberate: chaining the original as an
	// InnerException would carry the untranslated Message back into the formatted
	// output, which is exactly the leak the first bullet prevents.
	//
	// ArgumentException — not IOException — because a bad value is a bad COMMAND
	// LINE, and Bootstrap already maps that to ExitCodes.InvalidUrl (Bootstrap.cs:87-90),
	// the same exit code CliParser produces for a missing or blank --portable-data
	// value. The disk leg uses IOException for the opposite reason: by then the value
	// was usable and the DISK refused.
	private static string NormalizeArgumentRoot(
		string portableDataArgument,
		IStorageDriveTypeProbe driveTypeProbe,
		IStoragePathGate pathGate)
	{
		try
		{
			string candidate = portableDataArgument.Trim();
			if (!IsOrdinaryDriveLetterRooted(candidate))
			{
				throw CreateSafetyRejection("origin is not an ordinary absolute drive path");
			}

			ValidateComponents(candidate, PortableDataContext);
			string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
			if (!IsOrdinaryDriveLetterRooted(normalized))
			{
				throw CreateSafetyRejection("canonical path is not ordinary drive-rooted storage");
			}

			if (normalized.Length == 3)
			{
				throw CreateSafetyRejection("a bare volume root is unsafe for storage");
			}

			ValidateComponents(normalized, PortableDataContext);
			ValidateVolume(normalized, driveTypeProbe, PortableDataContext);
			TraversalBudget traversalBudget = new(PortableDataContext);
			ValidateExistingChain(
				normalized,
				driveTypeProbe,
				pathGate,
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				traversalBudget,
				requiredExistingSegments: 1);
			return normalized;
		}
		catch (ArgumentException malformed) when (malformed.ParamName != PortableDataParameterName)
		{
			throw CreateMalformedRejection(malformed.GetType().Name);
		}
		catch (PathTooLongException malformed)
		{
			throw CreateMalformedRejection(malformed.GetType().Name);
		}
	}

	private static bool IsOrdinaryDriveLetterRooted(string path)
	{
		return path.Length >= 3
			&& char.IsAsciiLetter(path[0])
			&& path[1] == ':'
			&& IsDirectorySeparator(path[2])
			&& Path.IsPathFullyQualified(path);
	}

	private static bool IsDirectorySeparator(char character)
	{
		return character is '\\' or '/';
	}

	// The context is a parameter, and that is the whole point of its presence: this
	// helper is shared by the CLI and env legs, so a rejection it raises must be worded
	// by WHICHEVER leg called it. The 1-arg factory that used to live here hardcoded
	// --portable-data, which made an env-derived rejection claim a rejected option that
	// was never on the command line (and, because the ParamName sentinel then matched,
	// let that rejection escape the env leg's translation filter).
	private static void ValidateComponents(string path, string createContext)
	{
		ReadOnlySpan<char> remainder = path.AsSpan(3);
		while (!remainder.IsEmpty)
		{
			int separatorIndex = remainder.IndexOfAny('\\', '/');
			ReadOnlySpan<char> component = separatorIndex < 0 ? remainder : remainder[..separatorIndex];
			if (!component.IsEmpty)
			{
				if (component.Contains(':'))
				{
					throw CreateSafetyRejection(createContext, "alternate data stream syntax is not allowed");
				}

				bool isDotSegment = component.SequenceEqual(".") || component.SequenceEqual("..");
				if (!isDotSegment && component[^1] is '.' or ' ')
				{
					throw CreateSafetyRejection(createContext, "a path component has an unsafe ending");
				}

				int extensionIndex = component.IndexOf('.');
				ReadOnlySpan<char> deviceStem = extensionIndex < 0 ? component : component[..extensionIndex];
				if (ReservedDeviceNames.Contains(deviceStem.ToString()))
				{
					throw CreateSafetyRejection(createContext, "a reserved Windows device name is not allowed");
				}
			}

			if (separatorIndex < 0)
			{
				break;
			}

			remainder = remainder[(separatorIndex + 1)..];
		}
	}

	// `requiredExistingSegments` is the MINIMUM SEGMENT DEPTH the existing chain must
	// reach before a MISSING entry is tolerated, and the entry points pass 1 because the
	// one thing the walk must never accept is a missing VOLUME ROOT (segment 1). Below
	// that, all three of these shapes are legitimate and must be resolved:
	//   * none of the candidate's components exist yet — the ordinary first-run case;
	//   * the candidate's immediate parent exists and its leaf does not — the ordinary
	//     provisioning case, where the leaf IS inspected before the walk accepts it;
	//   * only an ancestor exists, the intermediate and leaf do not.
	// The walk stops at the first MISSING entry and accepts there, so a missing
	// intermediate legitimately SHORT-CIRCUITS the rest of the path: that is why the
	// inspection sets pinned in PortableDataRootSafetyTests stop at the first absent
	// component. The recursion is the one place the depth matters, and it is handled
	// explicitly at the call site below by rebasing the count onto the resolved target
	// (CountSegments(target) + the suffix still owed), so the entry value never has to
	// carry the whole path's depth.
	private static void ValidateExistingChain(
		string normalized,
		IStorageDriveTypeProbe driveTypeProbe,
		IStoragePathGate pathGate,
		HashSet<string> activeReparsePoints,
		TraversalBudget traversalBudget,
		int requiredExistingSegments)
	{
		string current = normalized[..3];
		int componentStart = 3;
		int currentSegmentCount = 1;

		while (true)
		{
			traversalBudget.ConsumeInspection();
			StoragePathEntry entry;
			try
			{
				entry = pathGate.Inspect(current);
			}
			catch (Exception failure) when (IsOrdinarySafetyFailure(failure))
			{
				throw CreateSafetyRejection(traversalBudget.CreateContext, "the existing path chain could not be inspected");
			}

			if (entry == StoragePathEntry.Missing)
			{
				if (currentSegmentCount <= requiredExistingSegments)
				{
					throw CreateSafetyRejection(traversalBudget.CreateContext, "the resolved reparse target does not exist");
				}

				return;
			}

			if (entry == StoragePathEntry.ReparsePoint)
			{
				traversalBudget.ConsumeReparseResolution();
				if (!activeReparsePoints.Add(current))
				{
					throw CreateSafetyRejection(traversalBudget.CreateContext, "the reparse chain contains a cycle");
				}

				try
				{
					string target = ReadAndValidateImmediateReparseTarget(current, driveTypeProbe, pathGate, traversalBudget.CreateContext);
					string effectivePath = AppendRemainingSuffix(target, normalized, current);
					if (effectivePath.Length == 3)
					{
						throw CreateSafetyRejection(traversalBudget.CreateContext, "the effective storage root is an unsafe bare volume root");
					}

					int requiredSuffixSegments = Math.Max(0, requiredExistingSegments - currentSegmentCount);
					ValidateExistingChain(
						effectivePath,
						driveTypeProbe,
						pathGate,
						activeReparsePoints,
						traversalBudget,
						requiredExistingSegments: CountSegments(target) + requiredSuffixSegments);
				}
				finally
				{
					_ = activeReparsePoints.Remove(current);
				}

				return;
			}

			if (componentStart >= normalized.Length)
			{
				return;
			}

			int nextSeparator = normalized.IndexOfAny(['\\', '/'], componentStart);
			current = nextSeparator < 0 ? normalized : normalized[..nextSeparator];
			componentStart = nextSeparator < 0 ? normalized.Length : nextSeparator + 1;
			currentSegmentCount++;
		}
	}

	private static string AppendRemainingSuffix(string target, string sourcePath, string sourcePrefix)
	{
		if (sourcePrefix.Length == sourcePath.Length)
		{
			return target;
		}

		string suffix = sourcePath[(sourcePrefix.Length + 1)..];
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(target, suffix)));
	}

	private static int CountSegments(string path)
	{
		int segments = 1;
		for (int index = 3; index < path.Length; index++)
		{
			if (index == 3 || (IsDirectorySeparator(path[index - 1]) && !IsDirectorySeparator(path[index])))
			{
				segments++;
			}
		}

		return segments;
	}

	private static string ReadAndValidateImmediateReparseTarget(
		string reparsePath,
		IStorageDriveTypeProbe driveTypeProbe,
		IStoragePathGate pathGate,
		string createContext)
	{
		string? target;
		try
		{
			target = pathGate.ReadImmediateReparseTarget(reparsePath);
		}
		catch (Exception failure) when (IsOrdinarySafetyFailure(failure))
		{
			throw CreateSafetyRejection(createContext, "a reparse target could not be resolved");
		}

		if (string.IsNullOrWhiteSpace(target) || !IsOrdinaryDriveLetterRooted(target))
		{
			throw CreateSafetyRejection(createContext, "a reparse target could not be resolved safely");
		}

		string normalizedTarget;
		try
		{
			ValidateComponents(target, createContext);
			normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
			ValidateComponents(normalizedTarget, createContext);
		}
		catch (Exception failure) when (failure is ArgumentException or PathTooLongException)
		{
			throw CreateSafetyRejection(createContext, "a reparse target is malformed");
		}

		ValidateVolume(normalizedTarget, driveTypeProbe, createContext);
		return normalizedTarget;
	}

	private static void ValidateVolume(string path, IStorageDriveTypeProbe driveTypeProbe, string createContext)
	{
		try
		{
			DriveType driveType = driveTypeProbe.GetDriveType(path[..3]);
			if (driveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
			{
				throw CreateSafetyRejection(createContext, "the volume type is not allowed for storage");
			}
		}
		catch (ArgumentException rejected) when (rejected.ParamName == PortableDataParameterName)
		{
			throw;
		}
		catch (Exception failure) when (IsOrdinarySafetyFailure(failure))
		{
			throw CreateSafetyRejection(createContext, "the volume could not be classified safely");
		}
	}

	private static bool IsOrdinarySafetyFailure(Exception failure)
	{
		return failure is not OutOfMemoryException and not StackOverflowException;
	}

	private sealed class TraversalBudget(string createContext)
	{
		private int _remainingInspections = MaxInspectedComponents;

		private int _remainingReparseResolutions = MaxReparseResolutions;

		// The budget carries the caller's create context so its exhaustion rejection is
		// worded by the same leg that started the walk. The chain recursion below shares
		// ONE instance, which is correct on both counts: the caps are operation-wide by
		// design, and the context cannot change mid-walk.
		public string CreateContext { get; } = createContext;

		public void ConsumeInspection()
		{
			if (_remainingInspections == 0)
			{
				throw CreateSafetyRejection(CreateContext, "the path traversal budget was exhausted");
			}

			_remainingInspections--;
		}

		public void ConsumeReparseResolution()
		{
			if (_remainingReparseResolutions == 0)
			{
				throw CreateSafetyRejection(CreateContext, "the path traversal budget was exhausted");
			}

			_remainingReparseResolutions--;
		}
	}

	// CA2208 is suppressed individually on the two factories that actually pass the
	// sentinel, because the exception is the point of the whole family: they pass
	// `PortableDataParameterName` for a name that is not a parameter of either of them.
	// It is a create-context sentinel that the catch filters in NormalizeArgumentRoot and
	// NormalizeEnvDerivedRoot compare against.
	//
	// WHAT THE 3-ARG OVERLOAD IS FOR, and what its absence was: every rejection that can
	// be raised from a SHARED helper (component validation, the volume check, the chain
	// walk, the reparse-target read, the traversal budget) must take the caller's context,
	// because those helpers serve both the CLI leg and the env legs. The 1-arg factory
	// hardcodes --portable-data, so a shared helper raising through it produced a message
	// claiming a rejected option that was never supplied — and, worse, a ParamName that
	// made the env leg's `when (malformed.ParamName != PortableDataParameterName)` filter
	// treat its OWN rejection as a foreign one and let it escape untranslated, defeating
	// the privacy leg the comments above promise.
	//
	// The CLI-context convenience overload exists so the argument branch keeps reading as
	// it did. It is explicitly NOT for shared helpers: anything reachable from
	// NormalizeEnvDerivedRoot must call the 3-arg overload with the env context.
	private static ArgumentException CreateSafetyRejection(string reason)
	{
		return CreateSafetyRejection(PortableDataContext, reason);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2208", Justification = "'portableDataArgument' is a deliberate create-context sentinel, not a method parameter; the catch filters key on it to distinguish CLI from env-derived rejections.")]
	private static ArgumentException CreateSafetyRejection(string createContext, string reason)
	{
		return new ArgumentException(
			$"{createContext} was rejected ({reason}; value suppressed)",
			PortableDataParameterName);
	}

	private static ArgumentException CreateMalformedRejection(string failureTypeName)
	{
		return CreateMalformedRejection(PortableDataContext, failureTypeName);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2208", Justification = "'portableDataArgument' is a deliberate create-context sentinel, not a method parameter; the catch filters key on it to distinguish CLI from env-derived rejections.")]
	private static ArgumentException CreateMalformedRejection(string createContext, string failureTypeName)
	{
		return new ArgumentException(
			$"{createContext} is not a usable filesystem path ({failureTypeName}; value suppressed)",
			PortableDataParameterName);
	}

	// The classifier for the DISK leg, and the list is measured rather than guessed:
	// against Directory.CreateDirectory on .NET 10 the failures that reach a caller
	// are IOException ("a file or directory with the same name already exists", an
	// invalid character), DirectoryNotFoundException (unmapped drive, missing
	// parent), PathTooLongException and UnauthorizedAccessException (denied ACL) —
	// and ALL FOUR quote the full path. The first three are IOException subclasses;
	// UnauthorizedAccessException is not, which is why this is not `catch (IOException)`.
	// Same pair ConfigRepairExecutor and BrowserCatalogPersistenceService already use.
	//
	// DELIBERATELY NOT CAUGHT: ArgumentException. CreateDirectory raises it for an
	// embedded NUL and — measured — its message names no path, so translating it
	// would buy no privacy while reshaping a caller-supplied-garbage signal into an
	// I/O failure. Programming errors from a substituted IStorageFileSystem
	// (NullReferenceException and friends) are left alone for the same reason: this
	// guard exists to suppress paths, not to absorb bugs.
	private static bool IsTranslatable(Exception failure)
	{
		return failure is IOException or UnauthorizedAccessException;
	}
}
