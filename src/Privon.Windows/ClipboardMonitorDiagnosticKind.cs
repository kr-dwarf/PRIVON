namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP41.1 -- the exact native-boundary moment one
/// <see cref="ClipboardMonitorDiagnosticEvent"/> describes. Every value corresponds to an actual,
/// already-existing point inside <see cref="ClipboardChangeMonitor"/>'s own <c>RaiseChanged</c>
/// method -- none of these are invented stages.
///
/// TEMPORARY, THIS-STEP-ONLY INSTRUMENTATION (mirrors <c>Privon.App.ClipboardDiagnosticStage</c>'s
/// own scope note): exists purely so a real Windows + real ChatGPT manual reproduction can
/// distinguish "the native notification was never received," "it was received but classified as
/// our own self-write," and "it was received and handed to <see cref="ClipboardChangeMonitor.Changed"/>"
/// without inspecting source or attaching a debugger. Not part of the frozen 0.1 product surface.
///
/// Declared with <see cref="None"/> first so <c>default(ClipboardMonitorDiagnosticKind)</c> is
/// never mistaken for a real observation -- the same discipline already used throughout this
/// codebase (e.g. <see cref="ClipboardReadOutcome.NotRunning"/>).
/// </summary>
public enum ClipboardMonitorDiagnosticKind
{
    None,

    /// <summary>Fired unconditionally, exactly once, at the very top of every real
    /// <c>RaiseChanged</c> invocation -- i.e. once per actual <c>WM_CLIPBOARDUPDATE</c>-driven
    /// message the owner thread's message loop dispatched. This is the true native-boundary
    /// arrival point; nothing about suppression has been decided yet when this fires.</summary>
    NativeNotificationReceived,

    /// <summary>Fired only when the notification just observed above was classified as this
    /// process's own successful write becoming visible again (the existing
    /// SELF_WRITE_SUPPRESSION_MARKER_LIFECYCLE rule, entirely unchanged by this STEP) --
    /// <see cref="ClipboardChangeMonitor.Changed"/> is NOT raised for this native event. No App
    /// attempt is ever expected to follow this event.</summary>
    SelfWriteSuppressed,

    /// <summary>Fired only when the notification just observed above was NOT suppressed --
    /// immediately before <see cref="ClipboardChangeMonitor.Changed"/> is actually invoked, using
    /// the exact same sequence number that notification itself carries (see
    /// <see cref="ClipboardMonitorDiagnosticEvent.SequenceNumber"/>'s own doc for the resulting
    /// exact-match correlation this enables with the App-layer trace).</summary>
    ExternalChangeRaised,
}
