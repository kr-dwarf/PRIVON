namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP28/STEP28.1/STEP29 -- the production, READ-ONLY focused-composer text capability.
/// Owns ONE dedicated background worker thread and answers guarded
/// <see cref="ReadFocusedComposerTextAsync"/> requests on it. Deliberately mirrors
/// <see cref="ClipboardChangeMonitor"/>'s own public shape and lifecycle discipline (single-use
/// <see cref="Start"/>, idempotent-on-success <see cref="Stop"/>/<see cref="Dispose"/>, a bounded
/// shutdown join) wherever the two capabilities' actual mechanics allow it to.
///
/// 0.1_PURPOSE (Phase 3C STEP28, frozen): READ-ONLY. This type never modifies composer contents,
/// never injects text, never presses Enter, never blocks/forwards a Send, and knows nothing about
/// direct-typing protection or response de-aliasing. A successful read here is NOT
/// <c>Privon.Core.ProtectionState.Verified</c> -- Verified additionally requires an actual composer
/// paste, composer read-back, and final validation (docs/release-gate.md), none of which this type
/// performs or knows about; see <see cref="ComposerReadOutcome"/>'s own doc.
///
/// THREADING_MODEL (Phase 3C STEP28.1, corrected from STEP28's original STA choice): the worker
/// thread is explicit <see cref="System.Threading.ApartmentState.MTA"/> -- set explicitly before
/// <see cref="Thread.Start()"/> rather than relying on the CLR's own MTA default, purely so the
/// choice is self-documenting. MTA (not STA) is correct here specifically BECAUSE this worker owns
/// no HWND and runs no Win32 message loop of any kind (no <c>GetMessage</c>/<c>DispatchMessage</c>,
/// no WPF <see cref="System.Windows.Threading.Dispatcher"/>) -- an STA thread that never pumps
/// messages is a classic COM misuse; MTA has no such message-loop dependency.
///
/// DISPATCH_MECHANISM (Phase 3C STEP28's MESSAGE_LOOP_POLICY, implemented here): deliberately
/// simpler than <see cref="ClipboardChangeMonitor"/>'s own <c>PostMessageW</c>-based marshaling --
/// this worker has no OS-delivered message/event source to react to (no clipboard-format-listener
/// equivalent), so a plain <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> plus a
/// <see cref="System.Threading.SemaphoreSlim"/> wakeup is sufficient: enqueue under
/// <see cref="_gate"/>, release the semaphore, the worker wakes, drains the queue, and waits again.
/// Structurally the SAME shape as <see cref="ClipboardChangeMonitor"/>'s own queue-plus-signal
/// design, just without the Win32 message-loop machinery that design needs for an unrelated reason
/// (receiving <c>WM_CLIPBOARDUPDATE</c>).
///
/// FOREGROUND_GUARD (Phase 3C STEP28's CHECK1/CHECK2, implemented here): a narrow, INTENTIONALLY
/// DUPLICATED reimplementation of <see cref="ClipboardChangeMonitor"/>'s own private
/// <c>CheckForegroundTarget</c>/<c>ForegroundGuardResult</c> mechanism, built directly on the
/// SAME, already-<c>internal</c>, already-shared <see cref="IForegroundTargetSource"/> type (no
/// new P/Invoke declarations, no duplicated native interop) -- never a shared helper extracted
/// from <see cref="ClipboardChangeMonitor"/> itself, which remains completely unmodified (Phase 3C
/// STEP29 instruction Y's explicit "no behavior changes to ClipboardChangeMonitor, prefer a narrow
/// equivalent implementation and document the intentional duplicate security check" guidance).
/// CHECK1 runs before any UI Automation work; CHECK2 runs immediately before this type accepts
/// anything derived from that UI Automation work as a successful result -- if the foreground
/// target changed while that (potentially slow) UI Automation work was running, whatever was just
/// read is discarded and <see cref="ComposerReadOutcome.TargetChanged"/> is returned instead. No
/// retry exists at this layer.
///
/// SENSITIVE_TEXT_LIFETIME: composer text exists only as the return value of one
/// <see cref="ReadFocusedComposerTextAsync"/> call -- this type has no <see cref="string"/> field
/// of any kind, no cache, no log, no temp file, no exception-message interpolation. Only
/// reference-discard/GC-eligibility is claimed, never deterministic zeroization (same discipline
/// as every other sensitive value documented throughout this codebase).
///
/// TIMEOUT_LIMITATION (Phase 3C STEP28/STEP28.1, reconfirmed): .NET/COM provides no safe way to
/// abort an in-flight UI Automation call -- this type never attempts one. A pathologically slow or
/// hung UI Automation provider can stall this dedicated worker thread; that stall is isolated to
/// this one thread (no other capability/thread is affected), and <see cref="Stop"/>'s own bounded
/// join will surface it as a deterministic, honest timeout failure rather than hiding it -- exactly
/// matching <see cref="ClipboardChangeMonitor.Stop"/>'s own established CLIPBOARD_MONITOR_STOP_TIMEOUT
/// precedent. No new per-call read timeout is invented.
/// </summary>
public sealed class ComposerTextReader : IDisposable
{
    /// <summary>Mirrors <see cref="ClipboardChangeMonitor.StopTimeoutContractDefault"/>'s own
    /// exact precedent and value -- the maximum time <see cref="Stop"/>/<see cref="Dispose"/> will
    /// wait for the worker thread to confirm exit before treating shutdown as a lifecycle
    /// failure.</summary>
    internal static readonly TimeSpan StopTimeoutContractDefault = TimeSpan.FromSeconds(5);

    private readonly IComposerTextSource _source;
    private readonly IForegroundTargetSource _foregroundSource;
    private readonly TimeSpan _stopTimeout;
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<PendingRead> _pendingReads = new();
    private readonly SemaphoreSlim _workSignal = new(initialCount: 0);

    private bool _startCalled;
    private bool _disposed;
    private Thread? _workerThread;
    private volatile bool _running;

    private readonly record struct PendingRead(
        TaskCompletionSource<ComposerTextReadResult> Tcs,
        ForegroundTargetSnapshot ExpectedTarget);

    public ComposerTextReader() : this(new Win32ComposerTextSource())
    {
    }

    internal ComposerTextReader(
        IComposerTextSource source,
        IForegroundTargetSource? foregroundSource = null,
        TimeSpan? stopTimeoutOverride = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _foregroundSource = foregroundSource ?? new Win32ForegroundTargetSource();
        _stopTimeout = stopTimeoutOverride ?? StopTimeoutContractDefault;
    }

    /// <summary>Single-use, matching <see cref="ClipboardChangeMonitor.Start"/>'s own exact
    /// precedent -- a second call always throws, regardless of whether the first call
    /// succeeded.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startCalled)
                throw new InvalidOperationException(
                    "Start has already been called on this ComposerTextReader instance -- it is single-use.");
            _startCalled = true;
            _running = true;
        }

        _workerThread = new Thread(WorkerThreadMain)
        {
            IsBackground = true,
            Name = "PrivonComposerTextReader",
        };
        _workerThread.SetApartmentState(ApartmentState.MTA);
        _workerThread.Start();
    }

    /// <summary>
    /// The guarded, public read entry point. <paramref name="expectedTarget"/> is validated
    /// synchronously, on the CALLING thread, before anything is even enqueued -- an unresolved
    /// snapshot, a zero PID, or a null/empty/whitespace process name is rejected as
    /// <see cref="ComposerReadOutcome.InvalidExpectedTarget"/> with zero UI Automation calls of any
    /// kind, mirroring <see cref="ClipboardChangeMonitor.ReadTextSnapshotAsync(ForegroundTargetSnapshot)"/>'s
    /// own identical validation. The request is marshaled to the dedicated worker thread via a
    /// queued item plus a <see cref="TaskCompletionSource{TResult}"/> -- never a blocking
    /// cross-thread call, so the caller is never blocked waiting for the worker.
    /// </summary>
    public Task<ComposerTextReadResult> ReadFocusedComposerTextAsync(ForegroundTargetSnapshot expectedTarget)
    {
        if (!IsValidExpectedTarget(expectedTarget))
            return Task.FromResult(ComposerTextReadResult.Failure(ComposerReadOutcome.InvalidExpectedTarget));

        var tcs = new TaskCompletionSource<ComposerTextReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // LIFECYCLE_RACES: check-and-enqueue happens under the same _gate lock Stop() uses to
        // flip _running=false -- whichever side acquires the lock first "wins" a race against a
        // concurrent Stop(), exactly mirroring ClipboardChangeMonitor's own established
        // discipline. Either this request is enqueued before shutdown (and the worker then
        // completes it -- as real work if already-queued-before-shutdown, or as NotRunning if it
        // drains after seeing _running==false), or it observes _running already false here and
        // never enqueues at all. Either way the returned Task always completes.
        lock (_gate)
        {
            if (!_startCalled || !_running || _disposed)
            {
                tcs.SetResult(ComposerTextReadResult.Failure(ComposerReadOutcome.NotRunning));
                return tcs.Task;
            }

            _pendingReads.Enqueue(new PendingRead(tcs, expectedTarget));
        }

        _workSignal.Release();
        return tcs.Task;
    }

    /// <summary>Idempotent and safe before Start, after a failed Start, or after a prior Stop.
    /// Stops accepting new work, wakes the worker, and waits (bounded) for it to exit.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_startCalled || !_running) return;
            _running = false;
        }

        // Wake the worker so it notices _running==false even if it is currently idle, blocked in
        // _workSignal.Wait() -- one extra release is harmless if the worker is already awake
        // processing queued work (it will simply notice the flag on its next loop iteration).
        _workSignal.Release();

        var workerThread = _workerThread;
        workerThread?.Join(_stopTimeout);

        // A timed-out Join means the worker did not confirm exit within the bounded window --
        // this must never be silently treated as a clean stop (fail-closed), mirroring
        // ClipboardChangeMonitor.Stop's own identical CLIPBOARD_MONITOR_STOP_TIMEOUT precedent.
        // No raw composer content is or could be in this message -- only the operation, the
        // stage, and the timeout value.
        if (workerThread is { IsAlive: true })
        {
            throw new InvalidOperationException(
                $"Composer text reader worker thread did not exit within the shutdown timeout ({_stopTimeout.TotalSeconds:F0}s).");
        }
    }

    /// <summary>
    /// DISPOSE_FAILURE_BEHAVIOR (mirrors <see cref="ClipboardChangeMonitor.Dispose"/>'s own exact
    /// contract): idempotent on success, but NOT unconditionally silent -- a cleanup failure (the
    /// same shutdown-timeout <see cref="Stop"/> can throw) is never swallowed here. This instance
    /// is marked fully disposed -- making a later call a safe no-op -- ONLY once the underlying
    /// <see cref="Stop"/> has actually completed without throwing, so a later <see cref="Dispose"/>
    /// call genuinely retries cleanup rather than silently no-op'ing over an unresolved failure.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        Stop();

        lock (_gate)
        {
            _disposed = true;
        }

        _workSignal.Dispose();
    }

    // WORKER_SURVIVAL: this loop, and the per-request try/catch inside it, are the load-bearing
    // safety net that keeps this dedicated thread alive across an unexpected failure -- UI
    // Automation/COM is a far less predictable surface than the raw Win32 clipboard calls
    // elsewhere in this assembly (a third-party/OS UI tree with arbitrary provider
    // implementations), so -- unlike relying on Win32ComposerTextSource's own narrower,
    // evidence-grounded ElementNotAvailableException/COMException catches alone -- an outer,
    // catch-anything boundary here is a deliberate, additional safety net, not a duplicate of that
    // inner one. An unexpected exception here is mapped to AutomationFailure and NEVER lets this
    // thread die with a request's own TaskCompletionSource left incomplete forever (which would
    // silently strand every future request behind it too). No exception content of any kind is
    // ever logged or included in a result.
    private void WorkerThreadMain()
    {
        while (true)
        {
            _workSignal.Wait();

            if (!_running)
            {
                DrainPendingReadsAsNotRunning();
                return;
            }

            while (_pendingReads.TryDequeue(out var pending))
            {
                ComposerTextReadResult result;
                try
                {
                    result = ExecuteRead(pending.ExpectedTarget);
                }
                catch (Exception)
                {
                    result = ComposerTextReadResult.Failure(ComposerReadOutcome.AutomationFailure);
                }

                pending.Tcs.TrySetResult(result);
            }
        }
    }

    private void DrainPendingReadsAsNotRunning()
    {
        while (_pendingReads.TryDequeue(out var pending))
        {
            pending.Tcs.TrySetResult(ComposerTextReadResult.Failure(ComposerReadOutcome.NotRunning));
        }
    }

    // READ_FLOW: CHECK1 -> QueryFocusedElement (the one UI Automation call) -> CHECK2 -> map the
    // snapshot. expectedTarget is already known valid (IsValidExpectedTarget already ran on the
    // calling thread before this request was ever enqueued).
    private ComposerTextReadResult ExecuteRead(ForegroundTargetSnapshot expectedTarget)
    {
        var check1 = CheckForegroundTarget(expectedTarget);
        if (check1 != ForegroundGuardResult.Matched)
            return ComposerTextReadResult.Failure(MapGuardOutcome(check1));

        var snapshot = _source.QueryFocusedElement();

        // CHECK2 (Phase 3C STEP28's O. CHECK2 FOREGROUND GUARD): immediately before this method
        // accepts anything derived from the UI Automation work above. If the foreground target
        // changed while that (potentially slow) work was running, the snapshot -- including any
        // text it carries -- is discarded here and never returned.
        var check2 = CheckForegroundTarget(expectedTarget);
        if (check2 != ForegroundGuardResult.Matched)
            return ComposerTextReadResult.Failure(MapGuardOutcome(check2));

        return MapSnapshot(snapshot, expectedTarget);
    }

    // COMPOSER_IDENTITY (Phase 3C STEP28's K, frozen): a Resolved snapshot qualifies as the
    // composer only when ALL THREE hold -- UIA ProcessId == expectedTarget.ProcessId, ClassName
    // contains "ProseMirror-focused" (ordinal -- never culture-sensitive, a CSS/DOM class-list
    // string is never locale-dependent content), and IsEditControlType. EXACT_TEXT_POLICY: the
    // returned text is exactly what the snapshot carried -- no trim/normalize/case-fold/
    // canonicalization of any kind. EMPTY_TEXT: snapshot.Text == "" (successfully read, genuinely
    // empty) is a valid Success result, never reinterpreted as ComposerNotFocused/TextUnavailable
    // -- only snapshot.Text == null (neither pattern produced anything) maps to TextUnavailable.
    private static ComposerTextReadResult MapSnapshot(ComposerElementSnapshot snapshot, ForegroundTargetSnapshot expectedTarget)
    {
        switch (snapshot.Status)
        {
            case ComposerElementQueryStatus.AutomationFailure:
                return ComposerTextReadResult.Failure(ComposerReadOutcome.AutomationFailure);

            case ComposerElementQueryStatus.NoFocusedElement:
                return ComposerTextReadResult.Failure(ComposerReadOutcome.ComposerNotFocused);

            case ComposerElementQueryStatus.Resolved:
                bool isComposer = snapshot.ProcessId == expectedTarget.ProcessId
                    && (snapshot.ClassName ?? string.Empty).Contains("ProseMirror-focused", StringComparison.Ordinal)
                    && snapshot.IsEditControlType;

                if (!isComposer)
                    return ComposerTextReadResult.Failure(ComposerReadOutcome.ComposerNotFocused);

                return snapshot.Text is { } text
                    ? ComposerTextReadResult.Success(text)
                    : ComposerTextReadResult.Failure(ComposerReadOutcome.TextUnavailable);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(snapshot), snapshot.Status, "Undefined ComposerElementQueryStatus value.");
        }
    }

    // FOREGROUND_GUARD core -- BUG-004 Gate 2H.3 (E+): no longer a hand-rolled duplicate of
    // ClipboardChangeMonitor's own guard. Both now obtain a FRESH coherent current-foreground
    // identity through the ONE shared ForegroundIdentityCapture primitive and compare all four
    // identity facts, so the two transports can never again drift apart (the previous local copies
    // had both silently weakened to PID+ProcessName-only -- BUG004-TOCTOU-001). Fresh, uncached,
    // every call. This type still does not know "ChatGPT", any package family name, or any other
    // product policy -- it compares CURRENT against the caller's EXPECTED snapshot only.
    private ForegroundGuardResult CheckForegroundTarget(ForegroundTargetSnapshot expected)
    {
        if (!ForegroundIdentityCapture.TryCapture(_foregroundSource, out var current))
            return ForegroundGuardResult.Unavailable;

        return ForegroundIdentityCapture.Matches(current, expected)
            ? ForegroundGuardResult.Matched
            : ForegroundGuardResult.Changed;
    }

    private enum ForegroundGuardResult { Unavailable, Changed, Matched }

    private static ComposerReadOutcome MapGuardOutcome(ForegroundGuardResult result) => result switch
    {
        ForegroundGuardResult.Unavailable => ComposerReadOutcome.TargetUnavailable,
        ForegroundGuardResult.Changed => ComposerReadOutcome.TargetChanged,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Matched must never reach a failure mapping."),
    };

    // INVALID_TARGET_POLICY -- mirrors ClipboardChangeMonitor's own IsValidExpectedTarget exactly.
    private static bool IsValidExpectedTarget(ForegroundTargetSnapshot target) =>
        target.IsResolved && target.ProcessId != 0 && !string.IsNullOrWhiteSpace(target.ProcessName);
}
