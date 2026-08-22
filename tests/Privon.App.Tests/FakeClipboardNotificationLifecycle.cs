using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardNotificationLifecycle -- lets
// ClipboardPrivacyCoordinator's callback/dispatch orchestration be exercised without depending
// on the real lock-based ClipboardDecisionScopeLifecycle. No mocking framework -- hand-written,
// matching every other Fake* in this project.
internal sealed class FakeClipboardNotificationLifecycle : IClipboardNotificationLifecycle
{
    public int CallCount { get; private set; }

    private long _nextGeneration = 1;

    public long AdvanceOnClipboardNotification()
    {
        CallCount++;
        return _nextGeneration++;
    }
}
