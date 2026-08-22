using System.Threading.Channels;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP2 -- App Clipboard Dispatch + TargetGate Read Coordinator, extended in Phase 3B
/// STEP4 with the real Detection handoff, in Phase 3B STEP14 with the guarded clipboard write, in
/// Phase 3B STEP17 with the atomic clipboard-attempt generation lifecycle (Phase 3B
/// STEP16/STEP16.1), in Phase 3B STEP21 with decision-session publication (Phase 3B STEP20
/// audit), in Phase 3B STEP23 with the shared App-level operation gate (Phase 3B STEP22
/// audit's SELECTED_EXECUTION_LANE, MODEL D), and in Phase 3C STEP32 with the composer-verification
/// handoff/invalidation seams (<see cref="IClipboardComposerVerificationHandoff"/>/
/// <see cref="IClipboardComposerVerificationInvalidation"/>, Phase 3C STEP31/31.1 audits, frozen).
/// Connects <see cref="IClipboardReadTransport.Changed"/>
/// (metadata-only, raised synchronously on the Windows clipboard owner thread) through an
/// <see cref="IClipboardNotificationLifecycle"/> generation advance, an App-owned latest-pending
/// dispatch, an <see cref="IClipboardOperationGate"/> acquisition covering the complete dequeued
/// attempt, a foreground target capture, the <see cref="TargetGate"/> policy, a guarded clipboard
/// read, a single delegated call into <see cref="IClipboardPrivacyProcessor"/>, and -- exclusively
/// -- either (a) a single guarded clipboard write via <see cref="IClipboardWriteTransport"/> when
/// that call produces a <see cref="ClipboardWritePlan"/> and the successful read's own sequence is
/// reliable, or (b) a single decision-session publication attempt via
/// <see cref="IClipboardDecisionSessionPublisher"/> when that call produces a
/// <see cref="ClipboardDecisionPlan"/> instead. This type still does NOT itself own
/// <c>DetectionPipeline</c>, <c>CandidatePolicyEvaluator</c>, <c>AliasMap</c>, <c>AliasAssigner</c>,
/// <c>AliasReplacer</c>, a Storage bridge, <c>RevisionTracker</c>,
/// <see cref="ClipboardDecisionScope"/>/<c>IClipboardDecisionScopeLifecycle</c> (the
/// future-Runtime-Decision-facing half of the generation lifecycle -- see
/// <see cref="IClipboardNotificationLifecycle"/>'s own doc for why this type is deliberately never
/// given that wider interface), one-time grants, <c>ProtectionState</c> aggregation, or the concrete
/// <c>ClipboardComposerVerifier</c> itself (it holds only the two narrow handoff/invalidation
/// interfaces that type implements, never match semantics, never
/// <c>PendingComposerVerification</c>/<c>ComposerVerificationResult</c>) --
/// <see cref="IClipboardPrivacyProcessor"/> remains the only privacy-engine-facing dependency it
/// holds. <see cref="IClipboardWriteTransport"/>/<see cref="IClipboardNotificationLifecycle"/>/
/// <see cref="IClipboardDecisionSessionPublisher"/>/<see cref="IClipboardOperationGate"/> are all
/// mechanics-only, exactly like <see cref="IClipboardReadTransport"/>/
/// <see cref="IForegroundTargetCapture"/> are narrow seams rather than the real
/// Windows/Detection/Core types themselves -- this type understands "a plan exists, forward its
/// text, the same target, the same sequence," "a decision plan exists, forward it with this
/// attempt's exact generation and the exact raw text that was just read," "advance the lifecycle,
/// carry the returned generation," and "hold one permit for the duration of one attempt," never
/// <c>PiiType</c>/<c>CandidatePolicyDecision</c>/<c>AliasToken</c>/<c>TrustState</c>/
/// <c>RevisionTracker</c>/<c>RevisionStamp</c>/revision grants.
///
/// CLIPBOARD_NOTIFICATION_DISPATCH_BOUNDARY (open since Windows-layer Phase 3A.4 STEP2.1) is
/// resolved by this type: <see cref="OnClipboardChanged"/> does nothing but a synchronous
/// generation advance (see its own doc -- an already-proven-bounded, non-blocking operation, not
/// an exception to this rule) and a non-blocking <see cref="ChannelWriter{T}.TryWrite"/>, then
/// returns immediately -- no foreground capture, no clipboard read, no async work, no
/// <see cref="IClipboardOperationGate"/> acquisition of any kind, ever runs inside the
/// owner-thread callback. This is exactly what lets a newer clipboard notification still advance
/// the generation and invalidate an active decision scope even while this gate is currently held
/// by an in-flight attempt (Phase 3B STEP22 audit's CALLBACK_NONBLOCKING_PROOF).
///
/// OPERATION_GATE_ACQUISITION_BOUNDARY (Phase 3B STEP23, frozen): the gate is acquired in
/// <see cref="RunWorkerAsync"/> -- never while merely awaiting the next mailbox item -- and held
/// for the ENTIRE dequeued attempt (target capture/gate check, the awaited guarded read, the
/// processor call, and whichever of the guarded write / decision-session publication follows),
/// released in a <c>finally</c> block covering every outcome (success, target rejection, read
/// failure, a processor/publisher/write-transport exception). An idle coordinator -- no item yet
/// dequeued -- never holds this gate. LOCK_ORDER (frozen, no cycle possible):
/// <see cref="ProcessNotificationAsync"/> may, while already holding this gate, reach
/// <see cref="IClipboardDecisionSessionPublisher.TryPublish"/>, which itself briefly takes
/// <c>ClipboardDecisionScopeLifecycle</c>'s own tiny synchronous <c>_gate</c> -- i.e. "operation
/// gate -&gt; lifecycle lock" may occur. The reverse never can: <see cref="OnClipboardChanged"/>
/// takes the lifecycle lock (via <see cref="IClipboardNotificationLifecycle.AdvanceOnClipboardNotification"/>)
/// but never acquires this operation gate at all -- "lifecycle lock -&gt; operation gate" is
/// structurally unreachable, not merely avoided by convention. These are two different locks
/// serving two different purposes and are never merged: this gate is an async, App-semantic,
/// whole-attempt serialization primitive; the lifecycle's own <c>_gate</c> is a tiny synchronous
/// generation/scope-atomicity primitive (Phase 3B STEP16.1) -- see
/// <c>ClipboardPrivacyCoordinatorTests.NewNotification_AdvancesLifecycle_EvenWhileOperationGateIsHeld</c>/
/// <c>NonTextNotification_AdvancesLifecycle_EvenWhileOperationGateHeld</c> for the behavioral,
/// deadlock-freedom regression this ordering guarantee rests on.
///
/// UI-independent: this type has no WPF <c>Dispatcher</c> dependency of any kind (Phase 3B STEP2
/// instruction's WPF policy) -- nothing here touches UI state.
/// </summary>
internal sealed class ClipboardPrivacyCoordinator : IDisposable
{
    /// <summary>
    /// APP_COORDINATOR_LIFECYCLE -- mirrors <c>ClipboardChangeMonitor.StopTimeoutContractDefault</c>'s
    /// own precedent exactly: the maximum time <see cref="Stop"/>/<see cref="Dispose"/> will wait
    /// for the consumer worker to confirm exit, before treating shutdown as a lifecycle failure.
    /// </summary>
    internal static readonly TimeSpan WorkerStopTimeoutContractDefault = TimeSpan.FromSeconds(5);

    private readonly IClipboardReadTransport _transport;
    private readonly IForegroundTargetCapture _targetCapture;
    private readonly IClipboardPrivacyProcessor _processor;
    private readonly IClipboardWriteTransport _writeTransport;
    private readonly IClipboardNotificationLifecycle _notificationLifecycle;
    private readonly IClipboardDecisionSessionPublisher _decisionSessionPublisher;
    private readonly IClipboardOperationGate _operationGate;
    private readonly IClipboardComposerVerificationHandoff _verificationHandoff;
    private readonly IClipboardComposerVerificationInvalidation _verificationInvalidation;
    private readonly IClipboardDiagnosticRecorder _diagnostics;
    private readonly Channel<ClipboardDispatchItem> _mailbox;
    private readonly TimeSpan _workerStopTimeout;
    private readonly object _gate = new();

    private bool _startCalled;
    private bool _running;
    private bool _disposed;
    private Task? _workerTask;

    public ClipboardPrivacyCoordinator(
        IClipboardReadTransport transport,
        IForegroundTargetCapture targetCapture,
        IClipboardPrivacyProcessor processor,
        IClipboardWriteTransport writeTransport,
        IClipboardNotificationLifecycle notificationLifecycle,
        IClipboardDecisionSessionPublisher decisionSessionPublisher,
        IClipboardOperationGate operationGate,
        IClipboardComposerVerificationHandoff verificationHandoff,
        IClipboardComposerVerificationInvalidation verificationInvalidation,
        IClipboardDiagnosticRecorder? diagnostics = null)
        : this(transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation, stopTimeoutOverride: null, diagnostics: diagnostics)
    {
    }

    internal ClipboardPrivacyCoordinator(
        IClipboardReadTransport transport,
        IForegroundTargetCapture targetCapture,
        IClipboardPrivacyProcessor processor,
        IClipboardWriteTransport writeTransport,
        IClipboardNotificationLifecycle notificationLifecycle,
        IClipboardDecisionSessionPublisher decisionSessionPublisher,
        IClipboardOperationGate operationGate,
        IClipboardComposerVerificationHandoff verificationHandoff,
        IClipboardComposerVerificationInvalidation verificationInvalidation,
        TimeSpan? stopTimeoutOverride,
        IClipboardDiagnosticRecorder? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(targetCapture);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(writeTransport);
        ArgumentNullException.ThrowIfNull(notificationLifecycle);
        ArgumentNullException.ThrowIfNull(decisionSessionPublisher);
        ArgumentNullException.ThrowIfNull(operationGate);
        ArgumentNullException.ThrowIfNull(verificationHandoff);
        ArgumentNullException.ThrowIfNull(verificationInvalidation);
        _transport = transport;
        _targetCapture = targetCapture;
        _processor = processor;
        _writeTransport = writeTransport;
        _notificationLifecycle = notificationLifecycle;
        _decisionSessionPublisher = decisionSessionPublisher;
        _operationGate = operationGate;
        _verificationHandoff = verificationHandoff;
        _verificationInvalidation = verificationInvalidation;
        // Phase 3C STEP41 -- temporary diagnostic recorder seam, optional and trailing so every
        // pre-STEP41 constructor call (public or internal, 9 or 10 positional args) keeps compiling
        // and behaving identically: omitting this argument defaults to a pure no-op recorder (see
        // NullClipboardDiagnosticRecorder's own doc for the regression proving this).
        _diagnostics = diagnostics ?? NullClipboardDiagnosticRecorder.Instance;
        _workerStopTimeout = stopTimeoutOverride ?? WorkerStopTimeoutContractDefault;

        // CHANNEL_POLICY: capacity 1, DropOldest, single reader/writer -- the owner thread is the
        // only writer, this coordinator's own worker Task is the only reader. Gives latest-wins
        // coalescing for free; no debounce timer, no third-party package. Element type is
        // ClipboardDispatchItem (Phase 3B STEP17), not the bare ClipboardChangeNotification --
        // see that type's own doc for why the generation must travel WITH the item.
        _mailbox = Channel.CreateBounded<ClipboardDispatchItem>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>
    /// APP_COORDINATOR_LIFECYCLE: single-use, matching <c>ClipboardChangeMonitor.Start</c>'s own
    /// exact precedent -- a second call always throws, regardless of whether the first succeeded
    /// or failed. STARTUP_ORDER (frozen): validate lifecycle state -> create/start the consumer
    /// worker Task -> subscribe to <see cref="IClipboardReadTransport.Changed"/> -> call
    /// <see cref="IClipboardReadTransport.Start"/>. The worker is started (and therefore able to
    /// drain the channel) before the transport itself becomes active, so no notification can ever
    /// arrive with no consumer eventually able to process it.
    ///
    /// START_FAILURE_ROLLBACK: if <see cref="IClipboardReadTransport.Start"/> throws, this method
    /// unsubscribes, completes the mailbox, makes a best-effort bounded wait for the worker to
    /// exit, and rethrows -- never silently reporting a successful start, and never stranding a
    /// background worker. A failed Start still counts as "used up" the single-use guarantee (this
    /// instance can never be started again), exactly like <c>ClipboardChangeMonitor</c>'s own
    /// <c>_startCalled</c> semantics.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startCalled)
                throw new InvalidOperationException(
                    "Start has already been called on this ClipboardPrivacyCoordinator instance -- it is single-use.");
            _startCalled = true;
        }

        _workerTask = Task.Run(RunWorkerAsync);
        _transport.Changed += OnClipboardChanged;

        try
        {
            _transport.Start();
        }
        catch
        {
            _transport.Changed -= OnClipboardChanged;
            _mailbox.Writer.Complete();
            try
            {
                _workerTask.Wait(_workerStopTimeout);
            }
            catch
            {
                // Best-effort cleanup during failure rollback -- the ORIGINAL transport.Start()
                // failure below is what must propagate, never a secondary rollback timeout.
            }

            throw;
        }

        lock (_gate)
        {
            _running = true;
        }
    }

    /// <summary>
    /// SHUTDOWN_ORDER (frozen, corrected): (1) mark no longer accepting work -- idempotent no-op
    /// if never started or already stopped; (2) unsubscribe from
    /// <see cref="IClipboardReadTransport.Changed"/> BEFORE stopping the transport, so the App
    /// side stops accepting notifications first; (3) <see cref="IClipboardReadTransport.Stop"/>;
    /// (4) complete the mailbox writer; (5) allow any in-flight/still-pending item to finish
    /// draining naturally (no queue-clearing/cancellation machinery); (6) bounded wait for the
    /// worker to exit; (7) nothing else to dispose -- <see cref="_transport"/>/
    /// <see cref="_targetCapture"/> are injected, not owned, so their disposal is the composition
    /// root's responsibility, not this coordinator's.
    ///
    /// PENDING_MAILBOX_AT_STOP: because unsubscribe happens before <c>Complete()</c>, a
    /// <see cref="IClipboardReadTransport.Changed"/> invocation that was already in progress on
    /// the owner thread when unsubscribe ran can still reach <see cref="OnClipboardChanged"/>
    /// afterward (multicast-delegate invocation-list snapshot semantics) -- its
    /// <see cref="ChannelWriter{T}.TryWrite"/> call simply returns false once the channel is
    /// completed, which <see cref="OnClipboardChanged"/> already tolerates by design (its return
    /// value is never used to alter control flow -- only forwarded to a diagnostic observation as
    /// of Phase 3C STEP41). This never blocks the clipboard owner thread.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_startCalled || !_running) return;
            _running = false;
        }

        _transport.Changed -= OnClipboardChanged;
        _transport.Stop();
        _mailbox.Writer.Complete();

        bool completed = _workerTask is null || _workerTask.Wait(_workerStopTimeout);
        if (!completed)
        {
            throw new InvalidOperationException(
                $"Clipboard privacy coordinator worker did not exit within the shutdown timeout ({_workerStopTimeout.TotalSeconds:F0}s).");
        }
    }

    /// <summary>
    /// Idempotent on success, not unconditionally silent on failure -- identical
    /// DISPOSE_FAILURE_BEHAVIOR discipline to <c>ClipboardChangeMonitor.Dispose</c>: only marked
    /// fully disposed once <see cref="Stop"/> has actually completed without throwing, so a
    /// cleanup failure is never hidden behind a falsely "disposed" state.
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
    }

    /// <summary>
    /// CALLBACK_BOUNDARY (frozen, extended Phase 3B STEP17, extended Phase 3C STEP32): runs
    /// synchronously on the Windows clipboard owner thread. Does exactly four things, in this
    /// exact order -- (1) UNCONDITIONALLY advances <see cref="_notificationLifecycle"/> for EVERY
    /// notification received here, text or not (Phase 3B STEP16's NON_TEXT_INVALIDATION/
    /// UNSUPPORTED_TARGET_INVALIDATION findings: the mere fact that the clipboard changed to
    /// something -- an image, a change nobody will ever authorize PRIVON to read -- is itself
    /// proof any old pending decision scope is no longer trustworthy, so this must happen BEFORE
    /// the text filter below and is structurally already before <see cref="TargetGate"/>, which
    /// only ever runs later inside worker processing); (2) UNCONDITIONALLY invokes
    /// <see cref="_verificationInvalidation"/>'s <see cref="IClipboardComposerVerificationInvalidation.InvalidatePending"/>
    /// for the SAME reason and at the SAME point as (1) -- Phase 3C STEP31 audit's
    /// NOTIFICATION_INVALIDATION finding, a second, independently-owned piece of sensitive-adjacent
    /// state that must be eagerly dropped on every notification, not just a Runtime-Decision scope;
    /// (3) checks <see cref="ClipboardChangeNotification.HasUnicodeText"/> (APP_NON_TEXT_EVENT_POLICY:
    /// a non-text notification is discarded here, AFTER both invalidations above, before
    /// enqueueing, before any foreground inspection, before any read attempt); and (4), for a text
    /// notification, a single non-blocking <see cref="ChannelWriter{T}.TryWrite"/> of a
    /// <see cref="ClipboardDispatchItem"/> carrying the notification and the EXACT generation the
    /// advance above returned -- then returns. Both
    /// <see cref="IClipboardNotificationLifecycle.AdvanceOnClipboardNotification"/> and
    /// <see cref="IClipboardComposerVerificationInvalidation.InvalidatePending"/> are themselves
    /// proven-bounded, lock-guarded metadata operations (Phase 3B STEP16.1's
    /// CALLBACK_LOCK_ACCEPTABILITY finding, applied identically to the second lock) -- taking
    /// either here does not violate this callback's own promptness contract, and neither ever
    /// acquires <see cref="_operationGate"/>. No foreground capture, no clipboard read, no UI
    /// Automation work, no async work, no logging of any content, no UI Dispatcher call ever
    /// happens here. <c>TryWrite</c>'s return value is never used to alter control flow -- as of
    /// Phase 3C STEP41 it is additionally forwarded to <see cref="Diagnose"/>
    /// (<see cref="ClipboardDiagnosticStage.MailboxWriteAttempted"/>) for real-environment manual
    /// QA correlation only; a <see langword="false"/> return (mailbox already completed, e.g.
    /// mid-shutdown) is still not an error, just nothing left to do.
    ///
    /// DIAGNOSTIC_INSTRUMENTATION (Phase 3C STEP41, temporary): the two <see cref="Diagnose"/>
    /// calls below are the ONLY additions to this method -- both are bounded, non-blocking,
    /// exception-isolated observations (see <see cref="Diagnose"/>'s own doc) that never alter
    /// which branch this method takes or what it does; a coordinator built without a diagnostic
    /// recorder (every pre-STEP41 constructor call) behaves identically to before this STEP.
    /// </summary>
    private void OnClipboardChanged(object? sender, ClipboardChangeNotification notification)
    {
        var generation = _notificationLifecycle.AdvanceOnClipboardNotification();
        Diagnose(ClipboardDiagnosticEvent.AppClipboardChangeReceived(
            generation, notification.SequenceNumber, notification.HasReliableSequence, notification.HasUnicodeText));

        _verificationInvalidation.InvalidatePending();
        if (!notification.HasUnicodeText)
        {
            Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(generation, ClipboardDiagnosticTerminalReason.NonTextNotification));
            return;
        }

        bool accepted = _mailbox.Writer.TryWrite(new ClipboardDispatchItem(notification, generation));
        Diagnose(ClipboardDiagnosticEvent.MailboxWriteAttempted(generation, accepted));
    }

    /// <summary>
    /// Phase 3C STEP41 (temporary, this-STEP-only instrumentation): the single point every
    /// diagnostic observation in this type flows through. Wraps <see cref="_diagnostics"/>'s own
    /// <see cref="IClipboardDiagnosticRecorder.Record"/> call in a defensive
    /// <c>try</c>/<c>catch</c> so that even an incorrect recorder implementation can never affect
    /// protection processing, never propagate an exception into a caller that did not expect one,
    /// and never change this coordinator's own control flow or timing in any observable way (the
    /// call itself is synchronous and, for both <see cref="NullClipboardDiagnosticRecorder"/> and
    /// <see cref="FileClipboardDiagnosticRecorder"/>, non-blocking).
    /// </summary>
    private void Diagnose(ClipboardDiagnosticEvent diagnosticEvent)
    {
        try
        {
            _diagnostics.Record(diagnosticEvent);
        }
        catch
        {
            // SINK_FAILURE_ISOLATION (Phase 3C STEP41) -- a diagnostic recorder failure must never
            // reach protection processing, regardless of which call site above triggered it.
        }
    }

    /// <summary>
    /// ACQUISITION_BOUNDARY (Phase 3B STEP23, frozen): <see cref="_operationGate"/> is acquired
    /// only AFTER <c>await foreach</c> has actually yielded a dequeued <see cref="ClipboardDispatchItem"/>
    /// -- never while merely awaiting the next mailbox item, so an idle coordinator (nothing to
    /// process) never holds the gate and can never block a future concurrent decision-action
    /// attempt indefinitely. RELEASE_BOUNDARY: the gate is released in a <c>finally</c> block
    /// that covers every possible outcome of the attempt below -- normal completion, a target
    /// rejection, a guarded-read failure, or an exception from the processor/publisher/write
    /// transport (all already isolated from the worker loop itself by the existing
    /// <c>catch</c> below, unchanged from before this STEP). SHUTDOWN_BEHAVIOR: no
    /// <see cref="CancellationToken"/> is introduced anywhere in this acquisition -- consistent
    /// with every other async seam in this codebase (see
    /// <c>IClipboardReadTransport_ReadTextSnapshotAsync_HasNoCancellationTokenParameter</c>'s own
    /// precedent) -- so a worker stuck awaiting <see cref="IClipboardOperationGate.WaitAsync"/>
    /// during shutdown fails exactly the same deterministic, already-tested way any other stuck
    /// worker already does: <see cref="Stop"/>'s existing bounded <see cref="Task.Wait(TimeSpan)"/>
    /// times out and throws (see <c>Stop_WorkerDoesNotExitInTime_ThrowsDeterministically</c>) --
    /// no new shutdown machinery was needed or added.
    /// </summary>
    private async Task RunWorkerAsync()
    {
        await foreach (var item in _mailbox.Reader.ReadAllAsync())
        {
            Diagnose(ClipboardDiagnosticEvent.WorkerDequeued(item.Generation));

            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ProcessNotificationAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An unexpected failure processing one notification must never end the worker
                // loop for all future notifications -- mirrors ClipboardChangeMonitor.Changed's
                // own subscriber-exception-isolation precedent. Nothing sensitive is ever held
                // here to leak either way. Phase 3C STEP41: the exception's own GetType().Name
                // ONLY (never Message) is forwarded to Diagnose for manual-QA correlation -- this
                // does not change which exceptions are caught here (every thrown .NET exception is
                // already Exception-derived, so `catch (Exception ex)` behaves identically to the
                // bare `catch` this replaces).
                Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(
                    item.Generation, ClipboardDiagnosticTerminalReason.ProcessingException, ex.GetType().Name));
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    /// <summary>
    /// TARGET_TOKEN_FLOW (frozen): captures the foreground target EXACTLY ONCE per notification,
    /// applies <see cref="TargetGate"/> to that exact snapshot, and -- only if supported -- passes
    /// THAT SAME snapshot value as the guarded read's <c>expectedTarget</c>. A second App-side
    /// capture between the policy check and the read call is deliberately never performed: it
    /// would reopen exactly the staleness window Phase 3A.5 STEP3/STEP4 minimized at the Windows
    /// layer, for no benefit, since Windows performs its own fresh CHECK1/CHECK2 against whatever
    /// token it is handed regardless.
    ///
    /// READ_OUTCOME_POLICY: <see cref="ClipboardReadOutcome.Success"/> reaches the
    /// SUCCESS_HANDOFF_BOUNDARY below and hands off to <see cref="_processor"/> -- no logging of
    /// <see cref="ClipboardTextSnapshot.Text"/>, ever. Every other outcome (including
    /// <see cref="ClipboardReadOutcome.Busy"/> -- APP_BUSY_POLICY: no retry, no backoff, wait for
    /// a future clipboard event) ends the attempt with no retry and no fallback to any unguarded
    /// path (which is not even reachable through this seam).
    ///
    /// APP_CLIPBOARD_WRITE_OWNER / APP_WRITE_TARGET_TOKEN_FLOW / APP_WRITE_SEQUENCE_TOKEN_FLOW /
    /// APP_UNRELIABLE_SEQUENCE_WRITE_POLICY (Phase 3B STEP13 audit, frozen, implemented in
    /// WRITE_HANDOFF below): when the processor's outcome carries a
    /// <see cref="ClipboardWritePlan"/>, this coordinator -- never the processor, which stays
    /// synchronous -- performs the ONE guarded write, forwarding the EXACT SAME
    /// <paramref name="item"/>-scoped <c>expectedTarget</c> that already authorized the
    /// guarded read (no recapture) and the successful read's own
    /// <see cref="ClipboardTextSnapshot.SequenceNumber"/> (never
    /// <see cref="ClipboardChangeNotification.SequenceNumber"/>, which never becomes a write
    /// token). If that snapshot's own <see cref="ClipboardTextSnapshot.HasReliableSequence"/> is
    /// false, no write is attempted at all -- no unguarded fallback, no retry, no
    /// protection-success claim. Exactly one write call per eligible attempt; every
    /// <see cref="ClipboardWriteResult"/> is classified only via
    /// <see cref="ClipboardWriteResultClassifier.IsRewriteVerified"/> (APP_MUTATED_UNVERIFIED_WRITE_POLICY)
    /// and never retried or rolled back -- see that type's own doc for what "verified" does and
    /// does NOT mean (APP_WRITE_SUCCESS_NOT_VERIFIED: never <c>ProtectionState.Verified</c>).
    ///
    /// WORKER_GENERATION_FLOW (Phase 3B STEP17, extended STEP21): <paramref name="item"/>.<c>Generation</c>
    /// is the exact value <see cref="OnClipboardChanged"/>'s advance call returned for this
    /// specific notification -- carried through this attempt and, since Phase 3B STEP21, forwarded
    /// unchanged as <see cref="IClipboardDecisionSessionPublisher.TryPublish"/>'s
    /// <c>expectedGeneration</c> when the processor produces a <see cref="ClipboardDecisionPlan"/>
    /// (see DECISION_SESSION_PUBLICATION below) -- never re-read from
    /// <c>IClipboardNotificationLifecycle.AdvanceOnClipboardNotification</c>/re-derived any other
    /// way, and never used for target gating, sequence CAS, or grant matching.
    /// <paramref name="item"/>.<c>Notification</c> replaces the bare
    /// <see cref="ClipboardChangeNotification"/> this method used to take directly; nothing below
    /// reads it (it never did -- the notification's own fields were never consulted in this
    /// method's body even before Phase 3B STEP17).
    ///
    /// DECISION_SESSION_PUBLICATION (Phase 3B STEP20 audit, frozen, implemented here): when the
    /// processor's outcome instead carries a <see cref="ClipboardDecisionPlan"/> (mutually
    /// exclusive with <see cref="ClipboardWritePlan"/> by construction -- see
    /// <see cref="ClipboardPrivacyProcessingOutcome"/>'s own doc), this coordinator makes exactly
    /// one <see cref="IClipboardDecisionSessionPublisher.TryPublish"/> call, forwarding
    /// <paramref name="item"/>.<c>Generation</c> (the exact generation captured when this
    /// notification arrived) and <see cref="ClipboardTextSnapshot.Text"/> from the SAME successful
    /// guarded read that already produced this plan (never re-read, never normalized) -- unlike the
    /// write path, this is NOT gated on <see cref="ClipboardTextSnapshot.HasReliableSequence"/>
    /// (UNRELIABLE_SEQUENCE_DECISION_SESSION_POLICY: a decision session's identity is
    /// (App generation, exact-text RevisionStamp), never clipboard sequence reliability -- see that
    /// policy's own doc for why this deliberately differs from the write path). The returned
    /// <c>bool</c> is never used to alter control flow -- no retry, no UI, no special error
    /// recovery either way (as of Phase 3C STEP41 it is additionally forwarded to a diagnostic
    /// observation only); a <c>false</c> result (the generation had already moved on) is not an
    /// error, just nothing left to do for this attempt. An unexpected exception from the publisher
    /// is never caught here -- it propagates to this method's own caller
    /// (<see cref="RunWorkerAsync"/>'s outer per-notification catch), which is what keeps the
    /// worker loop alive for later notifications.
    /// </summary>
    private async Task ProcessNotificationAsync(ClipboardDispatchItem item)
    {
        var expectedTarget = _targetCapture.Capture();
        bool authorized = TargetGate.IsSupportedTarget(expectedTarget);
        Diagnose(ClipboardDiagnosticEvent.TargetCaptured(item.Generation, expectedTarget, authorized));

        if (!authorized)
        {
            Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(item.Generation, ClipboardDiagnosticTerminalReason.UnauthorizedTarget));
            return;
        }

        var result = await _transport.ReadTextSnapshotAsync(expectedTarget).ConfigureAwait(false);
        Diagnose(ClipboardDiagnosticEvent.GuardedReadCompleted(
            item.Generation, result.Outcome, result.Snapshot?.SequenceNumber, result.Snapshot?.HasReliableSequence));

        if (result.Outcome != ClipboardReadOutcome.Success)
        {
            Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(item.Generation, ClipboardDiagnosticTerminalReason.ReadRejected));
            return;
        }

        // SUCCESS_HANDOFF_BOUNDARY: exactly one delegated call into the real Detection ->
        // Trust/Exception -> Base Policy -> Alias Assignment -> (when eligible) Alias Replacement
        // chain via the narrow IClipboardPrivacyProcessor seam -- the SAME expectedTarget
        // snapshot that already passed TargetGate above (no second App-side capture; see
        // TARGET_TOKEN_FLOW). result.Snapshot itself is never logged or otherwise touched beyond
        // being handed, once, to the processor (and, when a DecisionPlan results, once more to the
        // decision-session publisher -- see DECISION_SESSION_PUBLICATION above).
        var snapshot = result.Snapshot!.Value;
        var outcome = _processor.Process(expectedTarget, snapshot);
        Diagnose(ClipboardDiagnosticEvent.PolicyEvaluated(
            item.Generation,
            outcome.Result.CandidateCount,
            outcome.Result.TrustedCount,
            outcome.Result.ProtectCount,
            outcome.Result.NeedsDecisionCount,
            outcome.Result.BypassCount,
            outcome.WritePlan is not null,
            outcome.DecisionPlan is not null));

        // WRITE_HANDOFF (Phase 3B STEP14): a plan exists -> base policy fully resolved this
        // attempt with something to protect.
        if (outcome.WritePlan is not null)
        {
            // APP_UNRELIABLE_SEQUENCE_WRITE_POLICY: a plan may exist locally even when the read's
            // own sequence was never reliable -- the write is still never attempted in that case.
            if (!snapshot.HasReliableSequence)
            {
                Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(
                    item.Generation, ClipboardDiagnosticTerminalReason.WriteSkippedUnreliableSequence));
                return;
            }

            var writeResult = await _writeTransport.WriteTextIfSequenceMatchesAsync(
                expectedTarget, snapshot.SequenceNumber, outcome.WritePlan.ReplacementText).ConfigureAwait(false);

            // APP_MUTATED_UNVERIFIED_WRITE_POLICY / APP_WRITE_SUCCESS_NOT_VERIFIED: classification
            // only -- no retry, no rollback, no ProtectionState transition, no logging of any
            // kind. Every WRITE_RETRY_POLICY case (Busy/SequenceChanged/TargetChanged/
            // TargetUnavailable/NativeFailure/VerificationUnavailable/Superseded/ReadBackMismatch/
            // ...) ends this attempt here exactly the same way Success does: this method simply
            // returns.
            bool verified = ClipboardWriteResultClassifier.IsRewriteVerified(writeResult);
            Diagnose(ClipboardDiagnosticEvent.GuardedWriteCompleted(
                item.Generation, writeResult.Outcome, writeResult.ClipboardMutated, verified));

            // COMPOSER_VERIFICATION_HANDOFF (Phase 3C STEP31/31.1/32, implemented here): ONLY on a
            // classified-verified rewrite, exactly one Publish call using THIS attempt's own
            // (expectedTarget, replacementText, item.Generation) -- never on write failure,
            // mutated-unverified, or any other outcome. The returned bool is deliberately never
            // used to alter control flow (as of Phase 3C STEP41 it is additionally forwarded to
            // Diagnose for manual-QA correlation only), matching every other discarded-bool
            // handoff in this method.
            if (verified)
            {
                bool published = _verificationHandoff.Publish(expectedTarget, outcome.WritePlan.ReplacementText, item.Generation);
                Diagnose(ClipboardDiagnosticEvent.ComposerVerificationHandoffPublished(item.Generation, published));
                Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(item.Generation, ClipboardDiagnosticTerminalReason.Success));
            }
            else
            {
                Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(item.Generation, ClipboardDiagnosticTerminalReason.WriteRejected));
            }
            return;
        }

        // DECISION_SESSION_PUBLICATION (Phase 3B STEP20 audit, implemented here): a DecisionPlan
        // exists -> at least one candidate still needs a decision. Neither AliasReplacer nor
        // ClipboardWritePlan nor a clipboard write is ever involved on this path (STEP13/STEP19
        // contracts unchanged) -- only a session-publication attempt.
        if (outcome.DecisionPlan is not null)
        {
            bool published = _decisionSessionPublisher.TryPublish(item.Generation, snapshot.Text, outcome.DecisionPlan);
            Diagnose(ClipboardDiagnosticEvent.DecisionSessionPublishAttempted(
                item.Generation, outcome.DecisionPlan.Items.Count, published));
            Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(item.Generation, ClipboardDiagnosticTerminalReason.DecisionPending));
            return;
        }

        // ALL_BYPASS / NO_PII (Phase 3C STEP41 diagnostic terminal): neither plan was produced --
        // nothing to protect and nothing to decide. Unchanged control flow from before this STEP
        // (this method simply falls through to its end either way); the only addition is this
        // terminal-reason observation.
        Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(item.Generation, ClipboardDiagnosticTerminalReason.NoActionRequired));
    }
}
