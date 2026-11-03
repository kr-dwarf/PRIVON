using System.Reflection;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Privon.App;
using Privon.Detection;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. Settings checkboxes (Phone, Email, United States): every way of changing one -- the mouse, the space key, and the UI Automation
// Toggle pattern a screen reader / assistive technology uses -- must reach the SAME change path, once per change, and a render must never count as a change.
//
// A real WPF CheckBox is raised on an STA dispatcher thread and never shown. The UIA path is the real thing: the control's own AutomationPeer, its real
// IToggleProvider, and Toggle() -- the call an accessibility client makes. The mouse / space path is ButtonBase.OnClick (what a mouse release and the space
// key both run), exactly as CountryChoiceUiPathTests already does it: there is no mouse and no person here, so these tests show which INPUT PATHS reach the
// save, never that a person chose. Nothing here is a real-Windows result.
public sealed class SettingsToggleUiaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SettingsToggleUia", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly string[] Names = ["_phoneToggle", "_emailToggle", "_unitedStatesToggle"];

    public static IEnumerable<object[]> Toggles() => Names.Select(n => new object[] { n });

    private static CheckBox Box(SettingsWindow window, string name) =>
        (CheckBox)typeof(SettingsWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    // the interface event that carries this toggle's user request to the coordinator
    private static void Listen(SettingsWindow window, string name, List<bool> sink)
    {
        ISettingsSurface surface = window;
        switch (name)
        {
            case "_phoneToggle": surface.PhoneToggleRequested += (_, desired) => sink.Add(desired); break;
            case "_emailToggle": surface.EmailToggleRequested += (_, desired) => sink.Add(desired); break;
            default: surface.UnitedStatesToggleRequested += (_, desired) => sink.Add(desired); break;
        }
    }

    private static SettingsViewState State(bool phone, bool email, bool? us, CountryChoiceState basis = CountryChoiceState.Missing, bool editable = true) => new(
        PhoneEnabled: phone, EmailEnabled: email, Exceptions: [], MasterKeyUnavailable: false, StatusMessage: null,
        UnitedStatesChoice: us is { } enabled ? new UnitedStatesChoiceView(enabled, basis, editable) : null);

    // the call an accessibility client makes: the control's own peer, its real Toggle pattern
    private static IToggleProvider UiaToggle(CheckBox box)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(box);
        Assert.NotNull(peer);
        return Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
    }

    private static void PumpPendingWork(System.Windows.Threading.Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

    private static void MouseOrSpace(CheckBox box) =>
        typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(box, null);

    // ===== the window: each input path raises exactly one request per change ======================================================

    [Theory]
    [MemberData(nameof(Toggles))]
    public void UiaToggle_Off_ThenOn_RaisesTheRequestOncePerChange_WithTheNewState(string name)
    {
        using var host = new DispatcherAffineTestHost();
        var (raised, states) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            var box = Box(window, name);
            var sink = new List<bool>();
            Listen(window, name, sink);
            window.RenderState(State(phone: true, email: true, us: true));
            var uia = UiaToggle(box);

            uia.Toggle();                                                  // On -> Off
            var afterOff = (sink.ToList(), uia.ToggleState);
            uia.Toggle();                                                  // Off -> On
            var afterOn = (sink.ToList(), uia.ToggleState);
            window.Close();
            return (new[] { afterOff.Item1, afterOn.Item1 }, new[] { afterOff.Item2, afterOn.Item2 });
        });
        PumpPendingWork(host.Dispatcher);

        Assert.Equal([false], raised[0]);                                  // one request, for the state the control now shows
        Assert.Equal([false, true], raised[1]);                            // one more for the next change, no duplicates
        Assert.Equal([System.Windows.Automation.ToggleState.Off, System.Windows.Automation.ToggleState.On], states);
    }

    [Theory]
    [MemberData(nameof(Toggles))]
    public void MouseOrSpace_Click_StillRaisesTheRequestOncePerChange(string name)
    {
        using var host = new DispatcherAffineTestHost();
        var raised = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            var box = Box(window, name);
            var sink = new List<bool>();
            Listen(window, name, sink);
            window.RenderState(State(phone: true, email: true, us: true));

            MouseOrSpace(box);                                             // On -> Off
            MouseOrSpace(box);                                             // Off -> On
            var result = sink.ToList();
            window.Close();
            return result;
        });

        Assert.Equal([false, true], raised);                               // exactly one request per click -- the Checked change is the only source
    }

    [Theory]
    [MemberData(nameof(Toggles))]
    public void RenderingNeverRaisesARequest_NotTheFirstRender_NotARenderAfterAChange_NotARollback(string name)
    {
        using var host = new DispatcherAffineTestHost();
        var raised = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            var box = Box(window, name);
            var sink = new List<bool>();
            Listen(window, name, sink);

            window.RenderState(State(phone: false, email: false, us: false));   // first render, everything off
            window.RenderState(State(phone: true, email: true, us: true));      // a render that flips every control
            window.RenderState(State(phone: true, email: true, us: true));      // the same render again
            int afterRenders = sink.Count;

            UiaToggle(box).Toggle();                                            // ONE user change ...
            int afterChange = sink.Count;
            window.RenderState(State(phone: false, email: false, us: false));   // ... the reload after a save shows the stored state ...
            window.RenderState(State(phone: true, email: true, us: true));      // ... and a failed save puts the control back
            int afterReRender = sink.Count;
            window.Close();
            return (afterRenders, afterChange, afterReRender);
        });

        Assert.Equal(0, raised.afterRenders);                              // rendering is not a user change
        Assert.Equal(1, raised.afterChange);
        Assert.Equal(1, raised.afterReRender);                             // the re-render after the change, and the rollback, add no request
    }

    [Fact]
    public void ADisabledUnitedStatesControl_CannotBeToggledByUia_AndRaisesNothing()
    {
        using var host = new DispatcherAffineTestHost();
        var (raised, threw) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            var box = Box(window, "_unitedStatesToggle");
            var sink = new List<bool>();
            Listen(window, "_unitedStatesToggle", sink);
            window.RenderState(State(phone: true, email: true, us: true, CountryChoiceState.UnknownVersion, editable: false));

            bool elementNotEnabled = false;
            try { UiaToggle(box).Toggle(); } catch (ElementNotEnabledException) { elementNotEnabled = true; }
            var result = (sink.ToList(), elementNotEnabled);
            window.Close();
            return result;
        });

        Assert.Empty(raised);
        Assert.True(threw);
    }

    // ===== the coordinator behind a real window: the input reaches the SAVED setting ==========================================

    // Forwards the real window's events and state rendering to the coordinator but never shows it (a test must not put a window on a desktop).
    internal sealed class QuietSurface(SettingsWindow window) : ISettingsSurface
    {
        private ISettingsSurface Real => window;
        public event EventHandler? Closed { add => Real.Closed += value; remove => Real.Closed -= value; }
        public event EventHandler<bool>? PhoneToggleRequested { add => Real.PhoneToggleRequested += value; remove => Real.PhoneToggleRequested -= value; }
        public event EventHandler<bool>? EmailToggleRequested { add => Real.EmailToggleRequested += value; remove => Real.EmailToggleRequested -= value; }
        public event EventHandler<bool>? UnitedStatesToggleRequested { add => Real.UnitedStatesToggleRequested += value; remove => Real.UnitedStatesToggleRequested -= value; }
        public event EventHandler? ResetProtectionScopeRequested { add => Real.ResetProtectionScopeRequested += value; remove => Real.ResetProtectionScopeRequested -= value; }
        public event EventHandler<AddExceptionRequest>? AddExceptionRequested { add => Real.AddExceptionRequested += value; remove => Real.AddExceptionRequested -= value; }
        public event EventHandler<UserExceptionValue>? DeleteExceptionRequested { add => Real.DeleteExceptionRequested += value; remove => Real.DeleteExceptionRequested -= value; }
        public event EventHandler? ResetExceptionsRequested { add => Real.ResetExceptionsRequested += value; remove => Real.ResetExceptionsRequested -= value; }
        public event EventHandler? ChromeNativeMessagingProvisionRequested { add => Real.ChromeNativeMessagingProvisionRequested += value; remove => Real.ChromeNativeMessagingProvisionRequested -= value; }
        public event EventHandler? ChromeNativeMessagingRepairRequested { add => Real.ChromeNativeMessagingRepairRequested += value; remove => Real.ChromeNativeMessagingRepairRequested -= value; }
        public event EventHandler? EdgeNativeMessagingProvisionRequested { add => Real.EdgeNativeMessagingProvisionRequested += value; remove => Real.EdgeNativeMessagingProvisionRequested -= value; }
        public event EventHandler? EdgeNativeMessagingRepairRequested { add => Real.EdgeNativeMessagingRepairRequested += value; remove => Real.EdgeNativeMessagingRepairRequested -= value; }
        public void Show() { }
        public void Activate() { }
        public void Close() => window.Close();
        public void RenderState(SettingsViewState state) => Real.RenderState(state);
    }

    private sealed class CountingStore(PrivonLocalStore real, bool persist = true) : ICountryChoiceStore
    {
        public int Saves;
        public CountryChoiceRead Load(string country) => real.LoadCountryChoice(country);

        public void Save(string country, bool enabled, CountryChoiceOrigin origin)
        {
            Interlocked.Increment(ref Saves);
            if (persist) real.SaveCountryChoice(country, enabled, origin);
        }
    }

    private sealed class Rig
    {
        public required SettingsWindow Window { get; init; }
        public required SettingsCoordinator Coordinator { get; init; }
        public required PrivonLocalStore Store { get; init; }
        public required CountingStore Countries { get; init; }
        public int Changed;
    }

    private Rig Open(bool persist = true)
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var countries = new CountingStore(store, persist);
        var window = new SettingsWindow();
        var coordinator = new SettingsCoordinator(
            new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(),
            () => store.IsMasterKeyUnavailable, () => new QuietSurface(window),
            new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            countryChoices: new CountryChoiceService(countries));
        _disposables.Add(coordinator);
        var rig = new Rig { Window = window, Coordinator = coordinator, Store = store, Countries = countries };
        coordinator.CountryChoiceChanged += (_, _) => Interlocked.Increment(ref rig.Changed);
        coordinator.ShowRequested();
        return rig;
    }

    [Fact]
    public void UiaToggle_OnTheUnitedStatesControl_SavesTheChoice_ReadsItBack_AndTheScreenAgrees()
    {
        using var host = new DispatcherAffineTestHost();
        var outcome = host.Dispatcher.Invoke(() =>
        {
            var rig = Open();
            var box = Box(rig.Window, "_unitedStatesToggle");
            int savesAtOpen = rig.Countries.Saves;

            UiaToggle(box).Toggle();                                       // OFF through the accessibility path
            var off = (rig.Store.LoadCountryChoice("UnitedStates").State, box.IsChecked, rig.Countries.Saves, rig.Changed);
            UiaToggle(box).Toggle();                                       // ON again
            var on = (rig.Store.LoadCountryChoice("UnitedStates").State, box.IsChecked, rig.Countries.Saves, rig.Changed);
            rig.Window.Close();
            return (savesAtOpen, off, on);
        });

        Assert.Equal(0, outcome.savesAtOpen);                              // opening and the first render saved nothing
        Assert.Equal((CountryChoiceState.Off, (bool?)false, 1, 1), outcome.off);   // one save, one notification, stored Off, screen Off
        Assert.Equal((CountryChoiceState.On, (bool?)true, 2, 2), outcome.on);
    }

    [Fact]
    public void UiaToggle_OnPhoneAndEmail_SaveTheCategory_AndTheScreenAgrees()
    {
        using var host = new DispatcherAffineTestHost();
        var outcome = host.Dispatcher.Invoke(() =>
        {
            var rig = Open();
            var service = new ProtectionCategorySettingsService(rig.Store);
            var phone = Box(rig.Window, "_phoneToggle");
            var email = Box(rig.Window, "_emailToggle");

            UiaToggle(phone).Toggle();
            var afterPhone = (service.Load().PhoneEnabled, service.Load().EmailEnabled, phone.IsChecked, email.IsChecked);
            UiaToggle(email).Toggle();
            var afterEmail = (service.Load().PhoneEnabled, service.Load().EmailEnabled, phone.IsChecked, email.IsChecked);
            UiaToggle(phone).Toggle();
            var afterPhoneOn = (service.Load().PhoneEnabled, service.Load().EmailEnabled, phone.IsChecked, email.IsChecked);
            rig.Window.Close();
            return (afterPhone, afterEmail, afterPhoneOn);
        });

        Assert.Equal((false, true, (bool?)false, (bool?)true), outcome.afterPhone);
        Assert.Equal((false, false, (bool?)false, (bool?)false), outcome.afterEmail);
        Assert.Equal((true, false, (bool?)true, (bool?)false), outcome.afterPhoneOn);
    }

    [Fact]
    public void AFailedSave_ThroughUia_PutsTheControlBackToTheStoredChoice_AndSavesOnceNotInALoop()
    {
        using var host = new DispatcherAffineTestHost();
        var outcome = host.Dispatcher.Invoke(() =>
        {
            var rig = Open(persist: false);                                // Save reports success but stores nothing: the read-back decides
            var box = Box(rig.Window, "_unitedStatesToggle");

            UiaToggle(box).Toggle();                                       // the user's Off is NOT what the store holds
            var result = (box.IsChecked, rig.Countries.Saves, rig.Changed, rig.Store.LoadCountryChoice("UnitedStates").State);
            rig.Window.Close();
            return result;
        });
        PumpPendingWork(host.Dispatcher);

        Assert.Equal((true, 1, 0, CountryChoiceState.Missing), outcome);   // the screen is back On (what is stored), one attempt, nobody told it changed
    }

    [Fact]
    public void AFailedSave_ThroughUia_OnPhone_PutsTheControlBackToTheStoredCategory()
    {
        using var host = new DispatcherAffineTestHost();
        var outcome = host.Dispatcher.Invoke(() =>
        {
            var rig = Open();
            var service = new ProtectionCategorySettingsService(rig.Store);
            var phone = Box(rig.Window, "_phoneToggle");

            // settings.bin cannot be replaced while it is held open: the mutation fails and the screen must show what is stored
            string settingsFile = Path.Combine(_root, "settings.bin");
            UiaToggle(phone).Toggle();                                     // a first, successful change creates the file
            Assert.True(File.Exists(settingsFile));
            UiaToggle(phone).Toggle();                                     // back to On
            using (new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                UiaToggle(phone).Toggle();                                 // Off again -- the write fails
            }
            var result = (service.Load().PhoneEnabled, phone.IsChecked);
            rig.Window.Close();
            return result;
        });

        Assert.Equal((true, (bool?)true), outcome);                        // stored On, shown On: the failed Off was never presented as saved
    }

    [Fact]
    public void UiaOffThenOn_OnTheRealWindow_ChangesWhatTheOptInPipelineFollows()
    {
        const string usText = "SSN: 321-54-9876 Phone: 212-555-1212 Address: 123 Main ST";
        using var host = new DispatcherAffineTestHost();
        var outcome = host.Dispatcher.Invoke(() =>
        {
            var rig = Open();
            var owner = UsOptInOwner.CreateWithProductionAuthority(new Setting(rig.Store));
            _disposables.Add(owner);
            int UsHits() => owner.CurrentPipeline().Detect(usText).Candidates.Count(c => c.DetectorId.Value.StartsWith("us.", StringComparison.Ordinal));
            var box = Box(rig.Window, "_unitedStatesToggle");

            int before = UsHits();
            UiaToggle(box).Toggle();
            int off = UsHits();
            bool offEnabled = owner.CurrentReadiness().UsEnabled;
            UiaToggle(box).Toggle();
            int on = UsHits();
            rig.Window.Close();
            return (before, off, offEnabled, on);
        });

        Assert.True(outcome.before >= 3);                                  // no record yet: the pack is active
        Assert.Equal(0, outcome.off);                                      // the saved OFF is what the detectors follow
        Assert.False(outcome.offEnabled);
        Assert.True(outcome.on >= 3);
    }

    private sealed class Setting(PrivonLocalStore store) : ICountrySettingSource
    {
        private readonly StoreBackedCountrySettingSource _inner = new(store);
        public CountrySettingObservation Read() => _inner.Read();
    }

    // ===== the guard that an UNVERIFIED stored record never becomes a user OFF ==================================================

    [Fact]
    public void ADamagedRecord_IsStillShownAsTheSafeDefault_AndOpeningOrRenderingSavesNothing()
    {
        using var host = new DispatcherAffineTestHost();
        var outcome = host.Dispatcher.Invoke(() =>
        {
            var store = PrivonLocalStore.OpenOrCreate(_root);
            store.SaveCountryChoice("UnitedStates", enabled: false, CountryChoiceOrigin.SettingsUserAction);   // seeding a record to damage; not a user input
            string file = Path.Combine(_root, "country-settings.bin");
            File.WriteAllBytes(file, [1, 2, 3]);
            byte[] damaged = File.ReadAllBytes(file);

            var rig = Open();
            var box = Box(rig.Window, "_unitedStatesToggle");
            var result = (box.IsChecked, rig.Countries.Saves, File.ReadAllBytes(file).SequenceEqual(damaged), rig.Changed);
            rig.Window.Close();
            return result;
        });

        Assert.Equal(((bool?)true, 0, true, 0), outcome);                  // shown On (the safe default), and nothing was saved by showing it
    }
}
