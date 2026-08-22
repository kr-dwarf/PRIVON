using System.Text.RegularExpressions;
using Privon.Core;

namespace Privon.Detection;

/// <summary>
/// Phase 2Q.1 -- ContextRiskAdjustment minimal foundation. Runs after OverlapResolver and
/// before Exception/TrustedPublic evaluation (audit contract §7 pipeline order): its only job
/// is deciding whether a candidate's <see cref="DetectionConfidence"/> should move one step
/// down because of a nearby "this is example/sample data" signal -- design doc decision 179
/// ("명백한 예제·테스트 데이터는 일반 PII 확신도 완화 가능... 단 Level 3 비밀정보는 예제
/// 문맥만으로 자동 통과시키지 않는다").
///
/// Hard invariants, all enforced by construction, not by convention:
///   - Candidate COUNT never changes (no add, no drop) -- Detection already decided what
///     exists; Context only adjusts.
///   - PiiType / RawSpan / CanonicalValue / DetectorName never change on any candidate.
///   - RiskLevel.Level3 candidates are returned completely untouched -- not even their
///     Confidence moves. This is deliberately NOT a "smaller downgrade for Level3" rule; it is
///     zero adjustment, per decision 179's explicit Level3 carve-out and the audit contract's
///     fail-closed principle (see the Phase 2Q report's LEVEL3_CONTEXT_RULES).
///   - RiskLevel itself is never changed by this phase (Phase 2Q.1 scope is Confidence only --
///     RiskLevel-elevation rules like GPS's own local context are a later phase's decision,
///     not migrated here).
///
/// Downgrade step is conservative -- exactly one step, never straight to the floor:
/// High -> Medium, Medium -> Low, Low -> Low. The contract establishes that a downgrade is
/// possible, not how large a jump; a bigger jump isn't supported by any decision text.
///
/// Marker set is fixed and literal, per explicit instruction -- no synonym expansion (no
/// "fake"/"mock"/"demo"/"dev"/"개발용" etc. in this phase): sample, dummy, test, example,
/// 예시, 테스트, 샘플, 더미. Case-insensitive for the English markers.
///
/// Marker matching uses .NET regex's own Unicode-aware \b word-boundary (not a hand-rolled
/// tokenizer) so a marker is never matched as a substring of a larger, unrelated word --
/// "contest@example.com" does not trigger on "test" (no boundary between 'n' and 't'), and
/// "testing"/"testimonial"/"latest" don't trigger either. The same \b semantics apply to the
/// Hangul markers for the same reason (.NET's \w already covers the Hangul syllable Unicode
/// category), so e.g. "테스트기간" does not trigger on "테스트" as a bound word.
///
/// The context window is a small, fixed number of RAW characters immediately before and after
/// the candidate's own RawSpan (not the whole document) -- see WindowChars. This number is a
/// conservative implementation default, NOT a value taken from any contract text (tracked
/// internally as OPEN_QUESTION: SAMPLE_CONTEXT_WINDOW_SIZE). Matching is done
/// directly against NormalizedView.RawText using the candidate's own RawSpan, not the
/// normalized view -- this sidesteps needing any raw<->normalized reverse mapping and keeps
/// zero-width characters elsewhere in the text from affecting window arithmetic.
/// </summary>
public static class ContextRiskAdjuster
{
    // Conservative implementation default -- see class doc / OPEN_QUESTION:
    // SAMPLE_CONTEXT_WINDOW_SIZE. Not derived from any contract text.
    private const int WindowChars = 24;

    private static readonly Regex SampleMarkerPattern = new(
        @"\b(?:sample|dummy|test|example|예시|테스트|샘플|더미)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<DetectionCandidate> Adjust(NormalizedView view, IReadOnlyList<DetectionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0) return candidates;

        var rawText = view.RawText;
        var markerMatches = SampleMarkerPattern.Matches(rawText);
        if (markerMatches.Count == 0) return candidates;

        var adjusted = new List<DetectionCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.RiskLevel == RiskLevel.Level3 || !HasMarkerInWindow(rawText.Length, candidate.Span, markerMatches))
            {
                adjusted.Add(candidate);
                continue;
            }

            adjusted.Add(candidate with { Confidence = StepDown(candidate.Confidence) });
        }

        return adjusted;
    }

    private static bool HasMarkerInWindow(int rawTextLength, RawSpan span, MatchCollection markerMatches)
    {
        int windowStart = Math.Max(0, span.Start - WindowChars);
        int windowEnd = Math.Min(rawTextLength, span.End + WindowChars);

        foreach (Match m in markerMatches)
        {
            // Overlap test between the marker's own range and the candidate's local window --
            // a marker sitting entirely outside the window never counts, no matter how
            // "obviously test-related" the rest of the document is.
            if (m.Index < windowEnd && m.Index + m.Length > windowStart) return true;
        }

        return false;
    }

    private static DetectionConfidence StepDown(DetectionConfidence confidence) => confidence switch
    {
        DetectionConfidence.High => DetectionConfidence.Medium,
        DetectionConfidence.Medium => DetectionConfidence.Low,
        DetectionConfidence.Low => DetectionConfidence.Low,
        _ => confidence,
    };
}
