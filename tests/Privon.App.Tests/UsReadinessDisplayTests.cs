using System.Reflection;
using System.Security.Cryptography;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Authority;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The U.S. detector READINESS the user can read in the tray menu, through the existing path (the explicit opt-in's source ->
// PrivonAppUiBridge on the UI scheduler -> the tray surface). It is a statement about what can run, never about protection or sending: the product
// blocks neither, and nothing here says otherwise. Ready is not Verified. The real WinForms NotifyIcon is not shown on screen here; its menu objects
// are exercised on an STA thread only.
public sealed class UsReadinessDisplayTests : IDisposable
{
    private readonly List<string> _roots = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var r in _roots)
        {
            try { Directory.Delete(r, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(), new GpsCoordinateDetector(),
    ];

    // ---- what the future opt-in composition root would do: read the real authority / setting and state the readiness --------------------

    private static UsReadinessReport ReportOf(UsAuthoritySet authority, CountrySettingObservation setting)
    {
        UsDetectorAvailability Availability(DetectorId id, DetectorReadinessState readiness)
        {
            if (readiness == DetectorReadinessState.Ready) return UsDetectorAvailability.Ready;
            var states = id.Value == "us.phone"
                ? new[] { authority.NpaState }
                : new[] { authority.SuffixState, authority.StateState };
            bool onlyMissing = states.All(s => s == AuthoritySnapshotState.SnapshotMissing);
            bool anyBad = states.Any(s => s is not (AuthoritySnapshotState.SnapshotMissing or AuthoritySnapshotState.SnapshotValid));
            bool validButUnfit = states.All(s => s != AuthoritySnapshotState.SnapshotMissing);   // valid data that the scope check refused
            return onlyMissing || (!anyBad && !validButUnfit) ? UsDetectorAvailability.NotProvided : UsDetectorAvailability.ValidationFailed;
        }

        var detectors = UsIntegrationCandidates.ReadinessReport(authority)
            .Select(r => new UsDetectorReadiness(r.Id.Value, Availability(r.Id, r.Readiness))).ToList();
        var provenance = authority.Origin switch
        {
            UsAuthorityOrigin.ProductionScope => UsAuthorityProvenance.Production,
            UsAuthorityOrigin.Provided => UsAuthorityProvenance.Provided,
            _ => UsAuthorityProvenance.NotSelected,
        };
        return new UsReadinessReport(setting != CountrySettingObservation.AuthenticatedOff, provenance, detectors,
            authority.Origin == UsAuthorityOrigin.ProductionScope && detectors.All(d => d.Availability == UsDetectorAvailability.Ready)
                ? new UsProductionAuthorityProvider().ArtifactVersion : null);
    }

    private sealed class Live
    {
        public UsAuthoritySet Authority { get; set; } = UsAuthoritySet.Unavailable;
        public CountrySettingObservation Setting { get; set; } = CountrySettingObservation.Missing;
        public Exception? ReportThrows { get; set; }
        public Exception? PipelineThrows { get; set; }
        public int ReportReads;

        public DetectionPipeline Pipeline()
        {
            if (PipelineThrows is { } e) throw e;
            return UsOptInPipeline.Create(Legacy(), unitedStatesSetting: Setting, authority: Authority);
        }

        public UsReadinessReport Report()
        {
            Interlocked.Increment(ref ReportReads);
            if (ReportThrows is { } e) throw e;
            return ReportOf(Authority, Setting);
        }
    }

    private (PrivonAppUiBridge Bridge, FakeTrayIconSurface Tray, FakeDispatcherScheduler Scheduler) Wire(
        Live? live, ClipboardProtectionStatusTracker? tracker = null, bool synchronous = true, bool withReadiness = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "UsReadinessDisplay", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
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
            settingsPipelineSource: live is null ? null : live.Pipeline,
            protectionStatus: tracker,
            usReadinessSource: withReadiness && live is not null ? live.Report : null);
        _disposables.Add(bridge);
        return (bridge, tray, scheduler);
    }

    private static string Menu(FakeTrayIconSurface tray) => Assert.IsType<UsReadinessPresentation>(tray.CurrentUsReadiness).MenuText;

    private static string Everything(UsReadinessPresentation p) => p.MenuText + " | " + p.Detail;

    // ---- a provider that serves the real production bytes with one deliberate defect ----------------------------------------------------

    private sealed class Defective : IAuthoritySnapshotProvider
    {
        private readonly UsProductionAuthorityProvider _real = new();
        private readonly HashSet<string> _missing = [];
        private readonly HashSet<string> _flipped = [];
        private readonly HashSet<string> _badHash = [];

        public Defective Without(AuthoritySnapshotId id) { _missing.Add(id.Value); return this; }
        public Defective Flipped(AuthoritySnapshotId id) { _flipped.Add(id.Value); return this; }
        public Defective BadHash(AuthoritySnapshotId id) { _badHash.Add(id.Value); return this; }

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
            metadata = _badHash.Contains(id.Value) ? real with { ExpectedSha256Hex = new string('0', 64) } : real;
            return true;
        }
    }

    // ===== the states ============================================================================================================

    [Fact]
    public void WithoutAnAuthority_ElevenOfThirteenAreReady_AndPhoneAndAddressAreNamedAsNotReady()
    {
        var live = new Live();
        var (bridge, tray, _) = Wire(live);

        bridge.Start();

        var p = Assert.IsType<UsReadinessPresentation>(tray.CurrentUsReadiness);
        Assert.Equal(UsReadinessKind.Partial, p.Kind);
        Assert.Contains("11/13", p.MenuText);
        Assert.Contains("Phone", p.MenuText);
        Assert.Contains("Address", p.MenuText);
        Assert.Contains("Data not selected", p.MenuText);
        Assert.DoesNotContain("validation failed", p.MenuText);
    }

    [Fact]
    public void TheVerifiedProductionAuthority_ShowsThirteenOfThirteen_AsProductionVerified()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var (bridge, tray, _) = Wire(live);

        bridge.Start();

        var p = Assert.IsType<UsReadinessPresentation>(tray.CurrentUsReadiness);
        Assert.Equal(UsReadinessKind.AllReady, p.Kind);
        Assert.Contains("13/13", p.MenuText);
        Assert.Contains("Verified Official Sources", p.MenuText);
        Assert.Contains("us-authority/2026-11-02.1", p.Detail);               // the artifact the claim rests on
    }

    [Fact]
    public void ASyntheticFixtureThatMakesAllThirteenReady_IsNeverShownAsProductionVerified()
    {
        var live = new Live { Authority = UsAuthoritySet.Load(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid()) };
        var (bridge, tray, _) = Wire(live);

        bridge.Start();

        var p = Assert.IsType<UsReadinessPresentation>(tray.CurrentUsReadiness);
        Assert.Equal(UsReadinessKind.AllReady, p.Kind);                       // the detectors can run ...
        Assert.Contains("13/13", p.MenuText);
        Assert.Contains("Not Production-Verified", p.MenuText);                          // ... and the text says the data is not production-verified
        Assert.DoesNotContain("Verified Official Sources", Everything(p));
        Assert.DoesNotContain("us-authority/", Everything(p));

        // the same real production bytes loaded WITHOUT the production scope check are Provided, not Production
        live.Authority = UsAuthoritySet.Load(new UsProductionAuthorityProvider());
        bridge.NotifyUsReadinessChanged();
        Assert.DoesNotContain("Verified Official Sources", Everything(tray.CurrentUsReadiness!));
    }

    [Theory]
    [InlineData("phone-only")]
    [InlineData("address-only")]
    public void AnAuthorityThatBacksOnlyOneDetector_ReadiesOnlyThat_AndNamesTheOther(string which)
    {
        var provider = which == "phone-only"
            ? new Defective().Without(UsAuthoritySet.SuffixId).Without(UsAuthoritySet.StateId)
            : new Defective().Without(UsAuthoritySet.NpaId);
        var live = new Live { Authority = UsAuthoritySet.LoadProduction(provider) };
        var (bridge, tray, _) = Wire(live);

        bridge.Start();

        var p = Assert.IsType<UsReadinessPresentation>(tray.CurrentUsReadiness);
        Assert.Equal(UsReadinessKind.Partial, p.Kind);
        Assert.Contains("12/13", p.MenuText);
        Assert.Contains(which == "phone-only" ? "Address" : "Phone", p.MenuText);
        Assert.DoesNotContain(which == "phone-only" ? "Phone" : "Address", p.MenuText);
    }

    [Theory]
    [InlineData("hash-in-metadata", "us.npa")]
    [InlineData("flipped-byte", "us.npa")]
    [InlineData("hash-in-metadata", "usps.suffix")]
    [InlineData("flipped-byte", "usps.state")]
    public void AnAuthorityThatFailsValidation_MakesItsDetectorUnusable_NotMerelyUnselected(string defect, string snapshot)
    {
        AuthoritySnapshotId.TryCreate(snapshot, out var id);
        var provider = defect == "hash-in-metadata" ? new Defective().BadHash(id) : new Defective().Flipped(id);
        var live = new Live { Authority = UsAuthoritySet.LoadProduction(provider) };
        var (bridge, tray, _) = Wire(live);

        bridge.Start();

        var p = Assert.IsType<UsReadinessPresentation>(tray.CurrentUsReadiness);
        Assert.Equal(UsReadinessKind.Partial, p.Kind);
        Assert.Contains("12/13", p.MenuText);
        Assert.Contains("Unavailable (data validation failed)", p.MenuText);
        Assert.Contains(snapshot == "us.npa" ? "Phone" : "Address", p.MenuText);
        Assert.DoesNotContain("Data not selected", p.MenuText);
    }

    [Fact]
    public void ATooSmallButValidAuthority_IsNotUsedAndIsShownAsUnusable()
    {
        // the four-entry D3 fixtures validate by shape but are not the production scope: the production load refuses them
        var live = new Live { Authority = UsAuthoritySet.LoadProduction(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid()) };
        var (bridge, tray, _) = Wire(live);

        bridge.Start();

        var p = Assert.IsType<UsReadinessPresentation>(tray.CurrentUsReadiness);
        Assert.Contains("11/13", p.MenuText);
        Assert.Contains("Unavailable (data validation failed)", p.MenuText);
        Assert.DoesNotContain("13/13", p.MenuText);
    }

    [Fact]
    public void WhenTheUsSettingIsOff_TheLineSaysUsIsOffAndKoreanAndGlobalRunSeparately()
    {
        var live = new Live { Authority = UsAuthoritySet.Production, Setting = CountrySettingObservation.AuthenticatedOff };
        var (bridge, tray, _) = Wire(live);

        bridge.Start();

        var p = Assert.IsType<UsReadinessPresentation>(tray.CurrentUsReadiness);
        Assert.Equal(UsReadinessKind.Off, p.Kind);
        Assert.Contains("Off", p.MenuText);
        Assert.Contains("Korean and Global", p.MenuText);
        Assert.DoesNotContain("13/13", Everything(p));
    }

    [Theory]
    [InlineData(CountrySettingObservation.Missing)]
    [InlineData(CountrySettingObservation.Corrupt)]
    [InlineData(CountrySettingObservation.Unauthenticated)]
    [InlineData(CountrySettingObservation.UnknownVersion)]
    [InlineData(CountrySettingObservation.AuthenticatedOn)]
    public void AnyUncertainOrOnSetting_KeepsTheUsDetectorsAndTheirReadiness(CountrySettingObservation setting)
    {
        var live = new Live { Authority = UsAuthoritySet.Production, Setting = setting };
        var (bridge, tray, _) = Wire(live);

        bridge.Start();

        Assert.Contains("13/13", Menu(tray));       // an unverifiable setting is not turned into "off", and nothing here claims it was authenticated
    }

    // ===== the text never claims protection =====================================================================================

    [Fact]
    public void EveryPresentation_SaysReadyIsNotProtection_AndNeverClaimsSafetyOrBlocking()
    {
        var states = new List<UsReadinessPresentation>
        {
            UsReadinessPresenter.Unknown(),
            UsReadinessPresenter.Present(ReportOf(UsAuthoritySet.Unavailable, CountrySettingObservation.Missing)),
            UsReadinessPresenter.Present(ReportOf(UsAuthoritySet.Production, CountrySettingObservation.Missing)),
            UsReadinessPresenter.Present(ReportOf(UsAuthoritySet.Load(new UsProductionAuthorityProvider()), CountrySettingObservation.Missing)),
            UsReadinessPresenter.Present(ReportOf(UsAuthoritySet.Production, CountrySettingObservation.AuthenticatedOff)),
        };

        Assert.All(states, p =>
        {
            Assert.Contains("does not guarantee that sending is safe", p.Detail);
            Assert.Contains("does not currently block sending", p.Detail);
            Assert.Contains("Verified", p.Detail);
            Assert.DoesNotContain("is protected", Everything(p));
            Assert.DoesNotContain("Protecting", Everything(p));
            Assert.DoesNotContain("content is safe", Everything(p));
            Assert.DoesNotContain("was blocked", Everything(p));
            Assert.DoesNotContain("has blocked", Everything(p));
        });
    }

    [Fact]
    public void ThePresentationAndTheReport_CarryNoClipboardText()
    {
        var report = ReportOf(UsAuthoritySet.Production, CountrySettingObservation.Missing);
        var all = report.ToString() + string.Join("", report.Detectors.Select(d => d.ToString())) + Everything(UsReadinessPresenter.Present(report));

        Assert.DoesNotContain("Alice", all);
        Assert.DoesNotContain("321-54", all);
        Assert.All(report.Detectors, d => Assert.Matches("^[a-z_0-9]+\\.[a-z_0-9]+$", d.DetectorId));
    }

    // ===== freshness =============================================================================================================

    [Fact]
    public void WhenTheReadinessCheckFails_TheOldThirteenOfThirteenDoesNotStay()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var (bridge, tray, _) = Wire(live);
        bridge.Start();
        Assert.Contains("13/13", Menu(tray));

        live.ReportThrows = new InvalidOperationException("synthetic readiness failure");
        bridge.NotifyUsReadinessChanged();

        var p = Assert.IsType<UsReadinessPresentation>(tray.CurrentUsReadiness);
        Assert.Equal(UsReadinessKind.Unknown, p.Kind);
        Assert.Contains("Readiness Unknown", p.MenuText);
        Assert.DoesNotContain("13/13", Everything(p));
        Assert.DoesNotContain("synthetic", Everything(p));                  // the failure's own text is never shown
    }

    [Fact]
    public void WhenThePipelineCannotBeRebuilt_TheLineIsUnknown_NotTheLastKnownReady()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var (bridge, tray, _) = Wire(live);
        bridge.Start();

        live.PipelineThrows = new InvalidOperationException("synthetic rebuild failure");
        bridge.NotifyUsReadinessChanged();

        Assert.Equal(UsReadinessKind.Unknown, tray.CurrentUsReadiness!.Kind);

        live.PipelineThrows = null;                                          // and it recovers by itself once the pipeline builds again
        bridge.NotifyUsReadinessChanged();
        Assert.Contains("13/13", Menu(tray));
    }

    [Fact]
    public void WhenTheAuthorityOrTheCountryScopeChanges_TheNextRenderShowsTheNewStateOnly()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var (bridge, tray, _) = Wire(live);
        bridge.Start();
        Assert.Contains("13/13", Menu(tray));

        live.Authority = UsAuthoritySet.LoadProduction(new Defective().Flipped(UsAuthoritySet.NpaId));   // the data changed under it
        bridge.NotifyUsReadinessChanged();
        Assert.Contains("12/13", Menu(tray));

        live.Setting = CountrySettingObservation.AuthenticatedOff;                                        // the scope changed
        bridge.NotifyUsReadinessChanged();
        Assert.Equal(UsReadinessKind.Off, tray.CurrentUsReadiness!.Kind);

        live.Setting = CountrySettingObservation.Missing;
        live.Authority = UsAuthoritySet.Unavailable;
        bridge.NotifyUsReadinessChanged();
        Assert.Contains("11/13", Menu(tray));
    }

    [Fact]
    public void ALateRender_ShowsTheNewestState_NotTheOneThatTriggeredIt()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var (bridge, tray, scheduler) = Wire(live, synchronous: false);
        bridge.Start();                                                      // the first render is queued, not yet run
        live.Authority = UsAuthoritySet.Unavailable;                         // the state changes while the UI is busy
        bridge.NotifyUsReadinessChanged();
        live.ReportThrows = new InvalidOperationException("later still");
        bridge.NotifyUsReadinessChanged();

        scheduler.RunAllPending();

        Assert.NotEmpty(tray.UsReadinessHistory);
        Assert.DoesNotContain(tray.UsReadinessHistory, h => h.MenuText.Contains("13/13"));   // the 13/13 that existed at queue time is never shown
        Assert.Equal(UsReadinessKind.Unknown, tray.CurrentUsReadiness!.Kind);               // the newest fact wins
    }

    [Fact]
    public void WhenTheUserOpensTheMenu_TheLineIsReReadAtThatMoment()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var (bridge, tray, _) = Wire(live);
        bridge.Start();
        int readsAfterStart = live.ReportReads;
        live.Authority = UsAuthoritySet.Unavailable;                         // nobody called NotifyUsReadinessChanged

        tray.RaiseUsReadinessRefreshRequested();                             // the menu opens

        Assert.True(live.ReportReads > readsAfterStart);
        Assert.Contains("11/13", Menu(tray));
    }

    [Fact]
    public void EveryProtectionStatusChange_AlsoRefreshesTheLine()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var (bridge, tray, _) = Wire(live, tracker);
        bridge.Start();
        live.Authority = UsAuthoritySet.Unavailable;

        tracker.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(1, 1, true, true));

        Assert.Contains("11/13", Menu(tray));
    }

    [Fact]
    public void ManyConcurrentChanges_EndWithTheFinalState()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var (bridge, tray, scheduler) = Wire(live, synchronous: false);
        bridge.Start();

        // The state changes race freely; only the hand-off to the fake scheduler is serialized, because FakeDispatcherScheduler queues into a
        // plain Queue (the real WPF dispatcher is thread-safe).
        var postGate = new object();
        Parallel.For(0, 200, i =>
        {
            live.Authority = i % 2 == 0 ? UsAuthoritySet.Production : UsAuthoritySet.Unavailable;
            lock (postGate) bridge.NotifyUsReadinessChanged();
        });
        live.Authority = UsAuthoritySet.Unavailable;                         // the last fact
        bridge.NotifyUsReadinessChanged();
        scheduler.RunAllPending();

        Assert.Contains("11/13", Menu(tray));
    }

    [Fact]
    public void AfterDispose_ACallbackOrANotificationTouchesNothing_AndThrowsNothing()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var (bridge, tray, scheduler) = Wire(live, synchronous: false);
        bridge.Start();
        scheduler.RunAllPending();
        int shown = tray.UsReadinessHistory.Count;
        bridge.NotifyUsReadinessChanged();                                   // queued, then the bridge goes away
        bridge.Dispose();

        scheduler.RunAllPending();
        bridge.NotifyUsReadinessChanged();
        tray.RaiseUsReadinessRefreshRequested();
        scheduler.RunAllPending();

        Assert.Equal(shown, tray.UsReadinessHistory.Count);
        Assert.Equal(0, tray.UsReadinessRefreshSubscriberCount);             // and the tray no longer calls back into it
    }

    [Fact]
    public void ATrayThatCannotShowTheLine_DoesNotBreakTheBridge()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var (bridge, tray, _) = Wire(live);
        tray.ThrowOnSetUsReadiness = new InvalidOperationException("tray gone");

        bridge.Start();
        bridge.NotifyUsReadinessChanged();

        Assert.Equal(1, tray.ShowCallCount);
    }

    // ===== priority: the protection status keeps the tray ========================================================================

    [Fact]
    public void AProtectionFailure_KeepsItsTooltipAndItsOnlyBalloon_TheReadinessLineAddsNeitherAndNeverReplacesThem()
    {
        var live = new Live { Authority = UsAuthoritySet.Production };
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var (bridge, tray, _) = Wire(live, tracker);
        bridge.Start();

        tracker.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(1, 1, true, true));
        tracker.Record(ClipboardDiagnosticEvent.AttemptTerminal(1, ClipboardDiagnosticTerminalReason.ProcessingException, "SyntheticException"));
        int statusCalls = tray.ProtectionStatusHistory.Count;
        live.Authority = UsAuthoritySet.Unavailable;
        bridge.NotifyUsReadinessChanged();
        tray.RaiseUsReadinessRefreshRequested();

        var last = tray.ProtectionStatusHistory[^1];
        Assert.Equal(ProtectionState.Error, last.Presentation.State);
        Assert.True(last.Presentation.IsWarning);
        Assert.Single(tray.ProtectionStatusHistory, h => h.Notify);          // exactly one warning balloon, as before
        Assert.Equal(statusCalls, tray.ProtectionStatusHistory.Count);       // readiness renders did not touch the protection status surface
        Assert.Contains("11/13", Menu(tray));
        Assert.All(tray.UsReadinessHistory, p => Assert.DoesNotContain("remain on the clipboard", Everything(p)));
    }

    [Fact]
    public void TheReadinessLine_NeverCarriesTheWarningWording_AndIsNotAWarning()
    {
        foreach (var report in new[]
                 {
                     ReportOf(UsAuthoritySet.Unavailable, CountrySettingObservation.Missing),
                     ReportOf(UsAuthoritySet.Production, CountrySettingObservation.Missing),
                 })
        {
            var p = UsReadinessPresenter.Present(report);
            Assert.DoesNotContain(ProtectionStatusPresenter.UnconfirmedWarning, p.Detail);
            Assert.True(p.MenuText.Length > 0);
        }
    }

    // ===== the default product is unchanged ======================================================================================

    [Fact]
    public void WithoutAReadinessSource_NothingIsShown_EvenWithAnOptInPipeline_AndNothingIsInferred()
    {
        var live = new Live();
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var (bridge, tray, scheduler) = Wire(live, tracker, withReadiness: false);

        bridge.Start();
        tracker.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(1, 1, true, true));
        bridge.NotifyUsReadinessChanged();
        scheduler.RunAllPending();

        Assert.Empty(tray.UsReadinessHistory);
        Assert.Equal(0, tray.UsReadinessRefreshSubscriberCount);
        Assert.Equal(0, live.ReportReads);
    }

    [Fact]
    public void TheReadinessSourceComesOnlyFromTheGatedOwner_AndNoSrcFileNamesAToolsOrExperimentAssembly()
    {
        string root = RepoRoot();
        string composition = File.ReadAllText(Path.Combine(root, "src", "Privon.App", "PrivonAppComposition.cs"));
        string bridge = File.ReadAllText(Path.Combine(root, "src", "Privon.App", "PrivonAppUiBridge.cs"));
        string tray = File.ReadAllText(Path.Combine(root, "src", "Privon.App", "WinFormsTrayIconSurface.cs"));

        Assert.Contains("composition.UsReadinessSource", bridge);
        Assert.Contains("if (_usReadinessSource is null", bridge);
        Assert.Contains("private readonly Func<UsReadinessReport>? _usReadinessSource;", composition);   // readonly: set once by the constructor, null for a plain composition
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".csproj")) && !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
        {
            string text = File.ReadAllText(file);
            string relative = Path.GetRelativePath(Path.Combine(root, "src"), file).Replace('\\', '/');
            string project = relative[..relative.IndexOf('/')];

            Assert.DoesNotContain("Experiment", text);                       // no product name, namespace, type or string carries the experiment name
            Assert.DoesNotContain("UsOptInHost", text);                      // the experimental host is never named from src
            Assert.DoesNotContain("..\\..\\tools", text);                     // and no src project reaches into tools
            if (project is not ("Privon.App" or "Privon.UsProductIntegration" or "Privon.UsSsn" or "Privon.UsMbiSevis"))
            {
                Assert.DoesNotContain("Privon.UsProductIntegration", text);  // only the app, the product assembly and the two cores (InternalsVisibleTo) name it
            }

            if (project is not ("Privon.UsProductIntegration" or "Privon.UsSsn" or "Privon.UsMbiSevis"))
            {
                Assert.DoesNotContain("Privon.UsSsn", text);                 // the two cores are reached only through the product assembly
                Assert.DoesNotContain("Privon.UsMbiSevis", text);
            }
        }

        Assert.Contains("Visible = false", tray);                                                  // hidden until a presentation is set
        string setter = tray[tray.IndexOf("public void SetUsReadiness", StringComparison.Ordinal)..tray.IndexOf("private void ShowUsReadinessDetail", StringComparison.Ordinal)];
        Assert.DoesNotContain("ShowBalloonTip", setter);                                           // the line never raises a balloon ...
        Assert.DoesNotContain("_icon.Text", setter);                                                // ... and never changes the protection tooltip
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.App", "UsReadiness.cs"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void ThePresenterRejectsAnInconsistentReport_AsUnknown()
    {
        var good = ReportOf(UsAuthoritySet.Production, CountrySettingObservation.Missing);

        Assert.Equal(UsReadinessKind.Unknown, UsReadinessPresenter.Present(null).Kind);
        Assert.Equal(UsReadinessKind.Unknown, UsReadinessPresenter.Present(good with { Detectors = good.Detectors.Take(12).ToList() }).Kind);
        Assert.Equal(UsReadinessKind.Unknown, UsReadinessPresenter.Present(good with { Detectors = good.Detectors.Append(good.Detectors[0]).ToList() }).Kind);
        Assert.Equal(UsReadinessKind.Unknown, UsReadinessPresenter.Present(good with { Detectors = good.Detectors.Select(d => d with { DetectorId = "us.name" }).ToList() }).Kind);
        // thirteen "ready" with no authority at all is a contradiction, not 13/13
        Assert.Equal(UsReadinessKind.Unknown, UsReadinessPresenter.Present(good with { Provenance = UsAuthorityProvenance.NotSelected }).Kind);
        Assert.Equal(UsReadinessKind.Unknown, UsReadinessPresenter.Present(good with { Provenance = (UsAuthorityProvenance)99 }).Kind);
        Assert.Equal(UsReadinessKind.Unknown,
            UsReadinessPresenter.Present(good with { Detectors = good.Detectors.Select(d => d with { Availability = (UsDetectorAvailability)42 }).ToList() }).Kind);
    }

    // ===== the real WinForms tray objects (on an STA thread, never shown) =======================================================

    [Fact]
    public void TheRealTrayMenu_HidesTheLineByDefault_ShowsItWhenSet_AndNeverTouchesTheTooltip()
    {
        (bool hiddenByDefault, string text, bool visibleAfter, string tooltipBefore, string tooltipAfter, bool separatorVisible, bool refreshRaised) outcome = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var tray = new WinFormsTrayIconSurface();
                object item = Field(tray, "_usReadinessItem");
                bool hidden = !(bool)item.GetType().GetProperty("Available")!.GetValue(item)!;
                object notifyIcon = Field(tray, "_icon");
                string before = (string)notifyIcon.GetType().GetProperty("Text")!.GetValue(notifyIcon)!;

                bool raised = false;
                tray.UsReadinessRefreshRequested += (_, _) => raised = true;
                object menu = Field(tray, "_menu");
                var onOpening = menu.GetType().GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                onOpening?.Invoke(menu, [new System.ComponentModel.CancelEventArgs()]);

                tray.SetUsReadiness(UsReadinessPresenter.Present(ReportOf(UsAuthoritySet.Production, CountrySettingObservation.Missing)));
                string after = (string)notifyIcon.GetType().GetProperty("Text")!.GetValue(notifyIcon)!;
                object sep = Field(tray, "_usReadinessSeparator");
                outcome = (hidden, (string)item.GetType().GetProperty("Text")!.GetValue(item)!,
                    (bool)item.GetType().GetProperty("Available")!.GetValue(item)!, before, after,
                    (bool)sep.GetType().GetProperty("Available")!.GetValue(sep)!, raised);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        Assert.Null(failure);
        Assert.True(outcome.hiddenByDefault);
        Assert.True(outcome.visibleAfter);
        Assert.True(outcome.separatorVisible);
        Assert.Contains("13/13", outcome.text);
        Assert.Equal(outcome.tooltipBefore, outcome.tooltipAfter);          // the protection tooltip is untouched
        Assert.True(outcome.refreshRaised);                                 // opening the menu asks for a fresh read

        static object Field(object o, string name) =>
            o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
    }
}
