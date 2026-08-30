using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.0 Gate 1B/1C.1 -- the only production implementation of
/// <see cref="IExecutableSignatureVerifier"/>. Performs ONE full WinVerifyTrust Authenticode
/// verification bound to the caller's already-open file object, per the frozen 0.3.0 authorization
/// contract (Windows Multi-AI Authorization Contract Freeze, Gate 0B/0D, independent-audit
/// correction Gate 1C.1). Owns no reuse/caching state of any kind -- see
/// <see cref="IExecutableSignatureVerifier"/>'s own REUSE_OWNERSHIP doc; that lives exclusively in
/// <see cref="Win32ForegroundTargetSource"/>.
///
/// EXACT_FILE_OBJECT_CONTINUITY (Gate 1C.1, frozen): this type NEVER opens or closes the executable
/// file itself -- <see cref="Verify"/> receives an already-open handle from the caller (never a
/// path) and binds WinVerifyTrust to it via <c>WINTRUST_FILE_INFO.hFile</c>. On a successful S_OK
/// evaluation, the signer certificate is read from that SAME live WinVerifyTrust state via the
/// WTHelper provider-data chain (<see cref="ISignerCertificateExtractor"/>) -- never by reopening
/// the file by path. <c>imagePath</c> is passed through only because <c>WINTRUST_FILE_INFO</c>
/// itself requires a path field alongside the handle; it participates in no identity or extraction
/// decision once the handle exists.
///
/// WINTRUST_FLAGS (frozen): WINTRUST_ACTION_GENERIC_VERIFY_V2, WTD_UI_NONE, WTD_REVOKE_WHOLECHAIN,
/// WTD_CACHE_ONLY_URL_RETRIEVAL (no network revocation retrieval). WTD_LIFETIME_SIGNING_FLAG is
/// deliberately NEVER set, so a properly RFC3161-timestamped signature remains valid after its
/// signing certificate's own validity window has expired.
///
/// REVOCATION_SEMANTICS (Gate 1C.1, narrowed): ONLY WinVerifyTrust's own S_OK continues to
/// signer/EKU/org evaluation. ANY nonzero result -- including a revocation status that could not be
/// determined offline (CERT_E_REVOCATION_FAILURE/CRYPT_E_REVOCATION_OFFLINE/
/// CRYPT_E_NO_REVOCATION_CHECK) -- is NOT Trusted. A recognized definitive negative trust decision
/// (unsigned, untrusted chain, revoked, tampered, expired without a timestamp, explicit distrust,
/// wrong usage) fails closed to <see cref="ExecutableSignatureResolution.Untrusted"/>; every other
/// nonzero HRESULT (recognized provider/system plumbing errors, the three revocation-inconclusive
/// codes above, and any HRESULT this method does not otherwise recognize) fails closed to
/// <see cref="ExecutableSignatureResolution.Unresolved"/> instead -- the distinction has ZERO effect
/// on the authorization decision (TargetGate rejects both identically) and exists purely for
/// diagnostic honesty. If offline users lack sufficient cached trust evidence and WinVerifyTrust
/// does not return S_OK, Claude becomes unsupported for that capture -- accepted 0.3.0 fail-closed
/// behavior; no network revocation retrieval is added to work around it.
///
/// WINTRUST_CLEANUP (Gate 1C.1, frozen): once WTD_STATEACTION_VERIFY has run (regardless of its
/// result), WTD_STATEACTION_CLOSE is attempted unconditionally from a <c>finally</c> block that also
/// covers the signer-extraction step -- an exception thrown during extraction can never bypass
/// CLOSE. The <c>WINTRUST_FILE_INFO</c> marshaled via <c>Marshal.StructureToPtr</c> (which owns a
/// separately-allocated native buffer for its <c>string</c> field) is released via
/// <c>Marshal.DestroyStructure</c> before <c>Marshal.FreeHGlobal</c> frees the outer block.
/// </summary>
internal sealed class Win32ExecutableSignatureVerifier : IExecutableSignatureVerifier
{
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private static readonly nint InvalidHandleValueForHwnd = new(-1);
    private const string CodeSigningEkuOid = "1.3.6.1.5.5.7.3.3";
    private const string OrganizationOid = "2.5.4.10";

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    // Definitive negative trust decisions -- WinVerifyTrust examined the signature/chain and
    // concluded it is NOT trustworthy. Fails closed to Untrusted.
    private static readonly HashSet<int> DefinitiveDistrustHResults = new()
    {
        unchecked((int)0x800B0004), // TRUST_E_SUBJECT_NOT_TRUSTED
        unchecked((int)0x80096010), // TRUST_E_BAD_DIGEST
        unchecked((int)0x800B0100), // TRUST_E_NOSIGNATURE
        unchecked((int)0x800B0101), // CERT_E_EXPIRED
        unchecked((int)0x800B0109), // CERT_E_UNTRUSTEDROOT
        unchecked((int)0x800B010A), // CERT_E_CHAINING
        unchecked((int)0x800B010B), // TRUST_E_FAIL
        unchecked((int)0x800B010C), // CERT_E_REVOKED
        unchecked((int)0x800B010D), // CERT_E_UNTRUSTEDTESTROOT
        unchecked((int)0x800B010F), // CERT_E_CN_NO_MATCH
        unchecked((int)0x800B0110), // CERT_E_WRONG_USAGE
        unchecked((int)0x800B0111), // TRUST_E_EXPLICIT_DISTRUST
        unchecked((int)0x800B0112), // CERT_E_UNTRUSTEDCA
        unchecked((int)0x800B0113), // CERT_E_INVALID_POLICY
        unchecked((int)0x800B0114), // CERT_E_INVALID_NAME
        unchecked((int)0x80092010), // CRYPT_E_REVOKED
    };

    private readonly ISignerCertificateExtractor _stateExtractor;

    public Win32ExecutableSignatureVerifier() : this(new WTHelperSignerCertificateExtractor())
    {
    }

    internal Win32ExecutableSignatureVerifier(ISignerCertificateExtractor stateExtractor)
    {
        ArgumentNullException.ThrowIfNull(stateExtractor);
        _stateExtractor = stateExtractor;
    }

    public ExecutableSignatureResolution Verify(nint fileHandle, string imagePath, out string? signerOrganization)
    {
        signerOrganization = null;

        try
        {
            return VerifyCore(fileHandle, imagePath, out signerOrganization);
        }
        catch (Exception)
        {
            // Any unexpected failure anywhere in this heavily native-interop path (marshaling,
            // unanticipated Win32/crypto exception) fails closed -- never Trusted, never a crash.
            signerOrganization = null;
            return ExecutableSignatureResolution.Unresolved;
        }
    }

    private ExecutableSignatureResolution VerifyCore(nint fileHandle, string imagePath, out string? signerOrganization)
    {
        signerOrganization = null;

        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = imagePath,
            hFile = fileHandle,
            pgKnownSubject = nint.Zero,
        };

        nint fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        bool fileInfoMarshaled = false;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            fileInfoMarshaled = true;

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = nint.Zero,
                pSIPClientData = nint.Zero,
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeWholeChain,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
                hWVTStateData = nint.Zero,
                pwszURLReference = nint.Zero,
                // WTD_LIFETIME_SIGNING_FLAG deliberately NOT set -- see this type's own doc.
                dwProvFlags = WtdCacheOnlyUrlRetrieval,
                dwUIContext = 0,
                pSignatureSettings = nint.Zero,
            };

            Guid action = WintrustActionGenericVerifyV2;
            bool stateMayExist = false;
            try
            {
                int hresult = WinTrustNativeMethods.WinVerifyTrust(InvalidHandleValueForHwnd, action, ref data);
                // A VERIFY call may allocate trust state regardless of its returned HRESULT --
                // CLOSE (below, in this try's own finally) must always be attempted once this point
                // is reached, even if classification/extraction below throws.
                stateMayExist = true;

                // REVOCATION_SEMANTICS (Gate 1C.1): ONLY S_OK continues. ANY nonzero result is not
                // Trusted -- no offline/revocation-inconclusive fallback in 0.3.0.
                var classification = ClassifyTrustResult(hresult);
                if (classification == TrustClassification.Untrusted)
                    return ExecutableSignatureResolution.Untrusted;
                if (classification == TrustClassification.Unresolved)
                    return ExecutableSignatureResolution.Unresolved;

                // classification == Continue (hresult == S_OK only) -- extract the signer
                // certificate from THIS SAME live state while it is still alive.
                return _stateExtractor.ExtractFromState(data.hWVTStateData, out signerOrganization);
            }
            finally
            {
                if (stateMayExist)
                {
                    try
                    {
                        data.dwStateAction = WtdStateActionClose;
                        WinTrustNativeMethods.WinVerifyTrust(InvalidHandleValueForHwnd, action, ref data);
                    }
                    catch (Exception)
                    {
                        // Closing the trust state is best-effort cleanup; a failure here must never
                        // change the already-determined verification outcome.
                    }
                }
            }
        }
        finally
        {
            if (fileInfoMarshaled)
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    internal enum TrustClassification { Continue, Untrusted, Unresolved }

    // PRIVON 0.3.0 Gate 1C.1 -- widened from private to internal SOLELY so the HRESULT-to-
    // classification mapping is directly, deterministically testable with simulated integer HRESULT
    // values (RED-3), with no real signed file and no native WinVerifyTrust call needed at all. Pure
    // visibility change -- no behavior altered by this alone.
    internal static TrustClassification ClassifyTrustResult(int hresult)
    {
        if (hresult == 0) // S_OK -- the ONLY value that continues (Gate 1C.1: no offline fallback).
            return TrustClassification.Continue;

        if (DefinitiveDistrustHResults.Contains(hresult))
            return TrustClassification.Untrusted;

        // Every other HRESULT -- recognized provider/system plumbing errors, the three
        // revocation-inconclusive codes (CERT_E_REVOCATION_FAILURE/CRYPT_E_REVOCATION_OFFLINE/
        // CRYPT_E_NO_REVOCATION_CHECK -- Gate 1C.1 removed their previous "Continue" tolerance), and
        // any unrecognized value -- fails closed to Unresolved. See this type's own
        // REVOCATION_SEMANTICS doc: this split has zero effect on the authorization decision.
        return TrustClassification.Unresolved;
    }

    // PRIVON 0.3.0 Gate 1C.1 -- widened from private to internal SOLELY so the EKU/organization
    // mapping logic is directly testable against synthetic certificates (missing EKU, missing
    // organization) without needing signing tooling to construct a full signed PE for every case.
    // Pure visibility change -- no behavior altered by this alone.
    internal static ExecutableSignatureResolution InspectEkuAndOrganization(X509Certificate2 cert, out string? signerOrganization)
    {
        signerOrganization = null;

        bool hasCodeSigningEku = false;
        foreach (X509Extension extension in cert.Extensions)
        {
            if (extension is X509EnhancedKeyUsageExtension eku
                && eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == CodeSigningEkuOid))
            {
                hasCodeSigningEku = true;
                break;
            }
        }

        if (!hasCodeSigningEku)
            return ExecutableSignatureResolution.Untrusted;

        string? organization = null;
        foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements)
                continue;
            if (rdn.GetSingleElementType().Value == OrganizationOid)
            {
                organization = rdn.GetSingleElementValue();
                break;
            }
        }

        if (string.IsNullOrEmpty(organization))
            return ExecutableSignatureResolution.Untrusted;

        signerOrganization = organization;
        return ExecutableSignatureResolution.Trusted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    private static class WinTrustNativeMethods
    {
        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        public static extern int WinVerifyTrust(
            nint hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);
    }

    // PRIVON 0.3.0 Gate 1C.1 -- the only production implementation of ISignerCertificateExtractor.
    // Reads the signer certificate from a live WinVerifyTrust state's own provider-data chain --
    // never by reopening the executable by path (BLOCKER 1/CERTIFICATE EXTRACTION correction).
    // Nested (not top-level) so it can share the outer type's private EKU/organization inspection
    // helper and native-method scoping discipline directly.
    private sealed class WTHelperSignerCertificateExtractor : ISignerCertificateExtractor
    {
        public ExecutableSignatureResolution ExtractFromState(nint stateData, out string? signerOrganization)
        {
            signerOrganization = null;

            if (stateData == nint.Zero)
                return ExecutableSignatureResolution.Unresolved;

            nint provData = WinTrustHelperNativeMethods.WTHelperProvDataFromStateData(stateData);
            if (provData == nint.Zero)
                return ExecutableSignatureResolution.Unresolved;

            nint provSigner = WinTrustHelperNativeMethods.WTHelperGetProvSignerFromChain(provData, 0, false, 0);
            if (provSigner == nint.Zero)
                return ExecutableSignatureResolution.Unresolved;

            nint provCert = WinTrustHelperNativeMethods.WTHelperGetProvCertFromChain(provSigner, 0);
            if (provCert == nint.Zero)
                return ExecutableSignatureResolution.Unresolved;

            var certHeader = Marshal.PtrToStructure<CRYPT_PROVIDER_CERT_HEADER>(provCert);
            if (certHeader.pCert == nint.Zero)
                return ExecutableSignatureResolution.Unresolved;

            var certContext = Marshal.PtrToStructure<CERT_CONTEXT>(certHeader.pCert);
            if (certContext.pbCertEncoded == nint.Zero || certContext.cbCertEncoded == 0)
                return ExecutableSignatureResolution.Unresolved;

            // Copy ONLY the raw DER-encoded certificate bytes out of WinTrust's own live state --
            // never retain a pointer into it (that memory becomes invalid the instant the caller
            // issues WTD_STATEACTION_CLOSE).
            byte[] encoded = new byte[certContext.cbCertEncoded];
            Marshal.Copy(certContext.pbCertEncoded, encoded, 0, encoded.Length);

            X509Certificate2? cert = null;
            try
            {
                // X509CertificateLoader (the SYSLIB0057-recommended, non-obsolete API) loading raw
                // DER bytes already extracted from the verified trust state -- never a file
                // re-open, and never the obsolete legacy signed-file certificate constructor this
                // replaces.
                cert = X509CertificateLoader.LoadCertificate(encoded);
                return InspectEkuAndOrganization(cert, out signerOrganization);
            }
            catch (CryptographicException)
            {
                return ExecutableSignatureResolution.Unresolved;
            }
            finally
            {
                cert?.Dispose();
            }
        }
    }

    // Partial-prefix view of the real (much larger) native CRYPT_PROVIDER_CERT structure --
    // sequential layout means declaring only the leading fields this type actually reads (cbStruct,
    // pCert) is safe and standard practice; the undeclared trailing fields are simply never
    // materialized.
    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_PROVIDER_CERT_HEADER
    {
        public uint cbStruct;
        public nint pCert; // PCCERT_CONTEXT
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CERT_CONTEXT
    {
        public uint dwCertEncodingType;
        public nint pbCertEncoded;
        public uint cbCertEncoded;
        public nint pCertInfo;
        public nint hCertStore;
    }

    // PRIVON 0.3.0 Gate 1C.1 -- the WinTrust provider-data helper chain used to read the signer
    // certificate out of a live, successfully-VERIFY'd trust state. Deliberately a separate nested
    // class from WinTrustNativeMethods above (a different concern: raw VERIFY/CLOSE actions vs.
    // reading the resulting provider chain).
    private static class WinTrustHelperNativeMethods
    {
        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        public static extern nint WTHelperProvDataFromStateData(nint hStateData);

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        public static extern nint WTHelperGetProvSignerFromChain(
            nint pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        public static extern nint WTHelperGetProvCertFromChain(nint pSgnr, uint idxCert);
    }
}
