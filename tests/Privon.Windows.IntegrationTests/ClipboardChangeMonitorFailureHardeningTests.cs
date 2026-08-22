using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3A.4 STEP3.1 -- Clipboard Read Failure-Path Hardening regression: PostMessageW failure,
// GlobalUnlock's ambiguous return-value contract, and CloseClipboard failure precedence. All
// tests use FakeClipboardMonitorNative + FakeClipboardTextNative (synthetic, OS-free) -- no real
// Windows clipboard content is read here, matching STEP3's WINDOWS_SMOKE_POLICY.
public class ClipboardChangeMonitorFailureHardeningTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static (ClipboardChangeMonitor Monitor, FakeClipboardMonitorNative Native, FakeClipboardTextNative TextNative) CreateStarted()
    {
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true };
        var textNative = new FakeClipboardTextNative();
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative);
        monitor.Start();
        return (monitor, native, textNative);
    }

    // ---- POSTMESSAGE_FAILURE_HANDLING: 1. Task completes promptly, never stranded ----
    [Fact]
    public async Task ReadTextSnapshotAsync_PostMessageFails_TaskCompletesPromptly()
    {
        var (monitor, native, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("x");
        native.PostReadWorkSignalResult = false;
        native.PostReadWorkSignalError = 87;

        // WaitAsync itself proves the Task did not hang -- if PostMessage failure left it
        // stranded, this would time out instead of completing.
        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);

        monitor.Stop();

        // ---- 2/3. outcome + Win32 error preserved ----
        Assert.Equal(ClipboardReadOutcome.NativeFailure, result.Outcome);
        Assert.Equal(87, result.Win32Error);
        Assert.Null(result.Snapshot);
    }

    // ---- 4. A later, unrelated ReadWork signal must never cause the stranded (already
    // completed) request to execute a real clipboard read -- ProcessOnePendingRead must skip
    // it and service the live request that actually arrived. ----
    [Fact]
    public async Task ReadTextSnapshotAsync_PostMessageFails_StaleEntryNeverTriggersRealRead_LaterRequestStillWorks()
    {
        var (monitor, native, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("first attempt payload -- must never be read");

        native.PostReadWorkSignalResult = false;
        native.PostReadWorkSignalError = 87;
        var failedRequest = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardReadOutcome.NativeFailure, failedRequest.Outcome);

        // A real clipboard read for the failed request's own payload must never have happened --
        // OpenClipboard is only called from inside ExecuteRead, which only runs for a live
        // (non-completed) dequeued request.
        int openCallsAfterFailedPost = textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.OpenClipboard));
        Assert.Equal(0, openCallsAfterFailedPost);

        // Now a genuine request succeeds normally -- the stale completed entry left behind in
        // _pendingReads must be silently skipped, not misrouted or double-processed.
        native.PostReadWorkSignalResult = true;
        textNative.SetUnicodeTextPayload("second attempt");
        var liveRequest = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);

        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, liveRequest.Outcome);
        Assert.Equal("second attempt", liveRequest.Snapshot!.Value.Text);
        Assert.Equal(1, textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.OpenClipboard)));
    }

    // ---- 5. Stop/Dispose still clean up normally after a PostMessage failure ----
    [Fact]
    public async Task ReadTextSnapshotAsync_PostMessageFails_StopStillCleansUpNormally()
    {
        var (monitor, native, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("x");
        native.PostReadWorkSignalResult = false;

        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeClipboardMonitorNative.RemoveClipboardFormatListener)));
        Assert.Equal(1, native.CallLog.Count(n => n == nameof(FakeClipboardMonitorNative.DestroyWindow)));
    }

    // ---- GLOBALUNLOCK_SEMANTICS: 1. FALSE + NO_ERROR -> normal, successful unlock ----
    [Fact]
    public async Task ReadTextSnapshotAsync_GlobalUnlockFalseNoError_IsTreatedAsNormalSuccess()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");
        textNative.GlobalUnlockBehavior = FakeClipboardTextNative.UnlockBehavior.Normal;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
        Assert.Equal("hello", result.Snapshot!.Value.Text);
    }

    // "still locked" (TRUE) is also not itself a failure in this type's single-lock usage.
    [Fact]
    public async Task ReadTextSnapshotAsync_GlobalUnlockStillLocked_IsNotTreatedAsFailure()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");
        textNative.GlobalUnlockBehavior = FakeClipboardTextNative.UnlockBehavior.StillLocked;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
    }

    // ---- 2. FALSE + nonzero error -> NativeFailure ----
    [Fact]
    public async Task ReadTextSnapshotAsync_GlobalUnlockFails_ReturnsNativeFailure()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");
        textNative.GlobalUnlockBehavior = FakeClipboardTextNative.UnlockBehavior.Failed;
        textNative.GlobalUnlockError = 6;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.NativeFailure, result.Outcome);
        Assert.Equal(6, result.Win32Error);
        Assert.Null(result.Snapshot);
    }

    // ---- 3. CloseClipboard is still called even when GlobalUnlock failed ----
    [Fact]
    public async Task ReadTextSnapshotAsync_GlobalUnlockFails_StillCallsCloseClipboard()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");
        textNative.GlobalUnlockBehavior = FakeClipboardTextNative.UnlockBehavior.Failed;

        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(1, textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.CloseClipboard)));
    }

    // ---- 4. Successful GlobalLock is still paired exactly once with TryGlobalUnlock, even on
    // unlock failure (existing invariant preserved) ----
    [Fact]
    public async Task ReadTextSnapshotAsync_GlobalUnlockFails_LockUnlockCountsStillMatch()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");
        textNative.GlobalUnlockBehavior = FakeClipboardTextNative.UnlockBehavior.Failed;

        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(
            textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.TryGlobalLock)),
            textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.TryGlobalUnlock)));
    }

    // ---- CLOSECLIPBOARD_FAILURE / FAILURE_PRECEDENCE: 1. successful read + Close failure ->
    // Success forbidden, final outcome is NativeFailure ----
    [Fact]
    public async Task ReadTextSnapshotAsync_SuccessfulRead_ButCloseClipboardFails_NeverReturnsSuccess()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");
        textNative.CloseClipboardResult = false;
        textNative.CloseClipboardError = 6;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.NotEqual(ClipboardReadOutcome.Success, result.Outcome);
        Assert.Equal(ClipboardReadOutcome.NativeFailure, result.Outcome);
        Assert.Equal(6, result.Win32Error);
        Assert.Null(result.Snapshot);
    }

    // ---- 2. MalformedData read + Close failure -> final outcome is still NativeFailure(close) ----
    [Fact]
    public async Task ReadTextSnapshotAsync_MalformedRead_AndCloseClipboardFails_ReportsCloseFailure()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetRawPayload([0x41, 0x00, 0x42]); // odd length -- malformed
        textNative.CloseClipboardResult = false;
        textNative.CloseClipboardError = 99;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.NativeFailure, result.Outcome);
        Assert.Equal(99, result.Win32Error);
    }

    // ---- 3. FormatUnavailable + Close failure -> final outcome is still NativeFailure(close) ----
    [Fact]
    public async Task ReadTextSnapshotAsync_FormatUnavailable_AndCloseClipboardFails_ReportsCloseFailure()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.UnicodeTextAvailable = false;
        textNative.CloseClipboardResult = false;
        textNative.CloseClipboardError = 55;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.NativeFailure, result.Outcome);
        Assert.Equal(55, result.Win32Error);
    }

    // ---- 4. CloseClipboard is still called exactly once even when it itself fails ----
    [Fact]
    public async Task ReadTextSnapshotAsync_CloseClipboardFails_IsStillCalledExactlyOnce()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");
        textNative.CloseClipboardResult = false;

        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(1, textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.CloseClipboard)));
    }

    // ---- 5. No raw text anywhere in a Close-failure result, regardless of what the read body
    // itself found ----
    [Fact]
    public async Task ReadTextSnapshotAsync_CloseClipboardFails_ResultNeverCarriesText()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("very sensitive synthetic content");
        textNative.CloseClipboardResult = false;
        textNative.CloseClipboardError = 6;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Null(result.Snapshot);
        // Structural guarantee, not just this test: ClipboardTextReadResult.Snapshot is only
        // ever populated by ClipboardTextReadResult.Success(...), never by Failure(...).
    }
}
