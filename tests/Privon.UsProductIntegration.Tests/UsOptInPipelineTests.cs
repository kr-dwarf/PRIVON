using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Family;
using Privon.UsProductIntegration;
using Privon.UsSsn;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. The U.S. implementations plugged into the product's REAL opt-in DetectionPipeline constructor.
public class UsOptInPipelineTests
{
    private static IReadOnlyList<DetectionCandidate> Pipe(string raw) => UsOptInPipeline.Create().Detect(raw).Candidates;

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    [Fact]
    public void ThePipeline_ProducesTheFrozenFiveRecords_ThroughTheProductsOwnConstructor()
    {
        const string raw = "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890";

        var found = Pipe(raw);

        Assert.Equal(
            [
                "us.atin|UsAtin|L3|S+C|6:11|912934567|[ATIN1]|50",
                "us.itin|UsItin|L3|S+C|24:11|912701234|[ITIN1]|60",
                "us.ssn|UsSsn|L3|S+C|41:11|321549876|[SSN1]|70",
                "us.mbi|UsMbi|L3|S+C|58:11|1A23C45DE67|[MBI1]|80",
                "us.sevis|UsSevis|L3|S+C|77:11|N1234567890|[SEVIS1]|90",
            ],
            found.OrderBy(c => c.Span.Start).Select(Oracle));
    }

    [Fact]
    public void TheRegistryHoldsAllFive_ButTheTaxpayerDetectorsOwnOutputIsNeverFinal()
    {
        // O-1: "ATIN: 912-88-4321" is an ITIN by structure. The target-only ITIN detector skips it (the label belongs to
        // ATIN); only the family path, which the pipeline uses, still protects it.
        const string raw = "ATIN: 912-88-4321";

        var viaPipeline = Assert.Single(Pipe(raw));

        Assert.Equal("us.itin", viaPipeline.DetectorId.Value);
        Assert.Equal(ResolutionEvidence.S, viaPipeline.Evidence);
        Assert.Empty(Run(new UsItinCandidateDetector(), raw));
    }

    [Fact]
    public void TheOptInPipeline_EqualsTheAssembledFlow_OnAGeneratedCorpus_WithAndWithoutLegacyDetectors()
    {
        var mismatches = new List<string>();
        var plain = UsOptInPipeline.Create();
        var flow = new UsDetectionFlow();
        var mixedPipeline = UsOptInPipeline.Create(Legacy());
        var mixedFlow = new UsDetectionFlow(new UsTaxpayerFamilyArbiterProvider(), Legacy(), UsTaxpayerLabelSet.Default);

        foreach (string raw in Corpus.Generate(seed: 20260801, count: 2000))
        {
            if (!Same(plain.Detect(raw).Candidates, flow.Detect(raw).Candidates)) mismatches.Add("plain:" + Show(raw));
            if (!Same(mixedPipeline.Detect(raw).Candidates, mixedFlow.Detect(raw).Candidates)) mismatches.Add("mixed:" + Show(raw));
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches: " + string.Join(" // ", mismatches.Take(5)));
    }

    [Fact]
    public void WithoutAnArbiter_ThePipelineFailsClosed_ForEveryText()
    {
        var pipeline = UsOptInPipeline.Create(arbiterProvider: new EmptyDetectorFamilyArbiterProvider());

        Assert.Throws<FamilyArbitrationUnavailableException>(() => pipeline.Detect("SSN: 321-54-9876"));
        Assert.Throws<FamilyArbitrationUnavailableException>(() => pipeline.Detect("nothing here"));
        Assert.Throws<FamilyArbitrationUnavailableException>(() => pipeline.Detect(""));
    }

    [Fact]
    public void ACompletedScanWithNothingFound_IsEmpty_AndDifferentFromTheFailure()
    {
        Assert.Empty(UsOptInPipeline.Create().Detect("SSN: 000-12-3456 and MBI: 1B23C45DE67").Candidates);
    }

    [Fact]
    public void TheFamilyAttemptSource_ListsTheThreeMemberIds_AndRefusesNulls()
    {
        var source = new UsTaxpayerFamilyAttempts();

        Assert.Equal(["us.atin", "us.itin", "us.ssn"], source.MemberDetectorIds.Select(i => i.Value));
        Assert.Equal(UsTaxpayerFamilyArbiter.TaxpayerFamilyName, source.FamilyName);
        Assert.Throws<ArgumentNullException>(() => new UsTaxpayerFamilyAttempts(null!));
        Assert.Throws<ArgumentNullException>(() => source.CollectAttempts(null!));
    }

    [Fact]
    public void TheDefaultPipeline_StillHasNoUsDetector_AfterTheOptInPipelineWasBuilt()
    {
        _ = UsOptInPipeline.Create();

        var found = DetectionPipeline.CreateDefault().Detect("SSN: 321-54-9876 MBI: 1A23C45DE67 N1234567890");

        Assert.DoesNotContain(found.Candidates, c => c.DetectorId.Value.StartsWith("us.", StringComparison.Ordinal));
    }

    private static bool Same(IReadOnlyList<DetectionCandidate> a, IReadOnlyList<DetectionCandidate> b) =>
        a.OrderBy(c => c.Span.Start).Select(Key).SequenceEqual(b.OrderBy(c => c.Span.Start).Select(Key));

    private static string Key(DetectionCandidate c) =>
        $"{c.DetectorId.Value}|{c.Span.Start}:{c.Span.Length}|{c.Canonical?.Value}|{c.Evidence}|{c.Confidence}|{c.RiskLevel}";

    private static string Show(string raw) => raw.Replace("\n", "\\n").Replace("\t", "\\t");
}
