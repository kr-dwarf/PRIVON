using System.Reflection;
using System.Text;
using Privon.App;
using Privon.Browser;
using Privon.Core;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 -- mechanics coverage for the frozen Web Authorization Contract's Phase A/B
// production seams (Gate 031E2 contract; Gate 031F1R.1 protocol codec; Gate 031F2/031F2.1 channel
// registry; Gate 031F3 bound challenge proof). Current composition, by group:
//   R1 (protocol codec)              GREEN -- real Privon.Browser.WebFrameDecoder
//   R2 (channel lifecycle)           GREEN -- real Privon.Browser.WebChannelRegistry
//   R3 (bound challenge proof)       GREEN -- real WebChallengeProof/WebDecisionContext/
//                                             WebTargetGate.Match three-way agreement
//   R5 (invalidate-before-assert)    GREEN -- real WebChannelRegistry
//   R6 (coordinator Web routing/B2)  RED   -- ClipboardPrivacyCoordinator has no Web-authorization
//                                             routing yet; see that group's own doc for why a full
//                                             behavioral test is the strongest honest substitute
//   R8 (zero-identifier fail-closed) GREEN -- real WebTargetGate.Match, corrected
// R4 (CHECK1/CHECK2 freshness) lives in Privon.Windows.IntegrationTests
// (Gate031D2_WebFreshnessSeamTests.cs), still RED -- no injectable seam exists on
// ClipboardChangeMonitor yet.
//
// R7 (WINDOWS_COMPOSER_REGRESSION_GREEN) is not duplicated here: it already exists, unmodified,
// as ClipboardPrivacyCoordinatorTests.VerifiedWrite_PublishesExactTargetTextGeneration, and is
// re-run as part of this gate's own pre-existing-regression pass.
//
// ChallengeConfirmed (Gate 031E1's original bare-bool term H) has been removed from production
// entirely -- WebDecisionContext.ChallengeProof is a WebChallengeProof? now, per Gate 031F3.
public class Gate031D2_WebMechanicsRedTests
{
    private static Assembly BrowserAssembly => typeof(WebForegroundEvidence).Assembly;
    private static Assembly AppAssembly => typeof(TargetGate).Assembly;

    // ==================================================================
    // R1 -- REAL_PROTOCOL_FRAMING_GREEN (Gate 031F1R). The wire format ambiguities Gate 031F1
    // correctly refused to guess at are now frozen by explicit commander decision (protocol
    // version is the JSON integer 1; message type and BrowserFocus/OriginResolution are exact,
    // case-sensitive JSON strings matching their C# names; property names are exact camelCase;
    // nonce is a 43-character unpadded Base64URL string decoding to exactly 32 bytes; unknown or
    // duplicate JSON properties are InvalidValue). Every case below now calls the real, production
    // Privon.Browser.WebFrameDecoder.Decode directly -- no reflection.
    // ==================================================================

    private const string ValidNonce = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"; // 43 chars, decodes to 32 zero bytes

    private static byte[] LengthPrefixed(byte[] payload)
    {
        byte[] length = BitConverter.GetBytes(payload.Length); // native byte order = little-endian on win-x64
        return [.. length, .. payload];
    }

    private static byte[] LengthPrefixedBigEndian(byte[] payload)
    {
        byte[] length = BitConverter.GetBytes(payload.Length);
        Array.Reverse(length); // forces an ordinarily-small length to decode as an enormous LE value
        return [.. length, .. payload];
    }

    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    public static IEnumerable<object[]> FrameScenarios()
    {
        // ---- framing (unchanged from Gate 031D2 -- these were never part of the wire-format gap) ----
        yield return new object[] { "1: truncated length prefix (3 bytes)", new byte[] { 1, 2, 3 }, WebFrameDecodeStatus.Incomplete };
        yield return new object[] { "2: zero payload length", BitConverter.GetBytes(0), WebFrameDecodeStatus.InvalidLength };
        yield return new object[] { "3: declared length 5000 > 4096", BitConverter.GetBytes(5000), WebFrameDecodeStatus.Oversized };
        yield return new object[] { "4: ordinary length encoded big-endian", LengthPrefixedBigEndian(Json("{\"v\":1,\"type\":\"Hello\"}")), WebFrameDecodeStatus.Oversized };
        yield return new object[] { "5: declared length exceeds supplied bytes", (byte[])[.. BitConverter.GetBytes(100), 1, 2, 3], WebFrameDecodeStatus.Incomplete };
        yield return new object[] { "6: invalid UTF-8 payload", LengthPrefixed([0xFF, 0xFE, 0x00, 0x01]), WebFrameDecodeStatus.InvalidUtf8 };
        yield return new object[] { "7: malformed JSON", LengthPrefixed(Json("{not json")), WebFrameDecodeStatus.MalformedJson };

        // ---- version (Gate 031F1R frozen: JSON integer 1 only) ----
        yield return new object[] { "8: unsupported version (wrong integer)", LengthPrefixed(Json("{\"v\":999,\"type\":\"Hello\"}")), WebFrameDecodeStatus.UnsupportedVersion };
        yield return new object[] { "8b: version as JSON string \"1\"", LengthPrefixed(Json("{\"v\":\"1\",\"type\":\"Hello\"}")), WebFrameDecodeStatus.UnsupportedVersion };
        yield return new object[] { "8c: version as JSON float 1.0", LengthPrefixed(Json("{\"v\":1.0,\"type\":\"Hello\"}")), WebFrameDecodeStatus.UnsupportedVersion };
        yield return new object[] { "8d: version absent", LengthPrefixed(Json("{\"type\":\"Hello\"}")), WebFrameDecodeStatus.UnsupportedVersion };

        // ---- type (Gate 031F1R frozen: exact case-sensitive string, six known families) ----
        yield return new object[] { "9: unknown message type", LengthPrefixed(Json("{\"v\":1,\"type\":\"Bogus\"}")), WebFrameDecodeStatus.UnknownType };
        yield return new object[] { "9b: wrong-case message type", LengthPrefixed(Json("{\"v\":1,\"type\":\"hello\"}")), WebFrameDecodeStatus.UnknownType };

        // ---- required fields ----
        yield return new object[] { "10: StateAssert missing required fields", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\"}")), WebFrameDecodeStatus.MissingField };
        yield return new object[] { "10b: HelloAck missing accepted", LengthPrefixed(Json("{\"v\":1,\"type\":\"HelloAck\"}")), WebFrameDecodeStatus.MissingField };
        yield return new object[] { "10c: ChallengeRequest missing nonce", LengthPrefixed(Json("{\"v\":1,\"type\":\"ChallengeRequest\"}")), WebFrameDecodeStatus.MissingField };

        // ---- enum value validation (Gate 031F1R frozen: exact case-sensitive string, no numeric ordinals) ----
        yield return new object[] { "11: invalid BrowserFocus enum value", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Sideways\",\"originResolution\":\"Unsupported\"}")), WebFrameDecodeStatus.InvalidValue };
        yield return new object[] { "11b: wrong-case BrowserFocus enum value", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"focused\",\"originResolution\":\"Unsupported\"}")), WebFrameDecodeStatus.InvalidValue };
        yield return new object[] { "11c: numeric BrowserFocus enum value", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":2,\"originResolution\":\"Unsupported\"}")), WebFrameDecodeStatus.InvalidValue };
        yield return new object[] { "12: invalid OriginResolution value", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Focused\",\"originResolution\":\"Bogus\"}")), WebFrameDecodeStatus.InvalidValue };
        yield return new object[] { "12b: numeric OriginResolution value", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Focused\",\"originResolution\":2}")), WebFrameDecodeStatus.InvalidValue };

        // ---- OriginResolution/Origin presence invariant ----
        yield return new object[] { "13: Resolved with absent Origin", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Focused\",\"originResolution\":\"Resolved\"}")), WebFrameDecodeStatus.InvalidValue };
        yield return new object[] { "14: Unsupported with non-null Origin", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Focused\",\"originResolution\":\"Unsupported\",\"origin\":\"https://chatgpt.com\"}")), WebFrameDecodeStatus.InvalidValue };
        yield return new object[] { "14b: Unresolved with non-null Origin", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Focused\",\"originResolution\":\"Unresolved\",\"origin\":\"https://chatgpt.com\"}")), WebFrameDecodeStatus.InvalidValue };
        yield return new object[] { "14c: Resolved with null Origin", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Focused\",\"originResolution\":\"Resolved\",\"origin\":null}")), WebFrameDecodeStatus.InvalidValue };

        // ---- strict object shape (Gate 031F1R frozen: unknown/duplicate properties => InvalidValue) ----
        yield return new object[] { "21: unknown extra property", LengthPrefixed(Json("{\"v\":1,\"type\":\"Hello\",\"extra\":\"nope\"}")), WebFrameDecodeStatus.InvalidValue };
        yield return new object[] { "22: duplicate JSON property", LengthPrefixed(Json("{\"v\":1,\"type\":\"Hello\",\"v\":1}")), WebFrameDecodeStatus.InvalidValue };

        // ---- nonce shape ----
        yield return new object[] { "23: nonce wrong length", LengthPrefixed(Json("{\"v\":1,\"type\":\"ChallengeRequest\",\"nonce\":\"tooshort\"}")), WebFrameDecodeStatus.InvalidValue };
        yield return new object[] { "24: nonce invalid alphabet", LengthPrefixed(Json($"{{\"v\":1,\"type\":\"ChallengeRequest\",\"nonce\":\"{new string('+', 43)}\"}}")), WebFrameDecodeStatus.InvalidValue };

        // ---- STATUS PRECEDENCE (Gate 031F1R.1): a duplicate/unknown-property defect must never
        // preempt an earlier-ordered version/type/missing-field failure. None of P1-P4 duplicates
        // "v" or "type" itself (per Gate 031F1R.1's own instruction) -- each duplicates an
        // unrelated "extra" property instead, so the earlier-stage check under test is unambiguous. ----
        yield return new object[] { "P1: unsupported version + known type + duplicate extra property -> version wins", LengthPrefixed(Json("{\"v\":999,\"type\":\"Hello\",\"extra\":\"a\",\"extra\":\"b\"}")), WebFrameDecodeStatus.UnsupportedVersion };
        yield return new object[] { "P2: supported version + unknown type + duplicate extra property -> type wins", LengthPrefixed(Json("{\"v\":1,\"type\":\"Bogus\",\"extra\":\"a\",\"extra\":\"b\"}")), WebFrameDecodeStatus.UnknownType };
        yield return new object[] { "P3: supported version + known type + missing required field + duplicate extra property -> missing-field wins", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"extra\":\"a\",\"extra\":\"b\"}")), WebFrameDecodeStatus.MissingField };
        yield return new object[] { "P4: supported version + known type + all required present + duplicate property -> InvalidValue", LengthPrefixed(Json("{\"v\":1,\"type\":\"Hello\",\"extra\":\"a\",\"extra\":\"b\"}")), WebFrameDecodeStatus.InvalidValue };
        yield return new object[] { "P5: supported version + known type + all required present + unknown extra property (no duplicate) -> InvalidValue", LengthPrefixed(Json("{\"v\":1,\"type\":\"Hello\",\"extra\":\"nope\"}")), WebFrameDecodeStatus.InvalidValue };

        // ---- TOP-LEVEL ENVELOPE SHAPE (Gate 031F1R.1 residual decision): a syntactically valid
        // JSON value whose top level is not an object is not a valid protocol envelope at all ----
        yield return new object[] { "E1: top-level array", LengthPrefixed(Json("[]")), WebFrameDecodeStatus.MalformedJson };
        yield return new object[] { "E2: top-level string", LengthPrefixed(Json("\"hello\"")), WebFrameDecodeStatus.MalformedJson };
        yield return new object[] { "E3: top-level number", LengthPrefixed(Json("1")), WebFrameDecodeStatus.MalformedJson };
        yield return new object[] { "E4: top-level boolean", LengthPrefixed(Json("true")), WebFrameDecodeStatus.MalformedJson };
        yield return new object[] { "E5: top-level null", LengthPrefixed(Json("null")), WebFrameDecodeStatus.MalformedJson };

        // ---- valid messages -> Ok (all six families, including HelloAck which Gate 031D2 never exercised) ----
        yield return new object[] { "15: valid Hello", LengthPrefixed(Json("{\"v\":1,\"type\":\"Hello\"}")), WebFrameDecodeStatus.Ok };
        yield return new object[] { "15b: valid HelloAck", LengthPrefixed(Json("{\"v\":1,\"type\":\"HelloAck\",\"accepted\":true}")), WebFrameDecodeStatus.Ok };
        yield return new object[] { "16: valid StateInvalidate", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateInvalidate\"}")), WebFrameDecodeStatus.Ok };
        yield return new object[] { "17: valid StateAssert (Resolved)", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Focused\",\"originResolution\":\"Resolved\",\"origin\":\"https://chatgpt.com\"}")), WebFrameDecodeStatus.Ok };
        yield return new object[] { "18: valid StateAssert (Unsupported, no real URL)", LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Focused\",\"originResolution\":\"Unsupported\"}")), WebFrameDecodeStatus.Ok };
        yield return new object[] { "19: valid ChallengeRequest", LengthPrefixed(Json($"{{\"v\":1,\"type\":\"ChallengeRequest\",\"nonce\":\"{ValidNonce}\"}}")), WebFrameDecodeStatus.Ok };
        yield return new object[] { "20: valid ChallengeResponse", LengthPrefixed(Json($"{{\"v\":1,\"type\":\"ChallengeResponse\",\"nonce\":\"{ValidNonce}\",\"focus\":\"Focused\",\"originResolution\":\"Resolved\",\"origin\":\"https://chatgpt.com\"}}")), WebFrameDecodeStatus.Ok };
    }

    [Theory]
    [MemberData(nameof(FrameScenarios))]
    public void R1_FrameDecoding_EachScenario_MatchesFrozenStatus(string scenario, byte[] frame, WebFrameDecodeStatus expectedStatus)
    {
        var actualStatus = WebFrameDecoder.Decode(frame, out _);

        Assert.True(actualStatus == expectedStatus, $"R1 ({scenario}): expected {expectedStatus}, got {actualStatus}.");
    }

    [Fact]
    public void R1_ValidStateAssert_DecodesExpectedFields()
    {
        var frame = LengthPrefixed(Json("{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"Focused\",\"originResolution\":\"Resolved\",\"origin\":\"https://chatgpt.com\"}"));

        var status = WebFrameDecoder.Decode(frame, out var message);

        Assert.Equal(WebFrameDecodeStatus.Ok, status);
        Assert.Equal(WebProtocolMessageType.StateAssert, message.Type);
        Assert.Equal(BrowserFocus.Focused, message.Focus);
        Assert.Equal(OriginResolution.Resolved, message.OriginResolution);
        Assert.Equal("https://chatgpt.com", message.Origin);
        Assert.Null(message.Nonce);
        Assert.Null(message.Accepted);
    }

    [Fact]
    public void R1_ProtocolMessage_CannotExpressPageContentOrPii()
    {
        var forbidden = new[] { "Url", "Uri", "Href", "Path", "Query", "Fragment", "Title", "Content", "Prompt", "Composer", "Cookie", "Token", "Account", "User" };

        var offending = typeof(WebProtocolMessage).GetProperties().Select(p => p.Name)
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offending);

        // No message carries the native-assigned facts (Gate 031F1R MESSAGE_FIELD_SETS).
        var forbiddenNativeFacts = new[] { "ChannelId", "EvidenceRevision", "ForegroundEpoch", "BrowserProcessId" };
        var offendingNativeFacts = typeof(WebProtocolMessage).GetProperties().Select(p => p.Name)
            .Where(name => forbiddenNativeFacts.Contains(name, StringComparer.Ordinal))
            .ToList();

        Assert.Empty(offendingNativeFacts);
    }

    // ==================================================================
    // R2 -- CHANNEL_LIFECYCLE_GREEN (Gate 031F2). Real behavioral tests against the actual
    // production Privon.Browser.WebChannelRegistry -- no reflection. A fresh registry is used per
    // test so cases never interfere with one another.
    // ==================================================================

    private const uint SamplePid = 4242;
    private const uint OtherPid = 4243;
    private const string SampleOrigin = "https://example.test";

    [Fact]
    public void R2_A_FirstChannelForPid_IsAccepted()
    {
        var registry = new WebChannelRegistry();

        Assert.True(registry.TryConnect(SamplePid, out _));
    }

    [Fact]
    public void R2_B_AssignedChannelId_IsNeverZero()
    {
        var registry = new WebChannelRegistry();

        Assert.True(registry.TryConnect(SamplePid, out long channelId));
        Assert.NotEqual(0, channelId);
    }

    [Fact]
    public void R2_C_SecondLiveChannelForSamePid_IsRefused()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long first));

        bool secondAccepted = registry.TryConnect(SamplePid, out long second);

        Assert.False(secondAccepted);
        Assert.Equal(0, second);
        Assert.True(registry.TryGetCurrentEvidence(first, out _), "the original channel must remain live and untouched by the refused attempt.");
    }

    [Fact]
    public void R2_D_Disconnect_MakesOldEvidenceUnusableImmediately()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long channelId));
        Assert.True(registry.TryAssert(channelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out _));

        Assert.True(registry.TryDisconnect(channelId));

        Assert.False(registry.TryGetCurrentEvidence(channelId, out _));
    }

    [Fact]
    public void R2_E_DisconnectedChannel_IsTerminal()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long channelId));
        Assert.True(registry.TryDisconnect(channelId));

        Assert.False(registry.TryDisconnect(channelId), "disconnecting twice must not succeed a second time.");
        Assert.False(registry.TryInvalidate(channelId, out _), "no invalidate may ever succeed on a disconnected channel.");
        Assert.False(registry.TryAssert(channelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out _), "no assert may ever succeed on a disconnected channel.");
    }

    [Fact]
    public void R2_F_ReconnectSamePid_ReceivesDifferentChannelId()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long firstChannelId));
        Assert.True(registry.TryDisconnect(firstChannelId));

        Assert.True(registry.TryConnect(SamplePid, out long secondChannelId));

        Assert.NotEqual(firstChannelId, secondChannelId);
        Assert.True(secondChannelId > firstChannelId, "T2: the reconnect id must be strictly greater than the disconnected one -- the monotonic allocator, not mere inequality, is the non-reuse mechanism.");
    }

    [Fact]
    public void R2_G_NewConnection_BeginsAtUnresolved()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long channelId));

        Assert.True(registry.TryGetCurrentEvidence(channelId, out var evidence));
        Assert.Equal(BrowserFocus.Unresolved, evidence.BrowserFocus);
        Assert.Equal(OriginResolution.Unresolved, evidence.OriginResolution);
        Assert.Null(evidence.Origin);
    }

    [Fact]
    public void R2_H_ReconnectAfterDisconnect_NeverCarriesPriorOriginForward()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long firstChannelId));
        Assert.True(registry.TryAssert(firstChannelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out _));
        Assert.True(registry.TryDisconnect(firstChannelId));

        Assert.True(registry.TryConnect(SamplePid, out long secondChannelId));
        Assert.True(registry.TryGetCurrentEvidence(secondChannelId, out var evidence));

        Assert.Null(evidence.Origin);
        Assert.Equal(OriginResolution.Unresolved, evidence.OriginResolution);
    }

    [Fact]
    public void R2_I_ReconnectAfterDisconnect_NeverCarriesPriorRevisionForwardAsCurrent()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long firstChannelId));
        Assert.True(registry.TryAssert(firstChannelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out var firstAsserted));
        Assert.True(registry.TryDisconnect(firstChannelId));

        Assert.True(registry.TryConnect(SamplePid, out long secondChannelId));
        Assert.True(registry.TryGetCurrentEvidence(secondChannelId, out var secondEvidence));

        Assert.NotEqual(firstAsserted.EvidenceRevision, secondEvidence.EvidenceRevision);
        Assert.Equal(new RevisionId(0), secondEvidence.EvidenceRevision);
    }

    [Fact]
    public void R2_J_DifferentPids_MayEachHoldIndependentLiveChannels()
    {
        var registry = new WebChannelRegistry();

        Assert.True(registry.TryConnect(SamplePid, out long first));
        Assert.True(registry.TryConnect(OtherPid, out long second));

        Assert.NotEqual(first, second);
        Assert.True(registry.TryGetCurrentEvidence(first, out _));
        Assert.True(registry.TryGetCurrentEvidence(second, out _));
    }

    [Fact]
    public void R2_K_RetiredChannelId_NeverBecomesCurrentAgain()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long channelId));
        Assert.True(registry.TryDisconnect(channelId));
        // A later, unrelated connect (different PID, so it cannot collide with the live-PID
        // refusal in R2_C) must never be able to observe or reuse the retired ChannelId's state.
        Assert.True(registry.TryConnect(OtherPid, out long unrelatedChannelId));

        Assert.NotEqual(channelId, unrelatedChannelId);
        Assert.False(registry.TryGetCurrentEvidence(channelId, out _));
    }

    [Fact]
    public void R2_BrowserProcessIdZero_ConnectionIsRefused()
    {
        var registry = new WebChannelRegistry();

        Assert.False(registry.TryConnect(0, out long channelId));
        Assert.Equal(0, channelId);
    }

    // ==================================================================
    // Gate 031F2.1 retention correction -- T1/T2 (T2 folded into R2_F above)/T3/T4.
    // ==================================================================

    [Fact]
    public void R2_T1_EveryOperationFailsClosedOnADisconnectedChannelId()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long channelId));
        Assert.True(registry.TryDisconnect(channelId));

        Assert.False(registry.TryGetCurrentEvidence(channelId, out _));
        Assert.False(registry.TryInvalidate(channelId, out _));
        Assert.False(registry.TryAssert(channelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out _));
        Assert.False(registry.TryDisconnect(channelId));
    }

    // Reflection into the private backing field, deliberately -- Gate 031F2.1 explicitly forbids
    // adding a public production API solely to observe this, matching this project's own
    // established convention for exactly this kind of internal-state assertion (e.g.
    // TargetGateTests reading production source text, Gate031_WebAuthorizationContractTests
    // reflecting over WebForegroundEvidence's shape).
    private static int GetActiveChannelCount(WebChannelRegistry registry)
    {
        var field = typeof(WebChannelRegistry).GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance);
        var dictionary = (System.Collections.ICollection)field!.GetValue(registry)!;
        return dictionary.Count;
    }

    [Fact]
    public void R2_T3_RepeatedConnectDisconnectCycles_NeverAccumulateInactiveEntries()
    {
        var registry = new WebChannelRegistry();
        Assert.Equal(0, GetActiveChannelCount(registry));

        for (uint pid = 1; pid <= 50; pid++)
        {
            Assert.True(registry.TryConnect(pid, out long channelId), $"connect failed for synthetic pid {pid}.");
            Assert.True(registry.TryAssert(channelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out _));
            Assert.True(registry.TryDisconnect(channelId));

            Assert.Equal(0, GetActiveChannelCount(registry));
        }
    }

    [Fact]
    public void R2_T4_DisconnectingOnePid_NeverAffectsAnotherStillLivePid()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long channelId));
        Assert.True(registry.TryConnect(OtherPid, out long otherChannelId));

        Assert.True(registry.TryDisconnect(channelId));

        Assert.Equal(1, GetActiveChannelCount(registry));
        Assert.True(registry.TryGetCurrentEvidence(otherChannelId, out _), "the still-live PID's channel must remain fully usable after an unrelated PID disconnects.");
        Assert.False(registry.TryGetCurrentEvidence(channelId, out _));
    }

    // ==================================================================
    // R3 -- CHALLENGE_BOUND_PROOF_GREEN (Gate 031F3). Real behavioral tests against the actual
    // production WebChallengeProof / WebDecisionContext / WebTargetGate.Match -- no reflection, no
    // bool anywhere. ChallengeConfirmed has been removed entirely from production; every case
    // below exercises its real replacement, the four-field three-way agreement (Gate 031F3
    // section 2/7): proof must agree with BOTH evidence and the live decision context on
    // ChannelId, EvidenceRevision, and ForegroundEpoch, and with both evidence and the native
    // foreground snapshot on BrowserProcessId.
    // ==================================================================

    private static WebForegroundEvidence R3ValidEvidence() =>
        new(BrowserProcessId: 100, ChannelId: 1, EvidenceRevision: new RevisionId(1),
            BrowserFocus: BrowserFocus.Focused, OriginResolution: OriginResolution.Resolved,
            Origin: "https://chatgpt.com", ForegroundEpoch: 1);

    private static WebChallengeProof R3ValidProof() =>
        new(ChannelId: 1, EvidenceRevision: new RevisionId(1), ForegroundEpoch: 1, BrowserProcessId: 100);

    private static WebDecisionContext R3ValidContext(WebChallengeProof? proof) =>
        new(CurrentRevision: new RevisionId(1), CurrentChannelId: 1, CurrentForegroundEpoch: 1, ChallengeProof: proof);

    [Fact]
    public void R3_A_NullProof_IsOutside()
    {
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), R3ValidContext(null));
        Assert.Null(result);
    }

    [Fact]
    public void R3_B_ProofChannelIdMismatchAgainstEvidence_IsOutside()
    {
        var proof = R3ValidProof() with { ChannelId = 2 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), R3ValidContext(proof));
        Assert.Null(result);
    }

    [Fact]
    public void R3_C_ProofChannelIdMismatchAgainstCurrentContext_IsOutside()
    {
        var evidence = R3ValidEvidence(); // ChannelId = 1
        var proof = R3ValidProof(); // ChannelId = 1 -- matches evidence
        var context = R3ValidContext(proof) with { CurrentChannelId = 2 }; // the live state moved on
        var result = WebTargetGate.Match(ChromeSnapshot(100), evidence, context);
        Assert.Null(result);
    }

    [Fact]
    public void R3_D_ProofRevisionMismatchAgainstEvidence_IsOutside()
    {
        var proof = R3ValidProof() with { EvidenceRevision = new RevisionId(2) };
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), R3ValidContext(proof));
        Assert.Null(result);
    }

    [Fact]
    public void R3_E_ProofRevisionMismatchAgainstCurrentContext_IsOutside()
    {
        var evidence = R3ValidEvidence();
        var proof = R3ValidProof(); // matches evidence's revision (1)
        var context = R3ValidContext(proof) with { CurrentRevision = new RevisionId(2) };
        var result = WebTargetGate.Match(ChromeSnapshot(100), evidence, context);
        Assert.Null(result);
    }

    [Fact]
    public void R3_F_ProofEpochMismatchAgainstEvidence_IsOutside()
    {
        var proof = R3ValidProof() with { ForegroundEpoch = 2 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), R3ValidContext(proof));
        Assert.Null(result);
    }

    [Fact]
    public void R3_G_ProofEpochMismatchAgainstCurrentContext_IsOutside()
    {
        var evidence = R3ValidEvidence();
        var proof = R3ValidProof(); // matches evidence's epoch (1)
        var context = R3ValidContext(proof) with { CurrentForegroundEpoch = 2 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), evidence, context);
        Assert.Null(result);
    }

    [Fact]
    public void R3_H_ProofBrowserProcessIdMismatchAgainstEvidence_IsOutside()
    {
        var proof = R3ValidProof() with { BrowserProcessId = 999 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), R3ValidContext(proof));
        Assert.Null(result);
    }

    [Fact]
    public void R3_I_ProofBrowserProcessIdMismatchAgainstForegroundSnapshot_IsOutside()
    {
        var evidence = R3ValidEvidence(); // BrowserProcessId = 100
        var proof = R3ValidProof(); // BrowserProcessId = 100 -- matches evidence
        var differentForeground = ChromeSnapshot(200); // the real OS foreground process differs
        var result = WebTargetGate.Match(differentForeground, evidence, R3ValidContext(proof));
        Assert.Null(result);
    }

    [Fact]
    public void R3_J_ProofMatchesEvidenceButCurrentContextHasChanged_IsOutside()
    {
        var evidence = R3ValidEvidence();
        var proof = R3ValidProof(); // agrees with evidence on all four fields
        var context = new WebDecisionContext(
            CurrentRevision: new RevisionId(99), CurrentChannelId: 999, CurrentForegroundEpoch: 999, ChallengeProof: proof);
        var result = WebTargetGate.Match(ChromeSnapshot(100), evidence, context);
        Assert.Null(result);
    }

    [Fact]
    public void R3_K_ProofMatchesCurrentContextButEvidenceDiffers_IsOutside()
    {
        var proof = R3ValidProof(); // agrees with the context below on all four fields
        var context = R3ValidContext(proof);
        var staleEvidence = R3ValidEvidence() with { ChannelId = 777 }; // evidence has not caught up
        var result = WebTargetGate.Match(ChromeSnapshot(100), staleEvidence, context);
        Assert.Null(result);
    }

    [Fact]
    public void R3_L_AllProofEvidenceContextForegroundFieldsAgree_AuthorizesWhenEveryOtherTermIsValid()
    {
        var evidence = R3ValidEvidence();
        var proof = R3ValidProof();
        var context = R3ValidContext(proof);

        var result = WebTargetGate.Match(ChromeSnapshot(100), evidence, context);

        Assert.Equal(SupportedWebTarget.ChatGptWeb, result);
    }

    [Fact]
    public void R3_WebChallengeProof_HasExactlyTheFourFrozenFields()
    {
        var actual = typeof(WebChallengeProof).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var expected = new HashSet<string>(StringComparer.Ordinal) { "ChannelId", "EvidenceRevision", "ForegroundEpoch", "BrowserProcessId" };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void R3_WebChallengeProof_CannotExpressNonceTimestampPayloadOrProductFields()
    {
        // Deliberately excludes "Browser" as a bare substring -- BrowserProcessId is one of the
        // four FROZEN required fields, not a forbidden one; a plain substring match would
        // incorrectly flag it. "Product"/"Success" remain narrow enough not to collide.
        var forbidden = new[] { "Nonce", "Timestamp", "Payload", "Origin", "Product", "Expiry", "Success" };

        var offending = typeof(WebChallengeProof).GetProperties().Select(p => p.Name)
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offending);
    }

    // ==================================================================
    // R5 -- INVALIDATE_BEFORE_ASSERT_GREEN (Gate 031F2). Real behavioral tests against
    // WebChannelRegistry. Per Gate 031F2 section 17, production does not need a separate API per
    // browser event -- every "ordinary" label below maps to the SAME mechanical
    // WebChannelRegistry.TryInvalidate transition; only the two terminal labels ("channel
    // disconnect", "browser process exit") map to TryDisconnect instead, which per section 16 need
    // not allow any subsequent assert at all. The event labels themselves live only in this test
    // data, never in production.
    // ==================================================================

    public static IEnumerable<object[]> InvalidateBeforeAssertEvents() =>
    [
        ["active tab changed", false],
        ["navigation committed", false],
        ["SPA/history state change", false],
        ["browser focused-window changed", false],
        ["window closed", false],
        ["extension startup (fresh channel)", false],
        ["extension reload (semantic reset)", false],
        ["channel disconnect", true],
        ["browser process exit", true],
    ];

    [Theory]
    [MemberData(nameof(InvalidateBeforeAssertEvents))]
    public void R5_InvalidateBeforeAssert_EachEvent_MatchesFrozenTransition(string eventName, bool isTerminalEvent)
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long channelId), $"R5 ({eventName}): setup connect failed.");
        Assert.True(registry.TryAssert(channelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out var asserted),
            $"R5 ({eventName}): setup assert failed.");
        long assertedRevision = asserted.EvidenceRevision.Value;

        if (isTerminalEvent)
        {
            Assert.True(registry.TryDisconnect(channelId), $"R5 ({eventName}): disconnect itself must succeed on a live channel.");
            Assert.False(registry.TryAssert(channelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out _),
                $"R5 ({eventName}): no assert may ever succeed after this terminal event.");
            Assert.False(registry.TryGetCurrentEvidence(channelId, out _),
                $"R5 ({eventName}): evidence must be immediately unreadable after this terminal event.");
            return;
        }

        Assert.True(registry.TryInvalidate(channelId, out var invalidated), $"R5 ({eventName}): invalidate must succeed on a live asserted channel.");
        Assert.Equal(assertedRevision + 1, invalidated.EvidenceRevision.Value);
        Assert.Equal(BrowserFocus.Unresolved, invalidated.BrowserFocus);
        Assert.Equal(OriginResolution.Unresolved, invalidated.OriginResolution);
        Assert.Null(invalidated.Origin);

        // No stale Asserted state survives this event: a caller reading current evidence right
        // now sees Unresolved, never the pre-event asserted facts.
        Assert.True(registry.TryGetCurrentEvidence(channelId, out var readBack));
        Assert.Equal(invalidated, readBack);

        Assert.True(registry.TryAssert(channelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out var reasserted),
            $"R5 ({eventName}): a fresh assert must be allowed immediately after this event's own invalidate.");
        Assert.Equal(assertedRevision + 2, reasserted.EvidenceRevision.Value);
        Assert.Equal(BrowserFocus.Focused, reasserted.BrowserFocus);
        Assert.Equal(OriginResolution.Resolved, reasserted.OriginResolution);
        Assert.Equal(SampleOrigin, reasserted.Origin);
    }

    [Fact]
    public void R5_DirectAssertedToAsserted_IsRejectedWithoutAnInterveningInvalidate()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long channelId));
        Assert.True(registry.TryAssert(channelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out var first));

        bool secondAssertSucceeded = registry.TryAssert(channelId, BrowserFocus.Focused, OriginResolution.Unsupported, null, out _);

        Assert.False(secondAssertSucceeded,
            "R5: a second StateAssert directly after an already-Asserted state must be rejected -- an intervening Invalidate is structurally required first.");
        Assert.True(registry.TryGetCurrentEvidence(channelId, out var current));
        Assert.Equal(first, current);
    }

    [Fact]
    public void R5_FirstAssertAfterConnect_IsAllowedWithoutAPriorInvalidate()
    {
        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(SamplePid, out long channelId));

        bool firstAssertSucceeded = registry.TryAssert(channelId, BrowserFocus.Focused, OriginResolution.Resolved, SampleOrigin, out var evidence);

        Assert.True(firstAssertSucceeded, "R5: the very first assert on a freshly connected (Unresolved) channel must be allowed directly.");
        Assert.Equal(OriginResolution.Resolved, evidence.OriginResolution);
    }

    // ==================================================================
    // R6 -- WEB_COMPOSER_EXCLUSION_RED (Gate 031E2 / B2), corrected per Gate 031D2.1, migrated to
    // GREEN per Gate 031F5C section 19.
    //
    // The real ClipboardPrivacyCoordinator is executed end to end. The WEB PRECONDITION step below
    // proves -- via a genuine call to the real, unmodified WebTargetGate.Match -- that the exact
    // browser foreground snapshot this test feeds the coordinator IS already a recognized Web
    // target under current production policy. The coordinator is now constructed with a
    // deterministic Phase-D IWebClipboardAuthorizationSource injected through the frozen optional
    // trailing constructor parameter, yielding a WebClipboardAuthorization bound to exactly this
    // matched target -- the ONLY change from this test's original setup (Gate 031F5C section 19:
    // "DO NOT change production default to satisfy R6... inject the deterministic Phase-D fake").
    // The coordinator's own production default (no source) still fails closed to UnauthorizedTarget
    // for this same snapshot -- see D_R7_NoWebAuthorizationSourceInjected_BrowserTarget_
    // ProducesUnauthorizedTarget in Gate031F5B_PhaseDCoordinatorWebRedTests.cs, which is what
    // proves the original defect this test exposed was real and is still guarded against.
    // ==================================================================
    [Fact]
    public async Task R6_WebAuthorizedAttempt_CoordinatorHasNoRoutingToReachVerifiedWebSuccess()
    {
        // ---- WEB PRECONDITION: real WebTargetGate.Match call, real production types ----
        var browserForeground = new ForegroundTargetSnapshot(
            IsResolved: true, ProcessId: 100, ProcessName: "chrome",
            PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
            ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Google LLC");
        var evidence = new WebForegroundEvidence(
            BrowserProcessId: 100, ChannelId: 1, EvidenceRevision: new RevisionId(1),
            BrowserFocus: BrowserFocus.Focused, OriginResolution: OriginResolution.Resolved,
            Origin: "https://chatgpt.com", ForegroundEpoch: 1);
        var proof = new WebChallengeProof(evidence.ChannelId, evidence.EvidenceRevision, evidence.ForegroundEpoch, evidence.BrowserProcessId);
        var context = new WebDecisionContext(
            CurrentRevision: evidence.EvidenceRevision, CurrentChannelId: evidence.ChannelId,
            CurrentForegroundEpoch: evidence.ForegroundEpoch, ChallengeProof: proof);

        SupportedWebTarget? webMatch = WebTargetGate.Match(browserForeground, evidence, context);
        Assert.True(webMatch is not null,
            "R6 precondition failed: this exact (browserForeground, evidence, context) triple must " +
            "already be recognized by the real, current WebTargetGate.Match -- if this fails, the " +
            "test setup itself is wrong, not the coordinator.");
        Assert.Equal(SupportedWebTarget.ChatGptWeb, webMatch);

        // ---- REAL COORDINATOR, fully wired for a successful verified write ----
        var transport = new FakeClipboardReadTransport();
        var targetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = browserForeground };
        var processor = new FakeClipboardPrivacyProcessor { WritePlanToReturn = new ClipboardWritePlan("[protected]") };
        var writeTransport = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 1) };
        var notificationLifecycle = new FakeClipboardNotificationLifecycle();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();
        var operationGate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var verificationInvalidation = new FakeClipboardComposerVerificationInvalidation();
        var diagnostics = new FakeClipboardDiagnosticRecorder();
        var webAuthorizationSource = new FakeWebClipboardAuthorizationSource
        {
            NextResult = new WebClipboardAuthorization(webMatch.Value, new StubFreshness()),
        };

        var coordinator = new ClipboardPrivacyCoordinator(
            transport, targetCapture, processor, writeTransport, notificationLifecycle,
            decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation,
            diagnostics: diagnostics, webAuthorizationSource: webAuthorizationSource);
        coordinator.Start();

        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "raw text"));
        transport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));

        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);
        await WaitUntilAsync(() => diagnostics.Events.Any(e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal));
        await Task.Delay(50); // settle window -- an authorized attempt would still be mid-pipeline here
        coordinator.Stop();

        // ---- CURRENT ACTUAL: the coordinator's OWN existing gate rejected this attempt before
        // any clipboard read/write/verification was ever attempted ----
        var terminalEvents = diagnostics.Events.Where(e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal).ToList();
        var actualTerminalReasons = terminalEvents.Select(e => e.Terminal).ToList();

        // ---- FUTURE REQUIRED BEHAVIOR (Gate 031E2 B2_EXCLUSION), asserted as real, executable
        // checks against the real fakes the real coordinator actually drove ----
        Assert.Contains(ClipboardDiagnosticTerminalReason.Success, actualTerminalReasons);
        Assert.Equal(1, notificationLifecycle.CompleteEvaluationCallCount);
        Assert.Equal(0, notificationLifecycle.AbandonEvaluationCallCount);
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    private sealed class StubFreshness : IClipboardAuthorizationFreshness
    {
        public bool IsStillCurrent() => true;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(10);
        }
    }

    // ==================================================================
    // R8 -- DEFAULT_ZERO_IDENTIFIER_FAIL_CLOSED_GREEN (Gate 031F3 section 5). Real behavioral
    // tests against the REAL, corrected WebTargetGate.Match. Each case constructs an OTHERWISE
    // fully valid Web authorization attempt (see R3ValidEvidence/R3ValidProof/R3ValidContext
    // above) and independently zeroes exactly one identifier -- proving the zero-rejection is
    // structural (checked before any equality involving that identifier), not incidentally implied
    // by some other already-invalid field.
    // ==================================================================

    private static ForegroundTargetSnapshot ChromeSnapshot(uint processId) =>
        new(IsResolved: true, ProcessId: processId, ProcessName: "chrome",
            PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
            ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Google LLC");

    [Fact]
    public void R8_1_ForegroundProcessIdZero_MustNotAuthorize()
    {
        var result = WebTargetGate.Match(ChromeSnapshot(0), R3ValidEvidence(), R3ValidContext(R3ValidProof()));
        Assert.Null(result);
    }

    [Fact]
    public void R8_2_EvidenceBrowserProcessIdZero_MustNotAuthorize()
    {
        var evidence = R3ValidEvidence() with { BrowserProcessId = 0 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), evidence, R3ValidContext(R3ValidProof()));
        Assert.Null(result);
    }

    [Fact]
    public void R8_3_ProofBrowserProcessIdZero_MustNotAuthorize()
    {
        var proof = R3ValidProof() with { BrowserProcessId = 0 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), R3ValidContext(proof));
        Assert.Null(result);
    }

    [Fact]
    public void R8_4_EvidenceChannelIdZero_MustNotAuthorize()
    {
        var evidence = R3ValidEvidence() with { ChannelId = 0 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), evidence, R3ValidContext(R3ValidProof()));
        Assert.Null(result);
    }

    [Fact]
    public void R8_5_ContextCurrentChannelIdZero_MustNotAuthorize()
    {
        var context = R3ValidContext(R3ValidProof()) with { CurrentChannelId = 0 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), context);
        Assert.Null(result);
    }

    [Fact]
    public void R8_6_ProofChannelIdZero_MustNotAuthorize()
    {
        var proof = R3ValidProof() with { ChannelId = 0 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), R3ValidContext(proof));
        Assert.Null(result);
    }

    [Fact]
    public void R8_7_EvidenceForegroundEpochZero_MustNotAuthorize()
    {
        var evidence = R3ValidEvidence() with { ForegroundEpoch = 0 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), evidence, R3ValidContext(R3ValidProof()));
        Assert.Null(result);
    }

    [Fact]
    public void R8_8_ContextCurrentForegroundEpochZero_MustNotAuthorize()
    {
        var context = R3ValidContext(R3ValidProof()) with { CurrentForegroundEpoch = 0 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), context);
        Assert.Null(result);
    }

    [Fact]
    public void R8_9_ProofForegroundEpochZero_MustNotAuthorize()
    {
        var proof = R3ValidProof() with { ForegroundEpoch = 0 };
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), R3ValidContext(proof));
        Assert.Null(result);
    }

    [Fact]
    public void R8_DefaultForegroundEvidence_IsOutside()
    {
        var result = WebTargetGate.Match(ChromeSnapshot(100), default, R3ValidContext(R3ValidProof()));
        Assert.Null(result);
    }

    [Fact]
    public void R8_DefaultChallengeProofUsedAsNonNullProof_MustNotAuthorize()
    {
        // default(WebChallengeProof) has ChannelId=0, EvidenceRevision=RevisionId.None(0),
        // ForegroundEpoch=0, BrowserProcessId=0 -- explicitly wrapped as a non-null nullable proof
        // (never relying on the null-proof path, which is R3_A's own separate case) so this test
        // fails specifically because the DEFAULT PROOF'S OWN identifiers are zero.
        WebChallengeProof? defaultProofAsNonNull = default(WebChallengeProof);
        var result = WebTargetGate.Match(ChromeSnapshot(100), R3ValidEvidence(), R3ValidContext(defaultProofAsNonNull));
        Assert.Null(result);
    }
}
