using Privon.Core;

namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031F2 (retention corrected by Gate 031F2.1) -- the mechanical channel
/// registry and invalidate-before-assert state machine frozen by Gate 031E2
/// (CHANNEL_OWNER/STATE_MACHINE). A single, in-process, thread-safe owner of: ChannelId
/// assignment, the one-live-channel-per-browser-process invariant, and the per-channel evidence
/// lifecycle (Unresolved -> Asserted -> Unresolved -> ... -> Disconnected, where Disconnected is
/// represented by the channel's entry no longer existing at all -- see
/// <see cref="TryDisconnect"/>'s own doc). Contains no product-specific browser, publisher,
/// target, or origin policy of any kind, and no supported-origin allowlist -- see this
/// assembly's own structural regression lock
/// (<c>Gate031_WebAuthorizationContractTests.Group18_PrivonBrowserAssembly_ContainsNoProductPolicyStrings</c>).
///
/// RETENTION (Gate 031F2.1 correction): a disconnected channel's <see cref="ChannelEntry"/> is
/// removed from <see cref="_channels"/> immediately -- this type maintains NO tombstone/retired-id
/// collection of any kind. Terminal-ness for an old ChannelId is proven entirely by the
/// monotonically-increasing, never-reused <see cref="_lastAssignedChannelId"/> allocator: a
/// disconnected id can never collide with a future id, so a bare "no entry found" is already
/// indistinguishable from, and exactly as fail-closed as, "this id was previously disconnected".
/// A registry that connects and disconnects the same (or many different) PIDs repeatedly holds
/// storage proportional only to its currently-LIVE channel count, never to the total number of
/// connections ever made.
///
/// SCOPE_BOUNDARY (Gate 031F2, deliberate): this type does NOT discover, verify, or retain a
/// native process handle for <c>BrowserProcessId</c> -- that PID is a caller-supplied mechanical
/// fact, exactly as a future Native Messaging host (which WILL derive it from the connecting
/// process's parent) is expected to supply it, without this type's own API or state semantics
/// needing to change. This type does not read raw bytes, parse JSON, or know anything about
/// <see cref="WebFrameDecoder"/>/<see cref="WebProtocolMessage"/> -- decoding wire bytes into
/// mechanical facts and transitioning state from those facts are deliberately separate concerns.
/// It implements no Hello/HelloAck handshake and no challenge/response of any kind (Gate 031E2
/// terms C/H remain entirely out of this type's scope -- see <c>WebChallengeProof</c>, not yet
/// implemented).
///
/// REVISION_MODEL: each channel owns its own <see cref="RevisionTracker"/> -- the SAME production
/// type this codebase already uses for the Windows clipboard-attempt generation, applied here via
/// <see cref="RevisionTracker.ForceAdvance"/> (its own documented purpose: "bumps the revision
/// without regard to text content ... for ... events that must still invalidate prior validations"
/// -- exactly what a browser-side Invalidate/Assert transition is). A freshly connected channel's
/// evidence carries <see cref="RevisionId.None"/> (0) -- <see cref="RevisionTracker"/>'s own
/// un-advanced starting value, matching this codebase's established convention that 0 means
/// "nothing has been observed/asserted yet", never a live asserted state. The first
/// <see cref="TryInvalidate"/> or <see cref="TryAssert"/> call on a channel performs that
/// channel's first <c>ForceAdvance</c>, producing revision 1.
///
/// INVALIDATE_BEFORE_ASSERT (Gate 031E2 RACE_CONTRACT, Gate 031F2 section 9, frozen): a channel
/// currently in the <c>Asserted</c> state rejects a further <see cref="TryAssert"/> outright -- an
/// intervening <see cref="TryInvalidate"/> (or the channel's own initial connect-time Unresolved
/// state, for the very first assert) is structurally required first. This is enforced by a single
/// state check, not by tracking "was there an invalidate since the last assert" separately.
///
/// FOREGROUND_EPOCH_OWNERSHIP (Gate 031E2, unchanged by this type): every evidence instance this
/// registry ever produces carries <c>ForegroundEpoch == 0</c> -- ownership of that field belongs
/// exclusively to <c>Privon.App</c> (via the existing <c>IClipboardForegroundTrigger</c> seam),
/// which stamps a real epoch only when it later pairs this registry's evidence with a fresh native
/// foreground capture. This type never claims to know the OS foreground state.
///
/// SYNCHRONIZATION: every public member acquires one private monitor (<c>lock (_gate)</c>) for its
/// entire body -- connect, disconnect, invalidate, assert, and current-evidence read are therefore
/// fully linearizable relative to one another. No callback of any kind is invoked while the lock is
/// held, no member is <see langword="async"/>, and every returned <see cref="WebForegroundEvidence"/>
/// is an independent value-type snapshot (it is a <see langword="readonly record struct"/>) -- no
/// caller can observe or mutate this registry's own internal state by reference.
///
/// PRIVACY: this type persists nothing beyond process memory, performs no logging of any kind, and
/// holds no more than the frozen seven-field <see cref="WebForegroundEvidence"/> shape already
/// permits per channel -- see that type's own PRIVACY_SHAPE doc.
/// </summary>
public sealed class WebChannelRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, long> _liveChannelIdByProcessId = new();
    private readonly Dictionary<long, ChannelEntry> _channels = new();
    private long _lastAssignedChannelId; // 0 == "none assigned yet"; first real ChannelId is 1

    /// <summary>
    /// Establishes a new channel for <paramref name="browserProcessId"/>. Fails
    /// (<paramref name="channelId"/> stays 0) when: <paramref name="browserProcessId"/> is 0
    /// (never a valid PID); a channel for that PID is already live (ONE_LIVE_CHANNEL_PER_PID,
    /// Gate 031E2 -- a second concurrent attempt for the same PID never "wins", it is simply
    /// refused); or the ChannelId counter has already reached <see cref="long.MaxValue"/> (fails
    /// closed rather than wrapping or reusing a retired id -- Gate 031F2 section 2).
    ///
    /// On success, the new channel's initial evidence is
    /// <c>(browserProcessId, channelId, RevisionId.None, BrowserFocus.Unresolved,
    /// OriginResolution.Unresolved, null, ForegroundEpoch: 0)</c> -- see this type's own
    /// REVISION_MODEL/FOREGROUND_EPOCH_OWNERSHIP docs for why revision and epoch both start at
    /// their safe zero value rather than an already-advanced one.
    /// </summary>
    public bool TryConnect(uint browserProcessId, out long channelId)
    {
        channelId = 0;
        if (browserProcessId == 0)
            return false;

        lock (_gate)
        {
            if (_liveChannelIdByProcessId.ContainsKey(browserProcessId))
                return false;

            if (_lastAssignedChannelId == long.MaxValue)
                return false;

            long newChannelId = ++_lastAssignedChannelId;
            var entry = new ChannelEntry(browserProcessId, newChannelId);
            _channels.Add(newChannelId, entry);
            _liveChannelIdByProcessId.Add(browserProcessId, newChannelId);

            channelId = newChannelId;
            return true;
        }
    }

    /// <summary>Reads the current evidence for <paramref name="channelId"/> without transitioning
    /// any state. Fails for an unknown or already-disconnected channel -- a disconnected channel's
    /// evidence is never readable as current again.</summary>
    public bool TryGetCurrentEvidence(long channelId, out WebForegroundEvidence evidence)
    {
        evidence = default;

        lock (_gate)
        {
            if (!TryGetLiveEntry(channelId, out var entry))
                return false;

            evidence = entry.CurrentEvidence;
            return true;
        }
    }

    /// <summary>
    /// The one mechanical invalidation transition (Gate 031F2 section 7/17): represents every
    /// browser-side event this registry needs to react to (tab change, navigation commit,
    /// SPA/history change, focused-window change, window close, extension startup/reload) --
    /// this type does not need, and deliberately does not have, a separate API per browser event.
    /// Fails for an unknown or already-disconnected channel.
    ///
    /// On success: revision advances; evidence becomes
    /// <c>(BrowserProcessId, ChannelId, newRevision, BrowserFocus.Unresolved,
    /// OriginResolution.Unresolved, null, ForegroundEpoch: 0)</c> -- no prior focus/origin is
    /// retained anywhere, even internally; there is no hidden "last good origin" a later read could
    /// ever recover.
    /// </summary>
    public bool TryInvalidate(long channelId, out WebForegroundEvidence evidence)
    {
        evidence = default;

        lock (_gate)
        {
            if (!TryGetLiveEntry(channelId, out var entry))
                return false;

            var revision = entry.Revisions.ForceAdvance().RevisionId;
            entry.State = ChannelLifecycleState.Unresolved;
            entry.CurrentEvidence = new WebForegroundEvidence(
                entry.BrowserProcessId, channelId, revision,
                BrowserFocus.Unresolved, OriginResolution.Unresolved, Origin: null, ForegroundEpoch: 0);

            evidence = entry.CurrentEvidence;
            return true;
        }
    }

    /// <summary>
    /// Asserts new mechanical browser facts on a live channel. Fails, with NO state change and NO
    /// revision advance, for: an unknown/disconnected channel; an out-of-range
    /// <paramref name="focus"/>/<paramref name="originResolution"/> value; a violation of the
    /// frozen Origin presence invariant (<paramref name="originResolution"/> ==
    /// <see cref="OriginResolution.Resolved"/> requires a non-null, non-empty
    /// <paramref name="origin"/>; any other value requires <paramref name="origin"/> to be
    /// <see langword="null"/>); or -- INVALIDATE_BEFORE_ASSERT (Gate 031F2 section 9) -- a channel
    /// that is currently <see cref="ChannelLifecycleState.Asserted"/> already. The very first
    /// assert on a freshly connected channel IS allowed (its initial state is
    /// <see cref="ChannelLifecycleState.Unresolved"/>, not <see cref="ChannelLifecycleState.Asserted"/>).
    ///
    /// On success: revision advances; evidence becomes the asserted facts, with
    /// <c>Origin</c> populated only when <paramref name="originResolution"/> is
    /// <see cref="OriginResolution.Resolved"/>.
    /// </summary>
    public bool TryAssert(long channelId, BrowserFocus focus, OriginResolution originResolution, string? origin, out WebForegroundEvidence evidence)
    {
        evidence = default;

        if (!Enum.IsDefined(focus) || !Enum.IsDefined(originResolution))
            return false;

        bool resolved = originResolution == OriginResolution.Resolved;
        if (resolved && string.IsNullOrEmpty(origin))
            return false;
        if (!resolved && origin is not null)
            return false;

        lock (_gate)
        {
            if (!TryGetLiveEntry(channelId, out var entry))
                return false;

            if (entry.State == ChannelLifecycleState.Asserted)
                return false; // INVALIDATE_BEFORE_ASSERT -- no direct Asserted -> Asserted replacement

            var revision = entry.Revisions.ForceAdvance().RevisionId;
            entry.State = ChannelLifecycleState.Asserted;
            entry.CurrentEvidence = new WebForegroundEvidence(
                entry.BrowserProcessId, channelId, revision,
                focus, originResolution, resolved ? origin : null, ForegroundEpoch: 0);

            evidence = entry.CurrentEvidence;
            return true;
        }
    }

    /// <summary>
    /// Marks <paramref name="channelId"/> permanently terminal (Gate 031F2 section 10, retention
    /// corrected by Gate 031F2.1): its <see cref="ChannelEntry"/> is REMOVED from
    /// <see cref="_channels"/> outright -- terminal-ness is represented by ABSENCE, never by a
    /// retained tombstone/disconnected-marker entry (Gate 031F2.1: "No retired-id set is
    /// necessary. The monotonic <c>_lastAssignedChannelId</c> is sufficient proof of non-reuse.").
    /// Because <paramref name="channelId"/> was assigned by that same strictly-increasing counter
    /// and is never reissued, a subsequent <see cref="TryGetCurrentEvidence"/>/
    /// <see cref="TryInvalidate"/>/<see cref="TryAssert"/>/<see cref="TryDisconnect"/> call for it
    /// finds no entry and fails closed exactly as if the id had never existed -- no separate
    /// "was this id ever disconnected" check is needed or present anywhere in this type. Its
    /// owning <c>BrowserProcessId</c> is freed, atomically with the removal, for a future,
    /// entirely independent <see cref="TryConnect"/> (which always allocates a NEW ChannelId).
    /// Fails (returns <see langword="false"/>) for an unknown or already-disconnected channel --
    /// disconnecting twice is a no-op, never an error the caller must special-case, but also never
    /// re-triggers anything.
    /// </summary>
    public bool TryDisconnect(long channelId)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(channelId, out var entry))
                return false;

            _channels.Remove(channelId);
            _liveChannelIdByProcessId.Remove(entry.BrowserProcessId);
            return true;
        }
    }

    // Caller MUST already hold _gate. A disconnected channel has no entry at all (see
    // TryDisconnect's own doc) -- so simple dictionary presence is the ENTIRE liveness check;
    // there is no separate disconnected-state flag to also exclude.
    private bool TryGetLiveEntry(long channelId, out ChannelEntry entry) =>
        _channels.TryGetValue(channelId, out entry!);

    private enum ChannelLifecycleState { Unresolved, Asserted }

    // Mutable ONLY behind _gate; never exposed by reference to any caller -- every public method
    // above returns an independent WebForegroundEvidence value copy.
    private sealed class ChannelEntry(uint browserProcessId, long channelId)
    {
        public readonly uint BrowserProcessId = browserProcessId;
        public readonly RevisionTracker Revisions = new();
        public ChannelLifecycleState State = ChannelLifecycleState.Unresolved;
        public WebForegroundEvidence CurrentEvidence = new(
            browserProcessId, channelId, RevisionId.None,
            BrowserFocus.Unresolved, OriginResolution.Unresolved, Origin: null, ForegroundEpoch: 0);
    }
}
