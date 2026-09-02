using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the App-side abstraction over a resolved E2
/// <see cref="BrowserHostBinding"/>'s own mechanical facts. Exists because
/// <see cref="BrowserHostBinding"/>/<see cref="RetainedProcess"/> are constructed exclusively,
/// internally, by <see cref="BrowserHostBindingResolver"/> (no App-visible constructor) -- this
/// interface is the seam a test double can implement instead, so E3's own session/server admission
/// pipeline can be exercised deterministically without a real Win32 process tree. The one production
/// implementation, <see cref="RetainedBrowserHostBinding"/>, is a pure delegation wrapper around a
/// real <see cref="BrowserHostBinding"/> -- never a parallel, independently-mutable snapshot of its
/// facts.
/// </summary>
internal interface IWebBrowserHostBinding : IDisposable
{
    uint BrowserProcessId { get; }
    string? BrowserProcessName { get; }
    PackageIdentityResolution BrowserPackageIdentity { get; }
    ExecutableSignatureResolution BrowserExecutableSignature { get; }
    string? BrowserSignerOrganization { get; }

    /// <summary>Delegates to the wrapped E2 <see cref="RetainedProcess.CheckLiveness"/> -- the SAME
    /// mechanical tri-state fact, never a second, independently-computed liveness authority (Gate
    /// 031F6H section 17: manager lookup must expose an Accepted session only while this reports
    /// <see cref="RetainedProcessLiveness.Alive"/>).</summary>
    RetainedProcessLiveness CheckLiveness();
}
