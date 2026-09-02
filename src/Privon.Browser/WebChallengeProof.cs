using Privon.Core;

namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031E2 (frozen contract, term H) / Gate 031F3 (this type) -- the mechanical,
/// bound result of a successful decision-time challenge/response exchange, bracketed by fresh
/// foreground captures before and after (Gate 031C CHALLENGE_CONTEXT_MODEL /
/// WEB_AUTHORIZATION_FORMULA term H). Exactly these four fields, no more:
/// <see cref="ChannelId"/>, <see cref="EvidenceRevision"/>, <see cref="ForegroundEpoch"/>,
/// <see cref="BrowserProcessId"/> -- deliberately NOT a nonce, timestamp, payload, origin, focus,
/// product, browser, expiry, or boolean success flag. This type represents only "this exact
/// mechanical state successfully passed the bracketed challenge" -- it does not itself perform or
/// prove the transport that earned it (Gate 031F3 section 9: no production helper anywhere
/// manufactures one; only a real, future decision-time exchange -- or, until then, a test -- may
/// construct one).
///
/// REPLACES_BOOLEAN (Gate 031E2 correction to Gate 031E1's original
/// <c>WebDecisionContext.ChallengeConfirmed</c> bool): a bare bool could not express WHAT state
/// was confirmed and was not re-checkable at guarded-transport CHECK1/CHECK2 (there was nothing to
/// compare it against). This type is bound, field-for-field, to the SAME channel/revision/epoch/
/// process facts <see cref="WebForegroundEvidence"/> and <see cref="Privon.App.WebDecisionContext"/>
/// already carry, so <c>Privon.App.WebTargetGate.Match</c> can require a genuine THREE-WAY
/// agreement (proof, evidence, and current state) on every one of these four facts -- see that
/// type's own doc for the exact comparison.
///
/// No persistence, no logging, no product/browser policy string of any kind -- this type is as
/// purely mechanical as <see cref="WebForegroundEvidence"/> itself.
/// </summary>
public readonly record struct WebChallengeProof(
    long ChannelId,
    RevisionId EvidenceRevision,
    long ForegroundEpoch,
    uint BrowserProcessId);
