using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the sole production <see cref="IWebHostBindingSource"/>
/// implementation: delegates entirely to a real, existing, frozen
/// <see cref="BrowserHostBindingResolver"/>, wrapping a successfully resolved
/// <see cref="BrowserHostBinding"/> in <see cref="RetainedBrowserHostBinding"/>. No topology, signer,
/// or browser-identity policy of any kind is duplicated here -- this type is pure delegation plus the
/// one wrapping step E3's own testable seam needs.
/// </summary>
internal sealed class BrowserHostBindingSource : IWebHostBindingSource
{
    private readonly BrowserHostBindingResolver _resolver;

    public BrowserHostBindingSource(BrowserHostBindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    public BrowserHostBindingStatus Resolve(uint hostProcessId, out IWebBrowserHostBinding? binding)
    {
        var status = _resolver.Resolve(hostProcessId, out var realBinding);
        binding = realBinding is null ? null : new RetainedBrowserHostBinding(realBinding);
        return status;
    }
}
