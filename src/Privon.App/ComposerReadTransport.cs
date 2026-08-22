using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP32 -- the only production implementation of <see cref="IComposerReadTransport"/>.
/// A thin translation layer only: its single member is a direct one-line delegation to an owned
/// <see cref="ComposerTextReader"/> instance, with no policy, no filtering, no additional state of
/// its own -- mirrors <see cref="ClipboardReadTransport"/>/<see cref="ClipboardWriteTransport"/>'s
/// own exact shape.
///
/// Not yet constructed by any live composition root (this STEP explicitly does not wire
/// <c>App.xaml.cs</c>). A future composition root that wants to share the SAME
/// <see cref="ComposerTextReader"/> instance across multiple consumers must pass one already-owned
/// instance to the internal constructor overload -- never let the public parameterless constructor
/// create a second, separate one.
/// </summary>
internal sealed class ComposerReadTransport : IComposerReadTransport, IDisposable
{
    private readonly ComposerTextReader _reader;

    public ComposerReadTransport() : this(new ComposerTextReader())
    {
    }

    internal ComposerReadTransport(ComposerTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public Task<ComposerTextReadResult> ReadFocusedComposerTextAsync(ForegroundTargetSnapshot expectedTarget) =>
        _reader.ReadFocusedComposerTextAsync(expectedTarget);

    public void Dispose() => _reader.Dispose();
}
