using Privon.App;
using Privon.Core;
using Privon.Detection;

namespace Privon.App.Tests;

// Phase 3C STEP40 -- DecisionPromptCoordinator regression: dispatcher hand-off + staleness recheck
// (Phase 3C STEP39.1 audit's frozen WPF_DISPATCHER_BOUNDARY/ACTIVE_SCOPE_RECHECK), single-current-
// prompt/replacement, Protect-All sequencing, user-close semantics, and subscription-lifetime
// disposal. Uses the REAL ClipboardDecisionScopeLifecycle/ClipboardDecisionSessionPublisher/
// ClipboardDecisionScope (via ClipboardDecisionPlan-driven publication) -- only the resolver,
// dispatcher scheduler, and prompt surface are hand-written fakes, matching the same "prove the
// real integration boundary, not a fabricated stand-in" discipline already established elsewhere
// in this test project.
public class DecisionPromptCoordinatorTests
{
    private static readonly ClipboardDecisionItem Item1 =
        new(new CanonicalValue(PiiType.Phone, "01011112222"), RiskLevel.Level1);
    private static readonly ClipboardDecisionItem Item2 =
        new(new CanonicalValue(PiiType.Email, "user@example.com"), RiskLevel.Level3);

    private static ClipboardDecisionPlan Plan(params ClipboardDecisionItem[] items) => new(items);

    private sealed class Harness
    {
        public required ClipboardDecisionScopeLifecycle Lifecycle { get; init; }
        public required ClipboardDecisionSessionPublisher Publisher { get; init; }
        public required FakeClipboardDecisionResolver Resolver { get; init; }
        public required FakeDispatcherScheduler Scheduler { get; init; }
        public required List<FakeDecisionPromptSurface> CreatedSurfaces { get; init; }
        public required DecisionPromptCoordinator Coordinator { get; init; }
    }

    private static Harness CreateHarness(bool runSchedulerSynchronously = true)
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var resolver = new FakeClipboardDecisionResolver();
        var scheduler = new FakeDispatcherScheduler { RunSynchronously = runSchedulerSynchronously };
        var createdSurfaces = new List<FakeDecisionPromptSurface>();

        var coordinator = new DecisionPromptCoordinator(publisher, lifecycle, resolver, scheduler, () =>
        {
            var surface = new FakeDecisionPromptSurface();
            createdSurfaces.Add(surface);
            return surface;
        });
        coordinator.Start();

        return new Harness
        {
            Lifecycle = lifecycle,
            Publisher = publisher,
            Resolver = resolver,
            Scheduler = scheduler,
            CreatedSurfaces = createdSurfaces,
            Coordinator = coordinator,
        };
    }

    // ==================================================================
    // DISPATCHER / STALENESS (section 24)
    // ==================================================================

    // ---- Case A: scope published -> dispatcher callback executes while exact scope still active
    // -> prompt shows ----
    [Fact]
    public void ScopePublished_DispatcherRunsWhileStillActive_ShowsPrompt()
    {
        var h = CreateHarness(runSchedulerSynchronously: false);
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));

        Assert.Equal(1, h.Scheduler.PendingCount);
        h.Scheduler.RunAllPending();

        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.Equal(1, surface.ShowCallCount);
    }

    // ---- Case B: scope published -> lifecycle advances BEFORE dispatcher callback executes ->
    // IsActive == false -> prompt never shows (no surface even constructed) ----
    [Fact]
    public void ScopePublished_LifecycleAdvancesBeforeDispatcherRuns_NeverShowsPrompt()
    {
        var h = CreateHarness(runSchedulerSynchronously: false);
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));

        // Supersede BEFORE the dispatcher callback actually runs.
        h.Lifecycle.AdvanceOnClipboardNotification();

        h.Scheduler.RunAllPending();

        Assert.Empty(h.CreatedSurfaces);
    }

    [Fact]
    public void StaleGenerationPublish_NoDispatchAtAll()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        h.Lifecycle.AdvanceOnClipboardNotification(); // g1 is now stale

        Assert.False(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));

        Assert.Equal(0, h.Scheduler.PostCallCount);
        Assert.Empty(h.CreatedSurfaces);
    }

    // ==================================================================
    // ONE CURRENT PROMPT / REPLACEMENT (sections 8, 26)
    // ==================================================================

    [Fact]
    public void NewerScopePublication_ClosesOlderPrompt_OnlyOneRemainsOpen()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text A", Plan(Item1)));
        var firstSurface = Assert.Single(h.CreatedSurfaces);
        Assert.False(firstSurface.IsClosed);

        var g2 = h.Lifecycle.AdvanceOnClipboardNotification(); // supersedes the first scope
        Assert.True(h.Publisher.TryPublish(g2, "raw text B", Plan(Item2)));

        Assert.Equal(2, h.CreatedSurfaces.Count);
        Assert.True(firstSurface.IsClosed);
        Assert.False(h.CreatedSurfaces[1].IsClosed);
    }

    [Fact]
    public async Task OldSurfaceAction_ResolvesOnlyAgainstItsOwnBoundOldScope_NeverTheNewerOne()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text A", Plan(Item1)));
        var firstScope = h.Lifecycle.GetActiveScope()!;
        var firstSurface = Assert.Single(h.CreatedSurfaces);

        var g2 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g2, "raw text B", Plan(Item2)));

        // The old (now-closed, now-detached) surface still fires its own click -- proves it can
        // never resolve the newer scope, only the exact old one it was bound to.
        h.Resolver.Enqueue(ClipboardDecisionActionResult.Stale());
        firstSurface.RaiseProtectAllRequested();
        await Task.Yield();

        var call = Assert.Single(h.Resolver.Calls);
        Assert.Same(firstScope, call.Scope);
    }

    // ==================================================================
    // PROTECT ALL (section 25)
    // ==================================================================

    [Fact]
    public async Task OneItemScope_ProtectCommitted_PromptCloses_NoSuccessMessage()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Resolver.Enqueue(ClipboardDecisionActionResult.Applied(clipboardMutated: true, scopeCommitted: true));

        surface.RaiseProtectAllRequested();
        await Task.Yield();

        var call = Assert.Single(h.Resolver.Calls);
        Assert.Equal(ClipboardDecisionIntent.Protect, call.Intent);
        Assert.True(surface.IsClosed);
        Assert.Null(surface.LastNeutralFailureMessage);
    }

    [Fact]
    public async Task MultiItemScope_IntermediateAppliedFalse_ThenFinalAppliedTrue_Closes()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1, Item2)));
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Resolver.Enqueue(ClipboardDecisionActionResult.Applied(clipboardMutated: false, scopeCommitted: false));
        h.Resolver.Enqueue(ClipboardDecisionActionResult.Applied(clipboardMutated: true, scopeCommitted: true));

        surface.RaiseProtectAllRequested();
        await Task.Yield();

        Assert.Equal(2, h.Resolver.Calls.Count);
        Assert.Equal(Item1, h.Resolver.Calls[0].Item);
        Assert.Equal(Item2, h.Resolver.Calls[1].Item);
        Assert.True(surface.IsClosed);
    }

    // SILENT_FAILURE_FIX (Phase 0.2G release-blocker fix): Stale used to close the surface with
    // NO feedback at all -- indistinguishable, from the user's point of view, from a genuine
    // committed success. This is exactly the misleading behavior a real live cross-app manual QA
    // observation surfaced (LEVEL3_PROTECT_SELF_STALE_ROOT_CAUSE): the popup "just disappeared"
    // after the user clicked "모두 보호," with the raw PII left completely unprotected on the
    // clipboard. Stale now falls through to the SAME neutral-failure-message path every other
    // non-committing outcome already used -- see AssertNonSuccessShowsNeutralFailure below, which
    // is now exercised by StaleOutcome_ShowsNeutralFailureOnly_NeverSuccessClaim too.
    [Fact]
    public async Task StaleOnFirstItem_StopsImmediately()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1, Item2)));
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Resolver.Enqueue(ClipboardDecisionActionResult.Stale());

        surface.RaiseProtectAllRequested();
        await Task.Yield();

        Assert.Single(h.Resolver.Calls);
        Assert.False(surface.IsClosed);
        Assert.Equal(DecisionPromptCoordinator.NeutralFailureMessage, surface.LastNeutralFailureMessage);
    }

    [Fact]
    public async Task StaleMidway_StopsBeforeLaterItemsAreAttempted()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1, Item2)));
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Resolver.Enqueue(ClipboardDecisionActionResult.Applied(clipboardMutated: false, scopeCommitted: false));
        h.Resolver.Enqueue(ClipboardDecisionActionResult.Stale());

        surface.RaiseProtectAllRequested();
        await Task.Yield();

        Assert.Equal(2, h.Resolver.Calls.Count); // never a third call for a hypothetical Item3
        Assert.False(surface.IsClosed);
        Assert.Equal(DecisionPromptCoordinator.NeutralFailureMessage, surface.LastNeutralFailureMessage);
    }

    // Four separate [Fact]s rather than a [Theory]/[MemberData] -- ClipboardDecisionActionResult
    // is internal, and a public theory method cannot take a less-accessible parameter type (CS0051).
    private async Task AssertNonSuccessShowsNeutralFailure(ClipboardDecisionActionResult failing)
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Resolver.Enqueue(failing);

        surface.RaiseProtectAllRequested();
        await Task.Yield();

        Assert.Equal(DecisionPromptCoordinator.NeutralFailureMessage, surface.LastNeutralFailureMessage);
        Assert.False(surface.IsClosed);
    }

    [Fact]
    public Task StaleOutcome_ShowsNeutralFailureOnly_NeverSuccessClaim() =>
        AssertNonSuccessShowsNeutralFailure(ClipboardDecisionActionResult.Stale());

    [Fact]
    public Task WriteFailedOutcome_ShowsNeutralFailureOnly_NeverSuccessClaim() =>
        AssertNonSuccessShowsNeutralFailure(ClipboardDecisionActionResult.WriteFailed());

    [Fact]
    public Task MutatedUnverifiedOutcome_ShowsNeutralFailureOnly_NeverSuccessClaim() =>
        AssertNonSuccessShowsNeutralFailure(ClipboardDecisionActionResult.MutatedUnverified());

    [Fact]
    public Task FailedOutcome_ShowsNeutralFailureOnly_NeverSuccessClaim() =>
        AssertNonSuccessShowsNeutralFailure(ClipboardDecisionActionResult.Failed());

    [Fact]
    public Task AwaitingSecondConfirmationOutcome_ShowsNeutralFailureOnly_NeverSuccessClaim() =>
        AssertNonSuccessShowsNeutralFailure(ClipboardDecisionActionResult.AwaitingSecondConfirmation());

    [Fact]
    public async Task UnexpectedResolverException_ShowsNeutralFailure_NeverPropagates()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Resolver.Enqueue(() => Task.FromException<ClipboardDecisionActionResult>(new InvalidOperationException("synthetic")));

        surface.RaiseProtectAllRequested();
        await Task.Yield();

        Assert.Equal(DecisionPromptCoordinator.NeutralFailureMessage, surface.LastNeutralFailureMessage);
    }

    [Fact]
    public async Task RepeatedClickBeforeFirstResolves_NeverProducesConcurrentDuplicateResolution()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));
        var surface = Assert.Single(h.CreatedSurfaces);

        var tcs = new TaskCompletionSource<ClipboardDecisionActionResult>();
        h.Resolver.Enqueue(() => tcs.Task);

        surface.RaiseProtectAllRequested();
        surface.RaiseProtectAllRequested(); // a second click before the first attempt resolved

        Assert.Single(h.Resolver.Calls);
        Assert.Equal(1, surface.DisableProtectActionCallCount);

        tcs.SetResult(ClipboardDecisionActionResult.Applied(clipboardMutated: false, scopeCommitted: true));
        await Task.Yield();
    }

    [Fact]
    public async Task ProtectAll_NeverEmitsBypassOnce()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1, Item2)));
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Resolver.Enqueue(ClipboardDecisionActionResult.Applied(clipboardMutated: false, scopeCommitted: false));
        h.Resolver.Enqueue(ClipboardDecisionActionResult.Applied(clipboardMutated: true, scopeCommitted: true));

        surface.RaiseProtectAllRequested();
        await Task.Yield();

        Assert.All(h.Resolver.Calls, call => Assert.Equal(ClipboardDecisionIntent.Protect, call.Intent));
    }

    // ==================================================================
    // USER CLOSING THE PROMPT (section 13)
    // ==================================================================

    [Fact]
    public void UserClosesPrompt_NoResolverActionOccurs()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.Close(); // simulates the user's own system close button

        Assert.Empty(h.Resolver.Calls);
    }

    [Fact]
    public void AfterUserCloses_ANewerPublicationCanStillShowANewPrompt()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text A", Plan(Item1)));
        var firstSurface = Assert.Single(h.CreatedSurfaces);
        firstSurface.Close();

        var g2 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g2, "raw text B", Plan(Item2)));

        Assert.Equal(2, h.CreatedSurfaces.Count);
        Assert.Equal(1, h.CreatedSurfaces[1].ShowCallCount);
    }

    // ==================================================================
    // SUBSCRIPTION LIFETIME / DISPOSE
    // ==================================================================

    [Fact]
    public void Dispose_UnsubscribesFromPublisher_LaterPublicationNeverDispatchesAgain()
    {
        var h = CreateHarness();
        h.Coordinator.Dispose();

        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));

        Assert.Equal(0, h.Scheduler.PostCallCount);
        Assert.Empty(h.CreatedSurfaces);
    }

    [Fact]
    public void Dispose_ClosesCurrentlyVisiblePrompt()
    {
        var h = CreateHarness();
        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Coordinator.Dispose();

        Assert.True(surface.IsClosed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var h = CreateHarness();
        h.Coordinator.Dispose();
        h.Coordinator.Dispose();
    }

    [Fact]
    public void Dispose_SafeWhenStartNeverCalled()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var coordinator = new DecisionPromptCoordinator(
            publisher, lifecycle, new FakeClipboardDecisionResolver(), new FakeDispatcherScheduler(),
            () => new FakeDecisionPromptSurface());

        coordinator.Dispose();
        coordinator.Dispose();
    }

    [Fact]
    public void Start_CalledTwice_Throws()
    {
        var h = CreateHarness();
        Assert.Throws<InvalidOperationException>(h.Coordinator.Start);
    }

    // ==================================================================
    // CONSTRUCTOR GUARDS
    // ==================================================================

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var resolver = new FakeClipboardDecisionResolver();
        var scheduler = new FakeDispatcherScheduler();
        Func<IDecisionPromptSurface> factory = () => new FakeDecisionPromptSurface();

        Assert.Throws<ArgumentNullException>(() => new DecisionPromptCoordinator(null!, lifecycle, resolver, scheduler, factory));
        Assert.Throws<ArgumentNullException>(() => new DecisionPromptCoordinator(publisher, null!, resolver, scheduler, factory));
        Assert.Throws<ArgumentNullException>(() => new DecisionPromptCoordinator(publisher, lifecycle, null!, scheduler, factory));
        Assert.Throws<ArgumentNullException>(() => new DecisionPromptCoordinator(publisher, lifecycle, resolver, null!, factory));
        Assert.Throws<ArgumentNullException>(() => new DecisionPromptCoordinator(publisher, lifecycle, resolver, scheduler, null!));
    }

    // ==================================================================
    // ACCESSIBILITY
    // ==================================================================

    [Theory]
    [InlineData(typeof(DecisionPromptCoordinator))]
    [InlineData(typeof(IDecisionPromptSurface))]
    [InlineData(typeof(IDispatcherScheduler))]
    [InlineData(typeof(IClipboardDecisionResolver))]
    public void Types_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }
}
