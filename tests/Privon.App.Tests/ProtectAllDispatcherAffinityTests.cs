using Privon.App;
using Privon.Core;
using Privon.Detection;

namespace Privon.App.Tests;

// Phase 3C STEP40.2 -- the real Dispatcher-affinity regression an independent pre-release audit
// asked for: proves DecisionPromptCoordinator.RunProtectAllAsync's post-await surface touches
// (Close/ShowNeutralFailure) genuinely execute on the correct WPF Dispatcher thread, even though
// the resolver's own Task genuinely suspends and later completes from a DIFFERENT (ThreadPool)
// thread -- exactly the real production shape (ClipboardDecisionActionResolver.ResolveAsync's own
// real async I/O), which the existing FakeDecisionPromptSurface-based
// DecisionPromptCoordinatorTests cannot detect (a plain C# object has no thread affinity of its
// own to violate).
//
// GENUINE_SUSPENSION (critical to this regression actually meaning anything): each resolver result
// is delivered via an explicit TaskCompletionSource that the test completes ONLY after confirming
// (via FakeClipboardDecisionResolver.OnCallStarted) that RunProtectAllAsync has already reached
// and genuinely suspended at its own `await` -- an already-completed Task (e.g. a bare
// Task.Run(() => value) that finishes before the `await` is even reached) would let the
// continuation run synchronously on the SAME (Dispatcher) thread that started it, which would
// make this regression pass even against the original, unfixed defect. This file's own
// GenuineSuspensionSanityCheck test directly proves this requirement is met.
//
// Uses a REAL, dedicated STA thread pumping a REAL WPF Dispatcher (DispatcherAffineTestHost) and a
// REAL DispatcherObject-derived surface (ThreadAffineDecisionPromptSurface) whose VerifyAccess()
// calls are the actual WPF thread-affinity mechanism -- never approximated. No
// System.Windows.Application is ever constructed. The production DecisionPromptCoordinator/
// ClipboardDecisionScopeLifecycle/ClipboardDecisionSessionPublisher are used unmodified; only the
// resolver is a fake and the dispatcher scheduler is the test-only RealDispatcherScheduler
// (targeting the dedicated test Dispatcher instead of System.Windows.Application.Current).
public class ProtectAllDispatcherAffinityTests
{
    private static readonly ClipboardDecisionItem Item1 =
        new(new CanonicalValue(PiiType.Phone, "01011112222"), RiskLevel.Level1);

    private static ClipboardDecisionPlan Plan(params ClipboardDecisionItem[] items) => new(items);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class Harness : IDisposable
    {
        public required DispatcherAffineTestHost Host { get; init; }
        public required ClipboardDecisionScopeLifecycle Lifecycle { get; init; }
        public required ClipboardDecisionSessionPublisher Publisher { get; init; }
        public required FakeClipboardDecisionResolver Resolver { get; init; }
        public required DecisionPromptCoordinator Coordinator { get; init; }
        public required TaskCompletionSource<ThreadAffineDecisionPromptSurface> SurfaceReady { get; init; }

        public void Dispose()
        {
            Coordinator.Dispose();
            Host.Dispose();
        }
    }

    private static Harness CreateHarness()
    {
        var host = new DispatcherAffineTestHost();
        var scheduler = new RealDispatcherScheduler(host.Dispatcher);
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var resolver = new FakeClipboardDecisionResolver();
        var surfaceReady = new TaskCompletionSource<ThreadAffineDecisionPromptSurface>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var coordinator = new DecisionPromptCoordinator(publisher, lifecycle, resolver, scheduler, () =>
        {
            var surface = new ThreadAffineDecisionPromptSurface();
            surfaceReady.TrySetResult(surface);
            return surface;
        });
        coordinator.Start();

        return new Harness
        {
            Host = host,
            Lifecycle = lifecycle,
            Publisher = publisher,
            Resolver = resolver,
            Coordinator = coordinator,
            SurfaceReady = surfaceReady,
        };
    }

    // Publishes a scope, clicks "모두 보호" (via a synchronous Dispatcher.Invoke, exactly how a
    // real WPF Button.Click always fires on its own Dispatcher thread), then delivers each queued
    // resolver result ONLY after confirming (via OnCallStarted) that RunProtectAllAsync has
    // genuinely suspended waiting for it -- and delivers it from an explicit Task.Run (a
    // guaranteed different, ThreadPool thread), never inline. Finally waits (deterministic TCS
    // signal, no polling) for the terminal Close/ShowNeutralFailure to have actually run.
    private static async Task<ThreadAffineDecisionPromptSurface> RunScenarioAsync(
        Harness h, ClipboardDecisionActionResult[] results, params ClipboardDecisionItem[] items)
    {
        var pending = new TaskCompletionSource<ClipboardDecisionActionResult>[results.Length];
        var callStarted = new TaskCompletionSource[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            pending[i] = new TaskCompletionSource<ClipboardDecisionActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            callStarted[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thisPending = pending[i];
            h.Resolver.Enqueue(() => thisPending.Task);
        }

        var nextCallIndex = 0;
        h.Resolver.OnCallStarted = () => callStarted[nextCallIndex++].TrySetResult();

        var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(items)));

        var surface = await h.SurfaceReady.Task.WaitAsync(Timeout);

        var mutationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        surface.OnMutation = () => mutationSignal.TrySetResult();

        h.Host.Dispatcher.Invoke(surface.RaiseProtectAllRequested);

        for (var i = 0; i < results.Length; i++)
        {
            // GENUINE_SUSPENSION: only proceed once RunProtectAllAsync has actually reached and
            // suspended at this call's own await -- never assume it happened instantly.
            await callStarted[i].Task.WaitAsync(Timeout);

            var value = results[i];
            var thisPending = pending[i];
            // Deliver the result from a GUARANTEED different (ThreadPool) thread -- never inline
            // on the Dispatcher thread that is awaiting it.
            await Task.Run(() => thisPending.SetResult(value));
        }

        await mutationSignal.Task.WaitAsync(Timeout);
        return surface;
    }

    // ==================================================================
    // Sanity check for the regression itself: proves GENUINE_SUSPENSION actually happens (the
    // continuation resumes on a DIFFERENT thread than the one that started it) -- without this,
    // the A-D tests below would prove nothing.
    //
    // Deliberately independent of DecisionPromptCoordinator/ThreadAffineDecisionPromptSurface: the
    // fixed coordinator ALWAYS re-marshals surface touches through _scheduler.Post, so observing
    // the continuation thread via the surface itself would only ever show the POST-marshal thread
    // (the dispatcher thread), regardless of whether the raw resolver-await continuation actually
    // hopped threads underneath. This check instead reproduces the exact same
    // BeginInvoke-then-Invoke-then-await-then-cross-thread-SetResult shape directly against the
    // real Dispatcher, with no marshaling in between, to prove the TEST ENVIRONMENT genuinely
    // produces a cross-thread continuation here (the precondition the A-D tests below rely on).
    [Fact]
    public async Task GenuineSuspensionSanityCheck_ContinuationThreadDiffersFromDispatcherThread()
    {
        using var host = new DispatcherAffineTestHost();
        var dispatcher = host.Dispatcher;

        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int continuationThreadId = -1;
        EventHandler? clicked = null;

        // FIRST dispatcher operation (posted, async -- like HandleScopeOnDispatcher via
        // IDispatcherScheduler.Post/BeginInvoke): wires up the event handler closure.
        await dispatcher.InvokeAsync(() =>
        {
            clicked = async (_, _) =>
            {
                callStarted.TrySetResult();
                await pending.Task.ConfigureAwait(false);
                continuationThreadId = Environment.CurrentManagedThreadId;
                done.TrySetResult();
            };
        }).Task.WaitAsync(Timeout);

        // SECOND, separate dispatcher operation (synchronous Invoke -- like
        // ThreadAffineDecisionPromptSurface.RaiseProtectAllRequested): fires the event wired up
        // in the FIRST operation, exactly like a real WPF Button.Click.
        dispatcher.Invoke(() => clicked?.Invoke(null, EventArgs.Empty));
        await callStarted.Task.WaitAsync(Timeout);

        await Task.Run(() => pending.SetResult(42));
        await done.Task.WaitAsync(Timeout);

        Assert.NotEqual(host.ThreadId, continuationThreadId);
    }

    // ==================================================================
    // A. final committed success -> surface close on Dispatcher
    // ==================================================================
    [Fact]
    public async Task CommittedSuccess_ClosesSurface_OnDispatcherThread_NoAffinityViolation()
    {
        using var h = CreateHarness();

        var surface = await RunScenarioAsync(
            h, [ClipboardDecisionActionResult.Applied(clipboardMutated: true, scopeCommitted: true)], Item1);

        Assert.Null(surface.ThreadAffinityViolation);
        Assert.True(surface.IsClosed);
        Assert.Equal(1, surface.CloseCallCount);
    }

    // ==================================================================
    // B. Stale -> close on Dispatcher
    // ==================================================================
    [Fact]
    public async Task Stale_ClosesSurface_OnDispatcherThread_NoAffinityViolation()
    {
        using var h = CreateHarness();

        var surface = await RunScenarioAsync(h, [ClipboardDecisionActionResult.Stale()], Item1);

        Assert.Null(surface.ThreadAffinityViolation);
        Assert.True(surface.IsClosed);
        Assert.Null(surface.LastNeutralFailureMessage);
    }

    // ==================================================================
    // C. resolver failure result -> neutral failure UI on Dispatcher
    // ==================================================================
    [Fact]
    public async Task ResolverFailureResult_ShowsNeutralFailure_OnDispatcherThread_NoAffinityViolation()
    {
        using var h = CreateHarness();

        var surface = await RunScenarioAsync(h, [ClipboardDecisionActionResult.WriteFailed()], Item1);

        Assert.Null(surface.ThreadAffinityViolation);
        Assert.Equal(DecisionPromptCoordinator.NeutralFailureMessage, surface.LastNeutralFailureMessage);
        Assert.False(surface.IsClosed);
    }

    // ==================================================================
    // D. unexpected resolver exception -> contained, neutral failure UI on Dispatcher, no
    // unobserved exception
    // ==================================================================
    [Fact]
    public async Task UnexpectedResolverException_ContainedOnDispatcherThread_NoUnobservedTaskException()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            unobserved.Add(e.Exception);
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            using var h = CreateHarness();

            var pending = new TaskCompletionSource<ClipboardDecisionActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Resolver.Enqueue(() => pending.Task);
            h.Resolver.OnCallStarted = () => callStarted.TrySetResult();

            var g1 = h.Lifecycle.AdvanceOnClipboardNotification();
            Assert.True(h.Publisher.TryPublish(g1, "raw text", Plan(Item1)));
            var surface = await h.SurfaceReady.Task.WaitAsync(Timeout);

            var mutationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            surface.OnMutation = () => mutationSignal.TrySetResult();

            h.Host.Dispatcher.Invoke(surface.RaiseProtectAllRequested);
            await callStarted.Task.WaitAsync(Timeout);

            // The resolver's Task itself faults (a "resolver execution unexpectedly throws
            // outside its normal result contract" scenario) -- delivered from a genuinely
            // different thread, exactly like the other scenarios.
            await Task.Run(() => pending.SetException(new InvalidOperationException("synthetic unexpected resolver failure")));

            await mutationSignal.Task.WaitAsync(Timeout);

            Assert.Null(surface.ThreadAffinityViolation);
            Assert.Equal(DecisionPromptCoordinator.NeutralFailureMessage, surface.LastNeutralFailureMessage);
            Assert.False(surface.IsClosed);

            // Encourage prompt finalization of any abandoned faulted Task so an unobserved
            // exception (if the containment were broken) would actually surface here rather than
            // silently later.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.Empty(unobserved);
    }

    // ==================================================================
    // Multi-item sequencing still marshals EVERY continuation correctly, including the
    // intermediate Applied(false) step that lets the loop continue (and call the resolver again)
    // on a ThreadPool thread before the final Dispatcher-marshaled close.
    // ==================================================================
    [Fact]
    public async Task MultiItem_IntermediateAppliedFalse_ThenFinalClose_NeverViolatesAffinity()
    {
        var item2 = new ClipboardDecisionItem(new CanonicalValue(PiiType.Email, "user@example.com"), RiskLevel.Level3);
        using var h = CreateHarness();

        var surface = await RunScenarioAsync(
            h,
            [
                ClipboardDecisionActionResult.Applied(clipboardMutated: false, scopeCommitted: false),
                ClipboardDecisionActionResult.Applied(clipboardMutated: true, scopeCommitted: true),
            ],
            Item1, item2);

        Assert.Null(surface.ThreadAffinityViolation);
        Assert.True(surface.IsClosed);
        Assert.Equal(2, h.Resolver.Calls.Count);
    }
}
