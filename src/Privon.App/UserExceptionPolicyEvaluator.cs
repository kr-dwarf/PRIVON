using Privon.Detection;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3B -- the App-owned "user exception" policy stage inserted into
/// <see cref="ClipboardPrivacyProcessor.ProcessWithAssignments"/>, after
/// <see cref="CategoryPolicyEvaluator.Apply"/> and before
/// <c>Privon.Detection.AliasAssigner.Assign</c>. Identical shape and safety contract to
/// <see cref="CategoryPolicyEvaluator"/> (see that type's own doc for the full reasoning) --
/// deliberately NOT merged with it or with <c>Privon.Detection.ExceptionTrustedEvaluator</c>'s
/// existing Level1 <c>Exceptions</c>/Level2 <c>TrustedPublicInfo</c> concepts, which remain a
/// separate, earlier, unrelated pipeline stage this Gate does not touch.
///
/// CONTRACT (frozen for this Gate, identical to CategoryPolicyEvaluator's own): only an
/// already-<see cref="CandidateDisposition.Protect"/> entry may ever be transformed, and only
/// ever DOWNGRADED to <see cref="CandidateDisposition.Bypass"/> -- never upgraded, never left as
/// some other disposition. <see cref="CandidateDisposition.NeedsDecision"/> and
/// <see cref="CandidateDisposition.Bypass"/> entries are returned completely untouched (same
/// object reference). This is what makes <see cref="Privon.Core.RiskLevel.Level3"/> priority
/// automatic here too -- a Level3 candidate can never arrive as <c>Protect</c> in the first
/// place (forced to <c>NeedsDecision</c> upstream by <c>CandidatePolicyEvaluator</c>, and left
/// untouched by <see cref="CategoryPolicyEvaluator"/>, which runs immediately before this stage)
/// -- so a user exception can never weaken Level3, structurally, not by special-casing RiskLevel
/// here at all.
///
/// IDENTITY: exact <c>(PiiType, CanonicalValue)</c> struct equality against the caller-supplied
/// exception set -- no normalization, no prefix/substring/fuzzy matching. Detection's own
/// canonicalizers remain the sole normalization owner; this evaluator trusts whatever canonical
/// form Detection already produced for the candidate and whatever canonical form the exception
/// set already carries (itself produced by Detection at the time the exception was registered --
/// see <see cref="UserExceptionService.Add"/>).
///
/// VALUE_BASED_NOT_SPAN_BASED: a stored exception applies to every matching candidate
/// independently -- if the same canonical value appears twice in one attempt's candidate list,
/// both are transformed, since each is evaluated on its own <c>(PiiType, CanonicalValue)</c>,
/// never on span identity.
/// </summary>
// [PRIVON-AI-HANDOFF]
// ROLE: App policy stage after CategoryPolicy and before alias assignment.
// TRUTH: Exact PiiType plus CanonicalValue equality may change only Protect to Bypass.
// FROZEN: NeedsDecision and existing Bypass remain unchanged, so Level3 cannot be weakened.
// DO_NOT: Normalize, fuzzy-match, or merge this policy with older trust/exception concepts.
// NAVIGATE: ClipboardPrivacyProcessor owns ordering; UserExceptionProvider supplies persisted values.
internal static class UserExceptionPolicyEvaluator
{
    public static IReadOnlyList<CandidatePolicyDecision> Apply(
        IReadOnlyList<CandidatePolicyDecision> decisions, IReadOnlyList<UserExceptionValue> exceptions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(exceptions);

        if (exceptions.Count == 0)
            return decisions;

        var results = new List<CandidatePolicyDecision>(decisions.Count);
        foreach (var decision in decisions)
        {
            results.Add(Decide(decision, exceptions));
        }

        return results;
    }

    private static CandidatePolicyDecision Decide(CandidatePolicyDecision decision, IReadOnlyList<UserExceptionValue> exceptions)
    {
        // NeedsDecision and Bypass are NEVER altered -- see class doc. Same reference returned,
        // not a `with`-cloned copy, so the guarantee is verifiable by reference equality.
        if (decision.Disposition != CandidateDisposition.Protect)
            return decision;

        var candidate = decision.Candidate.Candidate;
        foreach (var exception in exceptions)
        {
            if (exception.PiiType == candidate.PiiType && exception.CanonicalValue == candidate.Canonical)
                return decision with { Disposition = CandidateDisposition.Bypass };
        }

        return decision;
    }
}
