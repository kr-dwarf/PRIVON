using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3A.5 STEP4 -- Foreground Execution Guard Implementation regression. All tests use
// FakeClipboardMonitorNative + FakeClipboardTextNative + FakeForegroundTargetSource (synthetic,
// OS-free). No real Windows clipboard content or foreground state is ever touched.
public class ClipboardChangeMonitorForegroundGuardTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private const uint DefaultSequence = 5;

    // Matches FakeForegroundTargetSource's own defaults (PID 4242, "ChatGPT") -- a guarded call
    // using this target matches "out of the box" unless a test deliberately reconfigures the fake.
    private static readonly ForegroundTargetSnapshot DefaultExpectedTarget =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");

    private static (ClipboardChangeMonitor Monitor, FakeClipboardMonitorNative Native, FakeClipboardTextNative TextNative, FakeForegroundTargetSource ForegroundSource) CreateStarted()
    {
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        var foregroundSource = new FakeForegroundTargetSource();
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative, foregroundSource: foregroundSource);
        monitor.Start();
        return (monitor, native, textNative, foregroundSource);
    }

    // ==================================================================
    // READ
    // ==================================================================

    // ---- 1. unresolved expected target -> InvalidExpectedTarget, zero native calls ----
    [Fact]
    public async Task GuardedRead_UnresolvedExpectedTarget_ReturnsInvalidExpectedTarget_ZeroNativeCalls()
    {
        var (monitor, native, textNative, foregroundSource) = CreateStarted();
        var invalidTarget = new ForegroundTargetSnapshot(IsResolved: false, ProcessId: 4242, ProcessName: "ChatGPT");

        var result = await monitor.ReadTextSnapshotAsync(invalidTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.InvalidExpectedTarget, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.Empty(textNative.CallLog);
        Assert.Empty(foregroundSource.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardMonitorNative.PostReadWorkSignal), native.CallLog);
    }

    // ---- 2. expected PID == 0 -> InvalidExpectedTarget ----
    [Fact]
    public async Task GuardedRead_ExpectedProcessIdZero_ReturnsInvalidExpectedTarget()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        var invalidTarget = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 0, ProcessName: "ChatGPT");

        var result = await monitor.ReadTextSnapshotAsync(invalidTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.InvalidExpectedTarget, result.Outcome);
        Assert.Empty(textNative.CallLog);
    }

    // ---- 3. expected ProcessName null/empty/whitespace -> InvalidExpectedTarget ----
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GuardedRead_ExpectedProcessNameInvalid_ReturnsInvalidExpectedTarget(string? processName)
    {
        var (monitor, _, textNative, _) = CreateStarted();
        var invalidTarget = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: processName);

        var result = await monitor.ReadTextSnapshotAsync(invalidTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.InvalidExpectedTarget, result.Outcome);
        Assert.Empty(textNative.CallLog);
    }

    // ---- 4. target unavailable at CHECK 1 -> TargetUnavailable, no OpenClipboard ----
    [Fact]
    public async Task GuardedRead_TargetUnavailableAtCheck1_ReturnsTargetUnavailable_NoOpenClipboard()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ForegroundWindowResult = 0;

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetUnavailable, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
    }

    // ---- 5. PID differs at CHECK 1 -> TargetChanged, no OpenClipboard ----
    [Fact]
    public async Task GuardedRead_ProcessIdDiffersAtCheck1_ReturnsTargetChanged_NoOpenClipboard()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.WindowThreadProcessIdValue = 9999;

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
    }

    // ---- 6. same PID, different ProcessName -> TargetChanged (PID-reuse defense) ----
    [Fact]
    public async Task GuardedRead_SamePidDifferentProcessName_ReturnsTargetChanged_ProvesPidReuseDefense()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ProcessNameValue = "SomeOtherApp"; // same PID 4242, different name

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
    }

    // ---- 7. ProcessName casing differs only -> still matched (OrdinalIgnoreCase) ----
    [Fact]
    public async Task GuardedRead_ProcessNameCasingDiffers_StillMatches()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ProcessNameValue = "CHATGPT";
        textNative.SetUnicodeTextPayload("hello");

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
    }

    // ---- 8. CHECK 1 passes, CHECK 2 unavailable -> no raw content copy ----
    [Fact]
    public async Task GuardedRead_Check1Passes_Check2Unavailable_NoRawContentCopy()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        textNative.SetUnicodeTextPayload("must never be copied");
        foregroundSource.ChangeAfterAttempt = 1;
        foregroundSource.ForegroundWindowResultAfter = 0;

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetUnavailable, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGetUnicodeTextHandle), textNative.CallLog);
        Assert.Contains(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.Contains(nameof(FakeClipboardTextNative.CloseClipboard), textNative.CallLog);
    }

    // ---- 9. CHECK 1 passes, CHECK 2 changed -> no raw content copy ----
    [Fact]
    public async Task GuardedRead_Check1Passes_Check2Changed_NoRawContentCopy()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        textNative.SetUnicodeTextPayload("must never be copied");
        foregroundSource.ChangeAfterAttempt = 1;
        foregroundSource.ProcessNameValueAfter = "SomeOtherApp";

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGetUnicodeTextHandle), textNative.CallLog);
        Assert.Contains(nameof(FakeClipboardTextNative.CloseClipboard), textNative.CallLog);
    }

    // ---- 10. CHECK 1 + CHECK 2 both match -> existing Success behavior ----
    [Fact]
    public async Task GuardedRead_BothChecksMatch_ReturnsSuccessWithExactText()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello world");

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
        Assert.Equal("hello world", result.Snapshot!.Value.Text);
    }

    // ---- 11. guarded read preserves Busy behavior ----
    [Fact]
    public async Task GuardedRead_OpenClipboardFails_PreservesBusyBehavior()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        textNative.OpenClipboardResult = false;
        textNative.OpenClipboardError = 5;

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Busy, result.Outcome);
        Assert.Equal(5, result.Win32Error);
    }

    // ---- 12. guarded read preserves FormatUnavailable behavior ----
    [Fact]
    public async Task GuardedRead_FormatUnavailable_PreservesExistingBehavior()
    {
        var (monitor, native, _, _) = CreateStarted();
        native.UnicodeTextAvailable = false;

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.FormatUnavailable, result.Outcome);
    }

    // ---- 13. guarded read preserves MalformedData/NativeFailure behavior ----
    [Fact]
    public async Task GuardedRead_MalformedData_PreservesExistingBehavior()
    {
        var (monitor, _, textNative, _) = CreateStarted();
        textNative.SetRawPayload([0x41, 0x00, 0x42]); // odd length

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.MalformedData, result.Outcome);
    }

    // ---- 14. CloseClipboard failure still overrides a target failure once clipboard was opened ----
    [Fact]
    public async Task GuardedRead_Check2Fails_AndCloseClipboardFails_ReturnsNativeFailure()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ChangeAfterAttempt = 1;
        foregroundSource.ForegroundWindowResultAfter = 0;
        textNative.CloseClipboardResult = false;
        textNative.CloseClipboardError = 6;

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.NativeFailure, result.Outcome);
        Assert.Equal(6, result.Win32Error);
    }

    // ---- 15. guard failures expose no raw text via ToString/diagnostics ----
    [Fact]
    public async Task GuardedRead_TargetChangedFailure_ResultToString_DoesNotExposeAnyText()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        textNative.SetUnicodeTextPayload("RAW-PII-SENTINEL-GUARD-TEST");
        foregroundSource.ProcessNameValue = "SomeOtherApp";

        var result = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        string rendered = result.ToString();
        Assert.DoesNotContain("RAW-PII-SENTINEL-GUARD-TEST", rendered);
        Assert.Null(result.Snapshot);
    }

    // ==================================================================
    // WRITE
    // ==================================================================

    // ---- 16. invalid expected target -> no HGLOBAL allocation, no OpenClipboard ----
    [Fact]
    public async Task GuardedWrite_InvalidExpectedTarget_NoAllocationNoOpen()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        var invalidTarget = new ForegroundTargetSnapshot(IsResolved: false, ProcessId: 4242, ProcessName: "ChatGPT");

        var result = await monitor.WriteTextIfSequenceMatchesAsync(invalidTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.InvalidExpectedTarget, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Empty(textNative.CallLog);
        Assert.Empty(foregroundSource.CallLog);
    }

    // ---- 17. CHECK 1 unavailable -> no HGLOBAL allocation, no OpenClipboard ----
    [Fact]
    public async Task GuardedWrite_Check1Unavailable_NoAllocationNoOpen()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ForegroundWindowResult = 0;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.TargetUnavailable, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGlobalAlloc), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
    }

    // ---- 18. CHECK 1 changed -> no HGLOBAL allocation, no OpenClipboard ----
    [Fact]
    public async Task GuardedWrite_Check1Changed_NoAllocationNoOpen()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ProcessNameValue = "SomeOtherApp";

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.TargetChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGlobalAlloc), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
    }

    // ---- 19. same PID, different ProcessName -> TargetChanged (PID-reuse defense, write side) ----
    [Fact]
    public async Task GuardedWrite_SamePidDifferentProcessName_ReturnsTargetChanged()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ProcessNameValue = "SomeOtherApp";

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.TargetChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
    }

    // ---- 20. CHECK 1 matches, sequence mismatch -> EmptyClipboard never called, HGLOBAL freed ----
    [Fact]
    public async Task GuardedWrite_Check1Matches_SequenceMismatch_EmptyClipboardNeverCalled_HGlobalFreed()
    {
        var (monitor, native, textNative, _) = CreateStarted();
        native.SequenceNumber = DefaultSequence;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, expectedSequence: 999, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.SequenceChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog);
    }

    // ---- 21. sequence matches, CHECK 2 unavailable -> EmptyClipboard never called, HGLOBAL freed ----
    [Fact]
    public async Task GuardedWrite_SequenceMatches_Check2Unavailable_EmptyClipboardNeverCalled_HGlobalFreed()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ChangeAfterAttempt = 1; // CHECK1 (attempt 1) matches, CHECK2 (attempt 2) fails
        foregroundSource.ForegroundWindowResultAfter = 0;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.TargetUnavailable, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog);
    }

    // ---- 22. sequence matches, CHECK 2 changed -> same guarantees ----
    [Fact]
    public async Task GuardedWrite_SequenceMatches_Check2Changed_EmptyClipboardNeverCalled_HGlobalFreed()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ChangeAfterAttempt = 1;
        foregroundSource.ProcessNameValueAfter = "SomeOtherApp";

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.TargetChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog);
    }

    // ---- 23. target + sequence both match -> normal existing write flow unchanged ----
    [Fact]
    public async Task GuardedWrite_TargetAndSequenceMatch_NormalWriteFlowUnchanged()
    {
        var (monitor, _, textNative, _) = CreateStarted();

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        var readBack = await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(ClipboardReadOutcome.Success, readBack.Outcome);
        Assert.Equal("hello", readBack.Snapshot!.Value.Text);
    }

    // ---- 24. CHECK 2 failure never establishes the self-write marker ----
    [Fact]
    public async Task GuardedWrite_Check2Failure_NeverEstablishesSelfWriteMarker()
    {
        var (monitor, native, _, foregroundSource) = CreateStarted();
        foregroundSource.ChangeAfterAttempt = 1;
        foregroundSource.ForegroundWindowResultAfter = 0;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.TargetUnavailable, result.Outcome);

        // If a marker had incorrectly been established, this notification would be wrongly
        // suppressed. Reset the foreground fake to a non-interfering state (guard logic never
        // runs for plain notifications) and confirm the notification is delivered normally.
        foregroundSource.ChangeAfterAttempt = null;
        var received = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => received.Add(n);
        native.SequenceNumber = 777;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout); // FIFO barrier
        monitor.Stop();

        Assert.Single(received);
    }

    // ---- 25. Close failure after an opened pre-mutation bracket preserves existing precedence ----
    [Fact]
    public async Task GuardedWrite_SequenceMismatch_AndCloseFails_PreservesExistingPrecedence()
    {
        var (monitor, native, textNative, _) = CreateStarted();
        native.SequenceNumber = DefaultSequence;
        textNative.CloseClipboardResult = false;
        textNative.CloseClipboardError = 6;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, expectedSequence: 999, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Equal(6, result.Win32Error);
    }

    // ---- 26. GlobalFree failure during target-failure cleanup preserves GLOBALFREE_LEAK_VISIBILITY ----
    [Fact]
    public async Task GuardedWrite_Check2Failure_AndGlobalFreeFails_ReturnsNativeFailureWithFreeError()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ChangeAfterAttempt = 1;
        foregroundSource.ForegroundWindowResultAfter = 0;
        textNative.GlobalFreeResult = false;
        textNative.GlobalFreeError = 9;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Equal(9, result.Win32Error); // the FREE's own error, not a fabricated target-failure error
    }

    // ---- 27. never GlobalFree after successful SetClipboardData ----
    [Fact]
    public async Task GuardedWrite_SetSucceeds_NeverFreesTheSetHandle()
    {
        var (monitor, _, textNative, _) = CreateStarted();

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.NotNull(textNative.SetHandle);
        Assert.DoesNotContain(textNative.SetHandle!.Value, textNative.FreedHandleLog);
    }

    // ==================================================================
    // VERIFICATION
    // ==================================================================

    // ---- 28. verification never rechecks the foreground target -- exactly CHECK1 + CHECK2, no
    // third capture attempt for the read-back reopen. Locks VERIFICATION_TARGET_POLICY. ----
    [Fact]
    public async Task GuardedWrite_VerificationPhase_NeverRechecksForegroundTarget()
    {
        var (monitor, _, textNative, foregroundSource) = CreateStarted();

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultExpectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        // Exactly CHECK1 (pre-HGLOBAL-prep) + CHECK2 (pre-EmptyClipboard) -- verification's own
        // reopen/readback never calls CheckForegroundTarget a third time.
        Assert.Equal(2, foregroundSource.CaptureAttemptCount);
    }

    // ==================================================================
    // API STRUCTURAL TESTS
    // ==================================================================

    // ---- 29/31. every public Read/WriteTextIfSequenceMatchesAsync overload requires
    // ForegroundTargetSnapshot as its first parameter ----
    [Fact]
    public void EveryPublicReadOrWriteOverload_RequiresForegroundTargetSnapshotFirst()
    {
        var methods = typeof(ClipboardChangeMonitor)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name is nameof(ClipboardChangeMonitor.ReadTextSnapshotAsync)
                     or nameof(ClipboardChangeMonitor.WriteTextIfSequenceMatchesAsync))
            .ToList();

        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            Assert.NotEmpty(parameters);
            Assert.Equal(typeof(ForegroundTargetSnapshot), parameters[0].ParameterType);
        }
    }

    // ---- 30. the zero-argument ReadTextSnapshotAsync() still exists (for this assembly's own
    // mechanics tests) but is not public ----
    [Fact]
    public void ZeroArgumentReadTextSnapshotAsync_ExistsButIsNotPublic()
    {
        var method = typeof(ClipboardChangeMonitor).GetMethod(
            nameof(ClipboardChangeMonitor.ReadTextSnapshotAsync),
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        Assert.NotNull(method);
        Assert.False(method!.IsPublic);
        Assert.True(method.IsAssembly); // internal
    }

    // ---- 32. the targetless (uint, string) WriteTextIfSequenceMatchesAsync overload still
    // exists but is not public ----
    [Fact]
    public void TargetlessSequenceOnlyWrite_ExistsButIsNotPublic()
    {
        var method = typeof(ClipboardChangeMonitor).GetMethod(
            nameof(ClipboardChangeMonitor.WriteTextIfSequenceMatchesAsync),
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(uint), typeof(string)],
            modifiers: null);

        Assert.NotNull(method);
        Assert.False(method!.IsPublic);
        Assert.True(method.IsAssembly);
    }

    // ---- 33. no ChatGPT/product-policy naming anywhere in ClipboardChangeMonitor ----
    [Fact]
    public void ClipboardChangeMonitor_HasNoProductPolicyNaming()
    {
        var forbidden = new[] { "ChatGPT", "IsChatGPT", "IsSupportedAi", "IsEligible", "PrivacyGate" };
        var allMembers = typeof(ClipboardChangeMonitor)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(allMembers, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- 34. existing enum ordinals were not reordered -- new outcomes only appended ----
    [Fact]
    public void ClipboardReadOutcome_ExistingValuesPreserveOrdinals()
    {
        Assert.Equal(0, (int)ClipboardReadOutcome.NotRunning);
        Assert.Equal(1, (int)ClipboardReadOutcome.Busy);
        Assert.Equal(2, (int)ClipboardReadOutcome.FormatUnavailable);
        Assert.Equal(3, (int)ClipboardReadOutcome.NativeFailure);
        Assert.Equal(4, (int)ClipboardReadOutcome.MalformedData);
        Assert.Equal(5, (int)ClipboardReadOutcome.Success);
    }

    [Fact]
    public void ClipboardWriteOutcome_ExistingValuesPreserveOrdinals()
    {
        Assert.Equal(0, (int)ClipboardWriteOutcome.NotRunning);
        Assert.Equal(1, (int)ClipboardWriteOutcome.InvalidExpectedSequence);
        Assert.Equal(2, (int)ClipboardWriteOutcome.InvalidText);
        Assert.Equal(3, (int)ClipboardWriteOutcome.Busy);
        Assert.Equal(4, (int)ClipboardWriteOutcome.SequenceChanged);
        Assert.Equal(5, (int)ClipboardWriteOutcome.NativeFailure);
        Assert.Equal(6, (int)ClipboardWriteOutcome.VerificationUnavailable);
        Assert.Equal(7, (int)ClipboardWriteOutcome.Superseded);
        Assert.Equal(8, (int)ClipboardWriteOutcome.ReadBackMismatch);
        Assert.Equal(9, (int)ClipboardWriteOutcome.Success);
    }

    // ---- 36. raw diagnostic hardening (Phase 3A.4 STEP3.2) still holds for the new outcome
    // values -- no raw text leaks through ToString for a guard failure ----
    [Fact]
    public void GuardFailureResults_ToString_NeverExposeRawText()
    {
        var readFailure = ClipboardTextReadResult.Failure(ClipboardReadOutcome.TargetChanged);
        var writeFailure = ClipboardWriteResult.Failure(ClipboardWriteOutcome.TargetChanged, mutated: false);

        Assert.DoesNotContain("Text =", readFailure.ToString());
        Assert.Contains("TargetChanged", readFailure.ToString());
        Assert.Contains("TargetChanged", writeFailure.ToString());
    }
}
