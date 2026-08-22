namespace Privon.App;

/// <summary>
/// Phase 3B STEP26 -- the full, metadata-only return of one
/// <see cref="ClipboardDecisionActionResolver.ResolveAsync"/> attempt. Deliberately carries only
/// <see cref="Outcome"/> plus two booleans -- no <see cref="ClipboardDecisionItem"/>, no
/// <see cref="Privon.Detection.CanonicalValue"/>, no <see cref="Privon.Core.RevisionStamp"/>/
/// <see cref="Privon.Core.SnapshotHash"/>, no <see cref="Privon.Windows.ForegroundTargetSnapshot"/>,
/// no clipboard sequence number, no raw or replacement text of any kind -- matching the same
/// "counts/flags only" diagnostic discipline already established for
/// <see cref="ClipboardPrivacyProcessingResult"/>/<see cref="ClipboardDecisionPlan"/>/
/// <see cref="TrustExceptionSnapshot"/>. Never named <c>Protected</c>/<c>Verified</c> -- see
/// <see cref="ClipboardDecisionActionResolver"/>'s own class doc for why.
///
/// <see cref="ClipboardMutated"/> and <see cref="ScopeCommitted"/> are deliberately independent of
/// <see cref="Outcome"/>'s own name -- exactly the same "never classify from the enum name alone"
/// discipline <see cref="ClipboardWriteResultClassifier"/> already established for
/// <see cref="Privon.Windows.ClipboardWriteResult"/>. In particular, <see cref="Outcome"/> ==
/// <see cref="ClipboardDecisionActionOutcome.Stale"/> can still carry <see cref="ClipboardMutated"/>
/// == <see langword="true"/> -- a guarded write may succeed, then a newer external clipboard
/// notification may invalidate the scope before this attempt's own STEP24.1 linearization
/// post-check runs (Phase 3B STEP24.1 audit's SUCCESS_THEN_INVALIDATION finding, applied here to
/// the write path specifically) -- so "Stale" must never be read as "the clipboard is definitely
/// unchanged."
/// </summary>
internal readonly record struct ClipboardDecisionActionResult
{
    public required ClipboardDecisionActionOutcome Outcome { get; init; }
    public required bool ClipboardMutated { get; init; }
    public required bool ScopeCommitted { get; init; }

    public static ClipboardDecisionActionResult Applied(bool clipboardMutated, bool scopeCommitted) =>
        new() { Outcome = ClipboardDecisionActionOutcome.Applied, ClipboardMutated = clipboardMutated, ScopeCommitted = scopeCommitted };

    public static ClipboardDecisionActionResult AwaitingSecondConfirmation() =>
        new() { Outcome = ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, ClipboardMutated = false, ScopeCommitted = false };

    /// <summary><paramref name="clipboardMutated"/> defaults to <see langword="false"/> -- the vast
    /// majority of Stale returns happen before any write is even attempted. The one path where a
    /// verified write already happened before staleness was discovered passes <see langword="true"/>
    /// explicitly.</summary>
    public static ClipboardDecisionActionResult Stale(bool clipboardMutated = false) =>
        new() { Outcome = ClipboardDecisionActionOutcome.Stale, ClipboardMutated = clipboardMutated, ScopeCommitted = false };

    public static ClipboardDecisionActionResult WriteFailed() =>
        new() { Outcome = ClipboardDecisionActionOutcome.WriteFailed, ClipboardMutated = false, ScopeCommitted = false };

    public static ClipboardDecisionActionResult MutatedUnverified() =>
        new() { Outcome = ClipboardDecisionActionOutcome.MutatedUnverified, ClipboardMutated = true, ScopeCommitted = false };

    /// <summary><paramref name="clipboardMutated"/> defaults to <see langword="false"/> -- most
    /// unexpected failures happen before any write is attempted. A failure discovered after a
    /// confirmed external write (but before this attempt could finish) passes <see langword="true"/>
    /// explicitly -- see <see cref="ClipboardDecisionActionResolver.ResolveAsync"/>'s own
    /// ERROR_BOUNDARY doc.</summary>
    public static ClipboardDecisionActionResult Failed(bool clipboardMutated = false) =>
        new() { Outcome = ClipboardDecisionActionOutcome.Failed, ClipboardMutated = clipboardMutated, ScopeCommitted = false };
}
