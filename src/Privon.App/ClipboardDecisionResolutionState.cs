using Privon.Core;
using Privon.Detection;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP25 -- the single, immutable snapshot of a <see cref="ClipboardDecisionScope"/>'s
/// runtime resolution progress: per-item provisional/final choices, minted one-time bypass
/// grants, and whether final resolution has been committed. Owned exclusively by
/// <see cref="ClipboardDecisionScope"/> -- constructed, mutated (by building a new instance --
/// this type itself has no setter of any kind), and read only through that type's own narrow
/// API; the <see cref="WithChoice"/>/<see cref="WithCommit"/> factory methods are
/// <see langword="internal"/>, intended only for that owning type's own use, never for a future
/// resolver to call directly (Phase 3B STEP25 instruction's explicit "do not expose a mutable
/// resolution-state object" requirement).
///
/// RESOLUTION_STATE_CONSISTENCY (Phase 3B STEP24/STEP24.1 audits, frozen, implemented here):
/// <see cref="Committed"/> plus both backing collections are set together, once, at construction
/// -- there is no path that updates one without the others. A caller can therefore never observe
/// an impossible partially-updated combination (e.g. grants installed but <see cref="Committed"/>
/// still <see langword="false"/>) because there is no code path that produces such a combination;
/// every transition is "construct one entirely new instance carrying the full next state, then
/// publish that single reference" (see <see cref="ClipboardDecisionScope"/>'s own
/// <c>Volatile.Write</c> of its single resolution-state field).
///
/// Both backing collections are defensively copied on construction (copy-on-write) so that an
/// older snapshot -- if anything still holds a reference to one -- can never be mutated by a
/// later update building on it.
///
/// Diagnostics: <see cref="ToString"/> exposes only counts and the <see cref="Committed"/> flag
/// -- never enumerates <see cref="ClipboardDecisionItem"/>/<see cref="ClipboardBypassGrant"/>
/// content, matching the same "counts only" discipline already established for
/// <see cref="ClipboardDecisionPlan"/>/<c>TrustExceptionSnapshot</c>.
/// </summary>
internal sealed class ClipboardDecisionResolutionState
{
    private readonly IReadOnlyDictionary<ClipboardDecisionItem, ClipboardRuntimeItemChoice> _choices;
    private readonly IReadOnlySet<ClipboardBypassGrant> _grants;

    /// <summary>The state every new <see cref="ClipboardDecisionScope"/> begins with: zero
    /// choices, zero grants, not committed.</summary>
    public static readonly ClipboardDecisionResolutionState Empty = new(
        new Dictionary<ClipboardDecisionItem, ClipboardRuntimeItemChoice>(),
        new HashSet<ClipboardBypassGrant>(),
        committed: false);

    private ClipboardDecisionResolutionState(
        IReadOnlyDictionary<ClipboardDecisionItem, ClipboardRuntimeItemChoice> choices,
        IReadOnlySet<ClipboardBypassGrant> grants,
        bool committed)
    {
        _choices = choices;
        _grants = grants;
        Committed = committed;
    }

    public bool Committed { get; }
    public int ChoiceCount => _choices.Count;
    public int GrantCount => _grants.Count;

    /// <summary>Returns the stored choice for <paramref name="item"/>, or
    /// <see cref="ClipboardRuntimeItemChoice.Unresolved"/> if no choice has been recorded for it
    /// yet -- a missing dictionary entry IS the "unresolved" representation (no entry is ever
    /// stored with that value explicitly).</summary>
    public ClipboardRuntimeItemChoice GetChoice(ClipboardDecisionItem item) =>
        _choices.TryGetValue(item, out var choice) ? choice : ClipboardRuntimeItemChoice.Unresolved;

    public bool HasGrant(RevisionStamp revision, CanonicalValue canonical) =>
        _grants.Contains(new ClipboardBypassGrant(revision, canonical));

    /// <summary>Builds the next snapshot with exactly one item's choice updated -- everything
    /// else, including the (always <see langword="false"/> at this point -- see
    /// <c>ClipboardDecisionScope.ApplyIntent</c>'s own committed-rejection check)
    /// <see cref="Committed"/> flag and grant set, is carried over unchanged.</summary>
    internal ClipboardDecisionResolutionState WithChoice(ClipboardDecisionItem item, ClipboardRuntimeItemChoice choice)
    {
        var nextChoices = new Dictionary<ClipboardDecisionItem, ClipboardRuntimeItemChoice>(_choices) { [item] = choice };
        return new ClipboardDecisionResolutionState(nextChoices, _grants, Committed);
    }

    /// <summary>Builds the final committed snapshot -- choices are carried over exactly as
    /// already validated fully resolved by the caller, <paramref name="grants"/> replaces
    /// whatever grant set existed before (always empty before this point -- grants are never
    /// minted at provisional-choice time, see the Phase 3B STEP24 audit's frozen
    /// SELECTED_GRANT_TIMING_MODEL), and <see cref="Committed"/> becomes <see langword="true"/>
    /// -- all three together, in the one new instance this method returns.</summary>
    internal ClipboardDecisionResolutionState WithCommit(IReadOnlySet<ClipboardBypassGrant> grants) =>
        new(_choices, new HashSet<ClipboardBypassGrant>(grants), committed: true);

    public override string ToString() =>
        $"{nameof(ClipboardDecisionResolutionState)} {{ ChoiceCount = {ChoiceCount}, GrantCount = {GrantCount}, {nameof(Committed)} = {Committed} }}";
}
