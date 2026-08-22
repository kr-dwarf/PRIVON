namespace Privon.App;

/// <summary>
/// Phase 3C STEP41 -- Real ChatGPT Clipboard Protection Intermittency Runtime Diagnostic. One
/// value per observation point along the real production path
/// (<see cref="ClipboardPrivacyCoordinator"/>'s own <c>OnClipboardChanged</c>/<c>RunWorkerAsync</c>/
/// <c>ProcessNotificationAsync</c>), added ONLY to answer "at which stage did this specific
/// clipboard attempt terminate" for real-environment manual QA -- never a product-facing concept,
/// never persisted beyond a single diagnostic session, never carrying clipboard content of any
/// kind (see <see cref="ClipboardDiagnosticEvent"/>'s own doc for the exact metadata-only fields
/// each stage populates).
///
/// TEMPORARY, THIS-STEP-ONLY INSTRUMENTATION: this type (and the rest of the
/// <c>ClipboardDiagnostic*</c> family) exists purely to let a real Windows + real ChatGPT manual
/// reproduction be correlated stage-by-stage without ever inspecting source or attaching a
/// debugger. It is not part of the frozen 0.1 product surface described elsewhere in this
/// codebase's own documentation.
///
/// LAYER_BOUNDARY_NAMING (Phase 3C STEP41.1 correction): every value here is an App-layer
/// observation only -- none of them claims visibility into the native Win32
/// <c>WM_CLIPBOARDUPDATE</c> message, the clipboard owner thread's suppression decision, or
/// whether <see cref="Privon.Windows.ClipboardChangeMonitor.Changed"/> even fired at all for a
/// given native event (a self-write-suppressed native notification produces NO
/// <see cref="AppClipboardChangeReceived"/> here -- App never sees it). That earlier,
/// Windows-owned boundary is now separately observable via
/// <see cref="Privon.Windows.ClipboardMonitorDiagnosticEvent"/>/
/// <see cref="Privon.Windows.ClipboardChangeMonitor.DiagnosticObserved"/> -- see that type's own
/// doc. <see cref="AppClipboardChangeReceived"/> (renamed from a prior, over-claiming
/// "NativeNotificationReceived") is deliberately named to make clear it is the moment
/// <c>Changed</c> reaches <c>Privon.App</c>, nothing earlier.
///
/// Declared with <see cref="None"/> first so <c>default(ClipboardDiagnosticStage)</c> is never
/// mistaken for a real observation -- the same discipline already used throughout this codebase
/// (e.g. <c>ClipboardReadOutcome.NotRunning</c>, <c>CandidateDisposition.NeedsDecision</c>).
/// </summary>
internal enum ClipboardDiagnosticStage
{
    None,

    /// <summary>Fired for EVERY <see cref="Privon.Windows.ClipboardChangeMonitor.Changed"/> event
    /// the coordinator's <c>OnClipboardChanged</c> receives, text or not -- the earliest App-visible
    /// evidence that a clipboard change reached this process at all. Deliberately NOT named after
    /// the native OS event -- see this type's own LAYER_BOUNDARY_NAMING doc.</summary>
    AppClipboardChangeReceived,

    /// <summary>Fired only for a text notification, immediately after the coordinator's
    /// non-blocking mailbox <c>TryWrite</c> call -- carries whether the channel actually accepted
    /// the item (a capacity-1 <c>DropOldest</c> channel always accepts; <c>false</c> would mean the
    /// mailbox was already completed, e.g. mid-shutdown).</summary>
    MailboxWriteAttempted,

    /// <summary>Fired the moment the coordinator's worker loop actually dequeues an item from the
    /// mailbox -- an attempt whose <c>MailboxWriteAttempted</c> generation never later appears here
    /// (and is superseded by a later generation's own <c>WorkerDequeued</c>) was dropped by the
    /// channel's <c>DropOldest</c> policy before the worker ever saw it.</summary>
    WorkerDequeued,

    /// <summary>Fired once per attempt, immediately after <c>IForegroundTargetCapture.Capture()</c>
    /// and the <c>TargetGate</c> authorization decision.</summary>
    TargetCaptured,

    /// <summary>Fired once per attempt, immediately after the guarded clipboard read completes
    /// (whatever its outcome).</summary>
    GuardedReadCompleted,

    /// <summary>Fired once per attempt that reached a successful guarded read, immediately after
    /// <c>IClipboardPrivacyProcessor.Process</c> returns -- counts only (Detection/Trust/Policy),
    /// never any detected value.</summary>
    PolicyEvaluated,

    /// <summary>Fired once per attempt that produced a <see cref="ClipboardWritePlan"/> and
    /// actually attempted the guarded clipboard write.</summary>
    GuardedWriteCompleted,

    /// <summary>Fired once per attempt whose guarded write was classified as a verified rewrite,
    /// immediately after the composer-verification handoff publish attempt.</summary>
    ComposerVerificationHandoffPublished,

    /// <summary>Fired once per attempt that produced a <see cref="ClipboardDecisionPlan"/>,
    /// immediately after the decision-session publish attempt.</summary>
    DecisionSessionPublishAttempted,

    /// <summary>Fired exactly once per attempt that reached the worker (never for an attempt the
    /// channel silently superseded before the worker ever dequeued it) -- the single terminal
    /// record answering "how did this attempt end." See
    /// <see cref="ClipboardDiagnosticTerminalReason"/>.</summary>
    AttemptTerminal,
}
