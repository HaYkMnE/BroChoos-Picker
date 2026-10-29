using BroChoos.Core.Routing;

namespace BroChoos.Core.Storage;

// rules.json — W7.1's generation-bearing rules document. Rules SUGGEST; they never open
// anything by themselves (spec 3.7 / D-v3.5-363, D-v3.5-375), so this document is data
// only: it carries no launch dependency and grants no authority to act.
//
// WHY A NEW TYPE RATHER THAN REUSING `RuleSetDocument`. `RuleSetDocument` is the
// evaluator's INPUT value: a generation plus a rule list, deliberately free of storage
// concerns. This type is the STORAGE document, and persistence requires the schema
// envelope and the ConfigDocument base so the EXISTING W3 generation/CAS machinery
// (ConfigFileObserver + ConfigLoadPlanner + GenerationMutationCoordinator +
// AtomicJsonFileStore) can read and write it unchanged. ToRuleSet is the one-way
// projection from what was persisted to what the evaluator consumes; the two are kept
// separate so a change to storage never silently reshapes rule evaluation.
//
// THE SAME ADDITIVE-MEMBER RULE EVERY CONFIG DOCUMENT CARRIES APPLIES HERE. A member
// added to this type later MUST carry an explicit default that is itself a valid
// non-null instance (`= []` for a collection, never `= null!`), because the read path's
// binder (RespectRequiredConstructorParameters) makes a no-default positional parameter
// mandatory in the JSON — an older revision's file would then fail to bind and land on
// BodyUnparseable, which licenses quarantine-then-overwrite of intact data. `Rules`
// itself is deliberately such a positional parameter with no default: a rules.json with
// no rules list is not a rules.json, mirroring BrowsersDocument.Browsers.
//
// `Rules` IS A COLLECTION MEMBER, so adding it OBLIGES the same two edits the tripwire
// names: an arm in ConfigFileObserver.HasUnusableCollectionMember and
// "RulesDocument.Rules" in EveryCollectionMemberOnACoreDocument_IsAccountedFor, plus the
// null-member/null-element tests beside Observe_BrowsersDocumentWithNullBrowsersMember.
public sealed record RulesDocument(
	IReadOnlyList<RoutingRule> Rules) : ConfigDocument
{
	public RuleSetDocument ToRuleSet()
	{
		return new RuleSetDocument(Generation, Rules);
	}
}
