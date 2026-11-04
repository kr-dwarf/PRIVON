using Privon.App;

namespace Privon.App.Tests;

// Deterministic, WPF-free double for IDecisionPromptSurface -- lets DecisionPromptCoordinatorTests
// drive the single "모두 보호" action and observe Show/DisableProtectAction/ShowNeutralFailure/Close
// without ever constructing a real DecisionPromptWindow (which requires a live WPF Dispatcher/STA
// thread). No mocking framework -- hand-written, matching every other Fake* in this project.
internal sealed class FakeDecisionPromptSurface : IDecisionPromptSurface
{
    public int ShowCallCount { get; private set; }
    public int CloseCallCount { get; private set; }
    public int DisableProtectActionCallCount { get; private set; }
    public string? LastNeutralFailureMessage { get; private set; }
    public bool IsClosed { get; private set; }

    /// <summary>Whether this fake claims to be the foreground window at the moment of a press (a keyboard / screen-reader activation).</summary>
    public bool IsForeground { get; set; }

    /// <summary>Whether this fake claims the press is being raised inside a fresh Enter / Space addressed to the button.</summary>
    public bool ByKeyboard { get; set; }

    /// <summary>The process this fake claims was the active application right before it became active (0 = unknown).</summary>
    public uint PreviousActiveProcessId { get; set; }

    public DecisionPromptInvocation CaptureInvocation() => new(IsForeground, ByKeyboard, PreviousActiveProcessId);

    /// <summary>Optional test hook invoked at the end of <see cref="Close"/> -- lets a test record
    /// cross-object disposal/close ORDER (e.g. proving a decision prompt closes before the tray
    /// surface is disposed) without needing a mocking framework.</summary>
    public Action? OnClose { get; set; }

    public event EventHandler? ProtectAllRequested;
    public event EventHandler? Closed;

    public void Show() => ShowCallCount++;

    public void DisableProtectAction() => DisableProtectActionCallCount++;

    public void ShowNeutralFailure(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        LastNeutralFailureMessage = message;
    }

    public void Close()
    {
        if (IsClosed) return;
        IsClosed = true;
        CloseCallCount++;
        Closed?.Invoke(this, EventArgs.Empty);
        OnClose?.Invoke();
    }

    /// <summary>Simulates the user (or the coordinator's own code) triggering the single "모두
    /// 보호" action.</summary>
    public void RaiseProtectAllRequested() => ProtectAllRequested?.Invoke(this, EventArgs.Empty);
}
