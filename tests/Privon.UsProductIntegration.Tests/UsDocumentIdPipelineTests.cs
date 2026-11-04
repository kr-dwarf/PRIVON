using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. us.passport / us.a_number / us.i94 together with the existing five, through the product's REAL opt-in
// DetectionPipeline (registry, family arbiter provider, family source, CandidateResolver), the per-country execution scope and
// the real alias / policy stages. Nothing is registered or activated by the product.
public class UsDocumentIdPipelineTests
{
    private const string AllEightText =
        "Passport Number: P12345678 State: NY\nA-Number: A1234567\nI-94 Number: 123456789AB\n" +
        "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890";

    private static readonly string[] NewIds = ["us.passport", "us.a_number", "us.i94"];

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static IReadOnlyList<DetectionCandidate> Pipe(string raw) => UsOptInPipeline.Create().Detect(raw).Candidates;

    private static string Key(DetectionCandidate c) =>
        $"{c.DetectorId.Value}|{c.Span.Start}:{c.Span.Length}|{c.Canonical?.Value}|{c.Evidence}|{c.OwnerClaim}|{c.RiskLevel}";

    private static List<string> Keys(DetectionResult r) =>
        r.Candidates.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();

    public static TheoryData<CountrySettingObservation> NotAuthenticatedOff => new()
    {
        CountrySettingObservation.AuthenticatedOn, CountrySettingObservation.Missing, CountrySettingObservation.Corrupt,
        CountrySettingObservation.Unauthenticated, CountrySettingObservation.UnknownVersion,
    };

    // ===== the eight, in the real pipeline ===================================================================================

    [Fact]
    public void ThePipeline_ProducesTheEightFrozenRecords_ThroughTheProductsOwnConstructor()
    {
        static string At(string value) => AllEightText.IndexOf(value, StringComparison.Ordinal).ToString();

        var found = Pipe(AllEightText).OrderBy(c => c.Span.Start).Select(OracleWithOwner).ToList();

        Assert.Equal(
            [
                $"us.passport|UsPassport|L3|S+C+J|{At("P12345678")}:9|P12345678|[여권번호1]|100|ownerClaim=true",
                $"us.a_number|UsANumber|L3|S+C|{At("A1234567")}:8|A1234567|[A-NUMBER1]|110",
                $"us.i94|UsI94|L3|S+C|{At("123456789AB")}:11|123456789AB|[I-941]|120",
                $"us.atin|UsAtin|L3|S+C|{At("912-93-4567")}:11|912934567|[ATIN1]|50",
                $"us.itin|UsItin|L3|S+C|{At("912-70-1234")}:11|912701234|[ITIN1]|60",
                $"us.ssn|UsSsn|L3|S+C|{At("321-54-9876")}:11|321549876|[SSN1]|70",
                $"us.mbi|UsMbi|L3|S+C|{At("1A23C45DE67")}:11|1A23C45DE67|[MBI1]|80",
                $"us.sevis|UsSevis|L3|S+C|{At("N1234567890")}:11|N1234567890|[SEVIS1]|90",
            ],
            found);
    }

    [Fact]
    public void TheRegistryHoldsEleven_EachWithItsFrozenRank_AndTheFactoryRegistersNothingByItself()
    {
        var registry = DetectorRegistry.Create(UsIntegrationCandidates.CreateDetectors());

        Assert.Equal(11, registry.All.Count);
        foreach (var (id, rank) in new[] { ("us.passport", 100), ("us.a_number", 110), ("us.i94", 120) })
        {
            Assert.Equal(rank, registry.GetRank(DetectorId.Create(id)));
        }

        var shipped = DetectionPipeline.CreateDefault();
        foreach (string raw in new[] { "Passport Number: P12345678 State: NY", "A-Number: A1234567", "I-94 Number: 123456789AB" })
        {
            Assert.DoesNotContain(shipped.Detect(raw).Candidates,
                c => c.PiiType is PiiType.UsPassport or PiiType.UsANumber or PiiType.UsI94);
        }
    }

    // ===== H-2: five -> eight, independent of the factory =====================================================================

    [Fact]
    public void TheApprovedIdList_IsTheElevenLiterals_NotTheElevenOfThirteenInTheScopeCatalog_AndNotTheFactoryOutput()
    {
        string[] expected =
        [
            "us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis", "us.passport", "us.a_number", "us.i94",
            "us.driver_state_id", "us.bank_account", "us.name",
        ];

        Assert.Equal(expected, UsIntegrationCandidates.ApprovedDetectorIds.Select(i => i.Value));
        Assert.Equal(expected.Order(StringComparer.Ordinal), UsIntegrationCandidates.CreateDetectors().Select(d => d.Id.Value).Order(StringComparer.Ordinal));
        Assert.Equal(UsIntegrationCandidates.ApprovedDetectorIds, UsOptInPipeline.UsScope(CountrySettingObservation.Missing).RequiredDetectorIds);

        // The five scope ids that still have no detector are neither listed nor required.
        foreach (string unimplemented in new[] { "us.phone", "us.address" })
        {
            Assert.DoesNotContain(UsIntegrationCandidates.ApprovedDetectorIds, i => i.Value == unimplemented);
        }
    }

    [Theory]
    [InlineData("us.passport")]
    [InlineData("us.a_number")]
    [InlineData("us.i94")]
    public void AFactoryThatDropsANewDetector_IsDetected_WheneverTheUsPackIsNotAuthenticatedOff(string dropped)
    {
        IReadOnlyList<IDetector> Without() => UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != dropped).ToList();

        foreach (var setting in new[]
                 { CountrySettingObservation.AuthenticatedOn, CountrySettingObservation.Missing, CountrySettingObservation.Corrupt,
                   CountrySettingObservation.Unauthenticated, CountrySettingObservation.UnknownVersion })
        {
            var pipeline = UsOptInPipeline.Create(unitedStatesSetting: setting, usDetectorFactory: Without);

            var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() => pipeline.Detect(AllEightText));
            Assert.Equal(dropped, ex.Id.Value);
            Assert.Equal(RequiredDetectorReason.Unregistered, ex.Reason);
        }
    }

    [Theory]
    [InlineData("us.passport")]
    [InlineData("us.a_number")]
    [InlineData("us.i94")]
    public void AnAuthenticatedOff_StillIgnoresAMissingNewDetector_ItIsStoppedNotRequired(string dropped)
    {
        IReadOnlyList<IDetector> Without() => UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != dropped).ToList();

        var off = UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, usDetectorFactory: Without);

        Assert.Empty(off.Detect(AllEightText).Candidates);
    }

    [Fact]
    public void ANewDetectorThatIsNotReady_IsDetected_WheneverTheUsPackIsNotAuthenticatedOff()
    {
        IReadOnlyList<IDetector> NotReadyPassport() =>
        [
            .. UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != "us.passport"),
            new NotReadyDetector(new UsPassportCandidateDetector()),
        ];

        var pipeline = UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.Missing, usDetectorFactory: NotReadyPassport);

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() => pipeline.Detect(AllEightText));
        Assert.Equal("us.passport", ex.Id.Value);
        Assert.Equal(RequiredDetectorReason.NotReady, ex.Reason);
    }

    private sealed class NotReadyDetector(IDetector inner) : IDetector
    {
        public string Name => inner.Name;
        public PiiType PiiType => inner.PiiType;
        public DetectorId Id => inner.Id;
        public int ResolutionRank => inner.ResolutionRank;
        public DetectorReadinessState Readiness => DetectorReadinessState.UnavailableAuthoritySnapshot;
        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) => inner.Detect(context);
    }

    // ===== per-country scope =================================================================================================

    [Fact]
    public void UnitedStatesAuthenticatedOff_StopsAllEight_AndLeavesKoreanAndGlobalUntouched()
    {
        var shipped = DetectionPipeline.CreateDefault();
        var off = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff);

        Assert.Empty(off.Detect(AllEightText).Candidates);

        const string mixed = AllEightText + "\n연락처 010-1234-5678 이메일 user@example.test 901231-1234567";
        Assert.Equal(Keys(shipped.Detect(mixed)), Keys(off.Detect(mixed)));
        Assert.True(shipped.Detect(mixed).Candidates.Count >= 3);
    }

    [Theory]
    [MemberData(nameof(NotAuthenticatedOff))]
    public void EveryOtherSetting_ProtectsAllEight_ExactlyLikeAnUnscopedPipeline(CountrySettingObservation setting)
    {
        var unscoped = UsOptInPipeline.Create(Legacy());
        var scoped = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting);

        Assert.Equal(8, scoped.Detect(AllEightText).Candidates.Count);
        Assert.Equal(Keys(unscoped.Detect(AllEightText)), Keys(scoped.Detect(AllEightText)));
    }

    [Fact]
    public void ScopedAndUnscopedPipelines_AgreeOnAGeneratedCorpusOfTheNewFormats_AndFlowEqualsPipeline()
    {
        var unscoped = UsOptInPipeline.Create(Legacy());
        var on = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOn);
        var missing = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.Missing);
        var off = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff);
        var shipped = DetectionPipeline.CreateDefault();
        var flow = new UsDetectionFlow(new UsTaxpayerFamilyArbiterProvider(), Legacy(), Privon.UsSsn.UsTaxpayerLabelSet.Default);
        int withNew = 0;

        foreach (string raw in DocumentCorpus.Generate(seed: 20261101, count: 2500))
        {
            var expected = Keys(unscoped.Detect(raw));
            if (expected.Any(k => NewIds.Any(id => k.StartsWith(id + "|", StringComparison.Ordinal)))) withNew++;

            Assert.Equal(expected, Keys(on.Detect(raw)));
            Assert.Equal(expected, Keys(missing.Detect(raw)));
            Assert.Equal(expected, Keys(flow.Detect(raw)));
            Assert.DoesNotContain(Keys(off.Detect(raw)), k => k.StartsWith("us.", StringComparison.Ordinal));
            Assert.Equal(Keys(shipped.Detect(raw)), Keys(off.Detect(raw)));
        }

        Assert.True(withNew > 400, "the corpus must actually contain candidates of the new detectors");
    }

    // ===== overlaps between different high-risk identifiers =================================================================

    [Fact]
    public void AnANumberLookingLikeACurrentFormatPassport_IsOneANumber_BecauseS_Plus_C_BeatsS_Plus_J()
    {
        // "A12345678" is both A + eight digits (A-Number, labelled: S+C) and one letter + eight digits (passport route A with
        // J: S+J). Same level, same span: the frozen evidence order S+C > S+J/P decides, not registration order.
        const string raw = "A-Number: A12345678 State: NY";

        var winner = Assert.Single(Pipe(raw));

        Assert.Equal("us.a_number", winner.DetectorId.Value);
        Assert.Equal(new RawSpan(10, 9), winner.Span);
        Assert.Equal(ResolutionEvidence.SC, winner.Evidence);
    }

    [Fact]
    public void AnI94WhoseValueLooksLikeASevis_IsOneI94_BecauseS_Plus_C_BeatsS()
    {
        var winner = Assert.Single(Pipe("I-94: N1234567890"));

        Assert.Equal("us.i94", winner.DetectorId.Value);
        Assert.Equal(ResolutionEvidence.SC, winner.Evidence);
    }

    [Fact]
    public void ALabelledPassportToken_ThatIsAlsoAValidBareMbi_IsOneMbi_BecauseS_BeatsC()
    {
        // Legacy passport evidence is C; a structurally valid MBI is S. The frozen order S > C picks the MBI.
        var winner = Assert.Single(Pipe("Passport: 1A23C45DE67"));

        Assert.Equal("us.mbi", winner.DetectorId.Value);
        Assert.Equal(ResolutionEvidence.S, winner.Evidence);
    }

    [Fact]
    public void ALabelledPassportToken_ThatIsAlsoAValidBareSevis_IsOneSevis_BecauseS_BeatsC()
    {
        var winner = Assert.Single(Pipe("Passport: N1234567890"));

        Assert.Equal("us.sevis", winner.DetectorId.Value);
    }

    [Fact]
    public void NonOverlappingIdentifiers_OfTheNewTypes_AreAllKept_InTextOrder()
    {
        const string raw = "Passport: P12345678; A-Number: A1234567; I-94: 123456789AB";

        Assert.Equal(["us.passport", "us.a_number", "us.i94"], Pipe(raw).OrderBy(c => c.Span.Start).Select(c => c.DetectorId.Value));
    }

    [Fact]
    public void TheResolution_DoesNotDependOnTheOrderOfTheDetectorsInTheFactory()
    {
        var forward = UsOptInPipeline.Create(usDetectorFactory: () => UsIntegrationCandidates.CreateDetectors());
        var reversed = UsOptInPipeline.Create(usDetectorFactory: () => UsIntegrationCandidates.CreateDetectors().Reverse().ToList());

        foreach (string raw in new[] { AllEightText, "A-Number: A12345678 State: NY", "I-94: N1234567890", "Passport: 1A23C45DE67" })
        {
            Assert.Equal(Keys(forward.Detect(raw)), Keys(reversed.Detect(raw)));
        }
    }

    [Fact]
    public void TheFiveExistingTypes_ProduceTheSameCandidates_InTheEightDetectorPipeline_ForTheirFrozenFixtures()
    {
        // The five-detector registry would now fail H-2 (eight are required), so the baseline is a five-detector pipeline that
        // carries no country scope.
        var five = new DetectionPipeline(DetectorRegistry.Create(AllFive()), new UsTaxpayerFamilyArbiterProvider(), [new UsTaxpayerFamilyAttempts()]);
        var eight = UsOptInPipeline.Create();

        foreach (string raw in new[]
                 { "ATIN: 912-93-4567", "ITIN: 912-70-1234", "SSN: 321-54-9876", "321-54-9876", "MBI: 1A23C45DE67", "N1234567890", "ATIN: 912-88-4321" })
        {
            Assert.Equal(Keys(five.Detect(raw)), Keys(eight.Detect(raw)));
        }
    }

    // ===== Level 3 policy and aliases (the real stages) =====================================================================

    [Theory]
    [InlineData("Passport Number: P12345678 State: NY")]
    [InlineData("Passport #: ZX81K2")]
    [InlineData("State: NY P12345678")]
    [InlineData("A-Number: A1234567")]
    [InlineData("I-94 Number: 123456789AB")]
    public void CurrentProductPolicy_IsNeedsDecision_ForEveryNewLevel3_AndGivesItNoAliasYet(string raw)
    {
        var candidate = Assert.Single(Pipe(raw));
        var evaluated = new EvaluatedCandidate(candidate, TrustState.Unknown);

        Assert.Equal(RiskLevel.Level3, candidate.RiskLevel);
        Assert.Equal(CandidateDisposition.NeedsDecision, CandidatePolicyEvaluator.Decide(evaluated));
        Assert.Null(AliasAssigner.Assign(CandidatePolicyEvaluator.Evaluate([evaluated]), new AliasMap()).Single().Alias);
    }

    [Fact]
    public void TheAliasMap_NumbersEqualValuesTogether_AndDifferentValuesSeparately_ForTheNewTypes()
    {
        var map = new AliasMap();
        const string raw =
            "Passport: P12345678\nPassport: P12345678\nPassport: Q87654321\nA-Number: A1234567\nA-Number: 1234567\nA-Number: A7654321\n" +
            "I-94: 123456789AB\nI-94: 123456789ab\nI-94: 987654321ZY";

        var tokens = Pipe(raw).OrderBy(c => c.Span.Start).Select(c => map.GetOrAdd(c.Canonical!.Value).Value).ToList();

        Assert.Equal(
            ["[여권번호1]", "[여권번호1]", "[여권번호2]", "[A-NUMBER1]", "[A-NUMBER1]", "[A-NUMBER2]", "[I-941]", "[I-941]", "[I-942]"],
            tokens);
    }

    [Theory]
    [InlineData(PiiType.UsPassport, "[여권번호1]")]
    [InlineData(PiiType.UsANumber, "[A-NUMBER1]")]
    [InlineData(PiiType.UsI94, "[I-941]")]
    public void AliasTokens_ForTheNewTypes_MatchTheFrozenContractTokens(PiiType type, string token)
    {
        Assert.Equal(token, new AliasIdentity(AliasFamilyTable.FamilyOf(type), 1).CanonicalContractToken);
        Assert.Equal(token, new AliasMap().GetOrAdd(new CanonicalValue(type, "x")).Value);
    }

    // ===== no raw value in any pipeline-level diagnostic string ============================================================

    [Fact]
    public void NoPipelineDiagnosticSurface_ShowsAnyNewIdentifier()
    {
        string[] secrets = ["P12345678", "1234567", "123456789AB", "ZX81K2"];
        const string raw = "Passport Number: P12345678 State: NY Passport: ZX81K2 A-Number: A1234567 I-94: 123456789AB";

        var result = UsOptInPipeline.Create().Detect(raw);
        var strings = new List<string> { result.ToString() ?? string.Empty };
        strings.AddRange(result.Candidates.Select(c => c.ToString()));
        strings.AddRange(result.Candidates.Select(c => c.Canonical!.Value.ToString()));
        strings.AddRange(result.Candidates.Select(c => c.AliasIdentity?.ToString() ?? string.Empty));
        strings.AddRange(UsIntegrationCandidates.CreateDetectors().Select(d => d.ToString() ?? string.Empty));

        foreach (string text in strings)
        {
            foreach (string secret in secrets)
            {
                Assert.DoesNotContain(secret, text);
            }
        }

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() =>
            UsOptInPipeline.Create(
                    unitedStatesSetting: CountrySettingObservation.Missing,
                    usDetectorFactory: () => UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != "us.i94").ToList())
                .Detect(raw));
        foreach (string secret in secrets)
        {
            Assert.DoesNotContain(secret, ex.Message);
            Assert.DoesNotContain(secret, ex.ToString());
        }
    }

    // ---- synthetic corpus of the new formats ---------------------------------------------------------------------------

    private static class DocumentCorpus
    {
        private static readonly string[] Labels =
        [
            "Passport: ", "Passport Number: ", "Passport No.: ", "Passport #", "U.S. Passport: ", "Passport ID: ", "A-Number: ",
            "A Number: ", "USCIS Number: ", "Alien Registration Number: ", "Alien Number: ", "I-94: ", "I94 Number: ",
            "I-94 Admission Number: ", "Form I-94 Number: ", "Admission Number: ", "State: ", "SSN: ", "MBI: ", "Phone: ", "", "", "",
        ];

        private static readonly string[] Values =
        [
            "P12345678", "p12345678", "Q87654321", "ZX81K2", "Z1234", "ABCDEF", "123456789012", "A1234567", "A 1234567", "A-1234567",
            "A123456", "A1234567890", "1234567", "123456789", "123456789AB", "123456789ab", "N1234567890", "1A23C45DE67", "NY", "CA",
            "ZZ", "321-54-9876", "X123456789ABY", "123456789A", "hello", "12345", "P1234567８", "A123456７",
        ];

        private static readonly string[] Joiners = [" ", "\n", "; ", " | ", "\t", ", ", " and ", " State: NY ", " in the United States "];

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
}
