namespace Privon.App;

/// <summary>
/// Phase 3B STEP17 -- the only production implementation of
/// <see cref="IClipboardNotificationLifecycle"/>/<see cref="IClipboardDecisionScopeLifecycle"/>,
/// implementing Phase 3B STEP16.1's frozen MODEL A: one small object owns
/// <see cref="_generation"/> AND <see cref="_activeScope"/> together, guarded by one
/// <see cref="_gate"/>, so "advance generation" and "drop/install the active scope" are always
/// each a single, indivisible critical section -- never two separately-lockable steps (which is
/// exactly what made the original STEP16 LATE_PUBLISH_RACE possible). Every method body below is
/// ONLY ever a <c>long</c> compare/increment plus a reference assignment/null-out -- no await, no
/// native calls, no Detection/Storage/clipboard-write work, no allocation-heavy work, matching
/// STEP16.1's CALLBACK_LOCK_ACCEPTABILITY finding (the lock is safe to take from the Windows
/// clipboard owner thread's synchronous callback because its hold time is provably bounded to a
/// few nanoseconds).
///
/// ATOMICITY_PROOF (Phase 3B STEP16.1): because <see cref="AdvanceOnClipboardNotification"/> and
/// <see cref="TryPublish"/> both hold <see cref="_gate"/> for their ENTIRE body with no
/// unlock/relock gap, the two can never interleave -- only two final orderings are possible for
/// any (publish G1, advance-to-G2) pair: publish wins the lock first (installs its scope, which
/// the following advance then unconditionally drops -- "published then immediately invalidated"),
/// or advance wins first (installs nothing, so the following publish observes its own
/// <c>expectedGeneration</c> no longer matches and returns <c>false</c> -- "publication
/// rejected"). A third, "stale-active" outcome is structurally impossible: there is no code path
/// in either method that checks the generation and later, outside that same lock acquisition,
/// acts on a decision made from a stale read.
///
/// Phase 3C STEP32 -- also implements <see cref="IClipboardGenerationSnapshot"/> as a THIRD narrow
/// view on this same object (STEP30.1's frozen design): <see cref="ClipboardComposerVerifier"/>,
/// live in production, depends only on that single-member interface for generation-freshness
/// checks, never on
/// <see cref="IClipboardDecisionScopeLifecycle"/>'s wider Runtime-Decision-scope surface, which it
/// has no legitimate reason to touch. <see cref="CurrentGeneration"/>'s existing implementation
/// (below) is unchanged -- this is purely an additional interface declaration.
///
/// Phase 0.2C (STEP58 audit, STEP59 implementation) -- adds <see cref="_evaluationState"/>
/// (a <see cref="ClipboardEvaluationState"/>) as a FOURTH piece of state guarded by this SAME
/// <see cref="_gate"/>, plus <see cref="TryBeginEvaluation"/>/<see cref="CompleteEvaluation"/>/
/// <see cref="AbandonEvaluation"/> -- deliberately NOT a second lock, NOT a dedicated adjacent
/// state object, and NOT <c>Interlocked</c>/<c>Volatile</c> parallel state (STEP58 audit's Option C
/// decision, rejecting a separately-locked holder as reopening exactly the two-lock-ordering
/// hazard this type's own MODEL A was built to eliminate). <see cref="_evaluationState"/> always
/// refers implicitly to the CURRENT generation ONLY -- there is no separate
/// <c>evaluatedGenerationId</c> field: both <see cref="AdvanceOnClipboardNotification"/> and
/// <see cref="Reset"/> unconditionally reset it to <see cref="ClipboardEvaluationState.NotEvaluated"/>
/// in the SAME critical section as the generation increment, exactly mirroring how
/// <see cref="_activeScope"/> is already reset there. <see cref="ClipboardDecisionScopeLifecycle"/>
/// was not widened with a NEW narrow interface for these three methods -- this codebase's own
/// established convention is to carve out a narrow interface only once a genuine distinct consumer
/// needs one (see e.g. <see cref="IClipboardGenerationSnapshot"/>'s own STEP30.1/STEP32 history) --
/// so they are plain <see langword="public"/> members of this <see langword="internal sealed"/>
/// class, exactly like every other member here. All three are live production entry points, called
/// by <see cref="ClipboardPrivacyCoordinator"/>'s own evaluation/retry flow (see that type's own
/// <c>IClipboardEvaluationLifecycle</c>-typed <c>TryBeginEvaluation</c>/<c>CompleteEvaluation</c>/
/// <c>AbandonEvaluation</c> calls) -- never dormant.
///
/// TRANSITION_GUARDS (STEP59, correcting STEP58's own pseudocode): <see cref="CompleteEvaluation"/>/
/// <see cref="AbandonEvaluation"/> require BOTH the generation match AND
/// <see cref="_evaluationState"/> == <see cref="ClipboardEvaluationState.InProgress"/> at the
/// instant of the call -- generation match alone is not sufficient. Without the InProgress
/// precondition, a malformed/double/stale call (no prior claim at all, or a claim that already
/// resolved one way or the other) could silently fabricate an Evaluated generation nobody actually
/// evaluated, reopen an already-terminal Evaluated generation back to NotEvaluated, or re-close an
/// already-abandoned one -- every one of these is a NO-OP here instead.
/// </summary>
internal sealed class ClipboardDecisionScopeLifecycle :
    IClipboardNotificationLifecycle, IClipboardDecisionScopeLifecycle, IClipboardGenerationSnapshot
{
    private readonly object _gate = new();
    private long _generation;
    private ClipboardDecisionScope? _activeScope;
    private ClipboardEvaluationState _evaluationState;

    public long AdvanceOnClipboardNotification()
    {
        lock (_gate)
        {
            _generation++;
            _activeScope = null;
            _evaluationState = ClipboardEvaluationState.NotEvaluated;
            return _generation;
        }
    }

    /// <summary>
    /// TryBeginEvaluation (Phase 0.2C, STEP58/STEP59, frozen): the atomic single-claim operation.
    /// Succeeds ONLY when <paramref name="expectedGeneration"/> still equals the current generation
    /// AND no other attempt currently holds the claim (<see cref="_evaluationState"/> is
    /// <see cref="ClipboardEvaluationState.NotEvaluated"/>) -- both checked and, on success,
    /// mutated inside the SAME <see cref="_gate"/> acquisition as every other operation on this
    /// type, exactly mirroring <see cref="TryPublish"/>'s own proven-safe compare-and-install
    /// shape. On failure (stale generation OR already claimed/evaluated), returns
    /// <see langword="false"/> and mutates nothing -- the caller cannot distinguish which reason
    /// applied, and does not need to: either way, nothing here needs to be (re)done by that caller.
    /// </summary>
    public bool TryBeginEvaluation(long expectedGeneration)
    {
        lock (_gate)
        {
            if (expectedGeneration != _generation) return false;
            if (_evaluationState != ClipboardEvaluationState.NotEvaluated) return false;
            _evaluationState = ClipboardEvaluationState.InProgress;
            return true;
        }
    }

    /// <summary>
    /// CompleteEvaluation (Phase 0.2C, STEP58/STEP59, frozen): reports a terminal (non-retryable)
    /// outcome for the claim taken out via <see cref="TryBeginEvaluation"/>. Transitions to
    /// <see cref="ClipboardEvaluationState.Evaluated"/> ONLY when <paramref name="claimedGeneration"/>
    /// still equals the current generation AND <see cref="_evaluationState"/> is currently
    /// <see cref="ClipboardEvaluationState.InProgress"/> -- see this type's own class doc
    /// TRANSITION_GUARDS for why generation match alone is not sufficient. Every other case
    /// (stale generation, or no matching in-flight claim at all -- already
    /// <see cref="ClipboardEvaluationState.NotEvaluated"/> or already
    /// <see cref="ClipboardEvaluationState.Evaluated"/>) is silently a NO-OP: never throws, never
    /// mutates. Which real-world outcome this corresponds to (protected/all-bypass/NeedsDecision
    /// published/no text present) is Phase 0.2D's concern entirely -- this method itself never
    /// touches <see cref="_generation"/> (directly satisfying the STEP42.1 self-write
    /// generation-stability invariant: a successfully verified protected self-write, which never
    /// advances the generation in the first place, must not be able to do so here either).
    /// </summary>
    public void CompleteEvaluation(long claimedGeneration)
    {
        lock (_gate)
        {
            if (claimedGeneration != _generation) return;
            if (_evaluationState != ClipboardEvaluationState.InProgress) return;
            _evaluationState = ClipboardEvaluationState.Evaluated;
        }
    }

    /// <summary>
    /// AbandonEvaluation (Phase 0.2C, STEP58/STEP59, frozen): reports a retryable (transient)
    /// outcome, releasing the claim so a future <see cref="TryBeginEvaluation"/> for the SAME
    /// current generation can succeed again. Transitions back to
    /// <see cref="ClipboardEvaluationState.NotEvaluated"/> ONLY when
    /// <paramref name="claimedGeneration"/> still equals the current generation AND
    /// <see cref="_evaluationState"/> is currently <see cref="ClipboardEvaluationState.InProgress"/>
    /// -- in particular, an already-<see cref="ClipboardEvaluationState.Evaluated"/> (terminal)
    /// generation is NEVER reopened by a stale/erroneous Abandon call; every other case is a
    /// silent NO-OP, exactly mirroring <see cref="CompleteEvaluation"/>'s own guard. Never touches
    /// <see cref="_generation"/>.
    /// </summary>
    public void AbandonEvaluation(long claimedGeneration)
    {
        lock (_gate)
        {
            if (claimedGeneration != _generation) return;
            if (_evaluationState != ClipboardEvaluationState.InProgress) return;
            _evaluationState = ClipboardEvaluationState.NotEvaluated;
        }
    }

    public long CurrentGeneration
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public bool HasActiveScope
    {
        get
        {
            lock (_gate)
            {
                return _activeScope is not null;
            }
        }
    }

    public bool IsActive(ClipboardDecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            return ReferenceEquals(_activeScope, scope);
        }
    }

    public ClipboardDecisionScope? GetActiveScope()
    {
        lock (_gate)
        {
            return _activeScope;
        }
    }

    public bool TryPublish(long expectedGeneration, ClipboardDecisionScope proposedScope)
    {
        ArgumentNullException.ThrowIfNull(proposedScope);
        lock (_gate)
        {
            if (expectedGeneration != _generation) return false;
            _activeScope = proposedScope;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _activeScope = null;
            _evaluationState = ClipboardEvaluationState.NotEvaluated;
        }
    }
}
