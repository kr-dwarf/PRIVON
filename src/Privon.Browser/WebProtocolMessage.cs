namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031F1R -- the minimum mechanical typed representation of a successfully
/// decoded Native Messaging frame. A single small, closed record covering all six frozen message
/// families (Gate 031F1R MESSAGE_FIELD_SETS) -- deliberately NOT an extensible dictionary/
/// arbitrary-property bag. Which fields are populated for a given <see cref="Type"/> is the
/// decoder's own responsibility (see <see cref="WebFrameDecoder"/>); this type itself carries no
/// per-type validation logic.
///
/// PRIVACY_SHAPE (Gate 031F1R): structurally incapable of expressing a full URL, path, query,
/// fragment, title, page/composer content, prompt text, cookie, token, or account/user identifier.
/// <see cref="Origin"/> is the ONLY origin-bearing field, and is exactly the already-prefiltered
/// mechanical origin string a <c>StateAssert</c>/<c>ChallengeResponse</c> message carries when
/// <see cref="OriginResolution"/> is <see cref="Browser.OriginResolution.Resolved"/> -- this type
/// (and the decoder that produces it) has no notion of which origin is actually supported; that
/// judgment belongs exclusively to <c>Privon.App.WebTargetGate</c>.
///
/// NO_CHANNEL_STATE (Gate 031F1R section 13): this type is a pure decode RESULT. It carries no
/// ChannelId, EvidenceRevision, ForegroundEpoch, or BrowserProcessId -- the wire protocol never
/// carries those (they are native-assigned/native-bound facts), and decoding this type performs no
/// side effect of any kind (no revision increment, no evidence storage, no connection tracking).
/// </summary>
public readonly record struct WebProtocolMessage(
    WebProtocolMessageType Type,
    bool? Accepted,
    BrowserFocus? Focus,
    OriginResolution? OriginResolution,
    string? Origin,
    string? Nonce);
