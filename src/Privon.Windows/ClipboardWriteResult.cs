namespace Privon.Windows;

/// <summary>
/// Phase 3A.4 STEP4 -- the result of one
/// <see cref="ClipboardChangeMonitor.WriteTextIfSequenceMatchesAsync"/> call.
///
/// <see cref="ClipboardMutated"/> is orthogonal to <see cref="Outcome"/>: it answers "did
/// EmptyClipboard actually succeed" independently of whatever happened afterward (Set failure,
/// a Close failure, a failed verification read-back, ...). It is the DESTRUCTIVE_BOUNDARY signal
/// -- once true, the original clipboard content is already gone and this layer never attempts a
/// rollback.
///
/// <see cref="ResultSequence"/> is populated on <see cref="ClipboardWriteOutcome.Success"/> (the
/// VERIFICATION-REOPEN sequence this write's content was proven, in that same open session, to
/// exactly match -- as of Phase 3C STEP42, this is no longer required to equal the intermediate
/// post-Set capture the mutation bracket itself observed), and -- as of Phase 3A.4 STEP4.1 --
/// also on the small set of verification-phase failures (<see cref="ClipboardWriteOutcome.NativeFailure"/>
/// or <see cref="ClipboardWriteOutcome.ReadBackMismatch"/> reached from
/// <c>ClipboardChangeMonitor.VerifyWhileClipboardOpen</c>) where the verification sequence was
/// already confirmed reliable (non-zero) before the failure occurred -- it is still a meaningful,
/// already-confirmed fact in those cases. Every other outcome (including
/// <see cref="ClipboardWriteOutcome.Superseded"/> and <see cref="ClipboardWriteOutcome.VerificationUnavailable"/>,
/// where no such confirmed state was ever established) leaves it null rather than inventing a
/// meaning for a sequence number that was never confirmed.
///
/// <see cref="Win32Error"/> carries only a numeric Win32 error code when one is available -- never
/// clipboard text, never a substring, never a pointer value (same SENSITIVE_DATA_RULES discipline
/// as <see cref="ClipboardTextReadResult"/>). It is legitimately null even for
/// <see cref="ClipboardWriteOutcome.NativeFailure"/> when the failure is a contract-level anomaly
/// with no corresponding Win32 error code (e.g. the verification format vanishing) rather than a
/// failed Win32 call.
/// </summary>
public readonly record struct ClipboardWriteResult
{
    public required ClipboardWriteOutcome Outcome { get; init; }
    public required bool ClipboardMutated { get; init; }
    public uint? ResultSequence { get; init; }
    public int? Win32Error { get; init; }

    public static ClipboardWriteResult Success(uint resultSequence) =>
        new()
        {
            Outcome = ClipboardWriteOutcome.Success,
            ClipboardMutated = true,
            ResultSequence = resultSequence,
        };

    public static ClipboardWriteResult Failure(
        ClipboardWriteOutcome outcome, bool mutated, int? win32Error = null, uint? resultSequence = null)
    {
        if (outcome == ClipboardWriteOutcome.Success)
            throw new ArgumentException("Use Success(...) to construct a successful result.", nameof(outcome));
        return new ClipboardWriteResult
        {
            Outcome = outcome,
            ClipboardMutated = mutated,
            Win32Error = win32Error,
            ResultSequence = resultSequence,
        };
    }
}
