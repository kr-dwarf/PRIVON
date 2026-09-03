using System.Text.Json;
using System.Text.RegularExpressions;
using Privon.Detection;
using Privon.Storage;
using AppBrowser = Privon.App.NativeMessagingBrowser;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5G.1C -- verified Chrome browser-store identity (VerifiedBrowserExtensionIdentity/
// VerifiedBrowserExtensionIdentities) and the explicit Settings Chrome Native Messaging provisioning
// trigger (SettingsCoordinator's own CHROME_PROVISIONING wiring). Identity-catalog cases use the real
// production catalog directly; Settings-wiring cases use a real SettingsCoordinator against a real
// temp PrivonLocalStore (same technique as SettingsCoordinatorTests) with a hand-written
// FakeNativeMessagingHostRegistrationEnvironment (Gate E5G.P3's own fake) -- no real HKCU/filesystem
// access anywhere in this file. WebExtensionOriginAllowlist.Production, the extension manifest, and
// every frozen NativeMessagingHostRegistrar/Coordinator/Layout contract are asserted UNCHANGED, never
// modified by this gate.
public class Gate031E5G1C_ChromeIdentityAndSettingsProvisioningRedTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "PrivonE5G1CTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string ChromeStoreItemId = "aieobgphcpmkfnhadocdhenigmackboo";
    private const string ExpectedChromeOrigin = "chrome-extension://aieobgphcpmkfnhadocdhenigmackboo/";
    private const string DevMeasureId = "aippijooaannjccdplmnfpjbgjppkoaa";

    // ==================================================================
    // IDENTITY CATALOG
    // ==================================================================

    [Fact]
    public void Case01_VerifiedChromeIdentity_Exists()
    {
        Assert.True(VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Chrome, out _));
    }

    [Fact]
    public void Case02_RawChromeStoreItemId_IsExact_Ordinal()
    {
        VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Chrome, out var identity);
        Assert.Equal(ChromeStoreItemId, identity.StoreItemId, StringComparer.Ordinal);
    }

    [Fact]
    public void Case03_DerivedNativeMessagingOrigin_IsExact_Ordinal()
    {
        VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Chrome, out var identity);
        Assert.Equal(ExpectedChromeOrigin, identity.NativeMessagingOrigin, StringComparer.Ordinal);
    }

    [Fact]
    public void Case04_Origin_MatchesFrozenChromeExtensionOriginShape()
    {
        VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Chrome, out var identity);
        Assert.Matches("^chrome-extension://[a-p]{32}/$", identity.NativeMessagingOrigin);
    }

    // No normalization/case/path mutation: the origin survives byte-for-byte through the SAME
    // frozen manifest-JSON builder the registrar/coordinator actually use -- a literal substring
    // check on the raw serialized bytes, never a JSON re-parse (which would hide an escaping
    // regression), and never comparing against a value built by that same method (which would be
    // self-referential and prove nothing).
    [Fact]
    public void Case05_OriginSurvivesManifestSerialization_LiteralBytes_NoNormalization()
    {
        VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Chrome, out var identity);
        string json = NativeMessagingHostRegistrationLayout.BuildManifestJson(
            new NativeMessagingHostRegistrationSpec(@"C:\Evidence\PRIVON.exe", identity.NativeMessagingOrigin));

        Assert.Contains("\"allowed_origins\":[\"" + ExpectedChromeOrigin + "\"]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Case06_EdgeIdentity_IsAbsent()
    {
        bool found = VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Edge, out var identity);
        Assert.False(found);
        Assert.Equal(default, identity);
    }

    [Fact]
    public void Case07_UnknownBrowserIdentity_IsAbsent()
    {
        Assert.False(VerifiedBrowserExtensionIdentities.TryGet((AppBrowser)99, out _));
    }

    [Fact]
    public void Case08_ProductionCatalog_ContainsOnlyChrome()
    {
        Assert.True(VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Chrome, out _));
        Assert.False(VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Edge, out _));
    }

    [Fact]
    public void Case09_ProductionRuntimeSource_ContainsExactlyOneExecutableChromeIdAuthority()
    {
        string appDir = FindAppSourceDirectory();
        int occurrences = 0;
        foreach (var file in Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            occurrences += Regex.Matches(File.ReadAllText(file), Regex.Escape(ChromeStoreItemId)).Count;
        }

        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Case10_DevMeasureId_IsNotTheProductionIdentity()
    {
        VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Chrome, out var identity);
        Assert.NotEqual(DevMeasureId, identity.StoreItemId);

        string source = File.ReadAllText(FindAppSourceFile("VerifiedBrowserExtensionIdentity.cs"));
        Assert.DoesNotContain(DevMeasureId, source, StringComparison.Ordinal);
    }

    [Fact]
    public void Case11_WebExtensionOriginAllowlist_RemainsEmpty_AfterIdentityCatalogAccess()
    {
        VerifiedBrowserExtensionIdentities.TryGet(AppBrowser.Chrome, out _);
        Assert.Empty(WebExtensionOriginAllowlist.Production);
    }

    [Fact]
    public void Case12_ExtensionManifest_StillHasNoKeyField()
    {
        string manifestPath = FindExtensionManifestFile();
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.False(manifest.RootElement.TryGetProperty("key", out _));
    }

    [Fact]
    public void Case13_CoordinatorSource_StillContainsNoHardCodedStoreId()
    {
        string source = File.ReadAllText(FindAppSourceFile("NativeMessagingHostRegistrationCoordinator.cs"));
        Assert.DoesNotContain(ChromeStoreItemId, source, StringComparison.Ordinal);
    }

    [Fact]
    public void Case14_PrivonAppCompositionSource_HasNoRegistrationIdentityOrCoordinatorWiring()
    {
        string source = File.ReadAllText(FindAppSourceFile("PrivonAppComposition.cs"));
        Assert.DoesNotContain("VerifiedBrowserExtensionIdentit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeMessagingHostRegistrationCoordinator", source, StringComparison.Ordinal);
    }

    // ==================================================================
    // SETTINGS CHROME PROVISIONING WIRING
    //
    // Cases 16 ("ordinary Start causes no Native Messaging mutation") and 17 ("Dispose causes no
    // uninstall") are already covered end-to-end by PrivonAppUiBridgeTests
    // (Start_CausesZeroNativeMessagingRegistrationMutation / Dispose_DoesNotUninstallTheNativeMessagingHost),
    // which construct SettingsCoordinator through the real PrivonAppUiBridge wiring this gate changed
    // -- re-run as part of this gate's affected regression, not duplicated here.
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

    private static string ChromeExpectedPath() => NativeMessagingHostRegistrationLayout.ExpectedManifestPath(AppBrowser.Chrome);

    private static bool IsWriteCall(string logEntry) =>
        logEntry.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal)
        || logEntry.StartsWith("WriteManifest(", StringComparison.Ordinal)
        || logEntry.StartsWith("DeleteSubkey(", StringComparison.Ordinal)
        || logEntry.StartsWith("DeleteManifest(", StringComparison.Ordinal);

    [Fact]
    public void Case15_OrdinaryConstruction_CausesZeroNativeMessagingMutation()
    {
        var h = CreateHarness();
        Assert.Empty(h.RegistrationEnvironment.CallLog);
    }

    [Fact]
    public void Case18_OpeningSettings_IsReadOnly_NoWriteCallsAndFreshReadinessRendered()
    {
        var h = CreateHarness();

        h.Coordinator.ShowRequested();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.True(NativeMessagingRegistrationReadiness.Fresh == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Fresh}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");
    }

    [Fact]
    public void Case19_20_21_Fresh_ExplicitProvision_ObtainsChromeOriginFromCatalog_RoutesChromeOnlyThroughCoordinator()
    {
        const string hostPath = @"D:\Evidence\PRIVON.exe";
        var h = CreateHarness(hostPath);
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseChromeNativeMessagingProvisionRequested();

        string manifest = h.RegistrationEnvironment.ManifestContentAt(ChromeExpectedPath())!;
        using var doc = JsonDocument.Parse(manifest);
        Assert.Equal(hostPath, doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(ExpectedChromeOrigin, doc.RootElement.GetProperty("allowed_origins")[0].GetString());

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, e => e.Contains("Edge", StringComparison.Ordinal));
        Assert.True(NativeMessagingRegistrationReadiness.Ready == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Ready}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");
    }

    [Fact]
    public void Case22_Ready_ExplicitProvisionAgain_PerformsNoWriteChurn()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        surface.RaiseChromeNativeMessagingProvisionRequested(); // Fresh -> Ready
        int callsSoFar = h.RegistrationEnvironment.CallLog.Count;

        surface.RaiseChromeNativeMessagingProvisionRequested(); // already Ready

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog.Skip(callsSoFar), IsWriteCall);
        Assert.True(NativeMessagingRegistrationReadiness.Ready == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Ready}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");
    }

    [Fact]
    public void Case23_OwnedNeedsRepair_Provision_DoesNotSilentlyRepair()
    {
        var h = CreateHarness();
        string expectedPath = ChromeExpectedPath();
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, expectedPath); // OWNED, manifest absent
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.True(NativeMessagingRegistrationReadiness.OwnedNeedsRepair == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.OwnedNeedsRepair}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");

        surface.RaiseChromeNativeMessagingProvisionRequested();

        Assert.False(h.RegistrationEnvironment.ManifestPresent(expectedPath));
        Assert.True(NativeMessagingRegistrationReadiness.OwnedNeedsRepair == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.OwnedNeedsRepair}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");
    }

    [Fact]
    public void Case24_OwnedNeedsRepair_ExplicitRepair_RoutesRepairAndReachesReady()
    {
        var h = CreateHarness();
        string expectedPath = ChromeExpectedPath();
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, expectedPath);
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseChromeNativeMessagingRepairRequested();

        Assert.True(h.RegistrationEnvironment.ManifestPresent(expectedPath));
        Assert.True(NativeMessagingRegistrationReadiness.Ready == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Ready}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");
    }

    [Fact]
    public void Case25_ForeignBlocked_SettingsActions_NeverAdoptOrDelete()
    {
        var h = CreateHarness();
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, @"C:\SomeOtherVendor\different-host.json");
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.True(NativeMessagingRegistrationReadiness.ForeignBlocked == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.ForeignBlocked}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");

        surface.RaiseChromeNativeMessagingProvisionRequested();
        surface.RaiseChromeNativeMessagingRepairRequested();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
        Assert.True(NativeMessagingRegistrationReadiness.ForeignBlocked == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.ForeignBlocked}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");
    }

    [Fact]
    public void Case26_OrphanBlocked_SettingsActions_NeverAdoptOrDelete()
    {
        var h = CreateHarness();
        string expectedPath = ChromeExpectedPath();
        h.RegistrationEnvironment.SeedManifest(expectedPath, "{}"); // file present, no registry witness
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.True(NativeMessagingRegistrationReadiness.OrphanBlocked == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.OrphanBlocked}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");

        surface.RaiseChromeNativeMessagingProvisionRequested();
        surface.RaiseChromeNativeMessagingRepairRequested();

        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, IsWriteCall);
        Assert.True(NativeMessagingRegistrationReadiness.OrphanBlocked == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.OrphanBlocked}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");
    }

    // COMMANDER CORRECTION -- FAILED_ACTION_MUST_SURFACE: the coordinator's OWN Provision() return
    // value is FROZEN authority for what a just-attempted action did (NativeMessagingHostRegistrationCoordinator's
    // own class doc: "Success requires the POST-mutation state to actually be Ready -- a non-throwing
    // Install call is never itself treated as success"). Settings must surface THAT exact outcome
    // when it is Failed -- never silently discard it and substitute a SEPARATE, independent
    // read-only Inspect() call's own (legitimately different) observation of the environment.
    //
    // This scenario is deliberately the one where discarding Failed is most dangerous: throwing on
    // the FIRST write (SetSubkeyDefaultValue, before ANY state changed) leaves the environment
    // completely untouched, so an independent Inspect() afterward legitimately -- but misleadingly
    // -- reports Fresh again, as if the user's Provision click had never happened at all. The
    // underlying registration ownership contract Inspect() reports is NOT falsified by this (a
    // fresh Inspect() call still correctly says Fresh, proven below) -- but the ACTION RESULT shown
    // to the user for the click they just made must be Failed, not a stale/misleading Fresh.
    [Fact]
    public void Case27_Provision_CoordinatorReturnsFailed_SurfacedAsFailed_NeverReadyOrSilentlyFresh()
    {
        var h = CreateHarness();
        h.RegistrationEnvironment.ThrowOnSetSubkeyDefaultValue = new InvalidOperationException("synthetic");
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.True(NativeMessagingRegistrationReadiness.Fresh == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Fresh}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");

        surface.RaiseChromeNativeMessagingProvisionRequested();

        // The action's own outcome (Provision() returned Failed -- Install threw) must be exactly
        // what is shown -- never collapsed to Ready, and never silently re-derived as Fresh merely
        // because the failed attempt happened to leave no trace.
        Assert.True(NativeMessagingRegistrationReadiness.Failed == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Failed} (the action's own outcome), got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");

        // The underlying registration ownership contract itself is NOT falsified by that display:
        // an independent, separate read-only Inspect() still correctly reports the environment's
        // real (untouched) state -- Fresh -- proving this correction does not lie about ownership,
        // it only stops discarding the action's own result for the render that follows the click.
        Assert.Equal(NativeMessagingRegistrationReadiness.Fresh, h.RegistrationCoordinator.Inspect(AppBrowser.Chrome, ExpectedChromeOrigin));

        // No additional registry/manifest mutation occurred to produce this failure display --
        // exactly the one SetSubkeyDefaultValue attempt from the Provision click itself.
        Assert.Equal(1, h.RegistrationEnvironment.CallLog.Count(e => e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal)));
        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, e => e.StartsWith("WriteManifest(", StringComparison.Ordinal));
    }

    // Companion for Repair: OwnedNeedsRepair start state, DeleteSubkey (the FIRST primitive
    // Repair's own Uninstall step calls) throws before removing anything, so the leaf stays exactly
    // as witnessed as it started -- an independent Inspect() afterward legitimately reports
    // OwnedNeedsRepair again (never Fresh, never Failed on its own), which is a DIFFERENT
    // non-Ready state than Provision's own scenario above -- proving this correction is not
    // special-cased to one specific underlying readiness value.
    [Fact]
    public void Case27B_Repair_CoordinatorReturnsFailed_SurfacedAsFailed_NeverReadyOrSilentlyOwnedNeedsRepair()
    {
        var h = CreateHarness();
        string expectedPath = ChromeExpectedPath();
        h.RegistrationEnvironment.SeedLeaf(AppBrowser.Chrome, expectedPath);
        h.RegistrationEnvironment.ThrowOnDeleteSubkey = new InvalidOperationException("synthetic");
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);
        Assert.True(NativeMessagingRegistrationReadiness.OwnedNeedsRepair == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.OwnedNeedsRepair}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");

        surface.RaiseChromeNativeMessagingRepairRequested();

        Assert.True(NativeMessagingRegistrationReadiness.Failed == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Failed} (the action's own outcome), got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");

        // Underlying ownership contract not falsified: an independent Inspect() still correctly
        // reports OwnedNeedsRepair (the leaf survived the throw, exactly as witnessed).
        Assert.Equal(NativeMessagingRegistrationReadiness.OwnedNeedsRepair, h.RegistrationCoordinator.Inspect(AppBrowser.Chrome, ExpectedChromeOrigin));

        // No manifest mutation was ever reached -- Repair's own Uninstall step failed on its first
        // primitive, before Install could even be attempted.
        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, e => e.StartsWith("WriteManifest(", StringComparison.Ordinal));
        Assert.DoesNotContain(h.RegistrationEnvironment.CallLog, e => e.StartsWith("DeleteManifest(", StringComparison.Ordinal));
    }

    [Fact]
    public void Case28_SuccessfulProvision_DoesNotPopulateWebAuthorizationAllowlist()
    {
        var h = CreateHarness();
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseChromeNativeMessagingProvisionRequested();

        Assert.True(NativeMessagingRegistrationReadiness.Ready == surface.LastRenderedState!.ChromeNativeMessagingReadiness, $"expected {NativeMessagingRegistrationReadiness.Ready}, got {surface.LastRenderedState!.ChromeNativeMessagingReadiness}");
        Assert.Empty(WebExtensionOriginAllowlist.Production);
    }

    [Fact]
    public void Case29_Provision_UsesExactlyTheInjectedHostPathProviderValue()
    {
        const string distinctHostPath = @"E:\CustomEvidence\PRIVON.exe";
        var h = CreateHarness(distinctHostPath);
        h.Coordinator.ShowRequested();
        var surface = Assert.Single(h.CreatedSurfaces);

        surface.RaiseChromeNativeMessagingProvisionRequested();

        using var doc = JsonDocument.Parse(h.RegistrationEnvironment.ManifestContentAt(ChromeExpectedPath())!);
        Assert.Equal(distinctHostPath, doc.RootElement.GetProperty("path").GetString());
    }

    // Native-host child mode: with the production allowlist empty (unchanged by this gate), a
    // host-shaped invocation is rejected before either runner -- so neither the normal tray/App/
    // PrivonAppUiBridge/SettingsCoordinator path nor the host relay path is ever reached, and
    // Settings provisioning (which lives entirely behind the normal-launch UI bridge) is therefore
    // structurally unreachable from a native-host child process.
    [Fact]
    public void Case30_NativeHostChildShapedInvocation_NeverReachesEitherRunner()
    {
        int normalCalls = 0, hostCalls = 0;
        var emptyAllowlist = new HashSet<string>(StringComparer.Ordinal);
        string[] hostShapedArgs = [ExpectedChromeOrigin, "--parent-window=1"];

        PrivonEntryPoint.Run(hostShapedArgs, emptyAllowlist, normalRunner: () => normalCalls++, hostRunner: () => hostCalls++);

        Assert.Equal(0, normalCalls);
        Assert.Equal(0, hostCalls);
    }

    [Fact]
    public void Case31_NoCliProvisioningVerbExists_HypotheticalVerbIsRejectedHostShaped()
    {
        string[] args = ["--provision-chrome"];
        var kind = NativeMessagingHostInvocation.Classify(args, new HashSet<string>(StringComparer.Ordinal)).Kind;
        Assert.Equal(NativeMessagingInvocationKind.RejectedHostShaped, kind);
    }

    [Fact]
    public void Case32_Settings_NeverConstructsASeparateRegistrarEnvironmentOrCoordinatorInstance()
    {
        string coordinatorSource = File.ReadAllText(FindAppSourceFile("SettingsCoordinator.cs"));
        Assert.DoesNotContain("new NativeMessagingHostRegistrar(", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new WindowsNativeMessagingHostRegistrationEnvironment(", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new NativeMessagingHostRegistrationCoordinator(", coordinatorSource, StringComparison.Ordinal);

        string windowSource = File.ReadAllText(FindAppSourceFile("SettingsWindow.cs"));
        Assert.DoesNotContain("NativeMessagingHostRegistrationCoordinator", windowSource, StringComparison.Ordinal);
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
