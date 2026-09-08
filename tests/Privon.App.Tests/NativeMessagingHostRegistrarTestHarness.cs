using System.Reflection;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5D.B -- behavioral RED harness for the not-yet-corrected production Native
// Messaging host registrar. Resolves, by reflection, the FROZEN future production shape:
//
//   internal enum Privon.App.NativeMessagingBrowser { Chrome = 1, Edge = 2 }
//   internal interface Privon.App.INativeMessagingHostRegistrationEnvironment { ... }
//   internal sealed class/record Privon.App.NativeMessagingHostRegistrationSpec(
//       string HostExecutablePath, string ExtensionOrigin)
//   internal sealed class Privon.App.NativeMessagingHostRegistrar(
//       INativeMessagingHostRegistrationEnvironment environment)
//   {
//       object? Install(NativeMessagingBrowser browser, NativeMessagingHostRegistrationSpec spec);
//       object? Uninstall(NativeMessagingBrowser browser);
//   }
//
// then, ONLY once all of those exist, builds a REAL runtime object genuinely implementing
// INativeMessagingHostRegistrationEnvironment (via System.Reflection.DispatchProxy -- the same
// general-purpose technique already established in Gate031F6H_E3RedTests.cs's own
// DynamicInterfaceStub/CreateStub), constructs the registrar against it, constructs a synthetic
// NativeMessagingHostRegistrationSpec, and returns everything a test needs to actually INVOKE
// Install/Uninstall and assert on the resulting state.
//
// PRIMITIVE-ONLY BOUNDARY: unchanged from Gate E5C.2R -- the proxy forwards each interface call to
// exactly the matching FakeHostRegistrationEnvironment primitive and nothing else. No OWNED/STALE/
// FOREIGN/orphan decision, and no manifest-content decision, lives in the proxy.
internal static class NativeMessagingHostRegistrarTestHarness
{
    private static Assembly AppAssembly => typeof(WebBrowserGate).Assembly;

    // ==================================================================
    // DYNAMIC INTERFACE STUB -- unchanged from Gate E5C.2R; reuses the established
    // Gate031F6H_E3RedTests.DynamicInterfaceStub / CreateStub pattern.
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
    // RESOLUTION PIPELINE
    // ==================================================================

    public sealed record RegistrarHarness(
        object RegistrarInstance,
        ConstructorInfo SpecConstructor,
        ParameterInfo[] SpecConstructorParameters,
        MethodInfo InstallMethod,
        MethodInfo UninstallMethod,
        object ChromeBrowserValue,
        object EdgeBrowserValue);

    /// <summary>Resolves the frozen future production shape, in order, against
    /// <paramref name="fake"/>. Returns false with a specific, step-attributable
    /// <paramref name="failureReason"/> the moment any required type/member is missing -- a
    /// legitimate RED boundary until E5D.C corrects the implementation.</summary>
    public static bool TryBuildHarness(
        FakeHostRegistrationEnvironment fake, out RegistrarHarness? harness, out string failureReason)
    {
        harness = null;

        var browserType = AppAssembly.GetType("Privon.App.NativeMessagingBrowser");
        if (browserType is null)
        {
            failureReason = "Privon.App.NativeMessagingBrowser does not exist yet.";
            return false;
        }
        if (!browserType.IsEnum)
        {
            failureReason = "Privon.App.NativeMessagingBrowser exists but is not an enum.";
            return false;
        }
        var browserNames = Enum.GetNames(browserType);
        if (!browserNames.Contains("Chrome") || !browserNames.Contains("Edge"))
        {
            failureReason = "Privon.App.NativeMessagingBrowser exists but does not contain both " +
                "Chrome and Edge members.";
            return false;
        }

        var environmentInterfaceType = AppAssembly.GetType("Privon.App.INativeMessagingHostRegistrationEnvironment");
        if (environmentInterfaceType is null)
        {
            failureReason = "Privon.App.INativeMessagingHostRegistrationEnvironment does not exist yet.";
            return false;
        }
        if (!environmentInterfaceType.IsInterface)
        {
            failureReason = "Privon.App.INativeMessagingHostRegistrationEnvironment exists but is not an interface.";
            return false;
        }

        // Gate E5D.B: the future composition-input spec, carrying ONLY HostExecutablePath and
        // ExtensionOrigin -- never a manifest path (that remains registrar-owned/deterministic).
        var specType = AppAssembly.GetType("Privon.App.NativeMessagingHostRegistrationSpec");
        if (specType is null)
        {
            failureReason = "Privon.App.NativeMessagingHostRegistrationSpec does not exist yet " +
                "(Gate E5D.B frozen future contract: exposes exactly HostExecutablePath and " +
                "ExtensionOrigin, both string).";
            return false;
        }
        var specCtor = specType
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 2
                && c.GetParameters().All(p => p.ParameterType == typeof(string)));
        if (specCtor is null)
        {
            failureReason = "Privon.App.NativeMessagingHostRegistrationSpec exists but has no " +
                "constructor taking exactly two string parameters (HostExecutablePath, ExtensionOrigin).";
            return false;
        }
        var specParameters = specCtor.GetParameters();
        bool namesResolvable = specParameters.Any(p =>
            string.Equals(p.Name, "HostExecutablePath", StringComparison.OrdinalIgnoreCase))
            && specParameters.Any(p => string.Equals(p.Name, "ExtensionOrigin", StringComparison.OrdinalIgnoreCase));
        if (!namesResolvable)
        {
            failureReason = "Privon.App.NativeMessagingHostRegistrationSpec's two-string constructor " +
                "exists but its parameters are not named HostExecutablePath/ExtensionOrigin (found: " +
                string.Join(", ", specParameters.Select(p => p.Name)) + ").";
            return false;
        }

        var registrarType = AppAssembly.GetType("Privon.App.NativeMessagingHostRegistrar");
        if (registrarType is null)
        {
            failureReason = "Privon.App.NativeMessagingHostRegistrar does not exist yet.";
            return false;
        }

        var ctor = registrarType
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1
                && c.GetParameters()[0].ParameterType == environmentInterfaceType);
        if (ctor is null)
        {
            failureReason = "Privon.App.NativeMessagingHostRegistrar exists but has no constructor " +
                "taking exactly one INativeMessagingHostRegistrationEnvironment parameter.";
            return false;
        }

        // Gate E5D.B: Install now takes (browser, spec) -- NOT (browser) alone.
        var installMethod = registrarType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Install"
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == browserType
                && m.GetParameters()[1].ParameterType == specType);
        if (installMethod is null)
        {
            failureReason = "Privon.App.NativeMessagingHostRegistrar exists but has no " +
                "Install(NativeMessagingBrowser, NativeMessagingHostRegistrationSpec) method " +
                "(Gate E5D.B: Install(browser) alone is no longer the frozen contract).";
            return false;
        }

        var uninstallMethod = registrarType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Uninstall" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == browserType);
        if (uninstallMethod is null)
        {
            failureReason = "Privon.App.NativeMessagingHostRegistrar exists but has no " +
                "Uninstall(NativeMessagingBrowser) method.";
            return false;
        }

        var (proxy, stub) = CreateStub(environmentInterfaceType);
        WireHandlers(stub, fake);

        object registrarInstance = ctor.Invoke([proxy]);

        object chromeValue = Enum.Parse(browserType, "Chrome");
        object edgeValue = Enum.Parse(browserType, "Edge");

        harness = new RegistrarHarness(
            registrarInstance, specCtor, specParameters, installMethod, uninstallMethod, chromeValue, edgeValue);
        failureReason = "";
        return true;
    }

    /// <summary>Constructs a real instance of the future NativeMessagingHostRegistrationSpec, mapping
    /// purely by (case-insensitive) constructor parameter name -- never by guessed positional order --
    /// so a spec whose parameters are declared in either order resolves correctly.</summary>
    public static object BuildSpec(RegistrarHarness harness, string hostExecutablePath, string extensionOrigin)
    {
        var args = new object[harness.SpecConstructorParameters.Length];
        for (int i = 0; i < harness.SpecConstructorParameters.Length; i++)
        {
            string? paramName = harness.SpecConstructorParameters[i].Name;
            args[i] = paramName switch
            {
                not null when string.Equals(paramName, "HostExecutablePath", StringComparison.OrdinalIgnoreCase) => hostExecutablePath,
                not null when string.Equals(paramName, "ExtensionOrigin", StringComparison.OrdinalIgnoreCase) => extensionOrigin,
                _ => throw new InvalidOperationException(
                    $"NativeMessagingHostRegistrationSpec constructor parameter '{paramName}' is " +
                    "neither HostExecutablePath nor ExtensionOrigin."),
            };
        }

        return harness.SpecConstructor.Invoke(args);
    }

    /// <summary>Routes each expected primitive INativeMessagingHostRegistrationEnvironment call into
    /// the matching FakeHostRegistrationEnvironment member. Contains no OWNED/STALE/FOREIGN/orphan or
    /// manifest-content decision -- see this file's own header.</summary>
    private static void WireHandlers(DynamicInterfaceStub stub, FakeHostRegistrationEnvironment fake)
    {
        stub.Handlers["SubkeyExists"] = args => fake.SubkeyExists(MapBrowser(args[0]!));
        stub.Handlers["GetSubkeyDefaultValue"] = args => fake.GetSubkeyDefaultValue(MapBrowser(args[0]!));
        stub.Handlers["SetSubkeyDefaultValue"] = args =>
        {
            fake.SetSubkeyDefaultValue(MapBrowser(args[0]!), (string)args[1]!);
            return null;
        };
        stub.Handlers["DeleteSubkey"] = args =>
        {
            fake.DeleteSubkey(MapBrowser(args[0]!));
            return null;
        };
        stub.Handlers["ManifestExists"] = args => fake.ManifestExists((string)args[0]!);
        stub.Handlers["ReadManifest"] = args => fake.ReadManifest((string)args[0]!);
        stub.Handlers["WriteManifest"] = args =>
        {
            fake.WriteManifest((string)args[0]!, (string)args[1]!);
            return null;
        };
        stub.Handlers["DeleteManifest"] = args =>
        {
            fake.DeleteManifest((string)args[0]!);
            return null;
        };
    }

    /// <summary>Maps a boxed value of the PRODUCTION Privon.App.NativeMessagingBrowser enum (known
    /// only by name -- never referenced at compile time) to this test project's own fixture-side
    /// <see cref="NativeMessagingBrowser"/> enum, purely by member name.</summary>
    private static NativeMessagingBrowser MapBrowser(object productionEnumValue) =>
        Enum.Parse<NativeMessagingBrowser>(productionEnumValue.ToString()!);

    // ==================================================================
    // FROZEN MANIFEST PATH FORMULA (Gate E5D.B, commander-frozen) -- computed here INDEPENDENTLY of
    // whatever production actually does, so a test asserting against it can never be circular. If
    // production computes a different path, the independence itself is what turns the suite RED.
    // ==================================================================

    public static string ExpectedManifestPath(NativeMessagingBrowser browser)
    {
        string fileName = browser switch
        {
            NativeMessagingBrowser.Chrome => "chrome-host.json",
            NativeMessagingBrowser.Edge => "edge-host.json",
            _ => throw new ArgumentOutOfRangeException(nameof(browser), browser, "Unrecognized NativeMessagingBrowser value."),
        };

        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PRIVON", "NativeMessaging", fileName);
    }
}
