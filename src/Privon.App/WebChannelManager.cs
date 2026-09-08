using Privon.Browser;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- owns two distinct structures with two distinct meanings (Gate
/// 031F6H section 12): <c>_live</c>, every admission-started session retained until it Closes, and
/// the PID-keyed accepted-visibility map, holding only sessions that have actually completed the
/// Hello/HelloAck handshake and been promoted. One shared shutdown flag (<c>_stopping</c>) governs
/// both the shutdown admission barrier (section 13) and promotion (section 16).
/// </summary>
internal sealed class WebChannelManager
{
    private readonly object _gate = new();
    private readonly WebChannelRegistry _registry;
    private readonly HashSet<WebChannelSession> _live = new();
    private readonly Dictionary<uint, WebChannelSession> _accepted = new();
    private bool _stopping;

    public WebChannelManager(WebChannelRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public void BeginShutdown()
    {
        lock (_gate)
        {
            _stopping = true;
        }
    }

    /// <summary>Total, non-throwing (Gate 031F6H section 13): admits <paramref name="session"/> into
    /// <c>_live</c> and starts it, UNLESS shutdown has already begun, in which case the session is
    /// closed instead and never enters <c>_live</c> or gets started. Ownership of
    /// <paramref name="session"/>'s ownedresources has already fully transferred to it by the time
    /// this is called (Gate 031F6H section 7) -- this method never reclaims them itself; it only ever
    /// asks the session to close itself.</summary>
    public void BeginAdmission(WebChannelSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        bool rejected;
        lock (_gate)
        {
            if (_stopping)
            {
                rejected = true;
            }
            else
            {
                _live.Add(session);
                rejected = false;
            }
        }

        if (rejected)
            session.Close();
        else
            session.Start();
    }

    public IReadOnlyList<WebChannelSession> SnapshotLive()
    {
        lock (_gate)
        {
            return _live.ToArray();
        }
    }

    /// <summary>Atomically, under the manager lock (Gate 031F6H section 16): rejects if shutdown has
    /// begun, rejects if <paramref name="session"/> is already Closed (the R45B residual and the R32
    /// Close-before-promotion race both resolve here), and rejects a duplicate live PID -- the
    /// original accepted session for that PID is never disturbed. No race window exists between the
    /// duplicate-PID check and the map insertion; both happen inside the same lock acquisition.</summary>
    public bool TryPromoteToAccepted(WebChannelSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            if (_stopping)
                return false;

            if (session.State == WebChannelSessionState.Closed)
                return false;

            uint pid = session.BrowserProcessId;
            if (_accepted.ContainsKey(pid))
                return false;

            _accepted[pid] = session;
            return true;
        }
    }

    /// <summary>Identity-aware removal (Gate 031F6H R31): removes <paramref name="session"/> from
    /// <c>_live</c> unconditionally, but only removes it from the accepted map if it is still the
    /// CURRENT entry for <paramref name="browserProcessId"/> -- a stale, already-superseded session's
    /// own (possibly late) Close call must never evict a newer session that has since reconnected for
    /// the same PID.</summary>
    public void RemoveIfCurrent(uint browserProcessId, WebChannelSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            if (_accepted.TryGetValue(browserProcessId, out var current) && ReferenceEquals(current, session))
                _accepted.Remove(browserProcessId);

            _live.Remove(session);
        }
    }

    /// <summary>Manager lookup exposes only a currently-Accepted session (Gate 031F6H R28) -- never
    /// AwaitingHello, never Closed. The defensive <see cref="WebChannelSessionState.Accepted"/> check
    /// (beyond mere map presence) closes the narrow window between a session's own atomic Close
    /// (State flips to Closed immediately) and its later <see cref="RemoveIfCurrent"/> call (which
    /// physically removes the map entry) -- a lookup landing inside that window must still never
    /// report the session as accepted.
    ///
    /// LIVENESS (Gate 031F6H section 17): an Accepted session is only ever returned while its own
    /// browser binding reports <see cref="Privon.Windows.RetainedProcessLiveness.Alive"/>. For
    /// Exited/Unavailable, accepted visibility is removed HERE, under the manager lock, but the
    /// session's own <see cref="WebChannelSession.Close"/> is deliberately invoked OUTSIDE the lock --
    /// Close's own teardown (registry disconnect, binding/transport dispose, Lease release) is
    /// potentially destructive/slow work that must never run while other callers are blocked waiting
    /// on this manager's own lock.</summary>
    public bool TryGetAcceptedSession(uint browserProcessId, out WebChannelSession? session)
    {
        WebChannelSession? toClose = null;

        lock (_gate)
        {
            if (_accepted.TryGetValue(browserProcessId, out var found) && found.State == WebChannelSessionState.Accepted)
            {
                if (found.CheckBindingLiveness() == Privon.Windows.RetainedProcessLiveness.Alive)
                {
                    session = found;
                    return true;
                }

                _accepted.Remove(browserProcessId);
                toClose = found;
            }
        }

        toClose?.Close();

        session = null;
        return false;
    }
}
