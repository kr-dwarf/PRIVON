using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardWriteTransport -- lets ClipboardPrivacyCoordinator's
// write-handoff orchestration be exercised without any real Windows clipboard/foreground state.
// No mocking framework -- hand-written, matching the FakeClipboardReadTransport/
// FakeClipboardPrivacyProcessor precedent already established in this project.
internal sealed class FakeClipboardWriteTransport : IClipboardWriteTransport
{
    public List<string> CallLog { get; } = [];
    public int CallCount { get; private set; }
    public List<ForegroundTargetSnapshot> ReceivedExpectedTargets { get; } = [];
    public List<uint> ReceivedSequences { get; } = [];
    public List<string> ReceivedReplacementTexts { get; } = [];

    public ClipboardWriteResult NextResult { get; set; } =
        ClipboardWriteResult.Failure(ClipboardWriteOutcome.NotRunning, mutated: false);

    /// <summary>When set, <see cref="WriteTextIfSequenceMatchesAsync"/> throws this instead of
    /// returning -- lets a test prove the coordinator's worker survives a write-transport
    /// failure/throw.</summary>
    public Exception? ThrowOnWrite { get; set; }

    public Task<ClipboardWriteResult> WriteTextIfSequenceMatchesAsync(
        ForegroundTargetSnapshot expectedTarget, uint expectedSequence, string replacementText)
    {
        // ORDERING_HAZARD (Stabilization Gate, post-Phase-0.2E): CallCount is written LAST,
        // strictly after every other field a caller might read once it observes CallCount having
        // incremented -- a coordinator test that polls CallCount via WaitUntilAsync on this
        // background-worker-driven fake and then immediately reads a companion Received* field
        // must never be able to observe CallCount already incremented while that companion field
        // is still unpopulated. See ClipboardTestDoubleOrderingHazardTests for the deterministic
        // proof of this exact mechanism.
        CallLog.Add(nameof(WriteTextIfSequenceMatchesAsync));
        ReceivedExpectedTargets.Add(expectedTarget);
        ReceivedSequences.Add(expectedSequence);
        ReceivedReplacementTexts.Add(replacementText);
        CallCount++;

        if (ThrowOnWrite is { } ex)
            throw ex;

        return Task.FromResult(NextResult);
    }
}
