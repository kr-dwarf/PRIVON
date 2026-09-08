using Microsoft.Win32.SafeHandles;

namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6F (E2) -- the sole owner of a retained browser process's native handle.
/// Internal construction only (<see cref="BrowserHostBindingResolver"/> is the only production
/// caller) -- a future <c>Privon.App</c> consumer can hold, dispose, and query liveness on this
/// object without ever touching a raw handle itself; App Win32 plumbing is neither required nor
/// possible through this public surface.
///
/// NO_RAW_HANDLE_LEAK (frozen): the owned <see cref="SafeProcessHandle"/> is never exposed publicly
/// in any form (no <see cref="SafeProcessHandle"/>/<see cref="IntPtr"/>/<c>nint</c> public member) --
/// <see cref="ProcessId"/> and <see cref="CheckLiveness"/> are the only facts this type will ever
/// surface.
/// </summary>
public sealed class RetainedProcess : IDisposable
{
    private readonly SafeProcessHandle _handle;
    private readonly IProcessTopologySource _source;
    private bool _disposed;

    internal RetainedProcess(SafeProcessHandle handle, uint processId, IProcessTopologySource source)
    {
        _handle = handle;
        ProcessId = processId;
        _source = source;
    }

    public uint ProcessId { get; }

    /// <summary>PID_REOPEN_FREE, TIMER_FREE, POLL_FREE (frozen): a single zero-timeout wait against
    /// the ONE retained handle this object owns -- see <see cref="RetainedProcessLiveness"/>'s own
    /// doc for the tri-state mapping.</summary>
    public RetainedProcessLiveness CheckLiveness()
    {
        if (_disposed || _handle.IsClosed || _handle.IsInvalid)
            return RetainedProcessLiveness.Unavailable;

        return _source.CheckLiveness(_handle);
    }

    /// <summary>Idempotent -- a second Dispose() call is a safe no-op, matching this codebase's
    /// existing minimal-disposal discipline.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _handle.Dispose();
    }
}
