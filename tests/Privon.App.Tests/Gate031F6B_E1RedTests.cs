using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Privon.Browser;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031F6B/031F6C -- Phase E1 BEHAVIORAL suite for the first two mechanical Phase-E
// layers, frozen by Gate 031F6B's own commander decision (NATIVE_MESSAGE_FRAME_IS_PRIVON_FRAME -- the
// existing 4-byte-LE-length + UTF-8-JSON native message frame IS the complete PRIVON frame; there is
// no second inner envelope):
//
//   Privon.Browser.WebFrameEncoder         -- codec only, the exact behavioral inverse of the
//                                              existing, frozen, UNMODIFIED WebFrameDecoder.
//   Privon.Browser.WebNativeMessagingPump  -- a dumb byte relay with zero protocol/JSON awareness
//                                              (Gate 031F6A.1's frozen boundary: all decoding
//                                              happens exactly once, in the tray App).
//
// Originally written RED (Gate 031F6B, neither production type existed yet); Gate 031F6C then
// implemented src/Privon.Browser/WebFrameEncoder.cs + WebFrameEncodeStatus.cs and
// WebNativeMessagingPump.cs + WebPumpRelayStatus.cs, turning every test below GREEN unmodified
// except the two dedicated existence canaries (E1-R1/E1-R14), whose names/comments were updated to
// state presence rather than absence -- their assertion body is unchanged. Every test locates its
// target type/member by reflection (matching this project's own established
// Gate031F4_2_C2FreshnessRedTests convention) and then executes real behavior against the REAL
// production WebFrameEncoder/WebNativeMessagingPump plus the REAL, EXISTING, unmodified
// WebProtocolMessage/WebFrameDecoder/WebFrameDecodeStatus types -- never a shape-only test. Existing
// R1 codec tests (Gate031D2_WebMechanicsRedTests.cs) are untouched and remain a separate GREEN
// control (48/48).
//
// API SURFACE FROZEN BY THIS GATE'S OWN TEST HARNESS (the smallest repository-consistent shape,
// mirroring WebFrameDecoder.Decode's own Status-enum-plus-out-param convention):
//
//   public static class WebFrameEncoder
//   {
//       public static WebFrameEncodeStatus Encode(WebProtocolMessage message, out byte[]? frame);
//   }
//   public enum WebFrameEncodeStatus { <failure member(s)>, ..., Ok }   // Ok declared LAST
//                                                                        // (SAFE_VALUE_FIRST).
//
//   public static class WebNativeMessagingPump
//   {
//       public static Task<WebPumpRelayStatus> RelayOneFrameAsync(Stream source, Stream destination);
//   }
//   public enum WebPumpRelayStatus { EndOfStream, <failure member(s)>, ..., Relayed }
//
// Deliberately NOT frozen beyond the two required member names ("Ok"/"Relayed") plus the one
// required "EndOfStream" distinction (section 18): the exact vocabulary for
// truncated/zero-length/oversized failures is left to E1 GREEN's own discretion (Gate 031F6B
// section 4 -- "do not freeze an unnecessarily elaborate API"). Tests below assert those failure
// cases only as "not Ok"/"not Relayed"/"not EndOfStream", never a specific failure member name.
//
// CANCELLATION (Gate 031F6B section 24): RelayOneFrameAsync deliberately takes NO CancellationToken
// in this gate -- any future full-duplex host relay's lifecycle/cancellation need is recorded as
// E3_LIFECYCLE_DECISION, not frozen here. The reflection harness below asserts the parameter list is
// exactly (Stream, Stream) so a CancellationToken parameter would itself be a RED failure.
//
// SHORT_WRITE (Gate 031F6B section 21): no test below exercises a short-write loop. Standard .NET
// Stream.WriteAsync(ReadOnlyMemory<byte>) / Stream.Write(byte[], int, int) already guarantee the
// entire supplied range is written (or an exception is thrown) -- unlike Read, which may legally
// return fewer bytes than requested. Inventing a short-write test double would assert a contract
// .NET does not have. MEMORY_BOUND (section 22) is not independently black-box testable without a
// production test hook (which this gate must not add); it is deferred to structural code review at
// E1 GREEN.
public class Gate031F6B_E1RedTests
{
    private static Assembly BrowserAssembly => typeof(WebForegroundEvidence).Assembly;

    private const string EncoderTypeName = "Privon.Browser.WebFrameEncoder";
    private const string PumpTypeName = "Privon.Browser.WebNativeMessagingPump";

    private static Type? EncoderType => BrowserAssembly.GetType(EncoderTypeName);
    private static Type? PumpType => BrowserAssembly.GetType(PumpTypeName);

    private const string EncoderContract =
        "Privon.Browser.WebFrameEncoder does not exist yet (Gate 031F6B E1, frozen contract). " +
        "Required: public static class WebFrameEncoder exposing exactly one member -- " +
        "public static WebFrameEncodeStatus Encode(WebProtocolMessage message, out byte[]? frame). " +
        "WebFrameEncodeStatus needs at least one non-Ok failure member and exactly one success " +
        "member named \"Ok\" (SAFE_VALUE_FIRST: Ok declared last). On success, frame is the COMPLETE " +
        "Native Messaging frame -- a 4-byte little-endian payload-length prefix followed by the " +
        "exact UTF-8 JSON payload, no BOM, no second envelope, max 4096 JSON payload bytes " +
        "(mirroring WebFrameDecoder.MaxPayloadBytes). On failure, frame is null and no partial bytes " +
        "are ever produced. The encoder must be the exact behavioral inverse of the existing, frozen " +
        "WebFrameDecoder: every Ok-encoded frame must decode via WebFrameDecoder.Decode back to a " +
        "WebProtocolMessage equal to the one encoded, for all six frozen message families. A " +
        "structurally invalid WebProtocolMessage (a field the identified Type does not own is set; a " +
        "required field is missing or invalid; the OriginResolution/Origin presence invariant is " +
        "violated; an out-of-range enum Type) must fail closed rather than ever emitting a " +
        "decoder-invalid frame.";

    private const string PumpContract =
        "Privon.Browser.WebNativeMessagingPump does not exist yet (Gate 031F6B E1, frozen contract). " +
        "Required: public static class WebNativeMessagingPump exposing exactly one member -- " +
        "public static Task<WebPumpRelayStatus> RelayOneFrameAsync(Stream source, Stream " +
        "destination) -- deliberately with NO CancellationToken parameter (section 24: deferred as " +
        "E3_LIFECYCLE_DECISION). It is a DUMB BYTE RELAY with zero protocol/JSON awareness (Gate " +
        "031F6A.1's frozen boundary: all decoding happens exactly once, in the tray App, via " +
        "WebFrameDecoder): it reads exactly one complete Native Messaging frame (4-byte " +
        "little-endian length prefix + exactly that many payload bytes, looping over short reads " +
        "rather than assuming Stream.ReadAsync fulfills the requested count in one call) and writes " +
        "those exact same bytes, unmodified, to destination. WebPumpRelayStatus must include at " +
        "least a distinct \"EndOfStream\" member for a clean channel end BEFORE any prefix byte is " +
        "read, and a distinct \"Relayed\" member (SAFE_VALUE_FIRST: Relayed declared last) for a " +
        "fully successful relay -- every other outcome (partial prefix, truncated payload, zero " +
        "declared length, declared length > 4096) must be neither EndOfStream nor Relayed. A " +
        "declared length exceeding 4096 must be rejected using the raw prefix value ALONE -- no " +
        "payload byte may ever be read or buffered for an oversized frame.";

    private const string ValidNonce = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"; // 43 chars, decodes to 32 zero bytes

    // ==================================================================
    // SHARED FRAME HELPERS (mirroring Gate031D2_WebMechanicsRedTests' own private helpers).
    // ==================================================================

    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    private static byte[] LengthPrefixed(byte[] payload)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
        return [.. length, .. payload];
    }

    private static byte[] ValidHelloFrame => LengthPrefixed(Json("{\"v\":1,\"type\":\"Hello\"}"));

    /// <summary>Strips the 4-byte prefix and returns the JSON payload text, also asserting (R11's
    /// companion invariant) that the frame carries no trailing bytes beyond the declared length.</summary>
    private static string ExtractJsonPayload(byte[] frame)
    {
        Assert.True(frame.Length >= 4, "an Ok-encoded frame must contain at least the 4-byte length prefix.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        Assert.Equal((int)length, frame.Length - 4);
        return Encoding.UTF8.GetString(frame, 4, (int)length);
    }

    // ==================================================================
    // ENCODER REFLECTION HARNESS -- locate-only; every assertion below it is real behavior against
    // the real, existing WebProtocolMessage/WebFrameDecoder/WebFrameDecodeStatus types.
    // ==================================================================

    private static (string Status, byte[]? Frame) InvokeEncode(WebProtocolMessage message, string requiredBehavior)
    {
        Assert.True(EncoderType is not null, EncoderContract + " REQUIRED FOR THIS TEST: " + requiredBehavior);

        var method = EncoderType!.GetMethod("Encode", BindingFlags.Public | BindingFlags.Static);
        Assert.True(method is not null,
            $"{EncoderTypeName} exists but exposes no public static Encode(...) method. " + EncoderContract);

        var parameters = method!.GetParameters();
        Assert.True(
            parameters.Length == 2
                && parameters[0].ParameterType == typeof(WebProtocolMessage)
                && !parameters[0].IsOut
                && parameters[1].IsOut
                && parameters[1].ParameterType == typeof(byte[]).MakeByRefType(),
            $"{EncoderTypeName}.Encode must be exactly Encode(WebProtocolMessage message, out byte[]? frame). " +
            EncoderContract);

        var args = new object?[] { message, null };
        object? resultObj = method.Invoke(null, args);
        Assert.True(resultObj is not null, $"{EncoderTypeName}.Encode returned null instead of a WebFrameEncodeStatus value.");

        return (resultObj!.ToString()!, (byte[]?)args[1]);
    }

    // ==================================================================
    // PUMP REFLECTION HARNESS -- same locate-then-behave discipline, over a real async invocation.
    // ==================================================================

    private static async Task<string> InvokeRelayOneFrameAsync(Stream source, Stream destination, string requiredBehavior)
    {
        Assert.True(PumpType is not null, PumpContract + " REQUIRED FOR THIS TEST: " + requiredBehavior);

        var method = PumpType!.GetMethod("RelayOneFrameAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.True(method is not null,
            $"{PumpTypeName} exists but exposes no public static RelayOneFrameAsync(...) method. " + PumpContract);

        var parameters = method!.GetParameters();
        Assert.True(
            parameters.Length == 2
                && typeof(Stream).IsAssignableFrom(parameters[0].ParameterType)
                && typeof(Stream).IsAssignableFrom(parameters[1].ParameterType),
            $"{PumpTypeName}.RelayOneFrameAsync must take exactly (Stream source, Stream destination), " +
            "with no CancellationToken parameter (E3_LIFECYCLE_DECISION, deferred). " + PumpContract);

        object? invokeResult = method.Invoke(null, [source, destination]);
        Assert.True(invokeResult is Task, $"{PumpTypeName}.RelayOneFrameAsync must return a Task<WebPumpRelayStatus>. " + PumpContract);

        var task = (Task)invokeResult!;
        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        Assert.True(resultProperty is not null,
            $"{PumpTypeName}.RelayOneFrameAsync must return Task<WebPumpRelayStatus>, not a bare Task. " + PumpContract);

        object? status = resultProperty!.GetValue(task);
        Assert.True(status is not null, $"{PumpTypeName}.RelayOneFrameAsync completed with a null WebPumpRelayStatus.");
        return status!.ToString()!;
    }

    // ==================================================================
    // E1-R1 -- production type presence (dedicated canary; every test below also re-locates it).
    // Gate 031F6C GREEN: WebFrameEncoder now exists, so this asserts its presence rather than its
    // absence -- the same reflection-based lookup, now proving the opposite fact.
    // ==================================================================

    [Fact]
    public void E1_R1_WebFrameEncoder_ProductionTypeExists()
    {
        Assert.True(EncoderType is not null, EncoderContract);
    }

    // ==================================================================
    // E1-R2..R8 -- round-trip: Encode(M) -> WebFrameDecoder.Decode(...) -> exact semantic M, for all
    // six frozen message families.
    // ==================================================================

    public static IEnumerable<object[]> RoundTripMessages() =>
    [
        ["Hello", new WebProtocolMessage(WebProtocolMessageType.Hello, null, null, null, null, null)],
        ["HelloAck (accepted=true)", new WebProtocolMessage(WebProtocolMessageType.HelloAck, true, null, null, null, null)],
        ["HelloAck (accepted=false)", new WebProtocolMessage(WebProtocolMessageType.HelloAck, false, null, null, null, null)],
        ["StateInvalidate", new WebProtocolMessage(WebProtocolMessageType.StateInvalidate, null, null, null, null, null)],
        ["StateAssert (Resolved)", new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Focused, OriginResolution.Resolved, "https://chatgpt.com", null)],
        ["StateAssert (Unsupported)", new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.NotFocused, OriginResolution.Unsupported, null, null)],
        ["StateAssert (Unresolved)", new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Unresolved, OriginResolution.Unresolved, null, null)],
        ["ChallengeRequest", new WebProtocolMessage(WebProtocolMessageType.ChallengeRequest, null, null, null, null, ValidNonce)],
        ["ChallengeResponse (Resolved)", new WebProtocolMessage(WebProtocolMessageType.ChallengeResponse, null, BrowserFocus.Focused, OriginResolution.Resolved, "https://chatgpt.com", ValidNonce)],
        ["ChallengeResponse (Unsupported)", new WebProtocolMessage(WebProtocolMessageType.ChallengeResponse, null, BrowserFocus.NotFocused, OriginResolution.Unsupported, null, ValidNonce)],
    ];

    [Theory]
    [MemberData(nameof(RoundTripMessages))]
    public void E1_R2toR8_RoundTrip_EncodeThenDecode_ProducesExactSemanticMessage(string scenario, WebProtocolMessage original)
    {
        var (status, frame) = InvokeEncode(original,
            $"Encode({scenario}) must succeed (Ok) and the resulting frame must decode, via the real " +
            "WebFrameDecoder.Decode, back to a WebProtocolMessage exactly equal to the one encoded -- " +
            "no field loss, no extra field, no alternate casing, no serialization alias.");

        Assert.Equal("Ok", status);
        Assert.NotNull(frame);

        var decodeStatus = WebFrameDecoder.Decode(frame!, out var decoded);
        Assert.True(decodeStatus == WebFrameDecodeStatus.Ok,
            $"E1 ({scenario}): the encoder produced a frame the real WebFrameDecoder rejected with {decodeStatus}.");
        Assert.Equal(original, decoded);
    }

    [Theory]
    [MemberData(nameof(RoundTripMessages))]
    public void E1_TypeString_IsExactCaseSensitiveEnumName(string scenario, WebProtocolMessage original)
    {
        var (status, frame) = InvokeEncode(original,
            $"Encode({scenario}) must emit \"type\" as the exact case-sensitive string \"{original.Type}\" " +
            "-- never lowercase, never a numeric ordinal.");

        Assert.Equal("Ok", status);
        using var document = JsonDocument.Parse(ExtractJsonPayload(frame!));
        Assert.True(document.RootElement.TryGetProperty("type", out var typeElement));
        Assert.Equal(JsonValueKind.String, typeElement.ValueKind);
        Assert.Equal(original.Type.ToString(), typeElement.GetString());
    }

    // ==================================================================
    // E1-R6 (origin absence) -- StateAssert Unsupported/Unresolved must never emit "origin" at all
    // (structural JSON absence, never present-as-null).
    // ==================================================================

    [Fact]
    public void E1_R6_StateAssert_Unsupported_JsonHasNoOriginProperty()
    {
        var message = new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.NotFocused, OriginResolution.Unsupported, null, null);
        var (status, frame) = InvokeEncode(message,
            "StateAssert with OriginResolution.Unsupported must never emit an \"origin\" JSON property " +
            "-- not null, not empty string, structurally ABSENT.");

        Assert.Equal("Ok", status);
        using var document = JsonDocument.Parse(ExtractJsonPayload(frame!));
        Assert.False(document.RootElement.TryGetProperty("origin", out _));
    }

    [Fact]
    public void E1_R6_StateAssert_Unresolved_JsonHasNoOriginProperty()
    {
        var message = new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Unresolved, OriginResolution.Unresolved, null, null);
        var (status, frame) = InvokeEncode(message,
            "StateAssert with OriginResolution.Unresolved must never emit an \"origin\" JSON property.");

        Assert.Equal("Ok", status);
        using var document = JsonDocument.Parse(ExtractJsonPayload(frame!));
        Assert.False(document.RootElement.TryGetProperty("origin", out _));
    }

    [Fact]
    public void E1_R5_StateAssert_Resolved_JsonHasExactNonEmptyOriginProperty()
    {
        var message = new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Focused, OriginResolution.Resolved, "https://chatgpt.com", null);
        var (status, frame) = InvokeEncode(message,
            "StateAssert with OriginResolution.Resolved must emit \"origin\" as the exact non-empty string.");

        Assert.Equal("Ok", status);
        using var document = JsonDocument.Parse(ExtractJsonPayload(frame!));
        Assert.True(document.RootElement.TryGetProperty("origin", out var originElement));
        Assert.Equal(JsonValueKind.String, originElement.ValueKind);
        Assert.Equal("https://chatgpt.com", originElement.GetString());
    }

    // ==================================================================
    // E1-R7 -- per-message exact permitted property set (v/type always present; no more, no fewer).
    // ==================================================================

    public static IEnumerable<object[]> AllowedPropertyScenarios() =>
    [
        ["Hello", new WebProtocolMessage(WebProtocolMessageType.Hello, null, null, null, null, null), new HashSet<string>(StringComparer.Ordinal) { "v", "type" }],
        ["HelloAck", new WebProtocolMessage(WebProtocolMessageType.HelloAck, true, null, null, null, null), new HashSet<string>(StringComparer.Ordinal) { "v", "type", "accepted" }],
        ["StateInvalidate", new WebProtocolMessage(WebProtocolMessageType.StateInvalidate, null, null, null, null, null), new HashSet<string>(StringComparer.Ordinal) { "v", "type" }],
        ["StateAssert (Resolved)", new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Focused, OriginResolution.Resolved, "https://chatgpt.com", null), new HashSet<string>(StringComparer.Ordinal) { "v", "type", "focus", "originResolution", "origin" }],
        ["StateAssert (Unsupported)", new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.NotFocused, OriginResolution.Unsupported, null, null), new HashSet<string>(StringComparer.Ordinal) { "v", "type", "focus", "originResolution" }],
        ["ChallengeRequest", new WebProtocolMessage(WebProtocolMessageType.ChallengeRequest, null, null, null, null, ValidNonce), new HashSet<string>(StringComparer.Ordinal) { "v", "type", "nonce" }],
        ["ChallengeResponse (Resolved)", new WebProtocolMessage(WebProtocolMessageType.ChallengeResponse, null, BrowserFocus.Focused, OriginResolution.Resolved, "https://chatgpt.com", ValidNonce), new HashSet<string>(StringComparer.Ordinal) { "v", "type", "nonce", "focus", "originResolution", "origin" }],
    ];

    [Theory]
    [MemberData(nameof(AllowedPropertyScenarios))]
    public void E1_R7_Encode_EmitsExactlyThePermittedPropertySet(string scenario, WebProtocolMessage message, HashSet<string> expectedProperties)
    {
        var (status, frame) = InvokeEncode(message,
            $"Encode({scenario}) must emit exactly its permitted JSON property set -- no more, no " +
            "fewer, and never an optional field the message's Type does not own.");

        Assert.Equal("Ok", status);
        using var document = JsonDocument.Parse(ExtractJsonPayload(frame!));
        var actualProperties = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(expectedProperties.SetEquals(actualProperties),
            $"E1 ({scenario}): expected property set {{{string.Join(", ", expectedProperties)}}}, " +
            $"got {{{string.Join(", ", actualProperties)}}}.");
    }

    // ==================================================================
    // E1-R6 (version/type exact contract) + enum string exactness.
    // ==================================================================

    [Fact]
    public void E1_Version_IsFrozenJsonIntegerOne_NeverStringFloatOrNull()
    {
        var message = new WebProtocolMessage(WebProtocolMessageType.Hello, null, null, null, null, null);
        var (status, frame) = InvokeEncode(message,
            "The \"v\" property must be the JSON integer 1 -- never a JSON string, a JSON float, or null.");

        Assert.Equal("Ok", status);
        using var document = JsonDocument.Parse(ExtractJsonPayload(frame!));
        Assert.True(document.RootElement.TryGetProperty("v", out var vElement));
        Assert.Equal(JsonValueKind.Number, vElement.ValueKind);
        Assert.True(vElement.TryGetInt32(out int version));
        Assert.Equal(1, version);
        Assert.Equal("1", vElement.GetRawText()); // never "1.0" -- proves it is a JSON integer, not a float.
    }

    [Fact]
    public void E1_EnumStrings_AreExactCaseSensitiveNames_NotLowercaseOrNumeric()
    {
        var message = new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Focused, OriginResolution.Resolved, "https://chatgpt.com", null);
        var (status, frame) = InvokeEncode(message,
            "focus/originResolution must be emitted as the exact case-sensitive enum member name " +
            "strings (e.g. \"Focused\", \"Resolved\") -- never lowercase, never a numeric ordinal.");

        Assert.Equal("Ok", status);
        var root = JsonDocument.Parse(ExtractJsonPayload(frame!)).RootElement;
        Assert.Equal(JsonValueKind.String, root.GetProperty("focus").ValueKind);
        Assert.Equal("Focused", root.GetProperty("focus").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("originResolution").ValueKind);
        Assert.Equal("Resolved", root.GetProperty("originResolution").GetString());
    }

    // ==================================================================
    // E1-R9 -- nonce preserved exactly: no re-encoding, no padding, no case change, no truncation.
    // ==================================================================

    [Fact]
    public void E1_R9_Nonce_PreservedExactly_NoPaddingAddedOrCaseChanged()
    {
        var message = new WebProtocolMessage(WebProtocolMessageType.ChallengeRequest, null, null, null, null, ValidNonce);
        var (status, frame) = InvokeEncode(message,
            "The nonce must be emitted verbatim -- no re-encoding, no padding character added, no " +
            "case transformation, no truncation or normalization.");

        Assert.Equal("Ok", status);
        string json = ExtractJsonPayload(frame!);
        Assert.DoesNotContain('=', json);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("nonce", out var nonceElement));
        Assert.Equal(ValidNonce, nonceElement.GetString());

        var decodeStatus = WebFrameDecoder.Decode(frame!, out var decoded);
        Assert.Equal(WebFrameDecodeStatus.Ok, decodeStatus);
        Assert.Equal(ValidNonce, decoded.Nonce);
    }

    // ==================================================================
    // E1-R10 -- 4-byte LE length prefix equals the exact UTF-8 BYTE count of the JSON payload.
    // ==================================================================

    [Theory]
    [MemberData(nameof(RoundTripMessages))]
    public void E1_R10_LengthPrefix_EqualsExactUtf8ByteCountOfJsonPayload(string scenario, WebProtocolMessage original)
    {
        var (status, frame) = InvokeEncode(original,
            $"Encode({scenario}): the 4-byte little-endian length prefix must equal exactly the " +
            "UTF-8 BYTE count of the JSON payload -- never a character count, never a UTF-16 count, " +
            "never the whole-frame byte count.");

        Assert.Equal("Ok", status);
        Assert.True(frame!.Length >= 4);
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        int actualPayloadBytes = frame.Length - 4;
        Assert.Equal((uint)actualPayloadBytes, declaredLength);

        string json = Encoding.UTF8.GetString(frame, 4, actualPayloadBytes);
        Assert.Equal(actualPayloadBytes, Encoding.UTF8.GetByteCount(json));
    }

    // ==================================================================
    // E1-R11 -- no BOM, no second envelope: the native frame IS the complete PRIVON frame.
    // ==================================================================

    [Theory]
    [MemberData(nameof(RoundTripMessages))]
    public void E1_R11_NoBom_NoSecondEnvelope_FrameIsExactlyPrefixPlusPayload(string scenario, WebProtocolMessage original)
    {
        var (status, frame) = InvokeEncode(original,
            $"Encode({scenario}) must never emit a UTF-8 BOM, and the frame must be exactly " +
            "4 + payloadLength bytes total -- no second inner PRIVON length/envelope of any kind.");

        Assert.Equal("Ok", status);
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        Assert.Equal(4 + (int)declaredLength, frame!.Length);
        Assert.False(frame.Length >= 7 && frame[4] == 0xEF && frame[5] == 0xBB && frame[6] == 0xBF,
            "frame must not begin its JSON payload with a UTF-8 byte-order-mark.");
    }

    // ==================================================================
    // E1-R12 -- every constructible invalid WebProtocolMessage fails closed (never Ok, never a
    // partial/decoder-invalid frame). Each case is genuinely representable via WebProtocolMessage's
    // own public constructor (CONSTRUCTOR_UNREPRESENTABLE states are never included).
    // ==================================================================

    public static IEnumerable<object[]> InvalidConstructibleMessages() =>
    [
        ["Hello with an unexpected Accepted value set", new WebProtocolMessage(WebProtocolMessageType.Hello, true, null, null, null, null)],
        ["HelloAck missing required Accepted", new WebProtocolMessage(WebProtocolMessageType.HelloAck, null, null, null, null, null)],
        ["ChallengeRequest missing required Nonce", new WebProtocolMessage(WebProtocolMessageType.ChallengeRequest, null, null, null, null, null)],
        ["ChallengeRequest with a structurally invalid Nonce", new WebProtocolMessage(WebProtocolMessageType.ChallengeRequest, null, null, null, null, "tooshort")],
        ["StateAssert missing required Focus and OriginResolution", new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, null, null, null, null)],
        ["StateAssert Resolved without Origin", new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Focused, OriginResolution.Resolved, null, null)],
        ["StateAssert Unsupported with a non-null Origin", new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Focused, OriginResolution.Unsupported, "https://chatgpt.com", null)],
        ["StateAssert Unresolved with a non-null Origin", new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Unresolved, OriginResolution.Unresolved, "https://chatgpt.com", null)],
        ["ChallengeResponse missing required Nonce", new WebProtocolMessage(WebProtocolMessageType.ChallengeResponse, null, BrowserFocus.Focused, OriginResolution.Resolved, "https://chatgpt.com", null)],
        ["ChallengeResponse Resolved without Origin", new WebProtocolMessage(WebProtocolMessageType.ChallengeResponse, null, BrowserFocus.Focused, OriginResolution.Resolved, null, ValidNonce)],
        ["out-of-range enum Type", new WebProtocolMessage((WebProtocolMessageType)999, null, null, null, null, null)],
    ];

    [Theory]
    [MemberData(nameof(InvalidConstructibleMessages))]
    public void E1_R12_InvalidConstructibleMessage_EncoderFailsClosed(string scenario, WebProtocolMessage invalid)
    {
        var (status, frame) = InvokeEncode(invalid,
            $"Encode must fail closed (never \"Ok\", never emit any frame bytes) for: {scenario}.");

        Assert.NotEqual("Ok", status);
        Assert.Null(frame);
    }

    // ==================================================================
    // E1-R13 -- payload-cap symmetry with WebFrameDecoder.MaxPayloadBytes (4096). Origin carries no
    // length bound of its own, so an oversized-but-otherwise-valid message IS constructible via the
    // real public WebProtocolMessage API -- no artificial/impossible fixture is needed.
    // ==================================================================

    [Fact]
    public void E1_R13_OversizedPayload_FailsClosed_NeverPartiallyEmitted()
    {
        string hugeOrigin = new string('x', 5000);
        var message = new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Focused, OriginResolution.Resolved, hugeOrigin, null);

        var (status, frame) = InvokeEncode(message,
            "A structurally valid message whose JSON payload would exceed WebFrameDecoder." +
            "MaxPayloadBytes (4096) must fail closed -- never truncated, never partially written.");

        Assert.NotEqual("Ok", status);
        Assert.Null(frame);
    }

    [Fact]
    public void E1_R13_OrdinarySmallPayload_StillEncodesSuccessfully()
    {
        // Proves the 4096 guard is a real boundary check, not an overzealous rejection of ordinary
        // valid messages.
        var message = new WebProtocolMessage(WebProtocolMessageType.StateAssert, null, BrowserFocus.Focused, OriginResolution.Resolved, "https://chatgpt.com", null);
        var (status, frame) = InvokeEncode(message, "An ordinary small valid message must still encode successfully.");

        Assert.Equal("Ok", status);
        Assert.NotNull(frame);
        Assert.True(frame!.Length - 4 <= 4096);
    }

    // ==================================================================
    // Reverse symmetry (section 14): a known-good DECODER frame -> decode -> re-encode -> decoder
    // accepts a semantically equivalent message. Byte-for-byte JSON equality is NOT required
    // (property order is not frozen contract) -- only semantic (record) equality after decoding.
    // ==================================================================

    [Fact]
    public void E1_DecoderProducedMessage_ReEncoded_DecodesBackToAnEqualMessage()
    {
        byte[] knownGoodFrame = LengthPrefixed(Json(
            "{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Focused\",\"originResolution\":\"Resolved\",\"origin\":\"https://chatgpt.com\"}"));
        var decodeStatus = WebFrameDecoder.Decode(knownGoodFrame, out var decoded);
        Assert.Equal(WebFrameDecodeStatus.Ok, decodeStatus);

        var (encodeStatus, reEncodedFrame) = InvokeEncode(decoded,
            "Re-encoding a message the real WebFrameDecoder just produced must succeed, and the " +
            "result must decode back to a message equal to the original decoded value.");

        Assert.Equal("Ok", encodeStatus);
        var reDecodeStatus = WebFrameDecoder.Decode(reEncodedFrame!, out var reDecoded);
        Assert.Equal(WebFrameDecodeStatus.Ok, reDecodeStatus);
        Assert.Equal(decoded, reDecoded);
    }

    // ==================================================================
    // TEST-ONLY STREAM DOUBLES for the pump (section 20/28: never trust MemoryStream's convenient
    // full reads to prove real pipe/stdin behavior). Write-side is never exercised on these --
    // RelayOneFrameAsync only ever reads from `source`; `destination` in every pump test below is a
    // plain writable MemoryStream.
    // ==================================================================

    /// <summary>Delivers <paramref name="data"/> across a scripted sequence of short-read chunk
    /// sizes (looping the final declared chunk size once the list is exhausted, then reporting a
    /// clean EOF once <paramref name="data"/> itself is exhausted). Read-only; CanSeek is false,
    /// matching a real stdin/named-pipe stream.</summary>
    private sealed class ScriptedChunkStream(byte[] data, IEnumerable<int> chunkSizes) : Stream
    {
        private readonly Queue<int> _chunkSizes = new(chunkSizes);
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(ReadCore(buffer.Span));

        private int ReadCore(Span<byte> destination)
        {
            if (_position >= data.Length)
                return 0; // clean EOF

            int remaining = data.Length - _position;
            int chunk = _chunkSizes.Count > 0 ? _chunkSizes.Dequeue() : remaining;
            int toCopy = Math.Min(Math.Min(chunk, remaining), destination.Length);
            data.AsSpan(_position, toCopy).CopyTo(destination);
            _position += toCopy;
            return toCopy;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Delivers exactly <paramref name="data"/> and then THROWS on any further read
    /// attempt, rather than reporting EOF -- used only to prove the oversized-frame guard (E1-R23)
    /// never even attempts to read a payload byte for a declared length beyond the 4096 cap.</summary>
    private sealed class ThrowsIfReadBeyondStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(ReadCore(buffer.Span));

        private int ReadCore(Span<byte> destination)
        {
            if (_position >= data.Length)
            {
                throw new InvalidOperationException(
                    "E1-R23: the pump attempted to read beyond the declared-oversized frame's prefix -- " +
                    "the 4096 cap must be enforced from the raw prefix value alone, before any payload " +
                    "byte is ever requested.");
            }

            int toCopy = Math.Min(data.Length - _position, destination.Length);
            data.AsSpan(_position, toCopy).CopyTo(destination);
            _position += toCopy;
            return toCopy;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ==================================================================
    // E1-R14 -- pump production type presence (dedicated canary). Gate 031F6C GREEN:
    // WebNativeMessagingPump now exists -- see E1-R1's own note above.
    // ==================================================================

    [Fact]
    public void E1_R14_WebNativeMessagingPump_ProductionTypeExists()
    {
        Assert.True(PumpType is not null, PumpContract);
    }

    // ==================================================================
    // E1-R15 -- one complete frame relayed byte-for-byte.
    // ==================================================================

    [Fact]
    public async Task E1_R15_OneCompleteFrame_RelayedByteForByte()
    {
        byte[] frame = ValidHelloFrame;
        var source = new MemoryStream(frame);
        var destination = new MemoryStream();

        string status = await InvokeRelayOneFrameAsync(source, destination,
            "A single complete, well-formed frame present in the input stream must be relayed " +
            "byte-for-byte to the output stream.");

        Assert.Equal("Relayed", status);
        Assert.Equal(frame, destination.ToArray());
    }

    // ==================================================================
    // E1-R16 -- multiple frames relay in exact order, fed via deliberately uneven short reads that
    // span frame boundaries.
    // ==================================================================

    [Fact]
    public async Task E1_R16_MultipleFrames_RelayedInExactOrder_UsingShortReads()
    {
        byte[] frameA = ValidHelloFrame;
        byte[] frameB = LengthPrefixed(Json("{\"v\":1,\"type\":\"StateInvalidate\"}"));
        byte[] frameC = LengthPrefixed(Json($"{{\"v\":1,\"type\":\"ChallengeRequest\",\"nonce\":\"{ValidNonce}\"}}"));
        byte[] all = [.. frameA, .. frameB, .. frameC];

        // Deliberately short, uneven reads spanning prefix/payload/frame boundaries -- never assumes
        // Stream.Read returns the requested byte count in one call (Gate 031F6B section 19/20).
        var source = new ScriptedChunkStream(all, [1, 2, 1, 3, 5, 2, 7, 4, 1]);
        var destination = new MemoryStream();

        string statusA = await InvokeRelayOneFrameAsync(source, destination, "First of three sequential frames must relay in order.");
        string statusB = await InvokeRelayOneFrameAsync(source, destination, "Second of three sequential frames must relay in order.");
        string statusC = await InvokeRelayOneFrameAsync(source, destination, "Third of three sequential frames must relay in order.");

        Assert.Equal("Relayed", statusA);
        Assert.Equal("Relayed", statusB);
        Assert.Equal("Relayed", statusC);
        Assert.Equal(all, destination.ToArray());
    }

    // ==================================================================
    // E1-R17/R18 -- short reads across the prefix, and across the payload.
    // ==================================================================

    [Fact]
    public async Task E1_R17_ShortReadAcrossThePrefix_StillRelaysCorrectly()
    {
        byte[] frame = ValidHelloFrame;
        var source = new ScriptedChunkStream(frame, [1, 2, 1]); // prefix delivered as 1+2+1 bytes
        var destination = new MemoryStream();

        string status = await InvokeRelayOneFrameAsync(source, destination,
            "A 4-byte length prefix delivered across multiple short reads (1+2+1 bytes) must still " +
            "be assembled correctly before the payload is read.");

        Assert.Equal("Relayed", status);
        Assert.Equal(frame, destination.ToArray());
    }

    [Fact]
    public async Task E1_R18_ShortReadAcrossThePayload_StillRelaysCorrectly()
    {
        byte[] frame = LengthPrefixed(Json("{\"v\":1,\"type\":\"StateInvalidate\"}"));
        var source = new ScriptedChunkStream(frame, [4, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1]); // full prefix, then 1-byte payload reads
        var destination = new MemoryStream();

        string status = await InvokeRelayOneFrameAsync(source, destination,
            "A declared payload delivered across many single-byte short reads must still be " +
            "assembled into exactly the declared byte count before relaying.");

        Assert.Equal("Relayed", status);
        Assert.Equal(frame, destination.ToArray());
    }

    // ==================================================================
    // E1-R19/R20 -- clean EOF before any prefix byte, vs. partial-prefix truncation. These must be
    // distinguishable outcomes (section 18): EndOfStream is pinned by name; the failure case is only
    // required to be neither EndOfStream nor Relayed (this gate does not freeze the exact failure
    // vocabulary -- see this file's header).
    // ==================================================================

    [Fact]
    public async Task E1_R19_CleanEndOfStream_BeforeAnyPrefixByte_IsReportedDistinctly()
    {
        var source = new ScriptedChunkStream([], []);
        var destination = new MemoryStream();

        string status = await InvokeRelayOneFrameAsync(source, destination,
            "EOF before any byte of the next prefix is read must be reported as a clean end-of-stream " +
            "-- distinct from a truncated/malformed frame.");

        Assert.Equal("EndOfStream", status);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task E1_R20_PartialPrefix_ThenEof_IsNeitherCleanEndOfStreamNorRelayed()
    {
        byte[] partialPrefix = [1, 2, 3]; // only 1-3 prefix bytes, then EOF
        var source = new ScriptedChunkStream(partialPrefix, []);
        var destination = new MemoryStream();

        string status = await InvokeRelayOneFrameAsync(source, destination,
            "EOF after only 1-3 prefix bytes must fail closed as a truncated frame -- never reported " +
            "as a clean end-of-stream, and never relayed as if it were a complete frame.");

        Assert.NotEqual("EndOfStream", status);
        Assert.NotEqual("Relayed", status);
        Assert.Equal(0, destination.Length);
    }

    // ==================================================================
    // E1-R21 -- full prefix, truncated payload.
    // ==================================================================

    [Fact]
    public async Task E1_R21_FullPrefix_TruncatedPayload_FailsClosed()
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 10); // declares 10 payload bytes
        byte[] truncated = [.. prefix, 1, 2, 3]; // only 3 of the declared 10 are ever available
        var source = new ScriptedChunkStream(truncated, []);
        var destination = new MemoryStream();

        string status = await InvokeRelayOneFrameAsync(source, destination,
            "EOF partway through a declared payload must fail closed as a truncated frame -- never " +
            "relayed as if it were complete.");

        Assert.NotEqual("Relayed", status);
        Assert.Equal(0, destination.Length);
    }

    // ==================================================================
    // E1-R22 -- zero declared length fails closed (mirrors WebFrameDecoder's own InvalidLength rule).
    // ==================================================================

    [Fact]
    public async Task E1_R22_ZeroLengthFrame_FailsClosed()
    {
        byte[] zeroPrefix = new byte[4]; // declared length == 0
        var source = new ScriptedChunkStream(zeroPrefix, []);
        var destination = new MemoryStream();

        string status = await InvokeRelayOneFrameAsync(source, destination,
            "A declared payload length of exactly zero must fail closed, mirroring WebFrameDecoder's " +
            "own InvalidLength rule -- never relayed as an empty payload.");

        Assert.NotEqual("Relayed", status);
        Assert.Equal(0, destination.Length);
    }

    // ==================================================================
    // E1-R23 -- oversized frame rejected from the raw prefix alone, before any payload byte is read.
    // ==================================================================

    [Fact]
    public async Task E1_R23_OversizedFrame_RejectedFromThePrefixAlone_BeforeAnyPayloadByteIsRead()
    {
        byte[] oversizedPrefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedPrefix, 5000); // > WebFrameDecoder.MaxPayloadBytes (4096)
        var source = new ThrowsIfReadBeyondStream(oversizedPrefix);
        var destination = new MemoryStream();

        string status = await InvokeRelayOneFrameAsync(source, destination,
            "A declared length exceeding the 4096 cap must be rejected using the raw prefix value " +
            "ALONE -- the pump must never attempt to read a single payload byte, let alone allocate a " +
            "buffer sized to the untrusted declared length.");

        Assert.NotEqual("Relayed", status);
        Assert.Equal(0, destination.Length);
    }

    // ==================================================================
    // E1-R24 -- the pump performs zero JSON decoding/validation: a mechanically well-framed but
    // semantically garbage (non-UTF-8) payload still relays, because the pump is a pure byte relay.
    // ==================================================================

    [Fact]
    public async Task E1_R24_Pump_NeverDecodesOrValidatesPayload_GarbagePayloadStillRelays()
    {
        byte[] garbagePayload = [0xFF, 0xFE, 0x00, 0x01, 0x02]; // deliberately invalid UTF-8/JSON
        byte[] frame = LengthPrefixed(garbagePayload);
        var source = new MemoryStream(frame);
        var destination = new MemoryStream();

        string status = await InvokeRelayOneFrameAsync(source, destination,
            "A mechanically well-framed but semantically invalid (non-UTF-8/non-JSON) payload must " +
            "still relay successfully -- the pump performs no decoding or validation of payload " +
            "content, only of the length framing (Gate 031F6A.1: decoding happens exactly once, in " +
            "the tray App, never in the browser-spawned host process).");

        Assert.Equal("Relayed", status);
        Assert.Equal(frame, destination.ToArray());
    }

    // ==================================================================
    // E1-R25 -- stdout purity: the output stream carries the relayed frame bytes and nothing else.
    // ==================================================================

    [Fact]
    public async Task E1_R25_Output_ContainsExactlyTheRelayedFrameBytes_NoExtraBytesOfAnyKind()
    {
        byte[] frame = ValidHelloFrame;
        var source = new MemoryStream(frame);
        var destination = new MemoryStream();

        string status = await InvokeRelayOneFrameAsync(source, destination,
            "The output stream must contain ONLY the relayed frame bytes -- no BOM, no newline, no " +
            "banner, no diagnostic text, no whitespace of any kind beyond the payload itself.");

        Assert.Equal("Relayed", status);
        Assert.Equal(frame.Length, destination.Length);
        Assert.Equal(frame, destination.ToArray());
    }

    // ==================================================================
    // SOURCE-HYGIENE LOCKS (sections 25/27) -- reflection can only see member names/constant values,
    // not comments or string literals, so these scan the FUTURE source file's entire text, matching
    // this project's own established Gate031F4_2_C2FreshnessRedTests convention
    // (TryFindAppSourceFile), pointed at src/Privon.Browser instead of src/Privon.App.
    // ==================================================================

    private static string? TryFindBrowserSourceFile(string typeSimpleName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PRIVON.slnx")))
            directory = directory.Parent;

        if (directory is null)
            return null;

        string candidate = Path.Combine(directory.FullName, "src", "Privon.Browser", typeSimpleName + ".cs");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>RED_INTEGRITY guard: proves the repository-root walk itself works, using an existing
    /// Privon.Browser file, before trusting any RED the source-hygiene scans below report for the
    /// not-yet-implemented E1 files.</summary>
    [Fact]
    public void E1_SourceDiscovery_WorksForAnExistingPrivonBrowserFile()
    {
        string? path = TryFindBrowserSourceFile(nameof(WebFrameDecoder));

        Assert.True(path is not null,
            "The repository-root walk used by the source-hygiene lock failed to locate a source file " +
            "that definitely exists (src/Privon.Browser/WebFrameDecoder.cs). Fix the discovery " +
            "technique before trusting any RED it reports for the not-yet-implemented E1 files.");
    }

    private static readonly string[] ForbiddenLoggingTokens =
    [
        "Console.Write", "Console.Out", "Console.Error", "Debug.Write", "Debug.Print",
        "Trace.Write", "ILogger", "Log.Information", "Log.Debug", "EventLog",
    ];

    [Theory]
    [InlineData("WebFrameEncoder")]
    [InlineData("WebNativeMessagingPump")]
    public void E1_R25_SourceHygiene_NoLoggingDependencyAnywhereInTheFile(string typeSimpleName)
    {
        string? path = TryFindBrowserSourceFile(typeSimpleName);
        Assert.True(path is not null,
            $"EXPECTED_E1_RED: src/Privon.Browser/{typeSimpleName}.cs does not exist yet. FROZEN " +
            $"SOURCE-HYGIENE RULE once it lands (Gate 031F6B section 25): {typeSimpleName} must " +
            "contain zero logging dependency of any kind, and must never log raw JSON, origin, " +
            "nonce, frame, or payload bytes.");

        string source = File.ReadAllText(path!);
        var hits = ForbiddenLoggingTokens.Where(t => source.Contains(t, StringComparison.Ordinal)).ToList();
        Assert.Empty(hits);
    }

    // Deliberately excludes bare "Browser" (Gate 031F3.1's own recorded false-positive precedent --
    // it would wrongly flag legitimate namespace/type-name text such as "Privon.Browser" itself) and
    // excludes bare "Chrome"/"Edge" as over-broad English-word tokens; the concrete executable/binary
    // names below are narrow enough not to collide.
    private static readonly string[] ForbiddenPhaseELeakageTokens =
    [
        "chrome.exe", "msedge.exe", "HKEY_", "Registry.", "ParentProcessId", "GetParentProcess",
        "NamedPipeServerStream", "NamedPipeClientStream", "WebTargetGate", "Clipboard",
        "System.Windows", "PresentationFramework", "CommandLineArgs",
    ];

    [Theory]
    [InlineData("WebFrameEncoder")]
    [InlineData("WebNativeMessagingPump")]
    public void E1_R27_SourceHygiene_NoPhaseELeakageAnywhereInTheFile(string typeSimpleName)
    {
        string? path = TryFindBrowserSourceFile(typeSimpleName);
        Assert.True(path is not null,
            $"EXPECTED_E1_RED: src/Privon.Browser/{typeSimpleName}.cs does not exist yet. FROZEN " +
            $"SOURCE-HYGIENE RULE once it lands (Gate 031F6B section 27): no Chrome/Edge executable " +
            "policy, registry paths, parent-PID logic, named-pipe endpoint names, WebTargetGate " +
            "policy, clipboard code, WPF, or host argv parsing anywhere in this file -- " +
            $"{typeSimpleName} is mechanical codec/transport only.");

        string source = File.ReadAllText(path!);
        var hits = ForbiddenPhaseELeakageTokens.Where(t => source.Contains(t, StringComparison.Ordinal)).ToList();
        Assert.Empty(hits);
    }
}
