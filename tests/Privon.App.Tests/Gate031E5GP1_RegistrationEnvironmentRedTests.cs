using AppBrowser = Privon.App.NativeMessagingBrowser;
using Harness = Privon.App.Tests.Gate031E5GP1_RegistrationEnvironmentTestHarness;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5G.P1 -- RED-first behavioral coverage for the PRODUCTION concrete
// implementation of the FROZEN Privon.App.INativeMessagingHostRegistrationEnvironment (E5D).
//
// SCOPE: OS mechanics ONLY. This gate implements the Windows adapter BEHIND the frozen interface and
// reopens nothing above it. NativeMessagingHostRegistrar's ownership classification
// (OWNED/STALE/FOREIGN/unwitnessed-orphan), its Ordinal witness comparison, its frozen mutation
// ORDER, NativeMessagingHostRegistrationSpec, and NativeMessagingBrowser all remain E5D-FROZEN and
// are neither exercised nor altered here.
//
// FROZEN BOUNDARY THIS GATE MUST NOT CROSS: no registrar Install call, no NativeMessagingHostRegistrationSpec
// construction, no extension origin (real, dev, or synthetic), no allowlist activation, no real
// com.privon.host registration, and no real production manifest mutation. The registry half runs
// entirely against FakeRegistryLeafMechanics; the filesystem half runs against a disposable temp
// directory only.
//
// SECURITY-CRITICAL CASE: case 09. NativeMessagingHostRegistrar decides OWNED vs FOREIGN by comparing
// the registry DEFAULT VALUE to the expected manifest path with StringComparison.Ordinal, where the
// expected path is the ALREADY-EXPANDED %LOCALAPPDATA% form. If a registry read expanded a
// REG_EXPAND_SZ default value, a FOREIGN registration storing "%LOCALAPPDATA%\PRIVON\NativeMessaging\
// chrome-host.json" would expand into an exact match and be silently promoted to OWNED -- then
// deleted by Uninstall. Case 09 asserts rawness at BOTH layers: behaviorally through the adapter, and
// structurally in the real mechanics implementation that performs the actual registry read.
public class Gate031E5GP1_RegistrationEnvironmentRedTests
{
    private const string ExpandableForeignValue = @"%LOCALAPPDATA%\PRIVON\NativeMessaging\chrome-host.json";

    private static INativeMessagingHostRegistrationEnvironment BuildAdapterOrFail(
        FakeRegistryLeafMechanics fake, string caseLabel)
    {
        bool built = Harness.TryBuildAdapter(fake, out var adapter, out string reason);
        Assert.True(built, $"Gate E5G.P1 {caseLabel}: {reason}");
        return adapter!;
    }

    private static string CreateTempRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "PrivonE5GP1_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Reads the production source of ALL THREE new types (adapter, mechanics seam interface,
    /// and its real implementation), failing with a specific reason while any is still missing. Used by
    /// the capability-absence cases (21-25), which must hold for the REAL registry code -- not merely
    /// for the fake-driven behavioral path -- and equally for the seam that code sits behind: a
    /// forbidden capability re-entering through the interface would be just as reachable.</summary>
    private static string ReadProductionSourcesOrFail(string caseLabel)
    {
        string[] required =
        [
            "WindowsNativeMessagingHostRegistrationEnvironment",
            "IHkcuRegistryLeafMechanics",
            "HkcuRegistryLeafMechanics",
        ];

        var sources = new List<string>(required.Length);
        foreach (string typeName in required)
        {
            string? path = Harness.TryFindAppSourceFile(typeName);
            Assert.True(path is not null,
                $"Gate E5G.P1 {caseLabel}: src/Privon.App/{typeName}.cs must exist.");
            sources.Add(File.ReadAllText(path!));
        }

        return string.Join("\n", sources);
    }

    // ==================================================================
    // 01/02 -- construction is side-effect free (this gate explicitly does NOT wire registration).
    // ==================================================================

    [Fact]
    public void Case01_Construction_PerformsZeroRegistryMechanicsCalls()
    {
        var fake = new FakeRegistryLeafMechanics();
        _ = BuildAdapterOrFail(fake, "case 01");

        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Case02_Construction_PerformsZeroFilesystemMutation()
    {
        string tempRoot = CreateTempRoot();
        string productionManifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PRIVON", "NativeMessaging");
        bool productionDirExistedBefore = Directory.Exists(productionManifestDir);

        try
        {
            var fake = new FakeRegistryLeafMechanics();
            _ = BuildAdapterOrFail(fake, "case 02");

            Assert.Empty(Directory.GetFileSystemEntries(tempRoot));
            Assert.Equal(productionDirExistedBefore, Directory.Exists(productionManifestDir));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ==================================================================
    // 03/04/05/06 -- exact per-browser leaf mapping and total cross-browser isolation.
    // ==================================================================

    [Fact]
    public void Case03_Chrome_MapsToExactlyTheFrozenChromeLeafPath()
    {
        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 03");

        adapter.SubkeyExists(AppBrowser.Chrome);

        Assert.Equal([$"LeafExists({Harness.ChromeLeafSubKeyPath})"], fake.CallLog);
    }

    [Fact]
    public void Case04_Edge_MapsToExactlyTheFrozenEdgeLeafPath()
    {
        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 04");

        adapter.SubkeyExists(AppBrowser.Edge);

        Assert.Equal([$"LeafExists({Harness.EdgeLeafSubKeyPath})"], fake.CallLog);
    }

    [Fact]
    public void Case05_EveryChromeOperation_NeverAddressesTheEdgeLeaf()
    {
        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 05");

        adapter.SubkeyExists(AppBrowser.Chrome);
        adapter.GetSubkeyDefaultValue(AppBrowser.Chrome);
        adapter.SetSubkeyDefaultValue(AppBrowser.Chrome, @"C:\Synthetic\chrome-host.json");
        adapter.DeleteSubkey(AppBrowser.Chrome);

        Assert.All(fake.CallLog, entry =>
            Assert.DoesNotContain(Harness.EdgeLeafSubKeyPath, entry, StringComparison.Ordinal));
        Assert.False(fake.LeafPresent(Harness.EdgeLeafSubKeyPath));
    }

    [Fact]
    public void Case06_EveryEdgeOperation_NeverAddressesTheChromeLeaf()
    {
        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 06");

        adapter.SubkeyExists(AppBrowser.Edge);
        adapter.GetSubkeyDefaultValue(AppBrowser.Edge);
        adapter.SetSubkeyDefaultValue(AppBrowser.Edge, @"C:\Synthetic\edge-host.json");
        adapter.DeleteSubkey(AppBrowser.Edge);

        Assert.All(fake.CallLog, entry =>
            Assert.DoesNotContain(Harness.ChromeLeafSubKeyPath, entry, StringComparison.Ordinal));
        Assert.False(fake.LeafPresent(Harness.ChromeLeafSubKeyPath));
    }

    // ==================================================================
    // 07/08/09 -- read primitives: read-only, and RAW.
    // ==================================================================

    [Fact]
    public void Case07_SubkeyExists_IsReadOnlyMechanics_NeverWritesOrDeletes()
    {
        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 07");
        fake.SeedLeaf(Harness.ChromeLeafSubKeyPath, @"C:\Synthetic\seeded.json");

        Assert.True(adapter.SubkeyExists(AppBrowser.Chrome));
        Assert.False(adapter.SubkeyExists(AppBrowser.Edge));

        Assert.All(fake.CallLog, entry => Assert.StartsWith("LeafExists(", entry, StringComparison.Ordinal));
        Assert.Equal(@"C:\Synthetic\seeded.json", fake.RawValueAt(Harness.ChromeLeafSubKeyPath));
    }

    [Fact]
    public void Case08_GetSubkeyDefaultValue_ReturnsTheStoredValueByteForByteUnchanged()
    {
        // Mixed case, embedded spaces, no trailing separator -- anything trimmed, case-folded or
        // canonicalized would break the registrar's frozen Ordinal witness comparison.
        const string stored = @"C:\Program Files\PRIVON Vendor\Chrome Host.JSON";

        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 08");
        fake.SeedLeaf(Harness.ChromeLeafSubKeyPath, stored);

        string? actual = adapter.GetSubkeyDefaultValue(AppBrowser.Chrome);

        Assert.Equal(stored, actual, StringComparer.Ordinal);
    }

    [Fact]
    public void Case09_ExpandableDefaultValue_IsNeverExpanded_AtEitherLayer()
    {
        // BEHAVIORAL half: the adapter must hand back the literal, unexpanded string.
        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 09");
        fake.SeedLeaf(Harness.ChromeLeafSubKeyPath, ExpandableForeignValue);

        string? actual = adapter.GetSubkeyDefaultValue(AppBrowser.Chrome);

        Assert.Equal(ExpandableForeignValue, actual, StringComparer.Ordinal);
        Assert.StartsWith("%LOCALAPPDATA%", actual!, StringComparison.Ordinal);

        // The exact promotion this must prevent: expanded, this FOREIGN value would equal the
        // registrar's own expected manifest path verbatim and be silently reclassified as OWNED.
        string expandedForm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRIVON", "NativeMessaging", "chrome-host.json");
        Assert.NotEqual(expandedForm, actual, StringComparer.Ordinal);

        // STRUCTURAL half: the behavioral half above runs against a fake and therefore cannot prove
        // the REAL registry read is raw. The production mechanics must opt out of expansion explicitly
        // -- Microsoft.Win32's default RegistryValueOptions.None DOES expand REG_EXPAND_SZ.
        string productionSource = ReadProductionSourcesOrFail("case 09");
        Assert.Contains("DoNotExpandEnvironmentNames", productionSource, StringComparison.Ordinal);
    }

    // ==================================================================
    // 10/11/12/13 -- write/delete primitives address exactly one leaf and nothing above or beside it.
    // ==================================================================

    [Fact]
    public void Case10_SetSubkeyDefaultValue_WritesOnlyTheExactLeafDefaultValue()
    {
        const string manifestPath = @"C:\Synthetic\PRIVON\NativeMessaging\chrome-host.json";

        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 10");

        adapter.SetSubkeyDefaultValue(AppBrowser.Chrome, manifestPath);

        Assert.Equal([$"SetDefaultValue({Harness.ChromeLeafSubKeyPath}, {manifestPath})"], fake.CallLog);
        Assert.Equal(manifestPath, fake.RawValueAt(Harness.ChromeLeafSubKeyPath), StringComparer.Ordinal);
    }

    [Fact]
    public void Case11_SetSubkeyDefaultValue_WritesNoSiblingLeafAndNoOtherBrowserLeaf()
    {
        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 11");
        fake.SeedLeaf(@"Software\Google\Chrome\NativeMessagingHosts\com.foreign.host", @"C:\Foreign\host.json");

        adapter.SetSubkeyDefaultValue(AppBrowser.Chrome, @"C:\Synthetic\chrome-host.json");

        Assert.Single(fake.CallLog);
        Assert.False(fake.LeafPresent(Harness.EdgeLeafSubKeyPath));
        Assert.Equal(@"C:\Foreign\host.json",
            fake.RawValueAt(@"Software\Google\Chrome\NativeMessagingHosts\com.foreign.host"), StringComparer.Ordinal);
    }

    [Fact]
    public void Case12_DeleteSubkey_DeletesOnlyTheExactLeaf_LeavingTheOtherBrowserIntact()
    {
        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 12");
        fake.SeedLeaf(Harness.ChromeLeafSubKeyPath, @"C:\Synthetic\chrome-host.json");
        fake.SeedLeaf(Harness.EdgeLeafSubKeyPath, @"C:\Synthetic\edge-host.json");

        adapter.DeleteSubkey(AppBrowser.Chrome);

        Assert.Equal([$"DeleteLeaf({Harness.ChromeLeafSubKeyPath})"], fake.CallLog);
        Assert.False(fake.LeafPresent(Harness.ChromeLeafSubKeyPath));
        Assert.True(fake.LeafPresent(Harness.EdgeLeafSubKeyPath));
    }

    [Fact]
    public void Case13_DeleteSubkey_NeverRequestsParentOrSiblingDeletion()
    {
        const string sibling = @"Software\Google\Chrome\NativeMessagingHosts\com.other.vendor";

        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 13");
        fake.SeedLeaf(Harness.ChromeLeafSubKeyPath, @"C:\Synthetic\chrome-host.json");
        fake.SeedLeaf(sibling, @"C:\Other\host.json");

        adapter.DeleteSubkey(AppBrowser.Chrome);
        adapter.DeleteSubkey(AppBrowser.Edge);

        foreach (string entry in fake.CallLog)
        {
            Assert.DoesNotContain($"({Harness.ChromeParentSubKeyPath})", entry, StringComparison.Ordinal);
            Assert.DoesNotContain($"({Harness.EdgeParentSubKeyPath})", entry, StringComparison.Ordinal);
            Assert.DoesNotContain(sibling, entry, StringComparison.Ordinal);
        }

        Assert.True(fake.LeafPresent(sibling));
    }

    // ==================================================================
    // 14 -- an undefined browser value fails closed BEFORE any mechanics call.
    // ==================================================================

    [Fact]
    public void Case14_InvalidBrowser_ThrowsBeforeAnyMechanicsCall()
    {
        const AppBrowser invalid = (AppBrowser)999;

        var fake = new FakeRegistryLeafMechanics();
        var adapter = BuildAdapterOrFail(fake, "case 14");

        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.SubkeyExists(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.GetSubkeyDefaultValue(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.SetSubkeyDefaultValue(invalid, @"C:\Synthetic\x.json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.DeleteSubkey(invalid));

        // default(NativeMessagingBrowser) is deliberately not a defined member (the enum starts at 1).
        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.SubkeyExists(default));

        Assert.Empty(fake.CallLog);
    }

    // ==================================================================
    // 15..20 -- filesystem primitives, exercised against the REAL File/Directory mechanics in a
    // disposable temp directory (the frozen interface already takes an arbitrary caller-supplied
    // path, so no seam is needed or introduced here).
    // ==================================================================

    [Fact]
    public void Case15_ManifestExists_ChecksExactlyTheSuppliedPath()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            var adapter = BuildAdapterOrFail(new FakeRegistryLeafMechanics(), "case 15");
            string present = Path.Combine(tempRoot, "present.json");
            string absent = Path.Combine(tempRoot, "absent.json");
            File.WriteAllText(present, "{}");

            Assert.True(adapter.ManifestExists(present));
            Assert.False(adapter.ManifestExists(absent));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Case16_ReadManifest_ReadsExactlyTheSuppliedPath()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            var adapter = BuildAdapterOrFail(new FakeRegistryLeafMechanics(), "case 16");
            string target = Path.Combine(tempRoot, "target.json");
            string sibling = Path.Combine(tempRoot, "sibling.json");
            File.WriteAllText(target, "{\"target\":true}");
            File.WriteAllText(sibling, "{\"sibling\":true}");

            Assert.Equal("{\"target\":true}", adapter.ReadManifest(target), StringComparer.Ordinal);
            Assert.Null(adapter.ReadManifest(Path.Combine(tempRoot, "missing.json")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Case17_WriteManifest_WritesExactContentToExactPath()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            var adapter = BuildAdapterOrFail(new FakeRegistryLeafMechanics(), "case 17");
            string target = Path.Combine(tempRoot, "written.json");
            const string content = "{\"name\":\"com.privon.host\"}";

            adapter.WriteManifest(target, content);

            Assert.True(File.Exists(target));
            Assert.Equal(content, File.ReadAllText(target), StringComparer.Ordinal);
            Assert.Equal([target], Directory.GetFileSystemEntries(tempRoot));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Case18_WriteManifest_CreatesOnlyTheContainingDirectoryChain()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            var adapter = BuildAdapterOrFail(new FakeRegistryLeafMechanics(), "case 18");
            string nestedDir = Path.Combine(tempRoot, "PRIVON", "NativeMessaging");
            string target = Path.Combine(nestedDir, "chrome-host.json");
            Assert.False(Directory.Exists(nestedDir));

            adapter.WriteManifest(target, "{}");

            Assert.True(File.Exists(target));
            // Exactly the containing chain, and exactly the one file inside it -- nothing beside it.
            Assert.Equal([Path.Combine(tempRoot, "PRIVON")], Directory.GetFileSystemEntries(tempRoot));
            Assert.Equal([nestedDir], Directory.GetFileSystemEntries(Path.Combine(tempRoot, "PRIVON")));
            Assert.Equal([target], Directory.GetFileSystemEntries(nestedDir));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Case19_DeleteManifest_DeletesExactlyTheSuppliedFile()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            var adapter = BuildAdapterOrFail(new FakeRegistryLeafMechanics(), "case 19");
            string target = Path.Combine(tempRoot, "target.json");
            string sibling = Path.Combine(tempRoot, "sibling.json");
            File.WriteAllText(target, "{}");
            File.WriteAllText(sibling, "{}");

            adapter.DeleteManifest(target);

            Assert.False(File.Exists(target));
            Assert.True(File.Exists(sibling));

            // Idempotent: deleting an already-absent manifest is not a failure.
            adapter.DeleteManifest(target);
            Assert.True(File.Exists(sibling));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Case20_DeleteManifest_NeverRemovesTheContainingDirectoryOrASiblingDirectory()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            var adapter = BuildAdapterOrFail(new FakeRegistryLeafMechanics(), "case 20");
            string nestedDir = Path.Combine(tempRoot, "NativeMessaging");
            string siblingDir = Path.Combine(tempRoot, "OtherVendor");
            Directory.CreateDirectory(nestedDir);
            Directory.CreateDirectory(siblingDir);
            string target = Path.Combine(nestedDir, "chrome-host.json");
            string siblingFile = Path.Combine(siblingDir, "keep.json");
            File.WriteAllText(target, "{}");
            File.WriteAllText(siblingFile, "{}");

            adapter.DeleteManifest(target);

            Assert.False(File.Exists(target));
            Assert.True(Directory.Exists(nestedDir));
            Assert.True(Directory.Exists(siblingDir));
            Assert.True(File.Exists(siblingFile));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ==================================================================
    // 21..25 -- capability ABSENCE in the real production source. These must hold for the actual
    // registry/filesystem code, which the fake-driven cases above deliberately never execute.
    // ==================================================================

    [Fact]
    public void Case21_AdapterSource_ContainsNoOwnershipClassificationLogic()
    {
        string source = ReadProductionSourcesOrFail("case 21");

        // OWNED/STALE/FOREIGN/orphan classification belongs exclusively to NativeMessagingHostRegistrar
        // (E5D, frozen). The adapter is OS mechanics and must carry no judgment of its own.
        Assert.DoesNotContain("OWNED", source, StringComparison.Ordinal);
        Assert.DoesNotContain("STALE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FOREIGN", source, StringComparison.Ordinal);
        Assert.DoesNotContain("orphan", source, StringComparison.OrdinalIgnoreCase);

        // Nor may it know anything about store identity or the registrar's manifest payload.
        Assert.DoesNotContain("ExtensionOrigin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("allowed_origins", source, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome-extension://", source, StringComparison.Ordinal);
        Assert.DoesNotContain("aippijooaannjccdplmnfpjbgjppkoaa", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Case22_ProductionSource_HasNoHklmCapability()
    {
        string source = ReadProductionSourcesOrFail("case 22");

        Assert.DoesNotContain("LocalMachine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HKLM", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HKEY_LOCAL_MACHINE", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Registry.CurrentUser", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Case23_ProductionSource_HasNoWow6432NodeOrRegistryViewSelection()
    {
        string source = ReadProductionSourcesOrFail("case 23");

        Assert.DoesNotContain("WOW6432Node", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RegistryView", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry32", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RegistryKey.OpenBaseKey", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Case24_ProductionSource_HasNoRecursiveRegistryDeletionCapability()
    {
        string source = ReadProductionSourcesOrFail("case 24");

        Assert.DoesNotContain("DeleteSubKeyTree", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Case25_ProductionSource_HasNoRecursiveFilesystemDeletionCapability()
    {
        string source = ReadProductionSourcesOrFail("case 25");

        Assert.DoesNotContain("Directory.Delete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("recursive:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteDirectory", source, StringComparison.Ordinal);
    }
}
