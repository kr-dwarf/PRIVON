using System.Reflection;
using Privon.App;
using Privon.Detection;
using Privon.Detection.Authority;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsOptInHost;
using Privon.UsProductIntegration;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The experimental U.S. opt-in HOST (tools/Privon.UsOptInHost) assembling the real app: the host's own builder and adapter, the app's
// ONE public assembly boundary (PrivonOptInSession), the real PrivonAppComposition it creates, and the real PrivonAppUiBridge / Settings coordinator /
// tray surface it feeds.
//
// What is real: every type named above, the production authority artifact, the real store, the owner and its host-cached pipeline.
// What is NOT done: nothing here starts a clipboard listener, registers a Windows hook, shows a tray icon or opens a window. The session is created through
// the real public Create (composition constructed, not started) and run through the internal lifecycle seam, which swaps only two things: "start the
// composition" (a no-op, because that starts the clipboard monitor) and "create the bridge" (the same bridge over fake surfaces instead of WinForms / WPF).
// That the host can run on a real desktop is therefore NOT shown here; it needs manual QA.
public sealed class UsOptInHostTests : IDisposable
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

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "UsOptInHost", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    // A named mutex can be re-acquired by the thread that already owns it, so "another instance" must be tried from another thread.
    private static T OnOtherThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() => { try { result = body(); } catch (Exception ex) { failure = ex; } });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) throw failure;
        return result;
    }

    private static string UniqueMutex() => "PRIVON-Test-" + Guid.NewGuid().ToString("N");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.App", "UsReadiness.cs"))) dir = dir.Parent;
        return dir!.FullName;
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

    private sealed class Probe(Action<string> record, string name) : IDisposable
    {
        public int Disposed;
        public Func<bool>? TrayWasDisposed;
        public void Dispose()
        {
            Interlocked.Increment(ref Disposed);
            record($"{name}:trayDisposed={TrayWasDisposed?.Invoke()}");
        }
    }

    // ---- a running host, with the app surfaces faked -------------------------------------------------------------------

    private sealed class Running
    {
        public required PrivonOptInSession Session { get; init; }
        public required FakeTrayIconSurface Tray { get; init; }
        public required FakeSettingsSurface Settings { get; init; }
        public required PrivonLocalStore Store { get; init; }
        public required string Root { get; init; }
    }

    private Running Assemble(
        string? root = null, string? mutex = null, Func<string, UsOptInSources>? build = null, bool startNow = true, Action? onBridge = null)
    {
        root ??= NewRoot();
        var tray = new FakeTrayIconSurface();
        var settings = new FakeSettingsSurface();
        var store = PrivonLocalStore.OpenOrCreate(root);
        var events = new List<string>();
        UsOptInSources? built = null;

        // The REAL public assembly boundary builds the composition from the host's sources ...
        var real = PrivonOptInSession.Create(r => built = (build ?? UsOptInSourcesBuilder.BuildProduction)(r), root);
        _disposables.Add(real);
        var composition = real.Composition;

        // ... and the lifecycle seam runs the same composition with a bridge over fake surfaces (and no clipboard listener).
        var probe = new Probe(events.Add, "lifetime") { TrayWasDisposed = () => tray.Disposed };
        var session = new PrivonOptInSession(
            composition, new CompositeLifetime(built!.Lifetime, probe), mutex ?? UniqueMutex(), _ => events.Add("composition-start"),
            c =>
            {
                onBridge?.Invoke();
                var lifecycle = new ClipboardDecisionScopeLifecycle();
                return new PrivonAppUiBridge(
                    new ClipboardDecisionSessionPublisher(lifecycle), lifecycle, new FakeClipboardDecisionResolver(),
                    new FakeDispatcherScheduler { RunSynchronously = true }, tray, () => new FakeDecisionPromptSurface(),
                    new WindowsAutoStartCoordinator(new FakeWindowsAutoStartRegistration(), () => "PRIVON.exe"),
                    new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(),
                    () => store.IsMasterKeyUnavailable, () => settings,
                    new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
                    settingsPipelineSource: c.DetectionPipelineSource,
                    usReadinessSource: c.UsReadinessSource,
                    countryChoices: new CountryChoiceService(store));
            });
        _disposables.Add(session);
        var running = new Running { Session = session, Tray = tray, Settings = settings, Store = store, Root = root };
        tray.OnDispose = () => events.Add("bridge-disposed");
        if (startNow)
        {
            Assert.True(session.TryStart());
            tray.RaiseSettingsRequested();                                                    // the tray opens Settings through the existing path
        }

        _eventLogs[session] = events;
        return running;
    }

    private readonly Dictionary<PrivonOptInSession, List<string>> _eventLogs = [];

    private sealed class CompositeLifetime(IDisposable? first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            second.Dispose();
            first?.Dispose();
        }
    }

    private static string Line(Running r) => Assert.IsType<UsReadinessPresentation>(r.Tray.CurrentUsReadiness).MenuText;

    private const string UsText = "SSN: 321-54-9876 Phone: 212-555-1212 Address: 123 Main ST Full Name: Alice O'Neil";

    private static int UsHits(DetectionPipeline p) =>
        p.Detect(UsText).Candidates.Count(c => c.DetectorId.Value.StartsWith("us.", StringComparison.Ordinal));

    // ===== the host project ====================================================================================================

    [Theory]
    [InlineData(new string[0], false, null)]
    [InlineData(new[] { "--experimental-us-opt-in" }, true, null)]
    [InlineData(new[] { "--experimental-us-opt-in", "--storage-root", "C:\\x" }, true, "C:\\x")]
    [InlineData(new[] { "--storage-root", "C:\\x" }, false, null)]                                   // a root alone is not an opt-in
    [InlineData(new[] { "--experimental-us-opt-in", "--storage-root" }, false, null)]               // a missing value is not a guess
    [InlineData(new[] { "--experimental-us-opt-in", "--storage-root", "--other" }, false, null)]
    [InlineData(new[] { "--experimental-us-opt-in", "--unknown" }, false, null)]                    // an unknown argument starts nothing
    [InlineData(new[] { "experimental-us-opt-in" }, false, null)]
    public void TheHostCommandLine_StartsOnlyOnTheExplicitFlag(string[] args, bool optIn, string? root)
    {
        var options = HostOptions.Parse(args);

        Assert.Equal(optIn, options.ExplicitOptIn);
        Assert.Equal(root, options.StorageRoot);
    }

    [Fact]
    public void TheHostEntryPoint_WithoutTheFlag_CreatesNothingAndReturnsNotOptedIn()
    {
        Assert.Equal(Privon.UsOptInHost.Program.ExitNotOptedIn, Privon.UsOptInHost.Program.Main([]));
        Assert.Equal(Privon.UsOptInHost.Program.ExitNotOptedIn, Privon.UsOptInHost.Program.Main(["--storage-root", "x"]));
        Assert.Equal(Privon.UsOptInHost.Program.ExitNotOptedIn, Privon.UsOptInHost.Program.Main(["--run"]));
    }

    // ===== the readiness adapter ================================================================================================

    [Fact]
    public void TheAdapter_IsOneToOne_ForProductionNoneAndProvidedAuthorities()
    {
        foreach (var (authority, provenance, ready) in new (UsAuthoritySet? A, UsAuthorityProvenance P, int R)[]
                 {
                     (UsAuthoritySet.Production, UsAuthorityProvenance.Production, 13),
                     (null, UsAuthorityProvenance.NotSelected, 11),
                     (UsAuthoritySet.Load(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid()), UsAuthorityProvenance.Provided, 13),
                     (UsAuthoritySet.LoadProduction(new Defective().Flipped(UsAuthoritySet.StateId)), UsAuthorityProvenance.Production, 12),
                 })
        {
            using var owner = UsOptInOwner.Create(new FixedSetting(CountrySettingObservation.Missing), authority);
            var snapshot = owner.CurrentReadiness();

            var report = UsReadinessAdapter.From(snapshot);

            Assert.Equal(provenance, report.Provenance);
            Assert.Equal(ready, report.Detectors.Count(d => d.Availability == UsDetectorAvailability.Ready));
            Assert.Equal(snapshot.Detectors.Select(d => d.DetectorId), report.Detectors.Select(d => d.DetectorId));
            Assert.Equal(snapshot.ArtifactVersion, report.ArtifactVersion);
            Assert.Equal(snapshot.UsEnabled, report.UsEnabled);
        }
    }

    private sealed class FixedSetting(CountrySettingObservation value) : ICountrySettingSource
    {
        public CountrySettingObservation Read() => value;
    }

    [Fact]
    public void TheAdapter_NeverPromotesAnUnknownValueToReadyOrProduction()
    {
        var snapshot = new UsOptInReadinessSnapshot(
            true, (UsOptInProvenance)99, [new UsOptInDetectorStatus("us.name", (UsOptInAvailability)99)], null);

        var report = UsReadinessAdapter.From(snapshot);

        Assert.Equal(UsAuthorityProvenance.NotSelected, report.Provenance);
        Assert.Equal(UsDetectorAvailability.NotProvided, Assert.Single(report.Detectors).Availability);
        Assert.Throws<ArgumentNullException>(() => UsReadinessAdapter.From(null!));
    }

    // ===== fail closed on a wrong authority ====================================================================================

    [Fact]
    public void TheVerifiedBuild_RefusesAnythingButAFullyUsableProductionAuthority()
    {
        string root = NewRoot();
        var refused = new UsAuthoritySet?[]
        {
            UsAuthoritySet.Unavailable,                                                                          // no authority at all
            UsAuthoritySet.Load(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid()),            // shape-only fixture: Provided, not production
            UsAuthoritySet.Load(new UsProductionAuthorityProvider()),                                            // the real bytes WITHOUT the production scope check
            UsAuthoritySet.LoadProduction(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid()),  // small fixtures refused by the production load
            UsAuthoritySet.LoadProduction(new Defective().Flipped(UsAuthoritySet.NpaId)),
            UsAuthoritySet.LoadProduction(new Defective().Without(UsAuthoritySet.SuffixId)),
        };

        foreach (var authority in refused)
        {
            Assert.Throws<InvalidOperationException>(() => UsOptInSourcesBuilder.BuildVerified(root, authority!));
        }

        Assert.Throws<ArgumentNullException>(() => UsOptInSourcesBuilder.BuildVerified(root, null!));
        using var ok = (IDisposable?)UsOptInSourcesBuilder.BuildVerified(root, UsAuthoritySet.Production).Lifetime;
        Assert.NotNull(ok);
    }

    [Fact]
    public void ADamagedAuthority_BuiltForATest_IsReportedAsDamaged_NeverAsProduction13()
    {
        var sources = UsOptInSourcesBuilder.Build(NewRoot(), UsAuthoritySet.LoadProduction(new Defective().Flipped(UsAuthoritySet.NpaId)));
        using var lifetime = sources.Lifetime;

        var report = sources.ReadinessSource();

        Assert.Equal(12, report.Detectors.Count(d => d.Availability == UsDetectorAvailability.Ready));
        Assert.Equal(UsDetectorAvailability.ValidationFailed, report.Detectors.Single(d => d.DetectorId == "us.phone").Availability);
        Assert.Null(report.ArtifactVersion);                                                                  // only a full production 13/13 names the artifact
        Assert.Equal(UsReadinessKind.Partial, UsReadinessPresenter.Present(report).Kind);
    }

    // ===== the app boundary ====================================================================================================

    [Fact]
    public void TheAppBoundary_BuildsTheRealCompositionFromTheHostsSources_WithoutStartingIt()
    {
        string root = NewRoot();
        var session = PrivonOptInSession.Create(UsOptInSourcesBuilder.BuildProduction, root);
        _disposables.Add(session);
        var composition = session.Composition;

        Assert.NotNull(composition.DetectionPipelineSource);
        Assert.NotNull(composition.UsReadinessSource);
        Assert.Null(composition.Resolver);                                                                    // constructed, never started: no monitor, no listener
        Assert.Null(composition.CountryChoices);                                                              // the service exists only after Start builds the graph ...
        Assert.True((bool)typeof(PrivonAppComposition).GetField("_offerUnitedStatesChoice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composition)!);   // ... and it will
        Assert.Equal(root, (string)typeof(PrivonAppComposition).GetField("_storageRootPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composition)!);
        Assert.Equal(13, composition.UsReadinessSource!().Detectors.Count(d => d.Availability == UsDetectorAvailability.Ready));
        Assert.True(UsHits(composition.DetectionPipelineSource!()) >= 3);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public void TheAppBoundary_UsesTheProductionStorageRoot_WhenNoneIsGiven()
    {
        string? seen = null;
        var session = PrivonOptInSession.Create(r => { seen = r; return UsOptInSourcesBuilder.Build(NewRoot(), null); });
        _disposables.Add(session);

        Assert.Equal(PrivonAppComposition.ProductionStorageRootPath, seen);
    }

    [Fact]
    public void WhenTheHostCannotBuild_NothingIsCreated_AndTheFailureIsNotSwallowed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PrivonOptInSession.Create(_ => throw new InvalidOperationException("synthetic host failure"), NewRoot()));
        Assert.Throws<InvalidOperationException>(() => PrivonOptInSession.Create(_ => null!, NewRoot()));
        Assert.Throws<ArgumentNullException>(() => PrivonOptInSession.Create(null!, NewRoot()));
        Assert.Throws<ArgumentException>(() => PrivonOptInSession.Create(UsOptInSourcesBuilder.BuildProduction, " "));
    }

    [Fact]
    public void WhenTheSourcesAreIncomplete_TheHostsLifetimeIsStillReleased()
    {
        var probe = new Probe(_ => { }, "lifetime");

        Assert.Throws<ArgumentNullException>(() => PrivonOptInSession.Create(
            _ => new UsOptInSources(null!, () => throw new InvalidOperationException(), probe), NewRoot()));

        Assert.Equal(1, probe.Disposed);
    }

    // ===== country choice: Settings event -> store -> the pipeline the host serves ==========================================

    [Fact]
    public void AUserOffInput_ReachesTheStore_AndTheHostPipelineStopsTheUsPack_WhileKoreanAndGlobalStay()
    {
        const string korean = "연락처 010-1234-5678 이메일 user@example.test 901231-1234567 카드 4111 1111 1111 1111 서버 192.168.10.20";
        string Key(DetectionCandidate c) => $"{c.PiiType}|{c.Span}|{c.Canonical?.Value}|{c.RiskLevel}";
        var host = Assemble();
        var pipelineSource = host.Session.Composition.DetectionPipelineSource!;
        Assert.True(UsHits(pipelineSource()) >= 3);                                                           // initial state read from the real store: no record, active
        Assert.Contains("13/13", Line(host));

        host.Settings.RaiseUnitedStatesToggleRequested(false);                                                // THE USER INPUT

        Assert.Equal(CountryChoiceState.Off, host.Store.LoadCountryChoice("UnitedStates").State);
        var off = pipelineSource();
        Assert.Equal(0, UsHits(off));
        Assert.Equal(UsReadinessKind.Off, host.Tray.CurrentUsReadiness!.Kind);                                 // the tray line followed at once
        Assert.Equal(
            DetectionPipeline.CreateDefault().Detect(korean).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList(),
            off.Detect(korean).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList());                    // the shipped nine detectors, unchanged

        host.Settings.RaiseUnitedStatesToggleRequested(true);
        var on = pipelineSource();
        Assert.True(UsHits(on) >= 3);
        Assert.NotSame(off, on);                                                                              // the OFF pipeline is not served again
        Assert.Contains("13/13", Line(host));
    }

    [Fact]
    public void TheInitialState_IsReadFromTheRealStore_AStoredOffIsHonoured_AnUnverifiableOneIsNot()
    {
        string root = NewRoot();
        PrivonLocalStore.OpenOrCreate(root).SaveCountryChoice("UnitedStates", enabled: false, CountryChoiceOrigin.SettingsUserAction);   // seeding a stored choice; not a user input
        var honoured = Assemble(root);
        Assert.Equal(0, UsHits(honoured.Session.Composition.DetectionPipelineSource!()));
        Assert.Equal(UsReadinessKind.Off, honoured.Tray.CurrentUsReadiness!.Kind);

        string tamperedRoot = NewRoot();
        var seed = PrivonLocalStore.OpenOrCreate(tamperedRoot);
        seed.SaveCountryChoice("UnitedStates", enabled: false, CountryChoiceOrigin.SettingsUserAction);
        string file = Path.Combine(tamperedRoot, "country-settings.bin");
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 1;
        File.WriteAllBytes(file, bytes);
        var tampered = Assemble(tamperedRoot);

        Assert.True(UsHits(tampered.Session.Composition.DetectionPipelineSource!()) >= 3);                  // an unauthenticated OFF is NOT applied
        Assert.Contains("13/13", Line(tampered));
    }

    [Fact]
    public void ASaveFailure_KeepsThePreviousStateInThePipelineAndTheTray()
    {
        var host = Assemble();
        host.Settings.RaiseUnitedStatesToggleRequested(true);                                                 // a verified On
        string file = Path.Combine(host.Root, "country-settings.bin");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            host.Settings.RaiseUnitedStatesToggleRequested(false);                                            // cannot be stored
        }

        Assert.Equal(CountryChoiceState.On, host.Store.LoadCountryChoice("UnitedStates").State);
        Assert.True(UsHits(host.Session.Composition.DetectionPipelineSource!()) >= 3);                       // the pack was never turned off
        Assert.Contains("13/13", Line(host));
        Assert.Equal(SettingsCoordinator.CountryChoiceFailureMessage, host.Settings.LastRenderedState!.StatusMessage);
    }

    [Fact]
    public void NoStalePipelineOrReadinessIsReused_AcrossAChangeAndAcrossTheMenuOpening()
    {
        var host = Assemble();
        var first = host.Session.Composition.DetectionPipelineSource!();
        Assert.Same(first, host.Session.Composition.DetectionPipelineSource!());                              // cached only while the setting is unchanged

        host.Settings.RaiseUnitedStatesToggleRequested(false);
        host.Tray.RaiseUsReadinessRefreshRequested();                                                         // the menu opens
        var second = host.Session.Composition.DetectionPipelineSource!();

        Assert.NotSame(first, second);
        Assert.Equal(UsReadinessKind.Off, host.Tray.CurrentUsReadiness!.Kind);
        Assert.DoesNotContain(host.Tray.UsReadinessHistory.Skip(host.Tray.UsReadinessHistory.Count - 2), h => h.MenuText.Contains("13/13"));
    }

    // ===== production authority -> thirteen detectors -> readiness ==============================================================

    [Fact]
    public void TheTrayNumber_IsTheNumberOfDetectorsTheHostPipelineActuallyRuns()
    {
        string[] texts =
        [
            "ATIN: 912-93-4567", "ITIN: 912-70-1234", "SSN: 321-54-9876", "MBI: 1A23C45DE67", "SEVIS: N1234567890", "Passport Number: P12345678 State: NY",
            "A-Number: A1234567", "I-94 Number: 123456789AB", "Driver License Number: D123-4567 State: CA",
            "Routing Number: 120000003 | Account Number: AB12CD34", "Phone: 212-555-1212", "Address: 123 Main ST", "Full Name: Alice O'Neil",
        ];
        string[] ids =
        [
            "us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis", "us.passport", "us.a_number", "us.i94", "us.driver_state_id", "us.bank_account",
            "us.phone", "us.address", "us.name",
        ];
        var variants = new (string Name, UsAuthoritySet? Authority, int Expected, string Label)[]
        {
            ("production", UsAuthoritySet.Production, 13, "Verified Official Sources"),
            ("none", null, 11, "Data not selected"),
            ("fixture", UsAuthoritySet.Load(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid()), 13, "Not Production-Verified"),
            ("phone-only", UsAuthoritySet.LoadProduction(new Defective().Without(UsAuthoritySet.SuffixId).Without(UsAuthoritySet.StateId)), 12, "Data not selected"),
            ("damaged", UsAuthoritySet.LoadProduction(new Defective().Flipped(UsAuthoritySet.NpaId)), 12, "Unavailable (data validation failed)"),
        };

        foreach (var (name, authority, expected, label) in variants)
        {
            var host = Assemble(build: r => UsOptInSourcesBuilder.Build(r, authority));
            var pipeline = host.Session.Composition.DetectionPipelineSource!();
            var serving = ids.Where((id, i) => pipeline.Detect(texts[i]).Candidates.Any(c => c.DetectorId.Value == id)).ToHashSet(StringComparer.Ordinal);

            Assert.True(expected == serving.Count, $"{name}: serving {serving.Count}");
            Assert.Contains($"{expected}/13", Line(host));
            Assert.Contains(label, Line(host) + host.Tray.CurrentUsReadiness!.Detail);
        }
    }

    [Fact]
    public void AFixtureAuthority_IsNeverShownAsProductionVerified_ThroughTheHost()
    {
        var host = Assemble(build: r => UsOptInSourcesBuilder.Build(r, UsAuthoritySet.Load(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid())));

        Assert.Contains("13/13", Line(host));
        Assert.DoesNotContain("Verified Official Sources", Line(host) + host.Tray.CurrentUsReadiness!.Detail);
    }

    [Fact]
    public void AProtectionFailure_KeepsItsWarning_WhileTheHostsReadinessLineIsShown()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        string root = NewRoot();
        var tray = new FakeTrayIconSurface();
        var store = PrivonLocalStore.OpenOrCreate(root);
        var sources = UsOptInSourcesBuilder.BuildProduction(root);
        _disposables.Add(sources.Lifetime!);
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var bridge = new PrivonAppUiBridge(
            new ClipboardDecisionSessionPublisher(lifecycle), lifecycle, new FakeClipboardDecisionResolver(), new FakeDispatcherScheduler { RunSynchronously = true },
            tray, () => new FakeDecisionPromptSurface(), new WindowsAutoStartCoordinator(new FakeWindowsAutoStartRegistration(), () => "PRIVON.exe"),
            new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(), () => false,
            () => new FakeSettingsSurface(), new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            settingsPipelineSource: sources.PipelineSource, protectionStatus: tracker, usReadinessSource: sources.ReadinessSource);
        _disposables.Add(bridge);
        bridge.Start();

        tracker.Record(Privon.App.ClipboardDiagnosticEvent.AppClipboardChangeReceived(1, 1, true, true));
        tracker.Record(Privon.App.ClipboardDiagnosticEvent.AttemptTerminal(1, ClipboardDiagnosticTerminalReason.ProcessingException, "SyntheticException"));
        tray.RaiseUsReadinessRefreshRequested();

        Assert.Equal(Privon.Core.ProtectionState.Error, tray.ProtectionStatusHistory[^1].Presentation.State);
        Assert.Single(tray.ProtectionStatusHistory, h => h.Notify);
        Assert.Contains("13/13", tray.CurrentUsReadiness!.MenuText);
    }

    // ===== lifecycle ==========================================================================================================

    [Fact]
    public void TheSession_StartsInTheShippedOrder_AndTearsDownBridgeThenLifetime()
    {
        var host = Assemble(startNow: false);
        Assert.False(host.Session.IsRunning);

        Assert.True(host.Session.TryStart());
        Assert.True(host.Session.IsRunning);
        host.Session.Dispose();

        var events = _eventLogs[host.Session];
        Assert.Equal(["composition-start"], events.Take(1));
        int bridge = events.IndexOf("bridge-disposed");
        int lifetime = events.FindIndex(e => e.StartsWith("lifetime:", StringComparison.Ordinal));
        Assert.True(bridge >= 0 && lifetime > bridge, string.Join(",", events));                              // the tray goes first, the lifetime after
        Assert.Contains("lifetime:trayDisposed=True", events);
        Assert.False(host.Session.IsRunning);
    }

    [Fact]
    public void ASecondInstance_CannotStart_AndStartsNothing_UntilTheFirstIsGone()
    {
        string mutex = UniqueMutex();
        var first = Assemble(mutex: mutex);
        int secondBridges = 0;
        var second = Assemble(mutex: mutex, startNow: false, onBridge: () => secondBridges++);

        Assert.False(OnOtherThread(() => second.Session.TryStart()));                                          // the mutex is held by the first
        Assert.Equal(0, secondBridges);                                                                        // no composition start, no bridge
        Assert.DoesNotContain("composition-start", _eventLogs[second.Session]);

        first.Session.Dispose();
        var third = Assemble(mutex: mutex, startNow: false);
        Assert.True(OnOtherThread(() => { bool started = third.Session.TryStart(); third.Session.Dispose(); return started; }));   // released on teardown
    }

    [Fact]
    public void AFailureWhileStarting_ReleasesTheMutex_AndTheNextStartCanProceed()
    {
        string mutex = UniqueMutex();
        var failing = Assemble(mutex: mutex, startNow: false, onBridge: () => throw new InvalidOperationException("synthetic bridge failure"));

        Assert.Throws<InvalidOperationException>(() => failing.Session.TryStart());
        Assert.False(failing.Session.IsRunning);

        var next = Assemble(mutex: mutex, startNow: false);
        Assert.True(OnOtherThread(() => { bool started = next.Session.TryStart(); next.Session.Dispose(); return started; }));   // not left held by the failure
    }

    [Fact]
    public void StartingTwiceOrAfterDispose_IsRefused_AndDisposeIsIdempotent()
    {
        var host = Assemble(startNow: false);
        Assert.True(host.Session.TryStart());
        Assert.Throws<InvalidOperationException>(() => host.Session.TryStart());

        host.Session.Dispose();
        host.Session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => host.Session.TryStart());
        Assert.Equal(1, _eventLogs[host.Session].Count(e => e == "bridge-disposed"));                           // torn down once, not twice
        Assert.Equal(1, _eventLogs[host.Session].Count(e => e.StartsWith("lifetime:", StringComparison.Ordinal)));   // and the host lifetime is released once
    }

    [Fact]
    public void AfterTheSessionIsDisposed_TheHostServesNeitherAPipelineNorAReadiness()
    {
        var host = Assemble();
        var composition = host.Session.Composition;
        host.Session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => composition.DetectionPipelineSource!());
        Assert.Throws<ObjectDisposedException>(() => composition.UsReadinessSource!());
    }

    [Fact]
    public void TheSessionUsesTheShippedSingleInstanceMutex_SoItNeverRunsBesideTheShippedApp()
    {
        var shipped = (string)typeof(App).GetField("SingleInstanceMutexName", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetRawConstantValue()!;

        Assert.Equal(shipped, PrivonOptInSession.SingleInstanceMutexName);
    }

    // ===== the shipped application is unchanged =================================================================================

    [Fact]
    public void TheShippedStartupAndPackaging_NameNothingOfTheHost_OrTheSession()
    {
        string root = RepoRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "Privon.App", "App.xaml.cs"));
        string appProject = File.ReadAllText(Path.Combine(root, "src", "Privon.App", "Privon.App.csproj"));
        string solution = File.ReadAllText(Path.Combine(root, "PRIVON.slnx"));
        string release = File.ReadAllText(Path.Combine(root, "tools", "publish-release.ps1"));

        foreach (string token in new[] { "UsOptInHost", "PrivonOptInSession", "UsOptInSources", "CreateWithUsOptIn" })
        {
            Assert.DoesNotContain(token, app);
            Assert.DoesNotContain(token, appProject);
            Assert.DoesNotContain(token, solution);
            Assert.DoesNotContain(token, release);
        }

        Assert.Contains("PrivonAppComposition.CreateProduction()", app);
        Assert.Contains("*Experiment*", release);                       // the release package is refused if anything named after an experiment is staged
        Assert.Contains("*OptInHost*", release);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".csproj")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                 && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            Assert.DoesNotContain("Privon.UsOptInHost", File.ReadAllText(file));
        }
    }

    [Fact]
    public void TheHostProject_ReferencesOnlyTheAppAndTheProductIntegration_AndNothingReferencesTheHostButItsTests()
    {
        string root = RepoRoot();
        string hostProject = File.ReadAllText(Path.Combine(root, "tools", "Privon.UsOptInHost", "Privon.UsOptInHost.csproj"));
        var references = System.Text.RegularExpressions.Regex.Matches(hostProject, "ProjectReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace('\\', '/')).ToList();

        // the host builds on the SAME product implementation the shipped app runs (no second copy of the detectors)
        Assert.Equal(["../../src/Privon.App/Privon.App.csproj", "../../src/Privon.UsProductIntegration/Privon.UsProductIntegration.csproj"], references);

        var referencing = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("Privon.UsOptInHost.csproj", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.Equal(["Privon.App.Tests.csproj"], referencing);
        Assert.DoesNotContain(typeof(PrivonAppComposition).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Privon.Us", StringComparison.Ordinal) && a.Name != "Privon.UsProductIntegration");
        Assert.DoesNotContain(typeof(UsOptInOwner).Assembly.GetReferencedAssemblies(), a => a.Name == "Privon.App");
    }

    [Fact]
    public void TheAppsPublicSurface_GrewByExactlyTheBoundaryTheReadinessReportAndItsAdapter()
    {
        var exported = typeof(PrivonAppComposition).Assembly.GetExportedTypes().Select(t => t.FullName!).Order(StringComparer.Ordinal).ToList();

        // the six public types that existed before the host, plus exactly the boundary, the readiness report and its one mapping
        var expected = new[]
        {
            "Privon.App.App", "Privon.App.UsOptInSources", "Privon.App.NativeMessagingHostInvocation", "Privon.App.NativeMessagingInvocationKind",
            "Privon.App.PrivonEntryPoint", "Privon.App.PrivonOptInSession", "Privon.App.Program", "Privon.App.UsAuthorityProvenance",
            "Privon.App.UsDetectorAvailability", "Privon.App.UsDetectorReadiness", "Privon.App.UsReadinessAdapter", "Privon.App.UsReadinessReport",
            "Privon.App.WebExtensionOriginAllowlist", "Privon.App.WebPipeEndpoint",
        }.Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, exported.Where(n => n.StartsWith("Privon.App.", StringComparison.Ordinal) && !n.Contains('+')).ToList());
        Assert.NotNull(typeof(PrivonAppComposition).GetConstructor(Type.EmptyTypes));                           // the plain composition is still built by a parameterless constructor
    }

    [Fact]
    public void KoreanAndGlobalDetection_ThroughTheHostPipeline_IsTheShippedOne_WhetherTheUsPackIsOnOrOff()
    {
        const string korean = "연락처 010-1234-5678 이메일 user@example.test 901231-1234567 카드 4111 1111 1111 1111 서버 192.168.10.20";
        string Key(DetectionCandidate c) => $"{c.PiiType}|{c.Span}|{c.Canonical?.Value}|{c.RiskLevel}";
        var expected = DetectionPipeline.CreateDefault().Detect(korean).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();
        var host = Assemble();

        var on = host.Session.Composition.DetectionPipelineSource!().Detect(korean).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();
        host.Settings.RaiseUnitedStatesToggleRequested(false);
        var off = host.Session.Composition.DetectionPipelineSource!().Detect(korean).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();

        Assert.Equal(expected, on);
        Assert.Equal(expected, off);
    }
}
