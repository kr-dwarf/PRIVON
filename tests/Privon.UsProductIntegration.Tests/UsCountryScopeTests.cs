using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Family;
using Privon.Detection.Scope;
using Privon.UsProductIntegration;
using Privon.UsSsn;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. The real U.S. five under the product's per-country execution scope. The setting layer does not
// exist yet; each test hands the pipeline the OBSERVATION a trusted layer would have made.
public class UsCountryScopeTests
{
    private const string UsAll = "ATIN: 912-93-4567 ITIN: 912-70-1234 SSN: 321-54-9876 MBI: 1A23C45DE67 SEVIS: N1234567890";

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static string Key(DetectionCandidate c) =>
        $"{c.DetectorId.Value}|{c.Span.Start}:{c.Span.Length}|{c.Canonical?.Value}|{c.Evidence}|{c.Confidence}|{c.RiskLevel}";

    private static List<string> Keys(DetectionResult r) =>
        r.Candidates.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();

    public static TheoryData<CountrySettingObservation> Uncertain => new()
    {
        CountrySettingObservation.Missing, CountrySettingObservation.Corrupt,
        CountrySettingObservation.Unauthenticated, CountrySettingObservation.UnknownVersion,
    };

    // ---- ON and OFF ------------------------------------------------------------------------------------------------

    [Fact]
    public void UnitedStatesOn_IsIdenticalToThePipelineWithoutAnyCountryScope_OnAGeneratedCorpus()
    {
        var unscoped = UsOptInPipeline.Create(Legacy());
        var on = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOn);

        foreach (string raw in Corpus.Generate(seed: 20261001, count: 1500).Append(UsAll))
        {
            Assert.Equal(Keys(unscoped.Detect(raw)), Keys(on.Detect(raw)));
        }

        Assert.Equal(5, on.Detect(UsAll).Candidates.Count);
    }

    [Fact]
    public void UnitedStatesAuthenticatedOff_FindsNoUsCandidate_ForAnyOfTheFiveTypes()
    {
        var off = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff);

        foreach (string raw in new[]
                 { "ATIN: 912-93-4567", "ITIN: 912-70-1234", "SSN: 321-54-9876", "321-54-9876", "MBI: 1A23C45DE67", "N1234567890", "ATIN: 912-88-4321", UsAll })
        {
            Assert.Empty(off.Detect(raw).Candidates);
        }
    }

    [Fact]
    public void UnitedStatesOff_LeavesKoreanAndGlobalResultsExactlyAsTheShippedDefaultPipelineProducesThem()
    {
        // The shipped default has no U.S. detector, so for ANY text its output is the Korean + Global result. With the
        // United States OFF the opt-in pipeline must reproduce it exactly, even when the text is full of U.S. identifiers.
        var shipped = DetectionPipeline.CreateDefault();
        var off = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff);
        var differences = new List<string>();
        int withCandidates = 0;
        var rng = new Random(20261002);
        var usSnippets = new[] { "SSN: 321-54-9876", "ATIN: 912-88-4321", "MBI: 1A23C45DE67", "N1234567890", "ITIN: 912-70-1234" };

        foreach (string raw in Corpus.Generate(seed: 20261003, count: 3000))
        {
            string text = raw + " 연락처 010-1234-5678 " + usSnippets[rng.Next(usSnippets.Length)] + " 이메일 a.b@example.test 901231-1234567";
            var expected = Keys(shipped.Detect(text));
            if (expected.Count > 0) withCandidates++;

            if (!expected.SequenceEqual(Keys(off.Detect(text)))) differences.Add(text.Replace("\n", "\\n").Replace("\t", "\\t"));
        }

        Assert.True(withCandidates > 2000, "the corpus must actually contain Korean and Global candidates");
        Assert.True(differences.Count == 0, $"{differences.Count} differ: " + string.Join(" // ", differences.Take(4)));
    }

    [Fact]
    public void ACompletedScanWithAnOffCountry_IsAnEmptyResultForUsText_NotAFailure()
    {
        var off = UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.AuthenticatedOff);

        var result = off.Detect("SSN: 321-54-9876");

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void AnOffCountry_NeedsNeitherTheArbiterNorTheSourceToBeAvailable()
    {
        var off = UsOptInPipeline.Create(
            Legacy(), new EmptyDetectorFamilyArbiterProvider(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff);

        var result = off.Detect("연락처 010-1234-5678 SSN: 321-54-9876");

        Assert.Equal(PiiType.Phone, Assert.Single(result.Candidates).PiiType);
    }

    // ---- uncertainty never shrinks protection -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Uncertain))]
    public void AnUncertainSetting_ProtectsExactlyLikeOn_OnAGeneratedCorpus(CountrySettingObservation setting)
    {
        var on = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOn);
        var uncertain = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting);

        foreach (string raw in Corpus.Generate(seed: 20261004, count: 800).Append(UsAll))
        {
            Assert.Equal(Keys(on.Detect(raw)), Keys(uncertain.Detect(raw)));
        }

        Assert.Equal(5, uncertain.Detect(UsAll).Candidates.Count);
    }

    [Theory]
    [MemberData(nameof(Uncertain))]
    public void AnUncertainSettingWithoutTheRequiredImplementation_FailsExplicitly_ItNeverBecomesAnOffOrAnEmptyScan(CountrySettingObservation setting)
    {
        // The U.S. pack is "restored to ON", but this configuration holds no U.S. detector and no taxpayer source.
        var registry = DetectorRegistry.Create(Legacy());
        var pipeline = new DetectionPipeline(registry, new UsTaxpayerFamilyArbiterProvider(), [], [UsOptInPipeline.UsScope(setting)]);

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() => pipeline.Detect("SSN: 321-54-9876"));
        Assert.Equal(RequiredDetectorReason.Unregistered, ex.Reason);

        // The very same configuration with an authenticated OFF is a complete, legitimate scan of the rest.
        var off = new DetectionPipeline(registry, new UsTaxpayerFamilyArbiterProvider(), [],
            [UsOptInPipeline.UsScope(CountrySettingObservation.AuthenticatedOff)]);
        Assert.Empty(off.Detect("SSN: 321-54-9876").Candidates);
    }

    [Theory]
    [MemberData(nameof(Uncertain))]
    public void AnUncertainSettingWithoutAnArbiter_FailsClosed(CountrySettingObservation setting)
    {
        var pipeline = UsOptInPipeline.Create(arbiterProvider: new EmptyDetectorFamilyArbiterProvider(), unitedStatesSetting: setting);

        Assert.Throws<FamilyArbitrationUnavailableException>(() => pipeline.Detect("no identifiers here"));
    }

    [Fact]
    public void AnUndefinedSettingValue_IsRejected_NeverReadAsOff()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UsOptInPipeline.Create(unitedStatesSetting: (CountrySettingObservation)42));
    }

    // ---- O-1, ordering, defaults -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(CountrySettingObservation.AuthenticatedOn)]
    [InlineData(CountrySettingObservation.Missing)]
    [InlineData(CountrySettingObservation.Corrupt)]
    public void O1_TheCrossLabelCaseStaysProtected_WheneverTheCountryIsNotAuthenticatedOff(CountrySettingObservation setting)
    {
        var pipeline = UsOptInPipeline.Create(unitedStatesSetting: setting);

        var found = Assert.Single(pipeline.Detect("ATIN: 912-88-4321").Candidates);

        Assert.Equal("us.itin", found.DetectorId.Value);
        Assert.Equal(ResolutionEvidence.S, found.Evidence);
    }

    [Fact]
    public void TheSettingChangesOnlyWhichDetectorsRun_NeverTheResultOfTheOnesThatDo()
    {
        var onResult = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOn).Detect(UsAll + " 010-1234-5678");
        var offResult = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff).Detect(UsAll + " 010-1234-5678");

        Assert.Equal(
            Keys(onResult).Where(k => !k.StartsWith("us.", StringComparison.Ordinal)),
            Keys(offResult));
    }

    [Fact]
    public void EachPipelineIsIndependent_AndTheShippedDefaultIsNotAffectedByAnyOfThem()
    {
        var off = UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.AuthenticatedOff);
        var on = UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.AuthenticatedOn);

        Assert.Empty(off.Detect(UsAll).Candidates);
        Assert.Equal(5, on.Detect(UsAll).Candidates.Count);
        Assert.Empty(off.Detect(UsAll).Candidates); // the ON pipeline did not change the OFF one
        Assert.DoesNotContain(DetectionPipeline.CreateDefault().Detect(UsAll).Candidates,
            c => c.DetectorId.Value.StartsWith("us.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRegistrationOrderOfTheDetectors_DoesNotChangeEitherSetting()
    {
        foreach (var setting in new[] { CountrySettingObservation.AuthenticatedOff, CountrySettingObservation.Missing })
        {
            string? reference = null;
            foreach (bool reverse in new[] { false, true })
            {
                var legacy = Legacy();
                var extra = reverse ? legacy.Reverse().ToArray() : legacy;
                string shown = string.Join(";", Keys(UsOptInPipeline.Create(extra, unitedStatesSetting: setting).Detect(UsAll + " a.b@example.test")));

                reference ??= shown;
                Assert.Equal(reference, shown);
            }
        }
    }
}
