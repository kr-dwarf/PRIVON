using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.1 Gate 031F4 -- R4 (CHECK1_CHECK2_WEB_FRESHNESS), now GREEN against real production.
// Gate 031D2 originally carried this file's frozen four-scenario contract as reflection-based
// absence proofs, since ClipboardChangeMonitor's guarded entry points took no freshness-verifier
// parameter at all. Gate 031F4 added the opaque, synchronous IClipboardAuthorizationFreshness seam
// (Privon.Windows/IClipboardAuthorizationFreshness.cs) at all four guard sites -- ExecuteRead
// (CHECK1), ReadWhileClipboardOpen (CHECK2), ExecuteWriteCore (CHECK1), MutateWhileClipboardOpen
// (CHECK2) -- so these scenarios are now exercised as real behavioral tests using
// ClipboardChangeMonitorForegroundGuardTests.CreateStarted's own FakeForegroundTargetSource-based
// pattern, driven by the TEST-ONLY ScriptedClipboardAuthorizationFreshness double.
public class Gate031D2_WebFreshnessSeamTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private const uint DefaultSequence = 5;

    // Matches FakeForegroundTargetSource's own defaults (PID 4242, "ChatGPT") -- see
    // ClipboardChangeMonitorForegroundGuardTests.DefaultExpectedTarget.
    private static readonly ForegroundTargetSnapshot DefaultExpectedTarget =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");

    private static (ClipboardChangeMonitor Monitor, FakeClipboardMonitorNative Native, FakeClipboardTextNative TextNative, FakeForegroundTargetSource ForegroundSource) CreateStarted()
    {
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        textNative.SetUnicodeTextPayload("ORIGINAL-CLIPBOARD-TEXT");
        var foregroundSource = new FakeForegroundTargetSource();
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative, foregroundSource: foregroundSource);
        monitor.Start();
        return (monitor, native, textNative, foregroundSource);
    }

    // ==================================================================
    // R4-A: READ CHECK1
    // ==================================================================

    [Fact]
    public async Task R4_A_ReadCheck1_FreshnessFalse_ReturnsTargetChanged_NeverOpensClipboard_CalledOnce()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        var freshness = new ScriptedClipboardAuthorizationFreshness(false);

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget, freshness).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.Equal(1, freshness.CallCount);
    }

    [Fact]
    public async Task R4_ReadCheck1_FreshnessThrows_ReturnsTargetChanged_NeverOpensClipboard()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        var freshness = ScriptedClipboardAuthorizationFreshness.Throwing(new InvalidOperationException("synthetic freshness failure"));

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget, freshness).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.Equal(1, freshness.CallCount);
    }

    // ==================================================================
    // R4-B: READ CHECK2
    // ==================================================================

    [Fact]
    public async Task R4_B_ReadCheck2_FreshnessTrueThenFalse_OpensClipboard_NeverReadsBody_ClosesNormally_CalledTwice()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        textNative.SetUnicodeTextPayload("must never be copied");
        var freshness = new ScriptedClipboardAuthorizationFreshness(true, false);

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget, freshness).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.Contains(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGetUnicodeTextHandle), textNative.CallLog);
        Assert.Contains(nameof(FakeClipboardTextNative.CloseClipboard), textNative.CallLog);
        Assert.Equal(2, freshness.CallCount);
    }

    [Fact]
    public async Task R4_ReadCheck2_FreshnessThrowsAfterCheck1True_ReturnsTargetChanged_NeverReadsBody()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        textNative.SetUnicodeTextPayload("must never be copied");
        var freshness = ScriptedClipboardAuthorizationFreshness.ThenThrowing(
            new InvalidOperationException("synthetic freshness failure"), resultsBeforeThrow: true);

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget, freshness).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
        Assert.Contains(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGetUnicodeTextHandle), textNative.CallLog);
        Assert.Contains(nameof(FakeClipboardTextNative.CloseClipboard), textNative.CallLog);
        Assert.Equal(2, freshness.CallCount);
    }

    // ==================================================================
    // R4-C: WRITE CHECK1
    // ==================================================================

    [Fact]
    public async Task R4_C_WriteCheck1_FreshnessFalse_ReturnsTargetChanged_NeverOpensOrAllocatesOrMutates_CalledOnce()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        var freshness = new ScriptedClipboardAuthorizationFreshness(false);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "replacement", freshness).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.TargetChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGlobalAlloc), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TrySetClipboardData), textNative.CallLog);
        Assert.Equal(1, freshness.CallCount);
    }

    [Fact]
    public async Task R4_WriteCheck1_FreshnessThrows_ReturnsTargetChanged_NeverOpensClipboard()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        var freshness = ScriptedClipboardAuthorizationFreshness.Throwing(new InvalidOperationException("synthetic freshness failure"));

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "replacement", freshness).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.TargetChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.Equal(1, freshness.CallCount);
    }

    // ==================================================================
    // R4-D: WRITE CHECK2
    // ==================================================================

    [Fact]
    public async Task R4_D_WriteCheck2_FreshnessTrueThenFalse_SequenceGuardPasses_NeverMutates_ClosesNormally_CalledTwice()
    {
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        var (monitor, native, textNative, _) = CreateStarted();
        native.SequenceNumber = DefaultSequence;
        var freshness = new ScriptedClipboardAuthorizationFreshness(true, false);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "replacement", freshness).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.TargetChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Contains(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TrySetClipboardData), textNative.CallLog);
        Assert.Contains(nameof(FakeClipboardTextNative.CloseClipboard), textNative.CallLog);
        Assert.Equal(2, freshness.CallCount);

        // BUG-006 global rollback slot invariant: an attempt that never reached rollback-slot
        // admission must leave the process-wide slot exactly as it found it.
        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    // ==================================================================
    // NULL COMPATIBILITY (Gate 031F4 section 13/16) -- real behavioral proof, not reflection-only
    // ==================================================================

    [Fact]
    public async Task R4_NullFreshness_RealReadSucceeds_IdenticalToPre031F4Behavior()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello world");

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
        Assert.Equal("hello world", result.Snapshot!.Value.Text);
    }

    [Fact]
    public async Task R4_NullFreshness_RealWriteSucceeds_IdenticalToPre031F4Behavior()
    {
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        var (monitor, _, textNative, _) = CreateStarted();

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "new text").WaitAsync(WaitTimeout);
        var readBack = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(ClipboardReadOutcome.Success, readBack.Outcome);
        Assert.Equal("new text", readBack.Snapshot!.Value.Text);
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    [Fact]
    public void R4_GuardedEntryPoints_FreshnessParameterIsOptionalAndDefaultsToNull()
    {
        var readMethod = typeof(ClipboardChangeMonitor).GetMethod(
            nameof(ClipboardChangeMonitor.ReadTextSnapshotAsync),
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(ForegroundTargetSnapshot), typeof(IClipboardAuthorizationFreshness)]);
        Assert.NotNull(readMethod);
        var readParam = readMethod!.GetParameters()[1];
        Assert.True(readParam.IsOptional);
        Assert.Null(readParam.DefaultValue);

        var writeMethod = typeof(ClipboardChangeMonitor).GetMethod(
            nameof(ClipboardChangeMonitor.WriteTextIfSequenceMatchesAsync),
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(ForegroundTargetSnapshot), typeof(uint), typeof(string), typeof(IClipboardAuthorizationFreshness)]);
        Assert.NotNull(writeMethod);
        var writeParam = writeMethod!.GetParameters()[3];
        Assert.True(writeParam.IsOptional);
        Assert.Null(writeParam.DefaultValue);
    }

    // ==================================================================
    // B2 boundary -- ComposerTextReader must never receive this hook
    // ==================================================================

    [Fact]
    public void R4_ComposerTextReader_MustReceiveNoFreshnessHook_ByDesign()
    {
        // Gate 031F4 section 12 -- ComposerTextReader is out of scope for this seam (deferred B2
        // boundary). Forward regression lock: its own CheckForegroundTarget sites must stay
        // exactly as they are today, with no freshness-shaped parameter ever added.
        var method = typeof(ComposerTextReader).GetMethod(nameof(ComposerTextReader.ReadFocusedComposerTextAsync));

        Assert.NotNull(method);
        Assert.Single(method!.GetParameters());
        Assert.Equal(typeof(ForegroundTargetSnapshot), method.GetParameters()[0].ParameterType);
    }
}
