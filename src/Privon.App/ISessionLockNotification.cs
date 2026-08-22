namespace Privon.App;

/// <summary>
/// Phase 3C STEP38 -- narrow App-owned seam over <see cref="Privon.Windows.SessionLockMonitor"/>,
/// scoped to exactly what <see cref="PrivonAppComposition"/> needs (one event, start, stop) --
/// mirrors <see cref="IClipboardReadTransport"/>'s own exact shape/reasoning. This interface exists
/// so the composition root's session-lock callback wiring can be tested with a hand-written fake
/// instead of a real Win32 session-lock listener, without widening <c>Privon.Windows</c>'s own
/// public surface or reaching into its internal native seam via <c>InternalsVisibleTo</c> -- the
/// same reasoning already used for <see cref="IClipboardReadTransport"/>/<see cref="IForegroundTargetCapture"/>/
/// <see cref="IComposerReadTransport"/>.
///
/// This interface knows nothing about <c>ClipboardDecisionScopeLifecycle</c>/
/// <c>ClipboardComposerVerifier</c>/"Reset"/"InvalidatePending" -- it is a pure OS-event seam; the
/// policy reaction to <see cref="Locked"/> lives entirely in <see cref="PrivonAppComposition"/>.
/// </summary>
internal interface ISessionLockNotification
{
    /// <summary>See <see cref="Privon.Windows.SessionLockMonitor.Locked"/> -- raised synchronously
    /// on the underlying owner thread, metadata-free.</summary>
    event EventHandler? Locked;

    void Start();
    void Stop();
}
