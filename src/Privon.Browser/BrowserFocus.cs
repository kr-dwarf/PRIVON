namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031C/031E1 -- the mechanical, product-policy-free tri-state result of
/// whether the browser window that owns a given <see cref="WebForegroundEvidence"/> was, at the
/// instant that evidence was produced, the browser's own focused window. Deliberately mirrors
/// <c>Privon.Windows.PackageIdentityResolution</c>'s own established discipline: a closed,
/// structurally distinct three-state fact, never a plain <see langword="bool"/> -- "we never
/// found out" must never be conflated with "we found out, and it was NOT focused".
///
/// Declared with <see cref="Unresolved"/> first (value 0), matching this codebase's "safe value
/// first" convention (<c>PackageIdentityResolution.Unresolved</c>,
/// <c>ExecutableSignatureResolution.NotInspected</c>): a caller that forgets to set this field, or
/// receives a default-initialized <see cref="WebForegroundEvidence"/>, can never mistake silence
/// for <see cref="Focused"/>. This type carries no opinion about which product/origin is
/// supported -- that judgment belongs exclusively to <c>Privon.App.WebTargetGate</c>.
/// </summary>
public enum BrowserFocus
{
    /// <summary>Focus state could not be established (the ordinary, expected state for a
    /// default-initialized or not-yet-resolved <see cref="WebForegroundEvidence"/>). Never treated
    /// as equivalent to <see cref="Focused"/>.</summary>
    Unresolved,

    /// <summary>Definitively established that the browser's own focused window was NOT the window
    /// this evidence describes (e.g. a background tab, or the user alt-tabbed to another
    /// window/application entirely).</summary>
    NotFocused,

    /// <summary>Definitively established that the browser's own focused window was the window this
    /// evidence describes, at the instant the evidence was produced.</summary>
    Focused,
}
