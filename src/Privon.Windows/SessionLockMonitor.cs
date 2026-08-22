using System.Runtime.ExceptionServices;

namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP37/STEP37.1/STEP38 -- the production Windows session-lock detection capability.
/// Owns ONE dedicated background owner thread and ONE message-only Win32 window, registers for
/// THIS session's WTS notifications only, and raises <see cref="Locked"/> synchronously on that
/// owner thread whenever <c>WM_WTSSESSION_CHANGE</c> carries <c>WTS_SESSION_LOCK</c>. Mirrors
/// <see cref="ClipboardChangeMonitor"/>'s own public shape/lifecycle discipline wherever the two
/// capabilities' actual mechanics allow it (single-use <see cref="Start"/>, idempotent-on-success
/// <see cref="Stop"/>/<see cref="Dispose"/>, a bounded shutdown join, cleanup only for startup steps
/// that actually succeeded, in reverse order).
///
/// 0.1_SCOPE (Phase 3C STEP37 audit, frozen): <c>WTS_SESSION_LOCK</c> is the ONLY 0.1 trigger.
/// <c>WTS_SESSION_UNLOCK</c> and every other WTS reason code (logon/logoff, console/remote
/// connect/disconnect, ...) are observed at the native layer (see <see cref="ISessionLockNative.WaitForNextMessage"/>)
/// but never raise <see cref="Locked"/> or any other public notification -- 0.1 App policy for all
/// of them is "no action" (see the STEP37 audit's SESSION_DISCONNECT_HARDENING, left OPEN/optional/
/// deferred, never silently implemented here). This type knows nothing about
/// <c>ClipboardDecisionScopeLifecycle</c>/<c>ClipboardComposerVerifier</c>/"Reset"/"InvalidatePending"/
/// any App policy of any kind -- exactly the same Windows/App boundary already established for
/// <c>ClipboardChangeMonitor</c>/<c>ComposerTextReader</c> (OS event detection here, policy action
/// entirely in <c>Privon.App</c>).
///
/// EVENT_MODEL: <see cref="Locked"/> is a plain, metadata-free <see cref="EventHandler"/> -- no
/// username, session name, session ID, process info, or clipboard/composer content of any kind is
/// ever exposed (the narrowest possible notification, per the STEP37 audit's EVENT_MODEL finding).
///
/// CALLBACK_THREAD_CONTRACT: <see cref="Locked"/> is raised synchronously on the dedicated owner
/// thread -- never marshaled, never invoked via the thread pool -- exactly mirroring
/// <see cref="ClipboardChangeMonitor.Changed"/>'s own identical contract. A subscriber MUST return
/// promptly; a subscriber exception is caught and dropped here so it can never corrupt this owner
/// thread's message loop or skip native resource cleanup.
/// </summary>
public sealed class SessionLockMonitor : IDisposable
{
    /// <summary>Mirrors <see cref="ClipboardChangeMonitor.StopTimeoutContractDefault"/>'s own exact
    /// precedent and value.</summary>
    internal static readonly TimeSpan StopTimeoutContractDefault = TimeSpan.FromSeconds(5);

    private readonly ISessionLockNative _native;
    private readonly TimeSpan _stopTimeout;
    private readonly string _windowClassName = "PrivonSessionLockMonitor_" + Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _startupSignal = new(initialState: false);

    private bool _startCalled;
    private bool _disposed;
    private Thread? _ownerThread;
    private nint _hwnd;
    private volatile bool _running;
    private ExceptionDispatchInfo? _startupFailure;

    /// <summary>See this type's own CALLBACK_THREAD_CONTRACT/EVENT_MODEL doc above.</summary>
    public event EventHandler? Locked;

    public SessionLockMonitor() : this(new Win32SessionLockNative())
    {
    }

    internal SessionLockMonitor(ISessionLockNative native, TimeSpan? stopTimeoutOverride = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
        _stopTimeout = stopTimeoutOverride ?? StopTimeoutContractDefault;
    }

    /// <summary>
    /// Single-use, matching <see cref="ClipboardChangeMonitor.Start"/>'s own exact precedent -- a
    /// second call always throws, regardless of whether the first call succeeded or failed. Blocks
    /// until every startup step (window class registration, message-only window creation,
    /// WTSRegisterSessionNotification) has actually succeeded, or one of them has failed -- never a
    /// "half-started" return. On failure, whatever partial state was created is already cleaned up
    /// (in reverse order) before this method throws.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startCalled)
                throw new InvalidOperationException(
                    "Start has already been called on this SessionLockMonitor instance -- it is single-use.");
            _startCalled = true;
        }

        _ownerThread = new Thread(OwnerThreadMain)
        {
            IsBackground = true,
            Name = "PrivonSessionLockMonitor",
        };
        _ownerThread.Start();

        _startupSignal.Wait();
        _startupFailure?.Throw();
    }

    /// <summary>Idempotent and safe before Start, after a failed Start, or after a prior Stop.
    /// Posts a shutdown request to the owner thread's own message queue rather than destroying the
    /// HWND cross-thread.</summary>
    public void Stop()
    {
        Thread? ownerThread;
        lock (_gate)
        {
            if (!_startCalled || !_running) return;
            ownerThread = _ownerThread;
        }

        _native.PostShutdown(_hwnd);
        ownerThread?.Join(_stopTimeout);

        // A timed-out Join means the owner thread did not confirm exit within the bounded window --
        // this must never be silently treated as a clean stop (fail-closed), mirroring
        // ClipboardChangeMonitor.Stop's own identical precedent.
        if (ownerThread is { IsAlive: true })
        {
            throw new InvalidOperationException(
                $"Session lock monitor owner thread did not exit within the shutdown timeout ({_stopTimeout.TotalSeconds:F0}s).");
        }
    }

    /// <summary>DISPOSE_FAILURE_BEHAVIOR (mirrors <see cref="ClipboardChangeMonitor.Dispose"/>'s own
    /// exact contract): idempotent on success, but a cleanup failure is never swallowed here -- only
    /// marked fully disposed once <see cref="Stop"/> has actually completed without throwing.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        Stop();

        lock (_gate)
        {
            _disposed = true;
        }

        _startupSignal.Dispose();
    }

    private void OwnerThreadMain()
    {
        nint hwnd = 0;
        bool classRegistered = false;
        bool windowCreated = false;
        bool notificationRegistered = false;
        bool startupFailed = false;

        try
        {
            if (!_native.RegisterWindowClass(_windowClassName, out int classError))
                throw new InvalidOperationException($"RegisterClassExW failed (Win32 error {classError}).");
            classRegistered = true;

            if (!_native.CreateMessageOnlyWindow(_windowClassName, out hwnd, out int windowError))
                throw new InvalidOperationException($"CreateWindowExW failed (Win32 error {windowError}).");
            windowCreated = true;
            _hwnd = hwnd;

            if (!_native.RegisterSessionNotification(hwnd, out int registerError))
                throw new InvalidOperationException($"WTSRegisterSessionNotification failed (Win32 error {registerError}).");
            notificationRegistered = true;

            _running = true;
            _startupSignal.Set();

            RunMessageLoop(hwnd);
        }
        catch (Exception ex)
        {
            _startupFailure = ExceptionDispatchInfo.Capture(ex);
            startupFailed = true;
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }

            // Reverse-order cleanup, only for steps that actually succeeded -- notification
            // unregistered before the window is destroyed, window destroyed before the class is
            // unregistered, matching Win32ClipboardMonitorNative's own required teardown order.
            if (notificationRegistered) _native.UnregisterSessionNotification(hwnd, out _);
            if (windowCreated) _native.DestroyWindow(hwnd, out _);
            if (classRegistered) _native.UnregisterWindowClass(_windowClassName, out _);

            // Startup-failure signaling deliberately deferred to here, after cleanup -- same
            // STARTUP_RACE_FIX discipline as ClipboardChangeMonitor.OwnerThreadMain: Start()'s
            // blocking wait must only unblock once whatever partial state was created is already
            // cleaned up.
            if (startupFailed) _startupSignal.Set();
        }
    }

    private void RunMessageLoop(nint hwnd)
    {
        while (true)
        {
            var kind = _native.WaitForNextMessage(hwnd);
            switch (kind)
            {
                case SessionLockMessageKind.Shutdown:
                    return;

                case SessionLockMessageKind.Locked:
                    RaiseLocked();
                    break;

                // SessionLockMessageKind.Other (unlock, logon/logoff, connect/disconnect, ...):
                // silently ignored -- 0.1's frozen scope is WTS_SESSION_LOCK only.
            }
        }
    }

    private void RaiseLocked()
    {
        try
        {
            Locked?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Subscriber exception isolation only -- never corrupts this owner thread's message
            // loop or skips native resource cleanup. Never logged with any content (there is none
            // to log -- EVENT_MODEL is metadata-free).
        }
    }
}
