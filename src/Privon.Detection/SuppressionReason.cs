namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §11.6 SUPPRESSION_REASON -- the first arbitration key on which the suppressed
/// candidate loses. Produced by R5 from a closed frozen mapping, never supplied as input, so it
/// cannot carry an unrecognized value and is not an R0.5 input (§13).
/// </summary>
public enum SuppressionReason
{
    LowerLevel = 0,
    WeakerEvidence = 1,
    LowerConfidence = 2,
    ShorterOverlappingSpan = 3,
    HigherNumericRank = 4,
    LegacyCompatibilityOrder = 5,
}
