using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for ISessionLockNotification -- lets PrivonAppComposition's own
// session-lock callback wiring (subscription timing, Reset-then-InvalidatePending ordering,
// start/shutdown ordering, start-failure rollback) be exercised without any real Windows session
// lock/unlock ever occurring. No mocking framework -- hand-written, matching every other Fake* in
// this project.
internal sealed class FakeSessionLockNotification : ISessionLockNotification
{
    public event EventHandler? Locked;

    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }

    /// <summary>When set, <see cref="Start"/> throws this instead of returning -- lets a test force
    /// the real, documented "session observer Start failure fails the whole composition-root
    /// startup" policy without touching real Win32/WTS APIs.</summary>
    public Exception? FailStartWith { get; set; }

    public void Start()
    {
        StartCallCount++;
        if (FailStartWith is { } ex) throw ex;
    }

    public void Stop() => StopCallCount++;

    /// <summary>Synthetically raises <see cref="Locked"/> exactly as
    /// <see cref="Privon.Windows.SessionLockMonitor.Locked"/> itself would -- synchronously, on
    /// whatever thread calls this (the test's own thread, standing in for the real owner thread).</summary>
    public void RaiseLocked() => Locked?.Invoke(this, EventArgs.Empty);
}
