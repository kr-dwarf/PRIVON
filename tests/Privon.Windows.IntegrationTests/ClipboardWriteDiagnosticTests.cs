using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3C STEP41.2 -- Guarded Write Sequence Attribution Diagnostic regression. All tests use
// FakeClipboardMonitorNative + FakeClipboardTextNative (synthetic, OS-free). No real Windows
// clipboard content is ever mutated here -- an automated test exercising real EmptyClipboard/
// SetClipboardData against the real system clipboard would violate this project's own established
// WINDOWS_SMOKE_POLICY (see ClipboardChangeMonitorWindowsSmokeTests's own class doc: the one real-
// Win32 test in this project deliberately never calls OpenClipboard/SetClipboardData). Real
// sequence-number attribution around a real guarded write therefore remains manual-QA-only -- see
// this STEP's own report for the exact procedure.
public class ClipboardWriteDiagnosticTests
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

    // ==================================================================
    // A. NORMAL SUCCESSFUL GUARDED WRITE -- full sequence timeline
    // ==================================================================

    [Fact]
    public async Task SuccessfulWrite_RecordsFullTimeline_ExpectedThenCasThenPostSetThenVerificationThenReadBackThenCompleted()
    {
        var (monitor, native, _) = CreateStarted();
        native.SequenceNumber = 77;
        var diagnostics = new List<ClipboardWriteDiagnosticEvent>();
        monitor.WriteDiagnosticObserved += (_, e) => diagnostics.Add(e);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(77, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);

        var kinds = diagnostics.Select(e => e.Kind).ToList();
        Assert.Equal(
            [
                ClipboardWriteDiagnosticKind.WriteStarted,
                ClipboardWriteDiagnosticKind.CasSequenceObserved,
                ClipboardWriteDiagnosticKind.PostSetSequenceCaptured,
                ClipboardWriteDiagnosticKind.VerificationSequenceObserved,
                ClipboardWriteDiagnosticKind.ReadBackAttempted,
                ClipboardWriteDiagnosticKind.ReadBackExactMatch,
                ClipboardWriteDiagnosticKind.WriteCompleted,
            ],
            kinds);

        var started = diagnostics[0];
        Assert.Equal(77u, started.ExpectedSequence);

        var cas = diagnostics[1];
        Assert.Equal(77u, cas.ObservedSequence);
        Assert.True(cas.SequenceMatched);

        var postSet = diagnostics[2];
        Assert.Equal(77u, postSet.ObservedSequence);

        var verification = diagnostics[3];
        Assert.Equal(77u, verification.ObservedSequence);
        Assert.True(verification.SequenceMatched);

        var readBackMatch = diagnostics[5];
        Assert.True(readBackMatch.ReadBackExactMatch);

        var completed = diagnostics[6];
        Assert.Equal(ClipboardWriteOutcome.Success, completed.Outcome);
        Assert.True(completed.ClipboardMutated);

        // Every event for this one write shares the SAME WriteId.
        Assert.Single(diagnostics.Select(e => e.WriteId).Distinct());
    }

    // ==================================================================
    // B. CORRECTED MODEL (Phase 3C STEP42) -- PostSet W != verification V, but content still
    // matches exactly => Success, not Superseded. Reproduces the exact real-environment
    // attempt-10 trace (W=3847, V=3850, content still the intended protected replacement) that
    // this STEP's correction fixes -- see ClipboardChangeMonitor.VerifyWhileClipboardOpen's own
    // FINAL_VERIFICATION_SEQUENCE doc for the full analysis.
    // ==================================================================

    [Fact]
    public async Task WSequenceDiffersFromVerificationSequence_ButContentMatchesExactly_RecordsFullReadBackTimeline_Success_ReportsVerificationSequence()
    {
        var (monitor, native, _) = CreateStarted();
        // CAS gate matches (5), post-Set capture observes 5 (W), verification reopen observes a
        // DIFFERENT value 42 (V) -- exactly the real-environment attempt-10 scenario. No override/
        // corruption is configured, so the fake's read-back genuinely reproduces what was written.
        native.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 42);
        var diagnostics = new List<ClipboardWriteDiagnosticEvent>();
        monitor.WriteDiagnosticObserved += (_, e) => diagnostics.Add(e);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(42u, result.ResultSequence); // V, not W

        // The read-back comparison IS now attempted even though V != W -- this is exactly the
        // corrected behavior (before STEP42, VerificationSequenceObserved's mismatch alone would
        // have short-circuited everything below it).
        var kinds = diagnostics.Select(e => e.Kind).ToList();
        Assert.Equal(
            [
                ClipboardWriteDiagnosticKind.WriteStarted,
                ClipboardWriteDiagnosticKind.CasSequenceObserved,
                ClipboardWriteDiagnosticKind.PostSetSequenceCaptured,
                ClipboardWriteDiagnosticKind.VerificationSequenceObserved,
                ClipboardWriteDiagnosticKind.ReadBackAttempted,
                ClipboardWriteDiagnosticKind.ReadBackExactMatch,
                ClipboardWriteDiagnosticKind.WriteCompleted,
            ],
            kinds);

        var postSet = diagnostics[2];
        Assert.Equal(DefaultSequence, postSet.ObservedSequence); // W

        var verification = diagnostics[3];
        Assert.Equal(42u, verification.ObservedSequence); // V
        Assert.False(verification.SequenceMatched); // W != V -- diagnostic-only now, not a gate

        var readBackMatch = diagnostics[5];
        Assert.True(readBackMatch.ReadBackExactMatch);

        var completed = diagnostics[6];
        Assert.Equal(ClipboardWriteOutcome.Success, completed.Outcome);
        Assert.True(completed.ClipboardMutated);

        // The self-write marker WAS installed (at V) -- proven by raising a native notification
        // carrying V and observing it IS suppressed as our own echo.
        var notifications = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => notifications.Add(n);
        native.SequenceNumber = 42;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout); // FIFO sync barrier
        monitor.Stop();

        Assert.Empty(notifications);
    }

    // ---- Companion: the SAME W != V transition, but with genuinely different read-back content
    // -- MUST still be Superseded (never Success, never a marker), proving the fix does not turn
    // every sequence transition into a false positive. ----
    [Fact]
    public async Task Superseded_PostSetSequenceDiffersFromVerificationSequence_AndContentGenuinelyDiffers_RecordsMismatch_NoMarkerInstalled()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 42);
        textNative.OverridePayloadOnSet = "someone else's content";
        var diagnostics = new List<ClipboardWriteDiagnosticEvent>();
        monitor.WriteDiagnosticObserved += (_, e) => diagnostics.Add(e);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);

        Assert.Equal(ClipboardWriteOutcome.Superseded, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Null(result.ResultSequence);

        // Read-back IS attempted this time (V is reliable, format is available) -- it is the
        // CONTENT comparison, not the sequence comparison, that ultimately decides Superseded.
        var kinds = diagnostics.Select(e => e.Kind).ToList();
        Assert.Equal(
            [
                ClipboardWriteDiagnosticKind.WriteStarted,
                ClipboardWriteDiagnosticKind.CasSequenceObserved,
                ClipboardWriteDiagnosticKind.PostSetSequenceCaptured,
                ClipboardWriteDiagnosticKind.VerificationSequenceObserved,
                ClipboardWriteDiagnosticKind.ReadBackAttempted,
                ClipboardWriteDiagnosticKind.ReadBackExactMatch,
                ClipboardWriteDiagnosticKind.WriteCompleted,
            ],
            kinds);

        var exactMatch = diagnostics.Single(e => e.Kind == ClipboardWriteDiagnosticKind.ReadBackExactMatch);
        Assert.False(exactMatch.ReadBackExactMatch);

        var completed = diagnostics.Single(e => e.Kind == ClipboardWriteDiagnosticKind.WriteCompleted);
        Assert.Equal(ClipboardWriteOutcome.Superseded, completed.Outcome);
        Assert.True(completed.ClipboardMutated);

        // No marker installed -- a notification carrying the original write's own confirmed
        // post-Set sequence (W) is NOT suppressed.
        var notifications = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => notifications.Add(n);
        native.SequenceNumber = DefaultSequence;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout); // FIFO sync barrier
        monitor.Stop();

        Assert.Single(notifications); // Changed WAS invoked -- not suppressed as a self-write echo.
    }

    // ==================================================================
    // C. EXACT READ-BACK MISMATCH -- sequence stable, comparison attempted, exact match false
    // ==================================================================

    [Fact]
    public async Task ReadBackMismatch_SequenceStable_ComparisonAttempted_ExactMatchFalse_ResultPreserved()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.OverridePayloadOnSet = "unexpected content";
        var diagnostics = new List<ClipboardWriteDiagnosticEvent>();
        monitor.WriteDiagnosticObserved += (_, e) => diagnostics.Add(e);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.ReadBackMismatch, result.Outcome);
        Assert.True(result.ClipboardMutated);

        var verification = diagnostics.Single(e => e.Kind == ClipboardWriteDiagnosticKind.VerificationSequenceObserved);
        Assert.True(verification.SequenceMatched); // sequence itself was stable

        Assert.Contains(diagnostics, e => e.Kind == ClipboardWriteDiagnosticKind.ReadBackAttempted);

        var exactMatch = diagnostics.Single(e => e.Kind == ClipboardWriteDiagnosticKind.ReadBackExactMatch);
        Assert.False(exactMatch.ReadBackExactMatch);

        var completed = diagnostics.Single(e => e.Kind == ClipboardWriteDiagnosticKind.WriteCompleted);
        Assert.Equal(ClipboardWriteOutcome.ReadBackMismatch, completed.Outcome);
        Assert.True(completed.ClipboardMutated);
    }

    // ---- ReadBackAttempted/ReadBackExactMatch are correctly absent/attempted-but-inconclusive for
    // the two "no valid string was ever produced" verification-failure classifications. ----

    [Fact]
    public async Task VerificationFormatMissing_NeverRecordsReadBackAttempted_OutcomeUnaffected()
    {
        var (monitor, native, _) = CreateStarted();
        native.UnicodeTextAvailable = false; // mutation body never checks this -- only verification does
        var diagnostics = new List<ClipboardWriteDiagnosticEvent>();
        monitor.WriteDiagnosticObserved += (_, e) => diagnostics.Add(e);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.DoesNotContain(diagnostics, e => e.Kind == ClipboardWriteDiagnosticKind.ReadBackAttempted);
        Assert.DoesNotContain(diagnostics, e => e.Kind == ClipboardWriteDiagnosticKind.ReadBackExactMatch);

        var verification = diagnostics.Single(e => e.Kind == ClipboardWriteDiagnosticKind.VerificationSequenceObserved);
        Assert.True(verification.SequenceMatched);
    }

    [Fact]
    public async Task VerificationMalformedPayload_RecordsReadBackAttempted_ButNeverReadBackExactMatch()
    {
        var (monitor, _, textNative) = CreateStarted();
        textNative.CorruptPayloadOnSet = true;
        var diagnostics = new List<ClipboardWriteDiagnosticEvent>();
        monitor.WriteDiagnosticObserved += (_, e) => diagnostics.Add(e);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.NativeFailure, result.Outcome);
        Assert.Contains(diagnostics, e => e.Kind == ClipboardWriteDiagnosticKind.ReadBackAttempted);
        Assert.DoesNotContain(diagnostics, e => e.Kind == ClipboardWriteDiagnosticKind.ReadBackExactMatch);
    }

    // ==================================================================
    // D. GENUINE EXTERNAL REPLACEMENT REMAINS SUPERSEDED -- diagnostic instrumentation cannot
    // alter the outcome, with or without a subscriber attached
    // ==================================================================

    [Fact]
    public async Task GenuineExternalReplacement_RemainsSuperseded_IdenticallyWithOrWithoutDiagnosticSubscriber()
    {
        // A sequence transition ALONE (unmodified content) is no longer enough to reach
        // Superseded as of Phase 3C STEP42 -- see section B above. This test therefore also
        // configures genuinely different read-back content, matching the true "genuine external
        // replacement" case Superseded is now reserved for.

        // Run 1: no WriteDiagnosticObserved subscriber at all (production default).
        var (monitorA, nativeA, textNativeA) = CreateStarted();
        nativeA.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 999);
        textNativeA.OverridePayloadOnSet = "someone else's content";
        var resultWithoutSubscriber = await monitorA.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitorA.Stop();

        // Run 2: identical native sequence + content script, WITH a diagnostic subscriber attached.
        var (monitorB, nativeB, textNativeB) = CreateStarted();
        nativeB.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 999);
        textNativeB.OverridePayloadOnSet = "someone else's content";
        var diagnostics = new List<ClipboardWriteDiagnosticEvent>();
        monitorB.WriteDiagnosticObserved += (_, e) => diagnostics.Add(e);
        var resultWithSubscriber = await monitorB.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitorB.Stop();

        Assert.Equal(ClipboardWriteOutcome.Superseded, resultWithoutSubscriber.Outcome);
        Assert.Equal(resultWithoutSubscriber.Outcome, resultWithSubscriber.Outcome);
        Assert.Equal(resultWithoutSubscriber.ClipboardMutated, resultWithSubscriber.ClipboardMutated);
        Assert.Equal(resultWithoutSubscriber.ResultSequence, resultWithSubscriber.ResultSequence);
        Assert.NotEmpty(diagnostics); // the subscriber really was exercised in run 2
    }

    // ==================================================================
    // E. DIAGNOSTIC SINK FAILURE CANNOT ALTER WRITE BEHAVIOR
    // ==================================================================

    [Fact]
    public async Task ThrowingWriteDiagnosticObserver_NeverAltersWriteResult_ForSuccess()
    {
        var (monitor, native, _) = CreateStarted();
        native.SequenceNumber = 7;
        monitor.WriteDiagnosticObserved += (_, _) => throw new InvalidOperationException("Synthetic write-diagnostic observer failure.");

        var result = await monitor.WriteTextIfSequenceMatchesAsync(7, "hello").WaitAsync(WaitTimeout);
        var readBack = await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.Equal(ClipboardReadOutcome.Success, readBack.Outcome);
        Assert.Equal("hello", readBack.Snapshot!.Value.Text);
    }

    [Fact]
    public async Task ThrowingWriteDiagnosticObserver_NeverAltersWriteResult_ForSuperseded()
    {
        var (monitor, native, textNative) = CreateStarted();
        native.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 42);
        textNative.OverridePayloadOnSet = "someone else's content"; // genuine external replacement
        monitor.WriteDiagnosticObserved += (_, _) => throw new InvalidOperationException("Synthetic write-diagnostic observer failure.");

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Superseded, result.Outcome);
        Assert.True(result.ClipboardMutated);
    }

    [Fact]
    public async Task ThrowingWriteDiagnosticObserver_NeverAltersWriteResult_ForSuccessWithDifferingSequence()
    {
        // Phase 3C STEP42: the "W != V" success path is now a distinct, real outcome from plain
        // Success (native.SequenceNumber constant) -- worth its own diagnostic-sink-failure-
        // isolation coverage alongside the pre-existing ForSuccess/ForSuperseded tests above.
        var (monitor, native, _) = CreateStarted();
        native.QueueSequenceNumbers(DefaultSequence, DefaultSequence, 42);
        monitor.WriteDiagnosticObserved += (_, _) => throw new InvalidOperationException("Synthetic write-diagnostic observer failure.");

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.Equal(42u, result.ResultSequence);
    }

    // ==================================================================
    // F. WriteId correlation
    // ==================================================================

    [Fact]
    public async Task WriteId_IsMonotonicallyIncreasing_AcrossSeparateWrites()
    {
        var (monitor, native, _) = CreateStarted();
        var diagnostics = new List<ClipboardWriteDiagnosticEvent>();
        monitor.WriteDiagnosticObserved += (_, e) => diagnostics.Add(e);

        native.SequenceNumber = 10;
        await monitor.WriteTextIfSequenceMatchesAsync(10, "first").WaitAsync(WaitTimeout);
        native.SequenceNumber = 11;
        await monitor.WriteTextIfSequenceMatchesAsync(11, "second").WaitAsync(WaitTimeout);
        monitor.Stop();

        var startedIds = diagnostics
            .Where(e => e.Kind == ClipboardWriteDiagnosticKind.WriteStarted)
            .Select(e => e.WriteId)
            .ToList();
        Assert.Equal(2, startedIds.Count);
        Assert.True(startedIds[1] > startedIds[0]);
    }

    // ---- CROSS_LAYER_CORRELATION: WriteStarted's own ExpectedSequence is the exact same uint the
    // caller already supplied as expectedSequence -- exactly the value an App-layer trace's own
    // GuardedReadCompleted.sequenceNumber would carry for the same attempt (see
    // ClipboardWriteDiagnosticEvent's own class doc). ----
    [Fact]
    public async Task WriteStarted_ExpectedSequence_ExactlyMatchesCallerSuppliedExpectedSequence()
    {
        var (monitor, native, _) = CreateStarted();
        native.SequenceNumber = 555;
        var diagnostics = new List<ClipboardWriteDiagnosticEvent>();
        monitor.WriteDiagnosticObserved += (_, e) => diagnostics.Add(e);

        await monitor.WriteTextIfSequenceMatchesAsync(555, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        var started = diagnostics.Single(e => e.Kind == ClipboardWriteDiagnosticKind.WriteStarted);
        Assert.Equal(555u, started.ExpectedSequence);
    }

    // ==================================================================
    // G. CHECK1 target-guard rejection -- WriteStarted + WriteCompleted only, no CAS/verification
    // events at all (the write never reaches OpenClipboard)
    // ==================================================================

    [Fact]
    public async Task Check1TargetRejection_RecordsOnlyWriteStartedAndWriteCompleted()
    {
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        var foregroundSource = new FakeForegroundTargetSource(); // defaults to PID 4242 / "ChatGPT"
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative, foregroundSource: foregroundSource);
        monitor.Start();

        var expectedTarget = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");
        foregroundSource.ProcessNameValue = "NotChatGPT"; // CHECK1 will observe a mismatch
        var diagnostics = new List<ClipboardWriteDiagnosticEvent>();
        monitor.WriteDiagnosticObserved += (_, e) => diagnostics.Add(e);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(expectedTarget, DefaultSequence, "hello").WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardWriteOutcome.TargetChanged, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Empty(textNative.CallLog); // never even reached OpenClipboard

        var kinds = diagnostics.Select(e => e.Kind).ToList();
        Assert.Equal(
            [ClipboardWriteDiagnosticKind.WriteStarted, ClipboardWriteDiagnosticKind.WriteCompleted],
            kinds);

        var completed = diagnostics[1];
        Assert.Equal(ClipboardWriteOutcome.TargetChanged, completed.Outcome);
        Assert.False(completed.ClipboardMutated);
    }

    // ==================================================================
    // H. PRIVACY BOUNDARY -- structural
    // ==================================================================

    [Fact]
    public void ClipboardWriteDiagnosticEvent_HasNoStringMembers()
    {
        var stringMembers = typeof(ClipboardWriteDiagnosticEvent)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m =>
                (m is PropertyInfo p && p.PropertyType == typeof(string)) ||
                (m is FieldInfo f && f.FieldType == typeof(string)));

        Assert.Empty(stringMembers);
    }

    [Fact]
    public void ClipboardWriteDiagnosticEvent_HasNoDebuggerDisplayOrTypeProxyAttributes()
    {
        var attributes = typeof(ClipboardWriteDiagnosticEvent).GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);
        Assert.DoesNotContain(attributes, name => name is "DebuggerDisplayAttribute" or "DebuggerTypeProxyAttribute");
    }

    [Fact]
    public void ClipboardWriteDiagnosticEvent_OnlyExpectedPrimitiveOrEnumFields()
    {
        var properties = typeof(ClipboardWriteDiagnosticEvent).GetProperties();
        var names = properties.Select(p => p.Name).ToList();

        Assert.Equal(8, properties.Length);
        Assert.Contains(nameof(ClipboardWriteDiagnosticEvent.WriteId), names);
        Assert.Contains(nameof(ClipboardWriteDiagnosticEvent.Kind), names);
        Assert.Contains(nameof(ClipboardWriteDiagnosticEvent.ExpectedSequence), names);
        Assert.Contains(nameof(ClipboardWriteDiagnosticEvent.ObservedSequence), names);
        Assert.Contains(nameof(ClipboardWriteDiagnosticEvent.SequenceMatched), names);
        Assert.Contains(nameof(ClipboardWriteDiagnosticEvent.ReadBackExactMatch), names);
        Assert.Contains(nameof(ClipboardWriteDiagnosticEvent.Outcome), names);
        Assert.Contains(nameof(ClipboardWriteDiagnosticEvent.ClipboardMutated), names);

        Assert.All(properties, p => Assert.True(
            p.PropertyType == typeof(long)
            || p.PropertyType == typeof(uint?)
            || p.PropertyType == typeof(bool?)
            || p.PropertyType == typeof(ClipboardWriteDiagnosticKind)
            || p.PropertyType == typeof(ClipboardWriteOutcome?)));
    }

    // ---- no ChatGPT/product-policy naming anywhere in the new diagnostic types either (mirrors
    // ClipboardMonitorDiagnosticTypes_HaveNoProductPolicyNaming's existing precedent) ----
    [Fact]
    public void ClipboardWriteDiagnosticTypes_HaveNoProductPolicyNaming()
    {
        var forbidden = new[] { "ChatGPT", "IsChatGPT", "IsSupportedAi", "IsEligible", "PrivacyGate" };

        foreach (var type in new[] { typeof(ClipboardWriteDiagnosticEvent), typeof(ClipboardWriteDiagnosticKind) })
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name);
            Assert.DoesNotContain(members, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
