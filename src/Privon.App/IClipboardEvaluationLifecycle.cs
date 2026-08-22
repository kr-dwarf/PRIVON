namespace Privon.App;

/// <summary>
/// Phase 0.2D (STEP61) -- the narrow, shared-coordinator-facing claim/report surface for
/// per-generation privacy evaluation, widening <see cref="IClipboardGenerationSnapshot"/> with the
/// three claim/report operations <see cref="ClipboardDecisionScopeLifecycle"/> already implements
/// (Phase 0.2C, STEP58/STEP59) but that had no real consumer until this STEP. Exists so
/// <see cref="ClipboardPrivacyCoordinator"/> -- via <see cref="IClipboardNotificationLifecycle"/>,
/// which now extends this interface -- can prevent two independent triggers (a clipboard-content
/// change and a foreground-focus change) from evaluating the SAME clipboard generation twice, using
/// only a plain counter compare-and-claim, without ever needing the wider Runtime-Decision-scope-
/// facing surface (<c>IClipboardDecisionScopeLifecycle</c>'s own <c>TryPublish</c>/<c>Reset</c>/
/// <c>GetActiveScope</c>/<c>IsActive</c>) -- the same narrow-seam-over-a-richer-concrete-type
/// pattern this project uses throughout (<see cref="IClipboardGenerationSnapshot"/> itself is the
/// direct STEP30.1/STEP32 precedent this interface extends as a fourth narrow view on the SAME
/// concrete <see cref="ClipboardDecisionScopeLifecycle"/> object).
/// </summary>
internal interface IClipboardEvaluationLifecycle : IClipboardGenerationSnapshot
{
    /// <summary>See <see cref="ClipboardDecisionScopeLifecycle.TryBeginEvaluation"/>.</summary>
    bool TryBeginEvaluation(long expectedGeneration);

    /// <summary>See <see cref="ClipboardDecisionScopeLifecycle.CompleteEvaluation"/>.</summary>
    void CompleteEvaluation(long claimedGeneration);

    /// <summary>See <see cref="ClipboardDecisionScopeLifecycle.AbandonEvaluation"/>.</summary>
    void AbandonEvaluation(long claimedGeneration);
}
