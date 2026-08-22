namespace Privon.App;

/// <summary>
/// Phase 0.2C (STEP58 contract, STEP59 implementation) -- the per-generation privacy-evaluation
/// state <see cref="ClipboardDecisionScopeLifecycle"/> tracks alongside its existing generation
/// counter/active-scope pair. Always refers implicitly to the CURRENT generation -- there is no
/// separate "which generation does this apply to" field anywhere; every generation-advancing
/// operation (<c>AdvanceOnClipboardNotification</c>/<c>Reset</c>) unconditionally resets this value
/// to <see cref="NotEvaluated"/> in the SAME critical section as the generation increment, so a
/// stale value for a superseded generation can never be observed.
///
/// <see cref="NotEvaluated"/> is deliberately the default (<c>0</c>) value, matching this
/// codebase's consistent convention for state-representing enums (<c>ClipboardRuntimeItemChoice.Unresolved</c>/
/// <c>CandidateDisposition.NeedsDecision</c>/<c>ClipboardReadOutcome.NotRunning</c> all follow the
/// identical "safe/default value first" pattern) -- a freshly-constructed
/// <see cref="ClipboardDecisionScopeLifecycle"/> is therefore correctly claimable from the moment
/// it exists, with no explicit initialization required.
///
/// A 3-state representation (rather than a plain boolean) is a deliberate choice, not merely a
/// defensive one: functionally, 2 states would already correctly prevent a duplicate concurrent
/// claim (<see cref="Evaluated"/> alone would already differ from <see cref="NotEvaluated"/>) --
/// but overloading one value to mean both "currently being evaluated" and "already finished" would
/// contradict this codebase's own consistent preference for explicit, individually-named states
/// over implicit dual-purpose values (see e.g. <c>VerificationOutcome</c>/<c>CandidateDisposition</c>).
/// <see cref="InProgress"/> exists so the in-flight period is directly, unambiguously observable
/// and testable, never inferred from the absence of a second signal.
///
/// PRIVACY: this type carries zero sensitive data by construction -- not even a generation number
/// (see above) -- making it strictly less than "generation identity + evaluation status." It can
/// never hold a <c>CanonicalValue</c>, raw clipboard text, a <c>RevisionStamp</c>, a PID/process
/// name, a window title, or a timestamp; it is, and can only ever be, one of these three named
/// values.
/// </summary>
internal enum ClipboardEvaluationState
{
    /// <summary>No attempt has claimed the current generation for evaluation (or a prior claim was
    /// abandoned, or the generation just advanced) -- freely claimable via
    /// <see cref="ClipboardDecisionScopeLifecycle.TryBeginEvaluation"/>.</summary>
    NotEvaluated,

    /// <summary>Exactly one attempt currently holds the claim for the current generation and has
    /// not yet reported a terminal or retryable outcome -- further
    /// <see cref="ClipboardDecisionScopeLifecycle.TryBeginEvaluation"/> calls for this same
    /// generation fail until that attempt calls
    /// <see cref="ClipboardDecisionScopeLifecycle.CompleteEvaluation"/> or
    /// <see cref="ClipboardDecisionScopeLifecycle.AbandonEvaluation"/>.</summary>
    InProgress,

    /// <summary>The current generation reached a stable, non-transient conclusion -- no further
    /// claim is ever granted for this generation (only a new generation, via
    /// <see cref="ClipboardDecisionScopeLifecycle.AdvanceOnClipboardNotification"/>/
    /// <see cref="ClipboardDecisionScopeLifecycle.Reset"/>, resets this). Which real-world outcome
    /// (protected/all-bypass/NeedsDecision-published/no-text-present) this corresponds to is
    /// entirely Phase 0.2D's future concern -- this type and
    /// <see cref="ClipboardDecisionScopeLifecycle"/> know nothing about outcomes, only about this
    /// one bit of "is it still worth trying again for this exact generation."</summary>
    Evaluated,
}
