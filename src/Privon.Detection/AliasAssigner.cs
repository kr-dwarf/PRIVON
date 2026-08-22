namespace Privon.Detection;

/// <summary>
/// Phase 2T.1 -- stateless assignment of alias tokens to <see cref="CandidatePolicyDecision"/>s.
/// Only <see cref="CandidateDisposition.Protect"/> candidates receive an alias; Bypass and
/// NeedsDecision candidates always get a null <see cref="AliasAssignment.Alias"/> -- a
/// NeedsDecision candidate (e.g. every current Level3 candidate, or Level1/Medium) is
/// deliberately never treated as Protect here; its eventual "보호 후 계속" resolution belongs
/// to a future runtime/revision-override phase, not this one.
///
/// Numbering order vs. output order (deliberately two separate concerns -- see the Phase 2T
/// report's DETECTIONRESULT_ORDER_NOT_RAW_SPAN_ORDER finding): the order <paramref
/// name="decisions"/> arrives in reflects OverlapResolver's RiskLevel/Confidence precedence,
/// NOT raw left-to-right text order, so it is never used to decide which canonical value gets
/// the lower alias number. Instead, new canonical values are registered with the AliasMap in a
/// separate pass sorted by RawSpan.Start ascending (i.e. the order they actually first appear
/// in the raw text) before the output is built. Two accepted candidates can never share the
/// same Span.Start after OverlapResolver (any two spans with equal Start and positive length
/// necessarily overlap, so OverlapResolver would already have dropped one) -- Length/PiiType/
/// DetectorName are included purely as deterministic, non-semantic tie-breakers, the same
/// style already used by DetectionPipeline's own pre-Overlap sort, never as an invented
/// priority rule. The returned list itself is built in <paramref name="decisions"/>' own
/// original order, unchanged -- only the internal numbering pass is reordered.
/// </summary>
public static class AliasAssigner
{
    public static IReadOnlyList<AliasAssignment> Assign(IReadOnlyList<CandidatePolicyDecision> decisions, AliasMap aliasMap)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(aliasMap);

        var protectInRawAppearanceOrder = decisions
            .Where(d => d.Disposition == CandidateDisposition.Protect)
            .OrderBy(d => d.Candidate.Candidate.Span.Start)
            .ThenBy(d => d.Candidate.Candidate.Span.Length)
            .ThenBy(d => d.Candidate.Candidate.PiiType)
            .ThenBy(d => d.Candidate.Candidate.DetectorName, StringComparer.Ordinal);

        foreach (var decision in protectInRawAppearanceOrder)
        {
            aliasMap.GetOrAdd(decision.Candidate.Candidate.Canonical);
        }

        var results = new List<AliasAssignment>(decisions.Count);
        foreach (var decision in decisions)
        {
            AliasToken? alias = decision.Disposition == CandidateDisposition.Protect
                ? aliasMap.GetOrAdd(decision.Candidate.Candidate.Canonical)
                : null;
            results.Add(new AliasAssignment(decision, alias));
        }

        return results;
    }
}
