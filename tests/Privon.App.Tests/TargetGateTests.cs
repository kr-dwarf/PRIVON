using System.Reflection;
using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3B STEP2 -- APP_TARGET_GATE regression. TargetGate is a pure function -- no fakes needed.
public class TargetGateTests
{
    // ---- 1. resolved "ChatGPT" -> true ----
    [Fact]
    public void IsSupportedTarget_ResolvedChatGpt_ReturnsTrue()
    {
        var snapshot = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");
        Assert.True(TargetGate.IsSupportedTarget(snapshot));
    }

    // ---- 2/3. casing differences -> still true (OrdinalIgnoreCase) ----
    [Theory]
    [InlineData("chatgpt")]
    [InlineData("CHATGPT")]
    [InlineData("ChAtGpT")]
    public void IsSupportedTarget_CasingVariants_ReturnsTrue(string processName)
    {
        var snapshot = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: processName);
        Assert.True(TargetGate.IsSupportedTarget(snapshot));
    }

    // ---- 4. unrelated process -> false ----
    [Fact]
    public void IsSupportedTarget_UnrelatedProcess_ReturnsFalse()
    {
        var snapshot = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "notepad");
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
}
