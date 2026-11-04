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
    PiiType PiiType,                                                 // (1)  UNCHANGED
    RawSpan Span,                                                    // (2)  UNCHANGED
    RiskLevel RiskLevel,                                             // (3)  UNCHANGED
    DetectionConfidence Confidence,                                  // (4)  UNCHANGED
    CanonicalValue? Canonical,                                       // (5)  TYPE WIDENED (§7.2)
    string DetectorName,                                             // (6)  UNCHANGED

    DetectorId DetectorId = default,                                 // (7)  NEW
    ResolutionEvidence Evidence = ResolutionEvidence.NotModelled,    // (8)  NEW
    int ResolutionRank = int.MaxValue,                               // (9)  NEW
                                                                     //      == UnassignedRank;
                                                                     //      a record primary-ctor
                                                                     //      default cannot name a
                                                                     //      member of its own type,
                                                                     //      so the literal is used
                                                                     //      and the named constant
                                                                     //      below stays public.
    AliasIdentity? AliasIdentity = null,                             // (10) NEW -- neutral (§22)
    OwnerClaim OwnerClaim = OwnerClaim.NotApplicable,                // (11) NEW
    NormativeFieldBag? AdditionalNormativeFields = null)             // (12) NEW -- §7.3
{
    /// <summary>§23.4 -- the ResolutionRank default for a candidate no detector stamped.</summary>
    public const int UnassignedRank = int.MaxValue;

    public override string ToString() =>
        $"{nameof(DetectionCandidate)} {{ {nameof(PiiType)} = {PiiType}, {nameof(Span)} = {Span}, " +
        $"{nameof(RiskLevel)} = {RiskLevel}, {nameof(Confidence)} = {Confidence}, {nameof(DetectorName)} = {DetectorName} }}";
}
