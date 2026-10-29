namespace BroChoos.Core.Storage;

// browsers.json — schema envelope plus the detected browser list. 0A-5 persists
// only the fields declared on BrowserDefinition; nothing else.
//
// EVERY ADDITIVE MEMBER ADDED BELOW MUST CARRY AN EXPLICIT DEFAULT, AND THAT DEFAULT
// MUST ITSELF BE A VALID NON-NULL INSTANCE — for a collection, an EMPTY collection
// (`= []`), NEVER `= null!`. Measured on .NET 10 against the read path's binder options:
//
//   no default at all -> JsonException "missing required properties" -> BodyUnparseable,
//                        which licenses quarantine-then-overwrite of the intact file an
//                        EARLIER revision wrote. That is the destructive direction, and
//                        it breaks D-070's promise that additive revisions stay readable.
//   `= null!`         -> BINDS with the member NULL, so a structurally broken document
//                        is reported Loaded and the consumer gets a
//                        NullReferenceException. It satisfies the LETTER of "carry a
//                        default" while defeating the purpose, which is why it is named.
//
// Browsers itself is a POSITIONAL PARAMETER WITH NO DEFAULT, deliberately, and that is
// not an exception to the rule above. The rule governs members added LATER, which must
// stay optional so an older revision's file still binds. This one is the document's
// entire content: a browsers.json with no browsers list is not a browsers.json, and the
// binder making it mandatory (RespectRequiredConstructorParameters) is what stopped
// {"schemaVersion":1,"schemaRevision":0} binding to a BrowsersDocument whose list was
// null and being reported Loaded. See ConfigFileObserver's BodyOptions for that history.
public sealed record BrowsersDocument(
	IReadOnlyList<BrowserDefinition> Browsers) : ConfigDocument;
