namespace Privon.App;

/// <summary>
/// BUG-002 Gate 2C -- the smallest non-PII signal <see cref="ClipboardPrivacyCoordinator"/>'s
/// single-attempt pipeline (<c>RunPrivacyPipelineAsync</c>) hands back to its own retry controller
/// (<c>ProcessWorkItemAsync</c>). Carries NOTHING beyond this one classification -- no
/// <c>ClipboardTextSnapshot</c>, no <c>ClipboardWritePlan</c>/replacement text, no raw clipboard
/// string of any kind, and no <see cref="ClipboardReadOutcome"/>/<see cref="Privon.Windows.ClipboardWriteOutcome"/>
/// value itself (those were already fully consumed, against the FROZEN
/// <see cref="ClipboardEvaluationOutcomeClassifier"/>, by the single-attempt method before this is
/// returned).
///
/// Declared with <see cref="Done"/> first, matching this codebase's established "safe value first"
/// discipline (e.g. <c>ClipboardReadOutcome.NotRunning</c>, <c>ClipboardEvaluationState.NotEvaluated</c>):
/// if a future code path ever failed to set this explicitly, the fail-closed
/// <c>default(ClipboardAttemptOutcome)</c> stops the retry controller, never accidentally starts an
/// unbounded loop.
///
/// This type deliberately collapses three conceptually distinct terminal reasons (a real success or
/// published decision, a "newer/changed truth already exists -- wait for a real external trigger"
/// disposition, and a "not a known-transient failure -- stay fail-closed" disposition) into the SAME
/// <see cref="Done"/> value: the retry controller's own behavior for all three is identical (stop,
/// do nothing further), so a finer split would only duplicate information the controller never acts
/// on differently -- see <see cref="ClipboardAutonomousRetryClassifier"/>'s own doc for where that
/// finer distinction actually lives.
/// </summary>
internal enum ClipboardAttemptOutcome
{
    Done,
    AutonomousRetryEligible,
}
