using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using BroChoos.Core.Storage;

namespace BroChoos.Platform.Windows.Storage;

// The OBSERVATION half of the D-063 / D-085 config read path. It looks at one
// destination file (and, separately, at one backup candidate) and reports what it
// saw as a Core ConfigFileObservation. It decides NOTHING and writes NOTHING.
//
// THE ABSENCE OF WRITES IS THE SAFETY PROPERTY, not an incidental limitation. The
// read path's ordering invariant is observe -> decide -> preserve the original ->
// only then write. This type takes IStorageReadFileSystem, which has no write
// member and no CreateDirectory, so it is structurally incapable of repairing,
// restoring, quarantining, provisioning or defaulting. Whatever bug it may contain,
// it cannot be a bug that destroys a user's configuration. Repair belongs to the
// next slice, which takes a write seam explicitly and is visible as such.
//
// FAILURE VOCABULARY. Absent, locked, denied and corrupt are OBSERVATIONS, never
// exceptions: every one of them is an ordinary state of a config file on a real
// machine, and a read path that throws for them forces its caller to use exception
// handling as control flow. What escapes instead is governed by ONE INVARIANT, stated
// as an invariant rather than as a list of categories on purpose:
//
//   THROUGH A CONFORMING SEAM, WHAT ESCAPES THIS TYPE IS ALWAYS A DEFECT, AND NEVER A
//   STATE OF A CONFIG FILE.
//
// THAT IS THE WHOLE RULE, AND IT IS THE TEST TO APPLY TO A NEWLY DISCOVERED ESCAPE:
// not "which of the listed categories does it match", but "is this a DEFECT, or is it a
// STATE a config file can be in on a real machine". A defect belongs out, loudly. A
// state belongs in the observation vocabulary, and converting it is what the guarded
// helpers below do.
//
// A CLOSED ENUMERATION STOOD HERE AND WAS MEASURED NARROWER THAN THE MECHANISM, FIVE
// TIMES OVER, so the list is gone rather than extended once more. The failure mode of a
// list is not that it is wrong on the day it is written — it is that a reader meeting a
// kind outside it concludes the escape does not belong here, and "fixes" it by
// converting a defect into an observation. THE INVARIANT SURVIVES A NEW KIND; A LIST
// DOES NOT. Same reasoning that removed four numeric counts from this file.
//
// THE KNOWN KINDS ARE EXAMPLES, NOT AN ENUMERATION, and nothing about the invariant
// depends on this being all of them:
//
//   A CALLER BUG, REJECTED AT AN ENTRY POINT BEFORE ANY I/O. ArgumentNullException
//     for a null argument, at every public entry point; ArgumentOutOfRangeException
//     where ObserveNthNewestBackup rejects a NEGATIVE index, because "the -1st newest
//     backup" is not a question about a file; and ArgumentException where the three
//     generic entry points reject an ABSTRACT TDocument, which is a caller bug that
//     compiles because a type satisfies its own constraint (see
//     RejectAbstractDocumentType, including why NotSupportedException is forbidden as
//     the type there). All are fixable at the call site, and none has touched the
//     filesystem when it throws. NOT the same site as the seam-reported NEGATIVE
//     LENGTH discussed in the TryReadAllBytes catch clause below — that one is an
//     observation (OpenFailed) and never escapes. Do not conflate them.
//   A DEFECT IN THE SEAM. ObjectDisposedException, from a seam that handed back an
//     already-disposed stream — a thing no state of the user's filesystem can
//     produce, only a wrong IStorageReadFileSystem implementation. Unlike the first
//     kind it is NOT caught before any I/O, and it is NOT confined to one call
//     site: the exclusion is BY TYPE, and the catch filter it modifies guards the
//     WHOLE try in TryReadAllBytes, which calls GetLength, OpenRead and stream.Read.
//     A seam wrapping a disposed handle therefore escapes from ANY of those three;
//     stream.Read is simply the route pinned by name in the mid-read fact. A BROKEN
//     DirectoryExists or FileExists is the same kind, reached from the unguarded sites
//     described further down.
//   A DTO-CONTRACT MISCONFIGURATION, which fits NEITHER of the two above and is the
//     kind that falsified the enumeration this invariant replaces. A storage DTO whose
//     System.Text.Json contract cannot be built makes the SERIALIZER throw
//     InvalidOperationException, which derives from neither JsonException nor
//     NotSupportedException — so NEITHER of TryBindBody's catch clauses covers it and it
//     escapes Observe. MEASURED five ways on .NET 10 with this type's BodyOptions: two
//     members colliding via [JsonPropertyName]; two members differing only in case;
//     two [JsonConstructor] attributes; two [JsonExtensionData] members; and
//     [JsonExtensionData] on a string member. It is a DEFECT — a DTO in this repository
//     that cannot be deserialized at all, fixable only by editing the DTO — and never a
//     state of a user's file, so the invariant places it OUT without needing a bullet of
//     its own. Recorded here so the measurement is not lost, not to close the list
//     again.
//
// THE DIRECTION OF AN ESCAPE IS THE SAFE ONE, which is why the invariant can afford to
// send every defect out. An escape WRITES NOTHING: it crosses the boundary loudly and
// the user's file is left exactly as it was. Reporting an intact file as absent or
// corrupt is the opposite — it licenses defaults written over it, with or without a
// quarantine. When the two are in tension, the escape is the lesser cost.
//
// NO COUNT IS STATED, DELIBERATELY, and that is a repair rather than a style choice.
// FOUR numerals stood in this file and in ConfigFileObserverTests and they contradicted
// each other: a type-wide "three" here, a "two" thirty lines below scoped to this type
// in its own words, a "two" correctly scoped to Observe that cited this header for a
// scope this header did not have, and a "three" on HasUnusableCollectionMember that
// counted a DESTRUCTIVE FALSE POSITIVE as though it were an escape. A count also invites
// the wrong repair — the fix for a stale numeral looks like incrementing it, when what a
// reader actually needs is the CATEGORY, which tells them whether a newly discovered
// escape belongs here at all. The precedent is the CLAUSE CONVERSION reference in the
// TryReadAllBytes catch clause below, which states no count for exactly this reason.
//
// PER ENTRY POINT the reachable SITES differ — the negative-index rejection belongs to
// ObserveNthNewestBackup, so it is not reachable from Observe — but that is a fact about
// which sites exist, visible at each throw, not a number anybody has to keep current.
//
// THE SEAM-DEFECT CATEGORY SURVIVES ONLY BECAUSE THAT FILTER EXCLUDES
// ObjectDisposedException BY TYPE — it derives from InvalidOperationException, which the
// filter converts into OpenFailed, so without the exclusion every read through a disposed
// stream would be reported as a refusal to open and nothing would fail. Because the
// exclusion is a type test rather than a per-call-site one, catching a disposed stream at
// ONE of those three calls is not a harmless narrowing: it reinstates the silent
// OpenFailed for that route. Read the clause there before touching the filter or the
// exclusion.
//
// A NON-CONFORMING SEAM IS A FURTHER ROUTE OUT, and it is left open deliberately.
// THIS IS A PROPERTY OF THE TYPE, NOT OF ONE METHOD. There are THREE such unguarded
// seam calls, and EVERY PUBLIC METHOD EXCEPT Observe(BackupCandidate) reaches one —
// none of them has a try/catch anywhere in it. Observe(BackupCandidate) is named
// rather than counted around because its only seam contact is the read inside
// TryReadAllBytes, which is guarded. The SITES and the METHODS are not the same
// thing and must not be collapsed:
//
//   Observe(string destinationPath) — _fileSystem.FileExists(destinationPath), and
//     then _fileSystem.DirectoryExists(destinationPath) on the not-found branch, both
//     BEFORE the guarded read in TryReadAllBytes.
//   ListBackupCandidates — _fileSystem.DirectoryExists(backupsDirectory), the
//     absent-directory early return, before the enumeration.
//   ObserveNthNewestBackup — the SAME site as the one above, reached because it
//     forwards to ListBackupCandidates. A third entry point, not a fourth site.
//
// A seam throwing from any of the three escapes. That is not a gap in the vocabulary:
// IStorageReadFileSystem DECLARES of BOTH members that they never throw for the
// absence cases these three sites test — FileExists is "False when the file is absent
// OR its directory is absent. Never throws for either", and DirectoryExists carries
// the matching declaration for an absent directory — so an implementation that throws
// there is not an unusual state of a filesystem, it is a BROKEN SEAM, the same
// kind of DEFECT as the escapes above. Both declarations are needed, because two
// of the three sites are DirectoryExists; resting this on FileExists alone would
// justify one site out of three. Wrapping those calls would convert a broken
// collaborator into "the config file is unreadable", which is exactly the
// diagnosability loss the ObjectDisposedException ruling rejected, so no try is added
// around them. The invariant above describes a CONFORMING seam, and is not a promise
// that arbitrary code cannot throw from anywhere.
//
// ALL THREE SITES ARE NOW PINNED BY FACTS, AND THIS PARAGRAPH PREVIOUSLY CLAIMED SO
// WHILE IT WAS FALSE — false, moreover, about the worst of the three. It read "All three
// sites are pinned by facts of their own" and cited the DirectoryExists facts for all of
// them. Those facts are three ENTRY POINTS but only TWO SITES: every one of them uses a
// seam whose FileExists is `=> false` and never throws, and an audit of every FileExists
// implementation in the test file found none that throws. So the FileExists site rested on
// nothing, and `try/catch (IOException) { return false; }` around it would have gone in
// with no red test. Each site now carries a fact naming the member that throws:
//
//   FileExists — Observe_SeamFileExistsThrowsIo_LetsItEscapeInsteadOfReportingAbsent.
//     ITS COST IS THE HIGHEST OF THE THREE, which is why its being unpinned mattered
//     most: a swallowed FileExists falls through to a DirectoryExists that answers
//     false, so Observe reports Absent() — and Absent routes to CreateDefaultsAndPersist,
//     writing defaults over an EXISTING file with NO QUARANTINE, because the planner was
//     told there was nothing to protect. The DirectoryExists sites convert to OpenFailed
//     or [], which write nothing.
//   DirectoryExists, both sites — the three
//     *_SeamDirectoryExistsThrowsIo_LetsItEscape* facts, across Observe,
//     ListBackupCandidates and ObserveNthNewestBackup.
//
// IOException IS THE THROWN TYPE IN ALL OF THEM ON PURPOSE: it is otherwise the most
// CONVERTIBLE type on these paths, so those facts prove the ABSENCE of a conversion
// rather than merely that something propagated.
//
// THE RULE THAT DECIDES CONVERT-OR-ESCAPE, stated so it is CHECKABLE AGAINST THE CALL
// SITES rather than taken on trust, because the three private seam helpers below catch
// THREE DIFFERENT VOCABULARIES and the difference is deliberate:
//
//   WHAT IS CONVERTED IS A STATE OF A REAL FILESYSTEM. WHAT ESCAPES IS A DEFECT —
//   the invariant at the top of this file is what decides which, and the KINDS it
//   lists are EXAMPLES, not a vocabulary to be applied by matching — AND THE WIDER
//   THE BLAST RADIUS OF THE CONVERSION, THE STRONGER THE CASE FOR LETTING IT ESCAPE.
//
//   NO LIST OF KINDS IS RESTATED HERE, DELIBERATELY. A restatement near a call site
//   goes stale where the header does not, and a reader who applies a stale list
//   LITERALLY has written license to catch a kind missing from it and convert it —
//   which on this path means turning a write-nothing escape into a quarantine plus
//   defaults over the user's file. That is the exact failure the header names.
//
// Read against the three helpers, that yields their actual vocabularies rather than an
// oversight to be tidied up:
//
//   TryReadAllBytes converts the most (IOException, UnauthorizedAccessException,
//     ArgumentException, NotSupportedException, InvalidOperationException, with
//     ObjectDisposedException excluded by type) and can AFFORD to: its conversion is
//     OpenFailed, which writes NOTHING and claims NOTHING about the bytes. It also has
//     the widest path provenance — a caller's destinationPath on one call and a
//     SEAM-ENUMERATED candidate.FullPath on the other, which is what makes its
//     ArgumentException clause reachable at all.
//
//     THIS HELPER IS GOVERNED BY TWO AXES AT ONCE, and neither one is the whole rule.
//     Presenting either as the whole rule is what made earlier accounts of this file
//     half-true, so both are named:
//
//       BLAST RADIUS decides WHAT IT CONVERTS. OpenFailed writes nothing and claims
//         nothing, so the widest vocabulary is affordable here and narrow ones are not
//         affordable elsewhere (see EnumerateQuietly, whose [] can cost a backup).
//       KIND AND DIAGNOSABILITY decide WHAT IT EXCLUDES. Blast radius CANNOT explain
//         the ObjectDisposedException exclusion, and it is worth being explicit that it
//         cannot: converting a disposed stream would also yield OpenFailed, which also
//         writes nothing, so on the blast-radius axis alone the exclusion has no
//         justification at all. It survives because a disposed stream IS NOT A STATE OF
//         A CONFIG FILE — it is a defect, the invariant at the top of this file sends
//         defects out, and an escape buys back the diagnosability that a silent
//         OpenFailed on every read would destroy.
//
//     A THIRD HYPOTHESIS WAS TRIED AND BROKE ON THIS SAME HELPER, recorded so it is not
//     tried again: that PATH PROVENANCE decides the vocabulary (a caller's path versus a
//     seam-enumerated one). It holds for StatQuietly and EnumerateQuietly below, and it
//     fails here — TryReadAllBytes takes BOTH kinds of path, so provenance cannot pick a
//     vocabulary for it. Provenance explains why its ArgumentException clause is
//     REACHABLE; it does not decide what the clause catches.
//   StatQuietly converts three (IOException, UnauthorizedAccessException,
//     ArgumentException) on a path the SEAM ITSELF produced by enumeration, so it must
//     tolerate a malformed name the seam handed back. Its conversion is the narrowest
//     available: ONE candidate is labelled StampUnavailable, and the label says
//     truthfully that nothing was learned. Nothing is lost.
//   EnumerateQuietly converts the FEWEST (IOException, UnauthorizedAccessException) and
//     that is the point, not an omission. Its conversion is [] — "no backup candidates
//     exist" — which the pure decision layer reads as "nothing to restore from", and
//     which feeds the promotion and defaults decisions. A swallow here can cost the user
//     a RECOVERABLE BACKUP, whereas an escape costs only a loud failure. Its directory
//     argument comes from the CALLER, so a malformed one is a caller bug, the first
//     category above. So NotSupportedException, InvalidOperationException and
//     ArgumentException are left to escape ListBackupCandidates (and
//     ObserveNthNewestBackup, which forwards to it) ON PURPOSE. DO NOT widen these
//     catches to match TryReadAllBytes' list for symmetry's sake — symmetry is not the
//     property being protected here.
//
// OutOfMemoryException ON THE FILE-CONTENT PATH IS CLOSED. Reading a file whole into
// memory with no size cap made OutOfMemoryException reachable for a pathologically
// large file in the config directory, and OOM is not an observation — it would have
// escaped Observe. The size is checked through the seam BEFORE any bytes are
// allocated and an oversized file is reported as OpenFailed; see MaxReadableBytes
// for the ceiling and why it cannot reject a legitimate file. Catching OOM was
// rejected outright: it hides a real fault and cannot tell an absurd file from a
// heap exhausted by something else.
//
// IT IS NOT CLOSED ON THE DIRECTORY-LISTING PATH, AND THAT IS STATED RATHER THAN
// IMPLIED SHUT. This paragraph previously read as though the byte ceiling closed the
// OOM hazard for this type. IT DOES NOT: MaxReadableBytes caps the CONTENT OF ONE FILE,
// and it says nothing about how many ENTRIES a directory listing may contain.
// EnumerateQuietly materializes the seam's sequence with .ToList(), so a seam yielding
// an unbounded number of entries exhausts the heap there, and EnumerateQuietly catches
// only IOException and UnauthorizedAccessException — so OutOfMemoryException escapes
// ListBackupCandidates and ObserveNthNewestBackup. Reachable only through a seam that
// hands back such a sequence; the property claimed by this file belongs to the TYPE, not
// to one implementation, so it is recorded as reachable rather than argued away.
//
// LEFT UNBOUNDED, DELIBERATELY, AND THE DECISION IS THE POINT RATHER THAN THE
// BEHAVIOUR. Two constraints pull against a bound and neither can be satisfied by
// choosing a number:
//
//   A BOUND CANNOT BE BOTH SAFE AND GENEROUS HERE, which is what separates this from
//     MaxReadableBytes. That ceiling works because 8 MiB is simultaneously absurd for a
//     config file AND small enough that reading it cannot exhaust the heap. No entry
//     count has both properties: a list of paths costs on the order of a hundred bytes
//     per entry, so a bound low enough to guarantee the heap survives is low enough to
//     be a directory a filesystem could really hold — and refusing THAT converts a
//     STATE into a failure, which is precisely what the invariant at the top of this
//     file forbids.
//   TRUNCATING IS WORSE THAN THE OOM, and this is the decisive one. Candidates are
//     sorted by stamp only AFTER the listing completes, so dropping entries at the
//     ceiling drops ARBITRARY ones — including, possibly, the NEWEST backup. Losing a
//     recoverable backup is a real cost to the user; an OOM escape costs a loud
//     failure and writes NOTHING. The direction is the safe one, so the cheaper failure
//     is the one to keep.
//
// AND THE .ToList() MUST NOT SIMPLY BE MADE LAZY to avoid the materialization: it is
// there so an IOException from the enumeration is caught INSIDE EnumerateQuietly's try
// rather than surfacing at the first MoveNext, in the caller's foreach, outside every
// guard. Removing it trades a reachable-only-through-a-hostile-seam OOM for an
// escape reachable from an ordinary removed directory. If this is ever bounded, the
// bound must REPORT being hit rather than silently absorb it, and it must not decide
// which candidates survive before they are sorted.
//
// REDACTION (D-075 / D-097 — full user file paths must not reach logs, because in
// installed mode the path embeds the Windows account name). This type composes NO
// message containing a path, which is the strongest available form of compliance:
// there is no string to leak. Two specific hazards are avoided rather than merely
// noted. A framework DirectoryNotFoundException embeds the full path in its OWN
// message, and the write side's failure wrapper keeps the framework exception as
// InnerException, whose message embeds the path too — so no framework exception
// caught here is ever rethrown, wrapped, logged, or rendered via ToString() or
// InnerException.Message. Each one is converted into an observation and dropped.
// Paths travel only as DATA, on BackupCandidate.FullPath.
//
// NO CLOCK IS INJECTED, deliberately. Every time value this type reports comes from
// a file name or from the filesystem's last-write-time; nothing here needs "now", so
// injecting TimeProvider would add an unused dependency. Reaching for the ambient
// static UTC clock instead is not an option either — the write side does that
// directly in three places and it is a known wart, not a pattern to copy. If a
// future change here ever needs the current time, it takes TimeProvider (already
// registered in ServiceCollectionExtensions).
public sealed class ConfigFileObserver
{
	// Mirrors AtomicJsonFileStore's read-affecting options exactly. Being LESS
	// tolerant than the writer would report files as corrupt that the store round
	// trips happily; being MORE tolerant would report files as sound that the store
	// cannot read back. UnmappedMemberHandling.Skip is what lets a NEWER REVISION's
	// additive fields bind instead of failing — which is precisely the D-064 /
	// D-070 behaviour that makes NewerRevisionReadKnownOnly a readable state rather
	// than a corrupt one.
	private static readonly JsonSerializerOptions BodyOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		Converters = { new JsonStringEnumConverter() },
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,

		// A DUPLICATE BODY MEMBER IS REFUSED, and what it actually catches is NOT what
		// this comment used to claim. It said it closed a divergence on the ENVELOPE
		// properties; it does not, because those never reach here — the envelope reader
		// runs FIRST and its counting scan refuses every duplicate spelling of
		// schemaVersion / schemaRevision on its own (see TryReadInt).
		//
		// ITS UNIQUE CONTRIBUTION IS A DUPLICATE ON A BOUND BODY MEMBER, which no
		// envelope mechanism can see. MEASURED on .NET 10:
		//
		//   {"schemaVersion":1,"schemaRevision":0,"browsers":[],"BROWSERS":[{...}]}
		//     envelope        -> Parsed v=1 r=0   (both envelope properties are single
		//                                          and unambiguous; nothing to refuse)
		//     body strict     -> Duplicate property 'browsers' encountered
		//     body dup-allowed-> binds, browsers=1 (the LAST spelling silently wins)
		//
		// WHY THE TWO SPELLINGS COLLIDE AT ALL, precisely: for JsonSerializer duplicate
		// detection operates on the BOUND MEMBER, not on the raw property name. With
		// PropertyNameCaseInsensitive = true (set above) "browsers" and "BROWSERS" bind
		// to the SAME member, so they are one member given two values and the flag
		// refuses them. The case-insensitivity is what makes this catch mixed case —
		// not something it works around. (JsonDocument is the opposite and compares raw
		// ordinal UTF-8, so {"a":1,"A":2} is never a duplicate there; that asymmetry is
		// exactly why the envelope needs its own counting scan.)
		//
		// Refusing is strictly better than letting the last spelling win: a document
		// that cannot say which value it means has no business binding, and the
		// alternative is a silently-resolved list the user never wrote.
		//
		// This does NOT violate the mirror rule below. That rule exists to stop the app
		// writing a file it then refuses to read, and AtomicJsonFileStore serializes
		// from a runtime object, so it CANNOT emit two properties differing only in
		// case — no storage DTO has members colliding case-insensitively and none
		// carries [JsonPropertyName]. Pinned by
		// StrictReader_AcceptsEveryDocumentTheWriterCanProduce.
		AllowDuplicateProperties = false,

		// A POSITIONAL RECORD'S PARAMETER IS MANDATORY AGAIN. Without these,
		// {"schemaVersion":1,"schemaRevision":0} bound to a NON-null BrowsersDocument
		// whose Browsers list was null, so TryBindBody's "is not null" test passed and
		// a structurally invalid document was reported Loaded — satisfying owner
		// decision 2 ("a write is licensed only on a whole-document parse") on a
		// document that never parsed whole, and handing the consumer a
		// NullReferenceException, which is not in this type's vocabulary.
		//
		// THE STANDING CONSTRAINT THIS IMPOSES, stated correctly. This comment used to
		// claim the flag covers "present AND future DTOs uniformly ... with nothing to
		// keep in sync". MEASURED, that is BACKWARDS: the flag makes every positional
		// parameter WITHOUT a default mandatory in the JSON, so there IS something to
		// keep in sync, and it points the other way —
		//
		//   EVERY FUTURE ADDITIVE DTO MEMBER MUST CARRY AN EXPLICIT DEFAULT, AND THAT
		//   DEFAULT MUST ITSELF BE A VALID, NON-NULL INSTANCE — for a collection, an
		//   EMPTY COLLECTION (`= []`), never `= null!`.
		//
		// THE SECOND CLAUSE IS NOT PEDANTRY; WITHOUT IT THE RULE LEADS A CAREFUL EDITOR
		// INTO A BUG. `= null!` is the idiomatic way to satisfy the compiler's
		// non-nullable analysis on a member you intend to populate later, so it is the
		// FIRST thing a competent editor reaches for — and it satisfies the letter of
		// "carry an explicit default" while defeating its entire purpose. MEASURED on
		// .NET 10 with these exact BodyOptions:
		//
		//   public IReadOnlyList<string> Tags { get; init; } = null!;
		//     member absent from the JSON -> BINDS, and Tags is NULL
		//                                    (consumer then throws NullReferenceException)
		//   same member with `= []`
		//     member absent from the JSON -> BINDS, Tags is an empty list  (correct)
		//   same member with NO default
		//     member absent from the JSON -> JsonException, "missing required properties"
		//                                    (what the rule intends to prevent)
		//
		// NOTE THE DIRECTION OF THE `= null!` FAILURE, because it sets the severity: it
		// makes a BROKEN file look INTACT. That is the opposite of this component's most
		// feared failure (an intact file reported corrupt, which licenses a destructive
		// repair), and it licenses no repair — it hands the consumer an exception outside
		// this type's vocabulary. It is still a defect, and it is the one this wording
		// exists to prevent.
		//
		// AND THE RULE MUST BE REPAIRED RATHER THAN DROPPED, because omitting the default
		// entirely IS the destructive path: that lands on BodyUnparseable, which routes to
		// quarantine-then-overwrite of an intact older file. Both failure modes are real
		// and they point in opposite directions, which is exactly why the rule has to name
		// the shape of an acceptable default instead of merely demanding that one exist.
		//
		// A `= null!` default that slips through anyway is CAUGHT rather than trusted to
		// review: TryBindBody's post-bind check refuses a bound document whose collection
		// member is null — see HasUnusableCollectionMember, which names each covered member
		// explicitly. It is NOT automatic for a DTO nobody has extended it for, and that is
		// deliberate: the automatic version executed arbitrary DTO getters on the read path
		// and produced a DESTRUCTIVE false positive on a [JsonIgnore] member. The obligation
		// to extend it is enforced at BUILD TIME instead, by the tripwire test
		// EveryCollectionMemberOnACoreDocument_IsAccountedFor.
		//
		// Measured on .NET 10, a later build reading an EXISTING revision-0 file that
		// predates the new member:
		//
		//   additive member, NO default   -> JsonException: missing required properties
		//                                    including: 'theme'
		//   additive member, WITH default -> binds
		//
		// Without the default the newer build cannot read ANY file an older revision
		// wrote — and since that lands on BodyUnparseable, which licenses a destructive
		// repair, it would quarantine the user's data and write defaults over it. D-070
		// promises additive revisions stay readable, so the default is what keeps that
		// promise. A matching note sits on BrowserDefinition, where the next field will
		// actually be added.
		//
		// LastVerifiedAt was exactly this bug already: DateTimeOffset?, documented as
		// "null means not yet verified", yet mandatory in the JSON until it was given
		// "= null".
		//
		// BOTH FLAGS ARE REQUIRED; do not "simplify" by dropping this one. Measured:
		// with RespectNullableAnnotations alone, omitting "vendor" binds a NULL string,
		// reintroducing precisely the bug the pair fixes. The Strict PRESET was rejected
		// — it also sets UnmappedMemberHandling.Disallow, which would report a newer
		// revision's additive fields as corrupt and break D-064 / D-070 outright.
		RespectRequiredConstructorParameters = true,
		RespectNullableAnnotations = true,
	};

	// THE ENVELOPE IS PARSED WITH DEFAULT OPTIONS, AND THE LENIENCY IS DELIBERATE.
	// AllowDuplicateProperties = false was set here once and it was WRONG, because
	// JsonDocument's duplicate detection is DOCUMENT-WIDE and RECURSIVE — the
	// uniqueness set is per-object-scope for every object in the whole tree, and
	// there is no option scoping it to the top level. So a duplicate key ANYWHERE in
	// the BODY made the envelope unreadable although the envelope itself was
	// perfectly unambiguous. MEASURED on .NET 10:
	//
	//   {"schemaVersion":2,"schemaRevision":0,"browsers":[{"x":1,"x":2}]}
	//     strict  -> PARSE-REJECTED   (a body-only duplicate, two levels down)
	//     default -> Parsed v=2 r=0
	//
	// THE COST WAS THE NEWER-VERSION HARD STOP, i.e. the one case where being wrong
	// is destructive. That file must reach NewerVersionReadOnly ->
	// TryHardStopOnSchema -> HardStopReadOnly, where NOTHING is ever written.
	// Refusing it instead produced EnvelopeUnparseable, which carries NO schema, so
	// the planner fell through to PlanRepair and quarantined the newer original into
	// backups\corrupted before writing defaults over it — and
	// BackupPromotionClearance.IsBlockedByNewerOrInvalidSchema could not intervene,
	// because the destination schema had become null. Converting a read-only hard
	// stop into a destructive repair is the worst outcome available on this path.
	//
	// NOTHING WAS LOST BY REMOVING IT. All four rows of
	// Observe_DuplicateEnvelopeKeys_AreRefusedRatherThanSilentlyResolved are refused
	// by the counting scan in TryReadInt ALONE (measured: every one of them
	// SCAN-REFUSED with default options); the flag uniquely refused none of them.
	// The scan is the mechanism that closes the reader/binder divergence — see
	// TryReadInt. The BODY's own AllowDuplicateProperties = false is a different
	// matter and stays: it operates on the BOUND MEMBER, not on the raw document,
	// so it is not document-wide and cannot reach past what this type binds.
	//
	// There is deliberately no JsonDocumentOptions field at all now: an options
	// object carrying no non-default setting is ceremony that invites someone to
	// "just add one" without noticing the recursion above.

	// The read ceiling, and WHY THIS NUMBER. Without a cap, a pathologically large
	// file in the config directory is read whole into memory and yields
	// OutOfMemoryException — which is NOT in this type's stated failure vocabulary
	// (that vocabulary admits only DEFECTS from a CONFORMING seam; the header's
	// invariant is the test for what counts as one, and the kinds it names are examples,
	// so no list of them is restated here. OOM satisfies NEITHER side of that test: it
	// is a fact about the heap, not about the caller's arguments, not about the seam's
	// conformance, and not a state the user's config file can be in) and would escape
	// Observe.
	//
	// The cap is deliberately NOT a catch(OutOfMemoryException): OOM means the
	// process is already in trouble, catching it hides a real fault, and it cannot
	// distinguish "this file is absurd" from "something else exhausted the heap".
	// Refusing to read an absurd file is a decision that can be made BEFORE any
	// allocation, from metadata alone.
	//
	// SIZED SO IT CANNOT REJECT A LEGITIMATE FILE, which is the only way this fix
	// could do harm. browsers.json holds a browser list; the kill-test harness
	// already exercises a synthetic 300-entry document of roughly 100 KB, i.e. about
	// 340 bytes per entry. 8 MiB is ~80x that document and leaves room for on the
	// order of 24,000 browser entries — orders of magnitude past any real machine,
	// while still small enough that reading it cannot plausibly exhaust the heap.
	// A file past this point is not a config file that grew; it is a different
	// problem, and reporting it as OpenFailed says exactly that: the bytes were not
	// obtained, so nothing is known about them and nothing may be written on their
	// strength.
	private const long MaxReadableBytes = 8L * 1024 * 1024;

	// The copy buffer. 64 KiB is the usual sweet spot for sequential file reads and
	// is unrelated to the ceiling above — it only decides how many syscalls a
	// legitimate document costs, never how much may be accepted.
	private const int CopyChunkBytes = 64 * 1024;

	private const string SchemaVersionProperty = "schemaVersion";

	private const string SchemaRevisionProperty = "schemaRevision";

	private readonly IStorageReadFileSystem _fileSystem;

	public ConfigFileObserver(IStorageReadFileSystem fileSystem)
	{
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
	}

	// A READ-ONLY follow-up question for a caller that got OpenFailed: was it the size
	// ceiling? The observation itself stays OpenFailed on purpose (see TryReadAllBytes: an
	// unread file must never license a repair); this only lets the picker's notice tell an
	// oversized settings file from a transient lock. A failed metadata read answers false.
	internal bool ExceedsReadCeiling(string path)
	{
		try
		{
			return _fileSystem.GetLength(path) > MaxReadableBytes;
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	// THE ONE "IS THIS FILE USABLE BY THE PICKER" ANSWER. The picker's stores and Safe Mode's
	// repair-settings both ask here, so the notice and the repair can never disagree about
	// whether a file is fixed (they did: a newer-REVISION file is readable for the planner but
	// rejected by the stores, so a repair said "already readable" while the notice stayed).
	// The document is returned only for Loaded.
	internal (ConfigReadOutcome Outcome, TDocument? Document) ObserveReadOutcome<TDocument>(string path)
		where TDocument : ConfigDocument
	{
		(ConfigFileObservation observation, TDocument? document) = ObserveDocument<TDocument>(path);
		ConfigReadOutcome outcome = ConfigReadOutcomes.Classify(observation, document is not null);
		if (outcome == ConfigReadOutcome.OpenFailed && ExceedsReadCeiling(path))
		{
			outcome = ConfigReadOutcome.TooLarge;
		}

		return (outcome, outcome == ConfigReadOutcome.Loaded ? document : null);
	}

	// AN ABSTRACT TDocument IS A CALLER BUG, AND IT IS REJECTED BEFORE ANY I/O AT EVERY
	// GENERIC ENTRY POINT. `where TDocument : ConfigDocument` does NOT exclude
	// ConfigDocument itself — a type satisfies its own constraint — so
	// Observe<ConfigDocument>(path) compiles from any assembly, including one this
	// repository does not contain.
	//
	// WHY IT MATTERS, AND IT IS THE DESTRUCTIVE DIRECTION. MEASURED on .NET 10 with this
	// type's BodyOptions, System.Text.Json refuses an abstract target:
	//
	//   JsonSerializer.Deserialize<ConfigDocument>({"schemaVersion":1,"schemaRevision":0,"items":[]})
	//     -> NotSupportedException: "Deserialization of interface or abstract types is
	//        not supported."
	//
	// and TryBindBody's catch (NotSupportedException) SWALLOWS it and returns false. So a
	// FULLY INTACT file was reported BodyUnparseable(schema), which routes to PlanRepair ->
	// QuarantineThenCreateDefaults: the user's config quarantined and defaults written over
	// it, on a document nothing was wrong with. Reporting an intact file as corrupt is the
	// worst outcome available on this path.
	//
	// ArgumentException, AND NOT NotSupportedException — the type matters and the obvious
	// choice is FORBIDDEN here. NotSupportedException is exactly what the framework raises
	// for this shape, so it reads as the faithful type; but TryBindBody catches it and
	// converts it into "the body did not bind", which is the very swallow this guard
	// exists to prevent. A guard that can be eaten by the code it protects is not a
	// guard. ArgumentException is the family this type's other caller-bug rejections
	// already use (ArgumentNullException for a null argument,
	// ArgumentOutOfRangeException for a negative index), and it is the honest
	// description: the caller supplied a type argument that cannot be bound.
	//
	// THE "BEFORE ANY I/O" PLACEMENT IS LOAD-BEARING TWICE OVER, so do not move this call
	// below a read. Once for the obvious reason — nothing has been observed, so nothing can
	// be reported about a file and no repair can be licensed. And once for a subtler one:
	// TryReadAllBytes' widened catch filter converts ArgumentException into OpenFailed, so
	// a guard sitting inside or after that read would be swallowed too, differently but
	// just as silently. Both hazards point the same way — reject at the entry point.
	//
	// A TYPE CHECK, NOT MEMBER REFLECTION. typeof(TDocument).IsAbstract asks one question
	// of the type itself and executes no DTO code; it does not enumerate, read or invoke
	// any member. The reversed reflection walk on the read path was reversed because
	// member.GetValue(document) ran ARBITRARY DTO GETTERS inside this component and
	// produced a destructive false positive. Nothing of that kind happens here, so the
	// structural safety case is untouched.
	//
	// ONE CHECK COVERS INTERFACES TOO: MEASURED, Type.IsAbstract is true for an interface
	// (System.Collections.IEnumerable) as well as for an abstract class (System.IO.Stream),
	// and false for a sealed one (System.String). Note what that does and does not buy
	// here — the constraint is a CLASS constraint, so no interface can satisfy it in the
	// first place; the interface case is covered rather than reachable. Abstract classes
	// are the reachable half.
	private static void RejectAbstractDocumentType<TDocument>()
		where TDocument : ConfigDocument
	{
		if (typeof(TDocument).IsAbstract)
		{
			throw new ArgumentException(
				"A config document type must be concrete: an abstract type cannot be deserialized.",
				nameof(TDocument));
		}
	}

	// Observes ONE destination file. Maps onto the Core factories as:
	//
	//   file (or its directory) not there        -> Absent()
	//   exists, will not open (lock / ACL)       -> OpenFailed()
	//   opened, envelope did not parse           -> EnvelopeUnparseable()
	//   opened, envelope parsed, body did not    -> BodyUnparseable(schema)
	//   opened, both parsed                      -> Loaded(schema)
	//
	// OpenFailed is kept strictly distinct from a corrupt state. A LOCKED FILE IS
	// NOT A DAMAGED FILE: its unreadable bytes may well be a newer version's
	// (D-066), and collapsing it into "corrupt" would later authorise a destructive
	// repair of perfectly good data.
	//
	// No backup is attached here. Choosing which candidate to promote is the pure
	// layer's decision, so this method never reaches for one; a caller that wants a
	// candidate described asks for it explicitly and attaches it with Core's
	// ConfigFileObservation.WithBackup.
	public ConfigFileObservation Observe<TDocument>(string destinationPath)
		where TDocument : ConfigDocument
	{
		return ObserveDocument<TDocument>(destinationPath).Observation;
	}

	internal (ConfigFileObservation Observation, TDocument? Document) ObserveDocument<TDocument>(string destinationPath)
		where TDocument : ConfigDocument
	{
		ArgumentNullException.ThrowIfNull(destinationPath);
		RejectAbstractDocumentType<TDocument>();

		if (!_fileSystem.FileExists(destinationPath))
		{
			// A DIRECTORY WHERE A CONFIG FILE BELONGS IS NOT AN ABSENT FILE.
			// File.Exists is false for a directory, so without this test the path was
			// reported Absent, the planner took STEP 1 and returned
			// CreateDefaultsAndPersist, and the next slice was licensed to write to a
			// path that is a directory — a write that cannot succeed, aimed at
			// something nobody read.
			//
			// The honest report is OpenFailed: something IS there and it cannot be read
			// as a file. That is also the safe report — the planner ends OpenFailed at
			// STEP 2 with in-memory defaults, explicitly "NO repair, NO write of any
			// kind" — so this authorises nothing on a path that was never read.
			// DirectoryExists is already on the seam, so this needs no new member and
			// no throw.
			//
			// A KNOWN LIMIT OF THE Absent REPORT, RECORDED AND DELIBERATELY NOT FIXED: a
			// file whose name ends in a SPACE or a DOT exists on disk yet is reported
			// Absent. MEASURED — such a name can be created through the \\?\ prefix, which
			// bypasses Win32 normalization, but FileExists here is File.Exists on the
			// ORDINARY path, normalization strips the trailing character, and the lookup
			// therefore misses: false, so Absent. Nothing this app writes can mint such a
			// name; AtomicJsonFileStore cannot produce one, so it takes an external tool.
			//
			// THE BOUND IS WHAT MAKES IT TOLERABLE, and it was measured too: normalization
			// applies EQUALLY to the write side, so the defaults write that Absent licenses
			// lands on the STRIPPED path — a DIFFERENT file. Nothing of the user's is
			// destroyed: MEASURED, the trailing-space file still holds its own bytes after
			// that write, read back through \\?\.
			//
			// THE Absent REPORT IS A PRECONDITION, NOT A PERMANENT PROPERTY. "Never opened"
			// stood here and was measured FALSE: once the stripped sibling exists, File.Exists
			// on the ORDINARY trailing-space path answers TRUE, and ReadAllText through that
			// path returns the SIBLING's bytes. So a second Observe of the trailing-space path
			// is not Absent — it resolves to the sibling and reads THAT file, rather than the
			// original staying orphaned.
			//
			// WHY NOT FIXED. A fix means \\?\ path handling on a READ path that is licensed
			// to write defaults, and long-path/verbatim semantics carry their own hazards —
			// paid to change a state that currently destroys nothing. Not worth it.
			return (_fileSystem.DirectoryExists(destinationPath)
				? ConfigFileObservation.OpenFailed()
				: ConfigFileObservation.Absent(), null);
		}

		(ConfigFileAccess access, byte[]? bytes) = TryReadAllBytes(destinationPath);
		if (access != ConfigFileAccess.Opened)
		{
			// A file that disappeared between the existence check and the open is
			// Absent, not OpenFailed — it is genuinely not there. Only a file that
			// exists and refuses to open is OpenFailed.
			return (access == ConfigFileAccess.Absent
				? ConfigFileObservation.Absent()
				: ConfigFileObservation.OpenFailed(), null);
		}

		// ENVELOPE BEFORE BODY (owner decision 1). schemaVersion / schemaRevision are
		// read with JsonDocument, touching only those two properties — the full DTO is
		// deliberately NOT deserialized to learn the envelope, because a body that
		// fails to bind would then destroy the envelope reading too, and the schema
		// verdict is exactly what decides whether a repair is even permitted.
		if (!TryReadEnvelope(bytes!, out int schemaVersion, out int schemaRevision))
		{
			// No SchemaCompatibility is passed, and the factory takes none: an
			// envelope that did not parse yields a schema that is UNKNOWABLE, and
			// none may be invented.
			return (ConfigFileObservation.EnvelopeUnparseable(), null);
		}

		SchemaCompatibility schema = SchemaCompatibilityEvaluator.Evaluate(schemaVersion, schemaRevision);

		// Owner decision 2: a write is licensed only on a WHOLE-DOCUMENT parse. The
		// envelope reading cleanly is NOT enough, so this is reported separately —
		// "envelope parses but body does not" is a distinct state, and the one that
		// must never later be plainly overwritten.
		return TryBindBody(bytes!, out TDocument? document)
			? (ConfigFileObservation.Loaded(schema), document)
			: (ConfigFileObservation.BodyUnparseable(schema), null);
	}

	// Every candidate backup of one document, NEWEST FIRST.
	//
	// NON-RECURSIVE, by contract. Quarantined originals will live in
	// backups\corrupted\, and recursing would pull them into the promotable-candidate
	// list — the same isolation the write side's retention prune relies on. The seam's
	// EnumerateFiles is non-recursive by declaration, so this cannot be reintroduced
	// by a parameter tweak.
	//
	// Candidates are matched on an explicit "<fileName>." boundary, mirroring the
	// write side's retention filter, so a document whose name merely prefixes another
	// ("app" vs "appx") never captures the other's backups.
	//
	// ORDERING. The key is the timestamp AtomicJsonFileStore mints into the file NAME.
	// Filesystem creation time cannot be used: File.Replace (Win32 ReplaceFile)
	// transfers the replaced destination's creation time onto the backup, so all
	// backups of one document share a ctime that says nothing about when they were
	// taken. A name with no parseable stamp falls back to last-write-time, which
	// ReplaceFile does not rewrite.
	//
	// THE SECONDARY KEY IS REQUIRED, not decorative. Two backups minted in the SAME
	// MILLISECOND carry IDENTICAL stamps, and OrderBy is a stable sort — so with the
	// stamp alone the winner would be decided by Directory.EnumerateFiles' return
	// order, which is filesystem-dependent and not contractual. The ordinal name
	// comparison makes "newest" reproducible. Which of two same-millisecond backups
	// wins carries NO MEANING (their names differ only by a random guid); the only
	// property claimed is that repeated runs agree.
	public IReadOnlyList<BackupCandidate> ListBackupCandidates(string backupsDirectory, string destinationFileName)
	{
		ArgumentNullException.ThrowIfNull(backupsDirectory);
		ArgumentNullException.ThrowIfNull(destinationFileName);

		if (!_fileSystem.DirectoryExists(backupsDirectory))
		{
			return [];
		}

		string prefix = destinationFileName + ".";

		List<BackupCandidate> candidates = [];
		foreach (string? path in EnumerateQuietly(backupsDirectory))
		{
			// A NULL ENTRY FROM THE SEAM IS SKIPPED, NOT THROWN ON, and it used to take the
			// whole listing down. MEASURED on .NET 10:
			//
			//   Path.GetFileName((string?)null)  -> null
			//   null.StartsWith(prefix, Ordinal) -> NullReferenceException
			//
			// So one null element in an EnumerateFiles result destroyed every other candidate
			// in the directory, and a NullReferenceException is not in this type's failure
			// vocabulary - it would have escaped ListBackupCandidates outright.
			//
			// NOT MERELY DEFENSIVE, AND NOT AN INTERNAL-ONLY CONCERN. Both
			// IStorageReadFileSystem and this type's constructor are PUBLIC, so an external
			// caller can supply such a seam through the public API. Unreachable with the real
			// SystemStorageReadFileSystem - and that is deliberately not the standard applied
			// here, exactly as the TryCreateName guard below argues for itself: the
			// never-throws property belongs to this TYPE, not to one seam implementation.
			//
			// The EMPTY name is skipped in the same breath. A trailing-separator entry reduces
			// to "" (measured), which the prefix filter already dropped, so this is not a new
			// behaviour - it is the same "not a candidate" verdict stated once, before any
			// member is called on the value.
			if (path is null)
			{
				continue;
			}

			string? name = Path.GetFileName(path);
			if (string.IsNullOrEmpty(name))
			{
				continue;
			}

			if (!name.StartsWith(prefix, StringComparison.Ordinal))
			{
				continue;
			}

			// A NAME THAT IS NOT A LEGAL BACKUP NAME IS NOT A CANDIDATE — skipped, not
			// thrown on. BackupName rejects empty, whitespace, "." / ".." and anything
			// path-qualified, and that construction used to sit outside every guard in
			// this method, so a seam whose EnumerateFiles yielded such an entry would
			// have made ListBackupCandidates throw ArgumentException. Unreachable with
			// the real Directory.EnumerateFiles (Path.GetFileName cannot produce those
			// from a real file path), but the never-throws property is a property of
			// this TYPE, not of one seam implementation.
			BackupCandidate? candidate = TryDescribe(path, name);
			if (candidate is null)
			{
				continue;
			}

			candidates.Add(candidate);
		}

		return [.. candidates
			.OrderByDescending(candidate => candidate.OrderingStampUtc)
			.ThenByDescending(candidate => candidate.Name.Value, StringComparer.Ordinal)];
	}

	// The Nth-newest candidate, 0 being the newest, or null when there is no such
	// candidate.
	//
	// SELECTION RULE: take the Nth-newest and merely DESCRIBE it. This deliberately
	// does NOT skip to a "better" candidate, however obviously unpromotable the Nth
	// one looks. Choosing which backup is promotable is the pure layer's decision
	// (D-085 attaches no schema precondition to its restore permission, and a backup
	// minted by a NEWER app version must not be promoted — that is a decision, made
	// from the observation, not inside it). The index exists so the next slice's retry
	// loop can walk the list while this type keeps deciding nothing.
	public BackupCandidateObservation? ObserveNthNewestBackup<TDocument>(
		string backupsDirectory,
		string destinationFileName,
		int index)
		where TDocument : ConfigDocument
	{
		ArgumentNullException.ThrowIfNull(backupsDirectory);
		ArgumentNullException.ThrowIfNull(destinationFileName);
		ArgumentOutOfRangeException.ThrowIfNegative(index);

		// REJECTED HERE RATHER THAN LEFT TO THE FORWARDED Observe, and the difference is
		// the whole point of guarding every generic entry point. This method would eventually
		// reach the guard anyway, through Observe<TDocument>(candidate) at the end — but
		// only AFTER ListBackupCandidates has already called DirectoryExists and
		// enumerated the directory. The property claimed is rejection BEFORE ANY I/O, so
		// inheriting the guard downstream is not the same guarantee.
		RejectAbstractDocumentType<TDocument>();

		IReadOnlyList<BackupCandidate> candidates = ListBackupCandidates(backupsDirectory, destinationFileName);
		return index < candidates.Count ? Observe<TDocument>(candidates[index]) : null;
	}

	// Describes one already-listed candidate. Separate from the listing so a caller
	// holding a candidate can observe it without re-enumerating (and without the
	// ordering shifting under it).
	public BackupCandidateObservation Observe<TDocument>(BackupCandidate candidate)
		where TDocument : ConfigDocument
	{
		ArgumentNullException.ThrowIfNull(candidate);
		RejectAbstractDocumentType<TDocument>();

		(ConfigFileAccess access, byte[]? bytes) = TryReadAllBytes(candidate.FullPath);
		if (access != ConfigFileAccess.Opened)
		{
			// Core's BackupObservation factories all presuppose that bytes were
			// obtained — there is no factory for "locked" or "vanished" — so those
			// states are reported by Access with a null Observation rather than by
			// forcing them into a factory that would misdescribe them.
			//
			// Named factories, not a raw constructor: "no bytes" and "here is a
			// verdict" are now structurally incapable of being reported together.
			return access == ConfigFileAccess.Absent
				? BackupCandidateObservation.Absent(candidate)
				: BackupCandidateObservation.OpenFailed(candidate);
		}

		return BackupCandidateObservation.Opened(candidate, ClassifyBackupBytes<TDocument>(candidate.Name, bytes!));
	}

	// Classifies ONE already-captured byte snapshot of a backup, with exactly the rules
	// Observe(BackupCandidate) applies: the BOM is skipped once, the envelope is read by the
	// counting scan, and the body is bound with the strict options. The repair executor calls
	// this on the SAME bytes it then stages and publishes, so promotion validates what it
	// writes rather than a second read of the file.
	internal static BackupObservation ObserveCapturedBackup<TDocument>(BackupName name, byte[] capturedBytes)
		where TDocument : ConfigDocument
	{
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(capturedBytes);
		RejectAbstractDocumentType<TDocument>();

		// The same ceiling TryReadAllBytes applies at selection: a snapshot past it is never
		// classified as promotable, however well it would bind.
		return capturedBytes.LongLength > MaxReadableBytes
			? BackupObservation.EnvelopeUnparseable(name)
			: ClassifyBackupBytes<TDocument>(name, SkipUtf8Bom(capturedBytes));
	}

	// The backup's OWN envelope, parsed with the same envelope-before-body
	// discipline as the destination. A backup minted by a newer app version is
	// itself newer-schema, and promoting it would convert a recoverable
	// corrupt-file problem into a permanent read-only lockout — so its verdict is
	// observed here and left for the pure layer to act on. `bytes` has had any BOM skipped.
	private static BackupObservation ClassifyBackupBytes<TDocument>(BackupName name, byte[] bytes)
		where TDocument : ConfigDocument
	{
		if (!TryReadEnvelope(bytes, out int version, out int revision))
		{
			return BackupObservation.EnvelopeUnparseable(name);
		}

		SchemaCompatibility schema = SchemaCompatibilityEvaluator.Evaluate(version, revision);
		return TryBindBody(bytes, out TDocument? _)
			? BackupObservation.Loaded(name, schema)
			: BackupObservation.BodyUnparseable(name, schema);
	}

	// Builds the ordering key for one candidate, and is where the 1601 TRAP is caught.
	//
	// File.GetLastWriteTimeUtc on a MISSING file does not throw — it returns
	// 1601-01-01T00:00:00Z. A candidate deleted between enumeration and stat would
	// therefore acquire the oldest-possible timestamp and sort as the oldest real
	// backup, with nothing in the result saying otherwise. The sentinel is detected
	// and reported as Vanished instead: it still sorts oldest (there is nothing better
	// to sort it by), but it is LABELLED, so no caller can read that 1601 as an
	// observed time.
	//
	// A FAILED STAT IS A THIRD CASE, not a flavour of the second. It used to be folded
	// into Vanished, which made the label untrue in the one dimension that matters:
	// Vanished says "gone, move to the next-newest candidate", so a file that merely
	// denied its stat — and might well become readable — invited a caller to abandon
	// it. It is reported as StampUnavailable now. Both still sort oldest; only one of
	// them claims the file is gone.
	//
	// Returns null when the entry cannot be a candidate at all, so the caller skips it
	// instead of the listing throwing.
	private BackupCandidate? TryDescribe(string path, string name)
	{
		if (!TryCreateName(name, out BackupName? backupName))
		{
			return null;
		}

		if (BackupFileNameStamp.TryParse(name, out DateTime minted))
		{
			return new BackupCandidate(backupName, path, minted, BackupStampSource.MintedFileName);
		}

		DateTime? lastWrite = StatQuietly(path);

		// THREE outcomes, kept distinct because the middle one used to be mislabelled.
		// A stat that FAILED (null) is not evidence the file is gone — it is evidence
		// nothing was learned about it. Reporting that as Vanished told a caller "do
		// not come back for this candidate" about a file that exists and may well
		// become readable, which is how a retryable/not-retryable distinction gets
		// lost. Both sentinel-stamped cases still sort oldest; only the LABEL differs,
		// and the label is now true.
		return lastWrite is null
			? new BackupCandidate(
				backupName,
				path,
				BackupFileNameStamp.MissingFileTimestampSentinel,
				BackupStampSource.StampUnavailable)
			: lastWrite.Value == BackupFileNameStamp.MissingFileTimestampSentinel
			? new BackupCandidate(backupName, path, lastWrite.Value, BackupStampSource.Vanished)
			: new BackupCandidate(backupName, path, lastWrite.Value, BackupStampSource.LastWriteTime);
	}

	// Asks CORE whether a name is a legal backup name, instead of re-testing its rules
	// here. BackupName owns the definition (non-empty, not "." / "..", no path
	// qualifier); duplicating those tests platform-side would give the read path a
	// SECOND definition of legality that could silently drift from Core's. So the
	// constructor is the validator, and its refusal is converted into "not a
	// candidate" — the same convert-and-drop discipline every other failure in this
	// type uses. Nothing is rethrown, wrapped, logged or rendered: the exception
	// object is not touched, so no path-bearing message can escape.
	private static bool TryCreateName(string name, [NotNullWhen(true)] out BackupName? backupName)
	{
		try
		{
			backupName = new BackupName(name);
			return true;
		}
		catch (ArgumentException)
		{
			backupName = null;
			return false;
		}
	}

	// Reads the whole file into memory so the envelope and the body are parsed from
	// ONE snapshot of the bytes. Re-opening for the second parse would let a
	// concurrent writer change the file in between, producing a self-contradictory
	// observation (an envelope from one revision, a body from another).
	//
	// Absent and OpenFailed are returned, never thrown. Note what is NOT caught:
	// nothing rethrows or logs the framework exception, so neither
	// DirectoryNotFoundException's path-bearing message nor any InnerException chain
	// can reach a sink.
	private (ConfigFileAccess Access, byte[]? Bytes) TryReadAllBytes(string path)
	{
		try
		{
			// A CHEAP EARLY-OUT, NOT THE GUARANTEE. The length comes through the read
			// seam and throws in the SAME vocabulary as OpenRead, so the two catch
			// clauses below already describe every way it can fail — no new handling,
			// and no exception escapes.
			//
			// Over the ceiling is OpenFailed, not a corrupt state: nothing was read, so
			// nothing is known to be damaged. Calling an oversized file corrupt would
			// later authorise a destructive repair of a file nobody has looked at.
			long length = _fileSystem.GetLength(path);

			// A NEGATIVE LENGTH IS GUARDED EXPLICITLY, so the intent is legible rather
			// than incidental. It cannot come from FileInfo.Length, but the seam is an
			// interface and this type's never-throws property belongs to the TYPE, not
			// to one implementation. Left unguarded it reached
			// new MemoryStream((int)length), which throws ArgumentOutOfRangeException —
			// caught below only because that derives from ArgumentException, i.e. by
			// accident. Refused as OpenFailed: a length that cannot be true means
			// nothing was learned about the file, so nothing may be written on its
			// strength.
			if (length < 0)
			{
				return (ConfigFileAccess.OpenFailed, null);
			}

			if (length > MaxReadableBytes)
			{
				return (ConfigFileAccess.OpenFailed, null);
			}

			using Stream stream = _fileSystem.OpenRead(path);

			// THE COPY ITSELF IS BOUNDED, and this is what actually closes the hole.
			// The check above is a separate call from the open below, so it is
			// inherently TOCTOU: a file that measures 1 MiB and is extended before
			// OpenRead would otherwise be read to EOF — a MemoryStream grows, so
			// CopyTo would happily consume all of it and OutOfMemoryException, the very
			// escape this ceiling exists to prevent, would still be reachable. So the
			// limit is enforced against the BYTES ACTUALLY ARRIVING, not against a
			// size that was true a moment ago.
			//
			// Pre-sized from the measured length purely to avoid regrowth on the normal
			// path; correctness does not depend on that number being current.
			using MemoryStream buffer = new((int)length);
			byte[] chunk = new byte[CopyChunkBytes];
			long total = 0;

			while (true)
			{
				// Never ask for more than ONE byte past the ceiling. That single byte is
				// all the evidence needed to prove the file is oversized, so an absurd
				// file is refused without reading the rest of it into memory.
				int want = (int)Math.Min(chunk.Length, MaxReadableBytes + 1 - total);
				int read = stream.Read(chunk, 0, want);
				if (read == 0)
				{
					// End of stream — but whether that is a real EOF or a SHORT READ is
					// decided AFTER the loop, against the length the seam reported. A
					// zero here is not by itself evidence the document is complete.
					break;
				}

				total += read;
				if (total > MaxReadableBytes)
				{
					return (ConfigFileAccess.OpenFailed, null);
				}

				buffer.Write(chunk, 0, read);
			}

			// A SHORT READ IS NOT AN EOF, and treating it as one handed a SILENTLY
			// TRUNCATED snapshot on as though it were complete. Stream.Read is permitted
			// to return fewer bytes than asked for, and a 0 mid-stream is
			// indistinguishable from EOF at the call site. MEASURED: a 200 KiB stream
			// returning 0 once at 1 KiB produced Opened with a 65536-byte snapshot; a
			// seam claiming 8 MiB and yielding 10 bytes produced Opened with 10 bytes.
			//
			// WHY THIS IS THE WORST SHAPE OF BUG ON THIS PATH: a truncated JSON document
			// does not look truncated, it looks CORRUPT. It parses as
			// EnvelopeUnparseable, which licenses a quarantine-then-overwrite of a file
			// that was ACTUALLY INTACT — precisely the outcome the read path exists to
			// prevent.
			//
			// THE TEST IS AGAINST THE REPORTED LENGTH, which is the only independent
			// statement of how much there was to read. FEWER bytes arriving is a failed
			// read, so OpenFailed: the bytes were not obtained, therefore nothing is
			// known about them and nothing may be written on their strength. MORE
			// arriving is deliberately NOT failed — that is the TOCTOU growth case,
			// already bounded by the ceiling check inside the loop. A genuine SHRINK
			// between the check and the read is not a state this app creates: the writer
			// publishes by File.Replace and never truncates in place.
			if (total < length)
			{
				return (ConfigFileAccess.OpenFailed, null);
			}

			// ONE SNAPSHOT, and the BOM is skipped ONCE on it, here — so the envelope
			// reader and the body binder are handed the SAME array and cannot disagree
			// about which bytes they read. Compared against the reported length BEFORE
			// the skip, because the preamble is part of the file's length.
			return (ConfigFileAccess.Opened, SkipUtf8Bom(buffer.ToArray()));
		}
		catch (Exception gone) when (gone is FileNotFoundException or DirectoryNotFoundException)
		{
			// Both derive from IOException, so they must be caught FIRST: the file (or
			// its directory) is genuinely not there, which is Absent, not a refusal.
			return (ConfigFileAccess.Absent, null);
		}
		catch (Exception cannotOpen) when (cannotOpen is not ObjectDisposedException
			and (IOException or UnauthorizedAccessException or ArgumentException
			or NotSupportedException or InvalidOperationException))
		{
			// ObjectDisposedException IS EXCLUDED, and it is the one exception on this list
			// that must NOT become an observation. It derives from
			// InvalidOperationException, so the widened filter caught it silently: a seam
			// handing back an already-disposed stream — `using var s = File.OpenRead(path);
			// return s;`, a one-line reflex edit — would make the stream.Read inside this
			// try throw it, and EVERY read would then report OpenFailed with no exception
			// raised and no test failing on the production path. Before the widening it
			// surfaced loudly.
			//
			// THE REASON IS A CATEGORY DIFFERENCE, not a preference. Every other type in
			// this filter is an ORDINARY STATE OF A CONFIG FILE on a real machine (locked,
			// denied, a malformed path, a seam refusing) and therefore belongs in the
			// observation vocabulary. A DISPOSED STREAM IS ALWAYS A DEFECT: no state of the
			// user's filesystem produces one, only a wrong seam implementation does. It is
			// the same category as the ArgumentNullException this type deliberately lets
			// escape as a caller bug, so it escapes for the same reason — and the cost of
			// swallowing it is diagnosability, which is exactly what an escape buys back.
			//
			// EXCLUDED IN THE FILTER RATHER THAN RETHROWN FROM A CLAUSE OF ITS OWN, for two
			// reasons. The exclusion sits against the inclusion list it modifies, so a
			// reader sees the whole vocabulary of this clause in one place. And nothing is
			// caught at all: the exception is never handled, so there is no `throw;` site
			// for a later edit to grow a wrapped or rendered message on — which matters
			// here, where D-075 / D-097 forbid a path-bearing message reaching a sink. No
			// text is composed about it either way; the object is not touched.
			//
			// The Absent clause above still comes FIRST and must stay there:
			// FileNotFoundException and DirectoryNotFoundException both derive from
			// IOException, so reversing the two would report a missing file as a refusal.
			//
			// A sharing violation (another process holds it) and a denied ACL both land
			// here, and both are OpenFailed — NOT corrupt. Nothing may be written on
			// the strength of bytes that were never read.
			//
			// THIS LIST IS CLOSED BY THE CONTRACT, NOT BY WHAT THE CURRENT SEAM HAPPENS
			// TO THROW. The declared property at the top of this file is TYPE-WIDE: through a
			// CONFORMING seam this type returns observations and throws only for DEFECTS. WHAT
			// COUNTS AS ONE IS THE HEADER'S INVARIANT, AND NO COUNT AND NO LIST OF KINDS IS
			// RESTATED HERE — a numeral goes stale silently, and a restated vocabulary is
			// worse than stale: a reader who applies it LITERALLY is licensed to catch a kind
			// missing from it and convert it, which on this path turns a write-nothing escape
			// into a quarantine plus defaults over the user's file. Observe reaches a SUBSET of
			// the throwing SITES, and a site is visible where it throws rather than being a
			// number to keep current.
			//
			// THAT EXCLUSION IS NOT MID-READ ONLY: it is a type test, and this filter
			// guards the whole try above, which calls GetLength, OpenRead and stream.Read — so
			// a seam wrapping a disposed handle escapes from any of the three, and stream.Read
			// is only the route the mid-read fact pins. Catching a disposed stream at
			// GetLength or OpenRead would therefore not close a harmless gap; it would
			// reinstate the silent OpenFailed for that route. Neither defect is a state of a
			// config file, so
			// neither weakens what follows: everything this clause DOES catch is such a
			// state. That never-throws-for-a-state property belongs to THIS TYPE, so the
			// filter has to cover every way an ARBITRARY IStorageReadFileSystem
			// implementation can
			// fail — the seam is an interface, and a future or test implementation is
			// under no obligation to restrict itself to the framework's file vocabulary.
			// Enumerating only what SystemStorageReadFileSystem is observed to throw
			// would make the contract true by coincidence and silently false the day a
			// second implementation appears.
			//
			// NotSupportedException and InvalidOperationException ARE IN THIS LIST
			// BECAUSE THE FILE WAS ALREADY INCONSISTENT ABOUT THEM, which is a stronger
			// reason than any imported style rule. TryBindBody below already catches
			// NotSupportedException and converts it into "the body did not bind", so the
			// SAME exception type was an in-vocabulary observation when the binder raised
			// it and an escape when the seam did. And the reversal account on
			// HasUnusableCollectionMember records, as a measured DEFECT, that
			// "InvalidOperationException escaped Observe outright, breaking the
			// never-throws vocabulary" — so this file already treats an escaping
			// InvalidOperationException as a bug to be closed, not as a caller bug to be
			// propagated. MEASURED through the public constructor and the public seam
			// before this clause was widened: GetLength and OpenRead throwing either
			// type, and Stream.Read throwing InvalidOperationException, all escaped
			// Observe; each is now OpenFailed. Pinned by the
			// Observe_Seam*_ReportsOpenFailedInsteadOfEscaping facts in
			// ConfigFileObserverTests. NO COUNT IS STATED, deliberately: the suffix is
			// what identifies a CLAUSE CONVERSION, so a sixth one added under the same
			// name matches this reference without anybody having to update a number
			// here. The looser _ReportsOpenFailed* would also catch the length-guard
			// facts (a negative and an over-reported length), which this clause does
			// not convert — they are refused explicitly before the read. The
			// deliberate-escape facts end _LetsItEscapeInsteadOfReportingOpenFailed
			// and correctly match neither pattern.
			//
			// WHY THIS IS NOT THE ArgumentNullException CASE, since the two look alike.
			// ArgumentNullException is thrown BY THIS TYPE, at its own entry points,
			// before any I/O, about arguments ITS OWN CALLER passed — a bug the caller
			// can see and fix at the call site. A seam throwing mid-read is a fact about
			// a COLLABORATOR, which this type's caller did not necessarily choose and
			// cannot fix from the call site; propagating it would force exactly the
			// exception-handling-as-control-flow this vocabulary exists to remove. The
			// safe direction is also asymmetric: OpenFailed writes NOTHING, whereas an
			// escape crosses a boundary whose caller is not written to expect it.
			//
			// ArgumentException IS IN THIS LIST because it is NEITHER of the other two
			// and would otherwise ESCAPE the never-throws contract. MEASURED on .NET 10:
			// File.OpenRead and new FileInfo(path).Length both throw ArgumentException
			// for a path containing a NUL character ("Null character in path") and for
			// an empty path ("The path is empty"). Reachable through this method because
			// ListBackupCandidates feeds it a seam-supplied path:
			// Path.GetFileName(@"X:\a\<NUL>b.bak") returns the name with the NUL INTACT
			// (measured), and BackupName's path qualifiers are only \ / : — so such a
			// name clears the prefix filter, clears TryCreateName, and arrives here.
			//
			// Unreachable through the real SystemStorageReadFileSystem, and that is
			// deliberately not the standard applied: TryCreateName was added for an
			// equally-unreachable case, because the never-throws property belongs to
			// this TYPE, not to one seam implementation.
			//
			// ArgumentOutOfRangeException derives from ArgumentException, so a seam
			// reporting a negative length is covered here too — but that case is guarded
			// EXPLICITLY above rather than left to this catch, so the intent is legible
			// rather than incidental.
			//
			// ArgumentNullException also derives from ArgumentException and so is caught
			// here, which masks nothing: every public entry point rejects a null path
			// with ArgumentNullException BEFORE any I/O, so a null can no longer reach
			// this far and a genuine caller bug still surfaces as a throw.
			return (ConfigFileAccess.OpenFailed, null);
		}
	}

	// Skips a leading UTF-8 BOM, ONCE, on the single in-memory snapshot.
	//
	// WHY AT ALL. The writer emits no BOM (encoderShouldEmitUTF8Identifier: false), so
	// no writer-produced file is affected and the mirror rule is untouched. But an
	// editor adding one on "Save As" cost the user their config outright. MEASURED on
	// .NET 10, with BOM + {"schemaVersion":1,"schemaRevision":0}:
	//
	//   envelope -> JsonException: '0xEF' is an invalid start of a value
	//   body     -> JsonException: '0xEF' is an invalid start of a value
	//
	// which is EnvelopeUnparseable -> quarantine-then-defaults, i.e. a destructive
	// repair of a document that is intact and completely unambiguous. After skipping
	// the three bytes both the envelope and the body read normally (measured).
	//
	// SKIPPED ONCE, HERE, AND NOT AT THE TWO PARSE SITES. Slicing separately in the
	// envelope reader and the body binder would be two chances to disagree about which
	// bytes are being read, and the one-snapshot property is what stops a torn
	// observation authorising a write against data nobody read coherently. Re-reading
	// the file is out of the question for the same reason.
	//
	// NO UTF-16, AND NO ENCODING DETECTION — a decision, not an omission. The writer is
	// UTF-8 only, so a UTF-16 file is not something this app can produce, and it is
	// correctly refused (measured: PARSE-REJECTED). Guessing an encoding from leading
	// bytes produces confidently wrong reads, and a confidently wrong read is exactly
	// what licenses a destructive repair here. A BOM-prefixed UTF-8 document is a
	// different case: it is unambiguous, and the preamble is the only thing in the way.
	private static byte[] SkipUtf8Bom(byte[] bytes)
	{
		return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
			? bytes[3..]
			: bytes;
	}

	// Reads ONLY schemaVersion and schemaRevision, without binding the document.
	//
	// BOTH must be present and numeric. A missing property is NOT defaulted, because
	// a verdict computed from an assumed version is exactly the "verdict you could not
	// compute" that Core's EnvelopeUnparseable factory exists to make unrepresentable.
	// Negative values are NOT rejected here — SchemaCompatibilityEvaluator classifies
	// them as Invalid, and that is its decision to make, not this type's.
	private static bool TryReadEnvelope(byte[] bytes, out int schemaVersion, out int schemaRevision)
	{
		schemaVersion = 0;
		schemaRevision = 0;

		try
		{
			// The ReadOnlyMemory<byte> conversion is written out rather than left to
			// overload resolution: byte[] converts implicitly to BOTH
			// ReadOnlyMemory<byte> and ReadOnlySequence<byte>, and JsonDocument.Parse
			// has an overload for each, so a bare Parse(bytes) is ambiguous.
			using JsonDocument document = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes));
			return document.RootElement.ValueKind == JsonValueKind.Object
				&& TryReadInt(document.RootElement, SchemaVersionProperty, out schemaVersion)
				&& TryReadInt(document.RootElement, SchemaRevisionProperty, out schemaRevision);
		}
		catch (JsonException)
		{
			// A zero-byte file, a truncated document and outright garbage all arrive
			// here. Each is an observation; none is an error to propagate.
			//
			// AN EMPTY FILE IS EnvelopeUnparseable, AND THAT IS THE RULING RATHER THAN A
			// GAP — recorded because the obvious "fix" is strictly worse than what it
			// replaces. MEASURED: a 0-byte file, a 3-byte BOM-only file (EF BB BF) and a
			// whitespace-only file all report EnvelopeUnparseable. JsonDocument.Parse on
			// empty input throws with "The input does not contain any JSON tokens", a
			// JsonException, so THIS clause is what converts it; the BOM-only file arrives
			// identically because SkipUtf8Bom hands on an EMPTY array (bytes[3..] of a
			// 3-byte array is empty, never null) and empty parses the same way.
			// Observe_ZeroByteFile_ReportsEnvelopeUnparseableWithNoSchema pins the 0-byte
			// row.
			//
			// WHY IT IS CORRECT. An empty file HAS no envelope, so the schema version
			// cannot be read, and a write is licensed only on a whole-document parse. And
			// the rule that normally argues against reporting a file corrupt does not bite
			// here: what a quarantine would destroy is ZERO BYTES. The file is EMPTY, NOT
			// INTACT, and "never report an INTACT file as corrupt" says nothing about a
			// file with no contents to preserve.
			//
			// THE TRAP, NAMED SO IT IS NOT WALKED INTO: reporting an empty file Absent
			// instead looks tidier and is the DESTRUCTIVE direction. Absent routes to
			// ConfigLoadPlanner.PlanForAbsentFile, which returns CreateDefaultsAndPersist
			// — defaults written with NO QUARANTINE and WITHOUT CONSULTING A BACKUP,
			// because the planner has been told there was nothing to protect.
			// EnvelopeUnparseable instead reaches PlanRepair, which with a promotable
			// candidate returns QuarantineThenRestoreFromBackup and gives the user their
			// settings back. That is not a hypothetical difference: a writer killed after
			// creating the destination but before writing its bytes leaves EXACTLY this
			// file next to a good backup, and only the EnvelopeUnparseable route recovers
			// it.
			return false;
		}
	}

	// Property lookup that matches the BODY reader's tolerance, and REFUSES to guess
	// when a file cannot say what it means.
	//
	// The tolerance first: BodyOptions sets PropertyNameCaseInsensitive, so an envelope
	// reader insisting on exact camelCase would be STRICTER than the binder underneath
	// it and would report files as envelope-unparseable that the store round trips
	// happily. So a single unconventional spelling is still accepted.
	//
	// THE AMBIGUITY TEST IS THE POINT, and it replaces an exact-first lookup that was
	// the source of a measured divergence. TryGetProperty is ordinal, so for
	// {"schemaVersion":1,...,"SCHEMAVERSION":2} it matched the ONE exact spelling,
	// returned 1, and never reached the case-insensitive fallback — while the binder,
	// for which both spellings are the same member, took the last and got 2. A
	// version-2 document was therefore cleared as version 1 / CompletelyCompatible, the
	// planner's STEP 3 hard stop was bypassed, and the next write would have dropped
	// the newer version's unknown members permanently.
	//
	// THIS SCAN IS NOW THE WHOLE MECHANISM, and it is the reason the envelope needs no
	// AllowDuplicateProperties = false (see EnvelopeOptions' removal above for why
	// that flag was actively harmful). It refuses BOTH shapes on its own — two
	// identical spellings AND two spellings differing only in case — because it counts
	// case-insensitive matches instead of asking the framework what a "duplicate" is.
	// MEASURED: all four rows of
	// Observe_DuplicateEnvelopeKeys_AreRefusedRatherThanSilentlyResolved are refused
	// here with default JsonDocumentOptions.
	//
	// WHY A COUNTING SCAN RATHER THAN A LOOKUP, in the framework's own words.
	// JsonElement.TryGetProperty / GetProperty document, for all six overloads:
	// "Property name matching is performed as an ordinal, case-sensitive comparison.
	// If a property is defined multiple times for the same object, the method matches
	// the last such definition."
	// https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonelement.trygetproperty
	//
	// So a lookup silently RESOLVES an ambiguity that this type must refuse: it is
	// ordinal, so it matched the one exact spelling and never saw the other; and where
	// both spellings are identical it takes the last, inventing an answer the document
	// does not unambiguously give. Counting refuses instead, which yields
	// EnvelopeUnparseable — and that is strictly better than clearing a version the
	// document never declared unambiguously. One scan, no early exact-match path, so
	// the reader and the binder can no longer disagree about which value they read.
	private static bool TryReadInt(JsonElement root, string propertyName, out int value)
	{
		// Assigned up front so no early return can leave it unset: TryGetInt32 is
		// short-circuited by the ValueKind test below, so it is not reached on every
		// path out of this method.
		value = 0;

		JsonElement? match = null;
		foreach (JsonProperty property in root.EnumerateObject())
		{
			if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (match is not null)
			{
				// Two spellings of the same envelope property. Which one the file
				// "means" is not knowable, and an invented answer is exactly what
				// EnvelopeUnparseable exists to prevent.
				return false;
			}

			match = property.Value;
		}

		return match is not null
			&& match.Value.ValueKind == JsonValueKind.Number
			&& match.Value.TryGetInt32(out value);
	}

	// Binds the WHOLE document. A null result counts as a failure to bind: a literal
	// "null" payload is valid JSON and parses, but it is not a document.
	private static bool TryBindBody<TDocument>(byte[] bytes, [NotNullWhen(true)] out TDocument? document)
		where TDocument : ConfigDocument
	{
		try
		{
			// Explicit span, for the same overload-ambiguity reason as the envelope
			// reader above.
			document = JsonSerializer.Deserialize<TDocument>(
				new ReadOnlySpan<byte>(bytes),
				BodyOptions);

			if (document is null)
			{
				return false;
			}

			// A NULL COLLECTION ELEMENT, COMPENSATING FOR A DOCUMENTED FRAMEWORK
			// LIMITATION. RespectNullableAnnotations does NOT reach collection ELEMENT
			// types — officially, in the framework team's own words: "we cannot
			// distinguish between List<string> and List<string?> types."
			// https://devblogs.microsoft.com/dotnet/system-text-json-in-dotnet-9/
			//
			// So MEASURED on .NET 10, {"browsers":[null]} BINDS: a non-null
			// BrowsersDocument whose list has Count 1 and whose single element is null.
			// The bind test passed, the file was reported Loaded, and the consumer got a
			// NullReferenceException — which is not in this type's vocabulary, and is
			// the same class of failure the RespectRequiredConstructorParameters flag
			// was added to close one level up.
			//
			// DO NOT DELETE THIS BELIEVING THE FLAGS COVER IT. They do not, and the
			// limitation is documented as intentional rather than as a bug awaiting a
			// fix. BodyUnparseable is the accurate verdict: the body produced no usable
			// document, so it did not parse WHOLE — and the envelope's schema verdict
			// still travels with it, so a newer-version file is still hard-stopped
			// before any repair is considered.
			//
			// GATED ON THE CONCRETE TYPE, DELIBERATELY, and this is the one place where
			// that reads as a weakness and is not. The check named here covers the
			// collection members it names and nothing else, so a DTO nobody extended it
			// for forgoes the protection — a structurally invalid document from such a
			// type is reported LOADED, and the consumer gets a NullReferenceException.
			//
			// THE ALTERNATIVE WAS TRIED AND REVERSED. A reflection walk covered every
			// DTO automatically and, measured, produced a DESTRUCTIVE false positive: it
			// had no [JsonIgnore] exclusion, so an ordinary `[JsonIgnore] ... = null!`
			// member — excluded from serialization precisely because it is NOT file
			// state — made an intact file BodyUnparseable, which licenses
			// quarantine-then-overwrite. It also let two exceptions escape Observe and
			// would have silently lost all protection under a trimmed publish. The full
			// account, including the measured costs so this is not re-litigated on
			// performance grounds, is on HasUnusableCollectionMember.
			//
			// THE SYNC OBLIGATION IS ENFORCED AT BUILD TIME rather than by memory:
			// EveryCollectionMemberOnACoreDocument_IsAccountedFor inventories Core's
			// collection members by reflection IN THE TEST ASSEMBLY and fails the suite
			// the moment one is added or removed. Reflection is the right tool for that
			// question, asked where a wrong answer is a red test rather than a deleted
			// config file.
			return !HasUnusableCollectionMember(document);
		}
		catch (JsonException)
		{
			document = null;
			return false;
		}
		catch (NotSupportedException)
		{
			// System.Text.Json reports some unbindable shapes (an unsupported
			// conversion for a constructor parameter) as NotSupportedException rather
			// than JsonException. Both mean the same thing to this type: the body did
			// not bind.
			document = null;
			return false;
		}
	}

	// Whether a bound document carries a collection member that makes it unusable: the
	// member itself null, or any element null.
	//
	// EXPLICIT, ONE NAMED MEMBER PER ARM, AND THE EXPLICITNESS IS THE SAFETY PROPERTY. A
	// reflection walk stood here for one revision — GetProperties over the bound document's
	// type, then member.GetValue(document) — and it was REVERSED. The reasoning is recorded
	// because the reflection version reads as the more thorough design and is the more
	// dangerous one.
	//
	// WHAT THE REVERSAL BUYS BACK. This type's entire safety case is STRUCTURAL: it reaches
	// the filesystem only through IStorageReadFileSystem, which declares no write member, so
	// whatever bug it contains cannot be a bug that destroys a user's configuration.
	// member.GetValue(document) executes ARBITRARY DTO CODE inside that component, so the
	// guarantee stopped being structural and became conditional on every present and future
	// getter being pure. A switch over named members executes only getters this repository
	// declares and a reviewer can see at the call site.
	//
	// THREE DEFECTS WERE MEASURED, AND THEY ARE NOT THE SAME KIND OF FAILURE. TWO were
	// ESCAPES. THE FIRST WAS A DESTRUCTIVE FALSE POSITIVE, which is strictly worse, and
	// conflating the two teaches the exact inverse of the distinction this component rests
	// on. AN ESCAPE WRITES NOTHING: it crosses the boundary loudly and the user's file is
	// left untouched, which is why the ObjectDisposedException ruling above chose an escape
	// deliberately. A FALSE POSITIVE reports an INTACT file as corrupt, and BodyUnparseable
	// licenses quarantine-then-overwrite — so it DESTROYS the user's configuration. The
	// first item below is therefore NOT an escape, and must not be called one:
	//
	//   A DESTRUCTIVE FALSE POSITIVE — a [JsonIgnore] collection member left null -> the walk
	//     had no [JsonIgnore] exclusion, so it inspected members the BINDER NEVER BOUND. A
	//     `[JsonIgnore] IReadOnlyList<T> Cache { get; init; } = null!;` is an ordinary,
	//     correct DTO member — excluded from serialization precisely BECAUSE it is not file
	//     state — and the guard returned true for it. That is BodyUnparseable on an intact
	//     file, i.e. quarantine-then-overwrite of data nobody found fault with. NOTHING
	//     ESCAPED HERE; the user's config was destroyed instead, which is the worst outcome
	//     available on this path.
	//   AN ESCAPE — a lazy/computed IEnumerable whose enumeration throws -> the element loop
	//     sat OUTSIDE the try, and TryBindBody catches only JsonException and
	//     NotSupportedException. Measured: InvalidOperationException escaped Observe
	//     outright, breaking the never-throws vocabulary. Loud, and it wrote nothing.
	//   AN ESCAPE — a SET-ONLY property -> GetProperties returns write-only properties,
	//     GetValue on one throws ArgumentException, and the catch handled only
	//     TargetInvocationException. It escaped too, and likewise wrote nothing.
	//
	// AND A ONE-SHOT SEQUENCE WAS DRAINED by the guard's own enumeration, so the consumer
	// received an empty one — the guard changing what it observed.
	//
	// THE TRIMMING HAZARD IS THE QUIET ONE. No trimming annotation exists anywhere in this
	// repository (PublishTrimmed, PublishAot, IsAotCompatible and EnableTrimAnalyzer are all
	// unset; there is no [DynamicallyAccessedMembers] in src), so under a trimmed publish
	// Type.GetProperties() could return a TRIMMED set, the guard would return false for every
	// document, and the protection would vanish with no warning and NO FAILING TEST.
	//
	// THE OBJECTION TO THIS SHAPE IS REAL AND IS ANSWERED ELSEWHERE. An explicit check falls
	// silently out of sync: add a collection member to a Core DTO and this switch covers
	// nothing, which reports a structurally invalid document as LOADED. That objection is met
	// at BUILD TIME by the tripwire test EveryCollectionMemberOnACoreDocument_IsAccountedFor,
	// which inventories Core's collection members by reflection — IN THE TEST, where a
	// failure is loud, harmless and lands the editor on the exact obligation — and fails the
	// suite the moment one is added or removed. Reflection is the right tool for that
	// question; it is the wrong tool on a read path that licenses a destructive repair.
	//
	// NOT A PERFORMANCE DECISION, so nobody re-litigates it as one. Measured: cached
	// discovery cost 0.0151 µs per call and uncached 0.300 µs, against a 300 ms warm target.
	// Reflection was never bought for speed. It was bought for AUTOMATISM, and the automatism
	// cost a destructive false positive.
	//
	// A VIRTUAL "IsStructurallyUsable" ON ConfigDocument WOULD ALSO WORK AND WAS REJECTED
	// HERE: it would put behaviour in Core, which this slice is not permitted to change
	// beyond comments, and it would again be something each new DTO must remember to
	// override. If Core is ever opened for this, that design is worth revisiting — it would
	// let each DTO state its own invariants instead of having them inferred here, and it
	// would move the obligation onto the type that owns the invariant.
	//
	// A DOCUMENT TYPE THIS SWITCH DOES NOT NAME FALLS TO `false`, WHICH IS THE SAFE
	// DIRECTION, and that asymmetry is the whole argument. A finding is reported as
	// BodyUnparseable, the verdict that licenses quarantine-then-overwrite, so a FALSE
	// POSITIVE is destructive while a false negative merely hands the consumer a
	// NullReferenceException. Observe<TDocument> constrains TDocument to ConfigDocument and
	// imposes NO assembly constraint, so a subtype declared outside Core reaches here and
	// gets no null-collection protection — a gap that now fails safe, where the reflection
	// walk's equivalent gap ([JsonIgnore]) failed destructively. AppDocument and
	// SettingsDocument declare no collection member, so they need no arm; the tripwire pins
	// that.
	private static bool HasUnusableCollectionMember(ConfigDocument document)
	{
		return document switch
		{
			BrowsersDocument browsers => HasNullMemberOrElement(browsers.Browsers),
			ActivationHistoryDocument history => HasNullMemberOrElement(history.Entries),
			RulesDocument rules => HasNullMemberOrElement(rules.Rules),
			_ => false,
		};
	}

	// ONE LEVEL DEEP, matching exactly what the concrete-type check before it did: the
	// document's OWN collection member and its elements. It does not walk INTO an element to
	// check that element's own collection members. Stated rather than left to be discovered,
	// because a nested DTO growing a collection member would need the same treatment and
	// would not get it here.
	//
	// MEASURED on .NET 10 with this component's exact BodyOptions, which is what makes this
	// necessary rather than defensive:
	//
	//   member `= null!`, absent from the JSON  -> BINDS with the member NULL
	//   member `[null]` in the JSON             -> BINDS with a null ELEMENT
	//
	// The first is what the recorded "every additive member must carry a default" rule
	// invites, because `= null!` is the idiomatic way to satisfy the compiler. The second is
	// a documented framework limitation: RespectNullableAnnotations cannot reach collection
	// ELEMENT types, in the framework team's own words ("we cannot distinguish between
	// List<string> and List<string?> types").
	//
	// THE TEST IS EXACTLY "null member or null element" AND NOTHING MORE INVENTIVE, because
	// an empty collection is a perfectly ordinary document (no browsers detected) and must
	// bind — pinned by a test of its own. Indexed rather than foreach'd: the value came from
	// System.Text.Json and is a List<T>, so Count and the indexer are the whole cost, no
	// enumerator is allocated, and nothing here can drain a sequence it was only asked to
	// inspect. A VALUE-element collection needs no scan at all and gets no arm above — an int
	// can never be null — which is why the constraint is `class`.
	private static bool HasNullMemberOrElement<TElement>(IReadOnlyList<TElement>? member)
		where TElement : class
	{
		if (member is null)
		{
			return true;
		}

		for (int index = 0; index < member.Count; index++)
		{
			if (member[index] is null)
			{
				return true;
			}
		}

		return false;
	}

	// Enumeration itself can fail — the directory can be removed, or its ACL can deny
	// listing, between the existence check and the walk. Neither is an error worth
	// propagating: "no candidates could be listed" is the honest observation, and it
	// is what the pure layer already handles as "no backup found". Materialized inside
	// the try because Directory.EnumerateFiles is lazy, so the failure would otherwise
	// surface at the first MoveNext, outside any guard here.
	//
	// THE .ToList() IS LOAD-BEARING AND UNBOUNDED, BOTH DELIBERATELY. It is what puts the
	// enumeration inside the try above, so do not make it lazy to save the allocation —
	// that moves an ordinary removed-directory IOException out to the caller's foreach,
	// where nothing guards it. And it is unbounded, so a seam yielding an unbounded
	// sequence exhausts the heap here and OutOfMemoryException escapes
	// ListBackupCandidates and ObserveNthNewestBackup. That is a KNOWN, ACCEPTED escape,
	// not an oversight: the byte ceiling caps one file's CONTENT and has never capped
	// this listing. The header records why no entry bound is imposed — chiefly that
	// candidates are sorted only AFTER listing, so truncating would drop arbitrary
	// entries and could discard the NEWEST backup, which costs the user more than a loud
	// failure that writes nothing.
	private IEnumerable<string> EnumerateQuietly(string directory)
	{
		try
		{
			return [.. _fileSystem.EnumerateFiles(directory)];
		}
		catch (IOException)
		{
			return [];
		}
		catch (UnauthorizedAccessException)
		{
			return [];
		}
	}

	// A stat that cannot throw. A failed stat must not abort the listing — the
	// candidate still has to sort somewhere deterministic — so the failure is
	// returned rather than propagated.
	//
	// null means THE STAT ITSELF FAILED (denied ACL, I/O error), which is NOT the
	// same fact as the framework reporting its missing-file sentinel. Collapsing the
	// two would label an existing-but-unreadable file Vanished, and a caller reading
	// Vanished is entitled to give up on that candidate for good. The never-throw
	// property is kept; the distinction is carried in the return value instead.
	private DateTime? StatQuietly(string path)
	{
		try
		{
			return _fileSystem.GetLastWriteTimeUtc(path);
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
		catch (ArgumentException)
		{
			// NEITHER OF THE TWO ABOVE, so without this clause it ESCAPED — and an
			// escape from here aborts the whole listing, not just one candidate.
			// MEASURED on .NET 10: File.GetLastWriteTimeUtc throws ArgumentException for
			// a path containing a NUL character ("Null character in path") and for an
			// empty path. Reachable because Path.GetFileName keeps a NUL intact
			// (measured) and BackupName's path qualifiers are only \ / :, so such a name
			// clears both filters and arrives here.
			//
			// A null stamp is the honest report: it means THE STAT ITSELF FAILED, which
			// is what StampUnavailable exists to say. It is deliberately not Vanished —
			// nothing here learned that the file is gone.
			return null;
		}
	}
}
