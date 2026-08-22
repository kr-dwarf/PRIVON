using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3A.4 STEP3 -- Read-Only Clipboard Text Snapshot Foundation regression. All tests use
// FakeClipboardMonitorNative + FakeClipboardTextNative (synthetic, OS-free, but the latter backs
// GlobalLock with real unmanaged memory so the actual Marshal.Copy path is genuinely exercised).
// No real Windows clipboard content is ever read here -- see Phase 3A.4 STEP3's
// WINDOWS_SMOKE_POLICY: an automated real-clipboard-content smoke test is deliberately NOT
// created in this STEP.
public class ClipboardChangeMonitorReadTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static (ClipboardChangeMonitor Monitor, FakeClipboardMonitorNative Native, FakeClipboardTextNative TextNative) CreateStarted(
        TimeSpan? stopTimeoutOverride = null)
    {
        // UnicodeTextAvailable defaults to true here -- the availability check lives on the
        // separate IClipboardMonitorNative fake (a STEP2 member reused by the STEP3 read flow),
        // while payload content lives on IClipboardTextNative. Tests that specifically want
        // FormatUnavailable override this back to false explicitly.
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true };
        var textNative = new FakeClipboardTextNative();
        var monitor = new ClipboardChangeMonitor(native, stopTimeoutOverride, textNative);
        monitor.Start();
        return (monitor, native, textNative);
    }

    // ---- 1. Read request executes on the owner thread, not the caller's ----
    [Fact]
    public async Task ReadTextSnapshotAsync_ExecutesOnOwnerThread()
    {
        var (monitor, native, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");
        int callingThreadId = Environment.CurrentManagedThreadId;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.NotEqual(callingThreadId, textNative.OpenClipboardThreadId);
        Assert.Equal(native.RegisterWindowClassThreadId, textNative.OpenClipboardThreadId);
        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
    }

    // ---- 2. Read before Start -> NotRunning ----
    [Fact]
    public async Task ReadTextSnapshotAsync_BeforeStart_ReturnsNotRunning()
    {
        var monitor = new ClipboardChangeMonitor(new FakeClipboardMonitorNative(), textNative: new FakeClipboardTextNative());

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);

        Assert.Equal(ClipboardReadOutcome.NotRunning, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    // ---- 3. Read after Stop -> NotRunning ----
    [Fact]
    public async Task ReadTextSnapshotAsync_AfterStop_ReturnsNotRunning()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");
        monitor.Stop();

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);

        Assert.Equal(ClipboardReadOutcome.NotRunning, result.Outcome);
    }

    // ---- 4. Read after Dispose -> NotRunning ----
    [Fact]
    public async Task ReadTextSnapshotAsync_AfterDispose_ReturnsNotRunning()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");
        monitor.Dispose();

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);

        Assert.Equal(ClipboardReadOutcome.NotRunning, result.Outcome);
    }

    // ---- 5. CF_UNICODETEXT unavailable -> FormatUnavailable, GetClipboardData never called ----
    [Fact]
    public async Task ReadTextSnapshotAsync_FormatUnavailable_DoesNotCallGetClipboardData()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.UnicodeTextAvailable = false;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.FormatUnavailable, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGetUnicodeTextHandle), textNative.CallLog);
    }

    // ---- 6. OpenClipboard failure -> Busy outcome, carries Win32 error ----
    [Fact]
    public async Task ReadTextSnapshotAsync_OpenClipboardFails_ReturnsBusy()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.OpenClipboardResult = false;
        textNative.OpenClipboardError = 5;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Busy, result.Outcome);
        Assert.Equal(5, result.Win32Error);
    }

    // ---- 7/8/9/10/11. Real end-to-end text reads through the full pipeline ----
    [Theory]
    [InlineData("hello world")]
    [InlineData("안녕하세요, 여러 줄\nsecond line\r\nthird line")]
    [InlineData("emoji \U0001F600 test")]
    public async Task ReadTextSnapshotAsync_SuccessfulRead_ReturnsExactText(string text)
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload(text);

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
        Assert.Equal(text, result.Snapshot!.Value.Text);
    }

    [Fact]
    public async Task ReadTextSnapshotAsync_ZeroWidthCharacter_IsPreserved()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var text = "a" + zeroWidthSpace + "b";
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload(text);

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(text, result.Snapshot!.Value.Text);
    }

    // ---- 12. Nonzero sequence -> HasReliableSequence = true ----
    [Fact]
    public async Task ReadTextSnapshotAsync_NonZeroSequence_HasReliableSequenceTrue()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.SequenceNumber = 99;
        textNative.SetUnicodeTextPayload("x");

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(99u, result.Snapshot!.Value.SequenceNumber);
        Assert.True(result.Snapshot.Value.HasReliableSequence);
    }

    // ---- 13. Sequence zero -> read still succeeds, but HasReliableSequence = false ----
    [Fact]
    public async Task ReadTextSnapshotAsync_ZeroSequence_StillSucceeds_ButUnreliable()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.SequenceNumber = 0;
        textNative.SetUnicodeTextPayload("x");

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
        Assert.Equal(0u, result.Snapshot!.Value.SequenceNumber);
        Assert.False(result.Snapshot.Value.HasReliableSequence);
    }

    // ---- 14. GetClipboardData NULL failure -> NativeFailure ----
    [Fact]
    public async Task ReadTextSnapshotAsync_GetClipboardDataFails_ReturnsNativeFailure()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.GetHandleResult = false;
        textNative.GetHandleError = 6;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.NativeFailure, result.Outcome);
        Assert.Equal(6, result.Win32Error);
    }

    // ---- 15. GlobalSize == 0 -> NativeFailure (not MalformedData -- see native file's own doc) ----
    [Fact]
    public async Task ReadTextSnapshotAsync_GlobalSizeZero_ReturnsNativeFailure()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetRawPayload([]);
        textNative.GetGlobalSizeResult = false;
        textNative.GetGlobalSizeError = 8;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.NativeFailure, result.Outcome);
    }

    // ---- 16. Odd GlobalSize -> MalformedData, end to end ----
    [Fact]
    public async Task ReadTextSnapshotAsync_OddGlobalSize_ReturnsMalformedData()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetRawPayload([0x41, 0x00, 0x42]); // 3 bytes -- odd

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.MalformedData, result.Outcome);
    }

    // ---- 17. GlobalLock failure -> NativeFailure ----
    [Fact]
    public async Task ReadTextSnapshotAsync_GlobalLockFails_ReturnsNativeFailure()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("x");
        textNative.GlobalLockResult = false;
        textNative.GlobalLockError = 487;

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.NativeFailure, result.Outcome);
        Assert.Equal(487, result.Win32Error);
    }

    // ---- 19. No NUL within bounds -> MalformedData, end to end ----
    [Fact]
    public async Task ReadTextSnapshotAsync_NoNulTerminator_ReturnsMalformedData()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetRawPayload([0x41, 0x00, 0x42, 0x00]); // "AB", no terminator

        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.MalformedData, result.Outcome);
    }

    // ---- 20. CloseClipboard called after every successful OpenClipboard, on every outcome ----
    [Theory]
    [InlineData(true, true, true, true)]   // success path
    [InlineData(true, false, true, true)]  // format unavailable
    [InlineData(true, true, false, true)]  // GetClipboardData fails
    [InlineData(true, true, true, false)]  // GlobalLock fails (after GetGlobalSize succeeds)
    public async Task ReadTextSnapshotAsync_AlwaysClosesClipboard_WhenOpenSucceeded(
        bool openOk, bool unicodeAvailable, bool getHandleOk, bool lockOk)
    {
        var (monitor, native, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("x");
        textNative.OpenClipboardResult = openOk;
        native.UnicodeTextAvailable = unicodeAvailable;
        textNative.GetHandleResult = getHandleOk;
        textNative.GlobalLockResult = lockOk;

        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(1, textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.CloseClipboard)));
    }

    [Fact]
    public async Task ReadTextSnapshotAsync_OpenClipboardFails_NeverCallsCloseClipboard()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.OpenClipboardResult = false;

        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.DoesNotContain(nameof(FakeClipboardTextNative.CloseClipboard), textNative.CallLog);
    }

    // ---- 21. GlobalUnlock paired with every successful GlobalLock ----
    [Fact]
    public async Task ReadTextSnapshotAsync_SuccessfulLock_IsAlwaysPairedWithUnlock()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("x");

        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(
            textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.TryGlobalLock)),
            textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.TryGlobalUnlock)));
    }

    // ---- 22. Read-only handles (obtained via GetClipboardData, never PRIVON-allocated) are never
    // freed by the READ path -- ExecuteRead/ReadWhileClipboardOpen/TryReadUnicodeTextBody never
    // call TryGlobalFree at all. (As of Phase 3A.4 STEP4, IClipboardTextNative legitimately DOES
    // have a GlobalFree member -- it is required for the WRITE path's own PRIVON-owned-allocation
    // cleanup. The narrower, still-true invariant "a read never frees anything" is what this
    // test now checks; see ClipboardChangeMonitorWriteTests for the write path's own ownership
    // invariant, "a successfully-Set handle is never freed by anyone.")
    [Fact]
    public async Task ReadTextSnapshotAsync_NeverCallsGlobalFree()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("hello");

        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGlobalFree), textNative.CallLog);
    }

    // ---- 23. Failure results never carry text -- structurally impossible (Snapshot is only
    // set by Success(...)), verified directly ----
    [Fact]
    public void ClipboardTextReadResult_Failure_NeverHasSnapshot()
    {
        foreach (var outcome in Enum.GetValues<ClipboardReadOutcome>().Where(o => o != ClipboardReadOutcome.Success))
        {
            var result = ClipboardTextReadResult.Failure(outcome, 42);
            Assert.Null(result.Snapshot);
        }
    }

    [Fact]
    public void ClipboardTextReadResult_Failure_RejectsSuccessOutcome()
    {
        Assert.Throws<ArgumentException>(() => ClipboardTextReadResult.Failure(ClipboardReadOutcome.Success));
    }

    // ---- 24. A read request pending when Stop() is invoked never leaves its Task permanently
    // incomplete -- it completes as NotRunning via the owner thread's shutdown drain ----
    // Deterministic by construction, not timing-dependent: ForceShutdown enqueues the Shutdown
    // message BEFORE ReadTextSnapshotAsync gets a chance to enqueue its own ReadWork wakeup.
    // Two possible interleavings exist, and BOTH must -- and do -- resolve to NotRunning:
    //   (a) the owner thread hasn't dequeued Shutdown yet when ReadTextSnapshotAsync's
    //       lock-protected check runs: the request is enqueued into _pendingReads, then
    //       ReadWork is posted -- but since Shutdown was queued first, FIFO ordering
    //       guarantees the owner thread's next Take() returns Shutdown, not ReadWork, so the
    //       loop exits and the finally-block drain (not ProcessOnePendingRead) completes the
    //       request as NotRunning;
    //   (b) the owner thread already processed Shutdown and set _running=false before
    //       ReadTextSnapshotAsync's check runs: the request is never even enqueued, and
    //       NotRunning is returned immediately.
    // Either way the returned Task always completes, and always as NotRunning -- never
    // Success, and never left permanently pending.
    [Fact]
    public async Task PendingRead_RacingWithShutdown_AlwaysCompletes_AsNotRunning()
    {
        var (monitor, native, textNative) = CreateStarted();
        textNative.SetUnicodeTextPayload("x");

        native.ForceShutdown();
        var result = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);

        Assert.Equal(ClipboardReadOutcome.NotRunning, result.Outcome);

        // The owner thread has already exited via ForceShutdown -- Stop() must still be a safe,
        // non-throwing no-op.
        monitor.Stop();
    }
}
