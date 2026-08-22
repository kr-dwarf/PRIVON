namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP41.2 -- the exact native-boundary moment one
/// <see cref="ClipboardWriteDiagnosticEvent"/> describes, along the guarded-write path
/// (<see cref="ClipboardChangeMonitor"/>'s own <c>ExecuteWrite</c>/<c>MutateWhileClipboardOpen</c>/
/// <c>VerifyWrite</c>/<c>VerifyWhileClipboardOpen</c>). Every value corresponds to an actual,
/// already-existing sequence-number read or comparison already performed by that write flow -- none
/// of these are invented stages, and none of them add a new native call that did not already exist
/// (see <see cref="ClipboardWriteDiagnosticEvent"/>'s own class doc for the one point this STEP's
/// audit deliberately declined to instrument for exactly that reason).
///
/// TEMPORARY, THIS-STEP-ONLY INSTRUMENTATION (mirrors <see cref="ClipboardMonitorDiagnosticKind"/>'s
/// own scope note): exists purely so a real Windows + real ChatGPT manual reproduction can attribute
/// a <see cref="Privon.Windows.ClipboardWriteOutcome.Superseded"/> (or any other guarded-write
/// outcome) to the exact sequence-number transition that produced it, without inspecting source or
/// attaching a debugger. Not part of the frozen 0.1 product surface.
///
/// Declared with <see cref="None"/> first so <c>default(ClipboardWriteDiagnosticKind)</c> is never
/// mistaken for a real observation -- the same discipline already used throughout this codebase
/// (e.g. <see cref="ClipboardMonitorDiagnosticKind.None"/>, <see cref="ClipboardReadOutcome.NotRunning"/>).
/// </summary>
public enum ClipboardWriteDiagnosticKind
{
    None,

    /// <summary>Fired unconditionally, exactly once, at the very top of every real
    /// <c>ExecuteWrite</c> invocation -- i.e. once per real dequeued write request the owner
    /// thread's message loop actually processes. Carries the caller-supplied
    /// <see cref="ClipboardWriteDiagnosticEvent.ExpectedSequence"/> -- the exact same <c>uint</c>
    /// already known before any native call is made.</summary>
    WriteStarted,

    /// <summary>Fired inside <c>MutateWhileClipboardOpen</c>, immediately after the sequence-CAS
    /// gate's own <c>GetClipboardSequenceNumber()</c> call -- the FIRST of this write's three
    /// sequence reads. <see cref="ClipboardWriteDiagnosticEvent.ObservedSequence"/> is the current
    /// clipboard sequence at that moment; <see cref="ClipboardWriteDiagnosticEvent.SequenceMatched"/>
    /// is whether it equalled <see cref="ClipboardWriteDiagnosticEvent.ExpectedSequence"/> (and was
    /// non-zero) -- i.e. whether the CAS gate passed. A <see langword="false"/> here means
    /// <c>EmptyClipboard</c>/<c>SetClipboardData</c> are never reached at all for this write.</summary>
    CasSequenceObserved,

    /// <summary>Fired inside <c>MutateWhileClipboardOpen</c>, immediately after
    /// <c>TrySetClipboardData</c> succeeds -- the SECOND of this write's three sequence reads, and
    /// the exact <c>writeSequence</c> value <c>ExecuteWrite</c>/<c>VerifyWrite</c> carry forward as
    /// the write's own confirmed marker. Captured here, still inside the mutation bracket, BEFORE
    /// <c>CloseClipboard</c> is ever called (see <see cref="ClipboardWriteDiagnosticEvent"/>'s own
    /// class doc for why no separate post-Close sequence read exists).</summary>
    PostSetSequenceCaptured,

    /// <summary>Fired inside <c>VerifyWhileClipboardOpen</c>, immediately after the
    /// verification-reopen's own <c>GetClipboardSequenceNumber()</c> call -- the THIRD and final
    /// sequence read, and the identity of the clipboard's final, externally-observable state (see
    /// <see cref="Privon.Windows.ClipboardChangeMonitor"/>'s own <c>VerifyWhileClipboardOpen</c>
    /// doc for the FINAL_VERIFICATION_SEQUENCE correction). <see cref="ClipboardWriteDiagnosticEvent.ObservedSequence"/>
    /// is the current clipboard sequence at verification time; <see cref="ClipboardWriteDiagnosticEvent.SequenceMatched"/>
    /// is whether it still equalled the write's own <c>writeSequence</c>
    /// (<see cref="PostSetSequenceCaptured"/>'s own value) -- AS OF PHASE 3C STEP42, this is
    /// DIAGNOSTIC/ATTRIBUTION information only, not a gate: <see langword="false"/> here no longer
    /// by itself means the read-back comparison below is skipped, or that the write failed. A real
    /// Windows + real ChatGPT manual trace proved this exact transition (a genuinely different
    /// verification sequence, with the write's own content still intact) can and does occur without
    /// any external replacement -- see <see cref="Privon.Windows.ClipboardWriteOutcome.Superseded"/>'s
    /// own doc for the narrower condition that value is now reserved for.</summary>
    VerificationSequenceObserved,

    /// <summary>Fired inside <c>VerifyWhileClipboardOpen</c>, whenever the verification sequence is
    /// reliable (non-zero) AND <c>CF_UNICODETEXT</c> is still available -- immediately before
    /// <c>TryReadUnicodeTextBody</c> is actually called, regardless of whether the verification
    /// sequence still equals the post-Set <c>writeSequence</c> (Phase 3C STEP42: this is no longer a
    /// precondition). Presence of this event (there is no "attempted=false" variant -- absence
    /// already means not attempted) is itself the "was the exact replacement read-back comparison
    /// attempted" signal.</summary>
    ReadBackAttempted,

    /// <summary>Fired only when <c>TryReadUnicodeTextBody</c> actually produced a valid parsed
    /// string to compare -- i.e. only on the same path that can legitimately produce
    /// <see cref="Privon.Windows.ClipboardWriteOutcome.Success"/>,
    /// <see cref="Privon.Windows.ClipboardWriteOutcome.ReadBackMismatch"/>, or (as of Phase 3C
    /// STEP42) <see cref="Privon.Windows.ClipboardWriteOutcome.Superseded"/> (never fired for a
    /// format-missing or malformed-payload verification failure, which never produced a real string
    /// to compare in the first place -- mirrors <c>WRITE_READBACK_MISMATCH_SEMANTICS</c> exactly).
    /// <see cref="ClipboardWriteDiagnosticEvent.ReadBackExactMatch"/> is the ordinal comparison
    /// result -- <see langword="true"/> here is what actually determines
    /// <see cref="Privon.Windows.ClipboardWriteOutcome.Success"/> now, not sequence equality.</summary>
    ReadBackExactMatch,

    /// <summary>Fired exactly once per real <c>ExecuteWrite</c> invocation, regardless of which
    /// branch produced the final result -- the single terminal record for one guarded-write
    /// attempt, carrying the final <see cref="ClipboardWriteDiagnosticEvent.Outcome"/> and
    /// <see cref="ClipboardWriteDiagnosticEvent.ClipboardMutated"/> (the exact same values the
    /// caller's own <see cref="Privon.Windows.ClipboardWriteResult"/> carries).</summary>
    WriteCompleted,
}
