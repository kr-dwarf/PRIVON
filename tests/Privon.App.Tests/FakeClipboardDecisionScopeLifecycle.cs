using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardDecisionScopeLifecycle -- lets
// ClipboardDecisionActionResolverTests control exactly which IsActive call in a given attempt
// (initial validation, post-read, post-evaluation, choice linearization, pre-commit/pre-write,
// post-write) returns false (or throws) without needing to race a real
// ClipboardDecisionScopeLifecycle's lock-guarded generation/scope state against real timing. No
// mocking framework -- hand-written, matching every other Fake* in this project.
internal sealed class FakeClipboardDecisionScopeLifecycle : IClipboardDecisionScopeLifecycle
{
    private readonly Queue<bool> _isActiveQueue = new();

    public int IsActiveCallCount { get; private set; }

    /// <summary>Returned once <see cref="_isActiveQueue"/> is exhausted.</summary>
    public bool DefaultIsActiveResult { get; set; } = true;

    /// <summary>When set, <see cref="IsActive"/> throws this on the exact call number given by
    /// <see cref="ThrowOnIsActiveCallNumber"/> (1-based) -- lets a test prove ERROR_BOUNDARY for a
    /// failure discovered mid-attempt (e.g. strictly after a confirmed external write).</summary>
    public Exception? ThrowOnIsActiveAtCall { get; set; }
    public int? ThrowOnIsActiveCallNumber { get; set; }

    public long CurrentGeneration { get; set; }
    public bool HasActiveScope { get; set; }
    public ClipboardDecisionScope? ActiveScopeToReturn { get; set; }
    public bool TryPublishResult { get; set; } = true;
    public int TryPublishCallCount { get; private set; }
    public int ResetCallCount { get; private set; }
    public int GetActiveScopeCallCount { get; private set; }

    /// <summary>Queues the NEXT <see cref="IsActive"/> return value (FIFO) -- once the queue is
    /// empty, <see cref="DefaultIsActiveResult"/> is returned for every subsequent call.</summary>
    public void EnqueueIsActiveResult(bool value) => _isActiveQueue.Enqueue(value);

    public bool IsActive(ClipboardDecisionScope scope)
    {
        IsActiveCallCount++;
        if (ThrowOnIsActiveCallNumber == IsActiveCallCount && ThrowOnIsActiveAtCall is { } ex)
            throw ex;
        return _isActiveQueue.Count > 0 ? _isActiveQueue.Dequeue() : DefaultIsActiveResult;
    }

    public ClipboardDecisionScope? GetActiveScope()
    {
        GetActiveScopeCallCount++;
        return ActiveScopeToReturn;
    }

    public bool TryPublish(long expectedGeneration, ClipboardDecisionScope proposedScope)
    {
        TryPublishCallCount++;
        return TryPublishResult;
    }

    public void Reset() => ResetCallCount++;
}
