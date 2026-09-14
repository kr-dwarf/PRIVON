using Privon.Browser;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6L (E4) -- the concrete, decision-time <see cref="IWebClipboardAuthorizationSource"/>
/// implementation. Bracketed foreground capture (Gate 031F3 CHALLENGE_CONTEXT_MODEL): the exact ordering
/// is frozen -- E1 (epoch) read, F1 validated, an Accepted session located for F1's PID, opening evidence
/// (EV1) read, a real decision-time challenge performed, THEN a fresh F2 capture, EV2, E2, a same-session
/// identity re-check, and finally the full A-H <see cref="WebTargetGate.Match"/> formula. Every rejection
/// point returns <see langword="null"/> (Outside) -- this type never throws for an ordinary decline, and
/// it never manufactures a <see cref="WebChallengeProof"/> except from a genuinely successful, internally
/// consistent bracket.
///
/// PRODUCTION_WIRING (Gate 031F6L origin, superseded at Gate 031F6I.1/E5F): a single concrete instance of
/// this type IS now constructed by <see cref="PrivonAppComposition"/> -- see that type's own
/// SHARED_CHANNEL_TRUTH doc -- against the exact same <see cref="WebChannelRegistry"/>/
/// <see cref="WebChannelManager"/> pair the real Web channel host server (<see cref="WebServerRuntime"/>)
/// consults, never an independently-constructed registry/manager. Construction is not authorization:
/// production accepts only an exact origin in <see cref="WebExtensionOriginAllowlist.Production"/> and
/// still requires this type's complete decision-time identity, session, challenge, and freshness bracket.
///
/// NO_TRANSPORT_ACCESS (Gate 031F6L G14): this type never reads from or writes to a
/// <see cref="System.IO.Stream"/> directly, and never introduces a second transport reader or dispatcher
/// -- it only calls <see cref="WebChannelSession.ChallengeAsync"/>, which is itself the sole owner of the
/// wire-level exchange.
/// </summary>
internal sealed class WebClipboardAuthorizationSource : IWebClipboardAuthorizationSource
{
    /// <summary>Frozen E4 default (Gate 031F6L) -- NOT the E5 real-device final tuning value.</summary>
    public static readonly TimeSpan ChallengeTimeoutContractDefault = TimeSpan.FromSeconds(1);

    private readonly WebChannelManager _manager;
    private readonly WebChannelRegistry _registry;
    private readonly IForegroundTargetCapture _foregroundCapture;
    private readonly IForegroundEpochSource _epochSource;
    private readonly TimeSpan _challengeTimeout;

    public WebClipboardAuthorizationSource(
        WebChannelManager manager,
        WebChannelRegistry registry,
        IForegroundTargetCapture foregroundCapture,
        IForegroundEpochSource epochSource)
        : this(manager, registry, foregroundCapture, epochSource, ChallengeTimeoutContractDefault)
    {
    }

    public WebClipboardAuthorizationSource(
        WebChannelManager manager,
        WebChannelRegistry registry,
        IForegroundTargetCapture foregroundCapture,
        IForegroundEpochSource epochSource,
        TimeSpan challengeTimeout)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(foregroundCapture);
        ArgumentNullException.ThrowIfNull(epochSource);

        _manager = manager;
        _registry = registry;
        _foregroundCapture = foregroundCapture;
        _epochSource = epochSource;
        _challengeTimeout = challengeTimeout;
    }

    public async Task<WebClipboardAuthorization?> AuthorizeAsync(ForegroundTargetSnapshot foreground)
    {
        // 1. E1 -- read early per the frozen bracket ordering; VALIDATED later, alongside E2, so a
        // genuine challenge round trip is always attempted regardless of E1's eventual validity.
        long e1 = _epochSource.CurrentEpoch;

        // 2. supplied F1 must already be a resolved, identifiable process.
        if (!foreground.IsResolved || foreground.ProcessId == 0)
            return null;

        // 3/4. accepted-session lookup + identity/ChannelId for F1's PID.
        if (!_manager.TryGetAcceptedSession(foreground.ProcessId, out var session) || session is null)
            return null;

        long? channelId = session.ChannelId;
        if (channelId is null)
            return null;

        // 5. EV1 -- opening evidence for the same channel.
        if (!_registry.TryGetCurrentEvidence(channelId.Value, out var ev1))
            return null;

        // 6. structural sanity only -- the registry's own invariants already guarantee this, but never
        // trust evidence describing a different channel/process than the one just looked up.
        if (ev1.ChannelId != channelId.Value || ev1.BrowserProcessId != foreground.ProcessId)
            return null;

        // 7. the decision-time challenge itself -- session-owned, mechanical, no policy.
        var challengeResult = await session.ChallengeAsync(_challengeTimeout).ConfigureAwait(false);

        // 8. only a genuine Success carries mechanical facts worth evaluating.
        if (challengeResult.Outcome != WebChallengeOutcome.Success)
            return null;

        // 9. fresh F2, captured only AFTER the challenge -- never reused from step 2.
        var f2 = _foregroundCapture.Capture();

        // 10. EV2 -- closing evidence, re-read fresh from the same channel.
        if (!_registry.TryGetCurrentEvidence(channelId.Value, out var ev2))
            return null;

        // 11. E2 -- closing epoch read, independent of E1's own read.
        long e2 = _epochSource.CurrentEpoch;

        // 12/13. same-session identity re-check -- PID alone is never sufficient (Gate 031F6L SESSION
        // REPLACEMENT): a same-PID reconnect always allocates a brand-new session/ChannelId, and a
        // stale caller's own old session object must never authorize through it.
        if (!_manager.TryGetAcceptedSession(foreground.ProcessId, out var currentSession) || currentSession is null)
            return null;
        if (!ReferenceEquals(currentSession, session))
            return null;

        // 14. opening/closing consistency -- zero-identifier fail-closed BEFORE any equality comparison
        // is trusted (mirrors WebTargetGate's own ZERO_IDENTIFIER_FAIL_CLOSED discipline), then the
        // actual bracket equalities. A revision drift here means StateInvalidate or a fresh StateAssert
        // advanced the channel mid-challenge -- Outside, never a mix of old and new evidence.
        if (e1 == 0 || e2 == 0 || e1 != e2)
            return null;
        if (!f2.IsResolved || f2.ProcessId == 0)
            return null;
        if (f2.ProcessId != foreground.ProcessId || f2.ProcessId != session.BrowserProcessId)
            return null;
        if (ev2.ChannelId != channelId.Value || ev2.BrowserProcessId != foreground.ProcessId)
            return null;
        if (ev1.EvidenceRevision != ev2.EvidenceRevision)
            return null;

        // ChallengeResponse mechanical facts must agree with the closing evidence -- never a mixed-time
        // proof built from what the browser claimed versus what the channel now actually reports.
        if (challengeResult.Focus != ev2.BrowserFocus
            || challengeResult.OriginResolution != ev2.OriginResolution
            || challengeResult.Origin != ev2.Origin)
        {
            return null;
        }

        // 15. proof -- exactly the four frozen fields, bound to this decision's own closing facts.
        var proof = new WebChallengeProof(channelId.Value, ev2.EvidenceRevision, e2, foreground.ProcessId);

        // 16. decision context + the App-owned epoch pairing (Gate 031C FOREGROUND_EPOCH_OWNERSHIP:
        // WebChannelRegistry's own evidence always carries ForegroundEpoch == 0 -- this type is exactly
        // the place documented to pair it with a fresh native epoch before any policy comparison).
        var evidenceForMatch = ev2 with { ForegroundEpoch = e2 };
        var context = new WebDecisionContext(ev2.EvidenceRevision, channelId.Value, e2, proof);

        // 17/18. the full, existing A-H formula -- never duplicated here.
        var target = WebTargetGate.Match(f2, evidenceForMatch, context);
        if (target is null)
            return null;

        // 19/20. freshness bound to the EXACT proof this decision produced -- never a later, independent
        // registry snapshot.
        var freshness = new WebClipboardAuthorizationFreshness(proof, _registry, _epochSource, _foregroundCapture);
        return new WebClipboardAuthorization(target.Value, freshness);
    }
}
