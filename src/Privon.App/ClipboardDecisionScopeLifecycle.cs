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
/// view on this same object (STEP30.1's frozen design): a future <c>ClipboardComposerVerifier</c>
/// depends only on that single-member interface for generation-freshness checks, never on
/// <see cref="IClipboardDecisionScopeLifecycle"/>'s wider Runtime-Decision-scope surface, which it
/// has no legitimate reason to touch. <see cref="CurrentGeneration"/>'s existing implementation
/// (below) is unchanged -- this is purely an additional interface declaration.
/// </summary>
internal sealed class ClipboardDecisionScopeLifecycle :
    IClipboardNotificationLifecycle, IClipboardDecisionScopeLifecycle, IClipboardGenerationSnapshot
{
    private readonly object _gate = new();
    private long _generation;
    private ClipboardDecisionScope? _activeScope;

    public long AdvanceOnClipboardNotification()
    {
        lock (_gate)
        {
            _generation++;
            _activeScope = null;
            return _generation;
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
        }
    }
}
