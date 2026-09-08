using Privon.Detection;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3C -- the plain-CLR controller that owns the Settings UI's singleton-window
/// lifecycle and wires every user-intent event a real <see cref="ISettingsSurface"/> raises through
/// to the App-owned mutation services, mirroring <see cref="DecisionPromptCoordinator"/>'s own
/// proven ownership style exactly: that type owns BOTH "one current prompt" lifecycle AND the
/// actual <c>ProtectAllRequested -&gt; resolver</c> wiring (see <see cref="DecisionPromptCoordinator.RunProtectAllAsync"/>)
/// -- this type is the direct Settings-side analogue, wiring toggle/add/delete/reset requests
/// through to <see cref="ProtectionCategorySettingsService"/>/<see cref="UserExceptionService"/>
/// instead of a resolver.
///
/// COORDINATOR_NOT_TRUTH: this type owns no persistence truth of its own (SOURCE_OF_TRUTH --
/// see <see cref="SettingsViewState"/>'s own doc) -- every mutation call is immediately followed by
/// a fresh <see cref="ProtectionCategorySettingsService.Load"/>/<see cref="UserExceptionService.List"/>
/// re-read, never a locally-cached copy.
///
/// ONE_CURRENT_SETTINGS_WINDOW (frozen for this Gate): at most one <see cref="ISettingsSurface"/>
/// is ever visible at a time. A second <see cref="ShowRequested"/> call while one is already open
/// activates the EXISTING surface (<see cref="ISettingsSurface.Activate"/>) -- it never constructs
/// a second one. Closing (however it happened) clears the tracked reference, so the NEXT
/// <see cref="ShowRequested"/> call constructs a genuinely fresh surface and reloads authoritative
/// state from scratch (REOPEN_RELOADS).
///
/// IMMEDIATE_SAVE / RELOAD_AFTER_WRITE (frozen for this Gate): every mutation -- toggle, Add,
/// Delete, Reset -- is a single synchronous service call, success or failure, followed
/// UNCONDITIONALLY by a fresh <see cref="RenderCurrentState"/> call. There is no staged/Apply-Save
/// UI state anywhere in this type: a failed mutation is caught here, surfaced via
/// <see cref="SettingsViewState.StatusMessage"/> on the SAME reload, and the reloaded state is
/// always whatever Storage actually now holds -- never a value this type merely hoped for (same
/// discipline already established by <see cref="PrivonAppUiBridge"/>'s own AUTO_START_UI_BINDING).
///
/// CANONICALIZATION (frozen for this Gate): <see cref="HandleAddException"/> is the ONLY place a
/// raw Add Exception value is ever interpreted -- it runs the real
/// <see cref="DetectionPipeline.Detect"/> over <see cref="AddExceptionRequest.RawValue"/> and
/// accepts the request ONLY when detection produced EXACTLY ONE candidate AND that candidate's
/// <see cref="DetectionCandidate.PiiType"/> equals <see cref="AddExceptionRequest.SelectedType"/> --
/// zero candidates, more than one candidate, and a type mismatch are each rejected with their own
/// distinct, generic, non-content-bearing <see cref="SettingsViewState.StatusMessage"/> (never the
/// raw entered value). <see cref="UserExceptionService.Add"/> is called with the candidate's OWN
/// <see cref="DetectionCandidate.Canonical"/> value -- never a UI-side re-normalization of
/// <see cref="AddExceptionRequest.RawValue"/>, which would violate this codebase's established
/// "Detection's own canonicalizers remain the sole normalization owner" precedent (see
/// <see cref="UserExceptionPolicyEvaluator"/>'s own IDENTITY doc).
///
/// CONCURRENCY: every public member here is invoked only from the single WPF Dispatcher thread in
/// production (a real <see cref="ISettingsSurface"/>'s own events all fire on that thread; the tray
/// SettingsRequested handoff is itself re-marshaled through <see cref="IDispatcherScheduler"/>
/// before ever reaching <see cref="ShowRequested"/> -- see <see cref="PrivonAppUiBridge"/>). No lock
/// is added here pre-emptively -- correctness comes from serialized dispatcher execution plus the
/// ONE_CURRENT_SETTINGS_WINDOW singleton, exactly like this Gate's own CONCURRENCY_CONTRACT
/// instruction states.
/// </summary>
// [PRIVON-AI-HANDOFF]
// ROLE: Plain-CLR owner of the one-window Settings lifecycle and UI-intent orchestration.
// TRUTH: Raw exception input is detected once; only one matching-type candidate reaches the mutation service.
// FROZEN: Persisted state is reloaded after synchronous mutations; degraded storage and transient status stay separate.
// THREADING: Production calls are dispatcher-serialized by UiBridge/WPF events; this type does not enforce affinity.
// DO_NOT: Cache preference truth, canonicalize in UI code, or add background persistence.
// NAVIGATE: PrivonAppUiBridge owns dispatch; the two Settings services own mutations.
internal sealed class SettingsCoordinator : IDisposable
{
    internal const string GenericFailureMessage = "요청을 처리할 수 없습니다. 다시 시도해 주세요.";
    internal const string NoCandidateMessage = "입력한 값을 인식할 수 없습니다. 값을 확인해 주세요.";
    internal const string MultipleCandidateMessage = "값을 하나만 입력해 주세요.";
    internal const string TypeMismatchMessage = "선택한 유형과 입력한 값이 일치하지 않습니다.";
    internal const string MasterKeyUnavailableBanner =
        "저장된 설정을 현재 사용할 수 없습니다.\n보호 기본값으로 동작 중입니다.";

    private readonly ProtectionCategorySettingsService _categoryService;
    private readonly UserExceptionService _exceptionService;
    private readonly DetectionPipeline _pipeline;
    private readonly Func<bool> _isMasterKeyUnavailable;
    private readonly Func<ISettingsSurface> _surfaceFactory;
    private readonly NativeMessagingHostRegistrationCoordinator _registrationCoordinator;

    // NO_LOCK (this Gate's own CONCURRENCY_CONTRACT instruction, frozen): every public member of
    // this type is reachable ONLY from the single WPF Dispatcher thread in production -- see class
    // doc's own CONCURRENCY paragraph -- so these two fields are read/written without a lock,
    // deliberately, matching the instruction's explicit "do NOT add a new synchronization lock"
    // (correctness comes from serialized dispatcher execution plus ONE_CURRENT_SETTINGS_WINDOW,
    // never a lock this type would otherwise need to invent evidence for).
    private ISettingsSurface? _currentSurface;
    private bool _disposed;

    public SettingsCoordinator(
        ProtectionCategorySettingsService categoryService,
        UserExceptionService exceptionService,
        DetectionPipeline pipeline,
        Func<bool> isMasterKeyUnavailable,
        Func<ISettingsSurface> surfaceFactory,
        NativeMessagingHostRegistrationCoordinator registrationCoordinator)
    {
        ArgumentNullException.ThrowIfNull(categoryService);
        ArgumentNullException.ThrowIfNull(exceptionService);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(isMasterKeyUnavailable);
        ArgumentNullException.ThrowIfNull(surfaceFactory);
        ArgumentNullException.ThrowIfNull(registrationCoordinator);
        _categoryService = categoryService;
        _exceptionService = exceptionService;
        _pipeline = pipeline;
        _isMasterKeyUnavailable = isMasterKeyUnavailable;
        _surfaceFactory = surfaceFactory;
        _registrationCoordinator = registrationCoordinator;
    }

    /// <summary>The tray's single Settings entry point. ONE_CURRENT_SETTINGS_WINDOW: activates the
    /// existing surface if one is already open; otherwise constructs a fresh one, wires it, renders
    /// authoritative state (REOPEN_RELOADS), and shows it.</summary>
    public void ShowRequested()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_currentSurface is { } existing)
        {
            existing.Activate();
            return;
        }

        var surface = _surfaceFactory();
        WireSurface(surface);
        _currentSurface = surface;

        RenderCurrentState(surface);
        surface.Show();
    }

    private void WireSurface(ISettingsSurface surface)
    {
        surface.Closed += (_, _) => OnSurfaceClosed(surface);
        surface.PhoneToggleRequested += (_, desired) => HandleMutation(surface, () => _categoryService.SetPhoneEnabled(desired));
        surface.EmailToggleRequested += (_, desired) => HandleMutation(surface, () => _categoryService.SetEmailEnabled(desired));
        surface.ResetProtectionScopeRequested += (_, _) => HandleMutation(surface, () => _categoryService.Reset());
        surface.ResetExceptionsRequested += (_, _) => HandleMutation(surface, () => _exceptionService.Reset());
        surface.DeleteExceptionRequested += (_, value) =>
            HandleMutation(surface, () => _exceptionService.Delete(value.PiiType, value.CanonicalValue));
        surface.AddExceptionRequested += (_, request) => HandleAddException(surface, request);
        surface.ChromeNativeMessagingProvisionRequested += (_, _) => HandleChromeMutation(surface, ProvisionChrome);
        surface.ChromeNativeMessagingRepairRequested += (_, _) => HandleChromeMutation(surface, RepairChrome);
        surface.EdgeNativeMessagingProvisionRequested += (_, _) => HandleEdgeMutation(surface, ProvisionEdge);
        surface.EdgeNativeMessagingRepairRequested += (_, _) => HandleEdgeMutation(surface, RepairEdge);
    }

    private void OnSurfaceClosed(ISettingsSurface surface)
    {
        if (ReferenceEquals(_currentSurface, surface))
        {
            _currentSurface = null;
        }
    }

    // IMMEDIATE_SAVE / RELOAD_AFTER_WRITE -- see class doc. A mutation failure (including
    // MASTER_KEY_UNAVAILABLE's own thrown InvalidOperationException) is caught here, never
    // rethrown -- the surface must never be left showing a state the underlying attempt did not
    // actually achieve, but this coordinator's own Dispatcher-thread caller (a real WPF event
    // handler) must also never see an unhandled exception escape a UI callback.
    private void HandleMutation(ISettingsSurface surface, Action mutate)
    {
        string? statusMessage = null;
        try
        {
            mutate();
        }
        catch
        {
            statusMessage = GenericFailureMessage;
        }

        RenderCurrentState(surface, statusMessage);
    }

    // COMMANDER CORRECTION (Gate E5G.1C) -- FAILED_ACTION_MUST_SURFACE: unlike every other mutation
    // above, an explicit Chrome Provision/Repair click cannot use the generic HandleMutation +
    // InspectChrome() re-read pair -- NativeMessagingHostRegistrationCoordinator.Provision/Repair's
    // own return value is FROZEN authority for what THAT attempt actually did (their own class doc:
    // "a non-throwing Install call is never itself treated as success"), and a SEPARATE, independent
    // Inspect() call afterward can legitimately observe a DIFFERENT state (e.g. a failed write that
    // left zero trace re-Inspects as Fresh, not Failed) -- discarding the action's own outcome and
    // substituting that independent observation would silently misrepresent a real failure as
    // "nothing happened" or "still needs repair," never as the failure it was. This method therefore
    // takes the mutation's OWN readiness return value and threads it directly into this one render
    // as an override -- it never calls InspectChrome() a second time for this render. The underlying
    // ownership contract itself is never touched by this: the very next unrelated render (reopen,
    // another toggle) calls RenderCurrentState with no override and InspectChrome() resumes being
    // the sole source of truth, exactly as before.
    private void HandleChromeMutation(ISettingsSurface surface, Func<NativeMessagingRegistrationReadiness> mutate)
    {
        NativeMessagingRegistrationReadiness result;
        try
        {
            result = mutate();
        }
        catch
        {
            result = NativeMessagingRegistrationReadiness.Failed;
        }

        RenderCurrentState(surface, statusMessage: null, chromeReadinessOverride: result);
    }

    // PRIVON 0.3.1 Gate E5G.1G -- the exact Edge counterpart of HandleChromeMutation, same reasoning:
    // Provision()/Repair()'s own return value is the authority for what THAT click did, threaded
    // directly into this one render as an override rather than discarded in favor of a second,
    // independent InspectEdge() read.
    private void HandleEdgeMutation(ISettingsSurface surface, Func<NativeMessagingRegistrationReadiness> mutate)
    {
        NativeMessagingRegistrationReadiness result;
        try
        {
            result = mutate();
        }
        catch
        {
            result = NativeMessagingRegistrationReadiness.Failed;
        }

        RenderCurrentState(surface, statusMessage: null, edgeReadinessOverride: result);
    }

    // CANONICALIZATION -- see class doc. Every rejection path below reloads authoritative state
    // alongside its own distinct, generic, non-content-bearing status message -- request.RawValue
    // itself is never placed into any message.
    private void HandleAddException(ISettingsSurface surface, AddExceptionRequest request)
    {
        DetectionResult detectionResult;
        try
        {
            detectionResult = _pipeline.Detect(request.RawValue);
        }
        catch
        {
            RenderCurrentState(surface, GenericFailureMessage);
            return;
        }

        if (detectionResult.Candidates.Count == 0)
        {
            RenderCurrentState(surface, NoCandidateMessage);
            return;
        }

        if (detectionResult.Candidates.Count > 1)
        {
            RenderCurrentState(surface, MultipleCandidateMessage);
            return;
        }

        var candidate = detectionResult.Candidates[0];
        if (candidate.PiiType != request.SelectedType)
        {
            RenderCurrentState(surface, TypeMismatchMessage);
            return;
        }

        HandleMutation(surface, () => _exceptionService.Add(candidate.PiiType, candidate.Canonical));
    }

    // PRIVON v0.2.1 Gate 3C DEGRADED_STORAGE_STATUS_CORRECTION: StatusMessage carries ONLY the
    // transient action/mutation-feedback fact -- it NEVER absorbs or synthesizes the degraded-
    // storage banner text as a fallback (an earlier version of this method did exactly that via
    // `statusMessage ?? (unavailable ? MasterKeyUnavailableBanner : null)`, which is precisely what
    // let a subsequent mutation-failure StatusMessage silently erase the degraded-storage
    // explanation -- see this Gate's own audit-correction instruction). MasterKeyUnavailable
    // remains the SOLE authoritative degraded-storage signal; a real ISettingsSurface renders both
    // facts as independent, simultaneously-visible presentation concerns -- see SettingsWindow's
    // own RenderState.
    // PRIVON 0.3.1 Gate E5G.1C, corrected (Gate E5G.1G extends the identical reasoning to Edge):
    // <paramref name="chromeReadinessOverride"/>/<paramref name="edgeReadinessOverride"/> exist ONLY
    // for HandleChromeMutation/HandleEdgeMutation's own use -- every other caller (ShowRequested,
    // every non-browser HandleMutation/HandleAddException path) omits both, so each browser's
    // readiness continues to come from the browser-specific Inspect method. Those methods first
    // enforce ReleaseBrowserSupportPolicy; only a browser supported by this release reaches the
    // registration coordinator's read-only Inspect call.
    private void RenderCurrentState(
        ISettingsSurface surface,
        string? statusMessage = null,
        NativeMessagingRegistrationReadiness? chromeReadinessOverride = null,
        NativeMessagingRegistrationReadiness? edgeReadinessOverride = null)
    {
        var categories = _categoryService.Load();
        var exceptions = _exceptionService.List();
        var unavailable = _isMasterKeyUnavailable();

        surface.RenderState(new SettingsViewState(
            PhoneEnabled: categories.PhoneEnabled,
            EmailEnabled: categories.EmailEnabled,
            Exceptions: exceptions,
            MasterKeyUnavailable: unavailable,
            StatusMessage: statusMessage,
            ChromeNativeMessagingReadiness: chromeReadinessOverride ?? InspectChrome(),
            EdgeNativeMessagingReadiness: edgeReadinessOverride ?? InspectEdge()));
    }

    // PRIVON 0.3.1 Gate E5G.1C -- CHROME_PROVISIONING: the verified Chrome identity is resolved
    // exclusively through VerifiedBrowserExtensionIdentities (never invented/guessed here), and every
    // readiness/mutation call is routed through the single ALREADY-OWNED
    // NativeMessagingHostRegistrationCoordinator this type was constructed with -- never a second,
    // independently-constructed registrar/environment/coordinator instance. InspectChrome is
    // read-only (called on every render, including on open) and safe to run unconditionally;
    // ProvisionChrome/RepairChrome are reachable ONLY from their own explicit WireSurface
    // subscriptions above, never from RenderCurrentState/ShowRequested itself.
    private NativeMessagingRegistrationReadiness InspectChrome()
    {
        if (!ReleaseBrowserSupportPolicy.IsSupported(NativeMessagingBrowser.Chrome))
            return NativeMessagingRegistrationReadiness.Failed;
        if (!VerifiedBrowserExtensionIdentities.TryGet(NativeMessagingBrowser.Chrome, out var identity))
            return NativeMessagingRegistrationReadiness.Failed;

        return _registrationCoordinator.Inspect(identity.Browser, identity.NativeMessagingOrigin);
    }

    // Returns the coordinator's OWN Provision() outcome -- see HandleChromeMutation's own doc for
    // why that exact value, not a subsequent independent Inspect(), is what gets displayed.
    private NativeMessagingRegistrationReadiness ProvisionChrome()
    {
        if (!ReleaseBrowserSupportPolicy.IsSupported(NativeMessagingBrowser.Chrome))
            return NativeMessagingRegistrationReadiness.Failed;
        if (!VerifiedBrowserExtensionIdentities.TryGet(NativeMessagingBrowser.Chrome, out var identity))
            return NativeMessagingRegistrationReadiness.Failed;

        return _registrationCoordinator.Provision(identity.Browser, identity.NativeMessagingOrigin);
    }

    // Returns the coordinator's OWN Repair() outcome -- same reasoning as ProvisionChrome.
    private NativeMessagingRegistrationReadiness RepairChrome()
    {
        if (!ReleaseBrowserSupportPolicy.IsSupported(NativeMessagingBrowser.Chrome))
            return NativeMessagingRegistrationReadiness.Failed;
        if (!VerifiedBrowserExtensionIdentities.TryGet(NativeMessagingBrowser.Chrome, out var identity))
            return NativeMessagingRegistrationReadiness.Failed;

        return _registrationCoordinator.Repair(identity.Browser, identity.NativeMessagingOrigin);
    }

    // PRIVON 0.3.1 Chrome-only remediation: Edge's identity and registration implementation remain
    // intact below for future reactivation, but the shared release policy fails closed before any
    // read or mutation reaches that implementation in 0.3.1. Chrome and Edge remain mechanically
    // independent -- each reachable coordinator call supplies only its own browser axis.
    private NativeMessagingRegistrationReadiness InspectEdge()
    {
        if (!ReleaseBrowserSupportPolicy.IsSupported(NativeMessagingBrowser.Edge))
            return NativeMessagingRegistrationReadiness.Failed;
        if (!VerifiedBrowserExtensionIdentities.TryGet(NativeMessagingBrowser.Edge, out var identity))
            return NativeMessagingRegistrationReadiness.Failed;

        return _registrationCoordinator.Inspect(identity.Browser, identity.NativeMessagingOrigin);
    }

    private NativeMessagingRegistrationReadiness ProvisionEdge()
    {
        if (!ReleaseBrowserSupportPolicy.IsSupported(NativeMessagingBrowser.Edge))
            return NativeMessagingRegistrationReadiness.Failed;
        if (!VerifiedBrowserExtensionIdentities.TryGet(NativeMessagingBrowser.Edge, out var identity))
            return NativeMessagingRegistrationReadiness.Failed;

        return _registrationCoordinator.Provision(identity.Browser, identity.NativeMessagingOrigin);
    }

    private NativeMessagingRegistrationReadiness RepairEdge()
    {
        if (!ReleaseBrowserSupportPolicy.IsSupported(NativeMessagingBrowser.Edge))
            return NativeMessagingRegistrationReadiness.Failed;
        if (!VerifiedBrowserExtensionIdentities.TryGet(NativeMessagingBrowser.Edge, out var identity))
            return NativeMessagingRegistrationReadiness.Failed;

        return _registrationCoordinator.Repair(identity.Browser, identity.NativeMessagingOrigin);
    }

    /// <summary>Idempotent. Closes whatever surface is currently tracked, if any -- ensures no
    /// Settings window remains open once application shutdown proceeds
    /// (SETTINGS_CLOSED_BEFORE_UI_BRIDGE_TEARDOWN -- see <see cref="PrivonAppUiBridge.Dispose"/>).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var surface = _currentSurface;
        _currentSurface = null;
        surface?.Close();
    }
}
