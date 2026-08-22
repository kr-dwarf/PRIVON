namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- the minimal 0.1 UI/lifetime surface, wired against the EXACT SAME shared
/// <see cref="ClipboardDecisionSessionPublisher"/>/<see cref="ClipboardDecisionScopeLifecycle"/>/
/// <see cref="ClipboardDecisionActionResolver"/> instances a <see cref="PrivonAppComposition"/>
/// already owns and started -- never a second lifecycle, never a second resolver, never a second
/// publisher (Phase 3C STEP40 instruction's COMPOSITION_ROOT_OWNERSHIP). Deliberately a SEPARATE
/// type from <see cref="PrivonAppComposition"/> (not a field on it) so that type's own "plain CLR,
/// no WPF <c>Application</c>/<c>Window</c>/<c>Dispatcher</c> dependency of any kind" invariant
/// (see its own class doc, and <c>PrivonAppComposition_IsInternalPlainClrType_NotAWpfType</c>/
/// <c>CompositionRootSource_HasNoUiOrHotkeyMembers</c>'s existing structural tests) stays
/// completely intact -- this type, not <see cref="PrivonAppComposition"/>, is what actually
/// touches <see cref="System.Windows.Forms.NotifyIcon"/>/WPF <see cref="System.Windows.Threading.Dispatcher"/>.
///
/// Owns exactly two things: a <see cref="DecisionPromptCoordinator"/> (the NeedsDecision Protect
/// UI) and an <see cref="ITrayIconSurface"/> (running presence + Exit). <see cref="CreateProduction"/>
/// is the ONLY production construction path -- it requires all six dependencies explicitly (no
/// optional-parameter defaults), so a test can never accidentally construct a real
/// <see cref="WinFormsTrayIconSurface"/>/<see cref="WpfDispatcherScheduler"/>/<see cref="DecisionPromptWindow"/>
/// merely by calling the ordinary constructor -- every dependency must be supplied explicitly,
/// production or fake alike.
/// </summary>
internal sealed class PrivonAppUiBridge : IDisposable
{
    private readonly DecisionPromptCoordinator _promptCoordinator;
    private readonly ITrayIconSurface _traySurface;
    private readonly IDispatcherScheduler _scheduler;
    private readonly object _gate = new();

    private bool _started;
    private bool _disposed;

    internal PrivonAppUiBridge(
        ClipboardDecisionSessionPublisher sessionPublisher,
        IClipboardDecisionScopeLifecycle lifecycle,
        IClipboardDecisionResolver resolver,
        IDispatcherScheduler scheduler,
        ITrayIconSurface traySurface,
        Func<IDecisionPromptSurface> promptSurfaceFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionPublisher);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(traySurface);
        ArgumentNullException.ThrowIfNull(promptSurfaceFactory);

        _scheduler = scheduler;
        _traySurface = traySurface;
        _promptCoordinator = new DecisionPromptCoordinator(sessionPublisher, lifecycle, resolver, scheduler, promptSurfaceFactory);
    }

    /// <summary>The only production construction path -- wires the real
    /// <see cref="WpfDispatcherScheduler"/>/<see cref="WinFormsTrayIconSurface"/>/
    /// <see cref="DecisionPromptWindow"/> against <paramref name="composition"/>'s already-started
    /// shared objects. <paramref name="composition"/> must have already completed a successful
    /// <see cref="PrivonAppComposition.Start"/> -- <see cref="PrivonAppComposition.SessionPublisher"/>/
    /// <see cref="PrivonAppComposition.Lifecycle"/>/<see cref="PrivonAppComposition.Resolver"/>
    /// are only non-null once it has.</summary>
    public static PrivonAppUiBridge CreateProduction(PrivonAppComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        var sessionPublisher = composition.SessionPublisher
            ?? throw new InvalidOperationException("PrivonAppComposition.Start() must succeed before creating the UI bridge.");
        var lifecycle = composition.Lifecycle
            ?? throw new InvalidOperationException("PrivonAppComposition.Start() must succeed before creating the UI bridge.");
        var resolver = composition.Resolver
            ?? throw new InvalidOperationException("PrivonAppComposition.Start() must succeed before creating the UI bridge.");

        return new PrivonAppUiBridge(
            sessionPublisher,
            lifecycle,
            resolver,
            new WpfDispatcherScheduler(),
            new WinFormsTrayIconSurface(),
            () => new DecisionPromptWindow());
    }

    /// <summary>Single-use. Subscribes the decision-prompt coordinator, wires the tray's Exit
    /// command, then shows the tray icon.
    ///
    /// STARTUP_ROLLBACK (Phase 3C STEP40.2, fixing a real gap an independent pre-release audit
    /// found): these three stages -- prompt-coordinator start, tray Exit subscription, tray
    /// <see cref="ITrayIconSurface.Show"/> -- are each independently fallible. If any stage
    /// throws, <see cref="RollbackPartialStart"/> tears down whatever already succeeded (mirroring
    /// <see cref="PrivonAppComposition"/>'s own established STARTUP_FAILURE_ROLLBACK precedent
    /// exactly) BEFORE the original exception is rethrown unchanged -- this type owns its own
    /// transactional startup rather than relying on <c>App.xaml.cs</c> to know which of its
    /// internal stages needs undoing (OPTION A: the owning type keeps the clearest deterministic
    /// lifecycle, keeping <c>App.xaml.cs</c> thin).</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                throw new InvalidOperationException("Start has already been called on this PrivonAppUiBridge instance -- it is single-use.");
            _started = true;
        }

        try
        {
            _promptCoordinator.Start();
            _traySurface.ExitRequested += OnExitRequested;
            _traySurface.Show();
        }
        catch
        {
            RollbackPartialStart();
            throw;
        }
    }

    // STARTUP_ROLLBACK: each step independently swallowed (the ORIGINAL startup exception, not a
    // cleanup failure, is what Start() rethrows) and safe to run regardless of which stage above
    // actually succeeded -- DecisionPromptCoordinator.Dispose()/ITrayIconSurface.Dispose() are
    // both already idempotent and safe even if their corresponding stage never ran (e.g. the
    // coordinator was never Start()-ed, or the tray's ExitRequested was never subscribed).
    private void RollbackPartialStart()
    {
        Safe(() => _promptCoordinator.Dispose());
        Safe(() =>
        {
            _traySurface.ExitRequested -= OnExitRequested;
            _traySurface.Dispose();
        });
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch
        {
            // A cleanup failure here must never replace the original startup exception Start() is
            // already unwinding with.
        }
    }

    // EXIT (Phase 3C STEP40 instruction, frozen): the tray's own Exit request is routed through
    // the SAME dispatcher scheduler used for decision-prompt hand-off (uniformly non-blocking,
    // even though a real WinForms tray click already runs on the shared UI thread) and calls
    // System.Windows.Application.Current.Shutdown() -- never Environment.Exit -- so the existing
    // App.OnExit -> PrivonAppComposition.Dispose() path always runs.
    private void OnExitRequested(object? sender, EventArgs e)
    {
        _scheduler.Post(() => System.Windows.Application.Current?.Shutdown());
    }

    /// <summary>SUBSCRIPTION_LIFETIME (Phase 3C STEP40 instruction, frozen): prevents new UI
    /// dispatches first (disposes the decision-prompt coordinator, which unsubscribes from the
    /// publisher before closing any currently-visible prompt), then disposes/removes the tray
    /// surface. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _promptCoordinator.Dispose();

        _traySurface.ExitRequested -= OnExitRequested;
        _traySurface.Dispose();
    }
}
