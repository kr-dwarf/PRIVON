namespace Privon.App;

/// <summary>
/// Phase 3B STEP25 -- the exact set of user-facing runtime decision intents supported by 0.1
/// core (Phase 3B STEP24 audit's ACTION_INTENTS finding): exactly two. Level3's "first request"/
/// "second confirmation" are deliberately NOT separate intents here -- the SAME
/// <see cref="BypassOnce"/> intent is submitted by the caller both times; how many times a given
/// item requires it (once for Level1, twice for Level3) is derived entirely from that item's own
/// current <see cref="ClipboardRuntimeItemChoice"/> inside <c>ClipboardDecisionScope.ApplyIntent</c>,
/// never from a distinct intent value. A future "앞으로도 아님" (persisted exception) intent is
/// deliberately not added yet (Phase 3B STEP24 audit's PERSISTED_TRUST_SCOPE/
/// RELEASE_0_1_REQUIREMENT finding) -- its CURRENT-action runtime effect would be identical to
/// <see cref="BypassOnce"/> regardless, only its (deferred) persistence side effect would differ.
/// </summary>
internal enum ClipboardDecisionIntent
{
    Protect,
    BypassOnce,
}
