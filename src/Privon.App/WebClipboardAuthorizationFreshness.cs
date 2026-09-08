using Privon.Browser;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F4.1 (frozen contract) / Gate 031F4.3 (this type) -- the App-side answer to
/// <c>Privon.Windows</c>'s opaque <see cref="IClipboardAuthorizationFreshness"/> question, for one
/// already-authorized Web clipboard operation. It answers exactly "is the state this operation was
/// authorized against still current, right now" -- and nothing else.
///
/// NOT_AN_AUTHORIZER (load-bearing): this type never authorizes anything. It consumes an
/// already-earned <see cref="WebChallengeProof"/> and re-checks it. It never constructs a proof,
/// never generates a nonce, never sends or receives a challenge, never restamps or repairs a
/// mismatched proof, and never mutates registry state (it calls exactly one read-only registry
/// member and nothing else).
///
/// NO_POLICY_REEVALUATION (Gate 031F4.1 POLICY_REVALIDATION): authorization already happened; this
/// is freshness enforcement, not a recomputation of product policy. This type never calls
/// <see cref="WebTargetGate"/>, never compares a signer organization or package identity against any
/// configured constant, and never sees an origin -- it does not retain one and does not read one.
/// The foreground capture it calls may MECHANICALLY populate signature/package facts as an internal
/// side effect of the single frozen capture primitive; this type reads only
/// <see cref="ForegroundTargetSnapshot.IsResolved"/> and
/// <see cref="ForegroundTargetSnapshot.ProcessId"/> from the result. Facts incidentally captured are
/// not policy re-evaluated.
///
/// NO_MEMOIZATION (frozen): nothing is cached between calls -- not the epoch, not the evidence, not
/// the snapshot, not a previous answer. Every <see cref="IsStillCurrent"/> call re-reads all live
/// state. This is what makes a single instance serving both the read and the write phase of one
/// protected attempt provably equivalent to a separately constructed instance per guarded operation.
///
/// LIFETIME: operation-scoped, created only after Web authorization succeeded, mirroring the existing
/// per-attempt foreground target token. Never static, never cached, never reused across attempts,
/// retries, or clipboard generations.
///
/// NO_CATCH_ALL (Gate 031F4.1 EXCEPTION_OWNER = WINDOWS_BOUNDARY): every dependency here already
/// expresses ordinary mechanical failure as a bool or a default value, so ordinary failure returns
/// <see langword="false"/> through plain control flow. A genuinely exceptional condition is allowed
/// to propagate: <c>ClipboardChangeMonitor</c>'s own single catch site is the one place an exception
/// is mapped to the same fail-closed outcome as a plain <see langword="false"/>. Duplicating that
/// here would mask real defects while changing nothing observable. Nothing is logged, ever.
/// </summary>
internal sealed class WebClipboardAuthorizationFreshness : IClipboardAuthorizationFreshness
{
    private readonly WebChallengeProof _proof;
    private readonly WebChannelRegistry _registry;
    private readonly IForegroundEpochSource _epochSource;
    private readonly IForegroundTargetCapture _foregroundCapture;

    public WebClipboardAuthorizationFreshness(
        WebChallengeProof proof,
        WebChannelRegistry registry,
        IForegroundEpochSource epochSource,
        IForegroundTargetCapture foregroundCapture)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(epochSource);
        ArgumentNullException.ThrowIfNull(foregroundCapture);

        _proof = proof;
        _registry = registry;
        _epochSource = epochSource;
        _foregroundCapture = foregroundCapture;
    }

    /// <summary>
    /// The frozen fifteen-step order (Gate 031F4.1 / Gate 031F4.3 section 11). The ordering is
    /// contract, not an implementation detail, for two independent reasons:
    ///
    /// ZERO_IDENTIFIER_FAIL_CLOSED (steps 1-3): each identifier is proven non-zero BEFORE any
    /// equality comparison involving it is trusted, so a zero-equals-zero coincidence can never
    /// authorize. This matters concretely -- an unresolved foreground snapshot also carries
    /// ProcessId 0.
    ///
    /// CHEAP_TERMS_FIRST (steps 4-10 before step 11): the foreground capture is the only expensive
    /// term (it can perform real signature verification on a retained-evidence miss). Placing it last
    /// keeps it entirely off the rejection path, which matters because this method runs on the
    /// clipboard owner thread -- at CHECK2, inside the global clipboard critical section, and on the
    /// write path inside the process-wide rollback-slot region as well.
    ///
    /// EPOCH_BRACKET (steps 4-6 paired with steps 14-15, load-bearing): the epoch is read twice and
    /// must equal the proof BOTH times. Without the second read, a foreground transition racing this
    /// method returns a stale <see langword="true"/> -- concretely, a move between two windows of the
    /// SAME process advances the epoch while leaving the PID (and, until the channel reports, the
    /// revision) unchanged, so every other term still agrees. Never cache the first read and reuse it
    /// as the second, and never move the capture after the closing read.
    /// </summary>
    public bool IsStillCurrent()
    {
        // 1-3: zero-identifier fail-closed, before any equality comparison is trusted.
        if (_proof.ChannelId <= 0)
            return false;
        if (_proof.ForegroundEpoch <= 0)
            return false;
        if (_proof.BrowserProcessId == 0)
            return false;

        // 4-6: epoch bracket, opening read. 0 covers unestablished, exhausted, and disposed alike.
        long epochBefore = _epochSource.CurrentEpoch;
        if (epochBefore == 0)
            return false;
        if (epochBefore != _proof.ForegroundEpoch)
            return false;

        // 7-10: the channel must still be live and still describe the exact bound state. A
        // disconnected channel has no entry at all, and a reconnect always allocates a new id, so a
        // retired id fails here rather than matching anything.
        if (!_registry.TryGetCurrentEvidence(_proof.ChannelId, out var evidence))
            return false;
        if (evidence.ChannelId != _proof.ChannelId)
            return false;
        if (evidence.EvidenceRevision != _proof.EvidenceRevision)
            return false;
        if (evidence.BrowserProcessId != _proof.BrowserProcessId)
            return false;

        // 11-13: the live foreground must still be the bound process. IsResolved is checked before
        // ProcessId is ever compared, so a default/unresolved snapshot can never be read as a PID.
        var snapshot = _foregroundCapture.Capture();
        if (!snapshot.IsResolved)
            return false;
        if (snapshot.ProcessId != _proof.BrowserProcessId)
            return false;

        // 14-15: epoch bracket, closing read -- a fresh read, never the value from step 4.
        long epochAfter = _epochSource.CurrentEpoch;
        if (epochAfter != _proof.ForegroundEpoch)
            return false;

        return true;
    }
}
