using Privon.Detection;
using Privon.Detection.Authority;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. The owner of the explicit U.S. opt-in: an explicitly chosen authority -> the opt-in pipeline for the CURRENT country
// setting -> the real CountryScopedPipelineHost -> a readiness snapshot read from the registry of the pipeline that is serving. It activates nothing
// (nothing in the shipped app constructs it), authenticates no setting (it follows the source it is given), and blocks neither a paste nor a send.
public class UsOptInOwnerTests
{
    private sealed class Source(CountrySettingObservation initial) : ICountrySettingSource
    {
        private int _value = (int)initial;
        public Exception? Throw { get; set; }
        public void Set(CountrySettingObservation value) => Interlocked.Exchange(ref _value, (int)value);

        public CountrySettingObservation Read()
        {
            if (Throw is { } e) throw e;
            return (CountrySettingObservation)Volatile.Read(ref _value);
        }
    }

    // one sample per detector: the label form each one needs, so "ready" can be checked against what the serving pipeline actually finds
    private static readonly (string Id, string Text)[] Samples =
    [
        ("us.atin", "ATIN: 912-93-4567"), ("us.itin", "ITIN: 912-70-1234"), ("us.ssn", "SSN: 321-54-9876"),
        ("us.mbi", "MBI: 1A23C45DE67"), ("us.sevis", "SEVIS: N1234567890"), ("us.passport", "Passport Number: P12345678 State: NY"),
        ("us.a_number", "A-Number: A1234567"), ("us.i94", "I-94 Number: 123456789AB"),
        ("us.driver_state_id", "Driver License Number: D123-4567 State: CA"),
        ("us.bank_account", "Routing Number: 120000003 | Account Number: AB12CD34"),
        ("us.phone", "Phone: 202-555-0147"), ("us.address", "Address: 123 Main Street"), ("us.name", "Full Name: Alice O'Neil"),
    ];

    private static ISet<string> FoundBy(DetectionPipeline pipeline, string text) =>
        pipeline.Detect(text).Candidates.Select(c => c.DetectorId.Value).ToHashSet(StringComparer.Ordinal);

    private static ISet<string> ServingIds(DetectionPipeline pipeline) =>
        Samples.Where(s => FoundBy(pipeline, s.Text).Contains(s.Id)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

    private static ISet<string> ReadyIds(UsOptInReadinessSnapshot snapshot) =>
        snapshot.Detectors.Where(d => d.Availability == UsOptInAvailability.Ready).Select(d => d.DetectorId).ToHashSet(StringComparer.Ordinal);

    private sealed class Defective : IAuthoritySnapshotProvider
    {
        private readonly UsProductionAuthorityProvider _real = new();
        private readonly HashSet<string> _missing = [];
        private readonly HashSet<string> _flipped = [];
        private readonly HashSet<string> _badHash = [];

        public Defective Without(AuthoritySnapshotId id) { _missing.Add(id.Value); return this; }
        public Defective Flipped(AuthoritySnapshotId id) { _flipped.Add(id.Value); return this; }
        public Defective BadHash(AuthoritySnapshotId id) { _badHash.Add(id.Value); return this; }

        public bool TryGetBytes(AuthoritySnapshotId id, out ReadOnlyMemory<byte> bytes)
        {
            if (_missing.Contains(id.Value) || !_real.TryGetBytes(id, out var real)) { bytes = default; return false; }
            var copy = real.ToArray();
            if (_flipped.Contains(id.Value)) copy[0] = copy[0] == (byte)'2' ? (byte)'3' : (byte)'2';
            bytes = copy;
            return true;
        }

        public bool TryGetMetadata(AuthoritySnapshotId id, out AuthoritySnapshotMetadata metadata)
        {
            if (_missing.Contains(id.Value) || !_real.TryGetMetadata(id, out var real)) { metadata = null!; return false; }
            metadata = _badHash.Contains(id.Value) ? real with { ExpectedSha256Hex = new string('0', 64) } : real;
            return true;
        }
    }

    // ===== the explicit choice of the verified authority ========================================================================

    [Fact]
    public void TheProductionChoice_ServesAThirteenOfThirteenPipeline_AndStatesItAsProductionVerified()
    {
        using var owner = UsOptInOwner.CreateWithProductionAuthority(new Source(CountrySettingObservation.Missing));

        var snapshot = owner.CurrentReadiness();

        Assert.True(snapshot.UsEnabled);
        Assert.Equal(UsOptInProvenance.Production, snapshot.Provenance);
        Assert.Equal(13, snapshot.Detectors.Count);
        Assert.All(snapshot.Detectors, d => Assert.Equal(UsOptInAvailability.Ready, d.Availability));
        Assert.Equal("us-authority/2026-11-02.1", snapshot.ArtifactVersion);
        Assert.Equal(ReadyIds(snapshot), ServingIds(owner.CurrentPipeline()));        // what is stated ready is exactly what the serving pipeline finds
    }

    [Fact]
    public void WithNoAuthority_ElevenAreReady_AndPhoneAndAddressAreNotProvided()
    {
        using var owner = UsOptInOwner.Create(new Source(CountrySettingObservation.Missing), null);

        var snapshot = owner.CurrentReadiness();

        Assert.Equal(UsOptInProvenance.NotSelected, snapshot.Provenance);
        Assert.Equal(11, snapshot.Detectors.Count(d => d.Availability == UsOptInAvailability.Ready));
        Assert.All(snapshot.Detectors.Where(d => d.DetectorId is "us.phone" or "us.address"),
            d => Assert.Equal(UsOptInAvailability.NotProvided, d.Availability));
        Assert.Null(snapshot.ArtifactVersion);
        Assert.Equal(ReadyIds(snapshot), ServingIds(owner.CurrentPipeline()));
    }

    [Fact]
    public void ASyntheticFixtureMakesAllThirteenReady_ButIsLabelledProvided_NeverProduction()
    {
        using var owner = UsOptInOwner.Create(new Source(CountrySettingObservation.Missing), UsTestAuthorityProvider.ValidSet());

        var snapshot = owner.CurrentReadiness();

        Assert.Equal(13, ReadyIds(snapshot).Count);
        Assert.Equal(UsOptInProvenance.Provided, snapshot.Provenance);
        Assert.Null(snapshot.ArtifactVersion);

        // and the same real bytes loaded without the production scope check are still only "Provided"
        using var shapeOnly = UsOptInOwner.Create(new Source(CountrySettingObservation.Missing), UsAuthoritySet.Load(new UsProductionAuthorityProvider()));
        Assert.Equal(UsOptInProvenance.Provided, shapeOnly.CurrentReadiness().Provenance);
    }

    [Fact]
    public void TheProductionLoadRefusesTheSmallFixtures_SoThePhoneAndAddressAreUnusableNotUnselected()
    {
        using var owner = UsOptInOwner.Create(new Source(CountrySettingObservation.Missing), UsAuthoritySet.LoadProduction(UsTestAuthorityProvider.Valid()));

        var snapshot = owner.CurrentReadiness();

        Assert.Equal(11, ReadyIds(snapshot).Count);
        Assert.All(snapshot.Detectors.Where(d => d.DetectorId is "us.phone" or "us.address"),
            d => Assert.Equal(UsOptInAvailability.ValidationFailed, d.Availability));
        Assert.Equal(ReadyIds(snapshot), ServingIds(owner.CurrentPipeline()));
    }

    [Theory]
    [InlineData("phone-only", "us.address")]
    [InlineData("address-only", "us.phone")]
    public void AnAuthorityThatBacksOneDetector_ReadiesThatOne_AndNamesTheOtherAsNotProvided(string which, string notReady)
    {
        var provider = which == "phone-only"
            ? new Defective().Without(UsAuthoritySet.SuffixId).Without(UsAuthoritySet.StateId)
            : new Defective().Without(UsAuthoritySet.NpaId);
        using var owner = UsOptInOwner.Create(new Source(CountrySettingObservation.Missing), UsAuthoritySet.LoadProduction(provider));

        var snapshot = owner.CurrentReadiness();

        Assert.Equal(12, ReadyIds(snapshot).Count);
        Assert.Equal(UsOptInAvailability.NotProvided, snapshot.Detectors.Single(d => d.DetectorId == notReady).Availability);
        Assert.Null(snapshot.ArtifactVersion);                                      // production-verified 13/13 is the only thing that names the artifact
        Assert.Equal(ReadyIds(snapshot), ServingIds(owner.CurrentPipeline()));
    }

    [Theory]
    [InlineData("bad-hash", "us.npa", "us.phone")]
    [InlineData("flipped", "us.npa", "us.phone")]
    [InlineData("bad-hash", "usps.suffix", "us.address")]
    [InlineData("flipped", "usps.state", "us.address")]
    public void ADamagedSnapshot_MakesItsDetectorUnusable_AndTheOtherStaysReady(string defect, string snapshotId, string unusable)
    {
        AuthoritySnapshotId.TryCreate(snapshotId, out var id);
        var provider = defect == "bad-hash" ? new Defective().BadHash(id) : new Defective().Flipped(id);
        using var owner = UsOptInOwner.Create(new Source(CountrySettingObservation.Missing), UsAuthoritySet.LoadProduction(provider));

        var snapshot = owner.CurrentReadiness();

        Assert.Equal(UsOptInAvailability.ValidationFailed, snapshot.Detectors.Single(d => d.DetectorId == unusable).Availability);
        Assert.Equal(12, ReadyIds(snapshot).Count);
        Assert.Equal(ReadyIds(snapshot), ServingIds(owner.CurrentPipeline()));
    }

    // ===== the country setting: followed, never authenticated ===================================================================

    [Fact]
    public void WhenTheSettingSaysOff_NoUsDetectorRuns_AndTheSnapshotSaysUsIsOff()
    {
        using var owner = UsOptInOwner.CreateWithProductionAuthority(new Source(CountrySettingObservation.AuthenticatedOff));

        var snapshot = owner.CurrentReadiness();

        Assert.False(snapshot.UsEnabled);
        Assert.Empty(ServingIds(owner.CurrentPipeline()));
    }

    [Theory]
    [InlineData(CountrySettingObservation.Missing)]
    [InlineData(CountrySettingObservation.Corrupt)]
    [InlineData(CountrySettingObservation.Unauthenticated)]
    [InlineData(CountrySettingObservation.UnknownVersion)]
    [InlineData(CountrySettingObservation.AuthenticatedOn)]
    public void AnyOtherOrUncertainSetting_KeepsTheUsDetectors_AsTheScopeAlreadyDoes(CountrySettingObservation setting)
    {
        using var owner = UsOptInOwner.CreateWithProductionAuthority(new Source(setting));

        var snapshot = owner.CurrentReadiness();

        Assert.True(snapshot.UsEnabled);
        Assert.Equal(13, ServingIds(owner.CurrentPipeline()).Count);
    }

    [Fact]
    public void TheProductsOwnSettingSource_NeverSaysOff_SoTheOwnerOverIt_NeverClaimsAnAuthenticatedSetting()
    {
        string root = Path.Combine(Path.GetTempPath(), "UsOptInOwner", Guid.NewGuid().ToString("N"));
        try
        {
            var store = PrivonLocalStore.OpenOrCreate(root);
            using var owner = UsOptInOwner.CreateWithProductionAuthority(new StoreBackedCountrySettingSource(store));

            Assert.True(owner.CurrentReadiness().UsEnabled);                          // T-1 is not created: this source can only say Missing / Unauthenticated
            Assert.Equal(13, ServingIds(owner.CurrentPipeline()).Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ===== freshness =============================================================================================================

    [Fact]
    public void ASettingChangeRebuildsOnce_AndTheSnapshotFollowsIt()
    {
        var source = new Source(CountrySettingObservation.Missing);
        using var owner = UsOptInOwner.CreateWithProductionAuthority(source);

        Assert.True(owner.CurrentReadiness().UsEnabled);
        Assert.True(owner.CurrentReadiness().UsEnabled);
        Assert.Equal(1, owner.BuildCount);                                            // served from the host's cache

        source.Set(CountrySettingObservation.AuthenticatedOff);
        Assert.False(owner.CurrentReadiness().UsEnabled);
        Assert.Equal(2, owner.BuildCount);

        source.Set(CountrySettingObservation.Missing);
        Assert.True(owner.CurrentReadiness().UsEnabled);
        Assert.Equal(3, owner.BuildCount);
    }

    [Fact]
    public void WhenTheSourceFails_NoOldReadinessIsServed_AndItRecoversWhenTheSourceDoes()
    {
        var source = new Source(CountrySettingObservation.Missing);
        using var owner = UsOptInOwner.CreateWithProductionAuthority(source);
        Assert.Equal(13, ReadyIds(owner.CurrentReadiness()).Count);

        source.Throw = new InvalidOperationException("synthetic settings failure");
        Assert.Throws<InvalidOperationException>(() => owner.CurrentReadiness());
        Assert.Throws<InvalidOperationException>(() => owner.CurrentPipeline());

        source.Throw = null;
        Assert.Equal(13, ReadyIds(owner.CurrentReadiness()).Count);
    }

    [Fact]
    public void AnUndefinedSettingObservation_IsRefused_NotServed()
    {
        var source = new Source((CountrySettingObservation)999);
        using var owner = UsOptInOwner.CreateWithProductionAuthority(source);

        Assert.ThrowsAny<ArgumentException>(() => owner.CurrentReadiness());
    }

    [Fact]
    public void AfterDispose_NeitherAPipelineNorAReadinessIsServed()
    {
        var owner = UsOptInOwner.CreateWithProductionAuthority(new Source(CountrySettingObservation.Missing));
        Assert.Equal(13, ReadyIds(owner.CurrentReadiness()).Count);

        owner.Dispose();
        owner.Dispose();                                                                 // idempotent

        Assert.Throws<ObjectDisposedException>(() => owner.CurrentReadiness());
        Assert.Throws<ObjectDisposedException>(() => owner.CurrentPipeline());
    }

    [Fact]
    public void ConcurrentSettingChangesAndReads_NeverYieldAMixedSnapshot_AndEndOnTheFinalSetting()
    {
        var source = new Source(CountrySettingObservation.Missing);
        using var owner = UsOptInOwner.CreateWithProductionAuthority(source);
        var bad = new List<string>();

        Parallel.For(0, 400, i =>
        {
            if (i % 3 == 0) source.Set(i % 2 == 0 ? CountrySettingObservation.AuthenticatedOff : CountrySettingObservation.Missing);
            var snapshot = owner.CurrentReadiness();
            // whichever setting it was built for, the snapshot is internally consistent: thirteen detectors, all ready
            if (snapshot.Detectors.Count != 13 || snapshot.Detectors.Any(d => d.Availability != UsOptInAvailability.Ready))
                lock (bad) bad.Add("inconsistent");
        });
        source.Set(CountrySettingObservation.AuthenticatedOff);

        Assert.Empty(bad);
        Assert.False(owner.CurrentReadiness().UsEnabled);
    }

    // ===== the rest of the product is unchanged ================================================================================

    // one sample per shipped detector, so that losing ANY of the nine from the owner's list changes the result
    private static readonly string[] ShippedSamples =
    [
        "연락처 010-1234-5678", "이메일 user@example.test", "901231-1234567", "카드 4111 1111 1111 1111", "계좌 123-456-789012",
        "api_key=SyntheticSecretValue123", "서버 192.168.10.20", "MAC 02:00:00:00:00:01", "37.5665,126.9780",
    ];

    [Fact]
    public void EachOfTheNineShippedDetectors_BehavesIdenticallyThroughTheOwner()
    {
        using var owner = UsOptInOwner.CreateWithProductionAuthority(new Source(CountrySettingObservation.AuthenticatedOff));
        string Key(DetectionCandidate c) => $"{c.PiiType}|{c.Span}|{c.Canonical?.Value}|{c.RiskLevel}|{c.DetectorId.Value}";

        foreach (string text in ShippedSamples)
        {
            var expected = DetectionPipeline.CreateDefault().Detect(text).Candidates.Select(Key).Order(StringComparer.Ordinal).ToList();
            Assert.NotEmpty(expected);                                                // the sample really is detected by the shipped pipeline
            Assert.Equal(expected, owner.CurrentPipeline().Detect(text).Candidates.Select(Key).Order(StringComparer.Ordinal).ToList());
        }

        Assert.Equal(9, UsOptInOwner.ShippedDetectors().Count);
    }

    [Fact]
    public void KoreanAndGlobalDetection_IsTheShippedOne_WhetherTheUsPackIsOnOrOff()
    {
        const string text = "연락처 010-1234-5678 이메일 user@example.test 901231-1234567 카드 4111 1111 1111 1111 서버 192.168.10.20";
        string Key(DetectionCandidate c) => $"{c.PiiType}|{c.Span}|{c.Canonical?.Value}|{c.RiskLevel}|{c.Confidence}";
        var expected = DetectionPipeline.CreateDefault().Detect(text).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();
        Assert.True(expected.Count >= 4);

        foreach (var setting in new[] { CountrySettingObservation.AuthenticatedOff, CountrySettingObservation.Missing, CountrySettingObservation.AuthenticatedOn })
        {
            using var owner = UsOptInOwner.CreateWithProductionAuthority(new Source(setting));
            Assert.Equal(expected, owner.CurrentPipeline().Detect(text).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList());
        }
    }

    [Fact]
    public void TheOwnerRegistersNothingAnywhere_TheShippedDefaultPipelineStillFindsNoUsType()
    {
        using var owner = UsOptInOwner.CreateWithProductionAuthority(new Source(CountrySettingObservation.Missing));
        _ = owner.CurrentPipeline();

        Assert.All(Samples, s => Assert.DoesNotContain(s.Id, FoundBy(DetectionPipeline.CreateDefault(), s.Text)));
    }

    [Fact]
    public void TheSnapshotCarriesNoClipboardText()
    {
        using var owner = UsOptInOwner.CreateWithProductionAuthority(new Source(CountrySettingObservation.Missing));
        var snapshot = owner.CurrentReadiness();
        string all = snapshot + string.Join("", snapshot.Detectors.Select(d => d.ToString()));

        Assert.DoesNotContain("Alice", all);
        Assert.DoesNotContain("321-54", all);
        Assert.All(snapshot.Detectors, d => Assert.Matches("^[a-z_0-9]+\\.[a-z_0-9]+$", d.DetectorId));
    }
}
