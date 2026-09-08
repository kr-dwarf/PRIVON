using System.IO;
using System.Security.Cryptography;
using Privon.Browser;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the owner of one admitted Web channel connection's full lifecycle:
/// the Hello/HelloAck handshake, the StateInvalidate/StateAssert message loop over the existing,
/// frozen <see cref="WebChannelRegistry"/>, and sole ownership of its transport/binding/admission
/// Lease (Gate 031F6H R10-R21, R34, R40-R42, R45/R45B).
///
/// STATE_MACHINE (frozen, Gate 031F6H section 14): an atomic <c>NotStarted -&gt; Started -&gt; Closed</c>
/// int guard (via <see cref="Interlocked.CompareExchange(ref int, int, int)"/>/
/// <see cref="Interlocked.Exchange(ref int, int)"/>) governs <see cref="Start"/>/<see cref="Close"/>
/// themselves -- Close always wins exactly once no matter how many callers race it, and a Close
/// that wins BEFORE Start's own CAS is a permanent no-op for Start (R45). The accepted residual
/// (R45B): if Start's CAS succeeds a moment before a concurrent Close claims terminal ownership,
/// <see cref="RunAsync"/> simply encounters an already-disposed transport/binding on its very first
/// operation, is caught by its own outer try/catch, and completes non-faulted via its own finally-Close
/// (itself a no-op by then) -- never resurrecting any resource/map/registry/permit state.
///
/// The richer, publicly observable <see cref="WebChannelSessionState"/> layers on top of that raw
/// atomic guard via a second, independent flag (<c>_accepted</c>) set only once the HelloAck has been
/// written AND flushed successfully and this session has been promoted by
/// <see cref="WebChannelManager.TryPromoteToAccepted"/> -- so <see cref="State"/> can distinguish
/// AwaitingHello from Accepted while the raw guard is still simply "Started".
///
/// GATE_031F6L (E4) ADDITIVE: this type also owns the decision-time ChallengeRequest/ChallengeResponse
/// exchange (<see cref="ChallengeAsync"/>) -- exactly one pending challenge slot, at most one retired
/// nonce (see <see cref="TryHandleChallengeResponse"/>), native nonce generation, and a write
/// serialization primitive (<c>_writeGate</c>) that <see cref="ChallengeAsync"/>'s own outbound
/// ChallengeRequest write goes through.
///
/// DOC_CORRECTION (Gate E5F carry-forward): <c>_writeGate</c> does NOT serialize the initial HelloAck
/// write -- <see cref="RunAsync"/> writes HelloAck (both the accepted and best-effort-rejected paths)
/// directly against <c>_transport</c>, never acquiring <c>_writeGate</c>. This is safe, not merely
/// undocumented: <see cref="ChallengeAsync"/> itself refuses to proceed past its own
/// <see cref="WebChannelSessionState.Accepted"/> check until AFTER HelloAck has already been written
/// and this session promoted (<see cref="WebChannelManager.TryPromoteToAccepted"/>) -- so the HelloAck
/// write and any ChallengeRequest write are structurally, temporally disjoint; <c>_writeGate</c> exists
/// solely to serialize ChallengeRequest writes against each other (relevant once more than one write
/// path can reach Accepted-state traffic), never to arbitrate HelloAck against anything.
///
/// <see cref="RunAsync"/> remains the SOLE transport reader -- <see cref="ChallengeAsync"/>
/// never reads from <c>_transport</c> itself; it only writes the request and awaits a
/// <see cref="TaskCompletionSource{TResult}"/> that <see cref="RunAsync"/>'s own loop resolves once a
/// matching (or contradictory) ChallengeResponse frame arrives. This is an E4_VERSIONED_EXTENSION of
/// the Accepted-state protocol, not an E3 defect correction: StateInvalidate/StateAssert handling is
/// unchanged; Hello/HelloAck/ChallengeRequest remain contradictions once Accepted.
/// </summary>
internal sealed class WebChannelSession
{
    private const int NotStarted = 0;
    private const int Started = 1;
    private const int Closed = 2;

    // Fixed-capacity, per Gate 031F6H section 21: 4 prefix bytes + WebFrameDecoder.MaxPayloadBytes
    // (4096) payload bytes = 4100 bytes total. Reset (Position/Length = 0) before every frame -- never
    // grown, never allowed to accumulate a previous frame's bytes.
    private const int FrameBufferCapacity = 4 + WebFrameDecoder.MaxPayloadBytes;

    // Gate 031F6L (E4): 32 cryptographically random bytes, Base64URL-unpadded, encodes to exactly this
    // many characters -- matches the frozen wire nonce shape (WebFrameDecoder/WebFrameEncoder).
    private const int NonceRandomByteCount = 32;

    private readonly Stream _transport;
    private readonly IWebBrowserHostBinding _binding;
    private readonly WebChannelRegistry _registry;
    private readonly WebSessionAdmissionBudget.Lease _lease;
    private readonly WebChannelManager _manager;
    private readonly uint _browserProcessId;

    private int _state;
    private int _accepted;
    private long _channelId; // 0 == not yet allocated (WebChannelRegistry's own convention).
    private Task? _lifecycle;

    // Gate 031F6L (E4) challenge ownership -- every field below is read/written only under _challengeGate,
    // except _writeGate itself (which serializes outbound frame writes, never challenge correlation state).
    private readonly object _challengeGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TaskCompletionSource<WebChallengeResult>? _pendingChallenge;
    private string? _pendingNonce;
    private string? _retiredNonce;

    public WebChannelSession(
        Stream transport,
        IWebBrowserHostBinding binding,
        WebChannelRegistry registry,
        WebSessionAdmissionBudget.Lease lease,
        WebChannelManager manager)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(manager);

        _transport = transport;
        _binding = binding;
        _registry = registry;
        _lease = lease;
        _manager = manager;
        _browserProcessId = binding.BrowserProcessId;
    }

    public WebChannelSessionState State
    {
        get
        {
            int state = Volatile.Read(ref _state);
            if (state == Closed) return WebChannelSessionState.Closed;
            if (state == NotStarted) return WebChannelSessionState.NotStarted;
            return Volatile.Read(ref _accepted) == 1 ? WebChannelSessionState.Accepted : WebChannelSessionState.AwaitingHello;
        }
    }

    public long? ChannelId
    {
        get
        {
            long value = Volatile.Read(ref _channelId);
            return value == 0 ? null : value;
        }
    }

    public uint BrowserProcessId => _browserProcessId;

    public Task Lifecycle => Volatile.Read(ref _lifecycle) ?? Task.CompletedTask;

    /// <summary>Delegates to the owned binding's own liveness fact -- consulted exclusively by
    /// <see cref="WebChannelManager.TryGetAcceptedSession"/> (Gate 031F6H section 17). Not part of
    /// this type's own frozen public test surface; additive, internal-only.</summary>
    internal Privon.Windows.RetainedProcessLiveness CheckBindingLiveness() => _binding.CheckLiveness();

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, Started, NotStarted) != NotStarted)
            return;

        Volatile.Write(ref _lifecycle, RunAsync());
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _state, Closed) == Closed)
            return; // exactly one teardown winner, regardless of concurrent callers (Gate 031F6H R30).

        try
        {
            // Gate 031F6L (E4): unblock any awaiting ChallengeAsync caller FIRST, before the rest of
            // teardown -- SessionClosed exactly once, never faulted, never left hanging.
            SafeInvoke(CompletePendingChallengeAsClosed);

            SafeInvoke(() => _manager.RemoveIfCurrent(_browserProcessId, this));

            long channelId = Volatile.Read(ref _channelId);
            if (channelId != 0)
                SafeInvoke(() => _registry.TryDisconnect(channelId));

            SafeInvoke(_binding.Dispose);
            SafeInvoke(_transport.Dispose);

            // _writeGate is deliberately NEVER disposed here: an in-flight ChallengeAsync call may still
            // be between its own write and its own `finally { _writeGate.Release(); }` when a concurrent
            // Close() runs (Gate 031F6H R30 -- Close can race any caller). Disposing it here would make
            // that still-pending Release() throw ObjectDisposedException. A SemaphoreSlim with no
            // pending waiters costs nothing to leave for GC -- exactly the same tradeoff this codebase
            // already accepts elsewhere (see WebSessionAdmissionBudget.Lease's own disposed-owner doc).
        }
        finally
        {
            _lease.Dispose();
        }
    }

    /// <summary>
    /// Gate 031F6L (E4) -- the decision-time ChallengeRequest/ChallengeResponse round trip. Installs
    /// the SOLE pending-challenge slot (rejecting a concurrent second caller with
    /// <see cref="WebChallengeOutcome.Busy"/> -- a distinct result, never sharing the first caller's
    /// own <see cref="TaskCompletionSource{TResult}"/>), generates a fresh native nonce, writes exactly
    /// one ChallengeRequest frame through the single serialized write path, then awaits either a
    /// matching ChallengeResponse (resolved by <see cref="RunAsync"/>'s own read loop -- this method
    /// never reads the transport itself) or the supplied timeout.
    /// </summary>
    public async Task<WebChallengeResult> ChallengeAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "timeout must be non-negative.");

        if (State != WebChannelSessionState.Accepted)
            return NotAcceptedResult();

        var tcs = new TaskCompletionSource<WebChallengeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        string nonce;

        lock (_challengeGate)
        {
            // Re-checked under the lock: a concurrent Close (or the R45B residual) may have landed
            // between the fast-path check above and here.
            if (State != WebChannelSessionState.Accepted)
                return NotAcceptedResult();

            if (_pendingChallenge is not null)
                return BusyResult(); // a distinct, un-shared result -- never the first caller's own TCS.

            nonce = GenerateNonce();
            _pendingChallenge = tcs;
            _pendingNonce = nonce;
        }

        var encodeStatus = WebFrameEncoder.Encode(
            new WebProtocolMessage(WebProtocolMessageType.ChallengeRequest, null, null, null, null, nonce),
            out byte[]? frame);
        if (encodeStatus != WebFrameEncodeStatus.Ok || frame is null)
        {
            CompletePendingChallenge(tcs, WriteFailedResult());
            Close();
            return await tcs.Task.ConfigureAwait(false);
        }

        bool writeFailed;
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _transport.WriteAsync(frame).ConfigureAwait(false);
            await _transport.FlushAsync().ConfigureAwait(false);
            writeFailed = false;
        }
        catch
        {
            writeFailed = true;
        }
        finally
        {
            // Released BEFORE any Close() call below -- Close() disposes this same gate, and disposing
            // it while still held (then releasing into a disposed SemaphoreSlim) would throw.
            _writeGate.Release();
        }

        if (writeFailed)
        {
            CompletePendingChallenge(tcs, WriteFailedResult());
            Close();
            return await tcs.Task.ConfigureAwait(false);
        }

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (winner != tcs.Task)
        {
            // Timeout: atomically retire the pending slot -- but never overwrite a response that
            // genuinely arrived concurrently with the timer (the read loop always wins that race).
            bool retiredHere;
            lock (_challengeGate)
            {
                retiredHere = ReferenceEquals(_pendingChallenge, tcs);
                if (retiredHere)
                {
                    _pendingChallenge = null;
                    _pendingNonce = null;
                    _retiredNonce = nonce;
                }
            }

            if (retiredHere)
                tcs.TrySetResult(new WebChallengeResult(WebChallengeOutcome.TimedOut, BrowserFocus.Unresolved, OriginResolution.Unresolved, null));
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Gate 031F6L (E4) -- dispatches one Accepted-state ChallengeResponse frame to the pending-challenge
    /// correlation mechanism. Returns <see langword="true"/> when the caller (RunAsync's own loop) may
    /// continue reading; <see langword="false"/> signals a protocol contradiction, exactly like a failed
    /// StateInvalidate/StateAssert, causing RunAsync to return (and Close via its own finally).
    ///
    /// RETIRED_NONCE_CONTRACT: exactly one retired nonce is remembered -- the nonce of whichever
    /// challenge most recently concluded normally (Success or TimedOut) while the session stayed alive.
    /// Its FIRST late/foreign response is tolerated (ignored, cleared, no authorization); a SECOND
    /// arrival of that same value, or any nonce matching neither the live pending challenge nor the
    /// retired one, is a contradiction. No response is ever buffered for a future challenge to consume.
    /// </summary>
    private bool TryHandleChallengeResponse(WebProtocolMessage message)
    {
        string nonce = message.Nonce!; // WebFrameDecoder guarantees non-null for a decoded ChallengeResponse.

        TaskCompletionSource<WebChallengeResult>? toComplete = null;
        WebChallengeResult completionResult = default;
        bool isContradiction = false;

        lock (_challengeGate)
        {
            if (_pendingChallenge is not null && string.Equals(_pendingNonce, nonce, StringComparison.Ordinal))
            {
                toComplete = _pendingChallenge;
                completionResult = new WebChallengeResult(
                    WebChallengeOutcome.Success, message.Focus!.Value, message.OriginResolution!.Value, message.Origin);
                _pendingChallenge = null;
                _pendingNonce = null;
                _retiredNonce = nonce; // this challenge just concluded normally -- its nonce retires.
            }
            else if (_retiredNonce is not null && string.Equals(_retiredNonce, nonce, StringComparison.Ordinal))
            {
                _retiredNonce = null; // first late/foreign copy: tolerated exactly once, then forgotten.
            }
            else
            {
                isContradiction = true;
            }
        }

        toComplete?.TrySetResult(completionResult);
        return !isContradiction;
    }

    private void CompletePendingChallenge(TaskCompletionSource<WebChallengeResult> tcs, WebChallengeResult result)
    {
        lock (_challengeGate)
        {
            if (ReferenceEquals(_pendingChallenge, tcs))
            {
                _pendingChallenge = null;
                _pendingNonce = null;
            }
        }

        tcs.TrySetResult(result);
    }

    private void CompletePendingChallengeAsClosed()
    {
        TaskCompletionSource<WebChallengeResult>? pending;
        lock (_challengeGate)
        {
            pending = _pendingChallenge;
            _pendingChallenge = null;
            _pendingNonce = null;
        }

        pending?.TrySetResult(new WebChallengeResult(WebChallengeOutcome.SessionClosed, BrowserFocus.Unresolved, OriginResolution.Unresolved, null));
    }

    private static WebChallengeResult NotAcceptedResult() =>
        new(WebChallengeOutcome.NotAccepted, BrowserFocus.Unresolved, OriginResolution.Unresolved, null);

    private static WebChallengeResult BusyResult() =>
        new(WebChallengeOutcome.Busy, BrowserFocus.Unresolved, OriginResolution.Unresolved, null);

    private static WebChallengeResult WriteFailedResult() =>
        new(WebChallengeOutcome.WriteFailed, BrowserFocus.Unresolved, OriginResolution.Unresolved, null);

    /// <summary>Gate 031F6L (E4) -- the native App is the sole nonce authority: 32 cryptographically
    /// random bytes, Base64URL-encoded without padding (exactly the frozen 43-character wire shape).
    /// Never persisted, never logged, never reused, never extension-supplied.</summary>
    private static string GenerateNonce()
    {
        Span<byte> bytes = stackalloc byte[NonceRandomByteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private async Task RunAsync()
    {
        try
        {
            byte[] frameBuffer = new byte[FrameBufferCapacity];
            var bufferStream = new MemoryStream(frameBuffer, writable: true);

            var (helloStatus, helloBytes) = await ReadOneFrameAsync(bufferStream).ConfigureAwait(false);
            if (helloStatus != WebPumpRelayStatus.Relayed)
                return;

            var helloDecodeStatus = WebFrameDecoder.Decode(helloBytes, out var helloMessage);
            if (helloDecodeStatus != WebFrameDecodeStatus.Ok || helloMessage.Type != WebProtocolMessageType.Hello)
                return;

            if (!_registry.TryConnect(_browserProcessId, out long channelId))
            {
                await TryWriteBestEffortAckAsync(accepted: false).ConfigureAwait(false);
                return;
            }

            Volatile.Write(ref _channelId, channelId);

            var ackEncodeStatus = WebFrameEncoder.Encode(
                new WebProtocolMessage(WebProtocolMessageType.HelloAck, true, null, null, null, null),
                out byte[]? ackFrame);
            if (ackEncodeStatus != WebFrameEncodeStatus.Ok || ackFrame is null)
                return;

            await _transport.WriteAsync(ackFrame).ConfigureAwait(false);
            await _transport.FlushAsync().ConfigureAwait(false);

            if (!_manager.TryPromoteToAccepted(this))
                return;

            Volatile.Write(ref _accepted, 1);

            while (true)
            {
                var (status, frameBytes) = await ReadOneFrameAsync(bufferStream).ConfigureAwait(false);
                if (status != WebPumpRelayStatus.Relayed)
                    return;

                var decodeStatus = WebFrameDecoder.Decode(frameBytes, out var message);
                if (decodeStatus != WebFrameDecodeStatus.Ok)
                    return;

                switch (message.Type)
                {
                    case WebProtocolMessageType.StateInvalidate:
                        if (!_registry.TryInvalidate(channelId, out _))
                            return;
                        break;

                    case WebProtocolMessageType.StateAssert:
                        if (message.Focus is null || message.OriginResolution is null)
                            return;
                        if (!_registry.TryAssert(channelId, message.Focus.Value, message.OriginResolution.Value, message.Origin, out _))
                            return;
                        break;

                    case WebProtocolMessageType.ChallengeResponse:
                        // Gate 031F6L (E4) -- the one Accepted-state protocol extension. Dispatched to
                        // the pending-challenge correlation mechanism only; RunAsync never inspects
                        // challenge/nonce state itself. A contradiction (wrong nonce, unsolicited, or a
                        // second stray copy of an already-cleared retired nonce) tears the session down,
                        // exactly like a failed StateInvalidate/StateAssert above.
                        if (message.Focus is null || message.OriginResolution is null)
                            return;
                        if (!TryHandleChallengeResponse(message))
                            return;
                        break;

                    default:
                        // Hello again, HelloAck, or ChallengeRequest -- still contradictions once
                        // Accepted (Gate 031F6H R15/R27; Gate 031F6L E4_VERSIONED_EXTENSION does not
                        // broaden this set any further).
                        return;
                }
            }
        }
        catch
        {
            // Contained here -- Lifecycle must never fault (Gate 031F6H section 15). A bad client's
            // transport fault, a disposed stream from the R45B residual, or any other unexpected
            // failure all resolve identically: this session simply tears itself down.
        }
        finally
        {
            Close();
        }
    }

    private async Task<(WebPumpRelayStatus Status, byte[] Frame)> ReadOneFrameAsync(MemoryStream bufferStream)
    {
        bufferStream.Position = 0;
        bufferStream.SetLength(0);
        var status = await WebNativeMessagingPump.RelayOneFrameAsync(_transport, bufferStream).ConfigureAwait(false);
        return (status, bufferStream.ToArray());
    }

    private async Task TryWriteBestEffortAckAsync(bool accepted)
    {
        try
        {
            var status = WebFrameEncoder.Encode(
                new WebProtocolMessage(WebProtocolMessageType.HelloAck, accepted, null, null, null, null),
                out byte[]? frame);
            if (status == WebFrameEncodeStatus.Ok && frame is not null)
            {
                await _transport.WriteAsync(frame).ConfigureAwait(false);
                await _transport.FlushAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort only -- the caller closes regardless (Gate 031F6H R13).
        }
    }

    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch
        {
            // Close never throws (Gate 031F6H section 19) -- a throwing binding/transport Dispose must
            // never skip the remaining cleanup steps, especially the final Lease release.
        }
    }
}
