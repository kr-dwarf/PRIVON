using System.Reflection;
using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3B STEP2 -- APP_TARGET_GATE regression. TargetGate is a pure function -- no fakes needed.
//
// BUG-004 Gate 2G: the two positive tests below (and this file's own SupportedPfn/SupportedChatGptSnapshot
// helpers) were migrated from the OLD, defective, name-only supported contract to the current one --
// see TargetGate's own SUPPORTED_IDENTITY_0_2_1 doc. Every OTHER test in this file is a NEGATIVE
// case (unrelated process, browser, unresolved, null/empty name) whose whole point is that the
// target must NOT be authorized -- none of those needed migration, since strengthening the policy
// can only ever keep a negative case negative, never flip it positive.
public class TargetGateTests
{
    // The ONE current approved product identity (Gate 2E/2E.1, locally + Store-catalog verified) --
    // duplicated here deliberately, as its own named constant, rather than reflecting into
    // TargetGate's private field: this test file should fail loudly if the production constant
    // ever drifts from what this suite believes is approved, not silently track it.
    private const string SupportedPfn = "OpenAI.Codex_2p2nqsd0c76g0";

    private static ForegroundTargetSnapshot SupportedChatGptSnapshot(string processName = "ChatGPT", uint processId = 4242) =>
        new(IsResolved: true, ProcessId: processId, ProcessName: processName,
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: SupportedPfn);

    // ---- 1. resolved "ChatGPT" + the approved current package identity -> true ----
    [Fact]
    public void IsSupportedTarget_ResolvedChatGptWithApprovedPackageIdentity_ReturnsTrue()
    {
        var snapshot = SupportedChatGptSnapshot();
        Assert.True(TargetGate.IsSupportedTarget(snapshot));
    }

    // ---- 2/3. casing differences -> still true (OrdinalIgnoreCase), approved package identity present ----
    [Theory]
    [InlineData("chatgpt")]
    [InlineData("CHATGPT")]
    [InlineData("ChAtGpT")]
    public void IsSupportedTarget_CasingVariantsWithApprovedPackageIdentity_ReturnsTrue(string processName)
    {
        var snapshot = SupportedChatGptSnapshot(processName);
        Assert.True(TargetGate.IsSupportedTarget(snapshot));
    }

    // ---- 4. unrelated process -> false ----
    [Fact]
    public void IsSupportedTarget_UnrelatedProcess_ReturnsFalse()
    {
        var snapshot = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "notepad");
        Assert.False(TargetGate.IsSupportedTarget(snapshot));
    }

    // ---- 4b. Phase 0.2H NON_AI_INTERFERENCE_GATE -- explicit browser process names, literally
    // covering the "ChatGPT Web in a browser must never be authorized as ChatGPT Desktop"
    // requirement. IsSupportedTarget only ever compares snapshot.ProcessName (the OS-reported
    // executable short name -- see Win32ForegroundTargetSource, which never reads a window title
    // or URL) against the literal "ChatGPT" -- a browser's ProcessName is "chrome"/"msedge"/
    // "firefox" regardless of which tab/URL (including chatgpt.com) is open, so this is a
    // structural impossibility, not merely an untested one. Explorer/a terminal are included for
    // the same completeness the 0.2H instruction's misidentification audit asked for.
    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData("explorer")]
    [InlineData("WindowsTerminal")]
    [InlineData("cmd")]
    [InlineData("powershell")]
    public void IsSupportedTarget_NonAiApplications_ReturnsFalse(string processName)
    {
        var snapshot = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: processName);
        Assert.False(TargetGate.IsSupportedTarget(snapshot));
    }

    // ---- 5. unresolved -> false, even with a matching name ----
    [Fact]
    public void IsSupportedTarget_Unresolved_ReturnsFalse()
    {
        var snapshot = new ForegroundTargetSnapshot(IsResolved: false, ProcessId: 4242, ProcessName: "ChatGPT");
        Assert.False(TargetGate.IsSupportedTarget(snapshot));
    }

    // ---- 6. null/empty process name -> false ----
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsSupportedTarget_NullOrEmptyProcessName_ReturnsFalse(string? processName)
    {
        var snapshot = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: processName);
        Assert.False(TargetGate.IsSupportedTarget(snapshot));
    }

    // ---- 7. no target-policy behavior/naming added to Privon.Windows ----
    [Fact]
    public void PrivonWindowsAssembly_HasNoChatGptOrTargetGateNaming()
    {
        var windowsAssembly = typeof(ForegroundTargetSnapshot).Assembly;
        var forbidden = new[] { "ChatGPT", "TargetGate", "IsSupportedTarget" };

        var offendingTypeNames = windowsAssembly.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offendingTypeNames);
    }

    // ==================================================================
    // UI-019 (PRIVON v0.2.1 Gate 3C) -- the Settings window's own real foreground process/package
    // identity can never satisfy TargetGate. Not a new branch -- a regression lock proving the
    // EXISTING exact-package-identity contract (already exhaustive: ONLY the one approved ChatGPT
    // package identity passes -- see SUPPORTED_IDENTITY_0_2_1) structurally covers this new window
    // too, without TargetGate itself needing (or getting) any Settings-specific change.
    // ==================================================================
    [Theory]
    [InlineData("PRIVON")]
    [InlineData("Privon.App")]
    [InlineData("Settings")]
    public void IsSupportedTarget_SettingsOrAppOwnProcessIdentity_NeverSatisfiesTargetGate(string processName)
    {
        // Even granting the maximally-favorable (and factually wrong) assumption that this
        // process's own package identity happened to equal the ONE approved ChatGPT PFN, a
        // non-"ChatGPT" process name alone already fails -- proving the process-name pre-filter
        // alone is sufficient to reject PRIVON's own windows, with no dependency on package identity
        // at all.
        var snapshot = new ForegroundTargetSnapshot(
            IsResolved: true, ProcessId: 9999, ProcessName: processName,
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: SupportedPfn);

        Assert.False(TargetGate.IsSupportedTarget(snapshot));
    }

    [Fact]
    public void TargetGateSource_NeverReferencesSettingsOrPrivonAsASupportedIdentity()
    {
        var source = File.ReadAllText(FindAppSourceFile("TargetGate.cs"));
        Assert.DoesNotContain("\"PRIVON\"", source);
        Assert.DoesNotContain("\"Settings\"", source);
        Assert.DoesNotContain("SettingsWindow", source);
    }

    private static string FindAppSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate src/Privon.App from the test output directory.");

        return Path.Combine(dir.FullName, "src", "Privon.App", fileName);
    }
}
