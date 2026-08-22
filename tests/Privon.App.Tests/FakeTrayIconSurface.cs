using Privon.App;

namespace Privon.App.Tests;

// Deterministic, shell-free double for ITrayIconSurface -- lets PrivonAppUiBridgeTests drive Exit
// and observe Show/Dispose lifecycle without ever constructing a real WinForms NotifyIcon (real
// tray rendering remains manual release QA, per Phase 3C STEP40 instruction). No mocking framework
// -- hand-written, matching every other Fake* in this project.
internal sealed class FakeTrayIconSurface : ITrayIconSurface
{
    public int ShowCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Optional test hook invoked at the end of <see cref="Dispose"/> -- lets a test
    /// record cross-object shutdown ORDER without a mocking framework.</summary>
    public Action? OnDispose { get; set; }

    /// <summary>Phase 3C STEP40.2 -- when set, <see cref="Show"/> throws this instead of
    /// incrementing <see cref="ShowCallCount"/>, letting a test force
    /// <see cref="PrivonAppUiBridge.Start"/>'s own STARTUP_ROLLBACK path deterministically.</summary>
    public Exception? ThrowOnShow { get; set; }

    /// <summary>Phase 3C STEP40.2 -- number of currently-live subscribers, tracked explicitly so
    /// a test can prove <see cref="PrivonAppUiBridge"/>'s rollback/dispose path really removed its
    /// own subscription (rather than merely assuming a field-like event's default add/remove
    /// worked).</summary>
    public int ExitRequestedSubscriberCount { get; private set; }

    private EventHandler? _exitRequested;

    public event EventHandler? ExitRequested
    {
        add { _exitRequested += value; ExitRequestedSubscriberCount++; }
        remove { _exitRequested -= value; ExitRequestedSubscriberCount--; }
    }

    public void Show()
    {
        if (ThrowOnShow is { } ex) throw ex;
        ShowCallCount++;
    }

    /// <summary>Simulates the user choosing the tray's Exit command.</summary>
    public void RaiseExitRequested() => _exitRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        DisposeCallCount++;
        Disposed = true;
        OnDispose?.Invoke();
    }
}
