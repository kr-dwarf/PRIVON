using Privon.Core;
using Privon.Detection;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP17 -- an opaque token representing one pending Runtime Decision session, bound to
/// the exact clipboard-attempt generation that authorized it (see
/// <see cref="IClipboardDecisionScopeLifecycle"/>). Phase 3B STEP21 gives it its real payload, per
/// the Phase 3B STEP18/STEP20 audits: a private, per-session <see cref="RevisionTracker"/>, the
/// initial <see cref="RevisionStamp"/> that tracker produced for the exact raw text that surfaced
/// the decision, and the immutable <see cref="Items"/> that need one. Phase 3B STEP25 adds the
/// runtime resolution machinery itself (Phase 3B STEP24/STEP24.1 audits' frozen model) -- per-item
/// provisional/final choices, minted one-time bypass grants, and a committed flag -- all reachable
/// through exactly ONE additional field, <see cref="_resolutionState"/>. Phase 3C STEP34 adds
/// exactly one more immutable field, <see cref="Generation"/> (see this type's own GENERATION doc
/// below) -- needed only because <see cref="IClipboardComposerVerificationHandoff"/> requires an
/// actual generation NUMBER, a fundamentally different identity mechanism than this type's own
/// reference-based <see cref="IClipboardDecisionScopeLifecycle.IsActive"/> check. This type still
/// carries NO raw text field, no <c>ForegroundTargetSnapshot</c>, no clipboard
/// <c>SequenceNumber</c>, no <c>AliasMap</c>/<c>AliasAssignment</c>/replacement text, no
/// <c>DetectionResult</c>/<c>CandidatePolicyDecision</c>, and no knowledge of
/// <see cref="IClipboardDecisionScopeLifecycle"/> or <c>IClipboardOperationGate</c> whatsoever
/// (Phase 3B STEP25 instruction's explicit NO_LIFECYCLE_COUPLING/NO_OPERATION_GATE_COUPLING
/// requirements -- this type does not know whether it is currently active; that authority remains
/// entirely external, in <see cref="IClipboardDecisionScopeLifecycle"/>).
///
/// REVISIONTRACKER_ENCAPSULATION (Phase 3B STEP20 audit, frozen): <see cref="_revisionTracker"/> is
/// never exposed as a property or field of any accessibility -- not even <c>internal</c>. The only
/// operation this type exposes against it is <see cref="ObserveCurrent"/>, which delegates to
/// <see cref="RevisionTracker.Observe"/> alone; <see cref="RevisionTracker.ForceAdvance"/> is
/// deliberately never reachable through this type (a future caller has no legitimate reason to bump
/// this session's revision without observing real current text, and exposing it would invite
/// exactly that misuse).
///
/// INITIAL_STAMP_CONTRACT (frozen): <see cref="InitialStamp"/> is captured once, at construction,
/// from the exact <c>RevisionTracker.Observe</c> call the future
/// <see cref="ClipboardDecisionSessionPublisher"/> makes against the exact raw text that produced
/// this session's <see cref="Items"/> -- never recomputed from <see cref="Items"/>, normalized
/// text, replacement text, <c>CanonicalValue</c>, or clipboard sequence. Because
/// <see cref="RevisionTracker"/> never advances on an unchanged hash but DOES advance every time
/// the hash changes and then changes back (see <see cref="RevisionTracker"/>'s own doc and
/// <c>RevisionStamp.Matches</c>'s <c>(RevisionId, Hash)</c> pair), a future
/// <c>currentStamp = scope.ObserveCurrent(currentRawText); currentStamp.Matches(scope.InitialStamp)</c>
/// check can never be fooled by an A -&gt; B -&gt; A round trip: the second A carries a strictly
/// later <see cref="RevisionId"/> than the first, even though its <see cref="SnapshotHash"/> is
/// identical.
///
/// Reference identity (not value equality) is deliberately what
/// <see cref="IClipboardDecisionScopeLifecycle.IsActive"/> checks against -- a plain
/// <c>sealed class</c>, not a record, so two otherwise-identical scopes are never treated as
/// interchangeable.
///
/// GENERATION (Phase 3C STEP33/STEP34, implementing the Phase 3C STEP33 audit's
/// SCOPE_GENERATION_AVAILABILITY finding): <see cref="Generation"/> is the exact clipboard-attempt
/// generation this scope was successfully published under -- captured once, at construction, from
/// the very <c>expectedGeneration</c> value <see cref="ClipboardDecisionSessionPublisher.TryPublish"/>
/// already used for its own <see cref="IClipboardDecisionScopeLifecycle.TryPublish"/> call -- never
/// recomputed, never rebound, never re-derived from a later <see cref="IClipboardDecisionScopeLifecycle.CurrentGeneration"/>
/// read. This is provably the correct value for as long as <see cref="IClipboardDecisionScopeLifecycle.IsActive"/>
/// continues to return <see langword="true"/> for THIS exact scope reference: <c>_activeScope</c> is
/// installed only by a successful <c>TryPublish(g, scope)</c> call atomically bound to generation
/// <c>g</c>, and is nulled out only together with a generation advance, in that SAME critical
/// section (<see cref="ClipboardDecisionScopeLifecycle"/>'s own ATOMICITY_PROOF) -- so reading
/// <see cref="Generation"/> after observing <c>IsActive(scope)==true</c> is equivalent to an atomic
/// "generation the still-active scope was published under" read, without a second lock acquisition
/// and without reopening the exact TOCTOU gap a fresh <c>CurrentGeneration</c> read at handoff time
/// would (Phase 3C STEP33 audit's GENERATION_REBIND_POLICY). Safe, non-sensitive metadata -- same
/// classification as <see cref="RevisionId"/>/<see cref="ClipboardDispatchItem.Generation"/>.
///
/// RESOLUTION_STATE_ATOMICITY / STATE_PUBLICATION_MODEL (Phase 3B STEP25, implementing the
/// Phase 3B STEP24.1 audit's SELECTED_MODEL): all runtime resolution state introduced by this
/// STEP is reachable through the single <see cref="_resolutionState"/> field, never through
/// separate mutable fields (a separate choice dictionary/grant set/committed bool would allow an
/// externally-observable partially-updated state -- see
/// <see cref="ClipboardDecisionResolutionState"/>'s own doc). Every mutation
/// (<see cref="ApplyIntent"/>/<see cref="CommitResolved"/>) follows the exact same shape:
/// construct one entirely new immutable <see cref="ClipboardDecisionResolutionState"/> carrying
/// the full next state, then publish that single reference. The field is read/written via
/// <see cref="Volatile.Read{T}(ref T)"/>/<see cref="Volatile.Write{T}(ref T, T)"/> rather than a
/// dedicated scope-owned lock -- the operation gate a future resolver holds already guarantees
/// only ONE writer is ever active system-wide at a time (Phase 3B STEP23/STEP24.1), so no
/// read-modify-write race on this field is structurally possible; <c>Volatile</c> alone is still
/// used (rather than trusting that external guarantee for memory VISIBILITY too) so that a reader
/// on a different thread than the most recent writer is always guaranteed to observe the latest
/// published snapshot, without this type needing to know the operation gate exists at all.
/// </summary>
internal sealed class ClipboardDecisionScope
{
    private readonly RevisionTracker _revisionTracker;
    private ClipboardDecisionResolutionState _resolutionState;

    internal ClipboardDecisionScope(
        RevisionTracker revisionTracker,
        RevisionStamp initialStamp,
        IReadOnlyList<ClipboardDecisionItem> items,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(revisionTracker);
        ArgumentNullException.ThrowIfNull(items);
        _revisionTracker = revisionTracker;
        InitialStamp = initialStamp;
        Items = items;
        Generation = generation;
        _resolutionState = ClipboardDecisionResolutionState.Empty;
    }

    /// <summary>The exact <see cref="RevisionStamp"/> the session's own tracker produced for the
    /// raw text that surfaced <see cref="Items"/> -- immutable after construction.</summary>
    public RevisionStamp InitialStamp { get; }

    /// <summary>The exact clipboard-attempt generation this scope was successfully published under
    /// -- immutable after construction. See this type's own GENERATION doc for why this is the
    /// correct, race-free source of identity for a future composer-verification handoff.</summary>
    public long Generation { get; }

    /// <summary>The NeedsDecision candidate identities this session covers -- exactly what the
    /// <c>ClipboardDecisionPlan</c> that produced this session carried, reused as-is (that type's
    /// own <c>Items</c> is already defensively materialized/immutable, so no further copy is made
    /// here).</summary>
    public IReadOnlyList<ClipboardDecisionItem> Items { get; }

    /// <summary>
    /// The only way to use this session's private <see cref="RevisionTracker"/>: observes
    /// <paramref name="currentText"/> exactly as given (no normalization of any kind -- exact text
    /// goes into <see cref="RevisionTracker.Observe"/>) and returns the resulting current
    /// <see cref="RevisionStamp"/>. A future caller compares that against <see cref="InitialStamp"/>
    /// via <see cref="RevisionStamp.Matches"/> to decide whether this session's original raw-text
    /// state still holds.
    /// </summary>
    public RevisionStamp ObserveCurrent(ReadOnlySpan<char> currentText) => _revisionTracker.Observe(currentText);

    /// <summary>Whether this scope's resolution has been finally committed (see
    /// <see cref="CommitResolved"/>) -- once <see langword="true"/>, <see cref="ApplyIntent"/>
    /// always throws.</summary>
    public bool Committed => Volatile.Read(ref _resolutionState).Committed;

    /// <summary>The currently-stored runtime choice for <paramref name="item"/> --
    /// <see cref="ClipboardRuntimeItemChoice.Unresolved"/> if none has been recorded yet. Does
    /// NOT validate that <paramref name="item"/> belongs to <see cref="Items"/> (a pure query,
    /// never a mutation) -- an item that could never belong to this scope simply always reads as
    /// Unresolved.</summary>
    public ClipboardRuntimeItemChoice GetChoice(ClipboardDecisionItem item) => Volatile.Read(ref _resolutionState).GetChoice(item);

    /// <summary>READINESS (Phase 3B STEP25, computed -- never a stored flag, per that STEP's
    /// instruction): <see langword="true"/> only when every entry in <see cref="Items"/> has a
    /// FINAL choice (<see cref="ClipboardRuntimeItemChoice.Protect"/> or
    /// <see cref="ClipboardRuntimeItemChoice.BypassOnce"/>) -- <see cref="ClipboardRuntimeItemChoice.Unresolved"/>
    /// and <see cref="ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation"/> both count as not
    /// ready. A scope with zero <see cref="Items"/> is never considered ready (production
    /// publication never produces one -- see <c>ClipboardPrivacyProcessor.BuildDecisionPlan</c>'s
    /// own fail-closed guarantee -- so this only affects test-only empty scopes, and treating an
    /// empty scope as vacuously "ready" would be the wrong default for a safety gate).</summary>
    public bool AllItemsResolved => IsFullyResolved(Volatile.Read(ref _resolutionState));

    private bool IsFullyResolved(ClipboardDecisionResolutionState state) =>
        Items.Count > 0 && Items.All(item => IsFinalChoice(state.GetChoice(item)));

    private static bool IsFinalChoice(ClipboardRuntimeItemChoice choice) =>
        choice is ClipboardRuntimeItemChoice.Protect or ClipboardRuntimeItemChoice.BypassOnce;

    /// <summary>
    /// APPLY_INTENT (Phase 3B STEP25, implementing the Phase 3B STEP24 audit's LEVEL1_ACTION_MODEL/
    /// LEVEL3_STATE_MACHINE): records one user-facing <paramref name="intent"/> against exactly one
    /// <paramref name="item"/>, returning the resulting stored choice. ITEM_MEMBERSHIP
    /// (defense-in-depth, in addition to a future resolver's own validation -- Phase 3B STEP25
    /// instruction): throws <see cref="ArgumentException"/> if <paramref name="item"/> is not an
    /// exact structural match for an entry in <see cref="Items"/>. APPLY_INTENT_AFTER_COMMITTED:
    /// throws <see cref="InvalidOperationException"/> if <see cref="Committed"/> is already
    /// <see langword="true"/> -- fail closed, a committed scope is never silently reopened.
    ///
    /// TRANSITION TABLE (Phase 3B STEP24 audit, frozen): for a <see cref="RiskLevel.Level1"/> item,
    /// the result is a pure function of <paramref name="intent"/> alone (<see cref="ClipboardDecisionIntent.Protect"/>
    /// always yields <see cref="ClipboardRuntimeItemChoice.Protect"/>,
    /// <see cref="ClipboardDecisionIntent.BypassOnce"/> always yields
    /// <see cref="ClipboardRuntimeItemChoice.BypassOnce"/> -- regardless of the current stored
    /// choice, so repeating or overwriting a Level1 choice is always immediate). For a
    /// <see cref="RiskLevel.Level3"/> item, <see cref="ClipboardDecisionIntent.Protect"/> ALWAYS
    /// yields <see cref="ClipboardRuntimeItemChoice.Protect"/> immediately (no second
    /// confirmation, no grant -- Phase 3B STEP24 audit's LEVEL3_STATE_MACHINE); but
    /// <see cref="ClipboardDecisionIntent.BypassOnce"/> yields
    /// <see cref="ClipboardRuntimeItemChoice.BypassOnce"/> ONLY when the item's current choice is
    /// already <see cref="ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation"/> (the second,
    /// fully-revalidated request) or already <see cref="ClipboardRuntimeItemChoice.BypassOnce"/>
    /// (idempotent repeat of an already-final choice) -- from EVERY other current state
    /// (<see cref="ClipboardRuntimeItemChoice.Unresolved"/> OR, critically,
    /// <see cref="ClipboardRuntimeItemChoice.Protect"/>) it instead yields
    /// <see cref="ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation"/> -- moving FROM a final
    /// Protect choice back toward raw use always restarts the two-step confirmation from stage 1;
    /// it can never jump directly to a final Bypass choice.
    ///
    /// Any <paramref name="item"/>.<c>RiskLevel</c> other than <see cref="RiskLevel.Level1"/>/
    /// <see cref="RiskLevel.Level3"/> (i.e. <see cref="RiskLevel.Level2"/>, which
    /// <c>CandidatePolicyEvaluator</c>'s own policy matrix can never actually produce as a
    /// NeedsDecision candidate, or any undefined value) throws
    /// <see cref="ArgumentOutOfRangeException"/> -- fail closed on an impossible input rather than
    /// silently falling through to some default behavior.
    /// </summary>
    internal ClipboardRuntimeItemChoice ApplyIntent(ClipboardDecisionItem item, ClipboardDecisionIntent intent)
    {
        if (!Enum.IsDefined(intent))
            throw new ArgumentOutOfRangeException(nameof(intent), intent, "Undefined ClipboardDecisionIntent value.");
        if (!Items.Contains(item))
            throw new ArgumentException("The item does not belong to this scope.", nameof(item));

        var state = Volatile.Read(ref _resolutionState);
        if (state.Committed)
            throw new InvalidOperationException(
                "This scope's resolution state is already committed -- no further choice mutation is allowed.");

        var current = state.GetChoice(item);
        var next = item.RiskLevel switch
        {
            RiskLevel.Level1 => intent switch
            {
                ClipboardDecisionIntent.Protect => ClipboardRuntimeItemChoice.Protect,
                ClipboardDecisionIntent.BypassOnce => ClipboardRuntimeItemChoice.BypassOnce,
                _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Undefined ClipboardDecisionIntent value."),
            },
            RiskLevel.Level3 => intent switch
            {
                ClipboardDecisionIntent.Protect => ClipboardRuntimeItemChoice.Protect,
                ClipboardDecisionIntent.BypassOnce => current switch
                {
                    ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation => ClipboardRuntimeItemChoice.BypassOnce,
                    ClipboardRuntimeItemChoice.BypassOnce => ClipboardRuntimeItemChoice.BypassOnce,
                    _ => ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Undefined ClipboardDecisionIntent value."),
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(item), item.RiskLevel, "Unsupported RiskLevel for runtime decision resolution (only Level1/Level3 NeedsDecision items are expected)."),
        };

        Volatile.Write(ref _resolutionState, state.WithChoice(item, next));
        return next;
    }

    /// <summary>
    /// COMMIT_RESOLVED (Phase 3B STEP25, implementing the Phase 3B STEP24 audit's
    /// ALL_ITEMS_RESOLVED_BARRIER/SELECTED_GRANT_TIMING_MODEL): the ONLY place a
    /// <see cref="ClipboardBypassGrant"/> is ever minted. Requires <see cref="Committed"/> to
    /// still be <see langword="false"/> and <see cref="AllItemsResolved"/> to already be
    /// <see langword="true"/> -- both violations throw <see cref="InvalidOperationException"/>
    /// rather than silently no-op, matching <see cref="ApplyIntent"/>'s own fail-closed
    /// discipline (a caller is expected to check both before calling, exactly like the future
    /// resolver's own IsActive-before-mutation discipline -- Phase 3B STEP24.1 audit).
    ///
    /// For every item whose FINAL stored choice is <see cref="ClipboardRuntimeItemChoice.BypassOnce"/>,
    /// mints exactly one <see cref="ClipboardBypassGrant"/> bound to <paramref name="finalRevision"/>
    /// + that item's own <c>Canonical</c> -- never the item's own <c>RiskLevel</c> (not part of the
    /// frozen grant identity). Two items sharing the same <c>CanonicalValue</c> (STEP18's
    /// DUPLICATE_CANONICAL_POLICY permits this with differing RiskLevel) naturally collapse to one
    /// grant, since <see cref="ClipboardBypassGrant"/> equality does not consider RiskLevel at all.
    /// Items with a final <see cref="ClipboardRuntimeItemChoice.Protect"/> choice never produce a
    /// grant. <paramref name="finalRevision"/> is supplied entirely by the caller -- this method
    /// has no clipboard/AliasMap/write dependency of any kind and does not itself decide whether
    /// that revision is the scope's own unchanged <see cref="InitialStamp"/> (all-Bypass, no write
    /// occurred) or a freshly-observed post-write stamp (Protect involved) -- that determination is
    /// entirely the future resolver's responsibility (Phase 3B STEP24 audit's
    /// ALL_BYPASS_VS_MIXED finding).
    ///
    /// Publishes the final choices + the complete new grant set + <see cref="Committed"/> = true
    /// together, as one new <see cref="ClipboardDecisionResolutionState"/> installed via a single
    /// reference swap -- no intermediate state (e.g. grants set but Committed still false) is ever
    /// externally observable.
    /// </summary>
    internal void CommitResolved(RevisionStamp finalRevision)
    {
        var state = Volatile.Read(ref _resolutionState);
        if (state.Committed)
            throw new InvalidOperationException("This scope's resolution state is already committed.");
        if (!IsFullyResolved(state))
            throw new InvalidOperationException("Not every item in this scope has a final resolved choice yet.");

        var grants = new HashSet<ClipboardBypassGrant>();
        foreach (var item in Items)
        {
            if (state.GetChoice(item) == ClipboardRuntimeItemChoice.BypassOnce)
            {
                grants.Add(new ClipboardBypassGrant(finalRevision, item.Canonical));
            }
        }

        Volatile.Write(ref _resolutionState, state.WithCommit(grants));
    }

    /// <summary>Whether an exact <see cref="ClipboardBypassGrant"/> matching
    /// <paramref name="revision"/> + <paramref name="canonical"/> currently exists on this scope
    /// -- read-only; grant consumption is not implemented anywhere near this type yet (Phase 3B
    /// STEP22/24 audits' SEND_CAPABILITY_LIMITATION).</summary>
    public bool HasBypassGrant(RevisionStamp revision, CanonicalValue canonical) =>
        Volatile.Read(ref _resolutionState).HasGrant(revision, canonical);

    /// <summary>
    /// SCOPE_DIAGNOSTICS (Phase 3B STEP21, extended Phase 3B STEP25): explicit, self-contained
    /// override -- never delegates to <see cref="RevisionStamp"/>'s or
    /// <see cref="ClipboardDecisionResolutionState"/>'s own (already-hardened) <c>ToString()</c>,
    /// matching the established "never rely transitively on a wrapped type's continued safety"
    /// discipline. Exposes only <see cref="Items"/>'s count, <see cref="InitialStamp"/>'s own
    /// safe, non-hash <see cref="RevisionId"/> field, and the resolution state's own safe counts
    /// (<c>ChoiceCount</c>/<c>GrantCount</c>/<c>Committed</c>) directly -- never
    /// <see cref="RevisionStamp.Hash"/>/<see cref="SnapshotHash"/>, never any
    /// <c>CanonicalValue</c> content, never raw text, never an enumeration of
    /// <see cref="Items"/>/choices/grants themselves, never a span or fingerprint of any kind.
    /// </summary>
    public override string ToString()
    {
        var state = Volatile.Read(ref _resolutionState);
        return $"{nameof(ClipboardDecisionScope)} {{ ItemCount = {Items.Count}, InitialRevisionId = {InitialStamp.RevisionId}, " +
               $"Generation = {Generation}, ChoiceCount = {state.ChoiceCount}, GrantCount = {state.GrantCount}, Committed = {state.Committed} }}";
    }
}
