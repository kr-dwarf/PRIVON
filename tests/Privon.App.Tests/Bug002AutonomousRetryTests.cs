using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// BUG-002 Gate 2B -- deterministic RED evidence for the CONFIRMED S1 defect: a retryable clipboard
// failure abandons the current evaluation with no autonomous retry, so a still-valid protection
// opportunity (supported target still foreground, newest generation still holding RAW PII) can be
// missed forever when no later clipboard/foreground event happens to arrive.
//
// These tests encode the FINAL, closed Gate 2A contract:
//   - retry owner              = ClipboardPrivacyCoordinator
//   - budget                   = 4 total attempts (1 + 3 retries), fixed 250 ms delay, no backoff
//   - autonomous allowlist     = Busy / NativeFailure / TargetUnavailable / VerificationUnavailable
//   - never autonomous         = SequenceChanged / TargetChanged / Superseded / ReadBackMismatch /
//                                NotRunning / unexpected ProcessingException / anything unknown
//   - every retry revalidates  = phase still Running, pinned generation still current, fresh
//                                foreground capture, supported-target recheck, fresh lifecycle
//                                claim, fresh clipboard read, full pipeline re-run
//   - lifecycle transitions    = owned SOLELY by the single-attempt pipeline; the retry controller
//                                never calls CompleteEvaluation/AbandonEvaluation itself
//
// GATE_2B_SCOPE: the autonomous-retry loop described above is now implemented in production (see
// ClipboardPrivacyCoordinator.ProcessWorkItemAsync's own retry loop and
// ClipboardAutonomousRetryClassifier) -- every RED-### test below now passes, characterizing live
// behavior rather than a missing one; the RED-### naming is kept as historical record of the
// defect each test was originally written to prove. Every GUARD-### test below is, as always, a
// contract guard, not a RED.
//
// DETERMINISM: no test sleeps for a fixed duration. FakeClipboardRetryDelay either completes
// instantly or blocks until the test releases it, and the ClipboardOperationGate's own frozen
// whole-attempt hold is what makes "attempt 1 has completely finished" observable by dispatching a
// second, deliberately target-rejected work item (the worker cannot begin it until attempt 1's
// entire processing -- including any retry loop -- has released the gate).
//
// PRIVACY: synthetic RFC 2606 data only. No RAW value is ever logged or asserted through a
// diagnostic sink.
public class Bug002AutonomousRetryTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    // Gate 1A/1C-verified synthetic input and its real production alias
    // (AliasLabelProvider maps PiiType.Email -> "이메일").
    private const string RawSynthetic = "synthetic.report@example.com";
    private const string ProtectedAlias = "[이메일1]";

    private static readonly TimeSpan ContractRetryDelay = TimeSpan.FromMilliseconds(250);

    private static readonly ClipboardChangeNotification TextNotification =
        new(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true);

    // BUG-004 Gate 2G: migrated to carry the approved current-product package identity -- this
    // constant's entire purpose is "the officially supported target," so it is exactly the kind of
    // fixture the Gate 2G migration instructions require updating (never a negative/unsupported
    // fixture, which is what SupportedTarget's sibling below remains).
    private static readonly ForegroundTargetSnapshot SupportedTarget =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    private static readonly ForegroundTargetSnapshot UnsupportedTarget =
        new(IsResolved: true, ProcessId: 5150, ProcessName: "Notepad");

    // ==================================================================
    // Harness
    // ==================================================================

    private sealed class Harness : IDisposable
    {
        public required ClipboardPrivacyCoordinator Coordinator { get; init; }
        public required FakeClipboardReadTransport Transport { get; init; }
        public required FakeForegroundTargetCapture TargetCapture { get; init; }
        public required FakeClipboardPrivacyProcessor Processor { get; init; }
        public required FakeClipboardWriteTransport WriteTransport { get; init; }
        public required FakeClipboardNotificationLifecycle Lifecycle { get; init; }
        public required FakeClipboardComposerVerificationHandoff VerificationHandoff { get; init; }
        public required FakeClipboardRetryDelay RetryDelay { get; init; }

        public void Dispose()
        {
            // Never leave the worker parked inside a held fake delay -- releasing first keeps
            // teardown bounded and deterministic regardless of which state the test ended in.
            RetryDelay.BlockUntilReleased = false;
            RetryDelay.ReleaseAll();
            try { Coordinator.Dispose(); }
            catch { /* teardown only -- a lifecycle failure here must never mask the real assertion */ }
        }
    }

    private static Harness CreateStarted()
    {
        var transport = new FakeClipboardReadTransport();
        var targetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = SupportedTarget };
        var processor = new FakeClipboardPrivacyProcessor();
        var writeTransport = new FakeClipboardWriteTransport();
        var lifecycle = new FakeClipboardNotificationLifecycle();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();
        var operationGate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var verificationInvalidation = new FakeClipboardComposerVerificationInvalidation();
        var retryDelay = new FakeClipboardRetryDelay();

        var coordinator = new ClipboardPrivacyCoordinator(
            transport, targetCapture, processor, writeTransport, lifecycle, decisionSessionPublisher,
            operationGate, verificationHandoff, verificationInvalidation,
            stopTimeoutOverride: null,
            retryDelay: retryDelay);
        coordinator.Start();

        return new Harness
        {
            Coordinator = coordinator,
            Transport = transport,
            TargetCapture = targetCapture,
            Processor = processor,
            WriteTransport = writeTransport,
            Lifecycle = lifecycle,
            VerificationHandoff = verificationHandoff,
            RetryDelay = retryDelay,
        };
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? WaitTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(5);
        }

        return true;
    }

    private static ClipboardTextReadResult RawRead(uint sequence = 7) =>
        ClipboardTextReadResult.Success(new ClipboardTextSnapshot(sequence, HasReliableSequence: true, RawSynthetic));

    private static ClipboardTextReadResult ProtectedRead(uint sequence = 8) =>
        ClipboardTextReadResult.Success(new ClipboardTextSnapshot(sequence, HasReliableSequence: true, ProtectedAlias));

    private static void ArmProtectableProcessor(Harness h)
    {
        h.Processor.ResultToReturn = new ClipboardPrivacyProcessingResult(
            CandidateCount: 1, TrustedCount: 0, ProtectCount: 1, NeedsDecisionCount: 0, BypassCount: 0);
        h.Processor.WritePlanToReturn = new ClipboardWritePlan(ProtectedAlias);
    }

    /// <summary>
    /// Deterministically proves the in-flight attempt (and any retry loop it owns) has COMPLETELY
    /// finished, without sleeping: dispatches a second work item whose target is deliberately
    /// unsupported, then waits for its foreground capture. The frozen
    /// OPERATION_GATE_ACQUISITION_BOUNDARY guarantees the worker cannot begin that second item until
    /// the first item's whole ProcessWorkItemAsync -- retry loop included -- has released the gate.
    /// </summary>
    private static async Task<bool> WaitUntilFirstItemFullyFinishedAsync(Harness h)
    {
        var capturesBefore = h.TargetCapture.CaptureCallCount;
        h.TargetCapture.SnapshotToReturn = UnsupportedTarget;
        h.Transport.RaiseChanged(TextNotification);
        return await WaitUntilAsync(() => h.TargetCapture.CaptureCallCount > capturesBefore);
    }

    // ==================================================================
    // GATE 2B STEP 1 -- seam characterization (expected GREEN)
    // ==================================================================

    // Proves the delay seam introduced in this STEP is genuinely inert: it is wired into the
    // coordinator, but a completely ordinary successful attempt never invokes it. This is the
    // evidence that adding the seam changed no production behavior.
    [Fact]
    public async Task Seam_IsWiredButNeverInvoked_OnAFullySuccessfulAttempt()
    {
        using var h = CreateStarted();
        Assert.Same(h.RetryDelay, h.Coordinator.RetryDelay);

        h.Transport.NextReadResult = RawRead();
        ArmProtectableProcessor(h);
        h.WriteTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 9);

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            await WaitUntilAsync(() => h.VerificationHandoff.CallCount >= 1),
            "A normal protected write never completed -- harness setup is wrong, not the seam.");
        Assert.Equal(0, h.RetryDelay.CallCount);
    }

    // ==================================================================
    // RED SUITE -- regression coverage for the live bounded autonomous-retry loop; the RED-###
    // naming is kept as historical record of the defect each test was originally written to prove
    // (see GATE_2B_SCOPE above)
    // ==================================================================

    // RED-001: transient read Busy, then Success -- with NO external clipboard/foreground event.
    [Fact]
    public async Task Red001_BusyThenSuccess_RetriesAutonomouslyAndProtects()
    {
        using var h = CreateStarted();
        h.Transport.ScriptReadResults(
            ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy),
            RawRead());
        ArmProtectableProcessor(h);
        h.WriteTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 9);

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            await WaitUntilAsync(() => h.WriteTransport.CallCount >= 1),
            "RED-001: after a transient read Busy the coordinator abandoned the evaluation and never " +
            "retried -- the guarded write was never reached even though the generation, target and " +
            "clipboard were all still valid, and no external clipboard/foreground event occurred.");

        Assert.Equal(2, h.Transport.ReadCallCount);
        Assert.Equal(1, h.RetryDelay.CallCount);
        Assert.Equal(ContractRetryDelay, Assert.Single(h.RetryDelay.RequestedDurations));
        Assert.Equal(ProtectedAlias, Assert.Single(h.WriteTransport.ReceivedReplacementTexts));
        Assert.Equal(1, h.VerificationHandoff.CallCount);
        // Attempt 1 abandoned (retryable), attempt 2 completed (verified) -- exactly one transition
        // per attempt, none of them made by the retry controller itself.
        Assert.Equal(1, h.Lifecycle.AbandonEvaluationCallCount);
        Assert.Equal(1, h.Lifecycle.CompleteEvaluationCallCount);
    }

    // RED-002: transient read NativeFailure, then Success.
    [Fact]
    public async Task Red002_NativeFailureThenSuccess_RetriesAutonomouslyAndProtects()
    {
        using var h = CreateStarted();
        h.Transport.ScriptReadResults(
            ClipboardTextReadResult.Failure(ClipboardReadOutcome.NativeFailure),
            RawRead());
        ArmProtectableProcessor(h);
        h.WriteTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 9);

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            await WaitUntilAsync(() => h.WriteTransport.CallCount >= 1),
            "RED-002: after a transient read NativeFailure the coordinator abandoned the evaluation " +
            "and never retried -- no autonomous retry path exists.");

        Assert.Equal(2, h.Transport.ReadCallCount);
        Assert.Equal(1, h.RetryDelay.CallCount);
        Assert.Equal(1, h.VerificationHandoff.CallCount);
        Assert.Equal(1, h.Lifecycle.AbandonEvaluationCallCount);
        Assert.Equal(1, h.Lifecycle.CompleteEvaluationCallCount);
    }

    // RED-003: transient TargetUnavailable, then a fresh capture of the SAME supported target.
    [Fact]
    public async Task Red003_TargetUnavailableThenFreshSupportedTarget_RetriesAutonomouslyAndProtects()
    {
        using var h = CreateStarted();
        h.Transport.ScriptReadResults(
            ClipboardTextReadResult.Failure(ClipboardReadOutcome.TargetUnavailable),
            RawRead());
        ArmProtectableProcessor(h);
        h.WriteTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 9);

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            await WaitUntilAsync(() => h.WriteTransport.CallCount >= 1),
            "RED-003: a transient TargetUnavailable (a mechanical foreground-resolution race, not a " +
            "confirmed target departure) abandoned the evaluation with no autonomous retry -- the " +
            "still-supported ChatGPT target was never re-captured and the clipboard was never protected.");

        // The retry must take a FRESH foreground capture rather than reusing attempt 1's snapshot.
        Assert.Equal(2, h.TargetCapture.CaptureCallCount);
        Assert.Equal(2, h.Transport.ReadCallCount);
        Assert.Equal(1, h.RetryDelay.CallCount);
        Assert.Equal(1, h.VerificationHandoff.CallCount);
    }

    // RED-004: VerificationUnavailable -- no false verified-success handoff, then a fresh retry that
    // re-reads the (now protected) clipboard and terminates on real truth rather than on a cached
    // rewrite result.
    [Fact]
    public async Task Red004_VerificationUnavailable_RetriesAgainstFreshClipboardTruthWithoutFakingSuccess()
    {
        using var h = CreateStarted();
        h.Transport.ScriptReadResults(RawRead(sequence: 7), ProtectedRead(sequence: 8));
        h.Processor.ScriptOutcomes(
            // Attempt 1 sees RAW PII -> a real write plan.
            new ClipboardPrivacyProcessingOutcome(
                new ClipboardPrivacyProcessingResult(1, 0, 1, 0, 0),
                new ClipboardWritePlan(ProtectedAlias),
                DecisionPlan: null),
            // Attempt 2 re-reads the already-protected clipboard -> nothing left to protect.
            new ClipboardPrivacyProcessingOutcome(
                new ClipboardPrivacyProcessingResult(0, 0, 0, 0, 0),
                WritePlan: null,
                DecisionPlan: null));
        h.WriteTransport.NextResult =
            ClipboardWriteResult.Failure(ClipboardWriteOutcome.VerificationUnavailable, mutated: true);

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            await WaitUntilAsync(() => h.Processor.CallCount >= 2),
            "RED-004: a VerificationUnavailable write abandoned the evaluation with no autonomous " +
            "retry -- the clipboard was never re-read, so PRIVON never established what the real " +
            "post-write clipboard truth was.");

        // The unverified write must never be reported as a protection success.
        Assert.Equal(0, h.VerificationHandoff.CallCount);
        // The retry must re-run the full pipeline against a FRESH read, not re-issue a cached rewrite.
        Assert.Equal(2, h.Transport.ReadCallCount);
        Assert.Equal(1, h.WriteTransport.CallCount);
        Assert.Equal(1, h.RetryDelay.CallCount);
        Assert.Equal(1, h.Lifecycle.AbandonEvaluationCallCount);
        Assert.Equal(1, h.Lifecycle.CompleteEvaluationCallCount);
    }

    // RED-005: repeated Busy exhausts the exact budget -- 4 attempts, 3 delays, no fifth, no fake success.
    [Fact]
    public async Task Red005_RepeatedBusy_ExhaustsExactBudgetWithoutFakingSuccess()
    {
        using var h = CreateStarted();
        h.Transport.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy);
        ArmProtectableProcessor(h);

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            await WaitUntilAsync(() => h.Transport.ReadCallCount >= 4),
            "RED-005: only one attempt was ever made for a persistently Busy clipboard -- the bounded " +
            "4-attempt autonomous retry budget does not exist.");

        Assert.True(
            await WaitUntilFirstItemFullyFinishedAsync(h),
            "The retry loop never released the operation gate -- cannot prove the budget terminated.");

        Assert.Equal(4, h.Transport.ReadCallCount);
        Assert.Equal(3, h.RetryDelay.CallCount);
        Assert.All(h.RetryDelay.RequestedDurations, d => Assert.Equal(ContractRetryDelay, d));
        // One Abandon per attempt, made by the single-attempt pipeline -- the controller adds none.
        Assert.Equal(4, h.Lifecycle.AbandonEvaluationCallCount);
        Assert.Equal(0, h.Lifecycle.CompleteEvaluationCallCount);
        Assert.Equal(0, h.WriteTransport.CallCount);
        Assert.Equal(0, h.VerificationHandoff.CallCount);
    }

    // RED-006: shutdown begins while a retry delay is pending -- zero new admission afterwards.
    [Fact]
    public async Task Red006_ShutdownDuringPendingRetryDelay_AdmitsNoFurtherAttempt()
    {
        using var h = CreateStarted();
        h.RetryDelay.BlockUntilReleased = true;
        h.Transport.ScriptReadResults(
            ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy),
            RawRead());
        ArmProtectableProcessor(h);
        h.WriteTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 9);

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            h.RetryDelay.WaitUntilEntered(WaitTimeout),
            "RED-006: the retry delay was never entered -- no autonomous retry exists, so the " +
            "shutdown-during-pending-delay boundary cannot be exercised at all.");

        var capturesAtDelay = h.TargetCapture.CaptureCallCount;
        var readsAtDelay = h.Transport.ReadCallCount;

        var stopTask = Task.Run(() => h.Coordinator.Stop());

        // transport.Stop() runs inside PerformCleanup, which runs strictly AFTER the shutdown
        // linearization point (_phase leaving Running) -- observing it proves shutdown has begun.
        Assert.True(
            await WaitUntilAsync(() => h.Transport.StopCallCount >= 1),
            "Shutdown never reached the transport -- cannot prove the shutdown boundary was crossed.");

        h.RetryDelay.ReleaseNext();

        Assert.True(await WaitUntilAsync(() => stopTask.IsCompleted), "Coordinator shutdown did not complete.");
        await stopTask;

        Assert.Equal(capturesAtDelay, h.TargetCapture.CaptureCallCount);
        Assert.Equal(readsAtDelay, h.Transport.ReadCallCount);
        Assert.Equal(0, h.WriteTransport.CallCount);
        // Attempt 1 already owns its single Abandon; the controller must not add a second.
        Assert.Equal(1, h.Lifecycle.AbandonEvaluationCallCount);
        Assert.Equal(0, h.Lifecycle.CompleteEvaluationCallCount);
    }

    // RED-007: a newer clipboard generation arrives while a retry delay is pending -- the stale
    // generation must never be re-claimed and must never produce a write.
    [Fact]
    public async Task Red007_NewerGenerationDuringRetryDelay_NeverWritesForStaleGeneration()
    {
        using var h = CreateStarted();
        h.RetryDelay.BlockUntilReleased = true;
        h.Transport.ScriptReadResults(
            ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy), // attempt 1, generation 1
            RawRead(sequence: 7),                                       // would-be stale retry, or generation 2
            RawRead(sequence: 8));
        ArmProtectableProcessor(h);
        h.WriteTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 9);

        h.Transport.RaiseChanged(TextNotification); // generation 1

        Assert.True(
            h.RetryDelay.WaitUntilEntered(WaitTimeout),
            "RED-007: the retry delay was never entered -- no autonomous retry exists, so the " +
            "supersede-during-delay boundary cannot be exercised at all.");

        h.Transport.RaiseChanged(TextNotification); // generation 2 supersedes generation 1
        Assert.True(
            await WaitUntilAsync(() => h.Lifecycle.CurrentGeneration >= 2),
            "The superseding notification never advanced the generation.");

        h.RetryDelay.ReleaseNext();

        Assert.True(
            await WaitUntilAsync(() => h.VerificationHandoff.CallCount >= 1),
            "The newer generation was never protected after the stale retry was rejected.");

        // The stale generation must never reach a verified protected write; only the newer one may.
        Assert.DoesNotContain(1L, h.VerificationHandoff.ReceivedExpectedGenerations);
        Assert.Contains(2L, h.VerificationHandoff.ReceivedExpectedGenerations);
    }

    // RED-008: the foreground target leaves ChatGPT while a retry delay is pending.
    [Fact]
    public async Task Red008_TargetLeavesDuringRetryDelay_PerformsNoWrite()
    {
        using var h = CreateStarted();
        h.RetryDelay.BlockUntilReleased = true;
        h.Transport.ScriptReadResults(
            ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy),
            RawRead());
        ArmProtectableProcessor(h);
        h.WriteTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 9);

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            h.RetryDelay.WaitUntilEntered(WaitTimeout),
            "RED-008: the retry delay was never entered -- no autonomous retry exists, so the " +
            "target-departure-during-delay boundary cannot be exercised at all.");

        h.TargetCapture.SnapshotToReturn = UnsupportedTarget;
        h.RetryDelay.ReleaseNext();

        Assert.True(
            await WaitUntilAsync(() => h.TargetCapture.CaptureCallCount >= 2),
            "The retry never re-captured the foreground target -- freshness revalidation is missing.");

        Assert.Equal(1, h.Transport.ReadCallCount);
        Assert.Equal(0, h.WriteTransport.CallCount);
        Assert.Equal(0, h.VerificationHandoff.CallCount);
    }

    // ==================================================================
    // GUARD SUITE -- contract guards, expected GREEN before implementation
    // ==================================================================

    // GUARD-001/002/003/004: every write outcome carrying positive evidence of NEWER or CHANGED
    // truth must terminate the current attempt and wait for a real external event -- never spin an
    // autonomous retry against a clipboard/target that has provably moved on.
    [Theory]
    [InlineData(ClipboardWriteOutcome.SequenceChanged)]  // GUARD-001
    [InlineData(ClipboardWriteOutcome.TargetChanged)]    // GUARD-002
    [InlineData(ClipboardWriteOutcome.Superseded)]       // GUARD-003
    [InlineData(ClipboardWriteOutcome.ReadBackMismatch)] // GUARD-004
    public async Task Guard_NewerOrChangedTruthWriteOutcome_NeverRetriesAutonomously(ClipboardWriteOutcome outcome)
    {
        using var h = CreateStarted();
        h.Transport.NextReadResult = RawRead();
        ArmProtectableProcessor(h);
        h.WriteTransport.NextResult = ClipboardWriteResult.Failure(outcome, mutated: false);

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            await WaitUntilAsync(() => h.WriteTransport.CallCount >= 1),
            "The guarded write was never attempted -- harness setup is wrong.");
        Assert.True(
            await WaitUntilFirstItemFullyFinishedAsync(h),
            "The first work item never finished -- cannot prove absence of an autonomous retry.");

        Assert.Equal(0, h.RetryDelay.CallCount);
        Assert.Equal(1, h.WriteTransport.CallCount);
        Assert.Equal(0, h.VerificationHandoff.CallCount);
    }

    // GUARD-005: an unexpected exception is NOT a known-transient clipboard outcome -- exactly one
    // attempt, exactly one Abandon, no autonomous retry, no broadening into generic exception recovery.
    [Fact]
    public async Task Guard005_ProcessingException_RunsExactlyOneAttemptAndNeverRetries()
    {
        using var h = CreateStarted();
        h.Transport.NextReadResult = RawRead();
        h.Processor.ThrowOnProcess = new InvalidOperationException("Synthetic processor failure for test.");

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            await WaitUntilAsync(() => h.Processor.CallCount >= 1),
            "The processor was never invoked -- harness setup is wrong.");
        Assert.True(
            await WaitUntilFirstItemFullyFinishedAsync(h),
            "The first work item never finished -- cannot prove absence of an autonomous retry.");

        Assert.Equal(0, h.RetryDelay.CallCount);
        Assert.Equal(1, h.Processor.CallCount);
        Assert.Equal(1, h.Transport.ReadCallCount);
        Assert.Equal(1, h.Lifecycle.AbandonEvaluationCallCount);
        Assert.Equal(0, h.Lifecycle.CompleteEvaluationCallCount);
    }

    // GUARD-006: NotRunning means the transport is stopped -- retrying inside the same worker cycle
    // is pointless and must never be attempted.
    [Fact]
    public async Task Guard006_NotRunningRead_NeverRetriesAutonomously()
    {
        using var h = CreateStarted();
        h.Transport.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.NotRunning);

        h.Transport.RaiseChanged(TextNotification);

        Assert.True(
            await WaitUntilAsync(() => h.Transport.ReadCallCount >= 1),
            "The guarded read was never attempted -- harness setup is wrong.");
        Assert.True(
            await WaitUntilFirstItemFullyFinishedAsync(h),
            "The first work item never finished -- cannot prove absence of an autonomous retry.");

        Assert.Equal(0, h.RetryDelay.CallCount);
        Assert.Equal(1, h.Transport.ReadCallCount);
        Assert.Equal(0, h.WriteTransport.CallCount);
    }

    // ==================================================================
    // STRUCTURAL PRIVACY GUARD
    // ==================================================================

    // The live retry controller must own only non-PII state (pinned generation, attempt count,
    // ForegroundTargetSnapshot). This asserts the coordinator holds no RAW-bearing field, so a
    // future change cannot silently introduce one without failing here. Deliberately a
    // structural check -- never a GC/zeroization test, which would prove nothing.
    [Fact]
    public void Coordinator_HasNoRawClipboardBearingField()
    {
        var forbidden = new[]
        {
            typeof(ClipboardTextSnapshot),
            typeof(ClipboardTextSnapshot?),
            typeof(ClipboardWritePlan),
            typeof(string),
        };

        var offenders = typeof(ClipboardPrivacyCoordinator)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                       System.Reflection.BindingFlags.Public)
            .Where(f => forbidden.Contains(f.FieldType))
            .Select(f => $"{f.FieldType.Name} {f.Name}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "ClipboardPrivacyCoordinator must never hold RAW clipboard text, a replacement string, or a " +
            "write plan as instance state -- those belong exclusively to one single-attempt method's own " +
            "locals and must not survive across a retry delay. Offending field(s): " + string.Join(", ", offenders));
    }
}
