using System.Reflection;
using System.Text.Json;
using Privon.App;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5D.B -- MANIFEST CONTRACT CORRECTION RED for the production Native Messaging
// host registration contract (host name "com.privon.host", never the E5B dev-only
// "com.privon.devmeasure" -- E5B is completely FROZEN and untouched by this gate).
//
// Corrects TWO confirmed defects found while validating the Gate E5D implementation against the
// prior (Gate E5C.2R) version of this file:
//
//   1. TEST SELF-CONTAMINATION (cases 1/2): a post-Act assertion (`fake.SubkeyExists(otherBrowser)`)
//      itself logged a read to the shared FakeHostRegistrationEnvironment.CallLog, which a
//      subsequent "no cross-browser activity" check then mistook for registrar behavior. Fixed here
//      by snapshotting `fake.CallLog` immediately after each Act (`actLog`), before any further
//      assertion touches the fake -- every cross-browser/ordering check below inspects that frozen
//      snapshot, never the live, ever-growing CallLog.
//
//   2. MANIFEST CONTRACT GAP (Gate E5D.A audit, classification MULTIPLE_CONTRACT_GAPS): the prior
//      Install(browser) signature had no seam for a real host executable path or a verified
//      extension origin, and its manifest path was an unreviewed implementation assumption. Gate
//      E5D.B commander-freezes: the manifest PATH formula (independently re-derived below, never
//      discovered from production output -- see ExpectedManifestPath in
//      NativeMessagingHostRegistrarTestHarness.cs), a new composition-input type
//      (NativeMessagingHostRegistrationSpec, carrying ONLY HostExecutablePath/ExtensionOrigin, never
//      a manifest path), the new Install(browser, spec) signature, the complete manifest payload
//      shape, and the mutation ORDER for fresh install (registry witness before manifest) and owned
//      uninstall (manifest before leaf) -- so a partial failure always lands on the STALE side of the
//      ownership contract (still provable/recoverable), never on an unwitnessed orphan.
//
// NO PRODUCTION TYPE SATISFYING THIS CORRECTED CONTRACT EXISTS YET -- the current Gate E5D
// implementation (Install(browser) alone, old manifest path, incomplete payload) is EXPECTED to go
// RED against this file, deliberately, per Gate E5D.B's own instruction. Every test resolves the
// full frozen future shape via NativeMessagingHostRegistrarTestHarness.TryBuildHarness before acting,
// so once E5D.C corrects the implementation, execution continues straight through construction and
// invocation into real behavioral assertions without any test body needing to change.
public class Gate031E5C_NativeMessagingHostRegistrarRedTests
{
    // Gate 13 (RECOVERABILITY ORDER) test data.
    private const string ChromeHostExecutablePath = @"C:\Synthetic\PRIVON\Privon.Host.Chrome.exe";
    private const string EdgeHostExecutablePath = @"C:\Synthetic\PRIVON\Privon.Host.Edge.exe";
    private const string ChromeExtensionOrigin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/";
    private const string EdgeExtensionOrigin = "chrome-extension://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/";

    private static NativeMessagingHostRegistrarTestHarness.RegistrarHarness BuildHarnessOrFail(
        FakeHostRegistrationEnvironment fake, string caseLabel)
    {
        bool built = NativeMessagingHostRegistrarTestHarness.TryBuildHarness(fake, out var harness, out string reason);
        Assert.True(built, $"{caseLabel}: {reason}");
        return harness!;
    }

    private static object ChromeSpec(NativeMessagingHostRegistrarTestHarness.RegistrarHarness harness) =>
        NativeMessagingHostRegistrarTestHarness.BuildSpec(harness, ChromeHostExecutablePath, ChromeExtensionOrigin);

    private static object EdgeSpec(NativeMessagingHostRegistrarTestHarness.RegistrarHarness harness) =>
        NativeMessagingHostRegistrarTestHarness.BuildSpec(harness, EdgeHostExecutablePath, EdgeExtensionOrigin);

    /// <summary>Invokes Install(browser, spec) and returns an ACT-only snapshot of CallLog, captured
    /// BEFORE any post-Act assertion can pollute it (Gate E5D.B defect fix).</summary>
    private static string[] InstallAndSnapshot(
        NativeMessagingHostRegistrarTestHarness.RegistrarHarness harness,
        FakeHostRegistrationEnvironment fake, NativeMessagingBrowser browser, object spec)
    {
        harness.InstallMethod.Invoke(
            harness.RegistrarInstance,
            [browser == NativeMessagingBrowser.Chrome ? harness.ChromeBrowserValue : harness.EdgeBrowserValue, spec]);
        return [.. fake.CallLog];
    }

    /// <summary>Invokes Uninstall(browser) and returns an ACT-only snapshot of CallLog (same
    /// contamination fix as <see cref="InstallAndSnapshot"/>).</summary>
    private static string[] UninstallAndSnapshot(
        NativeMessagingHostRegistrarTestHarness.RegistrarHarness harness,
        FakeHostRegistrationEnvironment fake, NativeMessagingBrowser browser)
    {
        harness.UninstallMethod.Invoke(
            harness.RegistrarInstance,
            [browser == NativeMessagingBrowser.Chrome ? harness.ChromeBrowserValue : harness.EdgeBrowserValue]);
        return [.. fake.CallLog];
    }

    private static void AssertNoForbiddenMutation(string[] actLog, NativeMessagingBrowser browser, string caseLabel)
    {
        string tag = $"({browser})";
        Assert.True(
            actLog.Any(e => e.StartsWith("SubkeyExists" + tag, StringComparison.Ordinal)
                || e.StartsWith("GetSubkeyDefaultValue" + tag, StringComparison.Ordinal)),
            $"{caseLabel}: the registrar must actually read {browser}'s state (no read evidence in " +
            "the ACT-only operation trace).");

        Assert.DoesNotContain(actLog, e =>
            e.StartsWith("SetSubkeyDefaultValue" + tag, StringComparison.Ordinal)
            || e.StartsWith("DeleteSubkey" + tag, StringComparison.Ordinal));
        Assert.DoesNotContain(actLog, e => e.StartsWith("WriteManifest(", StringComparison.Ordinal));
        Assert.DoesNotContain(actLog, e => e.StartsWith("DeleteManifest(", StringComparison.Ordinal));
    }

    private static JsonElement ParseManifest(string? content)
    {
        Assert.False(string.IsNullOrEmpty(content), "Manifest content must not be null/empty.");
        return JsonDocument.Parse(content!).RootElement.Clone();
    }

    // ==================================================================
    // 1/2 -- FRESH INSTALL CREATES THE EXACT LEAF + MANIFEST FOR THAT BROWSER ONLY
    // ==================================================================

    // AUDIT-CORRECTED (BrowserMutationClassifier, reused -- not duplicated): the previous oracle here
    // -- e.Contains("(Edge)"/"(Chrome)", StringComparison.Ordinal) -- forbade ANY log entry merely
    // containing that substring (even a harmless read), yet could never detect a manifest-only
    // opposite-browser mutation in the first place, since WriteManifest/DeleteManifest log only the
    // lowercase chrome-host.json/edge-host.json PATH, never the capitalized word "Chrome"/"Edge".
    // Each case below now also explicitly classifier-proves the target browser's own genuine
    // mutation (not left to final-state assertions alone), and the opposite-browser zero-mutation
    // proof is PRIMARY (four individual write-primitive checks plus the combined restatement),
    // NativeMessagingBrowser (unqualified) throughout this file resolves to THIS test project's own
    // fixture-side enum (FakeHostRegistrationEnvironment.cs) -- BrowserMutationClassifier needs the
    // REAL Privon.App.NativeMessagingBrowser instead, so it is always passed fully qualified below.
    // Both enums are proven name-identical by construction (NativeMessagingHostRegistrarTestHarness.
    // MapBrowser parses the real enum's ToString() into this local one), and
    // NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath is this gate's own commander-
    // frozen INDEPENDENT re-derivation of the exact same formula
    // NativeMessagingHostRegistrationLayout.ExpectedManifestPath uses in production (its own header:
    // "if production computes a different path, the independence itself is what turns the suite
    // RED") -- so passing the real enum to the classifier here is safe and correct.
    [Fact]
    public void Gate031E5C_1_ChromeFreshInstall_CreatesExactChromeLeafAndManifest_EdgeUntouched()
    {
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, "Gate E5C case 1 (Chrome fresh install)");
        string expectedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);
        string edgeExpectedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Edge);

        var actLog = InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));

        // Prove Chrome Install genuinely mutated Chrome.
        Assert.Contains(actLog, e => BrowserMutationClassifier.IsMutationFor(e, Privon.App.NativeMessagingBrowser.Chrome));
        Assert.True(fake.SubkeyExists(NativeMessagingBrowser.Chrome));
        Assert.Equal(expectedPath, fake.GetSubkeyDefaultValue(NativeMessagingBrowser.Chrome));
        Assert.True(fake.ManifestExists(expectedPath));

        // PRIMARY: zero Edge mutation events, individually for each of the four write primitives,
        // plus the combined restatement.
        Assert.DoesNotContain(actLog, e => BrowserMutationClassifier.IsSetSubkeyDefaultValueFor(e, Privon.App.NativeMessagingBrowser.Edge));
        Assert.DoesNotContain(actLog, e => BrowserMutationClassifier.IsDeleteSubkeyFor(e, Privon.App.NativeMessagingBrowser.Edge));
        Assert.DoesNotContain(actLog, e => BrowserMutationClassifier.IsWriteManifestFor(e, Privon.App.NativeMessagingBrowser.Edge));
        Assert.DoesNotContain(actLog, e => BrowserMutationClassifier.IsDeleteManifestFor(e, Privon.App.NativeMessagingBrowser.Edge));
        Assert.DoesNotContain(actLog, e => BrowserMutationClassifier.IsMutationFor(e, Privon.App.NativeMessagingBrowser.Edge));

        // SECONDARY: final Edge state remains completely untouched.
        Assert.False(fake.SubkeyExists(NativeMessagingBrowser.Edge));
        Assert.False(fake.ManifestExists(edgeExpectedPath));
    }

    [Fact]
    public void Gate031E5C_2_EdgeFreshInstall_CreatesExactEdgeLeafAndManifest_ChromeUntouched()
    {
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, "Gate E5C case 2 (Edge fresh install)");
        string expectedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Edge);
        string chromeExpectedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);

        var actLog = InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Edge, EdgeSpec(harness));

        // Prove Edge Install genuinely mutated Edge.
        Assert.Contains(actLog, e => BrowserMutationClassifier.IsMutationFor(e, Privon.App.NativeMessagingBrowser.Edge));
        Assert.True(fake.SubkeyExists(NativeMessagingBrowser.Edge));
        Assert.Equal(expectedPath, fake.GetSubkeyDefaultValue(NativeMessagingBrowser.Edge));
        Assert.True(fake.ManifestExists(expectedPath));

        // PRIMARY: zero Chrome mutation events, individually for each of the four write primitives,
        // plus the combined restatement.
        Assert.DoesNotContain(actLog, e => BrowserMutationClassifier.IsSetSubkeyDefaultValueFor(e, Privon.App.NativeMessagingBrowser.Chrome));
        Assert.DoesNotContain(actLog, e => BrowserMutationClassifier.IsDeleteSubkeyFor(e, Privon.App.NativeMessagingBrowser.Chrome));
        Assert.DoesNotContain(actLog, e => BrowserMutationClassifier.IsWriteManifestFor(e, Privon.App.NativeMessagingBrowser.Chrome));
        Assert.DoesNotContain(actLog, e => BrowserMutationClassifier.IsDeleteManifestFor(e, Privon.App.NativeMessagingBrowser.Chrome));
        Assert.DoesNotContain(actLog, e => BrowserMutationClassifier.IsMutationFor(e, Privon.App.NativeMessagingBrowser.Chrome));

        // SECONDARY: final Chrome state remains completely untouched.
        Assert.False(fake.SubkeyExists(NativeMessagingBrowser.Chrome));
        Assert.False(fake.ManifestExists(chromeExpectedPath));
    }

    // ==================================================================
    // 3 -- DEFAULT VALUE IS THE OWNERSHIP WITNESS (independently-derived expected path)
    // ==================================================================

    [Fact]
    public void Gate031E5C_3_NearMissDefaultValue_IsClassifiedForeign_NotOwned()
    {
        const string caseLabel = "Gate E5C case 3 (default value is the exact ownership witness)";
        string expectedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);
        string nearMissPath = expectedPath + ".legacy";

        var fake = new FakeHostRegistrationEnvironment();
        fake.SeedForeign(NativeMessagingBrowser.Chrome, nearMissPath, "{\"name\":\"not-ours\"}");
        var harness = BuildHarnessOrFail(fake, caseLabel);

        var actLog = UninstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome);

        Assert.Equal(nearMissPath, fake.GetSubkeyDefaultValue(NativeMessagingBrowser.Chrome));
        Assert.True(fake.ManifestUnchanged(nearMissPath, "{\"name\":\"not-ours\"}"));
        AssertNoForbiddenMutation(actLog, NativeMessagingBrowser.Chrome, caseLabel);
    }

    // ==================================================================
    // 4/5 -- FOREIGN FAILS SAFE (install and uninstall directions)
    // ==================================================================

    [Fact]
    public void Gate031E5C_4_ForeignInstall_FailsSafe_NoMutation()
    {
        const string caseLabel = "Gate E5C case 4 (foreign install fails safe)";
        const string foreignManifestPath = @"C:\Foreign\other-host.json";
        const string foreignManifestContent = "{\"name\":\"com.other-vendor.host\"}";

        var fake = new FakeHostRegistrationEnvironment();
        fake.SeedForeign(NativeMessagingBrowser.Chrome, foreignManifestPath, foreignManifestContent);
        var harness = BuildHarnessOrFail(fake, caseLabel);

        var actLog = InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));

        Assert.Equal(foreignManifestPath, fake.GetSubkeyDefaultValue(NativeMessagingBrowser.Chrome));
        Assert.True(fake.ManifestUnchanged(foreignManifestPath, foreignManifestContent));
        AssertNoForbiddenMutation(actLog, NativeMessagingBrowser.Chrome, caseLabel);
    }

    [Fact]
    public void Gate031E5C_5_ForeignUninstall_FailsSafe_NoMutation()
    {
        const string caseLabel = "Gate E5C case 5 (foreign uninstall fails safe)";
        const string foreignManifestPath = @"C:\Foreign\other-host.json";
        const string foreignManifestContent = "{\"name\":\"com.other-vendor.host\"}";

        var fake = new FakeHostRegistrationEnvironment();
        fake.SeedForeign(NativeMessagingBrowser.Edge, foreignManifestPath, foreignManifestContent);
        var harness = BuildHarnessOrFail(fake, caseLabel);

        var actLog = UninstallAndSnapshot(harness, fake, NativeMessagingBrowser.Edge);

        Assert.Equal(foreignManifestPath, fake.GetSubkeyDefaultValue(NativeMessagingBrowser.Edge));
        Assert.True(fake.ManifestUnchanged(foreignManifestPath, foreignManifestContent));
        AssertNoForbiddenMutation(actLog, NativeMessagingBrowser.Edge, caseLabel);
    }

    // ==================================================================
    // 6 -- OWNED UNINSTALL IS EXACT
    // ==================================================================

    [Fact]
    public void Gate031E5C_6_OwnedUninstall_RemovesExactlyOwnedLeafAndManifest()
    {
        const string caseLabel = "Gate E5C case 6 (owned uninstall is exact)";
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, caseLabel);
        string ownedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);

        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));
        Assert.True(fake.SubkeyExists(NativeMessagingBrowser.Chrome)); // sanity: real OWNED established
        fake.SeedSibling(NativeMessagingBrowser.Chrome, "com.example.other", @"C:\Foreign\example-other.json");

        UninstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome);

        Assert.False(fake.SubkeyExists(NativeMessagingBrowser.Chrome));
        Assert.False(fake.ManifestExists(ownedPath));
        Assert.True(fake.SiblingSubkeyUnchanged(
            NativeMessagingBrowser.Chrome, "com.example.other", @"C:\Foreign\example-other.json"));
    }

    // ==================================================================
    // 7 -- STALE UNINSTALL IS EXACT
    // ==================================================================

    [Fact]
    public void Gate031E5C_7_StaleUninstall_RemovesExactlyOwnedLeaf_ManifestCleanupIsIdempotent()
    {
        const string caseLabel = "Gate E5C case 7 (STALE uninstall is exact)";
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, caseLabel);
        string stalePath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);
        string edgePath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Edge);

        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));
        fake.DeleteManifest(stalePath); // simulate external drift: OWNED -> STALE, outside the registrar.
        Assert.False(fake.ManifestExists(stalePath));

        fake.SeedSibling(NativeMessagingBrowser.Chrome, "com.example.other", @"C:\Foreign\example-other.json");
        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Edge, EdgeSpec(harness)); // real Edge OWNED.

        UninstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome);

        Assert.False(fake.SubkeyExists(NativeMessagingBrowser.Chrome));
        Assert.False(fake.ManifestExists(stalePath)); // idempotent: was already absent, stays absent.
        Assert.True(fake.SiblingSubkeyUnchanged(
            NativeMessagingBrowser.Chrome, "com.example.other", @"C:\Foreign\example-other.json"));
        Assert.Equal(edgePath, fake.GetSubkeyDefaultValue(NativeMessagingBrowser.Edge));
        Assert.True(fake.SubkeyExists(NativeMessagingBrowser.Edge));
    }

    // ==================================================================
    // 8/9 -- CHROME / EDGE INDEPENDENCE (both directions)
    // ==================================================================

    [Fact]
    public void Gate031E5C_8_ChromeInstall_LeavesExistingEdgeOwnershipCompletelyUnchanged()
    {
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, "Gate E5C case 8 (Chrome operation leaves Edge unchanged)");

        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Edge, EdgeSpec(harness));
        string edgePath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Edge);
        string edgeManifestContent = fake.ReadManifest(edgePath)!;

        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));

        Assert.Equal(edgePath, fake.GetSubkeyDefaultValue(NativeMessagingBrowser.Edge));
        Assert.True(fake.ManifestUnchanged(edgePath, edgeManifestContent));
    }

    [Fact]
    public void Gate031E5C_9_EdgeInstall_LeavesExistingChromeOwnershipCompletelyUnchanged()
    {
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, "Gate E5C case 9 (Edge operation leaves Chrome unchanged)");

        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));
        string chromePath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);
        string chromeManifestContent = fake.ReadManifest(chromePath)!;

        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Edge, EdgeSpec(harness));

        Assert.Equal(chromePath, fake.GetSubkeyDefaultValue(NativeMessagingBrowser.Chrome));
        Assert.True(fake.ManifestUnchanged(chromePath, chromeManifestContent));
    }

    // ==================================================================
    // 10 -- SIBLING SUBKEY / PARENT PRESERVATION
    // ==================================================================

    [Fact]
    public void Gate031E5C_10_SiblingSubkeysAreNeverTouchedByRealUninstall()
    {
        const string caseLabel = "Gate E5C case 10 (sibling subkey and parent preservation)";
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, caseLabel);

        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));
        fake.SeedSibling(NativeMessagingBrowser.Chrome, "com.example.other", @"C:\Foreign\example-other.json");
        fake.SeedSibling(NativeMessagingBrowser.Chrome, "com.vendor.tool", @"C:\Foreign\vendor-tool.json");

        UninstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome);

        Assert.True(fake.SiblingSubkeyUnchanged(
            NativeMessagingBrowser.Chrome, "com.example.other", @"C:\Foreign\example-other.json"));
        Assert.True(fake.SiblingSubkeyUnchanged(
            NativeMessagingBrowser.Chrome, "com.vendor.tool", @"C:\Foreign\vendor-tool.json"));
    }

    // ==================================================================
    // 11 -- MANIFEST OWNERSHIP BOUNDARY (no directory-wide/wildcard/recursive cleanup)
    // ==================================================================

    [Fact]
    public void Gate031E5C_11_ManifestCleanup_DeletesOnlyTheExactOwnedManifest()
    {
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, "Gate E5C case 11 (manifest ownership boundary)");
        string ownedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);
        string siblingManifestInSameDirectory =
            Path.Combine(Path.GetDirectoryName(ownedPath) ?? "", "totally-unrelated-tool.json");
        const string siblingManifestContent = "{\"name\":\"com.some-other-tool.host\"}";

        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));
        fake.SeedManifest(siblingManifestInSameDirectory, siblingManifestContent);

        UninstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome);

        Assert.False(fake.ManifestExists(ownedPath));
        Assert.True(fake.ManifestUnchanged(siblingManifestInSameDirectory, siblingManifestContent));
    }

    // ==================================================================
    // 12 -- UNWITNESSED ORPHAN MANIFEST BOUNDARY
    // ==================================================================

    [Fact]
    public void Gate031E5C_12_UnwitnessedOrphanManifest_IsNeverAutoDeletedOrAdopted()
    {
        string expectedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);
        const string orphanContent = "{\"name\":\"leftover-from-a-manual-experiment-not-created-by-privon\"}";

        var fake = new FakeHostRegistrationEnvironment();
        fake.SeedManifest(expectedPath, orphanContent);
        var harness = BuildHarnessOrFail(fake, "Gate E5C case 12 (unwitnessed orphan manifest boundary)");

        var actLog = UninstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome);

        Assert.False(fake.SubkeyExists(NativeMessagingBrowser.Chrome));
        Assert.True(fake.ManifestUnchanged(expectedPath, orphanContent));
        Assert.DoesNotContain(actLog, e => e.StartsWith("DeleteManifest(", StringComparison.Ordinal));
        Assert.DoesNotContain(actLog, e => e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal));
    }

    // ==================================================================
    // 13 -- COMPLETE MANIFEST PAYLOAD STRUCTURE
    // ==================================================================

    [Fact]
    public void Gate031E5C_13_FreshInstall_WritesCompleteManifestPayload()
    {
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, "Gate E5C case 13 (complete manifest payload)");
        string expectedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);

        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));

        var manifest = ParseManifest(fake.ReadManifest(expectedPath));
        Assert.Equal("com.privon.host", manifest.GetProperty("name").GetString());
        Assert.Equal("PRIVON Native Messaging Host", manifest.GetProperty("description").GetString());
        Assert.Equal(ChromeHostExecutablePath, manifest.GetProperty("path").GetString());
        Assert.Equal("stdio", manifest.GetProperty("type").GetString());

        var origins = manifest.GetProperty("allowed_origins");
        Assert.Equal(JsonValueKind.Array, origins.ValueKind);
        var originList = origins.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Single(originList);
        Assert.Equal(ChromeExtensionOrigin, originList[0]);
    }

    // ==================================================================
    // 14 -- CASE-ONLY PATH MISMATCH IS FOREIGN (Ordinal boundary, Gate E5D.B commander-frozen)
    // ==================================================================

    [Fact]
    public void Gate031E5C_14_CaseOnlyPathMismatch_IsClassifiedForeign_NotOwnedOrStale()
    {
        const string caseLabel = "Gate E5C case 14 (case-only path mismatch is FOREIGN)";
        string expectedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);
        string caseOnlyDifferentPath = expectedPath.ToUpperInvariant();
        Assert.NotEqual(expectedPath, caseOnlyDifferentPath, StringComparer.Ordinal); // sanity: genuinely differs ordinally.

        var fake = new FakeHostRegistrationEnvironment();
        fake.SeedForeign(NativeMessagingBrowser.Chrome, caseOnlyDifferentPath, "{\"name\":\"case-only-mismatch\"}");
        var harness = BuildHarnessOrFail(fake, caseLabel);

        var actLog = UninstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome);

        // FOREIGN, not OWNED/STALE: no mutation despite the case-insensitive near-match.
        Assert.Equal(caseOnlyDifferentPath, fake.GetSubkeyDefaultValue(NativeMessagingBrowser.Chrome));
        Assert.True(fake.ManifestUnchanged(caseOnlyDifferentPath, "{\"name\":\"case-only-mismatch\"}"));
        AssertNoForbiddenMutation(actLog, NativeMessagingBrowser.Chrome, caseLabel);
    }

    // ==================================================================
    // 15/16 -- FROZEN MUTATION ORDER (recoverability: a partial failure must land on STALE, never
    // on an unwitnessed orphan)
    // ==================================================================

    [Fact]
    public void Gate031E5C_15_FreshInstall_SetsRegistryWitnessBeforeWritingManifest()
    {
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, "Gate E5C case 15 (install: registry witness before manifest)");

        var actLog = InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));

        int witnessIndex = Array.FindIndex(actLog, e => e.StartsWith("SetSubkeyDefaultValue(Chrome", StringComparison.Ordinal));
        int manifestIndex = Array.FindIndex(actLog, e => e.StartsWith("WriteManifest(", StringComparison.Ordinal));
        Assert.True(witnessIndex >= 0, "SetSubkeyDefaultValue(Chrome, ...) must occur during a fresh install.");
        Assert.True(manifestIndex >= 0, "WriteManifest(...) must occur during a fresh install.");
        Assert.True(witnessIndex < manifestIndex,
            "Registry witness must be set BEFORE the manifest is written -- a manifest-write failure " +
            "after this order still leaves a provable, recoverable STALE state, never an unwitnessed orphan.");
    }

    [Fact]
    public void Gate031E5C_16_OwnedUninstall_DeletesManifestBeforeLeaf()
    {
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, "Gate E5C case 16 (owned uninstall: manifest before leaf)");

        InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));
        var actLog = UninstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome);

        int manifestIndex = Array.FindIndex(actLog, e => e.StartsWith("DeleteManifest(", StringComparison.Ordinal));
        int leafIndex = Array.FindIndex(actLog, e => e.StartsWith("DeleteSubkey(Chrome", StringComparison.Ordinal));
        Assert.True(manifestIndex >= 0, "DeleteManifest(...) must occur during an OWNED uninstall.");
        Assert.True(leafIndex >= 0, "DeleteSubkey(Chrome) must occur during an OWNED uninstall.");
        Assert.True(manifestIndex < leafIndex,
            "Manifest must be deleted BEFORE the registry leaf -- a leaf-deletion failure after this " +
            "order still leaves a provable, recoverable STALE state, never an unwitnessed orphan.");
    }

    // ==================================================================
    // 17 -- UNWITNESSED ORPHAN MANIFEST BOUNDARY, DIRECTLY THROUGH Install (Gate E5G.P3.B closure).
    // Case 12 already proves Uninstall never adopts/deletes an unwitnessed orphan; this closes the
    // matching Install-side gap: a file already sitting at the expected path with no registry
    // witness proving PRIVON put it there must never be adopted (registry witness created over it)
    // by a fresh Install either.
    // ==================================================================

    [Fact]
    public void Gate031E5C_17_UnwitnessedOrphanManifest_InstallNeverAdopts_NoMutation()
    {
        var fake = new FakeHostRegistrationEnvironment();
        var harness = BuildHarnessOrFail(fake, "Gate E5C case 17 (unwitnessed orphan manifest -- Install must never adopt)");
        string expectedPath = NativeMessagingHostRegistrarTestHarness.ExpectedManifestPath(NativeMessagingBrowser.Chrome);
        const string orphanContent = "{\"name\":\"leftover-from-a-manual-experiment-not-created-by-privon\"}";
        fake.SeedManifest(expectedPath, orphanContent);

        var actLog = InstallAndSnapshot(harness, fake, NativeMessagingBrowser.Chrome, ChromeSpec(harness));

        Assert.False(fake.SubkeyExists(NativeMessagingBrowser.Chrome));
        Assert.True(fake.ManifestUnchanged(expectedPath, orphanContent));
        Assert.DoesNotContain(actLog, e => e.StartsWith("SetSubkeyDefaultValue(", StringComparison.Ordinal));
        Assert.DoesNotContain(actLog, e => e.StartsWith("WriteManifest(", StringComparison.Ordinal));
        Assert.DoesNotContain(actLog, e => e.StartsWith("DeleteManifest(", StringComparison.Ordinal));
        Assert.DoesNotContain(actLog, e => e.StartsWith("DeleteSubkey(", StringComparison.Ordinal));
    }
}
