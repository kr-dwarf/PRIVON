using Privon.Windows;

namespace Privon.App;

/// <summary>
/// BUG-002 Gate 2A/2C -- a pure, additive static classifier narrowing an ALREADY-Retryable
/// <see cref="ClipboardReadOutcome"/>/<see cref="ClipboardWriteOutcome"/> (per the FROZEN
/// <see cref="ClipboardEvaluationOutcomeClassifier"/>, which this type never modifies, calls, or
/// replaces) into whether <see cref="ClipboardPrivacyCoordinator"/>'s own bounded autonomous-retry
/// loop should attempt it again, or instead wait for a real external clipboard/foreground trigger.
/// No dependency on coordinator state, generation state, a lock, I/O, or a foreground type --
/// exactly matching <see cref="ClipboardEvaluationOutcomeClassifier"/>'s own established
/// small-pure-classifier shape.
///
/// AUTONOMOUS_RETRY_ALLOWLIST (Gate 2A final contract, frozen for this STEP): eligible ONLY for
/// outcomes with no positive evidence that a NEWER or CONFIRMED-DIFFERENT truth already exists --
/// <see cref="ClipboardReadOutcome.Busy"/>/<see cref="ClipboardWriteOutcome.Busy"/> (transient OS
/// contention), <see cref="ClipboardReadOutcome.NativeFailure"/>/<see cref="ClipboardWriteOutcome.NativeFailure"/>
/// (transient native failure), <see cref="ClipboardReadOutcome.TargetUnavailable"/>/
/// <see cref="ClipboardWriteOutcome.TargetUnavailable"/> (own doc: "could not be mechanically
/// resolved ... a real, expected race" -- a resolution glitch, not a confirmed departure -- gated in
/// practice by the mandatory fresh <c>TargetGate</c> recheck the retry controller performs before
/// every attempt), and <see cref="ClipboardWriteOutcome.VerificationUnavailable"/> (mutation may have
/// happened but could not be confirmed -- genuinely ambiguous, carries no newer-truth evidence of its
/// own).
///
/// EXCLUDED_BY_DESIGN, every one of them still handled exactly as before by the frozen classifier
/// (Retryable/Abandon, unchanged) -- this type only additionally says "and don't autonomously retry
/// it": <see cref="ClipboardWriteOutcome.SequenceChanged"/>/<see cref="ClipboardWriteOutcome.Superseded"/>/
/// <see cref="ClipboardWriteOutcome.ReadBackMismatch"/> (each own doc is positive evidence a
/// DIFFERENT/NEWER actor already changed the clipboard -- that newer generation's own real trigger
/// already drives a correct fresh attempt), <see cref="ClipboardReadOutcome.TargetChanged"/>/
/// <see cref="ClipboardWriteOutcome.TargetChanged"/> (own doc: a CONFIRMED departure, not a glitch --
/// directly required by the ABSOLUTE INVARIANT "target departure invalidates authorization"),
/// <see cref="ClipboardReadOutcome.NotRunning"/>/<see cref="ClipboardWriteOutcome.NotRunning"/> (the
/// transport is stopped -- retrying inside the same worker cycle achieves nothing), and every other
/// currently-defined member (<see cref="ClipboardReadOutcome.FormatUnavailable"/>/
/// <see cref="ClipboardReadOutcome.MalformedData"/>/<see cref="ClipboardReadOutcome.InvalidExpectedTarget"/>/
/// <see cref="ClipboardWriteOutcome.InvalidExpectedSequence"/>/<see cref="ClipboardWriteOutcome.InvalidText"/>/
/// <see cref="ClipboardWriteOutcome.InvalidExpectedTarget"/>).
///
/// UNKNOWN_DEFAULTS_NON_AUTONOMOUS (frozen, matching this codebase's established "explicit case, no
/// reflection/enum.GetValues cleverness" minimalism -- see e.g. <c>Privon.Detection.PiiTypeIdCodec</c>):
/// unlike <see cref="ClipboardEvaluationOutcomeClassifier"/>, which throws on an undefined value
/// (Terminal/Retryable is a hard classification every value must have), this type's own two switch
/// expressions instead default an unrecognized value to <see langword="false"/> (non-autonomous) --
/// a NEW future enum member this type has not yet been taught about must never silently become
/// autonomous-retry-eligible; it fails closed, and the frozen classifier's own Terminal/Retryable
/// call still runs unaffected. <see cref="ClipboardReadOutcome.Success"/>/<see cref="ClipboardWriteOutcome.Success"/>
/// are likewise never valid inputs in production (the caller only ever reaches this classifier from
/// the SAME non-Success branches that already call the frozen classifier) -- this type simply
/// classifies them <see langword="false"/> rather than throwing, since unlike
/// <see cref="ClipboardEvaluationOutcomeClassifier"/> this type is never the caller's ONLY source of
/// truth for that outcome.
/// </summary>
internal static class ClipboardAutonomousRetryClassifier
{
    public static bool IsAutonomousRetryEligible(ClipboardReadOutcome outcome) => outcome switch
    {
        ClipboardReadOutcome.Busy => true,
        ClipboardReadOutcome.NativeFailure => true,
        ClipboardReadOutcome.TargetUnavailable => true,
        _ => false,
    };

    public static bool IsAutonomousRetryEligible(ClipboardWriteOutcome outcome) => outcome switch
    {
        ClipboardWriteOutcome.Busy => true,
        ClipboardWriteOutcome.NativeFailure => true,
        ClipboardWriteOutcome.TargetUnavailable => true,
        ClipboardWriteOutcome.VerificationUnavailable => true,
        _ => false,
    };
}
