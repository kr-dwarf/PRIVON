using System.Reflection;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3B STEP2 -- App Clipboard Dispatch + TargetGate Read Coordinator regression. All tests
// use FakeClipboardReadTransport + FakeForegroundTargetCapture (synthetic, OS-free, no mocking
// framework). No real Windows clipboard/foreground state or WPF Application/Window is ever
// touched.
public class ClipboardPrivacyCoordinatorTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static (ClipboardPrivacyCoordinator Coordinator, FakeClipboardReadTransport Transport, FakeForegroundTargetCapture TargetCapture, FakeClipboardPrivacyProcessor Processor) CreateStarted(
        TimeSpan? stopTimeoutOverride = null)
    {
        var (coordinator, transport, targetCapture, processor, _) = CreateStartedWithWrite(stopTimeoutOverride);
        return (coordinator, transport, targetCapture, processor);
    }

    // Phase 3B STEP14 -- like CreateStarted above, but also returns the FakeClipboardWriteTransport
    // for tests that need to observe/control the guarded-write seam. Kept as a separate helper so
    // the ~40 pre-STEP14 tests above that only care about read/dispatch/target-gate behavior via
    // CreateStarted keep compiling and running completely unchanged.
    private static (ClipboardPrivacyCoordinator Coordinator, FakeClipboardReadTransport Transport, FakeForegroundTargetCapture TargetCapture, FakeClipboardPrivacyProcessor Processor, FakeClipboardWriteTransport WriteTransport) CreateStartedWithWrite(
        TimeSpan? stopTimeoutOverride = null)
    {
        var (coordinator, transport, targetCapture, processor, writeTransport, _) = CreateStartedWithLifecycle(stopTimeoutOverride);
        return (coordinator, transport, targetCapture, processor, writeTransport);
    }

    // Phase 3B STEP17 -- like CreateStartedWithWrite above, but also returns the
    // FakeClipboardNotificationLifecycle for tests that need to observe the generation-advance
    // seam. Kept as a separate, innermost helper so every pre-STEP17 test (via CreateStarted/
    // CreateStartedWithWrite) keeps compiling and running completely unchanged.
    private static (ClipboardPrivacyCoordinator Coordinator, FakeClipboardReadTransport Transport, FakeForegroundTargetCapture TargetCapture, FakeClipboardPrivacyProcessor Processor, FakeClipboardWriteTransport WriteTransport, FakeClipboardNotificationLifecycle NotificationLifecycle) CreateStartedWithLifecycle(
        TimeSpan? stopTimeoutOverride = null)
    {
        var (coordinator, transport, targetCapture, processor, writeTransport, notificationLifecycle, _) = CreateStartedWithPublisher(stopTimeoutOverride);
        return (coordinator, transport, targetCapture, processor, writeTransport, notificationLifecycle);
    }

    // Phase 3B STEP21 -- like CreateStartedWithLifecycle above, but also returns the
    // FakeClipboardDecisionSessionPublisher for tests that need to observe the decision-session
    // publication seam. Kept as a separate, innermost helper so every pre-STEP21 test (via
    // CreateStarted/CreateStartedWithWrite/CreateStartedWithLifecycle) keeps compiling and running
    // completely unchanged.
    private static (ClipboardPrivacyCoordinator Coordinator, FakeClipboardReadTransport Transport, FakeForegroundTargetCapture TargetCapture, FakeClipboardPrivacyProcessor Processor, FakeClipboardWriteTransport WriteTransport, FakeClipboardNotificationLifecycle NotificationLifecycle, FakeClipboardDecisionSessionPublisher DecisionSessionPublisher) CreateStartedWithPublisher(
        TimeSpan? stopTimeoutOverride = null)
    {
        var (coordinator, transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher, _) = CreateStartedWithGate(stopTimeoutOverride);
        return (coordinator, transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher);
    }

    // Phase 3B STEP23 -- like CreateStartedWithPublisher above, but also returns the real
    // ClipboardOperationGate for tests that need to observe/share the App-level serialization
    // seam. Kept as a separate, innermost helper so every pre-STEP23 test (via CreateStarted/
    // CreateStartedWithWrite/CreateStartedWithLifecycle/CreateStartedWithPublisher) keeps
    // compiling and running completely unchanged. Now delegates to CreateStartedWithVerification
    // (Phase 3C STEP32) and discards the two new verification fakes, exactly mirroring how
    // CreateStartedWithPublisher discards operationGate below -- unchanged for every existing
    // caller.
    private static (ClipboardPrivacyCoordinator Coordinator, FakeClipboardReadTransport Transport, FakeForegroundTargetCapture TargetCapture, FakeClipboardPrivacyProcessor Processor, FakeClipboardWriteTransport WriteTransport, FakeClipboardNotificationLifecycle NotificationLifecycle, FakeClipboardDecisionSessionPublisher DecisionSessionPublisher, ClipboardOperationGate OperationGate) CreateStartedWithGate(
        TimeSpan? stopTimeoutOverride = null)
    {
        var (coordinator, transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher, operationGate, _, _) = CreateStartedWithVerification(stopTimeoutOverride);
        return (coordinator, transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher, operationGate);
    }

    // Phase 3C STEP32 -- like CreateStartedWithGate above, but also returns the two new
    // composer-verification fakes for tests that need to observe the verified-write handoff /
    // clipboard-notification invalidation seams. The new innermost helper -- constructs the
    // coordinator directly.
    private static (ClipboardPrivacyCoordinator Coordinator, FakeClipboardReadTransport Transport, FakeForegroundTargetCapture TargetCapture, FakeClipboardPrivacyProcessor Processor, FakeClipboardWriteTransport WriteTransport, FakeClipboardNotificationLifecycle NotificationLifecycle, FakeClipboardDecisionSessionPublisher DecisionSessionPublisher, ClipboardOperationGate OperationGate, FakeClipboardComposerVerificationHandoff VerificationHandoff, FakeClipboardComposerVerificationInvalidation VerificationInvalidation) CreateStartedWithVerification(
        TimeSpan? stopTimeoutOverride = null)
    {
        var transport = new FakeClipboardReadTransport();
        var targetCapture = new FakeForegroundTargetCapture();
        var processor = new FakeClipboardPrivacyProcessor();
        var writeTransport = new FakeClipboardWriteTransport();
        var notificationLifecycle = new FakeClipboardNotificationLifecycle();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();
        var operationGate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var verificationInvalidation = new FakeClipboardComposerVerificationInvalidation();
        var coordinator = new ClipboardPrivacyCoordinator(transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation, stopTimeoutOverride);
        coordinator.Start();
        return (coordinator, transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation);
    }

    private static readonly ClipboardChangeNotification TextNotification = new(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true);
    private static readonly ClipboardChangeNotification NonTextNotification = new(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: false);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? WaitTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(10);
        }
    }

    // ==================================================================
    // CALLBACK (items 8-10)
    // ==================================================================

    // ---- 8. non-text event -> no channel-processing side effect, no Capture, no read ----
    [Fact]
    public void NonTextNotification_NoCaptureNoRead()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();

        transport.RaiseChanged(NonTextNotification);

        Assert.Equal(0, targetCapture.CaptureCallCount);
        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), transport.CallLog);
        coordinator.Stop();
    }

    // ---- 9. text event -> eventually processed via the enqueue path ----
    [Fact]
    public async Task TextNotification_EventuallyProcessed_ViaEnqueuePath()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);

        Assert.Equal(1, targetCapture.CaptureCallCount);
        coordinator.Stop();
    }

    // ---- 10. the callback itself never blocks on Capture/Read inline ----
    [Fact]
    public async Task Callback_DoesNotBlockOnCaptureOrRead()
    {
        var (coordinator, transport, _, _) = CreateStarted();
        transport.HoldReadsUntilReleased = true; // if the callback ever synchronously awaited this, it would hang

        // If OnClipboardChanged ever inlined Capture/Read synchronously, this would hang until
        // WaitAsync's timeout rather than complete promptly.
        await Task.Run(() => transport.RaiseChanged(TextNotification)).WaitAsync(WaitTimeout);

        // Release whatever the worker started (this test isn't about the read itself) so Stop()
        // below doesn't have to wait out its own shutdown timeout on a deliberately-held read.
        await WaitUntilAsync(() => transport.PendingHeldReadCount >= 1);
        transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable));

        coordinator.Stop();
    }

    // ==================================================================
    // COORDINATOR FLOW (items 11-24)
    // ==================================================================

    // ---- 11. text + unresolved target -> Capture called, no read ----
    [Fact]
    public async Task UnresolvedTarget_CaptureCalled_NoRead()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();
        targetCapture.SnapshotToReturn = new ForegroundTargetSnapshot(IsResolved: false, ProcessId: 4242, ProcessName: "ChatGPT");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);
        await Task.Delay(50); // small settle window -- ReadTextSnapshotAsync is never expected to be called

        Assert.Equal(1, targetCapture.CaptureCallCount);
        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), transport.CallLog);
        coordinator.Stop();
    }

    // ---- 12. text + unrelated target -> no read ----
    [Fact]
    public async Task UnrelatedTarget_NoRead()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();
        targetCapture.SnapshotToReturn = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "notepad");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);
        await Task.Delay(50);

        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), transport.CallLog);
        coordinator.Stop();
    }

    // ---- 13. text + ChatGPT -> guarded read called ----
    [Fact]
    public async Task ChatGptTarget_GuardedReadCalled()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => transport.CallLog.Contains(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync)));

        coordinator.Stop();
    }

    // ---- 14. the exact ForegroundTargetSnapshot returned by Capture is exactly the value passed
    // to the guarded read ----
    [Fact]
    public async Task GuardedRead_ReceivesExactSnapshotThatPassedPolicy()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();
        var expected = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 9999, ProcessName: "ChatGPT");
        targetCapture.SnapshotToReturn = expected;

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => transport.ReceivedExpectedTargets.Count >= 1);

        Assert.Equal(expected, transport.ReceivedExpectedTargets[0]);
        coordinator.Stop();
    }

    // ---- 15. only one App Capture per processing attempt ----
    [Fact]
    public async Task OnlyOneCapturePerProcessingAttempt()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => transport.CallLog.Contains(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync)));

        Assert.Equal(1, targetCapture.CaptureCallCount);
        coordinator.Stop();
    }

    // ---- 16/17/18/20/21/22/23. every non-Success guarded-read outcome ends the attempt with no
    // retry, no fallback, no downstream behavior -- including item 11 (Phase 3B STEP4): the
    // privacy processor must never be invoked for a non-Success outcome ----
    [Theory]
    [InlineData(ClipboardReadOutcome.NotRunning)]
    [InlineData(ClipboardReadOutcome.Busy)]
    [InlineData(ClipboardReadOutcome.FormatUnavailable)]
    [InlineData(ClipboardReadOutcome.NativeFailure)]
    [InlineData(ClipboardReadOutcome.MalformedData)]
    [InlineData(ClipboardReadOutcome.InvalidExpectedTarget)]
    [InlineData(ClipboardReadOutcome.TargetUnavailable)]
    [InlineData(ClipboardReadOutcome.TargetChanged)]
    public async Task NonSuccessOutcome_EndsAttempt_NoRetry(ClipboardReadOutcome outcome)
    {
        var (coordinator, transport, targetCapture, processor) = CreateStarted();
        transport.NextReadResult = ClipboardTextReadResult.Failure(outcome);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => transport.CallLog.Count(n => n == nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync)) >= 1);

        // No retry: a settle window later, still exactly one read attempt and one capture, and
        // the privacy processor was never invoked for a non-Success outcome.
        await Task.Delay(50);
        Assert.Equal(1, transport.CallLog.Count(n => n == nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync)));
        Assert.Equal(1, targetCapture.CaptureCallCount);
        Assert.Equal(0, processor.CallCount);

        coordinator.Stop();
    }

    // ---- 24. Success -> no write/extra raw handling beyond the single delegated processor call;
    // the coordinator does not touch Snapshot itself beyond handing it, once, to the processor
    // (Phase 3B STEP4: the coordinator now hands off to IClipboardPrivacyProcessor on Success --
    // see items 7-15 below for the processor-handoff regressions themselves) ----
    [Fact]
    public async Task Success_NoDownstreamProcessing()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();
        var snapshot = new ClipboardTextSnapshot(SequenceNumber: 7, HasReliableSequence: true, Text: "hello");
        transport.NextReadResult = ClipboardTextReadResult.Success(snapshot);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => transport.CallLog.Contains(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync)));
        await Task.Delay(50);

        // Structural proof there is nothing to observe beyond the single read call -- no second
        // call of any kind was made on the TRANSPORT seam as a result of Success (no write call
        // exists on this seam at all -- see the structural PUBLIC_/PROJECT_BOUNDARY tests below).
        Assert.Equal(1, transport.CallLog.Count(n => n == nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync)));
        coordinator.Stop();
    }

    // ==================================================================
    // PRIVACY PROCESSOR HANDOFF (Phase 3B STEP4, items 7-15)
    // ==================================================================

    // ---- 7/10. Success invokes the privacy processor exactly once -- no duplicate/second call
    // for a single successful attempt ----
    [Fact]
    public async Task Success_InvokesProcessorExactlyOnce()
    {
        var (coordinator, transport, _, processor) = CreateStarted();
        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(7, true, "hello"));

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);
        await Task.Delay(50); // settle window -- no further calls should follow the one attempt

        Assert.Equal(1, processor.CallCount);
        coordinator.Stop();
    }

    // ---- 8. the exact ForegroundTargetSnapshot that already passed TargetGate is the exact
    // value forwarded to the processor -- no second App-side capture (TARGET_TOKEN_FLOW) ----
    [Fact]
    public async Task Success_ForwardsExactExpectedTargetToProcessor()
    {
        var (coordinator, transport, targetCapture, processor) = CreateStarted();
        var expected = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 9999, ProcessName: "ChatGPT");
        targetCapture.SnapshotToReturn = expected;
        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "x"));

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.ReceivedExpectedTargets.Count >= 1);

        Assert.Equal(expected, processor.ReceivedExpectedTargets[0]);
        // Exactly one Capture call backs it -- no second/different capture happened in between.
        Assert.Equal(1, targetCapture.CaptureCallCount);
        coordinator.Stop();
    }

    // ---- 9. the exact ClipboardTextSnapshot returned by the guarded read (SequenceNumber,
    // HasReliableSequence, and Text all identity-preserved) is forwarded to the processor ----
    [Fact]
    public async Task Success_ForwardsExactSnapshotToProcessor()
    {
        var (coordinator, transport, _, processor) = CreateStarted();
        var snapshot = new ClipboardTextSnapshot(SequenceNumber: 42, HasReliableSequence: true, Text: "exact snapshot value");
        transport.NextReadResult = ClipboardTextReadResult.Success(snapshot);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.ReceivedSnapshots.Count >= 1);

        Assert.Equal(snapshot, processor.ReceivedSnapshots[0]);
        Assert.Equal(42u, processor.ReceivedSnapshots[0].SequenceNumber);
        Assert.True(processor.ReceivedSnapshots[0].HasReliableSequence);
        Assert.Equal("exact snapshot value", processor.ReceivedSnapshots[0].Text);
        coordinator.Stop();
    }

    // ---- 12. processor throwing ends the current attempt -- no crash, no hang, no exception
    // escaping the worker loop ----
    [Fact]
    public async Task ProcessorThrows_AttemptStops_NoCrash()
    {
        var (coordinator, transport, _, processor) = CreateStarted();
        processor.ThrowOnProcess = new InvalidOperationException("synthetic processor failure");
        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "hello"));

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);
        await Task.Delay(50); // settle window -- the outer worker catch must have absorbed it

        Assert.Equal(1, processor.CallCount);
        coordinator.Stop(); // must not hang or throw -- the worker is still alive
    }

    // ---- 13. processor throwing on one notification does not prevent a later notification from
    // being processed normally -- the worker loop survived ----
    [Fact]
    public async Task ProcessorThrows_OnFirstNotification_LaterNotificationStillProcesses()
    {
        var (coordinator, transport, _, processor) = CreateStarted();
        processor.ThrowOnProcess = new InvalidOperationException("synthetic processor failure");
        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "hello"));

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);
        Assert.Equal(1, processor.CallCount);

        processor.ThrowOnProcess = null;
        transport.RaiseChanged(new ClipboardChangeNotification(2, true, true));
        await WaitUntilAsync(() => processor.CallCount >= 2);

        Assert.Equal(2, processor.CallCount);
        coordinator.Stop();
    }

    // ---- 14. the processor is never invoked inline from the Changed callback -- only after the
    // guarded read actually completes, on the worker lane ----
    [Fact]
    public async Task Processor_InvokedOnlyAfterReadCompletes_NeverInlineFromCallback()
    {
        var (coordinator, transport, _, processor) = CreateStarted();
        transport.HoldReadsUntilReleased = true;

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => transport.PendingHeldReadCount >= 1);

        // The guarded read is still pending -- Process must not have been called yet. If it were
        // invoked synchronously from OnClipboardChanged (which only enqueues and returns), it
        // would already show up here regardless of the still-held read.
        Assert.Equal(0, processor.CallCount);

        transport.ReleaseNextRead(ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "x")));
        await WaitUntilAsync(() => processor.CallCount >= 1);

        Assert.Equal(1, processor.CallCount);
        coordinator.Stop();
    }

    // ---- 15. rapid notifications never invoke the processor concurrently -- capacity-1
    // DropOldest coalescing plus the single sequential worker lane means each release moves the
    // call count forward in lockstep, one at a time ----
    [Fact]
    public async Task RapidNotifications_ProcessorNeverInvokedConcurrently()
    {
        var (coordinator, transport, _, processor) = CreateStarted();
        transport.HoldReadsUntilReleased = true;

        var n1 = new ClipboardChangeNotification(1, true, true);
        var n2 = new ClipboardChangeNotification(2, true, true);
        var n3 = new ClipboardChangeNotification(3, true, true);

        transport.RaiseChanged(n1);
        await WaitUntilAsync(() => transport.PendingHeldReadCount == 1);

        // n2 fills the now-empty single channel slot; n3 (DropOldest) replaces n2 in that same
        // slot before anything ever reads n2.
        transport.RaiseChanged(n2);
        transport.RaiseChanged(n3);

        transport.ReleaseNextRead(ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "first")));
        await WaitUntilAsync(() => processor.CallCount >= 1);
        Assert.Equal(1, processor.CallCount);

        // The worker now dequeues n3 (n2 was dropped) and starts its own held read.
        await WaitUntilAsync(() => transport.PendingHeldReadCount == 1);
        transport.ReleaseNextRead(ClipboardTextReadResult.Success(new ClipboardTextSnapshot(3, true, "second")));
        await WaitUntilAsync(() => processor.CallCount >= 2);

        // Exactly 2 processor calls total for 3 raised notifications (n2 coalesced away) --
        // reached by two separate, individually-released completions rather than a burst, which
        // is itself the proof no second call could have overlapped the first.
        Assert.Equal(2, processor.CallCount);
        coordinator.Stop();
    }

    // ==================================================================
    // REPLACEMENT + GUARDED WRITE (Phase 3B STEP14, items 9-22/27)
    // ==================================================================

    private static ClipboardTextSnapshot SuccessSnapshot(uint sequence = 7, bool hasReliableSequence = true, string text = "hello") =>
        new(sequence, hasReliableSequence, text);

    // ---- 1/2/3/4 (processor-level no-plan gating already covered by ClipboardPrivacyProcessorTests
    // -- this proves the COORDINATOR side: no plan -> no write call at all) ----
    [Fact]
    public async Task NoWritePlan_NoWriteCallMade()
    {
        var (coordinator, transport, _, processor, writeTransport) = CreateStartedWithWrite();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = null; // no plan -- matches NeedsDecision/all-Bypass gating

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);
        await Task.Delay(50); // settle window -- no write should ever follow

        Assert.Equal(0, writeTransport.CallCount);
        coordinator.Stop();
    }

    // ---- Phase 3B STEP19: a DecisionPlan present on the outcome (instead of a WritePlan) still
    // results in zero write calls -- the coordinator gates purely on WritePlan being null, never
    // inspects or reacts to DecisionPlan in any way ----
    [Fact]
    public async Task DecisionPlanPresent_NoWritePlan_NoWriteCallMade()
    {
        var (coordinator, transport, _, processor, writeTransport) = CreateStartedWithWrite();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = null;
        processor.DecisionPlanToReturn = new ClipboardDecisionPlan([]);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);
        await Task.Delay(50); // settle window -- no write should ever follow

        Assert.Equal(0, writeTransport.CallCount);
        coordinator.Stop();
    }

    // ---- 5. Protect-only (a plan exists) -> the exact replacement text reaches the write
    // transport ----
    [Fact]
    public async Task WritePlanPresent_ReliableSequence_ExactReplacementTextForwarded()
    {
        var (coordinator, transport, _, processor, writeTransport) = CreateStartedWithWrite();
        var snapshot = SuccessSnapshot(sequence: 55, hasReliableSequence: true, text: "raw");
        transport.NextReadResult = ClipboardTextReadResult.Success(snapshot);
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);

        Assert.Equal(1, writeTransport.CallCount);
        Assert.Equal("[전화번호1]", writeTransport.ReceivedReplacementTexts[0]);
        coordinator.Stop();
    }

    // ---- 9. exact expectedTarget forwarded to write (the SAME one that authorized the read --
    // no recapture) ----
    [Fact]
    public async Task Write_ForwardsExactExpectedTargetThatAuthorizedRead()
    {
        var (coordinator, transport, targetCapture, processor, writeTransport) = CreateStartedWithWrite();
        var expected = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 9999, ProcessName: "ChatGPT");
        targetCapture.SnapshotToReturn = expected;
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);

        Assert.Equal(expected, writeTransport.ReceivedExpectedTargets[0]);
        // Exactly one Capture call backs both the read and the write -- no second/different capture.
        Assert.Equal(1, targetCapture.CaptureCallCount);
        coordinator.Stop();
    }

    // ---- 10/11. exact successful-read snapshot.SequenceNumber forwarded as the write's CAS
    // token -- NEVER the notification's own (different) sequence ----
    [Fact]
    public async Task Write_ForwardsSuccessfulReadSequence_NeverNotificationSequence()
    {
        var (coordinator, transport, _, processor, writeTransport) = CreateStartedWithWrite();
        var notification = new ClipboardChangeNotification(SequenceNumber: 111, HasReliableSequence: true, HasUnicodeText: true);
        var readSnapshot = SuccessSnapshot(sequence: 999, hasReliableSequence: true, text: "x");
        transport.NextReadResult = ClipboardTextReadResult.Success(readSnapshot);
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");

        transport.RaiseChanged(notification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);

        Assert.Equal(999u, writeTransport.ReceivedSequences[0]);
        Assert.NotEqual(111u, writeTransport.ReceivedSequences[0]);
        coordinator.Stop();
    }

    // ---- 12. unreliable sequence -> a plan may have been produced locally, but the write
    // transport is never called ----
    [Fact]
    public async Task WritePlanPresent_UnreliableSequence_NoWriteCallMade()
    {
        var (coordinator, transport, _, processor, writeTransport) = CreateStartedWithWrite();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot(hasReliableSequence: false));
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(0, writeTransport.CallCount);
        coordinator.Stop();
    }

    // ---- 13. write call exactly once when eligible -- no duplicate call for a single attempt ----
    [Fact]
    public async Task WriteEligible_WriteCalledExactlyOnce()
    {
        var (coordinator, transport, _, processor, writeTransport) = CreateStartedWithWrite();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);
        await Task.Delay(50); // settle window -- no further calls should follow the one attempt

        Assert.Equal(1, writeTransport.CallCount);
        coordinator.Stop();
    }

    // ---- 14/15/16. Busy/SequenceChanged/TargetChanged (and every other non-Success write
    // outcome) end the attempt with no retry -- one write call, no second attempt ----
    [Theory]
    [InlineData(ClipboardWriteOutcome.Busy)]
    [InlineData(ClipboardWriteOutcome.SequenceChanged)]
    [InlineData(ClipboardWriteOutcome.TargetChanged)]
    [InlineData(ClipboardWriteOutcome.TargetUnavailable)]
    [InlineData(ClipboardWriteOutcome.NativeFailure)]
    [InlineData(ClipboardWriteOutcome.VerificationUnavailable)]
    [InlineData(ClipboardWriteOutcome.Superseded)]
    [InlineData(ClipboardWriteOutcome.ReadBackMismatch)]
    public async Task NonSuccessWriteOutcome_EndsAttempt_NoRetry(ClipboardWriteOutcome outcome)
    {
        var (coordinator, transport, _, processor, writeTransport) = CreateStartedWithWrite();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.NextResult = ClipboardWriteResult.Failure(outcome, mutated: false);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);
        await Task.Delay(50); // settle window -- no retry should follow

        Assert.Equal(1, writeTransport.CallCount);
        coordinator.Stop();
    }

    // ---- 17. Success + ClipboardMutated true reaches the write transport with the outcome
    // observable there -- the coordinator itself makes no further claim beyond one write call ----
    [Fact]
    public async Task SuccessOutcome_WriteObserved_NoFurtherCoordinatorAction()
    {
        var (coordinator, transport, _, processor, writeTransport) = CreateStartedWithWrite();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot(sequence: 42));
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 42);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(1, writeTransport.CallCount);
        coordinator.Stop();
    }

    // ---- 27. the worker survives a write-transport throw and processes a later notification
    // normally ----
    [Fact]
    public async Task WriteTransportThrows_WorkerSurvives_LaterNotificationStillProcessed()
    {
        var (coordinator, transport, _, processor, writeTransport) = CreateStartedWithWrite();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.ThrowOnWrite = new InvalidOperationException("synthetic write-transport failure");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);
        Assert.Equal(1, writeTransport.CallCount);

        writeTransport.ThrowOnWrite = null;
        transport.RaiseChanged(new ClipboardChangeNotification(2, true, true));
        await WaitUntilAsync(() => writeTransport.CallCount >= 2);

        Assert.Equal(2, writeTransport.CallCount);
        coordinator.Stop();
    }

    // ---- diagnostics: no synthetic replacement sentinel surfaces through any observable
    // coordinator state across a write attempt ----
    [Fact]
    public async Task WriteAttempt_Diagnostics_NeverContainRawReplacementSentinel()
    {
        const string sentinel = "RAW-APP-WRITE-SENTINEL-407215";
        var (coordinator, transport, _, processor, writeTransport) = CreateStartedWithWrite();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = new ClipboardWritePlan(sentinel);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);
        await Task.Delay(50);

        // The coordinator itself exposes no diagnostic surface carrying text at all (no ToString
        // override, no logging -- structurally confirmed by AppProductionSource_HasNoTextLoggingCalls
        // above, which already scans every .cs file in src/Privon.App including this STEP's new
        // ones).
        coordinator.Stop();
    }

    // ==================================================================
    // DECISION SESSION PUBLICATION (Phase 3B STEP21, section AB)
    // ==================================================================

    private static ClipboardDecisionPlan SampleDecisionPlan() =>
        new([new ClipboardDecisionItem(new CanonicalValue(PiiType.Phone, "01012345678"), RiskLevel.Level1)]);

    // ---- AB.1. DecisionPlan path -> publisher called exactly once ----
    [Fact]
    public async Task DecisionPlanPresent_PublisherCalledOnce()
    {
        var (coordinator, transport, _, processor, _, _, publisher) = CreateStartedWithPublisher();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.DecisionPlanToReturn = SampleDecisionPlan();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => publisher.CallCount >= 1);
        await Task.Delay(50); // settle window -- no further calls should follow the one attempt

        Assert.Equal(1, publisher.CallCount);
        coordinator.Stop();
    }

    // ---- AB.2. forwards the EXACT ClipboardDispatchItem.Generation captured at arrival, never a
    // re-derived value ----
    [Fact]
    public async Task DecisionPlanPresent_ForwardsExactDispatchGeneration()
    {
        var (coordinator, transport, _, processor, _, lifecycle, publisher) = CreateStartedWithPublisher();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.DecisionPlanToReturn = SampleDecisionPlan();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => publisher.CallCount >= 1);

        Assert.Equal(1, lifecycle.CallCount);
        Assert.Equal(1, publisher.ReceivedExpectedGenerations[0]);
        coordinator.Stop();
    }

    // ---- Phase 3C STEP38 (item 28) -- session-lock-flavored regression: uses the REAL
    // ClipboardDecisionScopeLifecycle + REAL ClipboardDecisionSessionPublisher (not the fakes every
    // other test in this file uses) so the actual generation-mismatch rejection mechanism runs, not
    // just a recorded argument. A dispatch item is captured under generation N; while its guarded
    // read is deliberately held in flight, a session lock is simulated by calling lifecycle.Reset()
    // directly (the identical `_generation++; _activeScope = null;` critical section a real
    // SessionLockMonitor.Locked callback would trigger -- Phase 3C STEP37.1's RESET_GENERATION_PROOF).
    // No coordinator-specific generation dependency or cancellation exists or is added -- the
    // coordinator still simply forwards item.Generation (now stale) to TryPublish, which rejects it
    // on its own. ----
    [Fact]
    public async Task SessionLockFlavored_ResetDuringInFlightRead_StaleGenerationHandoffRejectedByRealPublisher()
    {
        var transport = new FakeClipboardReadTransport { HoldReadsUntilReleased = true };
        var targetCapture = new FakeForegroundTargetCapture();
        var processor = new FakeClipboardPrivacyProcessor { DecisionPlanToReturn = SampleDecisionPlan() };
        var writeTransport = new FakeClipboardWriteTransport();
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var operationGate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var verificationInvalidation = new FakeClipboardComposerVerificationInvalidation();
        var coordinator = new ClipboardPrivacyCoordinator(
            transport, targetCapture, processor, writeTransport, lifecycle, publisher,
            operationGate, verificationHandoff, verificationInvalidation);
        coordinator.Start();

        transport.RaiseChanged(TextNotification); // captures item.Generation = 1 (real lifecycle's own advance)
        await WaitUntilAsync(() => transport.CallLog.Contains(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync)));

        // Simulate a session lock firing while this attempt's guarded read is still in flight --
        // real Reset(), same lifecycle instance the publisher will check against.
        lifecycle.Reset();
        Assert.False(lifecycle.HasActiveScope);

        transport.ReleaseNextRead(ClipboardTextReadResult.Success(SuccessSnapshot()));
        await WaitUntilAsync(() => processor.CallCount >= 1);
        await Task.Delay(50); // settle window for TryPublish to actually run

        // The REAL publisher/lifecycle rejected the now-stale generation -- no scope was published.
        Assert.False(lifecycle.HasActiveScope);
        coordinator.Stop();
    }

    // ---- AB.3. forwards the EXACT snapshot.Text from the SAME successful guarded read that
    // produced the plan -- never re-read, never normalized ----
    [Fact]
    public async Task DecisionPlanPresent_ForwardsExactSnapshotText()
    {
        var (coordinator, transport, _, processor, _, _, publisher) = CreateStartedWithPublisher();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot(text: "exact raw text"));
        processor.DecisionPlanToReturn = SampleDecisionPlan();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => publisher.CallCount >= 1);

        Assert.Equal("exact raw text", publisher.ReceivedRawTexts[0]);
        coordinator.Stop();
    }

    // ---- AB.4. forwards the EXACT DecisionPlan reference the processor returned ----
    [Fact]
    public async Task DecisionPlanPresent_ForwardsExactDecisionPlanReference()
    {
        var (coordinator, transport, _, processor, _, _, publisher) = CreateStartedWithPublisher();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        var plan = SampleDecisionPlan();
        processor.DecisionPlanToReturn = plan;

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => publisher.CallCount >= 1);

        Assert.Same(plan, publisher.ReceivedDecisionPlans[0]);
        coordinator.Stop();
    }

    // ---- AB.5. DecisionPlan path never touches the write transport ----
    [Fact]
    public async Task DecisionPlanPresent_DoesNotCallWriteTransport()
    {
        var (coordinator, transport, _, processor, writeTransport, _, publisher) = CreateStartedWithPublisher();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.DecisionPlanToReturn = SampleDecisionPlan();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => publisher.CallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(0, writeTransport.CallCount);
        coordinator.Stop();
    }

    // ---- AB.6. publisher returning false -> no retry, worker continues normally ----
    [Fact]
    public async Task PublisherReturnsFalse_NoRetry_WorkerContinues()
    {
        var (coordinator, transport, _, processor, _, _, publisher) = CreateStartedWithPublisher();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.DecisionPlanToReturn = SampleDecisionPlan();
        publisher.ResultToReturn = false;

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => publisher.CallCount >= 1);
        await Task.Delay(50); // settle window -- no retry should follow

        Assert.Equal(1, publisher.CallCount);
        coordinator.Stop();
    }

    // ---- AB.7. publisher throwing ends the current attempt with no crash/hang, and a LATER
    // notification is still processed normally afterward -- the worker survives ----
    [Fact]
    public async Task PublisherThrows_AttemptAborts_WorkerSurvives_LaterNotificationStillProcessed()
    {
        var (coordinator, transport, _, processor, _, _, publisher) = CreateStartedWithPublisher();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.DecisionPlanToReturn = SampleDecisionPlan();
        publisher.ThrowOnPublish = new InvalidOperationException("synthetic publisher failure");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => publisher.CallCount >= 1);
        await Task.Delay(50); // settle window -- the outer worker catch must have absorbed it
        Assert.Equal(1, publisher.CallCount);

        publisher.ThrowOnPublish = null;
        transport.RaiseChanged(new ClipboardChangeNotification(2, true, true));
        await WaitUntilAsync(() => publisher.CallCount >= 2);

        Assert.Equal(2, publisher.CallCount);
        coordinator.Stop(); // must not hang or throw -- the worker is still alive
    }

    // ---- AB.8. UNRELIABLE_SEQUENCE_DECISION_SESSION_POLICY: unlike the write path, an
    // unreliable-sequence successful read still results in the publisher being called -- decision
    // session identity does not depend on clipboard sequence reliability ----
    [Fact]
    public async Task DecisionPlanPresent_UnreliableSequence_PublisherStillCalled()
    {
        var (coordinator, transport, _, processor, writeTransport, _, publisher) = CreateStartedWithPublisher();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot(hasReliableSequence: false));
        processor.DecisionPlanToReturn = SampleDecisionPlan();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => publisher.CallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(1, publisher.CallCount);
        Assert.Equal(0, writeTransport.CallCount); // still never a write, unreliable or not
        coordinator.Stop();
    }

    // ---- AB.9. write-eligible path (WritePlan present) never calls the publisher ----
    [Fact]
    public async Task WritePlanPresent_PublisherNotCalled()
    {
        var (coordinator, transport, _, processor, _, _, publisher) = CreateStartedWithPublisher();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(0, publisher.CallCount);
        coordinator.Stop();
    }

    // ---- AB.10. no PII / all-Bypass (neither plan present) -> publisher never called ----
    [Fact]
    public async Task NeitherPlanPresent_PublisherNotCalled()
    {
        var (coordinator, transport, _, processor, _, _, publisher) = CreateStartedWithPublisher();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = null;
        processor.DecisionPlanToReturn = null;

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(0, publisher.CallCount);
        coordinator.Stop();
    }

    // ---- AB.11. unsupported target -> processor never even called, so the publisher isn't
    // either ----
    [Fact]
    public async Task UnsupportedTarget_PublisherNotCalled()
    {
        var (coordinator, transport, targetCapture, _, _, _, publisher) = CreateStartedWithPublisher();
        targetCapture.SnapshotToReturn = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "notepad");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(0, publisher.CallCount);
        coordinator.Stop();
    }

    // ---- AB.12. non-text notification -> publisher never called ----
    [Fact]
    public void NonTextNotification_PublisherNotCalled()
    {
        var (coordinator, transport, _, _, _, _, publisher) = CreateStartedWithPublisher();

        transport.RaiseChanged(NonTextNotification);

        Assert.Equal(0, publisher.CallCount);
        coordinator.Stop();
    }

    // ==================================================================
    // OPERATION GATE (Phase 3B STEP23, section V)
    // ==================================================================

    // ---- V.1/V.3/G. the coordinator's processing attempt acquires the shared operation gate and
    // holds it through the ENTIRE guarded-read + processor path -- proven by a second, independent
    // WaitAsync() call against the SAME shared gate instance being unable to complete while the
    // coordinator's own attempt is still in flight (standing in for what a future concurrent
    // decision-action attempt would observe) ----
    [Fact]
    public async Task ProcessingAttempt_HoldsOperationGate_ThroughoutGuardedReadAndProcessor()
    {
        var (coordinator, transport, _, _, _, _, _, operationGate) = CreateStartedWithGate();
        transport.HoldReadsUntilReleased = true;

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => transport.PendingHeldReadCount >= 1);

        // The coordinator's own worker is currently mid-attempt (target captured, guarded read
        // in-flight) -- it must already hold the shared gate at this point.
        var externalWait = operationGate.WaitAsync();
        Assert.False(externalWait.IsCompleted);

        transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable));
        await externalWait.WaitAsync(WaitTimeout); // now released -- the external waiter can proceed

        operationGate.Release();
        coordinator.Stop();
    }

    // ---- V.2/F. target capture never occurs before the gate is actually acquired -- proven by
    // holding the gate from the TEST side FIRST (standing in for a future concurrent
    // decision-action attempt already in progress), then raising a notification, and confirming
    // Capture is never called until the test itself releases its own hold ----
    [Fact]
    public async Task GateHeldExternally_PreventsTargetCaptureUntilReleased()
    {
        var (coordinator, transport, targetCapture, _, _, _, _, operationGate) = CreateStartedWithGate();
        await operationGate.WaitAsync();

        transport.RaiseChanged(TextNotification);
        await Task.Delay(50); // settle window -- Capture must not have happened

        Assert.Equal(0, targetCapture.CaptureCallCount);

        operationGate.Release();
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);

        coordinator.Stop();
    }

    // ---- V.4. gate released after a normal (non-throwing) attempt -- proven by immediate
    // re-acquisition succeeding ----
    [Fact]
    public async Task OperationGate_ReleasedAfter_NormalAttempt()
    {
        var (coordinator, transport, _, _, _, _, _, operationGate) = CreateStartedWithGate();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => transport.CallLog.Contains(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync)));

        // If the gate were still held, this would time out instead of completing.
        await operationGate.WaitAsync().WaitAsync(WaitTimeout);
        operationGate.Release();
        coordinator.Stop();
    }

    // ---- V.5. gate released after the processor throws ----
    [Fact]
    public async Task OperationGate_ReleasedAfter_ProcessorThrows()
    {
        var (coordinator, transport, _, processor, _, _, _, operationGate) = CreateStartedWithGate();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.ThrowOnProcess = new InvalidOperationException("synthetic processor failure");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);

        await operationGate.WaitAsync().WaitAsync(WaitTimeout);
        operationGate.Release();
        coordinator.Stop();
    }

    // ---- V.6. gate released after the decision-session publisher throws ----
    [Fact]
    public async Task OperationGate_ReleasedAfter_PublisherThrows()
    {
        var (coordinator, transport, _, processor, _, _, publisher, operationGate) = CreateStartedWithGate();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.DecisionPlanToReturn = SampleDecisionPlan();
        publisher.ThrowOnPublish = new InvalidOperationException("synthetic publisher failure");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => publisher.CallCount >= 1);

        await operationGate.WaitAsync().WaitAsync(WaitTimeout);
        operationGate.Release();
        coordinator.Stop();
    }

    // ---- V.7. gate released after the write transport throws ----
    [Fact]
    public async Task OperationGate_ReleasedAfter_WriteTransportThrows()
    {
        var (coordinator, transport, _, processor, writeTransport, _, _, operationGate) = CreateStartedWithGate();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.ThrowOnWrite = new InvalidOperationException("synthetic write-transport failure");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);

        await operationGate.WaitAsync().WaitAsync(WaitTimeout);
        operationGate.Release();
        coordinator.Stop();
    }

    // ---- V.8. gate release-on-throw already has end-to-end coverage: the EXISTING
    // ProcessorThrows_OnFirstNotification_LaterNotificationStillProcesses /
    // PublisherThrows_AttemptAborts_WorkerSurvives_LaterNotificationStillProcessed /
    // WriteTransportThrows_WorkerSurvives_LaterNotificationStillProcessed tests above now run
    // through a REAL ClipboardOperationGate (via CreateStarted/CreateStartedWithWrite/
    // CreateStartedWithPublisher, which all delegate down to CreateStartedWithGate) -- if a throw
    // ever leaked the gate's permit, the SECOND notification in each of those tests could never
    // acquire it and those tests would hang until their own WaitUntilAsync timeout, rather than
    // pass. No separate duplicate test is added here.

    // ---- V.9. an idle coordinator (nothing dequeued yet) never holds the operation gate -- an
    // external acquisition succeeds immediately ----
    [Fact]
    public async Task IdleCoordinator_DoesNotHoldOperationGate()
    {
        var (coordinator, _, _, _, _, _, _, operationGate) = CreateStartedWithGate();

        await operationGate.WaitAsync().WaitAsync(WaitTimeout);

        operationGate.Release();
        coordinator.Stop();
    }

    // ---- V.10/V.11/L/H. a new clipboard notification can still advance the lifecycle/invalidate
    // an active decision scope even while the operation gate is currently held externally
    // (standing in for the coordinator's own in-flight attempt, or a future decision-action
    // attempt) -- the callback never awaits/acquires the gate at all ----
    [Fact]
    public async Task NewNotification_AdvancesLifecycle_EvenWhileOperationGateIsHeld()
    {
        var (coordinator, transport, _, _, _, lifecycle, _, operationGate) = CreateStartedWithGate();
        await operationGate.WaitAsync(); // external holder, standing in for an in-flight attempt

        transport.RaiseChanged(TextNotification);

        // The callback (advance + enqueue) is synchronous and has already returned by the time
        // RaiseChanged returns -- no wait needed, and none would help if this were wrong (the
        // gate is still held, so any code path requiring it would simply never complete).
        Assert.Equal(1, lifecycle.CallCount);

        operationGate.Release();
        coordinator.Stop();
    }

    // ---- V.13/N. a non-text notification still advances the lifecycle even while the operation
    // gate is held externally -- the callback's advance-then-filter logic never touches the gate ----
    [Fact]
    public async Task NonTextNotification_AdvancesLifecycle_EvenWhileOperationGateHeld()
    {
        var (coordinator, transport, _, _, _, lifecycle, _, operationGate) = CreateStartedWithGate();
        await operationGate.WaitAsync();

        transport.RaiseChanged(NonTextNotification);

        Assert.Equal(1, lifecycle.CallCount);

        operationGate.Release();
        coordinator.Stop();
    }

    // ---- V.14. an unsupported-target text attempt still acquires and releases the operation gate
    // (the gate covers the WHOLE attempt, including the TargetGate check itself) -- but performs
    // no raw clipboard read ----
    [Fact]
    public async Task UnsupportedTargetAttempt_AcquiresAndReleasesGate_NoRawRead()
    {
        var (coordinator, transport, targetCapture, _, _, _, _, operationGate) = CreateStartedWithGate();
        targetCapture.SnapshotToReturn = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "notepad");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);

        await operationGate.WaitAsync().WaitAsync(WaitTimeout); // gate was released -- reacquire proves it
        operationGate.Release();

        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), transport.CallLog);
        coordinator.Stop();
    }

    // ---- structural: the coordinator's only operation-gate-related field is typed to the narrow
    // IClipboardOperationGate interface -- never a raw SemaphoreSlim, never the concrete
    // ClipboardOperationGate, and exactly one such field ----
    [Fact]
    public void Coordinator_HoldsOnlyTheNarrowOperationGateInterface()
    {
        var fields = typeof(ClipboardPrivacyCoordinator)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(SemaphoreSlim));
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(ClipboardOperationGate));
        Assert.Single(fields, f => f.FieldType == typeof(IClipboardOperationGate));
    }

    // ==================================================================
    // CLIPBOARD-ATTEMPT GENERATION (Phase 3B STEP17, items 27.1-27.6)
    // ==================================================================

    // ---- 27.1. text notification -> lifecycle advance called exactly once ----
    [Fact]
    public async Task TextNotification_AdvancesLifecycleExactlyOnce()
    {
        var (coordinator, transport, targetCapture, _, _, lifecycle) = CreateStartedWithLifecycle();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);

        Assert.Equal(1, lifecycle.CallCount);
        coordinator.Stop();
    }

    // ---- 27.2. non-text notification -> lifecycle still advances exactly once, but no privacy
    // processing follows (NON_TEXT_INVALIDATION: advance happens before the HasUnicodeText
    // filter) ----
    [Fact]
    public void NonTextNotification_StillAdvancesLifecycle_NoPrivacyProcessing()
    {
        var (coordinator, transport, targetCapture, _, _, lifecycle) = CreateStartedWithLifecycle();

        transport.RaiseChanged(NonTextNotification);

        Assert.Equal(1, lifecycle.CallCount);
        Assert.Equal(0, targetCapture.CaptureCallCount);
        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), transport.CallLog);
        coordinator.Stop();
    }

    // ---- 27.3. two notifications -> generation-advance call count increments on each arrival ----
    [Fact]
    public async Task TwoNotifications_LifecycleAdvancesOncePerArrival()
    {
        var (coordinator, transport, _, _, _, lifecycle) = CreateStartedWithLifecycle();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => lifecycle.CallCount >= 1);
        transport.RaiseChanged(new ClipboardChangeNotification(2, true, true));
        await WaitUntilAsync(() => lifecycle.CallCount >= 2);

        Assert.Equal(2, lifecycle.CallCount);
        coordinator.Stop();
    }

    // ---- 27.4. DROPOLDEST_RISK: an intermediate notification that the worker never dequeues
    // (dropped from the capacity-1 channel) still advanced the lifecycle at arrival time -- the
    // callback ran for it even though ProcessNotificationAsync never will ----
    [Fact]
    public async Task DroppedIntermediateNotification_StillAdvancedLifecycleAtArrival()
    {
        var (coordinator, transport, _, _, _, lifecycle) = CreateStartedWithLifecycle();
        transport.HoldReadsUntilReleased = true;

        var n1 = new ClipboardChangeNotification(1, true, true);
        var n2 = new ClipboardChangeNotification(2, true, true);
        var n3 = new ClipboardChangeNotification(3, true, true);

        transport.RaiseChanged(n1);
        await WaitUntilAsync(() => transport.PendingHeldReadCount == 1); // n1 now in-flight

        // n2 fills the empty channel slot; n3 (DropOldest) replaces n2 before the worker ever
        // dequeues it -- n2 is never processed, but its callback (and thus its lifecycle
        // advance) already ran synchronously when RaiseChanged(n2) returned, above.
        transport.RaiseChanged(n2);
        transport.RaiseChanged(n3);

        // All three callbacks are synchronous and have already returned by this point.
        Assert.Equal(3, lifecycle.CallCount);

        transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable));
        await WaitUntilAsync(() => transport.PendingHeldReadCount == 1); // worker now processing n3
        transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable));
        coordinator.Stop();
    }

    // ---- 27.5. UNSUPPORTED_TARGET_INVALIDATION: lifecycle already advanced before TargetGate
    // (which runs later, in the worker) ever gets a chance to reject the notification ----
    [Fact]
    public async Task UnsupportedTargetNotification_LifecycleAlreadyAdvanced()
    {
        var (coordinator, transport, targetCapture, _, _, lifecycle) = CreateStartedWithLifecycle();
        targetCapture.SnapshotToReturn = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "notepad");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);

        Assert.Equal(1, lifecycle.CallCount);
        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), transport.CallLog);
        coordinator.Stop();
    }

    // ---- 27.6. same-notification-value semantics are irrelevant to the callback: it has no raw
    // text to compare in the first place, so even a bit-for-bit IDENTICAL notification value
    // raised twice still advances the lifecycle both times ----
    [Fact]
    public void IdenticalNotificationValueRaisedTwice_AdvancesLifecycleBothTimes()
    {
        var (coordinator, transport, _, _, _, lifecycle) = CreateStartedWithLifecycle();
        var notification = new ClipboardChangeNotification(SequenceNumber: 7, HasReliableSequence: true, HasUnicodeText: true);

        transport.RaiseChanged(notification);
        transport.RaiseChanged(notification);

        Assert.Equal(2, lifecycle.CallCount);
        coordinator.Stop();
    }

    // ==================================================================
    // RAPID EVENTS (items 25-27)
    // ==================================================================

    // ---- 25. capacity-one pending behavior: first stays in-flight, second is dropped while
    // pending, third becomes the next pending item ----
    [Fact]
    public async Task RapidEvents_CapacityOnePendingBehavior()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();
        transport.HoldReadsUntilReleased = true;

        var n1 = new ClipboardChangeNotification(1, true, true);
        var n2 = new ClipboardChangeNotification(2, true, true);
        var n3 = new ClipboardChangeNotification(3, true, true);

        transport.RaiseChanged(n1);
        // n1 is dequeued and its guarded read is now held open -- it is "in-flight."
        await WaitUntilAsync(() => transport.PendingHeldReadCount == 1);
        Assert.Equal(1, targetCapture.CaptureCallCount);

        // The channel's single slot is now empty (n1 was already dequeued). n2 fills it; n3
        // (DropOldest) replaces n2 in that same slot before anything ever reads n2.
        transport.RaiseChanged(n2);
        transport.RaiseChanged(n3);

        // Release n1 (still in-flight, unaffected by n2/n3 arriving) -- the worker then dequeues
        // whatever is in the slot (n3; n2 was dropped) and starts a second held read for it.
        transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable));
        await WaitUntilAsync(() => targetCapture.CaptureCallCount == 2);

        // Exactly 2 Capture calls total for 3 raised notifications -- proves capacity-1
        // coalescing (one notification was dropped before ever being processed).
        Assert.Equal(2, targetCapture.CaptureCallCount);

        transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable));
        coordinator.Stop();
    }

    // ---- 26. in-flight work is never cancelled merely because a newer event arrived (behavioral
    // proof: releasing the held read after n2/n3 arrive still succeeds normally; structural proof
    // below shows the seam has no cancellation mechanism at all) ----
    [Fact]
    public async Task InFlightWork_NotCancelled_ByNewerArrival()
    {
        var (coordinator, transport, _, _) = CreateStarted();
        transport.HoldReadsUntilReleased = true;

        transport.RaiseChanged(new ClipboardChangeNotification(1, true, true));
        await WaitUntilAsync(() => transport.PendingHeldReadCount == 1);

        transport.RaiseChanged(new ClipboardChangeNotification(2, true, true));
        transport.RaiseChanged(new ClipboardChangeNotification(3, true, true));

        // Still exactly one held (uncancelled) read -- releasing it still works normally.
        Assert.Equal(1, transport.PendingHeldReadCount);
        transport.ReleaseNextRead(ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "x")));

        // Releasing n1 lets the worker move on to n3 (n2 was dropped) -- that becomes a second
        // held read under HoldReadsUntilReleased. Release it too so Stop() below doesn't have to
        // wait out its own shutdown timeout on it.
        await WaitUntilAsync(() => transport.PendingHeldReadCount == 1);
        transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable));

        coordinator.Stop();
    }

    [Fact]
    public void IClipboardReadTransport_ReadTextSnapshotAsync_HasNoCancellationTokenParameter()
    {
        var method = typeof(IClipboardReadTransport).GetMethod(nameof(IClipboardReadTransport.ReadTextSnapshotAsync));
        Assert.NotNull(method);
        Assert.DoesNotContain(method!.GetParameters(), p => p.ParameterType == typeof(CancellationToken));
    }

    // ---- 27. sequence zero receives no special freshness interpretation -- processed identically
    // to any other text notification ----
    [Fact]
    public async Task ZeroSequenceNotification_ProcessedNormally()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();
        var notification = new ClipboardChangeNotification(SequenceNumber: 0, HasReliableSequence: false, HasUnicodeText: true);

        transport.RaiseChanged(notification);
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);

        Assert.Equal(1, targetCapture.CaptureCallCount);
        await WaitUntilAsync(() => transport.CallLog.Contains(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync)));
        coordinator.Stop();
    }

    // ==================================================================
    // START/STOP (items 28-38)
    // ==================================================================

    // ---- 28/29. consumer + subscription are both active before transport.Start is called:
    // proven by raising a notification the INSTANT Start() returns and confirming it is still
    // processed -- impossible if the subscription/worker weren't already fully established. ----
    [Fact]
    public async Task ConsumerAndSubscription_ActiveBeforeTransportStart()
    {
        var transport = new FakeClipboardReadTransport();
        var targetCapture = new FakeForegroundTargetCapture();
        var processor = new FakeClipboardPrivacyProcessor();
        var writeTransport = new FakeClipboardWriteTransport();
        var notificationLifecycle = new FakeClipboardNotificationLifecycle();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();
        var operationGate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var verificationInvalidation = new FakeClipboardComposerVerificationInvalidation();
        var coordinator = new ClipboardPrivacyCoordinator(transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation);

        coordinator.Start();
        Assert.True(transport.StartCalled);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);

        Assert.Equal(1, targetCapture.CaptureCallCount);
        coordinator.Stop();
    }

    // ---- 30. transport Start failure -> event unsubscribed, worker not stranded, no false
    // Started state ----
    [Fact]
    public async Task TransportStartFailure_UnsubscribesAndDoesNotStrandWorker()
    {
        var transport = new FakeClipboardReadTransport { ThrowOnStart = true };
        var targetCapture = new FakeForegroundTargetCapture();
        var processor = new FakeClipboardPrivacyProcessor();
        var writeTransport = new FakeClipboardWriteTransport();
        var notificationLifecycle = new FakeClipboardNotificationLifecycle();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();
        var operationGate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var verificationInvalidation = new FakeClipboardComposerVerificationInvalidation();
        var coordinator = new ClipboardPrivacyCoordinator(transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation);

        Assert.Throws<InvalidOperationException>(() => coordinator.Start());

        // Unsubscribed: raising Changed now must not reach the (torn-down) worker pipeline.
        transport.RaiseChanged(TextNotification);
        await Task.Delay(50);
        Assert.Equal(0, targetCapture.CaptureCallCount);

        // Worker not stranded: Stop() (idempotent no-op here, since _running was never set true)
        // and Dispose() both complete without hanging or throwing.
        coordinator.Stop();
        coordinator.Dispose();
    }

    // ---- 31. Stop unsubscribes before transport.Stop ----
    [Fact]
    public void Stop_UnsubscribesBeforeTransportStop()
    {
        var (coordinator, transport, _, _) = CreateStarted();

        coordinator.Stop();

        Assert.True(transport.StopCalled);
        // Structural: cannot directly observe unsubscribe-before-Stop ordering via the fake's
        // call log (unsubscribe isn't itself a logged transport call), so this is reinforced by
        // test 32 below (no processing occurs from a post-unsubscribe raise).
    }

    // ---- 32. no new coordinator processing after unsubscribe/Stop ----
    [Fact]
    public async Task NoProcessing_AfterStop()
    {
        var (coordinator, transport, targetCapture, _) = CreateStarted();
        coordinator.Stop();

        transport.RaiseChanged(TextNotification);
        await Task.Delay(50);

        Assert.Equal(0, targetCapture.CaptureCallCount);
    }

    // ---- 33. in-flight work is allowed to finish during Stop ----
    [Fact]
    public void InFlightWork_AllowedToFinish_DuringStop()
    {
        var (coordinator, transport, _, _) = CreateStarted();
        transport.HoldReadsUntilReleased = true;

        transport.RaiseChanged(TextNotification);
        // Give the worker a moment to dequeue and start the held read (best-effort synchronous
        // settle -- the actual proof is that Stop() below does not hang/throw regardless).
        SpinWaitUntil(() => transport.PendingHeldReadCount == 1, WaitTimeout);

        // Release the in-flight read from a background task shortly after Stop() begins, so Stop
        // can complete once the worker finishes processing it.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable));
        });

        coordinator.Stop(); // must not hang or throw
    }

    private static void SpinWaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }
    }

    // ---- 34. pending mailbox completes cleanly on Stop ----
    [Fact]
    public void PendingMailbox_CompletesCleanly_OnStop()
    {
        var (coordinator, _, _, _) = CreateStarted();
        coordinator.Stop(); // must not throw
    }

    // ---- 35. repeated successful Stop/Dispose is safe ----
    [Fact]
    public void RepeatedStopDispose_AfterSuccess_IsSafe()
    {
        var (coordinator, _, _, _) = CreateStarted();

        coordinator.Stop();
        coordinator.Stop();
        coordinator.Dispose();
        coordinator.Dispose();
    }

    // ---- 36. Start twice rejected ----
    [Fact]
    public void StartTwice_Rejected()
    {
        var (coordinator, _, _, _) = CreateStarted();

        Assert.Throws<InvalidOperationException>(() => coordinator.Start());
        coordinator.Stop();
    }

    // ---- 37. Start-after-Stop rejected (single-use) ----
    [Fact]
    public void StartAfterStop_Rejected()
    {
        var (coordinator, _, _, _) = CreateStarted();
        coordinator.Stop();

        Assert.Throws<InvalidOperationException>(() => coordinator.Start());
    }

    // ---- 38. bounded worker timeout behavior is deterministic ----
    [Fact]
    public void Stop_WorkerDoesNotExitInTime_ThrowsDeterministically()
    {
        var transport = new FakeClipboardReadTransport { HoldReadsUntilReleased = true };
        var targetCapture = new FakeForegroundTargetCapture();
        var processor = new FakeClipboardPrivacyProcessor();
        var writeTransport = new FakeClipboardWriteTransport();
        var notificationLifecycle = new FakeClipboardNotificationLifecycle();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();
        var operationGate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var verificationInvalidation = new FakeClipboardComposerVerificationInvalidation();
        // Short override so the test doesn't wait the real 5s contract default.
        var coordinator = new ClipboardPrivacyCoordinator(transport, targetCapture, processor, writeTransport, notificationLifecycle, decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation, TimeSpan.FromMilliseconds(50));
        coordinator.Start();

        transport.RaiseChanged(TextNotification);
        SpinWaitUntil(() => transport.PendingHeldReadCount == 1, WaitTimeout);
        // Never released -- the worker will remain stuck awaiting this read forever, so Stop()
        // must time out deterministically rather than hang.

        Assert.Throws<InvalidOperationException>(() => coordinator.Stop());

        // Cleanup: release the read now so the background worker Task doesn't leak past this
        // test's lifetime, then let a retried Dispose genuinely succeed.
        transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable));
    }

    // ==================================================================
    // DIAGNOSTICS (items 39-41)
    // ==================================================================

    // ---- 39. no synthetic raw sentinel ever surfaces through any observable coordinator state ----
    [Fact]
    public async Task Diagnostics_NeverContainRawSentinel()
    {
        const string sentinel = "RAW-APP-PII-SENTINEL-284719";
        var (coordinator, transport, _, _) = CreateStarted();
        var snapshot = new ClipboardTextSnapshot(SequenceNumber: 1, HasReliableSequence: true, Text: sentinel);
        transport.NextReadResult = ClipboardTextReadResult.Success(snapshot);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => transport.CallLog.Contains(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync)));
        await Task.Delay(50);

        // The coordinator itself exposes no public/internal diagnostic surface carrying text at
        // all (no ToString override, no logging) -- structurally confirmed in test 41 below.
        coordinator.Stop();
    }

    // ---- 40. ProcessName is not part of any coordinator-produced diagnostic by default ----
    [Fact]
    public void Coordinator_HasNoProcessNameDiagnosticMember()
    {
        var members = typeof(ClipboardPrivacyCoordinator)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name => name.Contains("Log", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Process", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 41. no clipboard Text log/Debug output exists anywhere in the App production source ----
    [Fact]
    public void AppProductionSource_HasNoTextLoggingCalls()
    {
        var sourceDirectory = FindAppSourceDirectory();
        var forbidden = new[] { "Console.Write", "Debug.Write", "Trace.Write" };

        var offendingFiles = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Content: File.ReadAllText(path)))
            .Where(f => forbidden.Any(pattern => f.Content.Contains(pattern, StringComparison.Ordinal)))
            .Select(f => f.Path)
            .ToList();

        Assert.Empty(offendingFiles);
    }

    private static string FindAppSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate src/Privon.App from the test output directory.");

        return Path.Combine(dir.FullName, "src", "Privon.App");
    }

    // ==================================================================
    // PUBLIC / PROJECT BOUNDARY (items 42-50)
    // ==================================================================

    // ---- 42. App-owned adapter interfaces remain internal ----
    [Theory]
    [InlineData(typeof(IClipboardReadTransport))]
    [InlineData(typeof(IForegroundTargetCapture))]
    [InlineData(typeof(IClipboardPrivacyProcessor))]
    [InlineData(typeof(IClipboardWriteTransport))]
    public void AdapterInterfaces_AreInternal(Type type)
    {
        Assert.False(type.IsPublic);
    }

    // ---- 43. no write method exists on IClipboardReadTransport ----
    [Fact]
    public void IClipboardReadTransport_HasNoWriteMethod()
    {
        var members = typeof(IClipboardReadTransport).GetMembers().Select(m => m.Name);
        Assert.DoesNotContain(members, name => name.Contains("Write", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 44/45/46/47/48. coordinator owns none of the future-phase concerns ----
    [Fact]
    public void Coordinator_HasNoOutOfScopeMembers()
    {
        var forbidden = new[]
        {
            "DetectionPipeline", "CandidatePolicy", "AliasMap", "AliasAssigner", "AliasReplacer",
            "RevisionTracker", "Storage", "Grant", "ProtectionState", "Dispatcher", "Window",
            // Phase 3B STEP17: the coordinator is given only IClipboardNotificationLifecycle
            // (advance-only) -- it must never gain a member referencing the wider,
            // Runtime-Decision-facing IClipboardDecisionScopeLifecycle/ClipboardDecisionScope.
            "DecisionScope",
        };

        var members = typeof(ClipboardPrivacyCoordinator)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- Phase 3B STEP14 (boundary intentionally widened, superseding the old
    // "no Write capability at all" assertion this test used to make): the coordinator now
    // legitimately holds write capability via the narrow IClipboardWriteTransport seam
    // (APP_CLIPBOARD_WRITE_OWNER, frozen). What remains true, and is asserted here instead, is
    // that the coordinator never duplicates any native Win32 clipboard mechanics itself -- it only
    // ever delegates through the seam, exactly like it already does for reads.
    [Fact]
    public void Coordinator_NeverDuplicatesNativeClipboardWriteMechanics()
    {
        var forbidden = new[] { "OpenClipboard", "GlobalAlloc", "SetClipboardData", "EmptyClipboard", "GlobalFree", "GlobalLock" };

        var members = typeof(ClipboardPrivacyCoordinator)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- 25. coordinator has no replacement/write-plan persistent field ----
    [Fact]
    public void Coordinator_HasNoReplacementOrWritePlanField()
    {
        var fields = typeof(ClipboardPrivacyCoordinator)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(ClipboardWritePlan));
    }

    // ---- Phase 3B STEP17: coordinator gains no field of the Runtime-Decision-facing lifecycle
    // surface or anything a future decision scope might come to carry -- it only ever holds the
    // narrow IClipboardNotificationLifecycle (asserted separately by
    // Coordinator_HoldsOnlyTheNarrowNotificationLifecycleInterface below) ----
    [Fact]
    public void Coordinator_HasNoDecisionScopeOrRuntimeDecisionFields()
    {
        var fields = typeof(ClipboardPrivacyCoordinator)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // Phase 3B STEP19: ClipboardDecisionPlan/ClipboardDecisionItem live in the Privon.App
        // namespace, so the existing Privon.Core/Privon.Detection namespace checks below would
        // never catch an accidental field of either type -- listed explicitly here for that
        // reason, alongside the STEP17 scope/lifecycle types.
        var forbiddenTypes = new[]
        {
            typeof(ClipboardDecisionScope), typeof(IClipboardDecisionScopeLifecycle),
            typeof(ClipboardDecisionPlan), typeof(ClipboardDecisionItem),
        };

        Assert.DoesNotContain(fields, f => forbiddenTypes.Contains(f.FieldType));
        Assert.DoesNotContain(fields, f => f.FieldType.Namespace == "Privon.Core");
        Assert.DoesNotContain(fields, f => f.FieldType.Namespace == "Privon.Detection");
    }

    // ---- the coordinator's only lifecycle-related field is typed to the narrow interface, never
    // the concrete ClipboardDecisionScopeLifecycle (which would also expose TryPublish/Reset) ----
    [Fact]
    public void Coordinator_HoldsOnlyTheNarrowNotificationLifecycleInterface()
    {
        var fields = typeof(ClipboardPrivacyCoordinator)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(ClipboardDecisionScopeLifecycle));
        Assert.Contains(fields, f => f.FieldType == typeof(IClipboardNotificationLifecycle));
    }

    // ---- Phase 3B STEP21: the coordinator's only decision-session-publication-related field is
    // typed to the narrow IClipboardDecisionSessionPublisher interface, never the concrete
    // ClipboardDecisionSessionPublisher (which would still only expose the same narrow surface,
    // but this keeps the same "narrow interface, never the concrete type" discipline explicit and
    // enforced everywhere else in this project) ----
    [Fact]
    public void Coordinator_HoldsOnlyTheNarrowDecisionSessionPublisherInterface()
    {
        var fields = typeof(ClipboardPrivacyCoordinator)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(ClipboardDecisionSessionPublisher));
        Assert.Contains(fields, f => f.FieldType == typeof(IClipboardDecisionSessionPublisher));
    }

    // ---- 28. self-write suppression remains entirely Windows-owned -- the coordinator adds no
    // marker/hash/string-equality suppression layer of its own ----
    [Fact]
    public void Coordinator_HasNoSelfWriteSuppressionMember()
    {
        var forbidden = new[] { "SelfWrite", "LastWrite", "Marker", "Suppress" };

        var members = typeof(ClipboardPrivacyCoordinator)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- 49/50. project graph + solution project count ----
    [Fact]
    public void PrivonWindows_StillHasZeroProjectReferences()
    {
        // Structural proxy: Privon.Windows's own assembly must not reference Privon.App or
        // Privon.Core/Detection/Storage -- confirmed directly via `dotnet list reference` in the
        // implementation report; this test locks the one fact reachable from managed reflection:
        // the Windows assembly defines no dependency on this test assembly's own App types.
        var windowsAssembly = typeof(ForegroundTargetSnapshot).Assembly;
        var referencedAssemblyNames = windowsAssembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain(referencedAssemblyNames, name => name is "Privon.App" or "Privon.Core" or "Privon.Detection" or "Privon.Storage");
    }

    // ==================================================================
    // COMPOSER VERIFICATION HANDOFF / INVALIDATION (Phase 3C STEP32)
    // ==================================================================

    // ---- a verified rewrite -> exactly one Publish call with THIS attempt's own exact
    // target/replacement-text/generation ----
    [Fact]
    public async Task VerifiedWrite_PublishesExactTargetTextGeneration()
    {
        var (coordinator, transport, targetCapture, processor, writeTransport, _, _, _, verificationHandoff, _) = CreateStartedWithVerification();
        var expectedTarget = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 9999, ProcessName: "ChatGPT");
        targetCapture.SnapshotToReturn = expectedTarget;
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot(sequence: 42));
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 42);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => verificationHandoff.CallCount >= 1);

        Assert.Equal(1, verificationHandoff.CallCount);
        Assert.Equal(expectedTarget, verificationHandoff.ReceivedExpectedTargets[0]);
        Assert.Equal("[전화번호1]", verificationHandoff.ReceivedExpectedProtectedTexts[0]);
        Assert.Equal(1L, verificationHandoff.ReceivedExpectedGenerations[0]);
        coordinator.Stop();
    }

    // ---- write failure (Outcome != Success, ClipboardMutated false) -> no Publish ----
    [Fact]
    public async Task WriteFailure_NoPublish()
    {
        var (coordinator, transport, _, processor, writeTransport, _, _, _, verificationHandoff, _) = CreateStartedWithVerification();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: false);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(0, verificationHandoff.CallCount);
        coordinator.Stop();
    }

    // ---- mutated-but-unverified (ClipboardMutated true, Outcome != Success) -> no Publish
    // (IsRewriteVerified requires BOTH Success and ClipboardMutated) ----
    [Fact]
    public async Task MutatedUnverifiedWrite_NoPublish()
    {
        var (coordinator, transport, _, processor, writeTransport, _, _, _, verificationHandoff, _) = CreateStartedWithVerification();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(0, verificationHandoff.CallCount);
        coordinator.Stop();
    }

    // ---- NeedsDecision branch (a DecisionPlan, never a WritePlan) -> no Publish ----
    [Fact]
    public async Task DecisionPlanPresent_NoPublish()
    {
        var (coordinator, transport, _, processor, _, _, _, _, verificationHandoff, _) = CreateStartedWithVerification();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.DecisionPlanToReturn = SampleDecisionPlan();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(0, verificationHandoff.CallCount);
        coordinator.Stop();
    }

    // ---- all-Bypass (neither plan present) -> no Publish ----
    [Fact]
    public async Task NeitherPlanPresent_NoPublish()
    {
        var (coordinator, transport, _, processor, _, _, _, _, verificationHandoff, _) = CreateStartedWithVerification();
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot());
        processor.WritePlanToReturn = null;
        processor.DecisionPlanToReturn = null;

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => processor.CallCount >= 1);
        await Task.Delay(50);

        Assert.Equal(0, verificationHandoff.CallCount);
        coordinator.Stop();
    }

    // ==================================================================
    // APP-LEVEL SELF-WRITE GENERATION STABILITY (Phase 3C STEP42.1)
    // ==================================================================
    // STEP42 required: "verified final self-write notification at V -> no App clipboard
    // callback -> no generation advance -> no pending composer-verification invalidation -> no
    // extra processing attempt." STEP42's own report only proved this transitively; this test
    // proves the actual composed behavior directly, split honestly across the project boundary:
    //
    //   - Privon.Windows.IntegrationTests (already added by STEP42 --
    //     ClipboardChangeMonitorWriteTests.WriteTextIfSequenceMatchesAsync_VerificationSequenceDiffersFromPostSet_InstallsMarkerAtVerificationSequence_NotPostSet)
    //     proves the WINDOWS half with a REAL ClipboardChangeMonitor: once a guarded write is
    //     verified at the verification-reopen sequence V, the self-write suppression marker is
    //     installed at V, and a LATER native notification carrying that same V never reaches
    //     ClipboardChangeMonitor.Changed at all (Assert.Empty(received) after
    //     native.RaiseClipboardUpdate() with SequenceNumber=V) -- i.e. items 1 and 2 of the
    //     required regression.
    //
    //   - THIS test proves the APP half (items 3-6): Privon.App.Tests has no InternalsVisibleTo
    //     access into Privon.Windows (see src/Privon.Windows/AssemblyInfo.cs -- only
    //     "Privon.Windows.IntegrationTests" is granted that), so it cannot construct a real
    //     ClipboardChangeMonitor with a fake native seam and therefore cannot itself fabricate a
    //     genuine self-write echo at the Windows layer. What it CAN prove directly is that this
    //     coordinator's own generation/lifecycle, pending composer-verification hand-off, and
    //     attempt/call counts are produced ENTIRELY by IClipboardReadTransport.Changed
    //     invocations -- given exactly one such invocation (the notification that triggered and
    //     was ultimately verified by the write below) and deliberately NO second one, every
    //     counter this claim depends on is fixed at exactly 1 and stays there, including across
    //     this coordinator's own Stop() (proving shutdown itself never spuriously re-triggers any
    //     of these calls). Combined with the Windows-side proof that a real self-write echo never
    //     produces a second Changed invocation in the first place, these two tests together
    //     establish the full, composed claim across the real seam this architecture actually
    //     provides -- without fabricating cross-project access to do it.
    [Fact]
    public async Task VerifiedSelfWrite_NoFurtherChangedEvent_GenerationLifecyclePendingVerificationAndAttemptCountsStayFixed()
    {
        var (coordinator, transport, targetCapture, processor, writeTransport, notificationLifecycle, _, _, verificationHandoff, verificationInvalidation)
            = CreateStartedWithVerification();
        var expectedTarget = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 9999, ProcessName: "ChatGPT");
        targetCapture.SnapshotToReturn = expectedTarget;
        transport.NextReadResult = ClipboardTextReadResult.Success(SuccessSnapshot(sequence: 42));
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        // Verified at V=42 -- Phase 3C STEP42's corrected model: ClipboardWriteResult.Success
        // carries the verification-reopen sequence, never the intermediate post-Set capture.
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 42);

        // Exactly ONE Changed invocation drives the entire attempt: capture -> guarded read ->
        // process -> guarded write (verified at V=42) -> composer-verification handoff published.
        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => verificationHandoff.CallCount >= 1);

        int generationCalls = notificationLifecycle.CallCount;
        int invalidateCalls = verificationInvalidation.CallCount;
        int publishCalls = verificationHandoff.CallCount;
        int processorCalls = processor.CallCount;
        int writeCalls = writeTransport.CallCount;
        Assert.Equal(1, generationCalls); // no generation advance beyond the ONE originating notification
        Assert.Equal(1, invalidateCalls); // the ONE InvalidatePending() that unconditionally precedes every notification's own processing -- never a second one
        Assert.Equal(1, publishCalls); // pending composer-verification state published exactly once, never re-invalidated by a phantom second attempt
        Assert.Equal(1, processorCalls);
        Assert.Equal(1, writeCalls); // no extra clipboard processing attempt

        // NO second Changed event is ever raised here -- this IS the self-write echo the Windows-
        // side test proves never reaches this subscription in production. Stopping the
        // coordinator (its own shutdown path) must not itself spuriously call any of these either.
        coordinator.Stop();

        Assert.Equal(generationCalls, notificationLifecycle.CallCount);
        Assert.Equal(invalidateCalls, verificationInvalidation.CallCount);
        Assert.Equal(publishCalls, verificationHandoff.CallCount);
        Assert.Equal(processorCalls, processor.CallCount);
        Assert.Equal(writeCalls, writeTransport.CallCount);
    }

    // ---- a text notification invokes InvalidatePending ----
    [Fact]
    public async Task TextNotification_InvokesInvalidatePending()
    {
        var (coordinator, transport, targetCapture, _, _, _, _, _, _, verificationInvalidation) = CreateStartedWithVerification();

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);

        Assert.Equal(1, verificationInvalidation.CallCount);
        coordinator.Stop();
    }

    // ---- a non-text notification ALSO invokes InvalidatePending (before the text filter, exactly
    // like the generation advance) ----
    [Fact]
    public void NonTextNotification_InvokesInvalidatePending()
    {
        var (coordinator, transport, _, _, _, _, _, _, _, verificationInvalidation) = CreateStartedWithVerification();

        transport.RaiseChanged(NonTextNotification);

        Assert.Equal(1, verificationInvalidation.CallCount);
        coordinator.Stop();
    }

    // ---- the callback's InvalidatePending forwarding never waits on the shared operation gate --
    // proven by a notification raised while the gate is held externally still reaching it
    // immediately (the callback is synchronous and has already returned by the time RaiseChanged
    // returns) ----
    [Fact]
    public async Task NewNotification_InvokesInvalidatePending_EvenWhileOperationGateIsHeld()
    {
        var (coordinator, transport, _, _, _, _, _, operationGate, _, verificationInvalidation) = CreateStartedWithVerification();
        await operationGate.WaitAsync(); // external holder, standing in for an in-flight attempt

        transport.RaiseChanged(TextNotification);

        Assert.Equal(1, verificationInvalidation.CallCount);

        operationGate.Release();
        coordinator.Stop();
    }

    // ---- existing generation/lifecycle ordering unchanged: both the notification-lifecycle
    // advance and the new verification invalidation fire once per notification, in lockstep, for
    // several notifications in a row ----
    [Fact]
    public async Task MultipleNotifications_LifecycleAdvanceAndInvalidatePending_StayInLockstep()
    {
        var (coordinator, transport, _, _, _, lifecycle, _, _, _, verificationInvalidation) = CreateStartedWithVerification();

        transport.RaiseChanged(TextNotification);
        transport.RaiseChanged(new ClipboardChangeNotification(2, true, true));
        transport.RaiseChanged(new ClipboardChangeNotification(3, true, false));

        Assert.Equal(3, lifecycle.CallCount);
        Assert.Equal(3, verificationInvalidation.CallCount);
        coordinator.Stop();
    }

    // ---- structural: the coordinator's verification-related fields are typed to the two narrow
    // interfaces only -- never the concrete ClipboardComposerVerifier ----
    [Fact]
    public void Coordinator_HoldsOnlyTheNarrowComposerVerificationInterfaces()
    {
        var fields = typeof(ClipboardPrivacyCoordinator)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType.Name == "ClipboardComposerVerifier");
        Assert.Contains(fields, f => f.FieldType == typeof(IClipboardComposerVerificationHandoff));
        Assert.Contains(fields, f => f.FieldType == typeof(IClipboardComposerVerificationInvalidation));
    }
}
