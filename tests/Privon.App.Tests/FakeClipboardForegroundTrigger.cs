using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardForegroundTrigger -- lets ClipboardPrivacyCoordinator's
// second-trigger orchestration be exercised without any real Win32 SetWinEventHook/foreground state.
// No mocking framework -- hand-written, matching the FakeClipboardReadTransport precedent exactly
// (Start/Stop call log, ThrowOnStart/ThrowOnStop, a synchronous Raise() standing in for the real
// owner thread's synchronous WinEventProc-driven callback).
internal sealed class FakeClipboardForegroundTrigger : IClipboardForegroundTrigger
{
    public event EventHandler? Changed;

    public List<string> CallLog { get; } = [];
    public bool StartCalled { get; private set; }
    public bool StopCalled { get; private set; }
    public bool ThrowOnStart { get; set; }
    public bool ThrowOnStop { get; set; }

    /// <summary>Simulates the underlying foreground-trigger owner thread raising Changed synchronously.</summary>
    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public void Start()
    {
        CallLog.Add(nameof(Start));
        StartCalled = true;
        if (ThrowOnStart)
            throw new InvalidOperationException("Synthetic foreground trigger Start() failure for test.");
    }

    public void Stop()
    {
        CallLog.Add(nameof(Stop));
        StopCalled = true;
        if (ThrowOnStop)
            throw new InvalidOperationException("Synthetic foreground trigger Stop() failure for test.");
    }
}
