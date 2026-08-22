namespace Privon.Detection;

/// <summary>
/// Phase 2S.2 -- additive wrapper pairing an already-evaluated candidate with its policy
/// disposition. Neither <see cref="EvaluatedCandidate"/> nor the <see cref="DetectionCandidate"/>
/// it wraps is modified -- same "wrap, never mutate" pattern as
/// <see cref="EvaluatedCandidate"/> itself wrapping <see cref="DetectionCandidate"/>.
///
/// DETECTION_CANONICAL_DIAGNOSTIC_SURFACE (Phase 3B STEP3.1): <see cref="ToString"/> is
/// explicitly overridden to project the underlying <see cref="DetectionCandidate"/>'s own safe
/// fields directly, plus <see cref="Privon.Core.TrustState"/> and <see cref="Disposition"/> --
/// never delegating to <c>Candidate.ToString()</c>, self-contained like every other type
/// hardened in this phase.
/// </summary>
public sealed record CandidatePolicyDecision(EvaluatedCandidate Candidate, CandidateDisposition Disposition)
{
    public override string ToString() =>
        $"{nameof(CandidatePolicyDecision)} {{ PiiType = {Candidate.Candidate.PiiType}, Span = {Candidate.Candidate.Span}, " +
        $"RiskLevel = {Candidate.Candidate.RiskLevel}, Confidence = {Candidate.Candidate.Confidence}, " +
        $"DetectorName = {Candidate.Candidate.DetectorName}, TrustState = {Candidate.TrustState}, " +
        $"{nameof(Disposition)} = {Disposition} }}";
}
