using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// BUG-006 -- App-layer regression proving RAW_RESTORED_BUT_UNPROTECTED is STRUCTURALLY_PREVENTED,
// not merely POSSIBLE: when a Windows-layer guarded write fails after EmptyClipboard already
// succeeded (the write transport reports NativeFailure with ClipboardMutated=true -- from the App
// layer's own perspective, this is exactly what a BUG-006 recovery attempt looks like, whether the
// rollback itself succeeded or not), the App layer's own bounded autonomous retry (BUG-002) is NOT
// what guarantees a future protection attempt over the restored content -- an independent, later
// clipboard-change notification (the restore's own SetClipboardData succeeding) does, by advancing
// the generation to a NEW one with its own real trigger already in flight. This test proves BOTH
// halves together: (1) the newer generation genuinely reaches a fresh read/protect attempt over
// the same raw content, and (2) the OLD generation's own still-pending retry correctly defers to
// it once it wakes (freshness guard) rather than competing with or duplicating it.
//
// Uses the same deterministic Harness/FakeClipboardRetryDelay/FakeClipboardNotificationLifecycle
// infrastructure Bug002AutonomousRetryTests already established -- no new test infrastructure, and
// BUG-002's own production logic is never touched here.
public class Bug006FutureProtectionTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private const string RawSyntheticA = "SYNTHETIC-RESTORED-RAW-A";
    private const string ProtectedB = "SYNTHETIC-PROTECTED-B";

    private static readonly ForegroundTargetSnapshot SupportedTarget =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    private static readonly ClipboardChangeNotification OriginalWriteAttemptNotification =
        new(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true);

    // Represents the restore's own SetClipboardData succeeding and producing a genuinely new,
    // reliable clipboard sequence -- exactly the notification MutateWhileClipboardOpen's recovery
    // path structurally always produces (never suppressed as a self-write echo, since it never
    // arms the marker -- see the paired Windows-layer regression in Bug006RecoveryTests).
    private static readonly ClipboardChangeNotification RestoreNotification =
        new(SequenceNumber: 777, HasReliableSequence: true, HasUnicodeText: true);

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

    private static void ArmProtectableProcessor(Harness h)
    {
        h.Processor.ResultToReturn = new ClipboardPrivacyProcessingResult(
            CandidateCount: 1, TrustedCount: 0, ProtectCount: 1, NeedsDecisionCount: 0, BypassCount: 0);
        h.Processor.WritePlanToReturn = new ClipboardWritePlan(ProtectedB);
    }

    [Fact]
    public async Task RestoredRawContent_AlwaysReachesAFutureProtectionAttempt_AndStaleRetryDefersToIt()
    {
        using var h = CreateStarted();
        h.Transport.NextReadResult = ClipboardTextReadResult.Success(
            new ClipboardTextSnapshot(SequenceNumber: 1, HasReliableSequence: true, RawSyntheticA));
        ArmProtectableProcessor(h);

        // Call 1 (generation G): the protected write fails -- from the App layer's own
        // perspective, this is exactly what a BUG-006 recovery attempt looks like (Outcome=
        // NativeFailure, ClipboardMutated=true), whether or not the Windows-layer rollback itself
        // succeeded -- the App layer never needs to know which. Call 2 (generation G', once
        // reached): the fresh protection attempt over the restored content succeeds.
        h.WriteTransport.ScriptWriteResults(
            ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: true),
            ClipboardWriteResult.Success(resultSequence: 9));

        h.RetryDelay.BlockUntilReleased = true;

        // G: the original write attempt begins and fails, then parks in the retry delay.
        h.Transport.RaiseChanged(OriginalWriteAttemptNotification);
        Assert.True(
            h.RetryDelay.WaitUntilEntered(WaitTimeout),
            "G's own write attempt never reached the retry delay -- harness setup is wrong, not the fix under test.");
        Assert.Equal(1, h.WriteTransport.CallCount);
        Assert.Equal(1L, h.Lifecycle.CurrentGeneration);

        // The restore's own SetClipboardData succeeding produces exactly this notification --
        // independent of G's own still-parked retry, and never gated by the operation gate
        // (OnClipboardChanged only ever does a synchronous generation advance + non-blocking
        // mailbox write). This is what a genuine future protection attempt depends on.
        h.Transport.RaiseChanged(RestoreNotification);
        Assert.Equal(2L, h.Lifecycle.CurrentGeneration); // G' already exists, even though the
                                                           // operation gate still belongs to G.

        // Wake G's retry now that G' already exists -- FRESHNESS_BEFORE_EVERY_RETRY must reject it:
        // no second read, no second write, no lifecycle claim for the now-stale G.
        h.RetryDelay.ReleaseNext();

        // G' reaches its own fresh read/protect/verify cycle over the restored raw content, once
        // G's whole attempt (OPERATION_GATE_ACQUISITION_BOUNDARY) has fully released the gate.
        Assert.True(
            await WaitUntilAsync(() => h.VerificationHandoff.CallCount >= 1),
            "RAW_RESTORED_BUT_UNPROTECTED: the restored content never reached a future protection " +
            "attempt -- either the newer generation was never claimed, or the stale retry incorrectly " +
            "consumed it first.");

        // Exactly one write for G (failed) and exactly one write for G' (succeeded) -- G's stale
        // retry made no additional attempt of its own (no double-processing / no competing write).
        Assert.Equal(2, h.WriteTransport.CallCount);
        Assert.Equal(2, h.Processor.CallCount);
        Assert.All(h.Processor.ReceivedSnapshots, s => Assert.Equal(RawSyntheticA, s.Text));
        Assert.Equal(1, h.RetryDelay.CallCount); // only G's own single retry-delay entry -- never a second
        Assert.Equal(ProtectedB, h.WriteTransport.ReceivedReplacementTexts[^1]);
        Assert.Equal(1, h.VerificationHandoff.CallCount); // only G' was ever verified/published
    }
}
