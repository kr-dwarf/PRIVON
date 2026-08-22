using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS/Dispatcher-free double for IDispatcherScheduler -- lets
// DecisionPromptCoordinatorTests control exactly when a scheduled action actually runs, without
// needing a real WPF Dispatcher or arbitrary sleeps (Phase 3C STEP40's DISPATCHER_STALENESS_TESTS).
// No mocking framework -- hand-written, matching every other Fake* in this project.
internal sealed class FakeDispatcherScheduler : IDispatcherScheduler
{
    private readonly Queue<Action> _pending = new();

    public int PostCallCount { get; private set; }
    public int PendingCount => _pending.Count;

    /// <summary>When true (test default is false unless set), <see cref="Post"/> runs the action
    /// immediately, synchronously -- simulating a dispatcher that has already executed it by the
    /// time the caller observes anything. When false, the action is only queued -- a test must
    /// call <see cref="RunNextPending"/>/<see cref="RunAllPending"/> to simulate the dispatcher
    /// actually running it later, possibly after other state has changed in between.</summary>
    public bool RunSynchronously { get; set; }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PostCallCount++;
        if (RunSynchronously)
        {
            action();
        }
        else
        {
            _pending.Enqueue(action);
        }
    }

    public void RunNextPending()
    {
        if (_pending.Count == 0)
            throw new InvalidOperationException("No pending scheduled action.");
        _pending.Dequeue()();
    }

    public void RunAllPending()
    {
        while (_pending.Count > 0)
        {
            RunNextPending();
        }
    }
}
