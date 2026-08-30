using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.0 Gate 1C.1 -- behavioral coverage for the REAL Win32ExecutableSignatureVerifier
// mapping/ownership logic (not a restatement of TargetGate's policy comparison). Two techniques are
// combined, matching this gate's own explicit allowance ("these may use a native seam/fake WinTrust
// provider if generating every real PE signature variant is impractical"):
//
//  1. REAL files + the REAL, unmodified Win32ExecutableSignatureVerifier for the cases achievable
//     without signing tooling: a genuine Microsoft-signed system binary (S_OK path), a genuine
//     unsigned file, and a genuine tampered-digest file (a real signed binary with one byte flipped
//     after signing).
//  2. Synthetic X509Certificate2 objects run DIRECTLY through the real, production
//     InspectEkuAndOrganization mapping (widened to internal for exactly this purpose) for the two
//     cases that are structurally near-impossible to hit via a real WinVerifyTrust S_OK result
//     (missing EKU / missing organization on a certificate WinVerifyTrust itself already accepted)
//     and impractical to construct as a genuinely self-signed PE without signtool.exe/a CA
//     hierarchy, neither of which is guaranteed present on a dev or CI machine.
//
// NOT COVERED here, and explicitly acknowledged rather than faked: a genuinely wrong/untrusted-chain
// SIGNED PE (would require signtool.exe or equivalent signing tooling to construct) and the three
// revocation-inconclusive HRESULTs (already covered directly against the real production
// ClassifyTrustResult function with simulated HRESULT values in
// Gate1C1_AuthenticodeCorrectionTests.Red3_RevocationInconclusiveHResults_MustNotClassifyAsContinue
// -- not duplicated here).
public class Gate1C1_VerifierBehaviorTests
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private static readonly nint InvalidFileHandle = new(-1);

    // NOT notepad.exe/cmd.exe/powershell.exe: on modern Windows these are CATALOG-signed only
    // (verified via Get-AuthenticodeSignature: SignatureType=Catalog), and WINTRUST_FILE_INFO's
    // embedded-signature check (WTD_CHOICE_FILE) deterministically returns TRUST_E_NOSIGNATURE for
    // a catalog-only file despite it being validly signed by Microsoft -- catalog-based validation
    // is a structurally different WinTrust provider action this gate's frozen contract does not use.
    // dotnet.exe carries a genuine EMBEDDED Authenticode signature and is guaranteed present (this
    // test project requires the .NET SDK to build and run at all).
    private static string RealEmbeddedSignedPath => LocateEmbeddedSignedDotnetExecutable();

    private static string LocateEmbeddedSignedDotnetExecutable()
    {
        string? fromPath = Environment.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator)
            .Select(dir =>
            {
                try { return Path.Combine(dir, "dotnet.exe"); }
                catch { return null; }
            })
            .FirstOrDefault(p => p is not null && File.Exists(p));
        if (fromPath is not null)
            return fromPath;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string candidate = Path.Combine(programFiles, "dotnet", "dotnet.exe");
        if (File.Exists(candidate))
            return candidate;

        throw new InvalidOperationException(
            "Could not locate dotnet.exe (a real, embedded-Authenticode-signed executable) for this test.");
    }

    private static nint OpenForRead(string path)
    {
        nint handle = TestNativeMethods.CreateFileW(path, GenericRead, FileShareRead, 0, OpenExisting, FileAttributeNormal, 0);
        Assert.NotEqual(InvalidFileHandle, handle);
        return handle;
    }

    // ==================================================================
    // Real files, real (unmodified) verifier.
    // ==================================================================

    [Fact]
    public void RealSignedSystemFile_ReturnsTrustedWithNonEmptyOrganization()
    {
        var verifier = new Win32ExecutableSignatureVerifier();
        nint handle = OpenForRead(RealEmbeddedSignedPath);
        try
        {
            var result = verifier.Verify(handle, RealEmbeddedSignedPath, out string? organization);

            Assert.Equal(ExecutableSignatureResolution.Trusted, result);
            Assert.False(string.IsNullOrEmpty(organization));
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle);
        }
    }

    [Fact]
    public void RealUnsignedFile_ReturnsNotTrusted()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"privon_gate1c1_unsigned_{Guid.NewGuid():N}.exe");
        // "MZ" DOS-header magic followed by garbage -- not a valid, signed PE image.
        File.WriteAllBytes(tempPath, new byte[] { 0x4D, 0x5A, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        try
        {
            var verifier = new Win32ExecutableSignatureVerifier();
            nint handle = OpenForRead(tempPath);
            try
            {
                var result = verifier.Verify(handle, tempPath, out string? organization);

                Assert.NotEqual(ExecutableSignatureResolution.Trusted, result);
                Assert.Null(organization);
            }
            finally
            {
                TestNativeMethods.CloseHandle(handle);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void RealSignedFileWithTamperedDigest_ReturnsNotTrusted()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"privon_gate1c1_tampered_{Guid.NewGuid():N}.exe");
        byte[] bytes = File.ReadAllBytes(RealEmbeddedSignedPath);

        // Flip one byte roughly a third of the way into the file -- well inside the code/data
        // region for a real Windows binary of this size, away from the DOS/PE headers at the start
        // and the certificate table typically appended at the end. The exact HRESULT this produces
        // is not asserted (TRUST_E_BAD_DIGEST vs. a different definitive-negative code depending on
        // exactly what was corrupted) -- only that the result is never Trusted.
        int corruptIndex = bytes.Length / 3;
        bytes[corruptIndex] ^= 0xFF;
        File.WriteAllBytes(tempPath, bytes);

        try
        {
            var verifier = new Win32ExecutableSignatureVerifier();
            nint handle = OpenForRead(tempPath);
            try
            {
                var result = verifier.Verify(handle, tempPath, out string? organization);

                Assert.NotEqual(ExecutableSignatureResolution.Trusted, result);
                Assert.Null(organization);
            }
            finally
            {
                TestNativeMethods.CloseHandle(handle);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ==================================================================
    // RED-4 full behavioral proof: extraction failure after a REAL S_OK verify must not propagate
    // and must fail closed to Unresolved. The seam (ISignerCertificateExtractor) did not exist
    // before this gate's correction -- this is the GREEN-side completion Gate1C1_AuthenticodeCorrectionTests.Red4
    // explicitly deferred to.
    // ==================================================================

    private sealed class ThrowingSignerCertificateExtractor : ISignerCertificateExtractor
    {
        public bool WasCalled { get; private set; }

        public ExecutableSignatureResolution ExtractFromState(nint stateData, out string? signerOrganization)
        {
            WasCalled = true;
            signerOrganization = null;
            throw new InvalidOperationException("Simulated extraction failure (Gate 1C.1 RED-4).");
        }
    }

    [Fact]
    public void ExtractionFailureAfterRealSOkVerify_DoesNotPropagate_ReturnsUnresolved()
    {
        var throwingExtractor = new ThrowingSignerCertificateExtractor();
        var verifier = new Win32ExecutableSignatureVerifier(throwingExtractor);
        nint handle = OpenForRead(RealEmbeddedSignedPath);
        try
        {
            ExecutableSignatureResolution result = default;
            string? organization = "not-yet-set";
            var exception = Record.Exception(() =>
                result = verifier.Verify(handle, RealEmbeddedSignedPath, out organization));

            Assert.Null(exception);
            Assert.True(throwingExtractor.WasCalled,
                "Extraction must actually have been reached (a real S_OK verify) for this test to " +
                "prove anything about the exception-safety of the VERIFY -> extract -> CLOSE sequence.");
            Assert.Equal(ExecutableSignatureResolution.Unresolved, result);
            Assert.Null(organization);
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle);
        }
    }

    // ==================================================================
    // EKU / organization mapping -- exercised directly against synthetic certificates through the
    // REAL production InspectEkuAndOrganization method (widened to internal for exactly this
    // purpose; no signing tooling required).
    // ==================================================================

    private static X509Certificate2 CreateSyntheticCertificate(bool includeCodeSigningEku, bool includeOrganization)
    {
        using var rsa = RSA.Create(2048);
        string subjectName = includeOrganization
            ? "CN=Gate1C1 Test Signer, O=Gate1C1 Synthetic Org"
            : "CN=Gate1C1 Test Signer";

        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (includeCodeSigningEku)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, critical: false));
        }

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public void ActualMapping_MissingCodeSigningEku_ReturnsUntrusted()
    {
        using var cert = CreateSyntheticCertificate(includeCodeSigningEku: false, includeOrganization: true);

        var result = Win32ExecutableSignatureVerifier.InspectEkuAndOrganization(cert, out string? organization);

        Assert.Equal(ExecutableSignatureResolution.Untrusted, result);
        Assert.Null(organization);
    }

    [Fact]
    public void ActualMapping_MissingOrganization_ReturnsUntrusted()
    {
        using var cert = CreateSyntheticCertificate(includeCodeSigningEku: true, includeOrganization: false);

        var result = Win32ExecutableSignatureVerifier.InspectEkuAndOrganization(cert, out string? organization);

        Assert.Equal(ExecutableSignatureResolution.Untrusted, result);
        Assert.Null(organization);
    }

    [Fact]
    public void ActualMapping_ValidEkuAndOrganization_ReturnsTrustedWithExactOrganization()
    {
        using var cert = CreateSyntheticCertificate(includeCodeSigningEku: true, includeOrganization: true);

        var result = Win32ExecutableSignatureVerifier.InspectEkuAndOrganization(cert, out string? organization);

        Assert.Equal(ExecutableSignatureResolution.Trusted, result);
        Assert.Equal("Gate1C1 Synthetic Org", organization);
    }

    private static class TestNativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);
    }
}
