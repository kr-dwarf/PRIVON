using System.Text;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Authority;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. us.phone, us.address and us.name (Level 2 / Level 1) through the REAL downstream product path:
//   ClipboardPrivacyProcessor (trust -> CandidatePolicyEvaluator -> CategoryPolicyEvaluator -> UserExceptionPolicyEvaluator -> AliasAssigner
//   -> AliasReplacer) and ClipboardDecisionActionResolver. Phone and address run against the four-entry D3A authority fixtures served through
//   the production snapshot loader; the product itself ships no such data. Nothing writes the real clipboard.
public sealed class UsPhoneAddressNameExecutionPathTests
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    internal sealed class TestAuthorityProvider : IAuthoritySnapshotProvider
    {
        private readonly Dictionary<string, (byte[] Bytes, AuthoritySnapshotMetadata Metadata)> _items = new(StringComparer.Ordinal);

        public static TestAuthorityProvider Valid(bool withoutNpa = false)
        {
            var p = new TestAuthorityProvider();
            if (!withoutNpa) p.Add(UsAuthoritySet.NpaId, "212\n340\n787\n939\n", "https://reports.nanpa.com/public/npa_report.csv");
            p.Add(UsAuthoritySet.SuffixId, "AVE\nBLVD\nRD\nST\n", "https://pe.usps.com/text/pub28/28apc_002.htm");
            p.Add(UsAuthoritySet.StateId, "CA\nNY\nPR\nVI\n", "https://pe.usps.com/text/pub28/pub28apb.htm");
            return p;
        }

        private void Add(AuthoritySnapshotId id, string text, string url)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            var metadata = new AuthoritySnapshotMetadata(
                id, "1", url, null, new DateOnly(2026, 9, 15), 4, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
            _items[id.Value] = (bytes, metadata);
        }

        public bool TryGetBytes(AuthoritySnapshotId id, out ReadOnlyMemory<byte> bytes)
        {
            bool found = _items.TryGetValue(id.Value, out var item);
            bytes = found ? item.Bytes : default;
            return found;
        }

        public bool TryGetMetadata(AuthoritySnapshotId id, out AuthoritySnapshotMetadata metadata)
        {
            bool found = _items.TryGetValue(id.Value, out var item);
            metadata = found ? item.Metadata : null!;
            return found;
        }
    }

    private static readonly UsAuthoritySet Authority = UsAuthoritySet.Load(TestAuthorityProvider.Valid());

    private static ClipboardTextSnapshot Snapshot(string text, uint sequence = 1) =>
        new(SequenceNumber: sequence, HasReliableSequence: true, Text: text);

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static ClipboardPrivacyProcessor UsProcessor(
        DetectionPipeline? pipeline = null,
        ITrustExceptionProvider? trust = null,
        IProtectionCategorySettingsProvider? categories = null,
        IUserExceptionProvider? userExceptions = null) =>
        new(pipeline ?? UsOptInPipeline.Create(authority: Authority), trust ?? new FakeTrustExceptionProvider(), categories, userExceptions);

    private static ClipboardPrivacyProcessor DefaultProcessor() => new(new FakeTrustExceptionProvider());

    private static IReadOnlyList<DetectionCandidate> UsDetect(string text) => UsOptInPipeline.Create(authority: Authority).Detect(text).Candidates;

    private static ProtectionCategorySettings Off(bool name = true, bool phone = true, bool address = true) =>
        new(NameEnabled: name, PhoneEnabled: phone, EmailEnabled: true, AddressEnabled: address, CompanyEnabled: true);

    // ---- Level 2: the processor protects and renders the alias by itself --------------------------------------------------

    [Theory]
    [InlineData("Phone: 212-555-1212", "Phone: [전화번호1]")]                              // US-PHONE-LBL01
    [InlineData("212-555-1212", "[전화번호1]")]                                           // US-PHONE-P01
    [InlineData("+12125551212", "[전화번호1]")]                                           // US-PHONE-P02
    [InlineData("Phone: 212-555-1212 ext 42", "Phone: [전화번호1]")]                       // US-PHONE-P03
    [InlineData("Phone: 212-555-1212 ex 42", "Phone: [전화번호1] ex 42")]                  // US-PHONE-EXT-N01
    [InlineData("Phone: 212-555-1212 ext. 42", "Phone: [전화번호1] ext. 42")]              // US-PHONE-EXT03
    [InlineData("+1 (212) 555-1212", "[전화번호1]")]                                      // US-PHONE-CC-PAREN
    [InlineData("Cell Phone Number: 2125551212", "Cell Phone Number: [전화번호1]")]        // US-PHONE-LBL10
    [InlineData("Address: 123 Main ST", "Address: [주소1]")]                              // US-ADDR-P01
    [InlineData("Home Address: 123 Main ST", "Home Address: [주소1]")]                    // US-ADDR-LBL-HOME
    [InlineData("Address: 123 Main ST; memo", "Address: [주소1]; memo")]                  // US-ADDR-B01
    [InlineData("Address: 123 Main ST APT 4B", "Address: [주소1]")]                       // US-ADDR-UNIT-APT
    [InlineData("Mailing Address: PO Box 123", "Mailing Address: [주소1]")]               // US-ADDR-P03
    [InlineData("Address: RR 2 Box 15", "Address: [주소1]")]                              // US-ADDR-RR
    [InlineData("123 Main ST, New York, NY 10001", "[주소1]")]                            // US-ADDR-P02
    [InlineData("123 Main ST, New York, NY 10001.", "[주소1].")]                          // US-ADDR-TRAILPUNCT
    [InlineData("123 Main ST, New York, NY 10001-1234", "[주소1]")]                       // US-ADDR-ZIP4
    [InlineData("Full Name: Alice O'Neil", "Full Name: [이름1]")]                         // US-NAME-P01
    [InlineData("Full Name: Alice Beth Carol Doe", "Full Name: [이름1]")]                 // US-NAME-FOUR
    [InlineData("Full Name: J. Doe", "Full Name: [이름1]")]                               // US-NAME-INITIAL
    [InlineData("Family Name: O'Neil", "Family Name: [이름1]")]                           // US-NAME-LBL-FAMILY
    [InlineData("Surname: O'Neil", "Surname: [이름1]")]                                   // US-NAME-LBL-SURNAME
    public void ALevel2Candidate_IsProtected_AndTheProcessorWritesTheFrozenD3ARender(string text, string expectedRender)
    {
        var outcome = UsProcessor().Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.CandidateCount);
        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.Equal(0, outcome.Result.NeedsDecisionCount);
        Assert.Null(outcome.DecisionPlan);
        Assert.Equal(expectedRender, outcome.WritePlan!.ReplacementText);
    }

    [Fact]
    public void AKoreanAndAUsPhone_AreRenderedWithConsecutiveNumbers() // REG-KR-US-PHONE-001
    {
        var processor = UsProcessor(UsOptInPipeline.Create(Legacy(), authority: Authority));

        var outcome = processor.Process(ChatGpt, Snapshot("KR: 010-2345-6789 US: 212-555-1212"));

        Assert.Equal(2, outcome.Result.ProtectCount);
        Assert.Equal("KR: [전화번호1] US: [전화번호2]", outcome.WritePlan!.ReplacementText);
    }

    [Fact]
    public void EqualValuesShareOneAlias_DifferentValuesGetTheNextNumber_PerFamily()
    {
        var outcome = UsProcessor().Process(ChatGpt, Snapshot(
            "Phone: 212-555-1212; Tel: (212) 555-1212; Mobile: 340.555.1212\n" +
            "Address: 123 Main ST\n123 Main ST, New York, NY 10001\nAddress: 123 Main ST\n" +
            "Full Name: Alice Doe; Surname: Alice Doe; Given Name: Bob"));

        Assert.Equal(
            "Phone: [전화번호1]; Tel: [전화번호1]; Mobile: [전화번호2]\n" +
            "Address: [주소1]\n[주소2]\nAddress: [주소1]\n" +
            "Full Name: [이름1]; Surname: [이름1]; Given Name: [이름2]",
            outcome.WritePlan!.ReplacementText);
    }

    // ---- Level 1: an interim decision, then the protected render --------------------------------------------------------

    [Theory]
    [InlineData("Name: Alice O'Neil")]                       // US-NAME-P02
    [InlineData("Dr. Alice O'Neil")]                         // US-NAME-P03
    [InlineData("Name: Alice O'Neil, CPA")]                  // US-NAME-COMMA
    [InlineData("Mr Alice Doe")]                             // US-NAME-HON-01
    [InlineData("Mrs. Alice Doe")]                           // US-NAME-HON-04
    [InlineData("Miss Alice Doe")]                           // US-NAME-HON-07
    public void ALevel1Name_NeedsADecision_AndNothingIsWrittenUntilItIsMade(string text)
    {
        var outcome = UsProcessor().Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.ProtectCount);
        Assert.Null(outcome.WritePlan);
        var item = Assert.Single(outcome.DecisionPlan!.Items);
        Assert.Equal(RiskLevel.Level1, item.RiskLevel);
        Assert.Equal(PiiType.Name, item.Canonical.PiiType);
    }

    private static (ClipboardDecisionActionResolver Resolver, FakeClipboardReadTransport Read, FakeClipboardWriteTransport Write)
        CreateResolver(ClipboardPrivacyProcessor processor)
    {
        var read = new FakeClipboardReadTransport();
        var write = new FakeClipboardWriteTransport();
        var resolver = new ClipboardDecisionActionResolver(
            new ClipboardOperationGate(), new FakeClipboardDecisionScopeLifecycle(), new FakeForegroundTargetCapture(),
            read, write, processor, new FakeClipboardComposerVerificationHandoff());
        return (resolver, read, write);
    }

    private static ClipboardDecisionScope CreateScope(string sourceText, params ClipboardDecisionItem[] items)
    {
        var tracker = new RevisionTracker();
        return new ClipboardDecisionScope(tracker, tracker.Observe(sourceText), items, generation: 0);
    }

    [Theory]
    [InlineData("Name: Alice O'Neil", "Name: [이름1]")]                       // POL-L1-DECIDE / US-NAME-P02
    [InlineData("Dr. Alice O'Neil", "[이름1]")]                               // US-NAME-P03
    [InlineData("Name: Alice O'Neil, CPA", "Name: [이름1], CPA")]            // US-NAME-COMMA
    [InlineData("Mr Alice Doe", "[이름1]")]                                   // US-NAME-HON-01
    [InlineData("Miss Alice Doe", "[이름1]")]                                 // US-NAME-HON-07
    public async Task AfterTheUsersProtectDecision_ALevel1Name_IsWrittenWithTheFrozenRender(string text, string expectedRender)
    {
        var (resolver, read, write) = CreateResolver(UsProcessor());
        var nameCandidate = UsDetect(text).Single();
        var item = new ClipboardDecisionItem(CanonicalAssert.Present(nameCandidate), RiskLevel.Level1, nameCandidate.DetectorId);
        var scope = CreateScope(text, item);
        read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(text));
        write.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.Equal(expectedRender, Assert.Single(write.ReceivedReplacementTexts));
    }

    // ---- category ON / OFF (the existing Phone / Name / Address toggles) ---------------------------------------------------

    [Theory]
    [InlineData("Phone: 212-555-1212", "phone")]
    [InlineData("212-555-1212", "phone")]
    [InlineData("Address: 123 Main ST", "address")]
    [InlineData("123 Main ST, New York, NY 10001", "address")]
    [InlineData("Full Name: Alice O'Neil", "name")]     // POL-L2-CATOFF: the specific-label name is Level 2
    public void ALevel2Candidate_IsBypassed_WhenItsOwnCategoryIsOff(string text, string category)
    {
        var settings = category switch
        {
            "phone" => Off(phone: false),
            "address" => Off(address: false),
            _ => Off(name: false),
        };

        var off = UsProcessor(categories: new FakeProtectionCategorySettingsProvider(settings)).Process(ChatGpt, Snapshot(text));
        var on = UsProcessor(categories: new FakeProtectionCategorySettingsProvider(ProtectionCategorySettings.AllOn)).Process(ChatGpt, Snapshot(text));

        Assert.Equal(0, off.Result.ProtectCount);
        Assert.Equal(1, off.Result.BypassCount);
        Assert.Equal(1, on.Result.ProtectCount);
        Assert.Equal(0, on.Result.BypassCount);
    }

    [Theory]
    [InlineData("Name: Alice O'Neil")]       // POL-L1-CATOFF
    [InlineData("Dr. Alice O'Neil")]
    [InlineData("Mr Alice Doe")]
    public void ALevel1Name_IsBypassed_WhenTheNameCategoryIsOff_AndNeedsADecisionWhenItIsOn(string text)
    {
        var off = UsProcessor(categories: new FakeProtectionCategorySettingsProvider(Off(name: false))).Process(ChatGpt, Snapshot(text));
        var on = UsProcessor(categories: new FakeProtectionCategorySettingsProvider(ProtectionCategorySettings.AllOn)).Process(ChatGpt, Snapshot(text));

        Assert.Equal(0, off.Result.NeedsDecisionCount);
        Assert.Equal(1, off.Result.BypassCount);
        Assert.Null(off.DecisionPlan);
        Assert.Equal(1, on.Result.NeedsDecisionCount);
    }

    [Fact]
    public void AnOffCategory_OfAnotherFamily_ChangesNothing()
    {
        var phoneOff = new FakeProtectionCategorySettingsProvider(Off(phone: false));
        var nameOff = new FakeProtectionCategorySettingsProvider(Off(name: false));

        Assert.Equal(1, UsProcessor(categories: phoneOff).Process(ChatGpt, Snapshot("Full Name: Alice Doe")).Result.ProtectCount);
        Assert.Equal(1, UsProcessor(categories: phoneOff).Process(ChatGpt, Snapshot("Address: 123 Main ST")).Result.ProtectCount);
        Assert.Equal(1, UsProcessor(categories: nameOff).Process(ChatGpt, Snapshot("Phone: 212-555-1212")).Result.ProtectCount);
        Assert.Equal(1, UsProcessor(categories: nameOff).Process(ChatGpt, Snapshot("Address: 123 Main ST")).Result.ProtectCount);
    }

    [Theory]
    [InlineData("SSN: 321-54-9876")]
    [InlineData("Passport Number: P12345678")]
    [InlineData("Driver License Number: D123-4567 State: CA")]
    [InlineData("Bank Account: AB12CD34")]
    public void EveryCategoryOff_NeverWeakensALevel3_OfTheEarlierTen(string text)
    {
        var outcome = UsProcessor(categories: new FakeProtectionCategorySettingsProvider(Off(false, false, false))).Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.BypassCount);
    }

    [Fact]
    public void KoreanPhone_AndEmail_KeepTheirGate3ACategoryBehaviour()
    {
        var settings = new FakeProtectionCategorySettingsProvider(Off(phone: false));
        var processor = UsProcessor(UsOptInPipeline.Create(Legacy(), authority: Authority), categories: settings);

        var outcome = processor.Process(ChatGpt, Snapshot("연락처 010-1234-5678 이메일 user@example.test"));

        Assert.Equal(1, outcome.Result.BypassCount);   // the phone
        Assert.Equal(1, outcome.Result.ProtectCount);  // the email
    }

    // ---- the category evaluator itself: the Name / Address mapping is the ONLY change to the Gate 3A contract ----------------------

    private static IReadOnlyList<CandidatePolicyDecision> BaseDecisions(string text) =>
        CandidatePolicyEvaluator.Evaluate(ExceptionTrustedEvaluator.Evaluate(UsOptInPipeline.Create(authority: Authority).Detect(text), [], []));

    [Fact]
    public void TheCategoryEvaluator_DowngradesALevel1NameOrAddressOnly_WhenItsOwnCategoryIsOff()
    {
        var decisions = BaseDecisions("Name: Alice Doe");

        var off = CategoryPolicyEvaluator.Apply(decisions, Off(name: false));
        var on = CategoryPolicyEvaluator.Apply(decisions, ProtectionCategorySettings.AllOn);
        var otherOff = CategoryPolicyEvaluator.Apply(decisions, Off(phone: false, address: false));

        Assert.Equal(CandidateDisposition.Bypass, Assert.Single(off).Disposition);
        Assert.Same(decisions[0], on[0]);        // NeedsDecision stays the same reference when the category is ON
        Assert.Same(decisions[0], otherOff[0]);  // and when a different category is OFF
    }

    [Fact]
    public void TheCategoryEvaluator_NeverTouchesALevel3_OrAPhoneLevel1_WhateverTheSettings()
    {
        var level3 = BaseDecisions("SSN: 321-54-9876");
        var allOff = Off(false, false, false);

        Assert.Same(level3[0], CategoryPolicyEvaluator.Apply(level3, allOff)[0]);
        Assert.Equal(CandidateDisposition.NeedsDecision, level3[0].Disposition);
    }

    // ---- Trusted Public (Level 2) and AmbiguousExceptionStore (Level 1) ------------------------------------------------------

    private static ClipboardPrivacyProcessor WithTrust(TrustExceptionSnapshot snapshot) =>
        UsProcessor(trust: new FakeTrustExceptionProvider(snapshot));

    [Theory]
    [InlineData("Full Name: Alice O'Neil", PiiType.Name)]               // POL-L2-TRUSTED
    [InlineData("Phone: 212-555-1212", PiiType.Phone)]
    [InlineData("Address: 123 Main ST", PiiType.Address)]
    public void ALevel2_ExactTrustedPublicValue_IsLeftAsTyped_AndAnAmbiguousExceptionDoesNotApply(string text, PiiType type)
    {
        var canonical = CanonicalAssert.Present(UsDetect(text).Single());

        var trusted = WithTrust(new TrustExceptionSnapshot(trustedPublic: [new TrustedPublicValue(type, canonical)], exceptions: []))
            .Process(ChatGpt, Snapshot(text));
        var ambiguous = WithTrust(new TrustExceptionSnapshot(trustedPublic: [], exceptions: [new AmbiguousExceptionValue(type, canonical)]))
            .Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, trusted.Result.TrustedCount);   // POL-L2-TRUSTED: AllowOriginal
        Assert.Equal(0, trusted.Result.ProtectCount);
        Assert.Equal(0, ambiguous.Result.TrustedCount); // POL-L2-AMBEX-IGNORED: still protected
        Assert.Equal(1, ambiguous.Result.ProtectCount);
    }

    [Theory]
    [InlineData("Name: Alice O'Neil")]
    [InlineData("Dr. Alice O'Neil")]
    public void ALevel1_ExactAmbiguousException_IsLeftAsTyped_AndATrustedPublicValueDoesNotApply(string text) // POL-L1-AMBEX
    {
        var canonical = CanonicalAssert.Present(UsDetect(text).Single());

        var exception = WithTrust(new TrustExceptionSnapshot(trustedPublic: [], exceptions: [new AmbiguousExceptionValue(PiiType.Name, canonical)]))
            .Process(ChatGpt, Snapshot(text));
        var trusted = WithTrust(new TrustExceptionSnapshot(trustedPublic: [new TrustedPublicValue(PiiType.Name, canonical)], exceptions: []))
            .Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, exception.Result.TrustedCount);
        Assert.Equal(0, exception.Result.NeedsDecisionCount);
        Assert.Null(exception.DecisionPlan);
        Assert.Equal(0, trusted.Result.TrustedCount);
        Assert.Equal(1, trusted.Result.NeedsDecisionCount);
    }

    [Fact]
    public void AnExactValueOfAnotherName_IsNeverTrusted()
    {
        var other = new CanonicalValue(PiiType.Name, "Someone Else");

        var outcome = WithTrust(new TrustExceptionSnapshot(trustedPublic: [new TrustedPublicValue(PiiType.Name, other)], exceptions: []))
            .Process(ChatGpt, Snapshot("Full Name: Alice O'Neil"));

        Assert.Equal(0, outcome.Result.TrustedCount);
        Assert.Equal(1, outcome.Result.ProtectCount);
    }

    // ---- the thirteen together ------------------------------------------------------------------------------------------

    private const string ThirteenText =
        "Passport Number: P12345678 State: NY\nA-Number: A1234567\nI-94 Number: 123456789AB\n" +
        "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890\n" +
        "Driver License Number: D123-4567 State: CA\nRouting Number: 120000003 | Account Number: AB12CD34\n" +
        "Phone: 212-555-1212\nAddress: 123 Main ST\nFull Name: Alice O'Neil";

    [Fact]
    public void AllThirteen_AreProcessed_TenLevel3AsPendingDecisions_ThreeLevel2AsProtected()
    {
        var outcome = UsProcessor().Process(ChatGpt, Snapshot(ThirteenText));

        Assert.Equal(13, outcome.Result.CandidateCount);
        Assert.Equal(10, outcome.Result.NeedsDecisionCount);
        Assert.Equal(3, outcome.Result.ProtectCount);
        Assert.Null(outcome.WritePlan); // a pending decision blocks the whole replacement
        Assert.Equal(10, outcome.DecisionPlan!.Items.Count);
    }

    [Fact]
    public async Task AllThirteen_AreWrittenTogether_AfterTheUsersProtectDecisions()
    {
        var (resolver, read, write) = CreateResolver(UsProcessor());
        var items = UsDetect(ThirteenText).Where(c => c.RiskLevel == RiskLevel.Level3)
            .Select(c => new ClipboardDecisionItem(CanonicalAssert.Present(c), RiskLevel.Level3, c.DetectorId)).Distinct().ToArray();
        var scope = CreateScope(ThirteenText, items);
        write.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        foreach (var item in items)
        {
            read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(ThirteenText));
            Assert.Equal(ClipboardDecisionActionOutcome.Applied, (await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect)).Outcome);
        }

        Assert.Equal(
            "Passport Number: [여권번호1] State: NY\nA-Number: [A-NUMBER1]\nI-94 Number: [I-941]\n" +
            "ATIN: [ATIN1]\nITIN: [ITIN1]\nSSN: [SSN1]\nMBI: [MBI1]\nSEVIS: [SEVIS1]\n" +
            "Driver License Number: [운전면허번호1] State: CA\nRouting Number: 120000003 | Account Number: [계좌1]\n" +
            "Phone: [전화번호1]\nAddress: [주소1]\nFull Name: [이름1]",
            Assert.Single(write.ReceivedReplacementTexts));
    }

    // ---- country scope, authority, Korean / Global, the default ------------------------------------------------------------

    private static ClipboardPrivacyProcessor ScopedProcessor(CountrySettingObservation setting, UsAuthoritySet? authority = null) =>
        UsProcessor(UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting, authority: authority ?? Authority));

    [Theory]
    [InlineData("Phone: 212-555-1212")]
    [InlineData("Address: 123 Main ST")]
    [InlineData("Full Name: Alice O'Neil")]
    [InlineData("Name: Alice O'Neil")]
    public void UnitedStatesOff_ProducesAnEmptyCompleteOutcome_ForTheNewTypes(string text)
    {
        var outcome = ScopedProcessor(CountrySettingObservation.AuthenticatedOff).Process(ChatGpt, Snapshot(text));

        Assert.Equal(0, outcome.Result.CandidateCount);
        Assert.Null(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);
    }

    [Theory]
    [InlineData(CountrySettingObservation.AuthenticatedOn)]
    [InlineData(CountrySettingObservation.Missing)]
    [InlineData(CountrySettingObservation.Corrupt)]
    [InlineData(CountrySettingObservation.Unauthenticated)]
    [InlineData(CountrySettingObservation.UnknownVersion)]
    public void EveryOtherSetting_StillProcessesTheNewTypes(CountrySettingObservation setting)
    {
        Assert.Equal("Phone: [전화번호1]", ScopedProcessor(setting).Process(ChatGpt, Snapshot("Phone: 212-555-1212")).WritePlan!.ReplacementText);
        Assert.Equal("Address: [주소1]", ScopedProcessor(setting).Process(ChatGpt, Snapshot("Address: 123 Main ST")).WritePlan!.ReplacementText);
        Assert.Equal(1, ScopedProcessor(setting).Process(ChatGpt, Snapshot("Name: Alice Doe")).Result.NeedsDecisionCount);
    }

    [Theory]
    [InlineData("연락처 010-1234-5678 Phone: 212-555-1212 이메일 user@example.test")]
    [InlineData("901231-1234567 Full Name: Alice Doe Address: 123 Main ST")]
    public void KoreanAndGlobalOutcomes_AreIdenticalToTheShippedPipeline_WhenUnitedStatesIsOff(string text)
    {
        var shipped = DefaultProcessor().Process(ChatGpt, Snapshot(text));
        var off = ScopedProcessor(CountrySettingObservation.AuthenticatedOff).Process(ChatGpt, Snapshot(text));

        Assert.Equal(shipped.Result, off.Result);
        Assert.Equal(shipped.WritePlan?.ReplacementText, off.WritePlan?.ReplacementText);
        Assert.Equal(shipped.DecisionPlan?.Items.Count, off.DecisionPlan?.Items.Count);
    }

    [Fact]
    public void WithoutAnAuthority_PhoneAndAddressAreNotDetected_ButTheNameIs_AndTheScanSucceeds()
    {
        var processor = UsProcessor(UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.Missing));

        var outcome = processor.Process(ChatGpt, Snapshot("Phone: 212-555-1212 Address: 123 Main ST Full Name: Alice Doe"));

        Assert.Equal(1, outcome.Result.CandidateCount);
        Assert.Equal("Phone: 212-555-1212 Address: 123 Main ST Full Name: [이름1]", outcome.WritePlan!.ReplacementText);
    }

    [Fact]
    public void ATamperedNpaSnapshot_MakesOnlyThePhoneUnavailable()
    {
        var tampered = UsAuthoritySet.Load(TestAuthorityProvider.Valid(withoutNpa: true));

        var outcome = UsProcessor(UsOptInPipeline.Create(Legacy(), authority: tampered))
            .Process(ChatGpt, Snapshot("Email: alice@example.com Phone: 212-555-1212 Address: 123 Main ST"));

        Assert.Equal("Email: [이메일1] Phone: 212-555-1212 Address: [주소1]", outcome.WritePlan!.ReplacementText);
    }

    [Theory]
    [InlineData("us.name")]
    [InlineData("us.phone")]
    [InlineData("us.address")]
    public void AMissingRequiredDetector_FailsTheRealProcessorExplicitly_UnlessTheCountryIsAuthenticatedOff(string dropped)
    {
        IReadOnlyList<IDetector> Without() => UsIntegrationCandidates.CreateDetectors(Authority).Where(d => d.Id.Value != dropped).ToList();
        var on = UsProcessor(UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.Corrupt, usDetectorFactory: Without, authority: Authority));
        var off = UsProcessor(UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, usDetectorFactory: Without, authority: Authority));

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() => on.Process(ChatGpt, Snapshot("Phone: 212-555-1212")));
        Assert.Equal(dropped, ex.Id.Value);
        Assert.Equal(0, off.Process(ChatGpt, Snapshot("Phone: 212-555-1212")).Result.CandidateCount);
    }

    [Theory]
    [InlineData("Phone: 212-555-1212")]
    [InlineData("123 Main ST, New York, NY 10001")]
    [InlineData("Full Name: Alice O'Neil")]
    [InlineData("Name: Alice O'Neil")]
    [InlineData("Dr. Alice O'Neil")]
    public void TheShippedDefaultProcessor_StillDetectsNoNewIdentifier_NothingWasActivated(string text)
    {
        var outcome = DefaultProcessor().Process(ChatGpt, Snapshot(text));

        Assert.Equal(0, outcome.Result.CandidateCount);
        Assert.Null(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);
    }

    [Theory]
    [InlineData("연락처 010-1234-5678")]
    [InlineData("이메일 user@example.test")]
    [InlineData("901231-1234567 연락처 010-1234-5678")]
    [InlineData("카드 4111 1111 1111 1111")]
    [InlineData("오늘 날씨가 좋네요")]
    [InlineData("")]
    public void ForTextWithNoUsContent_TheThirteenDetectorFlowProducesTheSameOutcomeAsTheShippedPipeline(string text)
    {
        var shipped = DefaultProcessor().Process(ChatGpt, Snapshot(text));
        var assembled = UsProcessor(UsOptInPipeline.Create(Legacy(), authority: Authority)).Process(ChatGpt, Snapshot(text));

        Assert.Equal(shipped.Result, assembled.Result);
        Assert.Equal(shipped.WritePlan?.ReplacementText, assembled.WritePlan?.ReplacementText);
        Assert.Equal(shipped.DecisionPlan?.Items.Count, assembled.DecisionPlan?.Items.Count);
    }
}
