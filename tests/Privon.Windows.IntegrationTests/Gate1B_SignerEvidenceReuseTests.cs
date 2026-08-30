using System.Diagnostics;
using System.Runtime.InteropServices;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.0 Gate 1B -- closes Gate 0D's DEFERRED_TO_GREEN_SEAM (PROCESS_BOUND_REUSE/
// EXECUTABLE_BINDING/INVALIDATION/CALL_COUNT) now that Win32ForegroundTargetSource.ResolveExecutableSignature
// exists as an injectable, foreground-independent seam over the real reuse logic.
//
// OWNER_LEVEL_TEST_CHOICE: these tests drive REAL Win32 process/file handles through the REAL
// Win32ForegroundTargetSource reuse decision, substituting only the expensive WinVerifyTrust
// primitive with a call-counting SpyExecutableSignatureVerifier. Testing through the public
// TryResolveConfirmedForegroundIdentity instead would additionally require controlling the real OS
// foreground window, which PackageIdentityFactTests.Fact003c already documents as something
// integration tests cannot do deterministically. ResolveExecutableSignature isolates the reuse
// decision from that dependency without touching a single line of the decision logic itself -- the
// correct owner-level test surface Gate 1B calls for.
//
// UNTESTED_BY_CONSTRUCTION, documented rather than skipped:
//   - Genuine PID reuse (same PID number, two different kernel objects) cannot be forced
//     deterministically without faking OpenProcess itself. What IS proven here is that the reuse
//     decision NEVER inspects PID at all -- only CompareObjectHandles -- so PID reuse is defeated
//     by construction: DifferentRealProcess_NeverReusesAcrossDistinctKernelObjects below proves two
//     distinct real kernel objects are never conflated regardless of their PIDs.
//   - "Retained verified executable handle lost/unavailable" (Gate 0D case 9) shares its exact code
//     path (TryGetFileIdentityFromHandle failing) with the file-identity-mismatch case below; no
//     separate scenario exists to force independently without corrupting a private handle field.
public class Gate1B_SignerEvidenceReuseTests
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly string SelfImagePath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Test host process has no ProcessPath.");

    private static readonly string OtherRealFilePath = typeof(Gate1B_SignerEvidenceReuseTests).Assembly.Location;

    private static nint OpenSelfHandle()
    {
        nint handle = TestNativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)Environment.ProcessId);
        Assert.NotEqual(0, handle);
        return handle;
    }

    private static Process StartLongRunningHelperProcess()
    {
        string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var psi = new ProcessStartInfo(cmdPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start helper process.");
    }

    // ---- CASE 1: first observation -> full verification required, exactly once. ----
    [Fact]
    public void FirstObservation_CallsVerifierExactlyOnce_ReturnsTrusted()
    {
        var spy = new SpyExecutableSignatureVerifier();
        var source = new Win32ForegroundTargetSource(spy);
        nint handle = OpenSelfHandle();
        try
        {
            var result = source.ResolveExecutableSignature(handle, SelfImagePath, out string? organization);

            Assert.Equal(ExecutableSignatureResolution.Trusted, result);
            Assert.Equal(spy.SignerOrganizationToReturn, organization);
            Assert.Equal(1, spy.CallCount);
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle);
        }
    }

    // ---- CASE 2/CALL_COUNT: the exact same process, observed a second time via an independently
    // opened handle (as every real guarded check does) and the exact same executable file -> the
    // retained evidence is reused, verifier call count stays at 1. Fresh process-object validation
    // still genuinely occurs on the second call (CompareObjectHandles + GetExitCodeProcess +
    // file-identity re-derivation all run again) -- it is the EXPENSIVE verifier call alone that is
    // skipped. ----
    [Fact]
    public void SecondObservation_SameProcessAndFile_ReusesEvidence_VerifierCallCountStaysOne()
    {
        var spy = new SpyExecutableSignatureVerifier();
        var source = new Win32ForegroundTargetSource(spy);
        nint handle1 = OpenSelfHandle();
        nint handle2 = OpenSelfHandle(); // independently opened -- a genuinely different handle VALUE to the same kernel object
        try
        {
            var first = source.ResolveExecutableSignature(handle1, SelfImagePath, out string? org1);
            var second = source.ResolveExecutableSignature(handle2, SelfImagePath, out string? org2);

            Assert.Equal(ExecutableSignatureResolution.Trusted, first);
            Assert.Equal(ExecutableSignatureResolution.Trusted, second);
            Assert.Equal(org1, org2);
            Assert.Equal(1, spy.CallCount);
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle1);
            TestNativeMethods.CloseHandle(handle2);
        }
    }

    // ---- CASE 3/6 (PID-reuse defeat by construction): two DISTINCT real processes must never
    // share retained evidence -- proving the reuse decision discriminates by kernel object, never by
    // PID, name, or path alone. See this file's own UNTESTED_BY_CONSTRUCTION doc for why genuine PID
    // collision itself cannot be forced deterministically, and why this is the complete proof. ----
    [Fact]
    public void DifferentRealProcess_NeverReusesAcrossDistinctKernelObjects_VerifierCalledAgain()
    {
        var spy = new SpyExecutableSignatureVerifier();
        var source = new Win32ForegroundTargetSource(spy);
        nint selfHandle = OpenSelfHandle();
        using var helper = StartLongRunningHelperProcess();
        nint helperHandle = TestNativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)helper.Id);
        Assert.NotEqual(0, helperHandle);
        try
        {
            var first = source.ResolveExecutableSignature(selfHandle, SelfImagePath, out _);
            var second = source.ResolveExecutableSignature(helperHandle, SelfImagePath, out string? org2);

            Assert.Equal(ExecutableSignatureResolution.Trusted, first);
            Assert.Equal(ExecutableSignatureResolution.Trusted, second);
            Assert.Equal(2, spy.CallCount);
            Assert.Equal(spy.SignerOrganizationToReturn, org2);
        }
        finally
        {
            TestNativeMethods.CloseHandle(selfHandle);
            TestNativeMethods.CloseHandle(helperHandle);
            helper.Kill();
            helper.WaitForExit(5000);
        }
    }

    // ---- CASE 4: the retained process terminates between observations -> no reuse, full
    // verification required again. Uses two handles opened to the SAME real process WHILE it was
    // still alive (handle1 retained via verification, handle2 held independently) so the
    // post-termination check is fully deterministic -- no PID-reuse timing race. ----
    [Fact]
    public void RetainedProcessTerminates_NoReuse_VerifierCalledAgain()
    {
        var spy = new SpyExecutableSignatureVerifier();
        var source = new Win32ForegroundTargetSource(spy);
        using var helper = StartLongRunningHelperProcess();
        nint handle1 = TestNativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)helper.Id);
        nint handle2 = TestNativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)helper.Id);
        Assert.NotEqual(0, handle1);
        Assert.NotEqual(0, handle2);
        try
        {
            string helperImagePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var first = source.ResolveExecutableSignature(handle1, helperImagePath, out _);
            Assert.Equal(ExecutableSignatureResolution.Trusted, first);
            Assert.Equal(1, spy.CallCount);

            helper.Kill();
            helper.WaitForExit(5000);

            var second = source.ResolveExecutableSignature(handle2, helperImagePath, out string? org2);

            Assert.Equal(ExecutableSignatureResolution.Trusted, second);
            Assert.Equal(2, spy.CallCount);
            Assert.Equal(spy.SignerOrganizationToReturn, org2);
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle1);
            TestNativeMethods.CloseHandle(handle2);
        }
    }

    // ---- CASE 7/8 (executable file-object binding): the SAME process handle, but a DIFFERENT real
    // file on disk the second time -> no reuse, full verification required again. Isolates the
    // file-identity comparison from process-object comparison entirely. ----
    [Fact]
    public void SameProcess_DifferentRealFile_NoReuse_VerifierCalledAgain()
    {
        var spy = new SpyExecutableSignatureVerifier();
        var source = new Win32ForegroundTargetSource(spy);
        nint handle = OpenSelfHandle();
        try
        {
            var first = source.ResolveExecutableSignature(handle, SelfImagePath, out _);
            var second = source.ResolveExecutableSignature(handle, OtherRealFilePath, out string? org2);

            Assert.Equal(ExecutableSignatureResolution.Trusted, first);
            Assert.Equal(ExecutableSignatureResolution.Trusted, second);
            Assert.Equal(2, spy.CallCount);
            Assert.Equal(spy.SignerOrganizationToReturn, org2);
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle);
        }
    }

    // ---- Untrusted verification never creates a positive reusable entry -- every repeated
    // observation re-verifies. No negative caching either. ----
    [Fact]
    public void UntrustedVerification_NeverRetained_EveryObservationReVerifies()
    {
        var spy = new SpyExecutableSignatureVerifier { ResultToReturn = ExecutableSignatureResolution.Untrusted };
        var source = new Win32ForegroundTargetSource(spy);
        nint handle = OpenSelfHandle();
        try
        {
            var first = source.ResolveExecutableSignature(handle, SelfImagePath, out string? org1);
            var second = source.ResolveExecutableSignature(handle, SelfImagePath, out string? org2);

            Assert.Equal(ExecutableSignatureResolution.Untrusted, first);
            Assert.Equal(ExecutableSignatureResolution.Untrusted, second);
            Assert.Null(org1);
            Assert.Null(org2);
            Assert.Equal(2, spy.CallCount);
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle);
        }
    }

    // ---- Unresolved verification likewise never creates a positive reusable entry. ----
    [Fact]
    public void UnresolvedVerification_NeverRetained_EveryObservationReVerifies()
    {
        var spy = new SpyExecutableSignatureVerifier { ResultToReturn = ExecutableSignatureResolution.Unresolved };
        var source = new Win32ForegroundTargetSource(spy);
        nint handle = OpenSelfHandle();
        try
        {
            var first = source.ResolveExecutableSignature(handle, SelfImagePath, out string? org1);
            var second = source.ResolveExecutableSignature(handle, SelfImagePath, out string? org2);

            Assert.Equal(ExecutableSignatureResolution.Unresolved, first);
            Assert.Equal(ExecutableSignatureResolution.Unresolved, second);
            Assert.Null(org1);
            Assert.Null(org2);
            Assert.Equal(2, spy.CallCount);
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle);
        }
    }

    // ---- Only a successful Trusted result with a non-empty organization may ever be retained --
    // proven by contrast with the two negative tests above, which never reuse. ----
    [Fact]
    public void TrustedVerification_IsRetained_ThenReused()
    {
        var spy = new SpyExecutableSignatureVerifier { ResultToReturn = ExecutableSignatureResolution.Trusted, SignerOrganizationToReturn = "Test Publisher, Inc." };
        var source = new Win32ForegroundTargetSource(spy);
        nint handle1 = OpenSelfHandle();
        nint handle2 = OpenSelfHandle();
        try
        {
            source.ResolveExecutableSignature(handle1, SelfImagePath, out _);
            var second = source.ResolveExecutableSignature(handle2, SelfImagePath, out string? org2);

            Assert.Equal(ExecutableSignatureResolution.Trusted, second);
            Assert.Equal("Test Publisher, Inc.", org2);
            Assert.Equal(1, spy.CallCount);
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle1);
            TestNativeMethods.CloseHandle(handle2);
        }
    }

    private static class TestNativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);
    }
}

// Deterministic, OS-free double for IExecutableSignatureVerifier -- counts calls and returns a
// configurable result, so Win32ForegroundTargetSource's own reuse decision can be exercised without
// the real ~277ms-median WinVerifyTrust cost.
internal sealed class SpyExecutableSignatureVerifier : IExecutableSignatureVerifier
{
    public int CallCount { get; private set; }
    public ExecutableSignatureResolution ResultToReturn { get; set; } = ExecutableSignatureResolution.Trusted;
    public string? SignerOrganizationToReturn { get; set; } = "Anthropic, PBC";

    public ExecutableSignatureResolution Verify(nint processHandle, string imagePath, out string? signerOrganization)
    {
        CallCount++;
        signerOrganization = ResultToReturn == ExecutableSignatureResolution.Trusted ? SignerOrganizationToReturn : null;
        return ResultToReturn;
    }
}
