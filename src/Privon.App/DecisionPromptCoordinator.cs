using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- the plain-CLR controller that turns a successful
/// <see cref="ClipboardDecisionSessionPublisher.ScopePublished"/> notification into the 0.1
/// minimum NeedsDecision UI (Phase 3C STEP39/STEP39.1 audits, frozen). Deliberately NOT a WPF type
/// itself -- depends only on <see cref="IDispatcherScheduler"/>/<see cref="IDecisionPromptSurface"/>/
/// <see cref="IClipboardDecisionScopeLifecycle"/>/<see cref="IClipboardDecisionResolver"/>, so its
/// entire orchestration (publication receipt, dispatcher hand-off, staleness recheck, single
/// current prompt, Protect-All sequencing) is deterministically testable with hand-written fakes,
/// never a real WPF <c>Window</c>/<c>Dispatcher</c>/real <see cref="ClipboardDecisionActionResolver"/>
/// dependency graph.
///
/// PUBLICATION_THREAD (Phase 3C STEP39.1 audit, frozen): <see cref="OnScopePublished"/> runs on
/// whatever thread raised <see cref="ClipboardDecisionSessionPublisher.ScopePublished"/> -- the
/// coordinator's own ThreadPool worker in production, never the WPF Dispatcher thread and never
/// the Windows clipboard owner thread. No UI surface is ever created or shown from that thread --
/// it does nothing but a single, non-blocking <see cref="IDispatcherScheduler.Post"/> call.
///
/// ACTIVE_SCOPE_RECHECK (Phase 3C STEP39.1 audit, frozen): notification delivery is never itself
/// evidence that <paramref name="scope"/> (any use of that word in this doc) is still active --
/// by the time <see cref="HandleScopeOnDispatcher"/> actually runs (after however long the
/// dispatcher took), the scope may already have been superseded. <see cref="IClipboardDecisionScopeLifecycle.IsActive"/>
/// is rechecked there, on the SAME concrete lifecycle instance <see cref="PrivonAppComposition.BuildGraph"/>
/// creates and <see cref="PrivonAppUiBridge"/> shares with both this type and
/// <see cref="ClipboardDecisionSessionPublisher"/>/<see cref="ClipboardDecisionActionResolver"/>
/// -- if false, the notification is dropped silently: no dialog is shown, nothing is resolved.
///
/// ONE_CURRENT_PROMPT (Phase 3C STEP40 instruction, frozen): at most one <see cref="IDecisionPromptSurface"/>
/// is ever visible at a time. A newer scope publication that survives the staleness recheck above
/// closes whatever surface is currently tracked (belonging, by construction, to an older,
/// necessarily-already-superseded scope) before opening a new one -- no queue of stale dialogs is
/// ever accumulated, and no polling timer of any kind is used anywhere in this type.
///
/// PROTECT_ALL_SEMANTICS (Phase 3C STEP39.1/STEP40, extended by the Phase 0.2G SILENT_FAILURE_FIX):
/// the ONLY user action this type ever drives is <see cref="ClipboardDecisionIntent.Protect"/>,
/// applied sequentially to every entry in <see cref="ClipboardDecisionScope.Items"/> via
/// <see cref="IClipboardDecisionResolver.ResolveAsync"/> -- never <c>BypassOnce</c>. An
/// <c>Applied</c> result with <c>ScopeCommitted == false</c> continues to the next item; an
/// <c>Applied</c> result with <c>ScopeCommitted == true</c> closes the surface (never displaying
/// "Verified"/"보호 완료"/"검증 완료" -- see <see cref="ClipboardDecisionActionResolver"/>'s own
/// VERIFIED_BOUNDARY doc); any other outcome (<c>Stale</c>/<c>WriteFailed</c>/<c>MutatedUnverified</c>/
/// <c>Failed</c>/the structurally-unreachable <c>AwaitingSecondConfirmation</c>, since this type
/// never emits <c>BypassOnce</c>) shows a neutral, non-content-bearing failure message and stops --
/// never a success claim, and (as of the Phase 0.2G fix) never a silent close either. <c>Stale</c>
/// used to stop the sequence with a bare <c>surface.Close()</c> and NO feedback at all --
/// indistinguishable, from the user's own point of view, from a genuine committed success. A real
/// live cross-app manual QA observation against an actual ChatGPT Desktop window and an actual
/// running <c>Privon.App.exe</c> (never simulated) confirmed this exact, misleading failure mode
/// in production: the popup would "just disappear" after the user clicked "모두 보호," with the
/// raw PII left completely unprotected on the clipboard -- traced to <c>DecisionPromptWindow</c>
/// itself being able to steal real OS foreground activation away from the authorized ChatGPT
/// target on the user's own click, causing <see cref="ClipboardDecisionActionResolver"/>'s fresh
/// target check to correctly (and safely) reject it as <c>Stale</c>. See <c>DecisionPromptWindow</c>'s
/// own NON_ACTIVATING_PROMPT doc for that root-cause fix -- this type's own fix is independent
/// defense-in-depth: <c>Stale</c> remains a legitimate, expected outcome for entirely different
/// reasons too (e.g. the scope being superseded by a newer clipboard/foreground event while the
/// user was still deciding), and in EVERY case where Protect All does not end in a genuinely
/// committed <c>Applied</c> result, the user must be told explicitly -- never left to assume
/// silence means success.
///
/// USER_CLOSING_THE_PROMPT (Phase 3C STEP40 instruction, frozen): a surface's own
/// <see cref="IDecisionPromptSurface.Closed"/> event -- however it fired, whether this type closed
/// it programmatically or the user closed it themselves -- never triggers any
/// <see cref="IClipboardDecisionResolver.ResolveAsync"/> call of any kind. Closing is never
/// reinterpreted as <c>BypassOnce</c>, never marks the scope committed, and never shows a
/// protection-success claim -- the underlying engine (<see cref="ClipboardDecisionScope"/>/
/// <see cref="ClipboardDecisionActionResolver"/>) remains the sole authority on what happened.
/// </summary>
internal sealed class DecisionPromptCoordinator : IDisposable
{
    internal const string NeutralFailureMessage = "Could not process this. Please copy again.";

    private readonly ClipboardDecisionSessionPublisher _sessionPublisher;
    private readonly IClipboardDecisionScopeLifecycle _lifecycle;
    private readonly IClipboardDecisionResolver _resolver;
    private readonly IDispatcherScheduler _scheduler;
    private readonly Func<IDecisionPromptSurface> _surfaceFactory;
    private readonly IForegroundTargetCapture? _targetCapture;
    private readonly object _gate = new();

    private bool _started;
    private bool _disposed;
    private IDecisionPromptSurface? _currentSurface;
    private ClipboardDecisionScope? _currentScope;     // the scope _currentSurface was shown for
    private bool _attemptRunning;                      // a Protect All attempt for _currentSurface is in flight
    private IClipboardDecisionScopeEndNotifier? _endNotifier;

    public DecisionPromptCoordinator(
        ClipboardDecisionSessionPublisher sessionPublisher,
        IClipboardDecisionScopeLifecycle lifecycle,
        IClipboardDecisionResolver resolver,
        IDispatcherScheduler scheduler,
        Func<IDecisionPromptSurface> surfaceFactory,
        IForegroundTargetCapture? targetCapture = null)
    {
        ArgumentNullException.ThrowIfNull(sessionPublisher);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(surfaceFactory);
        _sessionPublisher = sessionPublisher;
        _lifecycle = lifecycle;
        _resolver = resolver;
        _scheduler = scheduler;
        _surfaceFactory = surfaceFactory;
        _targetCapture = targetCapture;
    }

    /// <summary>Single-use, matching this codebase's established <c>Start</c> precedent -- a
    /// second call always throws.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                throw new InvalidOperationException("Start has already been called on this DecisionPromptCoordinator instance -- it is single-use.");
            _started = true;
        }

        _sessionPublisher.ScopePublished += OnScopePublished;

        // OPTIONAL: a lifecycle that cannot say when a decision ends (a hand-written fake) simply leaves the prompt to the existing replace / dispose / user-close paths
        if (_lifecycle is IClipboardDecisionScopeEndNotifier notifier)
        {
            _endNotifier = notifier;
            notifier.ActiveScopeEnded += OnActiveScopeEnded;
        }
    }

    // SCOPE_ENDED (PRIVON 0.4.0): runs on the thread that dropped the scope (the clipboard owner thread / the session-lock thread) -- it only hands off.
    private void OnActiveScopeEnded(object? sender, ClipboardDecisionScope ended)
    {
        _scheduler.Post(() => CloseThePromptOfAnEndedDecision(ended));
    }

    // A decision prompt says "personal information was detected, press to protect": true only while the decision is alive. When the scope it was shown for
    // has ended it is closed -- unless a Protect All attempt for it is still running, because that attempt's failure message is the answer the user is waiting
    // for and a prompt that vanishes under a press looks like a success. Closing resolves nothing, writes nothing and claims nothing; a prompt that belongs to a
    // different (newer) scope is never touched.
    private void CloseThePromptOfAnEndedDecision(ClipboardDecisionScope ended)
    {
        IDecisionPromptSurface? surface;
        lock (_gate)
        {
            if (_disposed || _currentSurface is null) return;
            if (!ReferenceEquals(_currentScope, ended) || _attemptRunning) return;
            surface = _currentSurface;
        }

        if (_lifecycle.IsActive(ended)) return;
        surface.Close();
    }

    // PUBLICATION_THREAD: runs on whatever thread raised ScopePublished (the coordinator's own
    // ThreadPool worker, in production) -- never creates or shows any UI surface itself, only
    // schedules the dispatcher hand-off.
    private void OnScopePublished(object? sender, ClipboardDecisionScope scope)
    {
        var original = CaptureSupportedTarget();
        _scheduler.Post(() => HandleScopeOnDispatcher(scope, original));
    }

    // The foreground target at the moment the decision is shown, kept ONLY if it is a supported Windows target (nothing else is ever remembered).
    // No capture configured, a failing capture or an unsupported target all mean "none": the prompt then never carries the foreground carve-out.
    private ForegroundTargetSnapshot? CaptureSupportedTarget()
    {
        if (_targetCapture is null) return null;
        try
        {
            var snapshot = _targetCapture.Capture();
            return TargetGate.IsSupportedTarget(snapshot) ? snapshot : null;
        }
        catch
        {
            return null;
        }
    }

    // WPF_DISPATCHER_BOUNDARY: runs on the dispatcher thread in production (synchronously, in a
    // test fake scheduler). ACTIVE_SCOPE_RECHECK + ONE_CURRENT_PROMPT are both implemented here.
    private void HandleScopeOnDispatcher(ClipboardDecisionScope scope, ForegroundTargetSnapshot? originalTarget)
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        if (!_lifecycle.IsActive(scope)) return;

        IDecisionPromptSurface? previous;
        lock (_gate)
        {
            previous = _currentSurface;
            _currentSurface = null;
            _currentScope = null;
            _attemptRunning = false;
        }
        previous?.Close();

        var surface = _surfaceFactory();
        var protectAllInFlight = false;

        surface.Closed += (_, _) => OnSurfaceClosed(surface);
        surface.ProtectAllRequested += (_, _) =>
        {
            // Double-action prevention (Phase 3C STEP40 instruction): guarded here, on this
            // per-surface closure-captured flag, rather than relying solely on a real WPF button's
            // own disabled-state click suppression (which a fake surface would not otherwise
            // exercise).
            if (protectAllInFlight) return;
            protectAllInFlight = true;
            lock (_gate)
            {
                if (ReferenceEquals(_currentSurface, surface)) _attemptRunning = true;
            }

            // Read NOW, synchronously, on the UI thread, and used for THIS press only: how the prompt says this very press came about. The context is
            // built here, handed to one resolve and kept in no field.
            var promptContext = originalTarget is { } original ? BuildPromptContext(original, SafeCaptureInvocation(surface)) : null;

            surface.DisableProtectAction();
            _ = RunProtectAllAsync(scope, surface, promptContext);
        };

        lock (_gate)
        {
            if (_disposed)
            {
                surface.Close();
                return;
            }
            _currentSurface = surface;
            _currentScope = scope;
            _attemptRunning = false;
        }

        surface.Show();
    }

    private void OnSurfaceClosed(IDecisionPromptSurface surface)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_currentSurface, surface))
            {
                _currentSurface = null;
                _currentScope = null;
                _attemptRunning = false;
            }
        }
    }

    // PROTECT_ALL_SEMANTICS: exactly one ResolveAsync(Protect) call per scope.Items entry, in that
    // exact order, stopping immediately on Stale/any non-committing failure.
    //
    // WPF_THREAD_AFFINITY_CORRECTION (Phase 3C STEP40.2, fixing a real defect an independent
    // pre-release audit found): `await _resolver.ResolveAsync(...).ConfigureAwait(false)` may --
    // and in production, via ClipboardDecisionActionResolver's own real async I/O, routinely does
    // -- resume its continuation on an arbitrary ThreadPool thread, never the WPF Dispatcher
    // thread the real IDecisionPromptSurface (a DispatcherObject-derived DecisionPromptWindow) is
    // affine to. Every `surface.*` call below is therefore made ONLY through
    // `_scheduler.Post(...)` -- never directly -- so it is unconditionally, structurally
    // re-marshaled onto the correct thread regardless of which thread this method's continuation
    // actually resumed on. `ConfigureAwait(false)` is kept deliberately (not removed): relying on
    // the ambient SynchronizationContext to carry the continuation back to the Dispatcher would be
    // an IMPLICIT invariant this codebase has no way to prove holds in every production entry path
    // -- the explicit re-marshal through the already-established IDispatcherScheduler boundary is
    // what makes this structural and testable (see ProtectAllDispatcherAffinityTests.cs, which
    // proves this with a REAL pumped WPF Dispatcher and a real DispatcherObject-derived surface).
    //
    // RESOLVER_EXCEPTION_CONTAINMENT: a SINGLE outer try/catch now wraps the entire attempt (not
    // just the resolver call) -- this method's Task is deliberately fire-and-forget
    // (`_ = RunProtectAllAsync(...)` in HandleScopeOnDispatcher), so ANY unexpected exception here
    // (from the resolver, or from anything else in this method) must never become an unobserved
    // Task exception. Every path this catch reaches produces, at most, the same neutral
    // NeutralFailureMessage -- never re-interpreted as success, never any content-bearing detail.
    private static DecisionPromptInvocation SafeCaptureInvocation(IDecisionPromptSurface surface)
    {
        try { return surface.CaptureInvocation(); } catch { return default; }
    }

    // POLICY B (PRIVON 0.4.0, Controller-approved). A press made while PRIVON's own prompt is the foreground carries a permission only when:
    //   (1) the prompt itself is the foreground window now, AND the foreground recorded when the decision was shown was a supported target (the caller);
    //   (2) either the application active immediately before the prompt's application became active IS that original target (same process) -- the original
    //       target stands in -- or the click was raised inside a fresh Enter / Space addressed to the button itself -- a MANUAL decision that restores
    //       and claims no target (a synthesized key is indistinguishable, and nothing here says otherwise).
    // Anything else -- a UIA Invoke while another application was the foreground, an unknown or unresolvable predecessor, the Alt+Tab switcher without a
    // key -- gets no context and is refused exactly as before. The asynchronous foreground history is deliberately not consulted: it is neither ordered
    // nor complete enough to authorize anything.
    private static DecisionPromptForegroundContext? BuildPromptContext(ForegroundTargetSnapshot original, DecisionPromptInvocation invocation)
    {
        if (!invocation.IsForeground) return null;

        if (invocation.PreviousActiveProcessId != 0 && invocation.PreviousActiveProcessId == original.ProcessId)
            return new DecisionPromptForegroundContext(original, DecisionPromptAuthorityBasis.PreviousAppWasOriginalTarget);

        if (invocation.ByKeyboard)
            return new DecisionPromptForegroundContext(original, DecisionPromptAuthorityBasis.ManualKeyboardDecision);

        return null;
    }

    private async Task RunProtectAllAsync(ClipboardDecisionScope scope, IDecisionPromptSurface surface, DecisionPromptForegroundContext? promptContext)
    {
        try
        {
            foreach (var item in scope.Items)
            {
                var result = await _resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect, promptContext).ConfigureAwait(false);

                if (result.Outcome == ClipboardDecisionActionOutcome.Applied && result.ScopeCommitted)
                {
                    _scheduler.Post(surface.Close);
                    return;
                }

                if (result.Outcome == ClipboardDecisionActionOutcome.Applied)
                {
                    continue; // ScopeCommitted == false -- a sibling item is still unresolved.
                }

                // Stale / WriteFailed / MutatedUnverified / Failed / AwaitingSecondConfirmation
                // (structurally unreachable here since this type never emits BypassOnce) -- never
                // a success claim, and (Phase 0.2G SILENT_FAILURE_FIX) never a silent close either.
                // Stale used to close the surface with no feedback at all -- indistinguishable,
                // from the user's own point of view, from a genuine committed success. A real live
                // cross-app manual QA observation (LEVEL3_PROTECT_SELF_STALE_ROOT_CAUSE) confirmed
                // this exact, misleading failure mode: the popup "just disappeared" after the user
                // clicked "모두 보호," with the raw PII left completely unprotected on the
                // clipboard. Stale now falls through to the SAME neutral-failure-message path
                // every other non-committing outcome already used -- the user must always be told
                // explicitly when Protect All did not actually protect anything.
                _scheduler.Post(() => surface.ShowNeutralFailure(NeutralFailureMessage));
                return;
            }
        }
        catch
        {
            _scheduler.Post(() => surface.ShowNeutralFailure(NeutralFailureMessage));
        }
        finally
        {
            // the attempt is over (its Close / failure message is already queued ahead of anything a later scope end can queue)
            lock (_gate)
            {
                if (ReferenceEquals(_currentSurface, surface)) _attemptRunning = false;
            }
        }
    }

    /// <summary>Idempotent. Unsubscribes from <see cref="ClipboardDecisionSessionPublisher.ScopePublished"/>
    /// first (SUBSCRIPTION_LIFETIME: prevents any new dispatch from being scheduled), then closes
    /// whatever surface is currently tracked, if any.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _sessionPublisher.ScopePublished -= OnScopePublished;
        if (_endNotifier is not null) _endNotifier.ActiveScopeEnded -= OnActiveScopeEnded;

        IDecisionPromptSurface? surface;
        lock (_gate)
        {
            surface = _currentSurface;
            _currentSurface = null;
        }
        surface?.Close();
    }
}
