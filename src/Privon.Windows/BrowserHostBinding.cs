namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6F (E2) -- the mechanical result of a successfully resolved browser-host
/// process binding (<see cref="BrowserHostBindingResolver.Resolve"/>). Carries ONLY mechanical
/// facts about the SELECTED browser process plus its owned <see cref="RetainedProcess"/> -- never a
/// policy verdict, never Web origin/channel/revision/epoch, never the host or cmd intermediary's own
/// PID, and never a certificate metadata bag or window handle. A future <c>Privon.App</c>-owned
/// WebBrowserGate is the only place these facts are ever compared against a supported
/// browser/publisher identity.
///
/// SINGLE_PID_SOURCE (frozen): this type deliberately exposes NO independently stored
/// BrowserProcessId field of its own -- <see cref="BrowserProcessId"/> below is a pure delegation to
/// <see cref="RetainedProcess.ProcessId"/>, and internal construction (no public constructor) means
/// there is no way to build a binding whose "browser PID" could ever disagree with the PID its own
/// retained process object actually owns.
/// </summary>
public sealed class BrowserHostBinding : IDisposable
{
    private bool _disposed;

    internal BrowserHostBinding(
        RetainedProcess browserProcess,
        string browserProcessName,
        PackageIdentityResolution browserPackageIdentity,
        string? browserPackageFamilyName,
        ExecutableSignatureResolution browserExecutableSignature,
        string? browserSignerOrganization,
        BrowserHostTopology topology)
    {
        BrowserProcess = browserProcess;
        BrowserProcessName = browserProcessName;
        BrowserPackageIdentity = browserPackageIdentity;
        BrowserPackageFamilyName = browserPackageFamilyName;
        BrowserExecutableSignature = browserExecutableSignature;
        BrowserSignerOrganization = browserSignerOrganization;
        Topology = topology;
    }

    public RetainedProcess BrowserProcess { get; }

    /// <summary>Derived exclusively from <see cref="BrowserProcess"/>'s own <see cref="RetainedProcess.ProcessId"/>
    /// -- see this type's own SINGLE_PID_SOURCE doc.</summary>
    public uint BrowserProcessId => BrowserProcess.ProcessId;

    public string BrowserProcessName { get; }
    public PackageIdentityResolution BrowserPackageIdentity { get; }
    public string? BrowserPackageFamilyName { get; }
    public ExecutableSignatureResolution BrowserExecutableSignature { get; }
    public string? BrowserSignerOrganization { get; }
    public BrowserHostTopology Topology { get; }

    /// <summary>Disposes the owned <see cref="RetainedProcess"/>. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        BrowserProcess.Dispose();
    }
}
