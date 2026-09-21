namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §11.3 -- R2 overlap graph and connected components.
///
/// <c>overlaps(a, b) = a.Start &lt; b.Start + b.Length AND b.Start &lt; a.Start + a.Length</c>.
/// ADJACENCY IS NOT OVERLAP. A component is a connected component of the reflexive-transitive
/// closure of overlaps; components are pairwise independent.
///
/// Because <c>overlaps</c> is NOT transitive, one component may yield SEVERAL survivors -- a
/// component is not winner-take-all (§11.5).
/// </summary>
internal static class OverlapComponentBuilder
{
    public static bool Overlaps(RawSpan a, RawSpan b) =>
        a.Start < b.Start + b.Length && b.Start < a.Start + a.Length;

    /// <summary>
    /// Returns component membership as an index list per component. Built by union-find over
    /// candidate INDICES; the traversal order affects nothing observable because the result is
    /// the set of connected components, which is order-invariant.
    /// </summary>
    public static IReadOnlyList<List<int>> Build(IReadOnlyList<DetectionCandidate> candidates)
    {
        int n = candidates.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (Overlaps(candidates[i].Span, candidates[j].Span))
                {
                    int ri = Find(i), rj = Find(j);
                    if (ri != rj) parent[ri] = rj;
                }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
            list.Add(i);
        }

        return [.. groups.Values];
    }
}
