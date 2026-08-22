using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardComposerVerificationInvalidation -- lets
// ClipboardPrivacyCoordinator's callback invalidation forwarding be exercised without depending on
// the real ClipboardComposerVerifier. No mocking framework -- hand-written, matching every other
// Fake* in this project.
internal sealed class FakeClipboardComposerVerificationInvalidation : IClipboardComposerVerificationInvalidation
{
    public int CallCount { get; private set; }

    public void InvalidatePending() => CallCount++;
}
