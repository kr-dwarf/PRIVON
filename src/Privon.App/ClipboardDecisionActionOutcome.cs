namespace Privon.App;

/// <summary>
/// Phase 3B STEP26 -- the metadata-only outcome of one <see cref="ClipboardDecisionActionResolver.ResolveAsync"/>
/// attempt.
///
/// Declared with <see cref="Failed"/> first -- matching this codebase's established "declare the
/// safe/no-op value first so default(...) is never mistaken for something more dangerous"
/// discipline (<c>CandidateDisposition.NeedsDecision</c>, <c>Privon.Windows.ClipboardReadOutcome.NotRunning</c>,
/// <c>Privon.Windows.ClipboardWriteOutcome.NotRunning</c>, <c>ClipboardRuntimeItemChoice.Unresolved</c>).
/// <see cref="Applied"/> is the one outcome that can mean a runtime decision actually took effect
/// (a choice was recorded and/or a final resolution was committed) -- every other value is
/// deliberately NOT that, so <c>default(ClipboardDecisionActionOutcome)</c> can never be silently
/// misread as success.
///
/// None of these values is, or ever implies, <c>Privon.Core.ProtectionState.Verified</c> -- see
/// <see cref="ClipboardDecisionActionResolver"/>'s own class doc.
/// </summary>
internal enum ClipboardDecisionActionOutcome
{
    /// <summary>An unexpected internal exception occurred -- no success of any kind is claimed.
    /// See <see cref="ClipboardDecisionActionResult.ClipboardMutated"/> for whether an external
    /// clipboard write is nonetheless already an irreversible fact by the time this was
    /// returned.</summary>
    Failed,

    /// <summary>The original scope reference is no longer the active decision session (superseded
    /// by a newer clipboard notification, already committed, foreign item, unsupported current
    /// target, a failed/changed guarded read, a revision mismatch, a whole-plan mismatch, or
    /// invalidated at any later STEP24.1 linearization checkpoint). No retry is ever attempted for
    /// this outcome -- see <see cref="ClipboardDecisionActionResolver"/>'s own class doc.</summary>
    Stale,

    /// <summary>The requested item is <see cref="Privon.Core.RiskLevel.Level3"/> and this was the
    /// FIRST fully-revalidated <c>BypassOnce</c> request for it -- the item is now
    /// <c>ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation</c>. No write, no grant, no commit.
    /// The next confirmation must be a brand-new <c>ResolveAsync</c> attempt that repeats the FULL
    /// revalidation flow from scratch.</summary>
    AwaitingSecondConfirmation,

    /// <summary>A final commit required a clipboard write (at least one final overlaid disposition
    /// is Protect) but the write was never attempted (the guarded read's own sequence was
    /// unreliable) or the guarded write itself did not mutate the clipboard at all. No commit, no
    /// grants, no retry.</summary>
    WriteFailed,

    /// <summary>The guarded write DID mutate the clipboard (<see cref="ClipboardDecisionActionResult.ClipboardMutated"/>
    /// is always <see langword="true"/> for this outcome) but its own read-back verification did
    /// not succeed -- no rollback is attempted (the Windows-layer DESTRUCTIVE_BOUNDARY already
    /// forbids one) and no commit/grant is ever produced from an unverified write.</summary>
    MutatedUnverified,

    /// <summary>Either (a) a provisional runtime choice was recorded but at least one sibling item
    /// in the same scope is still unresolved (<see cref="ClipboardDecisionActionResult.ScopeCommitted"/>
    /// is <see langword="false"/>), or (b) every item in the scope is now finally resolved and the
    /// resulting resolution -- including, when required, a verified clipboard write -- was
    /// successfully committed to the scope while it was still the active decision session
    /// (<see cref="ClipboardDecisionActionResult.ScopeCommitted"/> is <see langword="true"/>).
    /// Never <c>Privon.Core.ProtectionState.Verified</c> either way.</summary>
    Applied,
}
