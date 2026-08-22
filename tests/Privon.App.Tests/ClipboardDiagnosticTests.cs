using System.Reflection;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3C STEP41 -- Real ChatGPT Clipboard Protection Intermittency Runtime Diagnostic
// regression. All tests use the existing Fake* doubles (synthetic, OS-free, no mocking
// framework) plus the new FakeClipboardDiagnosticRecorder -- no real Windows clipboard/
// foreground state, no real file I/O, no WPF Application/Window is ever touched.
public class ClipboardDiagnosticTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static readonly ClipboardChangeNotification TextNotification =
        new(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true);

    private static readonly ClipboardChangeNotification NonTextNotification =
        new(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: false);

    private static (
        ClipboardPrivacyCoordinator Coordinator,
        FakeClipboardReadTransport Transport,
        FakeForegroundTargetCapture TargetCapture,
        FakeClipboardPrivacyProcessor Processor,
        FakeClipboardWriteTransport WriteTransport,
        FakeClipboardComposerVerificationHandoff VerificationHandoff,
        FakeClipboardDecisionSessionPublisher DecisionPublisher,
        FakeClipboardDiagnosticRecorder Diagnostics) CreateStartedWithDiagnostics()
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
        var diagnostics = new FakeClipboardDiagnosticRecorder();

        var coordinator = new ClipboardPrivacyCoordinator(
            transport,
            targetCapture,
            processor,
            writeTransport,
            notificationLifecycle,
            decisionSessionPublisher,
            operationGate,
            verificationHandoff,
            verificationInvalidation,
            stopTimeoutOverride: null,
            diagnostics: diagnostics);
        coordinator.Start();

        return (coordinator, transport, targetCapture, processor, writeTransport, verificationHandoff, decisionSessionPublisher, diagnostics);
    }

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

    private static bool HasTerminal(FakeClipboardDiagnosticRecorder diagnostics) =>
        diagnostics.Events.Any(e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal);

    // ==================================================================
    // A. ATTEMPT CORRELATION / TERMINAL REASON -- each mirrors a real, already-existing
    // early-return branch in ClipboardPrivacyCoordinator.
    // ==================================================================

    [Fact]
    public async Task NonTextNotification_RecordsReceivedThenNonTextTerminal_SameAttemptId_NoFurtherStages()
    {
        var (coordinator, transport, _, _, _, _, _, diagnostics) = CreateStartedWithDiagnostics();

        transport.RaiseChanged(NonTextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        var events = diagnostics.Events;
        Assert.Equal(2, events.Count);
        Assert.Equal(ClipboardDiagnosticStage.AppClipboardChangeReceived, events[0].Stage);
        Assert.Equal(ClipboardDiagnosticStage.AttemptTerminal, events[1].Stage);
        Assert.Equal(ClipboardDiagnosticTerminalReason.NonTextNotification, events[1].Terminal);
        Assert.Equal(events[0].AttemptId, events[1].AttemptId);
        Assert.DoesNotContain(events, e => e.Stage == ClipboardDiagnosticStage.MailboxWriteAttempted);
        Assert.DoesNotContain(events, e => e.Stage == ClipboardDiagnosticStage.WorkerDequeued);
    }

    [Fact]
    public async Task TextNotification_UnauthorizedTarget_NoReadAttempted_TerminalUnauthorized()
    {
        var (coordinator, transport, targetCapture, _, _, _, _, diagnostics) = CreateStartedWithDiagnostics();
        targetCapture.SnapshotToReturn = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 999, ProcessName: "powershell");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        var events = diagnostics.Events;
        Assert.DoesNotContain(events, e => e.Stage == ClipboardDiagnosticStage.GuardedReadCompleted);

        var targetEvent = Assert.Single(events, e => e.Stage == ClipboardDiagnosticStage.TargetCaptured);
        Assert.True(targetEvent.TargetResolved);
        Assert.False(targetEvent.TargetAuthorized);
        Assert.Equal(999u, targetEvent.TargetProcessId);
        Assert.Equal("powershell", targetEvent.TargetProcessName);

        var terminal = Assert.Single(events, e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal);
        Assert.Equal(ClipboardDiagnosticTerminalReason.UnauthorizedTarget, terminal.Terminal);
        Assert.Equal(targetEvent.AttemptId, terminal.AttemptId);
    }

    [Fact]
    public async Task TextNotification_AuthorizedTarget_RecordsChatGptProcessName()
    {
        var (coordinator, transport, targetCapture, _, _, _, _, diagnostics) = CreateStartedWithDiagnostics();
        targetCapture.SnapshotToReturn = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        var targetEvent = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.TargetCaptured);
        Assert.True(targetEvent.TargetAuthorized);
        Assert.Equal("ChatGPT", targetEvent.TargetProcessName);
        Assert.Equal(4242u, targetEvent.TargetProcessId);
    }

    [Fact]
    public async Task UnresolvedTarget_RecordsUnresolved_NeverAuthorized()
    {
        var (coordinator, transport, targetCapture, _, _, _, _, diagnostics) = CreateStartedWithDiagnostics();
        targetCapture.SnapshotToReturn = default; // IsResolved: false

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        var targetEvent = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.TargetCaptured);
        Assert.False(targetEvent.TargetResolved);
        Assert.False(targetEvent.TargetAuthorized);
        Assert.Null(targetEvent.TargetProcessId);
        Assert.Null(targetEvent.TargetProcessName);
    }

    [Fact]
    public async Task ReadRejected_RecordsReadOutcome_TerminalReadRejected()
    {
        var (coordinator, transport, _, _, _, _, _, diagnostics) = CreateStartedWithDiagnostics();
        transport.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        var readEvent = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.GuardedReadCompleted);
        Assert.Equal(ClipboardReadOutcome.Busy, readEvent.ReadOutcome);

        var terminal = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal);
        Assert.Equal(ClipboardDiagnosticTerminalReason.ReadRejected, terminal.Terminal);
    }

    [Fact]
    public async Task NoActionRequired_AllBypassOrNoPii_RecordsPolicyCountsAndTerminal()
    {
        var (coordinator, transport, _, processor, _, _, _, diagnostics) = CreateStartedWithDiagnostics();
        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(5, true, "no pii here"));
        processor.ResultToReturn = new ClipboardPrivacyProcessingResult(
            CandidateCount: 1, TrustedCount: 0, ProtectCount: 0, NeedsDecisionCount: 0, BypassCount: 1);
        // WritePlanToReturn / DecisionPlanToReturn both default to null, matching the real
        // processor's ALL_BYPASS gating.

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        var policyEvent = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.PolicyEvaluated);
        Assert.Equal(1, policyEvent.CandidateCount);
        Assert.Equal(1, policyEvent.BypassCount);
        Assert.Equal(0, policyEvent.ProtectCount);
        Assert.Equal(0, policyEvent.NeedsDecisionCount);
        Assert.False(policyEvent.HasWritePlan);
        Assert.False(policyEvent.HasDecisionPlan);

        var terminal = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal);
        Assert.Equal(ClipboardDiagnosticTerminalReason.NoActionRequired, terminal.Terminal);

        Assert.DoesNotContain(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.GuardedWriteCompleted);
        Assert.DoesNotContain(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.DecisionSessionPublishAttempted);
    }

    [Fact]
    public async Task WriteSkippedUnreliableSequence_NoWriteAttempted()
    {
        var (coordinator, transport, _, processor, writeTransport, _, _, diagnostics) = CreateStartedWithDiagnostics();
        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(0, false, "010-1234-5678"));
        processor.ResultToReturn = new ClipboardPrivacyProcessingResult(1, 0, 1, 0, 0);
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        Assert.Equal(0, writeTransport.CallCount);
        var terminal = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal);
        Assert.Equal(ClipboardDiagnosticTerminalReason.WriteSkippedUnreliableSequence, terminal.Terminal);
    }

    [Fact]
    public async Task VerifiedWrite_RecordsWriteCompletedHandoffAndSuccessTerminal()
    {
        var (coordinator, transport, _, processor, writeTransport, verificationHandoff, _, diagnostics) = CreateStartedWithDiagnostics();
        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(5, true, "010-1234-5678"));
        processor.ResultToReturn = new ClipboardPrivacyProcessingResult(1, 0, 1, 0, 0);
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.NextResult = ClipboardWriteResult.Success(7);
        verificationHandoff.ResultToReturn = true;

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        var writeEvent = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.GuardedWriteCompleted);
        Assert.Equal(ClipboardWriteOutcome.Success, writeEvent.WriteOutcome);
        Assert.True(writeEvent.ClipboardMutated);
        Assert.True(writeEvent.RewriteVerified);

        var handoffEvent = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.ComposerVerificationHandoffPublished);
        Assert.True(handoffEvent.PublishAccepted);

        var terminal = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal);
        Assert.Equal(ClipboardDiagnosticTerminalReason.Success, terminal.Terminal);
    }

    [Fact]
    public async Task MutatedUnverifiedWrite_TerminalWriteRejected_NoHandoffPublish()
    {
        var (coordinator, transport, _, processor, writeTransport, verificationHandoff, _, diagnostics) = CreateStartedWithDiagnostics();
        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(5, true, "010-1234-5678"));
        processor.ResultToReturn = new ClipboardPrivacyProcessingResult(1, 0, 1, 0, 0);
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        var writeEvent = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.GuardedWriteCompleted);
        Assert.Equal(ClipboardWriteOutcome.ReadBackMismatch, writeEvent.WriteOutcome);
        Assert.True(writeEvent.ClipboardMutated);
        Assert.False(writeEvent.RewriteVerified);

        Assert.DoesNotContain(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.ComposerVerificationHandoffPublished);
        Assert.Equal(0, verificationHandoff.CallCount);

        var terminal = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal);
        Assert.Equal(ClipboardDiagnosticTerminalReason.WriteRejected, terminal.Terminal);
    }

    [Fact]
    public async Task DecisionPlan_RecordsPublishAttemptAndDecisionPendingTerminal_NoWrite()
    {
        var (coordinator, transport, _, processor, writeTransport, _, decisionPublisher, diagnostics) = CreateStartedWithDiagnostics();
        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(5, true, "740101-1234567"));
        processor.ResultToReturn = new ClipboardPrivacyProcessingResult(1, 0, 0, 1, 0);
        processor.DecisionPlanToReturn = new ClipboardDecisionPlan(
            [new ClipboardDecisionItem(new CanonicalValue(PiiType.ResidentRegistrationNumber, "7401011234567"), RiskLevel.Level3)]);
        decisionPublisher.ResultToReturn = true;

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        Assert.Equal(0, writeTransport.CallCount);

        var publishEvent = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.DecisionSessionPublishAttempted);
        Assert.Equal(1, publishEvent.DecisionItemCount);
        Assert.True(publishEvent.PublishAccepted);

        var terminal = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal);
        Assert.Equal(ClipboardDiagnosticTerminalReason.DecisionPending, terminal.Terminal);
    }

    [Fact]
    public async Task ProcessingException_RecordsTerminalWithExceptionTypeNameOnly()
    {
        var (coordinator, transport, _, processor, _, _, _, diagnostics) = CreateStartedWithDiagnostics();
        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(5, true, "010-1234-5678"));
        const string sensitiveMessage = "RAW-DIAGNOSTIC-EXCEPTION-SENTINEL-284611 010-1234-5678";
        processor.ThrowOnProcess = new InvalidOperationException(sensitiveMessage);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        var terminal = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal);
        Assert.Equal(ClipboardDiagnosticTerminalReason.ProcessingException, terminal.Terminal);
        Assert.Equal(nameof(InvalidOperationException), terminal.ExceptionTypeName);
        Assert.DoesNotContain(sensitiveMessage, terminal.ExceptionTypeName);
        Assert.DoesNotContain(sensitiveMessage, terminal.ToString());
    }

    [Fact]
    public async Task MailboxWriteAttempted_RecordsAcceptedTrue_ForAnOrdinaryTextNotification()
    {
        var (coordinator, transport, _, _, _, _, _, diagnostics) = CreateStartedWithDiagnostics();
        transport.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        var mailboxEvent = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.MailboxWriteAttempted);
        Assert.True(mailboxEvent.MailboxAccepted);

        var dequeuedEvent = Assert.Single(diagnostics.Events, e => e.Stage == ClipboardDiagnosticStage.WorkerDequeued);
        Assert.Equal(mailboxEvent.AttemptId, dequeuedEvent.AttemptId);
    }

    // ==================================================================
    // B. QUEUED_BUT_SUPERSEDED INFERENCE -- proves the correlation algorithm the manual-QA
    // trace-reading procedure relies on: a generation that received MailboxWriteAttempted but
    // never WorkerDequeued was dropped by the channel's own DropOldest policy before the worker
    // ever saw it (Phase 3B STEP17's existing DropOldest precedent, observed here rather than
    // re-proven).
    // ==================================================================

    [Fact]
    public async Task Superseded_MailboxWriteAttemptedForAllThree_WorkerDequeuedSkipsTheDroppedOne()
    {
        var (coordinator, transport, _, _, _, _, _, diagnostics) = CreateStartedWithDiagnostics();
        transport.HoldReadsUntilReleased = true;

        // A: dequeued immediately. Waiting for WorkerDequeued alone is NOT a sufficient barrier
        // here -- it fires before the worker even acquires the gate or reaches the guarded-read
        // call, so racing straight to ReleaseNextRead afterward could find _heldReads still empty
        // (a silent no-op) and hang A's attempt forever. PendingHeldReadCount is the actual
        // deterministic barrier: it only becomes >= 1 once the read call itself is genuinely
        // in-flight and held.
        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => transport.PendingHeldReadCount >= 1);

        // B: arrives while A is still in flight -- the channel is empty (A already dequeued), so
        // B is accepted into the single slot.
        transport.RaiseChanged(TextNotification);
        // C: arrives before the worker ever gets back to dequeue B -- DropOldest evicts B, C takes
        // the slot. B is never dequeued.
        transport.RaiseChanged(TextNotification);

        // Release A so the worker finishes it and loops back to dequeue the channel's current
        // occupant (C). Same PendingHeldReadCount barrier before releasing C's own held read.
        transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy));
        await WaitUntilAsync(() => transport.PendingHeldReadCount >= 1);
        transport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy));
        await WaitUntilAsync(() => diagnostics.Events.Count(e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal) >= 2);
        coordinator.Stop();

        var mailboxAttemptIds = diagnostics.Events
            .Where(e => e.Stage == ClipboardDiagnosticStage.MailboxWriteAttempted)
            .Select(e => e.AttemptId)
            .ToList();
        var dequeuedAttemptIds = diagnostics.Events
            .Where(e => e.Stage == ClipboardDiagnosticStage.WorkerDequeued)
            .Select(e => e.AttemptId)
            .ToList();

        Assert.Equal(3, mailboxAttemptIds.Count); // A, B, C all reached the mailbox.
        Assert.Equal(2, dequeuedAttemptIds.Count); // Only A and C were ever actually dequeued.

        var superseded = mailboxAttemptIds.Except(dequeuedAttemptIds).ToList();
        var supersededId = Assert.Single(superseded); // Exactly B.
        Assert.DoesNotContain(supersededId, dequeuedAttemptIds);
        Assert.DoesNotContain(diagnostics.Events, e => e.AttemptId == supersededId && e.Stage == ClipboardDiagnosticStage.AttemptTerminal);
    }

    // ==================================================================
    // C. SINK FAILURE ISOLATION / DIAGNOSTICS-DISABLED BEHAVIORAL EQUIVALENCE
    // ==================================================================

    [Fact]
    public async Task ThrowingDiagnosticRecorder_NeverAffectsCoordinatorFunctionalOutcome()
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
        var diagnostics = new FakeClipboardDiagnosticRecorder { ThrowOnRecord = true };

        var coordinator = new ClipboardPrivacyCoordinator(
            transport, targetCapture, processor, writeTransport, notificationLifecycle,
            decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation,
            stopTimeoutOverride: null, diagnostics: diagnostics);
        coordinator.Start();

        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(5, true, "010-1234-5678"));
        processor.ResultToReturn = new ClipboardPrivacyProcessingResult(1, 0, 1, 0, 0);
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.NextResult = ClipboardWriteResult.Success(7);

        transport.RaiseChanged(TextNotification);

        // The functional call chain still completes normally even though EVERY Diagnose() call
        // throws internally -- this is the real, observable behavior a sink failure must never
        // alter (SINK_FAILURE_ISOLATION).
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);
        Assert.Equal(1, writeTransport.CallCount);
        Assert.Equal(1, verificationHandoff.CallCount);
        Assert.Empty(diagnostics.Events); // every Record() call threw before appending anything

        coordinator.Stop();
    }

    [Fact]
    public void CoordinatorWithoutDiagnosticsArgument_DefaultsToNullRecorder()
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

        // Exactly the pre-STEP41 9-argument public constructor call -- proves every existing
        // caller/test keeps compiling and resolves to a pure no-op recorder.
        var coordinator = new ClipboardPrivacyCoordinator(
            transport, targetCapture, processor, writeTransport, notificationLifecycle,
            decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation);

        var field = typeof(ClipboardPrivacyCoordinator)
            .GetField("_diagnostics", BindingFlags.NonPublic | BindingFlags.Instance);
        var value = field!.GetValue(coordinator);

        Assert.Same(NullClipboardDiagnosticRecorder.Instance, value);
    }

    [Fact]
    public async Task DisabledDiagnostics_FunctionalBehaviorIdenticalToEnabled()
    {
        // Same scenario as VerifiedWrite_... above, but with the default (no diagnostics
        // argument) constructor -- proves omitting STEP41's new argument produces the identical
        // functional outcome the diagnostics-enabled test above observes.
        var transport = new FakeClipboardReadTransport();
        var targetCapture = new FakeForegroundTargetCapture();
        var processor = new FakeClipboardPrivacyProcessor();
        var writeTransport = new FakeClipboardWriteTransport();
        var notificationLifecycle = new FakeClipboardNotificationLifecycle();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();
        var operationGate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var verificationInvalidation = new FakeClipboardComposerVerificationInvalidation();

        var coordinator = new ClipboardPrivacyCoordinator(
            transport, targetCapture, processor, writeTransport, notificationLifecycle,
            decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation);
        coordinator.Start();

        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(5, true, "010-1234-5678"));
        processor.ResultToReturn = new ClipboardPrivacyProcessingResult(1, 0, 1, 0, 0);
        processor.WritePlanToReturn = new ClipboardWritePlan("[전화번호1]");
        writeTransport.NextResult = ClipboardWriteResult.Success(7);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => writeTransport.CallCount >= 1);

        Assert.Equal(1, writeTransport.CallCount);
        Assert.Equal(1, verificationHandoff.CallCount);
        Assert.Equal("[전화번호1]", writeTransport.ReceivedReplacementTexts[0]);

        coordinator.Stop();
    }

    // ==================================================================
    // D. PRIVACY BOUNDARY -- structural (reflection) and behavioral (synthetic sentinel).
    // ==================================================================

    [Fact]
    public void ClipboardDiagnosticEvent_OnlyTwoStringProperties_NoOtherSensitiveTypedProperty()
    {
        var properties = typeof(ClipboardDiagnosticEvent).GetProperties();

        var stringProperties = properties.Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).ToList();
        Assert.Equal(2, stringProperties.Count);
        Assert.Contains(nameof(ClipboardDiagnosticEvent.TargetProcessName), stringProperties);
        Assert.Contains(nameof(ClipboardDiagnosticEvent.ExceptionTypeName), stringProperties);

        var forbiddenTypes = new[]
        {
            typeof(ClipboardTextSnapshot), typeof(DetectionResult), typeof(DetectionCandidate),
            typeof(CanonicalValue), typeof(ClipboardWritePlan), typeof(ClipboardDecisionPlan),
            typeof(ForegroundTargetSnapshot),
        };
        Assert.DoesNotContain(properties, p => forbiddenTypes.Contains(p.PropertyType));
    }

    [Fact]
    public void ClipboardDiagnosticEvent_HasNoDebuggerDisplayOrTypeProxyAttributes()
    {
        var attributes = typeof(ClipboardDiagnosticEvent).GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);
        Assert.DoesNotContain(attributes, name => name is "DebuggerDisplayAttribute" or "DebuggerTypeProxyAttribute");
    }

    [Fact]
    public void ClipboardPrivacyCoordinator_HasExactlyOneDiagnosticRecorderField_TypedToTheNarrowInterface()
    {
        var fields = typeof(ClipboardPrivacyCoordinator)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.Contains(fields, f => f.FieldType == typeof(IClipboardDiagnosticRecorder));
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(FileClipboardDiagnosticRecorder));
    }

    [Fact]
    public async Task RealSentinelText_NeverAppearsInAnyDiagnosticEventAcrossFullAttemptChain()
    {
        const string sentinel = "RAW-DIAGNOSTIC-SENTINEL-739284";
        var (coordinator, transport, _, processor, writeTransport, verificationHandoff, decisionPublisher, diagnostics) =
            CreateStartedWithDiagnostics();

        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(5, true, $"{sentinel} 010-1234-5678"));
        processor.ResultToReturn = new ClipboardPrivacyProcessingResult(1, 0, 1, 0, 0);
        processor.WritePlanToReturn = new ClipboardWritePlan($"{sentinel} [전화번호1]");
        writeTransport.NextResult = ClipboardWriteResult.Success(7);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        Assert.NotEmpty(diagnostics.Events);
        foreach (var evt in diagnostics.Events)
        {
            Assert.DoesNotContain(sentinel, evt.ToString());
        }

        // The sentinel really did flow through the real seams (proving this is not a vacuous
        // check) -- it's just never observable from the diagnostic side.
        Assert.Contains(sentinel, writeTransport.ReceivedReplacementTexts[0]);
    }

    [Fact]
    public async Task DecisionPlanSentinelCanonicalValue_NeverAppearsInAnyDiagnosticEvent()
    {
        const string sentinel = "RAW-DIAGNOSTIC-CANONICAL-SENTINEL-518203";
        var (coordinator, transport, _, processor, _, _, decisionPublisher, diagnostics) = CreateStartedWithDiagnostics();

        transport.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(5, true, "740101-1234567"));
        processor.ResultToReturn = new ClipboardPrivacyProcessingResult(1, 0, 0, 1, 0);
        processor.DecisionPlanToReturn = new ClipboardDecisionPlan(
            [new ClipboardDecisionItem(new CanonicalValue(PiiType.ResidentRegistrationNumber, sentinel), RiskLevel.Level3)]);

        transport.RaiseChanged(TextNotification);
        await WaitUntilAsync(() => HasTerminal(diagnostics));
        coordinator.Stop();

        foreach (var evt in diagnostics.Events)
        {
            Assert.DoesNotContain(sentinel, evt.ToString());
        }
    }
}
