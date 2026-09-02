using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the sole production <see cref="IWebBrowserHostBinding"/>
/// implementation: wraps exactly one real, already-resolved E2 <see cref="BrowserHostBinding"/> and
/// delegates every fact and its <see cref="Dispose"/> to it, pure passthrough. Never copies browser
/// facts into a second, independently-mutable snapshot, and never exposes the underlying
/// <see cref="RetainedProcess"/> or any raw handle -- the wrapped binding's own already-frozen
/// no-raw-handle-leak discipline is preserved unchanged through this seam.
/// </summary>
internal sealed class RetainedBrowserHostBinding : IWebBrowserHostBinding
{
    private readonly BrowserHostBinding _binding;

    public RetainedBrowserHostBinding(BrowserHostBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _binding = binding;
    }

    public uint BrowserProcessId => _binding.BrowserProcessId;
    public string? BrowserProcessName => _binding.BrowserProcessName;
    public PackageIdentityResolution BrowserPackageIdentity => _binding.BrowserPackageIdentity;
    public ExecutableSignatureResolution BrowserExecutableSignature => _binding.BrowserExecutableSignature;
    public string? BrowserSignerOrganization => _binding.BrowserSignerOrganization;

    /// <summary>Pure delegation to the wrapped binding's own <see cref="RetainedProcess.CheckLiveness"/>
    /// -- the existing E2 authority, never a second, independently-computed liveness fact.</summary>
    public RetainedProcessLiveness CheckLiveness() => _binding.BrowserProcess.CheckLiveness();

    public void Dispose() => _binding.Dispose();
}
