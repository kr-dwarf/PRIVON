using System.Diagnostics.CodeAnalysis;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Family;
using Privon.UsProductIntegration;
using Privon.UsSsn;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. The assembled flow  five U.S. types -> Taxpayer Family arbitration (the REAL
// DetectorFamilyArbitrationBoundary) -> the REAL CandidateResolver -> ContextRiskAdjuster, driven only through public
// product types. The downstream policy / alias / replacement stages are exercised in Privon.App.Tests, where the
// internal ClipboardPrivacyProcessor is reachable.
public class UsDetectionFlowTests
{
    private static IReadOnlyList<DetectionCandidate> Flow(string raw) => new UsDetectionFlow().Detect(raw).Candidates;

    private sealed class SpyProvider(IDetectorFamilyArbiter arbiter) : IDetectorFamilyArbiterProvider
    {
        public List<string> Requested { get; } = [];

        public bool TryGetArbiter(string familyName, [MaybeNullWhen(false)] out IDetectorFamilyArbiter result)
        {
            Requested.Add(familyName);
            result = arbiter;
            return true;
        }
    }

    private sealed class FixedArbiter(Func<IReadOnlyList<FamilyCandidateAttempt>, FamilyArbitrationResult?> arbitrate) : IDetectorFamilyArbiter
    {
        public string FamilyName => UsTaxpayerFamilyArbiter.TaxpayerFamilyName;

        public int Calls { get; private set; }

        public FamilyArbitrationResult Arbitrate(IReadOnlyList<FamilyCandidateAttempt> attempts)
        {
            Calls++;
            return arbitrate(attempts)!;
        }
    }

    // ---- normal candidates -------------------------------------------------------------------------------------

    [Fact]
    public void AllFiveTypes_AreProduced_WithTheFrozenRecords_InTextOrder()
    {
        const string raw = "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890";

        var found = Flow(raw);

        Assert.Equal(
            [
                "us.atin|UsAtin|L3|S+C|6:11|912934567|[ATIN1]|50",
                "us.itin|UsItin|L3|S+C|24:11|912701234|[ITIN1]|60",
                "us.ssn|UsSsn|L3|S+C|41:11|321549876|[SSN1]|70",
                "us.mbi|UsMbi|L3|S+C|58:11|1A23C45DE67|[MBI1]|80",
                "us.sevis|UsSevis|L3|S+C|77:11|N1234567890|[SEVIS1]|90",
            ],
            found.OrderBy(c => c.Span.Start).Select(Oracle));
        Assert.All(found, c => Assert.Equal(c.Canonical!.Value.Value, Slice(raw, c).Replace("-", "").Replace(" ", "")));
    }

    [Fact]
    public void ARepeatedValue_IsOneCandidatePerOccurrence_NeverDuplicatedOnTheSameSpan()
    {
        const string raw = "SSN: 321-54-9876 and again 321-54-9876";

        var found = Flow(raw);

        Assert.Equal(["5:11", "27:11"], found.Select(c => $"{c.Span.Start}:{c.Span.Length}"));
        Assert.Equal(found.Count, found.Select(c => c.Span).Distinct().Count());
    }

    // ---- O-1: nothing the family mode protects is lost -----------------------------------------------------------

    [Theory]
    [InlineData("ATIN: 912-88-4321", "us.itin", "912884321")]   // a valid ITIN under another type's label
    [InlineData("ITIN: 321-54-9876", "us.ssn", "321549876")]    // a valid SSN under another type's label
    [InlineData("ITIN: 912-93-4567", "us.atin", "912934567")]   // a valid ATIN under another type's label
    [InlineData("SSN: 912-70-1234", "us.itin", "912701234")]
    public void O1_AStructurallyValidNumberUnderAnotherTaxLabel_IsStillProtected_ByStructureAlone(
        string raw, string expectedId, string expectedValue)
    {
        var viaFlow = Assert.Single(Flow(raw));
        var viaIndependentDetectors = RunAll(raw);

        Assert.Equal(expectedId, viaFlow.DetectorId.Value);
        Assert.Equal(expectedValue, viaFlow.Canonical!.Value.Value);
        Assert.Equal(ResolutionEvidence.S, viaFlow.Evidence); // the label belongs to another type: structure only
        // The preserved fact: the union of the three TargetDetectorOnly detectors would have missed it.
        Assert.DoesNotContain(viaIndependentDetectors, c => c.DetectorId.Value == expectedId);
    }

    [Fact]
    public void TheFlowEqualsTheTaxFamilyResolverMode_ForTheTaxpayerTypes_OnAGeneratedCorpus()
    {
        var taxIds = new[] { "us.atin", "us.itin", "us.ssn" };
        var mismatches = new List<string>();

        foreach (string raw in Corpus.Generate(seed: 20260701, count: 3000))
        {
            var viaFlow = Flow(raw).Where(c => taxIds.Contains(c.DetectorId.Value))
                .Select(c => $"{c.DetectorId.Value}|{c.Span.Start}:{c.Span.Length}|{c.Canonical!.Value.Value}|{c.Evidence}")
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            var viaModule = UsTaxpayerFamily.EvaluateTaxFamilyResolver(raw).Candidates
                .Select(c => $"{(c.Kind == UsTaxpayerKind.Atin ? "us.atin" : c.Kind == UsTaxpayerKind.Itin ? "us.itin" : "us.ssn")}|{c.Span.Start}:{c.Span.Length}|{c.Canonical.GetDigitsForTestingOnly()}|{(c.Evidence == (UsSsnEvidence.Structure | UsSsnEvidence.Context) ? ResolutionEvidence.SC : ResolutionEvidence.S)}")
                .OrderBy(x => x, StringComparer.Ordinal).ToList();

            if (!viaFlow.SequenceEqual(viaModule)) mismatches.Add(raw.Replace("\n", "\\n").Replace("\t", "\\t"));
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches: " + string.Join(" // ", mismatches.Take(5)));
    }

    [Fact]
    public void NoTwoCandidates_OfTheFlow_EverClaimOverlappingText_OnAGeneratedCorpus()
    {
        foreach (string raw in Corpus.Generate(seed: 20260702, count: 3000))
        {
            var found = Flow(raw);

            for (int i = 0; i < found.Count; i++)
            {
                for (int j = i + 1; j < found.Count; j++)
                {
                    Assert.False(found[i].Span.OverlapsWith(found[j].Span), "two candidates claimed overlapping text");
                }
            }
        }
    }

    // ---- rejected verdicts are never candidates -----------------------------------------------------------------

    [Theory]
    [InlineData("SSN: 000-12-3456")]
    [InlineData("SSN: 666-12-3456")]
    [InlineData("ITIN: 912-89-1234")]
    [InlineData("ATIN: 912-88-4321x")]
    [InlineData("SSN: 321.54.9876")]
    [InlineData("MBI: 1B23C45DE67")]
    [InlineData("SEVIS: N123456789")]
    public void ARejectedVerdict_NeverBecomesACandidate(string raw)
    {
        Assert.Empty(Flow(raw));
    }

    [Fact]
    public void WhenAHigherPriorityTypeAccepts_TheSupersededTypesAreNotEmittedForTheSameText()
    {
        var found = Flow("ATIN: 912-93-4567");

        Assert.Equal("us.atin", Assert.Single(found).DetectorId.Value);
    }

    // ---- arbitration happens first, through the real boundary ----------------------------------------------------

    [Fact]
    public void TheFlowAsksTheRealBoundaryForTheTaxpayerFamily_OnceForEveryDetection_EvenWithNoTaxpayerText()
    {
        var spy = new SpyProvider(new UsTaxpayerFamilyArbiter());
        var flow = new UsDetectionFlow(spy, [], UsTaxpayerLabelSet.Default);

        _ = flow.Detect("SSN: 321-54-9876");
        _ = flow.Detect("no identifiers here");
        _ = flow.Detect("");

        Assert.Equal(Enumerable.Repeat(UsTaxpayerFamilyArbiter.TaxpayerFamilyName, 3), spy.Requested);
    }

    [Fact]
    public void TheGeneralResolverConsumesTheArbitersOutput_SoArbitrationRunsBeforeIt()
    {
        // An arbiter that (wrongly) emits a candidate whose span is outside the text: the REAL CandidateResolver (R0)
        // rejects it. That can only happen if the resolver ran on the arbiter's output, i.e. after arbitration.
        const string raw = "SSN: 321-54-9876";
        var real = Run(new UsSsnCandidateDetector(), raw).Single();
        var arbiter = new FixedArbiter(_ => new FamilyArbitrationResult([real with { Span = new RawSpan(0, 500) }], []));
        var flow = new UsDetectionFlow(new SpyProvider(arbiter), [], UsTaxpayerLabelSet.Default);

        Assert.Throws<InvalidCandidateSpanException>(() => flow.Detect(raw));
        Assert.Equal(1, arbiter.Calls);
    }

    [Fact]
    public void AConflictInsideTheArbitersOwnOutput_IsStillResolvedByTheGeneralResolver_ByTheProductsRankOrder()
    {
        const string raw = "912-93-4567";
        var atin = Run(new UsAtinCandidateDetector(), raw).Single();
        var itin = atin with { PiiType = PiiType.UsItin, DetectorId = DetectorId.Create("us.itin"), ResolutionRank = 60,
            Canonical = new CanonicalValue(PiiType.UsItin, "912934567") };
        var arbiter = new FixedArbiter(_ => new FamilyArbitrationResult([itin, atin], []));
        var flow = new UsDetectionFlow(new SpyProvider(arbiter), [], UsTaxpayerLabelSet.Default);

        var found = flow.Detect(raw).Candidates;

        Assert.Equal("us.atin", Assert.Single(found).DetectorId.Value);
    }

    [Fact]
    public void ARejectedAttemptReturnedByTheArbiter_IsNeverTurnedIntoACandidate()
    {
        const string raw = "SSN: 000-12-3456";
        var arbiter = new FixedArbiter(attempts => new FamilyArbitrationResult([], attempts));
        var flow = new UsDetectionFlow(new SpyProvider(arbiter), [], UsTaxpayerLabelSet.Default);

        Assert.Empty(flow.Detect(raw).Candidates);
        Assert.Equal(1, arbiter.Calls);
    }

    // ---- unresolved policy is a failure, never "no candidates" ---------------------------------------------------

    [Fact]
    public void WhenNoArbiterIsAvailable_TheFlowFailsClosed_ForAnyText()
    {
        var flow = new UsDetectionFlow(new EmptyDetectorFamilyArbiterProvider(), [], UsTaxpayerLabelSet.Default);

        Assert.Throws<UsFamilyArbitrationUnavailableException>(() => flow.Detect("SSN: 321-54-9876"));
        Assert.Throws<UsFamilyArbitrationUnavailableException>(() => flow.Detect("no identifiers here"));
        Assert.Throws<UsFamilyArbitrationUnavailableException>(() => flow.Detect(""));
    }

    [Fact]
    public void AProviderForAnotherFamilyName_IsUnavailable_NotEmpty()
    {
        var provider = new OnlyForProvider("some.other.family");
        var flow = new UsDetectionFlow(provider, [], UsTaxpayerLabelSet.Default);

        Assert.Throws<UsFamilyArbitrationUnavailableException>(() => flow.Detect("SSN: 321-54-9876"));
    }

    private sealed class OnlyForProvider(string name) : IDetectorFamilyArbiterProvider
    {
        public bool TryGetArbiter(string familyName, [MaybeNullWhen(false)] out IDetectorFamilyArbiter arbiter)
        {
            arbiter = familyName == name ? new UsTaxpayerFamilyArbiter() : null;
            return arbiter is not null;
        }
    }

    [Fact]
    public void AnArbiterThatReturnsNothing_IsNotTreatedAsEmpty()
    {
        var flow = new UsDetectionFlow(new SpyProvider(new FixedArbiter(_ => null)), [], UsTaxpayerLabelSet.Default);

        Assert.Throws<NullFamilyArbitrationResultException>(() => flow.Detect("SSN: 321-54-9876"));
    }

    [Fact]
    public void AnArbiterFailure_Propagates()
    {
        var flow = new UsDetectionFlow(
            new SpyProvider(new FixedArbiter(_ => throw new InvalidOperationException("synthetic arbiter failure"))),
            [], UsTaxpayerLabelSet.Default);

        Assert.Throws<InvalidOperationException>(() => flow.Detect("SSN: 321-54-9876"));
    }

    [Fact]
    public void ANullTextOrNullArguments_AreRejected()
    {
        var flow = new UsDetectionFlow();

        Assert.Throws<ArgumentNullException>(() => flow.Detect(null!));
        Assert.Throws<ArgumentNullException>(() => new UsDetectionFlow(null!, [], UsTaxpayerLabelSet.Default));
        Assert.Throws<ArgumentNullException>(() => new UsDetectionFlow(new UsTaxpayerFamilyArbiterProvider(), null!, UsTaxpayerLabelSet.Default));
        Assert.Throws<ArgumentNullException>(() => new UsDetectionFlow(new UsTaxpayerFamilyArbiterProvider(), [], null!));
    }

    [Fact]
    public void TheIndividualTaxpayerDetectors_CannotBeAddedAsFlowDetectors_TheirOutputIsNeverFinal()
    {
        foreach (IDetector taxpayer in new IDetector[] { new UsAtinCandidateDetector(), new UsItinCandidateDetector(), new UsSsnCandidateDetector() })
        {
            Assert.Throws<ArgumentException>(() =>
                new UsDetectionFlow(new UsTaxpayerFamilyArbiterProvider(), [taxpayer], UsTaxpayerLabelSet.Default));
        }
    }

    // ---- other detectors in the same flow ------------------------------------------------------------------------

    [Fact]
    public void KoreanAndGlobalDetectors_CanRunInTheSameFlow_WithoutDisturbingTheirOwnCandidates()
    {
        IDetector[] legacy = [new EmailDetector(), new PhoneDetector(), new ResidentRegistrationNumberDetector()];
        var flow = new UsDetectionFlow(new UsTaxpayerFamilyArbiterProvider(), legacy, UsTaxpayerLabelSet.Default);
        const string raw = "mail a.b@example.com, 010-1234-5678, 901231-1234567, SSN: 321-54-9876";

        var viaFlow = flow.Detect(raw).Candidates;
        var viaLegacyOnly = new DetectionPipeline(legacy).Detect(raw).Candidates;

        Assert.Equal(
            viaLegacyOnly.OrderBy(c => c.Span.Start).Select(c => (c.PiiType, c.Span)),
            viaFlow.Where(c => c.PiiType is not (PiiType.UsSsn or PiiType.UsItin or PiiType.UsAtin or PiiType.UsMbi or PiiType.UsSevis))
                .OrderBy(c => c.Span.Start).Select(c => (c.PiiType, c.Span)));
        Assert.Contains(viaFlow, c => c.PiiType == PiiType.UsSsn);
    }

    [Fact]
    public void ADetectorThatFails_IsNotSwallowed()
    {
        var flow = new UsDetectionFlow(new UsTaxpayerFamilyArbiterProvider(), [new ThrowingDetector()], UsTaxpayerLabelSet.Default);

        Assert.Throws<InvalidOperationException>(() => flow.Detect("SSN: 321-54-9876"));
    }

    private sealed class ThrowingDetector : IDetector
    {
        public string Name => "ThrowingDetector";
        public PiiType PiiType => PiiType.Secret;
        public DetectorId Id { get; } = DetectorId.Create("test.us_flow_throwing");
        public int ResolutionRank => 900001;
        public DetectorReadinessState Readiness => DetectorReadinessState.Ready;
        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) =>
            throw new InvalidOperationException("synthetic detector failure");
    }

    // ---- privacy -----------------------------------------------------------------------------------------------------

    [Fact]
    public void FlowResultsAndFailures_NeverExposeAnIdentifier()
    {
        const string raw = "ATIN: 912-93-4567 SSN: 321-54-9876 MBI: 1A23C45DE67 SEVIS: N1234567890";
        var result = new UsDetectionFlow().Detect(raw);
        var failure = Record.Exception(() =>
            new UsDetectionFlow(new EmptyDetectorFamilyArbiterProvider(), [], UsTaxpayerLabelSet.Default).Detect(raw));
        var invalidSpan = Record.Exception(() => new UsDetectionFlow(
            new SpyProvider(new FixedArbiter(_ => new FamilyArbitrationResult(
                [Run(new UsSsnCandidateDetector(), "SSN: 321-54-9876").Single() with { Span = new RawSpan(0, 900) }], []))),
            [], UsTaxpayerLabelSet.Default).Detect("SSN: 321-54-9876"));

        var outputs = new List<string> { result.ToString(), failure!.ToString(), invalidSpan!.ToString() };
        outputs.AddRange(result.Candidates.Select(c => c.ToString()));

        foreach (string output in outputs)
        {
            foreach (string fragment in new[] { "912", "93-4567", "934567", "321-54", "321549876", "9876", "1A23", "C45", "N12345", "34567890" })
            {
                Assert.DoesNotContain(fragment, output);
            }
        }
    }
}
