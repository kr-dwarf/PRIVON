namespace Privon.Windows;

/// <summary>
/// Phase 3A.4 STEP3 -- the result of one <see cref="ClipboardChangeMonitor.ReadTextSnapshotAsync"/>
/// call. <see cref="Snapshot"/> is populated only when <see cref="Outcome"/> is
/// <see cref="ClipboardReadOutcome.Success"/> -- callers must check <see cref="Outcome"/> first,
/// never assume <see cref="Snapshot"/> is present.
///
/// <see cref="Win32Error"/> carries only a numeric Win32 error code when one is available (never
/// clipboard text, never a substring, never a pointer value) -- per the Phase 3A.4 STEP1/STEP2
/// SENSITIVE_DATA_RULES findings, extended here to the read path.
///
/// RAW_CLIPBOARD_DIAGNOSTIC_SURFACE (Phase 3A.4 STEP3.2): <see cref="ToString"/> is explicitly
/// overridden to metadata only -- Outcome plus (for a successful result) the snapshot's
/// SequenceNumber/HasReliableSequence, or (for a failure) Win32Error. It reads
/// <see cref="ClipboardTextSnapshot.SequenceNumber"/>/<see cref="ClipboardTextSnapshot.HasReliableSequence"/>
/// directly rather than calling <c>Snapshot.Value.ToString()</c> -- this type's own safety must
/// not depend on <see cref="ClipboardTextSnapshot"/>'s override continuing to exist/stay safe in
/// the future; it is self-contained.
/// </summary>
public readonly record struct ClipboardTextReadResult
{
    public required ClipboardReadOutcome Outcome { get; init; }
    public ClipboardTextSnapshot? Snapshot { get; init; }
    public int? Win32Error { get; init; }

    public static ClipboardTextReadResult Success(ClipboardTextSnapshot snapshot) =>
        new() { Outcome = ClipboardReadOutcome.Success, Snapshot = snapshot };

    public static ClipboardTextReadResult Failure(ClipboardReadOutcome outcome, int? win32Error = null)
    {
        if (outcome == ClipboardReadOutcome.Success)
            throw new ArgumentException("Use Success(...) to construct a successful result.", nameof(outcome));
        return new ClipboardTextReadResult { Outcome = outcome, Win32Error = win32Error };
    }

    public override string ToString()
    {
        if (Outcome == ClipboardReadOutcome.Success && Snapshot is { } snapshot)
        {
            return $"{nameof(ClipboardTextReadResult)} {{ {nameof(Outcome)} = {Outcome}, " +
                   $"SequenceNumber = {snapshot.SequenceNumber}, HasReliableSequence = {snapshot.HasReliableSequence} }}";
        }

        return $"{nameof(ClipboardTextReadResult)} {{ {nameof(Outcome)} = {Outcome}, {nameof(Win32Error)} = " +
               $"{(Win32Error is { } err ? err.ToString() : "null")} }}";
    }
}
