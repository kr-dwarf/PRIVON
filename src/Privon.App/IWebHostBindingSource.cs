using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the seam <see cref="WebChannelHostServer"/> consults to resolve
/// the mechanical E2 browser-host binding for a connected client's PID, without ever duplicating E2's
/// own topology/signer policy inside this layer. The one production implementation,
/// <see cref="BrowserHostBindingSource"/>, delegates entirely to the existing, frozen
/// <see cref="BrowserHostBindingResolver"/>.
/// </summary>
internal interface IWebHostBindingSource
{
    BrowserHostBindingStatus Resolve(uint hostProcessId, out IWebBrowserHostBinding? binding);
}
