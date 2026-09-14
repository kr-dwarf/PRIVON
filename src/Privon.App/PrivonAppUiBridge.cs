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
        NativeMessagingHostRegistrationCoordinator registrationCoordinator)
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
        _traySurface = traySurface;
        _autoStartCoordinator = autoStartCoordinator;
        _registrationCoordinator = registrationCoordinator;
        _promptCoordinator = new DecisionPromptCoordinator(sessionPublisher, lifecycle, resolver, scheduler, promptSurfaceFactory);
        _settingsCoordinator = new SettingsCoordinator(
            categorySettingsService, userExceptionService, detectionPipeline, isMasterKeyUnavailable, settingsSurfaceFactory,
            registrationCoordinator);
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
            new NativeMessagingHostRegistrationCoordinator(new WindowsNativeMessagingHostRegistrationEnvironment()));
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
            _traySurface.AutoStartToggleRequested += OnAutoStartToggleRequested;
            _traySurface.SettingsRequested += OnSettingsRequested;
            _traySurface.ChromeReconnectNotificationClicked += OnChromeReconnectNotificationClicked;
            _traySurface.SetAutoStartChecked(_autoStartCoordinator.IsEnabled());
            _traySurface.Show();
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

        _promptCoordinator.Dispose();
        _settingsCoordinator.Dispose();

        _traySurface.ExitRequested -= OnExitRequested;
        _traySurface.AutoStartToggleRequested -= OnAutoStartToggleRequested;
        _traySurface.SettingsRequested -= OnSettingsRequested;
        _traySurface.ChromeReconnectNotificationClicked -= OnChromeReconnectNotificationClicked;
        _traySurface.Dispose();
    }
}
