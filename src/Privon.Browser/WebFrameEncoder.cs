using System.Buffers.Binary;
using System.Text.Json;

namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6C -- the single, stateless, mechanical Native Messaging frame encoder
/// (Gate 031F6B E1's own frozen contract): the exact behavioral inverse of the existing, frozen
/// <see cref="WebFrameDecoder"/>. Pure function over a <see cref="WebProtocolMessage"/> value: no
/// pipe, no process, no host executable, no channel/connection state, no nonce generation (Gate
/// 031F6B section 10 -- this type serializes the supplied frozen representation, it never
/// manufactures one). This type contains no product-specific browser, publisher, target, or origin
/// policy of any kind -- see <see cref="WebProtocolMessage"/>'s own PRIVACY_SHAPE doc for what this
/// encoder can and cannot express.
///
/// FRAME_MODEL (Gate 031F6B section 3, frozen): a 4-byte little-endian payload-length prefix
/// followed by the exact UTF-8 JSON payload IS the complete PRIVON frame -- there is no second inner
/// PRIVON envelope of any kind.
///
/// VALIDATE_THEN_BUILD: every message is fully validated against its own <see cref="TryGetTypeName"/>
/// / <see cref="IsValidForType"/> shape rules BEFORE any JSON is written, so an invalid message never
/// produces even a partial frame. EXPLICIT_ENUM_MATCHING (mirroring the decoder's own documented
/// discipline): message type / <see cref="BrowserFocus"/> / <see cref="OriginResolution"/> wire
/// strings are emitted via an explicit, hand-written switch against the SAME string literals
/// <see cref="WebFrameDecoder"/> accepts -- never a blind <c>Enum.ToString()</c> that could silently
/// drift from the decoder's own accepted vocabulary if a future refactor renamed an enum member.
///
/// NONCE_DUPLICATION (deliberate): <see cref="WebFrameDecoder"/> is FROZEN (Gate 031F6B section 29 --
/// this gate may not modify it), and its nonce-shape validation is private, so this encoder carries
/// its own copy of the identical structural check (length 43, Base64URL alphabet, decodes to exactly
/// 32 bytes) rather than reaching into the decoder's internals. Both copies are held in sync by the
/// same frozen wire contract, not by shared code.
/// </summary>
public static class WebFrameEncoder
{
    // Mirrors WebFrameDecoder's own frozen SupportedProtocolVersion/NonceEncodedLength values -- see
    // this type's own NONCE_DUPLICATION doc for why these are independent literals rather than a
    // shared constant.
    private const int ProtocolVersion = 1;
    private const int NonceEncodedLength = 43;

    /// <summary>Encodes <paramref name="message"/> into exactly one complete Native Messaging frame.
    /// On any non-<see cref="WebFrameEncodeStatus.Ok"/> result, <paramref name="frame"/> is
    /// <see langword="null"/> and no bytes of any kind are produced.</summary>
    public static WebFrameEncodeStatus Encode(WebProtocolMessage message, out byte[]? frame)
    {
        frame = null;

        if (!TryGetTypeName(message.Type, out string? typeName) || !IsValidForType(message))
            return WebFrameEncodeStatus.InvalidMessage;

        byte[] payload = BuildPayload(message, typeName!);

        if (payload.Length > WebFrameDecoder.MaxPayloadBytes)
            return WebFrameEncodeStatus.Oversized;

        byte[] result = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)payload.Length);
        payload.CopyTo(result, 4);

        frame = result;
        return WebFrameEncodeStatus.Ok;
    }

    /// <summary>Validates that <paramref name="message"/>'s populated fields exactly match the
    /// frozen required/allowed field set for its own <see cref="WebProtocolMessage.Type"/> (Gate
    /// 031F1R MESSAGE_FIELD_SETS) -- the exact reverse of <see cref="WebFrameDecoder"/>'s own
    /// per-type field rules. <paramref name="message"/>.Type is assumed already known-valid (checked
    /// by <see cref="TryGetTypeName"/> before this is called).</summary>
    private static bool IsValidForType(WebProtocolMessage message)
    {
        bool hasAccepted = message.Accepted is not null;
        bool hasFocus = message.Focus is not null;
        bool hasOriginResolution = message.OriginResolution is not null;
        bool hasOrigin = message.Origin is not null;
        bool hasNonce = message.Nonce is not null;

        switch (message.Type)
        {
            case WebProtocolMessageType.Hello:
            case WebProtocolMessageType.StateInvalidate:
                return !hasAccepted && !hasFocus && !hasOriginResolution && !hasOrigin && !hasNonce;

            case WebProtocolMessageType.HelloAck:
                return hasAccepted && !hasFocus && !hasOriginResolution && !hasOrigin && !hasNonce;

            case WebProtocolMessageType.StateAssert:
                if (hasAccepted || hasNonce)
                    return false;
                if (!hasFocus || !hasOriginResolution)
                    return false;
                return TryGetFocusName(message.Focus!.Value, out _)
                    && TryGetOriginResolutionName(message.OriginResolution!.Value, out _)
                    && IsOriginConsistent(message.OriginResolution!.Value, message.Origin);

            case WebProtocolMessageType.ChallengeRequest:
                if (hasAccepted || hasFocus || hasOriginResolution || hasOrigin)
                    return false;
                return IsValidNonce(message.Nonce);

            case WebProtocolMessageType.ChallengeResponse:
                if (hasAccepted)
                    return false;
                if (!hasFocus || !hasOriginResolution)
                    return false;
                return TryGetFocusName(message.Focus!.Value, out _)
                    && TryGetOriginResolutionName(message.OriginResolution!.Value, out _)
                    && IsOriginConsistent(message.OriginResolution!.Value, message.Origin)
                    && IsValidNonce(message.Nonce);

            default:
                return false; // unreachable -- TryGetTypeName already rejected any other value.
        }
    }

    /// <summary>Writes exactly the permitted JSON property set for <paramref name="message"/>'s own
    /// type -- never an optional field the type does not own, never a present-but-null field. Called
    /// only after <see cref="IsValidForType"/> has already confirmed the message is well-formed.</summary>
    private static byte[] BuildPayload(WebProtocolMessage message, string typeName)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", ProtocolVersion);
            writer.WriteString("type", typeName);

            switch (message.Type)
            {
                case WebProtocolMessageType.HelloAck:
                    writer.WriteBoolean("accepted", message.Accepted!.Value);
                    break;

                case WebProtocolMessageType.StateAssert:
                    WriteFocusAndOrigin(writer, message);
                    break;

                case WebProtocolMessageType.ChallengeRequest:
                    writer.WriteString("nonce", message.Nonce);
                    break;

                case WebProtocolMessageType.ChallengeResponse:
                    writer.WriteString("nonce", message.Nonce);
                    WriteFocusAndOrigin(writer, message);
                    break;
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteFocusAndOrigin(Utf8JsonWriter writer, WebProtocolMessage message)
    {
        TryGetFocusName(message.Focus!.Value, out string? focusName);
        TryGetOriginResolutionName(message.OriginResolution!.Value, out string? originResolutionName);

        writer.WriteString("focus", focusName);
        writer.WriteString("originResolution", originResolutionName);

        if (message.OriginResolution!.Value == OriginResolution.Resolved)
            writer.WriteString("origin", message.Origin);
    }

    private static bool IsOriginConsistent(OriginResolution resolution, string? origin) =>
        resolution == OriginResolution.Resolved
            ? !string.IsNullOrEmpty(origin)
            : origin is null;

    // Exact-string mapping, deliberately not Enum.ToString() -- see this type's own
    // EXPLICIT_ENUM_MATCHING doc. Mirrors WebFrameDecoder.TryParseMessageType's own switch, in reverse.
    private static bool TryGetTypeName(WebProtocolMessageType type, out string? name)
    {
        switch (type)
        {
            case WebProtocolMessageType.Hello: name = "Hello"; return true;
            case WebProtocolMessageType.HelloAck: name = "HelloAck"; return true;
            case WebProtocolMessageType.StateInvalidate: name = "StateInvalidate"; return true;
            case WebProtocolMessageType.StateAssert: name = "StateAssert"; return true;
            case WebProtocolMessageType.ChallengeRequest: name = "ChallengeRequest"; return true;
            case WebProtocolMessageType.ChallengeResponse: name = "ChallengeResponse"; return true;
            default: name = null; return false;
        }
    }

    // Mirrors WebFrameDecoder.TryParseFocus's own switch, in reverse.
    private static bool TryGetFocusName(BrowserFocus focus, out string? name)
    {
        switch (focus)
        {
            case BrowserFocus.Unresolved: name = "Unresolved"; return true;
            case BrowserFocus.NotFocused: name = "NotFocused"; return true;
            case BrowserFocus.Focused: name = "Focused"; return true;
            default: name = null; return false;
        }
    }

    // Mirrors WebFrameDecoder.TryParseOriginResolution's own switch, in reverse.
    private static bool TryGetOriginResolutionName(OriginResolution resolution, out string? name)
    {
        switch (resolution)
        {
            case OriginResolution.Unresolved: name = "Unresolved"; return true;
            case OriginResolution.Unsupported: name = "Unsupported"; return true;
            case OriginResolution.Resolved: name = "Resolved"; return true;
            default: name = null; return false;
        }
    }

    // Structural validation only (length + alphabet + successful 32-byte Base64URL-without-padding
    // decode) -- mirrors WebFrameDecoder.TryValidateNonce's own check; see this type's own
    // NONCE_DUPLICATION doc for why the logic is duplicated rather than shared.
    private static bool IsValidNonce(string? nonce)
    {
        if (nonce is null || nonce.Length != NonceEncodedLength)
            return false;

        foreach (char c in nonce)
        {
            bool valid = (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c is '-' or '_';
            if (!valid)
                return false;
        }

        Span<char> standardBase64 = stackalloc char[NonceEncodedLength + 1];
        for (int i = 0; i < NonceEncodedLength; i++)
        {
            standardBase64[i] = nonce[i] switch { '-' => '+', '_' => '/', var c => c };
        }

        standardBase64[NonceEncodedLength] = '=';

        Span<byte> decoded = stackalloc byte[33];
        return Convert.TryFromBase64Chars(standardBase64, decoded, out int bytesWritten) && bytesWritten == 32;
    }
}
