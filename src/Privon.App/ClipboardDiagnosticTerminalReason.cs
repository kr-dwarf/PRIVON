namespace Privon.App;

/// <summary>
/// Phase 3C STEP41 -- the exact reason one clipboard attempt ended, recorded once per attempt that
/// reached the coordinator's worker (see <see cref="ClipboardDiagnosticStage.AttemptTerminal"/>).
/// Every value corresponds to an actual, already-existing early-return/terminal branch in
/// <c>ClipboardPrivacyCoordinator.ProcessNotificationAsync</c>/<c>RunWorkerAsync</c> -- none of
/// these are invented stages; each is named after the real source-level condition that produced it.
///
/// Declared with <see cref="None"/> first so <c>default(ClipboardDiagnosticTerminalReason)</c> is
/// never mistaken for a real terminal outcome -- the same discipline used throughout this codebase.
/// </summary>
internal enum ClipboardDiagnosticTerminalReason
{
    None,

    /// <summary>The notification's own <c>HasUnicodeText</c> was <see langword="false"/> -- the
    /// coordinator's callback discarded it before enqueueing (RECEIVED_BUT_SUPPRESSED family).</summary>
    NonTextNotification,

    /// <summary>The captured foreground target did not pass <c>TargetGate.IsSupportedTarget</c> --
    /// no guarded read was even attempted.</summary>
    UnauthorizedTarget,

    /// <summary>The guarded clipboard read's own <c>Outcome</c> was not <c>Success</c> -- see the
    /// paired <see cref="ClipboardDiagnosticEvent.ReadOutcome"/> for the exact
    /// <c>Privon.Windows.ClipboardReadOutcome</c> value (Busy/FormatUnavailable/NativeFailure/
    /// MalformedData/TargetUnavailable/TargetChanged).</summary>
    ReadRejected,

    /// <summary>The real Detection/Trust/Policy chain ran to completion and produced neither a
    /// <see cref="ClipboardWritePlan"/> nor a <see cref="ClipboardDecisionPlan"/> -- either no PII
    /// was detected at all, or every detected candidate resolved to base-policy Bypass. See the
    /// paired <see cref="ClipboardDiagnosticEvent.CandidateCount"/>/<c>BypassCount</c> to
    /// distinguish the two.</summary>
    NoActionRequired,

    /// <summary>A <see cref="ClipboardWritePlan"/> existed, but the successful guarded read's own
    /// sequence was not reliable -- the guarded write was never attempted at all.</summary>
    WriteSkippedUnreliableSequence,

    /// <summary>The guarded clipboard write was attempted but was not classified as a verified
    /// rewrite -- see the paired <see cref="ClipboardDiagnosticEvent.WriteOutcome"/>/
    /// <c>ClipboardMutated</c> for the exact <c>Privon.Windows.ClipboardWriteOutcome</c> and whether
    /// <c>EmptyClipboard</c> actually succeeded (READBACK_FAILURE and every other non-verified
    /// write outcome are distinguishable this way, without a separate terminal-reason value for
    /// each one).</summary>
    WriteRejected,

    /// <summary>A <see cref="ClipboardDecisionPlan"/> was produced (at least one candidate needs a
    /// user decision) and a decision-session publish attempt was made -- the STEP40 NeedsDecision
    /// UI path.</summary>
    DecisionPending,

    /// <summary>The guarded clipboard write was classified as a verified rewrite -- the clipboard
    /// was actually rewritten and the read-back verification succeeded. NOT
    /// <c>Privon.Core.ProtectionState.Verified</c> -- see
    /// <see cref="ClipboardWriteResultClassifier"/>'s own doc for why.</summary>
    Success,

    /// <summary>An unexpected exception escaped <c>ProcessNotificationAsync</c> -- see the paired
    /// <see cref="ClipboardDiagnosticEvent.ExceptionTypeName"/> (the exception's own
    /// <c>GetType().Name</c> ONLY, never its <c>Message</c>). The coordinator's own worker loop
    /// already isolates this from ending the worker for future attempts; this value exists only so
    /// a manual-QA trace can see that it happened at all.</summary>
    ProcessingException,
}
