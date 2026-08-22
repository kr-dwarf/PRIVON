using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardDecisionSessionPublisher -- lets
// ClipboardPrivacyCoordinator's decision-session-publication handoff be exercised without
// depending on the real RevisionTracker/ClipboardDecisionScopeLifecycle chain. No mocking
// framework -- hand-written, matching every other Fake* in this project.
internal sealed class FakeClipboardDecisionSessionPublisher : IClipboardDecisionSessionPublisher
{
    public int CallCount { get; private set; }
    public List<long> ReceivedExpectedGenerations { get; } = [];
    public List<string> ReceivedRawTexts { get; } = [];
    public List<ClipboardDecisionPlan> ReceivedDecisionPlans { get; } = [];

    public bool ResultToReturn { get; set; } = true;

    /// <summary>When set, <see cref="TryPublish"/> throws this instead of returning -- lets a test
    /// prove the coordinator's worker survives a decision-session-publisher failure/throw.</summary>
    public Exception? ThrowOnPublish { get; set; }

    public bool TryPublish(long expectedGeneration, string currentRawText, ClipboardDecisionPlan decisionPlan)
    {
        // ORDERING_HAZARD (Stabilization Gate, post-Phase-0.2E): CallCount is written LAST -- see
        // FakeClipboardWriteTransport's identical comment / ClipboardTestDoubleOrderingHazardTests
        // for why. This is the exact fake DecisionPlanPresent_ForwardsExactDecisionPlanReference
        // hit: `WaitUntilAsync(() => publisher.CallCount >= 1)` followed immediately by
        // `publisher.ReceivedDecisionPlans[0]` used to be able to observe CallCount == 1 with
        // ReceivedDecisionPlans still empty.
        ReceivedExpectedGenerations.Add(expectedGeneration);
        ReceivedRawTexts.Add(currentRawText);
        ReceivedDecisionPlans.Add(decisionPlan);
        CallCount++;

        if (ThrowOnPublish is { } ex)
            throw ex;

        return ResultToReturn;
    }
}
