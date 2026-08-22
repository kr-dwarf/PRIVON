namespace Privon.App;

/// <summary>
/// Phase 3C STEP30/31/31.1 -- outcome of one <see cref="ClipboardComposerVerifier.VerifyAsync"/>
/// attempt. Deliberately NOT <see cref="Privon.Core.ProtectionState"/> -- see
/// <see cref="ComposerVerificationResult"/>'s own doc for the point-in-time-evidence-vs-persistent-state
/// distinction this type exists to preserve.
///
/// Declared with <see cref="NotAttempted"/> first so that <c>default(VerificationOutcome)</c> is
/// never mistaken for <see cref="Verified"/> -- the same discipline used throughout this codebase.
///
/// <see cref="TargetUnavailable"/>/<see cref="TargetChanged"/>/<see cref="ComposerNotFocused"/>/
/// <see cref="TextUnavailable"/>/<see cref="AutomationFailure"/> reuse the exact
/// <see cref="Privon.Windows.ComposerReadOutcome"/> vocabulary 1:1 (Phase 3C STEP31.1 audit's
/// OTHER_OUTCOME_MAPPINGS) -- each is a genuine, expected, per-attempt environmental condition.
/// <see cref="Privon.Windows.ComposerReadOutcome.NotRunning"/> and
/// <see cref="Privon.Windows.ComposerReadOutcome.InvalidExpectedTarget"/> both map to
/// <see cref="Failed"/> instead (Phase 3C STEP31.1's NOTRUNNING_MAPPING/INVALID_EXPECTED_TARGET_MAPPING)
/// -- see <see cref="ClipboardComposerVerifier"/>'s own doc for why neither is treated as ordinary
/// environmental drift.
/// </summary>
internal enum VerificationOutcome
{
    NotAttempted,
    Verified,
    Stale,
    TargetUnavailable,
    TargetChanged,
    ComposerNotFocused,
    TextUnavailable,
    AutomationFailure,
    Mismatch,
    Failed,
}
