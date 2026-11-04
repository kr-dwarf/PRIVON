using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. us.driver_state_id and us.bank_account through the REAL downstream product path: ClipboardPrivacyProcessor
// (trust -> CandidatePolicyEvaluator -> CategoryPolicyEvaluator -> UserExceptionPolicyEvaluator -> AliasAssigner -> AliasReplacer)
// and ClipboardDecisionActionResolver (the user's decision, then the final write). Nothing writes the real clipboard.
public sealed class UsLicenseAndBankExecutionPathTests
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

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
        new(pipeline ?? UsOptInPipeline.Create(), trust ?? new FakeTrustExceptionProvider(), categories, userExceptions);

    private static ClipboardPrivacyProcessor DefaultProcessor() => new(new FakeTrustExceptionProvider());

    private static IReadOnlyList<DetectionCandidate> UsDetect(string text) => UsOptInPipeline.Create().Detect(text).Candidates;

    public static TheoryData<string, PiiType> NewTexts => new()
    {
        { "Driver License Number: D123-4567 State: CA", PiiType.UsDriverStateId },
        { "State ID: D1234567 State: CA", PiiType.UsDriverStateId },
        { "Document Type: Driver License; Customer ID Number: D1234567; State: CA", PiiType.UsDriverStateId },
        { "Bank Account Number: AB12CD34", PiiType.BankAccount },
        { "Routing Number: 120000003 | Account Number: AB12CD34", PiiType.BankAccount },
        { "Routing Number: 120000003\nAccount Number: AB12CD34", PiiType.BankAccount },
    };

    // ---- Level 3 is an interim pending decision -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NewTexts))]
    public void Level3_NeedsDecision_IsAnInterimPendingDecision_NotAProtectionFailure(string text, PiiType type)
    {
        var outcome = UsProcessor().Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.CandidateCount);
        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.ProtectCount);
        Assert.Equal(0, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan);
        var item = Assert.Single(outcome.DecisionPlan!.Items);
        Assert.Equal(RiskLevel.Level3, item.RiskLevel);
        Assert.Equal(type, item.Canonical.PiiType);
    }

    [Theory]
    [MemberData(nameof(NewTexts))]
    public void WhileADecisionIsPending_NoAliasExists(string text, PiiType type)
    {
        var (_, assignments) = UsProcessor().ProcessWithAssignments(ChatGpt, Snapshot(text));

        Assert.Null(Assert.Single(assignments!).Alias);
        _ = type;
    }

    // ---- the decision, then the protected render (the frozen D3A expectation) ----------------------------------------------

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

    private static ClipboardDecisionItem[] ItemsOf(string text) =>
        UsDetect(text).Where(c => c.RiskLevel == RiskLevel.Level3)
            .Select(c => new ClipboardDecisionItem(CanonicalAssert.Present(c), RiskLevel.Level3, c.DetectorId)).Distinct().ToArray();

    private static async Task<string> ProtectAllAsync(string text)
    {
        var (resolver, read, write) = CreateResolver(UsProcessor());
        var items = ItemsOf(text);
        var scope = CreateScope(text, items);
        write.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        foreach (var item in items)
        {
            read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(text));
            var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
            Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        }

        return Assert.Single(write.ReceivedReplacementTexts);
    }

    // Frozen D3A render expectations (read-only corpus): the protected text after the user's Protect decision.
    [Theory]
    [InlineData("Driver License Number: D123-4567 State: CA", "Driver License Number: [운전면허번호1] State: CA")]                       // US-DL-P01
    [InlineData("Driver License: D1234567 State: CA", "Driver License: [운전면허번호1] State: CA")]                                     // US-DL-LBL01
    [InlineData("Driver's License: D1234567 State: CA", "Driver's License: [운전면허번호1] State: CA")]                                 // US-DL-LBL02
    [InlineData("DL Number: D1234567 State: CA", "DL Number: [운전면허번호1] State: CA")]                                               // US-DL-LBL05
    [InlineData("DLN: D1234567 State: CA", "DLN: [운전면허번호1] State: CA")]                                                           // US-DL-LBL06
    [InlineData("State ID: D1234567 State: CA", "State ID: [운전면허번호1] State: CA")]                                                 // US-DL-LBL07
    [InlineData("State ID Number: D1234567 State: CA", "State ID Number: [운전면허번호1] State: CA")]                                   // US-DL-LBL08
    [InlineData("Document Type: Driver License; Customer ID Number: D1234567; State: CA",
                "Document Type: Driver License; Customer ID Number: [운전면허번호1]; State: CA")]                                    // US-DL-LBL09
    [InlineData("Bank Account Number: AB12CD34", "Bank Account Number: [계좌1]")]                                                      // US-BANK-P01
    [InlineData("Bank Account: AB12CD34", "Bank Account: [계좌1]")]                                                                     // LBL-BANK
    [InlineData("Checking Account Number: AB12CD34", "Checking Account Number: [계좌1]")]                                               // LBL-CHECKNO
    [InlineData("Savings Account: AB12CD34", "Savings Account: [계좌1]")]                                                               // LBL-SAVE
    [InlineData("Bank Account Number: AB12CD34EF56GH789", "Bank Account Number: [계좌1]")]                                              // MAX17
    [InlineData("Routing Number: 120000003 | Account Number: AB12CD34", "Routing Number: 120000003 | Account Number: [계좌1]")]         // P02
    [InlineData("ABA Routing Number: 120000003 | Account Number: AB12CD34", "ABA Routing Number: 120000003 | Account Number: [계좌1]")] // RT-ABA
    [InlineData("Routing Number: 120000003\nAccount Number: AB12CD34", "Routing Number: 120000003\nAccount Number: [계좌1]")]           // 2LINE
    [InlineData("Account Number: AB12CD34\nRouting Number: 120000003", "Account Number: [계좌1]\nRouting Number: 120000003")]           // 2LINE-REV
    [InlineData("Account Number: AB12CD34 | Routing Number: 120000003", "Account Number: [계좌1] | Routing Number: 120000003")]         // 1LINE-REV
    public async Task AfterTheUsersProtectDecision_TheRealResolverWritesTheFrozenD3ARender(string text, string expectedRender) =>
        Assert.Equal(expectedRender, await ProtectAllAsync(text));

    [Fact]
    public async Task TheMaximumDistancePair_AtLabelStart128_IsProtected_AndTheRoutingIsNeverAliased() // US-BANK-B128
    {
        string text = "Routing Number: 120000003" + new string(' ', 103) + "Account Number: AB12CD34";

        string render = await ProtectAllAsync(text);

        Assert.Equal("Routing Number: 120000003" + new string(' ', 103) + "Account Number: [계좌1]", render);
    }

    [Fact]
    public async Task EqualValuesShareOneAlias_DifferentValuesGetTheNextNumber_ForTheNewTypes()
    {
        const string text =
            "DLN: D1234567 State: CA\nDLN: d123-4567 State: CA\nDLN: Z7654321 State: CA\n" +
            "Bank Account: AB12CD34\nBank Account: ab12cd34\nBank Account: ZZ99YY88";

        Assert.Equal(
            "DLN: [운전면허번호1] State: CA\nDLN: [운전면허번호1] State: CA\nDLN: [운전면허번호2] State: CA\n" +
            "Bank Account: [계좌1]\nBank Account: [계좌1]\nBank Account: [계좌2]",
            await ProtectAllAsync(text));
    }

    [Fact]
    public async Task AllTenUsIdentifiers_AreProtectedTogether_WithOneAliasFamilyEach()
    {
        const string text =
            "Passport Number: P12345678 State: NY\nA-Number: A1234567\nI-94 Number: 123456789AB\n" +
            "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890\n" +
            "Driver License Number: D123-4567 State: CA\nRouting Number: 120000003 | Account Number: AB12CD34";

        Assert.Equal(
            "Passport Number: [여권번호1] State: NY\nA-Number: [A-NUMBER1]\nI-94 Number: [I-941]\n" +
            "ATIN: [ATIN1]\nITIN: [ITIN1]\nSSN: [SSN1]\nMBI: [MBI1]\nSEVIS: [SEVIS1]\n" +
            "Driver License Number: [운전면허번호1] State: CA\nRouting Number: 120000003 | Account Number: [계좌1]",
            await ProtectAllAsync(text));
    }

    [Fact]
    public async Task ALevel3BypassOnce_NeedsASecondConfirmation_ForTheNewTypes_AndWritesNothing()
    {
        foreach (string text in new[] { "Driver License: D1234567 State: CA", "Bank Account: AB12CD34" })
        {
            var (resolver, read, write) = CreateResolver(UsProcessor());
            var item = Assert.Single(ItemsOf(text));
            var scope = CreateScope(text, item);
            read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(text));

            var first = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

            Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, first.Outcome);
            Assert.Equal(0, write.CallCount);
        }
    }

    // ---- nothing lowers a U.S. Level 3 --------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NewTexts))]
    public void EveryCategoryOff_DoesNotLowerANewLevel3(string text, PiiType type)
    {
        var allOff = new ProtectionCategorySettings(
            NameEnabled: false, PhoneEnabled: false, EmailEnabled: false, AddressEnabled: false, CompanyEnabled: false);

        var outcome = UsProcessor(categories: new FakeProtectionCategorySettingsProvider(allOff)).Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan);
        Assert.Equal(type, Assert.Single(outcome.DecisionPlan!.Items).Canonical.PiiType);
    }

    [Theory]
    [MemberData(nameof(NewTexts))]
    public void TrustedAndExceptionStores_DoNotLowerANewLevel3(string text, PiiType type)
    {
        var canonical = CanonicalAssert.Present(UsDetect(text).Single());
        var snapshot = new TrustExceptionSnapshot(
            trustedPublic: [new TrustedPublicValue(type, canonical)],
            exceptions: [new AmbiguousExceptionValue(type, canonical)]);

        var outcome = UsProcessor(trust: new FakeTrustExceptionProvider(snapshot)).Process(ChatGpt, Snapshot(text));

        Assert.Equal(0, outcome.Result.TrustedCount);
        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Null(outcome.WritePlan);
    }

    [Theory]
    [MemberData(nameof(NewTexts))]
    public void AUserException_DoesNotLowerANewLevel3(string text, PiiType type)
    {
        var canonical = CanonicalAssert.Present(UsDetect(text).Single());
        var userExceptions = new FakeUserExceptionProvider([new UserExceptionValue(type, canonical)]);

        var outcome = UsProcessor(userExceptions: userExceptions).Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan);
    }

    // ---- mixed content, Korean / Global, the earlier eight ------------------------------------------------------------------

    [Fact]
    public void MixedKoreanGlobalAndNewContent_KeepsEachPathsOwnPolicy()
    {
        var processor = UsProcessor(UsOptInPipeline.Create(Legacy()));

        var outcome = processor.Process(
            ChatGpt, Snapshot("mail user@example.test; Bank Account: AB12CD34; DLN: D1234567 State: CA; 901231-1234567"));

        Assert.Equal(4, outcome.Result.CandidateCount);
        Assert.Equal(1, outcome.Result.ProtectCount);        // the email
        Assert.Equal(3, outcome.Result.NeedsDecisionCount);  // bank account, license and the Korean RRN
        Assert.Null(outcome.WritePlan);
    }

    [Theory]
    [InlineData("연락처 010-1234-5678")]
    [InlineData("이메일 user@example.test")]
    [InlineData("901231-1234567 연락처 010-1234-5678")]
    [InlineData("계좌번호 110-123-456789")]
    [InlineData("카드 4111 1111 1111 1111")]
    [InlineData("서버 192.168.10.20 에 접속")]
    [InlineData("오늘 날씨가 좋네요")]
    [InlineData("")]
    public void ForTextWithNoUsContent_TheTenDetectorFlowProducesTheSameOutcomeAsTheShippedPipeline(string text)
    {
        var shipped = DefaultProcessor().Process(ChatGpt, Snapshot(text));
        var assembled = UsProcessor(UsOptInPipeline.Create(Legacy())).Process(ChatGpt, Snapshot(text));

        Assert.Equal(shipped.Result, assembled.Result);
        Assert.Equal(shipped.WritePlan?.ReplacementText, assembled.WritePlan?.ReplacementText);
        Assert.Equal(shipped.DecisionPlan?.Items.Count, assembled.DecisionPlan?.Items.Count);
    }

    // ---- nothing is active by default ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NewTexts))]
    public void TheShippedDefaultProcessor_StillDetectsNoNewIdentifier_NothingWasActivated(string text, PiiType type)
    {
        var outcome = DefaultProcessor().Process(ChatGpt, Snapshot(text));

        Assert.Equal(0, outcome.Result.CandidateCount);
        Assert.Null(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);
        _ = type;
    }

    // ---- per-country execution scope, through the real processor -------------------------------------------------------------

    private static ClipboardPrivacyProcessor ScopedProcessor(CountrySettingObservation setting) =>
        UsProcessor(UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting));

    [Theory]
    [MemberData(nameof(NewTexts))]
    public void UnitedStatesOff_ProducesAnEmptyCompleteOutcome_ForTheNewTypes(string text, PiiType type)
    {
        var outcome = ScopedProcessor(CountrySettingObservation.AuthenticatedOff).Process(ChatGpt, Snapshot(text));

        Assert.Equal(0, outcome.Result.CandidateCount);
        Assert.Null(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);
        _ = type;
    }

    [Theory]
    [InlineData(CountrySettingObservation.AuthenticatedOn)]
    [InlineData(CountrySettingObservation.Missing)]
    [InlineData(CountrySettingObservation.Corrupt)]
    [InlineData(CountrySettingObservation.Unauthenticated)]
    [InlineData(CountrySettingObservation.UnknownVersion)]
    public void EveryOtherSetting_StillDecidesAllNewTypes(CountrySettingObservation setting)
    {
        foreach (var row in NewTexts)
        {
            string text = (string)row[0];
            var type = (PiiType)row[1];

            var outcome = ScopedProcessor(setting).Process(ChatGpt, Snapshot(text));

            Assert.Equal(1, outcome.Result.NeedsDecisionCount);
            Assert.Equal(type, Assert.Single(outcome.DecisionPlan!.Items).Canonical.PiiType);
        }
    }

    [Theory]
    [InlineData("연락처 010-1234-5678 Bank Account: AB12CD34 이메일 user@example.test")]
    [InlineData("901231-1234567 DLN: D1234567 State: CA")]
    public void KoreanAndGlobalOutcomes_AreIdenticalToTheShippedPipeline_WhenUnitedStatesIsOff(string text)
    {
        var shipped = DefaultProcessor().Process(ChatGpt, Snapshot(text));
        var off = ScopedProcessor(CountrySettingObservation.AuthenticatedOff).Process(ChatGpt, Snapshot(text));

        Assert.Equal(shipped.Result, off.Result);
        Assert.Equal(shipped.WritePlan?.ReplacementText, off.WritePlan?.ReplacementText);
        Assert.Equal(shipped.DecisionPlan?.Items.Count, off.DecisionPlan?.Items.Count);
    }

    [Theory]
    [InlineData("us.driver_state_id")]
    [InlineData("us.bank_account")]
    public void AMissingRequiredNewDetector_FailsTheRealProcessorExplicitly_UnlessTheCountryIsAuthenticatedOff(string dropped)
    {
        IReadOnlyList<IDetector> Without() => UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != dropped).ToList();
        var on = UsProcessor(UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.Corrupt, usDetectorFactory: Without));
        var off = UsProcessor(UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, usDetectorFactory: Without));

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() => on.Process(ChatGpt, Snapshot("Bank Account: AB12CD34")));
        Assert.Equal(dropped, ex.Id.Value);
        Assert.Equal(0, off.Process(ChatGpt, Snapshot("Bank Account: AB12CD34")).Result.CandidateCount);
    }
}
