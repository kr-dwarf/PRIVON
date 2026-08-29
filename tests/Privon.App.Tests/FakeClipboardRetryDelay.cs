using System.Collections.Concurrent;
using Privon.App;

namespace Privon.App.Tests;

// BUG-002 Gate 2B -- deterministic, wall-clock-free double for IClipboardRetryDelay. Hand-written,
// no mocking framework, matching every other Fake* in this project.
//
// Two modes, both fully test-driven -- neither ever sleeps for a fixed duration:
//   - BlockUntilReleased == false (default): DelayAsync completes IMMEDIATELY, so a retry-behavior
//     test observes the full retry sequence with zero elapsed wall-clock time.
//   - BlockUntilReleased == true: DelayAsync returns a Task that only completes when the test calls
//     ReleaseNext(), letting a test hold a retry "mid-delay" and deterministically interleave a
//     shutdown / a superseding clipboard notification / a target departure against it.
//
// WaitUntilEntered's timeout is a FAILURE-DETECTION bound only, never a "sleep and hope": when the
// production behavior under test exists, the underlying event is already signalled (or is signalled
// within microseconds) and the wait returns immediately; the timeout elapses only in the RED
// (not-yet-implemented) state, where it converts an otherwise-indefinite hang into a fast, readable
// assertion failure.
internal sealed class FakeClipboardRetryDelay : IClipboardRetryDelay
{
    private readonly ManualResetEventSlim _entered = new(initialState: false);
    private readonly ConcurrentQueue<TaskCompletionSource> _pending = new();
    private readonly object _gate = new();

    private int _callCount;

    /// <summary>Number of times DelayAsync has been invoked. Zero proves no autonomous retry
    /// occurred -- the delay is, by the Gate 2A contract, the FIRST thing a retry does.</summary>
    public int CallCount
    {
        get { lock (_gate) { return _callCount; } }
    }

    /// <summary>Every duration DelayAsync was asked for, in call order -- lets a test assert the
    /// exact fixed 250 ms production interval rather than merely "some delay happened."</summary>
    public List<TimeSpan> RequestedDurations { get; } = [];

    /// <summary>When true, DelayAsync blocks (asynchronously) until <see cref="ReleaseNext"/>.</summary>
    public bool BlockUntilReleased { get; set; }

    /// <summary>Number of delays currently held open awaiting <see cref="ReleaseNext"/>.</summary>
    public int PendingCount => _pending.Count;

    public Task DelayAsync(TimeSpan duration)
    {
        lock (_gate)
        {
            RequestedDurations.Add(duration);
            _callCount++;
        }

        if (!BlockUntilReleased)
        {
            _entered.Set();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Enqueued BEFORE the entered latch is set, so a test woken by WaitUntilEntered can never
        // observe "entered" while ReleaseNext would still find an empty queue.
        _pending.Enqueue(tcs);
        _entered.Set();
        return tcs.Task;
    }

    /// <summary>Blocks until DelayAsync has been entered at least once. See this type's own header
    /// comment for why the timeout is a failure-detection bound, not a sleep.</summary>
    public bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);

    /// <summary>Completes the OLDEST still-pending held delay (FIFO).</summary>
    public void ReleaseNext()
    {
        if (_pending.TryDequeue(out var tcs))
            tcs.SetResult();
    }

    /// <summary>Completes every still-pending held delay -- used in test teardown so a coordinator
    /// can never be left permanently blocked inside this fake.</summary>
    public void ReleaseAll()
    {
        while (_pending.TryDequeue(out var tcs))
            tcs.SetResult();
    }
}
