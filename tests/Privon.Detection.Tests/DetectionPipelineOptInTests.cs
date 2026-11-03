using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Family;

namespace Privon.Detection.Tests;

// SYNTHETIC DATA ONLY. The explicit opt-in DetectionPipeline constructor (registry + family arbiter provider + family
// sources), exercised with pure product types and caller-supplied doubles: Privon.Detection ships no source, arbiter or
// U.S. grammar, and these tests reference none. Constructing a registry or pipeline here registers nothing in the product.
public sealed class DetectionPipelineOptInTests
{
    // The frozen D2.2 ids are the only registrable ones, so the doubles borrow ids from the closed table.
    private static readonly DetectorId AtinId = DetectorId.Create("us.atin");
    private static readonly DetectorId ItinId = DetectorId.Create("us.itin");
    private static readonly DetectorId SsnId = DetectorId.Create("us.ssn");
    private static readonly DetectorId MbiId = DetectorId.Create("us.mbi");

    private const string FamilyName = "test.family";

    private sealed class Probe(DetectorId id, int rank, PiiType type, Func<DetectionContext, IReadOnlyList<DetectionCandidate>> detect, List<string>? log = null)
        : IDetector
    {
        public DetectorReadinessState State { get; set; } = DetectorReadinessState.Ready;
        public int Calls { get; private set; }
        public string Name => "Probe(" + id + ")";
        public PiiType PiiType => type;
        public DetectorId Id => id;
        public int ResolutionRank => rank;
        public DetectorReadinessState Readiness => State;

        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
        {
            Calls++;
            log?.Add("detector:" + id);
            return detect(context);
        }
    }

    private static DetectionCandidate Candidate(
        DetectorId id, int rank, PiiType type, int start, int length, string value, ResolutionEvidence evidence = ResolutionEvidence.S) =>
        new(type, new RawSpan(start, length), RiskLevel.Level3, DetectionConfidence.High, new CanonicalValue(type, value),
            "Probe", id, evidence, rank);

    private sealed class Source(
        IReadOnlyCollection<DetectorId> members, Func<DetectionContext, IReadOnlyList<FamilyCandidateAttempt>> collect, List<string>? log = null)
        : IDetectorFamilyAttemptSource
    {
        public string FamilyName { get; init; } = DetectionPipelineOptInTests.FamilyName;
        public IReadOnlyCollection<DetectorId> MemberDetectorIds => members;

        public IReadOnlyList<FamilyCandidateAttempt> CollectAttempts(DetectionContext context)
        {
            log?.Add("source");
            return collect(context);
        }
    }

    private sealed class Arbiter(Func<IReadOnlyList<FamilyCandidateAttempt>, FamilyArbitrationResult?> arbitrate, List<string>? log = null)
        : IDetectorFamilyArbiter
    {
        public string FamilyName => DetectionPipelineOptInTests.FamilyName;
        public int Calls { get; private set; }

        public FamilyArbitrationResult Arbitrate(IReadOnlyList<FamilyCandidateAttempt> attempts)
        {
            Calls++;
            log?.Add("arbiter");
            return arbitrate(attempts)!;
        }
    }

    private sealed class Provider(IDetectorFamilyArbiter? arbiter) : IDetectorFamilyArbiterProvider
    {
        public List<string> Requested { get; } = [];

        public bool TryGetArbiter(string familyName, [MaybeNullWhen(false)] out IDetectorFamilyArbiter result)
        {
            Requested.Add(familyName);
            result = arbiter;
            return arbiter is not null;
        }
    }

    // Accepted attempts become the emitted candidates (a faithful, trivial arbiter); rejected ones are reported, never emitted.
    private static FamilyArbitrationResult AcceptedAreEmitted(IReadOnlyList<FamilyCandidateAttempt> attempts) => new(
        attempts.Where(a => a.Outcome == FamilyAttemptOutcome.Accepted)
            .Select(a => Candidate(a.DetectorId, a.DetectorId.Value == "us.atin" ? 50 : a.DetectorId.Value == "us.itin" ? 60 : 70,
                a.DetectorId.Value == "us.atin" ? PiiType.UsAtin : a.DetectorId.Value == "us.itin" ? PiiType.UsItin : PiiType.UsSsn,
                a.Span.Start, a.Span.Length, a.Canonical!.Value.Value, a.Evidence)).ToList(),
        attempts.Where(a => a.Outcome == FamilyAttemptOutcome.Rejected).ToList());

    private static FamilyCandidateAttempt Accept(DetectorId id, PiiType type, int start, int length, string value) =>
        new(id, RiskLevel.Level3, ResolutionEvidence.S, new RawSpan(start, length), new CanonicalValue(type, value),
            FamilyAttemptOutcome.Accepted, null);

    private static FamilyCandidateAttempt Reject(DetectorId id, int start, int length, string reason) =>
        new(id, RiskLevel.Level3, ResolutionEvidence.S, new RawSpan(start, length), null, FamilyAttemptOutcome.Rejected, reason);

    private static (DetectionPipeline Pipeline, Probe Mbi, Probe Atin, Probe Itin, Probe Ssn, Arbiter Arbiter, Provider Provider, List<string> Log)
        Build(Func<IReadOnlyList<FamilyCandidateAttempt>, FamilyArbitrationResult?>? arbitrate = null,
              Func<DetectionContext, IReadOnlyList<FamilyCandidateAttempt>>? attempts = null)
    {
        var log = new List<string>();
        var mbi = new Probe(MbiId, 80, PiiType.UsMbi, c => c.View.RawText.Contains("MBI")
            ? [Candidate(MbiId, 80, PiiType.UsMbi, 0, 3, "MBI")] : [], log);
        // The family members' own Detect must never run in the opt-in pipeline.
        IReadOnlyList<DetectionCandidate> Forbidden(DetectionContext _) => throw new InvalidOperationException("member Detect must not run");
        var atin = new Probe(AtinId, 50, PiiType.UsAtin, Forbidden, log);
        var itin = new Probe(ItinId, 60, PiiType.UsItin, Forbidden, log);
        var ssn = new Probe(SsnId, 70, PiiType.UsSsn, Forbidden, log);
        var registry = DetectorRegistry.Create([ssn, mbi, atin, itin]);
        var arbiter = new Arbiter(arbitrate ?? AcceptedAreEmitted, log);
        var provider = new Provider(arbiter);
        var source = new Source([AtinId, ItinId, SsnId], attempts ?? (_ => []), log);
        return (new DetectionPipeline(registry, provider, [source]), mbi, atin, itin, ssn, arbiter, provider, log);
    }

    // ---- the default product is unchanged and not activated -------------------------------------------------------

    [Fact]
    public void CreateDefault_BuildsExactlyTheSameNineDetectors_InTheSameOrder()
    {
        var field = typeof(DetectionPipeline).GetField("_detectors", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var detectors = (IReadOnlyList<IDetector>)field.GetValue(DetectionPipeline.CreateDefault())!;

        Assert.Equal(
            [typeof(PhoneDetector), typeof(EmailDetector), typeof(ResidentRegistrationNumberDetector), typeof(CardNumberDetector),
             typeof(BankAccountNumberDetector), typeof(SecretDetector), typeof(IpAddressDetector), typeof(MacAddressDetector),
             typeof(GpsCoordinateDetector)],
            detectors.Select(d => d.GetType()));
        Assert.DoesNotContain(detectors, d => d.Id.Value.StartsWith("us.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheListAndRegistryOnlyConstructors_StillTakeTheLegacyPath_AndNeverReadAFamilySource()
    {
        var log = new List<string>();
        var probe = new Probe(MbiId, 80, PiiType.UsMbi, _ => [Candidate(MbiId, 80, PiiType.UsMbi, 0, 3, "MBI")], log);
        var registry = DetectorRegistry.Create([probe]);

        var viaList = new DetectionPipeline([probe]).Detect("MBI xx");
        var viaRegistry = new DetectionPipeline(registry).Detect("MBI xx");

        Assert.Single(viaList.Candidates);
        Assert.Single(viaRegistry.Candidates);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public void TheOptInConstructor_ChangesNothingUntilItIsChosen()
    {
        var before = DetectionPipeline.CreateDefault().Detect("a.b@example.com 010-1234-5678");
        _ = Build();
        var after = DetectionPipeline.CreateDefault().Detect("a.b@example.com 010-1234-5678");

        Assert.Equal(before.Candidates.Select(c => (c.PiiType, c.Span)), after.Candidates.Select(c => (c.PiiType, c.Span)));
    }

    // ---- execution order and what runs -----------------------------------------------------------------------------

    [Fact]
    public void OptIn_RunsNonMemberDetectors_ThenTheSourceAndTheArbiter_AndNeverAMembersOwnDetect()
    {
        var (pipeline, mbi, atin, itin, ssn, _, provider, log) = Build(
            attempts: _ => [Accept(SsnId, PiiType.UsSsn, 5, 11, "321549876")]);

        var result = pipeline.Detect("MBI: 321-54-9876");

        Assert.Equal(["detector:" + MbiId, "source", "arbiter"], log);
        Assert.Equal([0, 0, 0], new[] { atin.Calls, itin.Calls, ssn.Calls });
        Assert.Equal(1, mbi.Calls);
        Assert.Equal([FamilyName], provider.Requested);
        Assert.Equal(["us.mbi", "us.ssn"], result.Candidates.Select(c => c.DetectorId.Value).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void TheContractResolverRunsAfterArbitration_SoItValidatesTheArbitersOwnOutput()
    {
        var (pipeline, _, _, _, _, arbiter, _, _) = Build(
            arbitrate: _ => new FamilyArbitrationResult([Candidate(SsnId, 70, PiiType.UsSsn, 0, 900, "321549876")], []),
            attempts: _ => [Accept(SsnId, PiiType.UsSsn, 0, 5, "321549876")]);

        Assert.Throws<InvalidCandidateSpanException>(() => pipeline.Detect("short"));
        Assert.Equal(1, arbiter.Calls);
    }

    [Fact]
    public void OnlyTheArbitersEmittedList_BecomesCandidates_ARejectedAttemptNeverDoes()
    {
        var (pipeline, _, _, _, _, _, _, _) = Build(
            attempts: _ => [Reject(SsnId, 5, 11, "SSN_AREA_000"), Reject(ItinId, 5, 11, "ITIN_RANGE")]);

        Assert.Empty(pipeline.Detect("SSN: 000-12-3456").Candidates);

        var (leaky, _, _, _, _, _, _, _) = Build(
            arbitrate: attempts => new FamilyArbitrationResult([], attempts),
            attempts: _ => [Accept(SsnId, PiiType.UsSsn, 5, 11, "321549876")]);
        Assert.Empty(leaky.Detect("SSN: 321-54-9876").Candidates);
    }

    [Fact]
    public void ExactDuplicates_AreCollapsed_AndAnOverlapIsResolvedByTheFrozenRankOrder()
    {
        var (dup, _, _, _, _, _, _, _) = Build(
            arbitrate: _ => new FamilyArbitrationResult([
                Candidate(SsnId, 70, PiiType.UsSsn, 5, 11, "321549876"), Candidate(SsnId, 70, PiiType.UsSsn, 5, 11, "321549876")], []),
            attempts: _ => []);
        Assert.Single(dup.Detect("SSN: 321-54-9876").Candidates);

        var (overlap, _, _, _, _, _, _, _) = Build(
            arbitrate: _ => new FamilyArbitrationResult([
                Candidate(SsnId, 70, PiiType.UsSsn, 0, 11, "912934567"), Candidate(AtinId, 50, PiiType.UsAtin, 0, 11, "912934567")], []),
            attempts: _ => []);
        Assert.Equal("us.atin", Assert.Single(overlap.Detect("912-93-4567").Candidates).DetectorId.Value);
    }

    [Fact]
    public void AnInvalidSpanFromAnyDetector_FailsTheWholeDetection_NeverClamped()
    {
        var log = new List<string>();
        var bad = new Probe(MbiId, 80, PiiType.UsMbi, _ => [Candidate(MbiId, 80, PiiType.UsMbi, 2, 50, "x")], log);
        var registry = DetectorRegistry.Create([bad]);
        var pipeline = new DetectionPipeline(registry, new Provider(new Arbiter(AcceptedAreEmitted)), []);

        Assert.Throws<InvalidCandidateSpanException>(() => pipeline.Detect("short"));
    }

    [Fact]
    public void Level3_IsNeverTouchedByTheContextRiskAdjustment_InTheOptInPath()
    {
        var (pipeline, _, _, _, _, _, _, _) = Build(attempts: _ => [Accept(SsnId, PiiType.UsSsn, 20, 11, "321549876")]);

        var found = pipeline.Detect("sample example test 321-54-9876 sample").Candidates.Single();

        Assert.Equal(RiskLevel.Level3, found.RiskLevel);
        Assert.Equal(DetectionConfidence.High, found.Confidence);
    }

    // ---- failure is never "no candidates" --------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("nothing to arbitrate")]
    [InlineData("SSN: 321-54-9876")]
    public void WithNoArbiter_EveryDetectionFails_EvenWhenThereIsNothingToArbitrate(string text)
    {
        var log = new List<string>();
        var registry = DetectorRegistry.Create([new Probe(MbiId, 80, PiiType.UsMbi, _ => [], log)]);
        var pipeline = new DetectionPipeline(registry, new Provider(null), [new Source([], _ => [], log)]);

        var ex = Assert.Throws<FamilyArbitrationUnavailableException>(() => pipeline.Detect(text));
        Assert.Equal(FamilyName, ex.FamilyName);
    }

    [Fact]
    public void AnArbiterThatReturnsNothing_AnArbiterFailure_AndASourceFailure_AllPropagate()
    {
        var (nullResult, _, _, _, _, _, _, _) = Build(arbitrate: _ => null);
        var (arbiterFails, _, _, _, _, _, _, _) = Build(arbitrate: _ => throw new InvalidOperationException("synthetic arbiter failure"));
        var (sourceFails, _, _, _, _, _, _, _) = Build(attempts: _ => throw new InvalidOperationException("synthetic source failure"));

        Assert.Throws<NullFamilyArbitrationResultException>(() => nullResult.Detect("x"));
        Assert.Throws<InvalidOperationException>(() => arbiterFails.Detect("x"));
        Assert.Throws<InvalidOperationException>(() => sourceFails.Detect("x"));
    }

    [Fact]
    public void ACompletedScanWithNothingToReport_IsAnEmptyResult_DistinctFromEveryFailureAbove()
    {
        var (pipeline, _, _, _, _, arbiter, _, _) = Build();

        var result = pipeline.Detect("nothing to find here");

        Assert.Empty(result.Candidates);
        Assert.Equal(1, arbiter.Calls); // the arbiter really was consulted
    }

    [Fact]
    public void Readiness_IsQueriedAtUse_AndANotReadyOrMissingDetectorFailsTheScan()
    {
        var (pipeline, mbi, _, _, _, _, _, _) = Build();

        _ = pipeline.Detect("fine");
        mbi.State = DetectorReadinessState.UnavailableAuthoritySnapshot;
        var notReady = Assert.Throws<RequiredDetectorNotRegisteredException>(() => pipeline.Detect("fine"));
        mbi.State = DetectorReadinessState.Ready;

        Assert.Equal(RequiredDetectorReason.NotReady, notReady.Reason);
        _ = pipeline.Detect("fine again"); // it recovers: nothing was cached

        var registry = DetectorRegistry.Create([new Probe(MbiId, 80, PiiType.UsMbi, _ => [])]);
        var missingMembers = new DetectionPipeline(registry, new Provider(new Arbiter(AcceptedAreEmitted)), [new Source([AtinId], _ => [])]);
        var missing = Assert.Throws<RequiredDetectorNotRegisteredException>(() => missingMembers.Detect("x"));
        Assert.Equal(RequiredDetectorReason.Unregistered, missing.Reason);
    }

    // ---- construction ----------------------------------------------------------------------------------------------

    [Fact]
    public void Construction_RejectsNullsDuplicateFamiliesAndSharedMembers()
    {
        var registry = DetectorRegistry.Create([new Probe(MbiId, 80, PiiType.UsMbi, _ => [])]);
        var provider = new Provider(new Arbiter(AcceptedAreEmitted));
        var one = new Source([AtinId], _ => []);

        Assert.Throws<ArgumentNullException>(() => new DetectionPipeline(null!, provider, []));
        Assert.Throws<ArgumentNullException>(() => new DetectionPipeline(registry, null!, []));
        Assert.Throws<ArgumentNullException>(() => new DetectionPipeline(registry, provider, null!));
        Assert.Throws<ArgumentNullException>(() => new DetectionPipeline(registry, provider, [null!]));
        Assert.Throws<ArgumentException>(() => new DetectionPipeline(registry, provider, [one, new Source([ItinId], _ => [])]));       // same family name twice
        Assert.Throws<ArgumentException>(() => new DetectionPipeline(registry, provider, [one, new Source([AtinId], _ => []) { FamilyName = "other" }])); // shared member
        Assert.Throws<ArgumentException>(() => new DetectionPipeline(registry, provider, [new Source([AtinId], _ => []) { FamilyName = "" }]));
        Assert.Throws<ArgumentNullException>(() => Build().Pipeline.Detect(null!));
    }

    [Fact]
    public void TheProductAssembly_ShipsNoFamilySourceOrArbiter_AndOnlyTheEmptyProvider()
    {
        var shipped = typeof(DetectionPipeline).Assembly.GetExportedTypes();

        Assert.Empty(shipped.Where(t => !t.IsInterface && typeof(IDetectorFamilyAttemptSource).IsAssignableFrom(t)));
        Assert.Empty(shipped.Where(t => !t.IsInterface && typeof(IDetectorFamilyArbiter).IsAssignableFrom(t)));
        Assert.Equal(
            [typeof(EmptyDetectorFamilyArbiterProvider)],
            shipped.Where(t => !t.IsInterface && typeof(IDetectorFamilyArbiterProvider).IsAssignableFrom(t)));
        Assert.DoesNotContain(
            typeof(DetectionPipeline).Assembly.GetReferencedAssemblies(), a => a.Name!.Contains("Experiment") || a.Name.Contains("Integration"));
    }

    [Fact]
    public void FailureMessages_CarryNoCandidateValue()
    {
        var (pipeline, _, _, _, _, _, _, _) = Build(
            arbitrate: _ => new FamilyArbitrationResult([Candidate(SsnId, 70, PiiType.UsSsn, 0, 900, "321549876")], []),
            attempts: _ => [Accept(SsnId, PiiType.UsSsn, 0, 5, "321549876")]);

        var ex = Record.Exception(() => pipeline.Detect("short"));

        Assert.DoesNotContain("321549876", ex!.ToString());
        Assert.DoesNotContain("321-54", ex.ToString());
    }
}
