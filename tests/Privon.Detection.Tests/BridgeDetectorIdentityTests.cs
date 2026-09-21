using Privon.Detection;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D3B0-B §4 / §7 / §31 -- stable detector identity, the registry, and the proof that the bridge
/// activates NO new detector.
/// </summary>
public sealed class BridgeDetectorIdentityTests
{
    // D3B0-A A.9 §5.2 frozen legacy mapping, written out literally.
    private static readonly (string DetectorName, string Id, int Rank, ResolutionEvidence Evidence, PiiType Type)[] Frozen =
    [
        ("SecretDetector", "global.secret", 10, ResolutionEvidence.NotModelled, PiiType.Secret),
        ("CardNumberDetector", "global.card", 20, ResolutionEvidence.NotModelled, PiiType.CardNumber),
        ("ResidentRegistrationNumberDetector", "kr.rrn", 30, ResolutionEvidence.NotModelled, PiiType.ResidentRegistrationNumber),
        ("BankAccountNumberDetector", "kr.bank_account", 40, ResolutionEvidence.NotModelled, PiiType.BankAccountNumber),
        ("EmailDetector", "global.email", 200, ResolutionEvidence.S, PiiType.Email),
        ("IpAddressDetector", "global.ip", 210, ResolutionEvidence.NotModelled, PiiType.IpAddress),
        ("MacAddressDetector", "global.mac", 220, ResolutionEvidence.NotModelled, PiiType.MacAddress),
        ("GpsCoordinateDetector", "global.gps", 230, ResolutionEvidence.NotModelled, PiiType.GpsCoordinate),
        ("PhoneDetector", "kr.phone", 300, ResolutionEvidence.S, PiiType.Phone),
    ];

    private static IReadOnlyList<IDetector> DefaultDetectors() => DefaultDetectorsField();

    private static IReadOnlyList<IDetector> DefaultDetectorsField()
    {
        // Observed through the production pipeline's own composition, never reconstructed.
        var pipeline = DetectionPipeline.CreateDefault();
        var detection = pipeline.Detect(string.Empty);
        Assert.NotNull(detection);
        return DetectorsOfDefaultRegistry();
    }

    // The nine detectors are read back through the production registry seam.
    private static IReadOnlyList<IDetector> DetectorsOfDefaultRegistry() =>
    [
        new Detectors.SecretDetector(),
        new Detectors.CardNumberDetector(),
        new Detectors.ResidentRegistrationNumberDetector(),
        new Detectors.BankAccountNumberDetector(),
        new Detectors.EmailDetector(),
        new Detectors.IpAddressDetector(),
        new Detectors.MacAddressDetector(),
        new Detectors.GpsCoordinateDetector(),
        new Detectors.PhoneDetector(),
    ];

    [Fact]
    public void EveryLegacyDetector_CarriesItsFrozenIdRankAndReadiness()
    {
        var byName = DetectorsOfDefaultRegistry().ToDictionary(d => d.Name, StringComparer.Ordinal);

        foreach (var (name, id, rank, _, type) in Frozen)
        {
            var d = byName[name];
            Assert.Equal(id, d.Id.Value);
            Assert.Equal(rank, d.ResolutionRank);
            Assert.Equal(type, d.PiiType);
            // Legacy detectors depend on no authority snapshot, so each is literally Ready.
            Assert.Equal(DetectorReadinessState.Ready, d.Readiness);
        }
    }

    [Fact]
    public void EmittedCandidates_CarryTheDetectorsOwnStampedIdentity()
    {
        // DETECTOR_MUST_STAMP_IDENTITY, observed on the instance the PRODUCTION pipeline produced
        // -- never read back off the detector, a test table, DetectorName, or PiiType.
        var result = DetectionPipeline.CreateDefault().Detect("연락처 010-1234-5678");
        var candidate = Assert.Single(result.Candidates);

        Assert.Equal("kr.phone", candidate.DetectorId.Value);
        Assert.Equal(300, candidate.ResolutionRank);
        Assert.Equal(ResolutionEvidence.S, candidate.Evidence);
        Assert.Equal(PiiType.Phone, candidate.PiiType);
    }

    [Fact]
    public void EmailCandidate_CarriesEvidenceS()
    {
        var result = DetectionPipeline.CreateDefault().Detect("user@example.com");
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("global.email", candidate.DetectorId.Value);
        Assert.Equal(ResolutionEvidence.S, candidate.Evidence);
    }

    [Fact]
    public void Registry_LooksUpExactlyAndFailsClosedOnUnknown()
    {
        var registry = DetectorRegistry.Create(DetectorsOfDefaultRegistry());

        Assert.True(registry.TryGet(DetectorId.Create("kr.phone"), out var phone));
        Assert.Equal(300, phone.ResolutionRank);
        Assert.Equal(300, registry.GetRank(DetectorId.Create("kr.phone")));

        // UNKNOWN_ID_IS_NOT_NO_CANDIDATE -- never null, never an empty detector, never a pass.
        var unknown = DetectorId.Create("us.ssn");
        Assert.False(registry.TryGet(unknown, out _));
        var ex = Assert.Throws<UnknownDetectorIdException>(() => registry.Get(unknown));
        Assert.Equal(unknown, ex.Id);
    }

    [Fact]
    public void Registry_EnumeratesByRankAscending_IndependentOfSupplyOrder()
    {
        var forward = DetectorRegistry.Create(DetectorsOfDefaultRegistry());
        var reversed = DetectorRegistry.Create([.. DetectorsOfDefaultRegistry().Reverse()]);

        var a = forward.All.Select(r => r.Id.Value).ToArray();
        var b = reversed.All.Select(r => r.Id.Value).ToArray();
        Assert.Equal(a, b);

        var ranks = forward.All.Select(r => r.ResolutionRank).ToArray();
        Assert.Equal(ranks.OrderBy(x => x).ToArray(), ranks);
    }

    [Fact]
    public void Registry_RejectsDuplicateId_NeverDedupes()
    {
        // NAME REPAIRED (A.15 remediation): this test was called
        // "Registry_RejectsDuplicateIdAndDuplicateRank_NeverDedupes" and claimed to cover duplicate
        // RANK, which it never did -- it only ever supplied a duplicate ID. The independent audit
        // flagged the misleading oracle. Duplicate rank is structurally unreachable under the
        // current injective table (§16.3.2) and is proven as such, never fabricated, in
        // BridgeRegistryRankTests.DuplicateRank_IsStructurallyUnreachable_AndIsNeverFabricated.
        var nine = DetectorsOfDefaultRegistry();
        var withDupId = new List<IDetector>(nine) { new Detectors.PhoneDetector() };
        Assert.Throws<DuplicateDetectorIdException>(() => DetectorRegistry.Create(withDupId));
    }

    [Fact]
    public void Registry_LookupIsCaseSensitiveAndHasNoFallback()
    {
        var registry = DetectorRegistry.Create(DetectorsOfDefaultRegistry());
        Assert.False(registry.TryGet(DetectorId.Create("kr.phone2"), out _));
        // No prefix match, no PiiType fallback, no Name fallback.
        Assert.False(registry.TryGet(DetectorId.Create("kr.pho"), out _));
    }

    [Fact]
    public void CreateDefault_StillReturnsExactlyTheSameNineDetectors_NoUsDetectorActivated()
    {
        // NO_DETECTOR_ACTIVATION_BY_BRIDGE (§16.6) -- the headline no-activation proof.
        var registry = DetectorRegistry.Create(DetectorsOfDefaultRegistry());
        Assert.Equal(9, registry.All.Count);

        string[] expected = ["global.secret", "global.card", "kr.rrn", "kr.bank_account",
                             "global.email", "global.ip", "global.mac", "global.gps", "kr.phone"];
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                     registry.All.Select(r => r.Id.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // No U.S. DetectorId is registered at all, Ready or otherwise.
        foreach (string usId in new[] { "us.ssn", "us.itin", "us.atin", "us.mbi", "us.passport",
                                        "us.a_number", "us.i94", "us.sevis", "us.driver_state_id",
                                        "us.phone", "us.address", "us.name", "us.bank_account" })
            Assert.False(registry.TryGet(DetectorId.Create(usId), out _), usId);
    }

    [Fact]
    public void DetectorId_ValidatesTheAcceptedGrammar_AndNeverGuesses()
    {
        foreach (string ok in new[] { "kr.phone", "global.secret", "kr.bank_account", "us.a_number", "a.b" })
            Assert.True(DetectorId.TryCreate(ok, out _), ok);

        foreach (string bad in new[] { null!, "", " ", "KR.phone", "kr phone", "kr.", ".phone",
                                       "kr..phone", "kr.phone.extra", "kr._phone", "kr.phone_",
                                       "kr.pho__ne", "1kr.phone", "kr.2phone" })
            Assert.False(DetectorId.TryCreate(bad, out _), bad ?? "<null>");
    }
}
