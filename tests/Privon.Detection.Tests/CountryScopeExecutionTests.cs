using System.Diagnostics.CodeAnalysis;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Family;
using Privon.Detection.Scope;

namespace Privon.Detection.Tests;

// SYNTHETIC DATA ONLY. Per-country execution scope of the explicit opt-in DetectionPipeline, exercised with pure product
// types and caller-supplied doubles. The settings layer (authentication, persistence, migration) does not exist in the
// product and is NOT simulated: the tests hand the pipeline the OBSERVATION a trusted layer would have made.
public sealed class CountryScopeExecutionTests
{
    private static readonly DetectorId AtinId = DetectorId.Create("us.atin");
    private static readonly DetectorId ItinId = DetectorId.Create("us.itin");
    private static readonly DetectorId SsnId = DetectorId.Create("us.ssn");
    private static readonly DetectorId MbiId = DetectorId.Create("us.mbi");
    private static readonly DetectorId EmailId = DetectorId.Create("global.email");
    private static readonly DetectorId KrPhoneId = DetectorId.Create("kr.phone");

    private const string Family = "test.family";

    private sealed class Probe(DetectorId id, int rank, PiiType type, string marker, List<string>? log = null) : IDetector
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
            int at = context.View.RawText.IndexOf(marker, StringComparison.Ordinal);
            return at < 0
                ? []
                : [new DetectionCandidate(type, new RawSpan(at, marker.Length), RiskLevel.Level3, DetectionConfidence.High,
                    new CanonicalValue(type, marker), "Probe", id, ResolutionEvidence.S, rank)];
        }
    }

    private sealed class Source(IReadOnlyCollection<DetectorId> members, string marker, List<string>? log = null) : IDetectorFamilyAttemptSource
    {
        public int Calls { get; private set; }
        public string FamilyName => Family;
        public IReadOnlyCollection<DetectorId> MemberDetectorIds => members;

        public IReadOnlyList<FamilyCandidateAttempt> CollectAttempts(DetectionContext context)
        {
            Calls++;
            log?.Add("source");
            int at = context.View.RawText.IndexOf(marker, StringComparison.Ordinal);
            return at < 0
                ? []
                : [new FamilyCandidateAttempt(SsnId, RiskLevel.Level3, ResolutionEvidence.S, new RawSpan(at, marker.Length),
                    new CanonicalValue(PiiType.UsSsn, marker), FamilyAttemptOutcome.Accepted, null)];
        }
    }

    private sealed class Arbiter(List<string>? log = null) : IDetectorFamilyArbiter
    {
        public int Calls { get; private set; }
        public string FamilyName => Family;

        public FamilyArbitrationResult Arbitrate(IReadOnlyList<FamilyCandidateAttempt> attempts)
        {
            Calls++;
            log?.Add("arbiter");
            return new FamilyArbitrationResult(
                attempts.Where(a => a.Outcome == FamilyAttemptOutcome.Accepted)
                    .Select(a => new DetectionCandidate(PiiType.UsSsn, a.Span, RiskLevel.Level3, DetectionConfidence.High, a.Canonical,
                        "Arbiter", SsnId, a.Evidence, 70)).ToList(),
                []);
        }
    }

    private sealed class Provider(IDetectorFamilyArbiter? arbiter) : IDetectorFamilyArbiterProvider
    {
        public int Requests { get; private set; }

        public bool TryGetArbiter(string familyName, [MaybeNullWhen(false)] out IDetectorFamilyArbiter result)
        {
            Requests++;
            result = arbiter;
            return arbiter is not null;
        }
    }

    private sealed class Rig
    {
        public Probe Mbi { get; } = new(MbiId, 80, PiiType.UsMbi, "MBI-X");
        public Probe Atin { get; } = new(AtinId, 50, PiiType.UsAtin, "never");
        public Probe Itin { get; } = new(ItinId, 60, PiiType.UsItin, "never");
        public Probe Ssn { get; } = new(SsnId, 70, PiiType.UsSsn, "never");
        public Probe Email { get; } = new(EmailId, 200, PiiType.Email, "MAIL-X");
        public Probe KrPhone { get; } = new(KrPhoneId, 300, PiiType.Phone, "PHONE-X");
        public Source Source { get; } = new([AtinId, ItinId, SsnId], "SSN-X");
        public Arbiter Arbiter { get; } = new();
        public Provider Provider { get; }

        public Rig() => Provider = new Provider(Arbiter);

        public IEnumerable<IDetector> All => [Ssn, Mbi, Email, Atin, KrPhone, Itin];

        public DetectionPipeline Pipeline(IReadOnlyList<CountryPackScope>? scopes, IEnumerable<IDetector>? detectors = null,
            IDetectorFamilyArbiterProvider? provider = null) =>
            scopes is null
                ? new DetectionPipeline(DetectorRegistry.Create((detectors ?? All).ToList()), provider ?? Provider, [Source])
                : new DetectionPipeline(DetectorRegistry.Create((detectors ?? All).ToList()), provider ?? Provider, [Source], scopes);
    }

    private static CountryPackScope Us(CountrySettingObservation setting, bool requireFamily = true) => new(
        CountryPack.UnitedStates, setting, [MbiId, AtinId, ItinId, SsnId], requireFamily ? [Family] : []);

    private const string Text = "SSN-X MBI-X MAIL-X PHONE-X";

    private static string Show(DetectionResult r) =>
        string.Join(";", r.Candidates.OrderBy(c => c.Span.Start).Select(c => $"{c.DetectorId.Value}@{c.Span.Start}:{c.Span.Length}"));

    // ---- ON / OFF -------------------------------------------------------------------------------------------------

    [Fact]
    public void UnitedStatesOn_RunsTheWholeUsPath_ExactlyLikeTheOptInPipelineWithoutScopes()
    {
        var rig = new Rig();

        var withoutScopes = Show(rig.Pipeline(null).Detect(Text));
        var on = Show(rig.Pipeline([Us(CountrySettingObservation.AuthenticatedOn)]).Detect(Text));

        Assert.Equal("us.ssn@0:5;us.mbi@6:5;global.email@12:6;kr.phone@19:7", on);
        Assert.Equal(withoutScopes, on);
    }

    [Fact]
    public void UnitedStatesAuthenticatedOff_RunsNoUsDetectorAndNoUsFamilySource_AndAsksNoArbiter()
    {
        var rig = new Rig();

        var off = rig.Pipeline([Us(CountrySettingObservation.AuthenticatedOff)]).Detect(Text);

        Assert.Equal("global.email@12:6;kr.phone@19:7", Show(off));
        Assert.Equal([0, 0, 0, 0], new[] { rig.Mbi.Calls, rig.Atin.Calls, rig.Itin.Calls, rig.Ssn.Calls });
        Assert.Equal(0, rig.Source.Calls);
        Assert.Equal(0, rig.Arbiter.Calls);
        Assert.Equal(0, rig.Provider.Requests);
    }

    [Fact]
    public void WhenUnitedStatesIsOff_GlobalAndKoreanResultsAreExactlyWhatTheyAreWithNoUsDetectorsAtAll()
    {
        var rig = new Rig();
        var bare = new Rig();
        var withoutUsAtAll = new DetectionPipeline(DetectorRegistry.Create([bare.Email, bare.KrPhone]), new Provider(new Arbiter()), []);

        var off = rig.Pipeline([Us(CountrySettingObservation.AuthenticatedOff)]).Detect(Text);
        var on = rig.Pipeline([Us(CountrySettingObservation.AuthenticatedOn)]).Detect(Text);

        Assert.Equal(Show(withoutUsAtAll.Detect(Text)), Show(off));
        Assert.Equal(
            on.Candidates.Where(c => !c.DetectorId.Value.StartsWith("us.", StringComparison.Ordinal)).Select(c => (c.DetectorId.Value, c.Span, c.Canonical)),
            off.Candidates.Select(c => (c.DetectorId.Value, c.Span, c.Canonical)));
    }

    [Fact]
    public void AnOffCountryContributesNothingToTheScan_ItsRequirementsAreNotEvenChecked()
    {
        var rig = new Rig();
        rig.Mbi.State = DetectorReadinessState.UnavailableAuthoritySnapshot; // a not-ready US detector...
        var unregisteredRequirement = new CountryPackScope(
            CountryPack.UnitedStates, CountrySettingObservation.AuthenticatedOff,
            [MbiId, DetectorId.Create("us.passport")], ["a.family.that.has.no.source"]);

        var result = rig.Pipeline([unregisteredRequirement]).Detect(Text); // ...an unregistered one, an unknown family: all moot

        Assert.Equal("global.email@12:6;kr.phone@19:7", Show(result));
    }

    // ---- uncertain settings never shrink protection ---------------------------------------------------------------

    [Theory]
    [InlineData(CountrySettingObservation.Missing)]
    [InlineData(CountrySettingObservation.Corrupt)]
    [InlineData(CountrySettingObservation.Unauthenticated)]
    [InlineData(CountrySettingObservation.UnknownVersion)]
    public void AnUncertainSetting_IsNeverAnOff_TheUsPathRunsInFull(CountrySettingObservation setting)
    {
        var rig = new Rig();

        var result = rig.Pipeline([Us(setting)]).Detect(Text);

        Assert.Equal("us.ssn@0:5;us.mbi@6:5;global.email@12:6;kr.phone@19:7", Show(result));
        Assert.Equal(1, rig.Mbi.Calls);
        Assert.Equal(1, rig.Source.Calls);
    }

    [Theory]
    [InlineData(CountrySettingObservation.AuthenticatedOn)]
    [InlineData(CountrySettingObservation.Missing)]
    [InlineData(CountrySettingObservation.Corrupt)]
    [InlineData(CountrySettingObservation.Unauthenticated)]
    [InlineData(CountrySettingObservation.UnknownVersion)]
    public void AnActiveCountryWithAMissingRequirement_FailsExplicitly_NeverAsASmallerCompleteScan(CountrySettingObservation setting)
    {
        var notReady = new Rig();
        notReady.Mbi.State = DetectorReadinessState.UnavailableAuthoritySnapshot;
        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() => notReady.Pipeline([Us(setting)]).Detect(Text));
        Assert.Equal(RequiredDetectorReason.NotReady, ex.Reason);

        var rig = new Rig();
        var missingDetector = new CountryPackScope(CountryPack.UnitedStates, setting, [DetectorId.Create("us.passport")], []);
        var unregistered = Assert.Throws<RequiredDetectorNotRegisteredException>(() => rig.Pipeline([missingDetector]).Detect(Text));
        Assert.Equal(RequiredDetectorReason.Unregistered, unregistered.Reason);

        var noSource = new CountryPackScope(CountryPack.UnitedStates, setting, [], ["a.required.family"]);
        var family = Assert.Throws<FamilyArbitrationUnavailableException>(() => rig.Pipeline([noSource]).Detect(Text));
        Assert.Equal("a.required.family", family.FamilyName);

        var noArbiter = new Rig();
        Assert.Throws<FamilyArbitrationUnavailableException>(() =>
            noArbiter.Pipeline([Us(setting)], provider: new Provider(null)).Detect(Text));
    }

    [Fact]
    public void WithAnArbiterMissing_OffStillRunsTheRest_AndOnFailsEvenForTextWithNothingToArbitrate()
    {
        var rig = new Rig();
        var noArbiter = new Provider(null);

        var off = rig.Pipeline([Us(CountrySettingObservation.AuthenticatedOff)], provider: noArbiter).Detect(Text);

        Assert.Equal("global.email@12:6;kr.phone@19:7", Show(off));
        Assert.Equal(0, noArbiter.Requests);
        Assert.Throws<FamilyArbitrationUnavailableException>(() =>
            rig.Pipeline([Us(CountrySettingObservation.Missing)], provider: noArbiter).Detect("nothing"));
    }

    [Fact]
    public void AnUndefinedSettingOrPack_IsRejectedAtConstruction_NeverReadAsOff()
    {
        var rig = new Rig();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            rig.Pipeline([new CountryPackScope(CountryPack.UnitedStates, (CountrySettingObservation)99, [], [])]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            rig.Pipeline([new CountryPackScope((CountryPack)99, CountrySettingObservation.AuthenticatedOff, [], [])]));
    }

    // ---- ownership is by the approved table, never by a name -------------------------------------------------------

    [Fact]
    public void KoreanAndGlobalDetectors_HaveNoApprovedOwner_SoTheyRunUnderEverySetting()
    {
        foreach (var setting in Enum.GetValues<CountrySettingObservation>())
        {
            var rig = new Rig();
            var result = rig.Pipeline([Us(setting)]).Detect(Text);

            Assert.Contains(result.Candidates, c => c.DetectorId == EmailId);
            Assert.Contains(result.Candidates, c => c.DetectorId == KrPhoneId);
        }
    }

    [Fact]
    public void ARequiredDetectorOfAnotherOwnership_AndAMixedOwnershipSource_AreRefused()
    {
        var rig = new Rig();

        Assert.Throws<ArgumentException>(() => rig.Pipeline(
            [new CountryPackScope(CountryPack.UnitedStates, CountrySettingObservation.AuthenticatedOn, [EmailId], [])]));

        var mixed = new Source([SsnId, EmailId], "SSN-X");
        Assert.Throws<ArgumentException>(() => new DetectionPipeline(
            DetectorRegistry.Create(rig.All.ToList()), rig.Provider, [mixed], [Us(CountrySettingObservation.AuthenticatedOn)]));
    }

    [Fact]
    public void AFamilyWhoseMembersHaveNoOwner_IsNeverStopped()
    {
        var unowned = new Probe(EmailId, 200, PiiType.Email, "MAIL-X");
        var source = new FamilySource(Family, [EmailId]);
        var pipeline = new DetectionPipeline(
            DetectorRegistry.Create([unowned]), new Provider(new Arbiter()), [source],
            [Us(CountrySettingObservation.AuthenticatedOff, requireFamily: false) with { RequiredDetectorIds = [] }]);

        _ = pipeline.Detect("x");

        Assert.Equal(1, source.Calls);
    }

    private sealed class FamilySource(string name, IReadOnlyCollection<DetectorId> members) : IDetectorFamilyAttemptSource
    {
        public int Calls { get; private set; }
        public string FamilyName => name;
        public IReadOnlyCollection<DetectorId> MemberDetectorIds => members;

        public IReadOnlyList<FamilyCandidateAttempt> CollectAttempts(DetectionContext context)
        {
            Calls++;
            return [];
        }
    }

    // ---- selection order never changes the result ------------------------------------------------------------------

    [Fact]
    public void RegistrySupplyOrderAndRequirementOrder_DoNotChangeTheResultForAnySetting()
    {
        foreach (var setting in Enum.GetValues<CountrySettingObservation>())
        {
            string? reference = null;
            foreach (var order in new[] { new[] { 0, 1, 2, 3, 4, 5 }, new[] { 5, 4, 3, 2, 1, 0 }, new[] { 2, 0, 5, 1, 4, 3 } })
            {
                var rig = new Rig();
                var all = rig.All.ToArray();
                var scope = new CountryPackScope(CountryPack.UnitedStates, setting, order.Select(i => all[i].Id).Where(i => i.Value.StartsWith("us.", StringComparison.Ordinal)).ToArray(), [Family]);

                string shown = Show(rig.Pipeline([scope], order.Select(i => all[i])).Detect(Text));

                reference ??= shown;
                Assert.Equal(reference, shown);
            }
        }
    }

    [Fact]
    public void TwoScopesForTheSamePack_AreRefused_SoNoSelectionOrderCanDecideTheOutcome()
    {
        var rig = new Rig();

        Assert.Throws<ArgumentException>(() => rig.Pipeline(
            [Us(CountrySettingObservation.AuthenticatedOff), Us(CountrySettingObservation.AuthenticatedOn)]));
        Assert.Throws<ArgumentException>(() => rig.Pipeline(
            [Us(CountrySettingObservation.AuthenticatedOn), Us(CountrySettingObservation.AuthenticatedOff)]));
    }

    [Fact]
    public void ConstructionRejectsNullsAndBlankFamilyNames()
    {
        var rig = new Rig();

        Assert.Throws<ArgumentNullException>(() => new DetectionPipeline(DetectorRegistry.Create(rig.All.ToList()), rig.Provider, [rig.Source], null!));
        Assert.Throws<ArgumentNullException>(() => rig.Pipeline([null!]));
        Assert.Throws<ArgumentNullException>(() =>
            rig.Pipeline([new CountryPackScope(CountryPack.UnitedStates, CountrySettingObservation.AuthenticatedOn, null!, [])]));
        Assert.Throws<ArgumentNullException>(() =>
            rig.Pipeline([new CountryPackScope(CountryPack.UnitedStates, CountrySettingObservation.AuthenticatedOn, [], null!)]));
        Assert.Throws<ArgumentException>(() =>
            rig.Pipeline([new CountryPackScope(CountryPack.UnitedStates, CountrySettingObservation.AuthenticatedOn, [], [" "])]));
    }

    [Fact]
    public void TheDefaultPipeline_ReadsNoCountryScope_AndStillRunsEveryDetector()
    {
        var before = Show(DetectionPipeline.CreateDefault().Detect("a.b@example.com 010-1234-5678 901231-1234567"));
        _ = new Rig().Pipeline([Us(CountrySettingObservation.AuthenticatedOff)]);
        var after = Show(DetectionPipeline.CreateDefault().Detect("a.b@example.com 010-1234-5678 901231-1234567"));

        Assert.Equal(before, after);
        Assert.NotEqual(string.Empty, before);
    }
}
