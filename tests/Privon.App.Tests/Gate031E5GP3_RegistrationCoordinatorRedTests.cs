using AppBrowser = Privon.App.NativeMessagingBrowser;
using Harness = Privon.App.Tests.Gate031E5GP3_RegistrationCoordinatorTestHarness;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5G.P3 -- RED-first behavioral coverage for the PRODUCTION Native Messaging
// registration coordinator: the store-independent, App-level type that will eventually perform
// explicit per-browser provisioning/repair once a VERIFIED production extension origin exists.
//
// FROZEN BOUNDARY THIS GATE MUST NOT CROSS (E5G.P2 commander contract): no Store ID, no every-startup
// provisioning, no automatic repair, no cross-browser rollback, no PrivonAppComposition wiring, no
// WebExtensionOriginAllowlist.Production activation, no real registry/manifest mutation anywhere in
// this suite (FakeNativeMessagingHostRegistrationEnvironment only). NativeMessagingHostRegistrar
// (E5D) stays exactly void Install(...)/void Uninstall(...) -- this coordinator determines outcome
// through read-only post-state inspection alone, never a registrar return value.
//
// OWNERSHIP_VS_READINESS (the central invariant this file exists to prove): the registry witness
// (leaf existence + exact Ordinal default-value match) is the ONLY ownership authority. Manifest
// CONTENT is consulted only once ownership is already proven, purely to distinguish READY from
// OWNED_NEEDS_REPAIR -- it can never promote a FOREIGN or unwitnessed leaf into READY, no matter how
// exactly the manifest content happens to match.
public class Gate031E5GP3_RegistrationCoordinatorRedTests
{
    private const string ChromeExtensionOrigin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/";
    private const string EdgeExtensionOrigin = "chrome-extension://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/";
    private const string SyntheticHostPath = @"C:\Synthetic\PRIVON\PRIVON.exe";

    private static Harness.CoordinatorHandle BuildOrFail(
        FakeNativeMessagingHostRegistrationEnvironment fake, string caseLabel, Func<string?>? pathProvider = null)
    {
        bool built = Harness.TryBuildCoordinator(fake, pathProvider ?? (() => SyntheticHostPath), out var handle, out string reason);
        Assert.True(built, $"Gate E5G.P3 {caseLabel}: {reason}");
        return handle!;
    }

    private static string ChromeExpectedPath() =>
        NativeMessagingHostRegistrationLayout.ExpectedManifestPath(AppBrowser.Chrome);

    private static string EdgeExpectedPath() =>
        NativeMessagingHostRegistrationLayout.ExpectedManifestPath(AppBrowser.Edge);

    private static string ExpectedManifestJson(string hostPath, string origin) =>
        NativeMessagingHostRegistrationLayout.BuildManifestJson(new NativeMessagingHostRegistrationSpec(hostPath, origin));

    // ==================================================================
    // 1 -- construction causes zero mutation.
    // ==================================================================

    [Fact]
    public void Case01_Construction_PerformsZeroMutation()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        _ = BuildOrFail(fake, "case 01");

        Assert.Empty(fake.CallLog);
    }

    // ==================================================================
    // 3/4 -- fail-closed on unusable input, before any environment call.
    // ==================================================================

    [Fact]
    public void Case03_UnresolvedHostPath_Null_FailsClosed_ZeroMutation()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 03", pathProvider: () => null);

        Assert.Equal(CoordinatorReadiness.Failed, Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin));
        Assert.Equal(CoordinatorReadiness.Failed, Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin));
        Assert.Equal(CoordinatorReadiness.Failed, Harness.Repair(handle, AppBrowser.Chrome, ChromeExtensionOrigin));
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Case03B_UnresolvedHostPath_Whitespace_FailsClosed_ZeroMutation()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 03B", pathProvider: () => "   ");

        Assert.Equal(CoordinatorReadiness.Failed, Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin));
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Case04_EmptyExtensionOrigin_FailsClosed_ZeroMutation()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 04");

        Assert.Equal(CoordinatorReadiness.Failed, Harness.Inspect(handle, AppBrowser.Chrome, ""));
        Assert.Equal(CoordinatorReadiness.Failed, Harness.Provision(handle, AppBrowser.Chrome, "   "));
        Assert.Equal(CoordinatorReadiness.Failed, Harness.Repair(handle, AppBrowser.Chrome, null));
        Assert.Empty(fake.CallLog);
    }

    // ==================================================================
    // 5 -- Inspect is read-only under every state.
    // ==================================================================

    [Fact]
    public void Case05_Inspect_NeverMutates_AcrossEveryReachableState()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 05");

        // FRESH
        Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin);
        Assert.False(fake.LeafPresent(AppBrowser.Chrome));

        // FOREIGN
        fake.SeedLeaf(AppBrowser.Chrome, @"C:\Foreign\host.json");
        Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin);
        Assert.Equal(@"C:\Foreign\host.json", fake.LeafValue(AppBrowser.Chrome));

        // ORPHAN
        fake.SeedManifest(EdgeExpectedPath(), "{}");
        Harness.Inspect(handle, AppBrowser.Edge, EdgeExtensionOrigin);
        Assert.False(fake.LeafPresent(AppBrowser.Edge));
        Assert.True(fake.ManifestPresent(EdgeExpectedPath()));
    }

    // ==================================================================
    // 6 -- FRESH: leaf absent AND expected manifest absent.
    // ==================================================================

    [Fact]
    public void Case06_LeafAbsentAndManifestAbsent_IsFresh()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 06");

        Assert.Equal(CoordinatorReadiness.Fresh, Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin));
    }

    // ==================================================================
    // 7 -- READY: witnessed leaf + manifest exists + manifest content matches the supplied spec.
    // ==================================================================

    [Fact]
    public void Case07_WitnessedLeafWithMatchingManifest_IsReady()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 07");
        string expectedPath = ChromeExpectedPath();
        fake.SeedLeaf(AppBrowser.Chrome, expectedPath);
        fake.SeedManifest(expectedPath, ExpectedManifestJson(SyntheticHostPath, ChromeExtensionOrigin));

        Assert.Equal(CoordinatorReadiness.Ready, Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin));
    }

    // ==================================================================
    // 8/9 -- manifest content alone can never prove or grant ownership.
    // ==================================================================

    [Fact]
    public void Case08_UnwitnessedLeafAbsent_ManifestContentAlone_NeverProvesOwnership()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 08");
        string expectedPath = ChromeExpectedPath();
        // A perfectly valid-looking manifest sits at the expected path, but NO registry witness at all.
        fake.SeedManifest(expectedPath, ExpectedManifestJson(SyntheticHostPath, ChromeExtensionOrigin));

        var readiness = Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.NotEqual(CoordinatorReadiness.Ready, readiness);
        Assert.Equal(CoordinatorReadiness.OrphanBlocked, readiness);
    }

    [Fact]
    public void Case09_ForeignWitness_NeverBecomesReady_EvenWithExactlyMatchingManifestContent()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 09");
        string expectedPath = ChromeExpectedPath();
        fake.SeedLeaf(AppBrowser.Chrome, @"C:\SomeOtherVendor\different-host.json"); // FOREIGN witness
        // Manifest content at the EXPECTED path matches perfectly -- must still never matter.
        fake.SeedManifest(expectedPath, ExpectedManifestJson(SyntheticHostPath, ChromeExtensionOrigin));

        var readiness = Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.ForeignBlocked, readiness);
        Assert.NotEqual(CoordinatorReadiness.Ready, readiness);
    }

    // ==================================================================
    // 10/11/12 -- exact state-boundary classification.
    // ==================================================================

    [Fact]
    public void Case10_LeafAbsent_ExpectedManifestPresent_IsOrphanBlocked()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 10");
        fake.SeedManifest(ChromeExpectedPath(), "{\"unrelated\":true}");

        Assert.Equal(CoordinatorReadiness.OrphanBlocked, Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin));
    }

    [Fact]
    public void Case11_ExactWitness_MissingManifest_IsOwnedNeedsRepair()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 11");
        fake.SeedLeaf(AppBrowser.Chrome, ChromeExpectedPath()); // exact witness, no manifest seeded

        Assert.Equal(CoordinatorReadiness.OwnedNeedsRepair, Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin));
    }

    [Fact]
    public void Case12_ExactWitness_MismatchedManifestPayload_IsOwnedNeedsRepair()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 12");
        string expectedPath = ChromeExpectedPath();
        fake.SeedLeaf(AppBrowser.Chrome, expectedPath);
        // Witnessed and manifest exists, but content belongs to a DIFFERENT host path / stale origin.
        fake.SeedManifest(expectedPath, ExpectedManifestJson(@"C:\Stale\OldPRIVON.exe", ChromeExtensionOrigin));

        Assert.Equal(CoordinatorReadiness.OwnedNeedsRepair, Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin));
    }

    // ==================================================================
    // 13/14/15 -- PROVISION on FRESH.
    // ==================================================================

    [Fact]
    public void Case13_ProvisionOnFresh_InstallsExactlyOnce_ForExactBrowserAndSpec()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 13");
        string expectedPath = ChromeExpectedPath();

        Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(1, fake.CallLog.Count(e => e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal)));
        Assert.Equal(1, fake.CallLog.Count(e => e.StartsWith("WriteManifest(", StringComparison.Ordinal)));
        Assert.Equal(expectedPath, fake.LeafValue(AppBrowser.Chrome), StringComparer.Ordinal);
        Assert.Equal(ExpectedManifestJson(SyntheticHostPath, ChromeExtensionOrigin), fake.ManifestContentAt(expectedPath), StringComparer.Ordinal);
    }

    [Fact]
    public void Case14_ProvisionOnFresh_SuccessRequiresPostStateReady()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 14");

        var result = Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.Ready, result);
        Assert.Equal(CoordinatorReadiness.Ready, Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin));
    }

    [Fact]
    public void Case15_ProvisionOnFresh_SilentRegistrarNoOp_IsNeverReportedAsSuccess()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 15");
        // Simulates a registrar/environment mutation that silently fails to persist: writes are
        // swallowed rather than thrown, so Install() itself reports nothing (void), and the leaf/
        // manifest state afterward is exactly as if nothing happened.
        fake.ThrowOnSetSubkeyDefaultValue = null; // explicit: NOT an exception path -- a silent no-op
        var silentlyIgnoringFake = new SilentlyIgnoringEnvironment();
        Harness.TryBuildCoordinator(silentlyIgnoringFake, () => SyntheticHostPath, out var silentHandle, out string reason);
        Assert.True(silentHandle is not null, reason);

        var result = Harness.Provision(silentHandle!, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.NotEqual(CoordinatorReadiness.Ready, result);
    }

    // ==================================================================
    // 16/17/18/19 -- PROVISION on every non-FRESH state performs ZERO mutation.
    // ==================================================================

    [Fact]
    public void Case16_ProvisionOnReady_PerformsZeroMutation()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 16");
        string expectedPath = ChromeExpectedPath();
        fake.SeedLeaf(AppBrowser.Chrome, expectedPath);
        fake.SeedManifest(expectedPath, ExpectedManifestJson(SyntheticHostPath, ChromeExtensionOrigin));

        var result = Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.Ready, result);
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("WriteManifest(", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("DeleteSubkey(", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("DeleteManifest(", StringComparison.Ordinal));
    }

    [Fact]
    public void Case17_ProvisionOnOwnedNeedsRepair_DoesNotAutoRepair()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 17");
        fake.SeedLeaf(AppBrowser.Chrome, ChromeExpectedPath()); // witnessed, no manifest

        var result = Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.OwnedNeedsRepair, result);
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("WriteManifest(", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("DeleteSubkey(", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("DeleteManifest(", StringComparison.Ordinal));
    }

    [Fact]
    public void Case18_ProvisionOnForeign_PerformsZeroMutation()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 18");
        fake.SeedLeaf(AppBrowser.Chrome, @"C:\SomeOtherVendor\different-host.json");

        var result = Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.ForeignBlocked, result);
        Assert.Equal(@"C:\SomeOtherVendor\different-host.json", fake.LeafValue(AppBrowser.Chrome), StringComparer.Ordinal);
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal)
            || e.StartsWith("DeleteSubkey(", StringComparison.Ordinal));
    }

    [Fact]
    public void Case19_ProvisionOnOrphan_PerformsZeroMutation()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 19");
        string expectedPath = ChromeExpectedPath();
        fake.SeedManifest(expectedPath, "{\"foreign\":true}");

        var result = Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.OrphanBlocked, result);
        Assert.True(fake.ManifestPresent(expectedPath));
        Assert.Equal("{\"foreign\":true}", fake.ManifestContentAt(expectedPath), StringComparer.Ordinal);
        Assert.False(fake.LeafPresent(AppBrowser.Chrome));
    }

    // ==================================================================
    // 20/21/22/23/24 -- REPAIR.
    // ==================================================================

    [Fact]
    public void Case20_RepairOnOwnedNeedsRepair_UninstallsThenInstalls_InThatOrder()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 20");
        string expectedPath = ChromeExpectedPath();
        fake.SeedLeaf(AppBrowser.Chrome, expectedPath);
        fake.SeedManifest(expectedPath, ExpectedManifestJson(@"C:\Stale\OldPRIVON.exe", ChromeExtensionOrigin));

        Harness.Repair(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        int deleteSubkeyIndex = fake.CallLog.FindIndex(e => e.StartsWith("DeleteSubkey(", StringComparison.Ordinal));
        int setDefaultIndex = fake.CallLog.FindIndex(e => e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal));
        Assert.True(deleteSubkeyIndex >= 0, "Repair must call Uninstall's DeleteSubkey.");
        Assert.True(setDefaultIndex >= 0, "Repair must call Install's SetSubkeyDefaultValue.");
        Assert.True(deleteSubkeyIndex < setDefaultIndex, "Uninstall's mutation must precede Install's mutation.");
    }

    [Fact]
    public void Case21_RepairFinalSuccess_RequiresReady()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 21");
        string expectedPath = ChromeExpectedPath();
        fake.SeedLeaf(AppBrowser.Chrome, expectedPath);
        fake.SeedManifest(expectedPath, ExpectedManifestJson(@"C:\Stale\OldPRIVON.exe", ChromeExtensionOrigin));

        var result = Harness.Repair(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.Ready, result);
        Assert.Equal(ExpectedManifestJson(SyntheticHostPath, ChromeExtensionOrigin), fake.ManifestContentAt(expectedPath), StringComparer.Ordinal);
    }

    [Fact]
    public void Case22_RepairOnReady_DoesNotChurnRegistration()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 22");
        string expectedPath = ChromeExpectedPath();
        fake.SeedLeaf(AppBrowser.Chrome, expectedPath);
        fake.SeedManifest(expectedPath, ExpectedManifestJson(SyntheticHostPath, ChromeExtensionOrigin));

        var result = Harness.Repair(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.Ready, result);
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("DeleteSubkey(", StringComparison.Ordinal)
            || e.StartsWith("DeleteManifest(", StringComparison.Ordinal)
            || e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal)
            || e.StartsWith("WriteManifest(", StringComparison.Ordinal));
    }

    [Fact]
    public void Case23_RepairOnForeign_PerformsZeroMutation()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 23");
        fake.SeedLeaf(AppBrowser.Chrome, @"C:\SomeOtherVendor\different-host.json");

        var result = Harness.Repair(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.ForeignBlocked, result);
        Assert.Equal(@"C:\SomeOtherVendor\different-host.json", fake.LeafValue(AppBrowser.Chrome), StringComparer.Ordinal);
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("DeleteSubkey(", StringComparison.Ordinal)
            || e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal));
    }

    [Fact]
    public void Case24_RepairOnOrphan_PerformsZeroMutation()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 24");
        string expectedPath = ChromeExpectedPath();
        fake.SeedManifest(expectedPath, "{\"foreign\":true}");

        var result = Harness.Repair(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.OrphanBlocked, result);
        Assert.True(fake.ManifestPresent(expectedPath));
        Assert.False(fake.LeafPresent(AppBrowser.Chrome));
    }

    // ==================================================================
    // 24B -- Gate E5G.P3.B closure: proves the composed security boundary end-to-end, not merely
    // ordering. Repair legitimately starts from OWNED_NEEDS_REPAIR, but the environment mechanically
    // transitions to an unwitnessed orphan (leaf deleted, manifest survives) DURING Uninstall --
    // before Install runs. Install must decline (its own frozen orphan guard, E5C case 17) rather
    // than adopt the surviving manifest by writing a fresh registry witness over it. Final result
    // must be OrphanBlocked, never Ready -- no false success from a mid-repair race/no-op.
    // ==================================================================

    [Fact]
    public void Case24B_RepairTransitionsToOrphanDuringUninstall_InstallNeverAdopts_ResultIsOrphanBlocked()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 24B");
        string expectedPath = ChromeExpectedPath();
        fake.SeedLeaf(AppBrowser.Chrome, expectedPath); // exact registry witness
        fake.SeedManifest(expectedPath, ExpectedManifestJson(@"C:\Stale\OldPRIVON.exe", ChromeExtensionOrigin)); // mismatched -> OwnedNeedsRepair
        Assert.Equal(CoordinatorReadiness.OwnedNeedsRepair, Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin));

        // DeleteManifest mechanically no-ops (manifest survives) while DeleteSubkey still succeeds --
        // the real intermediate state genuinely becomes an unwitnessed orphan before Install runs.
        fake.SuppressNextDeleteManifest = true;
        fake.CallLog.Clear();

        var result = Harness.Repair(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Contains(fake.CallLog, e => e.StartsWith("DeleteManifest(", StringComparison.Ordinal));
        Assert.Contains(fake.CallLog, e => e.StartsWith("DeleteSubkey(", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.CallLog, e => e.StartsWith("WriteManifest(", StringComparison.Ordinal));

        Assert.Equal(CoordinatorReadiness.OrphanBlocked, result);
        Assert.NotEqual(CoordinatorReadiness.Ready, result);
        Assert.True(fake.ManifestPresent(expectedPath)); // survived -- never overwritten/adopted
        Assert.False(fake.LeafPresent(AppBrowser.Chrome)); // leaf genuinely removed, never recreated
    }

    // ==================================================================
    // 25 -- a thrown registrar/environment exception never becomes a false success.
    // ==================================================================

    [Fact]
    public void Case25_InstallThrows_NeverBecomesFalseSuccess()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment
        {
            ThrowOnWriteManifest = new IOException("synthetic disk failure"),
        };
        var handle = BuildOrFail(fake, "case 25 (Provision)");

        var result = Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.NotEqual(CoordinatorReadiness.Ready, result);
    }

    [Fact]
    public void Case25B_UninstallThrows_DuringRepair_NeverBecomesFalseSuccess()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment
        {
            ThrowOnDeleteSubkey = new IOException("synthetic registry failure"),
        };
        var handle = BuildOrFail(fake, "case 25B (Repair)");
        string expectedPath = ChromeExpectedPath();
        fake.SeedLeaf(AppBrowser.Chrome, expectedPath);
        fake.SeedManifest(expectedPath, ExpectedManifestJson(@"C:\Stale\OldPRIVON.exe", ChromeExtensionOrigin));

        var result = Harness.Repair(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.NotEqual(CoordinatorReadiness.Ready, result);
    }

    // ==================================================================
    // 26/27 -- Chrome and Edge operations are mechanically independent; no cross-browser mutation.
    // ==================================================================

    // AUDIT-CORRECTED (BrowserMutationClassifier): the previous oracle here --
    // e.Contains("Edge"/"Chrome", StringComparison.Ordinal) -- can never detect a manifest-only
    // mutation, since WriteManifest/DeleteManifest log only the lowercase chrome-host.json/
    // edge-host.json PATH, never the capitalized word "Chrome"/"Edge". It was also never anchored to
    // a proven-genuine mutation of the TARGET browser, so a future no-op Provision could have passed
    // this test vacuously. Both cases now independently confirm the target starts Fresh, prove the
    // actual Provision call produces a real, classifier-detected mutation of the target AND reaches
    // Ready, then prove the opposite browser had ZERO mutation events across all four write
    // primitives (PRIMARY evidence) with final leaf/manifest absence kept only as SECONDARY evidence.
    [Fact]
    public void Case26_ChromeProvision_NeverMutatesEdge()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 26");

        // A freshly constructed fake has no leaf/manifest for either browser -- independently
        // confirm Chrome starts Fresh before triggering the actual mutation.
        Assert.Equal(CoordinatorReadiness.Fresh, Harness.Inspect(handle, AppBrowser.Chrome, ChromeExtensionOrigin));

        var chromeResult = Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        // Prove Chrome Provision genuinely mutated Chrome -- never a vacuous no-op.
        Assert.Equal(CoordinatorReadiness.Ready, chromeResult);
        Assert.Contains(fake.CallLog, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Chrome));

        // PRIMARY: zero Edge mutation events, individually for each of the four write primitives,
        // plus the combined restatement.
        Assert.DoesNotContain(fake.CallLog, e => BrowserMutationClassifier.IsSetSubkeyDefaultValueFor(e, AppBrowser.Edge));
        Assert.DoesNotContain(fake.CallLog, e => BrowserMutationClassifier.IsDeleteSubkeyFor(e, AppBrowser.Edge));
        Assert.DoesNotContain(fake.CallLog, e => BrowserMutationClassifier.IsWriteManifestFor(e, AppBrowser.Edge));
        Assert.DoesNotContain(fake.CallLog, e => BrowserMutationClassifier.IsDeleteManifestFor(e, AppBrowser.Edge));
        Assert.DoesNotContain(fake.CallLog, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Edge));

        // SECONDARY: final Edge state remains completely untouched.
        Assert.False(fake.LeafPresent(AppBrowser.Edge));
        Assert.False(fake.ManifestPresent(EdgeExpectedPath()));
    }

    [Fact]
    public void Case27_EdgeProvision_NeverMutatesChrome()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 27");

        Assert.Equal(CoordinatorReadiness.Fresh, Harness.Inspect(handle, AppBrowser.Edge, EdgeExtensionOrigin));

        var edgeResult = Harness.Provision(handle, AppBrowser.Edge, EdgeExtensionOrigin);

        // Prove Edge Provision genuinely mutated Edge -- never a vacuous no-op.
        Assert.Equal(CoordinatorReadiness.Ready, edgeResult);
        Assert.Contains(fake.CallLog, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Edge));

        // PRIMARY: zero Chrome mutation events, individually for each of the four write primitives,
        // plus the combined restatement.
        Assert.DoesNotContain(fake.CallLog, e => BrowserMutationClassifier.IsSetSubkeyDefaultValueFor(e, AppBrowser.Chrome));
        Assert.DoesNotContain(fake.CallLog, e => BrowserMutationClassifier.IsDeleteSubkeyFor(e, AppBrowser.Chrome));
        Assert.DoesNotContain(fake.CallLog, e => BrowserMutationClassifier.IsWriteManifestFor(e, AppBrowser.Chrome));
        Assert.DoesNotContain(fake.CallLog, e => BrowserMutationClassifier.IsDeleteManifestFor(e, AppBrowser.Chrome));
        Assert.DoesNotContain(fake.CallLog, e => BrowserMutationClassifier.IsMutationFor(e, AppBrowser.Chrome));

        // SECONDARY: final Chrome state remains completely untouched.
        Assert.False(fake.LeafPresent(AppBrowser.Chrome));
        Assert.False(fake.ManifestPresent(ChromeExpectedPath()));
    }

    [Fact]
    public void Case26B_ChromeFailure_NeverRollsBackAlreadySucceededEdge()
    {
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 26B");

        var edgeResult = Harness.Provision(handle, AppBrowser.Edge, EdgeExtensionOrigin);
        Assert.Equal(CoordinatorReadiness.Ready, edgeResult);

        // Chrome leaf is FOREIGN -- Chrome provisioning must decline, but Edge's already-successful
        // registration must remain completely untouched (no cross-browser rollback).
        fake.SeedLeaf(AppBrowser.Chrome, @"C:\SomeOtherVendor\different-host.json");
        var chromeResult = Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        Assert.Equal(CoordinatorReadiness.ForeignBlocked, chromeResult);
        Assert.Equal(CoordinatorReadiness.Ready, Harness.Inspect(handle, AppBrowser.Edge, EdgeExtensionOrigin));
    }

    // ==================================================================
    // 28 -- no Store/devmeasure identity is hard-coded anywhere in the coordinator or layout source.
    // ==================================================================

    [Fact]
    public void Case28_ProductionSource_ContainsNoStoreOrDevmeasureIdentity()
    {
        string? coordinatorPath = Harness.TryFindAppSourceFile("NativeMessagingHostRegistrationCoordinator");
        Assert.True(coordinatorPath is not null, "Gate E5G.P3 case 28: NativeMessagingHostRegistrationCoordinator.cs must exist.");
        string? readinessPath = Harness.TryFindAppSourceFile("NativeMessagingRegistrationReadiness");
        string source = File.ReadAllText(coordinatorPath!)
            + (readinessPath is not null ? File.ReadAllText(readinessPath) : "");

        Assert.DoesNotContain("aippijooaannjccdplmnfpjbgjppkoaa", source, StringComparison.Ordinal); // E5B dev ID
        Assert.DoesNotContain("chrome-extension://", source, StringComparison.Ordinal); // no synthetic/guessed origin literal
        // No plausible Chrome-extension-ID-shaped literal (32 lowercase a-p characters) anywhere.
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(source, "\"[a-p]{32}\""),
            "a Chrome-extension-ID-shaped literal was found in the coordinator source.");
    }

    // ==================================================================
    // 29 -- the trusted host path threaded into the manifest is EXACTLY the injected provider value.
    // ==================================================================

    [Fact]
    public void Case29_ProvisionedManifest_UsesExactlyTheInjectedHostPathProviderValue()
    {
        const string distinctSyntheticPath = @"D:\CustomInstall\PRIVON.exe";
        var fake = new FakeNativeMessagingHostRegistrationEnvironment();
        var handle = BuildOrFail(fake, "case 29", pathProvider: () => distinctSyntheticPath);

        Harness.Provision(handle, AppBrowser.Chrome, ChromeExtensionOrigin);

        string content = fake.ManifestContentAt(ChromeExpectedPath())!;
        Assert.Equal(ExpectedManifestJson(distinctSyntheticPath, ChromeExtensionOrigin), content, StringComparer.Ordinal);
        Assert.DoesNotContain(SyntheticHostPath, content, StringComparison.Ordinal);
    }

    // ==================================================================
    // 30 -- PrivonAppComposition contains no registration coordinator/Install wiring. Re-affirms
    // (never weakens) Gate031E5F_ProductionCompositionRedTests.Case7_8_9_CompositionSource_NeverReferencesRegistrarInstall
    // from this new type's own side, extended to the coordinator name specifically.
    // ==================================================================

    [Fact]
    public void Case30_PrivonAppCompositionSource_NeverReferencesRegistrationCoordinatorOrInstall()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Gate E5G.P3 case 30: could not locate src/Privon.App.");
        string path = Path.Combine(dir!.FullName, "src", "Privon.App", "PrivonAppComposition.cs");
        Assert.True(File.Exists(path), "src/Privon.App/PrivonAppComposition.cs must exist.");
        string source = File.ReadAllText(path);

        Assert.DoesNotContain(".Install(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeMessagingHostRegistrar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeMessagingHostRegistrationCoordinator", source, StringComparison.Ordinal);
    }

    // ==================================================================
    // Helper for case 15 -- an environment whose registry writes are silently swallowed (never
    // thrown, never persisted), simulating a registrar mutation that reports nothing wrong (void)
    // but genuinely did not take effect.
    // ==================================================================

    private sealed class SilentlyIgnoringEnvironment : INativeMessagingHostRegistrationEnvironment
    {
        public bool SubkeyExists(AppBrowser browser) => false;
        public string? GetSubkeyDefaultValue(AppBrowser browser) => null;
        public void SetSubkeyDefaultValue(AppBrowser browser, string manifestPath) { /* silently ignored */ }
        public void DeleteSubkey(AppBrowser browser) { /* silently ignored */ }
        public bool ManifestExists(string path) => false;
        public string? ReadManifest(string path) => null;
        public void WriteManifest(string path, string content) { /* silently ignored */ }
        public void DeleteManifest(string path) { /* silently ignored */ }
    }
}
