using System.Buffers.Binary;

namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6C -- a dumb, mechanical, stateless Native Messaging frame relay (Gate
/// 031F6B E1's own frozen contract; boundary fixed by Gate 031F6A.1). This type carries ZERO
/// protocol/JSON awareness: it never references <see cref="WebFrameDecoder"/>, never references
/// <see cref="WebFrameEncoder"/>, and never parses, decodes, rewrites, or even looks at the content
/// of a payload -- it understands exactly one thing, the 4-byte little-endian length-prefixed frame
/// shape, and relays those bytes verbatim. All actual protocol decoding continues to happen exactly
/// once, in the tray App, via <see cref="WebFrameDecoder"/> (Gate 031F6A.1's frozen boundary) -- a
/// future browser-spawned host process built on this type gains no product/security policy of its
/// own by using it.
///
/// SINGLE_GENERIC_RELAY (Gate 031F6B section 23): one operation, reusable unchanged for both
/// directions of a future full-duplex relay (browser-to-app and app-to-browser) -- there is no
/// separate "BrowserToAppPump"/"AppToBrowserPump" type, since the mechanics are direction-agnostic.
///
/// NO_PARTIAL_EMISSION (Gate 031F6B section 19/24): nothing is ever written to the destination
/// stream until the ENTIRE source frame (prefix and declared payload) has been fully and
/// successfully acquired -- a clean end-of-stream, a truncated prefix, a truncated payload, a zero
/// declared length, and an oversized declared length all leave the destination completely untouched.
///
/// NO_CANCELLATION_IN_E1 (Gate 031F6B section 24, deliberate): this gate's own single-frame
/// primitive takes no <see cref="System.Threading.CancellationToken"/> -- any future full-duplex
/// host relay's lifecycle/shutdown story is an E3_LIFECYCLE_DECISION, not decided here.
///
/// BOUNDED_MEMORY (Gate 031F6B section 22): each call allocates at most a 4-byte prefix buffer plus
/// one payload buffer no larger than <see cref="MaxPayloadBytes"/> -- never a whole-channel buffer,
/// never a string conversion, never a JSON document.
///
/// CAP_DUPLICATION (deliberate, mirroring <see cref="WebFrameEncoder"/>'s own NONCE_DUPLICATION
/// doc): this type carries its own copy of the frozen 4096-byte payload cap rather than referencing
/// <c>WebFrameDecoder.MaxPayloadBytes</c> -- section 16 requires the pump to never call
/// <see cref="WebFrameDecoder"/> at all, so this type carries zero compile-time dependency on it.
/// Both copies are held in sync by the same frozen wire contract, not by shared code.
/// </summary>
public static class WebNativeMessagingPump
{
    private const int PrefixLength = 4;
    private const int MaxPayloadBytes = 4096;

    /// <summary>Relays exactly one complete Native Messaging frame from <paramref name="source"/> to
    /// <paramref name="destination"/>, looping over short reads rather than assuming a single
    /// <see cref="Stream.ReadAsync(byte[], int, int)"/> call fulfills the requested byte count.</summary>
    public static async Task<WebPumpRelayStatus> RelayOneFrameAsync(Stream source, Stream destination)
    {
        byte[] prefix = new byte[PrefixLength];
        int prefixBytesRead = await ReadExactAsync(source, prefix, PrefixLength).ConfigureAwait(false);

        if (prefixBytesRead == 0)
            return WebPumpRelayStatus.EndOfStream;

        if (prefixBytesRead < PrefixLength)
            return WebPumpRelayStatus.Truncated;

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);

        if (length == 0)
            return WebPumpRelayStatus.ZeroLength;

        // Rejected from the raw prefix value alone -- BEFORE any payload buffer is allocated or any
        // payload byte is ever read (mirrors the decoder's own Oversized precedent, without this
        // type ever referencing the decoder itself -- see this type's own CAP_DUPLICATION doc).
        if (length > (uint)MaxPayloadBytes)
            return WebPumpRelayStatus.Oversized;

        byte[] payload = new byte[length];
        int payloadBytesRead = await ReadExactAsync(source, payload, (int)length).ConfigureAwait(false);

        if (payloadBytesRead < length)
            return WebPumpRelayStatus.Truncated;

        await destination.WriteAsync(prefix, 0, PrefixLength).ConfigureAwait(false);
        await destination.WriteAsync(payload, 0, (int)length).ConfigureAwait(false);

        return WebPumpRelayStatus.Relayed;
    }

    /// <summary>Reads until exactly <paramref name="count"/> bytes have been copied into
    /// <paramref name="buffer"/>, or <paramref name="source"/> ends -- looping over short reads.
    /// Returns the number of bytes actually read, which is less than <paramref name="count"/> only
    /// when the source ended first.</summary>
    private static async Task<int> ReadExactAsync(Stream source, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await source.ReadAsync(buffer, totalRead, count - totalRead).ConfigureAwait(false);
            if (read == 0)
                break;

            totalRead += read;
        }

        return totalRead;
    }
}
