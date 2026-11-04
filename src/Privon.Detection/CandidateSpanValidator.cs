namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §11.1 -- R0 span validation.
///
/// SPAN_VALIDITY_IS_FAIL_CLOSED: any violation aborts the ENTIRE call. Never clamped, truncated,
/// dropped, skipped, coerced, or reported as NoCandidate/NoUsCandidate. No partial result.
/// </summary>
internal static class CandidateSpanValidator
{
    public static void Validate(IReadOnlyList<DetectionCandidate> candidates, int rawInputLength)
    {
        // CANONICAL_INVALID_SPAN_SELECTOR: collect ALL violating candidates and report the one
        // whose (Span.Start, Span.Length) is lexicographically smallest. INTEGERS ONLY -- never
        // the CCSK, because R0 precedes R0.5 and an unvalidated field must not be serialized.
        bool found = false;
        int selStart = 0, selLength = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            var span = candidates[i].Span;
            if (IsValid(span, rawInputLength)) continue;

            if (!found || span.Start < selStart || (span.Start == selStart && span.Length < selLength))
            {
                selStart = span.Start;
                selLength = span.Length;
                found = true;
            }
        }

        if (found) throw new InvalidCandidateSpanException(selStart, selLength, rawInputLength);
    }

    private static bool IsValid(RawSpan span, int n)
    {
        if (span.Start < 0) return false;          // negative Start
        if (span.Length <= 0) return false;        // zero or negative Length
        if (span.Start > n) return false;          // Start > N  (covers Start == N with positive Length below)
        // OVERFLOW-SAFE form of Start + Length <= N, evaluated exactly as written. N - Start
        // cannot underflow given the check above. 32-bit addition MUST NOT be used here.
        return span.Length <= n - span.Start;
    }
}
