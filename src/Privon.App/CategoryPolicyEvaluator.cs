using Privon.Detection;
using Privon.Storage;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3A -- the App-owned "category policy" stage inserted into
/// <see cref="ClipboardPrivacyProcessor.ProcessWithAssignments"/>, between the existing
/// <c>Privon.Detection.CandidatePolicyEvaluator.Evaluate</c> (BASE policy: RiskLevel/Confidence/
/// TrustState only) and <c>Privon.Detection.AliasAssigner.Assign</c>. Deliberately lives in
/// <c>Privon.App</c>, not <c>Privon.Detection</c> -- <see cref="ProtectionCategorySettings"/> is
/// user-mutable runtime preference state, categorically different from the built-in domain facts
/// <c>CandidatePolicyEvaluator</c> consumes; keeping this stage in App means
/// <c>Privon.Detection</c> gains no new reference to <c>Privon.Storage</c> and stays exactly what
/// it already is (factual classification), never touched or modified by this Gate.
///
/// CONTRACT (frozen for this Gate -- HISTORICAL v0.2.1 text, kept verbatim as evidence; the CURRENT behaviour, including the one
/// place it was extended, is in the "CURRENT BEHAVIOUR (0.4.0)" section below): only an already-<see cref="CandidateDisposition.Protect"/>
/// entry may ever be transformed, and only ever DOWNGRADED to
/// <see cref="CandidateDisposition.Bypass"/> -- never upgraded, never left as some other
/// disposition. <see cref="CandidateDisposition.NeedsDecision"/> and
/// <see cref="CandidateDisposition.Bypass"/> entries are returned completely untouched (same
/// object reference, not merely equal) -- this is what makes
/// <see cref="Privon.Core.RiskLevel.Level3"/> priority automatic rather than something this stage
/// has to special-case: <c>CandidatePolicyEvaluator</c> already forces every Level3 candidate to
/// <c>NeedsDecision</c> unconditionally, before <c>TrustState</c> is even inspected, so a Level3
/// candidate can never arrive here as <c>Protect</c> in the first place. A category setting can
/// therefore never weaken Level3 protection -- there is nothing for it to touch.
///
/// PIITYPE_MAPPING (frozen for this Gate, HISTORICAL v0.2.1 -- see <see cref="ProtectionCategorySettings"/>'s own
/// SCHEMA_SUPPORTED_DETECTOR_NOT_YET_IMPLEMENTED doc): only <see cref="PiiType.Phone"/>/
/// <see cref="PiiType.Email"/> currently map to a category toggle
/// (<see cref="ProtectionCategorySettings.PhoneEnabled"/>/<see cref="ProtectionCategorySettings.EmailEnabled"/>).
/// <see cref="ProtectionCategorySettings.NameEnabled"/>/<see cref="ProtectionCategorySettings.AddressEnabled"/>/
/// <see cref="ProtectionCategorySettings.CompanyEnabled"/> have no PiiType to gate yet -- no
/// behavioral branch for them exists here, deliberately, until a detector for one of them exists.
/// Every OTHER existing PiiType (<see cref="PiiType.IpAddress"/>/<see cref="PiiType.MacAddress"/>/
/// <see cref="PiiType.GpsCoordinate"/>/the three Level3 types) is likewise never mapped to any of
/// the 5 category toggles -- not an oversight, an explicit product-contract scope boundary (the
/// product contract only names Name/Phone/Email/Address/Company). A candidate of any of those
/// other types is returned completely untouched regardless of settings.
///
/// CURRENT BEHAVIOUR (0.4.0, what the code below does today). The sentences above about Name/Address having "no PiiType to gate yet"
/// and about NeedsDecision being "completely untouched" described v0.2.1, when no Name or Address detector existed; they are no longer
/// the whole truth. The 0.4.0 U.S. pack added exactly two things, both required by D2.1's policy table ("English personal name ->
/// existing Name category", "U.S. postal address -> existing Address category") and by the frozen D3A row POL-L1-CATOFF:
///   * <see cref="PiiType.Name"/> -> <see cref="ProtectionCategorySettings.NameEnabled"/> and
///     <see cref="PiiType.Address"/> -> <see cref="ProtectionCategorySettings.AddressEnabled"/> (a Protect becomes Bypass when OFF);
///   * for those two types ONLY, a Level-1 NeedsDecision becomes Bypass when its own category is OFF (a Level-1 name that would
///     otherwise wait for a decision is allowed to stay as typed -- no write, never reported as a verified protection).
/// Unchanged: <see cref="PiiType.Phone"/>/<see cref="PiiType.Email"/> keep the v0.2.1 contract exactly (a Phone or Email NeedsDecision
/// is returned untouched, same reference, whatever the toggle); every other type is untouched; Level 3 is never touched, because it is
/// always NeedsDecision and neither Name nor Address nor any other mapped type is Level 3 (the Level-1 branch also tests the level);
/// <see cref="ProtectionCategorySettings.CompanyEnabled"/> still maps to nothing. U.S. Address is Level 2 only (D2.2), so the
/// Level-1 branch is exercised by Name; Address has no direct frozen Level-1 case.
/// </summary>
// [PRIVON-AI-HANDOFF]
// ROLE: App-owned user-preference policy stage after base CandidatePolicy.
// TRUTH: A disabled live category changes Protect to Bypass for Phone/Email/Name/Address; ALSO a Level-1 NeedsDecision to Bypass, for Name/Address only (0.4.0, POL-L1-CATOFF). Phone/Email NeedsDecision, Level 3 and all other types are unchanged.
// FROZEN: Level3 arrives as NeedsDecision from upstream policy and cannot be weakened here. The v0.2.1 Gate 3A text above is historical evidence; the 0.4.0 extension is confined to Name/Address Level 1.
// DO_NOT: Move mutable preference policy into Detection or add normalization/trust semantics here.
// NAVIGATE: ClipboardPrivacyProcessor owns stage ordering; ProtectionCategorySettings owns the preference shape.
internal static class CategoryPolicyEvaluator
{
    public static IReadOnlyList<CandidatePolicyDecision> Apply(
        IReadOnlyList<CandidatePolicyDecision> baseDecisions, ProtectionCategorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(baseDecisions);
        ArgumentNullException.ThrowIfNull(settings);

        var results = new List<CandidatePolicyDecision>(baseDecisions.Count);
        foreach (var decision in baseDecisions)
        {
            results.Add(Decide(decision, settings));
        }

        return results;
    }

    private static bool IsCategoryEnabled(PiiType type, ProtectionCategorySettings settings) => type switch
    {
        PiiType.Phone => settings.PhoneEnabled,
        PiiType.Email => settings.EmailEnabled,
        PiiType.Name => settings.NameEnabled,
        PiiType.Address => settings.AddressEnabled,
        // Company and every other PiiType: this category filter has no opinion, never "protect nothing."
        _ => true,
    };

    private static CandidatePolicyDecision Decide(CandidatePolicyDecision decision, ProtectionCategorySettings settings)
    {
        var candidate = decision.Candidate.Candidate;

        // U.S. NAME / ADDRESS (0.4.0 D2.1 policy table: "English personal name -> existing Name category", "U.S. postal address ->
        // existing Address category"; the mapping the Gate 3A note above deferred "until a detector for one of them exists"). A
        // Level-1 candidate that is still awaiting a decision is suppressed too when its category is OFF -- the frozen D3A row
        // POL-L1-CATOFF. Phone / Email keep the Gate 3A contract below untouched, and Level 3 is not a mapped type.
        if (decision.Disposition == CandidateDisposition.NeedsDecision
            && candidate.RiskLevel == Core.RiskLevel.Level1
            && candidate.PiiType is PiiType.Name or PiiType.Address
            && !IsCategoryEnabled(candidate.PiiType, settings))
            return decision with { Disposition = CandidateDisposition.Bypass };

        // NeedsDecision and Bypass are NEVER altered otherwise -- see class doc. Returning the SAME
        // reference (not a `with`-cloned copy) makes that guarantee independently verifiable by
        // reference equality, not merely by re-inspecting field values.
        if (decision.Disposition != CandidateDisposition.Protect)
            return decision;

        return IsCategoryEnabled(candidate.PiiType, settings) ? decision : decision with { Disposition = CandidateDisposition.Bypass };
    }
}
