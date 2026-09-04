using System.Text.Json;
using System.Text.RegularExpressions;
using Privon.Detection;
using Privon.Storage;
using AppBrowser = Privon.App.NativeMessagingBrowser;

namespace Privon.App.Tests;

// PRIVON 0.3.1 -- verified Edge browser-store identity integration + explicit Settings Edge
// provisioning, extending Gate E5G.1C's Chrome-only identity catalog and Settings wiring. Chrome
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
    private const string EdgeCrxId = "fmdcgbjednllpjlogkcjmlocpnpbpjjn";
    private const string ExpectedEdgeOrigin = "chrome-extension://fmdcgbjednllpjlogkcjmlocpnpbpjjn/";

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
    public void Case06_18_19_20_Fresh_ExplicitProvision_RoutesEdgeOnly_ManifestContainsExactlyEdgeOrigin()
    {
        const string hostPath = @"D:\Evidence\PRIVON.exe";
        var h = CreateHarness(hostPath);
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.True(NativeMessagingRegistrationReadiness.Fresh == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Fresh}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");

        surface.RaiseEdgeNativeMessagingProvisionRequested();

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
        Assert.True(NativeMessagingRegistrationReadiness.Ready == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Ready}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
    }

    // Item 7: Ready causes zero mutation on a second Provision click.
    [Fact]
    public void Case07_Ready_ExplicitProvisionAgain_PerformsNoWriteChurn()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        surface.RaiseEdgeNativeMessagingProvisionRequested(); // Fresh -> Ready
        int callsSoFar = h.RegistrationEnvironment.CallLog.Count;

        surface.RaiseEdgeNativeMessagingProvisionRequested(); // already Ready

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog.Skip(callsSoFar), IsWriteCall);
        Assert.True(NativeMessagingRegistrationReadiness.Ready == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Ready}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
    }

    // Item 8: OwnedNeedsRepair exposes explicit Repair (and Provision must NOT silently repair it).
    [Fact]
    public void Case08_OwnedNeedsRepair_ExplicitRepair_RoutesRepairAndReachesReady_ProvisionDoesNotSilentlyRepair()
    {
        var h = CreateHarness();
        string expectedPath = EdgeExpectedPath();
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Edge, expectedPath); // OwnedNeedsRepair, manifest absent
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.True(NativeMessagingRegistrationReadiness.OwnedNeedsRepair == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.OwnedNeedsRepair}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");

        surface.RaiseEdgeNativeMessagingProvisionRequested();
        Assert.False(h.RegistrationEnvironment.ManifestPresent(expectedPath));
        Assert.True(NativeMessagingRegistrationReadiness.OwnedNeedsRepair == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.OwnedNeedsRepair}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");

        surface.RaiseEdgeNativeMessagingRepairRequested();
        Assert.True(h.RegistrationEnvironment.ManifestPresent(expectedPath));
        Assert.True(NativeMessagingRegistrationReadiness.Ready == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Ready}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
    }

    // Item 9: ForeignBlocked causes zero mutation.
    [Fact]
    public void Case09_ForeignBlocked_SettingsActions_NeverAdoptOrDelete()
    {
        var h = CreateHarness();
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Edge, @"C:\SomeOtherVendor\different-host.json");
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.True(NativeMessagingRegistrationReadiness.ForeignBlocked == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.ForeignBlocked}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");

        surface.RaiseEdgeNativeMessagingProvisionRequested();
        surface.RaiseEdgeNativeMessagingRepairRequested();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
        Assert.True(NativeMessagingRegistrationReadiness.ForeignBlocked == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.ForeignBlocked}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
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
        Assert.True(NativeMessagingRegistrationReadiness.OrphanBlocked == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.OrphanBlocked}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");

        surface.RaiseEdgeNativeMessagingProvisionRequested();
        surface.RaiseEdgeNativeMessagingRepairRequested();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
        Assert.True(NativeMessagingRegistrationReadiness.OrphanBlocked == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.OrphanBlocked}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
    }

    // Item 11: opening Settings causes zero Edge registration mutation.
    [Fact]
    public void Case11_OpeningSettings_IsReadOnly_NoWriteCallsAndFreshEdgeReadinessRendered()
    {
        var h = CreateHarness();

        h.Coordinator.ShowRequested();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.True(NativeMessagingRegistrationReadiness.Fresh == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Fresh}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
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

    // Item 15: provisioning Edge (a genuine Fresh -> Ready mutation) never mutates Chrome, and Chrome
    // remains independently Fresh (still snapshot-able as completely absent) throughout.
    [Fact]
    public void Case15_ProvisioningEdge_GenuinelyMutatesEdge_NeverMutatesChrome()
    {
        var h = CreateHarness();
        string chromeOrigin = "chrome-extension://" + ChromeStoreItemId + "/";
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.Equal(NativeMessagingRegistrationReadiness.Fresh, h.RegistrationCoordinator.Inspect(AppBrowser.Chrome, chromeOrigin));

        surface.RaiseEdgeNativeMessagingProvisionRequested(); // genuine Fresh -> Ready mutation

        Assert.True(NativeMessagingRegistrationReadiness.Ready == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Ready}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
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
    // That proved nothing about cross-browser mutation isolation. This version forces Edge through
    // the real OwnedNeedsRepair -> Ready mutation path before asserting Chrome isolation.
    [Fact]
    public void Case16_17_GenuineEdgeRepair_OwnedNeedsRepairToReady_NeverMutatesChrome_ChromeByteIdentical()
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
        Assert.True(NativeMessagingRegistrationReadiness.OwnedNeedsRepair == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.OwnedNeedsRepair}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");

        // 2/3. Chrome's own known-Ready state, snapshotted before the Edge action.
        Assert.Equal(NativeMessagingRegistrationReadiness.Ready, h.RegistrationCoordinator.Inspect(AppBrowser.Chrome, chromeOrigin));
        string? chromeLeafBefore = h.RegistrationEnvironment.LeafValue(AppBrowser.Chrome);
        string? chromeManifestBefore = h.RegistrationEnvironment.ManifestContentAt(chromeExpectedPath);
        int callsBeforeRepair = h.RegistrationEnvironment.CallLog.Count;

        // 4. Trigger the actual Edge Repair path through real Settings routing.
        surface.RaiseEdgeNativeMessagingRepairRequested();

        var repairDelta = h.RegistrationEnvironment.CallLog.Skip(callsBeforeRepair).ToList();

        // 5. Prove Edge Repair genuinely mutated Edge -- via BrowserMutationClassifier, which (unlike
        // a "Edge" substring search) correctly detects the manifest write too (WriteManifest logs
        // only the lowercase edge-host.json PATH, never the word "Edge").
        Assert.Contains(repairDelta, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Edge));
        Assert.True(NativeMessagingRegistrationReadiness.Ready == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Ready}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
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
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseEdgeNativeMessagingProvisionRequested();

        Assert.True(NativeMessagingRegistrationReadiness.Ready == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Ready}, got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
        Assert.Empty(WebExtensionOriginAllowlist.Production);
    }

    // Failed action must surface Failed (mirrors the E5G.1C commander correction, extended to Edge):
    // throwing on the FIRST write leaves zero trace, so an independent Inspect() afterward would
    // legitimately -- but misleadingly -- report Fresh again; the coordinator's own Provision()
    // outcome must be what is shown instead.
    [Fact]
    public void CaseExtra_EdgeProvision_CoordinatorReturnsFailed_SurfacedAsFailed_NeverReadyOrSilentlyFresh()
    {
        var h = CreateHarness();
        h.RegistrationEnvironment.ThrowOnSetSubkeyDefaultValue = new InvalidOperationException("synthetic");
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseEdgeNativeMessagingProvisionRequested();

        Assert.True(NativeMessagingRegistrationReadiness.Failed == surface.LastRenderedState!.EdgeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Failed} (the action's own outcome), got {surface.LastRenderedState!.EdgeNativeMessagingReadiness}");
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
