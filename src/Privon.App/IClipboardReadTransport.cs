using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP2 -- narrow App-owned seam over <see cref="ClipboardChangeMonitor"/>'s guarded
/// read surface, scoped to exactly what <c>ClipboardPrivacyCoordinator</c> needs. Deliberately
/// READ-ONLY for this STEP: no write method exists here -- replacement-write orchestration is a
/// future STEP's own seam addition, not designed here (avoids designing future API prematurely).
///
/// This interface exists so App-level orchestration can be tested with a hand-written fake
/// instead of a real Win32 clipboard/foreground listener, without widening
/// <c>Privon.Windows</c>'s own public surface (which Phase 3A.5 STEP4 deliberately narrowed to
/// guarded-only) or reaching into its internal native seams via <c>InternalsVisibleTo</c>.
/// </summary>
internal interface IClipboardReadTransport
{
    /// <summary>
    /// Metadata-only clipboard-change notification, raised synchronously on the underlying
    /// Windows owner thread -- see <see cref="ClipboardChangeMonitor.Changed"/>'s own
    /// CALLBACK_THREAD_CONTRACT doc for the full contract this still carries unchanged.
    /// </summary>
    event EventHandler<ClipboardChangeNotification>? Changed;

    void Start();
    void Stop();

    /// <summary>See <see cref="ClipboardChangeMonitor.ReadTextSnapshotAsync(ForegroundTargetSnapshot)"/>.</summary>
    Task<ClipboardTextReadResult> ReadTextSnapshotAsync(ForegroundTargetSnapshot expectedTarget);
}
