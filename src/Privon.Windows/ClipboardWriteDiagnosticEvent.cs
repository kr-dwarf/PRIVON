namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP41.2 -- one metadata-only observation of the guarded-write sequence-attribution
/// boundary (CAS gate -&gt; post-Set capture -&gt; verification reopen -&gt; read-back comparison -&gt;
/// terminal outcome), raised via <see cref="ClipboardChangeMonitor.WriteDiagnosticObserved"/>.
/// Structurally cannot carry raw clipboard text, replacement text, a
/// <c>Privon.Detection.CanonicalValue</c>, or any other free-form content: every field is a plain
/// <see langword="long"/>/<see langword="uint"/>/<see langword="bool"/>/enum (or a nullable of one
/// of those) -- no <see langword="string"/> field of any kind exists on this type. In particular
/// there is deliberately no read-back "digest"/hash/length field either -- exactly whether the
/// comparison was attempted, and whether it matched, is preferable and sufficient (see
/// <see cref="ClipboardWriteDiagnosticKind.ReadBackAttempted"/>/
/// <see cref="ClipboardWriteDiagnosticKind.ReadBackExactMatch"/>).
///
/// <see cref="WriteId"/> is a plain, owner-thread-only monotonic counter, distinct from and never
/// shared with <see cref="ClipboardMonitorDiagnosticEvent.LocalEventId"/> (that counter belongs to
/// the NATIVE-NOTIFICATION boundary; this one belongs to the GUARDED-WRITE boundary -- a single real
/// write can, and normally does, itself later cause a native notification the OTHER counter
/// separately numbers, and the two are never conflated). It is incremented exactly once per real
/// <c>ExecuteWrite</c> invocation and is shared by every <see cref="ClipboardWriteDiagnosticEvent"/>
/// that one invocation raises, so a reader can group
/// <see cref="ClipboardWriteDiagnosticKind.WriteStarted"/> through
/// <see cref="ClipboardWriteDiagnosticKind.WriteCompleted"/> into the exact sequence-number timeline
/// for one write attempt.
///
/// CROSS_LAYER_CORRELATION (frozen for this STEP): <see cref="WriteId"/> is Windows-owned and never
/// shared with <c>Privon.App</c> -- there is no shared identifier across this layer boundary for
/// writes, exactly as <see cref="ClipboardMonitorDiagnosticEvent"/>'s own doc already documents for
/// the read/notification boundary. Correlation with the App-layer trace is instead by EXACT VALUE
/// MATCH: a <see cref="ClipboardWriteDiagnosticKind.WriteStarted"/> event's
/// <see cref="ExpectedSequence"/> is the exact same <c>uint</c> the App layer already knows as the
/// successful guarded read's own <c>ClipboardTextSnapshot.SequenceNumber</c> for that same
/// attempt (i.e. <c>Privon.App.ClipboardDiagnosticEvent.GuardedReadCompleted</c>'s own
/// <c>SequenceNumber</c> field) -- <c>WriteTextIfSequenceMatchesAsync</c>'s <c>expectedSequence</c>
/// parameter is passed through unchanged from that exact value, so a reader can pair one
/// <see cref="ClipboardWriteDiagnosticKind.WriteStarted"/> line with the one App-layer attempt whose
/// <c>GuardedReadCompleted.sequenceNumber</c> equals it. This is a value-based, not an
/// identifier-based, correlation -- inventing a shared id neither layer already tracks would be a
/// fabrication this codebase's own established discipline (see that same class doc) rejects.
///
/// SEQUENCE_POINT_LIMITATION (frozen for this STEP): there is deliberately no observation for "the
/// clipboard sequence immediately after <c>CloseClipboard</c>" as its own, separate native call --
/// the real write flow never queries <c>GetClipboardSequenceNumber()</c> at that exact point today,
/// and adding one purely for diagnostics would be a genuine new native call inserted into the real
/// write path (a behavior change this STEP's own instruction forbids), not merely a passive
/// observation of an existing one. The closest available substitute is
/// <see cref="ClipboardWriteDiagnosticKind.VerificationSequenceObserved"/> itself: in the real flow,
/// <c>VerifyWrite</c>'s own re-open (and its immediate <c>GetClipboardSequenceNumber()</c> call) is
/// the very next thing that happens after the mutation bracket's <c>CloseClipboard</c> succeeds (with
/// only the <c>writeSequence == 0</c> early-return possibly intervening) -- so this is reported
/// honestly as a real, un-closeable gap in observability, never papered over with a fabricated value.
/// </summary>
public readonly record struct ClipboardWriteDiagnosticEvent(
    long WriteId,
    ClipboardWriteDiagnosticKind Kind,
    uint? ExpectedSequence,
    uint? ObservedSequence,
    bool? SequenceMatched,
    bool? ReadBackExactMatch,
    ClipboardWriteOutcome? Outcome,
    bool? ClipboardMutated);
