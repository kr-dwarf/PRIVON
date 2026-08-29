using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// BUG-006 EXCEPTION-ATOMIC CORRECTION -- exception-injection RED/characterization evidence
// (EX-006-001 .. EX-006-010). Companion to Bug006RecoveryTests.cs, which characterizes plain
// Win32-style (bool-return) failures; this suite characterizes genuine MANAGED EXCEPTIONS thrown
// from the native-wrapper seam (via FakeClipboardTextNative's ThrowOnGlobal*CallNumber /
// SyntheticNativeException seams -- see that file's own doc), standing in for the class of real
// exceptions (Marshal.Copy, managed array allocation) that can occur around the actual Win32
// P/Invoke calls, which themselves never throw catchable exceptions for an ordinary failure (see
// ClipboardChangeMonitor's own EXCEPTION_ATOMIC_SLOT_OWNERSHIP doc).
//
// SCOPE NOTE: an exception thrown from WITHIN TryPrepareRollbackFull (i.e. anywhere before the
// destructive boundary) is NOT caught anywhere in ClipboardChangeMonitor -- it is deliberately
// allowed to propagate past the slot-repair `finally` (per the "preserve/rethrow the original
// exception" contract) all the way up through ExecuteWrite/ProcessOnePendingWrite to
// OwnerThreadMain's own top-level catch, which (pre-existing, out-of-scope-for-BUG-006 behavior)
// treats it like a startup failure and tears the whole monitor down -- so the write's own Task
// never completes. Those tests therefore fire the write WITHOUT awaiting it to completion and
// instead poll the process-wide GLOBAL_ROLLBACK_SLOT's own static state directly, which the
// owner thread's `finally` chain resolves synchronously and near-instantly regardless of whether
// the write's own Task ever gets a result. Tests where the exception is fully caught INSIDE
// ResolveOrQuarantineRollbackHandle's own try/catch/finally (matching its documented "never
// throws" contract) complete normally and are awaited directly.
//
// Synthetic PII only throughout.
public class Bug006ExceptionAtomicTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private const uint DefaultSequence = 5;
    private const string OriginalA = "SYNTHETIC-ORIGINAL-A";
    private const string ProtectedB = "SYNTHETIC-PROTECTED-B";

    private static (ClipboardChangeMonitor Monitor, FakeClipboardMonitorNative Native, FakeClipboardTextNative TextNative) CreateStarted()
    {
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        textNative.SetUnicodeTextPayload(OriginalA);
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative);
        monitor.Start();
        return (monitor, native, textNative);
    }

    // Polls the process-wide slot until it leaves Reserved (or the deadline passes) -- used by
    // every test below whose triggering write's own Task never completes (see class doc). The
    // slot itself (an Interlocked/Volatile field) becomes visible to this polling thread the
    // instant MutateWhileClipboardOpen's own `finally` sets it, but that is NOT a synchronization
    // fence for OTHER, unrelated owner-thread state (e.g. the native fake's own CallLog list) --
    // the owner thread's exception is still unwinding further (through ExecuteWrite/
    // ProcessOnePendingWrite/RunMessageLoop, into OwnerThreadMain's own catch/finally) for a brief
    // moment afterward. A short settle delay here (not a production concern -- purely to let that
    // in-flight, single-threaded unwind finish before a test inspects other owner-thread-only
    // state) avoids a genuine "collection was modified" race on CallLog.
    private static async Task<int> WaitForSlotToLeaveReservedAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? WaitTimeout);
        int slot;
        do
        {
            slot = ClipboardChangeMonitor.GlobalRollbackSlotStateForTests;
            if (slot != ClipboardChangeMonitor.SlotReserved)
            {
                await Task.Delay(50);
                return slot;
            }
            await Task.Delay(5);
        } while (DateTime.UtcNow < deadline);
        return slot;
    }

    // ==================================================================
    // EX-006-001 -- shutdown's own opportunistic quarantine resolution throws (the FIRST,
    // previously-unguarded GlobalFree call inside ResolveOrQuarantineRollbackHandle -- see that
    // method's own EXCEPTION_ATOMIC_CORRECTION doc): the slot must end Quarantined with the SAME
    // handle/byteLength republished, never stranded at Reserved (the exact defect this fix
    // closes -- TryResolveQuarantineAtShutdown's own CompareExchange already moved the slot to
    // Reserved before this call, and nothing downstream of an uncaught exception there would ever
    // move it further without this fix).
    // ==================================================================
    [Fact]
    public void Ex006_001_ShutdownCleanupThrows_SlotEndsQuarantined_NeverStrandedReserved()
    {
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        textNative.SetUnicodeTextPayload(OriginalA);

        Assert.True(textNative.TryGlobalAlloc(16, out nint handle, out _));
        ClipboardChangeMonitor.ForceQuarantinedForTests(handle, 16);
        textNative.ThrowOnGlobalFreeCallNumber = 1; // the very first (previously unguarded) free attempt

        var monitor = new ClipboardChangeMonitor(native, textNative: textNative);
        monitor.Start();
        monitor.Stop(); // triggers OwnerThreadMain's own defensive try/catch around shutdown cleanup

        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        Assert.Equal(handle, ClipboardChangeMonitor.QuarantinedHandleForTests);
        Assert.Equal(16, ClipboardChangeMonitor.QuarantinedByteLengthForTests);

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    // ==================================================================
    // EX-006-002 -- a fresh admission's own rollback allocation (TryGlobalAlloc, the 2nd alloc
    // call -- B's own pre-OpenClipboard allocation is the 1st) throws BEFORE EmptyClipboard is
    // ever reached: nothing was ever allocated for the rollback (the out-param alias never got a
    // chance to be assigned), so the slot has nothing to resolve and ends Empty, never Reserved.
    // ==================================================================
    [Fact]
    public async Task Ex006_002_FreshAdmission_RollbackAllocationThrows_BeforeEmptyClipboard_SlotEndsEmpty()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.ThrowOnGlobalAllocCallNumber = 2; // rollback's own alloc (B's own is call #1)

        _ = monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB); // Task never completes -- see class doc
        int finalSlot = await WaitForSlotToLeaveReservedAsync();

        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, finalSlot);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TrySetClipboardData), textNative.CallLog);

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    // ==================================================================
    // EX-006-003 -- the rollback buffer's own fill lock (TryGlobalLock, the 3rd lock call -- B's
    // own prep is #1, the pre-Empty read-of-A is #2) throws BEFORE EmptyClipboard: unlike
    // EX-006-002, TryGlobalAlloc already succeeded here, so a real, still-PRIVON-owned rollback
    // handle exists at the moment of the throw -- EXCEPTION_ATOMIC_ALLOCATION_WINDOW (see
    // MutateWhileClipboardOpen's own doc) is what correctly resolves it via the outer `finally`
    // even though normal code never got the chance to. The slot still ends Empty (resolution
    // succeeds -- GlobalFree defaults to success), never stranded at Reserved.
    // ==================================================================
    [Fact]
    public async Task Ex006_003_RollbackFillLockThrows_BeforeEmptyClipboard_AllocatedHandleStillResolved()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.ThrowOnGlobalLockCallNumber = 3; // rollback's own fill lock

        _ = monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB); // Task never completes -- see class doc
        int finalSlot = await WaitForSlotToLeaveReservedAsync();

        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, finalSlot);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        // The rollback handle (B + rollback = 2 allocations) was resolved (freed) by the outer
        // finally's exception-fallback path, despite TryPrepareRollbackFull itself never
        // returning normally.
        Assert.Equal(2, textNative.AllocatedHandles.Count);
        var rollbackHandle = textNative.AllocatedHandles[1];
        Assert.Contains(rollbackHandle, textNative.FreedHandleLog);

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    // ==================================================================
    // EX-006-004 -- the READ-of-current-content step (the pre-Empty backup read's own
    // TryGlobalLock, the 2nd lock call) throws, distinct from EX-006-003's rollback-FILL lock: an
    // exception during the "read A" half of "prepare the rollback buffer", rather than the
    // "allocate + fill" half. Reached before TryGlobalAlloc for the rollback is ever called, so
    // (unlike EX-006-003) nothing rollback-shaped was ever allocated -- the slot has nothing to
    // resolve and ends Empty directly, mirroring RED-006-002's non-exception equivalent.
    // ==================================================================
    [Fact]
    public async Task Ex006_004_ReadOfCurrentContentLockThrows_BeforeAnyRollbackAllocation_SlotEndsEmpty()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.ThrowOnGlobalLockCallNumber = 2; // the pre-Empty read-of-A's own lock

        _ = monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB); // Task never completes -- see class doc
        int finalSlot = await WaitForSlotToLeaveReservedAsync();

        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, finalSlot);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.Single(textNative.AllocatedHandles); // only B's own pre-OpenClipboard allocation

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    // ==================================================================
    // EX-006-005 -- the rollback buffer's own fill UNLOCK (TryGlobalUnlock, the 3rd unlock call)
    // throws AFTER a genuinely successful Marshal.Copy already filled it -- exercising the
    // exception path from INSIDE TryPrepareRollbackFull's own try/finally (Marshal.Copy's finally
    // already ran TryGlobalUnlock for real; this is that call itself throwing), distinct from
    // EX-006-003 (lock throws BEFORE the try/finally is even entered). The already-filled handle
    // is still correctly resolved by the outer finally.
    // ==================================================================
    [Fact]
    public async Task Ex006_005_RollbackFillUnlockThrows_AfterSuccessfulCopy_AllocatedHandleStillResolved()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.ThrowOnGlobalUnlockCallNumber = 3; // rollback's own fill unlock (after a real copy)

        _ = monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB); // Task never completes -- see class doc
        int finalSlot = await WaitForSlotToLeaveReservedAsync();

        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, finalSlot);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.Equal(2, textNative.AllocatedHandles.Count);
        var rollbackHandle = textNative.AllocatedHandles[1];
        Assert.Contains(rollbackHandle, textNative.FreedHandleLog);

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    // ==================================================================
    // EX-006-006 -- EmptyClipboard itself fails, AND the (no-longer-needed) rollback buffer's own
    // GlobalFree also fails (scrub deliberately made impossible too, via a failing lock): Codex
    // finding #4 -- the slot must become Quarantined, never incorrectly released to Empty while a
    // still-PRIVON-owned handle remains unresolved. Plain Win32-style false returns throughout,
    // no exception injection needed for this one -- included here (rather than
    // Bug006RecoveryTests.cs) because it directly characterizes the specific pre-fix defect
    // report #4 this whole correction turn was scoped around.
    // ==================================================================
    [Fact]
    public async Task Ex006_006_EmptyClipboardFails_AndRollbackFreeAlsoFails_SlotBecomesQuarantined_NeverIncorrectlyEmptied()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.EmptyClipboardResult = false;
        textNative.EmptyClipboardError = 5;
        textNative.GlobalFreeResult = false;
        textNative.GlobalFreeError = 87;
        // Only the scrub's own (4th) lock call fails -- B's prep lock (#1), the pre-Empty
        // read-of-A lock (#2), and the rollback fill lock (#3) must all still succeed normally, or
        // the write would abort long before ever reaching EmptyClipboard at all.
        textNative.QueueGlobalLockResults((true, 0), (true, 0), (true, 0), (false, 77));

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);

        // Asserted BEFORE Stop() -- monitor.Stop() itself triggers OwnerThreadMain's own
        // TryResolveQuarantineAtShutdown opportunistic-cleanup hook (working as designed), which
        // would otherwise resolve THIS exact quarantine (GlobalFreeResult reverts to succeeding by
        // default on a fresh call with no fault-injection left queued) before we get to inspect it
        // -- matching Bug006RecoveryTests.cs's own GlobalSlot_D/E precedent.
        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.False(result.ClipboardMutated); // EmptyClipboard itself never succeeded
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        var rollbackHandle = textNative.AllocatedHandles[1];
        Assert.Equal(rollbackHandle, ClipboardChangeMonitor.QuarantinedHandleForTests);

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        monitor.Stop();
    }

    // ==================================================================
    // EX-006-007 -- B_FAILURE_ORDER under exception pressure: Set(B) fails, the already-prepared
    // rollback Set(A) SUCCEEDS (restoration), and B's OWN cleanup free (which only runs AFTER the
    // restoration attempt in program order) throws. Proves restoration is attempted, and
    // succeeds, strictly BEFORE B's cleanup is ever touched -- an exception while freeing B can
    // never retroactively un-attempt (or prevent) the already-completed restoration.
    // ==================================================================
    [Fact]
    public async Task Ex006_007_BCleanupThrows_RestorationWasAlreadyAttemptedAndSucceededFirst()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.QueueSetClipboardDataResults((false, 8), (true, 0)); // B fails, A-restore succeeds
        textNative.ThrowOnGlobalFreeCallNumber = 1; // B's own cleanup free (the rollback was never freed on this path)

        _ = monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB); // Task never completes -- see class doc

        // Poll until B's own cleanup free has been attempted (proof the whole sequence up to and
        // including the throwing call has run) rather than the slot (which was already released
        // by the time restoration succeeded, before B's cleanup was ever reached).
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!textNative.CallLog.Contains(nameof(FakeClipboardTextNative.TryGlobalFree)) && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        // Exactly 2 TrySetClipboardData calls (B's own failed attempt, then the restoration's
        // successful attempt) both happened BEFORE the single TryGlobalFree call (B's own cleanup,
        // which is what threw) -- restoration was never skipped or deferred behind B's cleanup.
        int freeIndex = textNative.CallLog.IndexOf(nameof(FakeClipboardTextNative.TryGlobalFree));
        int setCallsBeforeFree = textNative.CallLog.Take(freeIndex).Count(n => n == nameof(FakeClipboardTextNative.TrySetClipboardData));
        Assert.Equal(2, setCallsBeforeFree);
        // The restoration's own Set succeeded (SetHandle only updates on a successful Set) --
        // proof the restore genuinely completed, not merely that it was attempted.
        Assert.NotNull(textNative.SetHandle);
        Assert.NotEqual(textNative.AllocatedHandles[0], textNative.SetHandle); // not B's own handle -- the rollback's

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    // ==================================================================
    // EX-006-008 -- after the DESTRUCTIVE_BOUNDARY, both Set(B) and the rollback restoration fail,
    // and the resulting rollback-cleanup attempt's own scrub lock throws (not merely fails): fully
    // inside ResolveOrQuarantineRollbackHandle's own try/catch/finally (see its own
    // EXCEPTION_ATOMIC_CORRECTION doc), so this one completes and is awaited normally. The slot
    // must end Quarantined -- never stranded at Reserved -- exactly matching EX-006-001's
    // guarantee, but reached via the NORMAL write path instead of shutdown.
    // ==================================================================
    [Fact]
    public async Task Ex006_008_PostDestructiveBoundary_RollbackCleanupScrubThrows_SlotEndsQuarantined_NeverStrandedReserved()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetClipboardDataResult = false; // both B's Set and the restoration attempt fail
        textNative.SetClipboardDataError = 8;
        textNative.GlobalFreeResult = false; // rollback's own initial free fails -- scrub is attempted
        textNative.GlobalFreeError = 50;
        textNative.ThrowOnGlobalLockCallNumber = 4; // the scrub's own lock (1=B prep,2=read-of-A,3=rollback-fill,4=scrub)

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.True(result.ClipboardMutated); // EmptyClipboard already succeeded before any of this
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        var rollbackHandle = textNative.AllocatedHandles[1];
        Assert.Equal(rollbackHandle, ClipboardChangeMonitor.QuarantinedHandleForTests);

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
    }

    // ==================================================================
    // EX-006-009 -- a NEW write's own admission-time resolution of a PRE-EXISTING quarantine (left
    // over from an earlier, unrelated catastrophic failure) throws during that resolution attempt.
    // Fully inside ResolveOrQuarantineRollbackHandle's own try/catch/finally, so the write itself
    // completes normally: it must abort BEFORE any new rollback allocation of its own (never mind
    // EmptyClipboard), and the SAME coherent handle/byteLength -- not a new, second one -- must
    // remain quarantined afterward.
    // ==================================================================
    [Fact]
    public async Task Ex006_009_NewWriteAdmission_ResolvesPreExistingQuarantine_ThrowsDuringResolution_SameQuarantineRepublished_AbortsBeforeNewAllocation()
    {
        var (monitor, _, textNative) = CreateStarted();
        Assert.True(textNative.TryGlobalAlloc(16, out nint oldHandle, out _));
        ClipboardChangeMonitor.ForceQuarantinedForTests(oldHandle, 16);
        textNative.ThrowOnGlobalFreeCallNumber = 1; // the admission-cleanup's own (first) free attempt

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, ProtectedB).WaitAsync(WaitTimeout);

        // Asserted BEFORE Stop() -- see EX-006-006's own identical note: ThrowOnGlobalFreeCallNumber
        // fires exactly once (call #1), so a SECOND resolution attempt (monitor.Stop()'s own
        // opportunistic TryResolveQuarantineAtShutdown) would use the default, succeeding
        // GlobalFreeResult and genuinely resolve this exact quarantine before we get to inspect it.
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        Assert.Equal(oldHandle, ClipboardChangeMonitor.QuarantinedHandleForTests); // same handle, not a new one
        Assert.Equal(16, ClipboardChangeMonitor.QuarantinedByteLengthForTests);
        // Exactly 2 allocations total: the old quarantine's own (pre-test) alloc, and B's own
        // pre-OpenClipboard allocation (unconditional, before admission is ever checked) -- never
        // a 3rd, NEW rollback allocation for this aborted write.
        Assert.Equal(2, textNative.AllocatedHandles.Count);

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        monitor.Stop();
    }

    // ==================================================================
    // EX-006-010 -- cross-instance quarantine handoff survives an exception: a FIRST monitor's own
    // admission-time resolution of a pre-existing quarantine throws (republishing the SAME
    // handle, exactly as EX-006-009 proves in isolation); a SECOND, independent monitor instance
    // then correctly resolves that SAME republished handle (this time without any injected
    // failure) and completes an entirely normal successful write of its own. No double-free (the
    // first instance's own throwing attempt never actually freed the memory -- Win32's own
    // GlobalFree contract never invalidates a handle on failure) and no lost handle (the second
    // instance recovers the exact same one).
    // ==================================================================
    [Fact]
    public async Task Ex006_010_FirstMonitorsCleanupThrows_SecondIndependentMonitorStillResolvesSameHandle_NoDoubleFreeNoLostHandle()
    {
        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();

        var nativeA = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNativeA = new FakeClipboardTextNative();
        textNativeA.SetUnicodeTextPayload(OriginalA);
        Assert.True(textNativeA.TryGlobalAlloc(16, out nint oldHandle, out _));
        ClipboardChangeMonitor.ForceQuarantinedForTests(oldHandle, 16);
        textNativeA.ThrowOnGlobalFreeCallNumber = 1; // A's own admission-cleanup free throws
        var monitorA = new ClipboardChangeMonitor(nativeA, textNative: textNativeA);
        monitorA.Start();

        var resultA = await monitorA.WriteTextIfSequenceMatchesAsync(DefaultSequence, "SYNTHETIC-B-A").WaitAsync(WaitTimeout);

        // Deliberately NOT stopping monitorA yet -- see EX-006-006/009's own identical note:
        // ThrowOnGlobalFreeCallNumber fires exactly once, so monitorA's own Stop() would otherwise
        // opportunistically (and successfully, via textNativeA's now-default GlobalFreeResult)
        // resolve this exact quarantine before monitorB ever gets a chance to pick it up below.
        Assert.False(resultA.ClipboardMutated);
        Assert.Equal(ClipboardChangeMonitor.SlotQuarantined, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        Assert.Equal(oldHandle, ClipboardChangeMonitor.QuarantinedHandleForTests); // still the same handle

        // A second, independent monitor instance -- its OWN native seam has never even seen
        // `oldHandle` before now (it belongs to textNativeA's own allocation table) -- picks up
        // the SAME republished quarantine and resolves it normally.
        var nativeB = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNativeB = new FakeClipboardTextNative();
        textNativeB.SetUnicodeTextPayload(OriginalA);
        var monitorB = new ClipboardChangeMonitor(nativeB, textNative: textNativeB);
        monitorB.Start();

        var resultB = await monitorB.WriteTextIfSequenceMatchesAsync(DefaultSequence, "SYNTHETIC-B-B").WaitAsync(WaitTimeout);
        monitorB.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, resultB.Outcome);
        Assert.Equal(ClipboardChangeMonitor.SlotEmpty, ClipboardChangeMonitor.GlobalRollbackSlotStateForTests);
        // B's own admission-cleanup resolution called TryGlobalFree exactly once for the old
        // handle (on textNativeB's own seam) -- proof it was freed exactly once here, never twice,
        // and never a handle B itself had to freshly allocate.
        Assert.Single(textNativeB.FreedHandleLog, h => h == oldHandle);
        Assert.DoesNotContain(oldHandle, textNativeB.AllocatedHandles);

        ClipboardChangeMonitor.ResetGlobalRollbackSlotForTests();
        monitorA.Stop();
    }
}
