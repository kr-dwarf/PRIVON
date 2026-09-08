using System.Reflection;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5G.P1 -- behavioral RED harness for the not-yet-existing PRODUCTION concrete
// implementation of the FROZEN Privon.App.INativeMessagingHostRegistrationEnvironment (E5D).
//
// Resolves, by reflection, exactly the future production shape this gate introduces:
//
//   internal interface Privon.App.IHkcuRegistryLeafMechanics
//   {
//       bool    LeafExists(string subKeyPath);
//       string? GetRawDefaultValue(string subKeyPath);
//       void    SetDefaultValue(string subKeyPath, string value);
//       void    DeleteLeaf(string subKeyPath);
//   }
//
//   internal sealed class Privon.App.WindowsNativeMessagingHostRegistrationEnvironment
//       : INativeMessagingHostRegistrationEnvironment
//   {
//       WindowsNativeMessagingHostRegistrationEnvironment();                       // production
//       internal WindowsNativeMessagingHostRegistrationEnvironment(IHkcuRegistryLeafMechanics); // seam
//   }
//
// WHY REFLECTION: mirrors the established Gate E5C/E5D/E5F convention exactly -- the test assembly
// must still BUILD against unchanged production code, so every not-yet-existing member is located at
// runtime and each test fails with a specific, case-attributable message rather than a compile error.
// The two types that ALREADY exist (INativeMessagingHostRegistrationEnvironment and
// NativeMessagingBrowser) are referenced directly at compile time instead -- Privon.App grants
// InternalsVisibleTo to this project -- so the tests themselves read as ordinary behavioral calls.
//
// NO REAL OS ACCESS: the registry half is driven entirely through the in-memory
// FakeRegistryLeafMechanics below (bridged to the future seam interface via DispatchProxy, the same
// technique NativeMessagingHostRegistrarTestHarness already establishes). This suite NEVER reads or
// mutates the real HKCU Chrome/Edge NativeMessagingHosts leaves, never touches HKLM, and never
// touches a real production manifest -- per this gate's own explicit testability requirement, which
// is deliberately STRICTER than WindowsAutoStartManagerTests's real-HKCU-scratch-key precedent.
// The filesystem half needs no seam at all: the frozen interface already takes an arbitrary
// caller-supplied path, so those tests use a disposable temp directory and exercise the REAL
// File/Directory mechanics.
//
// PRIMITIVE-ONLY BOUNDARY: the proxy forwards each seam call to exactly the matching
// FakeRegistryLeafMechanics primitive and nothing else. No OWNED/STALE/FOREIGN/orphan decision, no
// manifest-content decision, and no browser->path mapping lives in the fake -- the browser->leaf
// mapping is precisely what the type under test owns and what these tests exist to verify.
internal static class Gate031E5GP1_RegistrationEnvironmentTestHarness
{
    private static Assembly AppAssembly => typeof(WebBrowserGate).Assembly;

    public const string SeamInterfaceName = "Privon.App.IHkcuRegistryLeafMechanics";
    public const string AdapterTypeName = "Privon.App.WindowsNativeMessagingHostRegistrationEnvironment";
    public const string MechanicsTypeName = "Privon.App.HkcuRegistryLeafMechanics";

    // ==================================================================
    // FROZEN EXACT LEAF PATHS (Gate E5C, commander-frozen) -- declared INDEPENDENTLY here, never read
    // back out of production, so an assertion against them can never be circular. If production maps a
    // browser to any other path, that independence is exactly what turns these tests RED.
    // ==================================================================

    public const string ChromeLeafSubKeyPath = @"Software\Google\Chrome\NativeMessagingHosts\com.privon.host";
    public const string EdgeLeafSubKeyPath = @"Software\Microsoft\Edge\NativeMessagingHosts\com.privon.host";

    /// <summary>The NativeMessagingHosts parent of each frozen leaf -- never a legitimate mutation
    /// target for anything in this gate. Used only to prove the adapter never asks for it.</summary>
    public const string ChromeParentSubKeyPath = @"Software\Google\Chrome\NativeMessagingHosts";
    public const string EdgeParentSubKeyPath = @"Software\Microsoft\Edge\NativeMessagingHosts";

    // ==================================================================
    // DYNAMIC INTERFACE STUB -- reuses the established
    // NativeMessagingHostRegistrarTestHarness.DynamicInterfaceStub / CreateStub pattern verbatim.
    // ==================================================================

    public class DynamicInterfaceStub : DispatchProxy
    {
        public Dictionary<string, Func<object?[], object?>> Handlers { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            string name = targetMethod!.Name;
            if (!Handlers.TryGetValue(name, out var handler))
                throw new InvalidOperationException($"DynamicInterfaceStub: no handler registered for '{name}'.");
            return handler(args ?? []);
        }
    }

    private static (object Proxy, DynamicInterfaceStub Stub) CreateStub(Type interfaceType)
    {
        var createMethod = typeof(DispatchProxy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(DispatchProxy.Create) && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 0)
            .MakeGenericMethod(interfaceType, typeof(DynamicInterfaceStub));
        object proxy = createMethod.Invoke(null, null)!;
        return (proxy, (DynamicInterfaceStub)proxy);
    }

    // ==================================================================
    // HARNESS CONSTRUCTION
    // ==================================================================

    /// <summary>
    /// Resolves the future seam interface and adapter type, builds a real runtime object implementing
    /// the seam (forwarding to <paramref name="fake"/>), and constructs the adapter against it.
    /// Returns <see langword="false"/> with a specific reason while any piece is still missing.
    /// </summary>
    public static bool TryBuildAdapter(
        FakeRegistryLeafMechanics fake,
        out INativeMessagingHostRegistrationEnvironment? adapter,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(fake);
        adapter = null;

        var seamType = AppAssembly.GetType(SeamInterfaceName);
        if (seamType is null)
        {
            failureReason = $"{SeamInterfaceName} does not exist yet -- Gate E5G.P1 requires the narrow " +
                "HKCU registry-leaf mechanics seam the production adapter delegates to.";
            return false;
        }

        if (!seamType.IsInterface)
        {
            failureReason = $"{SeamInterfaceName} exists but is not an interface.";
            return false;
        }

        foreach (string required in new[] { "LeafExists", "GetRawDefaultValue", "SetDefaultValue", "DeleteLeaf" })
        {
            if (seamType.GetMethod(required) is null)
            {
                failureReason = $"{SeamInterfaceName} exists but has no '{required}' method.";
                return false;
            }
        }

        var adapterType = AppAssembly.GetType(AdapterTypeName);
        if (adapterType is null)
        {
            failureReason = $"{AdapterTypeName} does not exist yet -- Gate E5G.P1 requires the concrete " +
                "production implementation of the frozen INativeMessagingHostRegistrationEnvironment.";
            return false;
        }

        if (!typeof(INativeMessagingHostRegistrationEnvironment).IsAssignableFrom(adapterType))
        {
            failureReason = $"{AdapterTypeName} exists but does not implement " +
                "Privon.App.INativeMessagingHostRegistrationEnvironment.";
            return false;
        }

        var seamConstructor = adapterType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [seamType],
            modifiers: null);
        if (seamConstructor is null)
        {
            failureReason = $"{AdapterTypeName} exists but has no constructor taking exactly one " +
                $"{SeamInterfaceName} -- required so tests can drive it without real HKCU access.";
            return false;
        }

        var (proxy, stub) = CreateStub(seamType);
        WireHandlers(stub, fake);

        adapter = (INativeMessagingHostRegistrationEnvironment)seamConstructor.Invoke([proxy]);
        failureReason = "";
        return true;
    }

    /// <summary>Routes each seam call into the matching <see cref="FakeRegistryLeafMechanics"/>
    /// primitive. Contains no mapping, ownership, or manifest decision of its own.</summary>
    private static void WireHandlers(DynamicInterfaceStub stub, FakeRegistryLeafMechanics fake)
    {
        stub.Handlers["LeafExists"] = args => fake.LeafExists((string)args[0]!);
        stub.Handlers["GetRawDefaultValue"] = args => fake.GetRawDefaultValue((string)args[0]!);
        stub.Handlers["SetDefaultValue"] = args =>
        {
            fake.SetDefaultValue((string)args[0]!, (string)args[1]!);
            return null;
        };
        stub.Handlers["DeleteLeaf"] = args =>
        {
            fake.DeleteLeaf((string)args[0]!);
            return null;
        };
    }

    // ==================================================================
    // PRODUCTION SOURCE LOCATION (mirrors Gate031F6K_E4RedTests / Gate031E5F's own
    // TryFindAppSourceFile convention exactly).
    // ==================================================================

    public static string? TryFindAppSourceFile(string typeNameWithoutExtension)
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

/// <summary>
/// PRIVON 0.3.1 Gate E5G.P1 -- fully in-memory stand-in for the future
/// <c>Privon.App.IHkcuRegistryLeafMechanics</c>. Keyed by the EXACT subkey path string the type under
/// test supplies, so a test can prove which leaf was addressed without the fake ever knowing that a
/// browser axis exists at all.
///
/// Deliberately has no concept of a registry hive, a parent key, a sibling key, or recursion: there is
/// simply no way to express "delete the parent" or "reach HKLM" through this fake, so a test asserting
/// those never happen is asserting against a seam that could not express them either.
///
/// Values are stored and returned VERBATIM (Ordinal), with no expansion of any kind -- a seeded
/// "%LOCALAPPDATA%\..." value comes back as that literal string, which is exactly what the
/// security-critical raw-default-value contract requires.
/// </summary>
internal sealed class FakeRegistryLeafMechanics
{
    private readonly Dictionary<string, string?> _leaves = new(StringComparer.Ordinal);

    public List<string> CallLog { get; } = [];

    // ---- Seeding / post-hoc verification (test-side only -- never reachable by the type under test) ----

    public void SeedLeaf(string subKeyPath, string? rawDefaultValue) => _leaves[subKeyPath] = rawDefaultValue;

    public bool LeafPresent(string subKeyPath) => _leaves.ContainsKey(subKeyPath);

    public string? RawValueAt(string subKeyPath) => _leaves.TryGetValue(subKeyPath, out var value) ? value : null;

    // ---- The seam primitives themselves ----

    public bool LeafExists(string subKeyPath)
    {
        CallLog.Add($"{nameof(LeafExists)}({subKeyPath})");
        return _leaves.ContainsKey(subKeyPath);
    }

    public string? GetRawDefaultValue(string subKeyPath)
    {
        CallLog.Add($"{nameof(GetRawDefaultValue)}({subKeyPath})");
        return _leaves.TryGetValue(subKeyPath, out var value) ? value : null;
    }

    public void SetDefaultValue(string subKeyPath, string value)
    {
        CallLog.Add($"{nameof(SetDefaultValue)}({subKeyPath}, {value})");
        _leaves[subKeyPath] = value;
    }

    public void DeleteLeaf(string subKeyPath)
    {
        CallLog.Add($"{nameof(DeleteLeaf)}({subKeyPath})");
        _leaves.Remove(subKeyPath);
    }
}
