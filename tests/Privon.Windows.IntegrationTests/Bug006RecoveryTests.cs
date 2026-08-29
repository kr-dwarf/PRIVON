using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// BUG-006 (S2 CONFIRMED) -- deterministic RED/characterization evidence.
//
// THE DEFECT: EmptyClipboard succeeding is the DESTRUCTIVE_BOUNDARY (see
// IClipboardTextNative.EmptyClipboard's own doc) -- the original clipboard content is gone the
// instant it returns true. If the immediately-following SetClipboardData(protected replacement)
// then fails, the original content is lost with no recovery attempted anywhere in this codebase.
//
// SELECTED RECOVERY CONTRACT (accepted design, this suite characterizes it): entirely inside the
// SAME OpenClipboard bracket MutateWhileClipboardOpen already owns -- after the sequence CAS gate
// and CHECK 2 both pass, read the CURRENT CF_UNICODETEXT and prepare a rollback HGLOBAL for it
// BEFORE EmptyClipboard is ever called. If the protected Set then fails, attempt SetClipboardData
// with the already-prepared rollback handle. Recovery success or failure both remain
// ClipboardWriteOutcome.NativeFailure -- a recovery path must NEVER report protection Success.
//
// Synthetic PII only throughout.
public class Bug006RecoveryTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private const uint DefaultSequence = 5;
    private const string OriginalA = "SYNTHETIC-ORIGINAL-A";
    private const string ProtectedB = "SYNTHETIC-PROTECTED-B";

    private static (ClipboardChangeMonitor Monitor, FakeClipboardMonitorNative Native, FakeClipboardTextNative TextNative) CreateStarted()
    {
        // BUG-006 GLOBAL_ROLLBACK_SLOT is process-wide (deliberately, so a quarantined handle
        // outlives any one monitor instance -- see its own doc) -- reset it here so every test in
        // this class starts from a clean Empty state regardless of what an earlier test (run
        // sequentially within this class, per xUnit's default) left behind.
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();

        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        textNative.SetUnicodeTextPayload(OriginalA);
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative);
        monitor.Start();
        return (monitor, native, textNative);
    }

    // ==================================================================
    // RED-006-001 -- the destructive defect itself, characterized at the native-seam level
    // (independent of ClipboardChangeMonitor's own, evolving production behavior): a successful
    // EmptyClipboard with no recovery attempted anywhere genuinely, permanently loses the original
    // content. Also doubles as fidelity proof for the Step-1 fake correction (EmptyClipboard now
    // actually clears the synthetic payload, mirroring real Win32 destructive semantics).
    // ==================================================================
    [Fact]
    public void Red006_001_EmptyClipboardSucceeds_SetFails_WithNoRecoveryAttempted_OriginalIsPermanentlyLost()
    {
        var textNative = new FakeClipboardTextNative();
        textNative.SetUnicodeTextPayload(OriginalA);

        Assert.True(textNative.OpenClipboard(0, out _));
        Assert.True(textNative.EmptyClipboard(out _)); // DESTRUCTIVE_BOUNDARY crossed -- A is gone now.

        // No recovery attempted (this is exactly the vulnerable, pre-BUG-006-fix shape) -- the
        // protected replacement Set also fails.
        textNative.SetClipboardDataResult = false;
        textNative.SetClipboardDataError = 8;
        Assert.False(textNative.TrySetClipboardData(new nint(999), out _));
        Assert.True(textNative.CloseClipboard(out _));

        // A is unrecoverable: the next read finds no real CF_UNICODETEXT content at all -- not A,
        // not B, nothing. Size 0 is never a legitimate CF_UNICODETEXT payload (production's own
        // GLOBALSIZE_ZERO_CLASSIFICATION, Win32ClipboardTextNative.TryGetGlobalSize, treats it
        // identically as "content is gone" -- see IClipboardTextNative.TryGetGlobalSize's own doc).
        Assert.True(textNative.TryGetUnicodeTextHandle(out nint handle, out _));
        textNative.TryGetGlobalSize(handle, out nuint size, out _);
        Assert.Equal((nuint)0, size);
    }

    // ==================================================================
    // RED-006-002 -- reading the current A (the first half of "prepare the rollback buffer")
    // fails BEFORE EmptyClipboard is ever reached -- fail closed, never cross the destructive
    // boundary for a write we could not have recovered from.
    // ==================================================================
    [Fact]
    public async Task Red006_002_ReadOfOriginalFailsBeforeEmptyClipboard_AbortsBeforeDestructiveBoundary()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.GetHandleResult = false; // the pre-Empty backup read can never even get a handle
        textNative.GetHandleError = 11;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TrySetClipboardData), textNative.CallLog);
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog); // B's handle freed
    }

    // ==================================================================
    // RED-006-003 -- B fails, the already-prepared rollback A Set succeeds: A is restored, but
    // this is NEVER reported as protection Success.
    // ==================================================================
    [Fact]
    public async Task Red006_003_ProtectedSetFails_RollbackSetSucceeds_OriginalIsRestored_NeverReportsSuccess()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.QueueSetClipboardDataResults((false, 8), (true, 0)); // B fails, A-restore succeeds

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.NotEqual(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.True(result.ClipboardMutated);

        var readBack = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, readBack.Outcome);
        Assert.Equal(OriginalA, readBack.Snapshot!.Value.Text);
    }

    // ==================================================================
    // RED-006-004 -- B fails, the rollback Set ALSO fails: hard failure, never a false Success,
    // both owned handles still resolved correctly (no leak surfaced as anything other than
    // NativeFailure).
    // ==================================================================
    [Fact]
    public async Task Red006_004_ProtectedSetFails_RollbackSetAlsoFails_HardFailure_NeverFalseSuccess()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetClipboardDataResult = false; // applies to both Set attempts -- no queue needed
        textNative.SetClipboardDataError = 8;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.NotEqual(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(2, textNative.AllocatedHandles.Count); // B + rollback
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog); // B freed
        Assert.Contains(textNative.AllocatedHandles[1], textNative.FreedHandleLog); // rollback freed
    }

    // ==================================================================
    // RED-006-005 -- B succeeds normally: the rollback Set is never invoked at all, the unused
    // rollback handle is freed exactly once, and the normal read-back verification path is
    // otherwise unchanged.
    // ==================================================================
    [Fact]
    public async Task Red006_005_ProtectedSetSucceeds_RollbackNeverInvoked_UnusedHandleFreedExactlyOnce()
    {
        var (monitor, _, textNative) = CreateStarted();

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        var readBack = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.Equal(ClipboardReadOutcome.Success, readBack.Outcome);
        Assert.Equal(ProtectedB, readBack.Snapshot!.Value.Text);

        Assert.Equal(1, textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.TrySetClipboardData)));
        Assert.Equal(2, textNative.AllocatedHandles.Count); // B + rollback (prepared, then unused)
        var rollbackHandle = textNative.AllocatedHandles[1];
        Assert.Equal(1, textNative.FreedHandleLog.Count(h => h == rollbackHandle));
        Assert.DoesNotContain(textNative.SetHandle!.Value, textNative.FreedHandleLog); // B's own handle never freed
    }

    // ==================================================================
    // RED-006-006 -- any failure before EmptyClipboard leaves A fully intact and readable
    // afterward, not merely "ClipboardMutated=false" but genuinely, byte-for-byte untouched.
    // ==================================================================
    [Fact]
    public async Task Red006_006_FailureBeforeEmptyClipboard_OriginalRemainsFullyIntactAndReadable()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.SequenceNumber = DefaultSequence;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(expectedSequence: 999, ProtectedB).WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.SequenceChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);

        var readBack = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, readBack.Outcome);
        Assert.Equal(OriginalA, readBack.Snapshot!.Value.Text);
    }

    // ==================================================================
    // RED-006-007 -- successful restore ownership transfer: B's handle is freed (its own Set
    // failed), the rollback handle is transferred to the system (it is what actually got Set) and
    // is NEVER GlobalFree'd.
    // ==================================================================
    [Fact]
    public async Task Red006_007_SuccessfulRestore_OwnershipTransfersCorrectly()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.QueueSetClipboardDataResults((false, 8), (true, 0));

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.Equal(2, textNative.AllocatedHandles.Count);
        var bHandle = textNative.AllocatedHandles[0];
        var rollbackHandle = textNative.AllocatedHandles[1];

        Assert.Contains(bHandle, textNative.FreedHandleLog); // B freed -- its own Set failed
        Assert.DoesNotContain(rollbackHandle, textNative.FreedHandleLog); // system-owned now -- never freed
        Assert.Equal(rollbackHandle, textNative.SetHandle); // the rollback handle is what actually got Set
    }

    // ==================================================================
    // RED-006-008 -- failed restore ownership: BOTH handles freed exactly once each -- no leak,
    // no double-free.
    // ==================================================================
    [Fact]
    public async Task Red006_008_FailedRestore_BothHandlesFreedExactlyOnce_NoLeakNoDoubleFree()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetClipboardDataResult = false;
        textNative.SetClipboardDataError = 8;

        await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(2, textNative.AllocatedHandles.Count);
        var bHandle = textNative.AllocatedHandles[0];
        var rollbackHandle = textNative.AllocatedHandles[1];

        Assert.Equal(1, textNative.FreedHandleLog.Count(h => h == bHandle));
        Assert.Equal(1, textNative.FreedHandleLog.Count(h => h == rollbackHandle));
    }

    // ==================================================================
    // RED-006-009 -- the read of A succeeds, but preparing the rollback HGLOBAL itself (a SECOND,
    // independent GlobalAlloc call -- B's own pre-OpenClipboard allocation is the first) fails:
    // still aborts before the destructive boundary.
    // ==================================================================
    [Fact]
    public async Task Red006_009_RollbackHGlobalAllocationFails_AbortsBeforeDestructiveBoundary()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.QueueGlobalAllocResults((true, 0), (false, 14)); // B's own alloc ok, rollback's alloc fails

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.Single(textNative.AllocatedHandles); // only B's own allocation ever completed
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog); // B freed
    }

    // ==================================================================
    // SELF-WRITE / GENERATION REGRESSION -- a successful restore must NEVER arm the self-write
    // suppression marker (that only ever happens inside VerifyWrite, on a genuine Success, which
    // this recovery path never reaches). Its own resulting native notification must always be
    // delivered normally, so a fresh generation/future protection attempt is always reachable.
    // Paired with the normal successful-write control, proving the two paths really are
    // distinguishable rather than suppression being broken entirely.
    // ==================================================================
    [Fact]
    public async Task Bug006_RestoreSucceeds_NeverArmsSelfWriteMarker_SubsequentNotificationIsNotSuppressed()
    {
        var (monitor, native, textNative) = CreateStarted();
        textNative.QueueSetClipboardDataResults((false, 8), (true, 0)); // B fails, A-restore succeeds
        var received = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => received.Add(n);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.NotEqual(ClipboardWriteOutcome.Success, result.Outcome);

        // The restore's own SetClipboardData produces a new, real clipboard sequence in
        // production -- simulate that here and confirm it is NOT treated as a self-write echo.
        native.SequenceNumber = 777;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout); // FIFO sync barrier
        monitor.Stop();

        Assert.Single(received);
        Assert.Equal(777u, received[0].SequenceNumber);
    }

    [Fact]
    public async Task Bug006_Control_NormalSuccessfulWrite_StillArmsSelfWriteMarker_SubsequentNotificationIsSuppressed()
    {
        var (monitor, native, _) = CreateStarted();
        var received = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => received.Add(n);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        uint writeSequence = result.ResultSequence!.Value;

        native.SequenceNumber = writeSequence;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Empty(received); // suppressed as our own verified self-write
    }

    // ==================================================================
    // RED-006-010 -- sequence mismatch (a newer generation already exists) exits before ANY
    // backup preparation, EmptyClipboard, or recovery machinery -- no stale-generation overwrite
    // path is ever reachable. Encodes the race property directly (recovery lives entirely inside
    // one OpenClipboard bracket the CAS gate already guards) rather than an impossible live
    // external-write race.
    // ==================================================================
    [Fact]
    public async Task Red006_010_SequenceMismatch_ExitsBeforeBackupPreparation_NoStaleOverwritePath()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.SequenceNumber = DefaultSequence;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(expectedSequence: 999, ProtectedB).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.SequenceChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGetUnicodeTextHandle), textNative.CallLog); // no backup read
        Assert.Single(textNative.AllocatedHandles); // only B's own pre-OpenClipboard allocation
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TrySetClipboardData), textNative.CallLog);
    }

    // Exact byte length of OriginalA as CF_UNICODETEXT (UTF-16LE + NUL terminator) -- what every
    // rollback allocation for it is sized to.
    private static readonly int OriginalAByteLength = System.Text.Encoding.Unicode.GetBytes(OriginalA + '\0').Length;

    // ==================================================================
    // GLOBAL_ROLLBACK_SLOT -- process-wide reservation/quarantine protocol regression.
    // ==================================================================

    // A. PRE_DESTRUCTIVE_ROLLBACK (supersedes the earlier fill-late design): a normal successful
    // write ALWAYS locks/fills the rollback buffer, even though it is never actually Set -- exactly
    // 4 GlobalLock calls total: B's own pre-OpenClipboard prep lock, the pre-Empty read-of-A lock,
    // the rollback buffer's own fill lock (all three now unconditional, before EmptyClipboard is
    // ever called), and the post-Set verification's own read-back lock (VerifyWhileClipboardOpen
    // reuses the same TryReadUnicodeTextBody helper).
    [Fact]
    public async Task GlobalSlot_A_PreDestructive_NormalSuccess_RollbackBufferIsAlwaysLockedAndFilled()
    {
        var (monitor, _, textNative) = CreateStarted();

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.Equal(4, textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.TryGlobalLock)));
        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
    }

    // C. S3: the restoration's own Win32 error -- not the protected Set's -- is what gets
    // reported, when the two genuinely differ and ordinary cleanup succeeds (no scrub path
    // exercised, isolating S3 from the cleanup-precedence rule).
    [Fact]
    public async Task GlobalSlot_C_S3_RestorationFailure_ReportsRestorationsOwnWin32Error()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.QueueSetClipboardDataResults((false, 8), (false, 99)); // B error 8, A error 99

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.Equal(99, result.Win32Error);
        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
    }

    // D. Free fails, scrub (Lock+zero-fill+Unlock) succeeds, but the retry-free ALSO fails: the
    // handle ends up quarantined, but its content is demonstrably all-zero -- proving the scrub
    // step genuinely ran and erased raw content before ownership was ever surrendered.
    [Fact]
    public async Task GlobalSlot_D_FreeFails_ScrubSucceeds_RetryFreeAlsoFails_ContentIsZeroed()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetClipboardDataResult = false;
        textNative.SetClipboardDataError = 8;
        // B_FAILURE_ORDER: restoration is attempted before B's own cleanup, so the rollback
        // handle's own free (initial attempt + retry-after-scrub) is queued first; B's own free
        // (which happens afterward) is left to the default (succeeds).
        textNative.QueueGlobalFreeResults((false, 50), (false, 50)); // rollback fails twice

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);

        // Asserted BEFORE Stop(): monitor.Stop() itself triggers OwnerThreadMain's own
        // TryResolveQuarantineAtShutdown opportunistic-cleanup hook (working as designed), which
        // would otherwise resolve THIS exact quarantine (fault-injection queues are exhausted by
        // then, so cleanup falls back to the succeeding default) before we get to inspect it.
        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);

        var rollbackHandle = textNative.AllocatedHandles[1];
        Assert.Equal(rollbackHandle, ClipboardChangeMonitor.QuarantinedHandleForTests);
        var content = textNative.ReadAllocatedBytes(rollbackHandle);
        Assert.All(content, b => Assert.Equal(0, b));

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        monitor.Stop();
    }

    // E. Free fails, and the scrub's OWN GlobalLock also fails (content-erasure could not even be
    // attempted this round): the exact handle + byteLength still becomes quarantined -- ownership
    // is never simply dropped just because the scrub itself couldn't run.
    [Fact]
    public async Task GlobalSlot_E_FreeFails_ScrubLockAlsoFails_ExactHandleAndByteLengthQuarantined()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetClipboardDataResult = false;
        textNative.SetClipboardDataError = 8;
        // B_FAILURE_ORDER: restoration (and its own cleanup resolution) is attempted before B's
        // own cleanup -- this is the rollback handle's own (only) free attempt; B's own free
        // (afterward) is left to the default (succeeds).
        textNative.QueueGlobalFreeResults((false, 50)); // rollback's only free attempt fails
        textNative.QueueGlobalLockResults((true, 0), (true, 0), (true, 0), (false, 77)); // 4th lock (scrub) fails

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);

        // Asserted BEFORE Stop() -- see GlobalSlot_D's own identical note.
        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);

        var rollbackHandle = textNative.AllocatedHandles[1];
        Assert.Equal(rollbackHandle, ClipboardChangeMonitor.QuarantinedHandleForTests);
        Assert.Equal(OriginalAByteLength, ClipboardChangeMonitor.QuarantinedByteLengthForTests);
        // No retry-free was even attempted -- exactly 2 GlobalFree calls total (B's own, plus
        // rollback's single failed attempt), never a 3rd.
        Assert.Equal(2, textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.TryGlobalFree)));

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        monitor.Stop();
    }

    // F / J. A future write, while the quarantine from a prior catastrophic failure remains
    // unresolved, aborts before any new rollback allocation or EmptyClipboard -- and the SAME
    // coherent handle/byteLength (not a new, second one) is what remains quarantined afterward.
    [Fact]
    public async Task GlobalSlot_F_J_FutureWrite_UnresolvedQuarantine_AbortsBeforeNewAllocation_SameHandleRepublished()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetClipboardDataResult = false;
        textNative.SetClipboardDataError = 8;
        textNative.QueueGlobalFreeResults((false, 50)); // rollback's only free attempt fails (B_FAILURE_ORDER)
        textNative.QueueGlobalLockResults((true, 0), (true, 0), (true, 0), (false, 77));

        var firstResult = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.NativeFailure, firstResult.Outcome);
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        var quarantinedHandle = ClipboardChangeMonitor.QuarantinedHandleForTests;

        // Keep cleanup failing deterministically for the second attempt too (queues now exhausted,
        // fall back to these plain flags).
        textNative.GlobalFreeResult = false;
        textNative.GlobalLockResult = false;

        var secondResult = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "SYNTHETIC-B-2").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.False(secondResult.ClipboardMutated);
        // EmptyClipboard was reached exactly once total (the FIRST write) -- the second never got
        // near the destructive boundary, and never allocated a second rollback buffer.
        Assert.Equal(1, textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.EmptyClipboard)));
        Assert.Equal(3, textNative.AllocatedHandles.Count); // B1, rollback1, B2 -- never rollback2
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        Assert.Equal(quarantinedHandle, ClipboardChangeMonitor.QuarantinedHandleForTests); // same handle, not a new one

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    // G. Once the pre-existing quarantine resolves DURING a new write's own admission, that same
    // winner keeps Reserved (no Empty round-trip) and its own new write proceeds normally.
    [Fact]
    public async Task GlobalSlot_G_QuarantineResolvesDuringAdmission_SameWinnerProceedsWithNewWrite()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetClipboardDataResult = false;
        textNative.SetClipboardDataError = 8;
        textNative.QueueGlobalFreeResults((false, 50)); // rollback's only free attempt fails (B_FAILURE_ORDER)
        textNative.QueueGlobalLockResults((true, 0), (true, 0), (true, 0), (false, 77));

        await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);

        // Cleanup and the new write's own Set both succeed now. The first write's own
        // EmptyClipboard already cleared the synthetic clipboard and nothing ever successfully
        // re-populated it (both its Sets failed) -- re-seed it here to represent NEW content now
        // present for this second, independent write to protect (exactly what a real later
        // clipboard-change event would provide).
        textNative.GlobalFreeResult = true;
        textNative.GlobalLockResult = true;
        textNative.SetClipboardDataResult = true;
        textNative.SetUnicodeTextPayload(OriginalA);

        var secondResult = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "SYNTHETIC-B-2").WaitAsync(WaitTimeout);
        var readBack = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, secondResult.Outcome);
        Assert.Equal("SYNTHETIC-B-2", readBack.Snapshot!.Value.Text);
        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
    }

    // H. Two concurrent monitor instances, fresh slot: exactly one can ever win the reservation --
    // proven structurally (a deterministic hold-and-release, never a timing-dependent race) by
    // pausing the winner immediately after admission (before EmptyClipboard) and confirming the
    // second monitor's own concurrent attempt is rejected while the first still holds Reserved.
    [Fact]
    public async Task GlobalSlot_H_TwoConcurrentMonitors_FreshSlot_ExactlyOneWinsReservation()
    {
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();

        var nativeA = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNativeA = new FakeClipboardTextNative();
        textNativeA.SetUnicodeTextPayload(OriginalA);
        textNativeA.HoldOnEmptyClipboard = true;
        var monitorA = new ClipboardChangeMonitor(nativeA, textNative: textNativeA);
        monitorA.Start();

        var nativeB = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNativeB = new FakeClipboardTextNative();
        textNativeB.SetUnicodeTextPayload(OriginalA);
        var monitorB = new ClipboardChangeMonitor(nativeB, textNative: textNativeB);
        monitorB.Start();

        var taskA = monitorA.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB);
        Assert.True(textNativeA.ReachedEmptyClipboard.Wait(WaitTimeout), "A never reached admission -- harness is wrong, not the slot.");

        // While A definitively holds Reserved, B's own concurrent write must lose.
        var resultB = await monitorB.WriteTextIfSequenceMatchesAsync(DefaultSequence, "SYNTHETIC-B-LOSER").WaitAsync(WaitTimeout);

        Assert.False(resultB.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNativeB.CallLog);
        Assert.Single(textNativeB.AllocatedHandles); // only B-loser's own replacement handle
        Assert.Contains(textNativeB.AllocatedHandles[0], textNativeB.FreedHandleLog);

        textNativeA.ReleaseEmptyClipboard();
        var resultA = await taskA.WaitAsync(WaitTimeout);
        monitorA.Stop();
        monitorB.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, resultA.Outcome);
        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
    }

    // I. Two concurrent monitor instances, quarantined slot: exactly one wins cleanup ownership.
    // Uses one SHARED native seam across the setup write and both competing monitors -- realistic,
    // since production has exactly one real Win32 API surface regardless of how many
    // ClipboardChangeMonitor wrapper instances exist. The loser never reaches its own
    // EmptyClipboard at all -- proving it never got far enough to touch the quarantined handle for
    // recovery purposes (recovery/cleanup only ever happens during admission, strictly before
    // EmptyClipboard).
    [Fact]
    public async Task GlobalSlot_I_TwoConcurrentMonitors_QuarantinedSlot_ExactlyOneWinsCleanupOwnership()
    {
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        var sharedTextNative = new FakeClipboardTextNative();
        sharedTextNative.SetUnicodeTextPayload(OriginalA);

        // Establish a genuine quarantine first.
        sharedTextNative.SetClipboardDataResult = false;
        sharedTextNative.SetClipboardDataError = 8;
        sharedTextNative.QueueGlobalFreeResults((false, 50)); // rollback's only free attempt fails (B_FAILURE_ORDER)
        sharedTextNative.QueueGlobalLockResults((true, 0), (true, 0), (true, 0), (false, 77));
        var setupNative = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var setupMonitor = new ClipboardChangeMonitor(setupNative, textNative: sharedTextNative);
        setupMonitor.Start();
        await setupMonitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);

        // Keep cleanup failing deterministically THROUGH setupMonitor's own Stop() -- otherwise its
        // own shutdown-resolution hook (GlobalFreeResult would otherwise already have fallen back
        // to its succeeding default, since the fault-injection queue is now exhausted) would
        // resolve this exact quarantine before monitor A/B ever get a chance to compete for it.
        sharedTextNative.GlobalFreeResult = false;
        setupMonitor.Stop();
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);

        // Cleanup would now succeed if attempted; pause whichever monitor wins right after
        // admission. The setup write's own EmptyClipboard cleared the synthetic clipboard and
        // nothing ever successfully re-populated it -- re-seed it to represent new content now
        // present for the competing writers below to protect.
        sharedTextNative.GlobalFreeResult = true;
        sharedTextNative.GlobalLockResult = true;
        sharedTextNative.SetClipboardDataResult = true;
        sharedTextNative.SetUnicodeTextPayload(OriginalA);
        sharedTextNative.HoldOnEmptyClipboard = true;
        sharedTextNative.ReachedEmptyClipboard.Reset();

        // Baseline: the setup write above already logged its own EmptyClipboard call on this SAME
        // shared fake -- only the DELTA from here on reflects the A-vs-B competition.
        int emptyClipboardCallsBeforeCompetition = sharedTextNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.EmptyClipboard));

        var nativeA = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var monitorA = new ClipboardChangeMonitor(nativeA, textNative: sharedTextNative);
        monitorA.Start();
        var taskA = monitorA.WriteTextIfSequenceMatchesAsync(DefaultSequence, "SYNTHETIC-B-A");
        Assert.True(sharedTextNative.ReachedEmptyClipboard.Wait(WaitTimeout));

        var nativeB = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var monitorB = new ClipboardChangeMonitor(nativeB, textNative: sharedTextNative);
        monitorB.Start();
        var resultB = await monitorB.WriteTextIfSequenceMatchesAsync(DefaultSequence, "SYNTHETIC-B-LOSER").WaitAsync(WaitTimeout);
        monitorB.Stop();

        Assert.False(resultB.ClipboardMutated);
        // Exactly one NEW EmptyClipboard call since the competition began (A's own) -- B's write
        // never got past admission.
        int emptyClipboardCallsAfterB = sharedTextNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.EmptyClipboard));
        Assert.Equal(1, emptyClipboardCallsAfterB - emptyClipboardCallsBeforeCompetition);

        sharedTextNative.ReleaseEmptyClipboard();
        var resultA = await taskA.WaitAsync(WaitTimeout);
        monitorA.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, resultA.Outcome);
        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
    }

    // K. Shutdown-vs-writer contention: shutdown's own resolution attempt uses the identical
    // Quarantined -> Reserved CAS a write uses -- if some other actor already holds Reserved,
    // shutdown backs off immediately and never touches this monitor's own native seam. The
    // companion case (shutdown genuinely resolves an unresolved quarantine of its own) is verified
    // separately.
    [Fact]
    public void GlobalSlot_K_Shutdown_WhenSlotAlreadyReservedByAnotherActor_NeverTouchesNativeSeam()
    {
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        ClipboardChangeMonitor.ForceReservedForTests(); // simulate: some other actor currently owns the slot

        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative);
        monitor.Start();
        monitor.Stop(); // triggers OwnerThreadMain's finally -> TryResolveQuarantineAtShutdown

        Assert.Equal(ClipboardChangeMonitor.SlotReserved, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests); // unchanged
        Assert.Empty(textNative.CallLog); // shutdown never called into this monitor's own native seam

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    [Fact]
    public void GlobalSlot_K_Shutdown_ResolvesItsOwnUnresolvedQuarantine_WhenCleanupNowSucceeds()
    {
        var (monitor, _, textNative) = CreateStarted();
        Assert.True(textNative.TryGlobalAlloc(16, out nint handle, out _));
        ClipboardChangeMonitor.ForceQuarantinedForTests(handle, 16);

        monitor.Stop(); // GlobalFreeResult defaults true -- shutdown's own attempt should resolve it

        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        Assert.Contains(handle, textNative.FreedHandleLog);
    }

    // L. Process-lifetime reachability: the quarantine survives even when no ClipboardChangeMonitor
    // instance exists to reference it at all -- proving reachability was never tied to any
    // instance's own lifetime, including instance disposal/collection.
    [Fact]
    public void GlobalSlot_L_QuarantineSurvivesWithNoMonitorInstanceReachable()
    {
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        var textNative = new FakeClipboardTextNative();
        Assert.True(textNative.TryGlobalAlloc(16, out nint handle, out _));
        ClipboardChangeMonitor.ForceQuarantinedForTests(handle, 16);

        // No ClipboardChangeMonitor instance was ever created in this test -- reachability cannot
        // possibly depend on one. Force a real GC anyway for good measure.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        Assert.Equal(handle, ClipboardChangeMonitor.QuarantinedHandleForTests);
        Assert.Equal(16, ClipboardChangeMonitor.QuarantinedByteLengthForTests);

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    // M. Every ordinary exit path -- success, pre-Empty failure, restore success, restore failure
    // with ordinary (non-catastrophic) cleanup -- returns the slot to Empty. None may leave it
    // accidentally Reserved.
    [Fact]
    public async Task GlobalSlot_M_AllOrdinaryPaths_ReturnSlotToEmpty_NeverAccidentallyReserved()
    {
        {
            var (monitor, _, _) = CreateStarted();
            await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
            monitor.Stop();
            Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        }
        {
            var (monitor, native, _) = CreateStarted();
            native.SequenceNumber = DefaultSequence;
            await monitor.WriteTextIfSequenceMatchesAsync(999, ProtectedB).WaitAsync(WaitTimeout);
            monitor.Stop();
            Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        }
        {
            var (monitor, _, textNative) = CreateStarted();
            textNative.QueueSetClipboardDataResults((false, 8), (true, 0));
            await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
            monitor.Stop();
            Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        }
        {
            var (monitor, _, textNative) = CreateStarted();
            textNative.SetClipboardDataResult = false;
            textNative.SetClipboardDataError = 8;
            await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
            monitor.Stop();
            Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        }
    }
}
