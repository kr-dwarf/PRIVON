using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP2 -- the only production implementation of <see cref="IClipboardReadTransport"/>.
/// A thin translation layer only: every member is a direct one-line delegation to an owned
/// <see cref="ClipboardChangeMonitor"/> instance, with no policy, no filtering, no additional
/// state of its own. No duplicate clipboard logic exists here -- if <see cref="ClipboardChangeMonitor"/>'s
/// own behavior ever needs to change, this wrapper never needs a matching change.
///
/// Constructed by the live composition root -- see <see cref="PrivonAppComposition.BuildGraph"/>,
/// which passes the shared <see cref="ClipboardChangeMonitor"/> instance to this type's internal
/// constructor overload.
/// </summary>
internal sealed class ClipboardReadTransport : IClipboardReadTransport, IDisposable
{
    private readonly ClipboardChangeMonitor _monitor;

    public ClipboardReadTransport() : this(new ClipboardChangeMonitor())
    {
    }

    internal ClipboardReadTransport(ClipboardChangeMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
    }

    public event EventHandler<ClipboardChangeNotification>? Changed
    {
        add => _monitor.Changed += value;
        remove => _monitor.Changed -= value;
    }

    public void Start() => _monitor.Start();

    public void Stop() => _monitor.Stop();

    public Task<ClipboardTextReadResult> ReadTextSnapshotAsync(
        ForegroundTargetSnapshot expectedTarget, IClipboardAuthorizationFreshness? authorizationFreshness = null) =>
        _monitor.ReadTextSnapshotAsync(expectedTarget, authorizationFreshness);

    public void Dispose() => _monitor.Dispose();
}
