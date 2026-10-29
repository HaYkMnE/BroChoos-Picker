namespace BroChoos.Core.Storage;

// app.json — 0A-5 carries ONLY the schema envelope (D-064). No app-scope fields
// exist yet; they land here as additive-0B fields without changing schemaVersion.
//
// EVERY ADDITIVE MEMBER ADDED BELOW MUST CARRY AN EXPLICIT DEFAULT, AND THAT DEFAULT
// MUST ITSELF BE A VALID NON-NULL INSTANCE — for a collection, an EMPTY collection
// (`= []`), NEVER `= null!`. Both halves are load-bearing and they fail in opposite
// directions (measured on .NET 10 against the read path's binder options):
//
//   no default at all -> JsonException "missing required properties", which lands on
//                        BodyUnparseable and licenses quarantine-then-overwrite of the
//                        intact file an EARLIER revision wrote. D-070 promises additive
//                        revisions stay readable; this breaks that destructively.
//   `= null!`         -> BINDS with the member NULL, so a structurally broken document
//                        is reported Loaded and the consumer gets a
//                        NullReferenceException. `= null!` satisfies the LETTER of
//                        "carry a default" and defeats its purpose, which is exactly
//                        why it is named here.
//
// The note is repeated on this type rather than only on ConfigFileObserver's BodyOptions
// (which carries the full reasoning) because THIS is where the next member gets added.
// A `= null!` that slips through is caught by the read path's post-bind check ONLY IF THAT
// CHECK NAMES THE MEMBER. ConfigFileObserver.HasUnusableCollectionMember is a switch with
// one arm per NAMED document member; AppDocument has no arm today because it declares no
// collection member, and a DTO the switch does not name gets NO protection — its
// structurally broken document is reported Loaded and the consumer gets a
// NullReferenceException. So adding a collection member below OBLIGES you to add an arm
// there in the same change. That obligation is enforced at build time, not by memory: the
// tripwire test EveryCollectionMemberOnACoreDocument_IsAccountedFor inventories Core's
// collection members and fails the suite the moment one is added or removed. A reflection
// walk that covered every bound DTO automatically stood in that switch's place for one
// revision and was REVERSED — it reported an INTACT file as BodyUnparseable, which licenses
// quarantine-then-overwrite. Do not restore the automatism; see ConfigFileObserver for the
// measured account.
public sealed record AppDocument : ConfigDocument;
