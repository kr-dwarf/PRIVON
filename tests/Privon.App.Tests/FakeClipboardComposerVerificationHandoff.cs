using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardComposerVerificationHandoff -- lets
// ClipboardPrivacyCoordinator's verified-write handoff be exercised without depending on the real
// ClipboardComposerVerifier. No mocking framework -- hand-written, matching every other Fake* in
// this project.
internal sealed class FakeClipboardComposerVerificationHandoff : IClipboardComposerVerificationHandoff
{
    public int CallCount { get; private set; }
    public List<ForegroundTargetSnapshot> ReceivedExpectedTargets { get; } = [];
    public List<string> ReceivedExpectedProtectedTexts { get; } = [];
    public List<long> ReceivedExpectedGenerations { get; } = [];

    public bool ResultToReturn { get; set; } = true;

    /// <summary>When set, <see cref="Publish"/> throws this instead of returning -- lets a test
    /// prove PUBLISH_EXCEPTION_POLICY (Phase 3C STEP33 audit, implemented Phase 3C STEP34): the
    /// caller's own outer error boundary, never a local swallow.</summary>
    public Exception? ThrowOnPublish { get; set; }

    public bool Publish(ForegroundTargetSnapshot expectedTarget, string expectedProtectedText, long expectedGeneration)
    {
        CallCount++;
        ReceivedExpectedTargets.Add(expectedTarget);
        ReceivedExpectedProtectedTexts.Add(expectedProtectedText);
        ReceivedExpectedGenerations.Add(expectedGeneration);
        if (ThrowOnPublish is { } ex) throw ex;
        return ResultToReturn;
    }
}
