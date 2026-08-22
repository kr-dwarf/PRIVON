namespace Privon.Detection;

/// <summary>
/// Resolves overlapping candidates (possibly from different detectors) into a
/// non-overlapping final set.
///
/// Canonical precedence (fixed, not configurable per detector):
///   1. RiskLevel, highest first (Level3 always outranks Level2/Level1 regardless of span
///      length or confidence -- a long low-risk match must never bury a short Level3 match).
///   2. Confidence, highest first.
///   3. Span length, longest first.
/// Detector execution order may be optimized for performance, but it never affects this
/// final precedence -- resolution always re-sorts by the rule above before selecting.
/// </summary>
public static class OverlapResolver
{
    public static IReadOnlyList<DetectionCandidate> Resolve(IReadOnlyList<DetectionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count <= 1) return candidates;

        var ordered = candidates
            .OrderByDescending(c => c.RiskLevel)
            .ThenByDescending(c => c.Confidence)
            .ThenByDescending(c => c.Span.Length)
            .ToList();

        var accepted = new List<DetectionCandidate>(ordered.Count);
        foreach (var candidate in ordered)
        {
            bool overlaps = accepted.Any(a => a.Span.OverlapsWith(candidate.Span));
            if (!overlaps) accepted.Add(candidate);
        }

        return accepted;
    }
}
