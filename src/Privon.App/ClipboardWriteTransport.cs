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
/// Constructed by the live composition root -- see <see cref="PrivonAppComposition.BuildGraph"/>,
/// which shares the SAME underlying owner thread/HWND with <see cref="ClipboardReadTransport"/>
/// (Phase 3A.4 STEP1's thread-ownership invariant -- exactly one <see cref="ClipboardChangeMonitor"/>
/// instance backing both): <c>BuildGraph</c> constructs exactly one <see cref="ClipboardChangeMonitor"/>
/// and passes it to both this type's and <see cref="ClipboardReadTransport"/>'s internal
/// constructor overload -- never each type's own public parameterless constructor creating its own
/// separate instance.
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
        ForegroundTargetSnapshot expectedTarget, uint expectedSequence, string replacementText,
        IClipboardAuthorizationFreshness? authorizationFreshness = null) =>
        _monitor.WriteTextIfSequenceMatchesAsync(expectedTarget, expectedSequence, replacementText, authorizationFreshness);

    public void Dispose() => _monitor.Dispose();
}
