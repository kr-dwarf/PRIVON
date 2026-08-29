using System.IO;
using System.Runtime.ExceptionServices;
using Privon.Storage;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP36 -- the App live composition root, extended in Phase 3C STEP38 with the Windows
/// session-lock sensitive-state-discard capability and in Phase 0.2E with the second, independent
/// <see cref="ForegroundChangeMonitor"/> trigger (Phase 0.2A-D's HYBRID clipboard-protection
/// architecture). Owns the ONE process-wide instance of every shared-identity leaf component
/// (<see cref="ClipboardOperationGate"/>/<see cref="ClipboardDecisionScopeLifecycle"/>/
/// <see cref="ClipboardComposerVerifier"/>/<see cref="ClipboardChangeMonitor"/>/
/// <see cref="ComposerTextReader"/>/<see cref="ISessionLockNotification"/>/
/// <see cref="ForegroundChangeMonitor"/>), wires the real production object graph exactly once, and
/// starts/stops it in the frozen order (Phase 3C STEP35/STEP35.1, extended STEP38, STEP61/Phase
/// 0.2E). This is a plain CLR class -- no DI container/framework, no WPF <c>Application</c>/
/// <c>Window</c>/<c>Dispatcher</c> dependency of any kind -- so it can be constructed and exercised
/// deterministically from <c>Privon.App.Tests</c> without ever instantiating
/// <see cref="System.Windows.Application"/>. <c>App.xaml.cs</c> is a thin adapter that owns exactly
/// one instance of this type.
///
/// STORAGE_ROOT (frozen, APP_COMPOSITION_ROOT_STORAGE_PATH): the production storage root is exactly
/// <see cref="ProductionStorageRootPath"/> -- <c>%LocalAppData%\PRIVON</c>. No fallback to
/// <c>Path.GetTempPath()</c>/<c>Directory.GetCurrentDirectory()</c>/<c>Environment.CurrentDirectory</c>/
/// any repo-relative path exists anywhere in this type. <see cref="PrivonLocalStore.OpenOrCreate"/>
/// remains solely responsible for creating that directory -- this type never calls
/// <c>Directory.CreateDirectory</c> itself and never logs the absolute path.
///
/// OBJECT_GRAPH / SHARED_IDENTITY (frozen, extended Phase 0.2E): exactly one
/// <see cref="ClipboardOperationGate"/>, one <see cref="ClipboardDecisionScopeLifecycle"/> (backing
/// all three of <see cref="IClipboardNotificationLifecycle"/>/<see cref="IClipboardDecisionScopeLifecycle"/>/
/// <see cref="IClipboardGenerationSnapshot"/>), one <see cref="ClipboardComposerVerifier"/> (backing
/// both <see cref="IClipboardComposerVerificationHandoff"/> and
/// <see cref="IClipboardComposerVerificationInvalidation"/> for the coordinator AND the resolver's
/// own handoff), one <see cref="ClipboardChangeMonitor"/> (backing both the read and write
/// transports), one <see cref="ComposerTextReader"/>, one <see cref="ISessionLockNotification"/>, and
/// one <see cref="ForegroundChangeMonitor"/> (backing the coordinator's own
/// <see cref="IClipboardForegroundTrigger"/> dependency, via <see cref="ClipboardForegroundTrigger"/>)
/// exist per instance of this type. The one <see cref="ClipboardPrivacyProcessor"/> and one
/// <see cref="ForegroundTargetCapture"/> constructed in <see cref="Start"/> are likewise reused by
/// both <see cref="ClipboardPrivacyCoordinator"/> and <see cref="ClipboardDecisionActionResolver"/>
/// -- never a second, independent instance of either. The session-lock notification has exactly ONE
/// consumer -- this type's own <see cref="OnSessionLocked"/> callback -- so no other component ever
/// references it. The <see cref="ForegroundChangeMonitor"/> has exactly ONE consumer -- the
/// coordinator's own <see cref="IClipboardForegroundTrigger"/> dependency, via the
/// <see cref="ClipboardForegroundTrigger"/> adapter constructed once in <see cref="BuildGraph"/> --
/// <see cref="ClipboardDecisionActionResolver"/> never receives it, exactly like the resolver never
/// receives <see cref="IClipboardReadTransport.Changed"/>/<see cref="IClipboardNotificationLifecycle"/>
/// directly either (the resolver's own entry point is always an explicit
/// <c>ResolveAsync</c> call, never trigger-driven).
///
/// DIRECT_OWNERSHIP (frozen minimal set, extended STEP38, extended Phase 0.2E): only the components
/// that need lifecycle/disposal/entrypoint ownership are held as fields --
/// <see cref="ISessionLockNotification"/>/<see cref="ClipboardChangeMonitor"/>/
/// <see cref="ComposerTextReader"/>/<see cref="ForegroundChangeMonitor"/>/
/// <see cref="ClipboardOperationGate"/>/<see cref="ClipboardDecisionScopeLifecycle"/>/
/// <see cref="ClipboardComposerVerifier"/>/<see cref="ClipboardPrivacyCoordinator"/>/
/// <see cref="ClipboardDecisionActionResolver"/>. Transitive dependencies already kept alive by their
/// live consumers (the read/write/composer-read/foreground-trigger transports, the trust/exception
/// provider, the privacy processor, the decision-session publisher, the foreground target capture)
/// are deliberately NOT duplicated as separate fields here -- GC reachability through the
/// coordinator/resolver/verifier is sufficient, and a duplicate field would only be dead weight.
/// <see cref="ForegroundChangeMonitor"/> is held directly, exactly mirroring <see cref="ClipboardChangeMonitor"/>'s
/// own reason for being a direct-ownership field despite the COORDINATOR (not this type) being what
/// actually calls its <c>Start</c>/<c>Stop</c> (via the <see cref="ClipboardForegroundTrigger"/>
/// seam) -- this type still needs the raw instance for disposal ownership and for
/// STARTUP_FAILURE_ROLLBACK to reach it even when the coordinator never got far enough to touch it
/// at all.
///
/// START_ORDER (frozen, Phase 3C STEP35.1, revised STEP38, revised Phase 0.2E): the ENTIRE object
/// graph is fully constructed (including subscribing <see cref="OnSessionLocked"/> to
/// <see cref="ISessionLockNotification.Locked"/> -- see <see cref="BuildGraph"/>) before any callback
/// source becomes live, then <see cref="ISessionLockNotification.Start"/>, then
/// <see cref="ComposerTextReader.Start"/>, then <see cref="ClipboardPrivacyCoordinator.Start"/> LAST
/// -- that single coordinator call is what ultimately starts BOTH <see cref="ClipboardChangeMonitor"/>
/// (via its own internal <see cref="IClipboardReadTransport.Start"/> call) AND
/// <see cref="ForegroundChangeMonitor"/> (via its own internal, optional
/// <see cref="IClipboardForegroundTrigger.Start"/> call, per <see cref="ClipboardPrivacyCoordinator.Start"/>'s
/// own frozen STARTUP_ORDER: clipboard transport started before the foreground trigger); this type
/// never calls <see cref="ClipboardChangeMonitor.Start"/> or <see cref="ForegroundChangeMonitor.Start"/>
/// directly, and never calls <see cref="Privon.Windows.SessionLockMonitor.Start"/> directly either
/// (only ever through <see cref="ISessionLockNotification"/>). The session-lock observer becomes live
/// BEFORE clipboard protection does, so a lock event can never be missed once protection begins
/// (Phase 3C STEP38 instruction's explicit ordering preference) -- but its own callback targets
/// (<c>_lifecycle</c>/<c>_verifier</c>) are already fully constructed by the time it starts, so no
/// callback can ever reach a partially-built graph either.
///
/// STARTUP_FAILURE_ROLLBACK (frozen, revised STEP38, revised Phase 0.2E): a single <c>try</c>/<c>catch</c>
/// wraps graph construction, the session-lock-observer start, the composer-reader start, and the
/// coordinator start (which itself starts both the clipboard monitor and the foreground monitor)
/// together -- ANY exception at ANY of those stages triggers the identical
/// <see cref="RollbackPartialStartup"/> cleanup (each step individually swallowed so a cleanup
/// failure never masks the original startup exception) before the original exception is rethrown
/// unchanged. Whatever direct-ownership fields never got assigned before the failure simply stay
/// <see langword="null"/> and are skipped by that cleanup -- there is no "degraded success" state; a
/// failed <see cref="Start"/> always leaves this instance with no active protection pipeline. Session-
/// lock detection is now REQUIRED security infrastructure (Phase 3C STEP38 instruction, frozen) --
/// its own <see cref="ISessionLockNotification.Start"/> failure fails the whole composition-root
/// startup exactly like a <see cref="ComposerTextReader.Start"/>/<see cref="ClipboardPrivacyCoordinator.Start"/>
/// failure already does; there is no "run without lock protection" fallback path anywhere in this
/// type. If <see cref="ForegroundChangeMonitor.Start"/> itself fails INSIDE the coordinator's own
/// <c>Start</c> call (e.g. it was already started via some other path before this composition ever
/// ran), the coordinator's own STARTUP_FAILURE_ROLLBACK already stops whatever it itself started
/// (the clipboard transport) before rethrowing -- this type's own <see cref="RollbackPartialStartup"/>
/// then independently disposes the raw <see cref="ForegroundChangeMonitor"/> field it directly owns,
/// exactly mirroring how it already disposes <c>_monitor</c> regardless of which layer actually
/// called <c>Start</c> on it.
///
/// SHUTDOWN_ORDER (frozen, Phase 3C STEP36 instruction, revised STEP38, revised Phase 0.2E): the
/// session-lock observer is unsubscribed and stopped FIRST -- <see cref="ISessionLockNotification.Locked"/>
/// -= <see cref="OnSessionLocked"/> then <see cref="ISessionLockNotification.Stop"/> -- so no lock
/// callback can ever fire into a dependency this type is about to tear down, THEN
/// <see cref="ClipboardPrivacyCoordinator.Dispose"/> (which itself stops both the clipboard transport
/// and, if it owns it, the foreground trigger -- see that type's own PerformCleanup doc) -&gt;
/// <see cref="ClipboardComposerVerifier.InvalidatePending"/> -&gt;
/// <see cref="ClipboardDecisionScopeLifecycle.Reset"/> -&gt; <see cref="ComposerTextReader.Dispose"/>
/// -&gt; <see cref="ClipboardChangeMonitor.Dispose"/> -&gt; <see cref="ForegroundChangeMonitor.Dispose"/>
/// -&gt; <see cref="ClipboardOperationGate.Dispose"/>. <see cref="ForegroundChangeMonitor.Dispose"/>
/// is placed immediately after <see cref="ClipboardChangeMonitor.Dispose"/> -- mirroring the
/// coordinator's own CLEANUP_ORDER, which always stops the clipboard transport before the foreground
/// trigger -- and is itself always safe to call even though the coordinator's own
/// <see cref="ClipboardPrivacyCoordinator.Dispose"/> (above) has typically already called
/// <see cref="ForegroundChangeMonitor.Stop"/> on it via the trigger seam: <c>Stop</c> is
/// idempotent-on-already-stopped (see that type's own doc), so this second, direct call is always a
/// safe no-op by the time it runs, exactly like the identical existing
/// <see cref="ClipboardChangeMonitor.Dispose"/>-after-coordinator-already-stopped-it sequence this
/// type has always relied on. <see cref="Dispose"/> runs every step even if an earlier one fails
/// (each wrapped independently), so the sensitive-state-discard steps always run regardless of an
/// observer/coordinator/reader/monitor cleanup failure -- but this instance is only marked fully
/// disposed once every step succeeded (mirroring this codebase's established
/// DISPOSE_FAILURE_BEHAVIOR discipline: a later retry genuinely retries cleanup rather than silently
/// no-op'ing over an unresolved failure); the FIRST failure encountered is what ultimately
/// propagates. <see cref="ISessionLockNotification"/> declares no <c>Dispose</c> member of its own --
/// <see cref="ISessionLockNotification.Stop"/> alone already achieves full, deterministic teardown
/// (unregister/destroy/join), exactly like calling <see cref="Privon.Windows.SessionLockMonitor.Stop"/>
/// directly would.
///
/// SESSION_LOCK_CALLBACK (Phase 3C STEP37/STEP37.1, frozen, implemented in <see cref="OnSessionLocked"/>):
/// runs synchronously on the underlying owner thread, does EXACTLY <c>lifecycle.Reset()</c> then
/// <c>verifier.InvalidatePending()</c> -- in that exact order (Reset is the generation-axis
/// invalidation linearization point; InvalidatePending is the pending-reference lifetime cleanup
/// boundary) -- and returns. Never acquires <see cref="ClipboardOperationGate"/>, never performs UI
/// Automation, never touches clipboard content, never mutates Storage, never logs anything (both
/// calls are already bounded, lock-only operations -- Phase 3B STEP16.1's CALLBACK_LOCK_ACCEPTABILITY
/// finding, applied identically here). On unlock (or any other WTS reason code), no App code runs at
/// all -- <see cref="Privon.Windows.SessionLockMonitor"/> itself never raises a notification for
/// anything except <c>WTS_SESSION_LOCK</c> (see that type's own 0.1_SCOPE doc); this type does not
/// restore scope/grants/pending state on unlock and never will.
///
/// OUT OF SCOPE for this STEP (deliberately not implemented anywhere near this type): a
/// <c>ProtectionState</c> aggregator, any automatic/timer/callback-triggered
/// <see cref="ClipboardComposerVerifier.VerifyAsync"/> call (remains explicit-user-triggered future
/// behavior), Send-intent interception, BypassOnce, a <c>MainWindow</c>/<c>StartupUri</c>, hotkeys,
/// and WTS session-disconnect/logoff hardening (<c>SESSION_DISCONNECT_HARDENING</c> remains
/// OPEN/optional/deferred). NeedsDecision UI and the tray icon are NOT out of scope of the product
/// as a whole -- both are live, owned by <see cref="PrivonAppUiBridge"/>, which is constructed
/// directly on top of this type's own exposed <see cref="Resolver"/>/<see cref="Lifecycle"/>/
/// <see cref="SessionPublisher"/> (see <see cref="PrivonAppUiBridge.CreateProduction"/>) -- they are
/// simply out of scope of THIS type's own direct ownership, exactly like every other field this
/// type exposes rather than owns.
/// </summary>
internal sealed class PrivonAppComposition : IDisposable
{
    /// <summary>
    /// APP_COMPOSITION_ROOT_STORAGE_PATH (frozen): the ONLY production storage root --
    /// <c>%LocalAppData%\PRIVON</c>. Computed once, at type load; never a temp directory, never the
    /// current working directory, never a repo-relative path.
    /// </summary>
    internal static readonly string ProductionStorageRootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PRIVON");

    private readonly string _storageRootPath;
    private readonly object _gate = new();

    private bool _startCalled;
    private bool _disposed;

    // Direct-ownership roots -- see this type's own DIRECT_OWNERSHIP doc above.
    // _sessionLockNotification/_monitor/_composerReader/_foregroundChangeMonitor are assigned in the
    // constructor (construction alone starts no thread and performs no I/O); every other
    // direct-ownership field is assigned only once graph construction inside Start() reaches it, so a
    // startup failure never leaves a field pointing at something this type never actually built.
    private readonly ISessionLockNotification _sessionLockNotification;
    private readonly ClipboardChangeMonitor _monitor;
    private readonly ComposerTextReader _composerReader;
    private readonly ForegroundChangeMonitor _foregroundChangeMonitor;
    private ClipboardOperationGate? _operationGate;
    private ClipboardDecisionScopeLifecycle? _lifecycle;
    private ClipboardComposerVerifier? _verifier;
    private ClipboardPrivacyCoordinator? _coordinator;
    private ClipboardDecisionActionResolver? _resolver;
    private ClipboardDecisionSessionPublisher? _sessionPublisher;

    // PRIVON v0.2.1 Gate 3C -- the Settings UI's own backend seams. _store is retained (unlike the
    // read-only *Provider locals BuildGraph already constructs) purely so IsMasterKeyUnavailable
    // below can expose Storage's own metadata-only signal -- never a second, independently-drifting
    // source of truth (see PrivonLocalStore.IsMasterKeyUnavailable's own doc). Assigned once, in
    // BuildGraph, exactly like every other direct-ownership field here.
    private PrivonLocalStore? _store;
    private ProtectionCategorySettingsService? _categorySettingsService;
    private UserExceptionService? _userExceptionService;

    // Phase 3C STEP41.1 correction (STEP43.1): the ONE diagnostic recorder instance for the whole
    // process -- but as of this STEP, only ever constructed at all when _enableDiagnostics is true
    // (see BuildGraph). PUBLIC_DIAGNOSTIC_DEFAULT_OFF (frozen, STEP43.1): the public, parameterless
    // production constructor below resolves _enableDiagnostics from a single environment-variable
    // check (DiagnosticsEnabledFromEnvironment) -- normal public launches never set that variable,
    // so this field simply stays null for the lifetime of the whole process: no FileStream is ever
    // opened, no background writer Task is ever started (FileClipboardDiagnosticRecorder's own
    // constructor does both unconditionally the instant it is constructed -- so "don't open the
    // file" alone is not enough; the fix is "never construct the type at all" in the default path).
    // Still holds no sensitive/security state of any kind when it IS constructed, so its disposal
    // remains placed last, after every other direct-ownership field, in both
    // RollbackPartialStartup and Dispose -- it never participates in, and never affects the
    // relative order of, any of this type's already-frozen sensitive-state-discard steps.
    private FileClipboardDiagnosticRecorder? _diagnostics;

    // EXPLICIT_QA_DIAGNOSTICS_GATE (frozen, STEP43.1): the sole switch controlling whether
    // BuildGraph constructs a real FileClipboardDiagnosticRecorder at all. Set once, in the
    // constructor, from either the caller-supplied test seam (internal constructor) or the
    // environment-variable check (public constructor) -- never re-evaluated afterward, never a
    // settings screen, never persisted anywhere.
    private readonly bool _enableDiagnostics;

    /// <summary>
    /// PUBLIC_DIAGNOSTIC_DEFAULT_OFF (STEP43.1): the only constructor <c>App.xaml.cs</c> ever
    /// calls -- wires the real production <see cref="SessionLockNotificationAdapter"/>/
    /// <see cref="ClipboardChangeMonitor"/>/<see cref="ComposerTextReader"/>/
    /// <see cref="ForegroundChangeMonitor"/> against the frozen production storage root, and enables
    /// the metadata-only diagnostic recorder ONLY when <see cref="DiagnosticsEnabledFromEnvironment"/>
    /// reports the explicit opt-in environment variable is set. A normal public launch (that
    /// variable unset, which is every real end user's machine) therefore never constructs
    /// <see cref="FileClipboardDiagnosticRecorder"/> at all -- no <c>%TEMP%\privon-diagnostic-*.log</c>
    /// file is ever created, no background writer thread is ever started, by default.
    /// </summary>
    public PrivonAppComposition()
        : this(ProductionStorageRootPath, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor(), DiagnosticsEnabledFromEnvironment())
    {
    }

    // EXPLICIT_QA_DIAGNOSTICS_GATE mechanism (STEP43.1): a single environment-variable check,
    // evaluated once, only from the public production constructor above -- never read anywhere
    // else in this type, never read by BuildGraph itself (which only ever consults the already-
    // resolved _enableDiagnostics field). Deliberately NOT a settings file/registry key/UI toggle
    // -- "least invasive mechanism supported by the current architecture" per this STEP's own
    // instruction: a developer or manual-QA session opts in by setting
    // PRIVON_ENABLE_DIAGNOSTICS=1 in the environment before launching the exe, and nothing else
    // in this codebase ever sets it implicitly. Exact-match "1" only -- no "true"/"yes"/case-
    // insensitive parsing, matching this codebase's established preference for simple, explicit
    // checks over general-purpose config parsing.
    private static bool DiagnosticsEnabledFromEnvironment() =>
        Environment.GetEnvironmentVariable("PRIVON_ENABLE_DIAGNOSTICS") == "1";

    /// <summary>
    /// Test-only seam: lets a targeted regression supply an alternate storage root (e.g. a temp
    /// directory, or a deliberately-unusable path to force a storage-construction failure), an
    /// alternate <see cref="ISessionLockNotification"/> (a hand-written fake, to exercise this type's
    /// own callback wiring/ordering/failure handling without any real Windows session-lock delivery),
    /// and/or an already-<see cref="ClipboardChangeMonitor.Start"/>-ed / already-<see cref="ComposerTextReader.Start"/>-ed
    /// / already-<see cref="ForegroundChangeMonitor.Start"/>-ed instance (to deterministically
    /// reproduce the real, documented "second Start() always throws" failure this type's own rollback
    /// logic must handle) -- without a mocking framework and without bypassing the real production
    /// object graph for anything else. The production (public) constructor above always supplies
    /// fresh, unstarted real instances.
    ///
    /// <paramref name="enableDiagnostics"/> (STEP43.1) defaults to <see langword="false"/> -- every
    /// pre-STEP43.1 test call site that omits this argument keeps compiling and keeps behaving
    /// exactly as if diagnostics were disabled (which, as of this STEP, they always were by
    /// construction anyway -- see <see cref="_enableDiagnostics"/>'s own doc). A test that needs to
    /// exercise the EXPLICIT_QA_DIAGNOSTICS_GATE path passes <see langword="true"/> here directly --
    /// never via a real environment-variable mutation, which would be global, shared, process-wide
    /// mutable state unsafe to touch from a parallel test run.
    /// </summary>
    internal PrivonAppComposition(
        string storageRootPath,
        ISessionLockNotification sessionLockNotification,
        ClipboardChangeMonitor monitor,
        ComposerTextReader composerReader,
        ForegroundChangeMonitor foregroundChangeMonitor,
        bool enableDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(storageRootPath);
        ArgumentNullException.ThrowIfNull(sessionLockNotification);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(composerReader);
        ArgumentNullException.ThrowIfNull(foregroundChangeMonitor);
        _storageRootPath = storageRootPath;
        _sessionLockNotification = sessionLockNotification;
        _monitor = monitor;
        _composerReader = composerReader;
        _foregroundChangeMonitor = foregroundChangeMonitor;
        _enableDiagnostics = enableDiagnostics;
    }

    /// <summary>Reachable once <see cref="Start"/> has completed successfully -- the future entry
    /// point a Runtime Decision UI will call <c>ResolveAsync</c> against. <see langword="null"/>
    /// before a successful <see cref="Start"/>.</summary>
    internal ClipboardDecisionActionResolver? Resolver => _resolver;

    /// <summary>Reachable once <see cref="Start"/> has completed successfully -- the future direct
    /// <c>VerifyAsync</c> entry point. <see langword="null"/> before a successful <see cref="Start"/>.</summary>
    internal ClipboardComposerVerifier? Verifier => _verifier;

    /// <summary>Phase 3C STEP40 -- reachable once <see cref="Start"/> has completed successfully,
    /// for <see cref="PrivonAppUiBridge.CreateProduction"/> to subscribe its
    /// <see cref="DecisionPromptCoordinator"/> against the SAME shared instance the coordinator
    /// itself already publishes through -- never a second, independent
    /// <see cref="ClipboardDecisionSessionPublisher"/>. <see langword="null"/> before a successful
    /// <see cref="Start"/>.</summary>
    internal ClipboardDecisionSessionPublisher? SessionPublisher => _sessionPublisher;

    /// <summary>Phase 3C STEP40 -- reachable once <see cref="Start"/> has completed successfully,
    /// exposed as the narrow <see cref="IClipboardDecisionScopeLifecycle"/> view for
    /// <see cref="PrivonAppUiBridge.CreateProduction"/>'s own staleness rechecks -- the SAME
    /// concrete <see cref="ClipboardDecisionScopeLifecycle"/> instance every other shared consumer
    /// already uses. <see langword="null"/> before a successful <see cref="Start"/>.</summary>
    internal IClipboardDecisionScopeLifecycle? Lifecycle => _lifecycle;

    /// <summary>PRIVON v0.2.1 Gate 3C -- reachable once <see cref="Start"/> has completed
    /// successfully, for <see cref="PrivonAppUiBridge.CreateProduction"/> to construct the ONE
    /// <see cref="SettingsCoordinator"/> against -- never a second, independently-constructed
    /// service over a different <see cref="PrivonLocalStore"/> instance. <see langword="null"/>
    /// before a successful <see cref="Start"/>.</summary>
    internal ProtectionCategorySettingsService? CategorySettingsService => _categorySettingsService;

    /// <summary>PRIVON v0.2.1 Gate 3C -- reachable once <see cref="Start"/> has completed
    /// successfully, for the SAME reason as <see cref="CategorySettingsService"/>.
    /// <see langword="null"/> before a successful <see cref="Start"/>.</summary>
    internal UserExceptionService? SettingsUserExceptionService => _userExceptionService;

    /// <summary>PRIVON v0.2.1 Gate 3C -- the Settings UI's own honest degraded-state signal, sourced
    /// directly from <see cref="PrivonLocalStore.IsMasterKeyUnavailable"/> (no duplicate source of
    /// truth). <see langword="false"/> before a successful <see cref="Start"/> -- there is nothing
    /// degraded to report about a composition root that never finished constructing its Storage
    /// layer at all.</summary>
    internal bool IsMasterKeyUnavailable => _store?.IsMasterKeyUnavailable ?? false;

    /// <summary>
    /// Single-use, matching every other <c>Start</c> in this codebase's own exact precedent (e.g.
    /// <see cref="ClipboardChangeMonitor.Start"/>/<see cref="ClipboardPrivacyCoordinator.Start"/>) --
    /// a second call always throws, regardless of whether the first succeeded or failed. See this
    /// type's own START_ORDER/STARTUP_FAILURE_ROLLBACK docs above for the full contract.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startCalled)
                throw new InvalidOperationException(
                    "Start has already been called on this PrivonAppComposition instance -- it is single-use.");
            _startCalled = true;
        }

        try
        {
            BuildGraph();
            _sessionLockNotification.Start();
            _composerReader.Start();
            _coordinator!.Start();
        }
        catch
        {
            RollbackPartialStartup();
            throw;
        }
    }

    // Constructs the complete object graph -- store/processor first (so a storage-construction
    // failure leaves every direct-ownership field untouched, satisfying "no partial runtime"), then
    // the shared roots, then the App-owned adapters, then the coordinator/resolver themselves, then
    // (last, since it depends on _lifecycle/_verifier already existing) the session-lock callback
    // subscription. Nothing capable of raising a live callback (the session-lock notification, the
    // monitor, the composer reader) is STARTED here -- only constructed/subscribed.
    private void BuildGraph()
    {
        var store = PrivonLocalStore.OpenOrCreate(_storageRootPath);
        _store = store;
        var trustExceptionProvider = new TrustExceptionProvider(store);
        var categorySettingsProvider = new ProtectionCategorySettingsProvider(store);
        var userExceptionProvider = new UserExceptionProvider(store);
        var processor = new ClipboardPrivacyProcessor(trustExceptionProvider, categorySettingsProvider, userExceptionProvider);

        // PRIVON v0.2.1 Gate 3C -- the SAME store instance backs both the read-only providers above
        // (consumed by the clipboard-privacy pipeline) and these mutation-capable services (consumed
        // only by the Settings UI) -- never a second, independently-opened PrivonLocalStore.
        _categorySettingsService = new ProtectionCategorySettingsService(store);
        _userExceptionService = new UserExceptionService(store);

        _operationGate = new ClipboardOperationGate();
        _lifecycle = new ClipboardDecisionScopeLifecycle();

        var readTransport = new ClipboardReadTransport(_monitor);
        var writeTransport = new ClipboardWriteTransport(_monitor);
        var composerReadTransport = new ComposerReadTransport(_composerReader);
        var targetCapture = new ForegroundTargetCapture();
        // Phase 0.2E: the second, independent trigger -- wraps this instance's own directly-owned
        // _foregroundChangeMonitor (never a second, freshly-constructed ForegroundChangeMonitor) so
        // that SESSION_LOCK/SHUTDOWN_ORDER's direct _foregroundChangeMonitor.Dispose() call and this
        // trigger's own Start()/Stop() (invoked entirely from inside ClipboardPrivacyCoordinator.Start/
        // PerformCleanup, never by this type directly -- see this type's own START_ORDER doc) are
        // always operating on the exact SAME underlying native monitor.
        var foregroundTrigger = new ClipboardForegroundTrigger(_foregroundChangeMonitor);

        _verifier = new ClipboardComposerVerifier(_lifecycle, composerReadTransport, _operationGate);

        _sessionPublisher = new ClipboardDecisionSessionPublisher(_lifecycle);

        // PUBLIC_DIAGNOSTIC_DEFAULT_OFF (STEP43.1): FileClipboardDiagnosticRecorder is now
        // constructed -- and _monitor.DiagnosticObserved/WriteDiagnosticObserved are now
        // subscribed -- ONLY when _enableDiagnostics is true. _diagnostics stays null on every
        // normal public launch: no FileStream is opened, no background writer Task is started (see
        // FileClipboardDiagnosticRecorder's own constructor -- both happen unconditionally the
        // instant the type is constructed, so skipping construction entirely, not merely skipping
        // the file-open, is what this gate actually needs to guarantee). When explicitly enabled
        // (EXPLICIT_QA_DIAGNOSTICS_GATE, see _enableDiagnostics's own doc), behavior is unchanged
        // from before this STEP: a fresh, per-process, metadata-only diagnostic log under the local
        // temp directory, routing both Windows-layer, native-boundary diagnostics (native
        // WM_CLIPBOARDUPDATE -> self-write-suppression decision -> Changed delivery; the
        // guarded-write sequence-attribution boundary) into the SAME file the App-layer trace
        // writes to -- see FileClipboardDiagnosticRecorder's own TWO_LAYER_SINK doc for why the
        // three event schemas are still never merged into one type. _monitor is disposed together
        // with this whole composition (see SHUTDOWN_ORDER above), so no explicit unsubscription is
        // needed when enabled -- once Dispose stops the owner thread, no further
        // DiagnosticObserved/WriteDiagnosticObserved invocation can occur.
        if (_enableDiagnostics)
        {
            _diagnostics = new FileClipboardDiagnosticRecorder(FileClipboardDiagnosticRecorder.CreateDefaultFilePath());
            _monitor.DiagnosticObserved += (_, windowsEvent) => _diagnostics.RecordWindowsEvent(windowsEvent);
            _monitor.WriteDiagnosticObserved += (_, writeEvent) => _diagnostics.RecordWriteDiagnosticEvent(writeEvent);
        }

        _coordinator = new ClipboardPrivacyCoordinator(
            readTransport,
            targetCapture,
            processor,
            writeTransport,
            _lifecycle,
            _sessionPublisher,
            _operationGate,
            _verifier,
            _verifier,
            diagnostics: _diagnostics,
            foregroundTrigger: foregroundTrigger);

        _resolver = new ClipboardDecisionActionResolver(
            _operationGate,
            _lifecycle,
            targetCapture,
            readTransport,
            writeTransport,
            processor,
            _verifier);

        // SESSION_LOCK_CALLBACK subscription -- last, since it closes over _lifecycle/_verifier,
        // which must already exist. Not started here (Start() is called separately, strictly before
        // the composer reader/coordinator, in the caller) -- subscribing alone raises nothing.
        _sessionLockNotification.Locked += OnSessionLocked;
    }

    // SESSION_LOCK_CALLBACK (Phase 3C STEP37/STEP37.1, frozen): runs synchronously on
    // ISessionLockNotification's own underlying owner thread. Exactly these two calls, in exactly
    // this order, then return -- no operation-gate acquisition, no UIA, no clipboard access, no
    // Storage mutation, no logging. See this type's own class doc for the full frozen contract.
    private void OnSessionLocked(object? sender, EventArgs e)
    {
        _lifecycle!.Reset();
        _verifier!.InvalidatePending();
    }

    // STARTUP_FAILURE_ROLLBACK -- every step independently swallowed (the ORIGINAL startup exception,
    // not a cleanup failure, is what Start() rethrows) and safe to run against a partially-built graph
    // (any field never reached during BuildGraph/start is simply null and skipped). The session-lock
    // observer is unsubscribed/stopped FIRST -- it was the first thing started, and no lock callback
    // may fire into a lifecycle/verifier this method is about to tear down. Every direct-ownership
    // field is nulled out afterward -- a failed Start() must never leave Resolver/Verifier exposing a
    // coordinator/resolver built on top of a monitor/reader this instance no longer stands behind
    // ("no degraded success state").
    private void RollbackPartialStartup()
    {
        Safe(() =>
        {
            _sessionLockNotification.Locked -= OnSessionLocked;
            _sessionLockNotification.Stop();
        });
        Safe(() => _coordinator?.Dispose());
        Safe(() => _verifier?.InvalidatePending());
        Safe(() => _lifecycle?.Reset());
        Safe(() => _composerReader.Dispose());
        Safe(() => _monitor.Dispose());
        Safe(() => _foregroundChangeMonitor.Dispose());
        Safe(() => _operationGate?.Dispose());
        Safe(() => _diagnostics?.Dispose());

        _coordinator = null;
        _resolver = null;
        _sessionPublisher = null;
        _verifier = null;
        _lifecycle = null;
        _operationGate = null;
        _diagnostics = null;
        _categorySettingsService = null;
        _userExceptionService = null;
        _store = null;
    }

    /// <summary>
    /// SHUTDOWN_ORDER / SENSITIVE_STATE_DISCARD (frozen, see this type's own class doc). Idempotent
    /// once every step has succeeded; a failure in one step never prevents the remaining steps from
    /// running, but this instance is only marked fully disposed once ALL of them succeeded -- a later
    /// call after a failure genuinely retries, matching this codebase's established
    /// DISPOSE_FAILURE_BEHAVIOR discipline (e.g. <see cref="ClipboardChangeMonitor.Dispose"/>). Safe
    /// to call even if <see cref="Start"/> was never called or failed (every direct-ownership field is
    /// then either unstarted-but-safe-to-dispose or still null).
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        Exception? firstFailure = null;
        void Step(Action action)
        {
            try { action(); }
            catch (Exception ex) { firstFailure ??= ex; }
        }

        Step(() =>
        {
            _sessionLockNotification.Locked -= OnSessionLocked;
            _sessionLockNotification.Stop();
        });
        Step(() => _coordinator?.Dispose());
        Step(() => _verifier?.InvalidatePending());
        Step(() => _lifecycle?.Reset());
        Step(() => _composerReader.Dispose());
        Step(() => _monitor.Dispose());
        Step(() => _foregroundChangeMonitor.Dispose());
        Step(() => _operationGate?.Dispose());
        Step(() => _diagnostics?.Dispose());

        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }

        lock (_gate)
        {
            _disposed = true;
        }
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch
        {
            // STARTUP_FAILURE_ROLLBACK: a cleanup failure here must never replace the original
            // startup exception Start() is already unwinding with.
        }
    }
}
