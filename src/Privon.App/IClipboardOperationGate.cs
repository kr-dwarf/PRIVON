namespace Privon.App;

/// <summary>
/// Phase 3B STEP23 -- the narrow, App-owned asynchronous mutual-exclusion seam
/// <see cref="ClipboardPrivacyCoordinator"/> uses to serialize its own processing attempts, and
/// that a future <c>ClipboardDecisionActionResolver</c> (Phase 3B STEP22 audit's
/// SERIALIZATION_PRIMITIVE finding) will share -- via the SAME concrete instance -- to serialize
/// its own decision-action attempts against the coordinator's. Deliberately Clipboard/App-specific,
/// not generic concurrency infrastructure: a two-method acquire/release seam, never exposing a raw
/// <see cref="SemaphoreSlim"/> anywhere outside its own production implementation.
///
/// STEP22's PROCESSOR_CONCURRENCY_FINDING is what this exists to defend against: no explicit
/// contract or test anywhere guarantees <c>DetectionPipeline</c>/<c>CandidatePolicyEvaluator</c>/
/// <c>ExceptionTrustedEvaluator</c>/<c>AliasAssigner</c>/<c>AliasReplacer</c>/
/// <c>TrustExceptionProvider</c> are safe to call concurrently -- rather than relying on that
/// structurally-plausible-but-unproven assumption, exactly one logical App-semantic attempt
/// (clipboard-notification-driven, or -- once a future STEP builds it -- decision-action-driven)
/// may hold this gate at a time.
/// </summary>
internal interface IClipboardOperationGate
{
    /// <summary>
    /// Asynchronously acquires the single permit, completing only once no other holder has it.
    /// ASYNC_ONLY (frozen): no synchronous <c>Wait()</c>/<c>.Result</c>/
    /// <c>.GetAwaiter().GetResult()</c> equivalent exists anywhere on this interface or its
    /// production implementation -- a caller on the Windows clipboard owner thread's synchronous
    /// callback must NEVER call this. <see cref="ClipboardPrivacyCoordinator"/>'s own clipboard
    /// callback never uses this gate at all, by design, exactly so a newer clipboard notification
    /// can always advance the lifecycle/invalidate an active decision scope immediately, even
    /// while this gate is held by an in-flight attempt (Phase 3B STEP22 audit's
    /// CALLBACK_NONBLOCKING_PROOF).
    /// </summary>
    Task WaitAsync();

    /// <summary>
    /// Releases the single permit, allowing the next waiter (if any) to proceed. Callers MUST call
    /// this in a <c>finally</c> block paired with every successful <see cref="WaitAsync"/> --
    /// never conditionally, never skipped on an exception path.
    /// </summary>
    void Release();
}
