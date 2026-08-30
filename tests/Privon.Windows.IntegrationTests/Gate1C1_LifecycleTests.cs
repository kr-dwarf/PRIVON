using System.Runtime.InteropServices;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.0 Gate 1C.1 -- LIFECYCLE: proves retained signer evidence is actually released (not
// merely that a Dispose() call is reachable -- Gate1C1_AuthenticodeCorrectionTests.Red6a/6b/6c
// already prove reachability through the real ClipboardChangeMonitor/ComposerTextReader/
// ForegroundTargetInspector production wiring). This file proves the OWNER-LEVEL consequence: after
// Win32ForegroundTargetSource.Dispose(), no stale retained entry can be reused.
public class Gate1C1_LifecycleTests
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly string SelfImagePath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Test host process has no ProcessPath.");

    private static nint OpenSelfHandle()
    {
        nint handle = TestNativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)Environment.ProcessId);
        Assert.NotEqual(0, handle);
        return handle;
    }

    // ---- Ordinary invalidation (a reuse-check miss) already unconditionally disposes+clears the
    // old entry before a fresh acquisition -- proven indirectly by every "no reuse, verifier called
    // again" test in Gate1B_SignerEvidenceReuseTests.cs (DifferentRealProcess/RetainedProcessTerminates/
    // SameProcess_DifferentRealFile all only pass because the old entry's handles are genuinely
    // closed and replaced, not merely shadowed). Not re-asserted here to avoid duplicating that
    // suite; this file covers what that one does not: explicit source Dispose(). ----

    // ---- Source Dispose(): retained evidence must be released such that it can never be reused
    // afterward. Observed indirectly (the private _retained field is not exposed) via the ONLY
    // externally observable consequence: a subsequent ResolveExecutableSignature call for the SAME
    // process+file must NOT reuse the disposed entry -- it must call Verify() again. If Dispose left
    // the entry in place (or merely closed handles without clearing the field), the reuse check
    // would either incorrectly still "match" a now-invalid entry, or throw/behave unpredictably
    // against closed handles -- neither of which is what a correct implementation does. ----
    [Fact]
    public void SourceDispose_ClearsRetainedEvidence_SubsequentObservationReVerifies()
    {
        var spy = new SpyExecutableSignatureVerifier();
        var source = new Win32ForegroundTargetSource(spy);
        nint handle1 = OpenSelfHandle();
        nint handle2 = OpenSelfHandle();
        try
        {
            var first = source.ResolveExecutableSignature(handle1, SelfImagePath, out string? org1);
            Assert.Equal(ExecutableSignatureResolution.Trusted, first);
            Assert.Equal(1, spy.CallCount);

            source.Dispose();

            var second = source.ResolveExecutableSignature(handle2, SelfImagePath, out string? org2);

            Assert.Equal(ExecutableSignatureResolution.Trusted, second);
            Assert.Equal(2, spy.CallCount); // re-verified -- the disposed entry was never reused.
            Assert.Equal(spy.SignerOrganizationToReturn, org2);
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle1);
            TestNativeMethods.CloseHandle(handle2);
        }
    }

    // ---- Double Dispose() on the source itself: no exception, no double handle release. ----
    [Fact]
    public void SourceDoubleDispose_NoException()
    {
        var spy = new SpyExecutableSignatureVerifier();
        var source = new Win32ForegroundTargetSource(spy);
        nint handle = OpenSelfHandle();
        try
        {
            source.ResolveExecutableSignature(handle, SelfImagePath, out _);

            source.Dispose();
            var exception = Record.Exception(() => source.Dispose());

            Assert.Null(exception);
        }
        finally
        {
            TestNativeMethods.CloseHandle(handle);
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
