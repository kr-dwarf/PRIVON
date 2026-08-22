using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP14 -- APP_MUTATED_UNVERIFIED_WRITE_POLICY / APP_WRITE_SUCCESS_NOT_VERIFIED
/// (frozen, Phase 3B STEP13 audit): the only place in <c>Privon.App</c> that classifies a
/// <see cref="ClipboardWriteResult"/> as "clipboard rewrite verified" -- and even that
/// classification means only "the guarded write's own read-back verification succeeded," never
/// <c>Privon.Core.ProtectionState.Verified</c> (which additionally requires an actual composer
/// paste, composer read-back, and final validation -- docs/release-gate.md, unaffected by this
/// type). No retry/rollback decision is ever made from this classification -- it exists purely so
/// a future step can tell "verified rewrite" apart from every other outcome without re-deriving
/// the rule inline, and so this rule itself has one direct, focused unit test surface.
///
/// A pure static function over <see cref="ClipboardWriteResult"/>'s own already-orthogonal
/// <see cref="ClipboardWriteResult.Outcome"/>/<see cref="ClipboardWriteResult.ClipboardMutated"/>
/// fields -- never classifies from <see cref="ClipboardWriteOutcome"/>'s enum name alone (see
/// <see cref="ClipboardWriteResult.ClipboardMutated"/>'s own doc: it is the authoritative
/// DESTRUCTIVE_BOUNDARY signal, independent of <see cref="ClipboardWriteResult.Outcome"/>). Every
/// outcome other than <see cref="ClipboardWriteOutcome.Success"/> with
/// <see cref="ClipboardWriteResult.ClipboardMutated"/> true -- including
/// <see cref="ClipboardWriteOutcome.NativeFailure"/>/<see cref="ClipboardWriteOutcome.VerificationUnavailable"/>/
/// <see cref="ClipboardWriteOutcome.Superseded"/>/<see cref="ClipboardWriteOutcome.ReadBackMismatch"/>
/// even when <see cref="ClipboardWriteResult.ClipboardMutated"/> is true (a mutated-but-unverified
/// clipboard) -- classifies as false here, with no distinction made between "no mutation
/// occurred" and "mutation occurred but is unverified": both are simply "not a verified rewrite,"
/// exactly matching this type's own single boolean contract. Callers that need the finer
/// mutated/unmutated distinction already have it directly via
/// <see cref="ClipboardWriteResult.ClipboardMutated"/> itself.
///
/// <see cref="ClipboardWriteOutcome.Success"/> is structurally only ever constructed with
/// <see cref="ClipboardWriteResult.ClipboardMutated"/> true (see
/// <see cref="ClipboardWriteResult.Success"/>'s own factory -- there is no code path that
/// produces <see cref="ClipboardWriteOutcome.Success"/> with <see cref="ClipboardWriteResult.ClipboardMutated"/>
/// false), so the AND below is defense-in-depth against a value shape this type has never
/// actually observed occur -- matching this codebase's established practice of checking an
/// invariant explicitly rather than trusting a constructor contract alone (e.g.
/// TYPED_VALUE_PIITYPE_INVARIANT).
/// </summary>
internal static class ClipboardWriteResultClassifier
{
    public static bool IsRewriteVerified(ClipboardWriteResult result) =>
        result.Outcome == ClipboardWriteOutcome.Success && result.ClipboardMutated;
}
