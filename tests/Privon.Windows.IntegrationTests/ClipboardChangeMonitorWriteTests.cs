using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3A.4 STEP4 -- Sequence-Guarded Clipboard Write Transport regression. All tests use
// FakeClipboardMonitorNative + FakeClipboardTextNative (synthetic, OS-free, but the latter backs
// GlobalAlloc/GlobalLock with real unmanaged memory so the actual Marshal.Copy write and
// ClipboardTextParser.Parse read-back paths are genuinely exercised). No real Windows clipboard
// content is ever mutated here -- matching STEP3's WINDOWS_SMOKE_POLICY, extended to write: live
// write validation against a real user clipboard is an isolated VM/manual QA future phase, never
// an automated test.
public class ClipboardChangeMonitorWriteTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private const uint DefaultSequence = 5;

    private static (ClipboardChangeMonitor Monitor, FakeClipboardMonitorNative Native, FakeClipboardTextNative TextNative) CreateStarted()
    {
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative);
        monitor.Start();
        return (monitor, native, textNative);
    }

    // ==== SEQUENCE_CAS ====

    // ---- 1. expectedSequence == 0 -> immediate reject, no native mutation ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_ExpectedSequenceZero_RejectsWithoutNativeMutation()
    {
        var (monitor, _, textNative) = CreateStarted();

        var result = await monitor.WriteTextIfSequenceMatchesAsync(0, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.InvalidExpectedSequence, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Empty(textNative.CallLog);
    }

    // ---- 2. sequence mismatch -> SequenceChanged, no Empty/Set ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_SequenceMismatch_ReturnsSequenceChanged_NoEmptyOrSet()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.SequenceNumber = DefaultSequence;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(expectedSequence: 999, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.SequenceChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TrySetClipboardData), textNative.CallLog);
    }

    // ---- 3. current sequence 0 -> no mutation (0 is never a trustworthy CAS baseline) ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_CurrentSequenceZero_ReturnsSequenceChanged_NoMutation()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.SequenceNumber = 0;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.SequenceChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
    }

    // ==== TEXT_VALIDATION ====

    // ---- 4. embedded NUL rejection ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_EmbeddedNul_ReturnsInvalidText_NoNativeMutation()
    {
        var (monitor, _, textNative) = CreateStarted();

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello\0world").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.InvalidText, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Empty(textNative.CallLog);
    }

    // ---- 5. empty string is allowed ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_EmptyString_WritesSuccessfully()
    {
        var (monitor, _, _) = CreateStarted();

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.True(result.ClipboardMutated);
    }

    // ---- 6/7/8. Korean, multiline (CRLF/LF), emoji round-trip exactly, verified via a genuine
    // follow-up read through the existing (already-tested) read path ----
    [Theory]
    [InlineData("안녕하세요, 여러 줄\nsecond line\r\nthird line")]
    [InlineData("emoji \U0001F600 test")]
    [InlineData("   whitespace only   ")]
    public async Task WriteTextIfSequenceMatchesAsync_SuccessfulWrite_RoundTripsExactText(string text)
    {
        var (monitor, _, _) = CreateStarted();

        var writeResult = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, text).WaitAsync(WaitTimeout);
        var readBack = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, writeResult.Outcome);
        Assert.Equal(ClipboardReadOutcome.Success, readBack.Outcome);
        Assert.Equal(text, readBack.Snapshot!.Value.Text);
    }

    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_ZeroWidthCharacter_IsPreserved()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var text = "a" + zeroWidthSpace + "b";
        var (monitor, _, _) = CreateStarted();

        var writeResult = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, text).WaitAsync(WaitTimeout);
        var readBack = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, writeResult.Outcome);
        Assert.Equal(text, readBack.Snapshot!.Value.Text);
    }

    // ---- 9. checked size computation: a large-but-legitimate size still round-trips correctly.
    // A genuine overflow-triggering string (~1B+ chars, ~2GB) is not exercised here -- allocating
    // one would make this test itself the thing that exhausts memory/time; the `checked` block's
    // presence in WriteTextIfSequenceMatchesAsync is the structural defense, verified by code
    // review rather than by an impractical mega-allocation test. ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_LargeText_RoundTripsCorrectly()
    {
        var (monitor, _, _) = CreateStarted();
        var text = new string('x', 50_000);

        var writeResult = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, text).WaitAsync(WaitTimeout);
        var readBack = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, writeResult.Outcome);
        Assert.Equal(text, readBack.Snapshot!.Value.Text);
    }

    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_NullText_ThrowsArgumentNullException()
    {
        var monitor = new ClipboardChangeMonitor(new FakeClipboardMonitorNative(), textNative: new FakeClipboardTextNative());
        // The exception is actually thrown synchronously, before any Task is even created --
        // ThrowsAsync still handles that correctly (it awaits whatever Task the delegate
        // produces, or the exception that prevented one from being produced at all).
        await Assert.ThrowsAsync<ArgumentNullException>(() => monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, null!));
    }

    // ==== HGLOBAL_OWNERSHIP / preparation ordering ====

    // ---- 10. HGLOBAL is prepared (alloc/lock/unlock) BEFORE OpenClipboard is ever called ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_PreparesHGlobal_BeforeOpeningClipboard()
    {
        var (monitor, _, textNative) = CreateStarted();

        await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        int allocIndex = textNative.CallLog.IndexOf(nameof(FakeClipboardTextNative.TryGlobalAlloc));
        int unlockIndex = textNative.CallLog.IndexOf(nameof(FakeClipboardTextNative.TryGlobalUnlock));
        int openIndex = textNative.CallLog.IndexOf(nameof(FakeClipboardTextNative.OpenClipboard));

        Assert.True(allocIndex >= 0 && unlockIndex >= 0 && openIndex >= 0);
        Assert.True(allocIndex < openIndex);
        Assert.True(unlockIndex < openIndex);
    }

    // ---- 11. GlobalAlloc failure -> NativeFailure, no clipboard touched at all ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_GlobalAllocFails_ReturnsNativeFailure_NoClipboardTouched()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.GlobalAllocResult = false;
        textNative.GlobalAllocError = 8;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Equal(8, result.Win32Error);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
    }

    // ---- 12. GlobalLock failure during preparation -> NativeFailure, allocation freed ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_GlobalLockFailsDuringPrepare_ReturnsNativeFailure_FreesAllocation()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.GlobalLockResult = false;
        textNative.GlobalLockError = 487;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Equal(487, result.Win32Error);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog);
    }

    // ---- 13. GlobalUnlock failure during preparation -> NativeFailure, allocation freed ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_GlobalUnlockFailsDuringPrepare_ReturnsNativeFailure_FreesAllocation()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.GlobalUnlockBehavior = FakeClipboardTextNative.UnlockBehavior.Failed;
        textNative.GlobalUnlockError = 6;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Equal(6, result.Win32Error);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog);
    }

    // ==== pre-mutation OpenClipboard / Busy ====

    // ---- 14. OpenClipboard Busy (pre-mutation) -> Busy, not mutated, allocation freed ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_OpenClipboardFails_ReturnsBusy_NotMutated_FreesAllocation()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.OpenClipboardResult = false;
        textNative.OpenClipboardError = 5;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Busy, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Equal(5, result.Win32Error);
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog);
    }

    // ==== DESTRUCTIVE_BOUNDARY / EmptyClipboard / Set ====

    // ---- 15. EmptyClipboard failure -> NativeFailure, not mutated, allocation freed, Set never
    // attempted ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_EmptyClipboardFails_ReturnsNativeFailure_NotMutated()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.EmptyClipboardResult = false;
        textNative.EmptyClipboardError = 5;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Equal(5, result.Win32Error);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TrySetClipboardData), textNative.CallLog);
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog);
    }

    // ---- 16. Empty success + Set failure -> NativeFailure, ClipboardMutated=true, hGlobal freed
    // (still PRIVON-owned) ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_EmptySucceeds_SetFails_ReturnsNativeFailure_MutatedTrue_FreesHandle()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetClipboardDataResult = false;
        textNative.SetClipboardDataError = 8;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(8, result.Win32Error);
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog);
    }

    // ---- 17. Set success -> ownership transfer: the Set handle is NEVER freed ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_SetSucceeds_NeverFreesTheSetHandle()
    {
        var (monitor, _, textNative) = CreateStarted();

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.NotNull(textNative.SetHandle);
        Assert.DoesNotContain(textNative.SetHandle!.Value, textNative.FreedHandleLog);
    }

    // ==== GLOBALFREE_LEAK_VISIBILITY ====

    // ---- 18. GlobalFree failure (during pre-Set-success cleanup) -> NativeFailure, carrying the
    // FREE's own error, never silently hidden behind the primary failure ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_GlobalFreeFailsAfterSetFailure_ReturnsNativeFailure_WithFreeError()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetClipboardDataResult = false;
        textNative.SetClipboardDataError = 8;
        textNative.GlobalFreeResult = false;
        textNative.GlobalFreeError = 9;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.True(result.ClipboardMutated); // Empty already succeeded
        Assert.Equal(9, result.Win32Error); // the FREE's own error, not Set's (8)
    }

    // ---- 19. Same principle applies to a sequence-mismatch cleanup free failure -- still
    // surfaces as NativeFailure with the free's own error, ClipboardMutated stays false ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_GlobalFreeFailsAfterSequenceMismatch_ReturnsNativeFailure_NotMutated()
    {
        var (monitor, native, textNative) = CreateStarted();
        textNative.GlobalFreeResult = false;
        textNative.GlobalFreeError = 11;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(expectedSequence: 999, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Equal(11, result.Win32Error);
    }

    // ==== CLOSECLIPBOARD_FAILURE / precedence (mutation bracket) ====

    // ---- 20. Case A: failure before Empty (sequence mismatch) + Close failure -> NativeFailure,
    // ClipboardMutated=false ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_SequenceMismatch_AndCloseFails_ReturnsNativeFailure_NotMutated()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.SequenceNumber = DefaultSequence;
        textNative.CloseClipboardResult = false;
        textNative.CloseClipboardError = 6;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(expectedSequence: 999, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Equal(6, result.Win32Error);
    }

    // ---- 21. Case B: Empty success + Set failure + Close failure -> NativeFailure,
    // ClipboardMutated=true, hGlobal (still PRIVON-owned) already freed ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_SetFails_AndCloseFails_ReturnsNativeFailure_MutatedTrue_FreesHandle()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.SetClipboardDataResult = false;
        textNative.SetClipboardDataError = 8;
        textNative.CloseClipboardResult = false;
        textNative.CloseClipboardError = 6;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(6, result.Win32Error); // Close's error overrides Set's (8)
        Assert.Contains(textNative.AllocatedHandles[0], textNative.FreedHandleLog);
    }

    // ---- 22. Case C: Set success + Close failure -> NativeFailure, ClipboardMutated=true,
    // hGlobal is system-owned -- NEVER freed ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_SetSucceeds_AndCloseFails_ReturnsNativeFailure_MutatedTrue_NeverFreesHandle()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.CloseClipboardResult = false;
        textNative.CloseClipboardError = 6;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(6, result.Win32Error);
        Assert.NotNull(textNative.SetHandle);
        Assert.DoesNotContain(textNative.SetHandle!.Value, textNative.FreedHandleLog);
    }

    // ==== WRITE_SEQUENCE_CAPTURE / VerificationUnavailable ====

    // ---- 23. writeSequence == 0 -> VerificationUnavailable, ClipboardMutated=true, no
    // verification reopen ever attempted ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_WriteSequenceZero_ReturnsVerificationUnavailable_MutatedTrue()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.QueueSequenceNumbers(DefaultSequence, 0); // gate check matches, post-Set capture is 0

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.VerificationUnavailable, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(1, textNative.CallLog.Count(n => n == nameof(FakeClipboardTextNative.OpenClipboard)));
    }

    // ==== FINAL_VERIFICATION_SEQUENCE (Phase 3C STEP42 correction) ====
    // A real Windows + real ChatGPT manual trace (attempt 10, "문의 전화 010-...") proved the
    // PRE-STEP42 invariant -- "the verification sequence V must equal the post-Set capture W, or
    // the write is Superseded" -- is invalid in production: W=3847, V=3850, yet the clipboard's
    // actual content at V was still exactly the intended protected replacement, and a human
    // tester visually observed the RAW (unprotected) value because the write was wrongly reported
    // as Superseded and no self-write marker was installed. This section's tests reproduce and
    // fix exactly that defect.

    // ---- STEP42 regression test A: W != V, but the verification-reopen's read-back text exactly
    // matches the intended replacement -- MUST be Success, carrying V (not W) as ResultSequence.
    // Before this correction this scenario incorrectly returned Superseded (see this test's own
    // previous name/assertions in version control), even though the write's own content was never
    // actually replaced by anything else. ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_VerificationSequenceDiffersFromPostSet_ButContentMatchesExactly_ReturnsSuccess_UsesVerificationSequence()
    {
        var (monitor, native, _) = CreateStarted();
        // gate=5 (CAS matches), post-Set capture W=5, verification reopen V=42 -- a genuinely
        // different reliable sequence, exactly like the real trace's 3847 -> 3850 transition. No
        // OverridePayloadOnSet/CorruptPayloadOnSet is configured, so the fake's read-back
        // genuinely reproduces the real bytes that were actually written ("hello") -- proving the
        // content itself never changed, only the sequence counter moved on.
        native.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 42);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(42u, result.ResultSequence); // V, not W (5)
        Assert.Null(result.Win32Error);
    }

    // ---- STEP42 regression test D: the self-write suppression marker installed by the write
    // above is V (42), not W (5) -- a subsequent notification carrying V is suppressed as our own
    // echo, while one carrying the OLD post-Set value W is treated as a genuinely different
    // (external) change, proving the marker really is keyed off V now. ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_VerificationSequenceDiffersFromPostSet_InstallsMarkerAtVerificationSequence_NotPostSet()
    {
        var (monitor, native, _) = CreateStarted();
        native.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 42);
        var received = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => received.Add(n);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.Equal(42u, result.ResultSequence);

        // A notification carrying V (42, the actual verified sequence) must be suppressed as our
        // own self-write echo -- this is the exact real-environment behavior the manual trace
        // showed was MISSING before this correction (App generation must not advance, no extra
        // App-level attempt is triggered, because Changed is never invoked at all).
        native.SequenceNumber = 42;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout); // FIFO sync barrier
        Assert.Empty(received);

        // A notification carrying the OLD post-Set value W (5) is NOT our verified sequence and
        // must be delivered normally.
        native.SequenceNumber = DefaultSequence;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Single(received);
        Assert.Equal(DefaultSequence, received[0].SequenceNumber);
    }

    // ---- STEP42 regression test C: a GENUINE external replacement must still fail -- sequence
    // transitions AND the read-back content genuinely differs from what was intended. Content
    // coherence, not sequence equality, is what determines success; this proves the fix does not
    // turn every sequence transition into a false Success. ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_GenuineExternalReplacement_SequenceChanges_AndContentDiffers_ReturnsSuperseded_NeverCarriesResultSequence()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 42);
        textNative.OverridePayloadOnSet = "someone else's content"; // genuinely different, valid text

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Superseded, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Null(result.ResultSequence);
        Assert.Null(result.Win32Error);
    }

    // ---- STEP42 regression test G: a verification sequence of 0 (unreliable) can never be the
    // basis of Success or the self-write marker, regardless of what the read-back would otherwise
    // show -- mirrors the "0 is never a trustworthy compare baseline" rule applied everywhere else
    // in this type. ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_VerificationSequenceZero_ReturnsVerificationUnavailable_NeverCarriesResultSequence()
    {
        var (monitor, native, _) = CreateStarted();
        native.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 0);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.VerificationUnavailable, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Null(result.ResultSequence);
    }

    // ---- STEP42 regression tests H/I: format-missing and malformed-payload verification
    // failures remain NativeFailure regardless of whether V differs from W -- content coherence
    // is checked only once a valid string is actually available to compare. ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_VerificationSequenceDiffers_AndFormatMissing_ReturnsNativeFailure_NeverSuperseded()
    {
        var (monitor, native, _) = CreateStarted();
        native.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 42);
        native.UnicodeTextAvailable = false;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.NotEqual(ClipboardWriteOutcome.Superseded, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(42u, result.ResultSequence); // V was still confirmed reliable
    }

    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_VerificationSequenceDiffers_AndPayloadMalformed_ReturnsNativeFailure_NeverSuperseded()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 42);
        textNative.CorruptPayloadOnSet = true;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.NotEqual(ClipboardWriteOutcome.Superseded, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(42u, result.ResultSequence);
    }

    // ---- STEP42 regression test K: even an exact-match read-back must still fail (and never
    // install the self-write marker) if the verification bracket's OWN CloseClipboard fails --
    // mirrors the mutation bracket's existing FAILURE_PRECEDENCE tests, applied to the
    // verification bracket instead. ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_ExactMatch_ButVerificationCloseFails_ReturnsNativeFailure_NoMarkerInstalled()
    {
        var (monitor, native, textNative) = CreateStarted();
        // First CloseClipboard call (mutation bracket) succeeds; second (verification bracket) fails.
        textNative.QueueCloseClipboardResults((true, 0), (false, 6));

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(6, result.Win32Error);

        // No marker installed -- a notification carrying the same sequence is delivered normally,
        // not suppressed as a self-write echo.
        var received = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => received.Add(n);
        native.SequenceNumber = DefaultSequence;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Single(received);
    }

    // ==== READBACK_VERIFICATION ====

    // ---- 25. verification reopen Busy -> Busy, ClipboardMutated=true (distinct from
    // pre-mutation Busy, which is ClipboardMutated=false -- see test 14), no internal retry ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_VerificationReopenFails_ReturnsBusy_MutatedTrue()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.QueueOpenClipboardResults((true, 0), (false, 5));

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Busy, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(5, result.Win32Error);
    }

    // ==== WRITE_READBACK_MISMATCH_SEMANTICS (Phase 3A.4 STEP4.1) ====
    // ReadBackMismatch is reserved EXCLUSIVELY for "a valid CF_UNICODETEXT payload was actually
    // parsed, and it is ordinally different from what was written." Format-missing and
    // malformed/unparseable payloads never produced a real string to compare, so they are
    // NativeFailure instead -- but ResultSequence is still the confirmed writeSequence in both
    // cases (the sequence match WAS confirmed before either failure was discovered).

    // ---- 26 / section 6 item 1. matching sequence + missing CF_UNICODETEXT -> NativeFailure,
    // NOT ReadBackMismatch. No native call actually failed, so Win32Error is null. ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_VerificationFormatMissing_ReturnsNativeFailure_NotReadBackMismatch()
    {
        var (monitor, native, _) = CreateStarted();
        native.UnicodeTextAvailable = false; // mutation body never checks this -- only verification does

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.NotEqual(ClipboardWriteOutcome.ReadBackMismatch, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Null(result.Win32Error);
        Assert.Equal(DefaultSequence, result.ResultSequence);
    }

    // ---- 27 / section 6 item 2. matching sequence + malformed global data (odd byte length) ->
    // NativeFailure, NOT ReadBackMismatch ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_VerificationReadsMalformedData_ReturnsNativeFailure_NotReadBackMismatch()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.CorruptPayloadOnSet = true;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.NotEqual(ClipboardWriteOutcome.ReadBackMismatch, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Null(result.Win32Error);
        Assert.Equal(DefaultSequence, result.ResultSequence);
    }

    // ---- 28 / section 6 item 3. matching sequence + successfully parsed but genuinely different
    // text -> ReadBackMismatch, and ResultSequence is still the confirmed writeSequence ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_VerificationTextDiffers_ReturnsReadBackMismatch_MutatedTrue()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.OverridePayloadOnSet = "unexpected content";

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.ReadBackMismatch, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(DefaultSequence, result.ResultSequence);
    }

    // ---- 29 / section 6 item 5. read-back comparison is StringComparison.Ordinal, not
    // normalization-aware: canonically-equivalent but ordinal-different strings (NFC vs NFD) must
    // mismatch -- confirming no normalization ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_ReadBackComparison_IsOrdinal_NotNormalizationAware()
    {
        var (monitor, _, textNative) = CreateStarted();
        const string nfc = "café";       // U+00E9 (single precomposed codepoint)
        const string nfd = "café"; // 'e' + U+0301 combining acute -- canonically equivalent
        textNative.OverridePayloadOnSet = nfd;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, nfc).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.ReadBackMismatch, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(DefaultSequence, result.ResultSequence);
    }

    // ---- 30 / section 6 item 4. matching sequence + exact ordinal text -> Success, carrying the
    // write's own confirmed sequence ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_ExactSuccess_ReportsConfirmedSequence()
    {
        var (monitor, native, _) = CreateStarted();
        native.SequenceNumber = 77;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(77, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(77u, result.ResultSequence);
        Assert.Null(result.Win32Error);
    }

    // Note: "Superseded never carries a ResultSequence" is now covered above by
    // WriteTextIfSequenceMatchesAsync_GenuineExternalReplacement_..._NeverCarriesResultSequence
    // (Phase 3C STEP42) -- a plain sequence transition with unmodified content, which used to
    // produce Superseded here, now correctly produces Success (see that section's own tests), so
    // this scenario needed a genuinely different read-back payload to still reach Superseded at all.

    // ==== WORK_MARSHALING / PostMessage failure ====

    // ---- 31. PostMessageW failure -> Task completes promptly (never stranded), no native
    // mutation at all ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_PostMessageFails_TaskCompletesPromptly_NoMutation()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.PostWriteWorkSignalResult = false;
        native.PostWriteWorkSignalError = 87;

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.Equal(87, result.Win32Error);
        Assert.False(result.ClipboardMutated);
        Assert.Empty(textNative.CallLog);
    }

    // ---- 32. A stale (already-completed) entry from a failed PostMessage must never trigger a
    // real mutation when a LATER, unrelated WriteWork wakeup dequeues it -- the live request that
    // actually arrives must still be serviced correctly. ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_PostMessageFails_StaleEntryNeverMutates_LaterRequestStillWorks()
    {
        var (monitor, native, textNative) = CreateStarted();

        native.PostWriteWorkSignalResult = false;
        native.PostWriteWorkSignalError = 87;
        var failedRequest = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "must never be written").WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.NativeFailure, failedRequest.Outcome);
        Assert.Empty(textNative.CallLog);

        native.PostWriteWorkSignalResult = true;
        var liveRequest = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "second attempt").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, liveRequest.Outcome);
    }

    // ==== SHUTDOWN_RACE ====

    // ---- 33. A write queued exactly as Stop() is invoked always completes as NotRunning, never
    // permanently pending, and never mutates the clipboard -- mirrors the read path's identical
    // ForceShutdown-before-enqueue race proof. ----
    [Fact]
    public async Task WriteTextIfSequenceMatchesAsync_RacingWithShutdown_AlwaysCompletes_AsNotRunning_NoMutation()
    {
        var (monitor, native, textNative) = CreateStarted();

        native.ForceShutdown();
        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);

        Assert.Equal(ClipboardWriteOutcome.NotRunning, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Empty(textNative.CallLog);

        monitor.Stop(); // owner thread already exited via ForceShutdown -- must still be a safe no-op
    }

    // ==== SELF_WRITE_SUPPRESSION_MARKER_LIFECYCLE (Phase 3A.4 STEP4.1, corrected) ====
    // The marker is set only by a fully-verified successful write (item 1) and is NOT consumed by
    // its first suppression -- it stays armed until a DIFFERENT reliable sequence proves the
    // clipboard genuinely changed again. All tests below use a follow-up ReadTextSnapshotAsync
    // call as a deterministic synchronization barrier: the owner thread's message loop is
    // strictly FIFO, so a ReadWork posted after a RaiseClipboardUpdate() call is only processed
    // once that notification has already been fully handled. No timing-dependent waits anywhere.

    // ---- items 1-4: a fully successful write establishes the marker, and EVERY notification
    // carrying that same reliable sequence is suppressed -- not just the first one. Multiple
    // WM_CLIPBOARDUPDATE messages can legitimately already be queued for a single
    // EmptyClipboard/SetClipboardData pair and all observe the same final sequence once
    // processed; none of them may leak through. ----
    [Fact]
    public async Task SuccessfulWrite_SelfEcho_IsSuppressed_RepeatedlyAcrossMultipleNotifications()
    {
        var (monitor, native, _) = CreateStarted();
        var received = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => received.Add(n);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        uint writeSequence = result.ResultSequence!.Value;
        native.SequenceNumber = writeSequence;

        // 1st matching notification -- suppressed.
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        Assert.Empty(received);

        // 2nd matching notification, same sequence -- ALSO suppressed (marker not consumed).
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        Assert.Empty(received);

        // 3rd matching notification, same sequence -- ALSO suppressed.
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Empty(received);
    }

    // ---- items 5-6: a genuinely different reliable sequence is delivered normally and retires
    // the marker -- and once retired, the ORIGINAL write sequence is no longer treated as an
    // active self marker (a later notification carrying that same old sequence is now delivered
    // too, not suppressed). ----
    [Fact]
    public async Task ExternalChange_DifferentSequence_IsDelivered_AndRetiresMarker_SoOldSequenceIsNoLongerSuppressed()
    {
        var (monitor, native, _) = CreateStarted();
        var received = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => received.Add(n);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        uint writeSequence = result.ResultSequence!.Value;
        uint externalSequence = writeSequence + 1;

        // A different reliable sequence -- delivered, and retires the marker.
        native.SequenceNumber = externalSequence;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        Assert.Single(received);
        Assert.Equal(externalSequence, received[0].SequenceNumber);

        // The marker is now retired -- a LATER notification carrying the ORIGINAL write sequence
        // must no longer be treated as a self-echo (it is structurally impossible for a real OS
        // sequence to legitimately go backwards, but this proves the marker itself, not sequence
        // ordering, is what's being checked).
        native.SequenceNumber = writeSequence;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(2, received.Count);
        Assert.Equal(writeSequence, received[1].SequenceNumber);
    }

    // ---- items 7-8: a sequence-0 notification after a successful write is always delivered (0
    // is never trusted for this comparison) AND does NOT retire the marker -- a subsequent
    // notification carrying the original write's reliable sequence is still suppressed
    // afterward. ----
    [Fact]
    public async Task ExternalChange_SequenceZero_IsDelivered_ButDoesNotRetireMarker_SelfSequenceStillSuppressedAfterward()
    {
        var (monitor, native, _) = CreateStarted();
        var received = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => received.Add(n);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        uint writeSequence = result.ResultSequence!.Value;

        // Item 7: sequence 0 -- delivered, marker untouched.
        native.SequenceNumber = 0;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        Assert.Single(received);
        Assert.False(received[0].HasReliableSequence);

        // Item 8: the ORIGINAL self sequence, delivered right after the zero notification -- must
        // still be suppressed (proves the zero notification did not retire the marker).
        native.SequenceNumber = writeSequence;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        Assert.Single(received); // unchanged -- the self-sequence notification was suppressed

        // Sanity: the subscription itself is still live -- a genuinely different sequence sent
        // afterward is delivered normally.
        native.SequenceNumber = writeSequence + 1;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(2, received.Count);
    }

    // ==== Result-model structural invariants ====

    [Fact]
    public void ClipboardWriteResult_Failure_RejectsSuccessOutcome()
    {
        Assert.Throws<ArgumentException>(() => ClipboardWriteResult.Failure(ClipboardWriteOutcome.Success, mutated: true));
    }

    // As of Phase 3A.4 STEP4.1, ResultSequence is no longer exclusively a Success-only field --
    // it defaults to null unless explicitly provided, which is what every construction site
    // other than VerifyWrite's own narrow verification-failure cases relies on.
    [Fact]
    public void ClipboardWriteResult_Failure_ResultSequenceDefaultsToNull_UnlessExplicitlyProvided()
    {
        foreach (var outcome in Enum.GetValues<ClipboardWriteOutcome>().Where(o => o != ClipboardWriteOutcome.Success))
        {
            var result = ClipboardWriteResult.Failure(outcome, mutated: true, win32Error: 42);
            Assert.Null(result.ResultSequence);
        }
    }

    [Fact]
    public void ClipboardWriteResult_Failure_CanCarryAnExplicitResultSequence()
    {
        var result = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true, resultSequence: 7u);
        Assert.Equal(7u, result.ResultSequence);
    }

    // GLOBALFREE_LEAK_VISIBILITY: "별도 public leak flag 추가하지 않는다" -- a leak is only ever
    // visible via Outcome==NativeFailure, never via a separate dedicated field.
    [Fact]
    public void ClipboardWriteResult_HasNoSeparateLeakFlag()
    {
        var members = typeof(ClipboardWriteResult).GetMembers().Select(m => m.Name);
        Assert.DoesNotContain(members, name => name.Contains("Leak", StringComparison.OrdinalIgnoreCase));
    }
}
