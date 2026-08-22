using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP41.1 -- the narrow, App-owned seam <see cref="PrivonAppComposition"/> uses to
/// route <see cref="ClipboardChangeMonitor.DiagnosticObserved"/> events into the same diagnostic
/// sink the App-level <see cref="IClipboardDiagnosticRecorder"/> already writes to. Deliberately a
/// SEPARATE interface from <see cref="IClipboardDiagnosticRecorder"/> -- the event schemas
/// (<see cref="ClipboardMonitorDiagnosticEvent"/>/<see cref="ClipboardWriteDiagnosticEvent"/> from
/// <c>Privon.Windows</c>, own class docs; <see cref="ClipboardDiagnosticEvent"/> from
/// <c>Privon.App</c>) are never merged into one type, so a reader can never mistake a
/// Windows-layer observation for an App-layer one (see <see cref="ClipboardDiagnosticStage"/>'s
/// own LAYER_BOUNDARY_NAMING doc) -- <c>Privon.Windows</c> itself still never references
/// <c>Privon.App</c> in any way; this interface and its only production implementation both live
/// here, in <c>Privon.App</c>.
///
/// Phase 3C STEP41.2: extended with <see cref="RecordWriteDiagnosticEvent"/> for the SECOND
/// Windows-owned event type <see cref="ClipboardChangeMonitor"/> raises --
/// <see cref="ClipboardWriteDiagnosticEvent"/> (the guarded-write sequence-attribution boundary),
/// distinct from <see cref="ClipboardMonitorDiagnosticEvent"/> (the native-notification boundary).
/// Both remain "this is a Windows-layer, <c>ClipboardChangeMonitor</c>-originated observation" --
/// they are still never merged into one schema, hence two distinct methods rather than one
/// overloaded/unioned one.
/// </summary>
internal interface IClipboardMonitorDiagnosticSink
{
    void RecordWindowsEvent(ClipboardMonitorDiagnosticEvent windowsEvent);

    void RecordWriteDiagnosticEvent(ClipboardWriteDiagnosticEvent writeEvent);
}
