using Privon.Core;

namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031C (frozen contract) / Gate 031E1 (this type) -- the seven-field,
/// mechanical-facts-only Web counterpart to <c>Privon.Windows.ForegroundTargetSnapshot</c>.
/// Deliberately carries no product/browser policy of any kind: no notion of "supported target",
/// a specific product or browser identity, or "eligible" exists anywhere in this type or the rest
/// of <c>Privon.Browser</c> -- that judgment belongs entirely to <c>Privon.App.WebTargetGate</c>,
/// the only place any field of this type is ever compared against a configured product/browser
/// constant. This assembly boundary is enforced by
/// <c>Gate031_WebAuthorizationContractTests.Group18_PrivonBrowserAssembly_ContainsNoProductPolicyStrings</c>.
///
/// FACT_MODEL_IS_FROZEN (Gate 031C): exactly these seven fields, no more. Deliberately excludes
/// path, query, fragment, page/composer content, tab title, favicon, cookies, tokens, account
/// identity, and browsing history -- this type structurally CANNOT express any of them, by design
/// (see Gate 031C PRIVACY / Gate 031D Group18's own structural regression test). <see cref="Origin"/>
/// is the only origin-bearing field, and represents nothing more than a normalized
/// <c>scheme://host[:port]</c> -- never a path, query, or fragment.
///
/// SAFE_DEFAULT (Gate 031C section 14): <c>default(WebForegroundEvidence)</c> has
/// <see cref="BrowserFocus"/> <see cref="Browser.BrowserFocus.Unresolved"/> and
/// <see cref="OriginResolution"/> <see cref="Browser.OriginResolution.Unresolved"/> (both enums'
/// own zero-valued, safe-first member), <see cref="Origin"/> <see langword="null"/>, and every
/// numeric field zero -- so a default-initialized or partially-populated instance can never be
/// mistaken for authorizing evidence by <c>WebTargetGate</c>, which requires
/// <see cref="OriginResolution"/> == <see cref="Browser.OriginResolution.Resolved"/> and
/// <see cref="BrowserFocus"/> == <see cref="Browser.BrowserFocus.Focused"/> as genuine,
/// independently-checked gates.
///
/// <see cref="EvidenceRevision"/> reuses <see cref="RevisionId"/> rather than a bare
/// <see langword="long"/> -- the same "just a monotonically increasing counter, never PII"
/// discipline <c>Privon.Core.RevisionId</c> already established for the Windows clipboard-attempt
/// generation, applied here to the Web evidence channel's own freshness counter.
/// </summary>
public readonly record struct WebForegroundEvidence(
    uint BrowserProcessId,
    long ChannelId,
    RevisionId EvidenceRevision,
    BrowserFocus BrowserFocus,
    OriginResolution OriginResolution,
    string? Origin,
    long ForegroundEpoch);
