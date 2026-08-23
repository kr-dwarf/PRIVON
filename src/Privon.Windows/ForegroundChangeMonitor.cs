using System.Runtime.ExceptionServices;

namespace Privon.Windows;

/// <summary>
/// Phase 0.2B (STEP56 contract, STEP57 implementation) -- the production Windows foreground-change
/// detection capability. Owns ONE dedicated background owner thread and ONE message-only Win32 window,
/// installs a system-wide EVENT_SYSTEM_FOREGROUND WinEvent hook (WINEVENT_OUTOFCONTEXT) FROM that same
/// owner thread, and raises <see cref="ForegroundChanged"/> synchronously on that owner thread whenever
/// the OS reports the foreground window has changed. Mirrors <see cref="SessionLockMonitor"/>'s own
/// public shape/lifecycle discipline (single-use <see cref="Start"/>, idempotent-on-success
/// <see cref="Stop"/>/<see cref="Dispose"/>, a bounded shutdown join, cleanup only for startup steps
/// that actually succeeded, in reverse order) -- STEP56's chosen structural precedent, confirmed by
/// STEP57 source inspection to have no necessary difference for this capability's lifecycle shape.
///
/// PURPOSE_BOUNDARY (STEP56, frozen): this type's ONLY job is to signal that Windows foreground
/// ownership changed. It never reads clipboard text, never runs Detection, never knows ChatGPT/
/// TargetGate, never resolves PID/process name/window title, never inspects UI Automation, never
/// intercepts Ctrl+V, never hooks keyboard input, never injects into another process, never mutates the
/// clipboard, and never persists user activity. See <see cref="Win32ForegroundChangeNative"/>'s own
/// WINEVENT_CALLBACK_CONTRACT doc for how the native layer structurally enforces the identity-resolution
/// half of this boundary.
///
/// MICROSOFT_LEARN_CONTRACT (STEP57, verified against live https://learn.microsoft.com documentation at
/// implementation time, not memory or the frozen Phase 0 spike):
///   1. "The client thread that calls SetWinEventHook must have a message loop in order to receive
///      events." (learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwineventhook, Remarks) --
///      this type's owner thread's own GetMessageW/DispatchMessageW loop (<see cref="RunMessageLoop"/>)
///      IS that message loop.
///   2. "For out-of-context events, the event is delivered on the same thread that called
///      SetWinEventHook." (same page, Remarks) -- <see cref="IForegroundChangeNative.SetHook"/> is
///      therefore called FROM the owner thread itself in <see cref="OwnerThreadMain"/> (never a
///      separate caller thread, unlike the old Phase 0 spike's V1/V2 split-thread arrangement -- see
///      STEP56's own report for why that arrangement's own hedged "may run on caller thread" comment was
///      never trustworthy evidence), so the delivery thread and the pumping thread are provably
///      identical.
///   3. "Call this function from the same thread that installed the event hook. UnhookWinEvent fails if
///      called from a thread different from the call that corresponds to SetWinEventHook."
///      (learn.microsoft.com/windows/win32/api/winuser/nf-winuser-unhookwinevent, Remarks) --
///      <see cref="IForegroundChangeNative.Unhook"/> is therefore also called FROM the owner thread,
///      during its own teardown in <see cref="OwnerThreadMain"/>'s <c>finally</c> block, never
///      cross-thread.
///   4. "When you use SetWinEventHook to set a callback in managed code, you should use the GCHandle
///      structure to avoid exceptions. This tells the garbage collector not to move the callback."
///      (SetWinEventHook page, Remarks) -- see <see cref="Win32ForegroundChangeNative"/>'s own doc for
///      the exact rooting mechanism (a GCHandle allocated in SetHook, freed only in Unhook).
///
/// DUPLICATE_EVENT_POLICY (STEP56, frozen): every accepted foreground-change event is raised as-is --
/// no dedup, no debounce, no coalescing, no generation logic, no target-authorization logic. That
/// responsibility belongs entirely to a future App-owned layer (Phase 0.2C), never here.
///
/// EVENT_MODEL: <see cref="ForegroundChanged"/> is a plain, metadata-free <see cref="EventHandler"/> --
/// no HWND, PID, process name, or window title of any kind is ever exposed (STEP56's frozen
/// EVENT_PAYLOAD = NONE decision, matching <see cref="SessionLockMonitor.Locked"/>'s own identical
/// shape).
///
/// CALLBACK_THREAD_CONTRACT: <see cref="ForegroundChanged"/> is raised synchronously on the dedicated
/// owner thread -- never marshaled, never invoked via the thread pool -- exactly mirroring
/// <see cref="SessionLockMonitor.Locked"/>'s/<see cref="ClipboardChangeMonitor.Changed"/>'s own
/// identical contract. A subscriber MUST return promptly; a subscriber exception is caught and dropped
/// here so it can never corrupt this owner thread's message loop or skip native resource cleanup.
///
/// Phase 0.2C (App-layer wiring, evaluated-generation state, ClipboardPrivacyCoordinator integration)
/// is explicitly NOT started by this type -- this monitor exists, fully tested, entirely unreferenced by
/// <c>Privon.App</c> as of this STEP.
/// </summary>
public sealed class ForegroundChangeMonitor : IDisposable
{
    /// <summary>Mirrors <see cref="SessionLockMonitor.StopTimeoutContractDefault"/>'s own exact
    /// precedent and value -- STEP56's "use the existing SessionLockMonitor timeout convention unless
    /// source proves otherwise" instruction; nothing in this component's own mechanics gives any reason
    /// to diverge.</summary>
    internal static readonly TimeSpan StopTimeoutContractDefault = TimeSpan.FromSeconds(5);

    private readonly IForegroundChangeNative _native;
    private readonly TimeSpan _stopTimeout;
    private readonly string _windowClassName = "PrivonForegroundChangeMonitor_" + Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _startupSignal = new(initialState: false);

    private bool _startCalled;
    private bool _disposed;
    private Thread? _ownerThread;
    private nint _hwnd;
    private volatile bool _running;
    private ExceptionDispatchInfo? _startupFailure;

    /// <summary>See this type's own EVENT_MODEL/DUPLICATE_EVENT_POLICY doc above.</summary>
    public event EventHandler? ForegroundChanged;

    public ForegroundChangeMonitor() : this(new Win32ForegroundChangeNative())
    {
    }

    internal ForegroundChangeMonitor(IForegroundChangeNative native, TimeSpan? stopTimeoutOverride = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
        _stopTimeout = stopTimeoutOverride ?? StopTimeoutContractDefault;
    }

    /// <summary>
    /// Single-use, matching <see cref="SessionLockMonitor.Start"/>'s own exact precedent -- a second
    /// call always throws, regardless of whether the first call succeeded or failed. Blocks until every
    /// startup step (window class registration, message-only window creation, SetWinEventHook) has
    /// actually succeeded, or one of them has failed -- never a "half-started" return. On failure,
    /// whatever partial state was created is already cleaned up (in reverse order) before this method
    /// throws.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startCalled)
                throw new InvalidOperationException(
                    "Start has already been called on this ForegroundChangeMonitor instance -- it is single-use.");
            _startCalled = true;
        }

        _ownerThread = new Thread(OwnerThreadMain)
        {
            IsBackground = true,
            Name = "PrivonForegroundChangeMonitor",
        };
        _ownerThread.Start();

        _startupSignal.Wait();
        _startupFailure?.Throw();
    }

    /// <summary>Idempotent and safe before Start, after a failed Start, or after a prior Stop. Posts a
    /// shutdown request to the owner thread's own message queue rather than destroying the HWND
    /// cross-thread.</summary>
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
        // SessionLockMonitor.Stop's own identical precedent.
        if (ownerThread is { IsAlive: true })
        {
            throw new InvalidOperationException(
                $"Foreground change monitor owner thread did not exit within the shutdown timeout ({_stopTimeout.TotalSeconds:F0}s).");
        }
    }

    /// <summary>DISPOSE_FAILURE_BEHAVIOR (mirrors <see cref="SessionLockMonitor.Dispose"/>'s own exact
    /// contract): idempotent on success, but a cleanup failure is never swallowed here -- only marked
    /// fully disposed once <see cref="Stop"/> has actually completed without throwing.</summary>
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
        bool hookInstalled = false;
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

            // MICROSOFT_LEARN_CONTRACT #2: SetHook is called from THIS owner thread -- the same thread
            // that immediately below runs GetMessageW/DispatchMessageW -- so the documented "delivered
            // on the same thread that called SetWinEventHook" guarantee resolves to this exact loop.
            if (!_native.SetHook(hwnd, out int hookError))
                throw new InvalidOperationException($"SetWinEventHook failed (Win32 error {hookError}).");
            hookInstalled = true;

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

            // Reverse-order cleanup, only for steps that actually succeeded -- hook removed before the
            // window is destroyed, window destroyed before the class is unregistered.
            // MICROSOFT_LEARN_CONTRACT #3: Unhook is called from THIS SAME owner thread that called
            // SetHook above -- never cross-thread.
            if (hookInstalled) _native.Unhook(out _);
            if (windowCreated) _native.DestroyWindow(hwnd, out _);
            if (classRegistered) _native.UnregisterWindowClass(_windowClassName, out _);

            // Startup-failure signaling deliberately deferred to here, after cleanup -- same
            // STARTUP_RACE_FIX discipline as SessionLockMonitor/ClipboardChangeMonitor's own
            // OwnerThreadMain: Start()'s blocking wait must only unblock once whatever partial state was
            // created is already cleaned up.
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
                case ForegroundChangeMessageKind.Shutdown:
                    return;

                case ForegroundChangeMessageKind.Changed:
                    RaiseForegroundChanged();
                    break;

                // ForegroundChangeMessageKind.Other: silently ignored.
            }
        }
    }

    private void RaiseForegroundChanged()
    {
        try
        {
            ForegroundChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Subscriber exception isolation only -- never corrupts this owner thread's message loop or
            // skips native resource cleanup. Never logged with any content (there is none to log --
            // EVENT_MODEL is metadata-free).
        }
    }
}
