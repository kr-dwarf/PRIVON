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

// SYNTHETIC DATA ONLY. The explicit opt-in through the REAL PrivonAppComposition: its internal constructor takes one per-use
// pipeline delegate, which BuildGraph hands to the real ClipboardPrivacyProcessor and PrivonAppUiBridge hands to the real
// SettingsCoordinator. Country observations come from a test fixture (the product has no source that can say OFF). Nothing
// here writes the clipboard: the composition's processor is called directly, and decisions use the same fake transports as
// the other resolver tests.
public sealed class UsAppCompositionOptInTests : IDisposable
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    private const string UsSsn = "SSN: 321-54-9876";

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

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsAppCompositionOptIn", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private PrivonAppComposition StartedComposition(Func<DetectionPipeline>? source, string? root = null)
    {
        var composition = new PrivonAppComposition(
            root ?? NewRoot(), new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(),
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

    // ---- the shipped composition is unchanged ------------------------------------------------------------------------

    [Fact]
    public void TheShippedComposition_HasNoPipelineSource_AndItsRealProcessorFindsNoUsIdentifier()
    {
        var composition = StartedComposition(null);
        var processor = ProcessorOf(composition);

        Assert.Null(composition.DetectionPipelineSource);
        foreach (string text in new[] { UsSsn, "ATIN: 912-93-4567", "ITIN: 912-70-1234", "MBI: 1A23C45DE67", "N1234567890" })
        {
            Assert.Equal(0, processor.Process(ChatGpt, Snapshot(text)).Result.CandidateCount);
        }

        Assert.Equal(1, processor.Process(ChatGpt, Snapshot("901231-1234567")).Result.NeedsDecisionCount);
    }

    [Fact]
    public void ThePublicProductionConstructor_CannotCarryAnOptIn()
    {
        var publicConstructors = typeof(PrivonAppComposition).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        var only = Assert.Single(publicConstructors);
        Assert.Empty(only.GetParameters());
        Assert.False(typeof(PrivonAppComposition).IsPublic);
    }

    // ---- the opt-in reaches the real processor ------------------------------------------------------------------------

    [Theory]
    [InlineData("SSN: 321-54-9876", PiiType.UsSsn)]
    [InlineData("ITIN: 912-70-1234", PiiType.UsItin)]
    [InlineData("ATIN: 912-93-4567", PiiType.UsAtin)]
    [InlineData("MBI: 1A23C45DE67", PiiType.UsMbi)]
    [InlineData("N1234567890", PiiType.UsSevis)]
    public void OptIn_TheCompositionsRealProcessorDetectsAllFive_AsLevel3Decisions(string text, PiiType type)
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOn);
        var host = new CountryScopedPipelineHost(source, Build);
        var composition = StartedComposition(() => host.Current());

        var outcome = ProcessorOf(composition).Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Null(outcome.WritePlan);
        Assert.Equal(type, Assert.Single(outcome.DecisionPlan!.Items).Canonical.PiiType);
    }

    [Fact]
    public void OptIn_TheDelegateIsExposedUnchanged_AndTheCoordinatorAndResolverStillShareOneProcessor()
    {
        Func<DetectionPipeline> source = () => Build(CountrySettingObservation.AuthenticatedOn);
        var composition = StartedComposition(source);

        var coordinator = typeof(PrivonAppComposition).GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composition)!;
        var coordinatorProcessor = coordinator.GetType().GetField("_processor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator);

        Assert.Same(source, composition.DetectionPipelineSource);
        Assert.Same(ProcessorOf(composition), coordinatorProcessor);
    }

    [Fact]
    public void OptIn_OffThenOnThenOff_IsFollowedByTheVeryNextProcessingAttempt()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        var host = new CountryScopedPipelineHost(source, Build);
        var processor = ProcessorOf(StartedComposition(() => host.Current()));

        Assert.Equal(0, processor.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);
        source.Set(CountrySettingObservation.AuthenticatedOn);
        Assert.Equal(1, processor.Process(ChatGpt, Snapshot(UsSsn)).Result.NeedsDecisionCount);
        source.Set(CountrySettingObservation.AuthenticatedOff);
        Assert.Equal(0, processor.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);
    }

    // ---- processor and Settings never disagree ----------------------------------------------------------------------

    private static (SettingsCoordinator Coordinator, List<FakeSettingsSurface> Surfaces) SettingsConsumerOf(
        PrivonAppComposition composition, PrivonLocalStore store)
    {
        var surfaces = new List<FakeSettingsSurface>();
        var coordinator = new SettingsCoordinator(
            new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(),
            () => store.IsMasterKeyUnavailable,
            () =>
            {
                var surface = new FakeSettingsSurface();
                surfaces.Add(surface);
                return surface;
            },
            new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            composition.DetectionPipelineSource);
        return (coordinator, surfaces);
    }

    private static string? AddUsException(SettingsCoordinator coordinator, List<FakeSettingsSurface> surfaces)
    {
        coordinator.ShowRequested();
        var surface = surfaces[^1];
        surface.RaiseAddExceptionRequested(new AddExceptionRequest(PiiType.Phone, UsSsn));
        return surface.LastRenderedState!.StatusMessage;
    }

    [Fact]
    public void OptIn_TheProcessorAndTheSettingsCanonicalizerSeeTheSameCountryState_AtEveryStep()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        var host = new CountryScopedPipelineHost(source, Build);
        var root = NewRoot();
        var composition = StartedComposition(() => host.Current(), root);
        var processor = ProcessorOf(composition);
        var store = PrivonLocalStore.OpenOrCreate(root);

        foreach (var (setting, usDetected) in new[]
                 { (CountrySettingObservation.AuthenticatedOff, false), (CountrySettingObservation.AuthenticatedOn, true),
                   (CountrySettingObservation.AuthenticatedOff, false), (CountrySettingObservation.Corrupt, true) })
        {
            source.Set(setting);
            var (coordinator, surfaces) = SettingsConsumerOf(composition, store);

            bool processorSeesUs = processor.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount > 0;
            string? settingsMessage = AddUsException(coordinator, surfaces);

            Assert.Equal(usDetected, processorSeesUs);
            // The Settings path agrees: a U.S. candidate is detected (type mismatch against the selected Phone type) exactly
            // when the processor detects it; otherwise nothing is recognised.
            Assert.Equal(usDetected ? SettingsCoordinator.TypeMismatchMessage : SettingsCoordinator.NoCandidateMessage, settingsMessage);
        }
    }

    [Fact]
    public void OptIn_TheUiBridgeHandsTheCompositionsDelegateToTheSettingsCoordinator()
    {
        var root = NewRoot();
        Func<DetectionPipeline> source = () => Build(CountrySettingObservation.AuthenticatedOn);
        var store = PrivonLocalStore.OpenOrCreate(root);
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var bridge = new PrivonAppUiBridge(
            new ClipboardDecisionSessionPublisher(lifecycle), lifecycle, new FakeClipboardDecisionResolver(),
            new FakeDispatcherScheduler { RunSynchronously = true }, new FakeTrayIconSurface(), () => new FakeDecisionPromptSurface(),
            new WindowsAutoStartCoordinator(new FakeWindowsAutoStartRegistration(), () => "C:\\PRIVON\\PRIVON.exe"),
            new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(),
            () => store.IsMasterKeyUnavailable, () => new FakeSettingsSurface(),
            new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            settingsPipelineSource: source);

        var coordinator = typeof(PrivonAppUiBridge).GetField("_settingsCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        var held = coordinator.GetType().GetField("_pipelineSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator);

        Assert.Same(source, held);
    }

    [Fact]
    public void CreateProduction_PassesTheCompositionsDelegate_AndTheShippedPathStillPassesTheDefaultPipeline()
    {
        string path = FindSource("PrivonAppUiBridge.cs");
        string text = File.ReadAllText(path);

        Assert.Contains("composition.DetectionPipelineSource", text);
        Assert.Contains("DetectionPipeline.CreateDefault()", text); // the shipped argument is still there, unchanged
    }

    private static string FindSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.App", file))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Privon.App", file);
    }

    // ---- failures never fall back to a stale OFF ----------------------------------------------------------------------

    [Fact]
    public void WhenTheRebuildForOnFails_BothConsumersFailInstead_AndNeitherReportsAnEmptyOrStaleOffResult()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        bool failOn = false;
        var host = new CountryScopedPipelineHost(source, s =>
            s == CountrySettingObservation.AuthenticatedOn && failOn ? throw new InvalidOperationException("synthetic rebuild failure") : Build(s));
        var root = NewRoot();
        var composition = StartedComposition(() => host.Current(), root);
        var processor = ProcessorOf(composition);
        var store = PrivonLocalStore.OpenOrCreate(root);
        Assert.Equal(0, processor.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);

        failOn = true;
        source.Set(CountrySettingObservation.AuthenticatedOn);
        var (coordinator, surfaces) = SettingsConsumerOf(composition, store);

        Assert.Throws<InvalidOperationException>(() => processor.Process(ChatGpt, Snapshot(UsSsn)));
        Assert.Equal(SettingsCoordinator.GenericFailureMessage, AddUsException(coordinator, surfaces)); // not "no candidate"

        failOn = false;
        Assert.Equal(1, processor.Process(ChatGpt, Snapshot(UsSsn)).Result.NeedsDecisionCount);
    }

    [Fact]
    public void AMissingRequiredUsDetector_FailsTheRealProcessorExplicitly_UnlessTheCountryIsAuthenticatedOff()
    {
        IReadOnlyList<IDetector> WithoutSevis() => UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != "us.sevis").ToList();
        var on = StartedComposition(() => UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.Missing, usDetectorFactory: WithoutSevis));
        var off = StartedComposition(() => UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, usDetectorFactory: WithoutSevis));

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() => ProcessorOf(on).Process(ChatGpt, Snapshot(UsSsn)));
        Assert.Equal("us.sevis", ex.Id.Value);
        Assert.Equal(0, ProcessorOf(off).Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);
    }

    // ---- what must not change ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("연락처 010-1234-5678 이메일 user@example.test")]
    [InlineData("901231-1234567 연락처 010-1234-5678")]
    [InlineData("카드 4111 1111 1111 1111")]
    [InlineData("오늘 날씨가 좋네요")]
    public void KoreanAndGlobalOutcomes_OfTheOptInCompositionEqualTheShippedCompositions_InEveryCountryState(string text)
    {
        var shipped = ProcessorOf(StartedComposition(null)).Process(ChatGpt, Snapshot(text));
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        var host = new CountryScopedPipelineHost(source, Build);
        var processor = ProcessorOf(StartedComposition(() => host.Current()));

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
    public async Task OptIn_Level3StaysNeedsDecision_AndTheUsersProtectDecisionStillProducesTheAlias()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        var host = new CountryScopedPipelineHost(source, Build);
        var processor = ProcessorOf(StartedComposition(() => host.Current()));
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
    public void OptIn_ConcurrentProcessingAndSettingChanges_NeverFail_NeverMix()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        var host = new CountryScopedPipelineHost(source, Build);
        var processor = ProcessorOf(StartedComposition(() => host.Current()));
        var problems = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var stop = new CancellationTokenSource();

        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    int n = processor.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount;
                    if (n != 0 && n != 1) problems.Add("unexpected count " + n);
                }
                catch (Exception ex)
                {
                    problems.Add(ex.GetType().Name);
                }
            }
        })).ToArray();

        for (int i = 0; i < 150; i++)
        {
            bool on = i % 2 == 0;
            source.Set(on ? CountrySettingObservation.AuthenticatedOn : CountrySettingObservation.AuthenticatedOff);
            int after = processor.Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount; // started after the change completed
            if (after != (on ? 1 : 0)) problems.Add($"stale after change #{i}");
        }

        stop.Cancel();
        Task.WaitAll(workers);
        Assert.True(problems.IsEmpty, string.Join(", ", problems.Distinct().Take(5)));
    }

    // ---- T-1 stays incomplete: the real store is the only real source, and it cannot say OFF -------------------------

    [Fact]
    public void WithTheRealStoreAsTheOnlySource_TheUsPackRunsInFull_ThroughTheRealComposition()
    {
        var root = NewRoot();
        var composition = StartedComposition(null, root); // creates the store at root
        var store = PrivonLocalStore.OpenOrCreate(root);
        var host = new CountryScopedPipelineHost(new StoreBackedCountrySettingSource(store), Build);
        var optIn = StartedComposition(() => host.Current());

        Assert.Equal(CountrySettingObservation.Missing, new StoreBackedCountrySettingSource(store).Read());
        Assert.Equal(1, ProcessorOf(optIn).Process(ChatGpt, Snapshot(UsSsn)).Result.NeedsDecisionCount);
        Assert.Equal(0, ProcessorOf(composition).Process(ChatGpt, Snapshot(UsSsn)).Result.CandidateCount);
    }
}
