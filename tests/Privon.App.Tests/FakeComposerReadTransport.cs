using System.Collections.Concurrent;
using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IComposerReadTransport -- lets ClipboardComposerVerifier's
// VerifyAsync orchestration be exercised without any real UI Automation. No mocking framework --
// hand-written, matching the FakeClipboardReadTransport precedent already established in this
// project exactly (including its HoldReadsUntilReleased/ReleaseNextRead deterministic-hold shape).
internal sealed class FakeComposerReadTransport : IComposerReadTransport
{
    public List<string> CallLog { get; } = [];
    public List<ForegroundTargetSnapshot> ReceivedExpectedTargets { get; } = [];

    /// <summary>Result returned by ReadFocusedComposerTextAsync when
    /// <see cref="HoldReadsUntilReleased"/> is false (the default) -- immediate completion, no
    /// async gap.</summary>
    public ComposerTextReadResult NextReadResult { get; set; } =
        ComposerTextReadResult.Failure(ComposerReadOutcome.NotRunning);

    /// <summary>
    /// When true, ReadFocusedComposerTextAsync returns a Task that only completes when the test
    /// calls <see cref="ReleaseNextRead"/> -- lets a test deterministically hold a composer read
    /// "in-flight" rather than guessing at timing.
    /// </summary>
    public bool HoldReadsUntilReleased { get; set; }

    /// <summary>When set, <see cref="ReadFocusedComposerTextAsync"/> throws this instead of
    /// returning -- lets a test prove ClipboardComposerVerifier.VerifyAsync's own error boundary
    /// (Failed + gate release + pinned-pending cleanup) survives a composer-transport failure.</summary>
    public Exception? ThrowOnRead { get; set; }

    private readonly ConcurrentQueue<TaskCompletionSource<ComposerTextReadResult>> _heldReads = new();

    public int PendingHeldReadCount => _heldReads.Count;

    public Task<ComposerTextReadResult> ReadFocusedComposerTextAsync(ForegroundTargetSnapshot expectedTarget)
    {
        CallLog.Add(nameof(ReadFocusedComposerTextAsync));
        ReceivedExpectedTargets.Add(expectedTarget);

        if (ThrowOnRead is { } ex)
            throw ex;

        if (!HoldReadsUntilReleased)
            return Task.FromResult(NextReadResult);

        var tcs = new TaskCompletionSource<ComposerTextReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _heldReads.Enqueue(tcs);
        return tcs.Task;
    }

    /// <summary>Completes the OLDEST still-pending held read with the given result (FIFO).</summary>
    public void ReleaseNextRead(ComposerTextReadResult result)
    {
        if (_heldReads.TryDequeue(out var tcs))
            tcs.SetResult(result);
    }
}
