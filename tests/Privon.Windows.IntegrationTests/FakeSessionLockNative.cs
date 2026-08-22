using System.Collections.Concurrent;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Deterministic, OS-free double for ISessionLockNative -- lets SessionLockMonitor's real
// thread/lifecycle/ordering/failure-cleanup logic run on a real background Thread without touching
// any actual Win32 window or WTS session. Message delivery is simulated via an in-memory queue the
// test drives directly (RaiseLocked/RaiseOther), matching WaitForNextMessage's blocking contract
// without a real GetMessage loop. Mirrors FakeClipboardMonitorNative's own exact shape.
internal sealed class FakeSessionLockNative : ISessionLockNative
{
    private readonly BlockingCollection<SessionLockMessageKind> _messages = new();
    private readonly object _log = new();

    public bool RegisterClassResult { get; set; } = true;
    public int RegisterClassError { get; set; }
    public bool CreateWindowResult { get; set; } = true;
    public int CreateWindowError { get; set; }
    public bool RegisterSessionNotificationResult { get; set; } = true;
    public int RegisterSessionNotificationError { get; set; }
    public bool UnregisterClassResult { get; set; } = true;
    public bool DestroyWindowResult { get; set; } = true;
    public bool UnregisterSessionNotificationResult { get; set; } = true;

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

    public bool RegisterSessionNotification(nint hwnd, out int win32Error)
    {
        Log(nameof(RegisterSessionNotification));
        win32Error = RegisterSessionNotificationResult ? 0 : RegisterSessionNotificationError;
        return RegisterSessionNotificationResult;
    }

    public bool UnregisterSessionNotification(nint hwnd, out int win32Error)
    {
        Log(nameof(UnregisterSessionNotification));
        win32Error = 0;
        return UnregisterSessionNotificationResult;
    }

    public bool PostShutdown(nint hwnd)
    {
        if (IgnoreShutdown) return true;
        _messages.Add(SessionLockMessageKind.Shutdown);
        return true;
    }

    public SessionLockMessageKind WaitForNextMessage(nint hwnd) => _messages.Take();

    public void RaiseLocked() => _messages.Add(SessionLockMessageKind.Locked);

    /// <summary>Simulates any non-lock WTS reason code (unlock, logon/logoff, connect/disconnect,
    /// ...) -- already classified as <see cref="SessionLockMessageKind.Other"/> by the real native
    /// layer's own wParam check, exactly as this fake models it directly.</summary>
    public void RaiseOther() => _messages.Add(SessionLockMessageKind.Other);

    // Bypasses IgnoreShutdown entirely -- used by tests to unstick a simulated-unresponsive owner
    // thread once the timeout/failure behavior under test has already been observed.
    public void ForceShutdown() => _messages.Add(SessionLockMessageKind.Shutdown);
}
