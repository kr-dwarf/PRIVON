using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP38 -- the only production implementation of <see cref="ISessionLockNotification"/>.
/// A thin translation layer only: every member is a direct one-line delegation to an owned
/// <see cref="SessionLockMonitor"/> instance, with no policy, no filtering, no additional state of
/// its own -- mirrors <see cref="ClipboardReadTransport"/>'s own exact shape.
/// </summary>
internal sealed class SessionLockNotificationAdapter : ISessionLockNotification, IDisposable
{
    private readonly SessionLockMonitor _monitor;

    public SessionLockNotificationAdapter() : this(new SessionLockMonitor())
    {
    }

    internal SessionLockNotificationAdapter(SessionLockMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
    }

    public event EventHandler? Locked
    {
        add => _monitor.Locked += value;
        remove => _monitor.Locked -= value;
    }

    public void Start() => _monitor.Start();

    public void Stop() => _monitor.Stop();

    public void Dispose() => _monitor.Dispose();
}
