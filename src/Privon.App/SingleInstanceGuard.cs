namespace Privon.App;

/// <summary>
/// Phase 0.2I -- the minimal current-user-scoped single-instance primitive: a named
/// <see cref="Mutex"/>, wrapped for the exact non-blocking "did I become the owner, yes or no"
/// question <c>App.xaml.cs</c>'s own startup sequencing needs. Pure BCL (<see cref="Mutex"/>) --
/// no P/Invoke, no IPC, no activation protocol of any kind (Phase 0.2I instruction: "불필요한
/// IPC/복잡한 activation protocol은 만들지 마라"). A second instance's own
/// <see cref="TryAcquire"/> call simply returns <see langword="false"/> immediately -- it never
/// blocks waiting for the first instance to exit (<see cref="Mutex.WaitOne(TimeSpan)"/> with
/// <see cref="TimeSpan.Zero"/>).
///
/// USER_SCOPED_NOT_GLOBAL (Phase 0.2I SINGLE INSTANCE CONTRACT, frozen): the mutex name passed to
/// the constructor is used EXACTLY as given -- this type never prepends <c>Global\</c> (which would
/// make the mutex visible/contended across every session on the machine, not just this user's).
/// The production name <c>App.xaml.cs</c> supplies has no prefix at all, which creates the kernel
/// object in the caller's own session namespace -- the appropriate scope for a per-user desktop
/// utility, not a system-wide one.
///
/// CRASH_RECOVERY (Phase 0.2I "crash 후 영구 lockout 없어야 함", frozen): if a previous owner of
/// this same-named mutex exited (crashed, was killed, ...) without ever calling
/// <see cref="Mutex.ReleaseMutex"/>, the OS marks that mutex "abandoned" -- the very next
/// <see cref="Mutex.WaitOne(TimeSpan)"/> from ANY waiter still successfully acquires it, but .NET
/// surfaces this as an <see cref="AbandonedMutexException"/> instead of a plain
/// <see langword="true"/> return. This type catches that exception and treats it as an ordinary
/// successful acquisition -- exactly the guarantee this contract requires, with no bespoke
/// lockout-detection/recovery logic of its own needed.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _acquired;
    private bool _disposed;

    public SingleInstanceGuard(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _mutex = new Mutex(initiallyOwned: false, name);
    }

    /// <summary>Non-blocking. Returns <see langword="true"/> only if THIS instance became the
    /// owner (first instance, or a prior owner released/abandoned it) -- <see langword="false"/>
    /// immediately if another live instance already owns it. Idempotent to call more than once:
    /// once acquired, later calls simply confirm ownership without re-acquiring (a named
    /// <see cref="Mutex"/> is re-entrant on the SAME thread).</summary>
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acquired) return true;

        try
        {
            _acquired = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // CRASH_RECOVERY -- see this type's own class doc.
            _acquired = true;
        }

        return _acquired;
    }

    /// <summary>Releases ownership (if held) and disposes the underlying <see cref="Mutex"/>.
    /// Idempotent; safe even if <see cref="TryAcquire"/> was never called or never succeeded.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_acquired)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread for some reason -- nothing meaningful to release.
            }
        }

        _mutex.Dispose();
    }
}
