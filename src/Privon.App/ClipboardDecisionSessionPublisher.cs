using Privon.Core;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP21 -- the only production implementation of
/// <see cref="IClipboardDecisionSessionPublisher"/>. Depends on ONLY
/// <see cref="IClipboardDecisionScopeLifecycle"/> (the future-Runtime-Decision-facing half of the
/// atomic generation/decision-scope lifecycle contract -- see that interface's own doc) -- no
/// clipboard transport, no Detection, no UI, and no persistent raw-text/<c>CanonicalValue</c>/
/// <c>RevisionTracker</c>/<c>RevisionStamp</c>/<c>ClipboardDecisionPlan</c>/
/// <c>ClipboardDecisionScope</c> field of any kind.
///
/// HASHING_LOCK_BOUNDARY (Phase 3B STEP16.1/STEP20 audits, frozen, implemented here): every step of
/// building a proposal -- constructing a fresh <see cref="RevisionTracker"/>, calling
/// <see cref="RevisionTracker.Observe"/> against <paramref name="currentRawText"/> is this type's
/// ONLY raw-text touch point -- and constructing the resulting <see cref="ClipboardDecisionScope"/>
/// happens entirely BEFORE the single call into
/// <see cref="IClipboardDecisionScopeLifecycle.TryPublish"/>. No lock of any kind is taken around
/// that call by this type -- the lifecycle's own internal <c>_gate</c> is the only synchronization
/// involved, and it protects nothing but a <c>long</c> compare and a reference assignment (see
/// <see cref="ClipboardDecisionScopeLifecycle"/>'s own ATOMICITY_PROOF doc).
///
/// STALE_GENERATION_POLICY (frozen): <see cref="TryPublish"/> never inspects
/// <see cref="IClipboardDecisionScopeLifecycle.CurrentGeneration"/> before calling the lifecycle's
/// own <c>TryPublish</c> -- a pre-check would only reopen the exact race the lifecycle's single
/// atomic compare-and-install already closes. On a stale-generation rejection, the constructed
/// proposal is discarded (nothing here retains it) and this method returns <c>false</c> -- no
/// retry, no attempt to republish against whatever the generation has since become.
///
/// GENERATION_BINDING (Phase 3C STEP34, implementing the Phase 3C STEP33 audit's
/// SCOPE_GENERATION_AVAILABILITY finding): the proposed <see cref="ClipboardDecisionScope"/> is
/// constructed with its <see cref="ClipboardDecisionScope.Generation"/> set to the exact SAME
/// <paramref name="expectedGeneration"/> value this method then passes to the lifecycle's own
/// <c>TryPublish</c> -- one parameter, two uses, never two separately-derived values. A scope that
/// fails to publish (stale generation) is discarded with whatever <c>Generation</c> it was built
/// with; nothing here ever mutates an existing scope's <c>Generation</c> after construction.
///
/// SCOPE_PUBLISHED (Phase 3C STEP39.1 audit's SELECTED_DECISION_NOTIFICATION_SEAM, implemented
/// Phase 3C STEP40): <see cref="ScopePublished"/> fires exactly once per successful <c>true</c>
/// return from <see cref="TryPublish"/> -- never on a stale/failed publication -- carrying ONLY the
/// exact <see cref="ClipboardDecisionScope"/> reference that was just installed (no raw text, no
/// flattened <c>CanonicalValue</c>, no separate content-bearing payload). Deliberately placed here,
/// never on <see cref="ClipboardDecisionScopeLifecycle"/> itself: that type's own tiny <c>_gate</c>
/// critical section must stay provably bounded to a `long` compare/reference assignment (its own
/// CALLBACK_LOCK_ACCEPTABILITY doc) -- an event raised from inside that lock would let an arbitrary
/// subscriber's cost extend a critical section also reachable from the Windows clipboard owner
/// thread's synchronous callback (via <c>AdvanceOnClipboardNotification</c>), which is exactly the
/// risk this design avoids. The event is raised here instead, AFTER the single
/// <see cref="IClipboardDecisionScopeLifecycle.TryPublish"/> call has already returned -- at which
/// point its own internal lock is provably already released -- using the exact <c>proposedScope</c>
/// local this method already holds (no second <c>GetActiveScope()</c> round trip, no reopened race
/// window). EVENT_FAILURE_CONTAINMENT: the invocation is wrapped in its own <c>try</c>/<c>catch</c>
/// -- exactly mirroring <see cref="Privon.Windows.ClipboardChangeMonitor"/>'s own established
/// subscriber-exception-isolation precedent for its <c>Changed</c> event -- so a broken UI
/// subscriber (e.g. a WPF <c>Dispatcher</c> already mid-shutdown) can never propagate back into
/// this method's own caller (<see cref="ClipboardPrivacyCoordinator.ProcessNotificationAsync"/>)
/// and can never affect the returned <c>bool</c> or the scope that was actually published.
/// </summary>
internal sealed class ClipboardDecisionSessionPublisher : IClipboardDecisionSessionPublisher
{
    private readonly IClipboardDecisionScopeLifecycle _lifecycle;

    public ClipboardDecisionSessionPublisher(IClipboardDecisionScopeLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        _lifecycle = lifecycle;
    }

    /// <summary>Fires only after a successful (generation-bound) scope publication -- see this
    /// type's own SCOPE_PUBLISHED doc. Never fires for a stale/failed publication.</summary>
    public event EventHandler<ClipboardDecisionScope>? ScopePublished;

    public bool TryPublish(long expectedGeneration, string currentRawText, ClipboardDecisionPlan decisionPlan)
    {
        ArgumentNullException.ThrowIfNull(currentRawText);
        ArgumentNullException.ThrowIfNull(decisionPlan);

        // OUTSIDE the lifecycle lock: fresh per-session tracker, its one Observe call against the
        // exact raw text (this type's only raw-text touch point -- never stored anywhere), and the
        // resulting proposal, fully constructed before the single TryPublish call below.
        var tracker = new RevisionTracker();
        var initialStamp = tracker.Observe(currentRawText);
        var proposedScope = new ClipboardDecisionScope(tracker, initialStamp, decisionPlan.Items, expectedGeneration);

        var published = _lifecycle.TryPublish(expectedGeneration, proposedScope);
        if (published)
        {
            try
            {
                ScopePublished?.Invoke(this, proposedScope);
            }
            catch
            {
                // EVENT_FAILURE_CONTAINMENT (see this type's own SCOPE_PUBLISHED doc) -- a
                // subscriber's own failure must never propagate back into TryPublish's caller.
            }
        }

        return published;
    }
}
