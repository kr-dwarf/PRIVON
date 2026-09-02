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
    // 4/5 -- production extension-origin allowlist is EMPTY, keeping identity authorization
    // fail-closed. (Allowlist itself is E5D/E3-frozen and re-asserted, unchanged, by
    // Gate031F6K_E4RedTests.G18_ProductionExtensionAllowlist_RemainsEmpty -- not duplicated here.)
    // ==================================================================

    [Fact]
    public void Case4_5_ProductionAllowlist_RemainsEmpty_EvenAfterCompositionStarts()
    {
        string storageRoot = CreateTempStorageRoot();
        using var composition = CreateRealComposition(storageRoot);
        composition.Start();
        try
        {
            Assert.Empty(WebExtensionOriginAllowlist.Production);
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

    [Fact]
    public void Case7_8_9_RealMachine_NoComPrivonHostRegistrationAfterCompositionStart()
    {
        string storageRoot = CreateTempStorageRoot();
        using var composition = CreateRealComposition(storageRoot);
        composition.Start();
        try
        {
            using var chromeKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Google\Chrome\NativeMessagingHosts\com.privon.host");
            using var edgeKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Edge\NativeMessagingHosts\com.privon.host");
            Assert.True(chromeKey is null, "Gate E5F case 7/8/9: real Chrome com.privon.host must remain absent.");
            Assert.True(edgeKey is null, "Gate E5F case 7/8/9: real Edge com.privon.host must remain absent.");
        }
        finally
        {
            composition.Dispose();
            Directory.Delete(storageRoot, recursive: true);
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
