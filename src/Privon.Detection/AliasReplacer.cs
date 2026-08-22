using System.Text;

namespace Privon.Detection;

/// <summary>
/// Phase 2U.1 -- the "Raw span replacement" stage of the audit contract's canonical pipeline
/// (contract §7), placed after Alias assignment and before Composer write. Produces a
/// **local rewritten string only** -- its success means nothing more than "this string
/// transformation completed correctly". It is never called "Protected"/"Safe"/"Verified" text:
/// those meanings only attach after Composer write, read-back, and final validation (§2.1),
/// none of which this class knows about or touches. <see cref="Privon.Core.ProtectionState"/>
/// is never referenced here.
///
/// Public-API boundary defense (this method's inputs are not trusted just because their C#
/// types look non-nullable): every reference in the
/// AliasAssignment -> CandidatePolicyDecision -> EvaluatedCandidate -> DetectionCandidate chain
/// is explicitly null-checked, every enum (CandidateDisposition, the alias token's PiiType) is
/// checked with <see cref="Enum.IsDefined{TEnum}(TEnum)"/>, and every
/// Protect/Bypass/NeedsDecision <-> Alias-presence combination is checked for consistency.
/// ALL validation for every assignment runs to completion BEFORE any mutation begins -- a
/// single malformed assignment anywhere in the list must never let a partially-rewritten
/// string escape this method (see <see cref="Apply"/>: the validation loop and the mutation
/// loop are two entirely separate passes).
///
/// ALIASTOKEN_STRUCTURAL_INVARIANT (Phase 2R.4-style defense-in-depth, not a type fix):
/// <see cref="AliasToken"/>/<see cref="AliasAssignment"/> have no constructor validation of
/// their own (Phase 2U STEP 1 finding), so this class re-derives the EXPECTED token text
/// ("[" + AliasLabelProvider.GetLabel(PiiType) + Number + "]") for every Protect assignment
/// and requires ordinal exact equality against the actual token -- never trim/case-fold/fuzzy.
/// The type itself is not modified; this structural debt is intentionally left in place (see
/// the Phase 2U.1 report).
///
/// RawSpan trust boundary: this class never re-runs a detector, a canonicalizer, or compares
/// the raw substring against CanonicalValue.Value -- RawSpan semantic correctness is upstream
/// Detection's responsibility (already established through Phase 2A-2T). It only validates
/// STRUCTURAL span/overlap safety (bounds, no overlap/duplicate among Protect spans) -- never
/// clamped/truncated/skipped, always fail-fast.
///
/// Mutation order: RawSpan.Start descending (contract §7: "raw index가 큰 범위부터 역순
/// 적용해 index shift 오류를 막는다"). This is deliberately the OPPOSITE of Phase 2T.1's
/// alias-NUMBERING order (Start ascending) -- the two sorts serve entirely different purposes
/// and are never conflated.
/// </summary>
public static class AliasReplacer
{
    public static string Apply(string rawText, IReadOnlyList<AliasAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentNullException.ThrowIfNull(assignments);

        // ---- Phase 1: validate EVERY assignment fully before any mutation is attempted. ----
        var toReplace = new List<(RawSpan Span, string Token)>(assignments.Count);
        foreach (var assignment in assignments)
        {
            if (assignment is null)
                throw new ArgumentException("assignments contains a null entry.", nameof(assignments));
            if (assignment.Decision is null)
                throw new ArgumentException("An assignment's Decision is null.", nameof(assignments));
            if (assignment.Decision.Candidate is null)
                throw new ArgumentException("An assignment's Decision.Candidate is null.", nameof(assignments));
            if (assignment.Decision.Candidate.Candidate is null)
                throw new ArgumentException("An assignment's underlying DetectionCandidate is null.", nameof(assignments));

            var disposition = assignment.Decision.Disposition;
            if (!Enum.IsDefined(disposition))
                throw new ArgumentOutOfRangeException(nameof(assignments), disposition, "Undefined CandidateDisposition value.");

            switch (disposition)
            {
                case CandidateDisposition.Protect:
                    if (assignment.Alias is null)
                        throw new ArgumentException("A Protect assignment is missing its alias.", nameof(assignments));
                    toReplace.Add(ValidateProtectAssignment(assignment, rawText));
                    break;

                case CandidateDisposition.Bypass:
                case CandidateDisposition.NeedsDecision:
                    if (assignment.Alias is not null)
                        throw new ArgumentException($"A {disposition} assignment must not carry an alias.", nameof(assignments));
                    break;
            }
        }

        ValidateNoOverlap(toReplace);

        if (toReplace.Count == 0) return rawText;

        // ---- Phase 2: mutate, strictly RawSpan.Start descending, only now that every
        // assignment above has already passed validation. ----
        var builder = new StringBuilder(rawText);
        foreach (var (span, token) in toReplace.OrderByDescending(r => r.Span.Start))
        {
            builder.Remove(span.Start, span.Length);
            builder.Insert(span.Start, token);
        }

        return builder.ToString();
    }

    private static (RawSpan Span, string Token) ValidateProtectAssignment(AliasAssignment assignment, string rawText)
    {
        var token = assignment.Alias!.Value;
        var candidate = assignment.Decision.Candidate.Candidate;

        if (token.Number <= 0)
            throw new ArgumentException("An alias token's Number is not positive.", nameof(assignment));
        if (!Enum.IsDefined(token.PiiType))
            throw new ArgumentOutOfRangeException(nameof(assignment), token.PiiType, "Undefined PiiType on an alias token.");
        if (token.PiiType != candidate.PiiType)
            throw new ArgumentException("An alias token's PiiType does not match its candidate's PiiType.", nameof(assignment));
        if (token.Value is null)
            throw new ArgumentException("An alias token's Value text is null.", nameof(assignment));

        // Re-derive the expected token text and require ordinal exact equality -- never
        // trust the stored Value alone. See class doc: ALIASTOKEN_STRUCTURAL_INVARIANT.
        var expected = $"[{AliasLabelProvider.GetLabel(token.PiiType)}{token.Number}]";
        if (!string.Equals(token.Value, expected, StringComparison.Ordinal))
            throw new ArgumentException("An alias token's Value does not match its expected canonical form.", nameof(assignment));

        ValidateSpanBounds(candidate.Span, rawText);

        return (candidate.Span, token.Value);
    }

    // Overflow-safe: never computes Start + Length. Start is checked against rawText.Length
    // first; only once Start is known to be within bounds is (rawText.Length - Start) --
    // itself always a safe, non-negative subtraction -- compared against Length.
    private static void ValidateSpanBounds(RawSpan span, string rawText)
    {
        if (span.Start < 0)
            throw new ArgumentOutOfRangeException(nameof(span), span.Start, "RawSpan.Start is negative.");
        if (span.Length <= 0)
            throw new ArgumentOutOfRangeException(nameof(span), span.Length, "RawSpan.Length is not positive.");
        if (span.Start > rawText.Length)
            throw new ArgumentOutOfRangeException(nameof(span), span.Start, "RawSpan.Start is beyond the end of rawText.");
        if (span.Length > rawText.Length - span.Start)
            throw new ArgumentOutOfRangeException(nameof(span), span.Length, "RawSpan extends beyond the end of rawText.");
    }

    // Sort-then-check-adjacent-pairs: a standard, correct O(n log n) way to detect ANY
    // overlap (including exact duplicates and full containment) across a whole set of
    // intervals -- if no adjacent pair in Start-sorted order overlaps, no pair anywhere in
    // the set does (two non-adjacent spans overlapping would force some adjacent pair between
    // them to overlap too). This never recomputes OverlapResolver's RiskLevel/Confidence
    // priority -- any overlap found here is treated as invalid upstream state, full stop.
    private static void ValidateNoOverlap(List<(RawSpan Span, string Token)> toReplace)
    {
        if (toReplace.Count < 2) return;

        var sorted = toReplace.Select(r => r.Span).OrderBy(s => s.Start).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i - 1].OverlapsWith(sorted[i]))
                throw new ArgumentException("Two Protect spans overlap or duplicate -- invalid upstream state.");
        }
    }
}
