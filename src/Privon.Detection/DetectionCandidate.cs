using Privon.Core;

namespace Privon.Detection;

/// <summary>
/// One detector's raw hit, before overlap resolution across detectors. RiskLevel (policy
/// severity) and Confidence (evidence strength) are deliberately independent fields -- see
/// OverlapResolver and the Phase 2 plan for why they must never be collapsed into one score.
///
/// DETECTION_CANONICAL_DIAGNOSTIC_SURFACE (Phase 3B STEP3.1): <see cref="ToString"/> is
/// explicitly overridden to project only safe structural metadata (PiiType, Span, RiskLevel,
/// Confidence, DetectorName) and never calls <see cref="Canonical"/>'s own ToString() --
/// this type's own safety must not depend on <see cref="CanonicalValue"/>'s override
/// continuing to exist/stay safe in the future; it is self-contained, exactly like
/// Privon.Windows.ClipboardTextReadResult reads ClipboardTextSnapshot's fields directly
/// rather than calling <c>Snapshot.Value.ToString()</c> (Phase 3A.4 STEP3.2 precedent).
/// </summary>
public sealed record DetectionCandidate(
    PiiType PiiType,
    RawSpan Span,
    RiskLevel RiskLevel,
    DetectionConfidence Confidence,
    CanonicalValue Canonical,
    string DetectorName)
{
    public override string ToString() =>
        $"{nameof(DetectionCandidate)} {{ {nameof(PiiType)} = {PiiType}, {nameof(Span)} = {Span}, " +
        $"{nameof(RiskLevel)} = {RiskLevel}, {nameof(Confidence)} = {Confidence}, {nameof(DetectorName)} = {DetectorName} }}";
}
