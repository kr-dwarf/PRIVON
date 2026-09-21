using System.Globalization;
using System.Text;
using Privon.Core;

namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §12 -- the complete, total, deterministic serialization key over the ELEVEN fields
/// of §12.1, in that exact order.
///
/// CCSK_IS_NOT_ARBITRATION: used for exactly three purposes -- the canonical ambiguity-payload
/// selector (§14.1), R6_SUPPRESSED_ORDER key 5 (§14.2), and deterministic
/// serialization/diagnostic bookkeeping. It MUST NOT influence winner selection, overlap
/// arbitration, precedence, legacy or mixed ownership, or SUPPRESSION_PROVENANCE_SELECTOR step
/// (i). No twelfth field exists: AdditionalNormativeFields never reaches the CCSK, because a
/// non-empty bag aborts at R0.5 check 1.
/// </summary>
public static class CanonicalCandidateSerializationKey
{
    /// <summary>
    /// Total order over the eleven fields. All string comparison is UTF-8 byte lexicographic
    /// (§12.2) -- never <c>string.CompareOrdinal</c>, <c>StringComparer.Ordinal</c>, or any
    /// culture-aware comparer. Nullable fields 9 and 10 sort ABSENT strictly before PRESENT.
    /// </summary>
    public static int Compare(DetectionCandidate a, DetectionCandidate b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (ReferenceEquals(a, b)) return 0;

        // 1. DetectorId.Value
        int c = Utf8ByteOrder.Compare(a.DetectorId.HasValue ? a.DetectorId.Value : null,
                                      b.DetectorId.HasValue ? b.DetectorId.Value : null);
        if (c != 0) return c;

        // 2. SemanticType, by stable ID STRING (never the enum ordinal -- §4.1)
        c = Utf8ByteOrder.Compare(PiiTypeIdCodec.ToStableId(a.PiiType), PiiTypeIdCodec.ToStableId(b.PiiType));
        if (c != 0) return c;

        // 3-4. Span
        c = a.Span.Start.CompareTo(b.Span.Start);
        if (c != 0) return c;
        c = a.Span.Length.CompareTo(b.Span.Length);
        if (c != 0) return c;

        // 5-8. frozen positions, then integer rank
        c = Position(a.RiskLevel).CompareTo(Position(b.RiskLevel));
        if (c != 0) return c;
        c = Position(a.Evidence).CompareTo(Position(b.Evidence));
        if (c != 0) return c;
        c = Position(a.Confidence).CompareTo(Position(b.Confidence));
        if (c != 0) return c;
        c = a.ResolutionRank.CompareTo(b.ResolutionRank);
        if (c != 0) return c;

        // 9. CanonicalValue -- genuine nullable; ABSENT < PRESENT by HasValue itself
        c = CompareCanonical(a.Canonical, b.Canonical);
        if (c != 0) return c;

        // 10. AliasIdentity -- genuine nullable; ABSENT < PRESENT, then contract token
        c = CompareAlias(a.AliasIdentity, b.AliasIdentity);
        if (c != 0) return c;

        // 11. OwnerClaim
        return Position(a.OwnerClaim).CompareTo(Position(b.OwnerClaim));
    }

    private static int CompareCanonical(CanonicalValue? a, CanonicalValue? b)
    {
        if (!a.HasValue && !b.HasValue) return 0;
        if (!a.HasValue) return -1;   // ABSENT < PRESENT
        if (!b.HasValue) return 1;

        int c = Utf8ByteOrder.Compare(PiiTypeIdCodec.ToStableId(a.Value.PiiType),
                                      PiiTypeIdCodec.ToStableId(b.Value.PiiType));
        return c != 0 ? c : Utf8ByteOrder.Compare(a.Value.Value, b.Value.Value);
    }

    private static int CompareAlias(AliasIdentity? a, AliasIdentity? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;     // ABSENT < PRESENT
        if (b is null) return 1;
        return Utf8ByteOrder.Compare(a.CanonicalContractToken, b.CanonicalContractToken);
    }

    private static int Position(RiskLevel v) =>
        FrozenPositions.TryRiskLevel(v, out int p) ? p : throw Undefined(nameof(RiskLevel));
    private static int Position(DetectionConfidence v) =>
        FrozenPositions.TryConfidence(v, out int p) ? p : throw Undefined(nameof(DetectionConfidence));
    private static int Position(ResolutionEvidence v) =>
        FrozenPositions.TryEvidence(v, out int p) ? p : throw Undefined(nameof(ResolutionEvidence));
    private static int Position(OwnerClaim v) =>
        FrozenPositions.TryOwnerClaim(v, out int p) ? p : throw Undefined(nameof(OwnerClaim));

    // Unreachable through the resolver: R0.5 check 2 rejects an undefined value before any CCSK
    // is computed. Present so a direct caller cannot silently order by a value with no position.
    private static InvalidOperationException Undefined(string field) =>
        new("Undefined value for closed domain " + field + "; no frozen CCSK position exists.");

    /// <summary>
    /// The eleven-field projection in §12.1 order using §12.2 rules, with ABSENT optional fields
    /// rendered as the frozen literal <c>null</c>. Integers render invariant-culture decimal: no
    /// grouping, no padding, no sign for non-negative.
    ///
    /// PRIVACY: this renders the canonical value, so it is a SERIALIZATION/diagnostic-bookkeeping
    /// surface for tests and record serialization only -- it is never written to a log, telemetry
    /// sink, or any failure payload (§15 forbids a canonical value in every payload).
    /// </summary>
    public static string Render(DetectionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var sb = new StringBuilder();
        sb.Append(candidate.DetectorId.HasValue ? candidate.DetectorId.Value : "null").Append('|');
        sb.Append(PiiTypeIdCodec.ToStableId(candidate.PiiType)).Append('|');
        sb.Append(candidate.Span.Start.ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(candidate.Span.Length.ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(Position(candidate.RiskLevel).ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(Position(candidate.Evidence).ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(Position(candidate.Confidence).ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(candidate.ResolutionRank.ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(candidate.Canonical is { } cv
            ? PiiTypeIdCodec.ToStableId(cv.PiiType) + ":" + cv.Value
            : "null").Append('|');
        sb.Append(candidate.AliasIdentity is { } alias ? alias.CanonicalContractToken : "null").Append('|');
        sb.Append(Position(candidate.OwnerClaim).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
