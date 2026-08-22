using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP31/31.1 -- the narrow, write-success-facing seam
/// <see cref="ClipboardPrivacyCoordinator"/> (and, in a future dedicated STEP,
/// <c>ClipboardDecisionActionResolver</c>'s own final verified-write path --
/// RESOLVER_VERIFICATION_HANDOFF_TIMING, still open) uses to hand off exactly one verified
/// protected clipboard write's identity to <see cref="ClipboardComposerVerifier"/>. The caller
/// supplies the exact (target, replacement text, generation) triple that already
/// authorized/described its own attempt -- never re-derived here.
/// </summary>
internal interface IClipboardComposerVerificationHandoff
{
    /// <summary>
    /// PUBLISH (Phase 3C STEP31.1, frozen three-phase sequence -- PRECHECK, INSTALL,
    /// POST_INSTALL_VALIDATION): returns <see langword="true"/> only if the resulting pending
    /// record was installed AND was still generation-current at the post-install validation
    /// instant -- this is NOT a durability guarantee beyond that instant (an
    /// immediately-following clipboard notification can still invalidate it through the ordinary
    /// <see cref="IClipboardComposerVerificationInvalidation.InvalidatePending"/> path). Never
    /// retried, never logs <paramref name="expectedProtectedText"/> or any other argument.
    /// </summary>
    bool Publish(ForegroundTargetSnapshot expectedTarget, string expectedProtectedText, long expectedGeneration);
}
