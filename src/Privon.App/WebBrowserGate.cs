using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6F (E2) -- the ONE shared source of Chrome/Edge browser-identity policy.
/// Extracted from <see cref="WebTargetGate"/>'s own original Gate 031C SUPPORTED_BROWSER_POLICY
/// (frozen, unchanged semantics) so a future Windows-side mechanical <c>BrowserHostBinding</c> and
/// <see cref="WebTargetGate"/>'s own Web-authorization formula can both consult exactly the same
/// predicate -- never two independently maintained copies of the four browser/publisher literals
/// below.
///
/// PURE FACT PREDICATE: this type never opens a process, never waits on a handle, and never
/// references <see cref="RetainedProcess"/>/<see cref="BrowserHostBinding"/> -- it consumes only the
/// already-resolved mechanical facts a caller supplies, exactly mirroring <see cref="TargetGate"/>'s
/// own Claude rule shape (process-name pre-filter, necessary but INSUFFICIENT, plus
/// <see cref="PackageIdentityResolution.NoPackage"/> plus <see cref="ExecutableSignatureResolution.Trusted"/>
/// plus an exact-Ordinal signer organization).
/// </summary>
internal static class WebBrowserGate
{
    private const string ChromeProcessName = "chrome";
    private const string EdgeProcessName = "msedge";
    private const string GoogleOrganization = "Google LLC";
    private const string MicrosoftOrganization = "Microsoft Corporation";

    public static bool IsSupported(
        string? processName,
        PackageIdentityResolution packageIdentity,
        ExecutableSignatureResolution executableSignature,
        string? signerOrganization) =>
        IsSupportedChrome(processName, packageIdentity, executableSignature, signerOrganization)
        || IsSupportedEdge(processName, packageIdentity, executableSignature, signerOrganization);

    private static bool IsSupportedChrome(
        string? processName, PackageIdentityResolution packageIdentity,
        ExecutableSignatureResolution executableSignature, string? signerOrganization) =>
        string.Equals(processName, ChromeProcessName, StringComparison.OrdinalIgnoreCase)
        && packageIdentity == PackageIdentityResolution.NoPackage
        && executableSignature == ExecutableSignatureResolution.Trusted
        && string.Equals(signerOrganization, GoogleOrganization, StringComparison.Ordinal);

    private static bool IsSupportedEdge(
        string? processName, PackageIdentityResolution packageIdentity,
        ExecutableSignatureResolution executableSignature, string? signerOrganization) =>
        string.Equals(processName, EdgeProcessName, StringComparison.OrdinalIgnoreCase)
        && packageIdentity == PackageIdentityResolution.NoPackage
        && executableSignature == ExecutableSignatureResolution.Trusted
        && string.Equals(signerOrganization, MicrosoftOrganization, StringComparison.Ordinal);
}
