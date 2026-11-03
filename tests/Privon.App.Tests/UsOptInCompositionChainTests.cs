using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Authority;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The chain from the owner of the explicit U.S. opt-in to the tray menu, through the REAL PrivonAppComposition's two delegates
// and the real PrivonAppUiBridge:
//   UsOptInOwner (explicit authority -> real CountryScopedPipelineHost) -> composition.DetectionPipelineSource / composition.UsReadinessSource
//   -> PrivonAppUiBridge -> IUsReadinessSurface (the tray).
//
// WHAT THIS IS. A test host: App.Tests is the only assembly that sees both the owner (tools) and the app's internals. The shipped app (src) references
// no tools assembly and no tools assembly references the app, so NO shipped code builds this chain; the single piece of glue below (Adapt) is exactly
// what a future host that owns both would have to write. Nothing here starts the composition, touches the clipboard or hooks Windows.
public sealed class UsOptInCompositionChainTests : IDisposable
{
    private readonly List<string> _roots = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
        foreach (var r in _roots)
        {
            try { Directory.Delete(r, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class Setting(CountrySettingObservation initial) : ICountrySettingSource
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

    // The glue a host owning both assemblies writes: map the owner's snapshot to the tray's report. Names and meanings are 1:1.
    private static UsReadinessReport Adapt(UsOptInReadinessSnapshot s) => new(
        s.UsEnabled,
        s.Provenance switch
        {
            UsOptInProvenance.Production => UsAuthorityProvenance.Production,
            UsOptInProvenance.Provided => UsAuthorityProvenance.Provided,
            _ => UsAuthorityProvenance.NotSelected,
        },
        s.Detectors.Select(d => new UsDetectorReadiness(d.DetectorId, d.Availability switch
        {
            UsOptInAvailability.Ready => UsDetectorAvailability.Ready,
            UsOptInAvailability.ValidationFailed => UsDetectorAvailability.ValidationFailed,
            _ => UsDetectorAvailability.NotProvided,
        })).ToList(),
        s.ArtifactVersion);

    private sealed class Rig
    {
        public required PrivonAppComposition Composition { get; init; }
        public required PrivonAppUiBridge Bridge { get; init; }
        public required FakeTrayIconSurface Tray { get; init; }
        public required FakeDispatcherScheduler Scheduler { get; init; }
    }

    private Rig Build(
        Func<DetectionPipeline>? pipelineSource, Func<UsReadinessReport>? readinessSource,
        bool synchronous = true, ClipboardProtectionStatusTracker? tracker = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "UsOptInChain", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        // the composition is constructed (so its two delegates are the ones the bridge is handed) but never started: no monitor, no hook
        var composition = new PrivonAppComposition(
            root, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor(),
            enableDiagnostics: false, detectionPipelineSource: pipelineSource, usReadinessSource: readinessSource);
        _disposables.Add(composition);

        var store = PrivonLocalStore.OpenOrCreate(root);
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var scheduler = new FakeDispatcherScheduler { RunSynchronously = synchronous };
        var tray = new FakeTrayIconSurface();
        var bridge = new PrivonAppUiBridge(
            new ClipboardDecisionSessionPublisher(lifecycle), lifecycle, new FakeClipboardDecisionResolver(), scheduler, tray,
            () => new FakeDecisionPromptSurface(),
            new WindowsAutoStartCoordinator(new FakeWindowsAutoStartRegistration(), () => "PRIVON.exe"),
            new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(),
            () => store.IsMasterKeyUnavailable, () => new FakeSettingsSurface(),
            new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            settingsPipelineSource: composition.DetectionPipelineSource,
            protectionStatus: tracker,
            usReadinessSource: composition.UsReadinessSource);
        _disposables.Add(bridge);
        return new Rig { Composition = composition, Bridge = bridge, Tray = tray, Scheduler = scheduler };
    }

    private Rig WithOwner(UsOptInOwner owner, bool synchronous = true, ClipboardProtectionStatusTracker? tracker = null)
    {
        _disposables.Add(owner);
        return Build(owner.CurrentPipeline, () => Adapt(owner.CurrentReadiness()), synchronous, tracker);
    }

    private static string Line(Rig rig) => Assert.IsType<UsReadinessPresentation>(rig.Tray.CurrentUsReadiness).MenuText;

    private static IReadOnlySet<string> ReadyOnTray(UsReadinessPresentation p, UsOptInOwner owner) =>
        owner.CurrentReadiness().Detectors.Where(d => d.Availability == UsOptInAvailability.Ready).Select(d => d.DetectorId).ToHashSet(StringComparer.Ordinal);

    // ===== the chain ============================================================================================================

    [Fact]
    public void TheCompositionHoldsTheOwnersTwoDelegates_AndTheBridgeShowsTheOwnersProductionState()
    {
        var owner = UsOptInOwner.CreateWithProductionAuthority(new Setting(CountrySettingObservation.Missing));
        var rig = WithOwner(owner);

        Assert.NotNull(rig.Composition.DetectionPipelineSource);
        Assert.NotNull(rig.Composition.UsReadinessSource);
        rig.Bridge.Start();

        Assert.Contains("13/13", Line(rig));
        Assert.Contains("Verified Official Sources", Line(rig));
        Assert.Equal(UsReadinessKind.AllReady, rig.Tray.CurrentUsReadiness!.Kind);
    }

    [Fact]
    public void TheNumberOnTheTray_IsTheNumberOfDetectorsTheServingPipelineActuallyRuns()
    {
        string[] texts =
        [
            "ATIN: 912-93-4567", "ITIN: 912-70-1234", "SSN: 321-54-9876", "MBI: 1A23C45DE67", "SEVIS: N1234567890",
            "Passport Number: P12345678 State: NY", "A-Number: A1234567", "I-94 Number: 123456789AB", "Driver License Number: D123-4567 State: CA",
            "Routing Number: 120000003 | Account Number: AB12CD34", "Phone: 212-555-1212", "Address: 123 Main ST", "Full Name: Alice O'Neil",   // 212 and ST are in the production lists AND in the small fixtures
        ];
        string[] ids =
        [
            "us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis", "us.passport", "us.a_number", "us.i94", "us.driver_state_id", "us.bank_account",
            "us.phone", "us.address", "us.name",
        ];
        var variants = new (string Name, UsAuthoritySet? Authority, int Expected)[]
        {
            ("production", UsAuthoritySet.Production, 13),
            ("none", null, 11),
            ("fixture", UsAuthoritySet.Load(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid()), 13),
            ("phone-only", UsAuthoritySet.LoadProduction(new Defective().Without(UsAuthoritySet.SuffixId).Without(UsAuthoritySet.StateId)), 12),
            ("damaged", UsAuthoritySet.LoadProduction(new Defective().Flipped(UsAuthoritySet.NpaId)), 12),
        };

        foreach (var (name, authority, expected) in variants)
        {
            var owner = UsOptInOwner.Create(new Setting(CountrySettingObservation.Missing), authority);
            var rig = WithOwner(owner);
            rig.Bridge.Start();

            var pipeline = rig.Composition.DetectionPipelineSource!();
            var serving = ids.Where((id, i) => pipeline.Detect(texts[i]).Candidates.Any(c => c.DetectorId.Value == id)).ToHashSet(StringComparer.Ordinal);

            Assert.True(expected == serving.Count, $"{name}: serving {serving.Count}");
            Assert.True(serving.SetEquals(ReadyOnTray(rig.Tray.CurrentUsReadiness!, owner)), $"{name}: snapshot vs serving");
            Assert.Contains($"{expected}/13", Line(rig));
        }
    }

    private sealed class Defective : IAuthoritySnapshotProvider
    {
        private readonly UsProductionAuthorityProvider _real = new();
        private readonly HashSet<string> _missing = [];
        private readonly HashSet<string> _flipped = [];

        public Defective Without(AuthoritySnapshotId id) { _missing.Add(id.Value); return this; }
        public Defective Flipped(AuthoritySnapshotId id) { _flipped.Add(id.Value); return this; }

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
            metadata = real;
            return true;
        }
    }

    [Fact]
    public void WithoutAnAuthority_TheTrayShowsElevenOfThirteen_NotSelected()
    {
        var rig = WithOwner(UsOptInOwner.Create(new Setting(CountrySettingObservation.Missing), null));

        rig.Bridge.Start();

        Assert.Contains("11/13", Line(rig));
        Assert.Contains("Data not selected", Line(rig));
        Assert.DoesNotContain("Verified Official Sources", Line(rig));
    }

    [Fact]
    public void ASyntheticFixtureIsNeverShownAsProductionVerified_EvenAtThirteenOfThirteen()
    {
        var rig = WithOwner(UsOptInOwner.Create(new Setting(CountrySettingObservation.Missing), UsAuthoritySet.Load(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid())));

        rig.Bridge.Start();

        Assert.Contains("13/13", Line(rig));
        Assert.Contains("Not Production-Verified", Line(rig));
        Assert.DoesNotContain("Verified Official Sources", rig.Tray.CurrentUsReadiness!.Detail);
    }

    [Fact]
    public void ADamagedSnapshot_IsShownAsUnusable_TwelveOfThirteen()
    {
        var rig = WithOwner(UsOptInOwner.Create(new Setting(CountrySettingObservation.Missing),
            UsAuthoritySet.LoadProduction(new Defective().Flipped(UsAuthoritySet.StateId))));

        rig.Bridge.Start();

        Assert.Contains("12/13", Line(rig));
        Assert.Contains("Unavailable (data validation failed): Address", Line(rig));
    }

    // ===== the country setting: followed, not authenticated ==================================================================

    [Fact]
    public void WhenTheSettingSaysOff_TheTrayShowsOff_AndTheServingPipelineRunsNoUsDetector()
    {
        var owner = UsOptInOwner.CreateWithProductionAuthority(new Setting(CountrySettingObservation.AuthenticatedOff));
        var rig = WithOwner(owner);

        rig.Bridge.Start();

        Assert.Equal(UsReadinessKind.Off, rig.Tray.CurrentUsReadiness!.Kind);
        Assert.Empty(rig.Composition.DetectionPipelineSource!().Detect("SSN: 321-54-9876 Phone: 202-555-0147").Candidates);
    }

    [Theory]
    [InlineData(CountrySettingObservation.Missing)]
    [InlineData(CountrySettingObservation.Corrupt)]
    [InlineData(CountrySettingObservation.Unauthenticated)]
    [InlineData(CountrySettingObservation.UnknownVersion)]
    public void AnUncertainSetting_KeepsTheUsDetectorsAndTheirLine(CountrySettingObservation setting)
    {
        var owner = UsOptInOwner.CreateWithProductionAuthority(new Setting(setting));
        var rig = WithOwner(owner);

        rig.Bridge.Start();

        Assert.Contains("13/13", Line(rig));
        Assert.NotEmpty(rig.Composition.DetectionPipelineSource!().Detect("SSN: 321-54-9876").Candidates);
    }

    [Fact]
    public void TheProductsOwnSettingSource_NeverProducesAnOffLine()
    {
        string root = Path.Combine(Path.GetTempPath(), "UsOptInChainStore", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var store = PrivonLocalStore.OpenOrCreate(root);
        var rig = WithOwner(UsOptInOwner.CreateWithProductionAuthority(new StoreBackedCountrySettingSource(store)));

        rig.Bridge.Start();

        Assert.NotEqual(UsReadinessKind.Off, rig.Tray.CurrentUsReadiness!.Kind);       // no persisted boundary can say "off": T-1 stays unresolved
    }

    // ===== freshness ============================================================================================================

    [Fact]
    public void WhenTheSettingChanges_TheNextRefreshShowsIt_AndNeverTheOldThirteenOfThirteen()
    {
        var setting = new Setting(CountrySettingObservation.Missing);
        var rig = WithOwner(UsOptInOwner.CreateWithProductionAuthority(setting));
        rig.Bridge.Start();
        Assert.Contains("13/13", Line(rig));

        setting.Set(CountrySettingObservation.AuthenticatedOff);
        rig.Bridge.NotifyUsReadinessChanged();

        Assert.Equal(UsReadinessKind.Off, rig.Tray.CurrentUsReadiness!.Kind);
        Assert.DoesNotContain(rig.Tray.UsReadinessHistory.Skip(1), h => h.MenuText.Contains("13/13"));
    }

    [Fact]
    public void WhenTheHostCannotRebuildOrTheSourceFails_TheTrayShowsUnknown_AndRecovers()
    {
        var setting = new Setting(CountrySettingObservation.Missing);
        var rig = WithOwner(UsOptInOwner.CreateWithProductionAuthority(setting));
        rig.Bridge.Start();

        setting.Throw = new InvalidOperationException("synthetic settings failure");
        rig.Bridge.NotifyUsReadinessChanged();
        Assert.Equal(UsReadinessKind.Unknown, rig.Tray.CurrentUsReadiness!.Kind);
        Assert.DoesNotContain("synthetic", rig.Tray.CurrentUsReadiness.Detail);

        setting.Throw = null;
        rig.Bridge.NotifyUsReadinessChanged();
        Assert.Contains("13/13", Line(rig));
    }

    [Fact]
    public void AReadinessProviderThatThrows_IsShownAsUnknown()
    {
        var owner = UsOptInOwner.CreateWithProductionAuthority(new Setting(CountrySettingObservation.Missing));
        _disposables.Add(owner);
        bool fail = false;
        var rig = Build(owner.CurrentPipeline, () => fail ? throw new InvalidOperationException("provider") : Adapt(owner.CurrentReadiness()));
        rig.Bridge.Start();
        Assert.Contains("13/13", Line(rig));

        fail = true;
        rig.Bridge.NotifyUsReadinessChanged();

        Assert.Equal(UsReadinessKind.Unknown, rig.Tray.CurrentUsReadiness!.Kind);
    }

    [Fact]
    public void WhenTheOwnerIsDisposed_TheLineBecomesUnknown_AndNoPipelineIsServed()
    {
        var owner = UsOptInOwner.CreateWithProductionAuthority(new Setting(CountrySettingObservation.Missing));
        var rig = WithOwner(owner);
        rig.Bridge.Start();
        Assert.Contains("13/13", Line(rig));

        owner.Dispose();
        rig.Bridge.NotifyUsReadinessChanged();

        Assert.Equal(UsReadinessKind.Unknown, rig.Tray.CurrentUsReadiness!.Kind);
        Assert.Throws<ObjectDisposedException>(() => rig.Composition.DetectionPipelineSource!());   // a processing attempt fails; it never runs on an old pipeline
    }

    [Fact]
    public void WhenTheAuthorityBehindTheLineIsReplaced_TheLineFollows()
    {
        var healthy = UsOptInOwner.CreateWithProductionAuthority(new Setting(CountrySettingObservation.Missing));
        var damaged = UsOptInOwner.Create(new Setting(CountrySettingObservation.Missing), UsAuthoritySet.LoadProduction(new Defective().Flipped(UsAuthoritySet.NpaId)));
        _disposables.Add(healthy);
        _disposables.Add(damaged);
        var current = healthy;
        var rig = Build(() => current.CurrentPipeline(), () => Adapt(current.CurrentReadiness()));
        rig.Bridge.Start();
        Assert.Contains("13/13", Line(rig));

        current = damaged;
        rig.Bridge.NotifyUsReadinessChanged();

        Assert.Contains("12/13", Line(rig));
        Assert.Contains("Phone", Line(rig));
    }

    [Fact]
    public void OpeningTheMenu_ReReadsBeforeItIsShown_EvenWhileTheUiQueueIsBusy()
    {
        var setting = new Setting(CountrySettingObservation.Missing);
        var rig = WithOwner(UsOptInOwner.CreateWithProductionAuthority(setting), synchronous: false);
        rig.Bridge.Start();
        rig.Scheduler.RunAllPending();
        Assert.Contains("13/13", Line(rig));

        setting.Set(CountrySettingObservation.AuthenticatedOff);       // the state changed; the UI queue is busy and nothing has been posted
        rig.Tray.RaiseUsReadinessRefreshRequested();                    // the user opens the menu

        Assert.Equal(UsReadinessKind.Off, rig.Tray.CurrentUsReadiness!.Kind);   // the line was verified before display, not after
        Assert.Equal(0, rig.Scheduler.PendingCount);                              // and it needed no queued render to do so
    }

    [Fact]
    public void ALateQueuedRender_ShowsTheNewestState()
    {
        var setting = new Setting(CountrySettingObservation.Missing);
        var rig = WithOwner(UsOptInOwner.CreateWithProductionAuthority(setting), synchronous: false);
        rig.Bridge.Start();                                             // queued, not run
        setting.Set(CountrySettingObservation.AuthenticatedOff);
        rig.Bridge.NotifyUsReadinessChanged();

        rig.Scheduler.RunAllPending();

        Assert.DoesNotContain(rig.Tray.UsReadinessHistory, h => h.MenuText.Contains("13/13"));
        Assert.Equal(UsReadinessKind.Off, rig.Tray.CurrentUsReadiness!.Kind);
    }

    [Fact]
    public void ConcurrentSettingChanges_EndWithTheFinalState()
    {
        var setting = new Setting(CountrySettingObservation.Missing);
        var rig = WithOwner(UsOptInOwner.CreateWithProductionAuthority(setting), synchronous: false);
        rig.Bridge.Start();
        var gate = new object();

        Parallel.For(0, 200, i =>
        {
            setting.Set(i % 2 == 0 ? CountrySettingObservation.AuthenticatedOff : CountrySettingObservation.Missing);
            lock (gate) rig.Bridge.NotifyUsReadinessChanged();      // FakeDispatcherScheduler queues into a plain Queue; the real dispatcher is thread-safe
        });
        setting.Set(CountrySettingObservation.Missing);
        lock (gate) rig.Bridge.NotifyUsReadinessChanged();
        rig.Scheduler.RunAllPending();

        Assert.Contains("13/13", Line(rig));
    }

    // ===== priority, subscriptions, Dispose =====================================================================================

    [Fact]
    public void AProtectionFailureKeepsItsWarning_TheReadinessLineNeverReplacesIt()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var rig = WithOwner(UsOptInOwner.CreateWithProductionAuthority(new Setting(CountrySettingObservation.Missing)), tracker: tracker);
        rig.Bridge.Start();

        tracker.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(1, 1, true, true));
        tracker.Record(ClipboardDiagnosticEvent.AttemptTerminal(1, ClipboardDiagnosticTerminalReason.ProcessingException, "SyntheticException"));
        int statusCalls = rig.Tray.ProtectionStatusHistory.Count;
        rig.Tray.RaiseUsReadinessRefreshRequested();
        rig.Bridge.NotifyUsReadinessChanged();

        Assert.Equal(ProtectionState.Error, rig.Tray.ProtectionStatusHistory[^1].Presentation.State);
        Assert.Single(rig.Tray.ProtectionStatusHistory, h => h.Notify);                 // one warning balloon, as before
        Assert.Equal(statusCalls, rig.Tray.ProtectionStatusHistory.Count);              // the readiness line never touched the protection surface
        Assert.False(rig.Tray.CurrentUsReadiness!.Detail.Contains("remain on the clipboard"));
    }

    [Fact]
    public void TheBridgeSubscribesOnceAndReleasesEverythingOnDispose()
    {
        var rig = WithOwner(UsOptInOwner.CreateWithProductionAuthority(new Setting(CountrySettingObservation.Missing)));

        rig.Bridge.Start();
        Assert.Equal(1, rig.Tray.UsReadinessRefreshSubscriberCount);
        Assert.Throws<InvalidOperationException>(() => rig.Bridge.Start());              // the bridge is single-use: no second subscription
        Assert.Equal(1, rig.Tray.UsReadinessRefreshSubscriberCount);

        rig.Bridge.Dispose();
        int shown = rig.Tray.UsReadinessHistory.Count;
        rig.Bridge.NotifyUsReadinessChanged();
        rig.Tray.RaiseUsReadinessRefreshRequested();

        Assert.Equal(0, rig.Tray.UsReadinessRefreshSubscriberCount);
        Assert.Equal(shown, rig.Tray.UsReadinessHistory.Count);
    }

    // ===== the shipped application is unchanged =================================================================================

    [Fact]
    public void ADefaultComposition_HasNeitherDelegate_AndItsTrayShowsNoUsLine()
    {
        var rig = Build(null, null);

        rig.Bridge.Start();
        rig.Tray.RaiseUsReadinessRefreshRequested();
        rig.Bridge.NotifyUsReadinessChanged();

        Assert.Null(rig.Composition.DetectionPipelineSource);
        Assert.Null(rig.Composition.UsReadinessSource);
        Assert.Empty(rig.Tray.UsReadinessHistory);
        Assert.Equal(0, rig.Tray.UsReadinessRefreshSubscriberCount);
    }

    [Fact]
    public void TheShippedStartupPath_UsesTheGatedProductionFactory_AndNamesNothingFromTheOptInOrTheHost()
    {
        string root = RepoRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "Privon.App", "App.xaml.cs"));

        Assert.Contains("PrivonAppComposition.CreateProduction()", app);
        Assert.DoesNotContain("new PrivonAppComposition()", app);
        Assert.NotNull(typeof(PrivonAppComposition).GetConstructor(Type.EmptyTypes));                     // the plain public constructor still takes nothing
        Assert.DoesNotContain(typeof(PrivonAppComposition).GetConstructors(), c => c.GetParameters().Any(p =>
            p.ParameterType.Name.Contains("UsReadiness") || p.ParameterType.ToString().Contains("DetectionPipeline")));
        foreach (string token in new[] { "UsOptIn", "UsReadiness", "UsAuthority", "CountryScoped", "Privon.UsProductIntegration", "UsOptInHost", "PrivonOptInSession", "CreateWithUsOptIn" })
        {
            Assert.DoesNotContain(token, app);                           // the startup file names the factory only; nothing of the opt-in's machinery
        }
    }

    [Fact]
    public void TheReferenceDirection_IsSrcOnly_TheAppKnowsOneProductAssembly_AndNothingInSrcKnowsTools()
    {
        string root = RepoRoot();
        string appProject = File.ReadAllText(Path.Combine(root, "src", "Privon.App", "Privon.App.csproj"));
        string productProject = File.ReadAllText(Path.Combine(root, "src", "Privon.UsProductIntegration", "Privon.UsProductIntegration.csproj"));

        Assert.DoesNotContain("tools", appProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Experiment", appProject);
        var appUs = System.Text.RegularExpressions.Regex.Matches(appProject, "Include=\"([^\"]*Privon\\.Us[^\"]*)\"").Select(m => m.Groups[1].Value.Replace('\\', '/')).ToList();
        Assert.Equal(["../Privon.UsProductIntegration/Privon.UsProductIntegration.csproj"], appUs);         // the ONE U.S. reference of the shipped app, inside src
        Assert.DoesNotContain("Privon.App", productProject);
        Assert.DoesNotContain("tools", productProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Experiment", productProject);
        Assert.DoesNotContain(typeof(UsOptInOwner).Assembly.GetReferencedAssemblies(), a => a.Name == "Privon.App");
        Assert.DoesNotContain(typeof(PrivonAppComposition).Assembly.GetReferencedAssemblies(), a => a.Name!.Contains("Experiment") || a.Name == "Privon.UsOptInHost");
        Assert.DoesNotContain("Privon.UsProductIntegration", File.ReadAllText(Path.Combine(root, "src", "Privon.App", "AssemblyInfo.cs")));

        // nothing under tools is referenced by a src project, whatever its name
        foreach (string project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\")))
        {
            Assert.DoesNotContain("tools", File.ReadAllText(project), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void KoreanAndGlobalDetection_ThroughTheCompositionsPipeline_IsTheShippedOne()
    {
        const string text = "연락처 010-1234-5678 이메일 user@example.test 901231-1234567 카드 4111 1111 1111 1111";
        string Key(DetectionCandidate c) => $"{c.PiiType}|{c.Span}|{c.Canonical?.Value}|{c.RiskLevel}";
        var expected = DetectionPipeline.CreateDefault().Detect(text).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();
        var rig = WithOwner(UsOptInOwner.CreateWithProductionAuthority(new Setting(CountrySettingObservation.AuthenticatedOff)));

        var actual = rig.Composition.DetectionPipelineSource!().Detect(text).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();

        Assert.Equal(expected, actual);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.App", "UsReadiness.cs"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
