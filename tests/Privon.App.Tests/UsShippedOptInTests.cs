using System.Reflection;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Privon.App;
using Privon.Detection;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The shipped U.S. opt-in (P1): until the user has explicitly activated U.S. detection, the product runs exactly the shipped pipeline and no
// U.S. detector exists; an intact activation hands the decision to the existing per-country choice contract; an activation that cannot be verified, or an authority
// that is not fully usable, keeps U.S. detection off and says so -- never as a normal OFF choice, never as ready.
//
// What is real: the store (real files, real key), the gated owner with the real production authority, the CountryChoiceService, the coordinator, the real
// DetectionPipeline, the real WPF checkbox and its UI Automation Toggle pattern (on an STA thread, never shown), the real PrivonAppComposition graph. What is
// simulated: the user -- a test calls the service or raises the surface event the way the window does. So these tests show that an explicit Settings input is
// what turns U.S. detection on, never that a person chose.
public sealed class UsShippedOptInTests : IDisposable
{
    private const string UsText = "SSN: 321-54-9876 Phone: 212-555-1212 Address: 123 Main ST";
    private const string Korean = "연락처 010-1234-5678 이메일 user@example.test 901231-1234567 카드 4111 1111 1111 1111 서버 192.168.10.20";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "UsShippedOptIn", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string ActivationFile => Path.Combine(_root, "country-activation.bin");

    private string ChoiceFile => Path.Combine(_root, "country-settings.bin");

    private sealed class Rig(PrivonLocalStore store, UsGatedOptInOwner owner, CountryChoiceService service)
    {
        public PrivonLocalStore Store { get; } = store;
        public UsGatedOptInOwner Owner { get; } = owner;
        public CountryChoiceService Service { get; } = service;
    }

    private Rig Open(PrivonLocalStore? store = null)
    {
        store ??= PrivonLocalStore.OpenOrCreate(_root);
        var owner = UsGatedOptInOwner.CreateProduction(store);
        _disposables.Add(owner);
        return new Rig(store, owner, CountryChoiceService.ForShippedOptIn(store, () => owner.AuthorityUsable));
    }

    private static int UsHits(DetectionPipeline p, string text = UsText) =>
        p.Detect(text).Candidates.Count(c => c.DetectorId.Value.StartsWith("us.", StringComparison.Ordinal));

    private static string Key(DetectionCandidate c) => $"{c.PiiType}|{c.Span}|{c.Canonical?.Value}|{c.RiskLevel}";

    private static List<string> Keys(DetectionPipeline p, string text) => p.Detect(text).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();

    private static UsReadinessPresentation Tray(Rig rig) => UsReadinessPresenter.Present(UsReadinessAdapter.From(rig.Owner.CurrentReadiness()));

    private static UnitedStatesChoiceView View(Rig rig) => rig.Service.ViewUnitedStates(rig.Store.IsMasterKeyUnavailable);

    // ===== not selected: nothing is on ==========================================================================================

    [Fact]
    public void NotSelected_NoUsDetectorRuns_AndTheGateSaysNotSelected()
    {
        var rig = Open();

        Assert.Equal(UsOptInGate.NotSelected, rig.Owner.CurrentGate());
        Assert.Equal(0, UsHits(rig.Owner.CurrentPipeline()));
        Assert.False(File.Exists(ActivationFile));                                          // looking wrote nothing
        Assert.False(File.Exists(ChoiceFile));
    }

    [Fact]
    public void NotSelected_KoreanAndGlobalDetection_IsExactlyTheShippedDefaultPipeline()
    {
        var rig = Open();
        var shipped = DetectionPipeline.CreateDefault();

        Assert.Equal(Keys(shipped, Korean), Keys(rig.Owner.CurrentPipeline(), Korean));
        Assert.Equal(Keys(shipped, UsText), Keys(rig.Owner.CurrentPipeline(), UsText));    // U.S. text is seen exactly as the shipped app has always seen it
    }

    [Fact]
    public void NotSelected_TheCountrySettingIsNeverConsulted_AndNoAuthenticatedOffIsFabricated()
    {
        var spy = new SpyCountry(CountrySettingObservation.AuthenticatedOff);
        using var owner = UsGatedOptInOwner.Create(spy, new FixedActivation(CountryChoiceState.Missing), UsAuthoritySet.Production);

        var pipeline = owner.CurrentPipeline();
        var readiness = owner.CurrentReadiness();

        Assert.Equal(0, spy.Reads);                                                         // "never selected" is not translated into any country observation
        Assert.Equal(0, UsHits(pipeline));
        Assert.Equal(UsOptInGate.NotSelected, readiness.Gate);
        Assert.False(readiness.UsEnabled);
        Assert.Empty(readiness.Detectors);                                                  // no detector exists, so none is listed as ready
    }

    [Fact]
    public void NotSelected_TheTrayAndTheSettingsControl_SayNotSelected_NotOffAndNotReady()
    {
        var rig = Open();

        var tray = Tray(rig);
        Assert.Equal(UsReadinessKind.NotSelected, tray.Kind);
        Assert.DoesNotContain("13/13", tray.MenuText);

        var view = View(rig);
        Assert.False(view.Enabled);                                                         // unchecked: U.S. detection is not running
        Assert.Equal(UsOptInGate.NotSelected, view.Gate);
        Assert.True(view.Editable);                                                         // and the user can turn it on
    }

    [Fact]
    public void AStoredOffChoiceWithoutAnActivation_IsStillNotRunning_AndTheControlIsUnchecked()
    {
        PrivonLocalStore.OpenOrCreate(_root).SaveCountryChoice("UnitedStates", enabled: false, CountryChoiceOrigin.SettingsUserAction);
        var rig = Open();

        Assert.Equal(UsOptInGate.NotSelected, rig.Owner.CurrentGate());
        Assert.Equal(0, UsHits(rig.Owner.CurrentPipeline()));
        Assert.False(View(rig).Enabled);
    }

    [Fact]
    public void AStoredOnChoiceWithoutAnActivation_IsNotAnActivation_NothingRuns()
    {
        PrivonLocalStore.OpenOrCreate(_root).SaveCountryChoice("UnitedStates", enabled: true, CountryChoiceOrigin.SettingsUserAction);   // e.g. left by an earlier experiment build
        var rig = Open();

        Assert.Equal(UsOptInGate.NotSelected, rig.Owner.CurrentGate());
        Assert.Equal(0, UsHits(rig.Owner.CurrentPipeline()));
        Assert.False(View(rig).Enabled);
    }

    // ===== an explicit activation turns it on ===================================================================================

    [Fact]
    public void AnExplicitOn_RecordsTheChoiceThenTheActivation_AndTheUsPipelineRuns_ThroughTheVerifiedAuthority()
    {
        var rig = Open();

        rig.Service.SetUnitedStates(true);

        Assert.Equal(CountryChoiceState.On, rig.Store.LoadCountryChoice("UnitedStates").State);
        Assert.Equal(CountryChoiceState.On, rig.Store.LoadCountryActivation("UnitedStates").State);
        Assert.Equal(UsOptInGate.OptedIn, rig.Owner.CurrentGate());
        Assert.True(UsHits(rig.Owner.CurrentPipeline()) >= 3);                              // SSN, phone and address
        var tray = Tray(rig);
        Assert.Equal(UsReadinessKind.AllReady, tray.Kind);
        Assert.Contains("Verified Official Sources", tray.MenuText);                                // production authority; ready is not protection (the presenter says so in its detail)
        var view = View(rig);
        Assert.True(view.Enabled);
        Assert.Equal(UsOptInGate.OptedIn, view.Gate);
    }

    [Fact]
    public void AfterActivation_KoreanAndGlobalDetection_IsStillTheShippedOne()
    {
        var rig = Open();
        rig.Service.SetUnitedStates(true);

        Assert.Equal(Keys(DetectionPipeline.CreateDefault(), Korean), Keys(rig.Owner.CurrentPipeline(), Korean));
    }

    [Fact]
    public void AnAuthenticatedOff_AfterActivation_StopsTheUsPack_AndTheTrayStatesOff_NotNotSelected()
    {
        var rig = Open();
        rig.Service.SetUnitedStates(true);

        rig.Service.SetUnitedStates(false);

        Assert.Equal(CountryChoiceState.Off, rig.Store.LoadCountryChoice("UnitedStates").State);
        Assert.Equal(CountryChoiceState.On, rig.Store.LoadCountryActivation("UnitedStates").State);   // the activation stays: the user has used the feature
        Assert.Equal(UsOptInGate.OptedIn, rig.Owner.CurrentGate());
        Assert.Equal(0, UsHits(rig.Owner.CurrentPipeline()));
        Assert.Equal(UsReadinessKind.Off, Tray(rig).Kind);                                   // a chosen OFF is a different thing from "never selected"
        Assert.False(View(rig).Enabled);
        Assert.Equal(Keys(DetectionPipeline.CreateDefault(), Korean), Keys(rig.Owner.CurrentPipeline(), Korean));

        rig.Service.SetUnitedStates(true);                                                  // and ON again needs only the choice
        Assert.True(UsHits(rig.Owner.CurrentPipeline()) >= 3);
    }

    [Fact]
    public void TheChoiceAndTheActivation_SurviveARestart()
    {
        var first = Open();
        first.Service.SetUnitedStates(true);
        first.Service.SetUnitedStates(false);

        var second = Open(PrivonLocalStore.OpenOrCreate(_root));                            // a later run of the same user

        Assert.Equal(UsOptInGate.OptedIn, second.Owner.CurrentGate());
        Assert.Equal(0, UsHits(second.Owner.CurrentPipeline()));                            // the stored OFF is still an OFF
        Assert.False(View(second).Enabled);

        second.Service.SetUnitedStates(true);
        var third = Open(PrivonLocalStore.OpenOrCreate(_root));
        Assert.True(UsHits(third.Owner.CurrentPipeline()) >= 3);
        Assert.True(View(third).Enabled);
    }

    [Fact]
    public void ACorruptChoiceAfterActivation_FollowsTheExistingContract_ThePackStaysActive()
    {
        var rig = Open();
        rig.Service.SetUnitedStates(true);
        File.WriteAllBytes(ChoiceFile, [1, 2, 3]);

        Assert.Equal(UsOptInGate.OptedIn, rig.Owner.CurrentGate());
        Assert.True(UsHits(rig.Owner.CurrentPipeline()) >= 3);                              // FAIL_TOWARD_PROTECTION is unchanged for an activated user
        var view = View(rig);
        Assert.True(view.Enabled);
        Assert.Equal(CountryChoiceState.Corrupt, view.Basis);
    }

    // ===== an activation that cannot be verified ================================================================================

    [Theory]
    [InlineData("garbage")]
    [InlineData("tampered")]
    public void AnUnverifiableActivation_KeepsUsOff_IsReportedAsSuch_AndIsNeverANormalOff(string damage)
    {
        var rig = Open();
        rig.Service.SetUnitedStates(true);
        byte[] bytes = File.ReadAllBytes(ActivationFile);
        if (damage == "garbage") bytes = [1, 2, 3];
        else bytes[^1] ^= 1;
        File.WriteAllBytes(ActivationFile, bytes);

        Assert.Equal(UsOptInGate.Unverifiable, rig.Owner.CurrentGate());
        Assert.Equal(0, UsHits(rig.Owner.CurrentPipeline()));                               // an On country choice does not run on its own
        Assert.Equal(Keys(DetectionPipeline.CreateDefault(), Korean), Keys(rig.Owner.CurrentPipeline(), Korean));
        var tray = Tray(rig);
        Assert.Equal(UsReadinessKind.Unverifiable, tray.Kind);
        Assert.NotEqual(UsReadinessKind.Off, tray.Kind);
        Assert.NotEqual(UsReadinessKind.AllReady, tray.Kind);
        var view = View(rig);
        Assert.False(view.Enabled);
        Assert.Equal(UsOptInGate.Unverifiable, view.Gate);
        Assert.True(view.Editable);                                                         // an explicit ON writes the record afresh

        rig.Service.SetUnitedStates(true);
        Assert.Equal(UsOptInGate.OptedIn, rig.Owner.CurrentGate());
        Assert.True(UsHits(rig.Owner.CurrentPipeline()) >= 3);
    }

    [Fact]
    public void ANewerVersionActivation_KeepsUsOff_IsLockedInTheControl_AndIsNeverOverwritten()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.True(MasterKeyStore.TryLoad(Path.Combine(_root, "master.key"), out var key));
        var newer = SecureEnvelopeCodec.Encrypt(key, StorePurpose.CountryActivation,
            System.Text.Encoding.UTF8.GetBytes("{\"FormatVersion\":2,\"Revision\":9,\"Choices\":[]}")).Encode();
        File.WriteAllBytes(ActivationFile, newer);
        var rig = Open(store);

        Assert.Equal(UsOptInGate.Unverifiable, rig.Owner.CurrentGate());
        Assert.Equal(0, UsHits(rig.Owner.CurrentPipeline()));
        var view = View(rig);
        Assert.False(view.Editable);
        Assert.Throws<InvalidOperationException>(() => rig.Service.SetUnitedStates(true));
        Assert.Equal(newer, File.ReadAllBytes(ActivationFile));
    }

    [Fact]
    public void AnActivationThatCannotBeRead_IsUnverifiable_AndKoreanDetectionKeepsRunning()
    {
        using var owner = UsGatedOptInOwner.Create(new SpyCountry(CountrySettingObservation.Missing), new ThrowingActivation(), UsAuthoritySet.Production);

        Assert.Equal(UsOptInGate.Unverifiable, owner.CurrentGate());
        Assert.Equal(Keys(DetectionPipeline.CreateDefault(), Korean), Keys(owner.CurrentPipeline(), Korean));   // no exception reaches the scan
    }

    // ===== an authority that is not fully usable ================================================================================

    [Fact]
    public void AnUnusableAuthority_OffersNothing_RunsNothing_AndNeverClaimsReady()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveCountryActivation("UnitedStates", CountryChoiceOrigin.SettingsUserAction);   // even a user who activated earlier
        using var owner = UsGatedOptInOwner.Create(new StoreBackedCountrySettingSource(store), new StoreBackedUsActivationSource(store), UsAuthoritySet.Unavailable);
        var service = CountryChoiceService.ForShippedOptIn(store, () => owner.AuthorityUsable);

        Assert.False(owner.AuthorityUsable);
        Assert.Equal(UsOptInGate.Unavailable, owner.CurrentGate());
        Assert.Equal(0, UsHits(owner.CurrentPipeline()));
        Assert.Equal(Keys(DetectionPipeline.CreateDefault(), Korean), Keys(owner.CurrentPipeline(), Korean));
        var tray = UsReadinessPresenter.Present(UsReadinessAdapter.From(owner.CurrentReadiness()));
        Assert.Equal(UsReadinessKind.Unavailable, tray.Kind);
        Assert.DoesNotContain("Ready (", tray.MenuText);
        var view = service.ViewUnitedStates(masterKeyUnavailable: false);
        Assert.False(view.Enabled);
        Assert.False(view.Editable);
        Assert.Throws<InvalidOperationException>(() => service.SetUnitedStates(true));
        Assert.False(File.Exists(ChoiceFile));                                              // a refused request wrote nothing
    }

    [Fact]
    public void ABrokenProductionAuthority_IsNeverTreatedAsUsable()
    {
        Assert.True(UsOptInGateRules.IsUsable(UsAuthoritySet.Production));
        Assert.False(UsOptInGateRules.IsUsable(UsAuthoritySet.Unavailable));
        Assert.Equal(UsOptInGate.Unavailable, UsOptInGateRules.Resolve(CountryChoiceState.On, authorityUsable: false));
        Assert.Equal(UsOptInGate.OptedIn, UsOptInGateRules.Resolve(CountryChoiceState.On, authorityUsable: true));
        Assert.Equal(UsOptInGate.NotSelected, UsOptInGateRules.Resolve(CountryChoiceState.Missing, authorityUsable: true));
        foreach (var other in new[] { CountryChoiceState.Corrupt, CountryChoiceState.Unauthenticated, CountryChoiceState.UnknownVersion, CountryChoiceState.Off })
        {
            Assert.Equal(UsOptInGate.Unverifiable, UsOptInGateRules.Resolve(other, authorityUsable: true));
        }
    }

    // ===== saving fails: the screen and the policy never disagree ==============================================================

    private sealed class DroppingActivationStore(PrivonLocalStore real) : ICountryActivationStore
    {
        public int Saves;
        public CountryChoiceRead Load(string country) => real.LoadCountryActivation(country);

        public void Save(string country, CountryChoiceOrigin origin) => Saves++;            // reports success and stores nothing
    }

    [Fact]
    public void AnActivationThatIsNotReadBack_FailsTheRequest_AndNothingRuns_EvenThoughTheChoiceWasWritten()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        using var owner = UsGatedOptInOwner.CreateProduction(store);
        var dropping = new DroppingActivationStore(store);
        var service = new CountryChoiceService(new PrivonLocalCountryChoiceStore(store), dropping, () => owner.AuthorityUsable);

        Assert.Throws<InvalidOperationException>(() => service.SetUnitedStates(true));

        Assert.Equal(1, dropping.Saves);
        Assert.Equal(CountryChoiceState.On, store.LoadCountryChoice("UnitedStates").State);
        Assert.Equal(UsOptInGate.NotSelected, owner.CurrentGate());                         // no activation: still off
        Assert.Equal(0, UsHits(owner.CurrentPipeline()));
        var view = service.ViewUnitedStates(masterKeyUnavailable: false);
        Assert.False(view.Enabled);                                                         // and the screen, re-read from the store, says exactly that
        Assert.Equal(UsOptInGate.NotSelected, view.Gate);
    }

    [Fact]
    public void AChoiceThatIsNotWritten_FailsTheRequest_AndLeavesNoActivation()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveCountryChoice("UnitedStates", enabled: false, CountryChoiceOrigin.SettingsUserAction);
        using var owner = UsGatedOptInOwner.CreateProduction(store);
        var service = CountryChoiceService.ForShippedOptIn(store, () => owner.AuthorityUsable);

        using (new FileStream(ChoiceFile, FileMode.Open, FileAccess.Read, FileShare.Read))   // readable, but cannot be replaced
        {
            Assert.ThrowsAny<Exception>(() => service.SetUnitedStates(true));
        }

        Assert.False(File.Exists(ActivationFile));                                          // the activation is only written after the choice reads back
        Assert.Equal(0, UsHits(owner.CurrentPipeline()));
        Assert.False(service.ViewUnitedStates(false).Enabled);
    }

    [Fact]
    public void WithoutAUsableMasterKey_NothingIsActivated_AndTheControlCannotBeEdited()
    {
        PrivonLocalStore.OpenOrCreate(_root);
        using var held = new FileStream(Path.Combine(_root, "master.key"), FileMode.Open, FileAccess.Read, FileShare.None);
        var rig = Open(PrivonLocalStore.OpenOrCreate(_root));

        Assert.True(rig.Store.IsMasterKeyUnavailable);
        Assert.Equal(UsOptInGate.Unverifiable, rig.Owner.CurrentGate());
        Assert.Equal(0, UsHits(rig.Owner.CurrentPipeline()));
        Assert.False(View(rig).Editable);
        Assert.ThrowsAny<Exception>(() => rig.Service.SetUnitedStates(true));
        Assert.False(File.Exists(ActivationFile));
    }

    // ===== the settings path: surface event -> coordinator -> service -> store -> screen ========================================

    private sealed class SettingsRig
    {
        public required Rig Rig { get; init; }
        public required SettingsCoordinator Coordinator { get; init; }
        public required FakeSettingsSurface Surface { get; init; }
        public int Changed;
    }

    private SettingsRig OpenSettings()
    {
        var rig = Open();
        var surface = new FakeSettingsSurface();
        var coordinator = new SettingsCoordinator(
            new ProtectionCategorySettingsService(rig.Store), new UserExceptionService(rig.Store), DetectionPipeline.CreateDefault(),
            () => rig.Store.IsMasterKeyUnavailable, () => surface, new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            countryChoices: rig.Service);
        _disposables.Add(coordinator);
        var settings = new SettingsRig { Rig = rig, Coordinator = coordinator, Surface = surface };
        coordinator.CountryChoiceChanged += (_, _) => Interlocked.Increment(ref settings.Changed);
        coordinator.ShowRequested();
        return settings;
    }

    [Fact]
    public void TheSettingsInputs_TurnUsOnAndOff_AndTheScreenAlwaysShowsWhatIsStored()
    {
        var s = OpenSettings();
        var first = Assert.IsType<UnitedStatesChoiceView>(s.Surface.LastRenderedState!.UnitedStatesChoice);
        Assert.False(first.Enabled);                                                        // the first render: not selected, unchecked
        Assert.Equal(UsOptInGate.NotSelected, first.Gate);
        Assert.Equal(0, s.Changed);                                                         // and showing it stored nothing
        Assert.False(File.Exists(ActivationFile));

        s.Surface.RaiseUnitedStatesToggleRequested(true);
        var on = Assert.IsType<UnitedStatesChoiceView>(s.Surface.LastRenderedState!.UnitedStatesChoice);
        Assert.True(on.Enabled);
        Assert.Equal(UsOptInGate.OptedIn, on.Gate);
        Assert.True(UsHits(s.Rig.Owner.CurrentPipeline()) >= 3);
        Assert.Equal(1, s.Changed);

        s.Surface.RaiseUnitedStatesToggleRequested(false);
        var off = Assert.IsType<UnitedStatesChoiceView>(s.Surface.LastRenderedState!.UnitedStatesChoice);
        Assert.False(off.Enabled);
        Assert.Equal(CountryChoiceState.Off, off.Basis);
        Assert.Equal(0, UsHits(s.Rig.Owner.CurrentPipeline()));
        Assert.Equal(2, s.Changed);
    }

    // ===== the real window: UI Automation Toggle, mouse / space click, never shown ==============================================

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() => { try { result = body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)));
        Assert.Null(failure);
        return result;
    }

    private static IToggleProvider Uia(CheckBox box)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(box);
        return Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
    }

    [Fact]
    public void TheRealCheckbox_StartsUnchecked_AndUiaToggleAndClickTurnUsOnAndOff_WithTheStoreAndThePipelineFollowing()
    {
        var outcome = OnSta(() =>
        {
            var rig = Open();
            var window = new SettingsWindow();
            var box = (CheckBox)typeof(SettingsWindow).GetField("_unitedStatesToggle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var coordinator = new SettingsCoordinator(
                new ProtectionCategorySettingsService(rig.Store), new UserExceptionService(rig.Store), DetectionPipeline.CreateDefault(),
                () => rig.Store.IsMasterKeyUnavailable, () => new SettingsToggleUiaTests.QuietSurface(window),
                new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()), countryChoices: rig.Service);
            _disposables.Add(coordinator);
            coordinator.ShowRequested();

            bool startsUnchecked = box.IsChecked == false;
            int hitsBefore = UsHits(rig.Owner.CurrentPipeline());
            Uia(box).Toggle();                                                              // UIA: not selected -> on
            var afterUiaOn = (box.IsChecked, rig.Store.LoadCountryActivation("UnitedStates").State, UsHits(rig.Owner.CurrentPipeline()) >= 3);
            Uia(box).Toggle();                                                              // UIA: on -> chosen off
            var afterUiaOff = (box.IsChecked, rig.Store.LoadCountryChoice("UnitedStates").State, UsHits(rig.Owner.CurrentPipeline()));
            typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(box, null);   // mouse / space: on again
            var afterClickOn = (box.IsChecked, UsHits(rig.Owner.CurrentPipeline()) >= 3);
            window.Close();
            return (startsUnchecked, hitsBefore, afterUiaOn, afterUiaOff, afterClickOn);
        });

        Assert.True(outcome.startsUnchecked);
        Assert.Equal(0, outcome.hitsBefore);
        Assert.Equal(((bool?)true, CountryChoiceState.On, true), outcome.afterUiaOn);
        Assert.Equal(((bool?)false, CountryChoiceState.Off, 0), outcome.afterUiaOff);
        Assert.Equal(((bool?)true, true), outcome.afterClickOn);
    }

    // ===== the real composition graph ===========================================================================================

    [Fact]
    public void TheComposition_BuiltWithTheGatedOwner_WiresTheGatedPipelineTheReadinessAndTheControl()
    {
        string root = Path.Combine(_root, "composition");
        var store = PrivonLocalStore.OpenOrCreate(root);
        var owner = UsGatedOptInOwner.CreateProduction(store);
        var composition = new PrivonAppComposition(
            root, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor(),
            enableDiagnostics: false, usGatedOwner: owner);
        _disposables.Add(composition);

        // before Start: the delegates the processor and the tray will use are the gated owner's
        Assert.NotNull(composition.DetectionPipelineSource);
        Assert.NotNull(composition.UsReadinessSource);
        Assert.Equal(0, UsHits(composition.DetectionPipelineSource!()));                    // not selected: the processor's pipeline has no U.S. detector
        Assert.Equal(Keys(DetectionPipeline.CreateDefault(), Korean), Keys(composition.DetectionPipelineSource!(), Korean));
        Assert.Equal(UsReadinessKind.NotSelected, UsReadinessPresenter.Present(composition.UsReadinessSource!()).Kind);

        composition.Start();
        Assert.NotNull(composition.CountryChoices);                                         // the Settings control is offered, over the same store

        composition.CountryChoices!.SetUnitedStates(true);                                  // the explicit input
        Assert.True(UsHits(composition.DetectionPipelineSource!()) >= 3);                   // the SAME delegate the processor resolves per scan now serves the U.S. pack
        Assert.Equal(UsReadinessKind.AllReady, UsReadinessPresenter.Present(composition.UsReadinessSource!()).Kind);
    }

    // ===== F-1: one open of the store -- the owner and Settings can never look at two different opens of the same files =====================

    private static PrivonLocalStore? StoreOf(PrivonAppComposition composition) =>
        (PrivonLocalStore?)typeof(PrivonAppComposition).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composition);

    [Fact]
    public void TheCompositionBuiltWithTheGatedOwner_UsesTheOwnersOwnStore_NotASecondOpenOfTheSameFiles()
    {
        string root = Path.Combine(_root, "shared");
        var store = PrivonLocalStore.OpenOrCreate(root);
        var owner = UsGatedOptInOwner.CreateProduction(store);
        var composition = new PrivonAppComposition(
            root, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor(),
            enableDiagnostics: false, usGatedOwner: owner);
        _disposables.Add(composition);

        composition.Start();

        Assert.Same(store, owner.Store);
        Assert.Same(owner.Store, StoreOf(composition));                                      // the store Settings, the providers and the services use IS the owner's
    }

    [Fact]
    public void TheOwnerAndSettings_NeverDisagreeAboutTheMasterKey_SoTheScreenAndTheDetectionStayOneState()
    {
        string root = Path.Combine(_root, "keystate");
        PrivonLocalStore.OpenOrCreate(root);                                                // the key file exists
        UsGatedOptInOwner owner;
        using (new FileStream(Path.Combine(root, "master.key"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            owner = UsGatedOptInOwner.CreateProduction(PrivonLocalStore.OpenOrCreate(root));   // opened while the key cannot be read: this open has no usable key
        }

        // the lock is gone now: a SECOND open of the same files would read the key fine, and would disagree with the owner
        var composition = new PrivonAppComposition(
            root, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor(),
            enableDiagnostics: false, usGatedOwner: owner);
        _disposables.Add(composition);
        composition.Start();

        var service = composition.CountryChoices!;
        Assert.Equal(owner.Store!.IsMasterKeyUnavailable, composition.IsMasterKeyUnavailable);          // one open, one answer
        try { service.SetUnitedStates(true); } catch (Exception) { /* a write without a usable key is refused */ }

        bool screenSaysOn = service.ViewUnitedStates(composition.IsMasterKeyUnavailable).Enabled;
        bool detectionRuns = UsHits(composition.DetectionPipelineSource!()) >= 3;
        Assert.Equal(detectionRuns, screenSaysOn);                                          // what Settings shows is exactly what runs
        Assert.False(File.Exists(Path.Combine(root, "country-activation.bin")));            // nothing was recorded under a key the owner cannot read
    }

    [Fact]
    public void AnOwnerBuiltFromBareSources_HasNoStore_SoTheCompositionOpensItsOwnAsBefore()
    {
        string root = Path.Combine(_root, "bare");
        var store = PrivonLocalStore.OpenOrCreate(root);
        var owner = UsGatedOptInOwner.Create(new StoreBackedCountrySettingSource(store), new StoreBackedUsActivationSource(store), UsAuthoritySet.Production);
        var composition = new PrivonAppComposition(
            root, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor(),
            enableDiagnostics: false, usGatedOwner: owner);
        _disposables.Add(composition);

        composition.Start();

        Assert.Null(owner.Store);
        Assert.NotNull(StoreOf(composition));                                               // the test-seam path is unchanged: the composition opens the root itself
        Assert.NotNull(composition.CountryChoices);
    }

    [Fact]
    public void TheCompositionSource_AdoptsTheOwnersStore_AndTheFactoryOpensTheProductionRootExactlyOnce()
    {
        string composition = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Privon.App", "PrivonAppComposition.cs"));
        int at = composition.IndexOf("internal static PrivonAppComposition CreateProduction()", StringComparison.Ordinal);
        string factory = composition[at..composition.IndexOf("private static bool DiagnosticsEnabledFromEnvironment", at, StringComparison.Ordinal)];

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(factory, "PrivonLocalStore\\.OpenOrCreate\\(").Count);   // the factory opens the production root once ...
        Assert.Contains("var store = _usGatedOwner?.Store ?? PrivonLocalStore.OpenOrCreate(_storageRootPath);", composition);   // ... and the graph adopts that open
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(composition, "PrivonLocalStore\\.OpenOrCreate\\(").Count);   // the factory's, and the plain composition's own
    }

    [Fact]
    public void TheComposition_WithoutAGatedOwner_OffersNothingAndHasNoReadiness()
    {
        var composition = new PrivonAppComposition(
            Path.Combine(_root, "plain"), new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor());
        _disposables.Add(composition);
        composition.Start();

        Assert.Null(composition.DetectionPipelineSource);
        Assert.Null(composition.UsReadinessSource);
        Assert.Null(composition.CountryChoices);
    }

    [Fact]
    public void AGatedOwner_RefusesToBeCombinedWithAnotherPipelineOrReadinessSource()
    {
        var store = PrivonLocalStore.OpenOrCreate(Path.Combine(_root, "refuse"));
        using var owner = UsGatedOptInOwner.CreateProduction(store);

        Assert.Throws<ArgumentException>(() => new PrivonAppComposition(
            Path.Combine(_root, "refuse"), new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor(),
            detectionPipelineSource: DetectionPipeline.CreateDefault, usGatedOwner: owner));
    }

    [Fact]
    public void TheShippedStartup_UsesTheProductionFactory_WhichPassesTheProductionRootAndTheGatedOwnerOnly()
    {
        string root = RepoRoot();
        string composition = File.ReadAllText(Path.Combine(root, "src", "Privon.App", "PrivonAppComposition.cs"));
        int at = composition.IndexOf("internal static PrivonAppComposition CreateProduction()", StringComparison.Ordinal);
        string factory = composition[at..composition.IndexOf("private static bool DiagnosticsEnabledFromEnvironment", at, StringComparison.Ordinal)];

        Assert.Contains("UsGatedOptInOwner.CreateProduction(PrivonLocalStore.OpenOrCreate(ProductionStorageRootPath))", factory);
        Assert.Contains("usGatedOwner: owner", factory);
        Assert.Contains("return new PrivonAppComposition();", factory);                    // if the opt-in layer cannot be built, the shipped composition runs without U.S.
        Assert.DoesNotContain("AuthenticatedOff", factory);
        Assert.DoesNotContain("SaveCountry", factory);                                     // the factory never writes a choice or an activation
        // the one writer of an activation is the Settings service, from the Settings handler
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\")))
        {
            if (Path.GetFileName(file) is "PrivonLocalStore.cs" or "CountryChoiceService.cs") continue;
            Assert.DoesNotContain(".SaveCountryActivation(", File.ReadAllText(file));            // no other call site in src
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.App", "UsReadiness.cs"))) dir = dir.Parent;
        return dir!.FullName;
    }

    // ===== stand-ins ============================================================================================================

    private sealed class SpyCountry(CountrySettingObservation value) : ICountrySettingSource
    {
        public int Reads;

        public CountrySettingObservation Read()
        {
            Interlocked.Increment(ref Reads);
            return value;
        }
    }

    private sealed class FixedActivation(CountryChoiceState state) : IUsActivationSource
    {
        public CountryChoiceState Read() => state;
    }

    private sealed class ThrowingActivation : IUsActivationSource
    {
        public CountryChoiceState Read() => throw new IOException("synthetic: the activation record cannot be read");
    }
}
