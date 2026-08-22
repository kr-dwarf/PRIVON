namespace Privon.App;

/// <summary>
/// Phase 3B STEP21 -- narrow, App-owned seam a <see cref="ClipboardPrivacyCoordinator"/> uses to
/// attempt publishing a pending Runtime Decision session, without the coordinator itself ever
/// needing to know about <see cref="Privon.Core.RevisionTracker"/>/<see cref="Privon.Core.RevisionStamp"/>/
/// <see cref="ClipboardDecisionScope"/>/<see cref="IClipboardDecisionScopeLifecycle"/> -- the same
/// narrow-seam-over-a-richer-concern pattern already used for <see cref="IClipboardReadTransport"/>/
/// <see cref="IClipboardWriteTransport"/>/<see cref="IClipboardPrivacyProcessor"/>/
/// <see cref="IForegroundTargetCapture"/>.
///
/// Synchronous and CPU-local (matching <see cref="IClipboardPrivacyProcessor"/>'s own
/// PROCESSOR_API_RECOMMENDATION reasoning -- hashing/session construction is CPU-only work, never
/// I/O) -- no <c>Task</c>, no clipboard transport dependency, no Detection dependency, no UI.
/// </summary>
internal interface IClipboardDecisionSessionPublisher
{
    /// <summary>
    /// PUBLISH_FLOW (Phase 3B STEP20 audit, frozen): attempts to publish a new decision session for
    /// <paramref name="decisionPlan"/>, built from <paramref name="currentRawText"/> (the exact
    /// successful guarded read's own text -- never normalized/replacement text), against
    /// <paramref name="expectedGeneration"/> (the exact clipboard-attempt generation captured when
    /// the notification that led to this attempt arrived -- never re-read from the lifecycle at
    /// call time). Returns <c>true</c> only if the session was actually installed as the active
    /// scope; <c>false</c> if the generation had already moved on by the time publication was
    /// attempted, in which case the constructed proposal is simply discarded -- no retry, no
    /// re-check against a newer generation. An unexpected exception (from session construction or
    /// from the underlying lifecycle) is never caught here; the caller's own outer per-attempt
    /// boundary is what keeps the App worker loop alive.
    /// </summary>
    bool TryPublish(long expectedGeneration, string currentRawText, ClipboardDecisionPlan decisionPlan);
}
