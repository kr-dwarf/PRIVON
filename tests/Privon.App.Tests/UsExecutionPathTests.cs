using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Family;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.UsSsn;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The five U.S. identifiers run through the REAL downstream product path:
//   ClipboardPrivacyProcessor (trust -> CandidatePolicyEvaluator -> CategoryPolicyEvaluator -> UserExceptionPolicyEvaluator
//   -> AliasAssigner -> AliasReplacer) and ClipboardDecisionActionResolver (the user's decision, then the final write).
// The detection stage is the product's REAL DetectionPipeline, built through its explicit opt-in constructor (registry +
// family arbiter provider + family sources). ISOLATED CANDIDATE: nothing here registers or activates a U.S. detector.
public class UsExecutionPathTests
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
        new(pipeline ?? UsOptInPipeline.Create(),
            trust ?? new FakeTrustExceptionProvider(), categories, userExceptions);

    private static DetectionPipeline PipelineWithLegacy() => UsOptInPipeline.Create(Legacy());

    private static DetectionPipeline PipelineWithoutArbiter() =>
        UsOptInPipeline.Create(arbiterProvider: new EmptyDetectorFamilyArbiterProvider());

    private static IReadOnlyList<DetectionCandidate> UsDetect(string text) => UsOptInPipeline.Create().Detect(text).Candidates;

    private static ClipboardPrivacyProcessor DefaultProcessor() => new(new FakeTrustExceptionProvider());

    public static TheoryData<string, PiiType> UsTexts => new()
    {
        { "SSN: 321-54-9876", PiiType.UsSsn },
        { "ITIN: 912-70-1234", PiiType.UsItin },
        { "ATIN: 912-93-4567", PiiType.UsAtin },
        { "MBI: 1A23C45DE67", PiiType.UsMbi },
        { "N1234567890", PiiType.UsSevis },
    };

    // ---- 1. what NeedsDecision means -----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(UsTexts))]
    public void Level3_NeedsDecision_IsAnInterimPendingDecision_NotAProtectionFailure(string text, PiiType type)
    {
        var outcome = UsProcessor().Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.CandidateCount);
        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.ProtectCount);
        Assert.Equal(0, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan); // NEEDSDECISION_BLOCKS_REPLACEMENT: the raw text is not released as protected
        var item = Assert.Single(outcome.DecisionPlan!.Items);
        Assert.Equal(RiskLevel.Level3, item.RiskLevel);
        Assert.Equal(type, item.Canonical.PiiType);
    }

    [Fact]
    public void TheExistingKoreanLevel3_HasTheIdenticalShape_SoTheUsPathAddsNoNewSemantics()
    {
        var korean = DefaultProcessor().Process(ChatGpt, Snapshot("901231-1234567"));
        var us = UsProcessor().Process(ChatGpt, Snapshot("SSN: 321-54-9876"));

        Assert.Equal(korean.Result, us.Result with { CandidateCount = korean.Result.CandidateCount });
        Assert.Null(korean.WritePlan);
        Assert.Null(us.WritePlan);
        Assert.Equal(RiskLevel.Level3, Assert.Single(korean.DecisionPlan!.Items).RiskLevel);
        Assert.Equal(RiskLevel.Level3, Assert.Single(us.DecisionPlan!.Items).RiskLevel);
    }

    [Fact]
    public void WhileADecisionIsPending_NoAliasExists_ForAnyLevel3_UsOrKorean_ButALevel2Protect_GetsOne()
    {
        var (_, us) = UsProcessor().ProcessWithAssignments(ChatGpt, Snapshot("SSN: 321-54-9876"));
        var (_, kr) = DefaultProcessor().ProcessWithAssignments(ChatGpt, Snapshot("901231-1234567"));
        var (_, phone) = DefaultProcessor().ProcessWithAssignments(ChatGpt, Snapshot("연락처 010-1234-5678"));

        Assert.Null(Assert.Single(us!).Alias);
        Assert.Null(Assert.Single(kr!).Alias);
        Assert.NotNull(Assert.Single(phone!).Alias); // only a legitimately protected candidate is aliased
    }

    // ---- 2. the decision, then the protected render (the D3A expectation) ---------------------------------------

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

    private static ClipboardDecisionItem Level3ItemOf(string text)
    {
        var candidate = UsDetect(text).Single(c => c.RiskLevel == RiskLevel.Level3);
        return new(CanonicalAssert.Present(candidate), RiskLevel.Level3, candidate.DetectorId);
    }

    // Frozen D3A render expectations (read-only corpus): the protected text after the user's Protect decision.
    [Theory]
    [InlineData("SSN: 321-54-9876", "SSN: [SSN1]")]                                         // US-SSN-P01
    [InlineData("321-54-9876", "[SSN1]")]                                                   // US-SSN-X01
    [InlineData("SSN: 321549876", "SSN: [SSN1]")]                                           // US-SSN-X02
    [InlineData("Social Security Number: 321 54 9876", "Social Security Number: [SSN1]")]   // US-SSN-X03
    [InlineData("ITIN: 912-70-1234", "ITIN: [ITIN1]")]                                      // US-ITIN-P01
    [InlineData("ATIN: 912-93-4567", "ATIN: [ATIN1]")]                                      // US-TAX-MX01
    [InlineData("912-93-4567", "[ATIN1]")]                                                  // US-TAX-MX02
    [InlineData("MBI: 1A23C45DE67", "MBI: [MBI1]")]                                         // US-MBI-P01
    [InlineData("MBI: 1A23-C45-DE67", "MBI: [MBI1]")]                                       // US-MBI-G01
    [InlineData("MBI: 1a23c45de67", "MBI: [MBI1]")]                                         // US-MBI-LOWER
    [InlineData("N1234567890", "[SEVIS1]")]                                                 // US-SEVIS-P01
    public async Task AfterTheUsersProtectDecision_TheRealResolverWritesTheFrozenD3ARender(string text, string expectedRender)
    {
        var (resolver, read, write) = CreateResolver(UsProcessor());
        var item = Level3ItemOf(text);
        var scope = CreateScope(text, item);
        read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(text));
        write.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(expectedRender, Assert.Single(write.ReceivedReplacementTexts));
    }

    [Fact]
    public async Task TheKoreanControl_TakesTheSamePath_AndGetsItsOwnAlias()
    {
        var (resolver, read, write) = CreateResolver(DefaultProcessor());
        var koreanCandidate = DetectionPipeline.CreateDefault().Detect("901231-1234567").Candidates.Single();
        var item = new ClipboardDecisionItem(CanonicalAssert.Present(koreanCandidate), RiskLevel.Level3, koreanCandidate.DetectorId);
        var scope = CreateScope("901231-1234567", item);
        read.NextReadResult = ClipboardTextReadResult.Success(Snapshot("901231-1234567"));
        write.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal("[주민등록번호1]", Assert.Single(write.ReceivedReplacementTexts));
    }

    [Fact]
    public async Task EqualValuesShareOneAlias_DifferentValuesGetTheNextNumber_InTextOrder()
    {
        const string text = "SSN: 321-54-9876 and 123-45-6789 and 321-54-9876";
        var (resolver, read, write) = CreateResolver(UsProcessor());
        var flowItems = UsDetect(text)
            .Select(c => new ClipboardDecisionItem(CanonicalAssert.Present(c), RiskLevel.Level3, c.DetectorId)).Distinct().ToArray();
        var scope = CreateScope(text, flowItems);
        write.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        foreach (var item in flowItems)
        {
            read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(text));
            await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
        }

        Assert.Equal(2, flowItems.Length);
        Assert.Equal("SSN: [SSN1] and [SSN2] and [SSN1]", Assert.Single(write.ReceivedReplacementTexts));
    }

    [Fact]
    public async Task ALevel3BypassOnce_NeedsASecondConfirmation_ForUsExactlyAsForKorean_AndWritesNothing()
    {
        var (resolver, read, write) = CreateResolver(UsProcessor());
        var item = Level3ItemOf("SSN: 321-54-9876");
        var scope = CreateScope("SSN: 321-54-9876", item);
        read.NextReadResult = ClipboardTextReadResult.Success(Snapshot("SSN: 321-54-9876"));

        var first = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, first.Outcome);
        Assert.Equal(0, write.CallCount);
    }

    // ---- 3. nothing lowers a U.S. Level 3 ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(UsTexts))]
    public void EveryCategoryOff_DoesNotLowerAUsLevel3(string text, PiiType type)
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
    [MemberData(nameof(UsTexts))]
    public void TrustedAndExceptionStores_DoNotLowerAUsLevel3(string text, PiiType type)
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
    [MemberData(nameof(UsTexts))]
    public void AUserException_DoesNotLowerAUsLevel3(string text, PiiType type)
    {
        var canonical = CanonicalAssert.Present(UsDetect(text).Single());
        var userExceptions = new FakeUserExceptionProvider([new UserExceptionValue(type, canonical)]);

        var outcome = UsProcessor(userExceptions: userExceptions).Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan);
    }

    // ---- 4. mixed content and Korean / Global regression ---------------------------------------------------------

    [Fact]
    public void MixedKoreanGlobalAndUsContent_KeepsEachPathsOwnPolicy()
    {
        var processor = UsProcessor(PipelineWithLegacy());

        var outcome = processor.Process(ChatGpt, Snapshot("mail user@example.test, SSN: 321-54-9876, 901231-1234567"));

        Assert.Equal(3, outcome.Result.CandidateCount);
        Assert.Equal(1, outcome.Result.ProtectCount);        // the email
        Assert.Equal(2, outcome.Result.NeedsDecisionCount);  // the SSN and the Korean RRN
        Assert.Null(outcome.WritePlan);                      // a pending decision blocks the whole replacement
        Assert.Equal(2, outcome.DecisionPlan!.Items.Count);
    }

    [Theory]
    [InlineData("연락처 010-1234-5678")]
    [InlineData("이메일 user@example.test")]
    [InlineData("연락처 010-1234-5678, 이메일 user@example.test")]
    [InlineData("901231-1234567")]
    [InlineData("901231-1234567 연락처 010-1234-5678")]
    [InlineData("카드 4111 1111 1111 1111")]
    [InlineData("서버 192.168.10.20 에 접속")]
    [InlineData("37.5665,126.9780")]
    [InlineData("오늘 날씨가 좋네요")]
    [InlineData("")]
    public void ForTextWithNoUsContent_TheAssembledFlowProducesTheSameOutcomeAsTheShippedPipeline(string text)
    {
        var shipped = DefaultProcessor().Process(ChatGpt, Snapshot(text));
        var assembled = UsProcessor(PipelineWithLegacy()).Process(ChatGpt, Snapshot(text));

        Assert.Equal(shipped.Result, assembled.Result);
        Assert.Equal(shipped.WritePlan?.ReplacementText, assembled.WritePlan?.ReplacementText);
        Assert.Equal(shipped.DecisionPlan?.Items.Count, assembled.DecisionPlan?.Items.Count);
    }

    [Fact]
    public void TheShippedDefaultProcessor_StillDetectsNoUsIdentifier_NothingWasActivated()
    {
        foreach (string text in new[] { "SSN: 321-54-9876", "ITIN: 912-70-1234", "ATIN: 912-93-4567", "MBI: 1A23C45DE67", "N1234567890" })
        {
            var outcome = DefaultProcessor().Process(ChatGpt, Snapshot(text));

            Assert.Equal(0, outcome.Result.CandidateCount);
            Assert.Null(outcome.WritePlan);
            Assert.Null(outcome.DecisionPlan);
        }
    }

    [Fact]
    public void TheDetectionAssemblyReferencesNoUsImplementation_AndTheShippedAppReferencesOnlyTheProductAssembly()
    {
        // Detection (the pipeline and the frozen scope contract) knows no country implementation. The shipped app reaches the U.S. pack only through the ONE product
        // assembly (Privon.UsProductIntegration, in src) -- never an experiment assembly, never the two core assemblies directly -- and only the gate there can serve it.
        var detection = typeof(DetectionPipeline).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(detection, n => n.StartsWith("Privon.Us", StringComparison.Ordinal));

        var app = typeof(ClipboardPrivacyProcessor).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.Contains("Privon.UsProductIntegration", app);
        Assert.DoesNotContain(app, n => n is "Privon.UsSsn" or "Privon.UsMbiSevis");
        Assert.DoesNotContain(app, n => n.Contains("Experiment", StringComparison.Ordinal) || n.Contains("UsOptInHost", StringComparison.Ordinal));
    }

    // ---- 4b. per-country execution scope, through the real processor -------------------------------------------------

    private static ClipboardPrivacyProcessor ScopedProcessor(CountrySettingObservation setting) =>
        UsProcessor(UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting));

    [Fact]
    public void UnitedStatesOff_ProducesAnEmptyCompleteOutcome_ForUsText()
    {
        var outcome = ScopedProcessor(CountrySettingObservation.AuthenticatedOff).Process(ChatGpt, Snapshot("SSN: 321-54-9876 MBI: 1A23C45DE67"));

        Assert.Equal(0, outcome.Result.CandidateCount);
        Assert.Null(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);
    }

    [Theory]
    [InlineData("연락처 010-1234-5678 SSN: 321-54-9876 이메일 user@example.test")]
    [InlineData("901231-1234567 SSN: 321-54-9876 MBI: 1A23C45DE67")]
    [InlineData("ATIN: 912-88-4321 연락처 010-1234-5678")]
    [InlineData("SSN: 321-54-9876")]
    public void UnitedStatesOff_GivesTheSameOutcomeAsTheShippedProcessor_WhateverTheUsContent(string text)
    {
        var shipped = DefaultProcessor().Process(ChatGpt, Snapshot(text));
        var off = ScopedProcessor(CountrySettingObservation.AuthenticatedOff).Process(ChatGpt, Snapshot(text));

        Assert.Equal(shipped.Result, off.Result);
        Assert.Equal(shipped.WritePlan?.ReplacementText, off.WritePlan?.ReplacementText);
        Assert.Equal(shipped.DecisionPlan?.Items.Count, off.DecisionPlan?.Items.Count);
    }

    [Fact]
    public void UnitedStatesOff_KeepsTheKoreanLevel3Decision_AndTheLevel2Protection()
    {
        var outcome = ScopedProcessor(CountrySettingObservation.AuthenticatedOff)
            .Process(ChatGpt, Snapshot("901231-1234567 연락처 010-1234-5678 SSN: 321-54-9876"));

        Assert.Equal(2, outcome.Result.CandidateCount);
        Assert.Equal(1, outcome.Result.NeedsDecisionCount); // the Korean RRN
        Assert.Equal(1, outcome.Result.ProtectCount);       // the phone
        Assert.Equal(PiiType.ResidentRegistrationNumber, Assert.Single(outcome.DecisionPlan!.Items).Canonical.PiiType);
    }

    [Theory]
    [InlineData(CountrySettingObservation.AuthenticatedOn)]
    [InlineData(CountrySettingObservation.Missing)]
    [InlineData(CountrySettingObservation.Corrupt)]
    [InlineData(CountrySettingObservation.Unauthenticated)]
    [InlineData(CountrySettingObservation.UnknownVersion)]
    public void WheneverTheCountryIsNotAuthenticatedOff_TheUsLevel3StaysNeedsDecision(CountrySettingObservation setting)
    {
        var outcome = ScopedProcessor(setting).Process(ChatGpt, Snapshot("SSN: 321-54-9876"));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Null(outcome.WritePlan);
        Assert.Equal(PiiType.UsSsn, Assert.Single(outcome.DecisionPlan!.Items).Canonical.PiiType);
    }

    [Fact]
    public async Task AfterTheUsersProtectDecision_AnUncertainCountrySettingStillProducesTheFrozenRender()
    {
        var (resolver, read, write) = CreateResolver(ScopedProcessor(CountrySettingObservation.Corrupt));
        var item = Level3ItemOf("SSN: 321-54-9876");
        var scope = CreateScope("SSN: 321-54-9876", item);
        read.NextReadResult = ClipboardTextReadResult.Success(Snapshot("SSN: 321-54-9876"));
        write.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal("SSN: [SSN1]", Assert.Single(write.ReceivedReplacementTexts));
    }

    // ---- 5. failure propagation ------------------------------------------------------------------------------------

    [Fact]
    public void AnUnavailableFamilyArbiter_SurfacesAsAFailureOfTheWholeProcessing_NeverAsAZeroCandidateOutcome()
    {
        var processor = UsProcessor(PipelineWithoutArbiter());

        Assert.Throws<FamilyArbitrationUnavailableException>(() => processor.Process(ChatGpt, Snapshot("SSN: 321-54-9876")));
        Assert.Throws<FamilyArbitrationUnavailableException>(() => processor.Process(ChatGpt, Snapshot("no identifiers here")));
    }

    [Fact]
    public async Task AnUnavailableFamilyArbiter_AlsoFailsTheDecisionResolver_WithoutWritingAnything()
    {
        var (resolver, read, write) = CreateResolver(UsProcessor(PipelineWithoutArbiter()));
        var item = Level3ItemOf("SSN: 321-54-9876");
        var scope = CreateScope("SSN: 321-54-9876", item);
        read.NextReadResult = ClipboardTextReadResult.Success(Snapshot("SSN: 321-54-9876"));

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.NotEqual(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.Equal(0, write.CallCount);
        Assert.False(result.ClipboardMutated);
    }

    // ---- 6. what the current product interfaces can and cannot say about country packs ---------------------------

    [Fact]
    public void CountryPackOnOff_HasNoRepresentationInTheCurrentProductSettings_SoItIsNotProvenHere()
    {
        var names = typeof(ProtectionCategorySettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.DeclaringType == typeof(ProtectionCategorySettings) && p.Name != "EqualityContract")
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        // Five category switches and nothing country-shaped: a pack OFF/ON behaviour cannot be exercised through them.
        Assert.Equal(["AddressEnabled", "CompanyEnabled", "EmailEnabled", "NameEnabled", "PhoneEnabled"], names);
    }
}
