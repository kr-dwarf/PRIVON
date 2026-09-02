using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031F1R -- the single, stateless, mechanical Native Messaging frame decoder
/// (Gate 031E2 PARSER_FRAMING_SEAM, wire format frozen by Gate 031F1R's own commander decision).
/// Pure function over an in-memory byte buffer: no pipe, no process, no host executable, no
/// channel/connection state of any kind (Gate 031F1R section 13 NO_CHANNEL_STATE) -- it never
/// increments a revision, stores evidence, assigns a ChannelId, or tracks connection state. This
/// type contains no product-specific browser, publisher, target, or origin policy of any kind,
/// and no supported-origin allowlist -- see <see cref="WebProtocolMessage"/>'s own PRIVACY_SHAPE
/// doc for what this decoder can and cannot produce.
///
/// VALIDATION_ORDER (frozen, Gate 031F1R -- each step below reports its own status and returns
/// immediately; a later step never runs once an earlier one has already failed):
///   1. fewer than 4 prefix bytes available            -> Incomplete
///   2. decoded length == 0                             -> InvalidLength
///   3. decoded length > <see cref="MaxPayloadBytes"/>  -> Oversized (BEFORE any payload
///      allocation/copy -- the length is read directly off the input span; no buffer of the
///      declared size is ever allocated before this check)
///   4. declared payload not yet fully available         -> Incomplete
///   5. payload is not strict UTF-8                      -> InvalidUtf8
///   6. text is not exactly one well-formed JSON value, OR that value's top-level JSON kind is
///      not an object (Gate 031F1R.1: the Native Messaging application envelope IS an object --
///      a syntactically valid array/string/number/boolean/null top level is not a valid protocol
///      envelope at all, so it is MalformedJson, never InvalidValue) -> MalformedJson
///   7. "v" absent/non-integer/not exactly the one frozen supported version -> UnsupportedVersion
///   8. "type" absent or not an exact-case-sensitive match of one of the six frozen names ->
///      UnknownType
///   9. a field the identified type requires is absent   -> MissingField
///   10. a duplicate property name (NOTICED earlier, CLASSIFIED only here -- see
///       DETECTION_VS_CLASSIFICATION below), a property outside the identified type's allowed
///       set, or an individual field's own value-shape rule (enum spelling/casing, the
///       OriginResolution/Origin presence invariant, the nonce representation) -> InvalidValue
///
/// DETECTION_VS_CLASSIFICATION (Gate 031F1R.1 correction): a duplicate property name is NOTICED
/// while building this decoder's own field map (immediately after confirming the JSON root is an
/// object), but its RESULT -- <see cref="WebFrameDecodeStatus.InvalidValue"/> -- is not RETURNED
/// until steps 7, 8, and 9 have already had their required precedence. Detecting a fact early and
/// classifying the overall decode outcome from it are different points in this method: the field
/// map still resolves "v" and "type" from a duplicated key's FIRST occurrence (<c>Dictionary
/// .TryAdd</c> keeps the first value and reports the collision without overwriting it), so an
/// otherwise-unsupported version or unrecognized type is still reported as
/// <see cref="WebFrameDecodeStatus.UnsupportedVersion"/>/<see cref="WebFrameDecodeStatus.UnknownType"/>
/// even when the payload also contains an unrelated duplicated property -- never preempted by the
/// duplicate. Only once version, type, and required-field presence have all separately passed does
/// a previously-noticed duplicate finally surface as step 10's own
/// <see cref="WebFrameDecodeStatus.InvalidValue"/>.
///
/// LITTLE_ENDIAN_EXPLICIT: the 4-byte length prefix is read via
/// <see cref="BinaryPrimitives.ReadUInt32LittleEndian"/> regardless of host CPU endianness -- never
/// relying on <c>BitConverter</c>'s platform-dependent default, per Gate 031F1R's own
/// auditability requirement.
///
/// EXPLICIT_ENUM_MATCHING: <c>focus</c>/<c>originResolution</c> string values are matched by
/// explicit, hand-written string comparisons -- never <c>Enum.Parse</c>, which (without
/// <c>ignoreCase</c>) still silently accepts a purely-numeric string as a valid enum value (e.g.
/// <c>Enum.Parse&lt;BrowserFocus&gt;("2")</c> succeeds) and would therefore accidentally admit the
/// "numeric enum ordinal" wire form Gate 031F1R explicitly excluded.
/// </summary>
public static class WebFrameDecoder
{
    public const int MaxPayloadBytes = 4096;

    private const int SupportedProtocolVersion = 1;
    private const int NonceEncodedLength = 43;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions StrictJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>Decodes exactly one frame from the start of <paramref name="buffer"/>. On any
    /// non-<see cref="WebFrameDecodeStatus.Ok"/> result, <paramref name="message"/> is
    /// <c>default</c>.</summary>
    public static WebFrameDecodeStatus Decode(ReadOnlySpan<byte> buffer, out WebProtocolMessage message)
    {
        message = default;

        // 1. prefix incomplete
        if (buffer.Length < 4)
            return WebFrameDecodeStatus.Incomplete;

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(buffer);

        // 2. zero length
        if (length == 0)
            return WebFrameDecodeStatus.InvalidLength;

        // 3. oversized -- checked from the raw prefix value alone, before any payload buffer exists
        if (length > (uint)MaxPayloadBytes)
            return WebFrameDecodeStatus.Oversized;

        var afterPrefix = buffer[4..];

        // 4. payload incomplete
        if ((uint)afterPrefix.Length < length)
            return WebFrameDecodeStatus.Incomplete;

        var payload = afterPrefix[..(int)length];

        // 5. strict UTF-8
        string json;
        try
        {
            json = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return WebFrameDecodeStatus.InvalidUtf8;
        }

        // 6. exactly one well-formed JSON object
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, StrictJsonOptions);
        }
        catch (JsonException)
        {
            return WebFrameDecodeStatus.MalformedJson;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return WebFrameDecodeStatus.MalformedJson;

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            bool hasDuplicateProperty = false;
            foreach (var property in root.EnumerateObject())
            {
                // NOTICED here, CLASSIFIED only at step 10 -- see this type's own
                // DETECTION_VS_CLASSIFICATION doc. The first occurrence's value is kept (TryAdd
                // never overwrites), so version/type extraction below is unaffected.
                if (!fields.TryAdd(property.Name, property.Value))
                    hasDuplicateProperty = true;
            }

            // 7. version -- for every message, before type/fields are even inspected
            if (!fields.TryGetValue("v", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out int version)
                || version != SupportedProtocolVersion)
            {
                return WebFrameDecodeStatus.UnsupportedVersion;
            }

            // 8. type
            if (!fields.TryGetValue("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !TryParseMessageType(typeElement.GetString(), out var messageType))
            {
                return WebFrameDecodeStatus.UnknownType;
            }

            var (required, allowed) = FieldSetFor(messageType);

            // 9. missing required field
            foreach (var name in required)
            {
                if (!fields.ContainsKey(name))
                    return WebFrameDecodeStatus.MissingField;
            }

            // 10a. duplicate property, classified only now that version/type/required-field
            // precedence has already been satisfied (see DETECTION_VS_CLASSIFICATION doc above).
            if (hasDuplicateProperty)
                return WebFrameDecodeStatus.InvalidValue;

            // 10b. unknown property outside this type's allowed set (v/type always allowed)
            foreach (var name in fields.Keys)
            {
                if (name is "v" or "type")
                    continue;
                if (!allowed.Contains(name))
                    return WebFrameDecodeStatus.InvalidValue;
            }

            // 10c. per-field value validation
            bool? accepted = null;
            BrowserFocus? focus = null;
            OriginResolution? originResolution = null;
            string? origin = null;
            string? nonce = null;

            if (allowed.Contains("accepted"))
            {
                var element = fields["accepted"];
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return WebFrameDecodeStatus.InvalidValue;
                accepted = element.ValueKind == JsonValueKind.True;
            }

            if (allowed.Contains("focus"))
            {
                if (!TryParseFocus(fields["focus"], out var parsedFocus))
                    return WebFrameDecodeStatus.InvalidValue;
                focus = parsedFocus;
            }

            if (allowed.Contains("originResolution"))
            {
                if (!TryParseOriginResolution(fields["originResolution"], out var parsedOriginResolution))
                    return WebFrameDecodeStatus.InvalidValue;
                originResolution = parsedOriginResolution;

                bool originPresent = fields.TryGetValue("origin", out var originElement);
                if (parsedOriginResolution == Privon.Browser.OriginResolution.Resolved)
                {
                    if (!originPresent
                        || originElement.ValueKind != JsonValueKind.String
                        || originElement.GetString() is not { Length: > 0 } originValue)
                    {
                        return WebFrameDecodeStatus.InvalidValue;
                    }

                    origin = originValue;
                }
                else if (originPresent)
                {
                    // Origin MUST be absent (never present-with-null, never present-and-empty)
                    // when OriginResolution is Unresolved/Unsupported.
                    return WebFrameDecodeStatus.InvalidValue;
                }
            }

            if (allowed.Contains("nonce"))
            {
                if (!TryValidateNonce(fields["nonce"], out nonce))
                    return WebFrameDecodeStatus.InvalidValue;
            }

            message = new WebProtocolMessage(messageType, accepted, focus, originResolution, origin, nonce);
            return WebFrameDecodeStatus.Ok;
        }
    }

    private static bool TryParseMessageType(string? value, out WebProtocolMessageType type)
    {
        switch (value)
        {
            case "Hello": type = WebProtocolMessageType.Hello; return true;
            case "HelloAck": type = WebProtocolMessageType.HelloAck; return true;
            case "StateInvalidate": type = WebProtocolMessageType.StateInvalidate; return true;
            case "StateAssert": type = WebProtocolMessageType.StateAssert; return true;
            case "ChallengeRequest": type = WebProtocolMessageType.ChallengeRequest; return true;
            case "ChallengeResponse": type = WebProtocolMessageType.ChallengeResponse; return true;
            default: type = default; return false;
        }
    }

    // Exact-string matching, deliberately not Enum.Parse -- see this type's own
    // EXPLICIT_ENUM_MATCHING doc.
    private static bool TryParseFocus(JsonElement element, out BrowserFocus value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String) return false;

        switch (element.GetString())
        {
            case "Unresolved": value = BrowserFocus.Unresolved; return true;
            case "NotFocused": value = BrowserFocus.NotFocused; return true;
            case "Focused": value = BrowserFocus.Focused; return true;
            default: return false;
        }
    }

    private static bool TryParseOriginResolution(JsonElement element, out OriginResolution value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String) return false;

        switch (element.GetString())
        {
            case "Unresolved": value = OriginResolution.Unresolved; return true;
            case "Unsupported": value = OriginResolution.Unsupported; return true;
            case "Resolved": value = OriginResolution.Resolved; return true;
            default: return false;
        }
    }

    // Structural validation only (length + alphabet + successful 32-byte Base64URL-without-
    // padding decode) -- this decoder never generates or interprets nonce content (Gate 031F1R:
    // "The codec validates representation only. It does NOT generate nonce values in this gate.").
    private static bool TryValidateNonce(JsonElement element, out string? nonce)
    {
        nonce = null;
        if (element.ValueKind != JsonValueKind.String) return false;

        string? value = element.GetString();
        if (value is null || value.Length != NonceEncodedLength) return false;

        foreach (char c in value)
        {
            bool valid = (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c is '-' or '_';
            if (!valid) return false;
        }

        // 43 unpadded Base64URL characters encode exactly 32 bytes -- confirmed by an actual
        // decode (translating to standard Base64 + the one required '=' pad) rather than trusting
        // length/alphabet alone.
        Span<char> standardBase64 = stackalloc char[NonceEncodedLength + 1];
        for (int i = 0; i < NonceEncodedLength; i++)
        {
            standardBase64[i] = value[i] switch { '-' => '+', '_' => '/', var c => c };
        }

        standardBase64[NonceEncodedLength] = '=';

        Span<byte> decoded = stackalloc byte[33];
        if (!Convert.TryFromBase64Chars(standardBase64, decoded, out int bytesWritten) || bytesWritten != 32)
            return false;

        nonce = value;
        return true;
    }

    private static (string[] Required, HashSet<string> Allowed) FieldSetFor(WebProtocolMessageType type) => type switch
    {
        WebProtocolMessageType.Hello =>
            ([], new HashSet<string>(StringComparer.Ordinal)),
        WebProtocolMessageType.HelloAck =>
            (["accepted"], new HashSet<string>(StringComparer.Ordinal) { "accepted" }),
        WebProtocolMessageType.StateInvalidate =>
            ([], new HashSet<string>(StringComparer.Ordinal)),
        WebProtocolMessageType.StateAssert =>
            (["focus", "originResolution"], new HashSet<string>(StringComparer.Ordinal) { "focus", "originResolution", "origin" }),
        WebProtocolMessageType.ChallengeRequest =>
            (["nonce"], new HashSet<string>(StringComparer.Ordinal) { "nonce" }),
        WebProtocolMessageType.ChallengeResponse =>
            (["nonce", "focus", "originResolution"], new HashSet<string>(StringComparer.Ordinal) { "nonce", "focus", "originResolution", "origin" }),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
