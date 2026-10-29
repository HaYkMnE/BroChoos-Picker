// DEBUG-ONLY, and this #if is the SECOND of two independent gates on the same property.
// The first is in BroChoos.App.csproj, which removes Scaffold0A5 from compilation in every
// non-Debug configuration; this one means the file still refuses to compile into a Release
// build if that ItemGroup is ever deleted or a new configuration slips past it. Neither gate
// depends on the other being present.
//
// #if is prohibited in the storage SOURCES (src\BroChoos.Platform.Windows\Storage), where a
// configuration-dependent read path would be a defect. This file is the opposite case: a
// throwaway kill-test child whose whole requirement is that it CANNOT exist in a shipped
// build. The scope distinction is deliberate.
#if DEBUG
using System.Globalization;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;

namespace BroChoos.App.Scaffold0A5;

/// <summary>
/// 0A-5 SCAFFOLD - THROWAWAY, AND DEBUG-ONLY. The killable child of the D-v3.5-493
/// kill-process harness. Performs exactly ONE <c>AtomicJsonFileStore.Write</c> with an
/// <see cref="EventCheckpointObserver"/> attached, so the parent
/// (<c>tools\atomic-kill-test.ps1</c>) can park the write at a chosen stage, inspect the
/// destination while it is still parked, and kill this process there.
/// <para>THE PUBLIC CLI CONTRACT IS UNCHANGED AS SHIPPED, which is a different and stronger
/// claim than the one this comment used to make. It said the contract and every public exit
/// code "stay byte-for-byte unchanged" because the trigger was absent from <c>HelpText</c> and
/// unknown to <c>CliParser</c>. That confused undocumented with unreachable: the hook in
/// <c>Program</c> was UNCONDITIONAL, so the shipped exe really did accept an argument it had
/// never accepted before, and really could return 70, 71 or 72 - none of which appear in
/// <c>BroChoos.Core.ExitCodes</c>. What makes the claim true now is that this type does not
/// exist in a non-Debug build at all (see the <c>#if DEBUG</c> above and the
/// <c>Compile Remove</c> in <c>BroChoos.App.csproj</c>), so those exit codes are
/// unreachable by construction rather than by obscurity.</para>
/// <para>In a Debug build the mode is reachable, and deliberately so - the harness needs it -
/// but only through <see cref="IsTriggered"/>, which requires the trigger to be the FIRST
/// argument and requires <see cref="TriggerEnvironmentVariable"/> to be set. Goes away with
/// the scaffold.</para>
/// </summary>
internal static class AtomicWriteKillChild
{
	/// <summary>Verbose and namespaced so it cannot collide with a real option and is
	/// obviously not user-facing, exactly like the 0A-3 probe trigger it follows.</summary>
	public const string TriggerArgument = "--internal-0a5-atomic-write-child";

	/// <summary>The corroborating environment variable, which is the gate a URL cannot pass.
	/// A URL arrives as a command-line ARGUMENT; it cannot set a variable in this process's
	/// environment. So even in a build where this code exists, the injection shape - a hostile
	/// or malformed URL breaking out of the registered "%1" quoting and delivering extra
	/// arguments - cannot reach the write, because the environment will not carry this.</summary>
	public const string TriggerEnvironmentVariable = "BROCHOOS_0A5_KILL_CHILD";

	/// <summary>The only accepted value. An exact ordinal match, so a variable that merely
	/// EXISTS (empty, or set to something else) does not open the gate.</summary>
	public const string TriggerEnvironmentValue = "1";

	// Child-only exit codes. Deliberately in a range no public runner uses (see
	// BroChoos.Core.ExitCodes: 0-11 plus 99), so a harness reading an exit code can tell a
	// checkpoint failure from a product exit.
	//
	// THEY ARE NOT PART OF ANY CONTRACT, and the reason is now structural rather than
	// rhetorical. The old comment here said "this mode is unreachable without the internal
	// trigger above", which was technically true and misleading, because the trigger itself
	// was reachable: nothing gated the hook in Program, so a shipped exe could return these.
	// In a non-Debug build this file is not compiled at all, so 70/71/72 cannot occur in a
	// shipped build - which is why they are deliberately absent from
	// BroChoos.Core.ExitCodes, the file that documents the PUBLIC contract.
	private const int ExitWriteCompleted = 0;

	private const int ExitParkExpired = 70;

	private const int ExitWriteFailed = 71;

	private const int ExitBadArguments = 72;

	// The park bound the child enforces on ITSELF (the interface contract requires the
	// observer to bound its own wait). Generous enough that a loaded machine cannot make the
	// parent's inspection race it, short enough that an ORPHANED child - parent crashed, so
	// `proceed` will never be set - is gone in well under a minute instead of sitting on the
	// per-file write mutex forever. Overridable so the xUnit tests can use a short one.
	private static readonly TimeSpan DefaultParkTimeout = TimeSpan.FromSeconds(30);

	// A FIXED instant, never DateTimeOffset.UtcNow. The parent compares the destination against
	// canonical byte images captured by earlier runs of this same child, so the payload for a
	// given (revision, entry count) must be byte-identical across processes and across time. A
	// clock reading here would make every image unique and every byte comparison vacuous.
	private static readonly DateTimeOffset BulkVerifiedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	/// <summary>Whether this process was started as the kill-test child. THREE conditions, all
	/// required.</summary>
	///
	/// DEFENCE IN DEPTH, behind the two compilation gates rather than instead of them. In a
	/// shipped build this method does not exist; these checks are what make the mode
	/// unreachable-by-injection in the builds where it DOES exist, i.e. on a developer machine
	/// running a Debug exe that is also registered as the OS URL handler.
	///
	/// WHAT EACH CONDITION REMOVES:
	///
	///   1. args[0], not "any element". The measured injection shape is a URL carrying a
	///      literal unencoded quote: the shell's CommandLineToArgvW ends the quoted region
	///      there, so every following token becomes its own argument and the trigger arrives
	///      SOMEWHERE IN THE MIDDLE - never first, because the URL itself occupies args[0].
	///      Array.Exists accepted exactly that. A first-position test does not.
	///   2. The environment variable. A URL cannot set one. This alone removes the injection
	///      shape even where the code is present, which is why it is here and not only in the
	///      csproj.
	///   3. Ordinal, case-sensitive spelling, unchanged: an internal trigger takes no spelling
	///      liberties, unlike the public options CliParser matches case-insensitively.
	///
	/// A LOCAL PROCESS THAT CAN SET THE ENVIRONMENT AND CHOOSE args[0] IS STILL A CALLER THIS
	/// CANNOT REFUSE, and that is not what these gates are for - such a process can already
	/// write the user's config directly. What they close is the remote-ish shape: content
	/// (a URL) crossing into argument position.
	public static bool IsTriggered(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		return args.Length != 0 && string.Equals(args[0], TriggerArgument, StringComparison.Ordinal) && string.Equals(
			Environment.GetEnvironmentVariable(TriggerEnvironmentVariable),
			TriggerEnvironmentValue,
			StringComparison.Ordinal);
	}

	/// <summary>Runs the one observed write and returns the child exit code.</summary>
	///
	/// EVERY marker line is pure ASCII and single-line, because the parent is Windows
	/// PowerShell 5.1 reading a redirected stream with regex markers (the 0A-4 harness
	/// convention). Markers name the destination FILE ONLY, never its directory: the harness
	/// runs under a sandbox root, but a full path in a child's stdout would end up quoted in
	/// gate evidence, and D-075 / D-097 forbid full user file paths there.
	public static int Run(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		string? destination = ReadValue(args, "--dest");
		string? backups = ReadValue(args, "--backups");
		string? runId = ReadValue(args, "--run-id");
		string? stageText = ReadValue(args, "--stage");
		string? revisionText = ReadValue(args, "--revision");
		string? parkText = ReadValue(args, "--park-timeout-seconds");
		string? browsersText = ReadValue(args, "--browsers");

		if (string.IsNullOrWhiteSpace(destination)
			|| string.IsNullOrWhiteSpace(runId)
			|| !int.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int revision))
		{
			Console.WriteLine("KILLCHILD_BAD_ARGS need --dest, --run-id, --revision [--backups] [--stage] [--park-timeout-seconds]");
			return ExitBadArguments;
		}

		// "none" means: run the write at full speed, parked nowhere. Used for the canonical
		// byte capture and for the concurrent-reader case, which needs a REAL unkilled write.
		AtomicWriteStage[] armed = [];
		if (!string.IsNullOrWhiteSpace(stageText)
			&& !string.Equals(stageText, "none", StringComparison.OrdinalIgnoreCase))
		{
			if (!Enum.TryParse(stageText, ignoreCase: false, out AtomicWriteStage parsed))
			{
				Console.WriteLine(FormattableString.Invariant($"KILLCHILD_BAD_ARGS unknown stage '{stageText}'"));
				return ExitBadArguments;
			}

			armed = [parsed];
		}

		TimeSpan parkTimeout = int.TryParse(parkText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parkSeconds)
			&& parkSeconds > 0
			? TimeSpan.FromSeconds(parkSeconds)
			: DefaultParkTimeout;

		// The document under test.
		//
		// DEFAULT (no --browsers): AppDocument, which in 0A-5 carries ONLY the D-064 schema
		// envelope, so schemaRevision is the sole field that can make two revisions differ on
		// disk - all the kill cases need to tell OLD from NEW byte-for-byte, at 52 bytes.
		//
		// --browsers N: a BrowsersDocument carrying N synthetic entries, tens of KB. This exists
		// for the concurrent-reader case. At 52 bytes the parent's read loop always completed in
		// a SINGLE FileStream.Read, so "never saw a torn read" had almost no opportunity to fail
		// - a tear would have had to land between two reads of one file, which cannot happen when
		// there is only ever one read. A payload spanning many filesystem clusters gives that
		// observation something real to catch.
		//
		// This does NOT mean the small payload weakened the central D-493 claim: the destination
		// is never written in place, so the swap is a directory-entry metadata operation whose
		// atomicity is independent of payload size.
		ConfigDocument document;
		int browserCount = 0;

		if (string.IsNullOrWhiteSpace(browsersText))
		{
			document = new AppDocument { SchemaRevision = revision };
		}
		else if (int.TryParse(browsersText, NumberStyles.Integer, CultureInfo.InvariantCulture, out browserCount)
			&& browserCount >= 0)
		{
			document = new BrowsersDocument(BuildBrowsers(browserCount)) { SchemaRevision = revision };
		}
		else
		{
			Console.WriteLine(FormattableString.Invariant($"KILLCHILD_BAD_ARGS --browsers must be a non-negative integer, got '{browsersText}'"));
			return ExitBadArguments;
		}

		Console.WriteLine(FormattableString.Invariant(
			$"KILLCHILD_START file={Path.GetFileName(destination)} revision={revision} stage={(armed.Length == 0 ? "none" : armed[0].ToString())} parkSeconds={parkTimeout.TotalSeconds} payload={document.GetType().Name} browsers={browserCount}"));

		using EventCheckpointObserver observer = new(
			runId,
			armed,
			parkTimeout,
			OnParkExpired,
			static marker => Console.WriteLine(marker));

		AtomicJsonFileStore store = new(backups, writeLockTimeout: null, observer: observer);

		try
		{
			store.Write(destination, document);
		}
		catch (Exception failure)
		{
			// Reported, not rethrown: an unhandled exception would give the parent a crash
			// dump dialog and a stderr wall of text instead of one assertable line. The
			// D-063 step 9 classification keys are echoed because they are exactly what a
			// caller is supposed to branch on, so the harness can see them too.
			Console.WriteLine(FormattableString.Invariant(
				$"KILLCHILD_WRITE_FAILED type={failure.GetType().Name} destinationModified={Read(failure, "destinationModified")} restored={Read(failure, "restored")}"));
			return ExitWriteFailed;
		}

		Console.WriteLine("KILLCHILD_WRITE_OK");
		return ExitWriteCompleted;
	}

	// A park that expires means the PARENT never arrived - it crashed, was interrupted, or has
	// a bug. Nothing this process can still do is trustworthy: it is sitting at a checkpoint
	// inside the per-file write mutex (from LockAcquired onward), so the only correct move is
	// to stop being a problem for the next writer. Environment.Exit rather than a return
	// because the call site is deep inside AtomicJsonFileStore.Write - there is no way to
	// abandon the write from here without either throwing (which the write path would classify
	// as a WRITE failure, hiding a harness fault as a product defect) or letting the write
	// finish (which would silently invalidate whatever the parent later asserts).
	private static void OnParkExpired(AtomicWriteStage stage)
	{
		Console.WriteLine(FormattableString.Invariant(
			$"KILLCHILD_ORPHANED stage={stage} exiting={ExitParkExpired}"));
		Console.Out.Flush();

		Environment.Exit(ExitParkExpired);
	}

	// N synthetic browser entries, FULLY DETERMINISTIC. Every field is derived from the index
	// alone - no clock, no GUID, no environment, no machine state - because the parent compares
	// the destination byte-for-byte against canonical images captured by SEPARATE runs of this
	// same child. Any nondeterminism here would make each image unique and silently turn every
	// byte comparison into a tautology.
	//
	// The index is zero-padded to a fixed width so entries are uniform in length, which keeps
	// the OLD and NEW images of one entry count the SAME total size. That is deliberate for the
	// concurrent-reader case: with equal lengths, a short read cannot be mistaken for a
	// legitimate read of the other revision.
	//
	// Paths are synthetic and rooted at a name that exists nowhere; nothing here touches or
	// names a real browser installation or a real user directory.
	private static List<BrowserDefinition> BuildBrowsers(int count)
	{
		List<BrowserDefinition> browsers = new(count);

		for (int i = 0; i < count; i++)
		{
			string index = i.ToString("D4", CultureInfo.InvariantCulture);

			browsers.Add(new BrowserDefinition(
				BrowserId: FormattableString.Invariant($"synthetic-browser-{index}"),
				DisplayName: FormattableString.Invariant($"Synthetic Browser {index}"),
				Vendor: FormattableString.Invariant($"Synthetic Vendor {index}"),
				Channel: "stable",
				ExePath: FormattableString.Invariant($@"C:\SyntheticBrowsers\{index}\browser.exe"),
				DetectionSource: "known-path",
				LastVerifiedAt: BulkVerifiedAt));
		}

		return browsers;
	}

	private static string Read(Exception failure, string key)
	{
		object? value = failure.Data[key];
		return value is null ? "(absent)" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(null)";
	}

	// Ordinal "--name value" lookup. Hand-rolled on purpose: this mode must never touch
	// CliParser, so that the public parser's behavior cannot be changed by anything the
	// scaffold needs.
	private static string? ReadValue(string[] args, string name)
	{
		for (int i = 0; i < args.Length - 1; i++)
		{
			if (string.Equals(args[i], name, StringComparison.Ordinal))
			{
				return args[i + 1];
			}
		}

		return null;
	}
}
#endif
