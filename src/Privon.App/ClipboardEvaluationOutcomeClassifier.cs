using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 0.2D (STEP61) -- a pure static classifier mapping each real, already-existing
/// <see cref="ClipboardReadOutcome"/>/<see cref="ClipboardWriteOutcome"/> value to a
/// <see cref="ClipboardEvaluationDisposition"/>. No dependency on <c>IClipboardDiagnosticRecorder</c>,
/// coordinator state, generation state, a lock, I/O, a foreground type, or the diagnostic
/// terminal-reason enum -- this is a plain, independently testable function over an already-frozen
/// enum, exactly matching this codebase's established preference for a small pure classifier with
/// its own direct unit-test surface (see <c>ClipboardWriteResultClassifier</c>'s own doc for the
/// identical rationale).
///
/// <see cref="ClipboardReadOutcome.Success"/>/<see cref="ClipboardWriteOutcome.Success"/> are never
/// valid inputs here -- a caller only ever reaches this classifier once it already knows the
/// attempt did NOT succeed (the success path has its own, entirely separate, Complete-on-success
/// handling in <c>ClipboardPrivacyCoordinator</c>). Passing either in is a caller contract
/// violation, not a value this type has any sensible disposition for, so it throws rather than
/// silently picking a disposition.
///
/// Every OTHER currently-defined member of both enums is handled EXPLICITLY, one at a time
/// (matching this codebase's established "explicit case, no reflection/enum.GetValues cleverness"
/// minimalism -- see e.g. <c>Privon.Detection.PiiTypeIdCodec</c>) -- an undefined future value
/// (neither a currently-known member nor <c>Success</c>) falls through to a final
/// <see cref="ArgumentOutOfRangeException"/>, never a silently-assumed default disposition.
/// </summary>
internal static class ClipboardEvaluationOutcomeClassifier
{
    /// <summary>
    /// READ_OUTCOME_CLASSIFICATION (frozen): <see cref="ClipboardReadOutcome.FormatUnavailable"/>/
    /// <see cref="ClipboardReadOutcome.MalformedData"/> are <see cref="ClipboardEvaluationDisposition.Terminal"/>
    /// -- retrying against the SAME clipboard generation cannot change either fact. Every other
    /// non-Success outcome (<see cref="ClipboardReadOutcome.NotRunning"/>/
    /// <see cref="ClipboardReadOutcome.Busy"/>/<see cref="ClipboardReadOutcome.NativeFailure"/>/
    /// <see cref="ClipboardReadOutcome.InvalidExpectedTarget"/>/<see cref="ClipboardReadOutcome.TargetUnavailable"/>/
    /// <see cref="ClipboardReadOutcome.TargetChanged"/>) is <see cref="ClipboardEvaluationDisposition.Retryable"/>.
    /// </summary>
    public static ClipboardEvaluationDisposition ClassifyRead(ClipboardReadOutcome outcome) => outcome switch
    {
        ClipboardReadOutcome.NotRunning => ClipboardEvaluationDisposition.Retryable,
        ClipboardReadOutcome.Busy => ClipboardEvaluationDisposition.Retryable,
        ClipboardReadOutcome.FormatUnavailable => ClipboardEvaluationDisposition.Terminal,
        ClipboardReadOutcome.NativeFailure => ClipboardEvaluationDisposition.Retryable,
        ClipboardReadOutcome.MalformedData => ClipboardEvaluationDisposition.Terminal,
        ClipboardReadOutcome.InvalidExpectedTarget => ClipboardEvaluationDisposition.Retryable,
        ClipboardReadOutcome.TargetUnavailable => ClipboardEvaluationDisposition.Retryable,
        ClipboardReadOutcome.TargetChanged => ClipboardEvaluationDisposition.Retryable,
        ClipboardReadOutcome.Success => throw new ArgumentException(
            $"{nameof(ClipboardReadOutcome)}.{nameof(ClipboardReadOutcome.Success)} is not a valid input to " +
            $"{nameof(ClassifyRead)} -- a successful read has no retryable/terminal disposition of its own.",
            nameof(outcome)),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, $"Undefined {nameof(ClipboardReadOutcome)} value."),
    };

    /// <summary>
    /// WRITE_OUTCOME_CLASSIFICATION (frozen): <see cref="ClipboardWriteOutcome.InvalidText"/> is the
    /// only <see cref="ClipboardEvaluationDisposition.Terminal"/> write outcome -- the replacement
    /// text itself (embedded NUL, overflow) can never become valid by retrying. Every other
    /// non-Success outcome (<see cref="ClipboardWriteOutcome.NotRunning"/>/
    /// <see cref="ClipboardWriteOutcome.InvalidExpectedSequence"/>/<see cref="ClipboardWriteOutcome.Busy"/>/
    /// <see cref="ClipboardWriteOutcome.SequenceChanged"/>/<see cref="ClipboardWriteOutcome.NativeFailure"/>/
    /// <see cref="ClipboardWriteOutcome.VerificationUnavailable"/>/<see cref="ClipboardWriteOutcome.Superseded"/>/
    /// <see cref="ClipboardWriteOutcome.ReadBackMismatch"/>/<see cref="ClipboardWriteOutcome.InvalidExpectedTarget"/>/
    /// <see cref="ClipboardWriteOutcome.TargetUnavailable"/>/<see cref="ClipboardWriteOutcome.TargetChanged"/>)
    /// is <see cref="ClipboardEvaluationDisposition.Retryable"/>.
    /// </summary>
    public static ClipboardEvaluationDisposition ClassifyWrite(ClipboardWriteOutcome outcome) => outcome switch
    {
        ClipboardWriteOutcome.NotRunning => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.InvalidExpectedSequence => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.InvalidText => ClipboardEvaluationDisposition.Terminal,
        ClipboardWriteOutcome.Busy => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.SequenceChanged => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.NativeFailure => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.VerificationUnavailable => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.Superseded => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.ReadBackMismatch => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.InvalidExpectedTarget => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.TargetUnavailable => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.TargetChanged => ClipboardEvaluationDisposition.Retryable,
        ClipboardWriteOutcome.Success => throw new ArgumentException(
            $"{nameof(ClipboardWriteOutcome)}.{nameof(ClipboardWriteOutcome.Success)} is not a valid input to " +
            $"{nameof(ClassifyWrite)} -- a successful write has no retryable/terminal disposition of its own.",
            nameof(outcome)),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, $"Undefined {nameof(ClipboardWriteOutcome)} value."),
    };
}
