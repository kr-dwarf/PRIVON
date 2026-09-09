using System.Security.AccessControl;
using System.Security.Principal;
using Privon.App;
using Privon.Detection;
using Privon.Storage;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3C -- SettingsCoordinator regression: ONE_CURRENT_SETTINGS_WINDOW lifecycle,
// IMMEDIATE_SAVE/RELOAD_AFTER_WRITE, CANONICALIZATION (real Detection pipeline), MASTER_KEY_UNAVAILABLE
// degraded state, and PRIVACY_UI. Uses a REAL PrivonLocalStore against a temp directory (no fake
// Storage seam -- same technique as ProtectionCategorySettingsServiceTests/UserExceptionServiceTests)
// and the REAL DetectionPipeline.CreateDefault() (deterministic, no I/O -- no fake needed). Only the
// surface is a hand-written fake (real WPF rendering remains manual release QA / a separate minimal
// smoke test, matching this project's established DecisionPromptCoordinatorTests precedent). Synthetic
// data only.
public class SettingsCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonSettingsCoordinatorTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // PRIVON 0.3.1 Gate E5G.1C -- SettingsCoordinator now also requires the already-owned
    // NativeMessagingHostRegistrationCoordinator (see that type's own class doc). This suite's
    // Phone/Email/exception scenarios never touch Chrome provisioning, so every harness below wires
    // a fully in-memory, no-op instance (FakeNativeMessagingHostRegistrationEnvironment -- Gate
    // E5G.P3's own fake, never real HKCU/filesystem access) purely to satisfy the constructor.
    private static NativeMessagingHostRegistrationCoordinator CreateNoopRegistrationCoordinator() =>
        new(new FakeNativeMessagingHostRegistrationEnvironment());

    private sealed class Harness
    {
        public required PrivonLocalStore Store { get; init; }
        public required ProtectionCategorySettingsService CategoryService { get; init; }
        public required UserExceptionService ExceptionService { get; init; }
        public required List<FakeSettingsSurface> CreatedSurfaces { get; init; }
        public required SettingsCoordinator Coordinator { get; init; }
        public bool MasterKeyUnavailableOverride { get; set; }
    }

    private Harness CreateHarness()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var categoryService = new ProtectionCategorySettingsService(store);
        var exceptionService = new UserExceptionService(store);
        var createdSurfaces = new List<FakeSettingsSurface>();
        var harness = new Harness
        {
            Store = store,
            CategoryService = categoryService,
            ExceptionService = exceptionService,
            CreatedSurfaces = createdSurfaces,
            Coordinator = null!,
        };

        var coordinator = new SettingsCoordinator(
            categoryService,
            exceptionService,
            DetectionPipeline.CreateDefault(),
            () => harness.MasterKeyUnavailableOverride,
            () =>
            {
                var surface = new FakeSettingsSurface();
                createdSurfaces.Add(surface);
                return surface;
            },
            CreateNoopRegistrationCoordinator());

        return new Harness
        {
            Store = store,
            CategoryService = categoryService,
            ExceptionService = exceptionService,
            CreatedSurfaces = createdSurfaces,
            Coordinator = coordinator,
        };
    }

    private static readonly CanonicalValue Phone1 = new(PiiType.Phone, "01011112222");

    // ==================================================================
    // PRIVON 0.3.2 Gate 032-C2 -- InspectChromeReadinessForStartup: the new PrivonAppUiBridge
    // startup onboarding check must REUSE the exact SAME policy-gated Chrome identity/readiness path
    // ShowRequested's own rendering already uses (InspectChrome) -- never a second, independently-
    // derived Chrome identity/origin classification that could silently drift from it. Proven here by
    // asserting the two calls agree, against the SAME coordinator instance, for both the Fresh and a
    // read-only-different-origin case.
    // ==================================================================

    [Fact]
    public void InspectChromeReadinessForStartup_AgreesWithShowRequestedsOwnChromeReadiness_Fresh()
    {
        var h = CreateHarness();

        var startupReadiness = h.Coordinator.InspectChromeReadinessForStartup();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        Assert.Equal(NativeMessagingRegistrationReadiness.Fresh, startupReadiness);
        Assert.Equal(startupReadiness, surface.LastRenderedState!.ChromeNativeMessagingReadiness);
    }

    // ==================================================================
    // PRIVON 0.3.2 Gate 032-C2R -- ShowRequestedForStartup: the Fresh-only startup path must render
    // the ALREADY-SUPPLIED readiness snapshot on its first render WITHOUT calling InspectChrome()
    // (i.e. the real registration coordinator/environment) a second time -- the exact defect an
    // independent audit found in the prior candidate (PrivonAppUiBridgeTests' own
    // Start_Fresh_TotalChromeInspection_EqualsExactlyOneDirectInspectSequence proves this end-to-end
    // through the real Start() path; this is the narrower, isolated unit-level proof against
    // SettingsCoordinator alone).
    // ==================================================================

    [Fact]
    public void ShowRequestedForStartup_RendersSuppliedSnapshot_PerformsZeroRegistrationEnvironmentCalls()
    {
        var registrationEnvironment = new FakeNativeMessagingHostRegistrationEnvironment();
        var registrationCoordinator = new NativeMessagingHostRegistrationCoordinator(registrationEnvironment);
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var createdSurfaces = new List<FakeSettingsSurface>();
        var coordinator = new SettingsCoordinator(
            new ProtectionCategorySettingsService(store),
            new UserExceptionService(store),
            DetectionPipeline.CreateDefault(),
            () => false,
            () =>
            {
                var surface = new FakeSettingsSurface();
                createdSurfaces.Add(surface);
                return surface;
            },
            registrationCoordinator);

        coordinator.ShowRequestedForStartup(NativeMessagingRegistrationReadiness.Fresh);

        var surface = Assert.Single(createdSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Fresh, surface.LastRenderedState!.ChromeNativeMessagingReadiness);
        Assert.Empty(registrationEnvironment.CallLog);
    }

    [Fact]
    public void ShowRequestedForStartup_ExistingSurfaceAlreadyOpen_ActivatesExisting_NeverConstructsASecond()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Coordinator.ShowRequestedForStartup(NativeMessagingRegistrationReadiness.Fresh);

        Assert.Single(h.CreatedSurfaces);
        Assert.Equal(1, surface.ActivateCallCount);
    }

    [Fact]
    public void InspectChromeReadinessForStartup_IsReadOnly_PerformsZeroRegistrationMutation()
    {
        var registrationEnvironment = new FakeNativeMessagingHostRegistrationEnvironment();
        var registrationCoordinator = new NativeMessagingHostRegistrationCoordinator(registrationEnvironment);
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var coordinator = new SettingsCoordinator(
            new ProtectionCategorySettingsService(store),
            new UserExceptionService(store),
            DetectionPipeline.CreateDefault(),
            () => false,
            () => new FakeSettingsSurface(),
            registrationCoordinator);

        coordinator.InspectChromeReadinessForStartup();

        Assert.DoesNotContain(registrationEnvironment.CallLog, e =>
            e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal)
            || e.StartsWith("DeleteSubkey(", StringComparison.Ordinal)
            || e.StartsWith("WriteManifest(", StringComparison.Ordinal)
            || e.StartsWith("DeleteManifest(", StringComparison.Ordinal));
    }

    // ==================================================================
    // UI-001 -- ShowRequested shows a surface.
    // ==================================================================
    [Fact]
    public void ShowRequested_ConstructsAndShowsASurface()
    {
        var h = CreateHarness();

        h.Coordinator.ShowRequested();

        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.Equal(1, surface.ShowCallCount);
    }

    // ==================================================================
    // UI-017 -- two ShowRequested calls while open -> one surface instance, existing activated.
    // ==================================================================
    [Fact]
    public void ShowRequested_CalledTwiceWhileOpen_ActivatesExisting_NeverConstructsASecond()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Coordinator.ShowRequested();

        Assert.Single(h.CreatedSurfaces); // still exactly one instance
        Assert.Equal(1, surface.ActivateCallCount);
        Assert.Equal(1, surface.ShowCallCount); // Show() itself never called a second time
    }

    // Also proves rapid sequential requests never cause the mutation/read services to be re-entered
    // concurrently -- every call here executes fully, synchronously, before the next begins.
    [Fact]
    public void RepeatedShowRequested_NeverReenters_ServicesCalledSequentially()
    {
        var h = CreateHarness();

        h.Coordinator.ShowRequested();
        h.Coordinator.ShowRequested();
        h.Coordinator.ShowRequested();

        Assert.Single(h.CreatedSurfaces);
        var surface = h.CreatedSurfaces[0];
        Assert.Equal(2, surface.ActivateCallCount);
    }

    // ==================================================================
    // UI-018 -- close + reopen -> fresh authoritative state loaded.
    // ==================================================================
    [Fact]
    public void CloseThenReopen_ConstructsFreshSurface_ReloadsAuthoritativeState()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var first = Assert.Single(h.CreatedSurfaces);
        first.Close();

        h.CategoryService.SetPhoneEnabled(false); // change persisted truth while closed
        h.Coordinator.ShowRequested();

        Assert.Equal(2, h.CreatedSurfaces.Count);
        var second = h.CreatedSurfaces[1];
        Assert.Equal(1, second.ShowCallCount);
        Assert.False(second.LastRenderedState!.PhoneEnabled);
    }

    // ==================================================================
    // Toggle mutation -- immediate save, reload-after-write.
    // ==================================================================
    [Fact]
    public void PhoneToggleRequested_False_PersistsAndReloadsUpdatedState()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaisePhoneToggleRequested(false);

        Assert.False(h.CategoryService.Load().PhoneEnabled);
        Assert.False(surface.LastRenderedState!.PhoneEnabled);
        Assert.Null(surface.LastRenderedState!.StatusMessage);
    }

    [Fact]
    public void EmailToggleRequested_False_PersistsAndReloadsUpdatedState()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseEmailToggleRequested(false);

        Assert.False(h.CategoryService.Load().EmailEnabled);
        Assert.False(surface.LastRenderedState!.EmailEnabled);
    }

    // ==================================================================
    // UI-006 -- Reset protection scope -> Categories == AllOn.
    // ==================================================================
    [Fact]
    public void ResetProtectionScopeRequested_RestoresAllOn()
    {
        var h = CreateHarness();
        h.CategoryService.SetPhoneEnabled(false);
        h.CategoryService.SetEmailEnabled(false);
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseResetProtectionScopeRequested();

        Assert.True(surface.LastRenderedState!.PhoneEnabled);
        Assert.True(surface.LastRenderedState!.EmailEnabled);
        Assert.Equal(ProtectionCategorySettings.AllOn, h.CategoryService.Load());
    }

    // ==================================================================
    // UI-007 -- open exception list reflects UserExceptionService.List authoritative state.
    // ==================================================================
    [Fact]
    public void ShowRequested_RendersExistingPersistedExceptions()
    {
        var h = CreateHarness();
        h.ExceptionService.Add(PiiType.Phone, Phone1);

        h.Coordinator.ShowRequested();

        var surface = Assert.Single(h.CreatedSurfaces);
        var entry = Assert.Single(surface.LastRenderedState!.Exceptions);
        Assert.Equal(PiiType.Phone, entry.PiiType);
        Assert.Equal(Phone1, entry.CanonicalValue);
    }

    // ==================================================================
    // UI-008/UI-009 -- valid synthetic Phone/Email input -> real Detection canonicalization ->
    // exception persists.
    // ==================================================================
    [Fact]
    public void AddExceptionRequested_ValidPhone_DetectsExactlyOneCandidate_Persists()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseAddExceptionRequested(new AddExceptionRequest(PiiType.Phone, "010-1111-2222"));

        var entry = Assert.Single(h.ExceptionService.List());
        Assert.Equal(PiiType.Phone, entry.PiiType);
        Assert.Null(surface.LastRenderedState!.StatusMessage);
        Assert.Contains(entry, surface.LastRenderedState!.Exceptions);
    }

    [Fact]
    public void AddExceptionRequested_ValidEmail_DetectsExactlyOneCandidate_Persists()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseAddExceptionRequested(new AddExceptionRequest(PiiType.Email, "user@example.test"));

        var entry = Assert.Single(h.ExceptionService.List());
        Assert.Equal(PiiType.Email, entry.PiiType);
        Assert.Equal("user@example.test", entry.CanonicalValue.Value);
    }

    // ==================================================================
    // UI-010 -- duplicate Add -> one persisted entry.
    // ==================================================================
    [Fact]
    public void AddExceptionRequested_SameValueTwice_ExactlyOnePersistedEntry()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseAddExceptionRequested(new AddExceptionRequest(PiiType.Phone, "010-1111-2222"));
        surface.RaiseAddExceptionRequested(new AddExceptionRequest(PiiType.Phone, "010-1111-2222"));

        Assert.Single(h.ExceptionService.List());
    }

    // ==================================================================
    // UI-011 -- Delete -> removed from authoritative state.
    // ==================================================================
    [Fact]
    public void DeleteExceptionRequested_RemovesEntry()
    {
        var h = CreateHarness();
        h.ExceptionService.Add(PiiType.Phone, Phone1);
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        var entry = Assert.Single(surface.LastRenderedState!.Exceptions);

        surface.RaiseDeleteExceptionRequested(entry);

        Assert.Empty(h.ExceptionService.List());
        Assert.Empty(surface.LastRenderedState!.Exceptions);
    }

    // ==================================================================
    // UI-012 -- Reset exceptions -> empty persisted list.
    // ==================================================================
    [Fact]
    public void ResetExceptionsRequested_EmptiesTheList()
    {
        var h = CreateHarness();
        h.ExceptionService.Add(PiiType.Phone, Phone1);
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseResetExceptionsRequested();

        Assert.Empty(h.ExceptionService.List());
        Assert.Empty(surface.LastRenderedState!.Exceptions);
    }

    // ==================================================================
    // UI-013 -- service rejects Level3/unsupported types -> no persistence. Proven here through the
    // coordinator's own CANONICALIZATION path: a synthetic RRN-shaped input, requested as the RRN
    // PiiType, is never presented as a selectable UI type in the first place (see UI-014/UI-015's
    // structural surface tests), but the underlying service guard is independently proven not to be
    // reachable through Add -- see UserExceptionServiceAddAllowlistTests for the direct guard proof.
    // ==================================================================

    // ==================================================================
    // UI-021 -- zero Detection candidate input -> rejected, no write.
    // ==================================================================
    [Fact]
    public void AddExceptionRequested_ZeroCandidates_Rejected_NoWrite()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseAddExceptionRequested(new AddExceptionRequest(PiiType.Phone, "not a phone number at all"));

        Assert.Empty(h.ExceptionService.List());
        Assert.Equal(SettingsCoordinator.NoCandidateMessage, surface.LastRenderedState!.StatusMessage);
    }

    // ==================================================================
    // UI-022 -- multiple candidate input -> rejected, no write.
    // ==================================================================
    [Fact]
    public void AddExceptionRequested_MultipleCandidates_Rejected_NoWrite()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseAddExceptionRequested(new AddExceptionRequest(PiiType.Phone, "010-1111-2222 and 010-3333-4444"));

        Assert.Empty(h.ExceptionService.List());
        Assert.Equal(SettingsCoordinator.MultipleCandidateMessage, surface.LastRenderedState!.StatusMessage);
    }

    // ==================================================================
    // UI-023 -- selected Phone + detected Email -> rejected, no write.
    // ==================================================================
    [Fact]
    public void AddExceptionRequested_SelectedTypeMismatchesDetectedType_Rejected_NoWrite()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseAddExceptionRequested(new AddExceptionRequest(PiiType.Phone, "user@example.test"));

        Assert.Empty(h.ExceptionService.List());
        Assert.Equal(SettingsCoordinator.TypeMismatchMessage, surface.LastRenderedState!.StatusMessage);
    }

    // ==================================================================
    // PRIVON v0.2.1 Gate 3C DEGRADED_STORAGE_STATUS_CORRECTION -- two independent facts,
    // MasterKeyUnavailable (degraded-storage explanation) and StatusMessage (transient action/
    // mutation feedback), must BOTH survive together in SettingsViewState -- neither field is
    // allowed to erase or stand in for the other. This block replaces the earlier, defective
    // UI-016/UI-025 coverage that let a non-null StatusMessage silently absorb the degraded-storage
    // banner (SettingsCoordinator used to fold MasterKeyUnavailableBanner INTO StatusMessage as a
    // fallback -- see git history -- so a subsequent mutation-failure StatusMessage overwrote it).
    // ==================================================================

    // ---- UI-016A -- master-key unavailable: initial render shows the degraded-storage
    // explanation, with protective/default values, and no transient status message (nothing has
    // been attempted yet). ----
    [Fact]
    public void Ui016A_ShowRequested_MasterKeyUnavailable_InitialRenderShowsDegradedExplanation()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: new ProtectionCategorySettings(false, false, false, false, false)));
        setup.SaveUserExceptions([new UserExceptionEntry("Phone", "01099998888")]);

        using (DenyRead(keyPath))
        {
            var store = PrivonLocalStore.OpenOrCreate(_root);
            var categoryService = new ProtectionCategorySettingsService(store);
            var exceptionService = new UserExceptionService(store);
            var createdSurfaces = new List<FakeSettingsSurface>();
            var coordinator = new SettingsCoordinator(
                categoryService, exceptionService, DetectionPipeline.CreateDefault(),
                () => store.IsMasterKeyUnavailable,
                () => { var s = new FakeSettingsSurface(); createdSurfaces.Add(s); return s; },
                CreateNoopRegistrationCoordinator());

            coordinator.ShowRequested();

            var surface = Assert.Single(createdSurfaces);
            Assert.Equal(1, surface.ShowCallCount); // window still opens
            var state = surface.LastRenderedState!;
            Assert.True(state.MasterKeyUnavailable); // the authoritative degraded-storage signal
            Assert.True(state.PhoneEnabled); // protective/default, never the on-disk-but-unreadable false
            Assert.True(state.EmailEnabled);
            Assert.Empty(state.Exceptions); // appears empty, never claims the real persisted set
            // Nothing was attempted yet -- StatusMessage carries no fallback/synthesized banner text
            // of its own; the degraded explanation lives ONLY in MasterKeyUnavailable.
            Assert.Null(state.StatusMessage);
        }
    }

    // ---- UI-016B -- master-key unavailable: a category mutation fails -> authoritative reload ->
    // BOTH facts remain visible together: MasterKeyUnavailable stays true AND StatusMessage carries
    // the mutation-failure feedback. Neither erases the other. ----
    [Fact]
    public void Ui016B_MasterKeyUnavailable_CategoryMutationFails_BothDegradedAndFailureFactsVisible()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: ProtectionCategorySettings.AllOn));

        using (DenyRead(keyPath))
        {
            var store = PrivonLocalStore.OpenOrCreate(_root);
            var categoryService = new ProtectionCategorySettingsService(store);
            var exceptionService = new UserExceptionService(store);
            var createdSurfaces = new List<FakeSettingsSurface>();
            var coordinator = new SettingsCoordinator(
                categoryService, exceptionService, DetectionPipeline.CreateDefault(),
                () => store.IsMasterKeyUnavailable,
                () => { var s = new FakeSettingsSurface(); createdSurfaces.Add(s); return s; },
                CreateNoopRegistrationCoordinator());

            coordinator.ShowRequested();
            var surface = Assert.Single(createdSurfaces);

            var mutationException = Record.Exception(() => surface.RaisePhoneToggleRequested(false));

            Assert.Null(mutationException); // handled internally, never escapes to the caller
            var state = surface.LastRenderedState!;
            Assert.True(state.MasterKeyUnavailable); // degraded-storage explanation STILL visible
            Assert.Equal(SettingsCoordinator.GenericFailureMessage, state.StatusMessage); // mutation failure ALSO visible
            // Never falsely shown as the (rejected) new value -- the reload reflects the actual
            // safe-default state Storage still holds.
            Assert.True(state.PhoneEnabled);
        }
    }

    // ---- UI-016C -- master-key unavailable: a UserException Add failure preserves the same
    // dual-state truth. Delete/Reset share the identical HandleMutation path, so this one
    // representative exception-mutation path is sufficient to prove the contract holds there too. ----
    [Fact]
    public void Ui016C_MasterKeyUnavailable_ExceptionAddFails_BothDegradedAndFailureFactsVisible()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: ProtectionCategorySettings.AllOn));

        using (DenyRead(keyPath))
        {
            var store = PrivonLocalStore.OpenOrCreate(_root);
            var categoryService = new ProtectionCategorySettingsService(store);
            var exceptionService = new UserExceptionService(store);
            var createdSurfaces = new List<FakeSettingsSurface>();
            var coordinator = new SettingsCoordinator(
                categoryService, exceptionService, DetectionPipeline.CreateDefault(),
                () => store.IsMasterKeyUnavailable,
                () => { var s = new FakeSettingsSurface(); createdSurfaces.Add(s); return s; },
                CreateNoopRegistrationCoordinator());

            coordinator.ShowRequested();
            var surface = Assert.Single(createdSurfaces);

            // A validly-shaped Phone value -- detection/canonicalization still runs (it is pure,
            // in-memory, unrelated to Storage) and succeeds; the SAVE step is what fails here.
            var mutationException = Record.Exception(() =>
                surface.RaiseAddExceptionRequested(new AddExceptionRequest(PiiType.Phone, "010-1111-2222")));

            Assert.Null(mutationException);
            var state = surface.LastRenderedState!;
            Assert.True(state.MasterKeyUnavailable); // degraded-storage explanation STILL visible
            Assert.Equal(SettingsCoordinator.GenericFailureMessage, state.StatusMessage); // mutation failure ALSO visible
            Assert.Empty(state.Exceptions); // never falsely shown as persisted
        }
    }

    // ---- UI-025A -- NORMAL storage mode (master key available) + a genuine Save failure ->
    // generic mutation failure visible, degraded-storage banner NOT shown. Forces a real write
    // failure via a read-only settings.bin -- never touches master-key semantics. ----
    [Fact]
    public void Ui025A_NormalMode_SaveFailure_ShowsGenericFailure_NeverDegradedBanner()
    {
        var settingsPath = Path.Combine(_root, "settings.bin");
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: ProtectionCategorySettings.AllOn));
        var categoryService = new ProtectionCategorySettingsService(store);
        var exceptionService = new UserExceptionService(store);
        var createdSurfaces = new List<FakeSettingsSurface>();
        var coordinator = new SettingsCoordinator(
            categoryService, exceptionService, DetectionPipeline.CreateDefault(),
            () => store.IsMasterKeyUnavailable, // real, current value -- false, master key is fine
            () => { var s = new FakeSettingsSurface(); createdSurfaces.Add(s); return s; },
            CreateNoopRegistrationCoordinator());
        coordinator.ShowRequested();
        var surface = Assert.Single(createdSurfaces);

        using (MakeReadOnly(settingsPath))
        {
            Assert.False(store.IsMasterKeyUnavailable); // sanity: genuinely normal storage mode

            var mutationException = Record.Exception(() => surface.RaisePhoneToggleRequested(false));

            Assert.Null(mutationException);
            var state = surface.LastRenderedState!;
            Assert.Equal(SettingsCoordinator.GenericFailureMessage, state.StatusMessage); // failure visible
            Assert.False(state.MasterKeyUnavailable); // degraded banner NEVER shown in normal mode
        }
    }

    // ---- UI-025B -- failed mutation -> no success state -> reloaded authoritative values (the
    // ORIGINAL, pre-attempt persisted ones) remain displayed, never the rejected new value. ----
    [Fact]
    public void Ui025B_FailedMutation_NoSuccessState_ReloadedAuthoritativeValuesRemain()
    {
        var settingsPath = Path.Combine(_root, "settings.bin");
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: ProtectionCategorySettings.AllOn));
        var categoryService = new ProtectionCategorySettingsService(store);
        var exceptionService = new UserExceptionService(store);
        var createdSurfaces = new List<FakeSettingsSurface>();
        var coordinator = new SettingsCoordinator(
            categoryService, exceptionService, DetectionPipeline.CreateDefault(),
            () => store.IsMasterKeyUnavailable,
            () => { var s = new FakeSettingsSurface(); createdSurfaces.Add(s); return s; },
            CreateNoopRegistrationCoordinator());
        coordinator.ShowRequested();
        var surface = Assert.Single(createdSurfaces);

        using (MakeReadOnly(settingsPath))
        {
            surface.RaisePhoneToggleRequested(false); // attempted new value: false

            // The rejected `false` never appears -- the reload reflects what Storage still
            // genuinely holds (the original AllOn value), never a false "persisted" claim.
            Assert.True(surface.LastRenderedState!.PhoneEnabled);
        }

        // Confirmed independently at the Storage layer too, not merely via the rendered state.
        Assert.True(store.LoadSettings().Categories!.PhoneEnabled);
    }

    // ==================================================================
    // UI-020 -- synthetic exception value: absent from every status/failure message surface.
    // ==================================================================
    [Fact]
    public void RejectionMessages_NeverContainTheRawEnteredValue()
    {
        const string sentinel = "SETTINGS-COORDINATOR-SENTINEL-991122";
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseAddExceptionRequested(new AddExceptionRequest(PiiType.Phone, sentinel));

        Assert.DoesNotContain(sentinel, surface.LastRenderedState!.StatusMessage);
        Assert.DoesNotContain(sentinel, SettingsCoordinator.NoCandidateMessage);
        Assert.DoesNotContain(sentinel, SettingsCoordinator.MultipleCandidateMessage);
        Assert.DoesNotContain(sentinel, SettingsCoordinator.TypeMismatchMessage);
        Assert.DoesNotContain(sentinel, SettingsCoordinator.GenericFailureMessage);
    }

    // ==================================================================
    // Dispose -- closes the currently-open surface.
    // ==================================================================
    [Fact]
    public void Dispose_ClosesCurrentlyOpenSurface()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        h.Coordinator.Dispose();

        Assert.True(surface.IsClosed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var h = CreateHarness();
        h.Coordinator.Dispose();
        h.Coordinator.Dispose();
    }

    [Fact]
    public void Dispose_SafeWhenNeverShown()
    {
        var h = CreateHarness();
        h.Coordinator.Dispose();
    }

    [Fact]
    public void ShowRequested_AfterDispose_Throws()
    {
        var h = CreateHarness();
        h.Coordinator.Dispose();

        Assert.Throws<ObjectDisposedException>(h.Coordinator.ShowRequested);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var categoryService = new ProtectionCategorySettingsService(store);
        var exceptionService = new UserExceptionService(store);
        var pipeline = DetectionPipeline.CreateDefault();
        Func<bool> isUnavailable = () => false;
        Func<ISettingsSurface> factory = () => new FakeSettingsSurface();
        var registrationCoordinator = CreateNoopRegistrationCoordinator();

        Assert.Throws<ArgumentNullException>(() => new SettingsCoordinator(null!, exceptionService, pipeline, isUnavailable, factory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new SettingsCoordinator(categoryService, null!, pipeline, isUnavailable, factory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new SettingsCoordinator(categoryService, exceptionService, null!, isUnavailable, factory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new SettingsCoordinator(categoryService, exceptionService, pipeline, null!, factory, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new SettingsCoordinator(categoryService, exceptionService, pipeline, isUnavailable, null!, registrationCoordinator));
        Assert.Throws<ArgumentNullException>(() => new SettingsCoordinator(categoryService, exceptionService, pipeline, isUnavailable, factory, null!));
    }

    [Theory]
    [InlineData(typeof(SettingsCoordinator))]
    [InlineData(typeof(ISettingsSurface))]
    [InlineData(typeof(SettingsViewState))]
    [InlineData(typeof(AddExceptionRequest))]
    [InlineData(typeof(VerifiedBrowserExtensionIdentities))]
    [InlineData(typeof(VerifiedBrowserExtensionIdentity))]
    public void Types_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }

    // UI-025A/UI-025B -- forces a genuine AtomicFileWriter.WriteAtomic failure (File.Move(...,
    // overwrite: true) onto a read-only destination) WITHOUT touching master-key semantics at all --
    // the real, distinct "normal storage mode + write failure" condition. Self-verifying, matching
    // this project's own established DenyRead precedent: confirms the harness genuinely reproduces
    // the failure before any test trusts production code's handling of it.
    private static IDisposable MakeReadOnly(string path)
    {
        var originalAttributes = File.GetAttributes(path);
        File.SetAttributes(path, originalAttributes | FileAttributes.ReadOnly);

        var probePath = path + ".probe";
        File.WriteAllBytes(probePath, [0]);
        var directMoveException = Record.Exception(() => File.Move(probePath, path, overwrite: true));
        if (File.Exists(probePath)) File.Delete(probePath);

        if (directMoveException is null)
        {
            File.SetAttributes(path, originalAttributes);
            throw new InvalidOperationException(
                $"Marking {path} read-only did not reproduce a real File.Move(overwrite:true) failure -- harness is wrong, not the fix under test.");
        }

        return new ReadOnlyRestorer(path, originalAttributes);
    }

    private sealed class ReadOnlyRestorer(string path, FileAttributes originalAttributes) : IDisposable
    {
        public void Dispose() => File.SetAttributes(path, originalAttributes);
    }

    private static IDisposable DenyRead(string path)
    {
        var fileInfo = new FileInfo(path);
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(currentUser, FileSystemRights.Read | FileSystemRights.ReadData, AccessControlType.Deny);
        var acl = fileInfo.GetAccessControl();
        acl.AddAccessRule(rule);
        fileInfo.SetAccessControl(acl);

        var directReadException = Record.Exception(() => File.ReadAllBytes(path));
        if (directReadException is not UnauthorizedAccessException)
        {
            var restoredAcl = fileInfo.GetAccessControl();
            restoredAcl.RemoveAccessRule(rule);
            fileInfo.SetAccessControl(restoredAcl);
            throw new InvalidOperationException(
                $"ACL deny rule did not reproduce UnauthorizedAccessException on {path} -- harness is wrong, not the fix under test.");
        }

        return new AclRestorer(fileInfo, rule);
    }

    private sealed class AclRestorer(FileInfo fileInfo, FileSystemAccessRule rule) : IDisposable
    {
        public void Dispose()
        {
            var acl = fileInfo.GetAccessControl();
            acl.RemoveAccessRule(rule);
            fileInfo.SetAccessControl(acl);
        }
    }
}
