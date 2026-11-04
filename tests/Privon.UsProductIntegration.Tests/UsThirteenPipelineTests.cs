using System.Security.Cryptography;
using System.Text;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Authority;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. The thirteen U.S. identities through the product's REAL opt-in DetectionPipeline, the authority snapshots through
// the production loader, the per-country scope, and the real Level 1 / Level 2 / Level 3 policy and alias stages. The shipped product has
// NO production NPA / USPS snapshot, so phone and address are ready only when a provider backs them (here: the four-entry D3A fixtures).
public class UsThirteenPipelineTests
{
    private const string TenText =
        "Passport Number: P12345678 State: NY\nA-Number: A1234567\nI-94 Number: 123456789AB\n" +
        "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890\n" +
        "Driver License Number: D123-4567 State: CA\nRouting Number: 120000003 | Account Number: AB12CD34";

    private const string ThirteenText = TenText + "\nPhone: 212-555-1212\nAddress: 123 Main ST\nFull Name: Alice O'Neil";

    private static readonly UsAuthoritySet Authority = UsTestAuthorityProvider.ValidSet();

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static DetectionResult Pipe(string raw, UsAuthoritySet? authority = null) =>
        UsOptInPipeline.Create(authority: authority ?? Authority).Detect(raw);

    private static string Key(DetectionCandidate c) =>
        $"{c.DetectorId.Value}|{c.Span}|{c.Canonical?.Value}|{c.Evidence}|{c.OwnerClaim}|{c.RiskLevel}|{c.Confidence}";

    private static List<string> Keys(IEnumerable<DetectionCandidate> candidates) =>
        candidates.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();

    public static TheoryData<CountrySettingObservation> NotAuthenticatedOff => new()
    {
        CountrySettingObservation.AuthenticatedOn, CountrySettingObservation.Missing, CountrySettingObservation.Corrupt,
        CountrySettingObservation.Unauthenticated, CountrySettingObservation.UnknownVersion,
    };

    // ===== the authority snapshots: provenance and readiness ===============================================================

    [Fact]
    public void TheD3Snapshots_CarryTheFrozenProvenance_AndPassTheProductionLoader()
    {
        var provider = UsTestAuthorityProvider.Valid();

        foreach (var (id, url, sha, text) in new[]
                 {
                     (UsAuthoritySet.NpaId, UsTestAuthorityProvider.NpaUrl, UsTestAuthorityProvider.NpaSha256, UsTestAuthorityProvider.NpaText),
                     (UsAuthoritySet.SuffixId, UsTestAuthorityProvider.SuffixUrl, UsTestAuthorityProvider.SuffixSha256, UsTestAuthorityProvider.SuffixText),
                     (UsAuthoritySet.StateId, UsTestAuthorityProvider.StateUrl, UsTestAuthorityProvider.StateSha256, UsTestAuthorityProvider.StateText),
                 })
        {
            Assert.True(provider.TryGetMetadata(id, out var metadata));
            Assert.Equal(url, metadata.SourceUrl);
            Assert.Equal(new DateOnly(2026, 9, 15), metadata.RetrievalDate);
            Assert.Equal(4, metadata.ExpectedRecordCount);
            Assert.Equal(sha, metadata.ExpectedSha256Hex);
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
            Assert.True(AuthoritySnapshotLoader.TryLoad(provider, id, out var snapshot, out var validation));
            Assert.Equal(AuthoritySnapshotState.SnapshotValid, validation.State);
            Assert.Equal(4, snapshot.NormalizedRecords.Count);
        }

        Assert.True(provider.TryGetMetadata(UsAuthoritySet.NpaId, out var npa));
        Assert.Equal(UsTestAuthorityProvider.NpaCatalogUrl, npa.CatalogUrl);
    }

    [Fact]
    public void TheProductActivatesNoAuthoritySnapshotByDefault_NoHiddenList()
    {
        var assembly = typeof(UsAuthoritySet).Assembly;

        // 0.4.0: the official-source artifact IS embedded now (UsAuthorityProductionTests pins it), but only under AuthorityData.* and only as an
        // EXPLICIT choice (UsAuthoritySet.Production). Anything else carrying a phone / USPS list would still be a hidden default list.
        Assert.DoesNotContain(assembly.GetManifestResourceNames(), n =>
            !n.StartsWith("Privon.UsProductIntegration.AuthorityData.", StringComparison.Ordinal)
            && (n.Contains("npa", StringComparison.OrdinalIgnoreCase) || n.Contains("usps", StringComparison.OrdinalIgnoreCase)));
        Assert.False(UsAuthoritySet.Unavailable.PhoneReady);
        Assert.False(UsAuthoritySet.Unavailable.AddressReady);
        Assert.Equal(AuthoritySnapshotState.SnapshotMissing, UsAuthoritySet.Unavailable.NpaState);
        Assert.All(UsIntegrationCandidates.ReadinessReport(), r =>
            Assert.Equal(
                r.Id.Value is "us.phone" or "us.address" ? DetectorReadinessState.UnavailableAuthoritySnapshot : DetectorReadinessState.Ready,
                r.Readiness));
    }

    [Fact]
    public void WithTheValidD3Snapshots_AllThirteenAreReady()
    {
        var report = UsIntegrationCandidates.ReadinessReport(Authority);

        Assert.Equal(13, report.Count);
        Assert.All(report, r => Assert.Equal(DetectorReadinessState.Ready, r.Readiness));
        Assert.Equal(13, UsIntegrationCandidates.CreateDetectors(Authority).Count);
        Assert.Equal(13, UsIntegrationCandidates.RequiredDetectorIds(Authority).Count);
    }

    public static TheoryData<string, string> BrokenSnapshots => new()
    {
        { "us.npa", "missing" }, { "us.npa", "tampered" }, { "us.npa", "count" }, { "us.npa", "dupinvalid" }, { "us.npa", "metadata" },
        { "usps.suffix", "missing" }, { "usps.suffix", "tampered" }, { "usps.suffix", "count" }, { "usps.suffix", "metadata" },
        { "usps.state", "missing" }, { "usps.state", "tampered" }, { "usps.state", "count" }, { "usps.state", "dupinvalid" },
        { "usps.state", "metadata" },
    };

    private static UsTestAuthorityProvider Broken(string id, string how)
    {
        var snapshotId = id switch { "us.npa" => UsAuthoritySet.NpaId, "usps.suffix" => UsAuthoritySet.SuffixId, _ => UsAuthoritySet.StateId };
        var provider = UsTestAuthorityProvider.Valid();
        return how switch
        {
            "missing" => provider.Without(snapshotId),
            "tampered" => provider.Tampered(snapshotId),
            "count" => provider.WithContent(snapshotId, id == "us.npa" ? "212\n340\n787\n" : id == "usps.suffix" ? "AVE\nBLVD\nRD\n" : "CA\nNY\nPR\nVI\nTX\n")
                .WithDeclaredCount(snapshotId, 4),
            "dupinvalid" => provider.WithContent(snapshotId, id == "us.npa" ? "212\n212\n787\n93X\n" : "CA\nCA\nNY\nN1\n")
                .WithDeclaredCount(snapshotId, 4),
            "metadata" => provider.WithoutRetrievalDate(snapshotId),
            _ => throw new ArgumentOutOfRangeException(nameof(how)),
        };
    }

    [Theory]
    [MemberData(nameof(BrokenSnapshots))]
    public void AnyBrokenSnapshot_MakesOnlyTheDependingDetectorUnavailable_WithZeroCandidates_AndNoFallback(string id, string how)
    {
        var set = UsAuthoritySet.Load(Broken(id, how));

        Assert.Equal(id == "us.npa", !set.PhoneReady);
        Assert.Equal(id != "us.npa", !set.AddressReady);
        Assert.Equal(12, UsIntegrationCandidates.CreateDetectors(set).Count);
        var report = UsIntegrationCandidates.ReadinessReport(set);
        Assert.Equal(DetectorReadinessState.UnavailableAuthoritySnapshot,
            report.Single(r => r.Id.Value == (id == "us.npa" ? "us.phone" : "us.address")).Readiness);
        Assert.Equal(12, report.Count(r => r.Readiness == DetectorReadinessState.Ready));

        // the depending detector, constructed directly with the broken set, emits nothing; the other one is untouched
        var phone = Run(new UsPhoneCandidateDetector(set), "Phone: 212-555-1212 +12125551212");
        var address = Run(new UsAddressCandidateDetector(set), "Address: 123 Main ST\n123 Main ST, New York, NY 10001");
        Assert.Equal(id == "us.npa", phone.Count == 0);
        Assert.Equal(id != "us.npa", address.Count == 0);
        Assert.Equal(id == "us.npa" ? 2 : 0, address.Count);
        Assert.Equal(id == "us.npa" ? 0 : 2, phone.Count);
    }

    [Fact]
    public void TheLoaderStates_AreTheFrozenD3Ones()
    {
        Assert.Equal(AuthoritySnapshotState.SnapshotMissing, UsAuthoritySet.Load(Broken("us.npa", "missing")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureHashMismatch, UsAuthoritySet.Load(Broken("us.npa", "tampered")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureCountMismatch, UsAuthoritySet.Load(Broken("us.npa", "count")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord, UsAuthoritySet.Load(Broken("us.npa", "dupinvalid")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureMissingMetadata, UsAuthoritySet.Load(Broken("us.npa", "metadata")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotValid, UsAuthoritySet.Load(UsTestAuthorityProvider.Valid()).NpaState);
    }

    [Fact]
    public void ASelfConsistentSnapshotWhoseRecordsAreNotUsable_IsRejected_NotTrusted() // synthetic
    {
        // valid bytes, hash and count, but "999" is not a possible NPA (leading digit) and "S" is not a plausible suffix
        Assert.False(UsAuthoritySet.Load(UsTestAuthorityProvider.Valid().WithContent(UsAuthoritySet.NpaId, "212\n199\n")).PhoneReady);
        Assert.False(UsAuthoritySet.Load(UsTestAuthorityProvider.Valid().WithContent(UsAuthoritySet.SuffixId, "ST\nS\n")).AddressReady);
        Assert.False(UsAuthoritySet.Load(UsTestAuthorityProvider.Valid().WithContent(UsAuthoritySet.StateId, "NY\nNew\n")).AddressReady);
        Assert.Throws<ArgumentNullException>(() => UsAuthoritySet.Load(null!));
    }

    [Fact]
    public void AnotherSnapshotContent_ChangesTheDetection_ProvingNoListIsBuiltIn()
    {
        var other = UsAuthoritySet.Load(UsTestAuthorityProvider.Valid().WithContent(UsAuthoritySet.NpaId, "416\n"));

        Assert.Empty(Run(new UsPhoneCandidateDetector(other), "212-555-1212"));
        Assert.Single(Run(new UsPhoneCandidateDetector(other), "416-555-1212"));
        Assert.Empty(Run(new UsAddressCandidateDetector(UsAuthoritySet.Load(UsTestAuthorityProvider.Valid().WithContent(UsAuthoritySet.SuffixId, "ROAD\n"))),
            "Address: 123 Main ST"));
    }

    // ===== D3A snapshot rows at the pipeline level =========================================================================

    [Fact]
    public void SnapNpaMissing_PhoneIsUnavailable_TheEmailCarriesOn() // SNAP-NPA-MISSING
    {
        const string raw = "Email: alice@example.com Phone: 212-555-1212";
        var set = UsAuthoritySet.Load(UsTestAuthorityProvider.Valid().Without(UsAuthoritySet.NpaId));

        var found = UsOptInPipeline.Create(Legacy(), authority: set).Detect(raw).Candidates;

        var email = Assert.Single(found);
        Assert.Equal("global.email|Email|L2|S|7:17|alice@example.com|[이메일1]|200", Oracle(email));
    }

    [Fact]
    public void SnapNpaTampered_NothingIsDetected() // SNAP-NPA-TAMPERED
    {
        var set = UsAuthoritySet.Load(UsTestAuthorityProvider.Valid().Tampered(UsAuthoritySet.NpaId));

        Assert.Empty(UsOptInPipeline.Create(authority: set).Detect("212-555-1212").Candidates);
    }

    [Theory]
    [InlineData("usps.state", "missing", "123 Main ST, New York, NY 10001")]      // SNAP-STATE-MISSING
    [InlineData("usps.state", "tampered", "123 Main ST, New York, NY 10001")]     // SNAP-STATE-TAMPERED
    [InlineData("usps.suffix", "missing", "Address: 123 Main ST")]                // SNAP-SUFFIX-MISSING
    [InlineData("usps.suffix", "tampered", "Address: 123 Main ST")]               // SNAP-SUFFIX-TAMPERED
    public void SnapAddressAuthorityBroken_AddressIsUnavailable_NothingIsDetected(string id, string how, string raw)
    {
        var set = UsAuthoritySet.Load(Broken(id, how));

        Assert.Empty(UsOptInPipeline.Create(authority: set).Detect(raw).Candidates);
    }

    // ===== registration, H-2, country scope ================================================================================

    [Fact]
    public void ThePipeline_ProducesTheThirteenRecords_ThroughTheProductsOwnConstructor()
    {
        static string At(string value) => ThirteenText.IndexOf(value, StringComparison.Ordinal).ToString();

        var found = Pipe(ThirteenText).Candidates.OrderBy(c => c.Span.Start).Select(OracleWithOwner).ToList();

        Assert.Equal(13, found.Count);
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
                $"us.driver_state_id|UsDriverStateId|L3|S+C+J|{At("D123-4567")}:9|D1234567|[운전면허번호1]|130",
                $"us.bank_account|BankAccount|L3|S+C+P|{At("AB12CD34")}:8|AB12CD34|[계좌1]|140",
                $"us.phone|Phone|L2|S+C|{At("212-555-1212")}:12|2125551212|[전화번호1]|310",
                $"us.address|Address|L2|S+C|{At("123 Main ST")}:11|123 Main ST|[주소1]|330",
                $"us.name|Name|L2|C|{At("Alice O'Neil")}:12|Alice O'Neil|[이름1]|350",
            ],
            found);
    }

    [Fact]
    public void TheApprovedIdList_IsTheElevenLiterals_AndThePhoneAndAddressAreAuthorityDependent()
    {
        string[] eleven =
        [
            "us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis", "us.passport", "us.a_number", "us.i94",
            "us.driver_state_id", "us.bank_account", "us.name",
        ];

        Assert.Equal(eleven, UsIntegrationCandidates.ApprovedDetectorIds.Select(i => i.Value));
        Assert.Equal(["us.phone", "us.address"], UsIntegrationCandidates.AuthorityDependentDetectorIds.Select(i => i.Value));
        Assert.Equal(11, UsIntegrationCandidates.CreateDetectors().Count);
        Assert.Equal(eleven, UsIntegrationCandidates.RequiredDetectorIds().Select(i => i.Value));
        Assert.Equal(eleven, UsOptInPipeline.UsScope(CountrySettingObservation.Missing).RequiredDetectorIds.Select(i => i.Value));
        Assert.Equal(13, UsOptInPipeline.UsScope(CountrySettingObservation.Missing, Authority).RequiredDetectorIds.Count);
    }

    [Theory]
    [InlineData("us.name", false)]
    [InlineData("us.phone", true)]
    [InlineData("us.address", true)]
    public void AFactoryThatDropsARequiredDetector_IsDetected_WheneverTheUsPackIsNotAuthenticatedOff(string dropped, bool needsAuthority)
    {
        IReadOnlyList<IDetector> Without() => UsIntegrationCandidates.CreateDetectors(Authority).Where(d => d.Id.Value != dropped).ToList();

        foreach (var setting in new[]
                 { CountrySettingObservation.AuthenticatedOn, CountrySettingObservation.Missing, CountrySettingObservation.Corrupt,
                   CountrySettingObservation.Unauthenticated, CountrySettingObservation.UnknownVersion })
        {
            var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() =>
                UsOptInPipeline.Create(unitedStatesSetting: setting, usDetectorFactory: Without, authority: needsAuthority ? Authority : null)
                    .Detect(ThirteenText));
            Assert.Equal(dropped, ex.Id.Value);
            Assert.Equal(RequiredDetectorReason.Unregistered, ex.Reason);
        }

        Assert.Empty(UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, usDetectorFactory: Without, authority: Authority)
            .Detect(ThirteenText).Candidates);
    }

    [Fact]
    public void PhoneAndAddress_AreNotRequired_WithoutAnAuthority_ASmallerButHonestScan() // the report says so; D2.2: unavailable -> zero candidates
    {
        foreach (var setting in new[] { CountrySettingObservation.Missing, CountrySettingObservation.Corrupt, CountrySettingObservation.AuthenticatedOn })
        {
            var found = UsOptInPipeline.Create(unitedStatesSetting: setting).Detect(ThirteenText).Candidates;

            Assert.Equal(11, found.Count);
            Assert.DoesNotContain(found, c => c.PiiType is PiiType.Phone or PiiType.Address);
        }
    }

    [Fact]
    public void ANotReadyDetector_RegisteredByHand_FailsTheScan_AsEveryRegisteredDetectorMustBeReady()
    {
        IReadOnlyList<IDetector> WithNotReadyPhone() =>
            [.. UsIntegrationCandidates.CreateDetectors(), new UsPhoneCandidateDetector()];

        var pipeline = UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.Missing, usDetectorFactory: WithNotReadyPhone);

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() => pipeline.Detect("Phone: 212-555-1212"));
        Assert.Equal("us.phone", ex.Id.Value);
        Assert.Equal(RequiredDetectorReason.NotReady, ex.Reason);
    }

    [Fact]
    public void UnitedStatesAuthenticatedOff_StopsAllThirteen_AndLeavesKoreanAndGlobalUntouched()
    {
        var shipped = DetectionPipeline.CreateDefault();
        var off = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, authority: Authority);
        string mixed = ThirteenText + "\n연락처 010-1234-5678 이메일 user@example.test 901231-1234567";

        Assert.Empty(off.Detect(ThirteenText).Candidates);
        Assert.Equal(
            shipped.Detect(mixed).Candidates.OrderBy(c => c.Span.Start).Select(c => (c.PiiType, c.Span)),
            off.Detect(mixed).Candidates.OrderBy(c => c.Span.Start).Select(c => (c.PiiType, c.Span)));
    }

    [Theory]
    [MemberData(nameof(NotAuthenticatedOff))]
    public void EveryOtherSetting_ProtectsAllThirteen_ExactlyLikeAnUnscopedPipeline(CountrySettingObservation setting)
    {
        var unscoped = UsOptInPipeline.Create(authority: Authority);
        var scoped = UsOptInPipeline.Create(unitedStatesSetting: setting, authority: Authority);

        Assert.Equal(13, scoped.Detect(ThirteenText).Candidates.Count);
        Assert.Equal(Keys(unscoped.Detect(ThirteenText).Candidates), Keys(scoped.Detect(ThirteenText).Candidates));
    }

    [Fact]
    public void TheShippedDefaultPipeline_DetectsNoneOfTheNewTypes()
    {
        var shipped = DetectionPipeline.CreateDefault();

        foreach (string raw in new[] { "Full Name: Alice O'Neil", "Address: 123 Main ST", "123 Main ST, New York, NY 10001", "Mr Alice Doe", "Phone: 212-555-1212" })
        {
            Assert.DoesNotContain(shipped.Detect(raw).Candidates, c => c.DetectorId.HasValue && c.DetectorId.Value.StartsWith("us.", StringComparison.Ordinal));
        }
    }

    // ===== the earlier ten are unchanged ===================================================================================

    [Fact]
    public void TheEarlierTen_ProduceTheSameCandidates_InTheThirteenDetectorPipeline_OnTheFrozenFixturesAndAGeneratedCorpus()
    {
        var ten = new DetectionPipeline(
            DetectorRegistry.Create([.. AllEight(), new UsDriverStateIdCandidateDetector(), new UsBankAccountCandidateDetector()]),
            new UsTaxpayerFamilyArbiterProvider(), [new UsTaxpayerFamilyAttempts()]);
        var thirteen = UsOptInPipeline.Create(authority: Authority);
        HashSet<string> tenIds =
            ["us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis", "us.passport", "us.a_number", "us.i94", "us.driver_state_id", "us.bank_account"];
        int withNew = 0;

        string[] newFragments =
        [
            "Phone: 212-555-1212", "Name: Alice Doe", "Full Name: Bob O'Neil", "Address: 123 Main ST", "123 Main ST, New York, NY 10001",
            "Mr Alice Doe", "212-555-1212", "Tel: 340.555.1212 x5", "Surname: Smith", "+1 (787) 555-1212",
        ];
        int n = 0;
        foreach (string raw in Corpus.Generate(seed: 20261201, count: 2500)
                     .Select(s => s + (n % 2 == 0 ? "; " : "\n") + newFragments[n++ % newFragments.Length])
                     .Append(TenText).Append(ThirteenText).Append("ATIN: 912-88-4321 Name: Alice Doe Phone: 212-555-1212"))
        {
            var all = thirteen.Detect(raw).Candidates;
            if (all.Any(c => !tenIds.Contains(c.DetectorId.Value))) withNew++;

            Assert.Equal(Keys(ten.Detect(raw).Candidates), Keys(all.Where(c => tenIds.Contains(c.DetectorId.Value))));
        }

        Assert.True(withNew > 2000, "the corpus must actually contain candidates of the new detectors");
    }

    [Fact]
    public void TheCatalogAdditionsOfThePreviousTask_AndTheNewLabels_DoNotChangeTheEarlierTen_OnLabelledFixtures()
    {
        foreach (string raw in new[]
                 { "Passport: P12345678 Phone: 212-555-1212", "SSN: 321-54-9876 Name: Alice Doe", "I-94: 123456789AB Address: 123 Main ST" })
        {
            var without = UsOptInPipeline.Create().Detect(raw).Candidates.Where(c => c.PiiType is not (PiiType.Name or PiiType.Address or PiiType.Phone));
            var with = Pipe(raw).Candidates.Where(c => c.PiiType is not (PiiType.Name or PiiType.Address or PiiType.Phone));

            Assert.Equal(Keys(without), Keys(with));
        }
    }

    // ===== overlaps and resolver priority ==================================================================================

    [Fact]
    public void AKoreanAndAUsPhone_AreBothKept_WithConsecutiveAliasNumbers() // REG-KR-US-PHONE-001
    {
        const string raw = "KR: 010-2345-6789 US: 212-555-1212";
        var found = UsOptInPipeline.Create(Legacy(), authority: Authority).Detect(raw).Candidates.OrderBy(c => c.Span.Start).ToList();
        var map = new AliasMap();

        Assert.Equal(2, found.Count);
        Assert.Equal(PiiType.Phone, found[0].PiiType);
        Assert.Equal(new RawSpan(4, 13), found[0].Span);
        Assert.Equal("us.phone|Phone|L2|S|22:12|2125551212|[전화번호1]|310", Oracle(found[1])); // the oracle always prints number 1
        Assert.Equal(["[전화번호1]", "[전화번호2]"], found.Select(c => map.GetOrAdd(c.Canonical!.Value).Value));
    }

    [Fact]
    public void ALevel3Value_BeatsAnOverlappingLevel2Phone_ByTheFrozenLevelOrder()
    {
        // The driver-license value "212-555-1212" is also an unlabelled formatted US phone on the same span: Level 3 wins.
        const string raw = "Driver License: 212-555-1212 State: CA";

        var winner = Assert.Single(Pipe(raw).Candidates);

        Assert.Equal("us.driver_state_id", winner.DetectorId.Value);
        Assert.Equal(RiskLevel.Level3, winner.RiskLevel);
    }

    [Fact]
    public void ALabelledAddressAndItsOwnPathBReading_AreOneCandidate_WithTheLabelledEvidence()
    {
        var winner = Assert.Single(Pipe("Address: 123 Main ST, New York, NY 10001").Candidates);

        Assert.Equal(ResolutionEvidence.SC, winner.Evidence);
        Assert.Equal(new RawSpan(9, 31), winner.Span);
    }

    [Fact]
    public void ANameLabelInsideAnAddressField_AndAPhoneLabelNextToIt_StayIndependent()
    {
        const string raw = "Full Name: Alice Doe; Phone: 212-555-1212; Address: 123 Main ST";

        var found = Pipe(raw).Candidates.OrderBy(c => c.Span.Start).Select(c => (c.DetectorId.Value, raw.Substring(c.Span.Start, c.Span.Length))).ToList();

        Assert.Equal([("us.name", "Alice Doe"), ("us.phone", "212-555-1212"), ("us.address", "123 Main ST")], found);
    }

    [Fact]
    public void TheResolution_DoesNotDependOnTheOrderOfTheDetectorsInTheFactory()
    {
        var forward = UsOptInPipeline.Create(authority: Authority);
        var reversed = UsOptInPipeline.Create(
            usDetectorFactory: () => UsIntegrationCandidates.CreateDetectors(Authority).Reverse().ToList(), authority: Authority);

        foreach (string raw in new[] { ThirteenText, "Driver License: 212-555-1212 State: CA", "Address: 123 Main ST, New York, NY 10001", "Mr Alice Doe Phone: 212-555-1212" })
        {
            Assert.Equal(Keys(forward.Detect(raw).Candidates), Keys(reversed.Detect(raw).Candidates));
        }
    }

    // ===== Level 1 / Level 2 / Level 3 policy and aliases (the real stages) ================================================

    private static IReadOnlyList<CandidatePolicyDecision> Decide(
        string raw, IReadOnlyList<AmbiguousExceptionValue>? exceptions = null, IReadOnlyList<TrustedPublicValue>? trusted = null) =>
        CandidatePolicyEvaluator.Evaluate(
            ExceptionTrustedEvaluator.Evaluate(Pipe(raw), exceptions ?? [], trusted ?? []));

    private static CandidateDisposition Only(IReadOnlyList<CandidatePolicyDecision> decisions) => Assert.Single(decisions).Disposition;

    [Theory]
    [InlineData("Phone: 212-555-1212")]
    [InlineData("212-555-1212")]
    [InlineData("Address: 123 Main ST")]
    [InlineData("123 Main ST, New York, NY 10001")]
    [InlineData("Full Name: Alice O'Neil")]
    public void Level2_IsProtect_WhenNothingTrustsIt(string raw) => Assert.Equal(CandidateDisposition.Protect, Only(Decide(raw)));

    [Theory]
    [InlineData("Name: Alice O'Neil")]
    [InlineData("Dr. Alice O'Neil")]
    [InlineData("Mr Alice Doe")]
    public void Level1_IsNeedsDecision_WhenNothingTrustsIt(string raw) => Assert.Equal(CandidateDisposition.NeedsDecision, Only(Decide(raw)));

    [Theory]
    [InlineData("Phone: 212-555-1212", PiiType.Phone)]
    [InlineData("Address: 123 Main ST", PiiType.Address)]
    [InlineData("Full Name: Alice O'Neil", PiiType.Name)]
    public void Level2_AnExactTrustedPublicValue_Bypasses_AndAnAmbiguousExceptionIsIgnored(string raw, PiiType type)
    {
        var canonical = Assert.Single(Pipe(raw).Candidates).Canonical!.Value;

        Assert.Equal(CandidateDisposition.Bypass, Only(Decide(raw, trusted: [new TrustedPublicValue(type, canonical)])));
        Assert.Equal(CandidateDisposition.Protect, Only(Decide(raw, exceptions: [new AmbiguousExceptionValue(type, canonical)])));
    }

    [Theory]
    [InlineData("Name: Alice O'Neil")]
    [InlineData("Dr. Alice O'Neil")]
    public void Level1_AnExactAmbiguousException_Bypasses_AndATrustedPublicValueIsIgnored(string raw)
    {
        var canonical = Assert.Single(Pipe(raw).Candidates).Canonical!.Value;

        Assert.Equal(CandidateDisposition.Bypass, Only(Decide(raw, exceptions: [new AmbiguousExceptionValue(PiiType.Name, canonical)])));
        Assert.Equal(CandidateDisposition.NeedsDecision, Only(Decide(raw, trusted: [new TrustedPublicValue(PiiType.Name, canonical)])));
        Assert.Equal(CandidateDisposition.NeedsDecision, Only(Decide(raw, exceptions: [new AmbiguousExceptionValue(PiiType.Name, new CanonicalValue(PiiType.Name, "Someone Else"))])));
    }

    [Fact]
    public void AnExactValueOfAnotherType_NeverTrusts_ThePhoneOrTheName()
    {
        var phone = Assert.Single(Pipe("Phone: 212-555-1212").Candidates).Canonical!.Value;

        Assert.Equal(CandidateDisposition.Protect, Only(Decide("Phone: 212-555-1212", trusted: [new TrustedPublicValue(PiiType.Name, new CanonicalValue(PiiType.Name, phone.Value))])));
    }

    [Fact]
    public void Level3_StaysNeedsDecision_NoMatterWhatIsStored() // the earlier ten are not weakened
    {
        var ssn = Assert.Single(Pipe("SSN: 321-54-9876").Candidates).Canonical!.Value;

        Assert.Equal(CandidateDisposition.NeedsDecision, Only(Decide("SSN: 321-54-9876",
            exceptions: [new AmbiguousExceptionValue(PiiType.UsSsn, ssn)], trusted: [new TrustedPublicValue(PiiType.UsSsn, ssn)])));
    }

    [Fact]
    public void EveryNewCandidate_CarriesAPresentCanonicalValue_SoItCanBeAliased()
    {
        var found = Pipe(ThirteenText).Candidates;

        Assert.All(found, c => Assert.NotNull(c.Canonical));
    }

    [Fact]
    public void TheAliasMap_NumbersEqualValuesTogether_AndDifferentValuesSeparately_ForThePhoneAddressAndName()
    {
        var map = new AliasMap();
        const string raw =
            "Phone: 212-555-1212; Tel: (212) 555-1212; Mobile: 340.555.1212\n" +
            "Address: 123 Main ST\n123 Main ST, New York, NY 10001\nAddress: 123 Main ST\n" +
            "Full Name: Alice Doe; Surname: Alice Doe; Given Name: Bob";

        var tokens = Pipe(raw).Candidates.OrderBy(c => c.Span.Start).Select(c => map.GetOrAdd(c.Canonical!.Value).Value).ToList();

        Assert.Equal(
            ["[전화번호1]", "[전화번호1]", "[전화번호2]", "[주소1]", "[주소2]", "[주소1]", "[이름1]", "[이름1]", "[이름2]"],
            tokens);
    }

    [Theory]
    [InlineData(PiiType.Phone, "[전화번호1]")]
    [InlineData(PiiType.Address, "[주소1]")]
    [InlineData(PiiType.Name, "[이름1]")]
    public void AliasTokens_ForTheNewTypes_AreTheExistingFamilies(PiiType type, string token)
    {
        Assert.Equal(token, new AliasIdentity(AliasFamilyTable.FamilyOf(type), 1).CanonicalContractToken);
        Assert.Equal(token, new AliasMap().GetOrAdd(new CanonicalValue(type, "x")).Value);
    }

    // ===== unresolved FieldBinder cases: behaviour characterised, not normalised ============================================

    [Fact]
    public void Glued_AnExplicitSeparator_EndsAPhoneNameOrAddressField_LikeTheOtherDetectors()
    {
        var phone = Assert.Single(Pipe("Phone: 212-555-1212Name: Jo").Candidates, c => c.PiiType == PiiType.Phone);
        var name = Assert.Single(Pipe("Full Name: AliceTel: 5").Candidates, c => c.PiiType == PiiType.Name);
        var address = Assert.Single(Pipe("Address: 123 Main STPhone: 5").Candidates, c => c.PiiType == PiiType.Address);

        Assert.Equal(new RawSpan(7, 12), phone.Span);
        Assert.Equal(new RawSpan(11, 5), name.Span);
        Assert.Equal(new RawSpan(9, 11), address.Span);
    }

    [Fact]
    public void Glued_WithoutASeparator_TheLabelStaysInsideTheValue_UnresolvedAmbiguityPreserved()
    {
        Assert.DoesNotContain(Pipe("Phone: 212-555-1212Name Jo").Candidates, c => c.PiiType == PiiType.Phone && c.Span.Length == 12);
        Assert.DoesNotContain(Pipe("Full Name: AliceTel 5").Candidates, c => c.PiiType == PiiType.Name);
    }

    [Fact]
    public void Glued_HotelTail_InAnAddressOrNameValue_IsReadAsAGluedLabel_CharacterizationOnly()
    {
        // "Hotel:" ends the field at "tel:" (the unresolved ambiguity); the front part is judged by the full grammar.
        var name = Assert.Single(Pipe("Full Name: AliceHotel: x").Candidates, c => c.PiiType == PiiType.Name);

        Assert.Equal(new RawSpan(11, 7), name.Span);
    }

    // ===== bounded work =====================================================================================================

    [Fact]
    public void AMixedLongInput_IsBounded()
    {
        string line = "Full Name: Alice O'Neil; Phone: 212-555-1212; Address: 123 Main ST; 123 Main ST, New York, NY 10001; Mr Alice Doe";
        string many = string.Join("\n", Enumerable.Repeat(line, 1_500));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var found = Pipe(many).Candidates;

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
        Assert.True(found.Count >= 1_500 * 5);
    }
}
