using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Storage;
using Privon.Windows;
using AppBrowser = Privon.App.NativeMessagingBrowser;

namespace Privon.App.Tests;

// Phase 3C STEP40 -- PrivonAppUiBridge regression: composition-root ownership (no second
// lifecycle/resolver/publisher), tray running-presence/Exit lifecycle, subscription-lifetime
// disposal ordering, and privacy/UI-text structural boundaries. Real tray/WPF-window rendering is
// never exercised here (Phase 3C STEP40 instruction: "Real tray rendering can remain manual
// release QA") -- every test either uses hand-written fakes for ITrayIconSurface/
// IDispatcherScheduler/IDecisionPromptSurface, or (for CreateProduction's own guard) a real, but
// never-Start()-ed, PrivonAppComposition, which never reaches the real WinForms/WPF construction
// path at all.
public class PrivonAppUiBridgeTests
{
    private static readonly ClipboardDecisionItem Item1 =
        new(new CanonicalValue(PiiType.Phone, "01011112222"), RiskLevel.Level1);

    private static ClipboardDecisionPlan Plan(params ClipboardDecisionItem[] items) => new(items);

    private static string CreateTempStorageRoot() =>
        Path.Combine(Path.GetTempPath(), "PrivonAppUiBridgeTests", Guid.NewGuid().ToString("N"));

    // ==================================================================
    // CreateProduction guard (section 19)
    // ==================================================================

    [Fact]
    public void CreateProduction_CompositionNeverStarted_Throws()
    {
        using var composition = new PrivonAppComposition(
            CreateTempStorageRoot(), new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor());

        Assert.Throws<InvalidOperationException>(() => PrivonAppUiBridge.CreateProduction(composition));
    }

    [Fact]
    public void CreateProduction_NullComposition_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PrivonAppUiBridge.CreateProduction(null!));
    }

    // ==================================================================
    // Fake-driven harness (tray + prompt coordinator lifecycle, sections 19-20, 27)
    // ==================================================================

    private sealed class Harness
    {
        public required ClipboardDecisionScopeLifecycle Lifecycle { get; init; }
        public required ClipboardDecisionSessionPublisher Publisher { get; init; }
        public required FakeClipboardDecisionResolver Resolver { get; init; }
        public required FakeDispatcherScheduler Scheduler { get; init; }
        public required FakeTrayIconSurface Tray { get; init; }
        public required FakeWindowsAutoStartRegistration AutoStartRegistration { get; init; }
        public required WindowsAutoStartCoordinator AutoStartCoordinator { get; init; }
        public required FakeNativeMessagingHostRegistrationEnvironment RegistrationEnvironment { get; init; }
        public required NativeMessagingHostRegistrationCoordinator RegistrationCoordinator { get; init; }
        public required List<FakeDecisionPromptSurface> CreatedSurfaces { get; init; }
        public required List<FakeSettingsSurface> CreatedSettingsSurfaces { get; init; }
        public required PrivonAppUiBridge Bridge { get; init; }
        public required string StorageRoot { get; init; }
    }

    // Gate 032-C2 -- optional hostExecutablePathProvider override so a test can force the
    // registration coordinator's own Inspect() to observe a path-provider EXCEPTION (never merely a
    // null/empty value) -- Inspect() itself has no try/catch around that provider call, so this is
    // the seam that proves PrivonAppUiBridge's OWN new onboarding-check exception containment,
    // independent of the coordinator's already-tested null/whitespace-path Failed path.
    private static Harness CreateHarness(
        string currentExecutablePath = "C:\\PRIVON\\PRIVON.exe",
        Func<string?>? hostExecutablePathProvider = null,
        FakeNativeMessagingHostRegistrationEnvironment? registrationEnvironmentOverride = null)
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var resolver = new FakeClipboardDecisionResolver();
        var scheduler = new FakeDispatcherScheduler { RunSynchronously = true };
        var tray = new FakeTrayIconSurface();
        var autoStartRegistration = new FakeWindowsAutoStartRegistration();
        var autoStartCoordinator = new WindowsAutoStartCoordinator(autoStartRegistration, () => currentExecutablePath);
        var registrationEnvironment = registrationEnvironmentOverride ?? new FakeNativeMessagingHostRegistrationEnvironment();
        var registrationCoordinator = new NativeMessagingHostRegistrationCoordinator(
            registrationEnvironment, hostExecutablePathProvider ?? (() => currentExecutablePath));
        var createdSurfaces = new List<FakeDecisionPromptSurface>();
        var createdSettingsSurfaces = new List<FakeSettingsSurface>();

        var storageRoot = CreateTempStorageRoot();
        var store = PrivonLocalStore.OpenOrCreate(storageRoot);
        var categorySettingsService = new ProtectionCategorySettingsService(store);
        var userExceptionService = new UserExceptionService(store);

        var bridge = new PrivonAppUiBridge(
            publisher, lifecycle, resolver, scheduler, tray,
            () =>
            {
                var surface = new FakeDecisionPromptSurface();
                createdSurfaces.Add(surface);
                return surface;
            },
            autoStartCoordinator,
            categorySettingsService,
            userExceptionService,
            DetectionPipeline.CreateDefault(),
            () => store.IsMasterKeyUnavailable,
            () =>
            {
                var surface = new FakeSettingsSurface();
                createdSettingsSurfaces.Add(surface);
                return surface;
            },
            registrationCoordinator);

        return new Harness
        {
            Lifecycle = lifecycle,
            Publisher = publisher,
            Resolver = resolver,
            Scheduler = scheduler,
            Tray = tray,
            AutoStartRegistration = autoStartRegistration,
            AutoStartCoordinator = autoStartCoordinator,
            RegistrationEnvironment = registrationEnvironment,
            RegistrationCoordinator = registrationCoordinator,
            CreatedSurfaces = createdSurfaces,
            CreatedSettingsSurfaces = createdSettingsSurfaces,
            Bridge = bridge,
            StorageRoot = storageRoot,
        };
    }

    [Fact]
    public void Start_ShowsTray_AndSubscribesDecisionPromptCoordinator()
    {
        var h = CreateHarness();

        h.Bridge.Start();

        Assert.Equal(1, h.Tray.ShowCallCount);

        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));
        Assert.Single(h.CreatedSurfaces);
    }

    [Fact]
    public void ExitRequested_RoutesThroughTheSameDispatcherScheduler()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        var postCountBefore = h.Scheduler.PostCallCount;
        h.Tray.RaiseExitRequested();

        Assert.True(h.Scheduler.PostCallCount > postCountBefore);
    }

    [Fact]
    public void Start_CalledTwice_Throws()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        Assert.Throws<InvalidOperationException>(h.Bridge.Start);
    }

    // ==================================================================
    // STARTUP ROLLBACK (Phase 3C STEP40.2, section 6) -- forces the tray's own Show() to fail,
    // proving PrivonAppUiBridge.Start()'s own transactional rollback deterministically.
    // ==================================================================

    [Fact]
    public void Start_TrayShowThrows_OriginalExceptionPropagatesUnchanged()
    {
        var h = CreateHarness();
        var thrown = new InvalidOperationException("synthetic tray Show() failure");
        h.Tray.ThrowOnShow = thrown;

        var caught = Assert.Throws<InvalidOperationException>(h.Bridge.Start);
        Assert.Same(thrown, caught);
    }

    [Fact]
    public void Start_TrayShowThrows_PromptCoordinatorIsDisposed_LaterPublicationNeverShowsAPrompt()
    {
        var h = CreateHarness();
        h.Tray.ThrowOnShow = new InvalidOperationException("synthetic");

        Assert.ThrowsAny<Exception>(h.Bridge.Start);

        // The prompt coordinator's own Start() DID succeed (it runs before tray.Show()) -- proving
        // rollback actually disposed it: its own ScopePublished subscription must be gone.
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));
        Assert.Empty(h.CreatedSurfaces);
    }

    [Fact]
    public void Start_TrayShowThrows_ExitSubscriptionIsRemoved()
    {
        var h = CreateHarness();
        h.Tray.ThrowOnShow = new InvalidOperationException("synthetic");

        Assert.ThrowsAny<Exception>(h.Bridge.Start);

        Assert.Equal(0, h.Tray.ExitRequestedSubscriberCount);

        // Raising Exit after a failed Start() must not schedule anything -- there is no live
        // subscriber left to react to it.
        var postCountBefore = h.Scheduler.PostCallCount;
        h.Tray.RaiseExitRequested();
        Assert.Equal(postCountBefore, h.Scheduler.PostCallCount);
    }

    [Fact]
    public void Start_TrayShowThrows_TraySurfaceIsDisposed()
    {
        var h = CreateHarness();
        h.Tray.ThrowOnShow = new InvalidOperationException("synthetic");

        Assert.ThrowsAny<Exception>(h.Bridge.Start);

        Assert.True(h.Tray.Disposed);
    }

    [Fact]
    public void Start_TrayShowThrows_RepeatedCleanupIsSafe()
    {
        var h = CreateHarness();
        h.Tray.ThrowOnShow = new InvalidOperationException("synthetic");

        Assert.ThrowsAny<Exception>(h.Bridge.Start);

        // A subsequent explicit Dispose() (as App.xaml.cs's own catch path may still perform
        // defensively) must be safe even though rollback already ran internally.
        h.Bridge.Dispose();
        h.Bridge.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        h.Bridge.Dispose();
        h.Bridge.Dispose();
    }

    [Fact]
    public void Dispose_SafeWhenStartNeverCalled()
    {
        var h = CreateHarness();
        h.Bridge.Dispose();
    }

    [Fact]
    public void Dispose_RemovesTraySurface()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        h.Bridge.Dispose();

        Assert.True(h.Tray.Disposed);
    }

    [Fact]
    public void Dispose_UnsubscribesPromptCoordinator_LaterPublicationNeverShowsANewPrompt()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        h.Bridge.Dispose();

        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));

        Assert.Empty(h.CreatedSurfaces);
    }

    // ---- SUBSCRIPTION_LIFETIME ordering (section 20): the currently-visible decision prompt is
    // closed BEFORE the tray surface is disposed ----
    [Fact]
    public void Dispose_ClosesOpenPromptBeforeDisposingTray()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));
        var surface = Assert.Single(h.CreatedSurfaces);

        var order = new List<string>();
        surface.OnClose = () => order.Add("prompt");
        h.Tray.OnDispose = () => order.Add("tray");

        h.Bridge.Dispose();

        Assert.Equal(new[] { "prompt", "tray" }, order);
    }

    // ==================================================================
    // Phase 0.2I -- AUTO-START TOGGLE UI BINDING
    // ==================================================================

    [Fact]
    public void Start_FreshNoRegistration_TrayReflectsOff()
    {
        var h = CreateHarness();

        h.Bridge.Start();

        Assert.Equal(false, h.Tray.CurrentAutoStartChecked);
    }

    [Fact]
    public void Start_ExistingValidRegistration_TrayReflectsOn()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        h.AutoStartRegistration.Seed("PRIVON", "\"C:\\PRIVON\\PRIVON.exe\"");

        h.Bridge.Start();

        Assert.Equal(true, h.Tray.CurrentAutoStartChecked);
    }

    [Fact]
    public void Start_StaleDifferentPathRegistration_TrayReflectsOff_NeverFalsePositive()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        h.AutoStartRegistration.Seed("PRIVON", "\"C:\\OldLocation\\PRIVON.exe\"");

        h.Bridge.Start();

        Assert.Equal(false, h.Tray.CurrentAutoStartChecked);
    }

    [Fact]
    public void AutoStartToggle_FromOff_Enables_TrayShowsChecked()
    {
        var h = CreateHarness();
        h.Bridge.Start();
        Assert.Equal(false, h.Tray.CurrentAutoStartChecked);

        h.Tray.RaiseAutoStartToggleRequested();

        Assert.Equal(true, h.Tray.CurrentAutoStartChecked);
        Assert.True(h.AutoStartCoordinator.IsEnabled());
    }

    [Fact]
    public void AutoStartToggle_FromOn_Disables_TrayShowsUnchecked()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        h.AutoStartRegistration.Seed("PRIVON", "\"C:\\PRIVON\\PRIVON.exe\"");
        h.Bridge.Start();
        Assert.Equal(true, h.Tray.CurrentAutoStartChecked);

        h.Tray.RaiseAutoStartToggleRequested();

        Assert.Equal(false, h.Tray.CurrentAutoStartChecked);
        Assert.False(h.AutoStartCoordinator.IsEnabled());
    }

    [Fact]
    public void AutoStartToggle_EnableAttemptFails_TrayNeverFalselyShowsChecked()
    {
        var h = CreateHarness();
        h.AutoStartRegistration.FailSet = true;
        h.Bridge.Start();
        Assert.Equal(false, h.Tray.CurrentAutoStartChecked);

        h.Tray.RaiseAutoStartToggleRequested();

        // The attempt failed -- the tray must reflect the ACTUAL (still-off) registry state, never
        // a state this type merely hoped for.
        Assert.Equal(false, h.Tray.CurrentAutoStartChecked);
        Assert.False(h.AutoStartCoordinator.IsEnabled());
    }

    [Fact]
    public void AutoStartToggle_DisableAttemptFails_TrayNeverFalselyShowsUnchecked()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        h.AutoStartRegistration.Seed("PRIVON", "\"C:\\PRIVON\\PRIVON.exe\"");
        h.AutoStartRegistration.FailDelete = true;
        h.Bridge.Start();
        Assert.Equal(true, h.Tray.CurrentAutoStartChecked);

        h.Tray.RaiseAutoStartToggleRequested();

        Assert.Equal(true, h.Tray.CurrentAutoStartChecked); // still registered -- never falsely shown off
        Assert.True(h.AutoStartCoordinator.IsEnabled());
    }

    [Fact]
    public void AutoStartToggle_RoutesThroughTheSameDispatcherScheduler()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        var postCountBefore = h.Scheduler.PostCallCount;
        h.Tray.RaiseAutoStartToggleRequested();

        Assert.True(h.Scheduler.PostCallCount > postCountBefore);
    }

    [Fact]
    public void Dispose_UnsubscribesAutoStartToggle_LaterRaiseDoesNothing()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        h.Bridge.Dispose();

        Assert.Equal(0, h.Tray.AutoStartToggleRequestedSubscriberCount);

        var setCallsBefore = h.AutoStartRegistration.TrySetValueCallCount;
        h.Tray.RaiseAutoStartToggleRequested();
        Assert.Equal(setCallsBefore, h.AutoStartRegistration.TrySetValueCallCount);
    }

    [Fact]
    public void Start_TrayShowThrows_AutoStartToggleSubscriptionIsRemoved()
    {
        var h = CreateHarness();
        h.Tray.ThrowOnShow = new InvalidOperationException("synthetic");

        Assert.ThrowsAny<Exception>(h.Bridge.Start);

        Assert.Equal(0, h.Tray.AutoStartToggleRequestedSubscriberCount);
    }

    // ==================================================================
    // PRIVON 0.3.1 Gate E5G.P3 -- REGISTRATION_STAYS_INERT: the ONE owned
    // NativeMessagingHostRegistrationCoordinator must never be MUTATED by ordinary tray lifecycle
    // (construction, Start, Dispose) -- there is no tray button for it yet, and the E5G.P2 commander
    // contract forbids every-startup provisioning.
    //
    // PRIVON 0.3.2 Gate 032-C2 CONTRACT UPDATE: Start() now performs exactly one contained READ-ONLY
    // Chrome readiness inspection (Decision B) -- so "zero interaction of any kind" is no longer the
    // frozen contract; "zero MUTATION" is. AssertNoMutationCalls below filters the SAME CallLog to
    // exactly the four mutating members (SetSubkeyDefaultValue/DeleteSubkey/WriteManifest/
    // DeleteManifest) -- the read-only members (SubkeyExists/GetSubkeyDefaultValue/ManifestExists/
    // ReadManifest) are now expected to appear, and are covered by the new Gate 032-C2 suite below
    // instead.
    // ==================================================================

    private static readonly string[] MutationCallPrefixes =
        ["SetSubkeyDefaultValue(", "DeleteSubkey(", "WriteManifest(", "DeleteManifest("];

    private static void AssertNoMutationCalls(FakeNativeMessagingHostRegistrationEnvironment environment)
    {
        var mutations = environment.CallLog.Where(e => MutationCallPrefixes.Any(p => e.StartsWith(p, StringComparison.Ordinal))).ToList();
        Assert.Empty(mutations);
    }

    [Fact]
    public void Construction_CausesZeroNativeMessagingRegistrationCallsOfAnyKind()
    {
        // Construction alone (before Start()) performs no inspection at all -- the new Gate 032-C2
        // read-only check only runs as part of Start()'s own onboarding sequence.
        var h = CreateHarness();

        Assert.Empty(h.RegistrationEnvironment.CallLog);
    }

    [Fact]
    public void Start_CausesZeroNativeMessagingRegistrationMutation()
    {
        var h = CreateHarness();

        h.Bridge.Start();

        AssertNoMutationCalls(h.RegistrationEnvironment);
    }

    [Fact]
    public void Dispose_DoesNotUninstallTheNativeMessagingHost()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        h.Bridge.Dispose();

        AssertNoMutationCalls(h.RegistrationEnvironment);
        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, e => e.StartsWith("DeleteSubkey(", StringComparison.Ordinal));
        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, e => e.StartsWith("DeleteManifest(", StringComparison.Ordinal));
    }

    [Fact]
    public void FullLifecycle_StartThenDispose_NeverMutatesRegistration()
    {
        // No every-startup provisioning (E5G.P2 commander contract): a complete, ordinary
        // Start()-then-Dispose() cycle -- including a decision published and a tray toggle raised --
        // must never MUTATE native messaging registration (Gate 032-C2: read-only inspection is now
        // expected and is exercised/asserted separately below).
        var h = CreateHarness();
        h.Bridge.Start();

        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        h.Publisher.TryPublish(g1, "raw text", Plan(Item1));
        h.Tray.RaiseAutoStartToggleRequested();

        h.Bridge.Dispose();

        AssertNoMutationCalls(h.RegistrationEnvironment);
    }

    // ==================================================================
    // PRIVON 0.3.2 Gate 032-C2 -- FRIENDLY CONNECT CHROME + PROMPTED RECONNECT.
    //
    // Decision B: normal tray Start() performs exactly one contained READ-ONLY Chrome readiness
    // inspection (reusing SettingsCoordinator's own existing policy-gated Chrome identity/readiness
    // path -- never a second, independently-constructed coordinator/environment). Fresh proactively
    // opens/focuses the EXISTING Settings surface once; OwnedNeedsRepair shows exactly one reconnect
    // notification whose click ONLY opens/focuses Settings; every other state (Ready/ForeignBlocked/
    // OrphanBlocked/Failed) does nothing. Actual Provision/Repair mutation remains a separate,
    // later, explicit button click -- never reachable from this new startup path.
    // ==================================================================

    private const string ExpectedChromeOrigin = "chrome-extension://aieobgphcpmkfnhadocdhenigmackboo/";

    // Reuses the SAME frozen production path/manifest-JSON authority
    // (NativeMessagingHostRegistrationLayout, Gate E5D/E5G.P3) the registrar/coordinator themselves
    // use -- never a hand-rolled reimplementation that could silently drift from the real
    // serialization shape.
    private static string ExpectedChromeManifestPath() =>
        NativeMessagingHostRegistrationLayout.ExpectedManifestPath(AppBrowser.Chrome);

    private static string ExpectedChromeManifestJson(string hostExecutablePath) =>
        NativeMessagingHostRegistrationLayout.BuildManifestJson(
            new NativeMessagingHostRegistrationSpec(hostExecutablePath, ExpectedChromeOrigin));

    // ---- A. Fresh startup ----

    [Fact]
    public void Start_Fresh_ProactivelyOpensSettingsExactlyOnce()
    {
        var h = CreateHarness();

        h.Bridge.Start();

        var surface = Assert.Single(h.CreatedSettingsSurfaces);
        Assert.Equal(1, surface.ShowCallCount);
    }

    [Fact]
    public void Start_Fresh_DoesNotShowReconnectNotification()
    {
        var h = CreateHarness();

        h.Bridge.Start();

        Assert.Equal(0, h.Tray.ShowChromeReconnectNotificationCallCount);
    }

    [Fact]
    public void Start_Fresh_OnboardingOpensSettingsWithZeroRegistrationMutation()
    {
        var h = CreateHarness();

        h.Bridge.Start();

        Assert.Single(h.CreatedSettingsSurfaces);
        AssertNoMutationCalls(h.RegistrationEnvironment);
    }

    [Fact]
    public void Start_Fresh_NeverCallsProvision_ExplicitClickStillRequiredAfterwards()
    {
        var h = CreateHarness();

        h.Bridge.Start();

        // The proactively-opened surface itself never raised ChromeNativeMessagingProvisionRequested
        // -- opening Settings is read-only. Provisioning still requires the user's own later click.
        AssertNoMutationCalls(h.RegistrationEnvironment);
        var surface = Assert.Single(h.CreatedSettingsSurfaces);
        surface.RaiseChromeNativeMessagingProvisionRequested();
        Assert.Contains(h.RegistrationEnvironment.CallLog, e => e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal));
    }

    // ---- B. Ready startup ----

    [Fact]
    public void Start_Ready_DoesNotAutoOpenSettings()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON\\PRIVON.exe"));

        h.Bridge.Start();

        Assert.Empty(h.CreatedSettingsSurfaces);
    }

    [Fact]
    public void Start_Ready_DoesNotShowReconnectNotification()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON\\PRIVON.exe"));

        h.Bridge.Start();

        Assert.Equal(0, h.Tray.ShowChromeReconnectNotificationCallCount);
    }

    [Fact]
    public void Start_Ready_PerformsZeroRegistrationMutation()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON\\PRIVON.exe"));

        h.Bridge.Start();

        AssertNoMutationCalls(h.RegistrationEnvironment);
    }

    // ---- C. OwnedNeedsRepair startup (path drift) ----

    [Fact]
    public void Start_OwnedNeedsRepair_ShowsExactlyOneReconnectNotification()
    {
        // A registration OWNED by PRIVON (exact witness) whose manifest still names the OLD
        // executable path -- exactly the "moved/renamed folder" real-world scenario Gate 032-C1
        // identified -- classifies OwnedNeedsRepair because InspectCore rebuilds the expected
        // manifest from the CURRENT Environment.ProcessPath-equivalent (the injected provider here).
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON-NEW\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));

        h.Bridge.Start();

        Assert.Equal(1, h.Tray.ShowChromeReconnectNotificationCallCount);
    }

    [Fact]
    public void Start_OwnedNeedsRepair_DoesNotAutoOpenSettings()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON-NEW\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));

        h.Bridge.Start();

        Assert.Empty(h.CreatedSettingsSurfaces);
    }

    [Fact]
    public void Start_OwnedNeedsRepair_NotificationDisplayItselfPerformsZeroMutation()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON-NEW\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));

        h.Bridge.Start();

        AssertNoMutationCalls(h.RegistrationEnvironment);
    }

    [Fact]
    public void ChromeReconnectNotificationClicked_OnlyOpensSettings_NeverCallsRepair()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON-NEW\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));
        h.Bridge.Start();
        Assert.Equal(1, h.Tray.ShowChromeReconnectNotificationCallCount);

        h.Tray.RaiseChromeReconnectNotificationClicked();

        var surface = Assert.Single(h.CreatedSettingsSurfaces);
        Assert.Equal(1, surface.ShowCallCount);
        AssertNoMutationCalls(h.RegistrationEnvironment);
    }

    [Fact]
    public void ChromeReconnectNotificationClicked_RoutesThroughTheSameDispatcherScheduler()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON-NEW\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));
        h.Bridge.Start();

        var postCountBefore = h.Scheduler.PostCallCount;
        h.Tray.RaiseChromeReconnectNotificationClicked();

        Assert.True(h.Scheduler.PostCallCount > postCountBefore);
    }

    // ---- H. Existing Settings activation behavior reused, never duplicated ----

    [Fact]
    public void ChromeReconnectNotificationClicked_NoExistingSettingsWindow_CreatesAndShowsOne()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON-NEW\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));
        h.Bridge.Start();
        Assert.Empty(h.CreatedSettingsSurfaces);

        h.Tray.RaiseChromeReconnectNotificationClicked();

        var surface = Assert.Single(h.CreatedSettingsSurfaces);
        Assert.Equal(1, surface.ShowCallCount);
    }

    [Fact]
    public void ChromeReconnectNotificationClicked_ExistingSettingsWindowOpen_ActivatesExisting_NeverDuplicate()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON-NEW\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));
        h.Bridge.Start();
        h.Tray.RaiseSettingsRequested();
        var surface = Assert.Single(h.CreatedSettingsSurfaces);

        h.Tray.RaiseChromeReconnectNotificationClicked();

        Assert.Single(h.CreatedSettingsSurfaces);
        Assert.Equal(1, surface.ActivateCallCount);
    }

    // ---- D. Foreign / Orphan / Failed startup ----

    [Fact]
    public void Start_ForeignBlocked_NoNotification_NoAutoOpenSettings_NoMutation()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        // A leaf whose default value is NOT the expected manifest path at all -- a third party's
        // registration (registrar's own FOREIGN classification).
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, "C:\\SomeOtherApp\\other-host.json");

        h.Bridge.Start();

        Assert.Empty(h.CreatedSettingsSurfaces);
        Assert.Equal(0, h.Tray.ShowChromeReconnectNotificationCallCount);
        AssertNoMutationCalls(h.RegistrationEnvironment);
    }

    [Fact]
    public void Start_OrphanBlocked_NoNotification_NoAutoOpenSettings_NoMutation()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        // No registry witness at all, but a file already sits at the expected manifest path --
        // UNWITNESSED ORPHAN (registrar's own frozen classification).
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), "{\"not\":\"privon\"}");

        h.Bridge.Start();

        Assert.Empty(h.CreatedSettingsSurfaces);
        Assert.Equal(0, h.Tray.ShowChromeReconnectNotificationCallCount);
        AssertNoMutationCalls(h.RegistrationEnvironment);
    }

    [Fact]
    public void Start_Failed_NullHostPath_StartupStillSucceeds_NoNotification_NoAutoOpenSettings()
    {
        var h = CreateHarness(hostExecutablePathProvider: () => null);

        h.Bridge.Start();

        Assert.Equal(1, h.Tray.ShowCallCount); // normal startup completed
        Assert.Empty(h.CreatedSettingsSurfaces);
        Assert.Equal(0, h.Tray.ShowChromeReconnectNotificationCallCount);
        AssertNoMutationCalls(h.RegistrationEnvironment);
    }

    // ---- E. Exception containment: onboarding must never prevent normal startup ----

    [Fact]
    public void Start_HostPathProviderThrows_StartupStillSucceeds_NoNotification_NoAutoOpenSettings()
    {
        var h = CreateHarness(hostExecutablePathProvider: () => throw new InvalidOperationException("synthetic path-provider failure"));

        h.Bridge.Start(); // must not throw

        Assert.Equal(1, h.Tray.ShowCallCount);
        Assert.Empty(h.CreatedSettingsSurfaces);
        Assert.Equal(0, h.Tray.ShowChromeReconnectNotificationCallCount);
    }

    [Fact]
    public void Start_ReconnectNotificationDisplayThrows_StartupStillSucceeds()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON-NEW\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));
        h.Tray.ThrowOnShowChromeReconnectNotification = new InvalidOperationException("synthetic notification failure");

        h.Bridge.Start(); // must not throw -- normal startup already succeeded before onboarding runs

        Assert.Equal(1, h.Tray.ShowCallCount);
        // Existing tray Settings menu entry remains a working fallback even though the notification
        // display itself failed.
        h.Tray.RaiseSettingsRequested();
        Assert.Single(h.CreatedSettingsSurfaces);
    }

    [Fact]
    public void Start_OnboardingExceptionContainment_NeverTriggersStartupRollback()
    {
        // Distinguishes onboarding-exception containment from the PRE-EXISTING STARTUP_ROLLBACK
        // path (section 6): a failing onboarding check must NOT roll back the tray/prompt
        // subscriptions that already succeeded -- Start() must return normally, not throw.
        var h = CreateHarness(hostExecutablePathProvider: () => throw new InvalidOperationException("synthetic"));

        h.Bridge.Start();

        Assert.True(h.Tray.ShowCallCount == 1);
        Assert.True(h.Tray.ExitRequestedSubscriberCount > 0);
    }

    // ---- F. Edge: zero inspection, zero notification, zero mutation ----

    [Fact]
    public void Start_NeverInspectsOrMutatesEdge()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");

        h.Bridge.Start();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, e => e.Contains(nameof(AppBrowser.Edge), StringComparison.Ordinal));
    }

    // ---- G. Notification lifecycle / disposal ----

    [Fact]
    public void Start_SubscribesChromeReconnectNotificationClickedExactlyOnce()
    {
        var h = CreateHarness();

        h.Bridge.Start();

        Assert.Equal(1, h.Tray.ChromeReconnectNotificationClickedSubscriberCount);
    }

    [Fact]
    public void Start_TrayShowThrows_ChromeReconnectNotificationClickedSubscriptionIsRemoved()
    {
        var h = CreateHarness();
        h.Tray.ThrowOnShow = new InvalidOperationException("synthetic");

        Assert.ThrowsAny<Exception>(h.Bridge.Start);

        Assert.Equal(0, h.Tray.ChromeReconnectNotificationClickedSubscriberCount);

        var postCountBefore = h.Scheduler.PostCallCount;
        h.Tray.RaiseChromeReconnectNotificationClicked();
        Assert.Equal(postCountBefore, h.Scheduler.PostCallCount);
    }

    [Fact]
    public void Dispose_UnsubscribesChromeReconnectNotificationClicked_LaterRaiseDoesNothing()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        h.Bridge.Dispose();

        Assert.Equal(0, h.Tray.ChromeReconnectNotificationClickedSubscriberCount);

        var postCountBefore = h.Scheduler.PostCallCount;
        h.Tray.RaiseChromeReconnectNotificationClicked();
        Assert.Equal(postCountBefore, h.Scheduler.PostCallCount);
    }

    // ---- I. Path-drift / seeded scenarios ----

    [Fact]
    public void Start_CurrentPathMatchesRegisteredManifest_IsReady_NoPrompt()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON\\PRIVON.exe"));

        h.Bridge.Start();

        Assert.Empty(h.CreatedSettingsSurfaces);
        Assert.Equal(0, h.Tray.ShowChromeReconnectNotificationCallCount);
    }

    [Fact]
    public void Start_ChangedPathVersusRegisteredManifest_IsOwnedNeedsRepair_ShowsReconnectNotification()
    {
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON-NEW\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));

        h.Bridge.Start();

        Assert.Equal(1, h.Tray.ShowChromeReconnectNotificationCallCount);
    }

    [Fact]
    public void Start_AfterSuccessfulRepairSimulation_NextStartupIsReady_NoPrompt()
    {
        // Simulates "the user clicked Reconnect Chrome and it succeeded" by driving the SAME
        // underlying FakeNativeMessagingHostRegistrationEnvironment through the registrar's own
        // Repair() (exactly what SettingsCoordinator.RepairChrome would trigger), THEN constructing a
        // fresh PrivonAppUiBridge instance sharing that SAME environment against the SAME current
        // path (simulating the next process launch) and proving no prompt is shown.
        var environment = new FakeNativeMessagingHostRegistrationEnvironment();
        const string currentPath = "C:\\PRIVON-NEW\\PRIVON.exe";
        environment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        environment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));

        var repairCoordinator = new NativeMessagingHostRegistrationCoordinator(environment, () => currentPath);
        var repairResult = repairCoordinator.Repair(AppBrowser.Chrome, ExpectedChromeOrigin);
        Assert.Equal(NativeMessagingRegistrationReadiness.Ready, repairResult);

        var h = CreateHarness(currentExecutablePath: currentPath, registrationEnvironmentOverride: environment);

        h.Bridge.Start();

        Assert.Empty(h.CreatedSettingsSurfaces);
        Assert.Equal(0, h.Tray.ShowChromeReconnectNotificationCallCount);
    }

    // ==================================================================
    // PRIVON 0.3.2 Gate 032-C2R -- EXACTLY_ONE_STARTUP_INSPECTION remediation oracle.
    //
    // Independent audit finding: Fresh startup performed TWO equivalent Chrome registration
    // inspections (Start's own onboarding Inspect, THEN a second InspectChrome() inside the
    // Settings surface's own initial Render triggered by that same Fresh branch opening Settings).
    // The oracle below proves this directly against the real production read path -- never a
    // fabricated counter, mutation count, window count, or source substring count (section 5/6): it
    // captures the EXACT registration-environment call sequence a single, real, direct
    // NativeMessagingHostRegistrationCoordinator.Inspect() call produces against an equivalently-
    // seeded environment, then compares it, call-for-call, against the COMPLETE call sequence a real
    // Start() produces against a separate, equivalently-seeded environment. Before remediation this
    // must FAIL for Fresh (the actual sequence is the one-Inspect baseline repeated twice); every
    // other state already performs at most one Inspect today (Decision B never auto-opens Settings
    // for anything but Fresh), so those are expected to already pass and exist here purely as
    // regression coverage for after the fix.
    // ==================================================================

    private static void AssertTotalChromeInspectionReadSequenceMatchesOneDirectInspect(
        Action<FakeNativeMessagingHostRegistrationEnvironment> seed,
        string currentPath,
        NativeMessagingRegistrationReadiness expectedReadiness)
    {
        // Baseline: exactly ONE direct call to the SAME production Inspect path
        // (NativeMessagingHostRegistrationCoordinator.Inspect -- what
        // SettingsCoordinator.InspectChromeReadinessForStartup()/InspectChrome() themselves delegate
        // to, never a reimplementation), against its own separate, equivalently-seeded environment.
        var baselineEnvironment = new FakeNativeMessagingHostRegistrationEnvironment();
        seed(baselineEnvironment);
        var baselineCoordinator = new NativeMessagingHostRegistrationCoordinator(baselineEnvironment, () => currentPath);
        var baselineReadiness = baselineCoordinator.Inspect(AppBrowser.Chrome, ExpectedChromeOrigin);
        Assert.Equal(expectedReadiness, baselineReadiness);
        var oneInspectSequence = baselineEnvironment.CallLog.ToList();

        // Actual: a real Start() in the SAME state, against a SEPARATE but equivalently-seeded
        // environment -- the complete call sequence Start's own onboarding path produces.
        var actualEnvironment = new FakeNativeMessagingHostRegistrationEnvironment();
        seed(actualEnvironment);
        var h = CreateHarness(currentExecutablePath: currentPath, registrationEnvironmentOverride: actualEnvironment);

        h.Bridge.Start();

        Assert.Equal(oneInspectSequence, h.RegistrationEnvironment.CallLog);
    }

    private static void SeedFresh(FakeNativeMessagingHostRegistrationEnvironment env) { /* leaf and manifest both absent */ }

    private static void SeedReady(FakeNativeMessagingHostRegistrationEnvironment env, string currentPath)
    {
        env.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        env.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson(currentPath));
    }

    private static void SeedOwnedNeedsRepair(FakeNativeMessagingHostRegistrationEnvironment env)
    {
        env.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        env.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON-OLD\\PRIVON.exe"));
    }

    private static void SeedForeignBlocked(FakeNativeMessagingHostRegistrationEnvironment env) =>
        env.SeedLeaf(AppBrowser.Chrome, "C:\\SomeOtherApp\\other-host.json");

    private static void SeedOrphanBlocked(FakeNativeMessagingHostRegistrationEnvironment env) =>
        env.SeedManifest(ExpectedChromeManifestPath(), "{\"not\":\"privon\"}");

    // THE genuine RED case: this is the exact scenario the independent audit flagged.
    [Fact]
    public void Start_Fresh_TotalChromeInspection_EqualsExactlyOneDirectInspectSequence() =>
        AssertTotalChromeInspectionReadSequenceMatchesOneDirectInspect(
            SeedFresh, "C:\\PRIVON\\PRIVON.exe", NativeMessagingRegistrationReadiness.Fresh);

    [Fact]
    public void Start_Ready_TotalChromeInspection_EqualsExactlyOneDirectInspectSequence() =>
        AssertTotalChromeInspectionReadSequenceMatchesOneDirectInspect(
            env => SeedReady(env, "C:\\PRIVON\\PRIVON.exe"), "C:\\PRIVON\\PRIVON.exe", NativeMessagingRegistrationReadiness.Ready);

    [Fact]
    public void Start_OwnedNeedsRepair_TotalChromeInspection_EqualsExactlyOneDirectInspectSequence() =>
        AssertTotalChromeInspectionReadSequenceMatchesOneDirectInspect(
            SeedOwnedNeedsRepair, "C:\\PRIVON-NEW\\PRIVON.exe", NativeMessagingRegistrationReadiness.OwnedNeedsRepair);

    [Fact]
    public void Start_ForeignBlocked_TotalChromeInspection_EqualsExactlyOneDirectInspectSequence() =>
        AssertTotalChromeInspectionReadSequenceMatchesOneDirectInspect(
            SeedForeignBlocked, "C:\\PRIVON\\PRIVON.exe", NativeMessagingRegistrationReadiness.ForeignBlocked);

    [Fact]
    public void Start_OrphanBlocked_TotalChromeInspection_EqualsExactlyOneDirectInspectSequence() =>
        AssertTotalChromeInspectionReadSequenceMatchesOneDirectInspect(
            SeedOrphanBlocked, "C:\\PRIVON\\PRIVON.exe", NativeMessagingRegistrationReadiness.OrphanBlocked);

    [Fact]
    public void Start_Failed_NullHostPath_TotalChromeInspection_PerformsZeroEnvironmentCalls()
    {
        // Failed (unresolvable host path) never even reaches InspectCore -- the one-Inspect
        // baseline itself is zero calls. Proves Start() does not fabricate a second, different
        // (successful) Inspect after the first one already failed.
        var baselineEnvironment = new FakeNativeMessagingHostRegistrationEnvironment();
        var baselineCoordinator = new NativeMessagingHostRegistrationCoordinator(baselineEnvironment, () => null);
        var baselineReadiness = baselineCoordinator.Inspect(AppBrowser.Chrome, ExpectedChromeOrigin);
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, baselineReadiness);
        Assert.Empty(baselineEnvironment.CallLog);

        var h = CreateHarness(hostExecutablePathProvider: () => null);

        h.Bridge.Start();

        Assert.Empty(h.RegistrationEnvironment.CallLog);
    }

    // ---- One-shot snapshot / staleness safety (section 8) ----

    [Fact]
    public void Start_Fresh_InitialRenderUsesStartupSnapshot_ExplicitProvisionThenRendersLiveReadiness()
    {
        // The Fresh startup snapshot must be call-scoped, never a persistent cached field: after the
        // user's own explicit Provision click actually changes real state, the NEXT render must
        // reflect the CURRENT live readiness (Ready) -- never keep echoing the stale "Fresh" snapshot
        // captured at startup.
        var h = CreateHarness();
        h.Bridge.Start();
        var surface = Assert.Single(h.CreatedSettingsSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Fresh, surface.LastRenderedState!.ChromeNativeMessagingReadiness);

        surface.RaiseChromeNativeMessagingProvisionRequested();

        Assert.Equal(NativeMessagingRegistrationReadiness.Ready, surface.LastRenderedState!.ChromeNativeMessagingReadiness);
    }

    [Fact]
    public void Start_Fresh_SettingsClosedThenReopened_SecondOpenUsesLiveInspect_NotStaleStartupSnapshot()
    {
        var environment = new FakeNativeMessagingHostRegistrationEnvironment();
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe", registrationEnvironmentOverride: environment);
        h.Bridge.Start();
        var firstSurface = Assert.Single(h.CreatedSettingsSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Fresh, firstSurface.LastRenderedState!.ChromeNativeMessagingReadiness);
        firstSurface.Close();

        // Real state changes to Ready AFTER the startup snapshot was already taken and consumed,
        // BEFORE the user reopens Settings via the ordinary tray entry point.
        environment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        environment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON\\PRIVON.exe"));

        h.Tray.RaiseSettingsRequested();

        Assert.Equal(2, h.CreatedSettingsSurfaces.Count);
        var secondSurface = h.CreatedSettingsSurfaces[1];
        Assert.Equal(NativeMessagingRegistrationReadiness.Ready, secondSurface.LastRenderedState!.ChromeNativeMessagingReadiness);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var resolver = new FakeClipboardDecisionResolver();
        var scheduler = new FakeDispatcherScheduler();
        var tray = new FakeTrayIconSurface();
        Func<IDecisionPromptSurface> factory = () => new FakeDecisionPromptSurface();
        var autoStart = new WindowsAutoStartCoordinator(new FakeWindowsAutoStartRegistration());
        var store = PrivonLocalStore.OpenOrCreate(CreateTempStorageRoot());
        var categoryService = new ProtectionCategorySettingsService(store);
        var exceptionService = new UserExceptionService(store);
        var pipeline = DetectionPipeline.CreateDefault();
        Func<bool> isUnavailable = () => false;
        Func<ISettingsSurface> settingsFactory = () => new FakeSettingsSurface();
        var registrationCoordinator = new NativeMessagingHostRegistrationCoordinator(
            new FakeNativeMessagingHostRegistrationEnvironment(), () => "C:\\PRIVON\\PRIVON.exe");

        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(null!, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, null!, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, null!, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, null!, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, null!, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, null!, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, null!, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, null!, exceptionService, pipeline, isUnavailable, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, null!, pipeline, isUnavailable, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, null!, isUnavailable, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, null!, settingsFactory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, null!, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory, null!));
    }

    // ==================================================================
    // PRIVON v0.2.1 Gate 3C -- SETTINGS TRAY ENTRY POINT / ONE_CURRENT_SETTINGS_WINDOW / SHUTDOWN
    // ==================================================================

    // UI-001
    [Fact]
    public void SettingsRequested_ShowsASettingsSurface()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        h.Tray.RaiseSettingsRequested();

        var surface = Assert.Single(h.CreatedSettingsSurfaces);
        Assert.Equal(1, surface.ShowCallCount);
    }

    [Fact]
    public void SettingsRequested_RoutesThroughTheSameDispatcherScheduler()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        var postCountBefore = h.Scheduler.PostCallCount;
        h.Tray.RaiseSettingsRequested();

        Assert.True(h.Scheduler.PostCallCount > postCountBefore);
    }

    // UI-017
    [Fact]
    public void SettingsRequested_TwiceWhileOpen_ActivatesExisting_NeverConstructsASecond()
    {
        // Gate 032-C2: a Fresh-state Start() now proactively opens Settings itself, which would
        // otherwise pre-empt this test's own first RaiseSettingsRequested(). Seeded to Ready here so
        // Start()'s own onboarding check does nothing, keeping this test's original ONE_CURRENT_
        // SETTINGS_WINDOW-on-explicit-request intent isolated from the new Fresh-onboarding behavior
        // (which has its own dedicated coverage in the Gate 032-C2 suite below).
        var h = CreateHarness(currentExecutablePath: "C:\\PRIVON\\PRIVON.exe");
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, ExpectedChromeManifestPath());
        h.RegistrationEnvironment.SeedManifest(ExpectedChromeManifestPath(), ExpectedChromeManifestJson("C:\\PRIVON\\PRIVON.exe"));
        h.Bridge.Start();
        h.Tray.RaiseSettingsRequested();
        var surface = Assert.Single(h.CreatedSettingsSurfaces);

        h.Tray.RaiseSettingsRequested();

        Assert.Single(h.CreatedSettingsSurfaces);
        Assert.Equal(1, surface.ActivateCallCount);
    }

    [Fact]
    public void Start_TrayShowThrows_SettingsSubscriptionIsRemoved()
    {
        var h = CreateHarness();
        h.Tray.ThrowOnShow = new InvalidOperationException("synthetic");

        Assert.ThrowsAny<Exception>(h.Bridge.Start);

        Assert.Equal(0, h.Tray.SettingsRequestedSubscriberCount);
    }

    [Fact]
    public void Dispose_ClosesOpenSettingsSurface()
    {
        var h = CreateHarness();
        h.Bridge.Start();
        h.Tray.RaiseSettingsRequested();
        var surface = Assert.Single(h.CreatedSettingsSurfaces);

        h.Bridge.Dispose();

        Assert.True(surface.IsClosed);
    }

    // SETTINGS_CLOSED_BEFORE_UI_BRIDGE_TEARDOWN -- Settings closes before the tray is disposed,
    // mirroring the existing decision-prompt-before-tray ordering this project already established.
    [Fact]
    public void Dispose_ClosesOpenSettingsSurfaceBeforeDisposingTray()
    {
        var h = CreateHarness();
        h.Bridge.Start();
        h.Tray.RaiseSettingsRequested();
        var surface = Assert.Single(h.CreatedSettingsSurfaces);

        var order = new List<string>();
        surface.OnClose = () => order.Add("settings");
        h.Tray.OnDispose = () => order.Add("tray");

        h.Bridge.Dispose();

        Assert.Equal(new[] { "settings", "tray" }, order);
    }

    [Fact]
    public void Dispose_UnsubscribesSettingsRequested_LaterRaiseDoesNothing()
    {
        var h = CreateHarness();
        h.Bridge.Start();

        h.Bridge.Dispose();

        Assert.Equal(0, h.Tray.SettingsRequestedSubscriberCount);

        var postCountBefore = h.Scheduler.PostCallCount;
        h.Tray.RaiseSettingsRequested();
        Assert.Equal(postCountBefore, h.Scheduler.PostCallCount);
    }

    // ==================================================================
    // ACCESSIBILITY / STRUCTURAL (composition-root-adjacent boundary)
    // ==================================================================

    [Theory]
    [InlineData(typeof(PrivonAppUiBridge))]
    [InlineData(typeof(ITrayIconSurface))]
    [InlineData(typeof(WindowsAutoStartCoordinator))]
    [InlineData(typeof(IWindowsAutoStartRegistration))]
    [InlineData(typeof(WindowsAutoStartRegistration))]
    [InlineData(typeof(NativeMessagingHostRegistrationCoordinator))]
    public void Types_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }

    // PrivonAppComposition itself must remain completely UI-free -- this bridge, not the
    // composition root, is what actually touches tray/UI concerns. Re-affirms
    // PrivonAppCompositionTests.CompositionRootSource_HasNoUiOrHotkeyMembers from this new type's
    // own side.
    [Fact]
    public void PrivonAppCompositionSource_StillHasNoTrayOrUiReferences()
    {
        var source = File.ReadAllText(FindAppSourceFile("PrivonAppComposition.cs"));
        Assert.DoesNotContain("Tray", source);
        Assert.DoesNotContain("NotifyIcon", source);
        Assert.DoesNotContain("DecisionPromptWindow", source);
    }

    // ==================================================================
    // PRIVACY / UI TEXT (section 28) -- structural source scans, since none of these UI types can
    // safely be constructed on an automated test thread (real WPF Window/WinForms NotifyIcon both
    // need a live STA/Dispatcher-pumped thread -- real rendering remains manual release QA).
    // ==================================================================

    private static readonly string[] ForbiddenUiTerms =
    [
        "Verified", "보호 완료", "검증 완료", "BypassOnce", "그대로 보내기", "원문 사용",
        "예외 등록", "신뢰 등록", "VerifyAsync",
    ];

    [Theory]
    [InlineData("DecisionPromptWindow.cs")]
    [InlineData("DecisionPromptCoordinator.cs")]
    [InlineData("WinFormsTrayIconSurface.cs")]
    [InlineData("PrivonAppUiBridge.cs")]
    public void NewUiSourceFiles_NeverContainForbiddenClaimsOrActions(string fileName)
    {
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile(fileName)));

        foreach (var term in ForbiddenUiTerms)
        {
            Assert.DoesNotContain(term, source);
        }
    }

    [Fact]
    public void TraySurface_TooltipText_IsNeutral_NeverAProtectionClaim()
    {
        var source = File.ReadAllText(FindAppSourceFile("WinFormsTrayIconSurface.cs"));
        Assert.Contains("실행 중", source);
        Assert.DoesNotContain("보호 중", source);
    }

    private static string FindAppSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate src/Privon.App from the test output directory.");

        return Path.Combine(dir.FullName, "src", "Privon.App");
    }

    private static string FindAppSourceFile(string fileName) =>
        Path.Combine(FindAppSourceDirectory(), fileName);

    private static string StripDocComments(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
