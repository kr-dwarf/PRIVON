using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardPrivacyProcessor -- lets
// ClipboardPrivacyCoordinator's dispatch/target-gate/guarded-read/handoff orchestration be
// exercised without running the real DetectionPipeline on every coordinator-boundary test. No
// mocking framework -- hand-written, matching the FakeClipboardReadTransport/
// FakeForegroundTargetCapture precedent already established in this project.
internal sealed class FakeClipboardPrivacyProcessor : IClipboardPrivacyProcessor
{
    public int CallCount { get; private set; }
    public List<ForegroundTargetSnapshot> ReceivedExpectedTargets { get; } = [];
    public List<ClipboardTextSnapshot> ReceivedSnapshots { get; } = [];

    public ClipboardPrivacyProcessingResult ResultToReturn { get; set; } =
        new(CandidateCount: 0, TrustedCount: 0, ProtectCount: 0, NeedsDecisionCount: 0, BypassCount: 0);

    /// <summary>Phase 3B STEP14 -- when set, <see cref="Process"/> returns this as the outcome's
    /// <c>WritePlan</c>, letting a coordinator-boundary test prove write-handoff behavior without
    /// running the real Detection/Alias chain. Null (the default) means "no plan" -- matching the
    /// real processor's own NeedsDecision/all-Bypass gating.</summary>
    public ClipboardWritePlan? WritePlanToReturn { get; set; }

    /// <summary>Phase 3B STEP19 -- when set, <see cref="Process"/> returns this as the outcome's
    /// <c>DecisionPlan</c>, letting a coordinator-boundary test prove the coordinator performs no
    /// write and inspects nothing about it, without running the real Detection/Alias chain. Null
    /// (the default) means "no plan."</summary>
    public ClipboardDecisionPlan? DecisionPlanToReturn { get; set; }

    /// <summary>When set, <see cref="Process"/> throws this instead of returning -- lets a test
    /// prove DETECTION_FAILURE_POLICY/WORKER_SURVIVAL without a real throwing detector.</summary>
    public Exception? ThrowOnProcess { get; set; }

    public ClipboardPrivacyProcessingOutcome Process(ForegroundTargetSnapshot expectedTarget, ClipboardTextSnapshot snapshot)
    {
        // ORDERING_HAZARD (Stabilization Gate, post-Phase-0.2E): CallCount is written LAST -- see
        // FakeClipboardWriteTransport's identical comment / ClipboardTestDoubleOrderingHazardTests
        // for the deterministic proof of why this ordering matters for a background-worker-driven
        // fake a test polls via WaitUntilAsync.
        ReceivedExpectedTargets.Add(expectedTarget);
        ReceivedSnapshots.Add(snapshot);
        CallCount++;

        if (ThrowOnProcess is { } ex)
            throw ex;

        return new ClipboardPrivacyProcessingOutcome(ResultToReturn, WritePlanToReturn, DecisionPlanToReturn);
    }
}
