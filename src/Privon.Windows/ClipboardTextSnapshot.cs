namespace Privon.Windows;

/// <summary>
/// Phase 3A.4 STEP3 -- a single, self-consistent clipboard text read: the sequence number and
/// the text were captured together inside one OpenClipboard/CloseClipboard bracket (Phase 3A.4
/// STEP1 report's SNAPSHOT_SEMANTICS finding), never assembled from two separate calls.
///
/// <see cref="HasReliableSequence"/> follows exactly the same fail-closed rule as
/// <see cref="ClipboardChangeNotification"/>: a 0 sequence is never trusted for a future
/// compare-and-write, regardless of why it was 0 -- but the text itself can still be validly
/// read even when the sequence is unreliable (reading and compare-and-write are independent
/// concerns; see the STEP3 report's SNAPSHOT_SEMANTICS section).
///
/// <see cref="Text"/> is the raw text exactly as read from the clipboard -- no CRLF
/// normalization, no zero-width removal, no Unicode normalization, no trimming. Normalizing
/// text is Privon.Detection.NormalizedView's job, not this transport layer's (Phase 3A.4 STEP3
/// instruction's explicit NO_TEXT_NORMALIZATION rule).
///
/// RAW_CLIPBOARD_DIAGNOSTIC_SURFACE (Phase 3A.4 STEP3.2): <see cref="ToString"/> is explicitly
/// overridden to exclude <see cref="Text"/> entirely -- metadata only. The synthesized record
/// ToString() this type would otherwise get from the compiler prints every property including
/// <see cref="Text"/>, which would turn any accidental <c>Debug.WriteLine(snapshot)</c>/logger
/// call/test-output interpolation into a raw-clipboard-text leak. No text length or any other
/// content-derived value is included either -- the safest contract is no content-derived
/// metadata at all.
/// </summary>
public readonly record struct ClipboardTextSnapshot(uint SequenceNumber, bool HasReliableSequence, string Text)
{
    public override string ToString() =>
        $"{nameof(ClipboardTextSnapshot)} {{ {nameof(SequenceNumber)} = {SequenceNumber}, {nameof(HasReliableSequence)} = {HasReliableSequence} }}";
}
