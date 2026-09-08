using System.Reflection;
using AppBrowser = Privon.App.NativeMessagingBrowser;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5G.P3 -- reflection-based RED harness for the not-yet-existing production
// Privon.App.NativeMessagingHostRegistrationCoordinator, mirroring the established
// Gate031F6K_E4RedTests / Gate031E5F / NativeMessagingHostRegistrarTestHarness convention exactly:
// every not-yet-existing member is located at runtime so the test assembly still BUILDS against
// unchanged production code, and each test fails with a specific, case-attributable message rather
// than a compile error.
//
// Only the coordinator TYPE and its readiness ENUM are new -- INativeMessagingHostRegistrationEnvironment
// and AppBrowser already exist in production (Gate E5D) and are referenced directly at
// compile time (InternalsVisibleTo), so no DispatchProxy/dynamic-stub machinery is needed here at all.

/// <summary>This test project's own mirror of the production readiness enum, resolved purely by
/// member NAME from whatever the coordinator actually returns -- never assumed to exist with this
/// exact shape until <see cref="Gate031E5GP3_RegistrationCoordinatorTestHarness.TryBuildCoordinator"/>
/// has already verified it.</summary>
internal enum CoordinatorReadiness
{
    Fresh,
    Ready,
    OwnedNeedsRepair,
    ForeignBlocked,
    OrphanBlocked,
    Failed,
}

internal static class Gate031E5GP3_RegistrationCoordinatorTestHarness
{
    private static Assembly AppAssembly => typeof(WebBrowserGate).Assembly;

    public const string CoordinatorTypeName = "Privon.App.NativeMessagingHostRegistrationCoordinator";
    public const string ReadinessTypeName = "Privon.App.NativeMessagingRegistrationReadiness";

    public sealed class CoordinatorHandle
    {
        public required object Instance { get; init; }
        public required MethodInfo InspectMethod { get; init; }
        public required MethodInfo ProvisionMethod { get; init; }
        public required MethodInfo RepairMethod { get; init; }
    }

    public static bool TryBuildCoordinator(
        INativeMessagingHostRegistrationEnvironment environment,
        Func<string?> hostExecutablePathProvider,
        out CoordinatorHandle? handle,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(hostExecutablePathProvider);
        handle = null;

        var coordinatorType = AppAssembly.GetType(CoordinatorTypeName);
        if (coordinatorType is null)
        {
            failureReason = $"{CoordinatorTypeName} does not exist yet -- Gate E5G.P3 requires the " +
                "production Native Messaging registration coordinator.";
            return false;
        }

        var readinessType = AppAssembly.GetType(ReadinessTypeName);
        if (readinessType is null)
        {
            failureReason = $"{ReadinessTypeName} does not exist yet -- Gate E5G.P3 requires the " +
                "coordinator's own readiness state enum.";
            return false;
        }
        if (!readinessType.IsEnum)
        {
            failureReason = $"{ReadinessTypeName} exists but is not an enum.";
            return false;
        }

        var actualNames = new HashSet<string>(Enum.GetNames(readinessType), StringComparer.Ordinal);
        foreach (string required in new[] { "Fresh", "Ready", "OwnedNeedsRepair", "ForeignBlocked", "OrphanBlocked", "Failed" })
        {
            if (!actualNames.Contains(required))
            {
                failureReason = $"{ReadinessTypeName} exists but has no '{required}' member.";
                return false;
            }
        }

        var ctor = coordinatorType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(INativeMessagingHostRegistrationEnvironment), typeof(Func<string?>)],
            modifiers: null);
        if (ctor is null)
        {
            failureReason = $"{CoordinatorTypeName} exists but has no constructor taking exactly " +
                "(INativeMessagingHostRegistrationEnvironment, Func<string?>) -- required so tests can " +
                "inject a synthetic host-executable-path provider without real Environment.ProcessPath.";
            return false;
        }

        var inspectMethod = coordinatorType.GetMethod("Inspect", BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: [typeof(AppBrowser), typeof(string)], modifiers: null);
        if (inspectMethod is null)
        {
            failureReason = $"{CoordinatorTypeName} exists but has no public Inspect(AppBrowser, string) method.";
            return false;
        }

        var provisionMethod = coordinatorType.GetMethod("Provision", BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: [typeof(AppBrowser), typeof(string)], modifiers: null);
        if (provisionMethod is null)
        {
            failureReason = $"{CoordinatorTypeName} exists but has no public Provision(AppBrowser, string) method.";
            return false;
        }

        var repairMethod = coordinatorType.GetMethod("Repair", BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: [typeof(AppBrowser), typeof(string)], modifiers: null);
        if (repairMethod is null)
        {
            failureReason = $"{CoordinatorTypeName} exists but has no public Repair(AppBrowser, string) method.";
            return false;
        }

        object instance = ctor.Invoke([environment, hostExecutablePathProvider]);

        handle = new CoordinatorHandle
        {
            Instance = instance,
            InspectMethod = inspectMethod,
            ProvisionMethod = provisionMethod,
            RepairMethod = repairMethod,
        };
        failureReason = "";
        return true;
    }

    public static CoordinatorReadiness Inspect(CoordinatorHandle handle, AppBrowser browser, string? extensionOrigin) =>
        ParseReadiness(handle.InspectMethod.Invoke(handle.Instance, [browser, extensionOrigin]));

    public static CoordinatorReadiness Provision(CoordinatorHandle handle, AppBrowser browser, string? extensionOrigin) =>
        ParseReadiness(handle.ProvisionMethod.Invoke(handle.Instance, [browser, extensionOrigin]));

    public static CoordinatorReadiness Repair(CoordinatorHandle handle, AppBrowser browser, string? extensionOrigin) =>
        ParseReadiness(handle.RepairMethod.Invoke(handle.Instance, [browser, extensionOrigin]));

    private static CoordinatorReadiness ParseReadiness(object? productionEnumValue)
    {
        if (productionEnumValue is null)
            throw new InvalidOperationException("Coordinator method returned null instead of a readiness value.");
        return Enum.Parse<CoordinatorReadiness>(productionEnumValue.ToString()!);
    }

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
