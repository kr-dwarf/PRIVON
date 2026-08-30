namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.0 Gate 1B -- test seam over the expensive Authenticode signature verification
/// primitive (WinVerifyTrust + certificate subject inspection), matching this codebase's existing
/// native-seam pattern (<see cref="IForegroundTargetSource"/>, <see cref="IClipboardMonitorNative"/>):
/// an interface with no policy of its own and no reuse/caching logic of its own, so
/// <see cref="Win32ForegroundTargetSource"/>'s own process-bound reuse decision can be exercised
/// deterministically against a test spy instead of a real, slow (measured median ~277ms, p95
/// ~331ms) WinVerifyTrust call. The only production implementation is
/// <see cref="Win32ExecutableSignatureVerifier"/>.
///
/// REUSE_OWNERSHIP: this interface intentionally has no knowledge that reuse exists. The decision
/// of WHETHER to call <see cref="Verify"/> again for a given process observation -- process-object
/// identity, executable file-object identity, termination, restart -- lives entirely in
/// <see cref="Win32ForegroundTargetSource"/>, never here and never behind this interface. This is
/// deliberate: inventing a caching abstraction at THIS layer would be exactly the generic
/// cache/provider machinery PRIVON 0.3.0's own scope explicitly forbids.
///
/// FACTS_ONLY: never compares the extracted organization against any supported publisher constant
/// -- that decision belongs exclusively to <c>Privon.App</c>'s TargetGate.
/// </summary>
internal interface IExecutableSignatureVerifier
{
    /// <summary>
    /// PRIVON 0.3.0 Gate 1C.1 -- FILE_HANDLE_OWNERSHIP (corrected): <paramref name="fileHandle"/> is
    /// an ALREADY-OPEN handle to the executable, owned and opened exactly once by the CALLER
    /// (<see cref="Win32ForegroundTargetSource"/>) -- this method never opens or closes it, and never
    /// re-resolves the executable by <paramref name="imagePath"/> for any identity or extraction
    /// purpose. <paramref name="imagePath"/> is passed through only because
    /// <c>WINTRUST_FILE_INFO</c> itself requires a path field alongside its file handle; it is never
    /// a second, independent source of truth once <paramref name="fileHandle"/> exists (PATH IS NO
    /// LONGER IDENTITY once the authoritative handle exists).
    ///
    /// EXACT_FILE_OBJECT_CONTINUITY (frozen): the SAME <paramref name="fileHandle"/> flows through
    /// WinVerifyTrust AND, on a successful evaluation, through certificate/signer extraction --
    /// performed from that SAME live WinVerifyTrust state via the WTHelper provider-data chain,
    /// never by reopening the file by path. See <see cref="Win32ExecutableSignatureVerifier"/> for
    /// the full contract.
    ///
    /// Returns <see cref="ExecutableSignatureResolution.Trusted"/> with a non-null, non-empty
    /// <paramref name="signerOrganization"/> only when WinVerifyTrust returns S_OK (ANY nonzero
    /// result is not Trusted -- no offline/revocation-inconclusive fallback in 0.3.0), the signature
    /// carries the Code Signing EKU, and its certificate subject carries a non-empty Organization
    /// (2.5.4.10) attribute. Every other outcome -- unsigned, untrusted chain, missing EKU, empty
    /// organization, or an inconclusive/failed trust-API call -- returns
    /// <see cref="ExecutableSignatureResolution.Untrusted"/> or
    /// <see cref="ExecutableSignatureResolution.Unresolved"/> (never <see cref="ExecutableSignatureResolution.NotInspected"/>,
    /// which this method is never responsible for -- the caller only invokes this method when it has
    /// already decided to inspect) with <paramref name="signerOrganization"/> <see langword="null"/>.
    /// </summary>
    ExecutableSignatureResolution Verify(nint fileHandle, string imagePath, out string? signerOrganization);
}
