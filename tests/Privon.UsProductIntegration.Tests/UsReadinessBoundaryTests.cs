using Privon.Detection;
using Privon.Detection.Authority;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. How much of the thirteen is actually ready, and how that is reported. The product ships NO production NPA /
// USPS-suffix / USPS-state snapshot; the four-entry D3A fixtures used here are authority-provider fixtures served to the same
// production loader -- they are not, and are never substituted for, production data.
public class UsReadinessBoundaryTests
{
    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static readonly string[] AllThirteen =
    [
        "us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis", "us.passport", "us.a_number", "us.i94", "us.driver_state_id",
        "us.bank_account", "us.name", "us.phone", "us.address",
    ];

    private static int ReadyCount(UsAuthoritySet? authority) =>
        UsIntegrationCandidates.ReadinessReport(authority).Count(r => r.Readiness == DetectorReadinessState.Ready);

    // ===== 11 of 13 by default ===============================================================================================

    [Fact]
    public void WithoutAnyProductionSnapshot_ElevenOfThirteenAreReady_AndTheReportNamesTheTwoThatAreNot()
    {
        var report = UsIntegrationCandidates.ReadinessReport();

        Assert.Equal(AllThirteen.Order(StringComparer.Ordinal), report.Select(r => r.Id.Value).Order(StringComparer.Ordinal));
        Assert.Equal(11, ReadyCount(null));
        Assert.Equal(
            ["us.address", "us.phone"],
            report.Where(r => r.Readiness != DetectorReadinessState.Ready).Select(r => r.Id.Value).Order(StringComparer.Ordinal));
        Assert.All(report.Where(r => r.Readiness != DetectorReadinessState.Ready),
            r => Assert.Equal(DetectorReadinessState.UnavailableAuthoritySnapshot, r.Readiness));
        Assert.Equal(11, UsIntegrationCandidates.CreateDetectors().Count);
        Assert.Equal(11, UsIntegrationCandidates.RequiredDetectorIds().Count);
        Assert.Equal(11, UsOptInPipeline.UsScope(CountrySettingObservation.Missing).RequiredDetectorIds.Count);
    }

    [Fact]
    public void ThePipelineWithoutAnAuthority_DoesNotDetectPhoneOrAddress_AnyForm()
    {
        var pipeline = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.Missing);

        foreach (string raw in new[]
                 { "Phone: 212-555-1212", "(212) 555-1212", "+12125551212", "800-555-1212", "Address: 123 Main ST", "123 Main ST, New York, NY 10001" })
        {
            Assert.DoesNotContain(pipeline.Detect(raw).Candidates, c => c.DetectorId.HasValue && c.DetectorId.Value is "us.phone" or "us.address");
        }
    }

    [Fact]
    public void TheD3Fixtures_AreNotProductionData_TheirReadinessNeedsAnExplicitProvider()
    {
        // The same fixtures, given explicitly, make thirteen ready; without them the default is eleven. Nothing is picked up implicitly.
        Assert.Equal(13, ReadyCount(UsTestAuthorityProvider.ValidSet()));
        Assert.Equal(11, ReadyCount(null));
        Assert.Equal(11, ReadyCount(UsAuthoritySet.Unavailable));
        Assert.Equal(11, UsIntegrationCandidates.CreateDetectors(UsAuthoritySet.Unavailable).Count);
    }

    // ===== partial and broken provision ======================================================================================

    public static TheoryData<bool, bool, bool, int, bool, bool> Provision => new()
    {
        // npa, suffix, state -> ready count, phone ready, address ready
        { true, true, true, 13, true, true },
        { true, false, false, 12, true, false },
        { false, true, true, 12, false, true },
        { false, true, false, 11, false, false },
        { false, false, true, 11, false, false },
        { true, true, false, 12, true, false },
        { true, false, true, 12, true, false },
        { false, false, false, 11, false, false },
    };

    [Theory]
    [MemberData(nameof(Provision))]
    public void EveryPartialProvision_ReportsExactlyWhatIsBacked_AndNothingMore(
        bool npa, bool suffix, bool state, int ready, bool phoneReady, bool addressReady)
    {
        var provider = UsTestAuthorityProvider.Valid();
        if (!npa) provider.Without(UsAuthoritySet.NpaId);
        if (!suffix) provider.Without(UsAuthoritySet.SuffixId);
        if (!state) provider.Without(UsAuthoritySet.StateId);
        var set = UsAuthoritySet.Load(provider);

        Assert.Equal(phoneReady, set.PhoneReady);
        Assert.Equal(addressReady, set.AddressReady);
        Assert.Equal(ready, ReadyCount(set));
        Assert.Equal(ready, UsIntegrationCandidates.CreateDetectors(set).Count);
        Assert.Equal(ready, UsIntegrationCandidates.RequiredDetectorIds(set).Count);

        // the report and the detectors agree, id by id, and a detector built by hand reports the same readiness
        var created = UsIntegrationCandidates.CreateDetectors(set).Select(d => d.Id.Value).ToHashSet();
        foreach (var (id, readiness) in UsIntegrationCandidates.ReadinessReport(set))
        {
            Assert.Equal(readiness == DetectorReadinessState.Ready, created.Contains(id.Value));
            if (id.Value == "us.phone") Assert.Equal(readiness, new UsPhoneCandidateDetector(set).Readiness);
            if (id.Value == "us.address") Assert.Equal(readiness, new UsAddressCandidateDetector(set).Readiness);
        }
    }

    [Theory]
    [InlineData("us.npa", "tampered")]
    [InlineData("us.npa", "metadata")]
    [InlineData("us.npa", "missing")]
    [InlineData("usps.suffix", "tampered")]
    [InlineData("usps.suffix", "missing")]
    [InlineData("usps.state", "tampered")]
    [InlineData("usps.state", "missing")]
    public void ABrokenSnapshot_NeverBroadensDetection_TheDependingDetectorEmitsNothingForAnyInput(string id, string how)
    {
        var snapshotId = id switch { "us.npa" => UsAuthoritySet.NpaId, "usps.suffix" => UsAuthoritySet.SuffixId, _ => UsAuthoritySet.StateId };
        var provider = UsTestAuthorityProvider.Valid();
        _ = how switch
        {
            "tampered" => provider.Tampered(snapshotId),
            "metadata" => provider.WithoutRetrievalDate(snapshotId),
            _ => provider.Without(snapshotId),
        };
        var set = UsAuthoritySet.Load(provider);

        // no "all of NANP" and no built-in suffix / state list: valid-looking numbers and addresses of every kind stay undetected
        if (id == "us.npa")
        {
            foreach (string raw in new[] { "Phone: 212-555-1212", "212-555-1212", "800-555-1212", "416-555-1212", "+1 (787) 555-1212", "Tel: 3405551212" })
            {
                Assert.Empty(Run(new UsPhoneCandidateDetector(set), raw));
            }

            Assert.Equal(12, UsIntegrationCandidates.CreateDetectors(set).Count);
        }
        else
        {
            foreach (string raw in new[] { "Address: 123 Main ST", "Address: PO Box 7", "Address: RR 2 Box 15", "123 Main ST, New York, NY 10001", "123 Main AVE, Albany, CA 90210" })
            {
                Assert.Empty(Run(new UsAddressCandidateDetector(set), raw));
            }

            Assert.Equal(12, UsIntegrationCandidates.CreateDetectors(set).Count);
        }
    }

    [Fact]
    public void ANotReadyDetector_IsNeverReportedReady_AndAMissingOneIsNamedByTheReport()
    {
        var tampered = UsAuthoritySet.Load(UsTestAuthorityProvider.Valid().Tampered(UsAuthoritySet.NpaId));

        var report = UsIntegrationCandidates.ReadinessReport(tampered);

        Assert.Equal(DetectorReadinessState.UnavailableAuthoritySnapshot, report.Single(r => r.Id.Value == "us.phone").Readiness);
        Assert.Equal(DetectorReadinessState.Ready, report.Single(r => r.Id.Value == "us.address").Readiness);
        Assert.Equal(DetectorReadinessState.UnavailableAuthoritySnapshot, new UsPhoneCandidateDetector(tampered).Readiness);
        Assert.DoesNotContain(UsIntegrationCandidates.CreateDetectors(tampered), d => d.Id.Value == "us.phone");
    }

    // ===== a smaller scan is a known, reported scan; a missing REQUIRED detector is still a failure =========================

    [Fact]
    public void WhenAnAuthorityBacksPhoneAndAddress_TheyBecomeRequired_AndTheirLossIsAFailure_NotASilentSmallerScan()
    {
        var set = UsTestAuthorityProvider.ValidSet();
        IReadOnlyList<IDetector> WithoutPhone() => UsIntegrationCandidates.CreateDetectors(set).Where(d => d.Id.Value != "us.phone").ToList();

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() =>
            UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.Missing, usDetectorFactory: WithoutPhone, authority: set)
                .Detect("Phone: 212-555-1212"));

        Assert.Equal("us.phone", ex.Id.Value);
    }

    [Fact]
    public void WithoutAnAuthority_ThePhoneIsNotRequired_SoTheScanRunsAndTheReportIsTheOnlyPlaceItsAbsenceIsRecorded()
    {
        var pipeline = UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.Corrupt);

        var found = pipeline.Detect("SSN: 321-54-9876 Phone: 212-555-1212").Candidates;

        Assert.Equal("us.ssn", Assert.Single(found).DetectorId.Value);
        Assert.Contains(UsIntegrationCandidates.ReadinessReport(), r => r.Id.Value == "us.phone" && r.Readiness != DetectorReadinessState.Ready);
    }

    // ===== country OFF never touches Korean / Global =========================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitedStatesOff_StopsEveryUsDetector_AndKeepsKoreanAndGlobalExactlyAsShipped(bool withAuthority)
    {
        var shipped = DetectionPipeline.CreateDefault();
        var off = UsOptInPipeline.Create(
            Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, authority: withAuthority ? UsTestAuthorityProvider.ValidSet() : null);
        const string text =
            "연락처 010-1234-5678 이메일 user@example.test 901231-1234567 카드 4111 1111 1111 1111 SSN: 321-54-9876 Phone: 212-555-1212 " +
            "Address: 123 Main ST Full Name: Alice Doe Passport: P12345678";

        var shippedFound = shipped.Detect(text).Candidates.OrderBy(c => c.Span.Start).Select(c => (c.PiiType, c.Span)).ToList();
        var offFound = off.Detect(text).Candidates.OrderBy(c => c.Span.Start).Select(c => (c.PiiType, c.Span)).ToList();

        Assert.True(shippedFound.Count >= 3);
        Assert.Equal(shippedFound, offFound);
        Assert.DoesNotContain(off.Detect(text).Candidates, c => c.DetectorId.Value.StartsWith("us.", StringComparison.Ordinal));
    }
}
