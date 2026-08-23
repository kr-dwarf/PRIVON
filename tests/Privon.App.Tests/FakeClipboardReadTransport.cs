using System.Collections.Concurrent;
using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardReadTransport -- lets ClipboardPrivacyCoordinator's
// dispatch/target-gate/guarded-read orchestration be exercised without any real Windows clipboard
// or foreground state. No mocking framework -- hand-written, matching the FakeClipboardMonitorNative/
// FakeClipboardTextNative/FakeForegroundTargetSource precedent already established in
// Privon.Windows.IntegrationTests.
internal sealed class FakeClipboardReadTransport : IClipboardReadTransport
{
    public event EventHandler<ClipboardChangeNotification>? Changed;

    public List<string> CallLog { get; } = [];
    public List<ForegroundTargetSnapshot> ReceivedExpectedTargets { get; } = [];

    public bool StartCalled { get; private set; }
    public bool StopCalled { get; private set; }
    public bool ThrowOnStart { get; set; }

    /// <summary>Phase 0.2D (STEP61) -- when true, Stop() throws instead of returning, letting a
    /// coordinator-cleanup-retry test prove a failed source Stop() is genuinely retried by a later
    /// Stop()/Dispose() call rather than silently treated as done.</summary>
    public bool ThrowOnStop { get; set; }

    /// <summary>Number of times Stop() has actually been called -- distinct from the boolean
    /// StopCalled so a retry test can assert Stop() was attempted MORE THAN once.</summary>
    public int StopCallCount { get; private set; }

    /// <summary>Result returned by ReadTextSnapshotAsync when <see cref="HoldReadsUntilReleased"/>
    /// is false (the default) -- immediate completion, no async gap.</summary>
    public ClipboardTextReadResult NextReadResult { get; set; } =
        ClipboardTextReadResult.Failure(ClipboardReadOutcome.NotRunning);

    /// <summary>
    /// When true, ReadTextSnapshotAsync returns a Task that only completes when the test calls
    /// <see cref="ReleaseNextRead"/> -- lets a test deterministically hold a read "in-flight"
    /// rather than guessing at timing.
    /// </summary>
    public bool HoldReadsUntilReleased { get; set; }

    private readonly ConcurrentQueue<TaskCompletionSource<ClipboardTextReadResult>> _heldReads = new();

    public int PendingHeldReadCount => _heldReads.Count;

    /// <summary>Simulates the underlying Windows owner thread raising Changed synchronously.</summary>
    public void RaiseChanged(ClipboardChangeNotification notification) => Changed?.Invoke(this, notification);

    public void Start()
    {
        CallLog.Add(nameof(Start));
        StartCalled = true;
        if (ThrowOnStart)
            throw new InvalidOperationException("Synthetic transport.Start() failure for test.");
    }

    public void Stop()
    {
        CallLog.Add(nameof(Stop));
        StopCalled = true;
        StopCallCount++;
        if (ThrowOnStop)
            throw new InvalidOperationException("Synthetic transport.Stop() failure for test.");
    }

    public Task<ClipboardTextReadResult> ReadTextSnapshotAsync(ForegroundTargetSnapshot expectedTarget)
    {
        CallLog.Add(nameof(ReadTextSnapshotAsync));
        ReceivedExpectedTargets.Add(expectedTarget);

        if (!HoldReadsUntilReleased)
            return Task.FromResult(NextReadResult);

        var tcs = new TaskCompletionSource<ClipboardTextReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _heldReads.Enqueue(tcs);
        return tcs.Task;
    }

    /// <summary>Completes the OLDEST still-pending held read with the given result (FIFO).</summary>
    public void ReleaseNextRead(ClipboardTextReadResult result)
    {
        if (_heldReads.TryDequeue(out var tcs))
            tcs.SetResult(result);
    }
}
