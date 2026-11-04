using Privon.Core;

namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §10 -- the deterministic resolver pipeline R0 -> R6.
///
/// Each stage reads only the preceding stage's output and the candidates' own field values.
/// NO STAGE READS THE INPUT LIST'S ORDER. Arbitration keys are used only in R3, R4 and R5.
///
/// FORBIDDEN_HIDDEN_TIEBREAKERS (§9.2): registration order, source order, discovery order, UI
/// order, country order, PiiType ordinal, class name, DetectorName and input order MUST NOT
/// influence arbitration at any key in any domain, including the fail-closed path.
/// CCSK_IS_NOT_ARBITRATION (§12): the CCSK is used only for the ambiguity-payload selector and
/// as the total-making suffix of the suppressed output order -- never to choose a winner.
/// </summary>
public static class CandidateResolver
{
    public static ResolutionResult Resolve(ResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Candidates);

        var candidates = input.Candidates;

        // ---------------- R0: span validation, fail-closed ----------------
        CandidateSpanValidator.Validate(candidates, input.RawInputLength);

        // ---------------- R0.5: normative value-domain validation, fail-closed ----------------
        NormativeValueDomainValidator.Validate(candidates);

        // ---------------- R1: EXACT_DUPLICATE de-duplication ----------------
        // Collapses duplicate sets to ONE retained candidate, emits no SuppressedCandidate, is
        // not a throw, and is observationally equivalent to 0.3.2.
        var retained = new List<DetectionCandidate>(candidates.Count);
        foreach (var c in candidates)
        {
            bool duplicate = false;
            foreach (var kept in retained)
                if (CandidateSemanticIdentity.AreExactDuplicates(kept, c)) { duplicate = true; break; }
            if (!duplicate) retained.Add(c);
        }

        // ---------------- R2: overlap graph / connected components ----------------
        var components = OverlapComponentBuilder.Build(retained);

        // ---------------- R3: equal-key actual-overlap ambiguity precheck ----------------
        // Scan-global and BEFORE any greedy resolution begins. If it holds for ANY pair anywhere,
        // the ENTIRE resolve call aborts -- NO PARTIAL RESULTS, because returning resolved
        // candidates for unambiguous components would report an incomplete scan as complete.
        DetectAmbiguity(retained, components);

        // ---------------- R4: greedy survivor resolution ----------------
        var survivors = new List<DetectionCandidate>();
        var suppressedByComponent = new List<(DetectionCandidate Suppressed, List<DetectionCandidate> Component, ArbitrationDomain Domain)>();

        foreach (var component in components)
        {
            var members = component.Select(i => retained[i]).ToList();
            var domain = DomainOf(members);
            var comparer = ComparerFor(domain);

            // Lemma L2: S is input-order-invariant; EVERY linear extension of the component's
            // comparator yields the same S. Sorting on one such extension is therefore legitimate
            // -- but a total order is NEVER invented over non-overlapping candidates.
            var ordered = new List<DetectionCandidate>(members);
            ordered.Sort((a, b) => -comparer(a, b));   // strongest first

            var accepted = new List<DetectionCandidate>();
            foreach (var c in ordered)
            {
                bool blocked = accepted.Any(w => OverlapComponentBuilder.Overlaps(c.Span, w.Span));
                if (blocked) suppressedByComponent.Add((c, members, domain));
                else accepted.Add(c);
            }
            survivors.AddRange(accepted);
        }

        // ---------------- R5: suppression provenance ----------------
        var suppressed = new List<SuppressedCandidate>();
        foreach (var (s, members, domain) in suppressedByComponent)
        {
            var comparer = ComparerFor(domain);

            // Suppressors(s) = every survivor that ACTUALLY overlaps s -- non-empty by
            // R4_SURVIVOR_SET.
            var suppressors = survivors.Where(w => OverlapComponentBuilder.Overlaps(s.Span, w.Span)).ToList();

            // SUPPRESSION_PROVENANCE_SELECTOR: (i) greatest under the component's comparator,
            // then (ii) among those, least by Span.Start ascending. Lemma L3 shows step (ii)
            // selects exactly one.
            DetectionCandidate best = suppressors[0];
            foreach (var w in suppressors)
            {
                int c = comparer(w, best);
                if (c > 0 || (c == 0 && w.Span.Start < best.Span.Start)) best = w;
            }

            suppressed.Add(new SuppressedCandidate(s, ReasonFor(s, best, domain), best));
        }

        // ---------------- R6: deterministic normative output ordering ----------------
        // Winners: Span.Start ascending -- TOTAL, since survivors have distinct starts (§11.1).
        // Span.Length descending is a declared-unreachable secondary.
        survivors.Sort(static (a, b) =>
        {
            int c = a.Span.Start.CompareTo(b.Span.Start);
            return c != 0 ? c : b.Span.Length.CompareTo(a.Span.Length);
        });

        // Suppressed: Start asc -> Length desc -> Rank asc -> DetectorId UTF-8 asc ->
        // CCSK asc (the total-making suffix).
        suppressed.Sort(static (x, y) =>
        {
            var a = x.Candidate; var b = y.Candidate;
            int c = a.Span.Start.CompareTo(b.Span.Start);
            if (c != 0) return c;
            c = b.Span.Length.CompareTo(a.Span.Length);
            if (c != 0) return c;
            c = a.ResolutionRank.CompareTo(b.ResolutionRank);
            if (c != 0) return c;
            c = Utf8ByteOrder.Compare(a.DetectorId.HasValue ? a.DetectorId.Value : null,
                                      b.DetectorId.HasValue ? b.DetectorId.Value : null);
            if (c != 0) return c;
            return CanonicalCandidateSerializationKey.Compare(a, b);
        });

        return new ResolutionResult(survivors, suppressed);
    }

    // ================================================================================
    // §9 ARBITRATION_DOMAIN -- a property of EMISSION, fixed at construction, never inferred
    // from field contents.
    // ================================================================================
    private static ArbitrationDomain DomainOf(IReadOnlyList<DetectionCandidate> members)
    {
        bool anyLegacy = false, anyContract = false;
        foreach (var m in members)
        {
            if (FrozenPositions.IsLegacyDetectorId(m.DetectorId)) anyLegacy = true;
            else anyContract = true;
        }
        if (anyLegacy && anyContract) return ArbitrationDomain.Mixed;
        return anyLegacy ? ArbitrationDomain.LegacyDomain : ArbitrationDomain.ContractDomain;
    }

    private static Func<DetectionCandidate, DetectionCandidate, int> ComparerFor(ArbitrationDomain domain) =>
        domain switch
        {
            ArbitrationDomain.LegacyDomain => CompareLegacy,
            ArbitrationDomain.ContractDomain => CompareContract,
            _ => CompareMixed,
        };

    // §9.1 Legacy-only: RiskLevel desc -> DetectionConfidence desc -> Span.Length desc ->
    // Span.Start ASC -> LegacyOwnershipOrder ASC -> CanonicalValue.Value ordinal asc.
    // ResolutionEvidence is NEVER READ here (EVIDENCE_NEVER_READ_FOR_LEGACY).
    private static int CompareLegacy(DetectionCandidate a, DetectionCandidate b)
    {
        int c = Pos(a.RiskLevel).CompareTo(Pos(b.RiskLevel));
        if (c != 0) return c;
        c = Pos(a.Confidence).CompareTo(Pos(b.Confidence));
        if (c != 0) return c;
        c = a.Span.Length.CompareTo(b.Span.Length);
        if (c != 0) return c;
        c = b.Span.Start.CompareTo(a.Span.Start);                 // Start ASC => smaller is stronger
        if (c != 0) return c;
        c = b.LegacyOrder().CompareTo(a.LegacyOrder());            // order ASC => smaller is stronger
        if (c != 0) return c;
        // Key 6 is unreachable (Lemma L1) but written out so the comparator is total as declared.
        return -Utf8ByteOrder.Compare(a.Canonical?.Value, b.Canonical?.Value);
    }

    // §9.1 Contract-only: RiskLevel desc -> ResolutionEvidence desc -> Span.Length desc ->
    // ResolutionRank asc. DetectionConfidence is NEVER READ here.
    private static int CompareContract(DetectionCandidate a, DetectionCandidate b)
    {
        int c = Pos(a.RiskLevel).CompareTo(Pos(b.RiskLevel));
        if (c != 0) return c;
        c = Pos(a.Evidence).CompareTo(Pos(b.Evidence));
        if (c != 0) return c;
        c = a.Span.Length.CompareTo(b.Span.Length);
        if (c != 0) return c;
        return b.ResolutionRank.CompareTo(a.ResolutionRank);       // rank ASC => smaller is stronger
    }

    // §9.1 Mixed: RiskLevel desc -> Span.Length desc -> ResolutionRank asc. Evidence is NOT
    // consulted in a mixed set (PARENT_DEVIATION_A1.1-2), so span length is key 2.
    private static int CompareMixed(DetectionCandidate a, DetectionCandidate b)
    {
        int c = Pos(a.RiskLevel).CompareTo(Pos(b.RiskLevel));
        if (c != 0) return c;
        c = a.Span.Length.CompareTo(b.Span.Length);
        if (c != 0) return c;
        return b.ResolutionRank.CompareTo(a.ResolutionRank);
    }

    private static int Pos(RiskLevel v) { FrozenPositions.TryRiskLevel(v, out int p); return p; }
    private static int Pos(DetectionConfidence v) { FrozenPositions.TryConfidence(v, out int p); return p; }
    private static int Pos(ResolutionEvidence v) { FrozenPositions.TryEvidence(v, out int p); return p; }

    private static int LegacyOrder(this DetectionCandidate c)
    {
        FrozenPositions.TryLegacyOwnershipOrder(c.PiiType, out int order);
        return order;
    }

    // ================================================================================
    // §11.4 R3 + §14.1 canonical ambiguous-pair selection
    // ================================================================================
    private static void DetectAmbiguity(IReadOnlyList<DetectionCandidate> retained, IReadOnlyList<List<int>> components)
    {
        (int OverlapStart, int OverlapLength, DetectionCandidate Low, DetectionCandidate High, ArbitrationDomain Domain)? selected = null;

        foreach (var component in components)
        {
            var members = component.Select(i => retained[i]).ToList();
            var domain = DomainOf(members);
            var comparer = ComparerFor(domain);

            for (int i = 0; i < members.Count; i++)
                for (int j = i + 1; j < members.Count; j++)
                {
                    var u = members[i];
                    var v = members[j];

                    // Scope: DISTINCT pairs that ACTUALLY overlap and are equal on every
                    // arbitration key of their component's domain.
                    if (!OverlapComponentBuilder.Overlaps(u.Span, v.Span)) continue;
                    if (comparer(u, v) != 0) continue;

                    int overlapStart = Math.Max(u.Span.Start, v.Span.Start);
                    int overlapEnd = Math.Min(u.Span.End, v.Span.End);
                    int overlapLength = overlapEnd - overlapStart;
                    if (overlapLength <= 0) continue;

                    // Distinct post-R1 => distinct CCSK, so Low/High is well defined.
                    var (low, high) = CanonicalCandidateSerializationKey.Compare(u, v) <= 0 ? (u, v) : (v, u);

                    // CANONICAL_AMBIGUITY_SELECTOR: collect ALL ambiguous pairs and select the
                    // unique lexicographic minimum of AmbiguousPairKey =
                    // (OverlapStart, OverlapLength, CCSK(Low), CCSK(High)).
                    if (selected is null
                        || overlapStart < selected.Value.OverlapStart
                        || (overlapStart == selected.Value.OverlapStart
                            && (overlapLength < selected.Value.OverlapLength
                                || (overlapLength == selected.Value.OverlapLength
                                    && (CanonicalCandidateSerializationKey.Compare(low, selected.Value.Low) < 0
                                        || (CanonicalCandidateSerializationKey.Compare(low, selected.Value.Low) == 0
                                            && CanonicalCandidateSerializationKey.Compare(high, selected.Value.High) < 0))))))
                    {
                        selected = (overlapStart, overlapLength, low, high, domain);
                    }
                }
        }

        if (selected is { } s)
            throw new UnresolvableCandidateTieException(
                s.Domain, s.OverlapStart, s.OverlapLength, s.Low.DetectorId, s.High.DetectorId);
    }

    // §11.6 SUPPRESSION_REASON -- the FIRST arbitration key on which s loses.
    private static SuppressionReason ReasonFor(DetectionCandidate s, DetectionCandidate w, ArbitrationDomain domain)
    {
        if (Pos(s.RiskLevel) != Pos(w.RiskLevel)) return SuppressionReason.LowerLevel;

        if (domain == ArbitrationDomain.ContractDomain && Pos(s.Evidence) != Pos(w.Evidence))
            return SuppressionReason.WeakerEvidence;

        if (domain == ArbitrationDomain.LegacyDomain && Pos(s.Confidence) != Pos(w.Confidence))
            return SuppressionReason.LowerConfidence;

        if (s.Span.Length != w.Span.Length) return SuppressionReason.ShorterOverlappingSpan;

        if (domain != ArbitrationDomain.LegacyDomain && s.ResolutionRank != w.ResolutionRank)
            return SuppressionReason.HigherNumericRank;

        // Span.Start / LegacyOwnershipOrder / CanonicalValue.Value (legacy only).
        return SuppressionReason.LegacyCompatibilityOrder;
    }
}
