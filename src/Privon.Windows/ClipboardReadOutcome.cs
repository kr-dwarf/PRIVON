namespace Privon.Windows;

/// <summary>
/// Outcome of a <see cref="ClipboardChangeMonitor.ReadTextSnapshotAsync"/> call. Deliberately
/// NOT <see cref="Privon.Core.ProtectionState"/> -- this is a Windows-transport-layer outcome,
/// not a product-level protection state, and the two must never be confused (Phase 3A.4 STEP3
/// instruction's explicit rule).
///
/// Declared with <see cref="NotRunning"/> first so that <c>default(ClipboardReadOutcome)</c> is
/// never mistaken for <see cref="Success"/> -- the same discipline already used throughout this
/// codebase (e.g. <c>TrustState.Unknown</c>, <c>CandidateDisposition.NeedsDecision</c>,
/// <c>ProtectionState.Initializing</c> are all declared first for the identical reason). A
/// caller that forgets to check this field, or receives a default-initialized value by mistake,
/// ends up treating the read as unusable, never as a successful one.
/// </summary>
public enum ClipboardReadOutcome
{
    NotRunning,
    Busy,
    FormatUnavailable,
    NativeFailure,
    MalformedData,
    Success,

    // FOREGROUND_READ_EXECUTION_GUARD (Phase 3A.5 STEP4) -- appended, not inserted, so no
    // existing member's ordinal changes.
    /// <summary>The caller-supplied expected target was structurally invalid (unresolved, PID
    /// 0, or a null/empty/whitespace process name) -- rejected before any queueing or native
    /// call.</summary>
    InvalidExpectedTarget,
    /// <summary>The current foreground target could not be mechanically resolved at guard-check
    /// time (no foreground window, PID lookup failed, or process-name lookup failed).</summary>
    TargetUnavailable,
    /// <summary>The current foreground target resolved successfully but its PID/process name do
    /// not equal the caller's expected target.</summary>
    TargetChanged,
}
