namespace Privon.App;

/// <summary>
/// Phase 0.2D (STEP61) -- how a completed clipboard/foreground evaluation attempt's own terminal
/// fact (a guarded-read or guarded-write outcome, or one of the direct pipeline facts documented on
/// <see cref="ClipboardEvaluationOutcomeClassifier"/>) should be reported back to
/// <c>IClipboardEvaluationLifecycle</c>: <c>Terminal</c> facts are reported via
/// <c>CompleteEvaluation</c> (this exact generation is done, no future retry will help), and
/// <c>Retryable</c> facts are reported via <c>AbandonEvaluation</c> (release the claim so a LATER,
/// independent trigger -- another clipboard notification, or a foreground-focus change -- may
/// attempt this same still-current generation again).
///
/// Declared with <see cref="Retryable"/> first so <c>default(ClipboardEvaluationDisposition)</c> is
/// never mistaken for a stable, resolved outcome -- the same discipline used throughout this
/// codebase (e.g. <c>ClipboardReadOutcome.NotRunning</c>, <c>ClipboardEvaluationState.NotEvaluated</c>).
/// </summary>
internal enum ClipboardEvaluationDisposition
{
    Retryable = 0,
    Terminal = 1,
}
