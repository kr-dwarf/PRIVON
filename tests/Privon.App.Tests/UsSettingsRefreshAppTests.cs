using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. T-2 through the REAL ClipboardPrivacyProcessor / ClipboardDecisionActionResolver: the processor resolves
// its pipeline at the start of every attempt. The country observations come from a test fixture (no product source can
// produce an authenticated OFF), and the real PrivonLocalStore-backed providers are used where the product uses them.
public sealed class UsSettingsRefreshAppTests : IDisposable
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    private const string UsSsn = "SSN: 321-54-9876";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "privon-app-refresh-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class TestOnlySource(CountrySettingObservation initial) : ICountrySettingSource
    {
        private int _value = (int)initial;
        public void Set(CountrySettingObservation value) => Interlocked.Exchange(ref _value, (int)value);
        public CountrySettingObservation Read() => (CountrySettingObservation)Volatile.Read(ref _value);
    }

    private static ClipboardTextSnapshot Snapshot(string text) => new(SequenceNumber: 1, HasReliableSequence: true, Text: text);

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static DetectionPipeline Build(CountrySettingObservation setting) =>
        UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting);

    private static ClipboardPrivacyProcessor Processor(
        CountryScopedPipelineHost host, IProtectionCategorySettingsProvider? categories = null,
        ITrustExceptionProvider? trust = null, IUserExceptionProvider? userExceptions = null) =>
        new(() => host.Current(), trust ?? new FakeTrustExceptionProvider(), categories, userExceptions);

    // ---- the lifecycle the product has today, and the one this adds ------------------------------------------------

    [Fact]
    public void CharacterizationOfTodaysLifecycle_AProcessorWithAFixedPipelineNeverFollowsASettingChange()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        var fixedProcessor = new ClipboardPrivacyProcessor(Build(source.Read()), new FakeTrustExceptionProvider());

        source.Set(CountrySettingObservation.AuthenticatedOn);
        var outcome = fixedProcessor.Process(ChatGpt, Snapshot(UsSsn));

        Assert.Equal(0, outcome.Result.CandidateCount); // stale: this is the defect the per-attempt source removes
    }

    [Fact]
    public void WithAPerAttemptPipelineSource_OffThenOnThenOff_IsFollowedByTheVeryNextAttempt()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        var processor = Processor(new CountryScopedPipelineHost(source, Build));

        Assert.Equal(0, processor.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);

        source.Set(CountrySettingObservation.AuthenticatedOn);
        var on = processor.Process(ChatGpt, Snapshot(UsSsn));
        Assert.Equal(1, on.Result.NeedsDecisionCount);
        Assert.Null(on.WritePlan);

        source.Set(CountrySettingObservation.AuthenticatedOff);
        Assert.Equal(0, processor.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);
    }

    [Fact]
    public void TheShippedConstructors_AreUnchanged_TheFixedPipelineStillWorksAndNoUsDetectorIsPresent()
    {
        var shipped = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());
        var fixedDefault = new ClipboardPrivacyProcessor(DetectionPipeline.CreateDefault(), new FakeTrustExceptionProvider());

        Assert.Equal(0, shipped.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);
        Assert.Equal(1, fixedDefault.Process(ChatGpt, Snapshot("901231-1234567")).Result.NeedsDecisionCount);
        Assert.Throws<ArgumentNullException>(() => new ClipboardPrivacyProcessor((DetectionPipeline)null!, new FakeTrustExceptionProvider()));
        Assert.Throws<ArgumentNullException>(() => new ClipboardPrivacyProcessor((Func<DetectionPipeline>)null!, new FakeTrustExceptionProvider()));
    }

    [Fact]
    public void ARebuildThatFailsForOn_NeverFallsBackToTheOldOffState_AndNeverReturnsAnEmptyOutcome()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        bool failOn = false;
        var host = new CountryScopedPipelineHost(source, s =>
            s == CountrySettingObservation.AuthenticatedOn && failOn ? throw new InvalidOperationException("synthetic rebuild failure") : Build(s));
        var processor = Processor(host);
        Assert.Equal(0, processor.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);

        failOn = true;
        source.Set(CountrySettingObservation.AuthenticatedOn);

        Assert.Throws<InvalidOperationException>(() => processor.Process(ChatGpt, Snapshot(UsSsn)));
        Assert.Throws<InvalidOperationException>(() => processor.Process(ChatGpt, Snapshot("nothing to find")));

        failOn = false;
        Assert.Equal(1, processor.Process(ChatGpt, Snapshot(UsSsn)).Result.NeedsDecisionCount);
    }

    [Fact]
    public void APipelineSourceThatReturnsNothing_FailsTheAttempt()
    {
        var processor = new ClipboardPrivacyProcessor(() => null!, new FakeTrustExceptionProvider());

        Assert.Throws<InvalidOperationException>(() => processor.Process(ChatGpt, Snapshot(UsSsn)));
    }

    // ---- what must not change ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("연락처 010-1234-5678 이메일 user@example.test")]
    [InlineData("901231-1234567 연락처 010-1234-5678")]
    [InlineData("카드 4111 1111 1111 1111")]
    [InlineData("오늘 날씨가 좋네요")]
    public void KoreanAndGlobalOutcomes_AreTheSameAsTheShippedProcessor_InEveryCountryState(string text)
    {
        var shipped = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider()).Process(ChatGpt, Snapshot(text));
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        var processor = Processor(new CountryScopedPipelineHost(source, Build));

        foreach (var setting in Enum.GetValues<CountrySettingObservation>())
        {
            source.Set(setting);
            var outcome = processor.Process(ChatGpt, Snapshot(text));

            Assert.Equal(shipped.Result, outcome.Result);
            Assert.Equal(shipped.WritePlan?.ReplacementText, outcome.WritePlan?.ReplacementText);
            Assert.Equal(shipped.DecisionPlan?.Items.Count, outcome.DecisionPlan?.Items.Count);
        }
    }

    [Fact]
    public async Task Level3_StaysNeedsDecision_AndTheUsersProtectDecisionStillProducesTheAlias_AfterAnOffToOnChange()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        var host = new CountryScopedPipelineHost(source, Build);
        var processor = Processor(host);
        Assert.Equal(0, processor.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);
        source.Set(CountrySettingObservation.AuthenticatedOn);

        var pending = processor.Process(ChatGpt, Snapshot(UsSsn));
        var item = Assert.Single(pending.DecisionPlan!.Items);
        var read = new FakeClipboardReadTransport { NextReadResult = ClipboardTextReadResult.Success(Snapshot(UsSsn)) };
        var write = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 2) };
        var resolver = new ClipboardDecisionActionResolver(
            new ClipboardOperationGate(), new FakeClipboardDecisionScopeLifecycle(), new FakeForegroundTargetCapture(),
            read, write, processor, new FakeClipboardComposerVerificationHandoff());
        var tracker = new RevisionTracker();
        var scope = new ClipboardDecisionScope(tracker, tracker.Observe(UsSsn), [item], generation: 0);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(1, pending.Result.NeedsDecisionCount);
        Assert.Null(pending.WritePlan);
        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.Equal("SSN: [SSN1]", Assert.Single(write.ReceivedReplacementTexts));
    }

    [Fact]
    public void ConcurrentAttemptsAndSettingChanges_EachAttemptIsEitherFullyOffOrFullyOn_NeverAFailureOrAMixture()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        var processor = Processor(new CountryScopedPipelineHost(source, Build));
        var problems = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var stop = new CancellationTokenSource();
        const string text = UsSsn + " 연락처 010-1234-5678";

        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var r = processor.Process(ChatGpt, Snapshot(text)).Result;
                    bool off = r.CandidateCount == 1 && r.NeedsDecisionCount == 0;
                    bool on = r.CandidateCount == 2 && r.NeedsDecisionCount == 1;
                    if (!off && !on) problems.Add($"mixed outcome {r}");
                }
                catch (Exception ex)
                {
                    problems.Add(ex.GetType().Name);
                }
            }
        })).ToArray();

        for (int i = 0; i < 200; i++)
        {
            bool on = i % 2 == 0;
            source.Set(on ? CountrySettingObservation.AuthenticatedOn : CountrySettingObservation.AuthenticatedOff);
            var r = processor.Process(ChatGpt, Snapshot(text)).Result; // started after the change completed
            if (r.NeedsDecisionCount != (on ? 1 : 0)) problems.Add($"stale after change #{i}");
        }

        stop.Cancel();
        Task.WaitAll(workers);
        Assert.True(problems.IsEmpty, string.Join(", ", problems.Distinct().Take(5)));
    }

    // ---- the closest thing to the real composition: the real store and its real providers -----------------------

    [Fact]
    public void WithTheRealStoreAndItsProviders_TheUsPackRunsInFull_BecauseNoCountrySettingIsPersisted_AndCategoryTogglesStillOnlyTouchTheirOwnCategory()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var host = new CountryScopedPipelineHost(new StoreBackedCountrySettingSource(store), Build);
        var processor = Processor(
            host, new ProtectionCategorySettingsProvider(store), new TrustExceptionProvider(store), new UserExceptionProvider(store));

        // The user turns the Phone category off through the real service.
        new ProtectionCategorySettingsService(store).SetPhoneEnabled(false);
        var outcome = processor.Process(ChatGpt, Snapshot(UsSsn + " 연락처 010-1234-5678"));

        Assert.Equal(2, outcome.Result.CandidateCount);
        Assert.Equal(1, outcome.Result.BypassCount);        // the phone, category OFF
        Assert.Equal(1, outcome.Result.NeedsDecisionCount); // the SSN: a category switch never touches a U.S. Level 3
        Assert.Equal(PiiType.UsSsn, Assert.Single(outcome.DecisionPlan!.Items).Canonical.PiiType);
    }

    [Fact]
    public void ATamperedSettingsFile_SilentlyFallsBackToTheProtectiveDefault_AndNeverTurnsTheUsPackOff()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveSettings(PrivonSettings.SafeDefault);
        File.WriteAllBytes(Path.Combine(_root, "settings.bin"), System.Text.Encoding.UTF8.GetBytes("{\"UnitedStates\":\"Off\"}"));
        var processor = Processor(
            new CountryScopedPipelineHost(new StoreBackedCountrySettingSource(store), Build),
            new ProtectionCategorySettingsProvider(store));

        var outcome = processor.Process(ChatGpt, Snapshot(UsSsn));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
    }

    [Fact]
    public void ThePlainProcessor_StillBuildsItsPipelineFromTheDefaultPipeline_AndNoExperimentAssemblyIsReferenced()
    {
        var referenced = typeof(ClipboardPrivacyProcessor).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(referenced, n => n.Contains("Experiment") || n.Contains("UsOptInHost"));
        Assert.Equal(0, new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider(), new ProtectionCategorySettingsProvider(PrivonLocalStore.OpenOrCreate(_root)))
            .Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);
    }
}
