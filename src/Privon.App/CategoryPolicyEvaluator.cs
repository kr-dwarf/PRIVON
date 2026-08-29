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
/// CONTRACT (frozen for this Gate): only an already-<see cref="CandidateDisposition.Protect"/>
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
/// PIITYPE_MAPPING (frozen for this Gate -- see <see cref="ProtectionCategorySettings"/>'s own
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
/// </summary>
// [PRIVON-AI-HANDOFF]
// ROLE: App-owned user-preference policy stage after base CandidatePolicy.
// TRUTH: A disabled live category may change only Protect to Bypass; other dispositions and types are unchanged.
// FROZEN: Level3 arrives as NeedsDecision from upstream policy and cannot be weakened here.
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

    private static CandidatePolicyDecision Decide(CandidatePolicyDecision decision, ProtectionCategorySettings settings)
    {
        // NeedsDecision and Bypass are NEVER altered -- see class doc. Returning the SAME
        // reference (not a `with`-cloned copy) makes that guarantee independently verifiable by
        // reference equality, not merely by re-inspecting field values.
        if (decision.Disposition != CandidateDisposition.Protect)
            return decision;

        bool categoryEnabled = decision.Candidate.Candidate.PiiType switch
        {
            PiiType.Phone => settings.PhoneEnabled,
            PiiType.Email => settings.EmailEnabled,
            // No PiiType mapping exists yet for Name/Address/Company (see class doc), and every
            // other PiiType is out of this Gate's product-contract scope -- both cases mean "this
            // category filter has no opinion," never "protect nothing."
            _ => true,
        };

        return categoryEnabled ? decision : decision with { Disposition = CandidateDisposition.Bypass };
    }
}
