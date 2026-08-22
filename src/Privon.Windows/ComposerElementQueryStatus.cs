namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP29 -- the native-layer status of one <see cref="IComposerTextSource.QueryFocusedElement"/>
/// call. Deliberately narrower/lower-level than <see cref="ComposerReadOutcome"/>: this enum only
/// ever describes what the ONE atomic UI Automation query itself observed (no focused element at
/// all, a focused element was found, or the query itself failed unexpectedly) -- it carries no
/// product-level "is this the composer" judgment (that comparison against an expected target's
/// PID/class/ControlType is <see cref="ComposerTextReader"/>'s own policy, never this native
/// seam's -- matching <see cref="IForegroundTargetSource"/>'s own "no policy of its own"
/// precedent).
///
/// Declared with <see cref="NoFocusedElement"/> first so that <c>default(ComposerElementQueryStatus)</c>
/// is never mistaken for <see cref="Resolved"/>.
/// </summary>
internal enum ComposerElementQueryStatus
{
    NoFocusedElement,
    Resolved,
    AutomationFailure,
}
