using Privon.App;

namespace Privon.App.Tests;

// Deterministic, WPF-free double for ISettingsSurface -- lets SettingsCoordinatorTests drive every
// user-intent event and observe Show/Activate/Close/RenderState without ever constructing a real
// WPF Window. No mocking framework -- hand-written, matching every other Fake* in this project.
internal sealed class FakeSettingsSurface : ISettingsSurface
{
    public int ShowCallCount { get; private set; }
    public int ActivateCallCount { get; private set; }
    public int CloseCallCount { get; private set; }
    public bool IsClosed { get; private set; }
    public List<SettingsViewState> RenderedStates { get; } = [];
    public SettingsViewState? LastRenderedState => RenderedStates.Count > 0 ? RenderedStates[^1] : null;

    /// <summary>Optional test hook invoked at the end of <see cref="Close"/> -- lets a test record
    /// cross-object close ORDER without a mocking framework.</summary>
    public Action? OnClose { get; set; }

    public event EventHandler? Closed;
    public event EventHandler<bool>? PhoneToggleRequested;
    public event EventHandler<bool>? EmailToggleRequested;
    public event EventHandler? ResetProtectionScopeRequested;
    public event EventHandler<AddExceptionRequest>? AddExceptionRequested;
    public event EventHandler<UserExceptionValue>? DeleteExceptionRequested;
    public event EventHandler? ResetExceptionsRequested;
    public event EventHandler? ChromeNativeMessagingProvisionRequested;
    public event EventHandler? ChromeNativeMessagingRepairRequested;

    public void Show() => ShowCallCount++;

    public void Activate() => ActivateCallCount++;

    public void Close()
    {
        if (IsClosed) return;
        IsClosed = true;
        CloseCallCount++;
        Closed?.Invoke(this, EventArgs.Empty);
        OnClose?.Invoke();
    }

    public void RenderState(SettingsViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        RenderedStates.Add(state);
    }

    public void RaisePhoneToggleRequested(bool desired) => PhoneToggleRequested?.Invoke(this, desired);

    public void RaiseEmailToggleRequested(bool desired) => EmailToggleRequested?.Invoke(this, desired);

    public void RaiseResetProtectionScopeRequested() => ResetProtectionScopeRequested?.Invoke(this, EventArgs.Empty);

    public void RaiseAddExceptionRequested(AddExceptionRequest request) => AddExceptionRequested?.Invoke(this, request);

    public void RaiseDeleteExceptionRequested(UserExceptionValue value) => DeleteExceptionRequested?.Invoke(this, value);

    public void RaiseResetExceptionsRequested() => ResetExceptionsRequested?.Invoke(this, EventArgs.Empty);

    public void RaiseChromeNativeMessagingProvisionRequested() => ChromeNativeMessagingProvisionRequested?.Invoke(this, EventArgs.Empty);

    public void RaiseChromeNativeMessagingRepairRequested() => ChromeNativeMessagingRepairRequested?.Invoke(this, EventArgs.Empty);
}
