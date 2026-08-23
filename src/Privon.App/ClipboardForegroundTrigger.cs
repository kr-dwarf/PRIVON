using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 0.2E -- the only production implementation of <see cref="IClipboardForegroundTrigger"/>.
/// A thin translation layer only: every member is a direct one-line delegation to an owned
/// <see cref="ForegroundChangeMonitor"/> instance, with no policy, no filtering, no additional
/// state of its own -- mirrors <see cref="ClipboardReadTransport"/>'s/<see cref="SessionLockNotificationAdapter"/>'s
/// own exact shape. The only non-trivial mapping is the event name itself:
/// <see cref="ForegroundChangeMonitor.ForegroundChanged"/> (the Windows-layer name) becomes
/// <see cref="Changed"/> (the App-owned seam's own, deliberately generic name -- see
/// <see cref="IClipboardForegroundTrigger"/>'s own doc for why it is payload-free and generically
/// named rather than clipboard-specific).
///
/// Not yet constructed by any live composition root before this STEP (Phase 0.2D explicitly left
/// this deferred) -- <see cref="PrivonAppComposition"/> is the first, and only, production
/// consumer.
/// </summary>
internal sealed class ClipboardForegroundTrigger : IClipboardForegroundTrigger, IDisposable
{
    private readonly ForegroundChangeMonitor _monitor;

    public ClipboardForegroundTrigger() : this(new ForegroundChangeMonitor())
    {
    }

    internal ClipboardForegroundTrigger(ForegroundChangeMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
    }

    public event EventHandler? Changed
    {
        add => _monitor.ForegroundChanged += value;
        remove => _monitor.ForegroundChanged -= value;
    }

    public void Start() => _monitor.Start();

    public void Stop() => _monitor.Stop();

    public void Dispose() => _monitor.Dispose();
}
