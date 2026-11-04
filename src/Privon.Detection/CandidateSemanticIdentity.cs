namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §8 -- exactly ELEVEN normative semantic fields:
/// DetectorId, SemanticType, Span.Start, Span.Length, RiskLevel, ResolutionEvidence,
/// DetectionConfidence, ResolutionRank, CanonicalValue, AliasIdentity, OwnerClaim.
///
/// EXCLUDED (diagnostic only): IDetector.Name / DetectorName, and every ToString() projection.
/// EXCLUDED (never reaches identity): AdditionalNormativeFields -- a non-empty bag aborts the
/// call at R0.5 check 1, so it can never participate.
///
/// EXACT_DUPLICATE: two candidates duplicate IFF they are equal on all eleven. Any single field
/// differing makes them distinct -- so alias-different is NOT a duplicate, OwnerClaim-different
/// is NOT a duplicate, and canonical-different INCLUDING ABSENT vs PRESENT is NOT a duplicate.
///
/// No reduced test-only equality exists anywhere.
/// </summary>
public static class CandidateSemanticIdentity
{
    public static bool AreExactDuplicates(DetectionCandidate a, DetectionCandidate b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (ReferenceEquals(a, b)) return true;

        return a.DetectorId.Equals(b.DetectorId)                  // 1
            && a.PiiType == b.PiiType                             // 2
            && a.Span.Start == b.Span.Start                       // 3
            && a.Span.Length == b.Span.Length                     // 4
            && a.RiskLevel == b.RiskLevel                         // 5
            && a.Evidence == b.Evidence                           // 6
            && a.Confidence == b.Confidence                       // 7
            && a.ResolutionRank == b.ResolutionRank               // 8
            // 9: Nullable<CanonicalValue> equality -- two ABSENT are equal, ABSENT != any
            //    PRESENT, two PRESENT compare by the struct's own record equality.
            && Nullable.Equals(a.Canonical, b.Canonical)
            // 10: the NEUTRAL identity (Family, Occurrence) via the record's own equality --
            //     never a localized display string (§22.2).
            && Equals(a.AliasIdentity, b.AliasIdentity)
            && a.OwnerClaim == b.OwnerClaim;                      // 11
    }
}
