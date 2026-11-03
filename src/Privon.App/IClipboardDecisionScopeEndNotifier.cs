namespace Privon.App;

/// <summary>
/// PRIVON 0.4.0 -- the narrow, OPTIONAL view of the scope lifecycle that says when a decision stops being answerable. A decision scope ends silently:
/// every clipboard change and every session lock drops the active scope, and without this signal a decision prompt that was shown for it would stay on
/// screen, still saying "personal information was detected, press to protect", for a decision that can no longer be answered. It is a separate
/// interface (not a member of <see cref="IClipboardDecisionScopeLifecycle"/>) so every existing implementer and fake is untouched; a consumer asks
/// for it with a type test and works without it.
/// </summary>
internal interface IClipboardDecisionScopeEndNotifier
{
    /// <summary>
    /// Raised once for a scope that was the ACTIVE one when a clipboard change or a session lock dropped it -- never for a scope that was already gone,
    /// never when there was no active scope. It is raised AFTER the lifecycle's lock has been released, on whatever thread dropped the scope (the
    /// clipboard owner thread's notification, or the session-lock thread), so a subscriber must do nothing but hand off; nothing a subscriber throws
    /// reaches the caller. It carries the scope object only -- no text, no values.
    /// </summary>
    event EventHandler<ClipboardDecisionScope>? ActiveScopeEnded;
}
