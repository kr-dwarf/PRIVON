using Privon.Browser;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6L (E4) -- the mechanical result of one <see cref="WebChannelSession.ChallengeAsync"/>
/// attempt. On <see cref="WebChallengeOutcome.Success"/>, <see cref="Focus"/>/<see cref="OriginResolution"/>/
/// <see cref="Origin"/> carry the browser's own mechanical facts EXACTLY as received in the matching
/// ChallengeResponse frame -- this type never runs product/origin policy of any kind (that remains
/// <see cref="WebTargetGate"/>'s exclusive responsibility). For every other outcome these three fields
/// carry their safe default (<see cref="BrowserFocus.Unresolved"/>/<see cref="OriginResolution.Unresolved"/>/
/// <see langword="null"/>).
/// </summary>
internal readonly record struct WebChallengeResult(
    WebChallengeOutcome Outcome,
    BrowserFocus Focus,
    OriginResolution OriginResolution,
    string? Origin);
