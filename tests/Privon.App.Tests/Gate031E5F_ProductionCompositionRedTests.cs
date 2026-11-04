using System.Reflection;
using Privon.Browser;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5F -- RED-ONLY coverage for production composition of the Web authorization
// machinery, while the production browser-extension identity/origin allowlist remains EMPTY (so
// Web authorization stays fail-closed regardless of source construction). Follows the established
// Gate0D/Gate031F6H/Gate031F6K reflection-based convention for not-yet-existing members: every test
// locates its target member by reflection, so the assembly still builds against unchanged
// production code, and each fails with a specific, case-attributable message rather than a compile
// error.
//
// FROZEN BOUNDARY THIS GATE MUST NOT CROSS: no real com.privon.host registration, no registrar
// Install call, no non-empty production extension allowlist, no E4 challenge/decision-time logic
// change. See Gate031F6K_E4RedTests.G18_ProductionExtensionAllowlist_RemainsEmpty (unchanged,
// still-authoritative regression) for the allowlist half of this contract.
public class Gate031E5F_ProductionCompositionRedTests
{
    private static Assembly AppAssembly => typeof(WebBrowserGate).Assembly;
    private static Type? GetAppType(string name) => AppAssembly.GetType(name);

    private static string CreateTempStorageRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "PrivonE5FCompositionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static PrivonAppComposition CreateRealComposition(string storageRoot) =>
        new(storageRoot, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(),
            new ComposerTextReader(), new ForegroundChangeMonitor());

    // ==================================================================
    // 2/3 -- GREEN TARGET: composition constructs exactly one production WebClipboardAuthorizationSource
    // through the frozen registry/session/challenge machinery, not a bypass/fake source.
    // ==================================================================

    [Fact]
    public void Case2_3_StartedComposition_ExposesARealWebClipboardAuthorizationSource()
    {
        var property = GetAppType("Privon.App.PrivonAppComposition")?
            .GetProperty("WebAuthorizationSource", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(property is not null,
            "Gate E5F case 2/3: Privon.App.PrivonAppComposition must expose a " +
            "WebAuthorizationSource property (of type IWebClipboardAuthorizationSource?), non-null " +
            "after a successful Start() -- not present yet.");

        string storageRoot = CreateTempStorageRoot();
        using var composition = CreateRealComposition(storageRoot);
        composition.Start();
        try
        {
            object? source = property!.GetValue(composition);
            Assert.True(source is not null,
                "Gate E5F case 2/3: WebAuthorizationSource must be non-null after a successful Start().");
            Assert.Equal("WebClipboardAuthorizationSource", source!.GetType().Name);
        }
        finally
        {
            composition.Dispose();
            Directory.Delete(storageRoot, recursive: true);
        }
    }

    // ==================================================================
    // 4/5 -- production extension-origin allowlist is UNCHANGED by composition startup. As of Gate
    // E5G.3 it authorizes exactly the verified Chrome Store origin (no longer empty -- see
    // Gate031E5G3_ChromeOnlyProductionAllowlistRedTests.A and
    // Gate031F6K_E4RedTests.G18_ProductionExtensionAllowlist_IsExactlyTheVerifiedChromeOrigin_Gate031E5G3,
    // both GREEN) -- but composition Start() must never itself mutate this static, compile-time-fixed
    // set, so the exact content is re-asserted before and after Start() here rather than merely by
    // omission.
    // ==================================================================

    [Fact]
    public void Case4_5_ProductionAllowlist_IsUnchangedByCompositionStart()
    {
        var beforeStart = WebExtensionOriginAllowlist.Production.ToArray();

        string storageRoot = CreateTempStorageRoot();
        using var composition = CreateRealComposition(storageRoot);
        composition.Start();
        try
        {
            Assert.Single(WebExtensionOriginAllowlist.Production);
            Assert.Contains("chrome-extension://aieobgphcpmkfnhadocdhenigmackboo/", WebExtensionOriginAllowlist.Production);
            Assert.DoesNotContain("chrome-extension://fmdcgbjednllpjlogkcjmlocpnpbpljn/", WebExtensionOriginAllowlist.Production);
            Assert.Equal(beforeStart, WebExtensionOriginAllowlist.Production.ToArray());
        }
        finally
        {
            composition.Dispose();
            Directory.Delete(storageRoot, recursive: true);
        }
    }

    // ==================================================================
    // 6 -- source existence alone cannot authorize Web clipboard handling: with no live Native
    // Messaging channel ever admitted (impossible in production today -- no com.privon.host, empty
    // allowlist), AuthorizeAsync must return Outside (null) for any foreground.
    // ==================================================================

    [Fact]
    public async Task Case6_SourceExistenceAlone_CannotAuthorize_NoAcceptedSessionEverExists()
    {
        var property = GetAppType("Privon.App.PrivonAppComposition")?
            .GetProperty("WebAuthorizationSource", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(property is not null, "Gate E5F case 6: requires WebAuthorizationSource -- see case 2/3.");

        string storageRoot = CreateTempStorageRoot();
        using var composition = CreateRealComposition(storageRoot);
        composition.Start();
        try
        {
            var source = property!.GetValue(composition) as IWebClipboardAuthorizationSource;
            Assert.True(source is not null, "Gate E5F case 6: WebAuthorizationSource must be non-null after Start().");

            var foreground = new ForegroundTargetSnapshot(
                IsResolved: true, ProcessId: 999999, ProcessName: "chrome",
                PackageIdentity: Privon.Windows.PackageIdentityResolution.NoPackage, PackageFamilyName: null,
                ExecutableSignature: Privon.Windows.ExecutableSignatureResolution.Trusted, SignerOrganization: "Google LLC");

            var result = await source!.AuthorizeAsync(foreground);
            Assert.True(result is null,
                "Gate E5F case 6: a real source with no accepted session (the only state reachable in " +
                "production today) must authorize nothing -- source existence alone is not authorization.");
        }
        finally
        {
            composition.Dispose();
            Directory.Delete(storageRoot, recursive: true);
        }
    }

    // ==================================================================
    // 7/8/9 -- no registrar Install call, no Chrome/Edge com.privon.host mutation occurs anywhere
    // during composition/startup. Verified structurally: PrivonAppComposition's own source never
    // references Install/NativeMessagingHostRegistrar at all, AND the real machine's registry is
    // independently confirmed untouched by this same test run.
    //
    // THE CONTRACT IS "START DOES NOT CHANGE THE REGISTRATION", NOT "THERE IS NO REGISTRATION". A developer machine that ever pressed the
    // Settings button already has HKCU registrations (written by SettingsCoordinator -> NativeMessagingHostRegistrationCoordinator, never by
    // composition Start), so the real-machine test compares the state before and after Start -- key present or absent, every value with its kind
    // and data, subkeys, and the manifest file the default value points at -- and the snapshot itself is proven to see every kind of change
    // on a private scratch key that is never one of the real registrations.
    // ==================================================================

    [Fact]
    public void Case7_8_9_CompositionSource_NeverReferencesRegistrarInstall()
    {
        string? path = TryFindAppSourceFile("PrivonAppComposition");
        Assert.True(path is not null, "src/Privon.App/PrivonAppComposition.cs must exist.");
        string source = File.ReadAllText(path!);

        Assert.DoesNotContain(".Install(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeMessagingHostRegistrar", source, StringComparison.Ordinal);
    }

    private const string ChromeHostKey = @"Software\Google\Chrome\NativeMessagingHosts\com.privon.host";
    private const string EdgeHostKey = @"Software\Microsoft\Edge\NativeMessagingHosts\com.privon.host";

    /// <summary>
    /// A read-only, content-free description of one HKCU registration: absent, or present with every value (name, kind, data -- environment variables NOT
    /// expanded), every subkey, and -- for a default value that names a file -- that file's existence, length and last-write time. Two descriptions are
    /// equal exactly when nothing a registrar could create, change, retarget or delete differs.
    /// </summary>
    private static string DescribeRegistration(string subKeyPath)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
        if (key is null) return subKeyPath + ": absent";

        var sb = new System.Text.StringBuilder(subKeyPath + ": present");
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            var kind = key.GetValueKind(name);
            object? data = key.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
            sb.Append("\n  value '").Append(name).Append("' kind=").Append(kind).Append(" data=").Append(FormatData(data));
            if (name.Length == 0 && data is string path)
            {
                var file = new FileInfo(path);
                sb.Append(file.Exists
                    ? $"\n  manifest length={file.Length} lastWriteUtcTicks={file.LastWriteTimeUtc.Ticks}"
                    : "\n  manifest missing");
            }
        }
        foreach (string sub in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
            sb.Append("\n  subkey '").Append(sub).Append('\'');
        return sb.ToString();
    }

    private static string FormatData(object? data) => data switch
    {
        null => "(null)",
        byte[] bytes => Convert.ToHexString(bytes),
        string[] many => string.Join("\u0001", many),
        _ => Convert.ToString(data, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    private static void AssertRegistrationsUnchanged(string[] watched, string[] before, string phase)
    {
        for (int i = 0; i < watched.Length; i++)
        {
            string now = DescribeRegistration(watched[i]);
            Assert.True(before[i] == now,
                "Gate E5F case 7/8/9: composition " + phase + " changed " + watched[i] + "." + Environment.NewLine + "before: " + before[i] + Environment.NewLine + "after:  " + now);
        }
    }

    [Fact]
    public void Case7_8_9_RealMachine_CompositionStart_LeavesTheHostRegistrationsExactlyAsFound()
    {
        // whatever this machine has (nothing, or an old Settings-button registration), Start and Dispose must leave it exactly as found
        string[] watched = [ChromeHostKey, EdgeHostKey];
        string[] before = watched.Select(DescribeRegistration).ToArray();

        string storageRoot = CreateTempStorageRoot();
        using var composition = CreateRealComposition(storageRoot);
        try
        {
            composition.Start();
            AssertRegistrationsUnchanged(watched, before, "Start");
        }
        finally
        {
            composition.Dispose();
            Directory.Delete(storageRoot, recursive: true);
        }
        AssertRegistrationsUnchanged(watched, before, "Dispose");
    }

    [Fact]
    public void Case7_8_9_TheRealMachineAssertion_FiresOnAnUnauthorizedRegistrationChange_AbsentOrPresent()
    {
        // the same assertion the real-machine test uses, pointed at a private scratch path, with the test itself standing in for "Start did it"
        string scratchRoot = @"Software\PrivonTestScratch_" + Guid.NewGuid().ToString("N");
        string hostPath = scratchRoot + @"\NativeMessagingHosts\com.privon.host";
        string[] watched = [hostPath];
        var hkcu = Microsoft.Win32.Registry.CurrentUser;
        try
        {
            // no registration on the machine: nothing happening is fine; a registration appearing is not
            string[] absent = watched.Select(DescribeRegistration).ToArray();
            AssertRegistrationsUnchanged(watched, absent, "Start");
            using (var created = hkcu.CreateSubKey(hostPath))
            {
                created.SetValue("", @"C:\scratch\host.json");
                Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRegistrationsUnchanged(watched, absent, "Start"));

                // an existing registration: nothing happening is fine; a value changed or the registration deleted is not
                string[] present = watched.Select(DescribeRegistration).ToArray();
                AssertRegistrationsUnchanged(watched, present, "Start");
                created.SetValue("", @"C:\scratch\other-host.json");
                Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRegistrationsUnchanged(watched, present, "Start"));
            }
            string[] changed = watched.Select(DescribeRegistration).ToArray();
            hkcu.DeleteSubKeyTree(scratchRoot, throwOnMissingSubKey: false);
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRegistrationsUnchanged(watched, changed, "Start"));   // the registration deleted
        }
        finally
        {
            hkcu.DeleteSubKeyTree(scratchRoot, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Case7_8_9_RegistrationSnapshot_SeesEveryKindOfChange_WhetherTheRegistrationWasThereOrNot()
    {
        // A private scratch key under HKCU, unique to this run and removed at the end -- NEVER com.privon.host, Chrome's or Edge's. The same
        // DescribeRegistration that guards the real machine must tell every one of these apart.
        string scratchRoot = @"Software\PrivonTestScratch_" + Guid.NewGuid().ToString("N");
        string hostPath = scratchRoot + @"\NativeMessagingHosts\com.privon.host";
        string manifest = Path.Combine(Path.GetTempPath(), "PrivonScratchManifest_" + Guid.NewGuid().ToString("N") + ".json");
        var hkcu = Microsoft.Win32.Registry.CurrentUser;
        try
        {
            // ABSENT environment: untouched stays equal; creating it is a change
            string absent = DescribeRegistration(hostPath);
            Assert.Equal(absent, DescribeRegistration(hostPath));
            Assert.EndsWith(": absent", absent);

            using (var created = hkcu.CreateSubKey(hostPath))
            {
                string emptyKey = DescribeRegistration(hostPath);
                Assert.NotEqual(absent, emptyKey);                                    // created from nothing

                // PRESENT environment: untouched stays equal, and each kind of change is seen
                File.WriteAllText(manifest, "{}");
                created.SetValue("", manifest, Microsoft.Win32.RegistryValueKind.String);
                string present = DescribeRegistration(hostPath);
                Assert.NotEqual(emptyKey, present);                                   // a value added
                Assert.Equal(present, DescribeRegistration(hostPath));                // unchanged -> equal

                created.SetValue("", manifest + "2", Microsoft.Win32.RegistryValueKind.String);
                Assert.NotEqual(present, DescribeRegistration(hostPath));             // retargeted
                created.SetValue("", manifest, Microsoft.Win32.RegistryValueKind.String);
                Assert.Equal(present, DescribeRegistration(hostPath));                // restored -> equal again

                created.SetValue("", manifest, Microsoft.Win32.RegistryValueKind.ExpandString);
                Assert.NotEqual(present, DescribeRegistration(hostPath));             // same text, different kind
                created.SetValue("", manifest, Microsoft.Win32.RegistryValueKind.String);

                created.SetValue("extra", "x", Microsoft.Win32.RegistryValueKind.String);
                Assert.NotEqual(present, DescribeRegistration(hostPath));             // an extra value
                created.DeleteValue("extra");
                Assert.Equal(present, DescribeRegistration(hostPath));

                using (created.CreateSubKey("child")) { }
                Assert.NotEqual(present, DescribeRegistration(hostPath));             // an extra subkey
                created.DeleteSubKey("child");
                Assert.Equal(present, DescribeRegistration(hostPath));

                File.WriteAllText(manifest, "{ \"changed\": true }");
                Assert.NotEqual(present, DescribeRegistration(hostPath));             // the manifest the registration points at was rewritten
                File.WriteAllText(manifest, "{}");
                File.SetLastWriteTimeUtc(manifest, File.GetLastWriteTimeUtc(manifest).AddMinutes(1));
                Assert.NotEqual(present, DescribeRegistration(hostPath));             // ... even to the same length, at a different time
                File.Delete(manifest);
                Assert.NotEqual(present, DescribeRegistration(hostPath));             // the manifest removed

                created.DeleteValue("");
                Assert.NotEqual(present, DescribeRegistration(hostPath));             // the value deleted
            }

            hkcu.DeleteSubKeyTree(scratchRoot, throwOnMissingSubKey: false);
            Assert.Equal(absent, DescribeRegistration(hostPath));                     // and the whole registration deleted -> absent again
        }
        finally
        {
            hkcu.DeleteSubKeyTree(scratchRoot, throwOnMissingSubKey: false);
            if (File.Exists(manifest)) File.Delete(manifest);
        }
    }

    // ==================================================================
    // 11 -- disposal/lifecycle ownership is deterministic: Dispose() must be safe to call, and
    // WebAuthorizationSource must not throw or leak a resource needing explicit disposal (the type
    // itself is not IDisposable -- composition owns no extra teardown step for it beyond the
    // existing frozen Dispose() sequence).
    // ==================================================================

    [Fact]
    public void Case11_Dispose_IsSafeAndDeterministic_WithWebAuthorizationSourceComposed()
    {
        var property = GetAppType("Privon.App.PrivonAppComposition")?
            .GetProperty("WebAuthorizationSource", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(property is not null, "Gate E5F case 11: requires WebAuthorizationSource -- see case 2/3.");

        string storageRoot = CreateTempStorageRoot();
        var composition = CreateRealComposition(storageRoot);
        composition.Start();
        var exception = Record.Exception(() => composition.Dispose());
        Assert.Null(exception);
        Directory.Delete(storageRoot, recursive: true);
    }

    // ==================================================================
    // 12 -- source is not duplicated across repeated composition access: WebAuthorizationSource
    // returns the exact SAME instance on every read after Start() (singleton per composition
    // instance, matching this type's own established OBJECT_GRAPH/SHARED_IDENTITY discipline).
    // ==================================================================

    [Fact]
    public void Case12_WebAuthorizationSource_IsTheSameInstanceAcrossRepeatedAccess()
    {
        var property = GetAppType("Privon.App.PrivonAppComposition")?
            .GetProperty("WebAuthorizationSource", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(property is not null, "Gate E5F case 12: requires WebAuthorizationSource -- see case 2/3.");

        string storageRoot = CreateTempStorageRoot();
        using var composition = CreateRealComposition(storageRoot);
        composition.Start();
        try
        {
            object? first = property!.GetValue(composition);
            object? second = property!.GetValue(composition);
            Assert.True(first is not null && second is not null);
            Assert.Same(first, second);
        }
        finally
        {
            composition.Dispose();
            Directory.Delete(storageRoot, recursive: true);
        }
    }

    // ==================================================================
    // 13/14 -- no E5B dev extension identity, and no guessed/synthetic production extension origin,
    // anywhere in production composition source.
    // ==================================================================

    [Fact]
    public void Case13_14_CompositionSource_ContainsNoDevOrSyntheticExtensionIdentity()
    {
        string? path = TryFindAppSourceFile("PrivonAppComposition");
        Assert.True(path is not null, "src/Privon.App/PrivonAppComposition.cs must exist.");
        string source = File.ReadAllText(path!);

        Assert.DoesNotContain("aippijooaannjccdplmnfpjbgjppkoaa", source, StringComparison.Ordinal); // E5B dev ID
        Assert.DoesNotContain("chrome-extension://", source, StringComparison.Ordinal); // no synthetic/guessed origin
    }

    // ==================================================================
    // 15/16 -- SupportedWebTarget remains the AI-service axis only; NativeMessagingBrowser remains
    // the Chrome/Edge axis only. Structural regression, unchanged by E5F.
    // ==================================================================

    [Fact]
    public void Case15_SupportedWebTarget_RemainsExactlyTheFiveAiServiceMembers()
    {
        var type = typeof(SupportedWebTarget);
        var names = Enum.GetNames(type).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var expected = new[] { "ChatGptWeb", "ClaudeWeb", "DeepSeekWeb", "GeminiWeb", "GrokWeb" }
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, names);
    }

    [Fact]
    public void Case16_NativeMessagingBrowser_RemainsExactlyChromeAndEdge()
    {
        var type = GetAppType("Privon.App.NativeMessagingBrowser");
        Assert.True(type is not null, "Privon.App.NativeMessagingBrowser must exist (Gate E5D, frozen).");
        var names = Enum.GetNames(type!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Chrome", "Edge" }, names);
    }

    // ==================================================================
    // Shared registry/session truth: WebServerRuntime and the composed WebClipboardAuthorizationSource
    // must consult the SAME WebChannelRegistry/WebChannelManager instances -- never two independent
    // registries that could disagree about channel truth. Proven by actual runtime reference identity
    // (ReferenceEquals against the real, started composition's own fields) -- not merely by the shape
    // of WebServerRuntime's public surface -- so a future regression that quietly constructs a second,
    // independent registry/manager pair for either side would fail this test even though every other
    // structural/shape check in this file would still pass.
    // ==================================================================

    [Fact]
    public void CaseSharedTruth_WebServerRuntimeExposesTheSameManagerTheAuthorizationSourceUses()
    {
        var compositionType = GetAppType("Privon.App.PrivonAppComposition");
        Assert.True(compositionType is not null, "Privon.App.PrivonAppComposition must exist.");

        var webServerField = compositionType!.GetField("_webServer", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(webServerField is not null,
            "Gate E5F shared-truth case: PrivonAppComposition must hold a private _webServer field -- not present yet.");

        var webRegistryField = compositionType.GetField("_webRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(webRegistryField is not null,
            "Gate E5F shared-truth case: PrivonAppComposition must hold a private _webRegistry field -- not present yet.");

        var authorizationSourceProperty = compositionType.GetProperty("WebAuthorizationSource",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(authorizationSourceProperty is not null,
            "Gate E5F shared-truth case: requires WebAuthorizationSource -- see case 2/3.");

        var webServerType = GetAppType("Privon.App.WebServerRuntime");
        var managerProperty = webServerType?.GetProperty("Manager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(managerProperty is not null,
            "Gate E5F shared-truth case: Privon.App.WebServerRuntime must expose its own WebChannelManager " +
            "(so composition can construct WebClipboardAuthorizationSource against the exact same instance) " +
            "-- not present yet.");

        var sourceType = GetAppType("Privon.App.WebClipboardAuthorizationSource");
        var sourceManagerField = sourceType?.GetField("_manager", BindingFlags.NonPublic | BindingFlags.Instance);
        var sourceRegistryField = sourceType?.GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(sourceManagerField is not null && sourceRegistryField is not null,
            "Gate E5F shared-truth case: WebClipboardAuthorizationSource must hold private _manager/_registry fields.");

        string storageRoot = CreateTempStorageRoot();
        using var composition = CreateRealComposition(storageRoot);
        composition.Start();
        try
        {
            object? webServer = webServerField!.GetValue(composition);
            Assert.True(webServer is not null,
                "Gate E5F shared-truth case: composition's own _webServer must be non-null after a successful Start().");

            object? webServerManager = managerProperty!.GetValue(webServer);
            Assert.True(webServerManager is not null, "Gate E5F shared-truth case: WebServerRuntime.Manager must be non-null.");

            object? authorizationSource = authorizationSourceProperty!.GetValue(composition);
            Assert.True(authorizationSource is not null,
                "Gate E5F shared-truth case: WebAuthorizationSource must be non-null after Start().");

            object? authorizationSourceManager = sourceManagerField!.GetValue(authorizationSource);
            Assert.True(authorizationSourceManager is not null,
                "Gate E5F shared-truth case: the composed source's own _manager must be non-null.");

            Assert.True(ReferenceEquals(authorizationSourceManager, webServerManager),
                "Gate E5F shared-truth case: WebServerRuntime and the composed WebClipboardAuthorizationSource " +
                "must consult the SAME WebChannelManager instance -- never two independently-constructed " +
                "managers that could disagree about accepted-session truth.");

            // Registry identity: WebServerRuntime deliberately never keeps a WebChannelRegistry field of its
            // own (only the transient WebChannelHostServer built inside StartOrNull does), so the only
            // production-API-safe cross-check available here is against composition's own _webRegistry field
            // -- the exact same instance PrivonAppComposition.Start() hands to BOTH
            // WebClipboardAuthorizationSource's constructor and WebServerRuntime.StartOrNull.
            object? compositionRegistry = webRegistryField!.GetValue(composition);
            object? authorizationSourceRegistry = sourceRegistryField!.GetValue(authorizationSource);
            Assert.True(compositionRegistry is not null && authorizationSourceRegistry is not null,
                "Gate E5F shared-truth case: both composition's own _webRegistry and the composed source's " +
                "own _registry must be non-null after Start().");
            Assert.True(ReferenceEquals(compositionRegistry, authorizationSourceRegistry),
                "Gate E5F shared-truth case: the composed WebClipboardAuthorizationSource must consult the " +
                "exact same WebChannelRegistry instance composition itself owns and hands to " +
                "WebServerRuntime.StartOrNull -- never an independently-constructed registry.");
        }
        finally
        {
            composition.Dispose();
            Directory.Delete(storageRoot, recursive: true);
        }
    }

    // ==================================================================
    // Helpers (mirrors Gate031F6K_E4RedTests's own TryFindAppSourceFile convention).
    // ==================================================================

    private static string? TryFindAppSourceFile(string typeNameWithoutExtension)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
            dir = dir.Parent;

        if (dir is null)
            return null;

        string path = Path.Combine(dir.FullName, "src", "Privon.App", typeNameWithoutExtension + ".cs");
        return File.Exists(path) ? path : null;
    }
}
