namespace Privon.Windows;

/// <summary>
/// Phase 3A.4 STEP2 -- metadata-only clipboard-change notification. Deliberately carries no
/// clipboard text and no field capable of holding it: this layer knows only that the clipboard
/// changed, not what it now contains (see the Phase 3A.4 STEP1 report's
/// EVENT_PAYLOAD_STRATEGY/WINDOWS_BOUNDARY_RESPONSIBILITY findings). Reading text, if it is
/// ever needed, is a separate transport-layer concern for a future STEP -- not implemented here.
///
/// <see cref="HasReliableSequence"/> is PRIVON's own fail-closed interpretation of
/// <see cref="SequenceNumber"/> being 0 -- Microsoft's documented contract only states that
/// missing WINSTA_ACCESSCLIPBOARD access produces 0, not that 0 can *only* mean that (see the
/// Phase 3A.4 STEP1 report's CLIPBOARD_SEQUENCE_ZERO_SEMANTICS finding). A 0 sequence is
/// therefore never treated as usable for a guarded compare/write, regardless of why it was 0.
/// </summary>
public readonly record struct ClipboardChangeNotification(
    uint SequenceNumber,
    bool HasReliableSequence,
    bool HasUnicodeText);
