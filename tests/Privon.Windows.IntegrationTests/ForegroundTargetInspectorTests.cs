using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3A.5 STEP2 -- Foreground Target Mechanical Primitive regression. All tests except the
// dedicated real-Win32 ones use FakeForegroundTargetSource (synthetic, OS-free). No clipboard
// content API is ever touched by this capability -- verified structurally below, mirroring the
// existing IClipboardTextNative_HasNoGlobalFreeMember-style checks used elsewhere in this project.
public class ForegroundTargetInspectorTests
{
    // ---- 1. hwnd == 0 -> unresolved ----
    [Fact]
    public void Capture_ForegroundWindowZero_ReturnsUnresolved()
    {
        var source = new FakeForegroundTargetSource { ForegroundWindowResult = 0 };
        var inspector = new ForegroundTargetInspector(source);

        var snapshot = inspector.Capture();

        Assert.False(snapshot.IsResolved);
        Assert.Equal(0u, snapshot.ProcessId);
        Assert.Null(snapshot.ProcessName);
        Assert.DoesNotContain(nameof(FakeForegroundTargetSource.TryGetWindowThreadProcessId), source.CallLog);
    }

    // ---- 2. GetWindowThreadProcessId failure -> unresolved ----
    [Fact]
    public void Capture_WindowThreadProcessIdFails_ReturnsUnresolved()
    {
        var source = new FakeForegroundTargetSource { WindowThreadProcessIdResult = false };
        var inspector = new ForegroundTargetInspector(source);

        var snapshot = inspector.Capture();

        Assert.False(snapshot.IsResolved);
        Assert.Equal(0u, snapshot.ProcessId);
        Assert.Null(snapshot.ProcessName);
        Assert.DoesNotContain(nameof(FakeForegroundTargetSource.TryGetProcessName), source.CallLog);
    }

    // ---- 3. pid == 0 -> unresolved, even if the source claims success (defensive: 0 is never
    // trusted as a valid process ID regardless of what the seam reports) ----
    [Fact]
    public void Capture_ProcessIdZero_ReturnsUnresolved_EvenIfSourceClaimsSuccess()
    {
        var source = new FakeForegroundTargetSource
        {
            WindowThreadProcessIdResult = true,
            WindowThreadProcessIdValue = 0,
        };
        var inspector = new ForegroundTargetInspector(source);

        var snapshot = inspector.Capture();

        Assert.False(snapshot.IsResolved);
        Assert.Equal(0u, snapshot.ProcessId);
        Assert.Null(snapshot.ProcessName);
        Assert.DoesNotContain(nameof(FakeForegroundTargetSource.TryGetProcessName), source.CallLog);
    }

    // ---- 4/5/6. valid hwnd/pid/process -> resolved, PID and ProcessName preserved exactly ----
    [Fact]
    public void Capture_AllStepsSucceed_ReturnsResolvedSnapshot_WithExactValues()
    {
        var source = new FakeForegroundTargetSource
        {
            ForegroundWindowResult = 777,
            WindowThreadProcessIdResult = true,
            WindowThreadProcessIdValue = 4242,
            ProcessNameResult = true,
            ProcessNameValue = "ChatGPT",
        };
        var inspector = new ForegroundTargetInspector(source);

        var snapshot = inspector.Capture();

        Assert.True(snapshot.IsResolved);
        Assert.Equal(4242u, snapshot.ProcessId);
        Assert.Equal("ChatGPT", snapshot.ProcessName);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(999999u)]
    public void Capture_ReturnedProcessId_MatchesSourceExactly(uint processId)
    {
        var source = new FakeForegroundTargetSource { WindowThreadProcessIdValue = processId };
        var inspector = new ForegroundTargetInspector(source);

        var snapshot = inspector.Capture();

        Assert.Equal(processId, snapshot.ProcessId);
    }

    [Theory]
    [InlineData("ChatGPT")]
    [InlineData("notepad")]
    [InlineData("SomeOtherApp")]
    public void Capture_ReturnedProcessName_MatchesSourceExactly(string processName)
    {
        var source = new FakeForegroundTargetSource { ProcessNameValue = processName };
        var inspector = new ForegroundTargetInspector(source);

        var snapshot = inspector.Capture();

        Assert.Equal(processName, snapshot.ProcessName);
    }

    // ---- 7. process-exited race (name lookup fails after PID was already resolved) ->
    // unresolved, no exception ----
    [Fact]
    public void Capture_ProcessExitedBeforeNameLookup_ReturnsUnresolved_DoesNotThrow()
    {
        var source = new FakeForegroundTargetSource
        {
            WindowThreadProcessIdResult = true,
            WindowThreadProcessIdValue = 555,
            ProcessNameResult = false,
        };
        var inspector = new ForegroundTargetInspector(source);

        var snapshot = inspector.Capture();

        Assert.False(snapshot.IsResolved);
        Assert.Equal(0u, snapshot.ProcessId);
        Assert.Null(snapshot.ProcessName);
    }

    // ---- 8. process-name lookup failure against the REAL Win32 implementation -- a PID that
    // (almost certainly) does not correspond to any running process must resolve to false, never
    // throw. This exercises Win32ForegroundTargetSource's actual Process.GetProcessById exception
    // handling directly, not the fake. ----
    [Fact]
    public void Win32Source_TryGetProcessName_NonexistentPid_ReturnsFalse_DoesNotThrow()
    {
        var source = new Win32ForegroundTargetSource();

        // Int32.MaxValue is not a valid PID on any real Windows system (PIDs are allocated in a
        // much smaller range) -- this deterministically exercises the "no such process" path
        // without depending on any specific process having exited during the test run.
        bool result = source.TryGetProcessName(int.MaxValue, out string? processName);

        Assert.False(result);
        Assert.Null(processName);
    }

    // ---- 9. no stale prior successful identity reused after failure ----
    [Fact]
    public void Capture_AfterPriorSuccess_ThenFailure_DoesNotReturnStalePriorIdentity()
    {
        var source = new FakeForegroundTargetSource
        {
            WindowThreadProcessIdValue = 111,
            ProcessNameValue = "ChatGPT",
        };
        var inspector = new ForegroundTargetInspector(source);

        var first = inspector.Capture();
        Assert.True(first.IsResolved);
        Assert.Equal(111u, first.ProcessId);
        Assert.Equal("ChatGPT", first.ProcessName);

        source.ForegroundWindowResult = 0; // now nothing is foreground
        var second = inspector.Capture();

        Assert.False(second.IsResolved);
        Assert.Equal(0u, second.ProcessId);
        Assert.Null(second.ProcessName);
    }

    // ---- 10. multiple Capture calls reflect current source state, not cached state ----
    [Fact]
    public void Capture_MultipleCalls_EachReflectsCurrentSourceState_NotCached()
    {
        var source = new FakeForegroundTargetSource
        {
            WindowThreadProcessIdValue = 100,
            ProcessNameValue = "AppOne",
        };
        var inspector = new ForegroundTargetInspector(source);

        var first = inspector.Capture();
        Assert.Equal(100u, first.ProcessId);
        Assert.Equal("AppOne", first.ProcessName);

        source.WindowThreadProcessIdValue = 200;
        source.ProcessNameValue = "AppTwo";
        var second = inspector.Capture();

        Assert.Equal(200u, second.ProcessId);
        Assert.Equal("AppTwo", second.ProcessName);
        Assert.NotEqual(first.ProcessId, second.ProcessId);
        Assert.NotEqual(first.ProcessName, second.ProcessName);
    }

    // ---- 11. structural: no clipboard content API anywhere in this capability's public/internal
    // surface (ForegroundTargetSnapshot / IForegroundTargetSource / ForegroundTargetInspector /
    // Win32ForegroundTargetSource) ----
    [Theory]
    [InlineData(typeof(ForegroundTargetSnapshot))]
    [InlineData(typeof(ForegroundTargetInspector))]
    [InlineData(typeof(IForegroundTargetSource))]
    [InlineData(typeof(Win32ForegroundTargetSource))]
    public void ForegroundTargetTypes_HaveNoClipboardContentMembers(Type type)
    {
        var forbidden = new[] { "Clipboard", "OpenClipboard", "GetClipboardData", "SetClipboardData", "EmptyClipboard" };
        var members = type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- Forbidden product-policy naming (Windows returns facts only -- CHATGPT PROCESS NAME
    // POLICY explicitly forbids these names anywhere in Privon.Windows) ----
    [Theory]
    [InlineData(typeof(ForegroundTargetSnapshot))]
    [InlineData(typeof(ForegroundTargetInspector))]
    [InlineData(typeof(IForegroundTargetSource))]
    [InlineData(typeof(Win32ForegroundTargetSource))]
    public void ForegroundTargetTypes_HaveNoProductPolicyMembers(Type type)
    {
        var forbidden = new[] { "IsChatGPT", "IsSupportedAi", "IsEligible", "Protect", "PrivacyGate" };
        var members = type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Constructor_NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ForegroundTargetInspector(null!));
    }

    // ---- 9 (real Windows structural smoke, section 9 of the STEP2 instruction): actual
    // GetForegroundWindow + GetWindowThreadProcessId + process-name lookup against whatever is
    // currently foreground in this environment. Asserts only coherent mechanical behavior --
    // NEVER asserts a specific application is foreground (this sandbox cannot be assumed to have
    // any particular app running). No clipboard content is touched. ----
    [Fact]
    public void RealWindows_Capture_ReturnsCoherentSnapshot_RegardlessOfWhatIsForeground()
    {
        var inspector = new ForegroundTargetInspector();

        var snapshot = inspector.Capture();

        if (snapshot.IsResolved)
        {
            Assert.NotEqual(0u, snapshot.ProcessId);
            Assert.False(string.IsNullOrEmpty(snapshot.ProcessName));
        }
        else
        {
            Assert.Equal(0u, snapshot.ProcessId);
            Assert.Null(snapshot.ProcessName);
        }
    }
}
