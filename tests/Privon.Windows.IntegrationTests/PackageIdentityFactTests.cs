using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// BUG-004 Gate 2F -- FACT-level regression for the widened, mechanical, product-policy-free
// package-identity fact model (PackageIdentityResolution / ForegroundTargetSnapshot.PackageIdentity
// / ForegroundTargetSnapshot.PackageFamilyName / IForegroundTargetSource.TryGetProcessIdentity /
// Win32ForegroundTargetSource's own implementation). This file proves the FACTS can be represented
// and resolved correctly -- it asserts NOTHING about which identity is "supported" (that remains
// TargetGate's own, deliberately UNCHANGED-in-this-gate, concern in Privon.App).
public class PackageIdentityFactTests
{
    // ---- FACT-001: a packaged process identity fact can be represented in the snapshot, and
    // flows through ForegroundTargetInspector.Capture() unchanged from what the source reports ----
    [Fact]
    public void Fact001_PackagedIdentity_IsRepresentedInSnapshot()
    {
        var source = new FakeForegroundTargetSource
        {
            ProcessNameValue = "ChatGPT",
            PackageIdentityValue = PackageIdentityResolution.Resolved,
            PackageFamilyNameValue = "OpenAI.Codex_2p2nqsd0c76g0",
        };
        var inspector = new ForegroundTargetInspector(source);

        var snapshot = inspector.Capture();

        Assert.True(snapshot.IsResolved);
        Assert.Equal(PackageIdentityResolution.Resolved, snapshot.PackageIdentity);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", snapshot.PackageFamilyName);
    }

    // ---- FACT-002: "definitively no package" is representable and distinct from "unresolved" ----
    [Fact]
    public void Fact002_NoPackageState_IsDistinctFromUnresolved()
    {
        var source = new FakeForegroundTargetSource
        {
            ProcessNameValue = "notepad",
            PackageIdentityValue = PackageIdentityResolution.NoPackage,
            PackageFamilyNameValue = null,
        };
        var inspector = new ForegroundTargetInspector(source);

        var snapshot = inspector.Capture();

        Assert.Equal(PackageIdentityResolution.NoPackage, snapshot.PackageIdentity);
        Assert.Null(snapshot.PackageFamilyName);
        Assert.NotEqual(PackageIdentityResolution.Unresolved, snapshot.PackageIdentity);
    }

    // ---- FACT-003: identity-inspection failure/inconclusive is representable and distinct from
    // both NoPackage and Resolved ----
    [Fact]
    public void Fact003_UnresolvedIdentityInspection_IsDistinctFromNoPackageAndResolved()
    {
        var source = new FakeForegroundTargetSource
        {
            ProcessNameValue = "ChatGPT",
            PackageIdentityValue = PackageIdentityResolution.Unresolved,
            PackageFamilyNameValue = null,
        };
        var inspector = new ForegroundTargetInspector(source);

        var snapshot = inspector.Capture();

        Assert.Equal(PackageIdentityResolution.Unresolved, snapshot.PackageIdentity);
        Assert.Null(snapshot.PackageFamilyName);
    }

    // ---- FACT-003b: real Win32 smoke -- a PID that cannot correspond to any running process must
    // resolve to a clean `false`, never throw. Post-Gate-2H.3 this exercises the real OpenProcess
    // failure path. ----
    [Fact]
    public void Fact003b_Win32Source_ConfirmedIdentity_NonexistentPid_ReturnsFalse_DoesNotThrow()
    {
        var source = new Win32ForegroundTargetSource();

        bool result = source.TryResolveConfirmedForegroundIdentity(
            int.MaxValue, out string? processName, out var packageIdentity, out string? packageFamilyName);

        Assert.False(result);
        Assert.Null(processName);
        Assert.Equal(PackageIdentityResolution.Unresolved, packageIdentity);
        Assert.Null(packageFamilyName);
    }

    // ---- FACT-003c (BUG-004 Gate 2H.3, rewritten): the REAL-Windows analog of RED-ID-009. This
    // test host process genuinely exists and its handle can genuinely be opened -- but it is not the
    // foreground window's process. Under the corrected E+ contract the FOREGROUND CONFIRMATION must
    // therefore reject it, and no identity facts may be handed back.
    //
    // Before Gate 2H.3 the equivalent call SUCCEEDED for this same non-foreground process, which is
    // precisely the hole E+ closes: identity facts could be resolved for a process that was not the
    // current foreground target. Deliberately asserts nothing about which application is foreground.
    [Fact]
    public void Fact003c_Win32Source_ConfirmedIdentity_NonForegroundProcess_IsRejected()
    {
        var source = new Win32ForegroundTargetSource();
        uint currentPid = (uint)Environment.ProcessId;

        // Guard: if this test host somehow IS the foreground process, the premise does not hold and
        // there is nothing meaningful to assert -- never assert a specific app is foreground.
        nint foregroundHwnd = source.GetForegroundWindow();
        bool hostIsForeground = foregroundHwnd != 0
            && source.TryGetWindowThreadProcessId(foregroundHwnd, out uint fgPid)
            && fgPid == currentPid;
        if (hostIsForeground)
            return;

        bool result = source.TryResolveConfirmedForegroundIdentity(
            currentPid, out string? processName, out var packageIdentity, out string? packageFamilyName);

        Assert.False(result);
        Assert.Null(processName);
        Assert.Equal(PackageIdentityResolution.Unresolved, packageIdentity);
        Assert.Null(packageFamilyName);
    }

    // ---- FACT-004: the fact model structurally cannot express a package VERSION/PackageFullName/
    // install-path pin -- proven at the TYPE level, not merely "we chose not to use it." ----
    [Theory]
    [InlineData(typeof(ForegroundTargetSnapshot))]
    [InlineData(typeof(IForegroundTargetSource))]
    [InlineData(typeof(Win32ForegroundTargetSource))]
    [InlineData(typeof(PackageIdentityResolution))]
    public void Fact004_IdentityFactTypes_HaveNoVersionOrPathBearingMember(Type type)
    {
        var forbidden = new[] { "Version", "PackageFullName", "FullName", "InstallLocation", "Path" };
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- FACT-005: Privon.Windows still contains no ChatGPT/product-policy naming, now covering
    // the new package-identity types too. This is largely already guaranteed by the existing,
    // unmodified, assembly-wide TargetGateTests.PrivonWindowsAssembly_HasNoChatGptOrTargetGateNaming
    // scan (which automatically covers every type in the assembly, including these new ones) --
    // this test exists for direct, local traceability of that same invariant against the specific
    // new types this gate introduces. ----
    [Theory]
    [InlineData(typeof(PackageIdentityResolution))]
    [InlineData(typeof(ForegroundTargetSnapshot))]
    [InlineData(typeof(IForegroundTargetSource))]
    [InlineData(typeof(Win32ForegroundTargetSource))]
    public void Fact005_PackageIdentityTypes_HaveNoProductPolicyOrChatGptNaming(Type type)
    {
        var forbidden = new[] { "ChatGPT", "TargetGate", "IsSupportedTarget", "IsChatGPT", "IsSupportedAi", "IsEligible", "Codex", "Classic" };
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- structural: the new native P/Invoke surface declares exactly the one method this
    // capability needs, and nothing resembling a window-text/URL/title/clipboard API -- mirroring
    // Win32Source_NativeMethods_DeclaresOnlyForegroundWindowAndThreadProcessId's own discipline,
    // applied to the NEW, separate nested class this gate introduces. ----
    [Fact]
    public void Win32Source_PackageNativeMethods_DeclaresOnlyGetPackageFamilyName()
    {
        var nativeMethodsType = typeof(Win32ForegroundTargetSource).GetNestedType("PackageNativeMethods", BindingFlags.NonPublic);
        Assert.NotNull(nativeMethodsType);

        var declaredMethodNames = nativeMethodsType!
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "GetPackageFamilyName" }, declaredMethodNames);

        var forbidden = new[] { "WindowText", "GetWindowText", "Caption", "Title", "Url", "InternetGetConnectedState", "Clipboard" };
        Assert.DoesNotContain(declaredMethodNames, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- no stale prior package identity reused after a later capture reports none ----
    [Fact]
    public void Capture_AfterPriorPackagedResult_ThenNoPackage_DoesNotReturnStalePriorIdentity()
    {
        var source = new FakeForegroundTargetSource
        {
            ProcessNameValue = "ChatGPT",
            PackageIdentityValue = PackageIdentityResolution.Resolved,
            PackageFamilyNameValue = "OpenAI.Codex_2p2nqsd0c76g0",
        };
        var inspector = new ForegroundTargetInspector(source);

        var first = inspector.Capture();
        Assert.Equal(PackageIdentityResolution.Resolved, first.PackageIdentity);

        source.PackageIdentityValue = PackageIdentityResolution.NoPackage;
        source.PackageFamilyNameValue = null;
        var second = inspector.Capture();

        Assert.Equal(PackageIdentityResolution.NoPackage, second.PackageIdentity);
        Assert.Null(second.PackageFamilyName);
    }

    // ---- default-initialized snapshot (e.g. an unresolved capture) has Unresolved package
    // identity and no PFN, never a false NoPackage/Resolved claim ----
    [Fact]
    public void DefaultSnapshot_HasUnresolvedPackageIdentity_NotNoPackageOrResolved()
    {
        var snapshot = default(ForegroundTargetSnapshot);

        Assert.Equal(PackageIdentityResolution.Unresolved, snapshot.PackageIdentity);
        Assert.Null(snapshot.PackageFamilyName);
    }
}
