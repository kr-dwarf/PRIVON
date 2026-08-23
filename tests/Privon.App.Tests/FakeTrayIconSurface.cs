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

    /// <summary>Phase 0.2I -- the auto-start toggle's own subscriber count, same reasoning as
    /// <see cref="ExitRequestedSubscriberCount"/>.</summary>
    public int AutoStartToggleRequestedSubscriberCount { get; private set; }

    /// <summary>Phase 0.2I -- every value <see cref="ITrayIconSurface.SetAutoStartChecked"/> was
    /// ever called with, in order, so a test can observe not just the FINAL checked state but the
    /// exact sequence (e.g. "queried once at Start, then re-queried once after a toggle attempt").
    /// </summary>
    public List<bool> AutoStartCheckedHistory { get; } = [];

    /// <summary>The most recent value passed to <see cref="ITrayIconSurface.SetAutoStartChecked"/>,
    /// or <see langword="null"/> if it was never called.</summary>
    public bool? CurrentAutoStartChecked => AutoStartCheckedHistory.Count > 0 ? AutoStartCheckedHistory[^1] : null;

    private EventHandler? _exitRequested;
    private EventHandler? _autoStartToggleRequested;

    public event EventHandler? ExitRequested
    {
        add { _exitRequested += value; ExitRequestedSubscriberCount++; }
        remove { _exitRequested -= value; ExitRequestedSubscriberCount--; }
    }

    public event EventHandler? AutoStartToggleRequested
    {
        add { _autoStartToggleRequested += value; AutoStartToggleRequestedSubscriberCount++; }
        remove { _autoStartToggleRequested -= value; AutoStartToggleRequestedSubscriberCount--; }
    }

    public void Show()
    {
        if (ThrowOnShow is { } ex) throw ex;
        ShowCallCount++;
    }

    public void SetAutoStartChecked(bool isChecked) => AutoStartCheckedHistory.Add(isChecked);

    /// <summary>Simulates the user choosing the tray's Exit command.</summary>
    public void RaiseExitRequested() => _exitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Simulates the user clicking the auto-start toggle menu item.</summary>
    public void RaiseAutoStartToggleRequested() => _autoStartToggleRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        DisposeCallCount++;
        Disposed = true;
        OnDispose?.Invoke();
    }
}
