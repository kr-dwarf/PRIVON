namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6C -- the frozen, mechanical, product-policy-free encode-status set for
/// <see cref="WebFrameEncoder.Encode"/>. Exactly these two failure members plus <see cref="Ok"/>, no
/// more -- mirroring <see cref="WebFrameDecodeStatus"/>'s own minimal-vocabulary discipline: the pump
/// and the encoder are codecs, not decision-makers, so their status sets stay small.
///
/// SAFE_VALUE_FIRST (matching <see cref="WebFrameDecodeStatus"/>'s own documented convention):
/// <see cref="InvalidMessage"/> is declared first (value 0) and <see cref="Ok"/> last, so a caller
/// that forgets to check this value, or receives a default-initialized
/// <see cref="WebFrameEncodeStatus"/>, can never mistake silence for a successfully encoded frame.
/// </summary>
public enum WebFrameEncodeStatus
{
    /// <summary>The supplied <see cref="WebProtocolMessage"/> is structurally invalid for its own
    /// <see cref="WebProtocolMessage.Type"/> -- a field the type does not own is set, a field the
    /// type requires is missing or fails its own value-shape rule (invalid nonce, out-of-range enum
    /// value), the OriginResolution/Origin presence invariant is violated, or the message carries an
    /// out-of-range/unrecognized <see cref="WebProtocolMessage.Type"/>. No frame is ever produced.</summary>
    InvalidMessage,

    /// <summary>The message is otherwise structurally valid, but its JSON payload would exceed
    /// <see cref="WebFrameDecoder.MaxPayloadBytes"/>. No frame is ever produced -- never truncated,
    /// never partially written.</summary>
    Oversized,

    /// <summary>Encoding succeeded: the produced frame is exactly the 4-byte little-endian payload
    /// length followed by the UTF-8 JSON payload, with no BOM and no second envelope.</summary>
    Ok,
}
