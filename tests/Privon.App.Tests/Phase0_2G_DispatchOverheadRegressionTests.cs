using Privon.Windows;

namespace Privon.App.Tests;

// Phase 0.2G -- Immediate Paste Race Gate: AUTOMATED_EVIDENCE for the ONE component of the
// "raw PII on clipboard -> foreground/clipboard trigger fires -> evaluation claimed" race window
// that cannot be measured by the separate, real-Win32 tools/ClipboardRaceMeasurement tool --
// ClipboardPrivacyCoordinator itself is internal to Privon.App and has no InternalsVisibleTo
// grant to any standalone tool project, so its own channel/gate/intake dispatch overhead can only
// be measured from inside this test project.
//
// Uses only existing hand-written fakes (FakeClipboard*/FakeForegroundTargetCapture) -- no real
// Windows clipboard/foreground state, no mocking framework, no production code change. Because
// every dependency here is a fake that completes synchronously/instantly, the elapsed time
// measured is (almost) entirely the coordinator's own dispatch machinery: a channel TryWrite, a
// ThreadPool-scheduled worker wakeup, an uncontended ClipboardOperationGate.WaitAsync, target
// capture, TargetGate, and the generation claim -- see ClipboardPrivacyCoordinator's own
// ACQUISITION_BOUNDARY/CALLBACK_BOUNDARY doc for why none of this ever runs inside the raising
// callback itself.
//
// Ceilings here are deliberately generous (hundreds of milliseconds) -- this is diagnostic
// evidence for a release-blocker judgment, not a tight performance SLA, and must never become a
// source of flaky CI failures under unrelated ThreadPool/scheduler load.
public class Phase0_2G_DispatchOverheadRegressionTests
{
    private static readonly TimeSpan DispatchCeiling = TimeSpan.FromMilliseconds(500);

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.SpinWait(50);
        }
        return true;
    }

    // ---- 1. clipboard-change-triggered dispatch: raise Changed -> processor actually invoked ----
    [Fact]
    public void ClipboardChangeTrigger_DispatchToProcessorInvocation_WellUnderGenerousCeiling()
    {
        var transport = new FakeClipboardReadTransport
        {
            NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "no PII here")),
        };
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
        try
        {
            var notification = new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            transport.RaiseChanged(notification);
            bool completed = SpinUntil(() => processor.CallCount >= 1, TimeSpan.FromSeconds(5));
            sw.Stop();

            Assert.True(completed, "Processor was never invoked within 5s -- dispatch appears stuck, not merely slow.");
            Assert.True(
                sw.Elapsed < DispatchCeiling,
                $"Clipboard-change dispatch (channel write -> worker dequeue -> gate acquire -> " +
                $"target capture -> TargetGate -> generation claim -> processor call) took " +
                $"{sw.Elapsed.TotalMilliseconds:F3}ms with every dependency faked to complete " +
                "instantly -- this suggests real dispatch overhead, not real I/O, may be " +
                "contributing meaningfully to the Phase 0.2G race window.");
        }
        finally
        {
            coordinator.Stop();
        }
    }

    // ---- 2. foreground-change-triggered dispatch: the Phase 0.2G scenario's own trigger -- raise
    // ForegroundTrigger.Changed -> processor actually invoked, with NO clipboard content change at
    // all (the exact "already-raw-PII-from-an-earlier-copy, user just switched focus" scenario) ----
    [Fact]
    public void ForegroundChangeTrigger_DispatchToProcessorInvocation_WellUnderGenerousCeiling()
    {
        var transport = new FakeClipboardReadTransport
        {
            NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "no PII here")),
        };
        var targetCapture = new FakeForegroundTargetCapture();
        var processor = new FakeClipboardPrivacyProcessor();
        var writeTransport = new FakeClipboardWriteTransport();
        var notificationLifecycle = new FakeClipboardNotificationLifecycle();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();
        var operationGate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var verificationInvalidation = new FakeClipboardComposerVerificationInvalidation();
        var foregroundTrigger = new FakeClipboardForegroundTrigger();

        var coordinator = new ClipboardPrivacyCoordinator(
            transport, targetCapture, processor, writeTransport, notificationLifecycle,
            decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation,
            foregroundTrigger: foregroundTrigger);
        coordinator.Start();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foregroundTrigger.Raise();
            bool completed = SpinUntil(() => processor.CallCount >= 1, TimeSpan.FromSeconds(5));
            sw.Stop();

            Assert.True(completed, "Processor was never invoked within 5s -- dispatch appears stuck, not merely slow.");
            Assert.True(
                sw.Elapsed < DispatchCeiling,
                $"Foreground-change dispatch (the Phase 0.2G scenario's own trigger path) took " +
                $"{sw.Elapsed.TotalMilliseconds:F3}ms with every dependency faked to complete " +
                "instantly -- this suggests real dispatch overhead, not real I/O, may be " +
                "contributing meaningfully to the race window.");
        }
        finally
        {
            coordinator.Stop();
        }
    }
}
