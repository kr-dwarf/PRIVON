using Privon.Detection;

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
/// Owns exactly three things: a <see cref="DecisionPromptCoordinator"/> (the NeedsDecision Protect
/// UI), an <see cref="ITrayIconSurface"/> (running presence + Exit + Phase 0.2I's auto-start
/// toggle), and (Phase 0.2I) a <see cref="WindowsAutoStartCoordinator"/> -- the auto-start POLICY
/// object the tray's toggle click is bound against. <see cref="CreateProduction"/> is the ONLY
/// production construction path -- it requires every dependency explicitly (no optional-parameter
/// defaults), so a test can never accidentally construct a real
/// <see cref="WinFormsTrayIconSurface"/>/<see cref="WpfDispatcherScheduler"/>/<see cref="DecisionPromptWindow"/>/
/// <see cref="WindowsAutoStartRegistration"/> merely by calling the ordinary constructor -- every
/// dependency must be supplied explicitly, production or fake alike.
///
/// AUTO_START_UI_BINDING (Phase 0.2I): <see cref="Start"/> queries the CURRENT actual registration
/// state via <see cref="WindowsAutoStartCoordinator.IsEnabled"/> (a read-only registry lookup) and
/// reflects it on the tray immediately -- never assumes "OFF" by default without actually checking.
/// The toggle click handler always re-derives "enable or disable" from a FRESH
/// <see cref="WindowsAutoStartCoordinator.IsEnabled"/> read (never from the menu item's own visual
/// state, which this type never reads back), attempts exactly the one corresponding operation, and
/// then reflects the tray's checked state from a SECOND fresh <see cref="WindowsAutoStartCoordinator.IsEnabled"/>
/// read taken AFTER that attempt -- so a failed enable/disable (whatever the underlying reason)
/// always renders whatever the registry actually ends up containing, never a state this type merely
/// hoped for.
///
/// PRIVON v0.2.1 Gate 3C -- this type now ALSO owns the ONE <see cref="SettingsCoordinator"/>,
/// exactly like it already owns the ONE <see cref="DecisionPromptCoordinator"/>: constructed
/// internally (never passed in pre-built), from the SAME shared
/// <see cref="PrivonAppComposition.CategorySettingsService"/>/<see cref="PrivonAppComposition.SettingsUserExceptionService"/>
/// a <see cref="PrivonAppComposition"/> already owns and started -- never a second, independently-
/// opened <c>PrivonLocalStore</c> (COMPOSITION_ROOT_OWNERSHIP, same precedent as the decision-prompt
/// wiring above). The tray's own <see cref="ITrayIconSurface.SettingsRequested"/> is routed through
/// the SAME <see cref="IDispatcherScheduler"/> used for Exit/auto-start-toggle, for the identical
/// uniform-non-blocking reason.
///
/// PRIVON 0.3.1 Gate E5G.P2/P3 -- this type now ALSO owns the ONE
/// <see cref="NativeMessagingHostRegistrationCoordinator"/> (the store-independent Native Messaging
/// registration coordinator), mirroring exactly how it already owns the ONE
/// <see cref="WindowsAutoStartCoordinator"/>: supplied fully-constructed, never built internally
/// (matching the auto-start precedent's own shape, since both wrap a narrow OS-mechanics seam this
/// type itself never touches directly). Gate E5G.1C wires this SAME coordinator instance through,
/// unchanged, to the ONE <see cref="SettingsCoordinator"/> this type also owns (see that type's own
/// CHROME_PROVISIONING doc) -- the explicit Chrome Native Messaging setup/repair actions live
/// entirely behind Settings, never a second independently-constructed coordinator instance.
///
/// PRIVON 0.3.2 Gate 032-C2 -- CONTRACT UPDATE (supersedes the former REGISTRATION_STAYS_INERT
/// "zero interaction of any kind" contract): <see cref="Start"/> now performs exactly ONE contained,
/// READ-ONLY Chrome readiness inspection (via <see cref="SettingsCoordinator.InspectChromeReadinessForStartup"/>
/// -- never a second, independently-constructed coordinator/environment) as its own final step, and
/// reacts with ZERO mutation of any kind: Fresh proactively opens/focuses the existing Settings
/// surface once (friendly Connect Chrome onboarding, still requiring the user's own later explicit
/// click to actually provision); OwnedNeedsRepair shows exactly one reconnect notification via
/// <see cref="ITrayIconSurface.ShowChromeReconnectNotification"/>, whose click ONLY opens/focuses
/// Settings (never Repair); every other state (Ready/ForeignBlocked/OrphanBlocked/Failed) does
/// nothing. This entire onboarding branch is exception-contained
/// (<see cref="RunChromeOnboardingCheck"/>) and can never prevent or roll back normal startup --
/// unlike the tray-show/prompt-coordinator stages above, a failure here is swallowed, never
/// rethrown. Edge is never inspected or mutated by this new path (Chrome-only, matching
/// <see cref="SettingsCoordinator"/>'s own <see cref="ReleaseBrowserSupportPolicy"/> gating).
/// Constructing this type still performs zero mutation, and neither this constructor nor
/// <see cref="Dispose"/> reaches Provision/Repair -- only the new, contained, read-only Inspect call
/// inside <see cref="Start"/> is new.
/// </summary>
internal sealed class PrivonAppUiBridge : IDisposable
{
    private readonly DecisionPromptCoordinator _promptCoordinator;
    private readonly SettingsCoordinator _settingsCoordinator;
    private readonly ITrayIconSurface _traySurface;
    private readonly IDispatcherScheduler _scheduler;

    // Built for every composition. The status itself is read on the UI thread at render time, so a render
    // that runs late still shows the newest status, never the one that triggered it.
    private readonly ClipboardProtectionStatusTracker? _protectionStatus;
    private ProtectionStatusPresentation? _lastPresentation;
    private bool _statusSubscribed;

    /// <summary>
    /// How long a NEW warning must last before it raises a balloon. A rewrite or read that fails and is retried successfully within a fraction of a
    /// second (a clipboard another program briefly holds) is a recorded state and a tooltip, but not an alert: the user is told of failures that
    /// persist, once per episode. The tooltip itself is never delayed.
    /// </summary>
    internal static readonly TimeSpan ProductionWarningNotifyDelay = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _warningNotifyDelay;
    private readonly Func<TimeSpan, Action, IDisposable> _startTimer;
    private IDisposable? _pendingNotifyTimer;
    private bool _notifyPending;

    // Explicit opt-in only (null in a shipped launch): the U.S. readiness the tray shows as a persistent menu line. Read on the UI thread at
    // render time -- never cached -- together with a check that the pipeline in use can be built right now.
    private readonly Func<UsReadinessReport>? _usReadinessSource;
    private readonly Func<DetectionPipeline>? _usReadinessPipelineSource;
    private bool _usReadinessSubscribed;
    private readonly WindowsAutoStartCoordinator _autoStartCoordinator;
    private readonly NativeMessagingHostRegistrationCoordinator _registrationCoordinator;
    private readonly object _gate = new();

    private bool _started;
    private bool _disposed;

    internal PrivonAppUiBridge(
        ClipboardDecisionSessionPublisher sessionPublisher,
        IClipboardDecisionScopeLifecycle lifecycle,
        IClipboardDecisionResolver resolver,
        IDispatcherScheduler scheduler,
        ITrayIconSurface traySurface,
        Func<IDecisionPromptSurface> promptSurfaceFactory,
        WindowsAutoStartCoordinator autoStartCoordinator,
        ProtectionCategorySettingsService categorySettingsService,
        UserExceptionService userExceptionService,
        DetectionPipeline detectionPipeline,
        Func<bool> isMasterKeyUnavailable,
        Func<ISettingsSurface> settingsSurfaceFactory,
        NativeMessagingHostRegistrationCoordinator registrationCoordinator,
        Func<DetectionPipeline>? settingsPipelineSource = null,
        ClipboardProtectionStatusTracker? protectionStatus = null,
        Func<UsReadinessReport>? usReadinessSource = null,
        CountryChoiceService? countryChoices = null,
        TimeSpan? warningNotifyDelay = null,
        Func<TimeSpan, Action, IDisposable>? startTimer = null,
        IForegroundTargetCapture? foregroundTargetCapture = null)
    {
        ArgumentNullException.ThrowIfNull(sessionPublisher);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(traySurface);
        ArgumentNullException.ThrowIfNull(promptSurfaceFactory);
        ArgumentNullException.ThrowIfNull(autoStartCoordinator);
        ArgumentNullException.ThrowIfNull(categorySettingsService);
        ArgumentNullException.ThrowIfNull(userExceptionService);
        ArgumentNullException.ThrowIfNull(detectionPipeline);
        ArgumentNullException.ThrowIfNull(isMasterKeyUnavailable);
        ArgumentNullException.ThrowIfNull(settingsSurfaceFactory);
        ArgumentNullException.ThrowIfNull(registrationCoordinator);

        _scheduler = scheduler;
        _warningNotifyDelay = warningNotifyDelay ?? TimeSpan.Zero;
        _startTimer = startTimer ?? StartRealTimer;
        _protectionStatus = protectionStatus;
        _usReadinessSource = usReadinessSource;
        _usReadinessPipelineSource = settingsPipelineSource;
        _traySurface = traySurface;
        _autoStartCoordinator = autoStartCoordinator;
        _registrationCoordinator = registrationCoordinator;
        _promptCoordinator = new DecisionPromptCoordinator(sessionPublisher, lifecycle, resolver, scheduler, promptSurfaceFactory, foregroundTargetCapture);
        _settingsCoordinator = new SettingsCoordinator(
            categorySettingsService, userExceptionService, detectionPipeline, isMasterKeyUnavailable, settingsSurfaceFactory,
            registrationCoordinator, settingsPipelineSource, countryChoices);

        // A stored United States choice changes what the opt-in pipeline follows: refresh the readiness line at once (a no-op without a readiness source).
        _settingsCoordinator.CountryChoiceChanged += (_, _) => NotifyUsReadinessChanged();
    }

    /// <summary>The only production construction path -- wires the real
    /// <see cref="WpfDispatcherScheduler"/>/<see cref="WinFormsTrayIconSurface"/>/
    /// <see cref="DecisionPromptWindow"/>/<see cref="WindowsAutoStartCoordinator"/> against
    /// <paramref name="composition"/>'s already-started shared objects. <paramref name="composition"/>
    /// must have already completed a successful <see cref="PrivonAppComposition.Start"/> --
    /// <see cref="PrivonAppComposition.SessionPublisher"/>/<see cref="PrivonAppComposition.Lifecycle"/>/
    /// <see cref="PrivonAppComposition.Resolver"/> are only non-null once it has.</summary>
    public static PrivonAppUiBridge CreateProduction(PrivonAppComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        var sessionPublisher = composition.SessionPublisher
            ?? throw new InvalidOperationException("PrivonAppComposition.Start() must succeed before creating the UI bridge.");
        var lifecycle = composition.Lifecycle
            ?? throw new InvalidOperationException("PrivonAppComposition.Start() must succeed before creating the UI bridge.");
        var resolver = composition.Resolver
            ?? throw new InvalidOperationException("PrivonAppComposition.Start() must succeed before creating the UI bridge.");
        var categorySettingsService = composition.CategorySettingsService
            ?? throw new InvalidOperationException("PrivonAppComposition.Start() must succeed before creating the UI bridge.");
        var userExceptionService = composition.SettingsUserExceptionService
            ?? throw new InvalidOperationException("PrivonAppComposition.Start() must succeed before creating the UI bridge.");

        return new PrivonAppUiBridge(
            sessionPublisher,
            lifecycle,
            resolver,
            new WpfDispatcherScheduler(),
            new WinFormsTrayIconSurface(),
            () => new DecisionPromptWindow(),
            new WindowsAutoStartCoordinator(new WindowsAutoStartRegistration()),
            categorySettingsService,
            userExceptionService,
            DetectionPipeline.CreateDefault(),
            () => composition.IsMasterKeyUnavailable,
            () => new SettingsWindow(),
            new NativeMessagingHostRegistrationCoordinator(new WindowsNativeMessagingHostRegistrationEnvironment()),
            composition.DetectionPipelineSource,
            composition.ProtectionStatus,
            composition.UsReadinessSource,
            composition.CountryChoices,
            ProductionWarningNotifyDelay,
            foregroundTargetCapture: composition.TargetCapture);
    }

    private static IDisposable StartRealTimer(TimeSpan delay, Action action) =>
        new System.Threading.Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan);

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
            _traySurface.AutoStartToggleRequested += OnAutoStartToggleRequested;
            _traySurface.SettingsRequested += OnSettingsRequested;
            _traySurface.ChromeReconnectNotificationClicked += OnChromeReconnectNotificationClicked;
            _traySurface.SetAutoStartChecked(_autoStartCoordinator.IsEnabled());
            _traySurface.Show();
            StartProtectionStatusDisplay();
            StartUsReadinessDisplay();
        }
        catch
        {
            RollbackPartialStart();
            throw;
        }

        // Gate 032-C2: onboarding is non-essential and must never affect the STARTUP_ROLLBACK
        // above -- deliberately OUTSIDE that try/catch, with its own total exception containment.
        RunChromeOnboardingCheck();
    }

    // PRIVON 0.3.2 Gate 032-C2 -- the ONE contained, READ-ONLY Chrome readiness inspection Start()
    // performs, exactly once per process. Reuses SettingsCoordinator's own policy-gated Chrome
    // identity/readiness path (never a second, independently-constructed coordinator/environment).
    // Exception-contained in full: any failure anywhere in this method (the inspection itself, the
    // proactive Settings open, or the notification display) is swallowed -- onboarding must never
    // prevent or roll back normal PRIVON startup, which has already fully succeeded by the time this
    // runs.
    private void RunChromeOnboardingCheck()
    {
        try
        {
            var readiness = _settingsCoordinator.InspectChromeReadinessForStartup();
            switch (readiness)
            {
                case NativeMessagingRegistrationReadiness.Fresh:
                    // Gate 032-C2R: feed the readiness THIS SAME call already obtained into the
                    // surface's own first render -- never a second InspectChrome() (see
                    // SettingsCoordinator.ShowRequestedForStartup's own EXACTLY_ONE_STARTUP_INSPECTION
                    // doc). ShowRequested() remains untouched for every other caller.
                    _settingsCoordinator.ShowRequestedForStartup(readiness);
                    break;
                case NativeMessagingRegistrationReadiness.OwnedNeedsRepair:
                    _traySurface.ShowChromeReconnectNotification();
                    break;
            }
        }
        catch
        {
            // Onboarding is non-essential (Gate 032-C2 section 10) -- normal PRIVON startup has
            // already succeeded above; the existing tray Settings menu entry remains available
            // regardless of what happens here.
        }
    }

    // Gate 032-C2: the ONLY authorized reaction to a reconnect-notification click is opening/
    // focusing Settings -- never Repair, never any registry/manifest mutation. Routed through the
    // SAME dispatcher scheduler as every other tray-originated event, for the same uniform-non-
    // blocking reason. ONE_CURRENT_SETTINGS_WINDOW itself remains entirely SettingsCoordinator's own
    // responsibility (mirrors OnSettingsRequested exactly).
    private void OnChromeReconnectNotificationClicked(object? sender, EventArgs e)
    {
        _scheduler.Post(_settingsCoordinator.ShowRequested);
    }

    // STARTUP_ROLLBACK: each step independently swallowed (the ORIGINAL startup exception, not a
    // cleanup failure, is what Start() rethrows) and safe to run regardless of which stage above
    // actually succeeded -- DecisionPromptCoordinator.Dispose()/ITrayIconSurface.Dispose() are
    // both already idempotent and safe even if their corresponding stage never ran (e.g. the
    // coordinator was never Start()-ed, or the tray's ExitRequested/AutoStartToggleRequested was
    // never subscribed).
    private void RollbackPartialStart()
    {
        Safe(StopProtectionStatusDisplay);
        Safe(StopUsReadinessDisplay);
        Safe(() => _promptCoordinator.Dispose());
        Safe(() => _settingsCoordinator.Dispose());
        Safe(() =>
        {
            _traySurface.ExitRequested -= OnExitRequested;
            _traySurface.AutoStartToggleRequested -= OnAutoStartToggleRequested;
            _traySurface.SettingsRequested -= OnSettingsRequested;
            _traySurface.ChromeReconnectNotificationClicked -= OnChromeReconnectNotificationClicked;
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

    // PROTECTION STATUS DISPLAY (every launch). The tracker raises Changed on whichever thread produced the change, so
    // the handler only schedules a render on the UI scheduler; the render reads the newest status itself. Display failures are
    // contained: showing a status must never reach protection processing.
    //
    // The tooltip follows the status at once. A WARNING BALLOON is held back for warningNotifyDelay and raised only if the warning is still there
    // when the delay ends: a failure that the coordinator retries away (a clipboard another program briefly holds) must not alarm the user. At most
    // one balloon is pending, it is withdrawn the moment the warning clears, and everything here runs on the UI scheduler.
    private void StartProtectionStatusDisplay()
    {
        if (_protectionStatus is null || _traySurface is not IProtectionStatusSurface) return;

        _protectionStatus.Changed += OnProtectionStatusChanged;
        _statusSubscribed = true;
        _scheduler.Post(RenderProtectionStatus);
    }

    private void StopProtectionStatusDisplay()
    {
        CancelPendingNotify();
        if (!_statusSubscribed || _protectionStatus is null) return;
        _protectionStatus.Changed -= OnProtectionStatusChanged;
        _statusSubscribed = false;
    }

    private void OnProtectionStatusChanged(object? sender, EventArgs e) => _scheduler.Post(RenderProtectionStatus);

    private void RenderProtectionStatus()
    {
        if (_disposed || _protectionStatus is null || _traySurface is not IProtectionStatusSurface surface) return;

        var presentation = ProtectionStatusPresenter.Present(_protectionStatus.Current);
        bool episodeStart = ProtectionStatusPresenter.ShouldNotify(_lastPresentation, presentation);
        _lastPresentation = presentation;

        if (!presentation.IsWarning) CancelPendingNotify();

        bool notifyNow = false;
        if (episodeStart)
        {
            if (_warningNotifyDelay <= TimeSpan.Zero) notifyNow = true;
            else SchedulePendingNotify();
        }

        try
        {
            surface.SetProtectionStatus(presentation, notifyNow);
        }
        catch
        {
            // A tray that cannot display the status must not affect clipboard protection or the rest of this bridge.
        }
    }

    private void SchedulePendingNotify()
    {
        if (_notifyPending) return;   // one balloon per episode: it will describe whatever the warning is when the delay ends
        _notifyPending = true;
        try
        {
            _pendingNotifyTimer = _startTimer(_warningNotifyDelay, () => _scheduler.Post(ConfirmPendingNotify));
        }
        catch
        {
            _notifyPending = false;   // no timer, no balloon: the tooltip still shows the warning
        }
    }

    private void CancelPendingNotify()
    {
        _notifyPending = false;
        var timer = _pendingNotifyTimer;
        _pendingNotifyTimer = null;
        try { timer?.Dispose(); } catch { }
    }

    // Runs on the UI scheduler when the delay ended. It looks at the status NOW, never at the one that started the timer.
    private void ConfirmPendingNotify()
    {
        if (_disposed || !_notifyPending || _protectionStatus is null || _traySurface is not IProtectionStatusSurface surface) return;

        CancelPendingNotify();
        var presentation = ProtectionStatusPresenter.Present(_protectionStatus.Current);
        if (!presentation.IsWarning) return;

        _lastPresentation = presentation;
        try
        {
            surface.SetProtectionStatus(presentation, true);
        }
        catch
        {
            // Contained, exactly like the render above.
        }
    }

    // U.S. DETECTOR READINESS DISPLAY (explicit opt-in only: it exists only when a readiness source was supplied; a pipeline source alone shows
    // nothing and nothing is inferred). The line is re-read, never remembered: on start, whenever the protection status changes (every attempt
    // re-resolves the pipeline), whenever the user opens the tray menu, and whenever the owner of the authority / country setting calls
    // NotifyUsReadinessChanged. Each render runs on the UI scheduler and reads the source when it RUNS, so a render that arrives late shows the
    // newest state, never the one that triggered it. A source that throws, or a pipeline that cannot be built now, is shown as "cannot be
    // determined" -- an earlier 13/13 is never left on screen. It never raises a balloon and never touches the protection status text.
    private void StartUsReadinessDisplay()
    {
        if (_usReadinessSource is null || _traySurface is not IUsReadinessSurface surface) return;

        surface.UsReadinessRefreshRequested += OnUsReadinessRefreshRequested;
        if (_protectionStatus is not null) _protectionStatus.Changed += OnUsReadinessTrigger;
        _usReadinessSubscribed = true;
        _scheduler.Post(RenderUsReadiness);
    }

    private void StopUsReadinessDisplay()
    {
        if (!_usReadinessSubscribed) return;
        if (_traySurface is IUsReadinessSurface surface) surface.UsReadinessRefreshRequested -= OnUsReadinessRefreshRequested;
        if (_protectionStatus is not null) _protectionStatus.Changed -= OnUsReadinessTrigger;
        _usReadinessSubscribed = false;
    }

    // The menu is about to be shown: the line is re-read RIGHT HERE, before it is displayed. The event is raised on the UI thread (WinForms'
    // contract), so this is the same thread a posted render would use -- but a posted render may run only after the menu has painted the old
    // text, and an old 13/13 must never be what the user sees. Reading takes the in-memory host/registry state only.
    private void OnUsReadinessRefreshRequested(object? sender, EventArgs e) => RenderUsReadiness();

    private void OnUsReadinessTrigger(object? sender, EventArgs e) => _scheduler.Post(RenderUsReadiness);

    /// <summary>For the owner of the U.S. authority / country setting: something it states may have changed -- show the current state. Safe
    /// from any thread; a no-op without a readiness source or after Dispose.</summary>
    internal void NotifyUsReadinessChanged()
    {
        if (_disposed || _usReadinessSource is null) return;
        _scheduler.Post(RenderUsReadiness);
    }

    private void RenderUsReadiness()
    {
        if (_disposed || _usReadinessSource is null || _traySurface is not IUsReadinessSurface surface) return;

        UsReadinessPresentation presentation;
        try
        {
            // The pipeline in use must be buildable right now; if the rebuild fails, no earlier "ready" can stand.
            _ = _usReadinessPipelineSource?.Invoke();
            presentation = UsReadinessPresenter.Present(_usReadinessSource());
        }
        catch
        {
            presentation = UsReadinessPresenter.Unknown();
        }

        try
        {
            surface.SetUsReadiness(presentation);
        }
        catch
        {
            // A tray that cannot display the line must not affect clipboard protection or the rest of this bridge.
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

    // AUTO_START_TOGGLE (Phase 0.2I): routed through the SAME dispatcher scheduler as Exit, for the
    // same uniform-non-blocking reason. ALWAYS re-derives "enable or disable" from a fresh
    // IsEnabled() read (never from any state this handler itself might have cached), attempts
    // exactly the one corresponding operation, then reflects the tray's checked state from a
    // SECOND fresh IsEnabled() read taken after that attempt -- so a failed attempt (whatever the
    // underlying reason) always renders the registry's own actual resulting state, never a state
    // this type merely hoped for (see this type's own AUTO_START_UI_BINDING doc).
    private void OnAutoStartToggleRequested(object? sender, EventArgs e)
    {
        _scheduler.Post(() =>
        {
            if (_autoStartCoordinator.IsEnabled())
                _autoStartCoordinator.TryDisable();
            else
                _autoStartCoordinator.TryEnable();

            _traySurface.SetAutoStartChecked(_autoStartCoordinator.IsEnabled());
        });
    }

    // SETTINGS (PRIVON v0.2.1 Gate 3C): routed through the SAME dispatcher scheduler as Exit/
    // auto-start-toggle, for the same uniform-non-blocking reason. ONE_CURRENT_SETTINGS_WINDOW
    // itself is entirely SettingsCoordinator's own responsibility -- this handler only ever forwards
    // the request.
    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        _scheduler.Post(_settingsCoordinator.ShowRequested);
    }

    /// <summary>SUBSCRIPTION_LIFETIME (Phase 3C STEP40 instruction, frozen; extended PRIVON v0.2.1
    /// Gate 3C): prevents new UI dispatches first -- disposes the decision-prompt coordinator
    /// (unsubscribes from the publisher, closes any currently-visible prompt) and the Settings
    /// coordinator (closes any currently-open Settings window -- SETTINGS_CLOSED_BEFORE_UI_BRIDGE_TEARDOWN,
    /// this Gate's own instruction: Settings must be closed before this method returns) -- THEN
    /// disposes/removes the tray surface. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Safe(StopProtectionStatusDisplay);
        Safe(StopUsReadinessDisplay);
        _promptCoordinator.Dispose();
        _settingsCoordinator.Dispose();

        _traySurface.ExitRequested -= OnExitRequested;
        _traySurface.AutoStartToggleRequested -= OnAutoStartToggleRequested;
        _traySurface.SettingsRequested -= OnSettingsRequested;
        _traySurface.ChromeReconnectNotificationClicked -= OnChromeReconnectNotificationClicked;
        _traySurface.Dispose();
    }
}
