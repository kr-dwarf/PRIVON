namespace Privon.App;

/// <summary>
/// Phase 3B STEP19 -- the internal, sensitive, ephemeral handoff of NeedsDecision candidate
/// identity from <see cref="ClipboardPrivacyProcessor"/> toward a future Runtime Decision
/// session (Phase 3B STEP18 audit's DECISION_PLAN_PURPOSE, frozen). It is deliberately NOT UI
/// state, NOT grant state, NOT persisted state, NOT <see cref="ClipboardWritePlan"/>, NOT
/// <c>Privon.Core.ProtectionState</c>, and NOT a raw-text container -- it exists only so a future
/// consumer can know WHICH canonical identities need a decision and at what
/// <c>Privon.Core.RiskLevel</c>. Nothing about a decision is resolved, published, or acted on by
/// this type itself.
///
/// <see cref="Items"/> is defensively materialized from whatever collection the caller passed to
/// the constructor -- later mutation of that original collection can never alter this plan's own
/// contents afterward. Deduplicated (by <see cref="ClipboardDecisionItem"/>'s own structural
/// equality) and first-appearance-ordered by <see cref="ClipboardPrivacyProcessor"/>'s own
/// DECISION_PLAN_DUPLICATE_POLICY implementation -- this type itself does not deduplicate or
/// reorder; it only stores exactly what it was given.
///
/// <see cref="ToString"/> exposes only a count -- never enumerates or stringifies
/// <see cref="Items"/> -- matching the same "counts only" diagnostic discipline already
/// established for <see cref="ClipboardWritePlan"/>/<c>TrustExceptionSnapshot</c>.
/// </summary>
internal sealed class ClipboardDecisionPlan
{
    public ClipboardDecisionPlan(IReadOnlyList<ClipboardDecisionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToArray();
    }

    public IReadOnlyList<ClipboardDecisionItem> Items { get; }

    public override string ToString() => $"{nameof(ClipboardDecisionPlan)} {{ ItemCount = {Items.Count} }}";
}
