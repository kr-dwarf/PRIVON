using Privon.App;

namespace Privon.App.Tests;

// Deterministic double for IClipboardDecisionResolver -- lets DecisionPromptCoordinatorTests drive
// the Protect-All sequencing (multi-item Applied(false)-then-Applied(true), Stale-stops-immediately,
// write/failure-shows-neutral-message, double-click-in-flight-guard) without needing the real
// ClipboardDecisionActionResolver's full dependency graph. No mocking framework -- hand-written,
// matching every other Fake* in this project.
internal sealed class FakeClipboardDecisionResolver : IClipboardDecisionResolver
{
    private readonly Queue<Func<Task<ClipboardDecisionActionResult>>> _results = new();

    public List<(ClipboardDecisionScope Scope, ClipboardDecisionItem Item, ClipboardDecisionIntent Intent)> Calls { get; } = new();

    /// <summary>Phase 3C STEP40.2 -- optional hook invoked synchronously the instant
    /// <see cref="ResolveAsync"/> is entered (before the queued factory is even invoked) -- lets a
    /// test know precisely when a call has genuinely started, so it can complete a
    /// <see cref="TaskCompletionSource{TResult}"/>-backed result from a deliberately different
    /// thread only AFTER the caller has already reached and suspended at its own <c>await</c>
    /// (ProtectAllDispatcherAffinityTests' own genuine-cross-thread-suspension requirement).</summary>
    public Action? OnCallStarted { get; set; }

    public void Enqueue(ClipboardDecisionActionResult result) => _results.Enqueue(() => Task.FromResult(result));

    public void Enqueue(Func<Task<ClipboardDecisionActionResult>> resultFactory) => _results.Enqueue(resultFactory);

    public Task<ClipboardDecisionActionResult> ResolveAsync(
        ClipboardDecisionScope scope, ClipboardDecisionItem item, ClipboardDecisionIntent intent)
    {
        Calls.Add((scope, item, intent));
        OnCallStarted?.Invoke();
        if (_results.Count == 0)
            throw new InvalidOperationException("FakeClipboardDecisionResolver: no queued result for this ResolveAsync call.");
        return _results.Dequeue()();
    }
}
