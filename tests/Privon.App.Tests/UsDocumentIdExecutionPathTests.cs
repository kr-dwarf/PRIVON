using System.Reflection;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. us.passport / us.a_number / us.i94 through the REAL downstream product path:
//   ClipboardPrivacyProcessor (trust -> CandidatePolicyEvaluator -> CategoryPolicyEvaluator -> UserExceptionPolicyEvaluator
//   -> AliasAssigner -> AliasReplacer) and ClipboardDecisionActionResolver (the user's decision, then the final write).
// The detection stage is the product's REAL DetectionPipeline built through its explicit opt-in constructor. ISOLATED
// CANDIDATE: nothing here registers or activates a U.S. detector, and nothing writes the real clipboard.
public sealed class UsDocumentIdExecutionPathTests : IDisposable
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    private readonly List<PrivonAppComposition> _compositions = [];
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var c in _compositions) c.Dispose();
        foreach (var r in _roots)
        {
            try { Directory.Delete(r, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

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
        { "Passport Number: P12345678 State: NY", PiiType.UsPassport },
        { "Passport Number: P12345678", PiiType.UsPassport },
        { "State: NY P12345678", PiiType.UsPassport },
        { "Passport #: ZX81K2", PiiType.UsPassport },
        { "A-Number: A1234567", PiiType.UsANumber },
        { "I-94 Number: 123456789AB", PiiType.UsI94 },
    };

    // ---- 1. Level 3 is an interim pending decision --------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NewTexts))]
    public void Level3_NeedsDecision_IsAnInterimPendingDecision_NotAProtectionFailure(string text, PiiType type)
    {
        var outcome = UsProcessor().Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.CandidateCount);
        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.ProtectCount);
        Assert.Equal(0, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan); // NEEDSDECISION_BLOCKS_REPLACEMENT
        var item = Assert.Single(outcome.DecisionPlan!.Items);
        Assert.Equal(RiskLevel.Level3, item.RiskLevel);
        Assert.Equal(type, item.Canonical.PiiType);
    }

    [Theory]
    [MemberData(nameof(NewTexts))]
    public void WhileADecisionIsPending_NoAliasExistsForTheNewTypes(string text, PiiType type)
    {
        var (_, assignments) = UsProcessor().ProcessWithAssignments(ChatGpt, Snapshot(text));

        Assert.Null(Assert.Single(assignments!).Alias);
    }

    // ---- 2. the decision, then the protected render (the frozen D3A expectation) -----------------------------------------

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
    [InlineData("Passport Number: P12345678 State: NY", "Passport Number: [여권번호1] State: NY")]   // US-PAS-P01
    [InlineData("Passport Number: P12345678", "Passport Number: [여권번호1]")]                      // US-PAS-P02
    [InlineData("State: NY P12345678", "State: NY [여권번호1]")]                                    // US-PAS-J01
    [InlineData("Passport #: ZX81K2", "Passport #: [여권번호1]")]                                   // US-PAS-P03
    [InlineData("Passport: P12345678 State: NY", "Passport: [여권번호1] State: NY")]                // US-PAS-LBL01
    [InlineData("Passport No.: P12345678 State: NY", "Passport No.: [여권번호1] State: NY")]        // US-PAS-LBL03
    [InlineData("Passport #: P12345678 State: NY", "Passport #: [여권번호1] State: NY")]            // US-PAS-LBL04
    [InlineData("U.S. Passport: P12345678", "U.S. Passport: [여권번호1]")]                          // US-PAS-LBL05
    [InlineData("A-Number: A1234567", "A-Number: [A-NUMBER1]")]                                      // US-ANUM-P01
    [InlineData("A-Number: A 1234567", "A-Number: [A-NUMBER1]")]                                     // US-ANUM-SPACE
    [InlineData("A-Number: A-1234567", "A-Number: [A-NUMBER1]")]                                     // US-ANUM-HYPHEN
    [InlineData("I-94 Number: 123456789AB", "I-94 Number: [I-941]")]                                 // US-I94-P01
    [InlineData("I-94: 123456789AB", "I-94: [I-941]")]                                               // US-I94-LBL01
    [InlineData("I94: 123456789AB", "I94: [I-941]")]                                                 // US-I94-LBL02
    [InlineData("I94 Number: 123456789AB", "I94 Number: [I-941]")]                                   // US-I94-LBL03
    [InlineData("I-94 Admission Number: 123456789AB", "I-94 Admission Number: [I-941]")]             // US-I94-LBL04
    [InlineData("Form I-94 Number: 123456789AB", "Form I-94 Number: [I-941]")]                       // US-I94-LBL05
    public async Task AfterTheUsersProtectDecision_TheRealResolverWritesTheFrozenD3ARender(string text, string expectedRender) =>
        Assert.Equal(expectedRender, await ProtectAllAsync(text));

    // A value that merely CONTAINS the spelling of another label ("TEL", "DLN", "MBI", "SSN") is still one valid token.
    [Theory]
    [InlineData("Passport: HOTEL1234", "Passport: [여권번호1]")]
    [InlineData("Passport: XDLN1234", "Passport: [여권번호1]")]
    [InlineData("Passport: 12MBI345", "Passport: [여권번호1]")]
    [InlineData("I-94: 1234SSN5678", "I-94: [I-941]")]
    [InlineData("Passport: HOTEL1234 SSN: 321-54-9876", "Passport: [여권번호1] SSN: [SSN1]")]
    [InlineData("Passport: A12345678;SSN: 321-54-9876", "Passport: [여권번호1];SSN: [SSN1]")]
    // a valid value directly followed by the next approved label and its explicit separator
    [InlineData("Passport: A12345678Tel: 555", "Passport: [여권번호1]Tel: 555")]
    [InlineData("A-Number: A1234567Phone: 5", "A-Number: [A-NUMBER1]Phone: 5")]
    [InlineData("I-94: 123456789ABState: NY", "I-94: [I-941]State: NY")]
    [InlineData("Passport: ZX81K2State: NY", "Passport: [여권번호1]State: NY")]
    [InlineData("ATIN: 912-93-4567SSN: 321-54-9876", "ATIN: [ATIN1]SSN: [SSN1]")]
    [InlineData("SSN: 321-54-9876Name: Jo", "SSN: [SSN1]Name: Jo")]
    public async Task ATokenThatContainsALabelSpelling_IsDetected_AndProtectedWithItsOwnExactSpan(string text, string expectedRender)
    {
        var outcome = UsProcessor().Process(ChatGpt, Snapshot(text));
        Assert.True(outcome.Result.NeedsDecisionCount >= 1);

        Assert.Equal(expectedRender, await ProtectAllAsync(text));
    }

    [Fact]
    public async Task EqualValuesShareOneAlias_DifferentValuesGetTheNextNumber_InTextOrder_ForTheNewTypes()
    {
        const string text =
            "Passport: P12345678\nPassport: Q87654321\nPassport: p12345678\nA-Number: A1234567\nA-Number: 1234567\nA-Number: A7654321\n" +
            "I-94: 123456789AB\nI-94: 987654321ZY\nI-94: 123456789ab";

        string render = await ProtectAllAsync(text);

        Assert.Equal(
            "Passport: [여권번호1]\nPassport: [여권번호2]\nPassport: [여권번호1]\nA-Number: [A-NUMBER1]\nA-Number: [A-NUMBER1]\nA-Number: [A-NUMBER2]\n" +
            "I-94: [I-941]\nI-94: [I-942]\nI-94: [I-941]",
            render);
    }

    [Fact]
    public async Task AllEightUsIdentifiers_AreProtectedTogether_WithOneAliasFamilyEach()
    {
        const string text =
            "Passport Number: P12345678 State: NY\nA-Number: A1234567\nI-94 Number: 123456789AB\n" +
            "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890";

        string render = await ProtectAllAsync(text);

        Assert.Equal(
            "Passport Number: [여권번호1] State: NY\nA-Number: [A-NUMBER1]\nI-94 Number: [I-941]\n" +
            "ATIN: [ATIN1]\nITIN: [ITIN1]\nSSN: [SSN1]\nMBI: [MBI1]\nSEVIS: [SEVIS1]",
            render);
    }

    [Fact]
    public async Task ALevel3BypassOnce_NeedsASecondConfirmation_ForTheNewTypes_AndWritesNothing()
    {
        foreach (string text in new[] { "Passport Number: P12345678", "A-Number: A1234567", "I-94 Number: 123456789AB" })
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

    // ---- 3. nothing lowers a U.S. Level 3 ------------------------------------------------------------------------------

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

    // ---- 4. mixed content, Korean / Global, and the five ----------------------------------------------------------------

    [Fact]
    public void MixedKoreanGlobalAndNewContent_KeepsEachPathsOwnPolicy()
    {
        var processor = UsProcessor(UsOptInPipeline.Create(Legacy()));

        var outcome = processor.Process(
            ChatGpt, Snapshot("mail user@example.test; Passport: P12345678; A-Number: A1234567; 901231-1234567"));

        Assert.Equal(4, outcome.Result.CandidateCount);
        Assert.Equal(1, outcome.Result.ProtectCount);        // the email
        Assert.Equal(3, outcome.Result.NeedsDecisionCount);  // passport, A-Number and the Korean RRN
        Assert.Null(outcome.WritePlan);
        Assert.Equal(3, outcome.DecisionPlan!.Items.Count);
    }

    [Fact]
    public void TheFiveExistingTypes_KeepTheirExactOutcomes_NextToTheNewOnes()
    {
        foreach (var (text, type) in new[]
                 { ("SSN: 321-54-9876", PiiType.UsSsn), ("ITIN: 912-70-1234", PiiType.UsItin), ("ATIN: 912-93-4567", PiiType.UsAtin),
                   ("MBI: 1A23C45DE67", PiiType.UsMbi), ("N1234567890", PiiType.UsSevis) })
        {
            var outcome = UsProcessor().Process(ChatGpt, Snapshot(text));

            Assert.Equal(1, outcome.Result.CandidateCount);
            Assert.Equal(1, outcome.Result.NeedsDecisionCount);
            Assert.Equal(type, Assert.Single(outcome.DecisionPlan!.Items).Canonical.PiiType);
        }
    }

    [Theory]
    [InlineData("연락처 010-1234-5678")]
    [InlineData("이메일 user@example.test")]
    [InlineData("901231-1234567 연락처 010-1234-5678")]
    [InlineData("카드 4111 1111 1111 1111")]
    [InlineData("서버 192.168.10.20 에 접속")]
    [InlineData("오늘 날씨가 좋네요")]
    [InlineData("")]
    public void ForTextWithNoUsContent_TheEightDetectorFlowProducesTheSameOutcomeAsTheShippedPipeline(string text)
    {
        var shipped = DefaultProcessor().Process(ChatGpt, Snapshot(text));
        var assembled = UsProcessor(UsOptInPipeline.Create(Legacy())).Process(ChatGpt, Snapshot(text));

        Assert.Equal(shipped.Result, assembled.Result);
        Assert.Equal(shipped.WritePlan?.ReplacementText, assembled.WritePlan?.ReplacementText);
        Assert.Equal(shipped.DecisionPlan?.Items.Count, assembled.DecisionPlan?.Items.Count);
    }

    // ---- 5. nothing is active by default ---------------------------------------------------------------------------------

    [Fact]
    public void TheShippedDefaultProcessor_StillDetectsNoNewIdentifier_NothingWasActivated()
    {
        foreach (var (text, _) in NewTextsList())
        {
            var outcome = DefaultProcessor().Process(ChatGpt, Snapshot(text));

            Assert.Equal(0, outcome.Result.CandidateCount);
            Assert.Null(outcome.WritePlan);
            Assert.Null(outcome.DecisionPlan);
        }
    }

    private static IEnumerable<(string Text, PiiType Type)> NewTextsList() =>
        NewTexts.Select(row => ((string)row[0], (PiiType)row[1]));

    [Fact]
    public void TheDetectionAssemblyReferencesNoUsImplementation_AndTheShippedAppReachesThePackOnlyThroughTheProductAssembly()
    {
        // The pipeline and the frozen scope contract (Privon.Detection) know no country implementation. The shipped app references the ONE product assembly in src --
        // never an experiment assembly or the two cores directly -- and serves the pack only after an explicit activation (UsShippedOptInTests).
        Assert.DoesNotContain(typeof(DetectionPipeline).Assembly.GetReferencedAssemblies().Select(a => a.Name!), n => n.StartsWith("Privon.Us", StringComparison.Ordinal));
        var app = typeof(ClipboardPrivacyProcessor).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(app, n => n.Contains("Experiment") || n is "Privon.UsSsn" or "Privon.UsMbiSevis");
    }

    // ---- 6. per-country execution scope, through the real processor -------------------------------------------------------

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
        _ = type; // the row's type only identifies the case
    }

    [Theory]
    [InlineData(CountrySettingObservation.AuthenticatedOn)]
    [InlineData(CountrySettingObservation.Missing)]
    [InlineData(CountrySettingObservation.Corrupt)]
    [InlineData(CountrySettingObservation.Unauthenticated)]
    [InlineData(CountrySettingObservation.UnknownVersion)]
    public void EveryOtherSetting_StillDecidesAllNewTypes(CountrySettingObservation setting)
    {
        foreach (var (text, type) in NewTextsList())
        {
            var outcome = ScopedProcessor(setting).Process(ChatGpt, Snapshot(text));

            Assert.Equal(1, outcome.Result.NeedsDecisionCount);
            Assert.Equal(type, Assert.Single(outcome.DecisionPlan!.Items).Canonical.PiiType);
        }
    }

    [Theory]
    [InlineData("연락처 010-1234-5678 Passport: P12345678 이메일 user@example.test")]
    [InlineData("901231-1234567 A-Number: A1234567 I-94: 123456789AB")]
    public void KoreanAndGlobalOutcomes_AreIdenticalToTheShippedPipeline_WhenUnitedStatesIsOff(string text)
    {
        var shipped = DefaultProcessor().Process(ChatGpt, Snapshot(text));
        var off = ScopedProcessor(CountrySettingObservation.AuthenticatedOff).Process(ChatGpt, Snapshot(text));

        Assert.Equal(shipped.Result, off.Result);
        Assert.Equal(shipped.WritePlan?.ReplacementText, off.WritePlan?.ReplacementText);
        Assert.Equal(shipped.DecisionPlan?.Items.Count, off.DecisionPlan?.Items.Count);
    }

    [Theory]
    [InlineData("us.passport")]
    [InlineData("us.a_number")]
    [InlineData("us.i94")]
    public void AMissingRequiredNewDetector_FailsTheRealProcessorExplicitly_UnlessTheCountryIsAuthenticatedOff(string dropped)
    {
        IReadOnlyList<IDetector> Without() => UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != dropped).ToList();
        var on = UsProcessor(UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.Missing, usDetectorFactory: Without));
        var off = UsProcessor(UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, usDetectorFactory: Without));

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() => on.Process(ChatGpt, Snapshot("A-Number: A1234567")));
        Assert.Equal(dropped, ex.Id.Value);
        Assert.Equal(0, off.Process(ChatGpt, Snapshot("A-Number: A1234567")).Result.CandidateCount);
    }

    // ---- 7. through the real PrivonAppComposition opt-in --------------------------------------------------------------------

    private sealed class TestOnlySource(CountrySettingObservation initial) : ICountrySettingSource
    {
        private int _value = (int)initial;
        public void Set(CountrySettingObservation value) => Interlocked.Exchange(ref _value, (int)value);
        public CountrySettingObservation Read() => (CountrySettingObservation)Volatile.Read(ref _value);
    }

    private PrivonAppComposition StartedComposition(Func<DetectionPipeline>? source)
    {
        string root = Path.Combine(Path.GetTempPath(), "UsDocumentIdComposition", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var composition = new PrivonAppComposition(
            root, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(),
            new ForegroundChangeMonitor(), enableDiagnostics: false, detectionPipelineSource: source);
        _compositions.Add(composition);
        composition.Start();
        return composition;
    }

    private static ClipboardPrivacyProcessor ProcessorOf(PrivonAppComposition composition)
    {
        var field = typeof(ClipboardDecisionActionResolver).GetField("_processor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ClipboardPrivacyProcessor)field.GetValue(composition.Resolver!)!;
    }

    [Fact]
    public void TheShippedComposition_FindsNoNewIdentifier_WhileTheOptInCompositionDecidesThem_AndFollowsTheSetting()
    {
        var shipped = ProcessorOf(StartedComposition(null));
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOn);
        var host = new CountryScopedPipelineHost(source, s => UsOptInPipeline.Create(Legacy(), unitedStatesSetting: s));
        var optIn = ProcessorOf(StartedComposition(() => host.Current()));

        foreach (var (text, type) in NewTextsList())
        {
            Assert.Equal(0, shipped.Process(ChatGpt, Snapshot(text)).Result.CandidateCount);
            var outcome = optIn.Process(ChatGpt, Snapshot(text));
            Assert.Equal(type, Assert.Single(outcome.DecisionPlan!.Items).Canonical.PiiType);
        }

        source.Set(CountrySettingObservation.AuthenticatedOff);
        Assert.Equal(0, optIn.Process(ChatGpt, Snapshot("Passport Number: P12345678")).Result.CandidateCount);
        source.Set(CountrySettingObservation.Corrupt);
        Assert.Equal(1, optIn.Process(ChatGpt, Snapshot("Passport Number: P12345678")).Result.NeedsDecisionCount);
    }
}
