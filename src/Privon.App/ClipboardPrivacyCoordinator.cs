using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP2 -- App Clipboard Dispatch + TargetGate Read Coordinator, extended in Phase 3B
/// STEP4 with the real Detection handoff, in Phase 3B STEP14 with the guarded clipboard write, in
/// Phase 3B STEP17 with the atomic clipboard-attempt generation lifecycle (Phase 3B
/// STEP16/STEP16.1), in Phase 3B STEP21 with decision-session publication (Phase 3B STEP20
/// audit), in Phase 3B STEP23 with the shared App-level operation gate (Phase 3B STEP22
/// audit's SELECTED_EXECUTION_LANE, MODEL D), in Phase 3C STEP32 with the composer-verification
/// handoff/invalidation seams (<see cref="IClipboardComposerVerificationHandoff"/>/
/// <see cref="IClipboardComposerVerificationInvalidation"/>, Phase 3C STEP31/31.1 audits, frozen),
/// and in Phase 0.2D (STEP61) with a SECOND, independent trigger --
/// <see cref="IClipboardForegroundTrigger"/> -- sharing this same worker lane, mailbox, and privacy
/// pipeline (Phase 0.2A/STEP55 HYBRID architecture, Phase 0.2B/STEP56 foreground-signal contract,
/// Phase 0.2C/STEP58-59 per-generation evaluation state).
///
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
/// <see cref="IClipboardDecisionSessionPublisher"/>/<see cref="IClipboardOperationGate"/>/
/// <see cref="IClipboardForegroundTrigger"/> are all mechanics-only, exactly like
/// <see cref="IClipboardReadTransport"/>/<see cref="IForegroundTargetCapture"/> are narrow seams
/// rather than the real Windows/Detection/Core types themselves -- this type understands "a plan
/// exists, forward its text, the same target, the same sequence," "a decision plan exists, forward
/// it with this attempt's exact generation and the exact raw text that was just read," "advance the
/// lifecycle, carry the returned generation," "a second, independent signal arrived -- go find out
/// what the current generation actually is and try to claim it," and "hold one permit for the
/// duration of one attempt," never <c>PiiType</c>/<c>CandidatePolicyDecision</c>/<c>AliasToken</c>/
/// <c>TrustState</c>/<c>RevisionTracker</c>/<c>RevisionStamp</c>/revision grants.
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
/// <see cref="OnForegroundChanged"/> (Phase 0.2D) carries the identical discipline -- a single
/// non-blocking <c>TryWrite</c>, nothing else, never acquires this gate.
///
/// OPERATION_GATE_ACQUISITION_BOUNDARY (Phase 3B STEP23, frozen): the gate is acquired in
/// <see cref="RunWorkerAsync"/> -- never while merely awaiting the next mailbox item -- and held
/// for the ENTIRE dequeued attempt (target capture/gate check, the awaited guarded read, the
/// processor call, and whichever of the guarded write / decision-session publication follows),
/// released in a <c>finally</c> block covering every outcome (success, target rejection, read
/// failure, a processor/publisher/write-transport exception). An idle coordinator -- no item yet
/// dequeued -- never holds this gate. LOCK_ORDER (frozen, no cycle possible):
/// <see cref="ProcessWorkItemAsync"/> may, while already holding this gate, reach
/// <see cref="IClipboardDecisionSessionPublisher.TryPublish"/>, which itself briefly takes
/// <c>ClipboardDecisionScopeLifecycle</c>'s own tiny synchronous <c>_gate</c> -- i.e. "operation
/// gate -&gt; lifecycle lock" may occur. The reverse never can: <see cref="OnClipboardChanged"/>/
/// <see cref="OnForegroundChanged"/> take the lifecycle lock (via
/// <see cref="IClipboardNotificationLifecycle.AdvanceOnClipboardNotification"/>/nothing at all,
/// respectively) but never acquire this operation gate at all -- "lifecycle lock -&gt; operation
/// gate" is structurally unreachable, not merely avoided by convention. These are two different
/// locks serving two different purposes and are never merged: this gate is an async, App-semantic,
/// whole-attempt serialization primitive; the lifecycle's own <c>_gate</c> is a tiny synchronous
/// generation/scope-atomicity primitive (Phase 3B STEP16.1) -- see
/// <c>ClipboardPrivacyCoordinatorTests.NewNotification_AdvancesLifecycle_EvenWhileOperationGateIsHeld</c>/
/// <c>NonTextNotification_AdvancesLifecycle_EvenWhileOperationGateHeld</c> for the behavioral,
/// deadlock-freedom regression this ordering guarantee rests on.
///
/// CROSS_TRIGGER_SHARED_MAILBOX (Phase 0.2D, STEP61): the mailbox is now
/// <see cref="Channel{T}"/>&lt;<see cref="ClipboardCoordinatorWorkItem"/>&gt; with
/// <c>SingleWriter = false</c> -- there are now TWO independent producer threads (the clipboard
/// owner thread via <see cref="OnClipboardChanged"/>, and a second, independent owner thread via
/// <see cref="OnForegroundChanged"/>), and <see cref="System.Threading.Channels"/> itself already
/// supplies all writer-side synchronization needed for that; no external lock is added around
/// either <c>TryWrite</c> call. There remains exactly ONE reader (<see cref="RunWorkerAsync"/>).
/// SHARED_TRIGGER_INTAKE_VS_PIPELINE (frozen): <see cref="ProcessWorkItemAsync"/> is the only place
/// that ever branches on <see cref="ClipboardCoordinatorWorkItem.Kind"/> -- it performs target
/// capture, <see cref="TargetGate"/> authorization, and (only for an authorized target) determines
/// which generation to attempt to claim (the item's own captured generation for
/// <see cref="ClipboardCoordinatorTriggerKind.ClipboardChanged"/>; a FRESH
/// <see cref="IClipboardGenerationSnapshot.CurrentGeneration"/> read, taken at this exact moment,
/// for <see cref="ClipboardCoordinatorTriggerKind.ForegroundChanged"/> -- WHY_READING_LATER_IS_NOT_ENOUGH
/// applies here exactly as it does to the clipboard-item's own captured generation: reading it any
/// earlier, or trusting a value carried on the foreground event itself, would not correspond to
/// "the generation as of right now"). Once <see cref="IClipboardEvaluationLifecycle.TryBeginEvaluation"/>
/// succeeds, EVERYTHING from that point onward -- <see cref="RunPrivacyPipelineAsync"/> -- is
/// completely trigger-agnostic: it takes only an already-authorized <see cref="ForegroundTargetSnapshot"/>
/// and an already-claimed generation, contains no branch of any kind on
/// <see cref="ClipboardCoordinatorTriggerKind"/>, and is the exact same v0.1 guarded-read -&gt;
/// processor -&gt; WritePlan/DecisionPlan -&gt; guarded-write/verification -&gt; decision-publishing
/// path this type has always run, now simply reachable from either trigger.
///
/// CROSS_TRIGGER_SINGLE_EVALUATION (frozen): an unauthorized/unresolved target captured during
/// intake takes NO evaluation claim at all (<see cref="IClipboardEvaluationLifecycle.TryBeginEvaluation"/>
/// is never even called) -- this is what lets a LATER trigger (of either kind) still claim and
/// evaluate the SAME still-current generation once the target does become authorized (e.g. the user
/// switches to ChatGPT a moment after copying elsewhere). A claim that DOES succeed always resolves
/// to exactly one of <see cref="IClipboardEvaluationLifecycle.CompleteEvaluation"/> (a Terminal fact
/// -- see <see cref="ClipboardEvaluationOutcomeClassifier"/> and this type's own DIRECT_PIPELINE_FACTS
/// doc on <see cref="RunPrivacyPipelineAsync"/>) or <see cref="IClipboardEvaluationLifecycle.AbandonEvaluation"/>
/// (a Retryable fact) before that attempt ends -- with the sole intentional exception of an
/// unexpected exception escaping <see cref="ProcessWorkItemAsync"/>'s own pre-pipeline intake code
/// (target capture/gate/generation-read/claim itself), which this type does not attempt to classify
/// or report against the evaluation lifecycle at all (see <see cref="RunWorkerAsync"/>'s own doc).
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

    /// <summary>
    /// BUG-002 Gate 2A final contract (frozen for this STEP): the maximum number of TOTAL attempts
    /// -- the original intake attempt plus every autonomous retry -- this coordinator will make for
    /// one claimed generation. 3 retries maximum (4 total), fixed delay, no exponential backoff --
    /// see <see cref="RetryDelayIntervalContractDefault"/>. Chosen as a small, deterministic,
    /// practically-testable bound, not a latency-optimized or probability-derived value.
    /// </summary>
    internal const int MaxAttemptsContractDefault = 4;

    /// <summary>
    /// BUG-002 Gate 2A final contract (frozen for this STEP): the fixed pause between attempts,
    /// applied via the injected <see cref="IClipboardRetryDelay"/> seam -- never a real
    /// <see cref="Task.Delay(TimeSpan)"/> call directly, so the Gate 2B regression suite can drive
    /// every retry deterministically. Worst-case total scheduled delay across the whole budget
    /// (3 x 250ms = 750ms) stays comfortably inside <see cref="WorkerStopTimeoutContractDefault"/>
    /// -- see <see cref="ProcessWorkItemAsync"/>'s own SHUTDOWN_LINEARIZATION doc for why no
    /// <see cref="System.Threading.CancellationToken"/> is needed to keep shutdown bounded.
    /// </summary>
    internal static readonly TimeSpan RetryDelayIntervalContractDefault = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// COORDINATOR_LIFECYCLE_STATE_MODEL (Phase 0.2D, STEP61): replaces the old
    /// <c>_startCalled</c>/<c>_running</c> boolean pair, which was no longer sufficient once TWO
    /// independently-ownable, independently-stoppable sources (<see cref="_transport"/> and the
    /// optional <see cref="_foregroundTrigger"/>) exist. <see cref="TransitionInProgress"/> is
    /// load-bearing: it means exactly one thread currently owns either the startup transition
    /// (inside <see cref="Start"/>) or a cleanup transition (inside <see cref="PerformCleanup"/>,
    /// reached from <see cref="Start"/>'s own failure path, <see cref="Stop"/>, or
    /// <see cref="Dispose"/>) -- never both, never two threads performing either concurrently. All
    /// five values, and the two ownership booleans below, are guarded exclusively by
    /// <see cref="_gate"/> -- no second lock/semaphore of any kind exists on this type.
    /// </summary>
    private enum CoordinatorPhase
    {
        NotStarted,
        Running,
        TransitionInProgress,
        CleanupPending,
        Stopped,
    }

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
    private readonly IClipboardForegroundTrigger? _foregroundTrigger;
    private readonly IClipboardRetryDelay _retryDelay;
    private readonly Channel<ClipboardCoordinatorWorkItem> _mailbox;
    private readonly TimeSpan _workerStopTimeout;
    private readonly object _gate = new();

    private CoordinatorPhase _phase = CoordinatorPhase.NotStarted;
    private bool _clipboardTransportOwned;
    private bool _foregroundTriggerOwned;
    private bool _mailboxCompleted;
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
        IClipboardDiagnosticRecorder? diagnostics = null,
        IClipboardForegroundTrigger? foregroundTrigger = null,
        IClipboardRetryDelay? retryDelay = null)
        : this(transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation, stopTimeoutOverride: null, diagnostics: diagnostics, foregroundTrigger: foregroundTrigger, retryDelay: retryDelay)
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
        IClipboardDiagnosticRecorder? diagnostics = null,
        IClipboardForegroundTrigger? foregroundTrigger = null,
        IClipboardRetryDelay? retryDelay = null)
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
        // Phase 0.2D (STEP61) -- optional, trailing: omitting this argument (every pre-STEP61 call
        // site) means the coordinator behaves EXACTLY as before -- no second source is ever
        // subscribed/started/stopped, no foreground-triggered evaluation ever happens.
        _foregroundTrigger = foregroundTrigger;
        // BUG-002 Gate 2C -- optional, trailing: omitting this argument (every pre-Gate-2C call
        // site) resolves to the real Task.Delay-backed ClipboardRetryDelay, giving production the
        // real fixed 250ms interval. A test injects FakeClipboardRetryDelay to drive the retry loop
        // in ProcessWorkItemAsync deterministically instead.
        _retryDelay = retryDelay ?? new ClipboardRetryDelay();
        _workerStopTimeout = stopTimeoutOverride ?? WorkerStopTimeoutContractDefault;

        // CHANNEL_POLICY: capacity 1, DropOldest, single reader, MULTI writer (Phase 0.2D --
        // widened from SingleWriter=true now that OnClipboardChanged and OnForegroundChanged are
        // two independent producer threads; System.Threading.Channels itself already supplies all
        // writer-side synchronization this needs -- no external lock is added around either
        // TryWrite call). Gives latest-wins coalescing for free across BOTH trigger kinds; no
        // debounce timer, no third-party package. Element type is ClipboardCoordinatorWorkItem
        // (Phase 0.2D, replacing the clipboard-only ClipboardDispatchItem) -- see that type's own
        // doc for why the generation must travel WITH a clipboard-triggered item, and why a
        // foreground-triggered item deliberately carries none.
        _mailbox = Channel.CreateBounded<ClipboardCoordinatorWorkItem>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// BUG-002 Gate 2C -- the injected retry-delay seam, exposed read-only so a regression can
    /// prove WHICH instance this coordinator holds. Consumed by exactly one call site --
    /// <see cref="ProcessWorkItemAsync"/>'s own bounded retry loop -- immediately before every
    /// retry attempt (never before the original, first attempt for a claimed generation).
    /// </summary>
    internal IClipboardRetryDelay RetryDelay => _retryDelay;

    /// <summary>
    /// APP_COORDINATOR_LIFECYCLE: single-use, matching <c>ClipboardChangeMonitor.Start</c>'s own
    /// exact precedent -- a second call always throws, regardless of whether the first succeeded
    /// or failed. STARTUP_ORDER (frozen, Phase 0.2D): validate lifecycle state -&gt; create/start
    /// the consumer worker Task -&gt; subscribe to <see cref="IClipboardReadTransport.Changed"/>
    /// -&gt; subscribe to the optional <see cref="IClipboardForegroundTrigger.Changed"/> -&gt;
    /// start <see cref="IClipboardReadTransport.Start"/> (recording clipboard ownership immediately
    /// on success) -&gt; start the optional <see cref="IClipboardForegroundTrigger.Start"/> (if
    /// supplied; recording foreground ownership immediately on success). The worker is started (and
    /// therefore able to drain the channel) before either source becomes active, so no notification
    /// from either trigger can ever arrive with no consumer eventually able to process it.
    ///
    /// START_FAILURE_ROLLBACK (Phase 0.2D): if EITHER <see cref="IClipboardReadTransport.Start"/> or
    /// <see cref="IClipboardForegroundTrigger.Start"/> throws, this method runs the exact same
    /// exclusive cleanup <see cref="Stop"/>/<see cref="Dispose"/> use (see
    /// <see cref="PerformCleanup"/>'s own doc) -- unsubscribing both handlers, stopping whichever
    /// source(s) actually finished starting, completing the mailbox, and making a best-effort
    /// bounded wait for the worker to exit -- before rethrowing the ORIGINAL exception unchanged; a
    /// cleanup failure during rollback is captured but never replaces that original exception. A
    /// failed Start still counts as "used up" the single-use guarantee (this instance can never be
    /// started again), exactly like <c>ClipboardChangeMonitor</c>'s own <c>_startCalled</c>
    /// semantics -- and never leaves <see cref="_phase"/> back at <see cref="CoordinatorPhase.NotStarted"/>.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_phase != CoordinatorPhase.NotStarted)
                throw new InvalidOperationException(
                    "Start has already been called on this ClipboardPrivacyCoordinator instance -- it is single-use.");
            _phase = CoordinatorPhase.TransitionInProgress;
        }

        try
        {
            _workerTask = Task.Run(RunWorkerAsync);
            _transport.Changed += OnClipboardChanged;
            if (_foregroundTrigger is not null)
                _foregroundTrigger.Changed += OnForegroundChanged;

            _transport.Start();
            lock (_gate) { _clipboardTransportOwned = true; }

            if (_foregroundTrigger is not null)
            {
                _foregroundTrigger.Start();
                lock (_gate) { _foregroundTriggerOwned = true; }
            }

            lock (_gate)
            {
                _phase = CoordinatorPhase.Running;
                Monitor.PulseAll(_gate);
            }
        }
        catch
        {
            // START_FAILURE_ROLLBACK -- _phase is already TransitionInProgress (set above), so
            // PerformCleanup can run directly; its own return value (any cleanup failure) is
            // deliberately discarded here -- only the ORIGINAL exception below ever propagates.
            PerformCleanup();
            throw;
        }
    }

    /// <summary>
    /// SHUTDOWN_ORDER (frozen, corrected, extended Phase 0.2D): (1) claim exclusive cleanup rights
    /// -- a Stop-before-Start call is a harmless no-op that leaves <see cref="_phase"/> at
    /// <see cref="CoordinatorPhase.NotStarted"/> (see <see cref="ClaimCleanupTransition"/>'s own
    /// doc); a Stop against an already-fully-<see cref="CoordinatorPhase.Stopped"/> instance is
    /// likewise a silent no-op; (2) run the shared exclusive cleanup (see
    /// <see cref="PerformCleanup"/>'s own doc for the full unsubscribe/stop/mailbox-complete/
    /// worker-wait sequence, now covering BOTH <see cref="_transport"/> and the optional
    /// <see cref="_foregroundTrigger"/>); (3) rethrow (preserving the original stack trace via
    /// <see cref="ExceptionDispatchInfo"/>) whatever failure -- if any -- that cleanup encountered.
    /// FAILED_STOP_RETRYABLE (Phase 0.2D): a failure here leaves <see cref="_phase"/> at
    /// <see cref="CoordinatorPhase.CleanupPending"/>, not <see cref="CoordinatorPhase.Stopped"/> --
    /// a LATER <see cref="Stop"/>/<see cref="Dispose"/> call is guaranteed to claim exclusive
    /// cleanup rights again and genuinely retry only whatever remains (see
    /// <see cref="PerformCleanup"/>'s own ownership-flag doc), never silently no-op over an
    /// unresolved failure.
    /// </summary>
    public void Stop()
    {
        if (!ClaimCleanupTransition())
            return;

        var failure = PerformCleanup();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// DISPOSE_BEFORE_START_TERMINAL (Phase 0.2D, frozen): unlike <see cref="Stop"/>-before-Start
    /// (a harmless no-op that leaves <see cref="_phase"/> unchanged),
    /// <see cref="Dispose"/>-before-<see cref="Start"/> IS a real, terminal state transition --
    /// <see cref="_phase"/> moves directly to <see cref="CoordinatorPhase.Stopped"/> and
    /// <see cref="_disposed"/> becomes <see langword="true"/>, so a LATER <see cref="Start"/> call
    /// correctly throws <see cref="ObjectDisposedException"/> rather than silently proceeding as if
    /// this instance had never been touched. For every other state, this method claims the SAME
    /// exclusive cleanup transition <see cref="Stop"/> uses and is otherwise
    /// DISPOSE_FAILURE_BEHAVIOR-identical to every other <c>Dispose</c> in this codebase (e.g.
    /// <c>ClipboardChangeMonitor.Dispose</c>): <see cref="_disposed"/> is set to
    /// <see langword="true"/> ONLY once cleanup has ACTUALLY fully succeeded (<see cref="_phase"/>
    /// reaches <see cref="CoordinatorPhase.Stopped"/>) -- a cleanup failure here is thrown, leaves
    /// <see cref="_disposed"/> <see langword="false"/>, and a LATER <see cref="Dispose"/> call
    /// genuinely retries the still-outstanding cleanup rather than silently no-op'ing over it.
    /// CONCURRENT_STOP_DISPOSE_OVERLAP_SAFE: this method never returns claiming success while an
    /// overlapping <see cref="Stop"/>/<see cref="Dispose"/> call's own exclusive cleanup is still
    /// unresolved -- it waits (via <see cref="ClaimCleanupTransition"/>'s own
    /// <see cref="Monitor.Wait(object)"/> loop) for that transition to fully resolve first.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_phase == CoordinatorPhase.NotStarted)
            {
                _phase = CoordinatorPhase.Stopped;
                _disposed = true;
                return;
            }
        }

        if (!ClaimCleanupTransition())
        {
            // Another concurrent Stop/Dispose already fully resolved cleanup while this call was
            // waiting inside ClaimCleanupTransition -- _phase is guaranteed Stopped here (NotStarted
            // was already excluded above, and NotStarted is never re-entered once left).
            lock (_gate)
            {
                if (_phase == CoordinatorPhase.Stopped)
                    _disposed = true;
            }

            return;
        }

        var failure = PerformCleanup();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
            return;
        }

        lock (_gate)
        {
            _disposed = true;
        }
    }

    /// <summary>
    /// CLEANUP_SINGLE_EXECUTOR (Phase 0.2D, frozen): atomically claims exclusive cleanup rights,
    /// used identically by <see cref="Stop"/> and <see cref="Dispose"/> (never by <see cref="Start"/>,
    /// which sets <see cref="CoordinatorPhase.TransitionInProgress"/> itself as part of ITS OWN
    /// single-use guard -- see <see cref="Start"/>'s own doc). Returns <see langword="true"/>, having
    /// atomically transitioned <see cref="_phase"/> to <see cref="CoordinatorPhase.TransitionInProgress"/>,
    /// only when the caller is the ONE thread that gets to actually run <see cref="PerformCleanup"/>
    /// next. Returns <see langword="false"/>, with <see cref="_phase"/> left UNCHANGED, for
    /// <see cref="CoordinatorPhase.NotStarted"/> (Stop-before-Start: a harmless no-op) and
    /// <see cref="CoordinatorPhase.Stopped"/> (cleanup already fully done: also a no-op). If another
    /// thread currently holds <see cref="CoordinatorPhase.TransitionInProgress"/>, this method waits
    /// on <see cref="_gate"/> (releasing it while waiting, exactly like every other
    /// <see cref="Monitor.Wait(object)"/> use in this codebase) until that transition resolves --
    /// to either <see cref="CoordinatorPhase.Stopped"/> (nothing left to do, returns
    /// <see langword="false"/>) or <see cref="CoordinatorPhase.CleanupPending"/> (something remains;
    /// this thread claims it and returns <see langword="true"/>) -- so two concurrent
    /// <see cref="Stop"/>/<see cref="Dispose"/> callers can never invoke either source's native
    /// <c>Stop</c> concurrently, and a waiting <see cref="Dispose"/> can never report success before
    /// an overlapping cleanup attempt has actually resolved.
    /// </summary>
    private bool ClaimCleanupTransition()
    {
        lock (_gate)
        {
            while (true)
            {
                switch (_phase)
                {
                    case CoordinatorPhase.NotStarted:
                    case CoordinatorPhase.Stopped:
                        return false;
                    case CoordinatorPhase.Running:
                    case CoordinatorPhase.CleanupPending:
                        _phase = CoordinatorPhase.TransitionInProgress;
                        return true;
                    case CoordinatorPhase.TransitionInProgress:
                    default:
                        Monitor.Wait(_gate);
                        continue;
                }
            }
        }
    }

    /// <summary>
    /// The single shared exclusive-cleanup body -- assumes the caller ALREADY holds exclusive
    /// transition rights (<see cref="_phase"/> == <see cref="CoordinatorPhase.TransitionInProgress"/>,
    /// established either by <see cref="Start"/>'s own single-use guard on its failure path, or by
    /// <see cref="ClaimCleanupTransition"/> on the <see cref="Stop"/>/<see cref="Dispose"/> path) --
    /// never claims that transition itself. CLEANUP_ORDER (frozen): unsubscribe
    /// <see cref="IClipboardForegroundTrigger.Changed"/> (if a trigger was supplied), THEN
    /// unsubscribe <see cref="IClipboardReadTransport.Changed"/>, unconditionally and BEFORE either
    /// source's own <c>Stop</c> is even considered (multicast-delegate <c>-=</c> against an
    /// already-unsubscribed/never-subscribed handler is a documented safe no-op) -- so the App side
    /// stops accepting new notifications from either source first. Then, ONLY for whichever
    /// source(s) this instance actually owns (<see cref="_clipboardTransportOwned"/>/
    /// <see cref="_foregroundTriggerOwned"/> -- cleared ONLY once that source's own <c>Stop</c>
    /// call has actually returned successfully): <see cref="IClipboardReadTransport.Stop"/>, THEN
    /// (independently -- attempted even if the clipboard <c>Stop</c> above threw)
    /// <see cref="IClipboardForegroundTrigger.Stop"/>. FAILED_START_RETRY_CLEANUP /
    /// FAILED_STOP_RETRYABLE: a source whose own <c>Stop</c> throws keeps its ownership flag
    /// <see langword="true"/> -- a LATER call to this method (via a retried
    /// <see cref="Stop"/>/<see cref="Dispose"/>) will attempt that SAME source's <c>Stop</c> again,
    /// while skipping a source that already succeeded (its flag is already <see langword="false"/>).
    ///
    /// MAILBOX_COMPLETE_AT_MOST_ONCE (frozen): <see cref="_mailbox"/>'s writer is completed EXACTLY
    /// once, ever, for this instance -- guarded by <see cref="_mailboxCompleted"/> under
    /// <see cref="_gate"/> -- regardless of how many times this method itself is retried after a
    /// partial failure. WORKER_WAIT_RETRYABLE: <see cref="_workerTask"/> (the SAME Task instance
    /// across every retry -- never recreated, never a new <see cref="Channel{T}"/>) is waited on
    /// with <see cref="_workerStopTimeout"/>; a timeout here is captured as this method's own
    /// failure (never thrown directly the way the pre-STEP61 <see cref="Stop"/> used to) and is
    /// itself safely retryable -- <see cref="Task.Wait(TimeSpan)"/> may be called repeatedly against
    /// the same still-running (or by-then-completed) Task.
    ///
    /// Every step's own exception is captured independently (the FIRST one encountered is what this
    /// method returns) so that one failing step never prevents an attempt at every remaining step --
    /// exactly matching this codebase's established <c>PrivonAppComposition.Dispose</c> precedent.
    /// On return, <see cref="_phase"/> is set, under <see cref="_gate"/>, to
    /// <see cref="CoordinatorPhase.Stopped"/> ONLY if no failure occurred AND neither ownership flag
    /// remains <see langword="true"/> -- otherwise <see cref="CoordinatorPhase.CleanupPending"/> --
    /// and every thread currently waiting inside <see cref="ClaimCleanupTransition"/> is woken via
    /// <see cref="Monitor.PulseAll(object)"/>.
    /// </summary>
    private Exception? PerformCleanup()
    {
        Exception? firstFailure = null;

        if (_foregroundTrigger is not null)
            _foregroundTrigger.Changed -= OnForegroundChanged;
        _transport.Changed -= OnClipboardChanged;

        bool clipboardOwned, foregroundOwned;
        lock (_gate)
        {
            clipboardOwned = _clipboardTransportOwned;
            foregroundOwned = _foregroundTriggerOwned;
        }

        if (clipboardOwned)
        {
            try
            {
                _transport.Stop();
                lock (_gate) { _clipboardTransportOwned = false; }
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        if (foregroundOwned)
        {
            try
            {
                _foregroundTrigger!.Stop();
                lock (_gate) { _foregroundTriggerOwned = false; }
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        bool shouldCompleteMailbox;
        lock (_gate)
        {
            shouldCompleteMailbox = !_mailboxCompleted;
            if (shouldCompleteMailbox) _mailboxCompleted = true;
        }

        if (shouldCompleteMailbox)
        {
            try
            {
                _mailbox.Writer.Complete();
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        if (_workerTask is not null)
        {
            try
            {
                if (!_workerTask.Wait(_workerStopTimeout))
                {
                    throw new InvalidOperationException(
                        $"Clipboard privacy coordinator worker did not exit within the shutdown timeout ({_workerStopTimeout.TotalSeconds:F0}s).");
                }
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        lock (_gate)
        {
            bool stillOwned = _clipboardTransportOwned || _foregroundTriggerOwned;
            _phase = firstFailure is null && !stillOwned ? CoordinatorPhase.Stopped : CoordinatorPhase.CleanupPending;
            Monitor.PulseAll(_gate);
        }

        return firstFailure;
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
    /// <see cref="ClipboardCoordinatorWorkItem"/> (Phase 0.2D, replacing the clipboard-only
    /// <c>ClipboardDispatchItem</c>) carrying the notification and the EXACT generation the
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

        bool accepted = _mailbox.Writer.TryWrite(ClipboardCoordinatorWorkItem.ForClipboardChange(generation));
        Diagnose(ClipboardDiagnosticEvent.MailboxWriteAttempted(generation, accepted));
    }

    /// <summary>
    /// CALLBACK_BOUNDARY (Phase 0.2D, STEP61): runs synchronously on the foreground trigger's own
    /// underlying owner thread -- a DIFFERENT thread from the one <see cref="OnClipboardChanged"/>
    /// runs on. Does exactly one thing: a single non-blocking
    /// <see cref="ChannelWriter{T}.TryWrite"/> of <see cref="ClipboardCoordinatorWorkItem.ForForegroundChange"/>,
    /// then returns. Deliberately does NOT advance <see cref="_notificationLifecycle"/> -- a
    /// foreground-focus change is not itself a clipboard-content change, so it must never bump the
    /// generation or invalidate an active decision scope on its own (see
    /// <see cref="ClipboardCoordinatorWorkItem"/>'s own doc). No foreground capture, no clipboard
    /// read, no async work, no logging of any content, no <see cref="_operationGate"/> acquisition
    /// of any kind, ever happens here -- the identical CALLBACK_NONBLOCKING_PROOF discipline
    /// <see cref="OnClipboardChanged"/> already carries. No diagnostic observation is recorded for
    /// mere arrival here (Phase 0.2D instruction's explicit FOREGROUND_DIAGNOSTICS policy -- no new
    /// diagnostic stage/type is added merely for a foreground-trigger arrival, and there is no
    /// sequence/notification metadata/PID/HWND/process name to safely fabricate one from); the
    /// EXISTING shared-pipeline diagnostics (<see cref="Diagnose"/> calls inside
    /// <see cref="RunPrivacyPipelineAsync"/>) continue naturally, keyed by the real generation this
    /// attempt actually claims, once (and only once) <see cref="ProcessWorkItemAsync"/>'s own intake
    /// step successfully claims one.
    /// </summary>
    private void OnForegroundChanged(object? sender, EventArgs e)
    {
        _mailbox.Writer.TryWrite(ClipboardCoordinatorWorkItem.ForForegroundChange());
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
    /// only AFTER <c>await foreach</c> has actually yielded a dequeued <see cref="ClipboardCoordinatorWorkItem"/>
    /// -- never while merely awaiting the next mailbox item, so an idle coordinator (nothing to
    /// process) never holds the gate and can never block a future concurrent decision-action
    /// attempt indefinitely. RELEASE_BOUNDARY: the gate is released in a <c>finally</c> block
    /// that covers every possible outcome of the attempt below -- normal completion, a target
    /// rejection, a guarded-read failure, or an exception from any pipeline component (all already
    /// isolated from ending the worker loop by the identical outer <c>catch</c> below, unchanged in
    /// spirit from before this STEP). SHUTDOWN_BEHAVIOR: no <see cref="CancellationToken"/> is
    /// introduced anywhere in this acquisition -- consistent with every other async seam in this
    /// codebase (see <c>IClipboardReadTransport_ReadTextSnapshotAsync_HasNoCancellationTokenParameter</c>'s
    /// own precedent) -- so a worker stuck awaiting <see cref="IClipboardOperationGate.WaitAsync"/>
    /// during shutdown fails exactly the same deterministic, already-tested way any other stuck
    /// worker already does: <see cref="PerformCleanup"/>'s own bounded worker wait times out and is
    /// reported as a retryable cleanup failure (see <c>Stop_WorkerDoesNotExitInTime_ThrowsDeterministically</c>)
    /// -- no new shutdown machinery was needed or added.
    ///
    /// WORKER_SURVIVAL / EXCEPTION_BOUNDARY (Phase 0.2D): every exception a real pipeline component
    /// (processor/write-transport/decision-session-publisher/composer-verification-handoff) throws
    /// is ALREADY caught, reported to the evaluation lifecycle as
    /// <see cref="IClipboardEvaluationLifecycle.AbandonEvaluation"/>, and diagnosed INSIDE
    /// <see cref="RunPrivacyPipelineAsync"/> itself (see that method's own doc) -- so this outer
    /// <c>catch</c> exists purely as a defense-in-depth backstop against an exception escaping
    /// <see cref="ProcessWorkItemAsync"/>'s own pre-pipeline intake code (target capture/
    /// <see cref="TargetGate"/> check/fresh generation read/the claim call itself), none of which
    /// this codebase's own contracts document as ever throwing in practice. This backstop
    /// deliberately does NOT attempt to report anything to the evaluation lifecycle (an exception at
    /// that stage means this attempt may never have determined, let alone claimed, a real
    /// generation to report against in the first place) -- it only ensures the worker loop itself
    /// can never die, mirroring <see cref="Privon.Windows.ClipboardChangeMonitor.Changed"/>'s own
    /// subscriber-exception-isolation precedent. A clipboard-triggered item's own already-known
    /// <see cref="ClipboardCoordinatorWorkItem.ClipboardGeneration"/> is still forwarded to
    /// <see cref="Diagnose"/> here when available (Phase 3C STEP41 manual-QA correlation, unchanged
    /// in spirit); a foreground-triggered item has no such value to forward, so nothing is
    /// diagnosed for it at this backstop layer (FOREGROUND_DIAGNOSTICS policy, see
    /// <see cref="OnForegroundChanged"/>'s own doc).
    /// </summary>
    private async Task RunWorkerAsync()
    {
        await foreach (var item in _mailbox.Reader.ReadAllAsync())
        {
            if (item.Kind == ClipboardCoordinatorTriggerKind.None)
            {
                // Structurally unreachable via TryWrite -- both ClipboardCoordinatorWorkItem
                // factories always set a real Kind -- but guarded anyway so a stray
                // default(ClipboardCoordinatorWorkItem) can never enter the privacy pipeline.
                continue;
            }

            if (item.Kind == ClipboardCoordinatorTriggerKind.ClipboardChanged)
                Diagnose(ClipboardDiagnosticEvent.WorkerDequeued(item.ClipboardGeneration));

            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ProcessWorkItemAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (item.Kind == ClipboardCoordinatorTriggerKind.ClipboardChanged)
                {
                    Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(
                        item.ClipboardGeneration, ClipboardDiagnosticTerminalReason.ProcessingException, ex.GetType().Name));
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    /// <summary>
    /// SHARED_TRIGGER_INTAKE (Phase 0.2D, STEP61): the only place this type ever branches on
    /// <see cref="ClipboardCoordinatorWorkItem.Kind"/> -- performs a fresh
    /// <see cref="IForegroundTargetCapture.Capture"/> (regardless of trigger kind -- TARGET_TOKEN_FLOW,
    /// unchanged: captured EXACTLY ONCE per attempt, that SAME snapshot is what
    /// <see cref="TargetGate"/> evaluates and what the guarded read below eventually receives; no
    /// second App-side capture is ever performed) and applies <see cref="TargetGate"/>. For a
    /// <see cref="ClipboardCoordinatorTriggerKind.ClipboardChanged"/> item the existing
    /// <see cref="ClipboardDiagnosticStage.TargetCaptured"/>/<see cref="ClipboardDiagnosticTerminalReason.UnauthorizedTarget"/>
    /// diagnostics (unchanged from before this STEP, keyed by the item's own already-known
    /// <see cref="ClipboardCoordinatorWorkItem.ClipboardGeneration"/>) still fire exactly as before.
    /// For a <see cref="ClipboardCoordinatorTriggerKind.ForegroundChanged"/> item, NOTHING is
    /// diagnosed at this pre-claim stage -- there is no real attempt identity (generation) yet to
    /// key an observation with, and this STEP does not fabricate one (see
    /// <see cref="OnForegroundChanged"/>'s own doc).
    ///
    /// CROSS_TRIGGER_SINGLE_EVALUATION (frozen): an unauthorized/unresolved target takes NO
    /// evaluation claim of any kind -- <see cref="IClipboardEvaluationLifecycle.TryBeginEvaluation"/>
    /// is never even called -- so a later, independent trigger (of either kind) remains free to
    /// claim and evaluate this SAME still-current generation once/if the target does become
    /// authorized. Only for an authorized target does this method determine which generation to
    /// attempt to claim: <see cref="ClipboardCoordinatorWorkItem.ClipboardGeneration"/> itself for
    /// <see cref="ClipboardCoordinatorTriggerKind.ClipboardChanged"/> (already captured at arrival --
    /// WHY_READING_LATER_IS_NOT_ENOUGH, unchanged), or a FRESH
    /// <see cref="IClipboardGenerationSnapshot.CurrentGeneration"/> read taken at THIS exact moment
    /// for <see cref="ClipboardCoordinatorTriggerKind.ForegroundChanged"/> (the identical
    /// WHY_READING_LATER_IS_NOT_ENOUGH reasoning applies symmetrically here -- this is the earliest
    /// point a foreground-triggered attempt can meaningfully ask "what generation am I even trying
    /// to evaluate," and reading it any earlier, or trusting any value carried on the foreground
    /// event itself, would not correspond to "the generation as of right now"). A
    /// <see cref="IClipboardEvaluationLifecycle.TryBeginEvaluation"/> failure (stale generation, or
    /// already claimed/evaluated by a concurrent/earlier attempt for the SAME generation) is a
    /// silent no-op -- nothing meaningful to diagnose or act on either way (see that method's own
    /// doc: the caller cannot distinguish which reason applied and does not need to). Only once the
    /// claim succeeds does this method hand off to the fully trigger-agnostic
    /// <see cref="RunPrivacyPipelineAsync"/>.
    ///
    /// BUG-002 RETRY_CONTROLLER (Gate 2C, final Gate 2A contract): after the intake claim above,
    /// this method also owns the bounded autonomous-retry loop -- and ONLY the loop's own non-PII
    /// bookkeeping (<paramref name="item"/>'s already-known kind, <c>claimedGeneration</c>, and an
    /// attempt counter). It NEVER calls <see cref="IClipboardEvaluationLifecycle.CompleteEvaluation"/>/
    /// <see cref="IClipboardEvaluationLifecycle.AbandonEvaluation"/> itself -- LIFECYCLE_OWNERSHIP
    /// remains exclusively <see cref="RunPrivacyPipelineAsync"/>'s own (see that method's own
    /// DIRECT_PIPELINE_FACTS doc, unchanged): each call already discharges exactly one transition
    /// before returning its own non-PII <see cref="ClipboardAttemptOutcome"/>, so a subsequent retry
    /// iteration only ever needs to decide whether to call it again, never to transition anything
    /// itself. This is what makes a double-Complete/double-Abandon structurally unreachable from
    /// this loop.
    ///
    /// SHUTDOWN_LINEARIZATION (Gate 2C, no new field/lock/CancellationToken): RETRY_ATTEMPT_START_LINEARIZATION_POINT
    /// is the <c>lock (_gate)</c> critical section below that reads <see cref="_phase"/> immediately
    /// before admitting the next attempt; SHUTDOWN_TRANSITION_LINEARIZATION_POINT is the existing
    /// critical section in <see cref="ClaimCleanupTransition"/>/<see cref="Start"/>'s own failure
    /// path that moves <see cref="_phase"/> off <see cref="CoordinatorPhase.Running"/> -- BOTH on the
    /// SAME <see cref="_gate"/>, and the shutdown one always runs strictly before
    /// <see cref="PerformCleanup"/> (and therefore before either native monitor's own <c>Stop</c> and
    /// before <c>_mailbox.Writer.Complete()</c>). The two are therefore totally ordered: if admission
    /// observes <see cref="CoordinatorPhase.Running"/>, shutdown has not yet begun and this attempt is
    /// -- by this contract's own definition -- already in-flight, retaining the same existing
    /// frozen in-flight behavior any other already-dequeued attempt already has (nothing here
    /// interrupts it); if admission observes anything else, shutdown has already begun and NO further
    /// target capture/read/write is admitted. No <c>await</c> or other blocking operation occurs
    /// between a successful admission and the synchronous freshness checks immediately below it --
    /// admission and this attempt's own intake are one linearized step relative to shutdown. The lock
    /// is never held across the retry delay's own <c>await</c>.
    ///
    /// FRESHNESS_BEFORE_EVERY_RETRY (Gate 2A, unchanged by this STEP): after admission, in this exact
    /// order -- (1) the pinned generation must still equal <see cref="IClipboardGenerationSnapshot.CurrentGeneration"/>
    /// (a mismatch means a newer generation already exists and its own real trigger already owns a
    /// fresh, correct attempt -- this retry stops, no special-casing needed beyond the plain compare);
    /// (2) a FRESH <see cref="IForegroundTargetCapture.Capture"/> (never attempt N's own stale
    /// snapshot -- TARGET_TOKEN_FLOW's "captured exactly once per attempt" applies per-attempt, and a
    /// retry is a genuinely new attempt); (3) a fresh <see cref="TargetGate.IsSupportedTarget"/>
    /// check against that new capture; (4) a fresh <see cref="IClipboardEvaluationLifecycle.TryBeginEvaluation"/>
    /// re-claim of the SAME pinned generation (safe and race-free because
    /// <see cref="IClipboardEvaluationLifecycle.AbandonEvaluation"/> already returned this generation's
    /// evaluation state to claimable, and the frozen claim contract itself absorbs "stale generation
    /// OR already claimed" identically -- this loop never needs to distinguish which). Any one of
    /// these failing stops the retry immediately, with no clipboard I/O of any kind for this attempt.
    /// </summary>
    private async Task ProcessWorkItemAsync(ClipboardCoordinatorWorkItem item)
    {
        var expectedTarget = _targetCapture.Capture();
        bool authorized = TargetGate.IsSupportedTarget(expectedTarget);

        if (item.Kind == ClipboardCoordinatorTriggerKind.ClipboardChanged)
        {
            Diagnose(ClipboardDiagnosticEvent.TargetCaptured(item.ClipboardGeneration, expectedTarget, authorized));
            if (!authorized)
            {
                Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(item.ClipboardGeneration, ClipboardDiagnosticTerminalReason.UnauthorizedTarget));
                return;
            }
        }
        else if (!authorized)
        {
            return;
        }

        long claimedGeneration = item.Kind == ClipboardCoordinatorTriggerKind.ClipboardChanged
            ? item.ClipboardGeneration
            : _notificationLifecycle.CurrentGeneration;

        if (!_notificationLifecycle.TryBeginEvaluation(claimedGeneration))
            return;

        var outcome = await RunPrivacyPipelineAsync(expectedTarget, claimedGeneration).ConfigureAwait(false);

        for (int attempt = 2; outcome == ClipboardAttemptOutcome.AutonomousRetryEligible && attempt <= MaxAttemptsContractDefault; attempt++)
        {
            await _retryDelay.DelayAsync(RetryDelayIntervalContractDefault).ConfigureAwait(false);

            // RETRY_ATTEMPT_START_LINEARIZATION_POINT -- see this method's own SHUTDOWN_LINEARIZATION
            // doc above. Reading _phase is the ENTIRE critical section; every freshness check below
            // runs synchronously, with no await in between, before this attempt's own native work.
            lock (_gate)
            {
                if (_phase != CoordinatorPhase.Running)
                    return;
            }

            if (_notificationLifecycle.CurrentGeneration != claimedGeneration)
                return;

            var freshTarget = _targetCapture.Capture();
            if (!TargetGate.IsSupportedTarget(freshTarget))
                return;

            if (!_notificationLifecycle.TryBeginEvaluation(claimedGeneration))
                return;

            outcome = await RunPrivacyPipelineAsync(freshTarget, claimedGeneration).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// SHARED_TRIGGER_AGNOSTIC_PIPELINE (Phase 0.2D, STEP61): the exact v0.1 guarded-read -&gt;
    /// processor -&gt; WritePlan/DecisionPlan -&gt; guarded-write/verification -&gt; decision-
    /// publishing path this type has always run (Phase 3B STEP4/STEP14/STEP19/STEP21, Phase 3C
    /// STEP32) -- now reachable from either trigger kind via <see cref="ProcessWorkItemAsync"/>'s
    /// own intake. Contains NO branch of any kind on <see cref="ClipboardCoordinatorTriggerKind"/>,
    /// no re-derivation of <paramref name="claimedGeneration"/>, and no re-capture of
    /// <paramref name="target"/> -- both are already-established facts by the time this method is
    /// ever called (only ever from a successful <see cref="IClipboardEvaluationLifecycle.TryBeginEvaluation"/>
    /// claim).
    ///
    /// DIRECT_PIPELINE_FACTS (frozen, Phase 0.2D instruction's own classification table): every
    /// terminal branch below reports EXACTLY ONE of <see cref="IClipboardEvaluationLifecycle.CompleteEvaluation"/>
    /// (a Terminal fact) or <see cref="IClipboardEvaluationLifecycle.AbandonEvaluation"/> (a
    /// Retryable fact) before returning --
    /// a non-Success guarded-read outcome defers to <see cref="ClipboardEvaluationOutcomeClassifier.ClassifyRead"/>;
    /// <see cref="ClipboardDiagnosticTerminalReason.WriteSkippedUnreliableSequence"/> is always
    /// Retryable/Abandon; a non-verified guarded write defers to
    /// <see cref="ClipboardEvaluationOutcomeClassifier.ClassifyWrite"/>; a verified protected write
    /// is always Terminal/Complete; a decision-session publish attempt (successful OR rejected
    /// because the generation has already gone stale -- <see cref="IClipboardEvaluationLifecycle.CompleteEvaluation"/>'s
    /// own generation guard silently absorbs a late call for an already-superseded generation, so
    /// this is called unconditionally either way) is always Terminal/Complete; and
    /// <see cref="ClipboardDiagnosticTerminalReason.NoActionRequired"/> (neither plan -- no PII, or
    /// every candidate resolved to base-policy Bypass) is always Terminal/Complete. An unexpected
    /// exception from ANY pipeline component (processor/write-transport/decision-session-publisher/
    /// composer-verification-handoff) is caught HERE -- not by <see cref="RunWorkerAsync"/>'s own
    /// outer backstop -- specifically so <see cref="IClipboardEvaluationLifecycle.AbandonEvaluation"/>
    /// can be reported against the real <paramref name="claimedGeneration"/> this method already
    /// has in hand (WORKER_SURVIVAL: the exception is fully absorbed here, never rethrown -- this
    /// method's own caller sees a normal return either way, exactly preserving the existing
    /// PROCESSING_EXCEPTION diagnostic behavior for a clipboard-triggered attempt, now also correctly
    /// reported to the evaluation lifecycle, which the pre-STEP61 coordinator had no such lifecycle
    /// to report to).
    ///
    /// BUG-002 ATTEMPT_OUTCOME_RETURN (Gate 2C): now returns a <see cref="ClipboardAttemptOutcome"/>
    /// -- the ONLY change this STEP makes to this method's own shape -- so
    /// <see cref="ProcessWorkItemAsync"/>'s retry controller can decide whether to attempt again,
    /// without this method's own lifecycle-transition/diagnostic behavior changing in any way: every
    /// <see cref="IClipboardEvaluationLifecycle.CompleteEvaluation"/>/<see cref="IClipboardEvaluationLifecycle.AbandonEvaluation"/>
    /// call below is unchanged, still called from exactly the same branch, exactly once. The returned
    /// value is derived ADDITIONALLY, via <see cref="ClipboardAutonomousRetryClassifier"/> (never by
    /// modifying the FROZEN <see cref="ClipboardEvaluationOutcomeClassifier"/> calls themselves), and
    /// is <see cref="ClipboardAttemptOutcome.Done"/> on every path except a
    /// <see cref="ClipboardAutonomousRetryClassifier"/>-eligible non-Success read/write outcome. No
    /// RAW-bearing value (<c>snapshot</c>, <c>outcome.WritePlan.ReplacementText</c>) is ever part of
    /// the returned value or reachable from it.
    /// </summary>
    private async Task<ClipboardAttemptOutcome> RunPrivacyPipelineAsync(ForegroundTargetSnapshot target, long claimedGeneration)
    {
        try
        {
            var result = await _transport.ReadTextSnapshotAsync(target).ConfigureAwait(false);
            Diagnose(ClipboardDiagnosticEvent.GuardedReadCompleted(
                claimedGeneration, result.Outcome, result.Snapshot?.SequenceNumber, result.Snapshot?.HasReliableSequence));

            if (result.Outcome != ClipboardReadOutcome.Success)
            {
                Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(claimedGeneration, ClipboardDiagnosticTerminalReason.ReadRejected));
                ReportOutcome(claimedGeneration, ClipboardEvaluationOutcomeClassifier.ClassifyRead(result.Outcome));
                return ClipboardAutonomousRetryClassifier.IsAutonomousRetryEligible(result.Outcome)
                    ? ClipboardAttemptOutcome.AutonomousRetryEligible
                    : ClipboardAttemptOutcome.Done;
            }

            // SUCCESS_HANDOFF_BOUNDARY: exactly one delegated call into the real Detection ->
            // Trust/Exception -> Base Policy -> Alias Assignment -> (when eligible) Alias Replacement
            // chain via the narrow IClipboardPrivacyProcessor seam -- the SAME target snapshot that
            // already passed TargetGate in ProcessWorkItemAsync above (no second App-side capture;
            // see that method's own doc). result.Snapshot itself is never logged or otherwise
            // touched beyond being handed, once, to the processor (and, when a DecisionPlan
            // results, once more to the decision-session publisher -- see below).
            var snapshot = result.Snapshot!.Value;
            var outcome = _processor.Process(target, snapshot);
            Diagnose(ClipboardDiagnosticEvent.PolicyEvaluated(
                claimedGeneration,
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
                // APP_UNRELIABLE_SEQUENCE_WRITE_POLICY: a plan may exist locally even when the
                // read's own sequence was never reliable -- the write is still never attempted in
                // that case.
                if (!snapshot.HasReliableSequence)
                {
                    Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(
                        claimedGeneration, ClipboardDiagnosticTerminalReason.WriteSkippedUnreliableSequence));
                    _notificationLifecycle.AbandonEvaluation(claimedGeneration);
                    // BUG-002: an unreliable sequence is outside the Gate 2A autonomous-retry
                    // allowlist (it is not a ClipboardReadOutcome/ClipboardWriteOutcome value at all,
                    // and retrying gives no reason to expect a reliable sequence next time) -- stays
                    // Done, fail-closed, exactly like every outcome this classifier does not know.
                    return ClipboardAttemptOutcome.Done;
                }

                var writeResult = await _writeTransport.WriteTextIfSequenceMatchesAsync(
                    target, snapshot.SequenceNumber, outcome.WritePlan.ReplacementText).ConfigureAwait(false);

                // APP_MUTATED_UNVERIFIED_WRITE_POLICY / APP_WRITE_SUCCESS_NOT_VERIFIED:
                // classification only -- no retry, no rollback, no ProtectionState transition, no
                // logging of any kind. Every WRITE_RETRY_POLICY case (Busy/SequenceChanged/
                // TargetChanged/TargetUnavailable/NativeFailure/VerificationUnavailable/Superseded/
                // ReadBackMismatch/...) ends this attempt here exactly the same way Success does:
                // this method simply returns, now also reporting the classified disposition.
                bool verified = ClipboardWriteResultClassifier.IsRewriteVerified(writeResult);
                Diagnose(ClipboardDiagnosticEvent.GuardedWriteCompleted(
                    claimedGeneration, writeResult.Outcome, writeResult.ClipboardMutated, verified));

                // COMPOSER_VERIFICATION_HANDOFF (Phase 3C STEP31/31.1/32, implemented here): ONLY on
                // a classified-verified rewrite, exactly one Publish call using THIS attempt's own
                // (target, replacementText, claimedGeneration) -- never on write failure,
                // mutated-unverified, or any other outcome. The returned bool is deliberately never
                // used to alter control flow (as of Phase 3C STEP41 it is additionally forwarded to
                // Diagnose for manual-QA correlation only), matching every other discarded-bool
                // handoff in this method.
                if (verified)
                {
                    bool published = _verificationHandoff.Publish(target, outcome.WritePlan.ReplacementText, claimedGeneration);
                    Diagnose(ClipboardDiagnosticEvent.ComposerVerificationHandoffPublished(claimedGeneration, published));
                    Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(claimedGeneration, ClipboardDiagnosticTerminalReason.Success));
                    _notificationLifecycle.CompleteEvaluation(claimedGeneration);
                    return ClipboardAttemptOutcome.Done;
                }

                Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(claimedGeneration, ClipboardDiagnosticTerminalReason.WriteRejected));
                ReportOutcome(claimedGeneration, ClipboardEvaluationOutcomeClassifier.ClassifyWrite(writeResult.Outcome));
                return ClipboardAutonomousRetryClassifier.IsAutonomousRetryEligible(writeResult.Outcome)
                    ? ClipboardAttemptOutcome.AutonomousRetryEligible
                    : ClipboardAttemptOutcome.Done;
            }

            // DECISION_SESSION_PUBLICATION (Phase 3B STEP20 audit, implemented here): a DecisionPlan
            // exists -> at least one candidate still needs a decision. Neither AliasReplacer nor
            // ClipboardWritePlan nor a clipboard write is ever involved on this path (STEP13/STEP19
            // contracts unchanged) -- only a session-publication attempt.
            if (outcome.DecisionPlan is not null)
            {
                bool published = _decisionSessionPublisher.TryPublish(claimedGeneration, snapshot.Text, outcome.DecisionPlan);
                Diagnose(ClipboardDiagnosticEvent.DecisionSessionPublishAttempted(
                    claimedGeneration, outcome.DecisionPlan.Items.Count, published));
                Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(claimedGeneration, ClipboardDiagnosticTerminalReason.DecisionPending));
                // NeedsDecision published successfully -> Terminal/Complete. Rejected because the
                // generation had already gone stale -> the generation guard inside
                // CompleteEvaluation itself silently absorbs a late call for a superseded
                // generation (see that method's own doc) -- called unconditionally either way.
                _notificationLifecycle.CompleteEvaluation(claimedGeneration);
                return ClipboardAttemptOutcome.Done;
            }

            // ALL_BYPASS / NO_PII (Phase 3C STEP41 diagnostic terminal): neither plan was produced --
            // nothing to protect and nothing to decide.
            Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(claimedGeneration, ClipboardDiagnosticTerminalReason.NoActionRequired));
            _notificationLifecycle.CompleteEvaluation(claimedGeneration);
            return ClipboardAttemptOutcome.Done;
        }
        catch (Exception ex)
        {
            // WORKER_SURVIVAL / EXCEPTION_BOUNDARY -- see this method's own class doc. Absorbed
            // here, never rethrown; ProcessingException -> Retryable/Abandon (Phase 0.2D
            // instruction's own DIRECT_PIPELINE_FACTS table). BUG-002 Gate 2A Issue 3 (final): an
            // unexpected, unclassified exception is NOT a known-transient clipboard outcome -- no
            // evidence establishes it is safe to retry, so this stays Done (exactly today's
            // single-attempt behavior), never broadening into generic exception recovery.
            _notificationLifecycle.AbandonEvaluation(claimedGeneration);
            Diagnose(ClipboardDiagnosticEvent.AttemptTerminal(
                claimedGeneration, ClipboardDiagnosticTerminalReason.ProcessingException, ex.GetType().Name));
            return ClipboardAttemptOutcome.Done;
        }
    }

    private void ReportOutcome(long claimedGeneration, ClipboardEvaluationDisposition disposition)
    {
        if (disposition == ClipboardEvaluationDisposition.Terminal)
            _notificationLifecycle.CompleteEvaluation(claimedGeneration);
        else
            _notificationLifecycle.AbandonEvaluation(claimedGeneration);
    }
}
