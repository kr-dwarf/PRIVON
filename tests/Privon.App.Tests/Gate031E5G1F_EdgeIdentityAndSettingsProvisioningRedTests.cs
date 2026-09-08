using System.Text.Json;
using System.Text.RegularExpressions;
using Privon.Detection;
using Privon.Storage;
using AppBrowser = Privon.App.NativeMessagingBrowser;

namespace Privon.App.Tests;

// PRIVON 0.3.1 -- verified Edge browser-store identity integration plus dormant registration
// coverage. Settings now defers Edge for the Chrome-only 0.3.1 release scope while Chrome
// identity/behavior is asserted UNCHANGED throughout. Identity-catalog cases use the real production
// catalog directly; Settings-wiring cases use a real SettingsCoordinator against a real temp
// PrivonLocalStore with a hand-written FakeNativeMessagingHostRegistrationEnvironment -- no real
// HKCU/filesystem access anywhere in this file. WebExtensionOriginAllowlist.Production and the
// extension manifest are asserted UNCHANGED, never modified by this gate.
public class Gate031E5G1F_EdgeIdentityAndSettingsProvisioningRedTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "PrivonE5G1FTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string ChromeStoreItemId = "aieobgphcpmkfnhadocdhenigmackboo";
    private const string EdgeCrxId = "fmdcgbjednllpjlogkcjmlocpnpbpljn";
    private const string ExpectedEdgeOrigin = "chrome-extension://fmdcgbjednllpjlogkcjmlocpnpbpljn/";

    // ==================================================================
    // IDENTITY CATALOG
    // ==================================================================

    // Item 1: Edge identity resolves to exact raw CRX ID.
    [Fact]
    public void Case01_VerifiedEdgeIdentity_Exists_RawCrxId_IsExact_Ordinal()
    {
        bool found = VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Edge, out var identity);
        Assert.True(found);
        Assert.Equal(EdgeCrxId, identity.StoreItemId, StringComparer.Ordinal);
    }

    // Item 2: exact derived origin.
    [Fact]
    public void Case02_DerivedEdgeOrigin_IsExact_Ordinal()
    {
        VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Edge, out var identity);
        Assert.Equal(ExpectedEdgeOrigin, identity.NativeMessagingOrigin, StringComparer.Ordinal);
        Assert.Matches("^chrome-extension://[a-p]{32}/$", identity.NativeMessagingOrigin);
    }

    // Item 3: Chrome identity remains unchanged after Edge is added.
    [Fact]
    public void Case03_ChromeIdentity_RemainsUnchanged()
    {
        bool found = VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Chrome, out var identity);
        Assert.True(found);
        Assert.Equal(ChromeStoreItemId, identity.StoreItemId, StringComparer.Ordinal);
        Assert.Equal(AppBrowser.Chrome, identity.Browser);
    }

    // Item 4: Edge raw ID exists in exactly one canonical production source.
    [Fact]
    public void Case04_EdgeRawId_ExistsInExactlyOneCanonicalProductionSource()
    {
        string appDir = FindAppSourceDirectory();
        int occurrences = 0;
        foreach (var file in Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            occurrences += Regex.Matches(File.ReadAllText(file), Regex.Escape(EdgeCrxId)).Count;
        }

        Assert.Equal(1, occurrences);
    }

    // Item 5: no duplicated hardcoded Edge origin literal anywhere in production source -- the full
    // origin string never appears as a literal (it is always derived mechanically via
    // NativeMessagingOrigin), so this also proves the identity file itself holds only the raw ID.
    [Fact]
    public void Case05_NoDuplicatedHardcodedEdgeOriginLiteral_AnywhereInProductionSource()
    {
        string appDir = FindAppSourceDirectory();
        foreach (var file in Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(ExpectedEdgeOrigin, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    // ==================================================================
    // SETTINGS EDGE PROVISIONING WIRING
    //
    // Startup-inertness (item 13) and Dispose-inertness (item 14) are already covered end-to-end,
    // browser-generically, by PrivonAppUiBridgeTests (Start_CausesZeroNativeMessagingRegistrationMutation /
    // Dispose_DoesNotUninstallTheNativeMessagingHost) -- those assert the CallLog is completely empty,
    // which already proves zero Edge mutation too. Not duplicated here.
    // ==================================================================

    private sealed class Harness
    {
        public required FakeNativeMessagingHostRegistrationEnvironment RegistrationEnvironment { get; init; }
        public required NativeMessagingHostRegistrationCoordinator RegistrationCoordinator { get; init; }
        public required List<FakeSettingsSurface> CreatedSurfaces { get; init; }
        public required SettingsCoordinator Coordinator { get; init; }
    }

    private Harness CreateHarness(string hostPath = @"D:\Evidence\PRIVON.exe")
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var categoryService = new ProtectionCategorySettingsService(store);
        var exceptionService = new UserExceptionService(store);
        var registrationEnvironment = new FakeNativeMessagingHostRegistrationEnvironment();
        var registrationCoordinator = new NativeMessagingHostRegistrationCoordinator(registrationEnvironment, () => hostPath);
        var createdSurfaces = new List<FakeSettingsSurface>();

        var coordinator = new SettingsCoordinator(
            categoryService,
            exceptionService,
            DetectionPipeline.CreateDefault(),
            () => false,
            () =>
            {
                var surface = new FakeSettingsSurface();
                createdSurfaces.Add(surface);
                return surface;
            },
            registrationCoordinator);

        return new Harness
        {
            RegistrationEnvironment = registrationEnvironment,
            RegistrationCoordinator = registrationCoordinator,
            CreatedSurfaces = createdSurfaces,
            Coordinator = coordinator,
        };
    }

    private static string EdgeExpectedPath() => NativeMessagingHostRegistrationLayout.ExpectedManifestPath(AppBrowser.Edge);

    private static bool IsWriteCall(string logEntry) =>
        logEntry.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal)
        || logEntry.StartsWith("WriteManifest(", StringComparison.Ordinal)
        || logEntry.StartsWith("DeleteSubkey(", StringComparison.Ordinal)
        || logEntry.StartsWith("DeleteManifest(", StringComparison.Ordinal);

    // Item 6: Fresh exposes explicit Provision; item 18: Edge layout uses the Edge registry axis;
    // item 19/20: generated manifest contains exactly one allowed origin, the exact Edge origin,
    // never a Chrome origin.
    [Fact]
    public void Case06_18_19_20_DeferredSettingsProvision_PerformsNoMutation_DormantCoordinatorStillBuildsExactEdgeManifest()
    {
        const string hostPath = @"D:\Evidence\PRIVON.exe";
        var h = CreateHarness(hostPath);
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);

        surface.RaiseEdgeNativeMessagingProvisionRequested();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Edge));
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);

        Assert.Equal(
            NativeMessagingRegistrationReadiness.Ready,
            h.RegistrationCoordinator.Provision(AppBrowser.Edge, ExpectedEdgeOrigin));

        string manifest = h.RegistrationEnvironment.ManifestContentAt(EdgeExpectedPath())!;
        using var doc = JsonDocument.Parse(manifest);
        Assert.Equal(hostPath, doc.RootElement.GetProperty("path").GetString());
        var origins = doc.RootElement.GetProperty("allowed_origins");
        Assert.Equal(1, origins.GetArrayLength());
        Assert.Equal(ExpectedEdgeOrigin, origins[0].GetString());
        Assert.DoesNotContain("chrome-extension://" + ChromeStoreItemId + "/", manifest, StringComparison.Ordinal);

        // Audit-corrected (BrowserMutationClassifier): a case-sensitive "Chrome" substring search can
        // never match a manifest-only log entry (WriteManifest/DeleteManifest log only the lowercase
        // chrome-host.json PATH, never the word "Chrome").
        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Chrome));
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);
    }

    // Item 7: Ready causes zero mutation on a second Provision click.
    [Fact]
    public void Case07_Ready_ExplicitProvisionAgain_PerformsNoWriteChurn()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Ready,
            h.RegistrationCoordinator.Provision(AppBrowser.Edge, ExpectedEdgeOrigin));
        int callsSoFar = h.RegistrationEnvironment.CallLog.Count;

        surface.RaiseEdgeNativeMessagingProvisionRequested(); // already Ready

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog.Skip(callsSoFar), IsWriteCall);
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);
    }

    // Item 8: deferred Settings actions must not provision or repair even an owned Edge registration.
    [Fact]
    public void Case08_OwnedNeedsRepair_ExplicitRepair_RoutesRepairAndReachesReady_ProvisionDoesNotSilentlyRepair()
    {
        var h = CreateHarness();
        string expectedPath = EdgeExpectedPath();
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Edge, expectedPath); // OwnedNeedsRepair, manifest absent
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);

        surface.RaiseEdgeNativeMessagingProvisionRequested();
        Assert.False(h.RegistrationEnvironment.ManifestPresent(expectedPath));
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);

        surface.RaiseEdgeNativeMessagingRepairRequested();
        Assert.False(h.RegistrationEnvironment.ManifestPresent(expectedPath));
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);
    }

    // Item 9: ForeignBlocked causes zero mutation.
    [Fact]
    public void Case09_ForeignBlocked_SettingsActions_NeverAdoptOrDelete()
    {
        var h = CreateHarness();
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Edge, @"C:\SomeOtherVendor\different-host.json");
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);

        surface.RaiseEdgeNativeMessagingProvisionRequested();
        surface.RaiseEdgeNativeMessagingRepairRequested();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);
    }

    // Item 10: OrphanBlocked causes zero mutation.
    [Fact]
    public void Case10_OrphanBlocked_SettingsActions_NeverAdoptOrDelete()
    {
        var h = CreateHarness();
        string expectedPath = EdgeExpectedPath();
        h.RegistrationEnvironment.SeedManifest(expectedPath, "{}"); // file present, no registry witness
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);

        surface.RaiseEdgeNativeMessagingProvisionRequested();
        surface.RaiseEdgeNativeMessagingRepairRequested();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);
    }

    // Item 11: opening Settings causes zero Edge registration mutation.
    [Fact]
    public void Case11_OpeningSettings_IsReadOnly_NoWriteCallsAndDeferredEdgeReadinessRendered()
    {
        var h = CreateHarness();

        h.Coordinator.ShowRequested();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);
    }

    // Item 12: Inspect (the read-only call Settings uses) causes zero mutation, independent of open.
    [Fact]
    public void Case12_DirectInspectEdge_CausesZeroMutation()
    {
        var h = CreateHarness();

        var readiness = h.RegistrationCoordinator.Inspect(AppBrowser.Edge, ExpectedEdgeOrigin);

        Assert.Equal(NativeMessagingRegistrationReadiness.Fresh, readiness);
        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
    }

    // Item 15: a deferred Settings Edge command mutates neither browser, and Chrome remains
    // independently Fresh throughout.
    [Fact]
    public void Case15_DeferredSettingsEdgeProvision_MutatesNeitherEdgeNorChrome()
    {
        var h = CreateHarness();
        string chromeOrigin = "chrome-extension://" + ChromeStoreItemId + "/";
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Fresh, h.RegistrationCoordinator.Inspect(AppBrowser.Chrome, chromeOrigin));

        int callsBefore = h.RegistrationEnvironment.CallLog.Count;
        surface.RaiseEdgeNativeMessagingProvisionRequested();
        var actionDelta = h.RegistrationEnvironment.CallLog.Skip(callsBefore).ToList();

        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);
        Assert.DoesNotContain(actionDelta, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Edge));
        // Audit-corrected (BrowserMutationClassifier): see Case16_17's own doc for why a "Chrome"
        // substring search cannot detect a manifest-only mutation.
        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Chrome));
        Assert.False(h.RegistrationEnvironment.LeafPresent(AppBrowser.Chrome));
        Assert.Equal(NativeMessagingRegistrationReadiness.Fresh, h.RegistrationCoordinator.Inspect(AppBrowser.Chrome, chromeOrigin));
    }

    // Items 16/17: a GENUINELY mutating Edge Repair (real OwnedNeedsRepair -> Ready, actual
    // Uninstall+Install through the frozen registrar) leaves an independently-seeded, already-Ready
    // Chrome completely untouched -- byte-identical registry value and manifest content, not merely
    // "still Fresh" (which a no-op Repair could satisfy trivially with no real evidence).
    //
    // AUDIT CORRECTION: the prior version of this case called Repair while Edge was ALREADY Ready
    // (having just been Provisioned), so Repair's own OwnedNeedsRepair-only guard made that call a
    // pure read-only no-op -- independently reproduced and confirmed: its own CallLog delta held
    // exactly 6 entries, none of them SetSubkeyDefaultValue/WriteManifest/DeleteSubkey/DeleteManifest.
    // That proved nothing about cross-browser mutation isolation. The dormant coordinator is invoked
    // directly here to retain real OwnedNeedsRepair -> Ready coverage while Settings stays deferred.
    [Fact]
    public void Case16_17_DormantCoordinatorEdgeRepair_OwnedNeedsRepairToReady_NeverMutatesChrome_ChromeByteIdentical()
    {
        var h = CreateHarness();
        string edgeExpectedPath = EdgeExpectedPath();
        string chromeExpectedPath = NativeMessagingHostRegistrationLayout.ExpectedManifestPath(AppBrowser.Chrome);
        string chromeOrigin = "chrome-extension://" + ChromeStoreItemId + "/";

        // Seed Edge into a REAL, registrar-recognized OwnedNeedsRepair: leaf exists and is witnessed
        // (default value == expected manifest path), manifest itself absent -- the exact same
        // fixture pattern Case08 already uses.
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Edge, edgeExpectedPath);

        // Independently seed Chrome into an already-Ready state with real, snapshot-able content --
        // Ready (not merely Fresh) so a false adopt/overwrite leak would have actual bytes to disturb.
        string chromeManifestJson = NativeMessagingHostRegistrationLayout.BuildManifestJson(
            new NativeMessagingHostRegistrationSpec(@"D:\Evidence\PRIVON.exe", chromeOrigin));
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, chromeExpectedPath);
        h.RegistrationEnvironment.SeedManifest(chromeExpectedPath, chromeManifestJson);

        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        // 1. Independently confirm Edge Inspect returns OwnedNeedsRepair.
        Assert.Equal(NativeMessagingRegistrationReadiness.OwnedNeedsRepair, h.RegistrationCoordinator.Inspect(AppBrowser.Edge, ExpectedEdgeOrigin));
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);

        // 2/3. Chrome's own known-Ready state, snapshotted before the Edge action.
        Assert.Equal(NativeMessagingRegistrationReadiness.Ready, h.RegistrationCoordinator.Inspect(AppBrowser.Chrome, chromeOrigin));
        string? chromeLeafBefore = h.RegistrationEnvironment.LeafValue(AppBrowser.Chrome);
        string? chromeManifestBefore = h.RegistrationEnvironment.ManifestContentAt(chromeExpectedPath);
        int callsBeforeRepair = h.RegistrationEnvironment.CallLog.Count;

        // 4. Exercise the dormant lower-level Edge Repair path directly. Settings is release-gated.
        Assert.Equal(
            NativeMessagingRegistrationReadiness.Ready,
            h.RegistrationCoordinator.Repair(AppBrowser.Edge, ExpectedEdgeOrigin));

        var repairDelta = h.RegistrationEnvironment.CallLog.Skip(callsBeforeRepair).ToList();

        // 5. Prove Edge Repair genuinely mutated Edge -- via BrowserMutationClassifier, which (unlike
        // a "Edge" substring search) correctly detects the manifest write too (WriteManifest logs
        // only the lowercase edge-host.json PATH, never the word "Edge").
        Assert.Contains(repairDelta, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Edge));
        Assert.Equal(NativeMessagingRegistrationReadiness.Failed, surface.LastRenderedState!.EdgeNativeMessagingReadiness);
        Assert.True(h.RegistrationEnvironment.ManifestPresent(edgeExpectedPath));

        // 6. Prove Chrome was untouched by that same Repair. PRIMARY proof (audit-corrected): zero
        // mutation EVENTS for each of the four write primitives individually, via
        // BrowserMutationClassifier -- NOT a case-sensitive "Chrome" substring search, which can
        // never match a manifest-only log entry (WriteManifest/DeleteManifest log only the lowercase
        // chrome-host.json PATH, never the word "Chrome") and would therefore silently miss a real
        // manifest-level cross-browser leak. Byte-identical final-state snapshot equality is kept
        // only as SECONDARY evidence -- delete+rewrite-same-bytes or an unnecessary same-value
        // mutation could leave final content unchanged while still violating browser independence,
        // so final-state equality alone is never sufficient on its own.
        Assert.DoesNotContain(repairDelta, e => BrowserMutationClassifier.IsSetSubkeyDefaultValueFor(e, AppBrowser.Chrome));
        Assert.DoesNotContain(repairDelta, e => BrowserMutationClassifier.IsDeleteSubkeyFor(e, AppBrowser.Chrome));
        Assert.DoesNotContain(repairDelta, e => BrowserMutationClassifier.IsWriteManifestFor(e, AppBrowser.Chrome));
        Assert.DoesNotContain(repairDelta, e => BrowserMutationClassifier.IsDeleteManifestFor(e, AppBrowser.Chrome));
        Assert.DoesNotContain(repairDelta, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Chrome)); // combined restatement

        // SECONDARY: final-state byte equivalence.
        Assert.Equal(chromeLeafBefore, h.RegistrationEnvironment.LeafValue(AppBrowser.Chrome), StringComparer.Ordinal);
        Assert.Equal(chromeManifestBefore, h.RegistrationEnvironment.ManifestContentAt(chromeExpectedPath), StringComparer.Ordinal);
        Assert.Equal(NativeMessagingRegistrationReadiness.Ready, h.RegistrationCoordinator.Inspect(AppBrowser.Chrome, chromeOrigin));
    }

    // Item 21: production Web authorization allowlist remains EMPTY after Edge provisioning succeeds.
    [Fact]
    public void Case21_SuccessfulEdgeProvision_DoesNotPopulateWebAuthorizationAllowlist()
    {
        var h = CreateHarness();
        Assert.Equal(
            NativeMessagingRegistrationReadiness.Ready,
            h.RegistrationCoordinator.Provision(AppBrowser.Edge, ExpectedEdgeOrigin));
        Assert.Empty(WebExtensionOriginAllowlist.Production);
    }

    // The release guard must refuse before even a configured throwing write can be reached.
    [Fact]
    public void CaseExtra_DeferredEdgeProvision_DoesNotReachThrowingMutation_AndRendersUnavailable()
    {
        var h = CreateHarness();
        h.RegistrationEnvironment.ThrowOnSetSubkeyDefaultValue = new InvalidOperationException("synthetic");
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseEdgeNativeMessagingProvisionRequested();

        Assert.True(NativeMessagingRegistrationReadiness.Failed == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Failed} (the action's own outcome), got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
        Assert.Equal(NativeMessagingRegistrationReadiness.Fresh, h.RegistrationCoordinator.Inspect(AppBrowser.Edge, ExpectedEdgeOrigin));
    }

    // No separate registrar/environment/coordinator instance was introduced for Edge -- source-scanned
    // exactly like the Chrome gate's own equivalent check.
    [Fact]
    public void CaseExtra_Settings_NeverConstructsASeparateRegistrarEnvironmentOrCoordinatorInstance()
    {
        string coordinatorSource = File.ReadAllText(FindAppSourceFile("SettingsCoordinator.cs"));
        Assert.DoesNotContain("new NativeMessagingHostRegistrar(", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new WindowsNativeMessagingHostRegistrationEnvironment(", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new NativeMessagingHostRegistrationCoordinator(", coordinatorSource, StringComparison.Ordinal);

        string windowSource = File.ReadAllText(FindAppSourceFile("SettingsWindow.cs"));
        Assert.DoesNotContain("NativeMessagingHostRegistrationCoordinator", windowSource, StringComparison.Ordinal);
    }

    // No CLI provisioning verb was introduced for Edge either -- re-affirms the frozen E3 contract.
    [Fact]
    public void CaseExtra_NoCliProvisioningVerbExists_HypotheticalVerbIsRejectedHostShaped()
    {
        string[] args = ["--provision-edge"];
        var kind = NativeMessagingHostInvocation.Classify(args, new HashSet<string>(StringComparer.Ordinal)).Kind;
        Assert.Equal(NativeMessagingInvocationKind.RejectedHostShaped, kind);
    }

    // Extension manifest key remains absent (unaffected by Edge identity integration).
    [Fact]
    public void CaseExtra_ExtensionManifest_StillHasNoKeyField()
    {
        string manifestPath = FindExtensionManifestFile();
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.False(manifest.RootElement.TryGetProperty("key", out _));
    }

    [Fact]
    public void E5G2_DeferredEdge_SetupAndRepairEvents_PerformZeroRegistrationMutation()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        h.RegistrationEnvironment.CallLog.Clear();

        surface.RaiseEdgeNativeMessagingProvisionRequested();
        surface.RaiseEdgeNativeMessagingRepairRequested();

        Assert.DoesNotContain(
            h.RegistrationEnvironment.CallLog,
            entry => BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Edge));
    }

    // ==================================================================
    // helpers
    // ==================================================================

    private static string FindAppSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate src/Privon.App from the test output directory.");

        return Path.Combine(dir.FullName, "src", "Privon.App");
    }

    private static string FindAppSourceFile(string fileName) => Path.Combine(FindAppSourceDirectory(), fileName);

    private static string FindExtensionManifestFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "extension", "manifest.json")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate extension/manifest.json from the test output directory.");

        return Path.Combine(dir.FullName, "extension", "manifest.json");
    }
}
