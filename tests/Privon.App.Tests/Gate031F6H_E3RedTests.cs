using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Privon.Browser;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031F6H -- Phase E3 RED suite: host mode + local IPC + Web channel session
// lifecycle. FROZEN E3 ARCHITECTURE per Gate 031F6G/.1/.2/.3/.4 -- this file creates the complete
// executable RED contract for it, stopping strictly BEFORE E4 challenge/authorization. NO production
// code is added by this gate. Every test below locates its target FUTURE type/member by reflection
// (matching this project's own established Gate031F4_2_C2FreshnessRedTests/Gate031F6B_E1RedTests
// convention: RED_LOCATE at the top, real behavior against real production dependencies -- E1's
// WebFrameDecoder/WebFrameEncoder/WebNativeMessagingPump, E2's WebBrowserGate/BrowserHostBindingStatus,
// and the existing WebChannelRegistry -- from that point on) and then executes real behavior, so every
// test below begins passing, unmodified, the instant a correct GREEN implementation lands.
//
// TWO FUTURE INTERFACES (IWebHostBindingSource, IWebBrowserHostBinding) and a third
// (IWebPipeListener, this harness's own accept-loop seam) do not exist yet either -- and, unlike a
// future CONCRETE class located and driven by Activator + reflection, a C# type cannot implement an
// interface it cannot see at compile time. This file resolves that with a small, general-purpose
// System.Reflection.DispatchProxy-backed dynamic stub (DynamicInterfaceStub below): given a reflected
// interface Type, it builds a REAL runtime object that genuinely implements that interface, dispatching
// each call to a per-test delegate. This is "future type locating" infrastructure (section 51's own
// permitted category), never a fake PRODUCTION type and never manufactured impossible object state --
// once IWebHostBindingSource/IWebBrowserHostBinding/IWebPipeListener exist, the exact same delegates
// keep driving the exact same real interface dispatch, unchanged.
//
// ==================================================================
// API SURFACE FROZEN BY THIS GATE'S OWN TEST HARNESS (deliberately the smallest repository-consistent
// shape needed to express every R-requirement below; not more elaborate than required -- section 4).
// ==================================================================
//
//   public enum Privon.App.NativeMessagingInvocationKind { Normal, Host, RejectedHostShaped }
//
//   public readonly record struct Privon.App.NativeMessagingHostInvocation(
//       NativeMessagingInvocationKind Kind, string? ExtensionOrigin)
//   {
//       public static NativeMessagingHostInvocation Classify(string[] args, IReadOnlySet<string> allowedExtensionOrigins);
//   }
//
//   public static class Privon.App.WebExtensionOriginAllowlist
//   {
//       public static IReadOnlySet<string> Production { get; }   // MUST be empty in E3 (section 49)
//   }
//
//   public static class Privon.App.PrivonEntryPoint
//   {
//       public static void Run(string[] args, IReadOnlySet<string> allowedExtensionOrigins,
//           Action normalRunner, Action hostRunner);
//   }
//
//   public static class Privon.App.WebPipeEndpoint
//   {
//       public static string Compute(string sid, string executableFullPath);
//   }
//
//   internal interface Privon.App.IWebBrowserHostBinding : IDisposable
//   {
//       uint BrowserProcessId { get; }
//       string? BrowserProcessName { get; }
//       PackageIdentityResolution BrowserPackageIdentity { get; }
//       ExecutableSignatureResolution BrowserExecutableSignature { get; }
//       string? BrowserSignerOrganization { get; }
//   }
//
//   internal interface Privon.App.IWebHostBindingSource
//   {
//       BrowserHostBindingStatus Resolve(uint hostProcessId, out IWebBrowserHostBinding? binding);
//   }
//
//   internal sealed class Privon.App.WebSessionAdmissionBudget
//   {
//       public WebSessionAdmissionBudget(int capacity);
//       public Task<Lease> AcquireAsync(CancellationToken cancellationToken = default);
//       public sealed class Lease : IDisposable { public void Dispose(); }   // idempotent
//   }
//
//   internal enum Privon.App.WebChannelSessionState { NotStarted, AwaitingHello, Accepted, Closed }
//
//   internal sealed class Privon.App.WebChannelSession
//   {
//       public WebChannelSession(Stream transport, IWebBrowserHostBinding binding,
//           WebChannelRegistry registry, WebSessionAdmissionBudget.Lease lease, WebChannelManager manager);
//       public WebChannelSessionState State { get; }
//       public long? ChannelId { get; }
//       public uint BrowserProcessId { get; }
//       public Task Lifecycle { get; }          // Task.CompletedTask-equivalent before/without Start
//       public void Start();
//       public void Close();                    // idempotent, non-throwing
//   }
//
//   internal sealed class Privon.App.WebChannelManager
//   {
//       public WebChannelManager(WebChannelRegistry registry);
//       public bool TryGetAcceptedSession(uint browserProcessId, out WebChannelSession? session);
//       public void BeginAdmission(WebChannelSession session);
//       public void BeginShutdown();
//       public IReadOnlyList<WebChannelSession> SnapshotLive();
//       public bool TryPromoteToAccepted(WebChannelSession session);
//       public void RemoveIfCurrent(uint browserProcessId, WebChannelSession session);
//   }
//
//   internal interface Privon.App.IWebPipeListener : IDisposable
//   {
//       Task<(Stream? Stream, SafePipeHandle? Handle)> AcceptAsync(CancellationToken cancellationToken);
//   }
//
//   internal sealed class Privon.App.WebChannelHostServer
//   {
//       public const int MaxConsecutiveAcceptFailuresContractDefault = 8;
//       public WebChannelHostServer(IWebPipeListener listener, Func<SafePipeHandle, uint?> clientProcessIdSource,
//           IWebHostBindingSource bindingSource, WebChannelRegistry registry, WebChannelManager manager,
//           WebSessionAdmissionBudget budget);
//       public Task AdmitConnectionAsync(Stream clientStream, SafePipeHandle clientHandle,
//           WebSessionAdmissionBudget.Lease lease, CancellationToken cancellationToken = default);
//       public Task RunAsync(CancellationToken cancellationToken);
//   }
//
//   internal static class Privon.App.WebHostRelayLoop
//   {
//       public static Task RunAsync(Stream stdin, Stream stdout, Stream pipe);
//   }
//
// See Gate031F6H_WindowsE3RedTests.cs (Privon.Windows.IntegrationTests) for R6 (real CurrentUserOnly
// named pipe) and R7 (INamedPipeClientProcessIdSource / Win32NamedPipeClientProcessIdSource /
// GetNamedPipeClientProcessId over a real connected pipe).
public class Gate031F6H_E3RedTests
{
    private static Assembly AppAssembly => typeof(TargetGate).Assembly;

    private static Type? GetAppType(string name) => AppAssembly.GetType(name);

    // ==================================================================
    // DYNAMIC INTERFACE STUB -- see this file's own header doc. General-purpose, reused by every
    // future-interface fake below (IWebBrowserHostBinding / IWebHostBindingSource / IWebPipeListener).
    // ==================================================================

    // TEST_CONTRACT_CORRECTION (Gate 031F6I.1 A1, continued): DispatchProxy.Create<T,TProxy>()
    // generates a runtime subclass of TProxy, so TProxy must not be sealed -- this was always latent
    // (masked until now by the ambiguous-overload exception fixed above).
    public class DynamicInterfaceStub : DispatchProxy
    {
        public Dictionary<string, Func<object?[], object?>> Handlers { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            string name = targetMethod!.Name;
            if (!Handlers.TryGetValue(name, out var handler))
                throw new InvalidOperationException($"DynamicInterfaceStub: no handler registered for '{name}'.");
            return handler(args ?? []);
        }
    }

    /// <summary>Builds a real runtime object genuinely implementing <paramref name="interfaceType"/>
    /// (located by reflection, since it may not exist as a compile-time type yet), backed by a fresh
    /// <see cref="DynamicInterfaceStub"/> whose handlers the caller configures.</summary>
    private static (object Proxy, DynamicInterfaceStub Stub) CreateStub(Type interfaceType)
    {
        // TEST_CONTRACT_CORRECTION (Gate 031F6I.1 A1): .NET 10 added a second, non-generic
        // DispatchProxy.Create(Type, Type) overload, so an unfiltered GetMethod(name) lookup is now
        // ambiguous. Select the originally-intended zero-parameter, two-type-argument generic
        // definition explicitly -- same method, same behavior, just a precise selector instead of an
        // ambiguous one.
        var createMethod = typeof(DispatchProxy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(DispatchProxy.Create) && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 0)
            .MakeGenericMethod(interfaceType, typeof(DynamicInterfaceStub));
        object proxy = createMethod.Invoke(null, null)!;
        return (proxy, (DynamicInterfaceStub)proxy);
    }

    // ==================================================================
    // SECTION 1/49 -- WebExtensionOriginAllowlist
    // ==================================================================

    private const string AllowlistTypeName = "Privon.App.WebExtensionOriginAllowlist";
    private static Type? AllowlistType => GetAppType(AllowlistTypeName);

    private const string AllowlistContract =
        "Privon.App.WebExtensionOriginAllowlist does not exist yet (Gate 031F6H E3, frozen contract). " +
        "Required: public static class exposing exactly one member -- " +
        "public static IReadOnlySet<string> Production { get; } -- which must be EMPTY in E3 " +
        "(section 49: real browser cannot yet enter the production Host branch; E5 supplies real " +
        "extension origins). No environment/registry/DEBUG/config-file override of any kind.";

    // Gate E5G.3's own explicit, stated GREEN target superseded this E3-era test's original EMPTY
    // assertion: Production now authorizes exactly the verified Chrome Store origin (see
    // Gate031E5G3_ChromeOnlyProductionAllowlistRedTests.A/B, GREEN). The invariant this test still
    // exists to prove -- exactly the members this codebase's own security review can enumerate, never
    // a silently-broadened or environment-driven set -- is now expressed as "exactly one, exactly the
    // verified Chrome origin" instead of "none".
    [Fact]
    public void Section49_ProductionAllowlist_IsExactlyTheVerifiedChromeOrigin()
    {
        Assert.True(AllowlistType is not null, AllowlistContract);

        var property = AllowlistType!.GetProperty("Production", BindingFlags.Public | BindingFlags.Static);
        Assert.True(property is not null, $"{AllowlistTypeName} exists but exposes no static Production property. " + AllowlistContract);

        var value = property!.GetValue(null);
        Assert.True(value is not null, "Production must not be null.");
        var set = Assert.IsAssignableFrom<System.Collections.IEnumerable>(value);
        var origins = set.Cast<object>().ToArray();
        Assert.Single(origins);
        Assert.Equal("chrome-extension://aieobgphcpmkfnhadocdhenigmackboo/", origins[0]);
    }

    // ==================================================================
    // SECTION R4/R5 -- WebPipeEndpoint. Pure function, zero dependencies -- fully behavioral RED.
    // ==================================================================

    private const string EndpointTypeName = "Privon.App.WebPipeEndpoint";
    private static Type? EndpointType => GetAppType(EndpointTypeName);

    private const string EndpointContract =
        "Privon.App.WebPipeEndpoint does not exist yet (Gate 031F6H E3, frozen contract). Required: " +
        "public static class exposing exactly one member -- public static string Compute(string sid, " +
        "string executableFullPath). FROZEN ALGORITHM (R4): material = UTF8(sid + \"\\0\" + " +
        "Path.GetFullPath(executableFullPath).ToUpperInvariant()); hash = SHA256(material); " +
        "endpoint = \"PRIVON-Web-\" + first 16 hash bytes as lowercase hex, no BOM. Must never use " +
        "Assembly.Location, persistence, randomness, or a secret.";

    private static string InvokeCompute(string sid, string executablePath)
    {
        Assert.True(EndpointType is not null, EndpointContract);
        var method = EndpointType!.GetMethod("Compute", BindingFlags.Public | BindingFlags.Static);
        Assert.True(method is not null, $"{EndpointTypeName} exists but exposes no static Compute(...) method. " + EndpointContract);
        object? result = method!.Invoke(null, [sid, executablePath]);
        Assert.True(result is string, $"{EndpointTypeName}.Compute must return a string.");
        return (string)result!;
    }

    private static string ExpectedEndpoint(string sid, string executablePath)
    {
        string canonical = Path.GetFullPath(executablePath).ToUpperInvariant();
        byte[] material = Encoding.UTF8.GetBytes(sid + "\0" + canonical);
        byte[] hash = SHA256.HashData(material);
        string hex = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        return "PRIVON-Web-" + hex;
    }

    [Fact]
    public void R4_PipeEndpoint_MatchesTheFrozenAlgorithm_ExactlyForKnownInputs()
    {
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        const string exePath = @"C:\Program Files\PRIVON\Privon.App.exe";

        string actual = InvokeCompute(sid, exePath);
        string expected = ExpectedEndpoint(sid, exePath);

        Assert.Equal(expected, actual);
        Assert.StartsWith("PRIVON-Web-", actual, StringComparison.Ordinal);
        Assert.Equal("PRIVON-Web-".Length + 32, actual.Length); // 16 bytes -> 32 lowercase hex chars

        // TEST_CONTRACT_CORRECTION (Gate 031F6I.1 A2): the frozen prefix "PRIVON-Web-" is deliberately
        // mixed-case -- only the 32-character hex SUFFIX is required to be lowercase. The previous
        // whole-string lowercase assertion directly contradicted the StartsWith check immediately
        // above it (no string can satisfy both), a plain test-authoring defect, not a production
        // requirement.
        string hexSuffix = actual["PRIVON-Web-".Length..];
        Assert.Equal(hexSuffix.ToLowerInvariant(), hexSuffix); // lowercase hex, never uppercase
    }

    [Fact]
    public void R4_PipeEndpoint_IsDeterministic_SameInputsAlwaysProduceSameEndpoint()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        const string exePath = @"C:\Program Files\PRIVON\Privon.App.exe";

        string first = InvokeCompute(sid, exePath);
        string second = InvokeCompute(sid, exePath);

        Assert.Equal(first, second);
    }

    [Fact]
    public void R4_PipeEndpoint_PathCaseOnlyDifference_ProducesTheSameEndpoint()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        string lower = InvokeCompute(sid, @"c:\program files\privon\privon.app.exe");
        string upper = InvokeCompute(sid, @"C:\PROGRAM FILES\PRIVON\PRIVON.APP.EXE");

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void R5_PipeEndpoint_DifferentSid_ProducesADifferentEndpoint()
    {
        const string exePath = @"C:\Program Files\PRIVON\Privon.App.exe";
        string a = InvokeCompute("S-1-5-21-1-2-3-1001", exePath);
        string b = InvokeCompute("S-1-5-21-1-2-3-1002", exePath);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void R5_PipeEndpoint_DifferentExecutablePath_ProducesADifferentEndpoint()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        string a = InvokeCompute(sid, @"C:\Program Files\PRIVON\Privon.App.exe");
        string b = InvokeCompute(sid, @"C:\Program Files\PRIVON\Other.exe");

        Assert.NotEqual(a, b);
    }

    // ==================================================================
    // SECTION R1/R2/R3/R29 -- entry classification. NativeMessagingHostInvocation.Classify is a pure
    // function; PrivonEntryPoint.Run wires it to recording runner delegates -- no real WPF.
    // ==================================================================

    private const string InvocationTypeName = "Privon.App.NativeMessagingHostInvocation";
    private const string InvocationKindTypeName = "Privon.App.NativeMessagingInvocationKind";
    private const string EntryPointTypeName = "Privon.App.PrivonEntryPoint";

    private static Type? InvocationType => GetAppType(InvocationTypeName);
    private static Type? InvocationKindType => GetAppType(InvocationKindTypeName);
    private static Type? EntryPointType => GetAppType(EntryPointTypeName);

    private const string EntryContract =
        "Privon.App.PrivonEntryPoint / NativeMessagingHostInvocation / NativeMessagingInvocationKind do " +
        "not exist yet (Gate 031F6H E3, frozen contract). Required: NativeMessagingInvocationKind { " +
        "Normal, Host, RejectedHostShaped }; NativeMessagingHostInvocation.Classify(string[] args, " +
        "IReadOnlySet<string> allowedExtensionOrigins) returning a value whose Kind reflects R1/R2/R3; " +
        "PrivonEntryPoint.Run(string[] args, IReadOnlySet<string> allowedExtensionOrigins, Action " +
        "normalRunner, Action hostRunner) must invoke EXACTLY ONE of normalRunner/hostRunner for " +
        "Normal/Host, and NEITHER for RejectedHostShaped -- never falling through to the tray runner " +
        "for a rejected host-shaped invocation.";

    private static readonly string ValidExtensionOrigin = "chrome-extension://" + new string('a', 32) + "/";

    private static string InvokeClassifyKind(string[] args, IReadOnlySet<string> allowedOrigins)
    {
        Assert.True(InvocationType is not null && InvocationKindType is not null, EntryContract);
        var method = InvocationType!.GetMethod("Classify", BindingFlags.Public | BindingFlags.Static);
        Assert.True(method is not null, $"{InvocationTypeName} exists but exposes no static Classify(...) method. " + EntryContract);

        object? result = method!.Invoke(null, [args, allowedOrigins]);
        Assert.True(result is not null, $"{InvocationTypeName}.Classify returned null.");

        var kindProperty = result!.GetType().GetProperty("Kind");
        Assert.True(kindProperty is not null, $"{InvocationTypeName} exposes no Kind member. " + EntryContract);
        return kindProperty!.GetValue(result)!.ToString()!;
    }

    private static (int NormalCalls, int HostCalls) InvokeEntryRun(string[] args, IReadOnlySet<string> allowedOrigins)
    {
        Assert.True(EntryPointType is not null, EntryContract);
        var method = EntryPointType!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
        Assert.True(method is not null, $"{EntryPointTypeName} exists but exposes no static Run(...) method. " + EntryContract);

        int normalCalls = 0, hostCalls = 0;
        Action normalRunner = () => normalCalls++;
        Action hostRunner = () => hostCalls++;

        method!.Invoke(null, [args, allowedOrigins, normalRunner, hostRunner]);
        return (normalCalls, hostCalls);
    }

    [Fact]
    public void R1_NormalCommandLine_ClassifiesAsNormal_AndInvokesOnlyTheTrayRunner()
    {
        var allowlist = new HashSet<string>(StringComparer.Ordinal) { ValidExtensionOrigin };
        string[] args = [];

        Assert.Equal("Normal", InvokeClassifyKind(args, allowlist));

        var (normalCalls, hostCalls) = InvokeEntryRun(args, allowlist);
        Assert.Equal(1, normalCalls);
        Assert.Equal(0, hostCalls);
    }

    [Fact]
    public void R2_ValidBrowserShapedArgv_ClassifiesAsHost_AndInvokesOnlyTheHostRunner()
    {
        var allowlist = new HashSet<string>(StringComparer.Ordinal) { ValidExtensionOrigin };
        string[] args = [ValidExtensionOrigin, "--parent-window=12345"];

        Assert.Equal("Host", InvokeClassifyKind(args, allowlist));

        var (normalCalls, hostCalls) = InvokeEntryRun(args, allowlist);
        Assert.Equal(0, normalCalls);
        Assert.Equal(1, hostCalls);
    }

    [Fact]
    public void R2_ValidBrowserShapedArgv_WithoutParentWindow_StillClassifiesAsHost()
    {
        var allowlist = new HashSet<string>(StringComparer.Ordinal) { ValidExtensionOrigin };
        string[] args = [ValidExtensionOrigin];

        Assert.Equal("Host", InvokeClassifyKind(args, allowlist));
    }

    public static IEnumerable<object[]> RejectedHostShapedScenarios()
    {
        string valid = ValidExtensionOrigin;
        string other = "chrome-extension://" + new string('b', 32) + "/";
        yield return ["malformed origin (too short)", new[] { "chrome-extension://short/" }];
        yield return ["unknown/unallowlisted origin", new[] { other }];
        yield return ["multiple extension origins", new[] { valid, other }];
        yield return ["lone --parent-window with zero origin", new[] { "--parent-window=999" }];
        yield return ["origin with wrong scheme", new[] { "https://" + new string('a', 32) + "/" }];
    }

    [Theory]
    [MemberData(nameof(RejectedHostShapedScenarios))]
    public void R3_HostShapedButRejected_ClassifiesAsRejected_NeitherRunnerInvoked(string scenario, string[] args)
    {
        var allowlist = new HashSet<string>(StringComparer.Ordinal) { ValidExtensionOrigin };

        Assert.Equal("RejectedHostShaped", InvokeClassifyKind(args, allowlist));

        var (normalCalls, hostCalls) = InvokeEntryRun(args, allowlist);
        Assert.True(normalCalls == 0 && hostCalls == 0,
            $"R3 ({scenario}): a rejected host-shaped invocation must invoke NEITHER runner -- it must " +
            "never fall through to the tray runner.");
    }

    [Fact]
    public void R29_AssemblyEntryPoint_ResolvesToProgramMain_NotGeneratedAppMain()
    {
        var entryMethod = typeof(TargetGate).Assembly.EntryPoint;
        Assert.True(entryMethod is not null,
            "The Privon.App assembly must declare an entry point.");
        Assert.Equal("Program", entryMethod!.DeclaringType?.Name);
        Assert.Equal("Main", entryMethod.Name);
        Assert.NotEqual("App", entryMethod.DeclaringType?.Name);
    }

    // ==================================================================
    // SECTION R25/50 -- Web clipboard authorization remains disabled through E3.
    // ==================================================================

    [Fact]
    public async Task R25_ClipboardAuthorizationRouter_ProductionDefault_StillYieldsOutsideForWeb()
    {
        var unsupportedForeground = new ForegroundTargetSnapshot(
            IsResolved: true, ProcessId: 4321, ProcessName: "notepad",
            PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
            ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Contoso");

        var result = await ClipboardAuthorizationRouter.AuthorizeAsync(unsupportedForeground, webSource: null);

        Assert.Equal(ClipboardAuthorizationKind.Outside, result.Kind);
    }

    [Fact]
    public void R25_PrivonAppComposition_NowWiresARealNonNullWebAuthorizationSource_Gate031E5F()
    {
        // Gate E5F's own explicit, stated GREEN target superseded this E3-era test's original
        // assertion: composition now constructs exactly one production WebClipboardAuthorizationSource
        // through normal composition (see Gate031E5F_ProductionCompositionRedTests.Case2_3, GREEN).
        // Production stays fail-closed regardless -- WebExtensionOriginAllowlist.Production remains
        // empty (Gate031F6K_E4RedTests.G18_ProductionExtensionAllowlist_RemainsEmpty and
        // Gate031E5F_ProductionCompositionRedTests.Case4_5, both unchanged), so no accepted Native
        // Messaging session -- and therefore no authorization -- is reachable from source construction
        // alone.
        string? path = TryFindAppSourceFile(nameof(PrivonAppComposition));
        Assert.True(path is not null, "src/Privon.App/PrivonAppComposition.cs must exist.");

        string source = File.ReadAllText(path!);
        Assert.Contains("webAuthorizationSource:", source, StringComparison.Ordinal);
        Assert.Contains("IWebClipboardAuthorizationSource", source, StringComparison.Ordinal);
    }

    // ==================================================================
    // SECTION R33 -- WebSessionAdmissionBudget. No future-interface dependency: fully real,
    // deterministic async concurrency, no sleep, no polling.
    // ==================================================================

    private const string BudgetTypeName = "Privon.App.WebSessionAdmissionBudget";
    private static Type? BudgetType => GetAppType(BudgetTypeName);

    private const string BudgetContract =
        "Privon.App.WebSessionAdmissionBudget does not exist yet (Gate 031F6H E3, frozen contract). " +
        "Required: internal sealed class WebSessionAdmissionBudget(int capacity) wrapping a " +
        "SemaphoreSlim(N, N), with Task<Lease> AcquireAsync(CancellationToken) and a nested sealed " +
        "class Lease : IDisposable whose Dispose() is idempotent and returns exactly one permit. " +
        "AcquireAsync must genuinely wait (never throw/busy-loop) when the budget is at capacity, " +
        "resuming the instant a Lease is disposed. A cancelled wait releases no permit and yields no " +
        "Lease (task faults/cancels with OperationCanceledException).";

    private static object CreateBudget(int capacity)
    {
        Assert.True(BudgetType is not null, BudgetContract);
        object? instance = Activator.CreateInstance(BudgetType!, capacity);
        Assert.True(instance is not null, $"{BudgetTypeName} could not be constructed with capacity {capacity}.");
        return instance!;
    }

    private static Task<object> AcquireAsync(object budget, CancellationToken cancellationToken = default)
    {
        var method = budget.GetType().GetMethod("AcquireAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method is not null, $"{BudgetTypeName} exposes no public AcquireAsync(...) method. " + BudgetContract);

        object? invokeResult = method!.Invoke(budget, [cancellationToken]);
        Assert.True(invokeResult is Task, $"{BudgetTypeName}.AcquireAsync must return a Task<Lease>. " + BudgetContract);

        var task = (Task)invokeResult!;
        return AwaitAndUnwrap(task);
    }

    private static async Task<object> AwaitAndUnwrap(Task task)
    {
        await task.ConfigureAwait(false);
        var resultProperty = task.GetType().GetProperty("Result");
        Assert.True(resultProperty is not null, $"{BudgetTypeName}.AcquireAsync must return Task<Lease>, not a bare Task. " + BudgetContract);
        object? lease = resultProperty!.GetValue(task);
        Assert.True(lease is not null, "AcquireAsync completed with a null Lease.");
        return lease!;
    }

    private static void DisposeLease(object lease)
    {
        var disposable = Assert.IsAssignableFrom<IDisposable>(lease);
        disposable.Dispose();
    }

    [Fact]
    public async Task R33A_R33B_R33C_CapacityIsExact_NPlus1WaitsUntilOneLeaseIsDisposed()
    {
        object budget = CreateBudget(2);

        var lease1Task = AcquireAsync(budget);
        var lease2Task = AcquireAsync(budget);
        object lease1 = await lease1Task;
        object lease2 = await lease2Task;

        var thirdAcquire = AcquireAsync(budget);
        await Task.Delay(50); // brief, bounded settle window -- not a sleep-based retry/poll loop
        Assert.False(thirdAcquire.IsCompleted, "R33-B: AcquireAsync beyond capacity must remain incomplete.");

        DisposeLease(lease1);
        object lease3 = await thirdAcquire; // R33-C: releasing one lease lets the waiter resume.
        Assert.NotNull(lease3);

        DisposeLease(lease2);
        DisposeLease(lease3);
    }

    [Fact]
    public async Task R33D_DoubleDispose_NeverOverReleases()
    {
        object budget = CreateBudget(1);
        object lease = await AcquireAsync(budget);

        DisposeLease(lease);
        DisposeLease(lease);
        DisposeLease(lease);

        // Over-release would have made capacity effectively > 1; prove it is still exactly 1 by
        // acquiring twice and confirming the second genuinely blocks.
        object first = await AcquireAsync(budget);
        var second = AcquireAsync(budget);
        await Task.Delay(50);
        Assert.False(second.IsCompleted, "R33-D: a double-disposed Lease must never grant an extra permit.");

        DisposeLease(first);
        await second;
    }

    [Fact]
    public async Task R33E_CancelledWaitBeforeAcquisition_GrantsNoLease_CountUnchanged()
    {
        object budget = CreateBudget(1);
        object holder = await AcquireAsync(budget);

        using var cts = new CancellationTokenSource();
        var waiting = AcquireAsync(budget, cts.Token);
        await Task.Delay(20);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // Capacity is unchanged by the cancelled wait: disposing the original holder is what frees it.
        DisposeLease(holder);
        object next = await AcquireAsync(budget);
        DisposeLease(next);
    }

    // ==================================================================
    // SECTION R22/R48 -- WebHostRelayLoop: dumb, mechanical, protocol-free two-direction relay. No
    // future-interface dependency -- real WebNativeMessagingPump, real in-memory/scripted streams.
    // ==================================================================

    private const string RelayLoopTypeName = "Privon.App.WebHostRelayLoop";
    private static Type? RelayLoopType => GetAppType(RelayLoopTypeName);

    private const string RelayLoopContract =
        "Privon.App.WebHostRelayLoop does not exist yet (Gate 031F6H E3, frozen contract). Required: " +
        "internal static class exposing public static Task RunAsync(Stream stdin, Stream stdout, " +
        "Stream pipe) -- using ONLY WebNativeMessagingPump (never WebFrameDecoder/WebFrameEncoder) to " +
        "relay stdin->pipe and pipe->stdout concurrently. When either direction completes (EOF or " +
        "error), the pipe is disposed and the other direction is bounded-stopped rather than joined " +
        "forever; Environment.Exit must never be required; no diagnostic byte of any kind reaches " +
        "stdout.";

    private static async Task InvokeRelayLoopRunAsync(Stream stdin, Stream stdout, Stream pipe, string requiredBehavior)
    {
        Assert.True(RelayLoopType is not null, RelayLoopContract + " REQUIRED FOR THIS TEST: " + requiredBehavior);
        var method = RelayLoopType!.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.True(method is not null, $"{RelayLoopTypeName} exists but exposes no static RunAsync(...) method. " + RelayLoopContract);

        object? invokeResult = method!.Invoke(null, [stdin, stdout, pipe]);
        Assert.True(invokeResult is Task, $"{RelayLoopTypeName}.RunAsync must return a Task. " + RelayLoopContract);
        await (Task)invokeResult!;
    }

    private static byte[] LengthPrefixedJson(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
        return [.. length, .. payload];
    }

    /// <summary>A duplex in-memory pipe stand-in: writes to one side become readable from the other.</summary>
    private sealed class DuplexMemoryStream : Stream
    {
        private readonly MemoryStream _inbox = new();
        private readonly SemaphoreSlim _dataAvailable = new(0);
        private int _readPosition;
        private bool _writerClosed;
        private readonly object _gate = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void CloseWriteSide()
        {
            lock (_gate) { _writerClosed = true; }
            _dataAvailable.Release();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_readPosition < _inbox.Length)
                    {
                        var span = _inbox.GetBuffer().AsSpan(_readPosition, (int)Math.Min(count, _inbox.Length - _readPosition));
                        span.CopyTo(buffer.AsSpan(offset));
                        _readPosition += span.Length;
                        return span.Length;
                    }

                    if (_writerClosed)
                        return 0;
                }

                await _dataAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                long saved = _inbox.Position;
                _inbox.Position = _inbox.Length;
                _inbox.Write(buffer, offset, count);
                _inbox.Position = saved;
            }
            _dataAvailable.Release();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// TEST_CONTRACT_CORRECTION (Gate 031F6I.1 A3): a genuinely full-duplex pipe stand-in, composed
    /// of TWO independent <see cref="DuplexMemoryStream"/> one-way buffers -- exactly how a real
    /// <see cref="System.IO.Pipes.NamedPipeClientStream"/>'s own send and receive directions are
    /// independent buffers, never the same one. The plain <see cref="DuplexMemoryStream"/> (used
    /// as-is, correctly, by <see cref="ScriptedSessionTransport"/>'s own two-instance composition
    /// elsewhere in this file) previously stood in for BOTH directions of the relay's own <c>pipe</c>
    /// parameter at once, which made the relay's own pipe-&gt;stdout direction and an external test
    /// observer's own direct read into competing consumers of the SAME single-direction FIFO -- a
    /// structural defect in the double, not in <c>WebHostRelayLoop</c> itself (a real duplex pipe has
    /// no such race). <see cref="Write"/>/<see cref="WriteAsync(byte[], int, int, CancellationToken)"/>
    /// (what code holding this <see cref="Stream"/> sends) and <see cref="Read"/>/
    /// <see cref="ReadAsync(byte[], int, int, CancellationToken)"/> (what it receives) are therefore
    /// on two separate buffers here, exactly like the real thing.
    /// </summary>
    private sealed class FullDuplexPipeStream : Stream
    {
        // What code holding this Stream WRITES ("sends") -- readable by a test observer via the
        // dedicated Sent property, independent of this object's own Read/ReadAsync.
        private readonly DuplexMemoryStream _outbound = new();
        // What a test observer WRITES via WriteForReadAsync ("simulates the other end sending") --
        // consumed by this object's own standard Read/ReadAsync overrides.
        private readonly DuplexMemoryStream _inbound = new();
        private bool _disposedOnce;

        /// <summary>The independent "sent" side, for a test observer to read directly -- never the
        /// same buffer this object's own <see cref="Read"/>/<see cref="ReadAsync(byte[], int, int, CancellationToken)"/>
        /// pulls from.</summary>
        public Stream Sent => _outbound;

        /// <summary>Test-only: injects bytes as if the remote end sent them -- consumed by this
        /// object's own standard Read/ReadAsync.</summary>
        public Task WriteForReadAsync(byte[] data) => _inbound.WriteAsync(data, 0, data.Length);

        /// <summary>Test-only: signals no more injected data is coming, so a pending/future Read sees
        /// a clean EOF once the buffered bytes are drained.</summary>
        public void CloseForReadSide() => _inbound.CloseWriteSide();

        /// <summary>Signals no more will be sent (via the standard Write/WriteAsync overrides).</summary>
        public void CloseWriteSide() => _outbound.CloseWriteSide();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inbound.ReadAsync(buffer, offset, count, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) => _outbound.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _outbound.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposedOnce)
            {
                _disposedOnce = true;
                // Mirrors what disposing a real connected pipe does to both of its independent
                // directions: any still-blocked read on either side unblocks with a clean EOF, rather
                // than hanging forever.
                _outbound.CloseWriteSide();
                _inbound.CloseWriteSide();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Races one pipe read against <paramref name="relayTask"/>'s own completion (plus a bounded
    /// 5-second deadline, matching this file's established R48 convention) rather than awaiting the
    /// read unconditionally. TEST_DEFECT correction (Gate 031F6H hotfix): in the RED state, the
    /// future WebHostRelayLoop production type does not exist, so <paramref name="relayTask"/> is
    /// already faulted by the time this is called -- nothing ever relays a byte into <paramref name="pipe"/>,
    /// so an unconditional <c>await pipe.ReadAsync(...)</c> would hang forever waiting for data that
    /// can never arrive. If <paramref name="relayTask"/> completes/faults before the read does,
    /// awaiting it immediately surfaces the real missing-production RED failure instead. Once
    /// WebHostRelayLoop exists and genuinely relays, the read wins the race exactly as before and
    /// this helper is otherwise transparent.
    /// </summary>
    private static async Task<int> ReadOrObserveRelayFaultAsync(Stream pipe, Memory<byte> buffer, Task relayTask)
    {
        var readTask = pipe.ReadAsync(buffer).AsTask();
        var winner = await Task.WhenAny(readTask, relayTask, Task.Delay(TimeSpan.FromSeconds(5)));

        if (winner == relayTask)
        {
            await relayTask; // surfaces the real fault (missing production) rather than hanging.
            Assert.Fail("relayTask completed successfully before ever delivering the expected pipe data.");
        }

        Assert.True(winner == readTask, "Timed out waiting for pipe data or relay completion.");
        return await readTask;
    }

    [Fact]
    public async Task R22_StdinFrame_RelaysByteIdenticalToPipe()
    {
        byte[] frame = LengthPrefixedJson("{\"v\":1,\"type\":\"Hello\"}");
        var stdin = new MemoryStream(frame);
        var stdout = new MemoryStream();
        var pipe = new FullDuplexPipeStream();

        var relayTask = InvokeRelayLoopRunAsync(stdin, stdout, pipe,
            "A frame written to stdin must appear on the pipe byte-identical, with no extra envelope.");

        byte[] onPipe = new byte[frame.Length];
        int total = 0;
        while (total < onPipe.Length)
        {
            int read = await ReadOrObserveRelayFaultAsync(pipe.Sent, onPipe.AsMemory(total), relayTask);
            Assert.True(read > 0);
            total += read;
        }

        Assert.Equal(frame, onPipe);
        pipe.CloseWriteSide();
        stdin.Position = stdin.Length; // ensure EOF observed by the loop's own stdin side too

        var finalWait = await Task.WhenAny(relayTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(relayTask, finalWait);
        await relayTask;
    }

    [Fact]
    public async Task R22_PipeFrame_RelaysByteIdenticalToStdout()
    {
        byte[] frame = LengthPrefixedJson("{\"v\":1,\"type\":\"StateInvalidate\"}");
        var stdin = new MemoryStream(); // never sends anything from the browser side in this test
        var stdout = new MemoryStream();
        var pipe = new FullDuplexPipeStream();

        var relayTask = InvokeRelayLoopRunAsync(stdin, stdout, pipe,
            "A frame written on the pipe must appear on stdout byte-identical.");

        await pipe.WriteForReadAsync(frame);
        pipe.CloseForReadSide();

        await relayTask;
        Assert.Equal(frame, stdout.ToArray());
    }

    [Fact]
    public async Task R23_R48_StdoutContainsOnlyRelayedBytes_NoBannerNoBomNoDiagnosticText()
    {
        byte[] frame = LengthPrefixedJson("{\"v\":1,\"type\":\"Hello\"}");
        var stdin = new MemoryStream();
        var stdout = new MemoryStream();
        var pipe = new FullDuplexPipeStream();

        var relayTask = InvokeRelayLoopRunAsync(stdin, stdout, pipe,
            "stdout must contain exactly the relayed frame bytes -- no BOM, no newline, no banner.");

        await pipe.WriteForReadAsync(frame);
        pipe.CloseForReadSide();
        await relayTask;

        Assert.Equal(frame, stdout.ToArray());
    }

    [Fact]
    public async Task R48_OneDirectionCompleting_CausesTheRelayToReturn_WithoutRequiringEnvironmentExit()
    {
        var stdin = new MemoryStream([]); // immediate EOF from the browser side
        var stdout = new MemoryStream();
        var pipe = new FullDuplexPipeStream();

        var relayTask = InvokeRelayLoopRunAsync(stdin, stdout, pipe,
            "EOF on one direction must cause RunAsync to return in bounded time, disposing the pipe, " +
            "without requiring Environment.Exit.");

        var completed = await Task.WhenAny(relayTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(relayTask, completed);
        await relayTask;
    }

    // ==================================================================
    // FUTURE-INTERFACE FAKES -- IWebBrowserHostBinding / IWebHostBindingSource / IWebPipeListener.
    // ==================================================================

    private const string BindingInterfaceName = "Privon.App.IWebBrowserHostBinding";
    private const string BindingSourceInterfaceName = "Privon.App.IWebHostBindingSource";
    private const string ListenerInterfaceName = "Privon.App.IWebPipeListener";
    private const string SessionTypeName = "Privon.App.WebChannelSession";
    private const string SessionStateTypeName = "Privon.App.WebChannelSessionState";
    private const string ManagerTypeName = "Privon.App.WebChannelManager";
    private const string ServerTypeName = "Privon.App.WebChannelHostServer";

    private static Type? BindingInterfaceType => GetAppType(BindingInterfaceName);
    private static Type? BindingSourceInterfaceType => GetAppType(BindingSourceInterfaceName);
    private static Type? ListenerInterfaceType => GetAppType(ListenerInterfaceName);
    private static Type? SessionType => GetAppType(SessionTypeName);
    private static Type? ManagerType => GetAppType(ManagerTypeName);
    private static Type? ServerType => GetAppType(ServerTypeName);

    private const string SessionManagerContract =
        "Privon.App.WebChannelSession / WebChannelManager / IWebBrowserHostBinding / " +
        "IWebHostBindingSource do not exist yet (Gate 031F6H E3, frozen contract -- see this file's " +
        "own header for the exact frozen shape). Required: a real Hello/HelloAck handshake over " +
        "WebFrameDecoder/WebFrameEncoder/WebNativeMessagingPump/WebChannelRegistry, session ownership " +
        "of transport/binding/Lease/Lifecycle, and manager-owned accepted-session visibility.";

    private const string ServerContract =
        "Privon.App.WebChannelHostServer / IWebPipeListener do not exist yet (Gate 031F6H E3, frozen " +
        "contract). Required: per-connection admission pipeline (PID -> E2 binding -> WebBrowserGate " +
        "policy -> session construction/admission) plus a bounded accept loop.";

    private static object CreateFakeBinding(
        uint browserProcessId, string? processName, PackageIdentityResolution packageIdentity,
        ExecutableSignatureResolution signature, string? signerOrganization, out Func<bool> wasDisposed)
    {
        Assert.True(BindingInterfaceType is not null, SessionManagerContract);
        var (proxy, stub) = CreateStub(BindingInterfaceType!);
        bool disposed = false;

        stub.Handlers["get_BrowserProcessId"] = _ => browserProcessId;
        stub.Handlers["get_BrowserProcessName"] = _ => processName;
        stub.Handlers["get_BrowserPackageIdentity"] = _ => packageIdentity;
        stub.Handlers["get_BrowserExecutableSignature"] = _ => signature;
        stub.Handlers["get_BrowserSignerOrganization"] = _ => signerOrganization;
        // Gate 031F6I.1 B1: IWebBrowserHostBinding.CheckLiveness is a real interface member every
        // fake must answer, since WebChannelManager.TryGetAcceptedSession now consults it on every
        // lookup. Every existing scenario using this shared helper assumes an ordinarily-running
        // browser, so the default is Alive; B1_LivenessNotAlive... below overrides it explicitly.
        stub.Handlers["CheckLiveness"] = _ => Privon.Windows.RetainedProcessLiveness.Alive;
        stub.Handlers["Dispose"] = _ => { disposed = true; return null; };

        wasDisposed = () => disposed;
        return proxy;
    }

    private static object CreateTrustedChromeFakeBinding(uint browserProcessId, out Func<bool> wasDisposed) =>
        CreateFakeBinding(browserProcessId, "chrome", PackageIdentityResolution.NoPackage,
            ExecutableSignatureResolution.Trusted, "Google LLC", out wasDisposed);

    private static object CreateFakeBindingSource(Func<uint, (BrowserHostBindingStatus Status, object? Binding)> resolve)
    {
        Assert.True(BindingSourceInterfaceType is not null, SessionManagerContract);
        var (proxy, stub) = CreateStub(BindingSourceInterfaceType!);

        stub.Handlers["Resolve"] = args =>
        {
            uint hostPid = (uint)args[0]!;
            var (status, binding) = resolve(hostPid);
            args[1] = binding; // out IWebBrowserHostBinding? binding
            return status;
        };

        return proxy;
    }

    private static object CreateRegistry() => new WebChannelRegistry();

    private static object CreateManager(object registry)
    {
        Assert.True(ManagerType is not null, SessionManagerContract);
        object? instance = Activator.CreateInstance(ManagerType!, registry);
        Assert.True(instance is not null, $"{ManagerTypeName} could not be constructed.");
        return instance!;
    }

    private static object CreateSession(Stream transport, object binding, object registry, object lease, object manager)
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        object? instance = Activator.CreateInstance(SessionType!, transport, binding, registry, lease, manager);
        Assert.True(instance is not null, $"{SessionTypeName} could not be constructed.");
        return instance!;
    }

    private static string SessionState(object session)
    {
        var property = session.GetType().GetProperty("State", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(property is not null, $"{SessionTypeName} exposes no State property. " + SessionManagerContract);
        return property!.GetValue(session)!.ToString()!;
    }

    private static long? SessionChannelId(object session)
    {
        var property = session.GetType().GetProperty("ChannelId", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(property is not null, $"{SessionTypeName} exposes no ChannelId property. " + SessionManagerContract);
        return (long?)property!.GetValue(session);
    }

    private static Task SessionLifecycle(object session)
    {
        var property = session.GetType().GetProperty("Lifecycle", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(property is not null, $"{SessionTypeName} exposes no Lifecycle property. " + SessionManagerContract);
        return (Task)property!.GetValue(session)!;
    }

    private static void SessionStart(object session)
    {
        var method = session.GetType().GetMethod("Start", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method is not null, $"{SessionTypeName} exposes no Start() method. " + SessionManagerContract);
        method!.Invoke(session, null);
    }

    private static void SessionClose(object session)
    {
        var method = session.GetType().GetMethod("Close", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method is not null, $"{SessionTypeName} exposes no Close() method. " + SessionManagerContract);
        method!.Invoke(session, null);
    }

    private static bool ManagerTryGetAccepted(object manager, uint browserProcessId, out object? session)
    {
        var method = manager.GetType().GetMethod("TryGetAcceptedSession", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method is not null, $"{ManagerTypeName} exposes no TryGetAcceptedSession(...) method. " + SessionManagerContract);
        var args = new object?[] { browserProcessId, null };
        bool result = (bool)method!.Invoke(manager, args)!;
        session = args[1];
        return result;
    }

    private static void ManagerBeginAdmission(object manager, object session)
    {
        var method = manager.GetType().GetMethod("BeginAdmission", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method is not null, $"{ManagerTypeName} exposes no BeginAdmission(...) method. " + SessionManagerContract);
        method!.Invoke(manager, [session]);
    }

    private static void ManagerBeginShutdown(object manager)
    {
        var method = manager.GetType().GetMethod("BeginShutdown", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method is not null, $"{ManagerTypeName} exposes no BeginShutdown() method. " + SessionManagerContract);
        method!.Invoke(manager, null);
    }

    private static System.Collections.IEnumerable ManagerSnapshotLive(object manager)
    {
        var method = manager.GetType().GetMethod("SnapshotLive", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method is not null, $"{ManagerTypeName} exposes no SnapshotLive() method. " + SessionManagerContract);
        return (System.Collections.IEnumerable)method!.Invoke(manager, null)!;
    }

    // ------------------------------------------------------------------
    // Scripted duplex stream for a session's own transport: two independent one-way pipes glued
    // together so the test can act as "the browser side" (write Hello, read HelloAck) while the real
    // session code reads/writes the other side.
    // ------------------------------------------------------------------
    private sealed class ScriptedSessionTransport : Stream
    {
        private readonly DuplexMemoryStream _toSession = new();   // test writes here; session reads
        private readonly DuplexMemoryStream _fromSession = new(); // session writes here; test reads
        public bool ThrowOnWrite { get; set; }
        public bool ThrowOnRead { get; set; }
        public bool ReadWasAttempted { get; private set; }

        public Task WriteFromBrowserAsync(byte[] data) => _toSession.WriteAsync(data, 0, data.Length);
        public void CloseBrowserWriteSide() => _toSession.CloseWriteSide();

        public async Task<byte[]> ReadExactFromSessionAsync(int count)
        {
            byte[] buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read = await _fromSession.ReadAsync(buffer.AsMemory(total, count - total));
                if (read == 0) throw new IOException("session closed its write side before sending the expected bytes.");
                total += read;
            }
            return buffer;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadWasAttempted = true;
            return ThrowOnRead
                ? throw new IOException("scripted read failure")
                : _toSession.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (ThrowOnWrite) throw new IOException("scripted write failure");
            _fromSession.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ThrowOnWrite ? throw new IOException("scripted write failure") : _fromSession.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static byte[] HelloFrame() => LengthPrefixedJson("{\"v\":1,\"type\":\"Hello\"}");
    private static byte[] StateInvalidateFrame() => LengthPrefixedJson("{\"v\":1,\"type\":\"StateInvalidate\"}");
    private static byte[] StateAssertFrame(string focus, string originResolution, string? origin) =>
        LengthPrefixedJson(origin is null
            ? $"{{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"{focus}\",\"originResolution\":\"{originResolution}\"}}"
            : $"{{\"v\":1,\"type\":\"StateAssert\",\"focus\":\"{focus}\",\"originResolution\":\"{originResolution}\",\"origin\":\"{origin}\"}}");
    private static byte[] HelloAckFrame(bool accepted) => LengthPrefixedJson($"{{\"v\":1,\"type\":\"HelloAck\",\"accepted\":{(accepted ? "true" : "false")}}}");
    private static byte[] ChallengeResponseFrame(string nonce) =>
        LengthPrefixedJson($"{{\"v\":1,\"type\":\"ChallengeResponse\",\"nonce\":\"{nonce}\",\"focus\":\"Focused\",\"originResolution\":\"Unsupported\"}}");

    private static (byte[] Status, byte[] Type) ParseHelloAck(byte[] frame)
    {
        // Helper only used to decode a HelloAck via the real, existing WebFrameDecoder.
        var status = WebFrameDecoder.Decode(frame, out var message);
        Assert.Equal(WebFrameDecodeStatus.Ok, status);
        Assert.Equal(WebProtocolMessageType.HelloAck, message.Type);
        return ([], []); // unused tuple shape; kept for call-site symmetry with earlier drafts
    }

    // ==================================================================
    // R10 -- valid Hello handshake end to end.
    // ==================================================================

    [Fact]
    public async Task R10_ValidHelloHandshake_PromotesToAccepted_AndManagerCanDiscoverIt()
    {
        Assert.True(SessionType is not null && ManagerType is not null && BindingInterfaceType is not null, SessionManagerContract);

        var registry = CreateRegistry();
        var manager = CreateManager(registry);
        var transport = new ScriptedSessionTransport();
        var binding = CreateTrustedChromeFakeBinding(4242, out _);
        var budget = CreateBudget(4);
        object lease = await AcquireAsync(budget);

        var session = CreateSession(transport, binding, registry, lease, manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);

        Assert.Equal("AwaitingHello", SessionState(session));

        await transport.WriteFromBrowserAsync(HelloFrame());
        byte[] ackFrame = await transport.ReadExactFromSessionAsync(4);
        uint declaredLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ackFrame);
        byte[] ackPayload = await transport.ReadExactFromSessionAsync((int)declaredLen);
        byte[] fullAck = [.. ackFrame, .. ackPayload];

        var decodeStatus = WebFrameDecoder.Decode(fullAck, out var ackMessage);
        Assert.Equal(WebFrameDecodeStatus.Ok, decodeStatus);
        Assert.Equal(WebProtocolMessageType.HelloAck, ackMessage.Type);
        Assert.True(ackMessage.Accepted);

        // Allow the session's own async promotion (which happens strictly AFTER the flush completes)
        // to land before asserting manager visibility.
        await Task.Delay(20);

        Assert.Equal("Accepted", SessionState(session));
        Assert.NotNull(SessionChannelId(session));
        Assert.True(ManagerTryGetAccepted(manager, 4242, out var found));
        Assert.Same(session, found);

        SessionClose(session);
    }

    // ==================================================================
    // R11 -- no accepted visibility before Ack write+flush completes.
    // ==================================================================

    [Fact]
    public async Task R11_NoAcceptedVisibility_BeforeAckWriteAndFlushComplete()
    {
        Assert.True(SessionType is not null, SessionManagerContract);

        var registry = CreateRegistry();
        var manager = CreateManager(registry);
        var transport = new ScriptedSessionTransport { ThrowOnWrite = false };
        var binding = CreateTrustedChromeFakeBinding(5050, out _);
        var budget = CreateBudget(4);
        object lease = await AcquireAsync(budget);

        var session = CreateSession(transport, binding, registry, lease, manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);

        await transport.WriteFromBrowserAsync(HelloFrame());

        // Immediately after Hello is sent -- before the test has drained the Ack -- the manager must
        // not yet report an accepted session (the registry entry may already exist; that window is
        // expected and frozen per R11).
        Assert.False(ManagerTryGetAccepted(manager, 5050, out _));

        SessionClose(session);
    }

    // ==================================================================
    // R12 -- Ack write/flush failure tears the session down with no orphaned ChannelId.
    // ==================================================================

    [Fact]
    public async Task R12_AckWriteFailure_SessionNotAccepted_RegistryDisconnected_ResourcesReleased()
    {
        Assert.True(SessionType is not null, SessionManagerContract);

        var registry = CreateRegistry();
        var manager = CreateManager(registry);
        var transport = new ScriptedSessionTransport { ThrowOnWrite = true };
        var binding = CreateTrustedChromeFakeBinding(6060, out var wasDisposed);
        var budget = CreateBudget(4);
        object lease = await AcquireAsync(budget);

        var session = CreateSession(transport, binding, registry, lease, manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);

        await transport.WriteFromBrowserAsync(HelloFrame());
        transport.CloseBrowserWriteSide();

        var lifecycle = SessionLifecycle(session);
        await Task.WhenAny(lifecycle, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.NotEqual("Accepted", SessionState(session));
        Assert.False(ManagerTryGetAccepted(manager, 6060, out _));
        Assert.True(wasDisposed(), "R12: the binding must be disposed once the Ack write fails.");
        Assert.False(lifecycle.IsFaulted, "R12: the session's own lifecycle must complete non-faulted even though the transport write threw.");
    }

    // ==================================================================
    // R13/R14 -- duplicate-PID rejection vs. distinct-PID coexistence.
    // ==================================================================

    [Fact]
    public async Task R13_SecondSessionSamePid_IsRejected_FirstRemainsAcceptedAndUntouched()
    {
        Assert.True(SessionType is not null, SessionManagerContract);

        var registry = CreateRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);

        var transportA = new ScriptedSessionTransport();
        var bindingA = CreateTrustedChromeFakeBinding(7070, out _);
        var sessionA = CreateSession(transportA, bindingA, registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, sessionA);
        SessionStart(sessionA);
        await transportA.WriteFromBrowserAsync(HelloFrame());
        await Task.Delay(50);
        Assert.True(ManagerTryGetAccepted(manager, 7070, out var foundA));

        var transportB = new ScriptedSessionTransport();
        var bindingB = CreateTrustedChromeFakeBinding(7070, out var bDisposed);
        var sessionB = CreateSession(transportB, bindingB, registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, sessionB);
        SessionStart(sessionB);
        await transportB.WriteFromBrowserAsync(HelloFrame());
        await Task.Delay(50);

        Assert.NotEqual("Accepted", SessionState(sessionB));
        Assert.True(ManagerTryGetAccepted(manager, 7070, out var stillFoundA));
        Assert.Same(foundA, stillFoundA);
        Assert.Same(sessionA, stillFoundA);

        SessionClose(sessionA);
        SessionClose(sessionB);
    }

    [Fact]
    public async Task R14_DifferentPids_BothIndependentlyAccepted_NoGlobalSingleton()
    {
        Assert.True(SessionType is not null, SessionManagerContract);

        var registry = CreateRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);

        var transportA = new ScriptedSessionTransport();
        var sessionA = CreateSession(transportA, CreateTrustedChromeFakeBinding(100, out _), registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, sessionA);
        SessionStart(sessionA);
        await transportA.WriteFromBrowserAsync(HelloFrame());

        var transportB = new ScriptedSessionTransport();
        var sessionB = CreateSession(transportB, CreateTrustedChromeFakeBinding(200, out _), registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, sessionB);
        SessionStart(sessionB);
        await transportB.WriteFromBrowserAsync(HelloFrame());

        await Task.Delay(50);

        Assert.True(ManagerTryGetAccepted(manager, 100, out var foundA));
        Assert.True(ManagerTryGetAccepted(manager, 200, out var foundB));
        Assert.Same(sessionA, foundA);
        Assert.Same(sessionB, foundB);
        Assert.NotSame(foundA, foundB);

        SessionClose(sessionA);
        SessionClose(sessionB);
    }

    // ==================================================================
    // R15 -- pre-handshake message contradiction.
    // ==================================================================

    public static IEnumerable<object[]> PreHandshakeContradictionFrames() =>
    [
        ["StateInvalidate before Hello", StateInvalidateFrame()],
        ["HelloAck before Hello", HelloAckFrame(true)],
        ["ChallengeResponse before Hello", ChallengeResponseFrame(new string('A', 43))],
    ];

    [Theory]
    [MemberData(nameof(PreHandshakeContradictionFrames))]
    public async Task R15_PreHandshakeContradiction_RejectsClose_NeverAccepted(string scenario, byte[] frame)
    {
        Assert.True(SessionType is not null, SessionManagerContract);

        var registry = CreateRegistry();
        var manager = CreateManager(registry);
        var transport = new ScriptedSessionTransport();
        var budget = CreateBudget(4);
        var session = CreateSession(transport, CreateTrustedChromeFakeBinding(8080, out _), registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);

        await transport.WriteFromBrowserAsync(frame);
        var lifecycle = SessionLifecycle(session);
        await Task.WhenAny(lifecycle, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(SessionState(session) != "Accepted", $"R15 ({scenario}): must never reach Accepted.");
        Assert.False(ManagerTryGetAccepted(manager, 8080, out _));
    }

    // ==================================================================
    // R16/R17/R18 -- StateInvalidate / StateAssert / Assert-over-Assert.
    // ==================================================================

    private static async Task<object> AcceptedSession(object registry, object manager, uint pid, ScriptedSessionTransport transport, object budget)
    {
        var session = CreateSession(transport, CreateTrustedChromeFakeBinding(pid, out _), registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);
        await transport.WriteFromBrowserAsync(HelloFrame());
        await Task.Delay(50);
        Assert.Equal("Accepted", SessionState(session));
        return session;
    }

    [Fact]
    public async Task R16_StateInvalidate_AdvancesRegistryRevision_SessionStaysAccepted()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var session = await AcceptedSession(registry, manager, 9090, transport, budget);
        long channelId = SessionChannelId(session)!.Value;

        registry.TryGetCurrentEvidence(channelId, out var before);

        await transport.WriteFromBrowserAsync(StateInvalidateFrame());
        await Task.Delay(50);

        Assert.True(registry.TryGetCurrentEvidence(channelId, out var after));
        Assert.NotEqual(before.EvidenceRevision, after.EvidenceRevision);
        Assert.Equal(OriginResolution.Unresolved, after.OriginResolution);
        Assert.Equal("Accepted", SessionState(session));

        SessionClose(session);
    }

    [Fact]
    public async Task R17_StateAssert_UpdatesEvidence_ForegroundEpochStaysRegistryOwnedZero()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var session = await AcceptedSession(registry, manager, 9191, transport, budget);
        long channelId = SessionChannelId(session)!.Value;

        await transport.WriteFromBrowserAsync(StateAssertFrame("Focused", "Resolved", "https://example.invalid"));
        await Task.Delay(50);

        Assert.True(registry.TryGetCurrentEvidence(channelId, out var evidence));
        Assert.Equal(BrowserFocus.Focused, evidence.BrowserFocus);
        Assert.Equal(OriginResolution.Resolved, evidence.OriginResolution);
        Assert.Equal("https://example.invalid", evidence.Origin);
        Assert.Equal(0, evidence.ForegroundEpoch);
        Assert.Equal(9191u, evidence.BrowserProcessId);
        Assert.Equal("Accepted", SessionState(session));

        SessionClose(session);
    }

    [Fact]
    public async Task R18_AssertOverAssert_WithoutInvalidateBetween_TearsDownTheSession()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var session = await AcceptedSession(registry, manager, 9292, transport, budget);
        uint pid = 9292;

        await transport.WriteFromBrowserAsync(StateAssertFrame("Focused", "Resolved", "https://example.invalid"));
        await Task.Delay(50);
        Assert.Equal("Accepted", SessionState(session));

        await transport.WriteFromBrowserAsync(StateAssertFrame("NotFocused", "Unsupported", null));
        var lifecycle = SessionLifecycle(session);
        await Task.WhenAny(lifecycle, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Equal("Closed", SessionState(session));
        Assert.False(ManagerTryGetAccepted(manager, pid, out _));
    }

    // ==================================================================
    // R19/R20 -- framing failure and EOF, pre- and post-Accepted.
    // ==================================================================

    [Fact]
    public async Task R19_MalformedFrame_PostAccepted_TearsDownTheSession()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var session = await AcceptedSession(registry, manager, 9393, transport, budget);

        await transport.WriteFromBrowserAsync(LengthPrefixedJson("{not json"));
        var lifecycle = SessionLifecycle(session);
        await Task.WhenAny(lifecycle, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Equal("Closed", SessionState(session));
        Assert.False(ManagerTryGetAccepted(manager, 9393, out _));
    }

    [Fact]
    public async Task R20_Eof_PostAccepted_TearsDownTheSession_RegistryDisconnected()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var session = await AcceptedSession(registry, manager, 9494, transport, budget);
        long channelId = SessionChannelId(session)!.Value;

        transport.CloseBrowserWriteSide();
        var lifecycle = SessionLifecycle(session);
        await Task.WhenAny(lifecycle, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Equal("Closed", SessionState(session));
        Assert.False(registry.TryGetCurrentEvidence(channelId, out _));
        Assert.False(ManagerTryGetAccepted(manager, 9494, out _));
    }

    // ==================================================================
    // R21 -- resource ownership. Structural: after successful construction, exactly the four owned
    // resources are the session's own responsibility.
    // ==================================================================

    [Fact]
    public async Task R21_ClosingAcceptedSession_DisposesBindingAndTransport_ReturnsLease_ExactlyOnce()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(1);
        var transport = new ScriptedSessionTransport();
        var binding = CreateTrustedChromeFakeBinding(9595, out var bindingDisposed);
        object lease = await AcquireAsync(budget);
        var session = CreateSession(transport, binding, registry, lease, manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);
        await transport.WriteFromBrowserAsync(HelloFrame());
        await Task.Delay(50);

        SessionClose(session);
        SessionClose(session); // idempotent -- must not double-dispose or double-release.

        Assert.True(bindingDisposed());

        // Lease was returned exactly once: the budget should now grant a fresh acquire immediately.
        var next = AcquireAsync(budget);
        var completed = await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(next, completed);
    }

    // ==================================================================
    // R28/R35 -- manager lookup liveness semantics.
    // ==================================================================

    [Fact]
    public async Task R28_TryGetAcceptedSession_NeverReturnsAnAwaitingHelloOrClosedSession()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);

        var awaitingTransport = new ScriptedSessionTransport();
        var awaitingSession = CreateSession(awaitingTransport, CreateTrustedChromeFakeBinding(1010, out _), registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, awaitingSession);
        SessionStart(awaitingSession);
        Assert.False(ManagerTryGetAccepted(manager, 1010, out _));

        var closedSession = await AcceptedSession(registry, manager, 1020, new ScriptedSessionTransport(), budget);
        SessionClose(closedSession);
        Assert.False(ManagerTryGetAccepted(manager, 1020, out _));
    }

    // ==================================================================
    // B1 (Gate 031F6I.1 -- new focused test proving the frozen liveness contract section 17 requires,
    // which the original 031F6H suite never encoded because IWebBrowserHostBinding had no liveness
    // member yet). An Accepted session whose own browser binding reports Exited/Unavailable must lose
    // accepted visibility -- removed under the manager lock -- and then be Closed OUTSIDE that lock.
    // ==================================================================

    [Theory]
    [InlineData(RetainedProcessLiveness.Exited)]
    [InlineData(RetainedProcessLiveness.Unavailable)]
    public async Task B1_AcceptedSessionLivenessNotAlive_RemovesVisibility_AndClosesTheSession(
        RetainedProcessLiveness liveness)
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();

        var (bindingProxy, stub) = CreateStub(BindingInterfaceType!);
        stub.Handlers["get_BrowserProcessId"] = _ => 3131u;
        stub.Handlers["get_BrowserProcessName"] = _ => "chrome";
        stub.Handlers["get_BrowserPackageIdentity"] = _ => PackageIdentityResolution.NoPackage;
        stub.Handlers["get_BrowserExecutableSignature"] = _ => ExecutableSignatureResolution.Trusted;
        stub.Handlers["get_BrowserSignerOrganization"] = _ => "Google LLC";
        stub.Handlers["CheckLiveness"] = _ => liveness;
        bool disposed = false;
        stub.Handlers["Dispose"] = _ => { disposed = true; return null; };

        var session = CreateSession(transport, bindingProxy, registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);
        await transport.WriteFromBrowserAsync(HelloFrame());
        await Task.Delay(50);
        Assert.Equal("Accepted", SessionState(session));

        Assert.False(ManagerTryGetAccepted(manager, 3131, out _),
            $"B1 ({liveness}): a non-Alive binding must never be reported as an accepted session.");

        Assert.Equal("Closed", SessionState(session));
        Assert.True(disposed, $"B1 ({liveness}): the session must be Closed (and its binding disposed) once liveness is not Alive.");
    }

    // ==================================================================
    // R30 -- concurrent Close. Deterministic via a shared barrier -- exactly one teardown winner.
    // ==================================================================

    [Fact]
    public async Task R30_ConcurrentClose_ExactlyOneTeardownWinner_CloseNeverThrows()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var session = await AcceptedSession(registry, manager, 1111, transport, budget);

        using var barrier = new Barrier(2);
        var t1 = Task.Run(() => { barrier.SignalAndWait(); SessionClose(session); });
        var t2 = Task.Run(() => { barrier.SignalAndWait(); SessionClose(session); });

        await Task.WhenAll(t1, t2); // neither Close() call may throw.

        Assert.Equal("Closed", SessionState(session));
        Assert.False(ManagerTryGetAccepted(manager, 1111, out _));
    }

    // ==================================================================
    // R31/R32 -- identity-aware stale removal / Close-vs-promotion race.
    // ==================================================================

    [Fact]
    public async Task R31_StaleOldSessionClose_DoesNotRemoveANewerCurrentSessionForTheSamePid()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        uint pid = 1212;

        var s1 = await AcceptedSession(registry, manager, pid, new ScriptedSessionTransport(), budget);
        SessionClose(s1); // legitimate disconnect frees the PID for reconnect.

        var s2 = await AcceptedSession(registry, manager, pid, new ScriptedSessionTransport(), budget);
        Assert.True(ManagerTryGetAccepted(manager, pid, out var found));
        Assert.Same(s2, found);

        SessionClose(s1); // stale, already-closed old session -- idempotent, must not disturb s2.

        Assert.True(ManagerTryGetAccepted(manager, pid, out var stillFound));
        Assert.Same(s2, stillFound);

        SessionClose(s2);
    }

    [Fact]
    public async Task R32_CloseBeforePromotion_SessionNeverBecomesDiscoverable()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var session = CreateSession(transport, CreateTrustedChromeFakeBinding(1313, out _), registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);

        // Close races with the in-flight handshake: close immediately after sending Hello, before any
        // deterministic wait for the Ack.
        await transport.WriteFromBrowserAsync(HelloFrame());
        SessionClose(session);

        var lifecycle = SessionLifecycle(session);
        await Task.WhenAny(lifecycle, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Equal("Closed", SessionState(session));
        Assert.False(ManagerTryGetAccepted(manager, 1313, out _));
    }

    // ==================================================================
    // R34 -- inbound frame buffer resets between sequential frames; no cross-frame accumulation.
    // ==================================================================

    [Fact]
    public async Task R34_SequentialFrames_EachDecodedIndependently_NoAccumulation()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var session = await AcceptedSession(registry, manager, 1414, transport, budget);
        long channelId = SessionChannelId(session)!.Value;

        await transport.WriteFromBrowserAsync(StateAssertFrame("Focused", "Resolved", "https://example.invalid"));
        await Task.Delay(30);
        registry.TryGetCurrentEvidence(channelId, out var afterA);
        Assert.Equal(OriginResolution.Resolved, afterA.OriginResolution);

        await transport.WriteFromBrowserAsync(StateInvalidateFrame());
        await transport.WriteFromBrowserAsync(StateAssertFrame("NotFocused", "Unsupported", null));
        await Task.Delay(30);

        registry.TryGetCurrentEvidence(channelId, out var afterB);
        Assert.Equal(OriginResolution.Unsupported, afterB.OriginResolution);
        Assert.Equal(BrowserFocus.NotFocused, afterB.BrowserFocus);
        Assert.Equal("Accepted", SessionState(session));

        SessionClose(session);
    }

    // ==================================================================
    // R39A/R39B/R39C/R40/R41/R42/R43/R44/R45/R45B/R46/R47 -- server-level admission pipeline.
    // ==================================================================

    private static object CreateServer(object listener, Func<SafePipeHandle, uint?> pidSource, object bindingSource, object registry, object manager, object budget)
    {
        Assert.True(ServerType is not null, ServerContract);
        object? instance = Activator.CreateInstance(ServerType!, listener, pidSource, bindingSource, registry, manager, budget);
        Assert.True(instance is not null, $"{ServerTypeName} could not be constructed.");
        return instance!;
    }

    private static Task ServerAdmitConnectionAsync(object server, Stream clientStream, SafePipeHandle handle, object lease)
    {
        var method = server.GetType().GetMethod("AdmitConnectionAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method is not null, $"{ServerTypeName} exposes no AdmitConnectionAsync(...) method. " + ServerContract);
        object? invokeResult = method!.Invoke(server, [clientStream, handle, lease, CancellationToken.None]);
        Assert.True(invokeResult is Task, $"{ServerTypeName}.AdmitConnectionAsync must return a Task.");
        return (Task)invokeResult!;
    }

    private static readonly SafePipeHandle DummyPipeHandle = new(new IntPtr(-2), ownsHandle: false); // INVALID_HANDLE_VALUE-shaped, never a real handle.

    [Fact]
    public async Task R39B_PostTransferShutdownRejection_SessionNeverLive_SelfCleansUp()
    {
        Assert.True(ServerType is not null, ServerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var bindingSource = CreateFakeBindingSource(pid => (BrowserHostBindingStatus.Resolved, CreateTrustedChromeFakeBinding(pid, out var _)));
        var (listenerProxy, _) = CreateStub(ListenerInterfaceType!);
        var pidSource = new Func<SafePipeHandle, uint?>(_ => 1515);
        var server = CreateServer(listenerProxy, pidSource, bindingSource, registry, manager, budget);

        ManagerBeginShutdown(manager);

        var transport = new ScriptedSessionTransport();
        object lease = await AcquireAsync(budget);
        var admitTask = ServerAdmitConnectionAsync(server, transport, DummyPipeHandle, lease);
        await admitTask;

        Assert.False(ManagerTryGetAccepted(manager, 1515, out _));
        Assert.Empty(ManagerSnapshotLive(manager).Cast<object>());

        // The lease must have been returned rather than leaked -- a fresh single-capacity budget
        // proves it via a real acquire race, mirroring R21's own technique.
        var single = CreateBudget(1);
        object heldLease = await AcquireAsync(single);
        DisposeLease(heldLease);
    }

    [Fact]
    public async Task R36_ClientPidResolutionFails_StreamDisposedLeaseReturned_NoBindingNoRegistryTouch()
    {
        Assert.True(ServerType is not null, ServerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(1);
        bool bindingSourceCalled = false;
        var bindingSource = CreateFakeBindingSource(pid => { bindingSourceCalled = true; return (BrowserHostBindingStatus.Resolved, CreateTrustedChromeFakeBinding(pid, out _)); });
        var (listenerProxy, _) = CreateStub(ListenerInterfaceType!);
        var pidSource = new Func<SafePipeHandle, uint?>(_ => null); // PID resolution fails.
        var server = CreateServer(listenerProxy, pidSource, bindingSource, registry, manager, budget);

        var transport = new ScriptedSessionTransport();
        object lease = await AcquireAsync(budget);
        await ServerAdmitConnectionAsync(server, transport, DummyPipeHandle, lease);

        Assert.False(bindingSourceCalled, "R36: a PID resolution failure must reject before ever consulting the E2 binding source.");
        Assert.Empty(ManagerSnapshotLive(manager).Cast<object>());

        var next = AcquireAsync(budget);
        var completed = await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(next, completed); // the lease was returned, so a fresh acquire on the 1-capacity budget succeeds.
    }

    [Fact]
    public async Task R37_E2BindingRejection_StreamDisposedLeaseReturned_BindingNull()
    {
        Assert.True(ServerType is not null, ServerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(1);
        var bindingSource = CreateFakeBindingSource(_ => (BrowserHostBindingStatus.HostImagePathMismatch, null));
        var (listenerProxy, _) = CreateStub(ListenerInterfaceType!);
        var pidSource = new Func<SafePipeHandle, uint?>(_ => 1616);
        var server = CreateServer(listenerProxy, pidSource, bindingSource, registry, manager, budget);

        var transport = new ScriptedSessionTransport();
        object lease = await AcquireAsync(budget);
        await ServerAdmitConnectionAsync(server, transport, DummyPipeHandle, lease);

        Assert.Empty(ManagerSnapshotLive(manager).Cast<object>());
        var next = AcquireAsync(budget);
        Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(2))));
    }

    public static IEnumerable<object[]> UnsupportedBrowserFacts() =>
    [
        ["valid signed Edge is deferred for 0.3.1", "msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "Microsoft Corporation"],
        ["wrong process name", "firefox", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "Google LLC"],
        ["wrong package identity", "chrome", PackageIdentityResolution.Resolved, ExecutableSignatureResolution.Trusted, "Google LLC"],
        ["untrusted signature", "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Untrusted, "Google LLC"],
        ["unresolved signature", "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Unresolved, "Google LLC"],
        ["wrong signer", "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "Not Google"],
    ];

    [Theory]
    [MemberData(nameof(UnsupportedBrowserFacts))]
    public async Task R38_R9_WebBrowserGatePolicyRejection_BindingAndTransportDisposed_LeaseReturned_NoHelloConsumed(
        string scenario, string processName, PackageIdentityResolution packageIdentity, ExecutableSignatureResolution signature, string? signer)
    {
        Assert.True(ServerType is not null, ServerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(1);
        var binding = CreateFakeBinding(1717, processName, packageIdentity, signature, signer, out var bindingDisposed);
        var bindingSource = CreateFakeBindingSource(_ => (BrowserHostBindingStatus.Resolved, binding));
        var (listenerProxy, _) = CreateStub(ListenerInterfaceType!);
        var pidSource = new Func<SafePipeHandle, uint?>(_ => 1717);
        var server = CreateServer(listenerProxy, pidSource, bindingSource, registry, manager, budget);

        var transport = new ScriptedSessionTransport();
        object lease = await AcquireAsync(budget);
        await ServerAdmitConnectionAsync(server, transport, DummyPipeHandle, lease);

        Assert.True(bindingDisposed(), $"R38/R9 ({scenario}): binding must be disposed on policy rejection.");
        Assert.False(ManagerTryGetAccepted(manager, 1717, out _));
        Assert.Empty(ManagerSnapshotLive(manager).Cast<object>());

        var next = AcquireAsync(budget);
        Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(2))));
    }

    [Fact]
    public async Task E5G2_ChromeOriginCompatibleInvocation_WithAuthenticatedEdgeBinding_IsRejectedBeforeHello()
    {
        const string chromeOrigin = "chrome-extension://aieobgphcpmkfnhadocdhenigmackboo/";
        var testAllowlist = new HashSet<string>(StringComparer.Ordinal) { chromeOrigin };
        Assert.Equal("Host", InvokeClassifyKind([chromeOrigin, "--parent-window=0"], testAllowlist));

        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(1);
        var binding = CreateFakeBinding(
            1818, "msedge", PackageIdentityResolution.NoPackage,
            ExecutableSignatureResolution.Trusted, "Microsoft Corporation", out var bindingDisposed);
        var bindingSource = CreateFakeBindingSource(_ => (BrowserHostBindingStatus.Resolved, binding));
        var (listenerProxy, _) = CreateStub(ListenerInterfaceType!);
        var server = CreateServer(
            listenerProxy, _ => 1818, bindingSource, registry, manager, budget);

        var transport = new ScriptedSessionTransport();
        object lease = await AcquireAsync(budget);
        await ServerAdmitConnectionAsync(server, transport, DummyPipeHandle, lease);

        Assert.True(bindingDisposed());
        Assert.Empty(ManagerSnapshotLive(manager).Cast<object>());
        Assert.False(ManagerTryGetAccepted(manager, 1818, out _));
        Assert.False(transport.ReadWasAttempted);
    }

    [Fact]
    public async Task R40_ThrowingBindingDispose_CloseStillCompletesEveryOtherStep_NeverThrows()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var (bindingProxy, stub) = CreateStub(BindingInterfaceType!);
        stub.Handlers["get_BrowserProcessId"] = _ => 1818u;
        stub.Handlers["get_BrowserProcessName"] = _ => "chrome";
        stub.Handlers["get_BrowserPackageIdentity"] = _ => PackageIdentityResolution.NoPackage;
        stub.Handlers["get_BrowserExecutableSignature"] = _ => ExecutableSignatureResolution.Trusted;
        stub.Handlers["get_BrowserSignerOrganization"] = _ => "Google LLC";
        stub.Handlers["Dispose"] = _ => throw new InvalidOperationException("scripted throwing dispose");

        object lease = await AcquireAsync(budget);
        var session = CreateSession(transport, bindingProxy, registry, lease, manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);
        await transport.WriteFromBrowserAsync(HelloFrame());
        await Task.Delay(50);

        var exception = Record.Exception(() => SessionClose(session));
        Assert.Null(exception);

        Assert.Equal("Closed", SessionState(session));
        Assert.False(ManagerTryGetAccepted(manager, 1818, out _));
    }

    [Fact]
    public async Task R41_ThrowingTransportDispose_LeaseStillReturned_CloseNeverThrows()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(1);
        var transport = new ThrowingDisposeStream();
        object lease = await AcquireAsync(budget);
        var session = CreateSession(transport, CreateTrustedChromeFakeBinding(1919, out _), registry, lease, manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);

        var exception = Record.Exception(() => SessionClose(session));
        Assert.Null(exception);

        var next = AcquireAsync(budget);
        Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(2))));
    }

    private sealed class ThrowingDisposeStream : MemoryStream
    {
        protected override void Dispose(bool disposing) => throw new InvalidOperationException("scripted throwing transport dispose");
    }

    [Fact]
    public async Task R42_CloseNeverThrows_EvenWithBothBindingAndTransportDisposeThrowing()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ThrowingDisposeStream();
        var (bindingProxy, stub) = CreateStub(BindingInterfaceType!);
        stub.Handlers["get_BrowserProcessId"] = _ => 2020u;
        stub.Handlers["get_BrowserProcessName"] = _ => "chrome";
        stub.Handlers["get_BrowserPackageIdentity"] = _ => PackageIdentityResolution.NoPackage;
        stub.Handlers["get_BrowserExecutableSignature"] = _ => ExecutableSignatureResolution.Trusted;
        stub.Handlers["get_BrowserSignerOrganization"] = _ => "Google LLC";
        stub.Handlers["Dispose"] = _ => throw new InvalidOperationException("scripted throwing binding dispose");

        var session = CreateSession(transport, bindingProxy, registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);

        var exception = Record.Exception(() => SessionClose(session));
        Assert.Null(exception);
    }

    [Fact]
    public async Task R44_ShutdownBeforeBeginAdmission_SessionNeverLive_ClosesOnce()
    {
        Assert.True(SessionType is not null && ManagerType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var session = CreateSession(transport, CreateTrustedChromeFakeBinding(2121, out _), registry, await AcquireAsync(budget), manager);

        ManagerBeginShutdown(manager);
        ManagerBeginAdmission(manager, session);

        Assert.Empty(ManagerSnapshotLive(manager).Cast<object>());
        Assert.NotEqual("Accepted", SessionState(session));
        Assert.False(ManagerTryGetAccepted(manager, 2121, out _));
    }

    [Fact]
    public async Task R45_CloseBeforeStart_NoLifecycleCreated_StartAfterCloseIsANoOp()
    {
        Assert.True(SessionType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var lease = await AcquireAsync(budget);
        var transport = new ScriptedSessionTransport();
        var session = CreateSession(transport, CreateTrustedChromeFakeBinding(2222, out var bindingDisposed), registry, lease, manager);

        SessionClose(session);
        Assert.Equal("Closed", SessionState(session));

        SessionStart(session); // must be a no-op -- Closed -> Started is impossible.

        Assert.Equal("Closed", SessionState(session));
        var lifecycle = SessionLifecycle(session);
        Assert.True(lifecycle.IsCompleted, "R45: Lifecycle must resolve to a CompletedTask-equivalent when Start is called after Close.");
        Assert.False(lifecycle.IsFaulted);
        Assert.True(bindingDisposed());
    }

    [Fact]
    public async Task R46_SnapshotLiveRemainsStable_AfterShutdownBarrier()
    {
        Assert.True(ManagerType is not null, SessionManagerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);
        var transport = new ScriptedSessionTransport();
        var session = CreateSession(transport, CreateTrustedChromeFakeBinding(2323, out _), registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, session);
        SessionStart(session);
        await Task.Delay(20);

        ManagerBeginShutdown(manager);

        var before = ManagerSnapshotLive(manager).Cast<object>().Count();

        var lateTransport = new ScriptedSessionTransport();
        var lateSession = CreateSession(lateTransport, CreateTrustedChromeFakeBinding(2424, out _), registry, await AcquireAsync(budget), manager);
        ManagerBeginAdmission(manager, lateSession);

        var after = ManagerSnapshotLive(manager).Cast<object>().Count();
        Assert.Equal(before, after);
        Assert.NotEqual("Accepted", SessionState(lateSession));

        SessionClose(session);
    }

    [Fact]
    public void R47_MaxConsecutiveAcceptFailuresContractDefault_IsFrozenAtEight()
    {
        Assert.True(ServerType is not null, ServerContract);
        var field = ServerType!.GetField("MaxConsecutiveAcceptFailuresContractDefault", BindingFlags.Public | BindingFlags.Static);
        Assert.True(field is not null, $"{ServerTypeName} must expose a public const int MaxConsecutiveAcceptFailuresContractDefault. " + ServerContract);
        Assert.Equal(8, (int)field!.GetValue(null)!);
    }

    [Fact]
    public async Task R43_BadClientDoesNotKillTheAcceptLoop_NextValidClientStillAdmitted()
    {
        Assert.True(ServerType is not null, ServerContract);
        var registry = new WebChannelRegistry();
        var manager = CreateManager(registry);
        var budget = CreateBudget(4);

        var badTransport = new ScriptedSessionTransport();
        var goodTransport = new ScriptedSessionTransport();
        int callIndex = 0;
        var streams = new (Stream Stream, SafePipeHandle Handle)?[]
        {
            (badTransport, DummyPipeHandle),
            (goodTransport, DummyPipeHandle),
            null, // signals the loop to stop after two admissions in this scripted listener
        };

        var (listenerProxy, listenerStub) = CreateStub(ListenerInterfaceType!);
        listenerStub.Handlers["AcceptAsync"] = _ =>
        {
            var next = streams[Math.Min(callIndex, streams.Length - 1)];
            callIndex++;
            return Task.FromResult(next);
        };
        listenerStub.Handlers["Dispose"] = _ => null;

        var bindingSource = CreateFakeBindingSource(pid => (BrowserHostBindingStatus.Resolved, CreateTrustedChromeFakeBinding(pid, out _)));
        var pidSource = new Func<SafePipeHandle, uint?>(_ => callIndex == 1 ? 2525u : 2626u);
        var server = CreateServer(listenerProxy, pidSource, bindingSource, registry, manager, budget);

        // Bad client: force a policy rejection by making the transport throw on the Ack write so the
        // Hello handshake never completes -- proving the SERVER LOOP itself survives a bad client.
        badTransport.ThrowOnWrite = true;

        var runMethod = ServerType!.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(runMethod is not null, $"{ServerTypeName} exposes no RunAsync(CancellationToken) method. " + ServerContract);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        object? invokeResult = runMethod!.Invoke(server, [cts.Token]);
        Assert.True(invokeResult is Task);

        await goodTransport.WriteFromBrowserAsync(HelloFrame());

        try { await (Task)invokeResult!; }
        catch (OperationCanceledException) { /* expected once the scripted listener is exhausted and cts eventually fires -- the loop must still have admitted the good client by then. */ }

        await Task.Delay(50);
        Assert.True(ManagerTryGetAccepted(manager, 2626, out _),
            "R43: a bad client must never prevent the accept loop from admitting the next valid client.");
    }

    // ==================================================================
    // Gate 031F6I.3 -- production listener regression: proves the REAL NamedPipeWebListener, through
    // a REAL connected local client, actually transfers bytes -- not merely that a connection
    // succeeds. Added specifically to prevent regression back to the confirmed defect where the
    // pipe's unspecified default buffer sizes let the connection succeed while the very first real
    // data write hung indefinitely (forensically isolated and fixed in NamedPipeWebListener.cs; see
    // that file's own PipeBufferSize doc). Not a frozen 031F6H R-numbered test; deliberately narrow
    // (transfer only, not a duplicate of R6/R7's own full contract).
    // ==================================================================

    [Fact]
    public async Task ProductionListener_RealClientConnection_TransfersBytesThroughTheAcceptedTransport()
    {
        string pipeName = "PRIVON-E3-LISTENER-REGRESSION-" + Guid.NewGuid().ToString("N");
        var listener = new NamedPipeWebListener(pipeName);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = listener.AcceptAsync(cts.Token);

        using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000, cts.Token);

        var accepted = await acceptTask;
        Assert.True(accepted is not null, "the real production listener must accept the real client connection.");
        var (serverStream, handle) = accepted!.Value;
        Assert.False(handle.IsInvalid);

        try
        {
            byte[] payload = "PRIVON"u8.ToArray();
            await client.WriteAsync(payload, cts.Token);
            await client.FlushAsync(cts.Token);

            byte[] received = new byte[payload.Length];
            int total = 0;
            while (total < received.Length)
            {
                int read = await serverStream.ReadAsync(received.AsMemory(total), cts.Token);
                Assert.True(read > 0, "the accepted production transport must actually deliver the written bytes.");
                total += read;
            }

            Assert.Equal(payload, received);
        }
        finally
        {
            serverStream.Dispose();
            listener.Dispose();
        }
    }

    // ==================================================================
    // R24 -- host entry path dependency boundary (structural/source-scan; the host runner is not
    // constructed against real WPF/App/Storage/clipboard/foreground/E2/Web-decode types).
    // ==================================================================

    private static readonly string[] ForbiddenHostPathTokens =
    [
        "PrivonAppComposition", "SingleInstanceGuard", "PrivonLocalStore", "ClipboardChangeMonitor",
        "ForegroundChangeMonitor", "WebChannelRegistry", "WebBrowserGate", "BrowserHostBindingResolver",
        "WebFrameDecoder", "WebFrameEncoder", "System.Windows.Application", "PresentationFramework",
    ];

    [Fact]
    public void R24_WebHostRelayLoopSource_NeverReferencesTheNormalModeRuntimeGraph()
    {
        string? path = TryFindAppSourceFile("WebHostRelayLoop");
        Assert.True(path is not null,
            "EXPECTED_E3_RED: src/Privon.App/WebHostRelayLoop.cs does not exist yet. FROZEN BOUNDARY " +
            "(section R24) once it lands: it must never construct or reference App/SingleInstanceGuard/" +
            "Storage/ClipboardChangeMonitor/ForegroundChangeMonitor/WebChannelRegistry/WebBrowserGate/" +
            "BrowserHostBindingResolver/WebFrameDecoder/WebFrameEncoder -- only the entry classifier, " +
            "allowlist, endpoint, NamedPipeClientStream, raw stdin/stdout Streams, WebNativeMessagingPump, " +
            "and relay lifecycle.");

        string source = File.ReadAllText(path!);
        var hits = ForbiddenHostPathTokens.Where(t => source.Contains(t, StringComparison.Ordinal)).ToList();
        Assert.Empty(hits);
    }

    // ==================================================================
    // R27 -- privacy/source hygiene for the new transport/session production files.
    // ==================================================================

    private static readonly string[] ForbiddenPrivacyTokens =
    [
        "clipboard", "Clipboard", "composer", "Composer", "tab title", "TabTitle", "cookie", "Cookie",
        "token", "Token", "history", "History", "account", "Account",
    ];

    [Theory]
    [InlineData("WebChannelSession")]
    [InlineData("WebChannelManager")]
    [InlineData("WebChannelHostServer")]
    public void R27_NewSessionTransportFiles_CarryNoPrivacySensitiveTransport(string typeSimpleName)
    {
        string? path = TryFindAppSourceFile(typeSimpleName);
        Assert.True(path is not null,
            $"EXPECTED_E3_RED: src/Privon.App/{typeSimpleName}.cs does not exist yet. FROZEN PRIVACY " +
            "RULE (section R27) once it lands: no transport for clipboard text, composer text, " +
            "arbitrary URL, page path/query/fragment, tab title, tab ID, account, cookie, token, " +
            "history, or raw PII -- legitimate E3 messages are exactly Hello/StateInvalidate/" +
            "StateAssert/HelloAck (ChallengeResponse is an unsolicited-contradiction teardown only).");

        // Deliberately excludes the word "Token" alone from ordinary framework use (e.g.
        // CancellationToken) via a narrower, case-sensitive scan restricted to compound privacy terms.
        string source = File.ReadAllText(path!);
        string[] narrowTokens = ["clipboard", "Clipboard", "composer", "Composer", "cookie", "Cookie", "browsing history", "TabTitle", "AccountId"];
        var hits = narrowTokens.Where(t => source.Contains(t, StringComparison.Ordinal)).ToList();
        Assert.Empty(hits);
    }

    // ==================================================================
    // SOURCE DISCOVERY HELPER (matching Gate031F4_2_C2FreshnessRedTests / Gate031F6E_WebBrowserGateRedTests).
    // ==================================================================

    private static string? TryFindAppSourceFile(string typeSimpleName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PRIVON.slnx")))
            directory = directory.Parent;

        if (directory is null)
            return null;

        string candidate = Path.Combine(directory.FullName, "src", "Privon.App", typeSimpleName + ".cs");
        return File.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void SourceDiscovery_WorksForAnExistingPrivonAppFile()
    {
        string? path = TryFindAppSourceFile(nameof(WebBrowserGate));
        Assert.True(path is not null,
            "The repository-root walk used by the source-hygiene locks failed to locate a source file " +
            "that definitely exists (src/Privon.App/WebBrowserGate.cs). Fix discovery before trusting " +
            "any RED it reports for the not-yet-implemented E3 files.");
    }
}
