namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031C/031E1 -- the mechanical, product-policy-free tri-state result of
/// resolving the exact top-level origin of a browser's active tab. Deliberately NOT a nullable
/// string or an empty string -- either would collapse two structurally different facts ("origin
/// resolution was attempted and definitively found this page unsupported" vs. "origin resolution
/// itself failed or was inconclusive") into one ambiguous value. This type carries NO opinion
/// about WHICH origin is supported -- it only ever answers "could the active tab's origin be
/// established, and if so, is it one of ours to even consider."
///
/// Declared with <see cref="Unresolved"/> first, matching this codebase's established "safe value
/// first" discipline (<c>PackageIdentityResolution.Unresolved</c>): a caller that forgets to check
/// this value, or receives a default-initialized <see cref="WebForegroundEvidence"/>, never
/// mistakes silence for either "confirmed unsupported" or "confirmed a specific origin".
/// </summary>
public enum OriginResolution
{
    /// <summary>Origin resolution did not produce a definitive result. Never treated as equivalent
    /// to <see cref="Unsupported"/>.</summary>
    Unresolved,

    /// <summary>Resolution succeeded and definitively established that the active tab's origin is
    /// NOT one of the pre-filtered candidate origins -- an ordinary, expected, successful fact.
    /// <see cref="WebForegroundEvidence.Origin"/> is never populated for this state (Gate 031C
    /// PRIVACY: unsupported sites cross the boundary as this literal token, never as their actual
    /// URL).</summary>
    Unsupported,

    /// <summary>Resolution succeeded and definitively established the active tab's exact top-level
    /// origin as one of the pre-filtered candidates -- see
    /// <see cref="WebForegroundEvidence.Origin"/> for that value. Carries no opinion about WHICH
    /// origin is actually authorized; that comparison is exclusively
    /// <c>Privon.App.WebTargetGate</c>'s job.</summary>
    Resolved,
}
