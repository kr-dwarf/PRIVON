using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Storage;
using Privon.Windows;

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
        public required List<FakeDecisionPromptSurface> CreatedSurfaces { get; init; }
        public required List<FakeSettingsSurface> CreatedSettingsSurfaces { get; init; }
        public required PrivonAppUiBridge Bridge { get; init; }
        public required string StorageRoot { get; init; }
    }

    private static Harness CreateHarness(string currentExecutablePath = "C:\\PRIVON\\PRIVON.exe")
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var resolver = new FakeClipboardDecisionResolver();
        var scheduler = new FakeDispatcherScheduler { RunSynchronously = true };
        var tray = new FakeTrayIconSurface();
        var autoStartRegistration = new FakeWindowsAutoStartRegistration();
        var autoStartCoordinator = new WindowsAutoStartCoordinator(autoStartRegistration, () => currentExecutablePath);
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
            });

        return new Harness
        {
            Lifecycle = lifecycle,
            Publisher = publisher,
            Resolver = resolver,
            Scheduler = scheduler,
            Tray = tray,
            AutoStartRegistration = autoStartRegistration,
            AutoStartCoordinator = autoStartCoordinator,
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

        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(null!, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, null!, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, null!, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, null!, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, null!, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, null!, autoStart, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, null!, categoryService, exceptionService, pipeline, isUnavailable, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, null!, exceptionService, pipeline, isUnavailable, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, null!, pipeline, isUnavailable, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, null!, isUnavailable, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, null!, settingsFactory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, factory, autoStart, categoryService, exceptionService, pipeline, isUnavailable, null!));
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
        var h = CreateHarness();
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
