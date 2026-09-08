using Privon.Browser;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031C (frozen contract) / Gate 031E1 (this type) -- the ONLY place in this
/// codebase that owns Web product/browser authorization policy: the five-origin exact allowlist,
/// the Chrome and Edge browser-identity rules, and the eight-term WEB_AUTHORIZED conjunction.
/// Deliberately ADDITIVE and DISJOINT from <see cref="TargetGate"/> (Gate 031C section
/// 13/20/WINDOWS_FROZEN_BOUNDARY) -- this type never modifies, calls into, or reuses any of
/// <see cref="TargetGate"/>'s own constants or methods, and a Windows-target
/// <see cref="ForegroundTargetSnapshot"/> can never satisfy this type's rules (Chrome/Edge process
/// names never equal "ChatGPT"/"claude"), exactly as a browser's snapshot can never satisfy
/// <see cref="TargetGate"/>'s rules today or after this change.
///
/// NO_FAKE_TRANSPORT (Gate 031C section 11 / Gate 031E1 scope, unchanged by Gate 031F3): this type
/// implements NO Native Messaging channel, extension, protocol parser, or decision-time
/// challenge/response mechanism. Terms C ("live channel bound to browser process"), F ("current
/// revision"), G ("current foreground epoch"), and H ("bracketed challenge succeeded") are
/// evaluated purely by comparing <see cref="WebForegroundEvidence"/>'s own fields against an
/// externally-supplied <see cref="WebDecisionContext"/> (and, for term H, its bound
/// <see cref="WebChallengeProof"/>) -- this type never manufactures any of those facts itself; see
/// <see cref="WebDecisionContext"/>'s own NO_FAKE_TRANSPORT doc.
///
/// THREE_WAY_AGREEMENT (Gate 031F3 section 2/7, replacing Gate 031E1's bare
/// <c>ChallengeConfirmed</c> bool): term H requires a non-null <see cref="WebChallengeProof"/>
/// that agrees, field-for-field, with BOTH <paramref name="evidence"/> and the live
/// <paramref name="context"/>/<paramref name="foreground"/> facts on all four of its own fields --
/// ChannelId, EvidenceRevision, ForegroundEpoch, and BrowserProcessId (the last checked against
/// <paramref name="foreground"/>.ProcessId, never a separate context-owned PID field -- Gate 031F3
/// section 3: the current PID authority remains exclusively <c>ForegroundTargetSnapshot.ProcessId</c>).
/// A mismatch in ANY one field is OUTSIDE; this method never "repairs" or restamps a proof.
///
/// ZERO_IDENTIFIER_FAIL_CLOSED (Gate 031F3 section 5, closing the confirmed R8 defect): a real
/// equality check alone lets two zero values coincidentally "match". BrowserProcessId, ChannelId,
/// and ForegroundEpoch are therefore each independently checked non-zero -- on
/// <paramref name="foreground"/>, <paramref name="evidence"/>, <paramref name="context"/>, AND the
/// bound proof -- structurally, before any equality comparison involving them is ever trusted.
/// <see cref="Privon.Core.RevisionId"/> is deliberately NOT given an equivalent zero-invalidity
/// rule (Gate 031F3 section 6): <c>RevisionId.None</c> (0) remains the ordinary, safe
/// "nothing observed/asserted yet" sentinel this codebase already uses elsewhere -- only the exact
/// three-way revision EQUALITY already required above applies to it.
///
/// EXACT_ORIGIN_POLICY (Gate 031C section 1/7, frozen): <see cref="StringComparison.Ordinal"/>
/// equality against exactly five serialized origins -- never <c>Contains</c>/<c>EndsWith</c>/
/// <c>StartsWith</c>, never a wildcard, never arbitrary subdomain acceptance, never path/query/
/// fragment parsing (this type never even sees a path -- <see cref="WebForegroundEvidence.Origin"/>
/// structurally cannot express one), and never scheme fallback. A redirect-only host (e.g.
/// chat.openai.com, bard.google.com) or a broad host that also serves unrelated content (e.g.
/// x.com) is simply not one of the five constants and can never match by construction.
///
/// SUPPORTED_BROWSER_POLICY (Gate 031C section 2/8/9, frozen): mirrors
/// <see cref="TargetGate"/>'s own Claude rule shape exactly -- process-name pre-filter
/// (necessary, INSUFFICIENT) plus <see cref="PackageIdentityResolution.NoPackage"/> plus
/// <see cref="ExecutableSignatureResolution.Trusted"/> plus an exact-Ordinal
/// <see cref="ForegroundTargetSnapshot.SignerOrganization"/>. No path, version, hash, thumbprint,
/// issuer, serial, AUMID, or window title is or can ever be consulted -- none of those exist on
/// <see cref="ForegroundTargetSnapshot"/>. A packaged Edge deployment
/// (<see cref="PackageIdentityResolution.Resolved"/>) is deliberately, exhaustively UNSUPPORTED --
/// there is no fallback path (Gate 031C section 2 Edge caveat).
/// </summary>
internal static class WebTargetGate
{
    private const string ChatGptOrigin = "https://chatgpt.com";
    private const string ClaudeOrigin = "https://claude.ai";
    private const string GeminiOrigin = "https://gemini.google.com";
    private const string GrokOrigin = "https://grok.com";
    private const string DeepSeekOrigin = "https://chat.deepseek.com";

    /// <summary>
    /// The single WEB_AUTHORIZED operation (Gate 031C's eight-term formula, A-H). Returns the
    /// exact matched <see cref="SupportedWebTarget"/>, or <see langword="null"/> for "OUTSIDE" --
    /// never a sentinel value (see <see cref="SupportedWebTarget"/>'s own doc). Every term is
    /// evaluated; the first unmet term short-circuits to <see langword="null"/> with no further
    /// evaluation, but ALL terms are still genuinely required -- there is no term whose failure is
    /// tolerated because another term happened to succeed.
    ///
    /// TERM_MAPPING (Gate 031C formula -> this method):
    ///   A (native foreground resolved)      -> <paramref name="foreground"/>.IsResolved
    ///   B (supported-browser rule)          -> <see cref="IsSupportedBrowser"/>
    ///   C (live channel bound to browser)   -> ChannelId equality against
    ///                                          <paramref name="context"/>.CurrentChannelId
    ///   PID binding (section 12, part of B/C together)
    ///                                        -> ProcessId equality against
    ///                                          <paramref name="evidence"/>.BrowserProcessId
    ///   D (BrowserFocus == Focused)         -> direct enum comparison
    ///   E (supported exact origin)          -> <see cref="MatchOrigin"/>
    ///   F (current revision)                -> EvidenceRevision equality against
    ///                                          <paramref name="context"/>.CurrentRevision
    ///   G (current foreground epoch)        -> ForegroundEpoch equality against
    ///                                          <paramref name="context"/>.CurrentForegroundEpoch
    ///   H (bracketed challenge succeeded)   -> THREE_WAY_AGREEMENT against
    ///                                          <paramref name="context"/>.ChallengeProof (see
    ///                                          this type's own doc)
    /// </summary>
    public static SupportedWebTarget? Match(
        ForegroundTargetSnapshot foreground,
        WebForegroundEvidence evidence,
        WebDecisionContext context)
    {
        if (!foreground.IsResolved)
            return null;

        if (!IsSupportedBrowser(foreground))
            return null;

        // ZERO_IDENTIFIER_FAIL_CLOSED (Gate 031F3 section 5) -- checked before any equality
        // comparison involving these three identifiers is ever trusted, so a zero-equals-zero
        // coincidence can never authorize.
        if (foreground.ProcessId == 0) return null;
        if (evidence.BrowserProcessId == 0) return null;
        if (evidence.ChannelId == 0) return null;
        if (context.CurrentChannelId == 0) return null;
        if (evidence.ForegroundEpoch == 0) return null;
        if (context.CurrentForegroundEpoch == 0) return null;

        if (foreground.ProcessId != evidence.BrowserProcessId)
            return null;

        if (evidence.ChannelId != context.CurrentChannelId)
            return null;

        if (evidence.BrowserFocus != BrowserFocus.Focused)
            return null;

        if (evidence.OriginResolution != OriginResolution.Resolved)
            return null;

        if (evidence.Origin is null)
            return null;

        if (evidence.EvidenceRevision != context.CurrentRevision)
            return null;

        if (evidence.ForegroundEpoch != context.CurrentForegroundEpoch)
            return null;

        // THREE_WAY_AGREEMENT (Gate 031F3 section 2/7) -- term H. A null proof is always OUTSIDE;
        // never a fallback to "assume valid".
        if (context.ChallengeProof is not { } proof)
            return null;

        // The proof's own identifiers are independently non-zero -- structural, not merely implied
        // by the equality chain below (Gate 031F3 section 5: "the safety must be structural").
        if (proof.ChannelId == 0) return null;
        if (proof.ForegroundEpoch == 0) return null;
        if (proof.BrowserProcessId == 0) return null;

        if (proof.ChannelId != evidence.ChannelId || proof.ChannelId != context.CurrentChannelId)
            return null;

        if (proof.EvidenceRevision != evidence.EvidenceRevision || proof.EvidenceRevision != context.CurrentRevision)
            return null;

        if (proof.ForegroundEpoch != evidence.ForegroundEpoch || proof.ForegroundEpoch != context.CurrentForegroundEpoch)
            return null;

        if (proof.BrowserProcessId != evidence.BrowserProcessId || proof.BrowserProcessId != foreground.ProcessId)
            return null;

        return MatchOrigin(evidence.Origin);
    }

    private static SupportedWebTarget? MatchOrigin(string origin)
    {
        if (string.Equals(origin, ChatGptOrigin, StringComparison.Ordinal))
            return SupportedWebTarget.ChatGptWeb;
        if (string.Equals(origin, ClaudeOrigin, StringComparison.Ordinal))
            return SupportedWebTarget.ClaudeWeb;
        if (string.Equals(origin, GeminiOrigin, StringComparison.Ordinal))
            return SupportedWebTarget.GeminiWeb;
        if (string.Equals(origin, GrokOrigin, StringComparison.Ordinal))
            return SupportedWebTarget.GrokWeb;
        if (string.Equals(origin, DeepSeekOrigin, StringComparison.Ordinal))
            return SupportedWebTarget.DeepSeekWeb;

        return null;
    }

    // Gate 031F6F (E2) -- term B delegates to WebBrowserGate, the ONE shared source of Chrome/Edge
    // browser-identity policy (see that type's own doc). Code motion only: the four browser/
    // publisher literals this predicate used to declare privately now live exclusively there.
    private static bool IsSupportedBrowser(ForegroundTargetSnapshot foreground) =>
        WebBrowserGate.IsSupported(
            foreground.ProcessName, foreground.PackageIdentity, foreground.ExecutableSignature, foreground.SignerOrganization);
}
