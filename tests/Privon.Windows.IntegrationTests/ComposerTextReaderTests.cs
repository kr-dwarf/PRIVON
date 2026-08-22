using System.Diagnostics;
using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3C STEP29 -- ComposerTextReader regression: composer identity (FocusedElement-only, PID +
// "ProseMirror-focused" class + ControlType.Edit), CHECK1/CHECK2 foreground stability, exact-text/
// empty-text handling, failure mapping, dedicated-MTA-worker threading, and sensitive-surface
// hardening. All tests use FakeComposerTextSource + FakeForegroundTargetSource (synthetic,
// OS-free) unless explicitly marked as a real-Windows structural smoke test. No mocking framework.
public class ComposerTextReaderTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    // Matches FakeForegroundTargetSource's own defaults (PID 4242, "ChatGPT") and
    // FakeComposerTextSource's own default snapshot (PID 4242, class contains
    // "ProseMirror-focused", ControlType.Edit) -- a guarded call using this target succeeds "out
    // of the box" unless a test deliberately reconfigures one of the fakes.
    private static readonly ForegroundTargetSnapshot DefaultExpectedTarget =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");

    private static (ComposerTextReader Reader, FakeComposerTextSource Source, FakeForegroundTargetSource ForegroundSource) CreateStarted(
        TimeSpan? stopTimeoutOverride = null)
    {
        var source = new FakeComposerTextSource();
        var foregroundSource = new FakeForegroundTargetSource();
        var reader = new ComposerTextReader(source, foregroundSource, stopTimeoutOverride);
        reader.Start();
        return (reader, source, foregroundSource);
    }

    // ==================================================================
    // V. RESULT / API
    // ==================================================================

    // ---- 1. invalid expected target -> InvalidExpectedTarget, zero native calls ----
    [Theory]
    [InlineData(false, 4242, "ChatGPT")] // unresolved
    [InlineData(true, 0, "ChatGPT")] // zero PID
    [InlineData(true, 4242, null)] // null process name
    [InlineData(true, 4242, "")] // empty process name
    [InlineData(true, 4242, "   ")] // whitespace process name
    public async Task InvalidExpectedTarget_ReturnsInvalidExpectedTarget_ZeroNativeCalls(
        bool isResolved, uint processId, string? processName)
    {
        var (reader, source, foregroundSource) = CreateStarted();
        var invalidTarget = new ForegroundTargetSnapshot(isResolved, processId, processName);

        var result = await reader.ReadFocusedComposerTextAsync(invalidTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.InvalidExpectedTarget, result.Outcome);
        Assert.Null(result.Text);
        Assert.Equal(0, source.QueryCallCount);
        Assert.Empty(foregroundSource.CallLog);
    }

    // ---- 2. CHECK1 mismatch -> TargetChanged, no UIA query ----
    [Fact]
    public async Task Check1Mismatch_ReturnsTargetChanged_NoUiaQuery()
    {
        var (reader, source, foregroundSource) = CreateStarted();
        foregroundSource.WindowThreadProcessIdValue = 9999;

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.TargetChanged, result.Outcome);
        Assert.Equal(0, source.QueryCallCount);
    }

    // ---- CHECK1 unavailable (no foreground window at all) -> TargetUnavailable ----
    [Fact]
    public async Task Check1Unavailable_ReturnsTargetUnavailable_NoUiaQuery()
    {
        var (reader, source, foregroundSource) = CreateStarted();
        foregroundSource.ForegroundWindowResult = 0;

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.TargetUnavailable, result.Outcome);
        Assert.Equal(0, source.QueryCallCount);
    }

    // ---- 3. focused UIA PID mismatch -> ComposerNotFocused ----
    [Fact]
    public async Task FocusedElementProcessIdMismatch_ReturnsComposerNotFocused()
    {
        var (reader, source, _) = CreateStarted();
        source.SnapshotToReturn = ComposerElementSnapshot.Resolved(
            processId: 9999, className: "ProseMirror ProseMirror-focused", isEditControlType: true, text: "hello");

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.ComposerNotFocused, result.Outcome);
    }

    // ---- 4. correct PID but class lacks "ProseMirror-focused" -> ComposerNotFocused ----
    [Theory]
    [InlineData("ProseMirror")] // the documented Phase0 bug -- exact match, missing "-focused"
    [InlineData("SomeOtherEditor")]
    [InlineData(null)]
    public async Task ClassNameDoesNotContainProseMirrorFocused_ReturnsComposerNotFocused(string? className)
    {
        var (reader, source, _) = CreateStarted();
        source.SnapshotToReturn = ComposerElementSnapshot.Resolved(
            processId: 4242, className: className, isEditControlType: true, text: "hello");

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.ComposerNotFocused, result.Outcome);
    }

    // ---- 5. correct PID/class but ControlType != Edit -> ComposerNotFocused ----
    [Fact]
    public async Task NotEditControlType_ReturnsComposerNotFocused()
    {
        var (reader, source, _) = CreateStarted();
        source.SnapshotToReturn = ComposerElementSnapshot.Resolved(
            processId: 4242, className: "ProseMirror ProseMirror-focused", isEditControlType: false, text: null);

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.ComposerNotFocused, result.Outcome);
    }

    // ---- no focused element at all -> ComposerNotFocused ----
    [Fact]
    public async Task NoFocusedElement_ReturnsComposerNotFocused()
    {
        var (reader, source, _) = CreateStarted();
        source.SnapshotToReturn = ComposerElementSnapshot.NoFocusedElement();

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.ComposerNotFocused, result.Outcome);
    }

    // ---- 6/7. all identity checks pass -> exact Success text ----
    [Theory]
    [InlineData("hello world")]
    [InlineData("여러 줄\n한글 텍스트\n둘째 줄")]
    [InlineData("이모지 테스트 🙂🎉")]
    [InlineData("zero​width​present")]
    public async Task AllIdentityChecksPass_ReturnsExactSuccessText(string text)
    {
        var (reader, source, _) = CreateStarted();
        source.SnapshotToReturn = ComposerElementSnapshot.Resolved(
            processId: 4242, className: "ProseMirror ProseMirror-focused", isEditControlType: true, text: text);

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.Success, result.Outcome);
        Assert.Equal(text, result.Text); // EXACT_TEXT_POLICY: no trim/normalize/case-fold
    }

    // ---- 9. neither ValuePattern nor TextPattern produced text -> TextUnavailable ----
    [Fact]
    public async Task NeitherPatternAvailable_ReturnsTextUnavailable()
    {
        var (reader, source, _) = CreateStarted();
        source.SnapshotToReturn = ComposerElementSnapshot.Resolved(
            processId: 4242, className: "ProseMirror ProseMirror-focused", isEditControlType: true, text: null);

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.TextUnavailable, result.Outcome);
        Assert.Null(result.Text);
    }

    // ---- 10. empty readable composer -> Success + "" (never ComposerNotFocused/TextUnavailable) ----
    [Fact]
    public async Task EmptyReadableComposer_ReturnsSuccessWithEmptyString()
    {
        var (reader, source, _) = CreateStarted();
        source.SnapshotToReturn = ComposerElementSnapshot.Resolved(
            processId: 4242, className: "ProseMirror ProseMirror-focused", isEditControlType: true, text: "");

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.Success, result.Outcome);
        Assert.Equal("", result.Text);
    }

    // ---- 14. CHECK2 mismatch after the UIA work -> TargetChanged, read text never accepted ----
    [Fact]
    public async Task Check2Mismatch_ReturnsTargetChanged_NotSuccess()
    {
        var (reader, source, foregroundSource) = CreateStarted();
        source.SnapshotToReturn = ComposerElementSnapshot.Resolved(
            processId: 4242, className: "ProseMirror ProseMirror-focused", isEditControlType: true, text: "sensitive raw text");
        // CHECK1 (capture attempt 1) matches; CHECK2 (capture attempt 2) changes.
        foregroundSource.ChangeAfterAttempt = 1;
        foregroundSource.WindowThreadProcessIdValueAfter = 9999;

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.TargetChanged, result.Outcome);
        Assert.Null(result.Text);
        Assert.Equal(2, foregroundSource.CaptureAttemptCount); // both CHECK1 and CHECK2 actually ran
        Assert.Equal(1, source.QueryCallCount); // the UIA query itself still only happens once
    }

    // ---- 15. an unexpected exception from the source -> AutomationFailure, not a thrown fault ----
    [Fact]
    public async Task UnexpectedSourceException_ReturnsAutomationFailure()
    {
        var (reader, source, _) = CreateStarted();
        source.ThrowOnQuery = new InvalidOperationException("synthetic unexpected UIA failure");

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.AutomationFailure, result.Outcome);
        Assert.Null(result.Text);
    }

    // ---- an exception does not kill the worker -- a later request still succeeds ----
    [Fact]
    public async Task WorkerSurvivesUnexpectedException_LaterRequestStillSucceeds()
    {
        var (reader, source, _) = CreateStarted();
        source.ThrowOnQuery = new InvalidOperationException("synthetic unexpected UIA failure");

        var first = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        Assert.Equal(ComposerReadOutcome.AutomationFailure, first.Outcome);

        source.ThrowOnQuery = null;
        source.SnapshotToReturn = ComposerElementSnapshot.Resolved(
            processId: 4242, className: "ProseMirror ProseMirror-focused", isEditControlType: true, text: "recovered");
        var second = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.Success, second.Outcome);
        Assert.Equal("recovered", second.Text);
    }

    // ---- reading before Start -> deterministic NotRunning ----
    [Fact]
    public async Task ReadBeforeStart_ReturnsNotRunning()
    {
        var source = new FakeComposerTextSource();
        var foregroundSource = new FakeForegroundTargetSource();
        var reader = new ComposerTextReader(source, foregroundSource);

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);

        Assert.Equal(ComposerReadOutcome.NotRunning, result.Outcome);
        Assert.Equal(0, source.QueryCallCount);
    }

    // ---- reading after Stop -> deterministic NotRunning ----
    [Fact]
    public async Task ReadAfterStop_ReturnsNotRunning()
    {
        var (reader, source, _) = CreateStarted();
        reader.Stop();

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);

        Assert.Equal(ComposerReadOutcome.NotRunning, result.Outcome);
    }

    // ==================================================================
    // S. START / STOP / DISPOSE
    // ==================================================================

    [Fact]
    public void SecondStart_Throws()
    {
        var (reader, _, _) = CreateStarted();

        Assert.Throws<InvalidOperationException>(() => reader.Start());

        reader.Stop();
    }

    [Fact]
    public void Stop_BeforeStart_IsSafeNoOp()
    {
        var reader = new ComposerTextReader(new FakeComposerTextSource(), new FakeForegroundTargetSource());
        reader.Stop();
        reader.Stop();
    }

    [Fact]
    public void Stop_CalledTwice_IsIdempotent()
    {
        var (reader, _, _) = CreateStarted();
        reader.Stop();
        reader.Stop();
    }

    [Fact]
    public void Dispose_BeforeStart_IsSafe()
    {
        using var reader = new ComposerTextReader(new FakeComposerTextSource(), new FakeForegroundTargetSource());
    }

    [Fact]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        var (reader, _, _) = CreateStarted();
        reader.Dispose();
        reader.Dispose();
    }

    [Fact]
    public void Dispose_AfterStop_DoesNotDoubleStop()
    {
        var (reader, _, _) = CreateStarted();
        reader.Stop();
        reader.Dispose();
    }

    // ---- Stop that does not exit in time throws deterministically (mirrors
    // ClipboardChangeMonitor.Stop's own established precedent) ----
    [Fact]
    public void Stop_WorkerDoesNotExitInTime_ThrowsDeterministically()
    {
        var foregroundSource = new FakeForegroundTargetSource();
        // A tiny stop timeout combined with a query that blocks well past it deterministically
        // reproduces a stuck worker without any real hang or sleep-based flakiness. `entered` is
        // set by the worker the instant it is actually inside the blocking call -- waiting on it
        // (rather than calling Stop() immediately after enqueueing) is what makes this
        // deterministic: without it, Stop() could race ahead of the worker ever dequeuing the
        // request at all, in which case the worker's own _running check (Phase 3C STEP29's
        // WorkerThreadMain, matching ClipboardChangeMonitor's own shutdown-drain precedent) would
        // discard the request as NotRunning instead of ever reaching the blocking call -- making
        // Stop() succeed immediately rather than time out.
        var entered = new ManualResetEventSlim(initialState: false);
        var blockGate = new ManualResetEventSlim(initialState: false);
        var reader = new ComposerTextReader(new BlockingComposerTextSource(entered, blockGate), foregroundSource, stopTimeoutOverride: TimeSpan.FromMilliseconds(50));
        reader.Start();

        var pending = reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget);
        Assert.True(entered.Wait(WaitTimeout));

        Assert.Throws<InvalidOperationException>(() => reader.Stop());

        // Release the blocked worker so the process can clean up (and the pending Task eventually completes).
        blockGate.Set();
    }

    private sealed class BlockingComposerTextSource(ManualResetEventSlim entered, ManualResetEventSlim gate) : IComposerTextSource
    {
        public ComposerElementSnapshot QueryFocusedElement()
        {
            entered.Set();
            gate.Wait();
            return ComposerElementSnapshot.Resolved(4242, "ProseMirror ProseMirror-focused", true, "x");
        }
    }

    // ==================================================================
    // W. THREADING
    // ==================================================================

    // ---- 1/3/4. UIA source execution occurs on ONE dedicated worker, repeatedly, serializing
    // concurrent callers ----
    [Fact]
    public async Task RepeatedAndConcurrentReads_AllExecuteOnTheSameSingleWorkerThread()
    {
        var (reader, source, _) = CreateStarted();
        var observedThreadIds = new System.Collections.Concurrent.ConcurrentBag<int>();

        var recordingSource = new RecordingComposerTextSource(observedThreadIds);
        var reader2 = new ComposerTextReader(recordingSource, new FakeForegroundTargetSource());
        reader2.Start();

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => reader2.ReadFocusedComposerTextAsync(DefaultExpectedTarget))
            .ToArray();
        await Task.WhenAll(tasks).WaitAsync(WaitTimeout);
        reader2.Stop();
        reader.Stop();

        Assert.Equal(8, recordingSource.CallCount);
        Assert.Single(observedThreadIds.Distinct()); // exactly one worker thread serviced everything
    }

    private sealed class RecordingComposerTextSource(System.Collections.Concurrent.ConcurrentBag<int> threadIds) : IComposerTextSource
    {
        public int CallCount;

        public ComposerElementSnapshot QueryFocusedElement()
        {
            threadIds.Add(Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref CallCount);
            return ComposerElementSnapshot.Resolved(4242, "ProseMirror ProseMirror-focused", true, "x");
        }
    }

    // ---- 2. worker apartment state is MTA ----
    [Fact]
    public async Task WorkerThread_ApartmentStateIsMta()
    {
        ApartmentState observed = ApartmentState.Unknown;
        var recordingSource = new ApartmentRecordingComposerTextSource(() => observed = Thread.CurrentThread.GetApartmentState());
        var reader = new ComposerTextReader(recordingSource, new FakeForegroundTargetSource());
        reader.Start();

        await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ApartmentState.MTA, observed);
    }

    private sealed class ApartmentRecordingComposerTextSource(Action record) : IComposerTextSource
    {
        public ComposerElementSnapshot QueryFocusedElement()
        {
            record();
            return ComposerElementSnapshot.Resolved(4242, "ProseMirror ProseMirror-focused", true, "x");
        }
    }

    // ---- 5. the caller thread never itself executes the source ----
    [Fact]
    public async Task CallerThread_NeverExecutesTheSource()
    {
        int callerThreadId = Environment.CurrentManagedThreadId;
        int? sourceThreadId = null;
        var recordingSource = new ApartmentRecordingComposerTextSource(() => sourceThreadId = Thread.CurrentThread.ManagedThreadId);
        var reader = new ComposerTextReader(recordingSource, new FakeForegroundTargetSource());
        reader.Start();

        await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.NotNull(sourceThreadId);
        Assert.NotEqual(callerThreadId, sourceThreadId!.Value);
    }

    // ---- 6. no Dispatcher/message-loop type is required anywhere in this capability ----
    [Fact]
    public void ComposerTextReader_HasNoDispatcherOrMessageLoopMember()
    {
        var members = typeof(ComposerTextReader)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(members, m => m.Name.Contains("Dispatcher", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, m => m.Name.Contains("Hwnd", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, m => m.Name.Contains("MessageLoop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, m => m.Name.Contains("WndProc", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 7. Stop prevents later requests from ever reaching the source ----
    [Fact]
    public async Task Stop_PreventsLaterRequestsFromReachingSource()
    {
        var (reader, source, _) = CreateStarted();
        reader.Stop();

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);

        Assert.Equal(ComposerReadOutcome.NotRunning, result.Outcome);
        Assert.Equal(0, source.QueryCallCount);
    }

    // ---- 8. Start throws after Dispose (repeated Start/Stop/Dispose follows the exact
    // single-use contract) ----
    [Fact]
    public void StartAfterDispose_Throws()
    {
        var (reader, _, _) = CreateStarted();
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.Start());
    }

    // ==================================================================
    // X. SENSITIVE SURFACE
    // ==================================================================

    // ---- result ToString excludes synthetic sentinel text ----
    [Fact]
    public async Task ResultToString_NeverExposesText()
    {
        const string sentinel = "RAW-COMPOSER-SENTINEL-482913";
        var (reader, source, _) = CreateStarted();
        source.SnapshotToReturn = ComposerElementSnapshot.Resolved(4242, "ProseMirror ProseMirror-focused", true, sentinel);

        var result = await reader.ReadFocusedComposerTextAsync(DefaultExpectedTarget).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(sentinel, result.Text); // sanity: the value really is present on the struct
        Assert.DoesNotContain(sentinel, result.ToString());
        Assert.DoesNotContain(sentinel, $"{result}");
        Assert.Equal($"ComposerTextReadResult {{ Outcome = Success }}", result.ToString());
    }

    // ---- reader has no string field retaining composer text ----
    [Fact]
    public void ComposerTextReader_HasNoStringField()
    {
        var fields = typeof(ComposerTextReader).GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
    }

    // ---- source has no persistent AutomationElement/pattern field (structurally: neither type
    // even declares such a field, since neither can hold cross-call UIA state at all) ----
    [Fact]
    public void ComposerTextSourceTypes_HaveNoPersistentUiaOrTextField()
    {
        foreach (var type in new[] { typeof(Win32ComposerTextSource), typeof(ComposerTextReader) })
        {
            var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.DoesNotContain(fields, f => f.FieldType.Namespace == "System.Windows.Automation");
            Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
        }
    }

    // ---- no DebuggerDisplay/DebuggerTypeProxy leaks ----
    [Theory]
    [InlineData(typeof(ComposerTextReadResult))]
    [InlineData(typeof(ComposerReadOutcome))]
    [InlineData(typeof(ComposerTextReader))]
    [InlineData(typeof(Win32ComposerTextSource))]
    [InlineData(typeof(ComposerElementSnapshot))]
    [InlineData(typeof(ComposerElementQueryStatus))]
    public void ComposerTypes_HaveNoDebuggerAttributes(Type type)
    {
        Assert.Empty(type.GetCustomAttributes(typeof(DebuggerDisplayAttribute), inherit: false));
        Assert.Empty(type.GetCustomAttributes(typeof(DebuggerTypeProxyAttribute), inherit: false));
    }

    // ---- no production source anywhere in this capability contains SetValue/SendInput/
    // RegisterHotKey/Send-button/mouse-shield code (Phase 3C STEP28's SPIKE_CODE_REUSE_BOUNDARY,
    // enforced structurally by member-name scan rather than trusted by convention alone) ----
    [Theory]
    [InlineData(typeof(ComposerTextReader))]
    [InlineData(typeof(Win32ComposerTextSource))]
    public void ComposerCapability_HasNoForbiddenSpikeOnlyMembers(Type type)
    {
        var forbidden = new[] { "SetValue", "SendInput", "RegisterHotKey", "UnregisterHotKey", "SendButton", "Shield", "Overlay", "Invoke" };
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // ==================================================================
    // Z. REAL-WINDOWS STRUCTURAL SMOKE (no ChatGPT dependency)
    // ==================================================================

    // Actual AutomationElement.FocusedElement query against whatever is genuinely focused in this
    // sandbox right now. Asserts only coherent, non-throwing structural behavior -- NEVER a
    // specific application, NEVER any text content. Mirrors
    // ForegroundTargetInspectorTests.RealWindows_Capture_ReturnsCoherentSnapshot_RegardlessOfWhatIsForeground's
    // own exact precedent. MANUAL_CHATGPT_COMPOSER_READ_SMOKE (real ChatGPT UIA-tree/class-string
    // compatibility) remains a separate, OPEN, manual-only requirement -- this test proves the
    // native call path itself does not crash, nothing about the actual ChatGPT app.
    [Fact]
    public void RealWindows_QueryFocusedElement_ReturnsCoherentSnapshot_RegardlessOfWhatIsFocused()
    {
        var source = new Win32ComposerTextSource();

        var snapshot = source.QueryFocusedElement();

        switch (snapshot.Status)
        {
            case ComposerElementQueryStatus.Resolved:
                Assert.NotEqual(0u, snapshot.ProcessId);
                break;
            case ComposerElementQueryStatus.NoFocusedElement:
            case ComposerElementQueryStatus.AutomationFailure:
                Assert.Equal(0u, snapshot.ProcessId);
                Assert.Null(snapshot.ClassName);
                Assert.Null(snapshot.Text);
                break;
            default:
                Assert.Fail($"Undefined ComposerElementQueryStatus: {snapshot.Status}");
                break;
        }
    }
}
