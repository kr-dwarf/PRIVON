using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP14 -- the only production implementation of <see cref="IClipboardWriteTransport"/>.
/// A thin translation layer only: its single member is a direct one-line delegation to an owned
/// <see cref="ClipboardChangeMonitor"/> instance, with no policy, no filtering, no additional
/// state of its own -- mirrors <see cref="ClipboardReadTransport"/>'s own exact shape. No
/// duplicate clipboard logic exists here -- if <see cref="ClipboardChangeMonitor"/>'s own
/// behavior ever needs to change, this wrapper never needs a matching change.
///
/// Not yet constructed by any live composition root (this STEP explicitly does not wire
/// <c>App.xaml.cs</c>). A future composition root that wants read+write over the SAME underlying
/// owner thread/HWND (Phase 3A.4 STEP1's thread-ownership invariant -- exactly one
/// <see cref="ClipboardChangeMonitor"/> instance backing both) must construct exactly one
/// <see cref="ClipboardChangeMonitor"/> and pass it to both this type's and
/// <see cref="ClipboardReadTransport"/>'s internal constructor overload -- never let each type's
/// own public parameterless constructor create its own separate instance if both are used
/// together in the same process. That composition decision belongs to the future live-activation
/// STEP, not this one.
/// </summary>
internal sealed class ClipboardWriteTransport : IClipboardWriteTransport, IDisposable
{
    private readonly ClipboardChangeMonitor _monitor;

    public ClipboardWriteTransport() : this(new ClipboardChangeMonitor())
    {
    }

    internal ClipboardWriteTransport(ClipboardChangeMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
    }

    public Task<ClipboardWriteResult> WriteTextIfSequenceMatchesAsync(
        ForegroundTargetSnapshot expectedTarget, uint expectedSequence, string replacementText) =>
        _monitor.WriteTextIfSequenceMatchesAsync(expectedTarget, expectedSequence, replacementText);

    public void Dispose() => _monitor.Dispose();
}
