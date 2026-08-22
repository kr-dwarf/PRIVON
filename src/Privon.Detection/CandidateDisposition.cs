namespace Privon.Detection;

/// <summary>
/// Phase 2S.2 -- the candidate-level output of <see cref="CandidatePolicyEvaluator"/>: whether
/// one specific <see cref="EvaluatedCandidate"/> should be protected (aliased), bypassed, or
/// held for a decision. This is deliberately a different abstraction level from
/// <see cref="Privon.Core.ProtectionState"/> -- ProtectionState describes the whole
/// composition/runtime's current state (Scanning/NeedsDecision/Blocked/Verified/...), while
/// this enum describes one candidate's own disposition. The two are never used
/// interchangeably; aggregating many CandidateDisposition values into an overall
/// ProtectionState is a later runtime-aggregation phase's job, not this one's.
///
/// Declared with <see cref="NeedsDecision"/> first so that <c>default(CandidateDisposition)</c>
/// is never mistaken for either an active decision (<see cref="Protect"/>) or, more
/// importantly, <see cref="Bypass"/> -- the one outcome that lets original text through
/// unprotected. A caller that forgets to set this field ends up deferring, never leaking.
/// </summary>
public enum CandidateDisposition
{
    NeedsDecision,
    Protect,
    Bypass,
}
