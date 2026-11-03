using Privon.Core;

namespace Privon.App;

// [PRIVON-AI-HANDOFF]
// ROLE: Aggregator that turns each clipboard attempt's events into a Privon.Core.ProtectionState (built for every launch, shipped included).
// TRUTH: Verified only after a verified rewrite of the LATEST attempt; a failed or unfinished scan is never reported as safe.
// FROZEN: Holds no clipboard text, no candidate and no exception message; performs no I/O; blocks nothing.
// DO_NOT: Use it to claim a transmission was blocked (the product has no such capability) or to clear a failure early.
// NAVIGATE: ClipboardPrivacyCoordinator emits the events; PrivonAppComposition builds this for every composition;
//           PrivonAppUiBridge displays it through ProtectionStatusPresenter.

/// <summary>
/// Why the LAST clipboard change was not scanned. It is a presentation fact about that one change, not a protection state: the frozen
/// <see cref="ProtectionState"/> has no value for "not scanned", and a copy nobody scanned must not be shown as a finished scan.
/// </summary>
internal enum UnscannedReason
{
    None = 0,

    /// <summary>The foreground app is not one PRIVON supports, so no detection ran.</summary>
    UnsupportedTarget,

    /// <summary>The clipboard change carried no text, so there was nothing to scan.</summary>
    NonText,
}

/// <summary>
/// A point-in-time view of the protection status. <see cref="UnresolvedFailure"/> stays true across attempts that never
/// scanned (an unsupported target, a non-text change) and across a scan in progress, and is released only by a later attempt
/// whose scan COMPLETED. <see cref="FailureTypeName"/> is an exception TYPE name (never a message), so different failures stay
/// distinguishable without carrying any content.
/// </summary>
internal readonly record struct ClipboardProtectionStatus(
    ProtectionState State, long AttemptId, bool UnresolvedFailure, long CompletedAttempts, string? FailureTypeName = null,
    UnscannedReason LastNotScanned = UnscannedReason.None,
    bool DecisionKeptOriginal = false)
{
    /// <summary>True only for <see cref="ProtectionState.Verified"/> -- the existing Core meaning, not a new one.</summary>
    public bool ClaimsProtected => State.ClaimsProtected();

    /// <summary>
    /// True whenever the unprotected clipboard text may still be there for the user to paste: the scan failed or is not finished,
    /// a rewrite was not verified, a decision is pending, or the last copy was never scanned at all. False only for a verified
    /// rewrite or a completed scan with nothing to protect, and only while no earlier failure is unresolved.
    /// </summary>
    public bool UnprotectedTextMayRemain =>
        UnresolvedFailure || LastNotScanned != UnscannedReason.None || DecisionKeptOriginal || State is not (ProtectionState.Verified or ProtectionState.Ready);
}

/// <summary>
/// Consumes the coordinator's existing per-attempt event stream (<see cref="IClipboardDiagnosticRecorder"/>, the only place a
/// terminal outcome is reported) and keeps a <see cref="ProtectionState"/>. It decorates an optional inner recorder and
/// forwards every event to it unchanged; a failure of the inner recorder never changes the status.
///
/// FRESHNESS. The status always describes the NEWEST attempt: a clipboard change starts a new attempt (Scanning) at once, so an
/// earlier Verified is never shown for new content, and an older attempt's late event is ignored. The tracker cannot observe
/// the clipboard itself, so it cannot prove that content changed without a notification did not; Verified is therefore only
/// ever "the last processed attempt's rewrite was verified". A scan that starts but never reports an end (a lost event, a stuck
/// worker, a mailbox that refused the work) is turned into Limited by a watchdog instead of staying Scanning -- or, worse, an
/// earlier status -- indefinitely.
///
/// CAPABILITY: the product cannot intercept or block a paste or a send (see docs/threat-model.md,
/// SEND_INTERCEPTION_CAPABILITY, and the production P/Invoke inventory). This type therefore never reports a blocked
/// transmission; it only refuses to CLAIM protection that was not verified.
/// </summary>
internal sealed class ClipboardProtectionStatusTracker : IClipboardDiagnosticRecorder, IDisposable
{
    /// <summary>No production capability can stop a paste or a send, so no state may imply one was stopped.</summary>
    public const bool TransmitBlockingAvailable = false;

    /// <summary>How long a scan may stay unfinished before it is reported as Limited. Real scans take milliseconds.</summary>
    public static readonly TimeSpan DefaultScanWatchdog = TimeSpan.FromSeconds(30);

    /// <summary>The failure type recorded when the watchdog fires or the mailbox refused the work.</summary>
    public const string ScanDidNotCompleteName = "ScanDidNotComplete";

    private readonly IClipboardDiagnosticRecorder? _inner;
    private readonly object _gate = new();
    private readonly TimeSpan _watchdog;
    private readonly System.Threading.Timer? _timer;

    private ProtectionState _state = ProtectionState.Ready;
    private long _attemptId;
    private long _watchedAttempt;
    private long _completed;
    private bool _unresolvedFailure;
    private UnscannedReason _notScanned;
    private bool _keptOriginal;
    private ProtectionState _failureState = ProtectionState.Ready;
    private string? _failureTypeName;
    private bool _disposed;

    /// <param name="inner">An optional recorder every event is forwarded to unchanged.</param>
    /// <param name="scanWatchdog">Null means <see cref="DefaultScanWatchdog"/>; <see cref="Timeout.InfiniteTimeSpan"/> disables it.</param>
    public ClipboardProtectionStatusTracker(IClipboardDiagnosticRecorder? inner = null, TimeSpan? scanWatchdog = null)
    {
        _inner = inner;
        _watchdog = scanWatchdog ?? DefaultScanWatchdog;
        if (_watchdog != Timeout.InfiniteTimeSpan && _watchdog <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(scanWatchdog));

        if (_watchdog != Timeout.InfiniteTimeSpan)
            _timer = new System.Threading.Timer(OnWatchdog, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Raised AFTER the status changed, on whichever thread caused the change (a coordinator worker or the watchdog thread).
    /// A subscriber must not touch UI directly and should read <see cref="Current"/> rather than rely on arrival order.
    /// </summary>
    public event EventHandler? Changed;

    public ClipboardProtectionStatus Current
    {
        get
        {
            lock (_gate)
            {
                return Snapshot();
            }
        }
    }

    public void Record(ClipboardDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        bool changed;
        lock (_gate)
        {
            var before = Snapshot();
            Apply(diagnosticEvent);
            changed = before != Snapshot();
        }

        if (changed) RaiseChanged();

        try
        {
            _inner?.Record(diagnosticEvent);
        }
        catch
        {
            // The inner recorder is diagnostics only; its failure must not alter, or hide, the status already recorded.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _timer?.Dispose();
    }

    private ClipboardProtectionStatus Snapshot() =>
        new(_state, _attemptId, _unresolvedFailure, _completed, _failureTypeName, _notScanned, _keptOriginal);

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A display subscriber's failure must never reach protection processing or the recording path.
        }
    }

    private void OnWatchdog(object? state)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (_state == ProtectionState.Scanning && _attemptId == _watchedAttempt)
            {
                Fail(ProtectionState.Limited, ScanDidNotCompleteName);
                changed = true;
            }
        }

        if (changed) RaiseChanged();
    }

    // Caller holds _gate.
    private void Apply(ClipboardDiagnosticEvent e)
    {
        if (_disposed) return;

        if (e.Stage == ClipboardDiagnosticStage.AppClipboardChangeReceived)
        {
            if (e.AttemptId < _attemptId) return; // a late event of an older attempt never overrides a newer one
            _attemptId = e.AttemptId;
            _state = ProtectionState.Scanning;   // new content: nothing is verified about it yet; a failure stays recorded
            _notScanned = UnscannedReason.None;  // and what was said about the previous copy does not describe this one
            _keptOriginal = false;
            Arm(e.AttemptId);
            return;
        }

        if (e.Stage == ClipboardDiagnosticStage.MailboxWriteAttempted)
        {
            // The work item for the newest content was refused: it will never be scanned.
            if (e.MailboxAccepted == false && e.AttemptId == _attemptId && _state == ProtectionState.Scanning)
                Fail(ProtectionState.Limited, ScanDidNotCompleteName);
            return;
        }

        if (e.Stage != ClipboardDiagnosticStage.AttemptTerminal || e.Terminal is not { } terminal) return;
        if (e.AttemptId < _attemptId) return;
        _attemptId = e.AttemptId;

        switch (terminal)
        {
            // ---- the scan COMPLETED: a failure of earlier content is released ----
            case ClipboardDiagnosticTerminalReason.Success:
                Complete(ProtectionState.Verified);
                break;
            case ClipboardDiagnosticTerminalReason.NoActionRequired:
                Complete(ProtectionState.Ready);
                break;
            case ClipboardDiagnosticTerminalReason.DecisionPending:
                Complete(ProtectionState.NeedsDecision);
                break;
            case ClipboardDiagnosticTerminalReason.DecisionKeptOriginal:
                // The decision is no longer pending, and nothing was protected for the kept item: Ready carries only "nothing is waiting", the flag says why.
                Complete(ProtectionState.Ready);
                _keptOriginal = true;
                break;

            // ---- the scan failed, or the protected text was not verified in place: recorded, never released here ----
            case ClipboardDiagnosticTerminalReason.ProcessingException:
                Fail(ProtectionState.Error, e.ExceptionTypeName);
                break;
            case ClipboardDiagnosticTerminalReason.WriteRejected:
            case ClipboardDiagnosticTerminalReason.WriteSkippedUnreliableSequence:
            case ClipboardDiagnosticTerminalReason.ReadRejected:
                Fail(ProtectionState.Limited, null);
                break;

            // ---- nothing was scanned (unsupported target, non-text change): keep whatever was recorded ----
            case ClipboardDiagnosticTerminalReason.UnauthorizedTarget:
            case ClipboardDiagnosticTerminalReason.NonTextNotification:
                Disarm();
                _completed++;
                _state = _unresolvedFailure ? _failureState : ProtectionState.Ready;
                // Ready here means only "nothing was waiting": the copy was NOT scanned, and the display says so instead of "scan finished".
                _notScanned = terminal == ClipboardDiagnosticTerminalReason.UnauthorizedTarget ? UnscannedReason.UnsupportedTarget : UnscannedReason.NonText;
                break;

            default:
                break;
        }
    }

    private void Arm(long attemptId)
    {
        _watchedAttempt = attemptId;
        try { _timer?.Change(_watchdog, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private void Disarm()
    {
        try { _timer?.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Complete(ProtectionState state)
    {
        Disarm();
        _notScanned = UnscannedReason.None;
        _keptOriginal = false;
        _completed++;
        _state = state;
        _unresolvedFailure = false;
        _failureState = ProtectionState.Ready;
        _failureTypeName = null;
    }

    private void Fail(ProtectionState state, string? failureTypeName)
    {
        Disarm();
        _notScanned = UnscannedReason.None;
        _keptOriginal = false;
        _completed++;
        _state = state;
        _failureState = state;
        _unresolvedFailure = true;
        _failureTypeName = failureTypeName ?? _failureTypeName;
    }
}
