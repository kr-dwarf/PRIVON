namespace Privon.App;

/// <summary>
/// OPTIONAL capability of a tray surface: show the protection status the
/// <see cref="ClipboardProtectionStatusTracker"/> aggregated. It is a separate interface (not a new member of
/// <see cref="ITrayIconSurface"/>) so every existing implementer and test double stays valid; a tray that does not implement
/// it simply shows nothing new.
///
/// THREADING: invoked only from the UI thread, through <see cref="IDispatcherScheduler"/>.
/// TRUTH: it displays a status and, when <c>notify</c> is set, ONE warning notification. It never claims the product blocked a
/// paste or a send (the product cannot), and it carries no clipboard text.
/// </summary>
internal interface IProtectionStatusSurface
{
    void SetProtectionStatus(ProtectionStatusPresentation presentation, bool notify);
}
