namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP41.1 -- one metadata-only observation of the native
/// <c>WM_CLIPBOARDUPDATE</c> -&gt; self-write-suppression-decision -&gt;
/// <see cref="ClipboardChangeMonitor.Changed"/>-delivery boundary, raised via
/// <see cref="ClipboardChangeMonitor.DiagnosticObserved"/>. Structurally cannot carry raw
/// clipboard text, protected text, a <c>Privon.Detection.CanonicalValue</c>, or any other
/// free-form content: every field is a plain <see langword="long"/>/<see langword="uint"/>/
/// <see langword="bool"/>/enum. There is no <see langword="string"/> field of any kind on this
/// type -- not even a process/window name, since none is needed at this exact boundary (that
/// judgment belongs entirely to the App-layer target-capture stage, not here).
///
/// <see cref="LocalEventId"/> is a plain, owner-thread-only monotonic counter (never a clipboard
/// sequence number, never reset, never shared with any other component) -- its only purpose is to
/// let a reader pair the <see cref="ClipboardMonitorDiagnosticKind.NativeNotificationReceived"/>
/// event with the exactly one terminal event (<see cref="ClipboardMonitorDiagnosticKind.SelfWriteSuppressed"/>
/// or <see cref="ClipboardMonitorDiagnosticKind.ExternalChangeRaised"/>) that belongs to the SAME
/// real <c>RaiseChanged</c> invocation, even when <see cref="SequenceNumber"/> is 0 (unreliable)
/// for that invocation.
///
/// CROSS_LAYER_CORRELATION (frozen for this STEP, see <c>Privon.App.ClipboardDiagnosticStage</c>'s
/// own LAYER_BOUNDARY_NAMING doc for the App-side half of this): an
/// <see cref="ClipboardMonitorDiagnosticKind.ExternalChangeRaised"/> event's
/// <see cref="SequenceNumber"/> is the EXACT SAME value the resulting
/// <see cref="ClipboardChangeNotification.SequenceNumber"/> carries into
/// <c>Privon.App</c> -- when <see cref="HasReliableSequence"/> is <see langword="true"/>, a reader
/// can therefore pair this Windows-layer event with the App-layer event that reports the identical
/// <c>SequenceNumber</c> by EXACT VALUE MATCH, not merely by timestamp proximity. When
/// <see cref="HasReliableSequence"/> is <see langword="false"/> (sequence 0), exact-value
/// correlation is not possible (multiple unrelated 0-sequence events cannot be told apart by value
/// alone) -- a reader must then fall back to wall-clock/log-order proximity between this event and
/// the very next App-layer trace entry. This limitation is inherent to the underlying OS API
/// (<c>GetClipboardSequenceNumber</c> itself), not something this type's own design can close, and
/// is documented here rather than papered over with a fabricated shared identifier: the
/// App-layer <c>AttemptId</c> (a clipboard-attempt generation) does not exist yet at the moment
/// this event is raised, so it is never invented or guessed at this boundary.
/// </summary>
public readonly record struct ClipboardMonitorDiagnosticEvent(
    long LocalEventId,
    ClipboardMonitorDiagnosticKind Kind,
    uint SequenceNumber,
    bool HasReliableSequence);
