namespace Privon.Windows;

/// <summary>
/// Outcome of one <see cref="ComposerTextReader.ReadFocusedComposerTextAsync"/> call. Deliberately
/// NOT <see cref="Privon.Core.ProtectionState"/> -- this is a Windows-transport-layer outcome, not
/// a product-level protection state, matching the same rule already established for
/// <see cref="ClipboardReadOutcome"/>/<see cref="ClipboardWriteOutcome"/>.
///
/// Declared with <see cref="NotRunning"/> first so that <c>default(ComposerReadOutcome)</c> is
/// never mistaken for <see cref="Success"/> -- the same discipline already used throughout this
/// codebase.
///
/// <see cref="InvalidExpectedTarget"/>/<see cref="TargetUnavailable"/>/<see cref="TargetChanged"/>
/// reuse the EXACT names already established by <see cref="ClipboardReadOutcome"/>/
/// <see cref="ClipboardWriteOutcome"/>'s own FOREGROUND_READ_EXECUTION_GUARD/
/// FOREGROUND_WRITE_EXECUTION_GUARD additions (Phase 3A.5 STEP4) -- a deliberate vocabulary
/// choice, not three near-duplicate names, so the whole <c>Privon.Windows</c> layer speaks one
/// consistent language for foreground-target guard outcomes.
/// </summary>
public enum ComposerReadOutcome
{
    NotRunning,
    Success,
    InvalidExpectedTarget,
    TargetUnavailable,
    TargetChanged,
    ComposerNotFocused,
    TextUnavailable,
    AutomationFailure,
}
