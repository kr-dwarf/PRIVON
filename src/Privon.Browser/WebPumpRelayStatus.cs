namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6C -- the frozen, mechanical, protocol-free relay-status set for
/// <see cref="WebNativeMessagingPump.RelayOneFrameAsync"/>. Kept deliberately small (Gate 031F6B
/// section 25: "the pump is not a decoder" -- it does not need the decoder's own ten-value
/// vocabulary, since it only ever answers one question: was exactly one complete mechanical frame
/// relayed).
///
/// SAFE_VALUE_FIRST (matching <see cref="WebFrameDecodeStatus"/>/<see cref="WebFrameEncodeStatus"/>'s
/// own documented convention): <see cref="EndOfStream"/> is declared first (value 0) and
/// <see cref="Relayed"/> last, so a caller that forgets to check this value, or receives a
/// default-initialized <see cref="WebPumpRelayStatus"/>, can never mistake silence for a frame having
/// actually been relayed.
/// </summary>
public enum WebPumpRelayStatus
{
    /// <summary>The source stream ended before any byte of the next frame's length prefix was ever
    /// read -- the ordinary, expected way a channel closes between frames. Distinct from every
    /// truncation case below.</summary>
    EndOfStream,

    /// <summary>The source stream ended partway through the 4-byte length prefix, or partway through
    /// the declared payload -- a genuinely malformed/truncated frame. Nothing was written to the
    /// destination.</summary>
    Truncated,

    /// <summary>The declared payload length was exactly zero (mirroring
    /// <see cref="WebFrameDecodeStatus.InvalidLength"/>). Nothing was written to the destination.</summary>
    ZeroLength,

    /// <summary>The declared payload length exceeded <see cref="WebFrameDecoder.MaxPayloadBytes"/>,
    /// rejected using the raw prefix value alone before any payload byte was ever read. Nothing was
    /// written to the destination.</summary>
    Oversized,

    /// <summary>Exactly one complete mechanical frame (the original 4-byte prefix plus its exact
    /// payload bytes, unmodified) was written to the destination.</summary>
    Relayed,
}
