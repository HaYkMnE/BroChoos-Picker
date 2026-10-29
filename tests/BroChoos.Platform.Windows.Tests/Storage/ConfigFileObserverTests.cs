using System.Collections;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Serialization;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

// The OBSERVATION half of the config read path, against the real filesystem where
// the state can be produced deterministically, and against a fake read seam where it
// cannot (a file that vanishes between enumeration and stat).
//
// Every test owns a unique subdir under %TEMP% and removes it in a finally, so
// nothing leaks onto the developer's machine. None of them needs admin rights, a
// background thread, or a timing window.
public sealed class ConfigFileObserverTests
{
	// A valid 32-char hex guid segment, so crafted backup names differ from a real
	// minted one only where each test means them to.
	private const string GuidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

	private const string GuidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

	private static ConfigFileObserver NewObserver()
	{
		return new(new SystemStorageReadFileSystem());
	}

	// A path for the in-memory seams that never touch a disk. Rooted and plausible, so
	// nothing about these tests depends on a path shape, and no real file is created.
	private const string SeamPath = @"X:\brochoos-seam\app.json";

	// A backups directory for the seam facts. Nothing is ever created here: the seam under
	// test fails before any real path is touched.
	private const string SeamBackupsDirectory = @"X:\brochoos-seam\backups";

	// ---------------------------------------------------------------- destination

	// Maps to ConfigFileObservation.Absent(). Nothing to protect, nothing to preserve.
	[Fact]
	public void Observe_FileAbsent_ReportsAbsent()
	{
		string dir = NewTempDirectory();
		try
		{
			ConfigFileObservation observed = NewObserver().Observe<AppDocument>(Path.Combine(dir, "app.json"));

			_ = observed.Access.Should().Be(ConfigFileAccess.Absent);
			_ = observed.Envelope.Should().BeNull("bytes are a precondition for parsing");
			_ = observed.Body.Should().BeNull();
			_ = observed.Schema.Should().BeNull("no verdict may be invented for a file that was never read");
			_ = observed.Quarantine.Should().Be(QuarantineAttempt.NotAttempted);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A missing DIRECTORY must also be an observation, not an exception — and
	// specifically not the framework's DirectoryNotFoundException, whose own message
	// embeds the full path that D-075 / D-097 forbid in logs.
	[Fact]
	public void Observe_DirectoryAbsent_ReportsAbsentWithoutThrowing()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "no-such-dir", "app.json");

			ConfigFileObservation observed = NewObserver().Observe<AppDocument>(path);

			_ = observed.Access.Should().Be(ConfigFileAccess.Absent);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A zero-byte file is not valid JSON, so the envelope is unknowable. It must NOT
	// arrive as a schema verdict of any kind.
	[Fact]
	public void Observe_ZeroByteFile_ReportsEnvelopeUnparseableWithNoSchema()
	{
		AssertEnvelopeUnparseable(string.Empty);
	}

	[Fact]
	public void Observe_TruncatedJson_ReportsEnvelopeUnparseableWithNoSchema()
	{
		// Cut off mid-document: well-formed as far as it goes, and still garbage.
		AssertEnvelopeUnparseable("{\"schemaVersion\":1,\"schemaRev");
	}

	[Fact]
	public void Observe_InvalidJson_ReportsEnvelopeUnparseableWithNoSchema()
	{
		AssertEnvelopeUnparseable("this is not json at all");
	}

	// A missing envelope property is NOT defaulted. Defaulting it would manufacture
	// exactly the "verdict you could not compute" that Core's EnvelopeUnparseable
	// factory exists to make unrepresentable.
	[Fact]
	public void Observe_EnvelopePropertyMissing_ReportsEnvelopeUnparseableRatherThanDefaulting()
	{
		AssertEnvelopeUnparseable(/*lang=json,strict*/ "{\"schemaRevision\":0}");
	}

	[Fact]
	public void Observe_EnvelopePropertyNotANumber_ReportsEnvelopeUnparseable()
	{
		AssertEnvelopeUnparseable(/*lang=json,strict*/ "{\"schemaVersion\":\"one\",\"schemaRevision\":0}");
	}

	// THE STATE THAT MUST NEVER LATER BE PLAINLY OVERWRITTEN. The envelope reads
	// cleanly and evaluates to fully compatible, yet the body below it does not bind.
	// Reporting this as merely "corrupt" would lose the fact that the schema WAS
	// computed; reporting it as Loaded would authorise an overwrite of a file whose
	// content nobody has understood.
	[Fact]
	public void Observe_CompatibleEnvelopeWithUnbindableBody_ReportsBodyUnparseableWithTheSchemaVerdict()
	{
		string dir = NewTempDirectory();
		try
		{
			// "browsers" is declared as a list; a string there cannot bind. The envelope
			// above it is untouched and perfectly readable.
			string path = WriteFile(dir, "browsers.json", /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":\"not-an-array\"}");

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Access.Should().Be(ConfigFileAccess.Opened);
			_ = observed.Envelope.Should().Be(EnvelopeParse.Parsed, "the envelope was read BEFORE the body was bound");
			_ = observed.Body.Should().Be(DocumentBodyParse.Unparseable);
			_ = observed.Schema.Should().Be(
				SchemaCompatibility.CompletelyCompatible,
				"a cleared envelope is still worth reporting; it just does not license an overwrite");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void Observe_WellFormedDocument_ReportsLoadedAndCompletelyCompatible()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "browsers.json", /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":[]}");

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Access.Should().Be(ConfigFileAccess.Opened);
			_ = observed.Envelope.Should().Be(EnvelopeParse.Parsed);
			_ = observed.Body.Should().Be(DocumentBodyParse.Parsed);
			_ = observed.Schema.Should().Be(SchemaCompatibility.CompletelyCompatible);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// The schema verdicts, each read from a real file. The observer computes none of
	// these itself — it feeds SchemaCompatibilityEvaluator and reports what came back.
	[Theory]
	[InlineData(2, 0, SchemaCompatibility.NewerVersionReadOnly)]
	[InlineData(0, 0, SchemaCompatibility.OlderVersionMigrationRequired)]
	[InlineData(1, 5, SchemaCompatibility.NewerRevisionReadKnownOnly)]
	[InlineData(1, 0, SchemaCompatibility.CompletelyCompatible)]
	[InlineData(-1, 0, SchemaCompatibility.Invalid)]
	public void Observe_EnvelopeVersions_ReportTheEvaluatorsVerdict(
		int version,
		int revision,
		SchemaCompatibility expected)
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "app.json", Envelope(version, revision));

			ConfigFileObservation observed = NewObserver().Observe<AppDocument>(path);

			_ = observed.Envelope.Should().Be(EnvelopeParse.Parsed);
			_ = observed.Schema.Should().Be(expected);
			_ = observed.Body.Should().Be(
				DocumentBodyParse.Parsed,
				"an envelope-only document binds regardless of which version it declares");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A NEWER REVISION must remain READABLE, not corrupt. Unknown additive fields are
	// exactly what D-064 / D-070 anticipate, so the body still has to bind — otherwise
	// a future release's file would look damaged to this one and invite a repair.
	[Fact]
	public void Observe_NewerRevisionWithUnknownAdditiveFields_StillBindsTheBody()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(
				dir,
				"app.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":7,\"someFutureField\":{\"nested\":true}}");

			ConfigFileObservation observed = NewObserver().Observe<AppDocument>(path);

			_ = observed.Schema.Should().Be(SchemaCompatibility.NewerRevisionReadKnownOnly);
			_ = observed.Body.Should().Be(
				DocumentBodyParse.Parsed,
				"unmapped members are skipped, so a newer revision reads as sound rather than as damaged");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A LOCKED FILE IS NOT A DAMAGED FILE. Held with FileShare.None in this very
	// process — Windows share modes bind per handle, not per process, so this is
	// deterministic with no background thread.
	//
	// Collapsing this into a corrupt state would later authorise a destructive repair
	// of bytes that may well be a newer version's (D-066).
	[Fact]
	public void Observe_FileLockedWithShareNone_ReportsOpenFailedAndNotCorrupt()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "app.json", Envelope(1, 0));

			using FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
			// The precondition, asserted rather than assumed: if the lock did not
			// actually bite, the observation below would prove nothing.
			Action open = () => File.OpenRead(path);
			_ = open.Should().Throw<IOException>("the sharing violation must be real for this test to mean anything");

			ConfigFileObservation observed = NewObserver().Observe<AppDocument>(path);

			_ = observed.Access.Should().Be(ConfigFileAccess.OpenFailed);
			_ = observed.Envelope.Should().BeNull();
			_ = observed.Body.Should().BeNull(
				"a locked file must never be reported as unparseable — nothing was read, so nothing is known to be damaged");
			_ = observed.Schema.Should().BeNull();
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A DENIED ACL is the other half of OpenFailed, and it fails differently from a
	// lock (UnauthorizedAccessException, not IOException) — so it needs its own test.
	//
	// NO SKIP-IF-ELEVATED GUARD, deliberately: a deny ACE binds an elevated process
	// too (the real bypass needs SeBackupPrivilege ENABLED, which is not on by
	// default even for an administrator), so skipping on elevation would target the
	// wrong condition and silently drop this coverage. Only ReadData is denied, so
	// this process stays the owner and can always take the ACE back off — which the
	// finally does before deleting, or the temp directory would become undeletable
	// and poison every later run.
	[Fact]
	public void Observe_ReadDeniedByAcl_ReportsOpenFailedAndNotCorrupt()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "app.json", Envelope(1, 0));
			FileInfo file = new(path);
			SecurityIdentifier self = WindowsIdentity.GetCurrent().User!;
			FileSystemAccessRule deny = new(self, FileSystemRights.ReadData, AccessControlType.Deny);

			FileSecurity security = file.GetAccessControl();
			security.AddAccessRule(deny);
			file.SetAccessControl(security);

			try
			{
				// MUST FAIL, and this test MUST FAIL IF IT DOES NOT. Without this
				// assertion an ACE that quietly failed to bind would leave the test
				// passing for the wrong reason.
				Action open = () => File.OpenRead(path);
				_ = open.Should().Throw<UnauthorizedAccessException>(
					"the deny ACE must actually deny the read, otherwise this test proves nothing");

				ConfigFileObservation observed = NewObserver().Observe<AppDocument>(path);

				_ = observed.Access.Should().Be(ConfigFileAccess.OpenFailed);
				_ = observed.Envelope.Should().BeNull();
				_ = observed.Schema.Should().BeNull();
			}
			finally
			{
				FileSecurity restore = file.GetAccessControl();
				_ = restore.RemoveAccessRule(deny);
				file.SetAccessControl(restore);
			}
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// FIVE SEAM ESCAPES, one fact each. Each of these five combinations was MEASURED to
	// escape Observe outright before the OpenFailed filter in TryReadAllBytes was closed
	// by the contract, which broke the never-throws vocabulary declared at the top of
	// ConfigFileObserver.
	//
	// WHY THESE ARE OpenFailed AND NOT A CORRUPT VERDICT, which is the whole point: no
	// bytes were obtained, so nothing is KNOWN about the file. BodyUnparseable and
	// EnvelopeUnparseable license a destructive repair (quarantine the original, write
	// defaults over it), so reporting an intact file as corrupt is the worst outcome
	// available here. OpenFailed writes nothing, and ConfigFileObservation.OpenFailed()
	// takes no arguments, so a corrupt verdict is structurally unrepresentable from this
	// path.
	//
	// EACH TEST GUARDS ITS OWN PREMISE. The seam member is invoked DIRECTLY first and
	// asserted to throw the exception the test is named for — without that, a seam that
	// quietly stopped throwing would leave the test passing for the wrong reason and the
	// catch clause under test would never be entered at all.
	[Fact]
	public void Observe_SeamGetLengthThrowsNotSupported_ReportsOpenFailedInsteadOfEscaping()
	{
		ThrowingSeamReadFileSystem seam = new(ThrowingSeamMember.GetLength, static () => new NotSupportedException("Seam refuses."));

		Action probe = () => seam.GetLength(SeamPath);
		_ = probe.Should().Throw<NotSupportedException>(
			"the seam must really throw, or the catch clause under test is never reached");

		AssertOpenFailedWithNothingKnown(new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath));
	}

	[Fact]
	public void Observe_SeamGetLengthThrowsInvalidOperation_ReportsOpenFailedInsteadOfEscaping()
	{
		ThrowingSeamReadFileSystem seam = new(ThrowingSeamMember.GetLength, static () => new InvalidOperationException("Seam in a bad state."));

		Action probe = () => seam.GetLength(SeamPath);
		_ = probe.Should().Throw<InvalidOperationException>(
			"the seam must really throw, or the catch clause under test is never reached");

		AssertOpenFailedWithNothingKnown(new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath));
	}

	[Fact]
	public void Observe_SeamOpenReadThrowsNotSupported_ReportsOpenFailedInsteadOfEscaping()
	{
		ThrowingSeamReadFileSystem seam = new(ThrowingSeamMember.OpenRead, static () => new NotSupportedException("Seam refuses."));

		Action probe = () => seam.OpenRead(SeamPath);
		_ = probe.Should().Throw<NotSupportedException>(
			"the seam must really throw, or the catch clause under test is never reached");

		AssertOpenFailedWithNothingKnown(new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath));
	}

	[Fact]
	public void Observe_SeamOpenReadThrowsInvalidOperation_ReportsOpenFailedInsteadOfEscaping()
	{
		ThrowingSeamReadFileSystem seam = new(ThrowingSeamMember.OpenRead, static () => new InvalidOperationException("Seam in a bad state."));

		Action probe = () => seam.OpenRead(SeamPath);
		_ = probe.Should().Throw<InvalidOperationException>(
			"the seam must really throw, or the catch clause under test is never reached");

		AssertOpenFailedWithNothingKnown(new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath));
	}

	// THE MID-READ ESCAPE, and it is the one that reaches furthest into the method: the
	// length was reported, the stream was opened, the buffer was allocated, and only then
	// does the seam fail. Everything up to the failure looked like a healthy read.
	[Fact]
	public void Observe_SeamStreamReadThrowsInvalidOperation_ReportsOpenFailedInsteadOfEscaping()
	{
		ThrowingSeamReadFileSystem seam = new(ThrowingSeamMember.StreamRead, static () => new InvalidOperationException("Seam in a bad state."));

		using Stream probeStream = seam.OpenRead(SeamPath);
		Action probe = () => probeStream.ReadExactly(new byte[16], 0, 16);
		_ = probe.Should().Throw<InvalidOperationException>(
			"the stream must really throw mid-read, or the catch clause under test is never reached");

		AssertOpenFailedWithNothingKnown(new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath));
	}

	// THE SECOND DELIBERATE ESCAPE, ON ITS MID-READ ROUTE. ObjectDisposedException
	// derives from InvalidOperationException, so the widened catch in TryReadAllBytes would
	// convert it into OpenFailed like any other refusal — it survives ONLY because that
	// filter excludes it by name. A disposed stream is never a state of a config file, only
	// a wrong seam implementation, so it must reach the caller loudly instead of being
	// reported as a file that would not open.
	//
	// THIS FACT CAN FAIL, in the direction that matters: delete the `is not
	// ObjectDisposedException` exclusion and Observe returns an OpenFailed observation
	// rather than throwing, so the second assertion goes red instead of vacuously green.
	// It is the inverse of the four facts above — same seam, same member, opposite verdict —
	// and ThrowExactly rather than Throw, so a bare InvalidOperationException arriving here
	// would not satisfy it either.
	[Fact]
	public void Observe_SeamStreamReadThrowsObjectDisposed_LetsItEscapeInsteadOfReportingOpenFailed()
	{
		ThrowingSeamReadFileSystem seam = new(ThrowingSeamMember.StreamRead, static () => new ObjectDisposedException("Stream"));

		using Stream probeStream = seam.OpenRead(SeamPath);
		Action probe = () => probeStream.ReadExactly(new byte[16], 0, 16);
		_ = probe.Should().ThrowExactly<ObjectDisposedException>(
			"the stream must really throw mid-read, or the escape under test is never exercised");

		Action observe = () => new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath);

		_ = observe.Should().ThrowExactly<ObjectDisposedException>(
			"a disposed stream is always a defect, so it escapes Observe rather than becoming an OpenFailed observation");
	}

	// THE OTHER TWO ROUTES OF THAT SAME ESCAPE, which the fact above does NOT cover. The
	// exclusion in TryReadAllBytes is a TYPE test and the filter guards the WHOLE try,
	// which calls GetLength, OpenRead and stream.Read — so the escape is not a mid-read
	// speciality. With only the mid-read route pinned, adding a catch for a disposed
	// handle at either of the other two would have reinstated the silent OpenFailed with
	// every test still green.
	//
	// SAME SHAPE AS THE MID-READ FACT, for the same reasons: the seam member is invoked
	// DIRECTLY first, so a fake that stopped throwing turns these red at the premise
	// instead of leaving them vacuously green (the payload is a VALID document, so a
	// non-throwing seam yields a parsed observation, never a throw). ThrowExactly, not
	// Throw — ObjectDisposedException derives from InvalidOperationException, so Throw
	// would also be satisfied by the bare base type, which is the OpenFailed case.
	[Fact]
	public void Observe_SeamGetLengthThrowsObjectDisposed_LetsItEscapeInsteadOfReportingOpenFailed()
	{
		ThrowingSeamReadFileSystem seam = new(ThrowingSeamMember.GetLength, static () => new ObjectDisposedException("Stream"));

		Action probe = () => seam.GetLength(SeamPath);
		_ = probe.Should().ThrowExactly<ObjectDisposedException>(
			"the seam must really throw, or the escape under test is never exercised");

		Action observe = () => new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath);

		_ = observe.Should().ThrowExactly<ObjectDisposedException>(
			"a disposed handle is always a defect, so it escapes Observe from GetLength too rather than becoming an OpenFailed observation");
	}

	[Fact]
	public void Observe_SeamOpenReadThrowsObjectDisposed_LetsItEscapeInsteadOfReportingOpenFailed()
	{
		ThrowingSeamReadFileSystem seam = new(ThrowingSeamMember.OpenRead, static () => new ObjectDisposedException("Stream"));

		Action probe = () => seam.OpenRead(SeamPath);
		_ = probe.Should().ThrowExactly<ObjectDisposedException>(
			"the seam must really throw, or the escape under test is never exercised");

		Action observe = () => new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath);

		_ = observe.Should().ThrowExactly<ObjectDisposedException>(
			"a disposed handle is always a defect, so it escapes Observe from OpenRead too rather than becoming an OpenFailed observation");
	}

	// THE THREE UNGUARDED SEAM SITES, one fact each, and they pin a declaration that was
	// otherwise resting on nothing. IStorageReadFileSystem declares of DirectoryExists that
	// it never throws for an absent directory, and ConfigFileObserver's header rests THREE
	// unguarded seam calls on that declaration — two of which are DirectoryExists. The three
	// ObjectDisposedException escapes each carry a fact; these did not, so a `try` added
	// around any of them would have converted a broken collaborator into "no backups" or
	// "unreadable file" with every test still green.
	//
	// IOException IS THE THROWN TYPE ON PURPOSE, and it is what gives these facts their
	// force. It is otherwise the MOST CONVERTIBLE type on these paths — TryReadAllBytes
	// converts it to OpenFailed, EnumerateQuietly to [], StatQuietly to null — so if any
	// conversion existed at these sites, IOException is the type that would find it. These
	// therefore prove the ABSENCE OF A CONVERSION, not merely that something propagated.
	//
	// ThrowExactly, not Throw: FileNotFoundException and DirectoryNotFoundException both
	// derive from IOException and both mean "absent", which is an ordinary observation. A
	// bare Throw would be satisfied by those and would stop discriminating.
	//
	// NAMED _LetsItEscapeInsteadOf... so they stay OUT of the tightened
	// Observe_Seam*_ReportsOpenFailedInsteadOfEscaping glob, which selects the opposite
	// verdict.
	[Fact]
	public void Observe_SeamDirectoryExistsThrowsIo_LetsItEscapeInsteadOfReportingOpenFailed()
	{
		DirectoryExistsThrowingReadFileSystem seam = new();

		Action probe = () => seam.DirectoryExists(SeamPath);
		_ = probe.Should().ThrowExactly<IOException>(
			"the seam must really throw, or the escape under test is never exercised");

		Action observe = () => new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath);

		_ = observe.Should().ThrowExactly<IOException>(
			"a seam whose DirectoryExists throws is BROKEN, not a filesystem state, so it escapes Observe's not-found branch rather than becoming an OpenFailed observation");
	}

	[Fact]
	public void ListBackupCandidates_SeamDirectoryExistsThrowsIo_LetsItEscapeInsteadOfReturningEmpty()
	{
		DirectoryExistsThrowingReadFileSystem seam = new();

		Action probe = () => seam.DirectoryExists(SeamBackupsDirectory);
		_ = probe.Should().ThrowExactly<IOException>(
			"the seam must really throw, or the escape under test is never exercised");

		Action list = () => new ConfigFileObserver(seam).ListBackupCandidates(SeamBackupsDirectory, "app.json");

		_ = list.Should().ThrowExactly<IOException>(
			"returning [] would tell the pure layer there is NOTHING TO RESTORE FROM, which can cost the user a recoverable backup, so a broken seam escapes instead");
	}

	[Fact]
	public void ObserveNthNewestBackup_SeamDirectoryExistsThrowsIo_LetsItEscapeInsteadOfReturningNull()
	{
		DirectoryExistsThrowingReadFileSystem seam = new();

		Action probe = () => seam.DirectoryExists(SeamBackupsDirectory);
		_ = probe.Should().ThrowExactly<IOException>(
			"the seam must really throw, or the escape under test is never exercised");

		// Index 0 and non-null arguments, so the entry point's own caller-bug guards pass
		// and the forwarded DirectoryExists is genuinely the site under test.
		Action observe = () =>
			new ConfigFileObserver(seam).ObserveNthNewestBackup<AppDocument>(SeamBackupsDirectory, "app.json", 0);

		_ = observe.Should().ThrowExactly<IOException>(
			"this entry point forwards to ListBackupCandidates, so it inherits the same escape rather than reporting 'no such candidate'");
	}

	// THE UNGUARDED SEAM SITE THAT WAS PINNED BY NOTHING — the FileExists call at the top of
	// Observe. The three facts above are three ENTRY POINTS but only TWO SITES: all three use
	// DirectoryExistsThrowingReadFileSystem, whose FileExists is `=> false` and never throws.
	// An audit of every FileExists implementation in this file found none that throws, so the
	// header's claim that all three sites were pinned by facts of their own was FALSE, and
	// false about the worst of the three.
	//
	// WHY THAT GAP WAS THE WORST ONE OF THE THREE, and this is what sets its priority.
	// Wrapping the DirectoryExists sites yields OpenFailed or [], which write NOTHING.
	// Wrapping THIS site — `try/catch (IOException) { return false; }` — falls through to
	// the DirectoryExists test, which answers false, so Observe reports Absent(). Absent
	// routes to CreateDefaultsAndPersist, which writes defaults over an EXISTING file with
	// NO QUARANTINE, because the planner was told there was nothing to protect. That is
	// strictly worse than the BodyUnparseable path, which at least preserves the original.
	//
	// IOException FOR THE SAME REASON AS THE THREE ABOVE: it is the most CONVERTIBLE type
	// on this path (TryReadAllBytes converts it to OpenFailed), so if any conversion existed
	// here, IOException is the type that would find it. This proves the ABSENCE of a
	// conversion rather than merely that something propagated.
	//
	// DirectoryExists RETURNS FALSE rather than throwing, deliberately: with it throwing too
	// the escape could not be attributed to the FileExists site, and this fact would duplicate
	// Observe_SeamDirectoryExistsThrowsIo_LetsItEscapeInsteadOfReportingOpenFailed instead of
	// covering the site that nothing covered.
	[Fact]
	public void Observe_SeamFileExistsThrowsIo_LetsItEscapeInsteadOfReportingAbsent()
	{
		FileExistsThrowingReadFileSystem seam = new();

		Action probe = () => seam.FileExists(SeamPath);
		_ = probe.Should().ThrowExactly<IOException>(
			"the seam must really throw, or the escape under test is never exercised");

		// THE PREMISE THAT GIVES THIS FACT ITS FORCE, measured rather than assumed: with
		// FileExists converted to false, the OTHER seam member on that branch answers false
		// too, so the swallowed version would report Absent — the destructive verdict this
		// fact exists to keep unreachable.
		_ = seam.DirectoryExists(SeamPath).Should().BeFalse(
			"a swallowed FileExists would fall through to this answer and yield Absent, not OpenFailed");

		Action observe = () => new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath);

		_ = observe.Should().ThrowExactly<IOException>(
			"a seam whose FileExists throws is BROKEN, not a filesystem state, so it escapes rather than being reported as an ABSENT file — which would license defaults written over an existing file with no quarantine");
	}

	// ------------------------------------------- an abstract TDocument is a caller bug

	// AN ABSTRACT TDocument WAS DESTRUCTIVE, AND IT NEEDED NO CODE CHANGE TO REACH.
	// `where TDocument : ConfigDocument` does not exclude ConfigDocument itself — a type
	// satisfies its own constraint — so Observe<ConfigDocument>(path) compiles from any
	// assembly. System.Text.Json refuses an abstract target with NotSupportedException,
	// TryBindBody CATCHES that type and returns false, and a FULLY INTACT file was therefore
	// reported BodyUnparseable -> PlanRepair -> QuarantineThenCreateDefaults: the user's
	// config quarantined and defaults written over it.
	//
	// NOTHING PINNED IT because every inventory in this file filters `!type.IsAbstract`
	// (ConcreteConfigDocuments), so ConfigDocument itself is outside all of them.
	//
	// WHY ArgumentException AND NOT NotSupportedException, pinned rather than merely
	// commented: the faithful-looking type is the one TryBindBody catches, so a guard
	// throwing it could be SWALLOWED BY THE CODE IT PROTECTS. ThrowExactly, so a guard
	// later "corrected" to NotSupportedException turns this red instead of passing.
	//
	// AND EACH OF THESE ASSERTS THAT NO READ OCCURRED, which is the property that a
	// throw-only assertion would not have. A guard sitting AFTER the read would still throw
	// ArgumentException and satisfy the first assertion, while having already touched the
	// user's file — and worse, ArgumentException is on TryReadAllBytes' conversion list, so a
	// guard inside that try would be converted into OpenFailed and swallowed differently.
	// The recording seam holds a VALID document, so if the guard were removed entirely the
	// observer would read it and RETURN an observation rather than throwing, turning the
	// throw assertion red too. Both directions fail loudly.
	[Fact]
	public void Observe_AbstractDocumentType_IsRejectedBeforeAnyReadRatherThanReportedCorrupt()
	{
		RecordingReadFileSystem seam = new();

		Action observe = () => new ConfigFileObserver(seam).Observe<ConfigDocument>(SeamPath);

		_ = observe.Should().ThrowExactly<ArgumentException>(
			"an abstract TDocument is a caller bug, and NotSupportedException is forbidden here because TryBindBody catches it");
		_ = seam.Calls.Should().BeEmpty(
			"the rejection must happen BEFORE any I/O: nothing may be observed about the user's file on the strength of a type argument that cannot bind");

		// THE POSITIVE CONTROL FOR THE RECORDER ITSELF, without which the assertion above is
		// worthless: a fake that recorded NOTHING — one missing Calls.Add — would satisfy
		// BeEmpty for entirely the wrong reason. The SAME seam instance, given a CONCRETE
		// type argument, must show that it does record reads. So the emptiness above is
		// attributable to the guard rather than to a broken fake.
		_ = new ConfigFileObserver(seam).Observe<AppDocument>(SeamPath);

		_ = seam.Calls.Should().NotBeEmpty(
			"the recorder must actually record, or the BeEmpty assertion above proves nothing about the guard");
	}

	[Fact]
	public void ObserveCandidate_AbstractDocumentType_IsRejectedBeforeAnyReadRatherThanReportedCorrupt()
	{
		// The candidate is built through a seam of its own, so the recorder under test is
		// touched only by the call being measured.
		BackupCandidate candidate = SomeCandidate();
		RecordingReadFileSystem seam = new();

		Action observe = () => new ConfigFileObserver(seam).Observe<ConfigDocument>(candidate);

		_ = observe.Should().ThrowExactly<ArgumentException>(
			"the same hole in a second door: this entry point carries the same constraint and the same abstract-type hazard");
		_ = seam.Calls.Should().BeEmpty("rejected before TryReadAllBytes touches the candidate's path");

		// ITS OWN POSITIVE CONTROL, rather than resting on the one in the Observe fact
		// above. A fake that recorded NOTHING would satisfy BeEmpty for entirely the wrong
		// reason, and a fact should not depend on a sibling to rule that out.
		_ = new ConfigFileObserver(seam).Observe<AppDocument>(candidate);

		_ = seam.Calls.Should().NotBeEmpty(
			"the recorder must actually record, or the BeEmpty assertion above proves nothing about the guard");
	}

	[Fact]
	public void ObserveNthNewestBackup_AbstractDocumentType_IsRejectedBeforeAnyReadRatherThanReportedCorrupt()
	{
		RecordingReadFileSystem seam = new();

		Action observe = () =>
			new ConfigFileObserver(seam).ObserveNthNewestBackup<ConfigDocument>(SeamBackupsDirectory, "app.json", 0);

		_ = observe.Should().ThrowExactly<ArgumentException>(
			"the third door, and the one where inheriting the guard downstream would NOT have been equivalent");

		// THE SHARPEST OF THE THREE. This entry point forwards to Observe(candidate), which
		// carries the guard too — so it would eventually reject an abstract type ANYWAY,
		// but only AFTER DirectoryExists and the enumeration had already run. An empty
		// recorder is what distinguishes "guarded at the entry point" from "guarded
		// eventually", and only the former is what the header claims.
		_ = seam.Calls.Should().BeEmpty(
			"rejected before ListBackupCandidates calls DirectoryExists or enumerates, not merely before the forwarded read");

		// ITS OWN POSITIVE CONTROL, rather than resting on the one in the Observe fact
		// above. A fake that recorded NOTHING would satisfy BeEmpty for entirely the wrong
		// reason, and a fact should not depend on a sibling to rule that out. The seam's
		// EnumerateFiles yields one candidate that really clears the listing's prefix
		// filter, so with a CONCRETE type argument this reaches the enumeration and the
		// forwarded read.
		_ = new ConfigFileObserver(seam)
			.ObserveNthNewestBackup<AppDocument>(SeamBackupsDirectory, "app.json", 0);

		_ = seam.Calls.Should().NotBeEmpty(
			"the recorder must actually record, or the BeEmpty assertion above proves nothing about the guard");
	}

	// The whole observation, not merely "did not throw". A bare no-throw assertion would
	// also pass if the observer had invented a corrupt verdict, which is the outcome this
	// path exists to prevent.
	private static void AssertOpenFailedWithNothingKnown(ConfigFileObservation observed)
	{
		_ = observed.Access.Should().Be(ConfigFileAccess.OpenFailed);
		_ = observed.Envelope.Should().BeNull("nothing was read, so no envelope can be known");
		_ = observed.Body.Should().BeNull("nothing was read, so no body can be known");
		_ = observed.Schema.Should().BeNull("nothing was read, so no schema verdict can be known");
	}

	// THE REAL SEAM MUST HAND BACK A READABLE STREAM, and "not null" is not the same claim.
	//
	// WHY THIS EXISTS AT THE SEAM rather than only at the observer. A one-line reflex edit to
	// SystemStorageReadFileSystem.OpenRead —
	//
	//   public Stream OpenRead(string path) { using var s = File.OpenRead(path); return s; }
	//
	// — returns a stream that is already DISPOSED. It is non-null, so any test asserting only
	// that would stay green, while every read on the production path threw
	// ObjectDisposedException from stream.Read. TryReadAllBytes deliberately lets that escape
	// (it is always a defect, never a state of a config file), so the failure is loud rather
	// than silent — but loud at the CALLER, blamed on the read path. Asserting readability
	// HERE fails the edit at the line that made it.
	//
	// MEASURED on .NET 10: after Dispose, FileStream.CanRead is false and Read throws
	// ObjectDisposedException — so both assertions below genuinely discriminate, and this
	// test can fail.
	[Fact]
	public void SystemSeamOpenRead_ReturnsAReadableStream_NotMerelyANonNullOne()
	{
		string dir = NewTempDirectory();
		try
		{
			string content = /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}";
			string path = WriteFile(dir, "app.json", content);

			using Stream stream = new SystemStorageReadFileSystem().OpenRead(path);

			_ = stream.CanRead.Should().BeTrue(
				"a disposed stream is non-null but reports CanRead false, and every read through this seam would then throw");

			// The bytes themselves, not merely the flag: a stream could report CanRead and
			// still yield nothing. Read through the same shape TryReadAllBytes uses.
			byte[] buffer = new byte[content.Length];
			int total = 0;
			while (total < buffer.Length)
			{
				int read = stream.Read(buffer, total, buffer.Length - total);
				if (read == 0)
				{
					break;
				}

				total += read;
			}

			_ = total.Should().Be(
				buffer.Length,
				"the seam must yield the file's bytes, so a stream that opens but delivers nothing fails here too");
			_ = System.Text.Encoding.UTF8.GetString(buffer, 0, total).Should().Be(
				content,
				"and they must be the bytes that were written, not merely the right count");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// ------------------------------------------------------------------- backups

	[Fact]
	public void ListBackupCandidates_BackupsDirectoryAbsent_ReturnsEmpty()
	{
		string dir = NewTempDirectory();
		try
		{
			IReadOnlyList<BackupCandidate> candidates =
				NewObserver().ListBackupCandidates(Path.Combine(dir, "backups"), "app.json");

			_ = candidates.Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void ListBackupCandidates_MintedStamps_OrderedNewestFirst()
	{
		string dir = NewTempDirectory();
		try
		{
			_ = WriteBackup(dir, $"app.json.20260830120000001.{GuidA}.bak", Envelope(1, 0));
			_ = WriteBackup(dir, $"app.json.20260830120000003.{GuidA}.bak", Envelope(1, 0));
			_ = WriteBackup(dir, $"app.json.20260830120000002.{GuidA}.bak", Envelope(1, 0));

			IReadOnlyList<BackupCandidate> candidates = NewObserver().ListBackupCandidates(dir, "app.json");

			// Compared as one joined string rather than with a containment assertion, so
			// this pins the EXACT full order and not merely a relative one.
			_ = Names(candidates).Should().Be(string.Join(
				"|",
				$"app.json.20260830120000003.{GuidA}.bak",
				$"app.json.20260830120000002.{GuidA}.bak",
				$"app.json.20260830120000001.{GuidA}.bak"));
			_ = candidates.Should().OnlyContain(
				candidate => candidate.StampSource == BackupStampSource.MintedFileName,
				"every one of these names carries a parseable minted stamp");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// The candidate list must not capture another document's backups. "app" prefixes
	// "appx", so an ordinal test without the "." boundary would lump them together.
	[Fact]
	public void ListBackupCandidates_ForeignDocumentPrefix_IsNotCaptured()
	{
		string dir = NewTempDirectory();
		try
		{
			_ = WriteBackup(dir, $"app.json.20260830120000001.{GuidA}.bak", Envelope(1, 0));
			_ = WriteBackup(dir, $"appx.json.20260830120000002.{GuidA}.bak", Envelope(1, 0));

			IReadOnlyList<BackupCandidate> candidates = NewObserver().ListBackupCandidates(dir, "app.json");

			_ = candidates.Should().ContainSingle().Which.Name.Value.Should().StartWith("app.json.");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// NON-RECURSIVE, and this is the test that keeps it that way. Quarantined
	// originals will live in backups\corrupted\; recursing would offer them up as
	// promotable candidates (and, on the write side, feed them to the retention
	// prune).
	[Fact]
	public void ListBackupCandidates_QuarantineSubdirectory_IsNotEnumerated()
	{
		string dir = NewTempDirectory();
		try
		{
			_ = WriteBackup(dir, $"app.json.20260830120000001.{GuidA}.bak", Envelope(1, 0));

			string corrupted = Path.Combine(dir, "corrupted");
			_ = Directory.CreateDirectory(corrupted);
			File.WriteAllText(Path.Combine(corrupted, $"app.json.20260830120000009.{GuidA}.bak"), Envelope(1, 0));

			IReadOnlyList<BackupCandidate> candidates = NewObserver().ListBackupCandidates(dir, "app.json");

			_ = candidates.Should().ContainSingle(
				"backups\\corrupted\\ holds quarantined originals and must stay out of the candidate list")
				.Which.Name.Value.Should().Be($"app.json.20260830120000001.{GuidA}.bak");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A name with no parseable stamp is aged by LAST-WRITE-TIME (which File.Replace
	// does not rewrite), not by creation time (which it does).
	[Fact]
	public void ListBackupCandidates_UnparseableName_FallsBackToLastWriteTimeForOrdering()
	{
		string dir = NewTempDirectory();
		try
		{
			_ = WriteBackup(dir, $"app.json.20260830120000001.{GuidA}.bak", Envelope(1, 0));

			// A foreign file made deliberately NEWER than the stamped one, so ordering
			// by the fallback key is observable: if the fallback were ignored, this file
			// would sort at the default DateTime and land last.
			string foreign = WriteBackup(dir, "app.json.legacy.bak", Envelope(1, 0));
			File.SetLastWriteTimeUtc(foreign, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

			IReadOnlyList<BackupCandidate> candidates = NewObserver().ListBackupCandidates(dir, "app.json");

			_ = candidates.Should().HaveCount(2);
			_ = candidates[0].Name.Value.Should().Be("app.json.legacy.bak");
			_ = candidates[0].StampSource.Should().Be(BackupStampSource.LastWriteTime);
			_ = candidates[0].OrderingStampUtc.Should().Be(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
			_ = candidates[1].StampSource.Should().Be(BackupStampSource.MintedFileName);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// SAME-MILLISECOND TIE. Identical stamps plus a stable sort would leave the winner
	// to Directory.EnumerateFiles' return order, which is filesystem-dependent and not
	// contractual. The ordinal name key makes "newest" reproducible.
	//
	// WHICH of the two wins carries no meaning — their names differ only by a random
	// guid. The property claimed is only that repeated runs agree.
	[Fact]
	public void ListBackupCandidates_IdenticalStamps_BreaksTheTieDeterministically()
	{
		string dir = NewTempDirectory();
		try
		{
			_ = WriteBackup(dir, $"app.json.20260830120000123.{GuidA}.bak", Envelope(1, 0));
			_ = WriteBackup(dir, $"app.json.20260830120000123.{GuidB}.bak", Envelope(1, 0));

			ConfigFileObserver observer = NewObserver();

			IReadOnlyList<BackupCandidate> first = observer.ListBackupCandidates(dir, "app.json");
			IReadOnlyList<BackupCandidate> second = observer.ListBackupCandidates(dir, "app.json");

			// The ordinal name key decides it: "bbbb..." sorts after "aaaa...", and the
			// order is DESCENDING, so the B-guid name comes first.
			_ = Names(first).Should().Be(string.Join(
				"|",
				$"app.json.20260830120000123.{GuidB}.bak",
				$"app.json.20260830120000123.{GuidA}.bak"));
			_ = Names(second).Should().Be(
				Names(first),
				"repeated listings of the same directory must agree, or \"newest\" is not reproducible");
			_ = first[0].OrderingStampUtc.Should().Be(
				first[1].OrderingStampUtc,
				"the tie is real — both were minted in the same millisecond");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// THE 1601 TRAP, via the fake seam because a real filesystem cannot be made to
	// lose a file between enumeration and stat on demand.
	//
	// File.GetLastWriteTimeUtc does not throw for a missing path — it returns
	// 1601-01-01T00:00:00Z. Left undetected, a vanished candidate would sort as the
	// oldest REAL backup with nothing in the result saying otherwise.
	[Fact]
	public void ListBackupCandidates_CandidateVanishedBeforeStat_IsLabelledNotReportedAs1601()
	{
		FakeReadFileSystem fake = new();
		fake.Files["app.json.legacy.bak"] = null;
		fake.Files[$"app.json.20260830120000001.{GuidA}.bak"] = Envelope(1, 0);

		IReadOnlyList<BackupCandidate> candidates =
			new ConfigFileObserver(fake).ListBackupCandidates(@"X:\backups", "app.json");

		BackupCandidate vanished = candidates.Should().HaveCount(2).And
			.ContainSingle(candidate => candidate.Name.Value == "app.json.legacy.bak").Which;

		_ = vanished.StampSource.Should().Be(
			BackupStampSource.Vanished,
			"the missing-file sentinel must never be passed off as an observed timestamp");
		_ = vanished.Vanished.Should().BeTrue();
		_ = candidates[^1].Should().BeSameAs(vanished, "with nothing better to sort it by, it still sorts oldest");
	}

	// A vanished candidate that is then OBSERVED reports Absent, and carries no
	// BackupObservation at all: Core's factories all presuppose bytes were obtained,
	// so inventing one here would misdescribe the file.
	[Fact]
	public void ObserveBackup_CandidateVanished_ReportsAbsentWithNoObservation()
	{
		FakeReadFileSystem fake = new();
		fake.Files["app.json.legacy.bak"] = null;

		BackupCandidateObservation? observed =
			new ConfigFileObserver(fake).ObserveNthNewestBackup<AppDocument>(@"X:\backups", "app.json", 0);

		_ = observed.Should().NotBeNull();
		_ = observed!.Access.Should().Be(ConfigFileAccess.Absent);
		_ = observed.Observation.Should().BeNull();
	}

	// THE SELECTION RULE. The Nth-newest is taken and merely described — the accessor
	// must NOT skip past a candidate that looks unpromotable. Deciding which backup is
	// promotable belongs to the pure layer; stealing that here would defeat the split.
	[Fact]
	public void ObserveNthNewestBackup_NewestIsUnpromotable_StillDescribesItRatherThanSkipping()
	{
		string dir = NewTempDirectory();
		try
		{
			// Newest: its OWN envelope is newer-version, so promoting it would turn a
			// recoverable corrupt-file problem into a permanent read-only lockout.
			_ = WriteBackup(dir, $"app.json.20260830120000003.{GuidA}.bak", Envelope(2, 0));

			// A DIFFERENT document's backup, carrying the middle stamp. It must be
			// filtered out entirely, so the index below skips from ...003 to ...001 —
			// which also proves the Nth-newest index counts THIS document's candidates
			// and not everything sitting in the directory.
			_ = WriteBackup(
				dir,
				$"browsers.json.20260830120000002.{GuidA}.bak",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":[]}");

			// Oldest: the one a "helpful" implementation would jump to.
			_ = WriteBackup(dir, $"app.json.20260830120000001.{GuidA}.bak", Envelope(1, 0));

			ConfigFileObserver observer = NewObserver();

			BackupCandidateObservation newest =
				observer.ObserveNthNewestBackup<AppDocument>(dir, "app.json", 0)!;

			_ = newest.Candidate.Name.Value.Should().Be($"app.json.20260830120000003.{GuidA}.bak");
			_ = newest.Access.Should().Be(ConfigFileAccess.Opened);
			_ = newest.Observation!.Envelope.Should().Be(EnvelopeParse.Parsed);
			_ = newest.Observation.Schema.Should().Be(
				SchemaCompatibility.NewerVersionReadOnly,
				"the backup's own envelope is observed and reported; whether to promote it is not decided here");

			// The retry loop's next step — the accessor iterates, it does not choose.
			BackupCandidateObservation second =
				observer.ObserveNthNewestBackup<AppDocument>(dir, "app.json", 1)!;

			_ = second.Candidate.Name.Value.Should().Be($"app.json.20260830120000001.{GuidA}.bak");
			_ = second.Observation!.Schema.Should().Be(SchemaCompatibility.CompletelyCompatible);
			_ = second.Observation.Body.Should().Be(DocumentBodyParse.Parsed);

			_ = observer.ObserveNthNewestBackup<AppDocument>(dir, "app.json", 2).Should().BeNull(
				"past the end of the candidate list there is nothing to describe");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// The backup gets the same envelope-before-body discipline as the destination: its
	// envelope parsed, so a verdict exists, but its body is garbage.
	[Fact]
	public void ObserveNthNewestBackup_GarbageBody_ReportsBodyUnparseableWithASchemaVerdict()
	{
		string dir = NewTempDirectory();
		try
		{
			_ = WriteBackup(
				dir,
				$"browsers.json.20260830120000001.{GuidA}.bak",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":\"nope\"}");

			BackupCandidateObservation observed =
				NewObserver().ObserveNthNewestBackup<BrowsersDocument>(dir, "browsers.json", 0)!;

			_ = observed.Observation!.Envelope.Should().Be(EnvelopeParse.Parsed);
			_ = observed.Observation.Body.Should().Be(DocumentBodyParse.Unparseable);
			_ = observed.Observation.Schema.Should().Be(SchemaCompatibility.CompletelyCompatible);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void ObserveNthNewestBackup_UnparseableEnvelope_CarriesNoSchemaVerdict()
	{
		string dir = NewTempDirectory();
		try
		{
			_ = WriteBackup(dir, $"app.json.20260830120000001.{GuidA}.bak", "not json");

			BackupCandidateObservation observed =
				NewObserver().ObserveNthNewestBackup<AppDocument>(dir, "app.json", 0)!;

			_ = observed.Observation!.Envelope.Should().Be(EnvelopeParse.Unparseable);
			_ = observed.Observation.Body.Should().Be(DocumentBodyParse.Unparseable);
			_ = observed.Observation.Schema.Should().BeNull(
				"an envelope that did not parse yields no verdict, and none may be invented");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A locked BACKUP is OpenFailed too, and — like the destination — is not corrupt.
	[Fact]
	public void ObserveNthNewestBackup_CandidateLocked_ReportsOpenFailedWithNoObservation()
	{
		string dir = NewTempDirectory();
		try
		{
			string backup = WriteBackup(dir, $"app.json.20260830120000001.{GuidA}.bak", Envelope(1, 0));

			using FileStream exclusive = new(backup, FileMode.Open, FileAccess.Read, FileShare.None);
			BackupCandidateObservation observed =
				NewObserver().ObserveNthNewestBackup<AppDocument>(dir, "app.json", 0)!;

			_ = observed.Access.Should().Be(ConfigFileAccess.OpenFailed);
			_ = observed.Observation.Should().BeNull(
				"a backup that could not be read must not be described as damaged");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A candidate observation attaches to a Core ConfigFileObservation through Core's
	// own WithBackup — the observer never reaches for a backup on its own, because
	// pairing them is the caller's business.
	[Fact]
	public void Observe_DestinationObservation_CarriesNoBackupUntilOneIsAttached()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "app.json", Envelope(1, 0));
			_ = WriteBackup(dir, $"app.json.20260830120000001.{GuidA}.bak", Envelope(1, 0));

			ConfigFileObserver observer = NewObserver();
			ConfigFileObservation observed = observer.Observe<AppDocument>(path);

			_ = observed.Backup.Should().BeNull("the observer does not go looking for a backup by itself");

			BackupObservation backup = observer
				.ObserveNthNewestBackup<AppDocument>(dir, "app.json", 0)!
				.Observation!;

			_ = observed.WithBackup(backup).Backup.Should().BeSameAs(backup);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// -------------------------------------------------------------- no writes

	// PROVEN, not claimed. The whole safety argument for splitting this slice is that
	// the observer cannot write, so the claim has to be checked rather than asserted
	// in prose.
	//
	// The snapshot covers relative paths, sizes and last-write times of every entry in
	// the tree, sorted so enumeration order cannot affect the comparison. Reading a
	// file updates last-ACCESS time, not last-write time, so a clean read leaves this
	// snapshot byte-identical.
	//
	// Every observation path runs in between, including the failing ones — an absent
	// file, a missing directory, a corrupt document and a quarantine subdirectory are
	// exactly where a "helpful" CreateDirectory or defaults-write would creep in.
	[Fact]
	public void Observe_AcrossEveryObservationPath_LeavesTheDirectoryTreeUntouched()
	{
		string root = NewTempDirectory();
		try
		{
			string dataDir = Path.Combine(root, "data");
			string backupsDir = Path.Combine(root, "backups");
			_ = Directory.CreateDirectory(dataDir);
			_ = Directory.CreateDirectory(backupsDir);
			_ = Directory.CreateDirectory(Path.Combine(backupsDir, "corrupted"));

			string good = WriteFile(dataDir, "app.json", Envelope(1, 0));
			string corrupt = WriteFile(dataDir, "settings.json", "not json");
			string unbindable = WriteFile(
				dataDir,
				"browsers.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":\"nope\"}");
			_ = WriteBackup(backupsDir, $"app.json.20260830120000001.{GuidA}.bak", Envelope(1, 0));
			_ = WriteBackup(backupsDir, "app.json.legacy.bak", Envelope(1, 0));

			ConfigFileObserver observer = NewObserver();
			string before = SnapshotTree(root);

			_ = observer.Observe<AppDocument>(good);
			_ = observer.Observe<SettingsDocument>(corrupt);
			_ = observer.Observe<BrowsersDocument>(unbindable);
			_ = observer.Observe<AppDocument>(Path.Combine(dataDir, "absent.json"));
			_ = observer.Observe<AppDocument>(Path.Combine(root, "no-such-dir", "app.json"));
			_ = observer.ListBackupCandidates(backupsDir, "app.json");
			_ = observer.ListBackupCandidates(Path.Combine(root, "no-such-backups"), "app.json");
			_ = observer.ObserveNthNewestBackup<AppDocument>(backupsDir, "app.json", 0);
			_ = observer.ObserveNthNewestBackup<AppDocument>(backupsDir, "app.json", 1);
			_ = observer.ObserveNthNewestBackup<AppDocument>(backupsDir, "app.json", 99);

			_ = SnapshotTree(root).Should().Be(
				before,
				"the observer must not create, modify, delete or provision anything — that is the safety property this slice exists to guarantee");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// A missing destination directory must not be silently provisioned. Stated
	// separately from the snapshot test because this is the specific temptation:
	// "the directory isn't there, I'll just create it".
	[Fact]
	public void Observe_MissingDirectory_IsNotProvisioned()
	{
		string root = NewTempDirectory();
		try
		{
			string missing = Path.Combine(root, "data");

			_ = NewObserver().Observe<AppDocument>(Path.Combine(missing, "app.json"));

			_ = Directory.Exists(missing).Should().BeFalse("the read path never provisions");
			_ = Directory.GetFileSystemEntries(root).Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// ---------------------------------------------------------------- arguments

	// Throwing is reserved for a genuine programming error. Absent, locked, denied and
	// corrupt are observations; a null argument is a caller bug caught before any I/O.
	[Fact]
	public void Observer_NullArguments_ThrowArgumentNull()
	{
		ConfigFileObserver observer = NewObserver();

		Action nullSeam = () => _ = new ConfigFileObserver(null!);
		Action nullPath = () => observer.Observe<AppDocument>((string)null!);
		Action nullDirectory = () => observer.ListBackupCandidates(null!, "app.json");
		Action nullFileName = () => observer.ListBackupCandidates(@"X:\backups", null!);
		Action nullCandidate = () => observer.Observe<AppDocument>((BackupCandidate)null!);

		// ObserveNthNewestBackup's OWN guards, which nothing covered. It does not merely
		// forward to ListBackupCandidates — it guards first, so a null must be refused
		// BEFORE any I/O, and that is only demonstrable by calling it directly.
		Action nthNullDirectory = () => observer.ObserveNthNewestBackup<AppDocument>(null!, "app.json", 0);
		Action nthNullFileName = () => observer.ObserveNthNewestBackup<AppDocument>(@"X:\backups", null!, 0);

		_ = nullSeam.Should().Throw<ArgumentNullException>();
		_ = nullPath.Should().Throw<ArgumentNullException>();
		_ = nullDirectory.Should().Throw<ArgumentNullException>();
		_ = nullFileName.Should().Throw<ArgumentNullException>();
		_ = nullCandidate.Should().Throw<ArgumentNullException>();
		_ = nthNullDirectory.Should().Throw<ArgumentNullException>();
		_ = nthNullFileName.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void ObserveNthNewestBackup_NegativeIndex_ThrowsArgumentOutOfRange()
	{
		Action negative = () => NewObserver().ObserveNthNewestBackup<AppDocument>(@"X:\backups", "app.json", -1);

		_ = negative.Should().Throw<ArgumentOutOfRangeException>();
	}

	// ------------------------------------------- illegal states are unwritable

	// The three factories produce EXACTLY the three legal pairings, and nothing else
	// can be built. Before this, an internal unvalidated constructor let in-assembly
	// code (and the test assembly, via InternalsVisibleTo) write "opened but nothing
	// observed" or "could not read it, and here is its verdict" — self-contradictory
	// observations of the kind Core makes unrepresentable everywhere else.
	[Fact]
	public void BackupCandidateObservationFactories_ProduceExactlyTheIntendedPairings()
	{
		BackupCandidate candidate = SomeCandidate();
		BackupObservation observation = BackupObservation.Loaded(
			candidate.Name,
			SchemaCompatibility.CompletelyCompatible);

		BackupCandidateObservation absent = BackupCandidateObservation.Absent(candidate);
		BackupCandidateObservation openFailed = BackupCandidateObservation.OpenFailed(candidate);
		BackupCandidateObservation opened = BackupCandidateObservation.Opened(candidate, observation);

		_ = absent.Access.Should().Be(ConfigFileAccess.Absent);
		_ = absent.Observation.Should().BeNull("no bytes were obtained, so there is no verdict to carry");

		_ = openFailed.Access.Should().Be(ConfigFileAccess.OpenFailed);
		_ = openFailed.Observation.Should().BeNull(
			"a file that would not open is not known to be damaged, so it carries no verdict");

		_ = opened.Access.Should().Be(ConfigFileAccess.Opened);
		_ = opened.Observation.Should().BeSameAs(observation);

		_ = absent.Candidate.Should().BeSameAs(candidate);
		_ = openFailed.Candidate.Should().BeSameAs(candidate);
		_ = opened.Candidate.Should().BeSameAs(candidate);
	}

	// "Opened, but nothing observed" is refused at construction rather than documented.
	[Fact]
	public void BackupCandidateObservationOpened_NullObservation_ThrowsArgumentNull()
	{
		Action opened = () => BackupCandidateObservation.Opened(SomeCandidate(), null!);

		_ = opened.Should().Throw<ArgumentNullException>(
			"an Opened observation with nothing observed is a contradiction, not a state");
	}

	// The invariant is UNWRITABLE, not merely unwritten: there is no constructor a
	// future maintainer could reach for, from this assembly or the test assembly.
	[Fact]
	public void BackupCandidateObservation_HasNoConstructorReachableOutsideItself()
	{
		_ = typeof(BackupCandidateObservation)
			.GetConstructors(BindingFlags.Public
				| BindingFlags.NonPublic
				| BindingFlags.Instance)
			.Should().OnlyContain(
				constructor => constructor.IsPrivate,
				"the named factories are the only way in, or the illegal states come back");
	}

	// ------------------------------------------------- honest stamp labelling

	// A candidate whose STAT IS DENIED must not be labelled Vanished. Vanished means
	// "gone, move to the next-newest", so a caller reading it is entitled to abandon
	// the candidate for good — but this file EXISTS and may well become readable. It
	// is the locked-versus-absent distinction, one level down.
	[Fact]
	public void ListBackupCandidates_StatDenied_IsLabelledStampUnavailableAndNotVanished()
	{
		FakeReadFileSystem fake = new();

		// No parseable stamp in either name, so both must fall through to the stat —
		// which is the only path on which the label is decided.
		fake.Files["app.json.denied.bak"] = Envelope(1, 0);
		_ = fake.StatThrowsUnauthorized.Add("app.json.denied.bak");
		fake.Files["app.json.gone.bak"] = null;
		fake.Files[$"app.json.20260830120000001.{GuidA}.bak"] = Envelope(1, 0);

		ConfigFileObserver observer = new(fake);

		IReadOnlyList<BackupCandidate> candidates = observer.ListBackupCandidates(@"X:\backups", "app.json");

		BackupCandidate denied = candidates
			.Should().ContainSingle(candidate => candidate.Name.Value == "app.json.denied.bak").Which;
		BackupCandidate gone = candidates
			.Should().ContainSingle(candidate => candidate.Name.Value == "app.json.gone.bak").Which;

		_ = denied.StampSource.Should().Be(
			BackupStampSource.StampUnavailable,
			"the stat failed; the file did not vanish, and claiming it did would invite a caller to skip a recoverable backup");
		_ = denied.Vanished.Should().BeFalse("an existing file must never report itself as gone");
		_ = gone.StampSource.Should().Be(BackupStampSource.Vanished);
		_ = gone.Vanished.Should().BeTrue();
		_ = denied.StampSource.Should().NotBe(gone.StampSource, "the two states must stay distinguishable");

		// Both carry the sentinel, so both sort oldest — and the ordinal name key
		// settles the order between them reproducibly.
		_ = denied.OrderingStampUtc.Should().Be(BackupFileNameStamp.MissingFileTimestampSentinel);
		_ = gone.OrderingStampUtc.Should().Be(denied.OrderingStampUtc);
		_ = Names(candidates).Should().Be(
			Names(observer.ListBackupCandidates(@"X:\backups", "app.json")),
			"repeated listings must agree, or \"newest\" is not reproducible");
		_ = Names(candidates).Should().Be(string.Join(
			"|",
			$"app.json.20260830120000001.{GuidA}.bak",
			"app.json.gone.bak",
			"app.json.denied.bak"));
	}

	// A denied stat still yields OpenFailed when the candidate is actually observed,
	// so nothing collapses into a corrupt verdict on the strength of a failed stat.
	[Fact]
	public void ObserveBackup_StatDeniedCandidate_IsStillOpenFailedRatherThanCorrupt()
	{
		FakeReadFileSystem fake = new();
		fake.Files["app.json.denied.bak"] = Envelope(1, 0);
		_ = fake.StatThrowsUnauthorized.Add("app.json.denied.bak");
		_ = fake.OpenThrowsUnauthorized.Add("app.json.denied.bak");

		BackupCandidateObservation observed =
			new ConfigFileObserver(fake).ObserveNthNewestBackup<AppDocument>(@"X:\backups", "app.json", 0)!;

		_ = observed.Candidate.StampSource.Should().Be(BackupStampSource.StampUnavailable);
		_ = observed.Access.Should().Be(ConfigFileAccess.OpenFailed);
		_ = observed.Observation.Should().BeNull(
			"a denied file was never read, so it is not known to be damaged");
	}

	// ------------------------------------------------------- catch ordering

	// THE UNTESTED HALF OF A CORRECT ORDERING. DirectoryNotFoundException and
	// FileNotFoundException both derive from IOException, so the Absent catch must come
	// FIRST or a missing directory would be reported as a locked file. Only the
	// FileNotFoundException half was exercised before: the real-filesystem
	// missing-directory test returns at the FileExists guard and never reaches a catch.
	[Fact]
	public void Observe_OpenThrowsDirectoryNotFound_ReportsAbsentAndNotOpenFailed()
	{
		FakeReadFileSystem fake = new();

		// Present as far as FileExists is concerned, so the guard is passed and the
		// open really is reached — otherwise this would prove nothing about the catch.
		fake.Files["app.json"] = Envelope(1, 0);
		_ = fake.OpenThrowsDirectoryNotFound.Add("app.json");

		ConfigFileObservation observed = new ConfigFileObserver(fake).Observe<AppDocument>(@"X:\data\app.json");

		_ = observed.Access.Should().Be(
			ConfigFileAccess.Absent,
			"a missing directory is genuinely not there; reporting it as OpenFailed would describe it as a refusal");
		_ = observed.Envelope.Should().BeNull();
		_ = observed.Schema.Should().BeNull();
	}

	// ------------------------------------------- duplicate envelope keys

	// THE MEASURED DIVERGENCE, recorded in code so the reason for strictness cannot be
	// casually reverted. Before AllowDuplicateProperties = false:
	//
	//   {"schemaVersion":1,"schemaRevision":0,"SCHEMAVERSION":2}
	//     envelope -> 1   (TryGetProperty is ordinal; the one exact spelling matches,
	//                      so the case-insensitive fallback is never reached)
	//     body     -> 2   (PropertyNameCaseInsensitive makes both the same member,
	//                      and the last one wins)
	//
	// A version-2 document was therefore cleared as version 1 / CompletelyCompatible:
	// the planner's STEP 3 HardStopReadOnly was bypassed and UseLoadedDocument licensed
	// a write that drops the newer version's unknown members permanently. Refusing the
	// file routes it to a quarantine-FIRST repair instead, which preserves the original
	// bytes in backups\corrupted for a later version to read.
	[Theory]
	// Mixed-case duplicate, exact spelling FIRST — the divergent case.
	[InlineData(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0,\"SCHEMAVERSION\":2}")]
	// Mixed-case duplicate, exact spelling LAST.
	[InlineData(/*lang=json,strict*/ "{\"SCHEMAVERSION\":1,\"schemaVersion\":2,\"schemaRevision\":0}")]
	// Exact duplicate. Reader and binder agreed here (both took the last value), but a
	// document that cannot say what version it is has no business licensing a write.
	[InlineData(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaVersion\":2,\"schemaRevision\":0}")]
	// The duplicate on the OTHER envelope property.
	[InlineData(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0,\"SCHEMAREVISION\":9}")]
	public void Observe_DuplicateEnvelopeKeys_AreRefusedRatherThanSilentlyResolved(string content)
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "app.json", content);

			ConfigFileObservation observed = NewObserver().Observe<AppDocument>(path);

			_ = observed.Access.Should().Be(
				ConfigFileAccess.Opened,
				"the bytes WERE obtained — this is a content verdict, not a refusal to read");
			_ = observed.Envelope.Should().Be(
				EnvelopeParse.Unparseable,
				"a document whose declared version is ambiguous must not be cleared as any version");
			_ = observed.Schema.Should().BeNull(
				"no verdict may be invented for an envelope that could not be read unambiguously");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// THE MIRROR CONSTRAINT, asserted directly rather than argued in prose: the reader
	// must never refuse a file the writer can produce, or the app would write files it
	// then reports as corrupt. Driven through a REAL AtomicJsonFileStore.Write for
	// every storage DTO, because a hand-written literal would only pin the reader
	// against itself.
	//
	// Safe because the writer serializes from a runtime object: it cannot emit two
	// properties differing only in case (no storage DTO has members colliding
	// case-insensitively, and none carries [JsonPropertyName]), and it always emits
	// every constructor parameter.
	[Fact]
	public void StrictReader_AcceptsEveryDocumentTheWriterCanProduce()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			AtomicJsonFileStore store = new(backupsDir);
			ConfigFileObserver observer = NewObserver();

			string appPath = Path.Combine(dataDir, "app.json");
			string settingsPath = Path.Combine(dataDir, "settings.json");
			string browsersPath = Path.Combine(dataDir, "browsers.json");

			store.Write(appPath, new AppDocument());
			store.Write(settingsPath, new SettingsDocument());
			store.Write(browsersPath, new BrowsersDocument([SomeBrowser("edge-stable"), SomeBrowser("chrome-stable")]));

			// A second write of each, so the minted BACKUPS are covered too — the read
			// path parses those with the same strict options.
			store.Write(appPath, new AppDocument());
			store.Write(browsersPath, new BrowsersDocument([SomeBrowser("edge-stable")]));

			foreach ((string path, ConfigFileObservation observed) in new[]
			{
				(appPath, observer.Observe<AppDocument>(appPath)),
				(settingsPath, observer.Observe<SettingsDocument>(settingsPath)),
				(browsersPath, observer.Observe<BrowsersDocument>(browsersPath)),
			})
			{
				_ = observed.Access.Should().Be(ConfigFileAccess.Opened);
				_ = observed.Envelope.Should().Be(
					EnvelopeParse.Parsed,
					"the strict envelope reader must accept what the writer minted");
				_ = observed.Body.Should().Be(
					DocumentBodyParse.Parsed,
					"the strict binder must accept what the writer minted, or the app writes files it calls corrupt");
				_ = observed.Schema.Should().Be(SchemaCompatibility.CompletelyCompatible);
			}

			// The MINTED BACKUPS too — the read path parses those with the same strict
			// options, so a backup the writer produced must stay readable as well.
			_ = Directory.GetFiles(backupsDir).Should().HaveCount(
				2,
				"the second write of app.json and of browsers.json each minted one backup");

			BackupCandidateObservation appBackup =
				observer.ObserveNthNewestBackup<AppDocument>(backupsDir, "app.json", 0)!;
			BackupCandidateObservation browsersBackup =
				observer.ObserveNthNewestBackup<BrowsersDocument>(backupsDir, "browsers.json", 0)!;

			foreach (BackupCandidateObservation backup in new[] { appBackup, browsersBackup })
			{
				_ = backup.Access.Should().Be(ConfigFileAccess.Opened);
				_ = backup.Observation!.Envelope.Should().Be(EnvelopeParse.Parsed);
				_ = backup.Observation.Body.Should().Be(
					DocumentBodyParse.Parsed,
					"a minted backup must remain readable by the strict reader");
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// --------------------------------------- a whole-document parse, really

	// A POSITIONAL RECORD'S OMITTED PARAMETER IS NOT A PARSED DOCUMENT. Given only an
	// envelope, System.Text.Json used to hand back a NON-null BrowsersDocument whose
	// Browsers list was null, so the "is not null" bind test passed and this was
	// reported Loaded — satisfying owner decision 2 ("a write is licensed only on a
	// whole-document parse") on a document that never parsed whole, and handing the
	// consumer a NullReferenceException, which is not in this component's vocabulary.
	[Fact]
	public void Observe_BrowsersDocumentWithNoBrowsersMember_ReportsBodyUnparseableRatherThanLoaded()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "browsers.json", Envelope(1, 0));

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Access.Should().Be(ConfigFileAccess.Opened);
			_ = observed.Envelope.Should().Be(
				EnvelopeParse.Parsed,
				"the envelope is genuinely readable; it is the body that is incomplete");
			_ = observed.Body.Should().Be(
				DocumentBodyParse.Unparseable,
				"a required member was absent, so no usable document came back — that is not a whole-document parse");
			_ = observed.Schema.Should().Be(
				SchemaCompatibility.CompletelyCompatible,
				"the envelope cleared, and its verdict is still worth reporting");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// An explicit null for the same member is the same fact, by a different route.
	[Fact]
	public void Observe_BrowsersDocumentWithNullBrowsersMember_ReportsBodyUnparseable()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(
				dir,
				"browsers.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":null}");

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Body.Should().Be(
				DocumentBodyParse.Unparseable,
				"a non-nullable member given null yields no usable document");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// W7.1's RulesDocument carries the same kind of collection member, so it needs the
	// same pair of facts: a null member and a null element must both be refused rather
	// than handing the evaluator a document it would throw on.
	[Fact]
	public void Observe_RulesDocumentWithNullRulesMember_ReportsBodyUnparseable()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(
				dir,
				"rules.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0,\"rules\":null}");

			ConfigFileObservation observed = NewObserver().Observe<RulesDocument>(path);

			_ = observed.Body.Should().Be(
				DocumentBodyParse.Unparseable,
				"a non-nullable member given null yields no usable document");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void Observe_RulesDocumentWithNullRuleElement_ReportsBodyUnparseable()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(
				dir,
				"rules.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0,\"rules\":[null]}");

			ConfigFileObservation observed = NewObserver().Observe<RulesDocument>(path);

			_ = observed.Body.Should().Be(
				DocumentBodyParse.Unparseable,
				"a null ELEMENT is not file state the evaluator can consume, so the whole document is refused");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// ------------------------------------------------------------ size ceiling

	// A LEGITIMATE FILE MUST NOT BE REFUSED — the only way the ceiling could do harm.
	// The kill-test harness already exercises a synthetic 300-entry document of roughly
	// 100 KB; this one is several times larger and must still read as sound.
	[Fact]
	public void Observe_DocumentFarLargerThanTheKillTestHarnessCase_IsStillAccepted()
	{
		string dir = NewTempDirectory();
		try
		{
			// TEN TIMES the harness's entry count, and several times its byte size.
			const int entries = 3000;
			string path = WriteFile(dir, "browsers.json", BrowsersJson(entries));

			// The premise, asserted rather than assumed: a miscounted fixture would let
			// this test pass while proving nothing about headroom. The bound is set well
			// below the fixture's actual size (~600 KB) so it pins "comfortably larger
			// than the ~100 KB / 300-entry harness case" without being brittle about the
			// exact bytes per entry.
			_ = new FileInfo(path).Length.Should().BeGreaterThan(
				300 * 1024,
				"the point of this test is that a document well past the harness case is accepted");

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Access.Should().Be(ConfigFileAccess.Opened, "8 MiB leaves ample headroom over a real browser list");
			_ = observed.Body.Should().Be(DocumentBodyParse.Parsed);
			_ = observed.Schema.Should().Be(SchemaCompatibility.CompletelyCompatible);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// OVER THE CEILING IS AN OBSERVATION, NOT A THROW. Reported as OpenFailed rather
	// than as a corrupt state: nothing was read, so nothing is known to be damaged, and
	// calling it corrupt would later authorise destroying a file nobody looked at.
	[Fact]
	public void Observe_FileOverTheCeiling_ReportsOpenFailedRatherThanThrowing()
	{
		// Through the seam rather than by writing 8 MiB to disk: the ceiling is enforced
		// on the numbers the seam reports, so an oversized file can be described
		// without producing one.
		OversizedReadFileSystem fake = new(reportedLength: (8L * 1024 * 1024) + 1, streamedBytes: 64);

		ConfigFileObservation observed = new ConfigFileObserver(fake).Observe<AppDocument>(@"X:\data\app.json");

		_ = observed.Access.Should().Be(ConfigFileAccess.OpenFailed);
		_ = observed.Envelope.Should().BeNull("nothing was read, so nothing is known about the content");
		_ = observed.Schema.Should().BeNull();
	}

	// THE TOCTOU PATH, which the pre-check alone cannot close. The length is measured
	// in one call and the file opened in another, so a file that measures small and is
	// extended in between would be read to EOF — a MemoryStream grows — and the
	// OutOfMemoryException escape the ceiling exists to prevent would still be
	// reachable. The COPY is bounded, so the bytes actually arriving decide it.
	[Fact]
	public void Observe_FileGrowsAfterItsLengthIsChecked_IsStillRefusedByTheBoundedCopy()
	{
		// Reports an in-budget length, then streams well past the ceiling.
		OversizedReadFileSystem fake = new(
			reportedLength: 1024,
			streamedBytes: (8L * 1024 * 1024) + 4096);

		ConfigFileObservation observed = new ConfigFileObserver(fake).Observe<AppDocument>(@"X:\data\app.json");

		_ = observed.Access.Should().Be(
			ConfigFileAccess.OpenFailed,
			"the limit must be enforced against the bytes arriving, not against a size that was true a moment ago");
		_ = fake.BytesHandedOut.Should().BeLessThan(
			(8L * 1024 * 1024) + 4096,
			"an absurd file must be refused without first reading all of it into memory");
	}

	// ------------------------------------------ a directory is not an absent file

	// File.Exists is false for a DIRECTORY, so a directory sitting where a config file
	// belongs used to be reported Absent — which takes the planner to STEP 1 and
	// licenses CreateDefaultsAndPersist against a path that is a directory. The honest
	// report is OpenFailed: something is there, and it cannot be read as a file. That
	// is also the safe report, because the planner ends OpenFailed at STEP 2 with
	// in-memory defaults and no write of any kind.
	[Fact]
	public void Observe_DirectoryWhereTheConfigFileBelongs_ReportsOpenFailedRatherThanAbsent()
	{
		string root = NewTempDirectory();
		try
		{
			string path = Path.Combine(root, "app.json");
			_ = Directory.CreateDirectory(path);

			// The premise: the framework really does report this as "no such file".
			_ = File.Exists(path).Should().BeFalse("File.Exists is false for a directory — that is the trap");
			_ = Directory.Exists(path).Should().BeTrue();

			ConfigFileObservation observed = NewObserver().Observe<AppDocument>(path);

			_ = observed.Access.Should().Be(
				ConfigFileAccess.OpenFailed,
				"reporting Absent would license the next slice to write defaults to a path that is a directory");
			_ = observed.Envelope.Should().BeNull();
			_ = observed.Schema.Should().BeNull();
			_ = observed.Body.Should().BeNull();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// ------------------------------------------- listing never throws

	// A name that cannot be a legal BackupName is not a candidate — it is SKIPPED, not
	// thrown on. BackupName refuses empty, whitespace, "." / ".." and path-qualified
	// input, and that construction sat outside every guard in the listing, so a seam
	// yielding such an entry made ListBackupCandidates throw ArgumentException.
	// Unreachable with the real Directory.EnumerateFiles, but the never-throws property
	// belongs to the observer, not to one seam implementation.
	//
	// MEASURED, and the measurement matters: on MODERN .NET, Path.GetFileName splits
	// ONLY on '\' and '/'. It does NOT treat a colon as a volume separator (that was
	// .NET Framework behaviour), so a name carrying an alternate-data-stream colon
	// survives Path.GetFileName intact, passes the "<document>." prefix filter, and
	// reaches BackupName — which refuses it. So this guard is a LIVE path for any seam
	// implementation, not merely defensive: without it the listing would propagate
	// ArgumentException and the never-throws property would belong to the real
	// filesystem rather than to this type.
	[Fact]
	public void ListBackupCandidates_SeamYieldsUnusableEntries_SkipsThemWithoutThrowing()
	{
		// THE PREMISES, asserted because each decides WHICH mechanism drops the entry —
		// a miscounted assumption here would let this test pass for the wrong reason.
		_ = Path.GetFileName(@"X:\backups\app.json.alt:stream.bak").Should().Be(
			"app.json.alt:stream.bak",
			"modern .NET does not split on a colon, so this name really does reach the BackupName guard");
		_ = Path.GetFileName(@"X:\backups\app.json.trailing\").Should().BeEmpty(
			"a trailing separator yields an empty name, which the prefix filter drops before any parsing");
		_ = Path.GetFileName(@"X:\backups\.").Should().Be(".");

		HostileNameReadFileSystem fake = new(
		[
			$@"X:\backups\app.json.20260830120000001.{GuidA}.bak",

			// A NULL ENTRY, and it used to take the WHOLE LISTING down rather than just
			// itself. MEASURED on .NET 10: Path.GetFileName((string?)null) returns null,
			// and StartsWith on that null throws NullReferenceException — which is not in
			// the observer's failure vocabulary and would have escaped
			// ListBackupCandidates, destroying every other candidate in the directory
			// along with it. Both IStorageReadFileSystem and ConfigFileObserver's
			// constructor are PUBLIC, so an external caller can supply such a seam
			// through the public API; this is not an internal-only concern.
			//
			// `null!` states the hostility exactly: the seam's element type is
			// non-nullable and this implementation disregards that, which is what a
			// third-party implementation can do at run time because nullable annotations
			// are erased. Suppressed here rather than by widening the fake's element type,
			// which would misdescribe the SEAM as nullable when it is not.
			null!,

			// An EMPTY entry, which reduces to an empty name by a second route. The
			// prefix filter already dropped this, so the behaviour is unchanged — it is
			// here so the guard is exercised on both shapes it claims to cover rather
			// than on one.
			"",

			// Reduces to "" — dropped by the prefix filter.
			@"X:\backups\app.json.trailing\",

			// SURVIVES as "app.json.alt:stream.bak", matches the prefix, and is then
			// refused by BackupName as path-qualified. THE ENTRY THAT EXERCISES THE
			// GUARD: before it existed, this threw ArgumentException out of the listing.
			@"X:\backups\app.json.alt:stream.bak",

			// Reduce to "." and ".." — both dropped by the prefix filter.
			@"X:\backups\.",
			@"X:\backups\..",
		]);

		ConfigFileObserver observer = new(fake);

		Action list = () => observer.ListBackupCandidates(@"X:\backups", "app.json");
		_ = list.Should().NotThrow("an unusable entry is an observation, never an exception");

		IReadOnlyList<BackupCandidate> candidates = observer.ListBackupCandidates(@"X:\backups", "app.json");

		_ = candidates.Should().ContainSingle(
			"only the one legal, prefix-matching name is a candidate")
			.Which.Name.Value.Should().Be($"app.json.20260830120000001.{GuidA}.bak");

		// Whatever comes back is a legal BackupName — the type's own invariant, held
		// across a seam that tried its best to break it.
		_ = candidates.Should().OnlyContain(
			candidate => candidate.Name.Value.Length > 0
				&& !candidate.Name.Value.Contains('\\', StringComparison.Ordinal)
				&& !candidate.Name.Value.Contains('/', StringComparison.Ordinal)
				&& !candidate.Name.Value.Contains(':', StringComparison.Ordinal));
	}

	// ------------------- a body-only duplicate must not unmake the envelope

	// THE REGRESSION THAT REMOVING AllowDuplicateProperties FROM THE ENVELOPE EXISTS TO
	// PREVENT, pinned as BEHAVIOUR rather than as the absence of a line of code: this
	// test fails the moment anyone re-adds the flag.
	//
	// JsonDocument's duplicate detection is DOCUMENT-WIDE and recursive, with no option
	// scoping it to the top level, so the flag made a duplicate key ANYWHERE in the body
	// unmake an envelope that is itself completely unambiguous. That is destructive
	// precisely in the case where being wrong costs the most: a NEWER-VERSION file must
	// reach NewerVersionReadOnly -> HardStopReadOnly, where nothing is ever written.
	// With the envelope refused it became EnvelopeUnparseable, which carries NO schema,
	// so the planner fell through to a quarantine-then-defaults repair — the newer
	// original moved into backups\corrupted and defaults written over it.
	//
	// The planner outcome is asserted, not just the observation, because the observation
	// alone does not show what was at stake.
	[Fact]
	public void Observe_NewerVersionFileWithADuplicateKeyInItsBody_StillReadsTheEnvelopeSoThePlannerHardStops()
	{
		string dir = NewTempDirectory();
		try
		{
			// version 2 — a schema this build must refuse to WRITE but must still READ.
			// The duplicate sits two levels down, in a member this build does not map.
			string path = WriteFile(
				dir,
				"app.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":2,\"schemaRevision\":0,\"nested\":{\"x\":1,\"x\":2}}");

			ConfigFileObservation observed = NewObserver().Observe<AppDocument>(path);

			_ = observed.Access.Should().Be(ConfigFileAccess.Opened);
			_ = observed.Envelope.Should().Be(
				EnvelopeParse.Parsed,
				"the envelope's own two properties are single and unambiguous; a duplicate in the BODY says nothing about them");
			_ = observed.Schema.Should().Be(
				SchemaCompatibility.NewerVersionReadOnly,
				"the whole point is that this verdict survives to reach the planner");

			ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observed);

			_ = decision.Action.Should().Be(
				ConfigLoadAction.HardStopReadOnly,
				"a newer-version file is read-only; refusing its envelope would have routed it to a destructive repair instead");
			_ = decision.Reason.Should().Be(ConfigLoadReason.MainFileNewerSchemaReadOnly);
			_ = decision.MutatesDisk.Should().BeFalse("nothing may be written against a newer version's file");
			_ = decision.QuarantineFirst.Should().BeFalse("the newer original must not be moved into quarantine either");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// THE SAME PROTECTION ON THE BodyUnparseable ROUTE, which is the shape the envelope
	// change actually hands the planner most often: the envelope parses and carries a
	// schema verdict, while the body genuinely does not bind.
	//
	// This pins the PLANNER'S EVALUATION ORDER, which is what makes removing the flag
	// safe: ConfigLoadPlanner.Plan tests observation.Envelope == Parsed and runs
	// TryHardStopOnSchema BEFORE it looks at observation.Body, so a newer-version file
	// hard-stops at STEP 3 and never reaches STEP 5's repair. If that order were ever
	// swapped, this test fails and the file would be quarantined instead.
	[Fact]
	public void Observe_NewerVersionFileWithAnUnbindableBody_HardStopsBeforeAnyRepairRoute()
	{
		string dir = NewTempDirectory();
		try
		{
			// Envelope: version 2, perfectly readable. Body: "browsers" is declared as a
			// list, so a string there cannot bind — and a duplicate key sits in an
			// unmapped member as well, which the old envelope flag would have choked on.
			string path = WriteFile(
				dir,
				"browsers.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":2,\"schemaRevision\":0,\"browsers\":\"not-an-array\",\"nested\":{\"x\":1,\"x\":2}}");

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Envelope.Should().Be(EnvelopeParse.Parsed);
			_ = observed.Body.Should().Be(DocumentBodyParse.Unparseable);
			_ = observed.Schema.Should().Be(
				SchemaCompatibility.NewerVersionReadOnly,
				"an unparseable body still carries the envelope's verdict — that is what protects the file");

			ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observed);

			_ = decision.Action.Should().Be(
				ConfigLoadAction.HardStopReadOnly,
				"the schema hard stop is evaluated BEFORE any body-unparseable repair route");
			_ = decision.MutatesDisk.Should().BeFalse();
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// ------------------------------- an optional field is optional again

	// THE CORE DEFAULT ITSELF. LastVerifiedAt is DateTimeOffset? and documented as
	// "null means not yet verified", so it is optional in spirit — but a positional
	// parameter without a default is MANDATORY under
	// RespectRequiredConstructorParameters, which made an absent key destroy the whole
	// browser list. This pins the default at the DTO, where the fix lives.
	[Fact]
	public void BrowserDefinition_LastVerifiedAt_IsOptionalAndDefaultsToNull()
	{
		BrowserDefinition browser = new(
			"edge-stable",
			"Edge",
			"Microsoft",
			"stable",
			@"C:\Program Files\Edge\msedge.exe",
			"known-path");

		_ = browser.LastVerifiedAt.Should().BeNull(
			"null means \"not yet verified\", which is a legitimate state and not a missing value");
	}

	// A HAND-EDITED OR THIRD-PARTY browsers.json THAT OMITS lastVerifiedAt MUST READ.
	// Before the default, this was BodyUnparseable -> quarantine-then-defaults: the
	// user's entire browser list replaced because one nullable field was absent. The
	// mirror test cannot catch it, because the writer always emits every constructor
	// parameter.
	[Fact]
	public void Observe_BrowsersJsonOmittingLastVerifiedAt_BindsRatherThanDestroyingTheList()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "browsers.json", BrowsersJsonWithout(omittedKey: "lastVerifiedAt"));

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Body.Should().Be(
				DocumentBodyParse.Parsed,
				"an absent OPTIONAL field is not a corrupt document, and calling it one destroys the user's browser list");
			_ = observed.Schema.Should().Be(SchemaCompatibility.CompletelyCompatible);

			ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observed);

			_ = decision.Action.Should().Be(ConfigLoadAction.UseLoadedDocument);
			_ = decision.MutatesDisk.Should().BeFalse("nothing needs repairing here");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// An EXPLICIT null is the same fact by the other route, and must not regress into a
	// refusal now that the parameter carries a default.
	[Fact]
	public void Observe_BrowsersJsonWithExplicitNullLastVerifiedAt_StillBinds()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "browsers.json", BrowsersJsonWithout(omittedKey: null));

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Body.Should().Be(DocumentBodyParse.Parsed);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// THE SIX STRINGS STAY MANDATORY. The point of the fix was to make ONE nullable
	// field optional, not to loosen the DTO — omitting any required string still yields
	// no usable document. Without this, "give LastVerifiedAt a default" could quietly
	// become "drop RespectRequiredConstructorParameters", which measurably binds a NULL
	// string for an absent "vendor".
	[Theory]
	[InlineData("browserId")]
	[InlineData("displayName")]
	[InlineData("vendor")]
	[InlineData("channel")]
	[InlineData("exePath")]
	[InlineData("detectionSource")]
	public void Observe_BrowsersJsonOmittingARequiredString_IsStillRefused(string omittedKey)
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "browsers.json", BrowsersJsonWithout(omittedKey));

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Envelope.Should().Be(EnvelopeParse.Parsed, "the envelope is untouched; it is the body that is short a member");
			_ = observed.Body.Should().Be(
				DocumentBodyParse.Unparseable,
				"a non-nullable string given no value would bind null and hand the consumer a NullReferenceException");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A NULL LIST ELEMENT, which the nullable-annotation flag CANNOT catch. The
	// framework documents the limitation as intentional: "we cannot distinguish between
	// List<string> and List<string?> types."
	// https://devblogs.microsoft.com/dotnet/system-text-json-in-dotnet-9/
	//
	// So {"browsers":[null]} BINDS — a non-null document whose list holds one null — and
	// was reported Loaded, handing the consumer a NullReferenceException, which is not in
	// this component's vocabulary. The post-bind check turns it into BodyUnparseable.
	[Fact]
	public void Observe_BrowsersJsonWithANullListElement_ReportsBodyUnparseableRatherThanLoaded()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(
				dir,
				"browsers.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":[null]}");

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Envelope.Should().Be(EnvelopeParse.Parsed);
			_ = observed.Body.Should().Be(
				DocumentBodyParse.Unparseable,
				"a list holding a null element is not a usable document, and the nullable flags provably do not reach list elements");
			_ = observed.Schema.Should().Be(
				SchemaCompatibility.CompletelyCompatible,
				"the envelope cleared, so its verdict still travels — a newer-version file stays protected on this route");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A NULL BESIDE A NON-NULL ELEMENT, which is the shape `[null]` alone CANNOT pin.
	//
	// WHAT THIS ADDS OVER THE TEST ABOVE, precisely, because otherwise it is redundant.
	// HasNullMemberOrElement is an indexed loop, and `[null]` already kills both classic
	// off-by-one regressions on its own: starting at index = 1, or bounding at
	// index < Count - 1, each gives ZERO iterations against a one-element list, so the guard
	// returns false, the file is reported Loaded, and that test goes red. Those bounds are
	// therefore already pinned and nothing here is aimed at them.
	//
	// WHAT IS NOT PINNED is a regression that inspects only SOME elements — the shape
	// `if (member.Count > 0 && member[0] is null) return true;` — which passes `[null]`
	// happily and misses a null anywhere else. Before these rows no literal in the suite put
	// a null BESIDE a non-null element, so that regression was invisible. The four rows kill
	// it from both ends: "first element only" survives rows 1 and 4 but fails rows 2 and 3,
	// and "last element only" survives rows 3 and 4 but fails rows 1 and 2. No row is
	// redundant against both.
	//
	// THE PREMISE IS GUARDED PER ROW rather than assumed. The non-null element must be a
	// shape the binder GENUINELY ACCEPTS, or the row would report Unparseable because the
	// valid element was malformed — passing for the wrong reason and proving nothing about
	// null elements. So each row first observes the same element ALONE and asserts Parsed.
	[Theory]
	[InlineData("null,valid")]
	[InlineData("valid,null,valid")]
	[InlineData("valid,null")]
	[InlineData("null,null")]
	public void Observe_BrowsersJsonWithANullBesideANonNullElement_ReportsBodyUnparseable(string pattern)
	{
		string dir = NewTempDirectory();
		try
		{
			// THE PREMISE, checked and not assumed: the element this row pads with must bind
			// on its own. Without this the row could go green off a malformed literal.
			ConfigFileObservation control = NewObserver().Observe<BrowsersDocument>(
				WriteFile(dir, "browsers-control.json", BrowsersJsonWithElements("valid")));

			_ = control.Body.Should().Be(
				DocumentBodyParse.Parsed,
				"the padding element must be a shape the binder accepts, or this row would refuse the document for the wrong reason");

			string path = WriteFile(dir, "browsers.json", BrowsersJsonWithElements(pattern));

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Envelope.Should().Be(
				EnvelopeParse.Parsed,
				"the envelope is untouched; it is one element of the body list that is null");
			_ = observed.Body.Should().Be(
				DocumentBodyParse.Unparseable,
				"a null ANYWHERE in the list makes the document unusable, not merely a null in the first position");
			_ = observed.Schema.Should().Be(
				SchemaCompatibility.CompletelyCompatible,
				"the envelope cleared, so its verdict still travels — a newer-version file stays protected on this route");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// ---------- the post-bind collection check names its members, and the tripwire keeps it honest

	// WHAT THESE TESTS MEANT BEFORE, AND WHY THEY NOW SAY SOMETHING ELSE. Two of them asserted
	// that the post-bind check covers a DTO the observer has never heard of, because the check
	// was a REFLECTION WALK over the bound document's properties. That walk was REVERSED: it
	// executed arbitrary DTO getters on the read path, and measured, it reported an INTACT file
	// as BodyUnparseable for an ordinary `[JsonIgnore] ... = null!` member — a verdict that
	// licenses quarantine-then-overwrite. It also let InvalidOperationException and
	// ArgumentException escape Observe, and under a trimmed publish it would have silently
	// stopped protecting anything. See HasUnusableCollectionMember for the full account.
	//
	// SO THE COVERAGE CLAIM IS GONE AND IS NOT QUIETLY REPHRASED. The check now names each
	// member it inspects, and a document type it does not name falls to "usable", which is the
	// FAIL-SAFE direction: a false positive here is destructive, a false negative merely hands
	// the consumer a NullReferenceException. The obligation to extend the check when a Core DTO
	// gains a collection member is enforced at BUILD TIME by
	// EveryCollectionMemberOnACoreDocument_IsAccountedFor below, where a failure is loud and
	// harmless.
	//
	// The stand-in DTOs at the bottom of this file therefore serve a DIFFERENT purpose now:
	// they pin the BINDER's behaviour (which is framework behaviour, and is what D-070's
	// additive-readability promise rests on) and they pin the fail-safe direction.

	// THE ANTI-REFLECTION TRIPWIRE, and it is the inversion of the two tests it replaces.
	//
	// Both shapes below are ones the reverted reflection walk FLAGGED: a `= null!` collection
	// default on a DTO the observer does not name, and a null ELEMENT on the same. Both are now
	// reported Parsed, because the check names its members and these types are not among them.
	// Asserting that explicitly does three things a comment could not: it states the real
	// limitation instead of implying coverage that does not exist, it records that the
	// limitation is the safe direction, and it FAILS if the reflection walk is ever
	// reintroduced — because reflection would report Unparseable here.
	//
	// This is the honest replacement for
	// Observe_FutureDtoWithANullBangCollectionDefault_ReportsBodyUnparseableRatherThanLoaded and
	// Observe_FutureDtoWithANullCollectionElement_ReportsBodyUnparseableRatherThanLoaded, whose
	// assertions cannot hold against an explicit check and which would have had to be deleted
	// or weakened to "did not throw" — a test that cannot fail. The behaviour they were really
	// protecting, on the DTO that actually exists, is pinned against BrowsersDocument by
	// Observe_BrowsersDocumentWithNullBrowsersMember_ReportsBodyUnparseable (null member) and
	// Observe_BrowsersJsonWithANullListElement_ReportsBodyUnparseableRatherThanLoaded (null
	// element). Neither of those lost anything in the reversal.
	[Fact]
	public void Observe_DocumentTypeTheCheckDoesNotName_IsReportedLoadedRatherThanCorrupt()
	{
		string dir = NewTempDirectory();
		try
		{
			// The member is ABSENT, which is what a file written by an EARLIER revision looks
			// like to a later build. With `= null!` it binds and the member is null.
			string nullBangPath = WriteFile(
				dir,
				"app.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0}");

			ConfigFileObservation nullBang = NewObserver().Observe<FutureNullBangDefaultDocument>(nullBangPath);

			_ = nullBang.Envelope.Should().Be(EnvelopeParse.Parsed);
			_ = nullBang.Body.Should().Be(
				DocumentBodyParse.Parsed,
				"the check names the members it inspects and this type is not among them, so the document is not reported corrupt - the fail-safe direction, and a reintroduced reflection walk would report Unparseable here and fail this test");

			string nullElementPath = WriteFile(
				dir,
				"tags.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0,\"tags\":[\"ok\",null]}");

			ConfigFileObservation nullElement = NewObserver().Observe<FutureEmptyCollectionDefaultDocument>(nullElementPath);

			_ = nullElement.Body.Should().Be(
				DocumentBodyParse.Parsed,
				"same limitation, stated for the null-ELEMENT shape: unnamed type, so no scan, so no corrupt verdict on a file nobody found fault with");

			// AND THE JsonIgnore SHAPE THAT MADE THE REVERSAL NECESSARY. This is the measured
			// destructive false positive: the walk had no [JsonIgnore] exclusion, so it
			// inspected a member the binder never bound and called an intact file corrupt.
			string ignoredPath = WriteFile(
				dir,
				"cache.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0}");

			ConfigFileObservation ignored = NewObserver().Observe<FutureJsonIgnoreCacheDocument>(ignoredPath);

			_ = ignored.Body.Should().Be(
				DocumentBodyParse.Parsed,
				"a [JsonIgnore] collection member is not file state, so a null one says NOTHING about the file - this exact shape was reported BodyUnparseable by the reflection walk, which licenses quarantine-then-overwrite of an intact file");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// PINS THE BINDER, NOT THE CHECK, and the distinction is the whole reason this test kept
	// its assertion through the reversal. `= []` is the default shape the recorded additive
	// rule asks for, and what is being measured is that the FRAMEWORK binds an absent member to
	// an empty collection under this component's exact BodyOptions. That is what D-070's
	// promise (an additive revision stays readable by a later build) rests on, and it would
	// break if the options changed or the default were dropped — so the test can still fail for
	// the reason it exists.
	[Fact]
	public void Observe_FutureDtoWithAnEmptyCollectionDefault_StillBinds()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "app.json", /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");

			ConfigFileObservation observed = NewObserver().Observe<FutureEmptyCollectionDefaultDocument>(path);

			_ = observed.Body.Should().Be(
				DocumentBodyParse.Parsed,
				"`= []` is the default shape the rule asks for: the member is absent from an older revision's file and binds to an empty collection, which is a valid document");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A collection of VALUE elements can never hold a null, so nothing needs to look at its
	// elements at all — which is why HasNullMemberOrElement constrains its element type to
	// `class` and a value-element member would get no arm. Pinned as BINDER behaviour, like
	// the test above: a populated value collection is an ordinary document, and a guard that
	// refused it would quarantine intact files.
	[Fact]
	public void Observe_FutureDtoWithAValueElementCollection_StillBinds()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(
				dir,
				"app.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0,\"counts\":[1,2,3]}");

			ConfigFileObservation observed = NewObserver().Observe<FutureValueElementDocument>(path);

			_ = observed.Body.Should().Be(DocumentBodyParse.Parsed);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// THE TRIPWIRE, AND IT IS NOW THE ONLY THING STANDING WHERE AUTOMATISM USED TO. The
	// production check names each collection member it inspects, so adding one to a Core DTO
	// without extending it silently forgoes the protection. This makes that impossible to do
	// quietly: it inventories Core's collection members by reflection and compares them
	// against a pinned list, so ADDING OR REMOVING one fails the suite with instructions.
	//
	// REFLECTION BELONGS HERE AND NOT ON THE READ PATH. Here a wrong answer is a red test at
	// build time — loud, harmless, and landing the editor on the exact obligation. On the read
	// path the same mechanism executed arbitrary DTO getters and, measured, reported an INTACT
	// file as BodyUnparseable for a `[JsonIgnore]` member, which licenses
	// quarantine-then-overwrite. Same tool, opposite consequence of being wrong.
	//
	// A tripwire rather than a generated behaviour test: constructing valid JSON for an
	// arbitrary future DTO needs a mini serializer that would itself be a source of wrong
	// answers, and an unreliable generator guarding a destructive code path is a bad trade.
	//
	// TWO EXCLUSIONS, AND THE FIRST ONE IS WHY THIS TEST HAD TO CHANGE TOO. Without them the
	// tripwire DEMANDS COVERAGE OF THE EXACT SHAPE THAT MADE THE REVERSAL NECESSARY:
	//
	//   [JsonIgnore] — the member is excluded from serialization, so it is NOT FILE STATE and
	//     a null one says nothing whatever about the file. A tripwire instructing an editor to
	//     "cover" it would be instructing them to reintroduce a destructive false positive; it
	//     is worse than no tripwire. `[JsonIgnore] IReadOnlyList<T> Cache { get; init; } =
	//     null!;` is an ordinary, correct DTO member.
	//   !CanRead — GetProperties returns SET-ONLY properties, whose value cannot be read
	//     without throwing ArgumentException (that throw was one of the TWO exceptions the
	//     reverted walk let ESCAPE Observe — as distinct from the walk's third measured
	//     defect, the DESTRUCTIVE FALSE POSITIVE on an intact [JsonIgnore] document, which
	//     escaped nothing and overwrote the user's config instead). There is nothing to
	//     inspect, so there is nothing to demand.
	//
	// Otherwise the filter mirrors what counts as a collection member in the production check:
	// public instance, non-indexer (a value that cannot be read without arguments), not string
	// (every string is an IEnumerable<char>), assignable to IEnumerable. Inherited members ARE
	// walked, which is right — the serializer binds them too.
	[Fact]
	public void EveryCollectionMemberOnACoreDocument_IsAccountedFor()
	{
		string[] discovered = [.. CollectionMemberInventory(typeof(ConfigDocument).Assembly)];

		// Declared as a local rather than written inline, so the assertion binds
		// unambiguously to Equal(IEnumerable<string>, because) instead of leaving overload
		// resolution to decide between that and Equal(params string[]).
		string[] accountedFor = ["ActivationHistoryDocument.Entries", "BrowsersDocument.Browsers", "RulesDocument.Rules"];

		_ = discovered.Should().Equal(
			accountedFor,
			"a collection member was added to or removed from a Core config document. If ADDED: give it a default that is a valid non-null instance (`= []`, never `= null!`), add an arm for it to ConfigFileObserver.HasUnusableCollectionMember (the check names its members explicitly and does NOT cover it automatically - that automatism was reverted because it destroyed intact files), add a null-member and a null-element test beside Observe_BrowsersDocumentWithNullBrowsersMember_ReportsBodyUnparseable, and extend this inventory");
	}

	// THE AUDIT GAP THIS ANSWERS. The inventory above scans ONE assembly, while
	// Observe<TDocument> where TDocument : ConfigDocument imposes NO assembly constraint - so a
	// ConfigDocument subtype declared OUTSIDE Core reaches the read path and appears in no
	// inventory. This asserts that no such production type exists among the assemblies loaded
	// in this test process, which covers Core and Platform.Windows (both referenced by this
	// project, both loaded by the time any test here has run).
	//
	// STATED HONESTLY, THIS IS A BEST-EFFORT STRENGTHENING AND NOT A CLOSED GAP. Reflection
	// cannot see a type in an assembly that is not loaded, and BroChoos.App is not referenced
	// by this test project at all - a ConfigDocument subtype declared there would be invisible
	// here. What makes that tolerable is the DIRECTION of the residual failure, which is the
	// opposite of the reverted design's: an unnamed document type falls to "usable" in
	// HasUnusableCollectionMember, so the consequence is a false NEGATIVE (the consumer gets a
	// NullReferenceException) rather than an intact file reported corrupt. The reflection
	// walk's equivalent gap failed destructively. If a production ConfigDocument is ever
	// declared outside Core, this test is where to widen the scan.
	//
	// The test assembly is the one permitted exception: its stand-in DTOs at the bottom of this
	// file exist precisely to be types the observer does not name.
	[Fact]
	public void NoProductionAssemblyOtherThanCore_DeclaresAConfigDocument()
	{
		string[] declaring =
		[
			.. AppDomain.CurrentDomain.GetAssemblies()
				.Where(static assembly => ConcreteConfigDocuments(assembly).Any())
				.Select(static assembly => assembly.GetName().Name ?? "<unnamed>")
				.Distinct(StringComparer.Ordinal)
				.OrderBy(static name => name, StringComparer.Ordinal),
		];

		// A PREMISE, GUARDED. If Core were somehow absent from this list the test would pass
		// while proving nothing, so the positive control is asserted before the negative one.
		_ = declaring.Should().Contain(
			"BroChoos.Core",
			"the scan must actually be finding ConfigDocument subtypes, or its silence means nothing");

		string[] permitted = ["BroChoos.Core", "BroChoos.Platform.Windows.Tests"];

		_ = declaring.Should().BeEquivalentTo(
			permitted,
			"a ConfigDocument subtype outside Core reaches Observe<TDocument> (which constrains only to ConfigDocument) and appears in no inventory, so ConfigFileObserver.HasUnusableCollectionMember would give it no null-collection protection. Either move it to Core or widen EveryCollectionMemberOnACoreDocument_IsAccountedFor to scan its assembly too");
	}

	// Shared by both tests above so the two cannot disagree about what a collection member is.
	private static IEnumerable<string> CollectionMemberInventory(Assembly assembly)
	{
		return ConcreteConfigDocuments(assembly)
			.SelectMany(static type => type
				.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Where(static member => member.CanRead
					&& member.GetCustomAttribute<JsonIgnoreAttribute>() is null
					&& member.GetIndexParameters().Length == 0
					&& member.PropertyType != typeof(string)
					&& typeof(IEnumerable).IsAssignableFrom(member.PropertyType))
				.Select(member => $"{type.Name}.{member.Name}"))
			.OrderBy(static name => name, StringComparer.Ordinal);
	}

	// GetTypes throws for an assembly whose dependencies cannot all be resolved, and the
	// cross-assembly scan walks assemblies this project never chose. The loadable types are
	// enough for both questions asked here, and an unloadable one must not turn a scan into a
	// test failure that says nothing about config documents.
	private static IEnumerable<Type> ConcreteConfigDocuments(Assembly assembly)
	{
		Type[] types;
		try
		{
			types = assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException partial)
		{
			// Select after Where rather than a null-forgiving cast over the sequence: `!` on an
			// IEnumerable<Type?> does not change its ELEMENT nullability, so it would silence
			// nothing and mislead the next reader about what was checked.
			types = [.. partial.Types.Where(static type => type is not null).Select(static type => type!)];
		}

		return types.Where(static type => !type.IsAbstract && typeof(ConfigDocument).IsAssignableFrom(type));
	}

	// ------------------------------------------------------------- read seam shape

	// THE READ SEAM'S ONE-WAY PROPERTY, MADE FAILABLE. Three separate headers rest their
	// safety argument on IStorageReadFileSystem having NO WRITE MEMBER: the interface's own
	// ("Do not add a CreateDirectory, a Delete, a Move or an open-for-write member here"),
	// ConfigFileObserver's ordering argument, and FakeReadFileSystem's comment in this very
	// file ("Nothing here can write, because the interface has no write member to
	// implement"). The property is real, but it was COMPILER-ENFORCED ONLY: adding a Move to
	// the interface would have reddened NOTHING in this suite, and all three comments would
	// have gone on asserting a property the code no longer had.
	//
	// NAMES, NOT A COUNT. A bare HaveCount(6) is precisely the defect shape this codebase
	// keeps producing: it fails with "expected 6, found 7" and leaves the reader to diff an
	// interface by hand. Every assertion below names the member it is unhappy about and says
	// which direction to act in.
	//
	// THE VERB SCAN RUNS FIRST, deliberately, so the WORST addition gets the SHARPEST
	// message: a new Move fails as "declares a member that writes", not as an off-by-one in
	// a list. The exact-name pin below it then catches the additions and removals a verb
	// scan cannot judge — a harmless-looking GetCreationTimeUtc, or a GetLength that
	// someone deleted.
	//
	// WHAT THIS DOES NOT PROVE, stated rather than implied: OpenRead returns Stream, and a
	// Stream can be writable at runtime. This pins the seam's DECLARED SURFACE, which is
	// what the three comments actually claim; it cannot pin what an implementation hands
	// back through it.
	[Fact]
	public void TheReadSeam_DeclaresOnlyReadMembers_AndNothingThatCanWrite()
	{
		// Every member name that would mean this seam can change the disk. "Write" is in
		// here even though GetLastWriteTimeUtc contains it — the pinned names below are
		// exempted by EXACT match, so the legitimate member does not trip its own scan
		// while a real WriteAllBytes still would.
		string[] writeVerbs =
		[
			"Create", "Delete", "Move", "Copy", "Replace", "Write", "Append",
			"Truncate", "Remove", "Save", "Persist", "SetAttributes",
		];

		string[] pinned =
		[
			"DirectoryExists",
			"EnumerateFiles",
			"FileExists",
			"GetLastWriteTimeUtc",
			"GetLength",
			"OpenRead",
		];

		// PROPERTIES AND EVENTS FIRST, so the member inventory below needs no special-name
		// filtering: with both empty, everything GetMembers returns is a plain method and
		// no accessor can hide behind a name filter.
		_ = typeof(IStorageReadFileSystem).GetProperties().Should().BeEmpty(
			"a property could hand out a writable handle, and this test's member scan reads method names");
		_ = typeof(IStorageReadFileSystem).GetEvents().Should().BeEmpty();

		// THE HOLE THIS CLOSES IS NOT HYPOTHETICAL REFLECTION TRIVIA: GetMembers on an
		// INTERFACE does not return members inherited from base interfaces (unlike a class).
		// So `interface IStorageReadFileSystem : ISomethingWithMove` would add a Move that
		// the inventory below never sees. Asserting the seam extends nothing is what makes
		// the inventory complete.
		_ = typeof(IStorageReadFileSystem).GetInterfaces().Should().BeEmpty(
			"a base interface's members do not appear in GetMembers on an interface, so extending one would smuggle a member past this test");

		string[] discovered =
		[
			.. typeof(IStorageReadFileSystem)
				.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
				.Select(static member => member.Name)
				.OrderBy(static name => name, StringComparer.Ordinal),
		];

		string[] writeLike = [.. WriteLikeMembers(discovered, pinned, writeVerbs)];

		_ = writeLike.Should().BeEmpty(
			"IStorageReadFileSystem declares {0}, which names a disk mutation. The read seam's one-way property is what lets ConfigFileObserver be trusted not to write before the ordering invariant is satisfied (observe -> decide -> preserve -> write), and three headers cite it by name. Put the member on IStorageRepairFileSystem or IStorageFileSystem instead, so the component that needs it takes a seam that is VISIBLY able to write",
			string.Join(", ", writeLike));

		string[] added = [.. discovered.Except(pinned, StringComparer.Ordinal)];
		string[] removed = [.. pinned.Except(discovered, StringComparer.Ordinal)];

		_ = discovered.Should().Equal(
			pinned,
			"the READ seam's member list changed. ADDED: {0}. REMOVED: {1}. If ADDED and it only observes, confirm it writes nothing, then add it to this pin and to FakeReadFileSystem. If ADDED and it mutates anything, it does not belong on this interface at all. If REMOVED, drop it from this pin too - a stale pin that no longer matches the interface stops guarding it",
			added.Length == 0 ? "<none>" : string.Join(", ", added),
			removed.Length == 0 ? "<none>" : string.Join(", ", removed));

		// A PREMISE, GUARDED, in the manner of NoProductionAssemblyOtherThanCore above: if
		// the matcher above could not recognise a write member, its silence would mean
		// nothing and this whole test would pass vacuously. IStorageRepairFileSystem is the
		// control - the ACTING seam LEGITIMATELY declares Move and Copy, that is its entire
		// purpose, and it is a DIFFERENT interface that this test does not police. Feeding
		// it through the same matcher proves the matcher fires.
		string[] repairMembers =
		[
			.. typeof(IStorageRepairFileSystem)
				.GetMembers(BindingFlags.Public | BindingFlags.Instance)
				.Select(static member => member.Name),
		];

		// Declared as a local for the reason the inventory test above declares its own:
		// a collection expression cannot infer BeEquivalentTo's generic parameter.
		string[] expectedActingMembers = ["Move", "WriteBytesAtomically"];

		_ = WriteLikeMembers(repairMembers, pinned: [], writeVerbs).Should().BeEquivalentTo(
			expectedActingMembers,
			"the ACTING seam must trip the same matcher, or the read seam's clean result proves nothing. This is a control, not a violation: IStorageRepairFileSystem exists to move and copy bytes visibly");
	}

	// Members whose NAME claims a disk mutation. `pinned` names are exempted by exact
	// match, which is what keeps the legitimate GetLastWriteTimeUtc from matching "Write".
	private static IEnumerable<string> WriteLikeMembers(
		IEnumerable<string> members,
		string[] pinned,
		string[] writeVerbs)
	{
		return members
			.Where(member => !pinned.Contains(member, StringComparer.Ordinal))
			.Where(member => writeVerbs.Any(verb => member.Contains(verb, StringComparison.Ordinal)));
	}

	// AN EMPTY LIST IS NOT A NULL ELEMENT. The post-bind check must not overreach: no
	// browsers detected is an ordinary, valid state, and refusing it would quarantine a
	// perfectly good file.
	[Fact]
	public void Observe_BrowsersJsonWithAnEmptyList_StillBinds()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(
				dir,
				"browsers.json",
									 /*lang=json,strict*/
									 "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":[]}");

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Body.Should().Be(
				DocumentBodyParse.Parsed,
				"an empty browser list is a legitimate document, not a corrupt one");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// ------------------------------- ArgumentException is an observation too

	// A NUL-BEARING PATH FROM THE SEAM MUST NOT ESCAPE AS ArgumentException. The three
	// framework calls behind the seam throw it — measured below as a premise, not
	// assumed — and it is NEITHER IOException NOR UnauthorizedAccessException, so it
	// used to sail straight out of ListBackupCandidates and Observe.
	//
	// Unreachable through the real SystemStorageReadFileSystem, and deliberately fixed
	// anyway: TryCreateName exists for an equally-unreachable case, because the
	// never-throws property belongs to this TYPE, not to one seam implementation. The
	// seam here delegates to the REAL framework calls, so the exception is genuine
	// rather than staged.
	[Fact]
	public void Observer_SeamYieldsANulBearingPath_ReportsObservationsRatherThanThrowing()
	{
		// THE PREMISES, each measured on .NET 10 — a wrong assumption here would let
		// this test pass while proving nothing about which guard fired.
		string nulName = "app.json.a\0b.bak";
		string nulPath = @"X:\backups\" + nulName;

		_ = Path.GetFileName(nulPath).Should().Be(
			nulName,
			"the NUL survives Path.GetFileName, so the name really does clear the prefix filter");
		_ = nulName.IndexOfAny(['\\', '/', ':']).Should().Be(
			-1,
			"BackupName's path qualifiers do not include NUL, so this name really does clear TryCreateName too");

		Action realStat = () => File.GetLastWriteTimeUtc(nulPath);
		Action realLength = () => { _ = new FileInfo(nulPath).Length; };
		Action realOpen = () => File.OpenRead(nulPath).Dispose();

		_ = realStat.Should().Throw<ArgumentException>("the stat must really throw, or the catch under test is never reached");
		_ = realLength.Should().Throw<ArgumentException>();
		_ = realOpen.Should().Throw<ArgumentException>();

		// AND IT IS NEITHER OF THE TYPES ALREADY CAUGHT — the whole reason it escaped.
		// Stated as a type relationship rather than by observing a throw, so it cannot
		// pass for an incidental reason.
		_ = typeof(IOException).IsAssignableFrom(typeof(ArgumentException)).Should().BeFalse(
			"ArgumentException is not an IOException, so the existing catch filters could not have covered it");
		_ = typeof(UnauthorizedAccessException).IsAssignableFrom(typeof(ArgumentException)).Should().BeFalse();
		_ = typeof(ArgumentException).IsAssignableFrom(typeof(ArgumentOutOfRangeException)).Should().BeTrue(
			"which is why a negative reported length is covered too — though it is guarded explicitly rather than incidentally");

		RealCallReadFileSystem fake = new([nulPath]);
		ConfigFileObserver observer = new(fake);

		Action list = () => observer.ListBackupCandidates(@"X:\backups", "app.json");
		_ = list.Should().NotThrow("a path the seam cannot stat is an observation, never an exception");

		BackupCandidate candidate = observer.ListBackupCandidates(@"X:\backups", "app.json")
			.Should().ContainSingle().Which;

		_ = candidate.StampSource.Should().Be(
			BackupStampSource.StampUnavailable,
			"the stat FAILED — nothing was learned about the file, and it certainly was not observed to be gone");
		_ = candidate.Vanished.Should().BeFalse();

		BackupCandidateObservation observed = observer.Observe<AppDocument>(candidate);

		_ = observed.Access.Should().Be(
			ConfigFileAccess.OpenFailed,
			"the bytes were not obtained, so the file is not known to be damaged");
		_ = observed.Observation.Should().BeNull("nothing was read, so there is no verdict to carry");
	}

	// A SEAM REPORTING A NEGATIVE LENGTH. ArgumentOutOfRangeException derives from
	// ArgumentException, so the catch above would swallow the throw from
	// new MemoryStream((int)length) — but that would make the behaviour INCIDENTAL. The
	// negative is guarded explicitly, so the intent is legible, and the stream is never
	// even opened.
	[Fact]
	public void Observe_SeamReportsANegativeLength_ReportsOpenFailedWithoutReadingAnything()
	{
		OversizedReadFileSystem fake = new(reportedLength: -1, streamedBytes: 64);

		ConfigFileObservation observed = new ConfigFileObserver(fake).Observe<AppDocument>(@"X:\data\app.json");

		_ = observed.Access.Should().Be(
			ConfigFileAccess.OpenFailed,
			"a length that cannot be true means nothing was learned about the file");
		_ = observed.Envelope.Should().BeNull();
		_ = observed.Schema.Should().BeNull();
		_ = fake.BytesHandedOut.Should().Be(0, "the refusal is made from metadata alone, before any byte is pulled");
	}

	// ------------------------------------------------- a short read is not an EOF

	// A ZERO MID-STREAM MUST NOT BE MISTAKEN FOR EOF. Stream.Read may legally return
	// fewer bytes than asked for, and a 0 is indistinguishable from a real EOF at the
	// call site — so a partial document used to be handed on as complete, reported
	// Opened.
	//
	// WHY THAT IS THE WORST SHAPE OF BUG HERE: a truncated JSON document does not look
	// truncated, it looks CORRUPT. It parses as EnvelopeUnparseable, which licenses a
	// quarantine-then-overwrite of a file that was ACTUALLY INTACT.
	[Fact]
	public void Observe_StreamReturnsZeroMidWay_ReportsOpenFailedRatherThanATruncatedSnapshot()
	{
		// A 200 KiB file that stops yielding after 1 KiB — a genuinely intact document
		// as far as its length is concerned, and a truncated one as far as its bytes are.
		ShortReadFileSystem fake = new(reportedLength: 200 * 1024, servedBytes: 1024);

		ConfigFileObservation observed = new ConfigFileObserver(fake).Observe<AppDocument>(@"X:\data\app.json");

		_ = observed.Access.Should().Be(
			ConfigFileAccess.OpenFailed,
			"fewer bytes arrived than the file was reported to hold, so the read failed");
		_ = observed.Envelope.Should().BeNull(
			"reporting a corrupt envelope on a partial read would license destroying a file that is intact");
		_ = observed.Body.Should().BeNull();
		_ = observed.Schema.Should().BeNull();
	}

	// THE SAME CLASS, from the other direction: a seam that OVER-REPORTS the length.
	// Claims 8 MiB, yields 10 bytes — in budget, so the ceiling never fires, and the
	// 10-byte snapshot used to be reported Opened.
	[Fact]
	public void Observe_SeamOverReportsTheLength_ReportsOpenFailedRatherThanAShortSnapshot()
	{
		ShortReadFileSystem fake = new(reportedLength: 8L * 1024 * 1024, servedBytes: 10);

		ConfigFileObservation observed = new ConfigFileObserver(fake).Observe<AppDocument>(@"X:\data\app.json");

		_ = observed.Access.Should().Be(ConfigFileAccess.OpenFailed);
		_ = observed.Envelope.Should().BeNull();
	}

	// THE OTHER HALF OF THE SHORT-READ RULE, and the one that keeps the fix from being
	// a regression: a stream that serves the FULL length in many small chunks is a
	// perfectly ordinary stream, and must still be accepted. Comparing totals rather
	// than trusting a single Read is exactly what makes this safe.
	[Fact]
	public void Observe_StreamServesTheFullLengthInTinyChunks_IsStillAccepted()
	{
		string document = /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}";
		DribbleReadFileSystem fake = new(document, maxBytesPerRead: 7);

		ConfigFileObservation observed = new ConfigFileObserver(fake).Observe<AppDocument>(@"X:\data\app.json");

		_ = fake.ReadCallCount.Should().BeGreaterThan(
			1,
			"the premise: this stream really did need several reads, or the test proves nothing about chunking");
		_ = observed.Access.Should().Be(
			ConfigFileAccess.Opened,
			"a short read that still totals the reported length is an ordinary stream, not a truncated file");
		_ = observed.Envelope.Should().Be(EnvelopeParse.Parsed);
		_ = observed.Body.Should().Be(DocumentBodyParse.Parsed);
	}

	// ---------------------------------------------------- a leading UTF-8 BOM

	// AN EDITOR'S "Save As" MUST NOT COST THE USER THEIR CONFIG. The writer emits no
	// BOM, so the mirror rule is untouched — but a BOM-prefixed document is intact and
	// unambiguous, and without the skip it was EnvelopeUnparseable ('0xEF' is an invalid
	// start of a value) -> quarantine-then-defaults.
	//
	// Driven off REAL writer output, then prefixed, so the document under test is
	// byte-for-byte what the app itself produces plus the three bytes an editor adds.
	[Fact]
	public void Observe_DocumentPrefixedWithAUtf8Bom_ReadsAsLoaded()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			AtomicJsonFileStore store = new(backupsDir);
			string path = Path.Combine(dataDir, "browsers.json");
			store.Write(path, new BrowsersDocument([SomeBrowser("edge-stable")]));

			byte[] written = File.ReadAllBytes(path);
			byte[] preamble = System.Text.Encoding.UTF8.GetPreamble();

			// The premises: the writer emits NO BOM, and the preamble really is the three
			// bytes this test prepends.
			_ = preamble.Should().Equal([0xEF, 0xBB, 0xBF]);
			_ = written.Take(3).Should().NotEqual(
				preamble,
				"the writer emits no BOM, so this fixture must be the one adding it");

			File.WriteAllBytes(path, [.. preamble, .. written]);

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			_ = observed.Access.Should().Be(ConfigFileAccess.Opened);
			_ = observed.Envelope.Should().Be(
				EnvelopeParse.Parsed,
				"the preamble is skipped ONCE on the single snapshot, so the envelope reader sees real JSON");
			_ = observed.Body.Should().Be(
				DocumentBodyParse.Parsed,
				"the binder must see the SAME skipped bytes as the envelope reader, or the observation could tear");
			_ = observed.Schema.Should().Be(SchemaCompatibility.CompletelyCompatible);

			_ = ConfigLoadPlanner.Plan(observed).MutatesDisk.Should().BeFalse(
				"an intact document with a BOM needs no repair at all");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// UTF-16 STAYS REFUSED, and that is a decision rather than an oversight. The writer
	// is UTF-8 only, so a UTF-16 file is not something this app can produce, and
	// guessing encodings from leading bytes produces confidently wrong reads — which is
	// exactly what licenses a destructive repair on this path.
	[Fact]
	public void Observe_Utf16Document_IsStillRefusedRatherThanEncodingDetected()
	{
		string dir = NewTempDirectory();
		try
		{
			string path = Path.Combine(dir, "app.json");
			File.WriteAllBytes(
				path,
				[
					.. System.Text.Encoding.Unicode.GetPreamble(),
					.. System.Text.Encoding.Unicode.GetBytes(Envelope(1, 0)),
				]);

			ConfigFileObservation observed = NewObserver().Observe<AppDocument>(path);

			_ = observed.Access.Should().Be(ConfigFileAccess.Opened, "the bytes were obtained; they are simply not UTF-8");
			_ = observed.Envelope.Should().Be(
				EnvelopeParse.Unparseable,
				"no encoding detection is performed, deliberately — a confidently wrong read is worse than a refusal");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// -------------------------------- the body duplicate flag, finally pinned

	// THE ONE SHAPE BodyOptions.AllowDuplicateProperties = false UNIQUELY CATCHES, which
	// no test covered — so the flag read as a safety guarantee while nothing held it in
	// place.
	//
	// It is NOT about the envelope properties: those never reach the binder, because the
	// envelope reader's counting scan refuses them first. Its contribution is a
	// duplicate on a BOUND BODY MEMBER. MEASURED:
	//
	//   {"schemaVersion":1,"schemaRevision":0,"browsers":[],"BROWSERS":[{...}]}
	//     envelope         -> Parsed v=1 r=0   (no envelope mechanism sees this at all)
	//     body strict      -> Duplicate property 'browsers' encountered
	//     body dup-allowed -> binds, browsers=1 (the LAST spelling silently wins)
	//
	// The two spellings collide because JsonSerializer detects duplicates on the BOUND
	// MEMBER: with PropertyNameCaseInsensitive = true both bind to the same member, so
	// they are one member given two values. The case-insensitivity is what makes this
	// catch mixed case, not something it works around.
	[Fact]
	public void Observe_MixedCaseDuplicateBodyMember_IsRefusedByTheBodyOptionsFlagAlone()
	{
		string dir = NewTempDirectory();
		try
		{
			string content = "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":[],\"BROWSERS\":["
				+ OneBrowserObject
				+ "]}";
			string path = WriteFile(dir, "browsers.json", content);

			ConfigFileObservation observed = NewObserver().Observe<BrowsersDocument>(path);

			// THE PREMISE THAT MAKES THIS TEST MEAN ANYTHING: the envelope is entirely
			// happy, so the refusal below can only be the body flag's doing.
			_ = observed.Envelope.Should().Be(
				EnvelopeParse.Parsed,
				"both envelope properties are single and unambiguous — no envelope mechanism refuses this document");
			_ = observed.Schema.Should().Be(SchemaCompatibility.CompletelyCompatible);

			_ = observed.Body.Should().Be(
				DocumentBodyParse.Unparseable,
				"one bound member given two values cannot say which it means, and letting the last spelling win would silently pick a list the user never wrote");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// ------------------------------------------------- the ceiling boundary

	// EXACTLY AT THE CEILING IS ACCEPTED. The previous ceiling test asserted only
	// "BytesHandedOut < ceiling + 4096", which is satisfied by pulling ceiling + 1 — so
	// the boundary itself was unpinned in both directions.
	[Fact]
	public void Observe_FileExactlyAtTheCeiling_IsAccepted()
	{
		const long ceiling = 8L * 1024 * 1024;
		OversizedReadFileSystem fake = new(reportedLength: ceiling, streamedBytes: ceiling);

		ConfigFileObservation observed = new ConfigFileObserver(fake).Observe<AppDocument>(@"X:\data\app.json");

		_ = observed.Access.Should().Be(
			ConfigFileAccess.Opened,
			"the ceiling is inclusive — a file exactly at it was read in full and must not be refused");
		_ = fake.BytesHandedOut.Should().Be(ceiling, "every byte of an in-budget file is read");
		_ = observed.Envelope.Should().Be(
			EnvelopeParse.Unparseable,
			"the fixture's bytes are zeroes, so the CONTENT is garbage — this test is about the ceiling, not the parse");
	}

	// EXACTLY ONE PAST THE CEILING IS REFUSED, by both routes: the metadata pre-check,
	// and the bounded copy for a file that only reveals its size while being read.
	[Fact]
	public void Observe_FileExactlyOnePastTheCeiling_ReportsOpenFailed()
	{
		const long ceiling = 8L * 1024 * 1024;

		// Route 1: the seam reports ceiling + 1, so it is refused from metadata alone.
		OversizedReadFileSystem reported = new(reportedLength: ceiling + 1, streamedBytes: ceiling + 1);

		ConfigFileObservation fromMetadata =
			new ConfigFileObserver(reported).Observe<AppDocument>(@"X:\data\app.json");

		_ = fromMetadata.Access.Should().Be(ConfigFileAccess.OpenFailed);
		_ = reported.BytesHandedOut.Should().Be(0, "one byte past the ceiling is refused before anything is read");

		// Route 2: an in-budget length that streams ceiling + 1 — the TOCTOU shape, where
		// only the bytes actually arriving can decide it.
		OversizedReadFileSystem grew = new(reportedLength: 1024, streamedBytes: ceiling + 1);

		ConfigFileObservation fromCopy = new ConfigFileObserver(grew).Observe<AppDocument>(@"X:\data\app.json");

		_ = fromCopy.Access.Should().Be(
			ConfigFileAccess.OpenFailed,
			"the limit is enforced against the bytes arriving, not against a size that was true a moment ago");
		_ = grew.BytesHandedOut.Should().Be(
			ceiling + 1,
			"exactly one byte past the ceiling is all the evidence needed, so no more than that is ever pulled");
	}

	// ------------------------------------------------------------------ helpers

	// One fully-populated browser object, as a hand-written literal so no serializer
	// option can leak into the fixture.
	private const string OneBrowserObject =
		"{\"browserId\":\"edge-stable\",\"displayName\":\"Edge\",\"vendor\":\"Microsoft\","
		+ "\"channel\":\"stable\",\"exePath\":\"C:\\\\Program Files\\\\Edge\\\\msedge.exe\","
		+ "\"detectionSource\":\"known-path\",\"lastVerifiedAt\":null}";

	// A browsers.json holding one browser object with `omittedKey` left out entirely.
	// Passing null omits nothing, which is the "explicit lastVerifiedAt:null" fixture.
	private static string BrowsersJsonWithout(string? omittedKey)
	{
		string fields = string.Join(
			",",
			OneBrowserObject.TrimStart('{').TrimEnd('}').Split(',')
				.Where(field => omittedKey is null
					|| !field.StartsWith("\"" + omittedKey + "\"", StringComparison.Ordinal)));

		// The premise, guarded rather than assumed: a typo'd key name would silently omit
		// NOTHING and leave the test asserting against a complete document.
		return omittedKey is not null && fields.Contains("\"" + omittedKey + "\"", StringComparison.Ordinal)
			? throw new InvalidOperationException("The fixture failed to omit " + omittedKey + ".")
			: "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":[{" + fields + "}]}";
	}

	// A browsers.json whose list holds one element per comma-separated token: "null" emits a
	// literal JSON null, "valid" emits OneBrowserObject. Built from the SAME literal the
	// binding tests use, so a row cannot drift into asserting against a shape the binder
	// never accepted in the first place.
	private static string BrowsersJsonWithElements(string pattern)
	{
		return "{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":["
		+ string.Join(
			",",
			pattern.Split(',').Select(static token => token switch
			{
				"null" => "null",
				"valid" => OneBrowserObject,

				// A typo in an InlineData row would otherwise emit that token as raw JSON,
				// which parses as nothing and would make the row pass for the wrong reason.
				_ => throw new InvalidOperationException("Unknown element token '" + token + "'."),
			}))
		+ "]}";
	}

	private static BackupCandidate SomeCandidate()
	{
		return new ConfigFileObserver(NewFakeWithOneBackup()).ListBackupCandidates(@"X:\backups", "app.json")[0];
	}

	private static FakeReadFileSystem NewFakeWithOneBackup()
	{
		FakeReadFileSystem fake = new();
		fake.Files[$"app.json.20260830120000001.{GuidA}.bak"] = Envelope(1, 0);
		return fake;
	}

	private static BrowserDefinition SomeBrowser(string browserId)
	{
		return new(browserId, "Display Name", "Vendor", "stable", @"C:\Program Files\Browser\browser.exe", "known-path", null);
	}

	// A browsers.json with `entries` fully-populated browser objects, built by hand so
	// the size is predictable and no serializer options leak into the fixture.
	private static string BrowsersJson(int entries)
	{
		System.Text.StringBuilder json = new();
		_ = json.Append("{\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":[");

		for (int index = 0; index < entries; index++)
		{
			if (index > 0)
			{
				_ = json.Append(',');
			}

			_ = json.Append(FormattableString.Invariant(
				$"{{\"browserId\":\"browser-{index}\",\"displayName\":\"Browser Number {index}\",\"vendor\":\"Vendor {index}\",\"channel\":\"stable\",\"exePath\":\"C:\\\\Program Files\\\\Browser {index}\\\\browser.exe\",\"detectionSource\":\"known-path\",\"lastVerifiedAt\":null}}"));
		}

		return json.Append("]}").ToString();
	}

	private static void AssertEnvelopeUnparseable(string content)
	{
		string dir = NewTempDirectory();
		try
		{
			string path = WriteFile(dir, "app.json", content);

			ConfigFileObservation observed = NewObserver().Observe<AppDocument>(path);

			_ = observed.Access.Should().Be(ConfigFileAccess.Opened, "the bytes WERE obtained; they just do not parse");
			_ = observed.Envelope.Should().Be(EnvelopeParse.Unparseable);
			_ = observed.Body.Should().Be(DocumentBodyParse.Unparseable);
			_ = observed.Schema.Should().BeNull(
				"the unparseable-envelope factory takes no schema — a verdict that could not be computed cannot be reported");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// Candidate names joined in list order. Ordering is asserted as ONE string so a
	// test pins the exact full sequence, not merely a relative order — a containment
	// assertion would pass on an order that had silently gained an extra entry.
	private static string Names(IEnumerable<BackupCandidate> candidates)
	{
		return string.Join("|", candidates.Select(candidate => candidate.Name.Value));
	}

	private static string Envelope(int version, int revision)
	{
		return FormattableString.Invariant($"{{\"schemaVersion\":{version},\"schemaRevision\":{revision}}}");
	}

	private static string WriteFile(string directory, string fileName, string content)
	{
		string path = Path.Combine(directory, fileName);
		File.WriteAllText(path, content);
		return path;
	}

	private static string WriteBackup(string directory, string fileName, string content)
	{
		return WriteFile(directory, fileName, content);
	}

	// Relative path + size + last-write ticks for every entry, sorted ordinally so
	// enumeration order cannot influence the comparison.
	private static string SnapshotTree(string root)
	{
		return string.Join(
			"\n",
			Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
				.Select(entry => Directory.Exists(entry)
					? "D " + Path.GetRelativePath(root, entry)
					: FormattableString.Invariant(
						$"F {Path.GetRelativePath(root, entry)} {new FileInfo(entry).Length} {new FileInfo(entry).LastWriteTimeUtc.Ticks}"))
				.OrderBy(line => line, StringComparer.Ordinal));
	}

	private static string NewTempDirectory()
	{
		string path = Path.Combine(Path.GetTempPath(), "BroChoos.ConfigFileObserver", Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(path);
		return path;
	}

	// A data dir for the documents plus a pre-provisioned backups dir, mirroring
	// AtomicJsonFileStoreTests' layout — the store never creates the backups dir
	// itself. Needed by the mirror test, which drives a REAL write.
	private static (string DataDir, string BackupsDir, string Root) NewStorageLayout()
	{
		string root = NewTempDirectory();
		string dataDir = Path.Combine(root, "data");
		string backupsDir = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(dataDir);
		_ = Directory.CreateDirectory(backupsDir);
		return (dataDir, backupsDir, root);
	}

	// A read seam whose contents are entirely in memory, for the ONE state a real
	// filesystem cannot be made to produce on demand: a file that is enumerated and
	// then gone before it is stat'ed.
	//
	// A null value means "listed, but not there" — so GetLastWriteTimeUtc returns the
	// missing-file sentinel exactly as the framework does, and OpenRead throws
	// FileNotFoundException exactly as the framework does. Nothing here can write,
	// because the interface has no write member to implement.
	private sealed class FakeReadFileSystem : IStorageReadFileSystem
	{
		internal Dictionary<string, string?> Files { get; } = new(StringComparer.Ordinal);

		// Entries whose OpenRead throws DirectoryNotFoundException instead of yielding
		// bytes — the state a real filesystem cannot be asked for on demand once
		// FileExists has already said yes. Exists to exercise the SECOND half of the
		// observer's first catch clause, which nothing else reaches.
		internal HashSet<string> OpenThrowsDirectoryNotFound { get; } = new(StringComparer.Ordinal);

		// Entries whose stat fails rather than reporting a timestamp (denied ACL).
		// Distinct from a null entry, which is the framework's missing-file sentinel.
		internal HashSet<string> StatThrowsUnauthorized { get; } = new(StringComparer.Ordinal);

		// Entries whose OpenRead is denied by an ACL. Distinct from a lock only in the
		// exception type, and both must reach OpenFailed.
		internal HashSet<string> OpenThrowsUnauthorized { get; } = new(StringComparer.Ordinal);

		public bool FileExists(string path)
		{
			return Files.TryGetValue(Path.GetFileName(path), out string? content)
			&& content is not null;
		}

		public bool DirectoryExists(string path)
		{
			return true;
		}

		public Stream OpenRead(string path)
		{
			string name = Path.GetFileName(path);
			return OpenThrowsDirectoryNotFound.Contains(name)
				? throw new DirectoryNotFoundException("Directory not found.")
				: OpenThrowsUnauthorized.Contains(name)
				? throw new UnauthorizedAccessException("Access denied.")
				: (Stream)(Files.TryGetValue(name, out string? content) && content is not null
				? new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))
				: throw new FileNotFoundException("Not found."));
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return Files.Keys.Select(name => Path.Combine(directory, name));
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			string name = Path.GetFileName(path);
			return StatThrowsUnauthorized.Contains(name)
				? throw new UnauthorizedAccessException("Access denied.")
				: Files.TryGetValue(name, out string? content) && content is not null
				? new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc)
				: new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		}

		// The UTF-8 byte count of a present entry, so it agrees exactly with what
		// OpenRead hands back. Throws FileNotFoundException for an absent entry,
		// mirroring OpenRead rather than inventing a sentinel length — the seam
		// declares that vocabulary, and a sentinel would be a second value a caller
		// could mistake for a real size.
		public long GetLength(string path)
		{
			string name = Path.GetFileName(path);
			return Files.TryGetValue(name, out string? content) && content is not null
				? System.Text.Encoding.UTF8.GetByteCount(content)
				: throw new FileNotFoundException("Not found.");
		}
	}

	// Which seam member fails, for the seam-escape facts above. DELIBERATELY NOT A
	// COUNT: the figure here read "five" while six facts already used this fake, and a
	// number in prose goes stale silently every time a fact is added — which has bitten
	// this project repeatedly. The compiler tracks the users of this enum; prose cannot.
	private enum ThrowingSeamMember
	{
		GetLength,
		OpenRead,
		StreamRead,
	}

	// A seam that fails at ONE chosen member with ONE chosen exception, and otherwise
	// behaves like a healthy filesystem holding a VALID document.
	//
	// THE VALID PAYLOAD IS WHAT LETS THESE TESTS FAIL. If the injected throw ever stopped
	// happening, the observer would read these bytes, parse the envelope and report a
	// PARSED observation — not OpenFailed — so the assertions would fail loudly instead of
	// passing vacuously. A seam serving garbage would have made OpenFailed reachable for a
	// second reason and the tests would no longer isolate the escape.
	//
	// Nothing here can write: IStorageReadFileSystem declares six read-only members and no
	// write member, so a fake cannot be given one either.
	private sealed class ThrowingSeamReadFileSystem : IStorageReadFileSystem
	{
		private readonly ThrowingSeamMember _member;

		private readonly Func<Exception> _failure;

		internal ThrowingSeamReadFileSystem(ThrowingSeamMember member, Func<Exception> failure)
		{
			_member = member;
			_failure = failure;
		}

		private static byte[] Payload => System.Text.Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0}");

		public bool FileExists(string path)
		{
			return true;
		}

		public bool DirectoryExists(string path)
		{
			return true;
		}

		// A statement body with an explicit Stream return type, not a nested conditional:
		// ThrowOnReadStream and MemoryStream have no best common type between them, so a
		// ternary over the two does not compile.
		public Stream OpenRead(string path)
		{
			return _member == ThrowingSeamMember.OpenRead
				? throw _failure()
				: _member == ThrowingSeamMember.StreamRead
				? new ThrowOnReadStream(_failure, Payload.Length)
				: new MemoryStream(Payload);
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return [];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
		}

		public long GetLength(string path)
		{
			return _member == ThrowingSeamMember.GetLength
			? throw _failure()
			: Payload.Length;
		}

		// A stream that opens cleanly, reports a plausible length, and then fails on the
		// FIRST Read — the mid-read failure a concurrent teardown could produce.
		private sealed class ThrowOnReadStream : Stream
		{
			private readonly Func<Exception> _failure;

			private readonly long _length;

			internal ThrowOnReadStream(Func<Exception> failure, long length)
			{
				_failure = failure;
				_length = length;
			}

			public override bool CanRead => true;

			public override bool CanSeek => false;

			public override bool CanWrite => false;

			public override long Length => _length;

			public override long Position
			{
				get => 0;
				set => throw new NotSupportedException();
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				throw _failure();
			}

			public override void Flush()
			{
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				throw new NotSupportedException();
			}

			public override void SetLength(long value)
			{
				throw new NotSupportedException();
			}

			public override void Write(byte[] buffer, int offset, int count)
			{
				throw new NotSupportedException();
			}
		}
	}

	// A seam that can report one length and then stream a DIFFERENT number of bytes —
	// the TOCTOU shape a real filesystem would need a concurrent writer to produce.
	// Synthetic bytes rather than a real multi-megabyte file, so the ceiling can be
	// exercised without writing 8 MiB to a developer's disk.
	private sealed class OversizedReadFileSystem : IStorageReadFileSystem
	{
		private readonly long _reportedLength;

		private readonly long _streamedBytes;

		internal OversizedReadFileSystem(long reportedLength, long streamedBytes)
		{
			_reportedLength = reportedLength;
			_streamedBytes = streamedBytes;
		}

		// How many bytes the observer actually pulled. The point of the bounded copy is
		// that this stays near the ceiling instead of reaching the file's full size.
		internal long BytesHandedOut { get; private set; }

		public bool FileExists(string path)
		{
			return true;
		}

		public bool DirectoryExists(string path)
		{
			return false;
		}

		public Stream OpenRead(string path)
		{
			return new CountingStream(this, _streamedBytes);
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return [];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
		}

		public long GetLength(string path)
		{
			return _reportedLength;
		}

		// A forward-only stream of a fixed length that allocates nothing per byte and
		// records how much was consumed.
		private sealed class CountingStream : Stream
		{
			private readonly OversizedReadFileSystem _owner;

			private readonly long _total;

			private long _position;

			internal CountingStream(OversizedReadFileSystem owner, long total)
			{
				_owner = owner;
				_total = total;
			}

			public override bool CanRead => true;

			public override bool CanSeek => false;

			public override bool CanWrite => false;

			public override long Length => throw new NotSupportedException();

			public override long Position
			{
				get => throw new NotSupportedException();
				set => throw new NotSupportedException();
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				int served = (int)Math.Min(count, _total - _position);
				if (served <= 0)
				{
					return 0;
				}

				// Left as zeroes: an oversized file is refused before its content is
				// ever parsed, so the bytes themselves carry no meaning here.
				Array.Clear(buffer, offset, served);
				_position += served;
				_owner.BytesHandedOut += served;
				return served;
			}

			public override void Flush()
			{
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				throw new NotSupportedException();
			}

			public override void SetLength(long value)
			{
				throw new NotSupportedException();
			}

			public override void Write(byte[] buffer, int offset, int count)
			{
				throw new NotSupportedException();
			}
		}
	}

	// A seam that hands the observer the entry names it is given and then makes the REAL
	// framework calls on them. Nothing here stages an exception: the ArgumentException
	// under test is the genuine article, thrown by File.GetLastWriteTimeUtc,
	// new FileInfo(path).Length and File.OpenRead for a path they refuse to accept.
	//
	// That matters for this particular fix. A fake that THREW ArgumentException itself
	// would pin the catch clause against the test's own opinion of what the framework
	// does — and this change set already contains one premise that was measured wrong
	// that way (on .NET Framework, via PowerShell 5.1, where Path.GetFileName behaves
	// differently than on .NET 10).
	private sealed class RealCallReadFileSystem : IStorageReadFileSystem
	{
		private readonly string[] _entries;

		internal RealCallReadFileSystem(string[] entries)
		{
			_entries = entries;
		}

		public bool FileExists(string path)
		{
			return File.Exists(path);
		}

		// True, or the listing returns empty at its own directory guard and never
		// reaches the names.
		public bool DirectoryExists(string path)
		{
			return true;
		}

		public Stream OpenRead(string path)
		{
			return File.OpenRead(path);
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return _entries;
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return File.GetLastWriteTimeUtc(path);
		}

		public long GetLength(string path)
		{
			return new FileInfo(path).Length;
		}
	}

	// A seam that reports one length and then serves FEWER bytes than that, returning 0
	// part-way through as though it had hit EOF. The shape a genuinely short read has,
	// and the one a real filesystem will not produce on demand.
	private sealed class ShortReadFileSystem : IStorageReadFileSystem
	{
		private readonly long _reportedLength;

		private readonly long _servedBytes;

		internal ShortReadFileSystem(long reportedLength, long servedBytes)
		{
			_reportedLength = reportedLength;
			_servedBytes = servedBytes;
		}

		public bool FileExists(string path)
		{
			return true;
		}

		public bool DirectoryExists(string path)
		{
			return false;
		}

		public Stream OpenRead(string path)
		{
			return new TruncatedStream(_servedBytes);
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return [];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
		}

		public long GetLength(string path)
		{
			return _reportedLength;
		}

		// Serves a fixed number of bytes and then reports 0 forever — indistinguishable
		// from a real EOF at the call site, which is exactly why the total has to be
		// compared against the reported length.
		private sealed class TruncatedStream : Stream
		{
			private readonly long _total;

			private long _position;

			internal TruncatedStream(long total)
			{
				_total = total;
			}

			public override bool CanRead => true;

			public override bool CanSeek => false;

			public override bool CanWrite => false;

			public override long Length => throw new NotSupportedException();

			public override long Position
			{
				get => throw new NotSupportedException();
				set => throw new NotSupportedException();
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				int served = (int)Math.Min(count, _total - _position);
				if (served <= 0)
				{
					return 0;
				}

				// A plausible JSON opening brace, then zeroes: a truncated document that
				// STARTS like a real one, so nothing downstream can shortcut on the bytes.
				Array.Clear(buffer, offset, served);
				if (_position == 0)
				{
					buffer[offset] = (byte)'{';
				}

				_position += served;
				return served;
			}

			public override void Flush()
			{
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				throw new NotSupportedException();
			}

			public override void SetLength(long value)
			{
				throw new NotSupportedException();
			}

			public override void Write(byte[] buffer, int offset, int count)
			{
				throw new NotSupportedException();
			}
		}
	}

	// A seam that serves the FULL document, but only a few bytes per Read — an entirely
	// ordinary stream, and the case the short-read fix must not turn into a refusal.
	private sealed class DribbleReadFileSystem : IStorageReadFileSystem, IDisposable
	{
		private readonly byte[] _content;

		private readonly int _maxBytesPerRead;

		private DribbleStream? _stream;

		internal DribbleReadFileSystem(string content, int maxBytesPerRead)
		{
			_content = System.Text.Encoding.UTF8.GetBytes(content);
			_maxBytesPerRead = maxBytesPerRead;
		}

		// How many times Read was called. Asserted as a PREMISE by the test: if the
		// document arrived in one call, the test would prove nothing about chunking.
		internal int ReadCallCount => _stream?.ReadCallCount ?? 0;

		public bool FileExists(string path)
		{
			return true;
		}

		public bool DirectoryExists(string path)
		{
			return false;
		}

		public Stream OpenRead(string path)
		{
			_stream = new DribbleStream(_content, _maxBytesPerRead);
			return _stream;
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return [];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
		}

		public long GetLength(string path)
		{
			return _content.Length;
		}

		public void Dispose()
		{
			_stream?.Dispose();
		}

		private sealed class DribbleStream : Stream
		{
			private readonly byte[] _content;

			private readonly int _maxBytesPerRead;

			private int _position;

			internal DribbleStream(byte[] content, int maxBytesPerRead)
			{
				_content = content;
				_maxBytesPerRead = maxBytesPerRead;
			}

			internal int ReadCallCount { get; private set; }

			public override bool CanRead => true;

			public override bool CanSeek => false;

			public override bool CanWrite => false;

			public override long Length => throw new NotSupportedException();

			public override long Position
			{
				get => throw new NotSupportedException();
				set => throw new NotSupportedException();
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				ReadCallCount++;

				int served = Math.Min(Math.Min(count, _maxBytesPerRead), _content.Length - _position);
				if (served <= 0)
				{
					return 0;
				}

				Array.Copy(_content, _position, buffer, offset, served);
				_position += served;
				return served;
			}

			public override void Flush()
			{
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				throw new NotSupportedException();
			}

			public override void SetLength(long value)
			{
				throw new NotSupportedException();
			}

			public override void Write(byte[] buffer, int offset, int count)
			{
				throw new NotSupportedException();
			}
		}
	}

	// A seam whose DirectoryExists is BROKEN — it throws instead of answering. The interface
	// DECLARES that this member never throws for an absent directory, and ConfigFileObserver
	// leaves three seam calls unguarded resting on that declaration, so an implementation
	// that throws here is a defect whose exception is MEANT to escape.
	//
	// IOException rather than something exotic, because it is the type every guarded helper in
	// the observer CONVERTS — so these facts fail the moment a `try` appears around any of the
	// three unguarded sites.
	private sealed class DirectoryExistsThrowingReadFileSystem : IStorageReadFileSystem
	{
		// FALSE, so Observe takes its not-found branch and REACHES the DirectoryExists under
		// test. With true the read would proceed to the bytes and the unguarded call would
		// never be made, leaving the fact vacuously green.
		public bool FileExists(string path)
		{
			return false;
		}

		public bool DirectoryExists(string path)
		{
			throw new IOException("Seam cannot answer.");
		}

		public Stream OpenRead(string path)
		{
			throw new FileNotFoundException("Not found.");
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return [];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
		}

		public long GetLength(string path)
		{
			throw new FileNotFoundException("Not found.");
		}
	}

	// A seam whose FileExists is BROKEN — the sibling of the fake above, for the ONE unguarded
	// site that fake cannot reach. Its FileExists is `=> false` and never throws, and an audit
	// of every FileExists implementation in this file found none that throws, so the site at
	// the top of Observe had no fact resting on it.
	//
	// DirectoryExists ANSWERS FALSE rather than throwing, and that is what makes the fact
	// attributable: the escape can only have come from FileExists. It also states the cost
	// executably — a swallowed FileExists falls through to this false and yields Absent, which
	// routes to defaults written over an existing file with NO quarantine.
	private sealed class FileExistsThrowingReadFileSystem : IStorageReadFileSystem
	{
		public bool FileExists(string path)
		{
			throw new IOException("Seam cannot answer.");
		}

		public bool DirectoryExists(string path)
		{
			return false;
		}

		public Stream OpenRead(string path)
		{
			throw new FileNotFoundException("Not found.");
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return [];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
		}

		public long GetLength(string path)
		{
			throw new FileNotFoundException("Not found.");
		}
	}

	// A seam that RECORDS every member it is asked for and otherwise behaves like a healthy
	// filesystem holding a VALID document. It exists so a test can assert that NO READ
	// OCCURRED, which is the property the abstract-TDocument guard actually claims — a
	// throw-only assertion would be satisfied by a guard sitting after the read.
	//
	// THE VALID PAYLOAD IS WHAT LETS THOSE FACTS FAIL, exactly as for
	// ThrowingSeamReadFileSystem: with the guard removed the observer reads these bytes and
	// RETURNS an observation (BodyUnparseable, since an abstract type cannot bind), so the
	// throw assertion goes red rather than passing vacuously. Every member answers for ANY
	// path, so nothing here depends on a path shape.
	//
	// Nothing here can write: the interface declares six read-only members and no write
	// member, so a recording fake cannot be given one either.
	private sealed class RecordingReadFileSystem : IStorageReadFileSystem
	{
		// The member names in call order. A LIST rather than a flag, so a failure message
		// names which seam member was touched instead of merely reporting that one was.
		internal List<string> Calls { get; } = [];

		public bool FileExists(string path)
		{
			Calls.Add(nameof(FileExists));
			return true;
		}

		public bool DirectoryExists(string path)
		{
			Calls.Add(nameof(DirectoryExists));
			return true;
		}

		public Stream OpenRead(string path)
		{
			Calls.Add(nameof(OpenRead));
			return new MemoryStream(DocumentBytes);
		}

		// ONE candidate whose name really clears the listing's prefix filter, so with the
		// guard removed ObserveNthNewestBackup finds an index-0 candidate and genuinely
		// reads it. An empty listing would return null instead, which is also not a throw —
		// but then the "no read occurred" assertion would be true for the wrong reason.
		public IEnumerable<string> EnumerateFiles(string directory)
		{
			Calls.Add(nameof(EnumerateFiles));
			return [Path.Combine(directory, $"app.json.20260830120000001.{GuidA}.bak")];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			Calls.Add(nameof(GetLastWriteTimeUtc));
			return new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
		}

		public long GetLength(string path)
		{
			Calls.Add(nameof(GetLength));
			return DocumentBytes.Length;
		}

		// Agrees byte-for-byte with what OpenRead hands back, so the short-read guard
		// (total < length) cannot turn a valid document into OpenFailed and mask what these
		// facts measure.
		private static byte[] DocumentBytes => System.Text.Encoding.UTF8.GetBytes(Envelope(1, 0));
	}

	// A seam that yields deliberately hostile ENTRY NAMES, to prove the listing reports
	// "not a candidate" instead of throwing whatever the enumeration hands it.
	private sealed class HostileNameReadFileSystem : IStorageReadFileSystem
	{
		// DECLARED string[], AND THE NULL ROW AT THE CALL SITE IS WRITTEN `null!`. That is
		// not a trick to quieten the compiler — it is the fake's whole point stated in the
		// type system. A seam is an INTERFACE: nullable annotations are compile-time only,
		// so a third-party implementation of this PUBLIC interface can declare
		// IEnumerable<string> and hand back a null element at run time regardless of what
		// it promised. Declaring the field string?[] instead would push a nullable-variance
		// warning onto EnumerateFiles' return, which is the wrong place: nothing about the
		// seam's contract is nullable, only this fake's disregard for it.
		private readonly string[] _entries;

		internal HostileNameReadFileSystem(string[] entries)
		{
			_entries = entries;
		}

		public bool FileExists(string path)
		{
			return true;
		}

		// True, or the listing would return empty at its own directory guard and this
		// test would pass without ever reaching the names.
		public bool DirectoryExists(string path)
		{
			return true;
		}

		public Stream OpenRead(string path)
		{
			throw new FileNotFoundException("Not found.");
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return _entries;
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
		}

		public long GetLength(string path)
		{
			throw new FileNotFoundException("Not found.");
		}
	}
}

// STAND-INS THE OBSERVER HAS NEVER HEARD OF. Declared HERE, in the test assembly, on purpose,
// and what they prove CHANGED when the reflection walk was reverted.
//
// They used to prove the post-bind check covered any DTO automatically. It no longer does: the
// check names each member it inspects, because the automatic version executed arbitrary DTO
// getters on the read path and, measured, reported an INTACT file as BodyUnparseable for an
// ordinary `[JsonIgnore]` member. So these types now serve two honest purposes instead — they
// pin FRAMEWORK binder behaviour (what D-070's additive-readability promise rests on), and they
// pin the fail-safe DIRECTION of the named-member design: an unnamed type is reported Loaded,
// never corrupt. A reintroduced reflection walk fails those tests, which is the point.
//
// They live at namespace scope rather than nested in the test class so System.Text.Json binds
// them without any accessibility question, and they are deliberately NOT added to Core: Core
// carries the real documents, and inventing a fake one there to serve a test would put test
// scaffolding in the production contract. NoProductionAssemblyOtherThanCore_DeclaresAConfigDocument
// permits this assembly by name for exactly that reason.

// The shape the recorded additive-default rule invites. `= null!` is the idiomatic way to
// satisfy the compiler's non-nullable analysis on a member you mean to populate later, and
// MEASURED on .NET 10 it binds with Tags NULL when the member is absent from the JSON. Named
// FutureNullBang... rather than something neutral so a reader meeting it in a failure message
// learns what the shape is called and why it is wrong.
internal sealed record FutureNullBangDefaultDocument : ConfigDocument
{
	public IReadOnlyList<string> Tags { get; init; } = null!;
}

// THE SHAPE THAT FORCED THE REVERSAL, and it is an ORDINARY, CORRECT DTO member rather than a
// mistake being tolerated. [JsonIgnore] excludes the member from serialization precisely because
// it is NOT FILE STATE — a derived index, a lazily populated cache — so it is null on every
// freshly bound document, and `= null!` is the idiomatic way to say "populated later" to the
// compiler's non-nullable analysis.
//
// The reverted reflection walk had no [JsonIgnore] exclusion, so it inspected this member, found
// it null, and returned BodyUnparseable — a verdict that licenses quarantine-then-overwrite of a
// file nothing was wrong with. That is the worst outcome available on this path, produced by a
// document the binder handled perfectly. Kept as a type, not just as prose, so the failure is
// reproducible rather than remembered.
internal sealed record FutureJsonIgnoreCacheDocument : ConfigDocument
{
	[JsonIgnore]
	public IReadOnlyList<string> Cache { get; init; } = null!;
}

// The shape the rule ASKS for: a default that is itself a valid non-null instance. An absent
// member binds to an empty collection, which is an ordinary intact document and must be
// reported Parsed — the direction where a false positive would be destructive.
internal sealed record FutureEmptyCollectionDefaultDocument : ConfigDocument
{
	public IReadOnlyList<string> Tags { get; init; } = [];
}

// A collection whose elements cannot be null. It must pass through untouched, which pins that
// the check is about collections generally rather than about reference-element lists only.
internal sealed record FutureValueElementDocument : ConfigDocument
{
	public IReadOnlyList<int> Counts { get; init; } = [];
}
