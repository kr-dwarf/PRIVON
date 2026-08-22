namespace Privon.App;

/// <summary>
/// Phase 3B STEP17 -- the narrow, coordinator-facing half of the atomic generation/decision-scope
/// lifecycle contract frozen by Phase 3B STEP16/STEP16.1. Deliberately the ONLY member
/// <see cref="ClipboardPrivacyCoordinator"/> is given -- it never sees
/// <see cref="IClipboardDecisionScopeLifecycle"/> (the Runtime-Decision-facing publish/reset
/// surface), so the coordinator can advance the lifecycle on every clipboard notification without
/// ever needing to understand <c>PiiType</c>/<c>CandidatePolicyDecision</c>/<c>AliasToken</c>/
/// <c>TrustState</c>/revision grants -- exactly the same narrow-seam-over-a-richer-concrete-type
/// pattern already used for <see cref="IClipboardReadTransport"/>/<see cref="IClipboardWriteTransport"/>/
/// <see cref="IClipboardPrivacyProcessor"/>/<see cref="IForegroundTargetCapture"/>. The SAME
/// concrete object implements both this interface and <see cref="IClipboardDecisionScopeLifecycle"/>
/// (Phase 3B STEP16.1's OWNERSHIP_RECOMMENDATION correction -- generation and the active scope
/// reference MUST be owned by one object under one lock for the atomicity proof to hold; this
/// interface split only narrows which OPERATIONS each caller sees, never splits the underlying
/// state).
/// </summary>
internal interface IClipboardNotificationLifecycle
{
    /// <summary>
    /// ADVANCE_OPERATION (Phase 3B STEP16.1, frozen): called exactly once per notification the
    /// clipboard callback receives -- <c>Privon.Windows</c> notification metadata, text or not,
    /// BEFORE any <c>HasUnicodeText</c> filtering and structurally before <c>TargetGate</c> (which
    /// only ever runs later, inside worker processing). Atomically increments the generation AND
    /// drops any active decision scope in the SAME critical section, then returns the new
    /// generation value. Metadata only (a plain <c>long</c>) -- never touches raw clipboard text,
    /// never does I/O, never awaits, safe to call from the clipboard owner thread's synchronous
    /// callback (see <c>ClipboardChangeMonitor.Changed</c>'s own CALLBACK_THREAD_CONTRACT).
    /// </summary>
    long AdvanceOnClipboardNotification();
}
