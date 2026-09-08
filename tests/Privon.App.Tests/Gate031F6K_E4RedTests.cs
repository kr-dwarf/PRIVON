using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Text;
using Privon.Browser;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031F6K -- Phase E4 RED suite: the decision-time ChallengeRequest/ChallengeResponse
// exchange. FROZEN E4 CONTRACT per the Gate 031F6K prompt -- this file creates the complete executable
// RED contract for it. NO production code is added by this gate.
//
// Unlike Gate031F6H (E3), almost every dependency this file needs already exists and compiles today:
// WebChannelSession/WebChannelManager/WebChannelRegistry/WebSessionAdmissionBudget/
// IWebBrowserHostBinding/WebFrameDecoder/WebFrameEncoder/WebNativeMessagingPump/WebProtocolMessage/
// IWebClipboardAuthorizationSource/WebClipboardAuthorization/WebClipboardAuthorizationFreshness/
// WebDecisionContext/WebTargetGate/WebChallengeProof/IForegroundEpochSource/IForegroundTargetCapture/
// ForegroundTargetSnapshot/BrowserFocus/OriginResolution/SupportedWebTarget are all frozen, GREEN E1-E3
// production types, referenced directly here (Privon.App grants InternalsVisibleTo to this project).
//
// Exactly FOUR surfaces are genuinely new for E4 and do not exist in production yet -- these, and only
// these, are located by reflection so this file still compiles today:
//   - Privon.App.WebChallengeOutcome            (enum)
//   - Privon.App.WebChallengeResult              (record struct)
//   - Privon.App.WebChannelSession.ChallengeAsync(TimeSpan)   (additive instance method)
//   - Privon.App.WebClipboardAuthorizationSource  (concrete class)
// Every test below locates whichever of these it needs, fails promptly (EXPECTED_E4_RED) with a
// descriptive message when missing, and otherwise drives REAL behavior against the real, already-GREEN
// E1-E3 stack -- so every test begins passing, unmodified, the instant a correct E4 GREEN
// implementation lands.
//
// HANG SAFETY: any test that expects an outbound frame in response to a ChallengeAsync/AuthorizeAsync
// call races that read against the initiating task's own completion (see
// ReadFrameOrSurfaceChallengeFaultAsync below) -- in the current RED state the initiating call fails
// synchronously (before any I/O) the moment its reflection lookup fails, so the race resolves
// immediately and no test ever blocks waiting for a frame nothing will ever write.
public class Gate031F6K_E4RedTests
{
    private static Assembly AppAssembly => typeof(WebChannelSession).Assembly;
    private static Type? GetAppType(string name) => AppAssembly.GetType(name);

    private const string OutcomeTypeName = "Privon.App.WebChallengeOutcome";
    private const string ResultTypeName = "Privon.App.WebChallengeResult";
    private const string SourceTypeName = "Privon.App.WebClipboardAuthorizationSource";

    private static Type? OutcomeType => GetAppType(OutcomeTypeName);
    private static Type? ResultType => GetAppType(ResultTypeName);
    private static Type? SourceType => GetAppType(SourceTypeName);

    private const string E4Contract =
        "Gate 031F6K E4 (frozen contract, RED). Required additive surfaces (none exist yet): " +
        "internal enum Privon.App.WebChallengeOutcome { Success, Busy, NotAccepted, WriteFailed, " +
        "TimedOut, SessionClosed }; internal readonly record struct Privon.App.WebChallengeResult(" +
        "WebChallengeOutcome Outcome, BrowserFocus Focus, OriginResolution OriginResolution, string? " +
        "Origin); internal Task<WebChallengeResult> WebChannelSession.ChallengeAsync(TimeSpan timeout); " +
        "internal sealed class Privon.App.WebClipboardAuthorizationSource : IWebClipboardAuthorizationSource " +
        "with public static TimeSpan ChallengeTimeoutContractDefault = TimeSpan.FromSeconds(1) and a " +
        "constructor accepting (WebChannelManager, WebChannelRegistry, IForegroundTargetCapture, " +
        "IForegroundEpochSource, optional TimeSpan challengeTimeout). The frozen " +
        "IWebClipboardAuthorizationSource.AuthorizeAsync(ForegroundTargetSnapshot) signature itself is " +
        "unchanged from E3 -- see that interface's own doc.";

    // ==================================================================
    // SHAPE -- freezes the exact/additive E4 surface. Every one of these fails EXPECTED_E4_RED today.
    // ==================================================================

    [Fact]
    public void Shape_WebChallengeOutcome_HasExactlyTheFrozenSixValues()
    {
        Assert.True(OutcomeType is not null, E4Contract);
        Assert.True(OutcomeType!.IsEnum, $"{OutcomeTypeName} must be an enum. " + E4Contract);

        var names = Enum.GetNames(OutcomeType).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var expected = new[] { "Busy", "NotAccepted", "SessionClosed", "Success", "TimedOut", "WriteFailed" };
        Assert.Equal(expected, names);
    }

    [Fact]
    public void Shape_WebChallengeResult_IsAFourFieldRecordStruct_InTheFrozenOrderAndTypes()
    {
        Assert.True(ResultType is not null, E4Contract);

        var properties = ResultType!.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var names = properties.Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Outcome", "Focus", "OriginResolution", "Origin" }, names);

        Assert.Equal(typeof(BrowserFocus), ResultType.GetProperty("Focus")!.PropertyType);
        Assert.Equal(typeof(OriginResolution), ResultType.GetProperty("OriginResolution")!.PropertyType);
        Assert.Equal(typeof(string), ResultType.GetProperty("Origin")!.PropertyType);
    }

    [Fact]
    public void Shape_WebChannelSession_ExposesChallengeAsyncTimeSpan_ReturningATask()
    {
        var method = FindChallengeAsyncMethod();
        Assert.True(method is not null,
            "Privon.App.WebChannelSession exposes no public ChallengeAsync(TimeSpan) method yet. " + E4Contract);
        Assert.True(typeof(Task).IsAssignableFrom(method!.ReturnType),
            "ChallengeAsync must return a Task<WebChallengeResult>. " + E4Contract);
    }

    [Fact]
    public void Shape_WebClipboardAuthorizationSource_IsInternalSealed_AndImplementsTheFrozenInterface()
    {
        Assert.True(SourceType is not null, E4Contract);
        Assert.True(SourceType!.IsSealed, $"{SourceTypeName} must be sealed. " + E4Contract);
        Assert.False(SourceType.IsPublic, $"{SourceTypeName} must be internal, not public. " + E4Contract);
        Assert.Contains(typeof(IWebClipboardAuthorizationSource), SourceType.GetInterfaces());
    }

    [Fact]
    public void Shape_WebClipboardAuthorizationSource_ChallengeTimeoutContractDefault_Is1Second()
    {
        Assert.True(SourceType is not null, E4Contract);

        var property = SourceType!.GetProperty("ChallengeTimeoutContractDefault", BindingFlags.Public | BindingFlags.Static);
        var field = SourceType.GetField("ChallengeTimeoutContractDefault", BindingFlags.Public | BindingFlags.Static);
        Assert.True(property is not null || field is not null,
            $"{SourceTypeName} exposes no static ChallengeTimeoutContractDefault member. " + E4Contract);

        object? value = property is not null ? property.GetValue(null) : field!.GetValue(null);
        Assert.Equal(TimeSpan.FromSeconds(1), value);
    }

    [Fact]
    public void Shape_WebClipboardAuthorizationSource_ConstructorMatchesTheFrozenDependencyShape()
    {
        Assert.True(SourceType is not null, E4Contract);

        var ctors = SourceType!.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        bool hasFour = ctors.Any(c => ParametersMatch(c,
            typeof(WebChannelManager), typeof(WebChannelRegistry), typeof(IForegroundTargetCapture), typeof(IForegroundEpochSource)));
        bool hasFiveWithTimeout = ctors.Any(c =>
        {
            var p = c.GetParameters();
            return p.Length == 5
                && p[0].ParameterType == typeof(WebChannelManager)
                && p[1].ParameterType == typeof(WebChannelRegistry)
                && p[2].ParameterType == typeof(IForegroundTargetCapture)
                && p[3].ParameterType == typeof(IForegroundEpochSource)
                && p[4].ParameterType == typeof(TimeSpan);
        });

        Assert.True(hasFour || hasFiveWithTimeout,
            $"{SourceTypeName} exposes no constructor matching (WebChannelManager, WebChannelRegistry, " +
            "IForegroundTargetCapture, IForegroundEpochSource, optional TimeSpan). " + E4Contract);
    }

    [Fact]
    public void Shape_NoNewIWebChannelChallengeSessionInterfaceIsRequiredOrIntroduced()
    {
        // Documentation lock, not a production requirement either way -- Gate 031F6K explicitly says a
        // new IWebChannelChallengeSession interface is never required. Proves this RED suite itself
        // never accidentally started depending on one existing (ALREADY_GREEN_CONTROL: trivially true
        // today and stays true unless that rule is violated).
        Assert.Null(GetAppType("Privon.App.IWebChannelChallengeSession"));
    }

    private static bool ParametersMatch(ConstructorInfo ctor, params Type[] types)
    {
        var parameters = ctor.GetParameters();
        if (parameters.Length != types.Length)
            return false;
        for (int i = 0; i < types.Length; i++)
        {
            if (parameters[i].ParameterType != types[i])
                return false;
        }
        return true;
    }

    // ==================================================================
    // TEST INFRASTRUCTURE
    // ==================================================================

    private static MethodInfo? FindChallengeAsyncMethod() =>
        typeof(WebChannelSession).GetMethod("ChallengeAsync", BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: [typeof(TimeSpan)], modifiers: null);

    private readonly record struct ChallengeInvocation(string Outcome, BrowserFocus Focus, OriginResolution OriginResolution, string? Origin);

    /// <summary>Invokes the future ChallengeAsync(TimeSpan) via reflection and unwraps its
    /// Task&lt;WebChallengeResult&gt; result into a compile-time-typed shape. The FIRST thing this does
    /// is the existence assertion -- so in RED state, the Task this returns is already faulted before
    /// any transport I/O occurs (see this file's own HANG SAFETY doc).</summary>
    private static async Task<ChallengeInvocation> InvokeChallengeAsync(WebChannelSession session, TimeSpan timeout)
    {
        var method = FindChallengeAsyncMethod();
        Assert.True(method is not null,
            "Privon.App.WebChannelSession exposes no ChallengeAsync(TimeSpan) method yet. " + E4Contract);

        object? invokeResult = method!.Invoke(session, [timeout]);
        Assert.True(invokeResult is Task, "WebChannelSession.ChallengeAsync must return a Task<WebChallengeResult>. " + E4Contract);

        var task = (Task)invokeResult!;
        await task.ConfigureAwait(false);

        var resultProperty = task.GetType().GetProperty("Result");
        Assert.True(resultProperty is not null, "ChallengeAsync must return Task<WebChallengeResult>, not a bare Task. " + E4Contract);
        object result = resultProperty!.GetValue(task)!;
        var resultType = result.GetType();

        var outcomeProperty = resultType.GetProperty("Outcome");
        var focusProperty = resultType.GetProperty("Focus");
        var originResolutionProperty = resultType.GetProperty("OriginResolution");
        var originProperty = resultType.GetProperty("Origin");
        Assert.True(
            outcomeProperty is not null && focusProperty is not null && originResolutionProperty is not null && originProperty is not null,
            $"{ResultTypeName} exists but is missing one of the frozen Outcome/Focus/OriginResolution/Origin members. " + E4Contract);

        return new ChallengeInvocation(
            outcomeProperty!.GetValue(result)!.ToString()!,
            (BrowserFocus)focusProperty!.GetValue(result)!,
            (OriginResolution)originResolutionProperty!.GetValue(result)!,
            (string?)originProperty!.GetValue(result));
    }

    private static object CreateAuthorizationSource(
        WebChannelManager manager, WebChannelRegistry registry, IForegroundTargetCapture capture,
        IForegroundEpochSource epochSource, TimeSpan? challengeTimeout)
    {
        Assert.True(SourceType is not null, E4Contract);

        var ctors = SourceType!.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        int wantedLength = challengeTimeout is null ? 4 : 5;
        var ctor = ctors.FirstOrDefault(c => c.GetParameters().Length == wantedLength);
        Assert.True(ctor is not null,
            $"{SourceTypeName} exposes no constructor accepting {wantedLength} arguments matching the frozen dependency shape. " + E4Contract);

        object?[] args = challengeTimeout is null
            ? [manager, registry, capture, epochSource]
            : [manager, registry, capture, epochSource, challengeTimeout.Value];

        object? instance = ctor!.Invoke(args);
        Assert.True(instance is not null, $"{SourceTypeName} could not be constructed.");
        return instance!;
    }

    private sealed class FakeBrowserHostBinding(uint pid) : IWebBrowserHostBinding
    {
        public uint BrowserProcessId { get; } = pid;
        public string? BrowserProcessName => "chrome";
        public PackageIdentityResolution BrowserPackageIdentity => PackageIdentityResolution.NoPackage;
        public ExecutableSignatureResolution BrowserExecutableSignature => ExecutableSignatureResolution.Trusted;
        public string? BrowserSignerOrganization => "Google LLC";
        public bool Disposed { get; private set; }
        public RetainedProcessLiveness Liveness { get; set; } = RetainedProcessLiveness.Alive;

        public RetainedProcessLiveness CheckLiveness() => Liveness;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeEpochSource : IForegroundEpochSource
    {
        public long Epoch { get; set; } = 1;
        public long CurrentEpoch => Epoch;
    }

    private static ForegroundTargetSnapshot SupportedChromeSnapshot(uint pid) =>
        new(IsResolved: true, ProcessId: pid, ProcessName: "chrome",
            PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
            ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Google LLC");

    private sealed record TestSession(
        WebChannelSession Session, ScriptedSessionTransport Transport, WebChannelRegistry Registry,
        WebChannelManager Manager, FakeBrowserHostBinding Binding, long ChannelId);

    /// <summary>Drives a REAL Hello/HelloAck handshake over the real, already-GREEN E3
    /// WebChannelSession/WebChannelManager/WebChannelRegistry/WebSessionAdmissionBudget stack -- no
    /// reflection needed here at all, since every one of these types is a frozen, compile-time-visible
    /// E1-E3 production type.</summary>
    private static async Task<TestSession> CreateAcceptedSessionAsync(uint pid, WebChannelRegistry? registry = null, WebChannelManager? manager = null)
    {
        registry ??= new WebChannelRegistry();
        manager ??= new WebChannelManager(registry);
        var budget = new WebSessionAdmissionBudget(8);
        var lease = await budget.AcquireAsync();
        var binding = new FakeBrowserHostBinding(pid);
        var transport = new ScriptedSessionTransport();
        var session = new WebChannelSession(transport, binding, registry, lease, manager);

        session.Start();
        await transport.WriteFromBrowserAsync(HelloFrame());
        // Gate E5F carry-forward (harness correction, "first reuse" boundary): bounded like every
        // other outbound-frame read in this file (AwaitBounded/ReadFrameOrSurfaceChallengeFaultAsync)
        // -- previously a bare, unbounded await, so a genuine regression in the handshake path would
        // hang this helper (and every test built on it) forever instead of failing with a clear
        // timeout message. No production behavior changed by this fix.
        byte[] ackFrame = await AwaitBounded(ReadFullFrameAsync(transport), "the HelloAck frame during accepted-session test setup");
        Assert.Equal(WebFrameDecodeStatus.Ok, WebFrameDecoder.Decode(ackFrame, out var ackMessage));
        Assert.Equal(WebProtocolMessageType.HelloAck, ackMessage.Type);
        Assert.True(ackMessage.Accepted);
        Assert.Equal(WebChannelSessionState.Accepted, session.State);

        return new TestSession(session, transport, registry, manager, binding, session.ChannelId!.Value);
    }

    // ------------------------------------------------------------------
    // Protocol frame helpers -- real WebFrameDecoder/WebFrameEncoder wire shape, hand-built JSON so a
    // deliberately-wrong-shaped ChallengeResponse (G3/G17) can still be constructed.
    // ------------------------------------------------------------------

    private static byte[] LengthPrefixedJson(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
        return [.. length, .. payload];
    }

    private static byte[] HelloFrame() => LengthPrefixedJson("{\"v\":1,\"type\":\"Hello\"}");
    private static byte[] StateInvalidateFrame() => LengthPrefixedJson("{\"v\":1,\"type\":\"StateInvalidate\"}");

    private static byte[] StateAssertFrame(string focus, string originResolution, string? origin) =>
        LengthPrefixedJson(origin is null
            ? $"{{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"{focus}\",\"originResolution\":\"{originResolution}\"}}"
            : $"{{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"{focus}\",\"originResolution\":\"{originResolution}\",\"origin\":\"{origin}\"}}");

    private static byte[] ChallengeResponseFrame(string nonce, string focus, string originResolution, string? origin) =>
        LengthPrefixedJson(origin is null
            ? $"{{\"v\":1,\"type\":\"ChallengeResponse\",\"nonce\":\"{nonce}\",\"focus\":\"{focus}\",\"originResolution\":\"{originResolution}\"}}"
            : $"{{\"v\":1,\"type\":\"ChallengeResponse\",\"nonce\":\"{nonce}\",\"focus\":\"{focus}\",\"originResolution\":\"{originResolution}\",\"origin\":\"{origin}\"}}");

    private static string ValidNonce(byte fill)
    {
        byte[] bytes = new byte[32];
        Array.Fill(bytes, fill);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void AssertValidNonceShape(string nonce)
    {
        Assert.Equal(43, nonce.Length);
        foreach (char c in nonce)
        {
            bool valid = (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c is '-' or '_';
            Assert.True(valid, $"nonce contains a non-Base64URL character: '{c}'");
        }

        Span<char> standard = stackalloc char[44];
        for (int i = 0; i < 43; i++)
            standard[i] = nonce[i] switch { '-' => '+', '_' => '/', var ch => ch };
        standard[43] = '=';

        Span<byte> decoded = stackalloc byte[33];
        Assert.True(Convert.TryFromBase64Chars(standard, decoded, out int written) && written == 32,
            "nonce must decode to exactly 32 bytes.");
    }

    private static string DecodeChallengeRequestNonce(byte[] frame)
    {
        var status = WebFrameDecoder.Decode(frame, out var message);
        Assert.Equal(WebFrameDecodeStatus.Ok, status);
        Assert.Equal(WebProtocolMessageType.ChallengeRequest, message.Type);
        Assert.Null(message.Accepted);
        Assert.Null(message.Focus);
        Assert.Null(message.OriginResolution);
        Assert.Null(message.Origin);
        Assert.NotNull(message.Nonce);
        return message.Nonce!;
    }

    private static async Task<byte[]> ReadFullFrameAsync(ScriptedSessionTransport transport)
    {
        byte[] prefix = await transport.ReadExactFromSessionAsync(4);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        byte[] payload = await transport.ReadExactFromSessionAsync((int)length);
        return [.. prefix, .. payload];
    }

    /// <summary>Races reading one outbound frame against <paramref name="guardTask"/>'s own completion
    /// plus a bounded 5-second deadline (matching this repository's own R48 convention), instead of
    /// awaiting the read unconditionally. In RED state <paramref name="guardTask"/> is already faulted
    /// (missing production member) before this is even called, so the race resolves immediately and
    /// surfaces that real failure -- rather than hanging forever waiting for a frame nothing will ever
    /// write. Once E4 GREEN lands and genuinely writes a frame, the read wins the race exactly as
    /// before and this helper is otherwise transparent.</summary>
    private static async Task<byte[]> ReadFrameOrSurfaceChallengeFaultAsync(ScriptedSessionTransport transport, Task guardTask)
    {
        var readTask = ReadFullFrameAsync(transport);
        var winner = await Task.WhenAny(readTask, guardTask, Task.Delay(TimeSpan.FromSeconds(5)));

        if (winner == guardTask)
        {
            await guardTask; // surfaces the real failure (e.g. EXPECTED_E4_RED missing member).
            Assert.Fail("The challenge/authorization task completed before ever writing the expected outbound frame.");
        }

        Assert.True(winner == readTask, "Timed out waiting for an outbound frame or for the challenge/authorization task to complete.");
        return await readTask;
    }

    private static async Task<T> AwaitBounded<T>(Task<T> task, string context)
    {
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == task, "Timed out waiting for " + context + ".");
        return await task;
    }

    private static async Task AwaitBounded(Task task, string context)
    {
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == task, "Timed out waiting for " + context + ".");
        await task;
    }

    /// <summary>Gate 031F6K.1 determinism correction -- establishes a session's OPENING registry
    /// evidence synchronously and deterministically, via the real, thread-safe
    /// WebChannelRegistry.TryAssert called DIRECTLY (not through the wire protocol), so there is no
    /// async read-loop race to wait out for mere test setup. This is still the real, production
    /// WebChannelRegistry -- the exact same one the session/manager/source all share -- so it exercises
    /// identical state to a wire-level StateAssert; only the delivery mechanism differs. Reserved for
    /// TEST SETUP only: every state transition genuinely UNDER TEST (G8/G9's own mid-challenge
    /// StateInvalidate/StateAssert) still goes through the real session transport/read loop, exactly as
    /// the gate's own G8/G9 contract requires.</summary>
    private static void EstablishOpeningEvidence(TestSession s, string focus, string originResolution, string? origin)
    {
        var focusValue = Enum.Parse<BrowserFocus>(focus);
        var originResolutionValue = Enum.Parse<OriginResolution>(originResolution);
        Assert.True(s.Registry.TryAssert(s.ChannelId, focusValue, originResolutionValue, origin, out _));
    }

    /// <summary>Duplex in-memory pipe stand-in (mirrors Gate031F6H_E3RedTests's own private
    /// DuplexMemoryStream) -- writes to one side become readable from the other.</summary>
    private sealed class DuplexMemoryStream : Stream
    {
        private readonly MemoryStream _inbox = new();
        private readonly SemaphoreSlim _dataAvailable = new(0);
        private int _readPosition;
        private bool _writerClosed;
        private readonly object _gate = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void CloseWriteSide()
        {
            lock (_gate) { _writerClosed = true; }
            _dataAvailable.Release();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_readPosition < _inbox.Length)
                    {
                        var span = _inbox.GetBuffer().AsSpan(_readPosition, (int)Math.Min(count, _inbox.Length - _readPosition));
                        span.CopyTo(buffer.AsSpan(offset));
                        _readPosition += span.Length;
                        return span.Length;
                    }

                    if (_writerClosed)
                        return 0;
                }

                await _dataAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                long saved = _inbox.Position;
                _inbox.Position = _inbox.Length;
                _inbox.Write(buffer, offset, count);
                _inbox.Position = saved;
            }
            _dataAvailable.Release();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Scripted duplex stream for a session's own transport (mirrors Gate031F6H_E3RedTests's
    /// own private ScriptedSessionTransport): two independent one-way pipes, so the test can act as
    /// "the browser side" while the real session code reads/writes the other side.</summary>
    private sealed class ScriptedSessionTransport : Stream
    {
        private readonly DuplexMemoryStream _toSession = new();
        private readonly DuplexMemoryStream _fromSession = new();
        public bool ThrowOnWrite { get; set; }
        public bool ThrowOnRead { get; set; }

        public Task WriteFromBrowserAsync(byte[] data) => _toSession.WriteAsync(data, 0, data.Length);
        public void CloseBrowserWriteSide() => _toSession.CloseWriteSide();

        public async Task<byte[]> ReadExactFromSessionAsync(int count)
        {
            byte[] buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read = await _fromSession.ReadAsync(buffer.AsMemory(total, count - total));
                if (read == 0) throw new IOException("session closed its write side before sending the expected bytes.");
                total += read;
            }
            return buffer;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ThrowOnRead ? throw new IOException("scripted read failure") : _toSession.ReadAsync(buffer, offset, count, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (ThrowOnWrite) throw new IOException("scripted write failure");
            _fromSession.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ThrowOnWrite ? throw new IOException("scripted write failure") : _fromSession.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static string? TryFindAppSourceFile(string typeSimpleName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PRIVON.slnx")))
            directory = directory.Parent;

        if (directory is null)
            return null;

        string candidate = Path.Combine(directory.FullName, "src", "Privon.App", typeSimpleName + ".cs");
        return File.Exists(candidate) ? candidate : null;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ==================================================================
    // G1 -- HAPPY CHALLENGE ROUND TRIP
    // ==================================================================

    [Fact]
    public async Task G1_HappyChallengeRoundTrip_ProducesSuccessWithExactFields()
    {
        var s = await CreateAcceptedSessionAsync(50001);
        try
        {
            var challengeTask = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));

            byte[] requestFrame = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, challengeTask);
            var decodeStatus = WebFrameDecoder.Decode(requestFrame, out var requestMessage);
            Assert.Equal(WebFrameDecodeStatus.Ok, decodeStatus);
            Assert.Equal(WebProtocolMessageType.ChallengeRequest, requestMessage.Type);
            Assert.Null(requestMessage.Accepted);
            Assert.Null(requestMessage.Focus);
            Assert.Null(requestMessage.OriginResolution);
            Assert.Null(requestMessage.Origin);
            Assert.NotNull(requestMessage.Nonce);
            AssertValidNonceShape(requestMessage.Nonce!);

            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(requestMessage.Nonce!, "Focused", "Resolved", "https://claude.ai"));

            var result = await AwaitBounded(challengeTask, "G1 happy round trip to complete");
            Assert.Equal("Success", result.Outcome);
            Assert.Equal(BrowserFocus.Focused, result.Focus);
            Assert.Equal(OriginResolution.Resolved, result.OriginResolution);
            Assert.Equal("https://claude.ai", result.Origin);
        }
        finally
        {
            s.Session.Close();
        }
    }

    // ==================================================================
    // G2 -- NONCE AUTHORITY / UNIQUENESS
    // ==================================================================

    [Fact]
    public async Task G2_TwoSequentialCompletedChallenges_ProduceDifferentValidNonces()
    {
        var s = await CreateAcceptedSessionAsync(50002);
        try
        {
            var first = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
            string firstNonce = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, first));
            AssertValidNonceShape(firstNonce);
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(firstNonce, "Focused", "Resolved", "https://claude.ai"));
            Assert.Equal("Success", (await AwaitBounded(first, "challenge #1")).Outcome);

            var second = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
            string secondNonce = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, second));
            AssertValidNonceShape(secondNonce);
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(secondNonce, "Focused", "Resolved", "https://claude.ai"));
            Assert.Equal("Success", (await AwaitBounded(second, "challenge #2")).Outcome);

            // Native App is the nonce authority -- two independently-issued nonces must never collide.
            // "Response #1 cannot satisfy challenge #2" and "correct B may still complete challenge B
            // after a first retired-nonce replay" are proven directly by G4/G5 below.
            Assert.NotEqual(firstNonce, secondNonce);
        }
        finally
        {
            s.Session.Close();
        }
    }

    [Fact]
    public void G2_WebChannelSession_Source_NeverLogsOrPersistsAnything()
    {
        // ALREADY_GREEN_CONTROL today (WebChannelSession.cs currently contains none of these) --
        // remains a regression guard once nonce generation/challenge state is added to this same file.
        string? path = TryFindAppSourceFile("WebChannelSession");
        Assert.True(path is not null, "src/Privon.App/WebChannelSession.cs must exist.");
        string source = File.ReadAllText(path!);

        foreach (var forbidden in new[] { "Console.Write", "Trace.Write", "Debug.Write", "File.Append", "File.Write", "ILogger" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    // ==================================================================
    // G3 -- WRONG NONCE
    // ==================================================================

    [Fact]
    public async Task G3_WrongNonce_ClosesSessionAndFailsTheChallengeClosed()
    {
        var s = await CreateAcceptedSessionAsync(50003);
        var challenge = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
        string realNonce = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, challenge));

        string wrongNonce = realNonce == ValidNonce(0x11) ? ValidNonce(0x22) : ValidNonce(0x11);
        await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(wrongNonce, "Focused", "Resolved", "https://claude.ai"));

        var result = await AwaitBounded(challenge, "challenge to fail closed after a wrong (but validly shaped) nonce");
        Assert.NotEqual("Success", result.Outcome);
        Assert.Equal(WebChannelSessionState.Closed, s.Session.State);
        Assert.False(challenge.IsFaulted, "a wrong nonce must resolve the ChallengeAsync task, never fault it.");
    }

    // ==================================================================
    // G4 -- REPLAY AGAINST NEW CHALLENGE
    // ==================================================================

    [Fact]
    public async Task G4_ReplayOfChallenge1ResponseCannotSatisfyChallenge2_ButTheCorrectResponseStillCan()
    {
        var s = await CreateAcceptedSessionAsync(50004);
        try
        {
            var first = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
            string nonceA = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, first));
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonceA, "Focused", "Resolved", "https://claude.ai"));
            Assert.Equal("Success", (await AwaitBounded(first, "challenge #1")).Outcome);

            var second = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
            string nonceB = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, second));
            Assert.NotEqual(nonceA, nonceB);

            // Replaying A (from the already-completed challenge #1) against the live challenge for B:
            // authorizes nothing. Per the frozen retired-nonce rule (G5), this first foreign/late copy
            // is tolerated (ignored) rather than an instant contradiction -- "correct B may still
            // complete the live challenge if this is the first allowed retired response".
            //
            // Proven with POSITIVE, causal evidence -- never a negative timer. The replay carries
            // response FACTS deliberately different from B's expected ones (NotFocused/Unsupported
            // instead of Focused/Resolved/claude.ai); the correct response for B, with B's real expected
            // facts, is sent immediately afterward. If a defective implementation let the replay
            // satisfy B, the completed result would deterministically carry the replay's WRONG facts
            // instead of B's real ones -- the exact-field assertions below are what would catch that,
            // not an elapsed-time guess about whether anything happened yet.
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonceA, "NotFocused", "Unsupported", null));
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonceB, "Focused", "Resolved", "https://claude.ai"));
            var secondResult = await AwaitBounded(second, "the correct response B to complete the live challenge");

            Assert.Equal("Success", secondResult.Outcome);
            Assert.Equal(BrowserFocus.Focused, secondResult.Focus);
            Assert.Equal(OriginResolution.Resolved, secondResult.OriginResolution);
            Assert.Equal("https://claude.ai", secondResult.Origin);
            Assert.Equal(WebChannelSessionState.Accepted, s.Session.State);
        }
        finally
        {
            s.Session.Close();
        }
    }

    // ==================================================================
    // G5 -- RETIRED NONCE / LATE RESPONSE
    // ==================================================================

    [Fact]
    public async Task G5_TimedOutChallenge_FirstLateResponseTolerated_SecondIsContradiction()
    {
        var s = await CreateAcceptedSessionAsync(50005);
        var challenge = InvokeChallengeAsync(s.Session, TimeSpan.Zero); // non-negative, near-zero -- deterministic, never a real 1s sleep
        string nonce = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, challenge));

        var result = await AwaitBounded(challenge, "challenge to time out");
        Assert.Equal("TimedOut", result.Outcome);
        Assert.Equal(WebChannelSessionState.Accepted, s.Session.State); // session remains healthy after a timeout

        // First late copy of the now-retired nonce: ignored, no authorization, session survives. Prove
        // it is still genuinely alive and functional -- deterministically, via a real fresh challenge
        // round trip (a positive completion signal) -- rather than a fixed settle delay before peeking
        // at State.
        await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));
        var probe = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
        string probeNonce = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, probe));
        await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(probeNonce, "Focused", "Resolved", "https://claude.ai"));
        Assert.Equal("Success", (await AwaitBounded(probe, "the liveness-probe challenge to succeed after the first late copy")).Outcome);
        Assert.Equal(WebChannelSessionState.Accepted, s.Session.State);

        // Second additional copy of the SAME retired nonce (arriving now that nothing is pending):
        // contradiction, session Close. No response may ever be buffered for a future challenge to
        // consume. Proven via WebChannelSession.Lifecycle -- a real completion signal -- rather than a
        // fixed settle delay before peeking at State.
        await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));
        await AwaitBounded(s.Session.Lifecycle, "the session to close after a second, additional copy of a retired nonce");
        Assert.Equal(WebChannelSessionState.Closed, s.Session.State);
    }

    // Gate E5F carry-forward -- the retired slot holds AT MOST one nonce, and every normal
    // conclusion (Success OR TimedOut) unconditionally overwrites it. G5 above already exercises the
    // overwrite path via a TimedOut-then-Success sequence; this test proves the same overwrite holds
    // for TWO CONSECUTIVE TimedOut conclusions, never previously exercised: challenge A times out
    // (retires nonce A), challenge B ALSO times out before A's late reply ever arrives (retiring
    // nonce B, discarding A's own retired-slot claim), and A's late reply -- now matching neither a
    // pending challenge nor the current retired nonce -- must be a contradiction, not a tolerated
    // late copy.
    [Fact]
    public async Task G5_SecondConsecutiveTimeoutOverwritesTheRetiredSlot_FirstChallengesLateReplyBecomesContradiction()
    {
        var s = await CreateAcceptedSessionAsync(50051);

        var challengeA = InvokeChallengeAsync(s.Session, TimeSpan.Zero);
        string nonceA = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, challengeA));
        Assert.Equal("TimedOut", (await AwaitBounded(challengeA, "challenge A to time out")).Outcome);

        var challengeB = InvokeChallengeAsync(s.Session, TimeSpan.Zero);
        string nonceB = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, challengeB));
        Assert.NotEqual(nonceA, nonceB);
        Assert.Equal("TimedOut", (await AwaitBounded(challengeB, "challenge B to time out, overwriting the retired slot")).Outcome);
        Assert.Equal(WebChannelSessionState.Accepted, s.Session.State); // still healthy after two timeouts

        // Nonce A's late reply now matches neither a pending challenge (none) nor the retired slot
        // (which holds B, not A) -- a contradiction, closing the session. Proven via Lifecycle, a
        // real completion signal, never a fixed settle delay.
        await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonceA, "Focused", "Resolved", "https://claude.ai"));
        await AwaitBounded(s.Session.Lifecycle, "the session to close after A's late reply, once the retired slot has been overwritten by B");
        Assert.Equal(WebChannelSessionState.Closed, s.Session.State);
    }

    // ==================================================================
    // G6 -- TIMEOUT
    // ==================================================================

    [Fact]
    public async Task G6_ZeroTimeout_TimesOutPromptly_SessionStaysHealthy_FutureChallengeStillWorks()
    {
        var s = await CreateAcceptedSessionAsync(50006);
        try
        {
            var first = InvokeChallengeAsync(s.Session, TimeSpan.Zero);
            byte[] requestFrame = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, first);
            Assert.Equal(WebFrameDecodeStatus.Ok, WebFrameDecoder.Decode(requestFrame, out var message));
            Assert.Equal(WebProtocolMessageType.ChallengeRequest, message.Type);

            var firstResult = await AwaitBounded(first, "zero-timeout challenge to time out");
            Assert.Equal("TimedOut", firstResult.Outcome);
            Assert.Equal(WebChannelSessionState.Accepted, s.Session.State);

            var second = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
            string secondNonce = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, second));
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(secondNonce, "Focused", "Resolved", "https://claude.ai"));
            var secondResult = await AwaitBounded(second, "a subsequent challenge after a timeout to succeed normally");
            Assert.Equal("Success", secondResult.Outcome);
        }
        finally
        {
            s.Session.Close();
        }
    }

    [Fact]
    public void G6_ChallengeTimeoutContractDefault_Is1Second_WithoutActuallyWaiting()
    {
        // Duplicate, behavior-scoped assertion of the Shape section's own check -- kept here too since
        // G6 explicitly calls out proving this constant "without actually waiting one second".
        Assert.True(SourceType is not null, E4Contract);
        var property = SourceType!.GetProperty("ChallengeTimeoutContractDefault", BindingFlags.Public | BindingFlags.Static);
        var field = SourceType.GetField("ChallengeTimeoutContractDefault", BindingFlags.Public | BindingFlags.Static);
        Assert.True(property is not null || field is not null,
            $"{SourceTypeName} exposes no static ChallengeTimeoutContractDefault member. " + E4Contract);
        object? value = property is not null ? property.GetValue(null) : field!.GetValue(null);
        Assert.Equal(TimeSpan.FromSeconds(1), value);
    }

    // ==================================================================
    // G7 -- CLOSE / DISCONNECT MID-CHALLENGE
    // ==================================================================

    [Theory]
    [InlineData("ExplicitClose")]
    [InlineData("TransportDisconnect")]
    public async Task G7_CloseOrDisconnectMidChallenge_CompletesSessionClosed_NoHangNoFault(string mode)
    {
        var s = await CreateAcceptedSessionAsync(mode == "ExplicitClose" ? 50007u : 50008u);
        var challengeTask = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
        _ = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, challengeTask);

        if (mode == "ExplicitClose")
            s.Session.Close();
        else
            s.Transport.CloseBrowserWriteSide();

        var result = await AwaitBounded(challengeTask, $"pending challenge to resolve after {mode}");
        Assert.Equal("SessionClosed", result.Outcome);
        Assert.False(challengeTask.IsFaulted);
    }

    [Fact]
    public async Task G7_ManagerShutdownDuringPendingChallenge_DoesNotItselfResolveTheChallenge()
    {
        var s = await CreateAcceptedSessionAsync(50009);
        try
        {
            var challengeTask = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
            byte[] requestFrame = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, challengeTask);
            string nonce = DecodeChallengeRequestNonce(requestFrame);

            // Positive, causal proof -- never an intermediate "should still be pending right now"
            // observation (ChallengeAsync owns its own independent timeout completion source, so even
            // an instantaneous IsCompleted read is not immune to an adversarial scheduling delay).
            // BeginShutdown is called here, then the REAL matching response is sent and the SAME
            // challengeTask is awaited to its one and only completion via the existing PURE_HANG_GUARD
            // helper. If BeginShutdown had wrongly cancelled/resolved the already-live session's pending
            // challenge, this would deterministically surface as a non-Success outcome (or the
            // hang-guard's own timeout failure) below -- never as a race the test has to catch in the
            // instant it happens.
            s.Manager.BeginShutdown();

            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));
            var result = await AwaitBounded(challengeTask, "the challenge to still complete after a genuine reply, despite a concurrent manager shutdown");

            Assert.Equal("Success", result.Outcome);
            Assert.Equal(BrowserFocus.Focused, result.Focus);
            Assert.Equal(OriginResolution.Resolved, result.OriginResolution);
            Assert.Equal("https://claude.ai", result.Origin);
        }
        finally
        {
            s.Session.Close();
        }
    }

    // ==================================================================
    // G8 -- STATEINVALIDATE DURING CHALLENGE
    // ==================================================================

    [Fact]
    public async Task G8_StateInvalidateDuringChallenge_SourceReturnsOutside_SessionNeedNotClose()
    {
        var s = await CreateAcceptedSessionAsync(50010);
        try
        {
            EstablishOpeningEvidence(s, "Focused", "Resolved", "https://claude.ai");
            var openingEvidence = ReadCurrentEvidence(s);

            var capture = new FakeForegroundTargetCapture { SnapshotToReturn = SupportedChromeSnapshot(s.Binding.BrowserProcessId) };
            var epochSource = new FakeEpochSource { Epoch = 5 };
            var source = (IWebClipboardAuthorizationSource)CreateAuthorizationSource(s.Manager, s.Registry, capture, epochSource, TimeSpan.FromSeconds(5));

            var authTask = source.AuthorizeAsync(capture.SnapshotToReturn);
            byte[] requestFrame = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, authTask);
            string nonce = DecodeChallengeRequestNonce(requestFrame);

            // A genuine StateInvalidate, through the real session read loop, queued strictly before the
            // ChallengeResponse below on the SAME wire. The session owns exactly one sequential
            // transport reader (Gate 031F6K G14), which consumes frames in exactly the order they were
            // written -- write order alone (never elapsed time) is what proves the invalidate is
            // processed before the response, so no intermediate wait is needed. Only the FINAL authTask
            // completion (which can only happen once every earlier queued frame has been consumed) is a
            // valid point to inspect registry state.
            await s.Transport.WriteFromBrowserAsync(StateInvalidateFrame());
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));

            var authorization = await AwaitBounded(authTask, "G8 authorization to resolve after a mid-challenge StateInvalidate");
            Assert.Null(authorization); // Outside -- never constructed from a mix of old and new evidence.
            Assert.NotEqual(WebChannelSessionState.Closed, s.Session.State); // state change alone must not close the session.

            var invalidatedEvidence = ReadCurrentEvidence(s);
            Assert.NotEqual(openingEvidence.EvidenceRevision, invalidatedEvidence.EvidenceRevision);
        }
        finally
        {
            s.Session.Close();
        }
    }

    // ==================================================================
    // G9 -- NEW STATEASSERT DURING CHALLENGE
    // ==================================================================

    [Fact]
    public async Task G9_NewStateAssertDuringChallenge_RevisionAdvance_YieldsOutside()
    {
        var s = await CreateAcceptedSessionAsync(50011);
        try
        {
            EstablishOpeningEvidence(s, "Focused", "Resolved", "https://claude.ai");
            var openingEvidence = ReadCurrentEvidence(s);

            var capture = new FakeForegroundTargetCapture { SnapshotToReturn = SupportedChromeSnapshot(s.Binding.BrowserProcessId) };
            var epochSource = new FakeEpochSource { Epoch = 3 };
            var source = (IWebClipboardAuthorizationSource)CreateAuthorizationSource(s.Manager, s.Registry, capture, epochSource, TimeSpan.FromSeconds(5));

            var authTask = source.AuthorizeAsync(capture.SnapshotToReturn);
            byte[] requestFrame = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, authTask);
            string nonce = DecodeChallengeRequestNonce(requestFrame);

            // Invalidate-before-assert satisfied by a genuine StateInvalidate first, then a brand-new,
            // independently-valid StateAssert -- both through the real session read loop, queued
            // strictly before the ChallengeResponse below on the SAME wire. As in G8, write order alone
            // (the session's one sequential reader, Gate 031F6K G14) proves processing order -- no
            // intermediate wait is needed; only the FINAL authTask completion is a valid point to
            // inspect registry state.
            await s.Transport.WriteFromBrowserAsync(StateInvalidateFrame());
            await s.Transport.WriteFromBrowserAsync(StateAssertFrame("Focused", "Resolved", "https://claude.ai"));
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));

            var authorization = await AwaitBounded(authTask, "G9 authorization to resolve after a mid-challenge revision advance");
            Assert.Null(authorization); // EV1 revision != EV2 revision -> Outside, never a mix of old/new evidence.

            var reassertedEvidence = ReadCurrentEvidence(s);
            Assert.NotEqual(openingEvidence.EvidenceRevision, reassertedEvidence.EvidenceRevision);
        }
        finally
        {
            s.Session.Close();
        }
    }

    private static WebForegroundEvidence ReadCurrentEvidence(TestSession s)
    {
        Assert.True(s.Registry.TryGetCurrentEvidence(s.ChannelId, out var evidence));
        return evidence;
    }

    // ==================================================================
    // G10 -- FOREGROUND BRACKET
    // ==================================================================

    private static async Task<WebClipboardAuthorization?> RunG10ScenarioAsync(
        uint pid, long epochAtStart, long epochAtEnd, ForegroundTargetSnapshot f1, ForegroundTargetSnapshot? f2Override)
    {
        var s = await CreateAcceptedSessionAsync(pid);
        try
        {
            EstablishOpeningEvidence(s, "Focused", "Resolved", "https://claude.ai");

            var capture = new FakeForegroundTargetCapture { SnapshotToReturn = f1 };
            var epochSource = new FakeEpochSource { Epoch = epochAtStart };
            var source = (IWebClipboardAuthorizationSource)CreateAuthorizationSource(s.Manager, s.Registry, capture, epochSource, TimeSpan.FromSeconds(5));

            var authTask = source.AuthorizeAsync(f1);
            byte[] requestFrame = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, authTask);
            string nonce = DecodeChallengeRequestNonce(requestFrame);

            epochSource.Epoch = epochAtEnd;
            if (f2Override is { } f2)
                capture.SnapshotToReturn = f2;

            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));
            return await AwaitBounded(authTask, "G10 scenario to resolve");
        }
        finally
        {
            s.Session.Close();
        }
    }

    [Fact]
    public async Task G10_E1Zero_YieldsOutside()
    {
        var authorization = await RunG10ScenarioAsync(50101, epochAtStart: 0, epochAtEnd: 7, f1: SupportedChromeSnapshot(50101), f2Override: null);
        Assert.Null(authorization);
    }

    [Fact]
    public async Task G10_E2Zero_YieldsOutside()
    {
        var authorization = await RunG10ScenarioAsync(50102, epochAtStart: 7, epochAtEnd: 0, f1: SupportedChromeSnapshot(50102), f2Override: null);
        Assert.Null(authorization);
    }

    [Fact]
    public async Task G10_E1NotEqualE2_YieldsOutside()
    {
        var authorization = await RunG10ScenarioAsync(50103, epochAtStart: 7, epochAtEnd: 8, f1: SupportedChromeSnapshot(50103), f2Override: null);
        Assert.Null(authorization);
    }

    [Fact]
    public async Task G10_F2Unresolved_YieldsOutside()
    {
        var f1 = SupportedChromeSnapshot(50104);
        var f2 = f1 with { IsResolved = false };
        var authorization = await RunG10ScenarioAsync(50104, epochAtStart: 7, epochAtEnd: 7, f1: f1, f2Override: f2);
        Assert.Null(authorization);
    }

    [Fact]
    public async Task G10_F2PidDiffersFromF1Pid_YieldsOutside()
    {
        // In this harness the session's own bound PID always equals F1's PID by construction, so
        // changing F2's PID away from F1's PID necessarily also makes it differ from the session's own
        // bound PID -- this single scenario exercises both listed negative sub-cases at once.
        var f1 = SupportedChromeSnapshot(50105);
        var f2 = SupportedChromeSnapshot(50106);
        var authorization = await RunG10ScenarioAsync(50105, epochAtStart: 7, epochAtEnd: 7, f1: f1, f2Override: f2);
        Assert.Null(authorization);
    }

    // ==================================================================
    // G11 -- SAME-PID SESSION REPLACEMENT
    // ==================================================================

    [Fact]
    public async Task G11_SamePidSessionReplacement_OldSessionsPendingChallengeCannotStillAuthorize()
    {
        const uint pid = 50200;
        var registry = new WebChannelRegistry();
        var manager = new WebChannelManager(registry);

        var s1 = await CreateAcceptedSessionAsync(pid, registry, manager);
        var challenge = InvokeChallengeAsync(s1.Session, TimeSpan.FromSeconds(5));
        string nonce = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s1.Transport, challenge));

        s1.Session.Close(); // S1 becomes non-current/closed

        var s2 = await CreateAcceptedSessionAsync(pid, registry, manager); // new session, same PID
        try
        {
            Assert.NotEqual(s1.ChannelId, s2.ChannelId);
            Assert.True(manager.TryGetAcceptedSession(pid, out var current));
            Assert.Same(s2.Session, current);
            Assert.NotSame(s1.Session, current);

            // A response to S1's own (now-stale) pending challenge must never authorize through S2.
            await s1.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));
            var result = await AwaitBounded(challenge, "S1's own pending challenge to resolve after replacement");
            Assert.NotEqual("Success", result.Outcome);
        }
        finally
        {
            s2.Session.Close();
        }
    }

    // ==================================================================
    // G12 -- CONCURRENT AUTHORIZE / BUSY
    // ==================================================================

    [Fact]
    public async Task G12_ConcurrentChallenge_SecondCallerGetsBusy_FirstStillSucceeds()
    {
        var s = await CreateAcceptedSessionAsync(50300);
        try
        {
            var first = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
            string nonce = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, first));

            var second = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
            var secondResult = await AwaitBounded(second, "the second concurrent ChallengeAsync to resolve Busy");
            Assert.Equal("Busy", secondResult.Outcome);

            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));
            var firstResult = await AwaitBounded(first, "the first challenge to still succeed after a busy second caller");
            Assert.Equal("Success", firstResult.Outcome);
        }
        finally
        {
            s.Session.Close();
        }
    }

    [Fact]
    public async Task G12_ConcurrentAuthorize_OnUnrelatedSessions_NeitherBlocksTheOther()
    {
        // No global lock across unrelated sessions -- two independent sessions may each have their own
        // pending challenge concurrently.
        var a = await CreateAcceptedSessionAsync(50301);
        var b = await CreateAcceptedSessionAsync(50302);
        try
        {
            var challengeA = InvokeChallengeAsync(a.Session, TimeSpan.FromSeconds(5));
            var challengeB = InvokeChallengeAsync(b.Session, TimeSpan.FromSeconds(5));

            string nonceA = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(a.Transport, challengeA));
            string nonceB = DecodeChallengeRequestNonce(await ReadFrameOrSurfaceChallengeFaultAsync(b.Transport, challengeB));

            await a.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonceA, "Focused", "Resolved", "https://claude.ai"));
            await b.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonceB, "Focused", "Resolved", "https://claude.ai"));

            Assert.Equal("Success", (await AwaitBounded(challengeA, "session A's challenge")).Outcome);
            Assert.Equal("Success", (await AwaitBounded(challengeB, "session B's challenge")).Outcome);
        }
        finally
        {
            a.Session.Close();
            b.Session.Close();
        }
    }

    // ==================================================================
    // G13 -- WRITE SERIALIZATION / WRITE FAILURE
    // ==================================================================

    [Fact]
    public void G13_WebChannelSession_Source_WillOwnExactlyOneWriteSerializationPrimitive()
    {
        string? path = TryFindAppSourceFile("WebChannelSession");
        Assert.True(path is not null, "src/Privon.App/WebChannelSession.cs must exist.");
        string source = File.ReadAllText(path!);

        bool hasSemaphore = source.Contains("SemaphoreSlim", StringComparison.Ordinal);
        bool hasWriteGateField = source.Contains("_writeGate", StringComparison.Ordinal) || source.Contains("_writeLock", StringComparison.Ordinal);

        Assert.True(hasSemaphore || hasWriteGateField,
            "Gate 031F6K E4 contract: WebChannelSession must own exactly one session write " +
            "serialization primitive (e.g. a SemaphoreSlim or a dedicated write-gate lock) once " +
            "ChallengeRequest writes are introduced alongside the existing Hello/HelloAck write -- this " +
            "does not exist yet (EXPECTED_E4_RED). No manager lock may be held during a transport " +
            "write, and no challenge monitor may be held across an await.");
    }

    [Fact]
    public async Task G13_WriteFailureDuringChallengeRequest_YieldsWriteFailedAndClosesSession()
    {
        var s = await CreateAcceptedSessionAsync(50400);
        s.Transport.ThrowOnWrite = true;

        var challengeTask = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
        var result = await AwaitBounded(challengeTask, "ChallengeAsync to resolve WriteFailed after a scripted transport write failure");

        Assert.Equal("WriteFailed", result.Outcome);
        Assert.Equal(WebChannelSessionState.Closed, s.Session.State);
        Assert.False(challengeTask.IsFaulted, "a write failure must resolve WriteFailed, never fault the ChallengeAsync task.");
    }

    // Gate E5F carry-forward -- ChallengeAsync completes the caller's TaskCompletionSource with
    // WriteFailed BEFORE calling Close() (see WebChannelSession.ChallengeAsync's own source order).
    // The existing test above only checks the final State == Closed; it does not prove that by the
    // time the caller OBSERVES WriteFailed, Close()'s OTHER side effects (not just the atomic state
    // flag) are already visible too. This test proves exactly that: once ChallengeAsync's awaited
    // Task resolves WriteFailed, the manager must no longer report this session as the current
    // accepted session for its own PID -- i.e. full teardown, not merely the state transition, is
    // guaranteed complete by the time the caller can act on the result.
    [Fact]
    public async Task G13_WriteFailedResult_ObservedOnlyAfterFullTeardownSideEffectsAreVisible()
    {
        var registry = new WebChannelRegistry();
        var manager = new WebChannelManager(registry);
        var s = await CreateAcceptedSessionAsync(50401, registry, manager);
        s.Transport.ThrowOnWrite = true;

        Assert.True(manager.TryGetAcceptedSession(50401, out var beforeSession) && ReferenceEquals(beforeSession, s.Session),
            "sanity: the manager must report this session as current before the write failure.");

        var result = await AwaitBounded(
            InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5)),
            "ChallengeAsync to resolve WriteFailed after a scripted transport write failure");
        Assert.Equal("WriteFailed", result.Outcome);

        // Full Close() teardown -- not just the atomic State flag -- must already be visible the
        // instant the caller observes WriteFailed.
        Assert.False(manager.TryGetAcceptedSession(50401, out _),
            "the manager must no longer report this session as current once WriteFailed has been observed.");
        Assert.Equal(WebChannelSessionState.Closed, s.Session.State);
    }

    // ==================================================================
    // G14 -- SINGLE TRANSPORT READER
    // ==================================================================

    [Fact]
    public void G14_WebChannelSession_RemainsTheSoleTransportReadCallSite()
    {
        // ALREADY_GREEN_CONTROL today: WebChannelSession.cs currently has exactly one read call site
        // (inside RunAsync's own loop). This proves the baseline now, and remains a regression guard
        // through E4 GREEN -- ChallengeAsync must never add a second reader.
        string? path = TryFindAppSourceFile("WebChannelSession");
        Assert.True(path is not null, "src/Privon.App/WebChannelSession.cs must exist.");
        string source = File.ReadAllText(path!);

        int readCallSites = CountOccurrences(source, "RelayOneFrameAsync(");
        Assert.Equal(1, readCallSites);
    }

    [Fact]
    public void G14_WebClipboardAuthorizationSource_NeverReadsTransportDirectly()
    {
        string? path = TryFindAppSourceFile("WebClipboardAuthorizationSource");
        Assert.True(path is not null,
            $"src/Privon.App/WebClipboardAuthorizationSource.cs does not exist yet. {E4Contract}");

        string source = File.ReadAllText(path!);
        Assert.DoesNotContain(".ReadAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stream.Read", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Channel<", source, StringComparison.Ordinal);
    }

    // ==================================================================
    // G15 -- EXACT SOURCE RESULT / FRESHNESS
    // ==================================================================

    [Fact]
    public async Task G15_FreshnessProof_MatchesExactChallengeAttempt_AndIndependentlyDetectsStaleness()
    {
        var s = await CreateAcceptedSessionAsync(50500);
        try
        {
            EstablishOpeningEvidence(s, "Focused", "Resolved", "https://claude.ai");

            var capture = new FakeForegroundTargetCapture { SnapshotToReturn = SupportedChromeSnapshot(s.Binding.BrowserProcessId) };
            var epochSource = new FakeEpochSource { Epoch = 10 };
            var source = (IWebClipboardAuthorizationSource)CreateAuthorizationSource(s.Manager, s.Registry, capture, epochSource, TimeSpan.FromSeconds(5));

            var authTask = source.AuthorizeAsync(capture.SnapshotToReturn);
            byte[] requestFrame = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, authTask);
            string nonce = DecodeChallengeRequestNonce(requestFrame);
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));

            var authorization = await AwaitBounded(authTask, "G15 authorization to succeed");
            Assert.NotNull(authorization);
            Assert.True(authorization!.Freshness.IsStillCurrent(), "freshness must be current immediately after a successful authorization.");

            // Independent staleness after an epoch change.
            epochSource.Epoch = 11;
            Assert.False(authorization.Freshness.IsStillCurrent(), "an epoch change must independently invalidate freshness.");
            epochSource.Epoch = 10;
            Assert.True(authorization.Freshness.IsStillCurrent(), "restoring the epoch must restore freshness -- no memoization of a prior stale answer.");

            // Independent staleness after a revision change (a fresh invalidate/assert cycle). The
            // challenge has already completed, so nothing asynchronous is in flight to race against --
            // direct, synchronous registry calls (the same real, production WebChannelRegistry) are
            // fully deterministic here, with no wire round trip and no delay needed.
            Assert.True(s.Registry.TryInvalidate(s.ChannelId, out _));
            Assert.True(s.Registry.TryAssert(s.ChannelId, BrowserFocus.Focused, OriginResolution.Resolved, "https://claude.ai", out _));
            Assert.False(authorization.Freshness.IsStillCurrent(), "a revision change must independently invalidate freshness.");
        }
        finally
        {
            s.Session.Close();
        }
    }

    [Fact]
    public async Task G15_FreshnessProof_DetectsForegroundPidChange()
    {
        var s = await CreateAcceptedSessionAsync(50501);
        try
        {
            EstablishOpeningEvidence(s, "Focused", "Resolved", "https://claude.ai");

            var capture = new FakeForegroundTargetCapture { SnapshotToReturn = SupportedChromeSnapshot(s.Binding.BrowserProcessId) };
            var epochSource = new FakeEpochSource { Epoch = 4 };
            var source = (IWebClipboardAuthorizationSource)CreateAuthorizationSource(s.Manager, s.Registry, capture, epochSource, TimeSpan.FromSeconds(5));

            var authTask = source.AuthorizeAsync(capture.SnapshotToReturn);
            byte[] requestFrame = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, authTask);
            string nonce = DecodeChallengeRequestNonce(requestFrame);
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));

            var authorization = await AwaitBounded(authTask, "G15 (PID) authorization to succeed");
            Assert.NotNull(authorization);
            Assert.True(authorization!.Freshness.IsStillCurrent());

            capture.SnapshotToReturn = SupportedChromeSnapshot(s.Binding.BrowserProcessId + 1);
            Assert.False(authorization.Freshness.IsStillCurrent(), "a foreground PID change must independently invalidate freshness.");
        }
        finally
        {
            s.Session.Close();
        }
    }

    // ==================================================================
    // G16 -- FULL A-H FORMULA
    // ==================================================================

    [Fact]
    public async Task G16_FullFormula_AllTermsSatisfied_YieldsSuccessWithExactTarget()
    {
        var s = await CreateAcceptedSessionAsync(50600);
        try
        {
            EstablishOpeningEvidence(s, "Focused", "Resolved", "https://claude.ai");

            var capture = new FakeForegroundTargetCapture { SnapshotToReturn = SupportedChromeSnapshot(s.Binding.BrowserProcessId) };
            var epochSource = new FakeEpochSource { Epoch = 42 };
            var source = (IWebClipboardAuthorizationSource)CreateAuthorizationSource(s.Manager, s.Registry, capture, epochSource, TimeSpan.FromSeconds(5));

            var authTask = source.AuthorizeAsync(capture.SnapshotToReturn);
            byte[] requestFrame = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, authTask);
            string nonce = DecodeChallengeRequestNonce(requestFrame);
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, "Focused", "Resolved", "https://claude.ai"));

            var authorization = await AwaitBounded(authTask, "G16 full-formula AuthorizeAsync to succeed");
            Assert.NotNull(authorization);
            Assert.Equal(SupportedWebTarget.ClaudeWeb, authorization!.Target);
            Assert.True(authorization.Freshness.IsStillCurrent());
        }
        finally
        {
            s.Session.Close();
        }
    }

    [Theory]
    [InlineData("UnfocusedBrowser")]
    [InlineData("UnsupportedOrigin")]
    [InlineData("UnsupportedBrowserProcess")]
    public async Task G16_PerTermNegativeControl_FailsClosed(string scenario)
    {
        var s = await CreateAcceptedSessionAsync(scenario switch { "UnfocusedBrowser" => 50601u, "UnsupportedOrigin" => 50602u, _ => 50603u });
        try
        {
            (string focus, string originResolution, string? origin) = scenario switch
            {
                "UnfocusedBrowser" => ("NotFocused", "Resolved", "https://claude.ai"),
                "UnsupportedOrigin" => ("Focused", "Unsupported", null),
                _ => ("Focused", "Resolved", "https://claude.ai"),
            };
            EstablishOpeningEvidence(s, focus, originResolution, origin);

            var snapshot = scenario == "UnsupportedBrowserProcess"
                ? SupportedChromeSnapshot(s.Binding.BrowserProcessId) with { ProcessName = "notepad" }
                : SupportedChromeSnapshot(s.Binding.BrowserProcessId);

            var capture = new FakeForegroundTargetCapture { SnapshotToReturn = snapshot };
            var epochSource = new FakeEpochSource { Epoch = 9 };
            var source = (IWebClipboardAuthorizationSource)CreateAuthorizationSource(s.Manager, s.Registry, capture, epochSource, TimeSpan.FromSeconds(5));

            var authTask = source.AuthorizeAsync(snapshot);
            byte[] requestFrame = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, authTask);
            string nonce = DecodeChallengeRequestNonce(requestFrame);
            await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(nonce, focus, originResolution, origin));

            var authorization = await AwaitBounded(authTask, $"G16 negative control ({scenario}) to resolve");
            Assert.Null(authorization);
        }
        finally
        {
            s.Session.Close();
        }
    }

    // ==================================================================
    // G17 -- EXCEPTION / PROTOCOL CLASSIFICATION
    // ==================================================================

    [Fact]
    public async Task G17_ChallengeAsync_BeforeAccepted_YieldsNotAccepted()
    {
        var registry = new WebChannelRegistry();
        var manager = new WebChannelManager(registry);
        var budget = new WebSessionAdmissionBudget(4);
        var lease = await budget.AcquireAsync();
        var binding = new FakeBrowserHostBinding(50700);
        var transport = new ScriptedSessionTransport();
        var session = new WebChannelSession(transport, binding, registry, lease, manager);
        session.Start(); // AwaitingHello -- the browser side has not sent Hello yet.

        var result = await AwaitBounded(InvokeChallengeAsync(session, TimeSpan.FromSeconds(5)), "ChallengeAsync to return NotAccepted before Hello/Accept");
        Assert.Equal("NotAccepted", result.Outcome);

        session.Close();
    }

    [Fact]
    public async Task G17_MalformedChallengeResponseFrame_ClosesSessionAndFailsTheChallengeClosed()
    {
        var s = await CreateAcceptedSessionAsync(50701);
        var challengeTask = InvokeChallengeAsync(s.Session, TimeSpan.FromSeconds(5));
        _ = await ReadFrameOrSurfaceChallengeFaultAsync(s.Transport, challengeTask);

        byte[] malformed = LengthPrefixedJson("{\"v\":1,\"type\":\"ChallengeResponse\""); // truncated/invalid JSON
        await s.Transport.WriteFromBrowserAsync(malformed);

        var result = await AwaitBounded(challengeTask, "the challenge to fail closed after a malformed/undecodable response frame");
        Assert.NotEqual("Success", result.Outcome);
        Assert.Equal(WebChannelSessionState.Closed, s.Session.State);
        Assert.False(challengeTask.IsFaulted, "a protocol decode failure must resolve the ChallengeAsync task, never fault it.");
    }

    [Fact]
    public async Task G17_UnsolicitedChallengeResponse_WithNoPendingChallenge_ClosesTheSession()
    {
        var s = await CreateAcceptedSessionAsync(50702);
        await s.Transport.WriteFromBrowserAsync(ChallengeResponseFrame(ValidNonce(0x33), "Focused", "Resolved", "https://claude.ai"));
        await AwaitBounded(s.Session.Lifecycle, "the session to close after an unsolicited ChallengeResponse with no pending challenge");
        Assert.Equal(WebChannelSessionState.Closed, s.Session.State);
    }

    // ==================================================================
    // G18 -- PRIVACY / WINDOWS / PRODUCTION ACTIVATION (real E1-E3 production types -- ALREADY_GREEN)
    // ==================================================================

    [Fact]
    public void G18_ProductionExtensionAllowlist_IsExactlyTheVerifiedChromeOrigin_Gate031E5G3()
    {
        // Gate E5G.3 superseded this test's original EMPTY assertion with its own explicit, stated
        // GREEN target: Production now authorizes exactly the verified Chrome Store origin (see
        // Gate031E5G3_ChromeOnlyProductionAllowlistRedTests.A, GREEN). The Edge origin, and every other
        // origin, remains absent -- re-asserted here rather than merely by omission.
        Assert.Single(WebExtensionOriginAllowlist.Production);
        Assert.Contains("chrome-extension://aieobgphcpmkfnhadocdhenigmackboo/", WebExtensionOriginAllowlist.Production);
        Assert.DoesNotContain("chrome-extension://fmdcgbjednllpjlogkcjmlocpnpbpljn/", WebExtensionOriginAllowlist.Production);
    }

    [Fact]
    public void G18_PrivonAppComposition_NowWiresARealWebClipboardAuthorizationSource_Gate031E5F()
    {
        // Gate E5F superseded this test's original assertion (composition wired NO concrete
        // WebClipboardAuthorizationSource) with its own opposite, explicitly-intended GREEN target:
        // composition now constructs exactly one production WebClipboardAuthorizationSource through
        // normal composition (see Gate031E5F_ProductionCompositionRedTests.Case2_3, GREEN). As of Gate
        // E5G.3, Production is no longer empty (G18_ProductionExtensionAllowlist_IsExactlyTheVerifiedChromeOrigin_Gate031E5G3,
        // above) -- but no real com.privon.host is registered by composition alone (Gate031E5F case
        // 7/8/9), so no live OS-level Native Messaging connection is reachable from source construction
        // alone either way.
        string? path = TryFindAppSourceFile(nameof(PrivonAppComposition));
        Assert.True(path is not null, "src/Privon.App/PrivonAppComposition.cs must exist.");
        string source = File.ReadAllText(path!);
        Assert.Contains("WebClipboardAuthorizationSource", source, StringComparison.Ordinal);
    }

    [Fact]
    public void G18_ChallengeRequestWireShape_IsExactlyNonce_NoAdditionalFieldsAccepted()
    {
        byte[] validFrame = LengthPrefixedJson($"{{\"v\":1,\"type\":\"ChallengeRequest\",\"nonce\":\"{ValidNonce(0)}\"}}");
        Assert.Equal(WebFrameDecodeStatus.Ok, WebFrameDecoder.Decode(validFrame, out _));

        byte[] extraFieldFrame = LengthPrefixedJson($"{{\"v\":1,\"type\":\"ChallengeRequest\",\"nonce\":\"{ValidNonce(0)}\",\"origin\":\"https://claude.ai\"}}");
        Assert.Equal(WebFrameDecodeStatus.InvalidValue, WebFrameDecoder.Decode(extraFieldFrame, out _));
    }

    [Fact]
    public void G18_ChallengeResponseWireShape_IsExactlyNonceFocusOriginResolutionOptionalOrigin()
    {
        byte[] withoutOrigin = LengthPrefixedJson($"{{\"v\":1,\"type\":\"ChallengeResponse\",\"nonce\":\"{ValidNonce(0)}\",\"focus\":\"Focused\",\"originResolution\":\"Unsupported\"}}");
        Assert.Equal(WebFrameDecodeStatus.Ok, WebFrameDecoder.Decode(withoutOrigin, out _));

        byte[] withOrigin = LengthPrefixedJson($"{{\"v\":1,\"type\":\"ChallengeResponse\",\"nonce\":\"{ValidNonce(0)}\",\"focus\":\"Focused\",\"originResolution\":\"Resolved\",\"origin\":\"https://claude.ai\"}}");
        Assert.Equal(WebFrameDecodeStatus.Ok, WebFrameDecoder.Decode(withOrigin, out _));

        byte[] extraField = LengthPrefixedJson($"{{\"v\":1,\"type\":\"ChallengeResponse\",\"nonce\":\"{ValidNonce(0)}\",\"focus\":\"Focused\",\"originResolution\":\"Unsupported\",\"page\":\"x\"}}");
        Assert.Equal(WebFrameDecodeStatus.InvalidValue, WebFrameDecoder.Decode(extraField, out _));
    }

    [Fact]
    public async Task G18_UnsupportedWindowsForegroundTarget_ClipboardAuthorizationRouter_StillYieldsOutsideForWeb()
    {
        // Mirrors Gate031F6H_E3RedTests.R25 -- re-asserted here as an E4-scope ALREADY_GREEN_CONTROL
        // baseline (webSource: null remains the production default through E4 RED).
        var unsupportedForeground = new ForegroundTargetSnapshot(
            IsResolved: true, ProcessId: 4321, ProcessName: "notepad",
            PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
            ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Contoso");

        var result = await ClipboardAuthorizationRouter.AuthorizeAsync(unsupportedForeground, webSource: null);
        Assert.Equal(ClipboardAuthorizationKind.Outside, result.Kind);
    }
}
