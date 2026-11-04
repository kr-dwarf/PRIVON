using Privon.Core;

namespace Privon.App;

/// <summary>
/// PRIVON 0.4.0 -- Level-1 one-revision pass (frozen D3A rows POL-L1-ONCE-R1 / POL-L1-ONCE-R2). The trust of one evaluated candidate:
/// <see cref="OneRevisionPassExact"/> only when a live pass bound to exactly {revision, detectorId, canonical} was consumed for it in THIS
/// evaluation; every other candidate -- including a replay of an already-consumed pass -- is <see cref="Untrusted"/>. Declared with
/// <see cref="Untrusted"/> first so that <c>default</c> is never a pass.
/// </summary>
internal enum ClipboardOneRevisionPassTrust
{
    Untrusted = 0,
    OneRevisionPassExact,
}

/// <summary>
/// The per-candidate outcome of <see cref="ClipboardPrivacyProcessor.EvaluateOneRevisionPass"/>. <see cref="AllowOriginalOnce"/> means the
/// original text may stay as typed FOR THIS EVALUATION ONLY (the pass was consumed; nothing is rewritten for it); it never means Verified,
/// never lowers a Protect/Level-2/Level-3 decision, and is not a claim that anything was blocked or made safe to send (A-1).
/// </summary>
internal enum ClipboardOneRevisionPassDisposition
{
    Protect,
    Bypass,
    NeedsDecision,
    AllowOriginalOnce,
}

/// <summary>One distinct decision identity of the evaluated text and what the evaluation concluded for it.</summary>
/// <param name="Item">The same identity the decision plan uses: canonical, risk level and the detector that produced it.</param>
/// <param name="Disposition">The outcome for this candidate.</param>
/// <param name="Trust"><see cref="ClipboardOneRevisionPassTrust.OneRevisionPassExact"/> only for <see cref="ClipboardOneRevisionPassDisposition.AllowOriginalOnce"/>.</param>
/// <param name="PassRemainsAfter">Whether a pass for exactly this {revision, detectorId, canonical} is still live after the evaluation
/// (always <see langword="false"/> after a consumed pass; <see langword="false"/> when none ever existed).</param>
internal readonly record struct ClipboardOneRevisionPassCandidate(
    ClipboardDecisionItem Item,
    ClipboardOneRevisionPassDisposition Disposition,
    ClipboardOneRevisionPassTrust Trust,
    bool PassRemainsAfter)
{
    // Self-contained and content-free, like ClipboardDecisionItem: safe metadata only, never the canonical value.
    public override string ToString() =>
        $"{nameof(ClipboardOneRevisionPassCandidate)} {{ PiiType = {Item.Canonical.PiiType}, {nameof(Disposition)} = {Disposition}, {nameof(Trust)} = {Trust} }}";
}

/// <summary>The result of one evaluation of the current text against a scope's live one-revision passes.</summary>
internal sealed class ClipboardOneRevisionPassEvaluation
{
    public ClipboardOneRevisionPassEvaluation(IReadOnlyList<ClipboardOneRevisionPassCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Candidates = candidates.ToArray();
    }

    public IReadOnlyList<ClipboardOneRevisionPassCandidate> Candidates { get; }

    /// <summary>At least one candidate was allowed as typed by a pass consumed in this evaluation.</summary>
    public bool AnyPassed => Candidates.Any(c => c.Disposition == ClipboardOneRevisionPassDisposition.AllowOriginalOnce);

    /// <summary>At least one candidate still has to be protected: a pass never covers it.</summary>
    public bool HasProtect => Candidates.Any(c => c.Disposition == ClipboardOneRevisionPassDisposition.Protect);

    /// <summary>At least one candidate still waits for a user decision.</summary>
    public bool HasUnresolved => Candidates.Any(c => c.Disposition == ClipboardOneRevisionPassDisposition.NeedsDecision);

    public override string ToString() =>
        $"{nameof(ClipboardOneRevisionPassEvaluation)} {{ CandidateCount = {Candidates.Count}, AnyPassed = {AnyPassed} }}";
}

internal enum ClipboardOneRevisionPassOutcome
{
    /// <summary>Any unexpected failure: never a pass, never logged with content.</summary>
    Failed,

    /// <summary>The scope is no longer the active one, is not committed, or the target/read/revision no longer applies: no pass.</summary>
    Stale,

    /// <summary>The evaluation ran; see <see cref="ClipboardOneRevisionPassResult.Evaluation"/>.</summary>
    Evaluated,
}

internal readonly record struct ClipboardOneRevisionPassResult(ClipboardOneRevisionPassOutcome Outcome, ClipboardOneRevisionPassEvaluation? Evaluation)
{
    public static ClipboardOneRevisionPassResult Failed() => new(ClipboardOneRevisionPassOutcome.Failed, null);

    public static ClipboardOneRevisionPassResult Stale() => new(ClipboardOneRevisionPassOutcome.Stale, null);

    public static ClipboardOneRevisionPassResult Evaluated(ClipboardOneRevisionPassEvaluation evaluation) =>
        new(ClipboardOneRevisionPassOutcome.Evaluated, evaluation);
}
