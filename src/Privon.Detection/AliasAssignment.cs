namespace Privon.Detection;

/// <summary>
/// Phase 2T.1 -- additive wrapper pairing a <see cref="CandidatePolicyDecision"/> with the
/// alias it was assigned, if any. <see cref="Alias"/> is non-null if and only if
/// <c>Decision.Disposition == CandidateDisposition.Protect</c> -- Bypass and NeedsDecision
/// candidates always carry a null Alias. Neither <see cref="CandidatePolicyDecision"/> nor
/// anything it wraps is modified.
///
/// DETECTION_CANONICAL_DIAGNOSTIC_SURFACE (Phase 3B STEP3.1): <see cref="ToString"/> is
/// explicitly overridden to project the underlying <see cref="DetectionCandidate"/>'s own safe
/// fields directly (never delegating through <c>Decision.ToString()</c>), plus TrustState,
/// Disposition, and <see cref="Alias"/>. <see cref="AliasToken"/> is safe to include as-is --
/// its own <c>Value</c> is only ever the generated display token text (e.g. "[전화번호1]"),
/// never a copy of the original canonical PII value (see <see cref="AliasToken"/>'s own doc) --
/// so this is the one field in the whole hardened DTO graph that is safe to print by delegating
/// to its own default formatting.
/// </summary>
public sealed record AliasAssignment(CandidatePolicyDecision Decision, AliasToken? Alias)
{
    public override string ToString() =>
        $"{nameof(AliasAssignment)} {{ PiiType = {Decision.Candidate.Candidate.PiiType}, Span = {Decision.Candidate.Candidate.Span}, " +
        $"RiskLevel = {Decision.Candidate.Candidate.RiskLevel}, Confidence = {Decision.Candidate.Candidate.Confidence}, " +
        $"DetectorName = {Decision.Candidate.Candidate.DetectorName}, TrustState = {Decision.Candidate.TrustState}, " +
        $"Disposition = {Decision.Disposition}, {nameof(Alias)} = {Alias} }}";
}
