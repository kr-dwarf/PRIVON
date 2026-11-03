using System.Reflection;
using System.Text;
using Privon.App;
using Privon.Detection;
using Privon.Detection.Authority;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The path from a user's United States ON/OFF input to the store and back to the screen:
//   surface event -> SettingsCoordinator -> CountryChoiceService -> PrivonLocalStore.SaveCountryChoice -> LoadCountryChoice (verified) -> RenderState.
//
// What is real here: the coordinator, the service, the store, and (on an STA thread, never shown) the WPF SettingsWindow's own CheckBox and Click handling.
// What is simulated: the user. A test raises the surface event the way the window does, or invokes the CheckBox's own OnClick; there is no mouse and no
// person. So these tests show that a user INPUT on this control is what stores a choice -- never that a person chose, which nothing here can establish.
// Tests never call SaveCountryChoice themselves except where a comment says they are seeding unrelated state.
public sealed class CountryChoiceUiPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CountryChoiceUi", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string CountryFile => Path.Combine(_root, "country-settings.bin");

    private sealed class Rig
    {
        public required SettingsCoordinator Coordinator { get; init; }
        public required FakeSettingsSurface Surface { get; init; }
        public required PrivonLocalStore Store { get; init; }
        public int Changed;
    }

    private Rig Open(bool offer, Func<bool>? masterKeyUnavailable = null, PrivonLocalStore? store = null)
    {
        store ??= PrivonLocalStore.OpenOrCreate(_root);
        var surface = new FakeSettingsSurface();
        var coordinator = new SettingsCoordinator(
            new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(),
            masterKeyUnavailable ?? (() => store.IsMasterKeyUnavailable), () => surface,
            new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            countryChoices: offer ? new CountryChoiceService(store) : null);
        _disposables.Add(coordinator);
        var rig = new Rig { Coordinator = coordinator, Surface = surface, Store = store };
        coordinator.CountryChoiceChanged += (_, _) => Interlocked.Increment(ref rig.Changed);
        coordinator.ShowRequested();
        return rig;
    }

    private static UnitedStatesChoiceView View(Rig rig) => Assert.IsType<UnitedStatesChoiceView>(rig.Surface.LastRenderedState!.UnitedStatesChoice);

    // ===== shipped: nothing is offered, nothing can be stored ===================================================================

    [Fact]
    public void WithoutTheCapability_TheControlIsNotOffered_TheEventIsNotEvenSubscribed_AndNothingCanBeStored()
    {
        var rig = Open(offer: false);

        Assert.Null(rig.Surface.LastRenderedState!.UnitedStatesChoice);                  // the surface is told to show no control
        Assert.Equal(0, rig.Surface.UnitedStatesToggleSubscriberCount);                  // and no handler listens

        rig.Surface.RaiseUnitedStatesToggleRequested(false);                              // even a surface that raises it anyway reaches nothing
        rig.Surface.RaisePhoneToggleRequested(false);                                     // other settings still render, still without the control

        Assert.False(File.Exists(CountryFile));
        Assert.Equal(CountryChoiceState.Missing, rig.Store.LoadCountryChoice("UnitedStates").State);
        Assert.Null(rig.Surface.LastRenderedState!.UnitedStatesChoice);
        Assert.Equal(0, rig.Changed);
    }

    // ===== opened with the capability: showing and re-rendering never write ======================================================

    [Fact]
    public void ShowingAndReRendering_NeverWrite_AndTheControlStartsOnTheSafeDefault()
    {
        var rig = Open(offer: true);
        rig.Surface.RaisePhoneToggleRequested(false);                                     // unrelated mutations re-render the window
        rig.Surface.RaiseEmailToggleRequested(false);
        rig.Surface.RaiseResetProtectionScopeRequested();

        var view = View(rig);
        Assert.True(view.Enabled);                                                        // no record: the pack stays active
        Assert.Equal(CountryChoiceState.Missing, view.Basis);
        Assert.True(view.Editable);
        Assert.False(File.Exists(CountryFile));                                           // nothing was ever written by showing or rendering
        Assert.Equal(0, rig.Changed);
    }

    // ===== the user's input stores the choice, and it is read back ===============================================================

    [Fact]
    public void AUserOffInput_StoresAnOff_ReadsItBack_AndTheScreenShowsWhatWasVerified()
    {
        var rig = Open(offer: true);

        rig.Surface.RaiseUnitedStatesToggleRequested(false);

        var read = rig.Store.LoadCountryChoice("UnitedStates");
        Assert.Equal(CountryChoiceState.Off, read.State);
        Assert.Equal(CountryChoiceState.Off, PrivonLocalStore.OpenOrCreate(_root).LoadCountryChoice("UnitedStates").State);   // a later run reads it too
        var view = View(rig);
        Assert.False(view.Enabled);
        Assert.Equal(CountryChoiceState.Off, view.Basis);
        Assert.Null(rig.Surface.LastRenderedState!.StatusMessage);
        Assert.Equal(1, rig.Changed);
    }

    [Fact]
    public void AUserOnInput_AfterAnOff_StoresAnOn()
    {
        var rig = Open(offer: true);
        rig.Surface.RaiseUnitedStatesToggleRequested(false);

        rig.Surface.RaiseUnitedStatesToggleRequested(true);

        Assert.Equal(CountryChoiceState.On, rig.Store.LoadCountryChoice("UnitedStates").State);
        Assert.True(View(rig).Enabled);
        Assert.Equal(CountryChoiceState.On, View(rig).Basis);
    }

    [Fact]
    public void RapidOnOffOn_IsStoredInOrder_AndEndsOnTheLastInput()
    {
        var rig = Open(offer: true);
        var revisions = new List<long>();

        foreach (bool desired in new[] { true, false, true, false, true })
        {
            rig.Surface.RaiseUnitedStatesToggleRequested(desired);
            revisions.Add(rig.Store.LoadCountryChoice("UnitedStates").Revision);
        }

        Assert.Equal(CountryChoiceState.On, rig.Store.LoadCountryChoice("UnitedStates").State);
        Assert.Equal([1L, 2, 3, 4, 5], revisions);                                        // every distinct request was applied once, in order
    }

    [Fact]
    public void ADuplicateInput_WritesNothingTheSecondTime()
    {
        var rig = Open(offer: true);
        rig.Surface.RaiseUnitedStatesToggleRequested(false);
        long revision = rig.Store.LoadCountryChoice("UnitedStates").Revision;
        byte[] bytes = File.ReadAllBytes(CountryFile);

        rig.Surface.RaiseUnitedStatesToggleRequested(false);
        rig.Surface.RaiseUnitedStatesToggleRequested(false);

        Assert.Equal(revision, rig.Store.LoadCountryChoice("UnitedStates").Revision);
        Assert.Equal(bytes, File.ReadAllBytes(CountryFile));
        Assert.Equal(CountryChoiceState.Off, rig.Store.LoadCountryChoice("UnitedStates").State);
    }

    [Fact]
    public void ConcurrentSaves_FromManyThreads_LeaveOneConsistentVerifiedRecord()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var service = new CountryChoiceService(store);
        int failures = 0;

        // the coordinator is dispatcher-serialized in production; the service and store must still hold up if two paths ever overlap
        Parallel.For(0, 80, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            try { service.SetUnitedStates(i % 2 == 0); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Interlocked.Increment(ref failures); }
        });

        var final = store.LoadCountryChoice("UnitedStates");
        Assert.True(final.State is CountryChoiceState.On or CountryChoiceState.Off);
        Assert.True(final.Revision >= 1);
    }

    // ===== failure: nothing is shown as stored that was not ======================================================================

    [Fact]
    public void AFailedSave_PutsTheScreenBackToTheLastVerifiedChoice_AndShowsTheError()
    {
        var rig = Open(offer: true);
        rig.Surface.RaiseUnitedStatesToggleRequested(true);                                // a verified On exists
        byte[] before = File.ReadAllBytes(CountryFile);
        int changedBefore = rig.Changed;

        using (new FileStream(CountryFile, FileMode.Open, FileAccess.Read, FileShare.Read))   // readable, but cannot be replaced
        {
            rig.Surface.RaiseUnitedStatesToggleRequested(false);
        }

        Assert.Equal(before, File.ReadAllBytes(CountryFile));
        var view = View(rig);
        Assert.True(view.Enabled);                                                          // back to the verified On: the Off was never stored
        Assert.Equal(CountryChoiceState.On, view.Basis);
        Assert.Equal(SettingsCoordinator.CountryChoiceFailureMessage, rig.Surface.LastRenderedState!.StatusMessage);
        Assert.Equal(changedBefore, rig.Changed);                                           // and nobody was told the choice changed

        rig.Surface.RaiseUnitedStatesToggleRequested(false);                                // once the file is free the same request works
        Assert.Equal(CountryChoiceState.Off, rig.Store.LoadCountryChoice("UnitedStates").State);
        Assert.Null(rig.Surface.LastRenderedState!.StatusMessage);
    }

    // A store whose Save reports success but does not persist the requested choice (a lost write, a torn state): the read-back is what decides.
    private sealed class LyingStore(PrivonLocalStore real, bool persist) : ICountryChoiceStore
    {
        public int Saves;
        public CountryChoiceRead Load(string country) => real.LoadCountryChoice(country);

        public void Save(string country, bool enabled, CountryChoiceOrigin origin)
        {
            Interlocked.Increment(ref Saves);
            if (persist) real.SaveCountryChoice(country, enabled, origin);   // else: returns normally and stores nothing
        }
    }

    [Fact]
    public void ASaveThatReportsSuccessButIsNotReadBack_IsNeverShownAsStored()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var lying = new LyingStore(store, persist: false);
        var surface = new FakeSettingsSurface();
        var coordinator = new SettingsCoordinator(
            new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(), () => false, () => surface,
            new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            countryChoices: new CountryChoiceService(lying));
        _disposables.Add(coordinator);
        int changed = 0;
        coordinator.CountryChoiceChanged += (_, _) => changed++;
        coordinator.ShowRequested();

        surface.RaiseUnitedStatesToggleRequested(false);

        Assert.Equal(1, lying.Saves);                                                             // the write was attempted ...
        var view = Assert.IsType<UnitedStatesChoiceView>(surface.LastRenderedState!.UnitedStatesChoice);
        Assert.True(view.Enabled);                                                                // ... but the screen shows what the store actually holds
        Assert.Equal(CountryChoiceState.Missing, view.Basis);
        Assert.Equal(SettingsCoordinator.CountryChoiceFailureMessage, surface.LastRenderedState!.StatusMessage);
        Assert.Equal(0, changed);                                                                 // and nobody is told a choice was stored
    }

    [Fact]
    public void ASaveThatIsReadBack_IsShownAsStored_ThroughTheSameSeam()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var service = new CountryChoiceService(new LyingStore(store, persist: true));

        var read = service.SetUnitedStates(false);

        Assert.Equal(CountryChoiceState.Off, read.State);
    }

    [Fact]
    public void WithoutAUsableKey_TheControlCannotBeEdited_AndARequestStoresNothing()
    {
        PrivonLocalStore.OpenOrCreate(_root);
        using var held = new FileStream(Path.Combine(_root, "master.key"), FileMode.Open, FileAccess.Read, FileShare.None);
        var rig = Open(offer: true, store: PrivonLocalStore.OpenOrCreate(_root));

        Assert.False(View(rig).Editable);
        rig.Surface.RaiseUnitedStatesToggleRequested(false);

        Assert.False(File.Exists(CountryFile));
        Assert.Equal(SettingsCoordinator.CountryChoiceFailureMessage, rig.Surface.LastRenderedState!.StatusMessage);
        Assert.True(View(rig).Enabled);
        Assert.Equal(0, rig.Changed);
    }

    // ===== unverifiable records keep the pack active, and a newer record is never overwritten ====================================

    [Theory]
    [InlineData("garbage")]
    [InlineData("tampered")]
    public void ACorruptOrUnauthenticRecord_IsShownAsTheSafeDefault_AndTheNextUserInputReplacesIt(string damage)
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveCountryChoice("UnitedStates", enabled: false, CountryChoiceOrigin.SettingsUserAction);   // seeding a record to damage; not a user input
        byte[] bytes = File.ReadAllBytes(CountryFile);
        if (damage == "garbage") bytes = [1, 2, 3];
        else bytes[^1] ^= 1;
        File.WriteAllBytes(CountryFile, bytes);
        var rig = Open(offer: true, store: store);

        var view = View(rig);
        Assert.True(view.Enabled);                                                          // the damaged Off is NOT applied: the pack stays active
        Assert.Equal(damage == "garbage" ? CountryChoiceState.Corrupt : CountryChoiceState.Unauthenticated, view.Basis);
        Assert.True(view.Editable);

        rig.Surface.RaiseUnitedStatesToggleRequested(false);                                // the user chooses again

        Assert.Equal(CountryChoiceState.Off, store.LoadCountryChoice("UnitedStates").State);
        Assert.False(View(rig).Enabled);
    }

    [Fact]
    public void ARecordOfANewerVersion_IsShownAsLocked_AndNeverOverwritten()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.True(MasterKeyStore.TryLoad(Path.Combine(_root, "master.key"), out var key));
        var sealedNewer = SecureEnvelopeCodec.Encrypt(key, StorePurpose.CountrySettings,
            Encoding.UTF8.GetBytes("{\"FormatVersion\":2,\"Revision\":9,\"Choices\":[]}")).Encode();
        File.WriteAllBytes(CountryFile, sealedNewer);
        var rig = Open(offer: true, store: store);

        Assert.Equal(CountryChoiceState.UnknownVersion, View(rig).Basis);
        Assert.False(View(rig).Editable);
        Assert.True(View(rig).Enabled);

        rig.Surface.RaiseUnitedStatesToggleRequested(false);

        Assert.Equal(sealedNewer, File.ReadAllBytes(CountryFile));                         // a newer build's record is untouched
        Assert.Equal(SettingsCoordinator.CountryChoiceFailureMessage, rig.Surface.LastRenderedState!.StatusMessage);
        Assert.Equal(0, rig.Changed);
    }

    // ===== independence ==========================================================================================================

    [Fact]
    public void AnotherCountrysChoiceAndTheLegacySettings_AreUntouchedByAUserInput()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveCountryChoice("Korea", enabled: true, CountryChoiceOrigin.SettingsUserAction);       // seeding an unrelated record; not a user input
        store.SaveSettings(PrivonSettings.SafeDefault with { PausedUntilUtc = null });
        byte[] settingsBefore = File.ReadAllBytes(Path.Combine(_root, "settings.bin"));
        var rig = Open(offer: true, store: store);

        rig.Surface.RaiseUnitedStatesToggleRequested(false);

        Assert.Equal(CountryChoiceState.On, store.LoadCountryChoice("Korea").State);
        Assert.Equal(CountryChoiceState.Off, store.LoadCountryChoice("UnitedStates").State);
        Assert.Equal(settingsBefore, File.ReadAllBytes(Path.Combine(_root, "settings.bin")));          // the legacy settings file is byte-identical
        Assert.Equal(PrivonSettings.SafeDefault.Categories, store.LoadSettings().Categories);
    }

    // ===== lifetime ==============================================================================================================

    [Fact]
    public void AfterDispose_AnInputStoresNothing_AndNothingThrows()
    {
        var rig = Open(offer: true);
        rig.Coordinator.Dispose();

        rig.Surface.RaiseUnitedStatesToggleRequested(false);

        Assert.False(File.Exists(CountryFile));
        Assert.Equal(0, rig.Changed);
    }

    [Fact]
    public void AFailingSubscriber_CannotUndoOrHideAStoredChoice()
    {
        var rig = Open(offer: true);
        rig.Coordinator.CountryChoiceChanged += (_, _) => throw new InvalidOperationException("subscriber failure");

        rig.Surface.RaiseUnitedStatesToggleRequested(false);

        Assert.Equal(CountryChoiceState.Off, rig.Store.LoadCountryChoice("UnitedStates").State);
        Assert.Null(rig.Surface.LastRenderedState!.StatusMessage);
    }

    // ===== the real WPF window: user input vs programmatic change =================================================================

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() => { try { result = body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        Assert.Null(failure);
        return result;
    }

    private static object Field(object o, string name) => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    private static SettingsViewState State(UnitedStatesChoiceView? view) => new(
        PhoneEnabled: true, EmailEnabled: true, Exceptions: [], MasterKeyUnavailable: false, StatusMessage: null, UnitedStatesChoice: view);

    [Fact]
    public void TheRealWindow_HidesTheControlByDefault_AndRaisesTheEventOnlyForAUsersClick()
    {
        var outcome = OnSta(() =>
        {
            var window = new SettingsWindow();
            var toggle = (System.Windows.Controls.CheckBox)Field(window, "_unitedStatesToggle");
            var header = (System.Windows.Controls.TextBlock)Field(window, "_countryHeader");
            var raised = new List<bool>();
            ((ISettingsSurface)window).UnitedStatesToggleRequested += (_, desired) => raised.Add(desired);

            bool hiddenAtConstruction = toggle.Visibility == System.Windows.Visibility.Collapsed && header.Visibility == System.Windows.Visibility.Collapsed;
            window.RenderState(State(null));                                                  // a state with no country view keeps it hidden
            bool hiddenWithoutView = toggle.Visibility == System.Windows.Visibility.Collapsed;

            window.RenderState(State(new UnitedStatesChoiceView(Enabled: true, CountryChoiceState.Missing, Editable: true)));
            bool shownWithView = toggle.Visibility == System.Windows.Visibility.Visible && header.Visibility == System.Windows.Visibility.Visible;
            window.RenderState(State(new UnitedStatesChoiceView(Enabled: false, CountryChoiceState.Off, Editable: true)));   // rendering writes the control ...
            window.RenderState(State(new UnitedStatesChoiceView(Enabled: true, CountryChoiceState.On, Editable: true)));
            int raisedByRenderOrCode = raised.Count;                                              // ... and that is not a user input (RenderState is the window's only programmatic writer)

            // the control's own OnClick is what a mouse release or the space key runs: it toggles the control AND raises Click
            toggle.IsChecked = true;
            raised.Clear();
            typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toggle, null);
            var afterClick = raised.ToList();

            window.RenderState(State(new UnitedStatesChoiceView(Enabled: true, CountryChoiceState.Corrupt, Editable: false)));
            bool disabledWhenNotEditable = !toggle.IsEnabled;
            var note = (System.Windows.Controls.TextBlock)Field(window, "_unitedStatesNote");
            bool noteShown = note.Visibility == System.Windows.Visibility.Visible;
            window.Close();
            return (hiddenAtConstruction, hiddenWithoutView, shownWithView, raisedByRenderOrCode, afterClick, disabledWhenNotEditable, noteShown);
        });

        Assert.True(outcome.hiddenAtConstruction);
        Assert.True(outcome.hiddenWithoutView);
        Assert.True(outcome.shownWithView);
        Assert.Equal(0, outcome.raisedByRenderOrCode);                                          // no event from rendering or from program-code changes
        Assert.Equal([false], outcome.afterClick);                                              // one event for the one click: the click toggled On -> Off
        Assert.True(outcome.disabledWhenNotEditable);
        Assert.True(outcome.noteShown);
    }

    // ===== end to end: a user input reaches the pipeline and the tray line ======================================================

    private sealed class Setting(PrivonLocalStore store) : ICountrySettingSource
    {
        private readonly StoreBackedCountrySettingSource _inner = new(store);
        public CountrySettingObservation Read() => _inner.Read();
    }

    [Fact]
    public void AUserInput_ChangesWhatTheOptInPipelineFollows_AndKoreanAndGlobalStayTheShippedOne()
    {
        const string usText = "SSN: 321-54-9876 Phone: 212-555-1212 Address: 123 Main ST";
        const string korean = "연락처 010-1234-5678 이메일 user@example.test 901231-1234567 카드 4111 1111 1111 1111 서버 192.168.10.20";
        string Key(DetectionCandidate c) => $"{c.PiiType}|{c.Span}|{c.Canonical?.Value}|{c.RiskLevel}";
        var rig = Open(offer: true);
        var owner = UsOptInOwner.CreateWithProductionAuthority(new Setting(rig.Store));
        _disposables.Add(owner);
        int UsHits(DetectionPipeline p) => p.Detect(usText).Candidates.Count(c => c.DetectorId.Value.StartsWith("us.", StringComparison.Ordinal));

        Assert.True(UsHits(owner.CurrentPipeline()) >= 3);                                    // no record yet: the pack is active
        rig.Surface.RaiseUnitedStatesToggleRequested(false);                                  // THE USER INPUT

        var offPipeline = owner.CurrentPipeline();
        Assert.Equal(0, UsHits(offPipeline));
        Assert.False(owner.CurrentReadiness().UsEnabled);
        Assert.Equal(
            DetectionPipeline.CreateDefault().Detect(korean).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList(),
            offPipeline.Detect(korean).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList());   // the shipped nine detectors, unchanged

        rig.Surface.RaiseUnitedStatesToggleRequested(true);
        Assert.True(UsHits(owner.CurrentPipeline()) >= 3);
        Assert.NotSame(offPipeline, owner.CurrentPipeline());                                 // the OFF pipeline is not served again
    }

    [Fact]
    public void AUserInput_RefreshesTheTrayReadinessLineAtOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "CountryChoiceUiTray", Guid.NewGuid().ToString("N"));
        try
        {
            var store = PrivonLocalStore.OpenOrCreate(root);
            var owner = UsOptInOwner.CreateWithProductionAuthority(new Setting(store));
            _disposables.Add(owner);
            var surface = new FakeSettingsSurface();
            var tray = new FakeTrayIconSurface();
            var lifecycle = new ClipboardDecisionScopeLifecycle();
            var bridge = new PrivonAppUiBridge(
                new ClipboardDecisionSessionPublisher(lifecycle), lifecycle, new FakeClipboardDecisionResolver(), new FakeDispatcherScheduler { RunSynchronously = true },
                tray, () => new FakeDecisionPromptSurface(),
                new WindowsAutoStartCoordinator(new FakeWindowsAutoStartRegistration(), () => "PRIVON.exe"),
                new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(),
                () => store.IsMasterKeyUnavailable, () => surface,
                new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
                settingsPipelineSource: owner.CurrentPipeline,
                usReadinessSource: () => Adapt(owner.CurrentReadiness()),
                countryChoices: new CountryChoiceService(store));
            _disposables.Add(bridge);
            bridge.Start();
            Assert.Contains("13/13", tray.CurrentUsReadiness!.MenuText);
            tray.RaiseSettingsRequested();                                                     // the tray opens Settings through the existing path

            surface.RaiseUnitedStatesToggleRequested(false);                                   // THE USER INPUT

            Assert.Equal(UsReadinessKind.Off, tray.CurrentUsReadiness!.Kind);                  // the line follows the stored choice immediately
            surface.RaiseUnitedStatesToggleRequested(true);
            Assert.Contains("13/13", tray.CurrentUsReadiness!.MenuText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

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

    // ===== the composition's opt-in ===============================================================================================

    [Fact]
    public void TheCompositionOffersTheChoiceOnlyWhenAskedExplicitly()
    {
        PrivonAppComposition Make(bool offer)
        {
            string root = Path.Combine(_root, offer ? "offer" : "default");
            var composition = new PrivonAppComposition(
                root, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor(),
                enableDiagnostics: false, offerUnitedStatesChoice: offer);
            _disposables.Add(composition);
            composition.Start();
            return composition;
        }

        Assert.Null(Make(offer: false).CountryChoices);                                        // a default composition carries no capability
        Assert.NotNull(Make(offer: true).CountryChoices);
    }
}
