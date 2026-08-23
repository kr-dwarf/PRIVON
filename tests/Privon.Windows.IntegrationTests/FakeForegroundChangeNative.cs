using System.Collections.Concurrent;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 0.2B STEP57 -- deterministic, OS-free double for IForegroundChangeNative -- lets
// ForegroundChangeMonitor's real thread/lifecycle/ordering/failure-cleanup logic run on a real
// background Thread without touching any actual Win32 window or WinEvent hook. Message delivery is
// simulated via an in-memory queue the test drives directly (RaiseForegroundChanged/RaiseOther),
// matching WaitForNextMessage's blocking contract without a real GetMessage loop. Mirrors
// FakeSessionLockNative's own exact shape.
internal sealed class FakeForegroundChangeNative : IForegroundChangeNative
{
    private readonly BlockingCollection<ForegroundChangeMessageKind> _messages = new();
    private readonly object _log = new();

    public bool RegisterClassResult { get; set; } = true;
    public int RegisterClassError { get; set; }
    public bool CreateWindowResult { get; set; } = true;
    public int CreateWindowError { get; set; }
    public bool SetHookResult { get; set; } = true;
    public int SetHookError { get; set; }
    public bool UnregisterClassResult { get; set; } = true;
    public bool DestroyWindowResult { get; set; } = true;
    public bool UnhookResult { get; set; } = true;

    // Timeout-testing seam: when true, PostShutdown is a no-op, simulating an owner thread that
    // never responds to a shutdown request. ForceShutdown bypasses this flag entirely.
    public bool IgnoreShutdown { get; set; }

    public List<string> CallLog { get; } = [];

    private void Log(string name)
    {
        lock (_log) CallLog.Add(name);
    }

    public bool RegisterWindowClass(string className, out int win32Error)
    {
        Log(nameof(RegisterWindowClass));
        win32Error = RegisterClassResult ? 0 : RegisterClassError;
        return RegisterClassResult;
    }

    public bool UnregisterWindowClass(string className, out int win32Error)
    {
        Log(nameof(UnregisterWindowClass));
        win32Error = 0;
        return UnregisterClassResult;
    }

    public bool CreateMessageOnlyWindow(string className, out nint hwnd, out int win32Error)
    {
        Log(nameof(CreateMessageOnlyWindow));
        hwnd = CreateWindowResult ? new nint(1) : 0;
        win32Error = CreateWindowResult ? 0 : CreateWindowError;
        return CreateWindowResult;
    }

    public bool DestroyWindow(nint hwnd, out int win32Error)
    {
        Log(nameof(DestroyWindow));
        win32Error = 0;
        return DestroyWindowResult;
    }

    public bool SetHook(nint hwnd, out int win32Error)
    {
        Log(nameof(SetHook));
        win32Error = SetHookResult ? 0 : SetHookError;
        return SetHookResult;
    }

    public bool Unhook(out int win32Error)
    {
        Log(nameof(Unhook));
        win32Error = 0;
        return UnhookResult;
    }

    public bool PostShutdown(nint hwnd)
    {
        if (IgnoreShutdown) return true;
        _messages.Add(ForegroundChangeMessageKind.Shutdown);
        return true;
    }

    public ForegroundChangeMessageKind WaitForNextMessage(nint hwnd) => _messages.Take();

    public void RaiseForegroundChanged() => _messages.Add(ForegroundChangeMessageKind.Changed);

    /// <summary>Simulates the message-only window's queue receiving some unrelated message (neither
    /// the locally-defined shutdown message nor the locally-defined foreground-changed message) --
    /// already classified as <see cref="ForegroundChangeMessageKind.Other"/> by the real native
    /// layer's own message-id check, exactly as this fake models it directly.</summary>
    public void RaiseOther() => _messages.Add(ForegroundChangeMessageKind.Other);

    // Bypasses IgnoreShutdown entirely -- used by tests to unstick a simulated-unresponsive owner
    // thread once the timeout/failure behavior under test has already been observed.
    public void ForceShutdown() => _messages.Add(ForegroundChangeMessageKind.Shutdown);
}
