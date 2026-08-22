using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP41 -- one metadata-only observation, correlated by <see cref="AttemptId"/> (see
/// that property's own doc) across every stage of one real clipboard attempt. This type is the
/// single structural privacy boundary for the whole diagnostic capability: every property is
/// either a plain <see langword="bool"/>/<see langword="int"/>/<see langword="uint"/>/enum (or a
/// nullable of one of those), except exactly two <see langword="string"/> properties --
/// <see cref="TargetProcessName"/> (the OS-reported foreground executable's short name, sourced
/// only from <c>ForegroundTargetSnapshot.ProcessName</c>) and <see cref="ExceptionTypeName"/> (an
/// exception's own <c>GetType().Name</c> ONLY, never its <c>Message</c>). No property of this type
/// is, or can ever legitimately become, raw/protected clipboard text, a
/// <c>Privon.Detection.CanonicalValue</c>, a detected literal value, a Windows account/session
/// name, or ChatGPT conversation content -- see this project's own
/// <c>ClipboardDiagnosticPrivacyBoundaryTests</c> for the structural (reflection) and behavioral
/// (synthetic-sentinel) proof.
///
/// Only ever constructed via the static factory methods below, one per
/// <see cref="ClipboardDiagnosticStage"/> -- never via a public/internal constructor a caller could
/// use to smuggle an unexpected field's value into an unrelated stage. Each factory sets ONLY the
/// fields that stage's own real production call site already has in hand as a primitive/enum --
/// never a whole domain object (e.g. <c>ClipboardTextSnapshot</c>/<c>ClipboardPrivacyProcessingResult</c>)
/// stored as a field, even though several of those types are themselves already metadata-only --
/// this type never depends on any other type's own diagnostic hardening staying safe in the
/// future; it is entirely self-contained (matching this codebase's established
/// <c>ClipboardTextReadResult</c>/<c>ClipboardWriteResult</c> precedent).
///
/// <see cref="AttemptId"/> is deliberately the SAME value as the real production clipboard-attempt
/// <c>Generation</c> (<see cref="IClipboardNotificationLifecycle.AdvanceOnClipboardNotification"/>'s
/// own return value) -- already an opaque, monotonically-increasing, metadata-only <c>long</c>
/// minted exactly once per clipboard notification received. No second, redundant counter is
/// introduced: reusing the value that already correlates a real attempt end-to-end in production is
/// simpler and strictly more accurate than inventing a parallel diagnostic-only identifier that
/// could, in principle, drift out of sync with it.
/// </summary>
internal sealed class ClipboardDiagnosticEvent
{
    private ClipboardDiagnosticEvent()
    {
    }

    /// <summary>The real production clipboard-attempt generation this observation belongs to -- see
    /// this type's own class doc.</summary>
    public required long AttemptId { get; init; }

    public required ClipboardDiagnosticStage Stage { get; init; }

    /// <summary>Wall-clock UTC instant this event was constructed (i.e. the instant the real
    /// production call site reached this observation point) -- populated automatically, never
    /// supplied by a caller.</summary>
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    // ---- AppClipboardChangeReceived ----
    public uint? SequenceNumber { get; init; }
    public bool? HasReliableSequence { get; init; }
    public bool? HasUnicodeText { get; init; }

    // ---- MailboxWriteAttempted ----
    public bool? MailboxAccepted { get; init; }

    // ---- TargetCaptured ----
    public bool? TargetResolved { get; init; }
    public uint? TargetProcessId { get; init; }

    /// <summary>The OS-reported foreground executable's short name (e.g. "ChatGPT", "powershell",
    /// "explorer") -- exactly <c>ForegroundTargetSnapshot.ProcessName</c>, never normalized, never
    /// a window title, never a path. The only content-bearing field on this type besides
    /// <see cref="ExceptionTypeName"/>.</summary>
    public string? TargetProcessName { get; init; }

    public bool? TargetAuthorized { get; init; }

    // ---- GuardedReadCompleted / GuardedWriteCompleted ----
    public ClipboardReadOutcome? ReadOutcome { get; init; }
    public ClipboardWriteOutcome? WriteOutcome { get; init; }
    public bool? ClipboardMutated { get; init; }
    public bool? RewriteVerified { get; init; }

    // ---- PolicyEvaluated (Detection/Trust/Policy counts only -- never a detected value) ----
    public int? CandidateCount { get; init; }
    public int? TrustedCount { get; init; }
    public int? ProtectCount { get; init; }
    public int? NeedsDecisionCount { get; init; }
    public int? BypassCount { get; init; }
    public bool? HasWritePlan { get; init; }
    public bool? HasDecisionPlan { get; init; }

    // ---- DecisionSessionPublishAttempted / ComposerVerificationHandoffPublished ----
    public int? DecisionItemCount { get; init; }
    public bool? PublishAccepted { get; init; }

    // ---- AttemptTerminal ----
    public ClipboardDiagnosticTerminalReason? Terminal { get; init; }

    /// <summary>An unexpected exception's own <c>GetType().Name</c> ONLY -- e.g.
    /// "InvalidOperationException" -- never its <c>Message</c>, never a stack trace, never any
    /// interpolated content. See <c>ClipboardDiagnosticPrivacyBoundaryTests</c> for the behavioral
    /// proof that a deliberately sensitive-looking exception <c>Message</c> never reaches this
    /// field.</summary>
    public string? ExceptionTypeName { get; init; }

    public static ClipboardDiagnosticEvent AppClipboardChangeReceived(
        long attemptId, uint sequenceNumber, bool hasReliableSequence, bool hasUnicodeText) => new()
        {
            AttemptId = attemptId,
            Stage = ClipboardDiagnosticStage.AppClipboardChangeReceived,
            SequenceNumber = sequenceNumber,
            HasReliableSequence = hasReliableSequence,
            HasUnicodeText = hasUnicodeText,
        };

    public static ClipboardDiagnosticEvent MailboxWriteAttempted(long attemptId, bool accepted) => new()
    {
        AttemptId = attemptId,
        Stage = ClipboardDiagnosticStage.MailboxWriteAttempted,
        MailboxAccepted = accepted,
    };

    public static ClipboardDiagnosticEvent WorkerDequeued(long attemptId) => new()
    {
        AttemptId = attemptId,
        Stage = ClipboardDiagnosticStage.WorkerDequeued,
    };

    public static ClipboardDiagnosticEvent TargetCaptured(
        long attemptId, ForegroundTargetSnapshot target, bool authorized) => new()
        {
            AttemptId = attemptId,
            Stage = ClipboardDiagnosticStage.TargetCaptured,
            TargetResolved = target.IsResolved,
            TargetProcessId = target.IsResolved ? target.ProcessId : null,
            TargetProcessName = target.IsResolved ? target.ProcessName : null,
            TargetAuthorized = authorized,
        };

    public static ClipboardDiagnosticEvent GuardedReadCompleted(
        long attemptId, ClipboardReadOutcome outcome, uint? sequenceNumber, bool? hasReliableSequence) => new()
        {
            AttemptId = attemptId,
            Stage = ClipboardDiagnosticStage.GuardedReadCompleted,
            ReadOutcome = outcome,
            SequenceNumber = sequenceNumber,
            HasReliableSequence = hasReliableSequence,
        };

    public static ClipboardDiagnosticEvent PolicyEvaluated(
        long attemptId,
        int candidateCount,
        int trustedCount,
        int protectCount,
        int needsDecisionCount,
        int bypassCount,
        bool hasWritePlan,
        bool hasDecisionPlan) => new()
        {
            AttemptId = attemptId,
            Stage = ClipboardDiagnosticStage.PolicyEvaluated,
            CandidateCount = candidateCount,
            TrustedCount = trustedCount,
            ProtectCount = protectCount,
            NeedsDecisionCount = needsDecisionCount,
            BypassCount = bypassCount,
            HasWritePlan = hasWritePlan,
            HasDecisionPlan = hasDecisionPlan,
        };

    public static ClipboardDiagnosticEvent GuardedWriteCompleted(
        long attemptId, ClipboardWriteOutcome outcome, bool clipboardMutated, bool rewriteVerified) => new()
        {
            AttemptId = attemptId,
            Stage = ClipboardDiagnosticStage.GuardedWriteCompleted,
            WriteOutcome = outcome,
            ClipboardMutated = clipboardMutated,
            RewriteVerified = rewriteVerified,
        };

    public static ClipboardDiagnosticEvent ComposerVerificationHandoffPublished(long attemptId, bool published) => new()
    {
        AttemptId = attemptId,
        Stage = ClipboardDiagnosticStage.ComposerVerificationHandoffPublished,
        PublishAccepted = published,
    };

    public static ClipboardDiagnosticEvent DecisionSessionPublishAttempted(
        long attemptId, int decisionItemCount, bool published) => new()
        {
            AttemptId = attemptId,
            Stage = ClipboardDiagnosticStage.DecisionSessionPublishAttempted,
            DecisionItemCount = decisionItemCount,
            PublishAccepted = published,
        };

    public static ClipboardDiagnosticEvent AttemptTerminal(
        long attemptId, ClipboardDiagnosticTerminalReason reason, string? exceptionTypeName = null) => new()
        {
            AttemptId = attemptId,
            Stage = ClipboardDiagnosticStage.AttemptTerminal,
            Terminal = reason,
            ExceptionTypeName = exceptionTypeName,
        };

    /// <summary>
    /// Self-contained, metadata-only rendering -- reads only this type's own already-primitive
    /// fields, never delegates to any nested type's <c>ToString()</c> (there are no nested domain
    /// types stored on this instance to delegate to in the first place). One line per event, pipe-
    /// delimited, only the fields that stage actually populated (every unset nullable field is
    /// simply omitted, never rendered as a misleading "null").
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string>
        {
            $"ts={CapturedAtUtc:O}",
            $"attempt={AttemptId}",
            $"stage={Stage}",
        };

        void Add(string name, object? value)
        {
            if (value is not null) parts.Add($"{name}={value}");
        }

        Add("sequenceNumber", SequenceNumber);
        Add("hasReliableSequence", HasReliableSequence);
        Add("hasUnicodeText", HasUnicodeText);
        Add("mailboxAccepted", MailboxAccepted);
        Add("targetResolved", TargetResolved);
        Add("targetProcessId", TargetProcessId);
        Add("targetProcessName", TargetProcessName);
        Add("targetAuthorized", TargetAuthorized);
        Add("readOutcome", ReadOutcome);
        Add("writeOutcome", WriteOutcome);
        Add("clipboardMutated", ClipboardMutated);
        Add("rewriteVerified", RewriteVerified);
        Add("candidateCount", CandidateCount);
        Add("trustedCount", TrustedCount);
        Add("protectCount", ProtectCount);
        Add("needsDecisionCount", NeedsDecisionCount);
        Add("bypassCount", BypassCount);
        Add("hasWritePlan", HasWritePlan);
        Add("hasDecisionPlan", HasDecisionPlan);
        Add("decisionItemCount", DecisionItemCount);
        Add("publishAccepted", PublishAccepted);
        Add("terminal", Terminal);
        Add("exceptionType", ExceptionTypeName);

        return string.Join('|', parts);
    }
}
