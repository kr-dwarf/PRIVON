namespace Privon.App;

/// <summary>
/// Phase 3C STEP31/31.1 -- the narrow, clipboard-callback-safe seam
/// <see cref="ClipboardPrivacyCoordinator.OnClipboardChanged"/> uses to eagerly discard any pending
/// composer-verification attempt on every clipboard notification, text or not -- mirroring
/// <see cref="IClipboardNotificationLifecycle.AdvanceOnClipboardNotification"/>'s own
/// unconditional-scope-drop precedent, applied to a second, independently-owned piece of
/// sensitive-adjacent state (Phase 3C STEP31's NOTIFICATION_INVALIDATION finding). Deliberately a
/// SEPARATE interface from <see cref="IClipboardComposerVerificationHandoff"/> so the type used
/// inside the synchronous owner-thread callback structurally cannot expose anything
/// <see cref="IClipboardOperationGate"/>-dependent.
/// </summary>
internal interface IClipboardComposerVerificationInvalidation
{
    /// <summary>
    /// Unconditionally discards any current pending verification attempt. Synchronous, bounded (a
    /// lock plus a null-assignment), never touches <see cref="IClipboardOperationGate"/>, never
    /// performs UI Automation work, never inspects or compares the discarded attempt's own
    /// content -- safe to call from the Windows clipboard owner thread's callback.
    /// </summary>
    void InvalidatePending();
}
