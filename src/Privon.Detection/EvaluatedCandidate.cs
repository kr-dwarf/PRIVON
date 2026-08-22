using Privon.Core;

namespace Privon.Detection;

/// <summary>
/// Phase 2R.4 -- additive wrapper around an already-produced <see cref="DetectionCandidate"/>,
/// carrying the result of Exception/TrustedPublic evaluation. Deliberately does NOT add a
/// field to <see cref="DetectionCandidate"/> itself: that type represents purely structural
/// detection output (Phase 2A-2P/Q's responsibility boundary), and every field on it stays
/// meaningless until a later pipeline stage runs. Wrapping instead keeps that boundary intact,
/// the same way <see cref="DetectionResult"/> wraps candidates rather than mutating them.
///
/// No separate "reason" enum (e.g. NONE/AMBIGUOUS_EXCEPTION/TRUSTED_PUBLIC) is carried here --
/// see the Phase 2R STEP 1 report's EVALUATION_OUTPUT_MODEL analysis: <see cref="TrustState"/>
/// together with <c>Candidate.RiskLevel</c> already losslessly encodes which mechanism could
/// have produced a Trusted result (Level1+Trusted implies AmbiguousException; Level2+Trusted
/// implies TrustedPublic; Level3 can never be Trusted).
///
/// DETECTION_CANONICAL_DIAGNOSTIC_SURFACE (Phase 3B STEP3.1): <see cref="ToString"/> is
/// explicitly overridden to project <see cref="Candidate"/>'s own safe fields directly
/// (PiiType/Span/RiskLevel/Confidence/DetectorName) plus <see cref="TrustState"/> -- it never
/// calls <c>Candidate.ToString()</c>, so this type's own safety does not depend on
/// <see cref="DetectionCandidate"/>'s override continuing to exist/stay safe in the future
/// (same self-contained discipline as <see cref="DetectionCandidate"/> itself not delegating to
/// <see cref="CanonicalValue"/>'s override).
/// </summary>
public sealed record EvaluatedCandidate(DetectionCandidate Candidate, TrustState TrustState)
{
    public override string ToString() =>
        $"{nameof(EvaluatedCandidate)} {{ PiiType = {Candidate.PiiType}, Span = {Candidate.Span}, " +
        $"RiskLevel = {Candidate.RiskLevel}, Confidence = {Candidate.Confidence}, " +
        $"DetectorName = {Candidate.DetectorName}, {nameof(TrustState)} = {TrustState} }}";
}
