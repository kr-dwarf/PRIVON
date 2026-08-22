namespace Privon.App;

/// <summary>
/// Phase 3B STEP25 -- the per-item runtime resolution state a <see cref="ClipboardDecisionScope"/>
/// stores against one of its own <see cref="ClipboardDecisionItem"/> entries.
///
/// Declared with <see cref="Unresolved"/> first -- matching this codebase's established
/// "declare the safe/no-op value first so default(...) is never mistaken for something more
/// dangerous" discipline (<c>CandidateDisposition.NeedsDecision</c>,
/// <c>Privon.Windows.ClipboardReadOutcome.NotRunning</c>,
/// <c>Privon.Windows.ClipboardWriteOutcome.NotRunning</c>) -- even though
/// <see cref="ClipboardDecisionResolutionState.GetChoice"/> already treats a MISSING dictionary
/// entry as <see cref="Unresolved"/> before this enum's own default value would ever come into
/// play. This is deliberate double protection, not a contradiction: an entry can never
/// legitimately exist in the choice map with the value <see cref="Unresolved"/> in the first
/// place (see <c>ClipboardDecisionScope.ApplyIntent</c>, which only ever writes
/// <see cref="Protect"/>, <see cref="BypassOnce"/>, or <see cref="AwaitingLevel3Confirmation"/>)
/// -- but if that invariant were ever accidentally violated somewhere, a stray
/// <c>default(ClipboardRuntimeItemChoice)</c> would still resolve to the safe value, never to
/// <see cref="BypassOnce"/>.
///
/// Only <see cref="AwaitingLevel3Confirmation"/> is Level3-specific (see
/// <c>ClipboardDecisionScope.ApplyIntent</c>'s own transition table) -- a Level1 item's stored
/// choice is always exactly one of <see cref="Unresolved"/>/<see cref="Protect"/>/
/// <see cref="BypassOnce"/>, never this value.
/// </summary>
internal enum ClipboardRuntimeItemChoice
{
    Unresolved,
    Protect,
    BypassOnce,
    AwaitingLevel3Confirmation,
}
