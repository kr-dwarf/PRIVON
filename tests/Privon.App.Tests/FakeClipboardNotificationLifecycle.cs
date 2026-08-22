using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardNotificationLifecycle -- lets
// ClipboardPrivacyCoordinator's callback/dispatch orchestration be exercised without depending
// on the real lock-based ClipboardDecisionScopeLifecycle. No mocking framework -- hand-written,
// matching every other Fake* in this project.
//
// Phase 0.2D (STEP61): widened to also implement IClipboardEvaluationLifecycle (now part of
// IClipboardNotificationLifecycle) with the SAME tri-state (NotEvaluated/InProgress/Evaluated)
// generation-gated claim semantics ClipboardDecisionScopeLifecycle itself already implements
// (Phase 0.2C) -- so a coordinator-boundary test exercising this fake sees realistic
// TryBeginEvaluation/CompleteEvaluation/AbandonEvaluation behavior without needing the real,
// lock-based production type. Tests that need to prove the actual cross-thread/cross-trigger race
// guarantees still use the REAL ClipboardDecisionScopeLifecycle directly (see the existing
// SessionLockFlavored_... precedent in ClipboardPrivacyCoordinatorTests).
internal sealed class FakeClipboardNotificationLifecycle : IClipboardNotificationLifecycle
{
    public int CallCount { get; private set; }
    public int TryBeginEvaluationCallCount { get; private set; }
    public int CompleteEvaluationCallCount { get; private set; }
    public int AbandonEvaluationCallCount { get; private set; }

    private long _generation;
    private ClipboardEvaluationState _evaluationState;

    public long CurrentGeneration => _generation;

    public long AdvanceOnClipboardNotification()
    {
        CallCount++;
        _generation++;
        _evaluationState = ClipboardEvaluationState.NotEvaluated;
        return _generation;
    }

    public bool TryBeginEvaluation(long expectedGeneration)
    {
        TryBeginEvaluationCallCount++;
        if (expectedGeneration != _generation) return false;
        if (_evaluationState != ClipboardEvaluationState.NotEvaluated) return false;
        _evaluationState = ClipboardEvaluationState.InProgress;
        return true;
    }

    public void CompleteEvaluation(long claimedGeneration)
    {
        CompleteEvaluationCallCount++;
        if (claimedGeneration != _generation) return;
        if (_evaluationState != ClipboardEvaluationState.InProgress) return;
        _evaluationState = ClipboardEvaluationState.Evaluated;
    }

    public void AbandonEvaluation(long claimedGeneration)
    {
        AbandonEvaluationCallCount++;
        if (claimedGeneration != _generation) return;
        if (_evaluationState != ClipboardEvaluationState.InProgress) return;
        _evaluationState = ClipboardEvaluationState.NotEvaluated;
    }
}
