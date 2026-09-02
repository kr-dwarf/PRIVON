using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F5A/031F5A.1/031F5A.2 (frozen contract) / Gate 031F5C (this type) -- the
/// single, shared authorization decision point both <see cref="ClipboardPrivacyCoordinator"/> and
/// <see cref="ClipboardDecisionActionResolver"/> call at every fresh authorization moment. Neither
/// caller calls <see cref="TargetGate"/> or an <see cref="IWebClipboardAuthorizationSource"/>
/// directly -- both go through here, so the Windows-first ordering and the malformed-result
/// fail-closed validation exist in exactly one place.
///
/// WINDOWS_FIRST (Gate 031F5A section 9, frozen): <see cref="TargetGate.Match"/> is evaluated
/// FIRST and SYNCHRONOUSLY. On a match, this method returns without ever awaiting or otherwise
/// touching <paramref name="webSource"/> -- a valid Windows target never pays Web
/// authorization/challenge latency, and an unsupported desktop process can never be re-routed
/// through the Web branch by process-name confusion.
///
/// PRODUCTION_DEFAULT (Gate 031F5A.2 section 8, frozen): <paramref name="webSource"/> is
/// <see langword="null"/> in every real 0.3.1 launch before Phase E ships a concrete
/// <see cref="IWebClipboardAuthorizationSource"/> implementation -- <see cref="PrivonAppComposition"/>
/// passes nothing. A null source is therefore not an edge case but the actual production shape:
/// every non-Windows target fails closed to <see cref="ClipboardAuthorization.Outside"/>, truthfully,
/// with no "always trusted" production fake ever introduced.
///
/// MALFORMED_RESULT_FAIL_CLOSED (Gate 031F5A.2 section 8): even though
/// <see cref="WebClipboardAuthorization"/>'s own constructor already rejects an undefined/zero
/// target or a null freshness, this method re-validates defensively before ever calling the
/// throwing <see cref="ClipboardAuthorization.ForWeb"/> factory -- a buggy source implementation can
/// therefore never surface as an unhandled exception here; it degrades to Outside instead.
///
/// EXCEPTION_TRANSPARENCY (Gate 031F5A.2 section 12/028, frozen): this method deliberately does
/// NOT catch an exception thrown by (or faulting the <see cref="Task"/> returned from)
/// <paramref name="webSource"/>. Only a null RESULT is an ordinary decline; an exception is always
/// an unexpected defect and must propagate to each caller's own existing exception-handling
/// convention -- turning it into a normal Outside decline here would mask real defects.
///
/// FACTS_ONLY: this type calls no <c>WebTargetGate</c>, and knows no
/// <c>WebChallengeProof</c>/<c>WebForegroundEvidence</c>/<c>WebDecisionContext</c>/
/// <c>WebChannelRegistry</c>/origin/Native Messaging concept of any kind -- those all remain behind
/// the <see cref="IWebClipboardAuthorizationSource"/> boundary, owned exclusively by its future
/// Phase-E implementation.
/// </summary>
internal static class ClipboardAuthorizationRouter
{
    public static async Task<ClipboardAuthorization> AuthorizeAsync(
        ForegroundTargetSnapshot foreground, IWebClipboardAuthorizationSource? webSource)
    {
        if (TargetGate.Match(foreground) is { } windowsTarget)
            return ClipboardAuthorization.ForWindows(windowsTarget);

        if (webSource is null)
            return ClipboardAuthorization.Outside;

        WebClipboardAuthorization? result = await webSource.AuthorizeAsync(foreground).ConfigureAwait(false);
        if (result is null)
            return ClipboardAuthorization.Outside;

        if (!Enum.IsDefined(result.Target) || result.Freshness is null)
            return ClipboardAuthorization.Outside;

        return ClipboardAuthorization.ForWeb(result.Target, result.Freshness);
    }
}
