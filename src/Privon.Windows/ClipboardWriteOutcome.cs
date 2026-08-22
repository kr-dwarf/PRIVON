namespace Privon.Windows;

/// <summary>
/// Outcome of a <see cref="ClipboardChangeMonitor.WriteTextIfSequenceMatchesAsync"/> call.
/// Deliberately NOT <see cref="Privon.Core.ProtectionState"/> -- this is a Windows-transport-layer
/// outcome, not a product-level protection state (same rule as <see cref="ClipboardReadOutcome"/>).
/// A <see cref="Success"/> result here is NOT <c>ProtectionState.Verified</c> either -- Verified
/// additionally requires an actual composer paste, composer read-back, and final validation
/// (see docs/release-gate.md), none of which this Windows-transport write performs.
///
/// Deliberately a small, closed set -- no BeforeMutation/AfterMutation enum explosion. Whether a
/// mutation actually happened is carried orthogonally via <see cref="ClipboardWriteResult.ClipboardMutated"/>,
/// not encoded into extra outcome members.
///
/// Declared with <see cref="NotRunning"/> first so that <c>default(ClipboardWriteOutcome)</c> is
/// never mistaken for <see cref="Success"/> -- the same discipline used throughout this codebase
/// (e.g. <see cref="ClipboardReadOutcome"/>, <c>TrustState.Unknown</c>,
/// <c>CandidateDisposition.NeedsDecision</c>).
/// </summary>
public enum ClipboardWriteOutcome
{
    NotRunning,
    InvalidExpectedSequence,
    InvalidText,
    Busy,
    SequenceChanged,
    NativeFailure,
    /// <summary>The mutation itself succeeded, but no reliable sequence number is available to
    /// verify against -- either the post-Set capture was 0, or (as of Phase 3C STEP42) the
    /// verification-reopen's own sequence was 0. Never carries a <see cref="ClipboardWriteResult.ResultSequence"/>;
    /// never installs the self-write suppression marker.</summary>
    VerificationUnavailable,
    /// <summary>NARROWED as of Phase 3C STEP42: a valid <c>CF_UNICODETEXT</c> payload was read
    /// back, in the same verification session, that is genuinely different from the intended
    /// replacement text, AND the verification sequence no longer equals the write's own post-Set
    /// capture -- positive evidence a different actor is responsible for that different content.
    /// (Before this correction, ANY verification-sequence transition alone produced this outcome,
    /// even when the read-back content still exactly matched the intended replacement -- a
    /// real-environment defect this correction fixes; see <see cref="ClipboardChangeMonitor"/>'s
    /// own <c>VerifyWhileClipboardOpen</c> doc.) Never carries a <see cref="ClipboardWriteResult.ResultSequence"/>;
    /// never installs the self-write suppression marker.</summary>
    Superseded,
    /// <summary>A valid <c>CF_UNICODETEXT</c> payload was read back, in the same verification
    /// session, that is genuinely different from the intended replacement text, while the
    /// verification sequence still equals the write's own post-Set capture -- see
    /// <see cref="Superseded"/>'s own doc for the sibling outcome used when the sequence has
    /// since moved on instead. Carries the confirmed, reliable verification sequence as
    /// <see cref="ClipboardWriteResult.ResultSequence"/>; never installs the self-write
    /// suppression marker.</summary>
    ReadBackMismatch,
    /// <summary>The write was verified: in one coherent verification session, a reliable sequence
    /// number was observed, <c>CF_UNICODETEXT</c> was available, and the read-back text exactly
    /// (ordinally) matched the intended replacement -- as of Phase 3C STEP42, this no longer
    /// requires the verification sequence to equal the intermediate post-Set capture (see
    /// <see cref="ClipboardChangeMonitor"/>'s own <c>VerifyWhileClipboardOpen</c> doc). Carries
    /// the verified sequence as <see cref="ClipboardWriteResult.ResultSequence"/> and installs it
    /// as the self-write suppression marker. Still NOT <c>Privon.Core.ProtectionState.Verified</c>
    /// -- see this type's own class doc.</summary>
    Success,

    // FOREGROUND_WRITE_EXECUTION_GUARD (Phase 3A.5 STEP4) -- appended, not inserted, so no
    // existing member's ordinal changes.
    /// <summary>The caller-supplied expected target was structurally invalid (unresolved, PID
    /// 0, or a null/empty/whitespace process name) -- rejected before any queueing, HGLOBAL
    /// allocation, or native call.</summary>
    InvalidExpectedTarget,
    /// <summary>The current foreground target could not be mechanically resolved at guard-check
    /// time.</summary>
    TargetUnavailable,
    /// <summary>The current foreground target resolved successfully but its PID/process name do
    /// not equal the caller's expected target.</summary>
    TargetChanged,
}
