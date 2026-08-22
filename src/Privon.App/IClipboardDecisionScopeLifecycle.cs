namespace Privon.App;

/// <summary>
/// Phase 3B STEP17 -- the narrow, future-Runtime-Decision-facing half of the atomic
/// generation/decision-scope lifecycle contract frozen by Phase 3B STEP16/STEP16.1.
/// <see cref="ClipboardPrivacyCoordinator"/> is never given this interface -- see
/// <see cref="IClipboardNotificationLifecycle"/>'s own doc for why. The SAME concrete object
/// implements both interfaces (STEP16.1's OWNERSHIP_RECOMMENDATION correction).
///
/// <see cref="CurrentGeneration"/>/<see cref="HasActiveScope"/>/<see cref="IsActive"/> are
/// deliberately safe, metadata-only observability (a counter and two booleans) -- they never
/// expose the active scope's own contents (which, once a future STEP gives
/// <see cref="ClipboardDecisionScope"/> real fields, may include sensitive
/// <c>Privon.Detection.CanonicalValue</c> identities).
/// </summary>
internal interface IClipboardDecisionScopeLifecycle
{
    /// <summary>The current generation. Safe metadata -- see <c>RevisionId</c>'s own "just a
    /// counter" precedent.</summary>
    long CurrentGeneration { get; }

    /// <summary>Whether a decision scope is currently active. Never reveals anything about that
    /// scope's own (future) contents.</summary>
    bool HasActiveScope { get; }

    /// <summary>True only if <paramref name="scope"/> is REFERENCE-identical to the currently
    /// active scope (see <see cref="ClipboardDecisionScope"/>'s own doc on why reference, not
    /// value, identity is what matters here).</summary>
    bool IsActive(ClipboardDecisionScope scope);

    /// <summary>
    /// ACTIVE_DECISION_SCOPE_ACCESS (Phase 3B STEP20 audit, frozen): returns the currently active
    /// scope, or <c>null</c> if none is active, as a single point-in-time snapshot read under
    /// <c>_gate</c> -- nothing else happens under that lock (no hashing, no Detection, no
    /// callback/delegate execution of any kind). The returned reference does NOT guarantee
    /// continued validity: a notification that arrives immediately after this call returns can
    /// supersede it before the caller ever acts on it. Every future security-relevant action taken
    /// against a scope obtained this way MUST re-check <see cref="IsActive"/> against that SAME
    /// scope reference at the actual point of use -- this method is a convenience accessor, never
    /// the authority itself. Deliberately no lease/ref-counting/generation-copy mechanism exists
    /// here -- see <see cref="ClipboardDecisionScope"/>'s own doc for why a returned reference is
    /// safe to hold only as a short-lived local, never cached in a second long-lived owner.
    /// </summary>
    ClipboardDecisionScope? GetActiveScope();

    /// <summary>
    /// PUBLISH_OPERATION (Phase 3B STEP16.1, frozen): atomically installs
    /// <paramref name="proposedScope"/> as the active scope, but ONLY if
    /// <paramref name="expectedGeneration"/> still equals the current generation at the exact
    /// instant this check-and-install runs (single critical section, no unlock/relock gap between
    /// the compare and the install -- this is precisely what makes the STEP16.1 LATE_PUBLISH_RACE
    /// impossible). <paramref name="proposedScope"/> must already be fully constructed BEFORE this
    /// call -- nothing sensitive (a future <c>RevisionTracker.Observe</c> call, raw text, etc.) may
    /// happen inside this method or under its lock. Returns <c>false</c>, leaving the active scope
    /// unchanged, if the generation has already moved on; the caller must then discard
    /// <paramref name="proposedScope"/> itself (nothing here retains a rejected proposal).
    /// </summary>
    bool TryPublish(long expectedGeneration, ClipboardDecisionScope proposedScope);

    /// <summary>
    /// RESET_OPERATION (Phase 3B STEP16.1, frozen): atomically advances the generation AND drops
    /// the active scope, in the same critical section as <see cref="TryPublish"/> and
    /// <see cref="IClipboardNotificationLifecycle.AdvanceOnClipboardNotification"/>. Generation is
    /// advanced here too (not just the scope cleared) so that a proposal already in flight,
    /// carrying an <c>expectedGeneration</c> captured before this reset, can never successfully
    /// publish afterward. Reserved for future use (Windows session-lock observer/App
    /// shutdown/explicit composition reset) -- no such caller exists yet in this STEP.
    /// </summary>
    void Reset();
}
