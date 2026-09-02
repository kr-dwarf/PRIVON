namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F4.1 (frozen contract) / Gate 031F4.3 (this type) -- the App-owned,
/// purely mechanical counter identifying the current uninterrupted foreground-occupancy interval.
/// It exists so a previously-authorized decision can later be re-checked against "is the foreground
/// still the one that was authorized", without any layer below <c>Privon.App</c> needing to know
/// that such a question is being asked.
///
/// FACTS_ONLY: no product, browser, publisher, or origin policy of any kind lives here, and none
/// ever will -- this type cannot express one. It performs no logging, no persistence, no timestamps,
/// no grace period, and no TTL. It never inspects a window handle, a process identity, or a window
/// title: the signal it consumes (<see cref="IClipboardForegroundTrigger.Changed"/>) is structurally
/// payload-free, which is exactly why no de-duplication is possible or attempted here.
///
/// EPOCH_DOMAIN (frozen, see <see cref="IForegroundEpochSource"/>): a valid epoch is
/// <c>1 .. long.MaxValue</c>. <c>0</c> is not an epoch -- it is the one fail-closed sentinel for
/// UNAVAILABLE, covering "not yet established", "exhausted", and "disposed" identically.
///
/// INITIAL_EPOCH = UNESTABLISHED_ZERO_UNTIL_CAPTURE (Gate 031F4.1, chosen on safety): a freshly
/// constructed tracker reports <c>0</c>, so nothing can be authorized until an epoch is genuinely
/// established. Starting at 1 instead would assert an interval whose beginning was never observed,
/// and a foreground transition occurring before the native hook is installed would then be spanned
/// by that epoch rather than invalidating it. The cost of this choice is a pure false negative.
///
/// NO_DEDUP (frozen): every accepted <see cref="IClipboardForegroundTrigger.Changed"/> callback
/// advances the epoch exactly once -- no debounce, no coalescing, no window-handle comparison. A
/// duplicate or spurious signal therefore invalidates a still-valid proof, which is a SAFE false
/// negative (the next attempt simply re-authorizes); the converse -- missing a real transition and
/// authorizing across it -- is structurally impossible, since every real transition raises at least
/// one signal and every signal advances.
///
/// EXHAUSTION (frozen): reaching <c>long.MaxValue</c> as a value is legal and usable. An attempt to
/// advance BEYOND it latches a permanent, sticky unavailable state rather than wrapping, going
/// negative, or reusing an epoch -- mirroring <c>Privon.Browser.WebChannelRegistry</c>'s own
/// established "refuse rather than wrap" precedent for its ChannelId allocator. Once latched,
/// <see cref="CurrentEpoch"/> reports <c>0</c> forever and nothing revives it.
///
/// SYNCHRONIZATION: <see cref="IClipboardForegroundTrigger.Changed"/> is raised synchronously on the
/// foreground monitor's own dedicated owner thread, while <see cref="CurrentEpoch"/> is read from the
/// clipboard owner thread. One private monitor covers every read and every transition, so the two
/// reads a freshness bracket performs are each individually linearizable against any interleaved
/// advance. No <c>Interlocked</c> pair is used instead, deliberately: the exhaustion latch and the
/// counter must be updated together, atomically, and a lock states that far more plainly than a
/// hand-rolled compare-exchange loop would.
/// </summary>
internal sealed class ForegroundEpochTracker : IForegroundEpochSource, IDisposable
{
    private readonly object _gate = new();
    private readonly IClipboardForegroundTrigger _trigger;

    // The one and only mutable counter. Meaningful ONLY while _unavailable is false -- see
    // CurrentEpoch, which is the single place the two are ever combined into an answer.
    private long _epoch;

    // Latched, never cleared: set by exhaustion and by disposal alike, because from an authorization
    // point of view those two are the same fact -- this tracker can no longer vouch for the current
    // foreground interval.
    private bool _unavailable;

    private bool _disposed;

    public ForegroundEpochTracker(IClipboardForegroundTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        _trigger = trigger;

        // SUBSCRIBE_IN_CONSTRUCTOR (Gate 031F4.1 TRACKER_SUBSCRIBES_FIRST): subscribing here, rather
        // than in a separate Start method, is what makes composition order alone sufficient to
        // guarantee this tracker observes a foreground signal before any handler attached later --
        // notably the coordinator's, which subscribes during its own Start. Both handlers run
        // synchronously on the same single owner thread, so invocation order is subscription order.
        _trigger.Changed += OnForegroundChanged;
    }

    /// <summary>See <see cref="IForegroundEpochSource.CurrentEpoch"/>. Returns <c>0</c> whenever this
    /// tracker is unavailable for ANY reason (never established, exhausted, or disposed) -- a caller
    /// never needs to distinguish those cases, and no valid proof can carry epoch 0.</summary>
    public long CurrentEpoch
    {
        get
        {
            lock (_gate)
            {
                return _unavailable ? 0 : _epoch;
            }
        }
    }

    /// <summary>
    /// The <c>0 -> 1</c> transition, and nothing else. Intended to be called exactly once, by the
    /// composition root, only AFTER the native foreground hook is provably installed -- so the
    /// interval epoch 1 denotes is bounded, on both sides, by observed events.
    ///
    /// A no-op when an epoch is already established, when exhausted, or when disposed. It never
    /// decrements, resets, revives, or reuses an epoch.
    /// </summary>
    public void EstablishInitialEpoch()
    {
        lock (_gate)
        {
            if (_unavailable)
                return;
            if (_epoch > 0)
                return;

            _epoch = 1;
        }
    }

    private void OnForegroundChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_unavailable)
                return;

            // Checked BEFORE the increment, never after -- an increment performed first would
            // already have overflowed to long.MinValue by the time any guard could observe it.
            if (_epoch == long.MaxValue)
            {
                _unavailable = true;
                return;
            }

            _epoch++;
        }
    }

    /// <summary>
    /// Unsubscribes and makes this tracker permanently unavailable. Idempotent -- repeated calls are
    /// a silent no-op, matching this codebase's established Dispose discipline. Unlike the native
    /// monitors' own DISPOSE_FAILURE_BEHAVIOR, there is no cleanup here that can fail: this type owns
    /// no native resource, only an event subscription.
    ///
    /// Deliberately does NOT make <see cref="CurrentEpoch"/> or <see cref="EstablishInitialEpoch"/>
    /// throw <see cref="ObjectDisposedException"/> afterwards -- a late caller on a shutting-down
    /// graph must fail CLOSED (epoch 0, so nothing authorizes), never fail loudly into a guarded
    /// clipboard path.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _unavailable = true;
        }

        _trigger.Changed -= OnForegroundChanged;
    }
}
