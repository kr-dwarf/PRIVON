using Privon.App;
using Privon.Core;
using Privon.Detection;
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
            CreateTempStorageRoot(), new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader());

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
        public required List<FakeDecisionPromptSurface> CreatedSurfaces { get; init; }
        public required PrivonAppUiBridge Bridge { get; init; }
    }

    private static Harness CreateHarness()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var resolver = new FakeClipboardDecisionResolver();
        var scheduler = new FakeDispatcherScheduler { RunSynchronously = true };
        var tray = new FakeTrayIconSurface();
        var createdSurfaces = new List<FakeDecisionPromptSurface>();

        var bridge = new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, () =>
        {
            var surface = new FakeDecisionPromptSurface();
            createdSurfaces.Add(surface);
            return surface;
        });

        return new Harness
        {
            Lifecycle = lifecycle,
            Publisher = publisher,
            Resolver = resolver,
            Scheduler = scheduler,
            Tray = tray,
            CreatedSurfaces = createdSurfaces,
            Bridge = bridge,
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

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var resolver = new FakeClipboardDecisionResolver();
        var scheduler = new FakeDispatcherScheduler();
        var tray = new FakeTrayIconSurface();
        Func<IDecisionPromptSurface> factory = () => new FakeDecisionPromptSurface();

        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(null!, lifecycle, resolver, scheduler, tray, factory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, null!, resolver, scheduler, tray, factory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, null!, scheduler, tray, factory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, null!, tray, factory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, null!, factory));
        Assert.Throws<ArgumentNullException>(() => new PrivonAppUiBridge(publisher, lifecycle, resolver, scheduler, tray, null!));
    }

    // ==================================================================
    // ACCESSIBILITY / STRUCTURAL (composition-root-adjacent boundary)
    // ==================================================================

    [Theory]
    [InlineData(typeof(PrivonAppUiBridge))]
    [InlineData(typeof(ITrayIconSurface))]
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
