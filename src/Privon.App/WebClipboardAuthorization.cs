using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F5A.2 (frozen contract) / Gate 031F5C (this type) -- the narrow result a
/// future Phase-E <see cref="IWebClipboardAuthorizationSource"/> implementation hands back to
/// <see cref="ClipboardAuthorizationRouter"/>. Exactly two facts: the matched
/// <see cref="SupportedWebTarget"/> and the attempt-scoped, already-bound
/// <see cref="IClipboardAuthorizationFreshness"/> verifier. Nothing else -- no proof, no origin, no
/// evidence, no decision context, no nonce, no Native Messaging state of any kind.
///
/// The constructor validates both fields so a malformed instance (an undefined/zero target, or a
/// null verifier) can never be constructed at all, closing the exact defect class Gate 031F5A.2
/// section 8 requires: "no malformed source output may become an authorized Web path."
/// </summary>
internal sealed class WebClipboardAuthorization
{
    public SupportedWebTarget Target { get; }
    public IClipboardAuthorizationFreshness Freshness { get; }

    public WebClipboardAuthorization(SupportedWebTarget target, IClipboardAuthorizationFreshness freshness)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target), target, "SupportedWebTarget must be a defined, nonzero value.");
        ArgumentNullException.ThrowIfNull(freshness);

        Target = target;
        Freshness = freshness;
    }
}
