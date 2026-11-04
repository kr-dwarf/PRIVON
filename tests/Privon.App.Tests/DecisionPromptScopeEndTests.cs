using Privon.App;
using Privon.Core;
using Privon.Detection;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. A decision prompt says "personal information was detected, press to protect". That sentence is true only while the decision it
// belongs to is alive: the next clipboard change (or a session lock) ends the decision, and from then on pressing the button can only be refused.
// These tests drive the REAL scope lifecycle, publisher and prompt coordinator with a fake surface, resolver and dispatcher.
public class DecisionPromptScopeEndTests
{
    private static readonly ClipboardDecisionItem Item =
        new(new CanonicalValue(PiiType.Phone, "01011112222"), RiskLevel.Level1);

    private sealed class Harness
    {
        public ClipboardDecisionScopeLifecycle Lifecycle { get; } = new();
        public ClipboardDecisionSessionPublisher Publisher { get; }
        public FakeClipboardDecisionResolver Resolver { get; } = new();
        public FakeDispatcherScheduler Scheduler { get; } = new() { RunSynchronously = true };
        public List<FakeDecisionPromptSurface> Surfaces { get; } = [];
        public DecisionPromptCoordinator Coordinator { get; }

        public Harness()
        {
            Publisher = new ClipboardDecisionSessionPublisher(Lifecycle);
            Coordinator = new DecisionPromptCoordinator(Publisher, Lifecycle, Resolver, Scheduler, () =>
            {
                var surface = new FakeDecisionPromptSurface();
                Surfaces.Add(surface);
                return surface;
            });
            Coordinator.Start();
        }

        /// <summary>A clipboard change that starts a new decision, exactly as the privacy coordinator does it.</summary>
        public ClipboardDecisionScope Decide(string rawText = "raw text")
        {
            long generation = Lifecycle.AdvanceOnClipboardNotification();
            Assert.True(Publisher.TryPublish(generation, rawText, new ClipboardDecisionPlan([Item])));
            return Lifecycle.GetActiveScope()!;
        }
    }

    [Fact]
    public void ANewClipboardChange_ThatEndsTheDecision_ClosesItsPrompt()
    {
        var h = new Harness();
        h.Decide();
        var prompt = h.Surfaces.Single();
        Assert.False(prompt.IsClosed);

        h.Lifecycle.AdvanceOnClipboardNotification();            // the user copies something else: nothing to decide any more

        Assert.True(prompt.IsClosed);
        Assert.Empty(h.Resolver.Calls);                          // closing resolves nothing and claims nothing
    }

    [Fact]
    public void ASessionLock_ThatEndsTheDecision_ClosesItsPrompt()
    {
        var h = new Harness();
        h.Decide();

        h.Lifecycle.Reset();

        Assert.True(h.Surfaces.Single().IsClosed);
    }

    [Fact]
    public async Task AClipboardChange_DuringAProtectAttempt_DoesNotCloseThePrompt_SoTheFailureIsStillShown()
    {
        var h = new Harness();
        h.Decide();
        var prompt = h.Surfaces.Single();
        var release = new TaskCompletionSource<ClipboardDecisionActionResult>();
        h.Resolver.Enqueue(() => release.Task);
        prompt.RaiseProtectAllRequested();                       // the attempt is now running

        h.Lifecycle.AdvanceOnClipboardNotification();            // ... and the clipboard changes under it
        Assert.False(prompt.IsClosed);                           // a prompt that vanishes under the user's press would look like a success

        release.SetResult(ClipboardDecisionActionResult.Stale());
        await WaitAsync(() => prompt.LastNeutralFailureMessage is not null);

        Assert.False(prompt.IsClosed);
        Assert.Equal(DecisionPromptCoordinator.NeutralFailureMessage, prompt.LastNeutralFailureMessage);
    }

    [Fact]
    public async Task ARefusedPrompt_ThatIsStillOnScreen_ClosesOnTheNextClipboardChange()
    {
        var h = new Harness();
        h.Decide();
        var prompt = h.Surfaces.Single();
        h.Resolver.Enqueue(ClipboardDecisionActionResult.Stale());
        prompt.RaiseProtectAllRequested();
        await WaitAsync(() => prompt.LastNeutralFailureMessage is not null);
        Assert.False(prompt.IsClosed);                           // "copy again" stays up until the user does

        h.Lifecycle.AdvanceOnClipboardNotification();            // ... and they do

        Assert.True(prompt.IsClosed);
    }

    [Fact]
    public void ANewDecision_ReplacesTheOldPrompt_AndTheNewPromptIsNotClosedByTheOldDecisionEnding()
    {
        var h = new Harness();
        h.Decide("first");
        h.Decide("second");                                      // advancing ends the first decision, then the second is published

        Assert.Equal(2, h.Surfaces.Count);
        Assert.True(h.Surfaces[0].IsClosed);
        Assert.False(h.Surfaces[1].IsClosed);                    // exactly one prompt, for the live decision
    }

    [Fact]
    public void AfterDispose_TheCoordinatorNoLongerReactsToTheLifecycle()
    {
        var h = new Harness();
        h.Decide();
        var prompt = h.Surfaces.Single();
        h.Coordinator.Dispose();
        Assert.True(prompt.IsClosed);
        int closes = prompt.CloseCallCount;

        h.Lifecycle.AdvanceOnClipboardNotification();

        Assert.Equal(closes, prompt.CloseCallCount);
    }

    // ===== the notification itself ====================================================================================================

    [Fact]
    public void TheLifecycle_RaisesTheEndOnce_ForTheActiveScopeItDropped_AfterReleasingItsLock()
    {
        var h = new Harness();
        var scope = h.Decide();
        var seen = new List<ClipboardDecisionScope>();
        bool lockFreeWhileRaised = false;
        h.Lifecycle.ActiveScopeEnded += (_, ended) =>
        {
            seen.Add(ended);
            // another thread can take the lifecycle's lock while the event is being raised: it is raised outside the lock
            lockFreeWhileRaised = Task.Run(() => h.Lifecycle.CurrentGeneration).Wait(TimeSpan.FromSeconds(5));
        };

        h.Lifecycle.AdvanceOnClipboardNotification();

        Assert.Same(scope, Assert.Single(seen));
        Assert.True(lockFreeWhileRaised);
        Assert.False(h.Lifecycle.IsActive(scope));
    }

    [Fact]
    public void TheLifecycle_RaisesNothing_WhenThereWasNoActiveScope_OrWhenOneIsPublished()
    {
        var h = new Harness();
        int raised = 0;
        h.Lifecycle.ActiveScopeEnded += (_, _) => raised++;

        h.Lifecycle.AdvanceOnClipboardNotification();            // nothing active
        h.Lifecycle.Reset();                                      // nothing active
        long g = h.Lifecycle.AdvanceOnClipboardNotification();
        Assert.True(h.Publisher.TryPublish(g, "raw", new ClipboardDecisionPlan([Item])));   // publishing is not an end

        Assert.Equal(0, raised);

        h.Lifecycle.Reset();                                      // now there is one
        Assert.Equal(1, raised);
        h.Lifecycle.Reset();                                      // and it is gone: not raised again
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ASubscriberThatThrows_NeverBreaksTheClipboardOrSessionLockCaller()
    {
        var h = new Harness();
        h.Decide();
        h.Lifecycle.ActiveScopeEnded += (_, _) => throw new InvalidOperationException("subscriber failure");
        long before = h.Lifecycle.CurrentGeneration;

        long after = h.Lifecycle.AdvanceOnClipboardNotification();   // would throw into the clipboard owner thread's callback

        Assert.Equal(before + 1, after);
        Assert.False(h.Lifecycle.HasActiveScope);
    }

    /// <summary>A lifecycle whose end notification the test raises by hand, to reproduce the cross-thread ordering the real one can produce.</summary>
    private sealed class HandRaisedEndLifecycle(ClipboardDecisionScopeLifecycle inner) : IClipboardDecisionScopeLifecycle, IClipboardDecisionScopeEndNotifier
    {
        public event EventHandler<ClipboardDecisionScope>? ActiveScopeEnded;
        public void Raise(ClipboardDecisionScope ended) => ActiveScopeEnded?.Invoke(this, ended);
        public long CurrentGeneration => inner.CurrentGeneration;
        public bool HasActiveScope => inner.HasActiveScope;
        public bool IsActive(ClipboardDecisionScope scope) => inner.IsActive(scope);
        public ClipboardDecisionScope? GetActiveScope() => inner.GetActiveScope();
        public bool TryPublish(long expectedGeneration, ClipboardDecisionScope proposedScope) => inner.TryPublish(expectedGeneration, proposedScope);
        public void Reset() => inner.Reset();
    }

    [Fact]
    public void ALateEndOfAnOlderDecision_NeverClosesTheNewerDecisionsPrompt()
    {
        // the older decision's end is handed off on the clipboard thread; the newer decision is published on the worker thread -- the newer prompt can be
        // shown BEFORE the older end is processed. The end names its scope, and only that scope's prompt may be closed.
        var real = new ClipboardDecisionScopeLifecycle();
        var lifecycle = new HandRaisedEndLifecycle(real);
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var surfaces = new List<FakeDecisionPromptSurface>();
        var scheduler = new FakeDispatcherScheduler { RunSynchronously = true };
        using var coordinator = new DecisionPromptCoordinator(publisher, lifecycle, new FakeClipboardDecisionResolver(), scheduler, () =>
        {
            var s = new FakeDecisionPromptSurface();
            surfaces.Add(s);
            return s;
        });
        coordinator.Start();

        long g1 = real.AdvanceOnClipboardNotification();
        Assert.True(publisher.TryPublish(g1, "first", new ClipboardDecisionPlan([Item])));
        var older = real.GetActiveScope()!;
        long g2 = real.AdvanceOnClipboardNotification();
        Assert.True(publisher.TryPublish(g2, "second", new ClipboardDecisionPlan([Item])));
        Assert.True(surfaces[0].IsClosed);                        // replaced by the newer decision's prompt
        Assert.False(surfaces[1].IsClosed);

        lifecycle.Raise(older);                                   // ... and only now does the older decision's end get processed

        Assert.False(surfaces[1].IsClosed);
    }

    [Fact]
    public void AnEndNotice_ForADecisionThatIsStillAlive_ClosesNothing()
    {
        // the notice is only a hand-off; the prompt re-checks the lifecycle itself before closing anything
        var real = new ClipboardDecisionScopeLifecycle();
        var lifecycle = new HandRaisedEndLifecycle(real);
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var surfaces = new List<FakeDecisionPromptSurface>();
        using var coordinator = new DecisionPromptCoordinator(publisher, lifecycle, new FakeClipboardDecisionResolver(),
            new FakeDispatcherScheduler { RunSynchronously = true }, () =>
            {
                var s = new FakeDecisionPromptSurface();
                surfaces.Add(s);
                return s;
            });
        coordinator.Start();
        long g = real.AdvanceOnClipboardNotification();
        Assert.True(publisher.TryPublish(g, "raw", new ClipboardDecisionPlan([Item])));

        lifecycle.Raise(real.GetActiveScope()!);                  // a spurious / early notice for the live decision

        Assert.False(surfaces.Single().IsClosed);
    }

    [Fact]
    public void AfterDispose_AnEndNotice_SchedulesNothing()
    {
        var real = new ClipboardDecisionScopeLifecycle();
        var lifecycle = new HandRaisedEndLifecycle(real);
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var scheduler = new FakeDispatcherScheduler { RunSynchronously = false };
        var coordinator = new DecisionPromptCoordinator(publisher, lifecycle, new FakeClipboardDecisionResolver(), scheduler, () => new FakeDecisionPromptSurface());
        coordinator.Start();
        long g = real.AdvanceOnClipboardNotification();
        Assert.True(publisher.TryPublish(g, "raw", new ClipboardDecisionPlan([Item])));
        var scope = real.GetActiveScope()!;
        scheduler.RunAllPending();
        coordinator.Dispose();

        lifecycle.Raise(scope);

        Assert.Equal(0, scheduler.PendingCount);                  // the coordinator has unsubscribed: it no longer even hands off
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(5);
        }
    }
}
