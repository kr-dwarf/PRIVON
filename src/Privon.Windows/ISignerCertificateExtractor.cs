namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.0 Gate 1C.1 -- test seam over the post-VERIFY signer/certificate extraction step
/// (WTHelperProvDataFromStateData -&gt; WTHelperGetProvSignerFromChain -&gt; WTHelperGetProvCertFromChain),
/// isolated from <see cref="Win32ExecutableSignatureVerifier"/>'s own WinVerifyTrust VERIFY/CLOSE
/// orchestration so a test can force a failure between them and prove CLOSE still runs
/// unconditionally. The only production implementation reads the certificate from the SAME live
/// WinTrust state <paramref name="stateData"/> identifies -- never by reopening the executable by
/// path.
/// </summary>
internal interface ISignerCertificateExtractor
{
    /// <summary>
    /// Extracts the signer certificate from the live WinVerifyTrust state identified by
    /// <paramref name="stateData"/> (the <c>hWVTStateData</c> handle a prior WTD_STATEACTION_VERIFY
    /// call produced), requires the Code Signing EKU (1.3.6.1.5.5.7.3.3) and a non-empty
    /// Organization (2.5.4.10) subject attribute, and copies ONLY the resulting mechanical
    /// organization string into managed state -- never retains a pointer into WinTrust state, which
    /// becomes invalid the instant the caller closes it. Any inconclusive/failed step (no signer, no
    /// certificate, malformed encoding) returns <see cref="ExecutableSignatureResolution.Unresolved"/>;
    /// a certificate present but missing the EKU or organization returns
    /// <see cref="ExecutableSignatureResolution.Untrusted"/>.
    /// </summary>
    ExecutableSignatureResolution ExtractFromState(nint stateData, out string? signerOrganization);
}
