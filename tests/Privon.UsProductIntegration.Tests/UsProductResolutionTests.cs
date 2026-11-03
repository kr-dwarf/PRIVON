using Privon.Core;
using Privon.Detection;
using Privon.Detection.Family;
using Privon.UsProductIntegration;
using Privon.UsSsn;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. Resolution, family arbitration, alias and policy behaviour, driven through the REAL product
// CandidateResolver, DetectorFamilyArbitrationBoundary, AliasMap / AliasIdentity and CandidatePolicyEvaluator.
public class UsProductResolutionTests
{
    private static ResolutionResult Resolve(IReadOnlyList<DetectionCandidate> candidates, string raw) =>
        CandidateResolver.Resolve(new ResolutionInput(candidates, raw.Length));

    // ---- duplicates and conflicts, through the product resolver -----------------------------------------------

    [Fact]
    public void TheSameSpanReportedTwice_IsCollapsedByTheResolversExactDuplicateRule()
    {
        const string raw = "SSN: 321-54-9876";
        var once = Run(new UsSsnCandidateDetector(), raw);
        var twice = once.Concat(Run(new UsSsnCandidateDetector(), raw)).ToList();

        var result = Resolve(twice, raw);

        Assert.Equal(2, twice.Count);
        Assert.Single(result.Winners);
        Assert.Empty(result.Suppressed);
    }

    [Fact]
    public void NonOverlappingCandidates_OfDifferentTypes_AllSurvive()
    {
        const string raw = "ATIN: 912-93-4567 ITIN: 912-70-1234 SSN: 321-54-9876 MBI: 1A23C45DE67 SEVIS: N1234567890";

        var result = Resolve(RunAll(raw), raw);

        Assert.Equal(5, result.Winners.Count);
        Assert.Empty(result.Suppressed);
    }

    [Fact]
    public void OnlyTheLegalAdjacentCase_TwoIdentifiersTouchingEachOther_IsStillTwoCandidates()
    {
        // OVR-ADJ-001 style adjacency, with a separator so that every grammar's exact-boundary rule holds.
        const string raw = "321-54-9876 N1234567890";

        var result = Resolve(RunAll(raw), raw);

        Assert.Equal(["us.ssn", "us.sevis"], result.Winners.Select(w => w.DetectorId.Value));
    }

    [Fact]
    public void InjectedSameSpanConflict_IsDecidedByTheProductsFrozenRankOrder_ReportingTheLoser()
    {
        // The detectors never emit these together (see the family tests); the conflict is injected the way the D3A
        // ResolverHarness injects candidates, to prove the product resolver reads the ranks these detectors stamp.
        const string raw = "912-93-4567";
        var atin = Run(new UsAtinCandidateDetector(), raw).Single();
        var itin = atin with { PiiType = PiiType.UsItin, DetectorId = DetectorId.Create("us.itin"), ResolutionRank = 60,
            Canonical = new CanonicalValue(PiiType.UsItin, "912934567") };
        var ssn = atin with { PiiType = PiiType.UsSsn, DetectorId = DetectorId.Create("us.ssn"), ResolutionRank = 70,
            Canonical = new CanonicalValue(PiiType.UsSsn, "912934567") };

        var forward = Resolve([ssn, itin, atin], raw);
        var backward = Resolve([atin, itin, ssn], raw);

        Assert.Equal("us.atin", Assert.Single(forward.Winners).DetectorId.Value);
        Assert.Equal(["us.itin", "us.ssn"], forward.Suppressed.Select(s => s.Candidate.DetectorId.Value).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(forward.Winners.Select(w => w.DetectorId.Value), backward.Winners.Select(w => w.DetectorId.Value));
    }

    [Fact]
    public void TheResolverAlone_LetsEvidenceOutrankFamilyPriority_WhichIsWhyTheFamilyArbiterMustRunFirst()
    {
        // Characterization of the REAL resolver (CompareContract: level, evidence, length, then rank). An ITIN claim with
        // context (S+C) beats an ATIN claim without it (S) on the same span. D2.2 requires ATIN > ITIN > SSN regardless.
        const string raw = "ITIN: 912-93-4567";
        var itin = Run(new UsItinCandidateDetector(), "ITIN: 912-70-1234").Single() with { Span = new RawSpan(6, 11) };
        var atin = Run(new UsAtinCandidateDetector(), "912-93-4567").Single() with { Span = new RawSpan(6, 11) };

        var result = Resolve([atin, itin], raw);

        Assert.Equal(ResolutionEvidence.SC, itin.Evidence);
        Assert.Equal(ResolutionEvidence.S, atin.Evidence);
        Assert.Equal("us.itin", Assert.Single(result.Winners).DetectorId.Value);
    }

    [Fact]
    public void AnInvalidSpan_FailsTheWholeResolveClosed_ItIsNeverClampedOrDropped()
    {
        const string raw = "SSN: 321-54-9876";
        var candidates = Run(new UsSsnCandidateDetector(), raw);

        Assert.Throws<InvalidCandidateSpanException>(() => CandidateResolver.Resolve(new ResolutionInput(candidates, raw.Length - 1)));
    }

    [Fact]
    public void EveryCandidateOverAGeneratedCorpus_PassesTheResolversFailClosedValidation()
    {
        foreach (string raw in Corpus.Generate(seed: 20260601, count: 1500))
        {
            var all = RunAll(raw);
            var result = Resolve(all, raw); // R0 span validation and R0.5 value-domain validation throw on any violation

            Assert.Equal(all.Count, result.Winners.Count + result.Suppressed.Count);
        }
    }

    // ---- family arbitration: the REAL boundary -------------------------------------------------------------------

    [Fact]
    public void TheFamilyBoundary_ResolvesTheTaxpayerArbiter_AndEmitsTheFrozenWinnerRecord()
    {
        var attempts = UsTaxpayerFamilyAttemptSource.Collect("912-93-4567"); // US-TAX-MX02

        bool available = DetectorFamilyArbitrationBoundary.TryArbitrate(
            new UsTaxpayerFamilyArbiterProvider(), UsTaxpayerFamilyArbiter.TaxpayerFamilyName, attempts,
            out var result, out var observation);

        Assert.True(available);
        Assert.Equal(FamilyArbitrationState.ArbitrationAvailable, observation.State);
        Assert.Equal(3, observation.AttemptCount);
        Assert.Equal("us.atin|UsAtin|L3|S|0:11|912934567|[ATIN1]|50", Oracle(Assert.Single(result!.Emitted)));
        Assert.Equal(2, result.Rejected.Count);
        Assert.All(result.Rejected, r => Assert.Equal(FamilyAttemptOutcome.Rejected, r.Outcome));
        Assert.Contains(result.Rejected, r => r.DetectorId.Value == "us.itin" && r.RejectionReason == nameof(UsTaxpayerRejectionReason.ItinRange93Excluded));
        Assert.Contains(result.Rejected, r => r.DetectorId.Value == "us.ssn" && r.RejectionReason == nameof(UsTaxpayerRejectionReason.Area900To999));
    }

    [Fact]
    public void TheAttemptSource_ListsEveryTypesVerdict_AcceptedRejectedWithReasons_InTheFrozenAttemptShape()
    {
        var attempts = UsTaxpayerFamilyAttemptSource.Collect("912-93-4567");

        Assert.Equal(
            ["us.atin|L3|S|0:11|Accepted", "us.itin|L3|S|0:11|Rejected", "us.ssn|L3|S|0:11|Rejected"],
            attempts.OrderBy(a => a.DetectorId.Value, StringComparer.Ordinal)
                .Select(a => $"{a.DetectorId.Value}|{(a.Level == RiskLevel.Level3 ? "L3" : "?")}|{a.Evidence}|{a.Span.Start}:{a.Span.Length}|{a.Outcome}"));
        Assert.All(attempts.Where(a => a.Outcome == FamilyAttemptOutcome.Accepted), a => Assert.NotNull(a.Canonical));
        Assert.All(attempts.Where(a => a.Outcome == FamilyAttemptOutcome.Rejected), a => Assert.NotNull(a.RejectionReason));
    }

    [Fact]
    public void ARejectedAttempt_IsNeverEmitted_UnderAnyInputOrder()
    {
        var attempts = UsTaxpayerFamilyAttemptSource.Collect("ATIN: 912-93-4567 ITIN: 912-70-1234 SSN: 000-12-3456");
        var arbiter = new UsTaxpayerFamilyArbiter();

        foreach (var permutation in Permutations(attempts.ToList()).Take(200))
        {
            var result = arbiter.Arbitrate(permutation);
            var emittedIds = result.Emitted.Select(e => e.DetectorId.Value).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.Equal(["us.atin", "us.itin"], emittedIds);
            Assert.DoesNotContain(result.Rejected, r => r.Outcome == FamilyAttemptOutcome.Accepted);
        }
    }

    [Fact]
    public void SameSpanAcceptedAttempts_AreArbitratedAtinOverItinOverSsn_RegardlessOfEvidenceAndOrder()
    {
        // Synthetic accepted attempts on one span, ITIN with context and ATIN without (the resolver-alone trap).
        var span = new RawSpan(6, 11);
        FamilyCandidateAttempt Accept(string id, PiiType type, ResolutionEvidence ev) =>
            new(DetectorId.Create(id), RiskLevel.Level3, ev, span, new CanonicalValue(type, "912934567"),
                FamilyAttemptOutcome.Accepted, null);
        var atin = Accept("us.atin", PiiType.UsAtin, ResolutionEvidence.S);
        var itin = Accept("us.itin", PiiType.UsItin, ResolutionEvidence.SC);
        var ssn = Accept("us.ssn", PiiType.UsSsn, ResolutionEvidence.SC);
        var arbiter = new UsTaxpayerFamilyArbiter();

        foreach (var order in Permutations(new List<FamilyCandidateAttempt> { atin, itin, ssn }))
        {
            var result = arbiter.Arbitrate(order);

            Assert.Equal("us.atin", Assert.Single(result.Emitted).DetectorId.Value);
            Assert.Equal(["us.itin", "us.ssn"], result.Rejected.Select(r => r.DetectorId.Value).OrderBy(x => x, StringComparer.Ordinal));
            Assert.All(result.Rejected, r => Assert.Equal(UsTaxpayerFamilyArbiter.SupersededReason, r.RejectionReason));
        }
    }

    [Fact]
    public void AnUnavailableArbiter_IsAnObservedUnavailability_NotAnEmptyResult()
    {
        var attempts = UsTaxpayerFamilyAttemptSource.Collect("912-93-4567");

        bool otherFamily = DetectorFamilyArbitrationBoundary.TryArbitrate(
            new UsTaxpayerFamilyArbiterProvider(), "some.other.family", attempts, out var r1, out var o1);
        bool shippedProvider = DetectorFamilyArbitrationBoundary.TryArbitrate(
            new EmptyDetectorFamilyArbiterProvider(), UsTaxpayerFamilyArbiter.TaxpayerFamilyName, attempts, out var r2, out var o2);

        Assert.False(otherFamily);
        Assert.Null(r1);
        Assert.Equal(FamilyArbitrationState.ArbitrationUnavailable, o1.State);
        Assert.False(shippedProvider); // the product's own provider still resolves nothing: this module changed nothing there
        Assert.Null(r2);
        Assert.Equal(FamilyArbitrationState.ArbitrationUnavailable, o2.State);
    }

    [Fact]
    public void ArbiterInputsThatCannotBeArbitrated_FailClosed_InsteadOfBeingDropped()
    {
        var arbiter = new UsTaxpayerFamilyArbiter();
        var span = new RawSpan(0, 11);
        var good = new CanonicalValue(PiiType.UsAtin, "912934567");

        Assert.Throws<ArgumentNullException>(() => arbiter.Arbitrate(null!));
        // an id outside the taxpayer family
        Assert.Throws<ArgumentException>(() => arbiter.Arbitrate([new(DetectorId.Create("us.mbi"), RiskLevel.Level3,
            ResolutionEvidence.S, span, new CanonicalValue(PiiType.UsMbi, "1A23C45DE67"), FamilyAttemptOutcome.Accepted, null)]));
        // an accepted attempt without the value it claims to accept
        Assert.Throws<ArgumentException>(() => arbiter.Arbitrate([new(DetectorId.Create("us.atin"), RiskLevel.Level3,
            ResolutionEvidence.S, span, null, FamilyAttemptOutcome.Accepted, null)]));
        // a canonical whose type does not match the detector
        Assert.Throws<ArgumentException>(() => arbiter.Arbitrate([new(DetectorId.Create("us.itin"), RiskLevel.Level3,
            ResolutionEvidence.S, span, good, FamilyAttemptOutcome.Accepted, null)]));
        // an evidence the cores never produce
        Assert.Throws<ArgumentException>(() => arbiter.Arbitrate([new(DetectorId.Create("us.atin"), RiskLevel.Level3,
            ResolutionEvidence.JOrP, span, good, FamilyAttemptOutcome.Accepted, null)]));
        // a level other than 3
        Assert.Throws<ArgumentException>(() => arbiter.Arbitrate([new(DetectorId.Create("us.atin"), RiskLevel.Level2,
            ResolutionEvidence.S, span, good, FamilyAttemptOutcome.Accepted, null)]));
        // a rejected attempt of a foreign id is also not silently accepted as "nothing"
        Assert.Throws<ArgumentException>(() => arbiter.Arbitrate([new(DetectorId.Create("global.email"), RiskLevel.Level3,
            ResolutionEvidence.S, span, null, FamilyAttemptOutcome.Rejected, "x")]));
    }

    [Fact]
    public void EmptyAttempts_AreAGenuineEmptyArbitration()
    {
        var result = new UsTaxpayerFamilyArbiter().Arbitrate([]);

        Assert.Empty(result.Emitted);
        Assert.Empty(result.Rejected);
    }

    // ---- the two modes are not confused ---------------------------------------------------------------------------

    [Fact]
    public void TheFamilyAttemptSourcePlusTheArbiter_ReproduceTheModulesTaxFamilyResolverMode_Exactly()
    {
        var mismatches = new List<string>();
        var arbiter = new UsTaxpayerFamilyArbiter();

        foreach (string raw in Corpus.Generate(seed: 20260602, count: 3000))
        {
            var viaProduct = arbiter.Arbitrate(UsTaxpayerFamilyAttemptSource.Collect(raw)).Emitted
                .Select(c => $"{c.DetectorId.Value}|{c.Span.Start}:{c.Span.Length}|{c.Canonical!.Value.Value}|{c.Evidence}")
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            var viaModule = ModuleFamily(raw);

            if (!viaProduct.SequenceEqual(viaModule))
            {
                mismatches.Add(Visible(raw));
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches: " + string.Join(" // ", mismatches.Take(5)));
    }

    // O-1 (field ownership) CHARACTERIZATION -- the policy is NOT changed or decided here.
    // The three TargetDetectorOnly detectors and the family mode of the same core disagree only where a number sits inside
    // a field labelled for ANOTHER taxpayer type: the family mode still reports its structure alone (evidence S), the
    // per-type detectors do not. If the three were registered as independent detectors, that structure-only protection
    // would be lost; that is a decision for O-1, not for this adapter.
    [Fact]
    public void O1_TheIndependentDetectorsNeverExceedTheFamilyMode_AndTheyFallShortOnlyByStructureOnlyMatches()
    {
        int divergent = 0;

        foreach (string raw in Corpus.Generate(seed: 20260604, count: 3000))
        {
            var independent = new IDetector[] { new UsAtinCandidateDetector(), new UsItinCandidateDetector(), new UsSsnCandidateDetector() }
                .SelectMany(d => Run(d, raw))
                .Select(c => $"{c.DetectorId.Value}|{c.Span.Start}:{c.Span.Length}|{c.Canonical!.Value.Value}|{c.Evidence}")
                .ToHashSet(StringComparer.Ordinal);
            var family = ModuleFamily(raw);

            Assert.Subset(family.ToHashSet(StringComparer.Ordinal), independent);

            var missing = family.Where(f => !independent.Contains(f)).ToList();
            Assert.All(missing, m => Assert.EndsWith("|S", m)); // structure only, never a context-bound (S+C) candidate
            if (missing.Count > 0) divergent++;
        }

        Assert.True(divergent > 0, "the divergence is real on this corpus, so the characterization is not vacuous");
    }

    private static List<string> ModuleFamily(string raw) =>
        UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw).Candidates
            .Select(c => $"{IdOf(c.Kind)}|{c.Span.Start}:{c.Span.Length}|{c.Canonical.GetDigitsForTestingOnly()}|{(c.Evidence == (UsSsnEvidence.Structure | UsSsnEvidence.Context) ? ResolutionEvidence.SC : ResolutionEvidence.S)}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void TheThreeTargetDetectorsAlone_NeverEmitTwoTaxpayerTypesForOneRawValue_OnTheGeneratedCorpus()
    {
        foreach (string raw in Corpus.Generate(seed: 20260603, count: 3000))
        {
            var taxpayer = new IDetector[] { new UsAtinCandidateDetector(), new UsItinCandidateDetector(), new UsSsnCandidateDetector() }
                .SelectMany(d => Run(d, raw)).ToList();

            for (int i = 0; i < taxpayer.Count; i++)
            {
                for (int j = i + 1; j < taxpayer.Count; j++)
                {
                    Assert.False(taxpayer[i].Span.OverlapsWith(taxpayer[j].Span), "two taxpayer types claimed overlapping text");
                }
            }
        }
    }

    private static string Visible(string raw) => raw.Replace("\n", "\\n").Replace("\t", "\\t");

    private static string IdOf(UsTaxpayerKind kind) => kind switch
    {
        UsTaxpayerKind.Atin => "us.atin",
        UsTaxpayerKind.Itin => "us.itin",
        _ => "us.ssn",
    };

    // ---- aliases and policy: compatibility with the existing downstream contracts ------------------------------

    [Theory]
    [InlineData(PiiType.UsSsn, "[SSN1]")]
    [InlineData(PiiType.UsItin, "[ITIN1]")]
    [InlineData(PiiType.UsAtin, "[ATIN1]")]
    [InlineData(PiiType.UsMbi, "[MBI1]")]
    [InlineData(PiiType.UsSevis, "[SEVIS1]")]
    public void AliasTokens_ForTheFiveTypes_MatchTheFrozenContractTokens(PiiType type, string token)
    {
        Assert.Equal(token, new AliasIdentity(AliasFamilyTable.FamilyOf(type), 1).CanonicalContractToken);
        Assert.Equal(token, AliasToken(type));
    }

    private static string AliasToken(PiiType type)
    {
        var map = new AliasMap();
        return map.GetOrAdd(new CanonicalValue(type, "x")).Value;
    }

    [Fact]
    public void TheAliasMap_NumbersEqualValuesTogether_AndDifferentValuesSeparately_ForTheNewTypes()
    {
        var map = new AliasMap();
        var candidates = RunAll("SSN: 321-54-9876 SSN: 123-45-6789 SSN: 321-54-9876 MBI: 1A23C45DE67");

        var tokens = candidates.OrderBy(c => c.Span.Start).Select(c => map.GetOrAdd(c.Canonical!.Value).Value).ToList();

        Assert.Equal(["[SSN1]", "[SSN2]", "[SSN1]", "[MBI1]"], tokens);
    }

    [Fact]
    public void CurrentProductPolicy_TreatsEveryLevel3CandidateAsNeedsDecision_WhileTheFrozenD3AOracleExpectsProtect_ContractImplementationGap()
    {
        // OBSERVATION, not a fix: CandidatePolicyEvaluator.Decide returns NeedsDecision for ALL Level 3 candidates (its
        // "defense-in-depth" rule), so none of the five U.S. identifiers receives an alias through today's policy path,
        // whereas every D3A DetectorPipeline fixture for them states disposition=Protect and render=`... [SSN1]`.
        // Changing the evaluator is a production change outside this task's approved boundary.
        var candidate = Run(new UsSsnCandidateDetector(), "SSN: 321-54-9876").Single();
        var evaluated = new EvaluatedCandidate(candidate, TrustState.Unknown);

        Assert.Equal(CandidateDisposition.NeedsDecision, CandidatePolicyEvaluator.Decide(evaluated));
        var assigned = AliasAssigner.Assign(CandidatePolicyEvaluator.Evaluate([evaluated]), new AliasMap());
        Assert.Null(assigned.Single().Alias);
    }

    // ---- privacy ---------------------------------------------------------------------------------------------------

    [Fact]
    public void NoDiagnosticSurface_EverShowsAnIdentifierOrAnyPartOfIt()
    {
        const string raw = "ATIN: 912-93-4567 ITIN: 912-70-1234 SSN: 321-54-9876 MBI: 1A23C45DE67 SEVIS: N1234567890 SSN: 000-12-3456";
        var candidates = RunAll(raw);
        var resolved = Resolve(candidates, raw);
        var attempts = UsTaxpayerFamilyAttemptSource.Collect(raw);
        var arbitrated = new UsTaxpayerFamilyArbiter().Arbitrate(attempts);
        var pipeline = new DetectionPipeline(AllFive()).Detect(raw);
        var thrown = Record.Exception(() => new UsTaxpayerFamilyArbiter().Arbitrate([new(DetectorId.Create("us.mbi"),
            RiskLevel.Level3, ResolutionEvidence.S, new RawSpan(0, 11), new CanonicalValue(PiiType.UsMbi, "1A23C45DE67"),
            FamilyAttemptOutcome.Accepted, null)]));

        var outputs = new List<string>();
        outputs.AddRange(AllFive().Select(d => d.ToString()!));
        outputs.AddRange(candidates.Select(c => c.ToString()));
        outputs.AddRange(candidates.Select(c => c.Canonical!.Value.ToString()));
        outputs.Add(resolved.ToString());
        outputs.AddRange(resolved.Winners.Select(w => w.ToString()));
        outputs.AddRange(attempts.Select(a => a.ToString()));
        outputs.Add(arbitrated.ToString());
        outputs.AddRange(arbitrated.Emitted.Select(e => e.ToString()));
        outputs.AddRange(arbitrated.Rejected.Select(r => r.ToString()));
        outputs.Add(pipeline.ToString());
        outputs.Add(thrown!.ToString());

        foreach (string output in outputs)
        {
            foreach (string fragment in new[]
                     { "912", "934567", "9345", "701234", "321549876", "321-54", "9876", "123456", "1A23", "C45", "DE67", "N12345", "34567890", "000-12" })
            {
                Assert.DoesNotContain(fragment, output);
            }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static IEnumerable<List<T>> Permutations<T>(List<T> items)
    {
        if (items.Count <= 1)
        {
            yield return [.. items];
            yield break;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var rest = new List<T>(items);
            rest.RemoveAt(i);
            foreach (var tail in Permutations(rest))
            {
                tail.Insert(0, items[i]);
                yield return tail;
            }
        }
    }
}

/// <summary>Deterministic synthetic text built from label, value and joiner fragments.</summary>
internal static class Corpus
{
    private static readonly string[] Labels =
    [
        "ATIN: ", "ITIN: ", "SSN: ", "Social Security Number: ", "Adoption Taxpayer Identification Number: ", "ssn=", "ITIN #",
        "MBI: ", "SEVIS ID: ", "Phone: ", "Name: ", "I-94: ", "Address: ", "", "", "",
    ];

    private static readonly string[] Values =
    [
        "912-93-4567", "912 93 4567", "912934567", "912-70-1234", "912 70 1234", "912701234", "912-94-1234", "912-88-4321",
        "912-89-1234", "912-69-1234", "321-54-9876", "321 54 9876", "321549876", "123-45-6789", "000-12-3456", "666-12-3456",
        "900-12-3456", "321-00-9876", "321-54-0000", "321.54.9876", "1A23C45DE67", "1a23-c45-de67", "1B23C45DE67",
        "N1234567890", "N123456789", "n1234567890", "X321-54-9876Y", "12345", "hello",
    ];

    private static readonly string[] Joiners = [" ", "\n", "; ", " | ", "\t", ", ", " and "];

    public static IEnumerable<string> Generate(int seed, int count)
    {
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            int parts = rng.Next(1, 5);
            var sb = new System.Text.StringBuilder();
            for (int p = 0; p < parts; p++)
            {
                if (p > 0) sb.Append(Joiners[rng.Next(Joiners.Length)]);
                sb.Append(Labels[rng.Next(Labels.Length)]).Append(Values[rng.Next(Values.Length)]);
            }

            yield return sb.ToString();
        }
    }
}
