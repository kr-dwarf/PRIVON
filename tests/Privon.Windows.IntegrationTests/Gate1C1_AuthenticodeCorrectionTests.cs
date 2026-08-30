using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.0 Gate 1C.1 -- RED-first coverage for the independent audit's three blockers (file
// object continuity, revocation-fallback narrowing, retained-handle lifecycle propagation) plus the
// cleanup blocker (WinTrust CLOSE guarantee, Marshal.DestroyStructure) and the certificate
// extraction correction (no path-based re-extraction). Every test in this file is written and run
// against the UNCORRECTED Gate 1B implementation first; RED_VALIDITY in the final report records
// which failed as expected before mutation.
//
// TWO EXCEPTIONS ARE STRUCTURAL RATHER THAN BEHAVIORAL (both explicitly anticipated by this gate's
// own instructions): RED-1's full "same file object" proof and RED-4's full "CLOSE always runs"
// proof both require an injectable seam that does not exist in the uncorrected code -- introducing
// one IS part of this gate's correction, not something that can predate it. Each is therefore first
// proven structurally (the forbidden current shape genuinely exists), with the full behavioral
// proof added as GREEN-side verification once the seam exists (see Gate1C1_VerifierBehaviorTests.cs
// and Gate1C1_LifecycleTests.cs).
public class Gate1C1_AuthenticodeCorrectionTests
{
    private static string ReadWindowsSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.Windows")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate src/Privon.Windows from the test output directory.");

        return File.ReadAllText(Path.Combine(dir.FullName, "src", "Privon.Windows", fileName));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ==================================================================
    // RED-1 / RED-2 -- FILE OBJECT CONTINUITY / NO PATH-BASED CERTIFICATE EXTRACTION
    // ==================================================================

    // ---- RED-2: the production verifier must never re-extract the signer certificate by
    // reopening the file via path (X509Certificate.CreateFromSignedFile) -- it must instead read
    // the certificate from the SAME live WinVerifyTrust state via the WTHelper provider-data chain.
    // Current (Gate 1B) code calls CreateFromSignedFile -- this must RED now. ----
    [Fact]
    public void Red2_VerifierSource_ContainsNoPathBasedCertificateExtraction()
    {
        string source = ReadWindowsSourceFile("Win32ExecutableSignatureVerifier.cs");

        Assert.DoesNotContain("CreateFromSignedFile", source, StringComparison.Ordinal);
    }

    // ---- RED-2b: the corrected extraction mechanism (WTHelperProvDataFromStateData, the
    // documented entry point into a live WinVerifyTrust state's provider chain) must be present.
    // Absent in current (Gate 1B) code -- RED now. ----
    [Fact]
    public void Red2b_VerifierSource_ContainsWinTrustProviderDataExtraction()
    {
        string source = ReadWindowsSourceFile("Win32ExecutableSignatureVerifier.cs");

        Assert.Contains("WTHelperProvDataFromStateData", source, StringComparison.Ordinal);
    }

    // ---- RED-1: retained positive signer evidence must be bound to the EXACT file object
    // WinVerifyTrust verified -- never a second, independently reopened handle. Two
    // FileIdentityNativeMethods.CreateFileW call sites are legitimate both before AND after
    // correction (the reuse-freshness identity check in TryReuseRetainedEvidence, which compares an
    // EXISTING retained entry against a fresh path-derived identity and never touches signer
    // extraction, always needs its own open) -- so a whole-file call COUNT cannot discriminate
    // correct from incorrect. The real discriminator is narrower: TryRetainEvidence -- the method
    // that builds RetainedSignerEvidence, the object ultimately bound into positive signer
    // authorization -- must never itself open a file. Before correction it does (BLOCKER 1's exact
    // violation): it receives only imagePath and reopens by path to derive file identity, producing
    // a SECOND, independent handle from the one WinVerifyTrust verified. ----
    [Fact]
    public void Red1_TryRetainEvidenceMethodBody_NeverCallsCreateFileW()
    {
        string source = ReadWindowsSourceFile("Win32ForegroundTargetSource.cs");
        string methodBody = ExtractMethodBody(source, "TryRetainEvidence");

        Assert.DoesNotContain("CreateFileW", methodBody, StringComparison.Ordinal);
    }

    // ---- Companion positive check: TryRetainEvidence must accept an already-open file handle as a
    // parameter (rather than only a path), proving the correction's ownership-transfer shape exists
    // -- not just that a string happens to be absent. ----
    [Fact]
    public void Red1b_TryRetainEvidenceMethodSignature_AcceptsFileHandleParameter()
    {
        string source = ReadWindowsSourceFile("Win32ForegroundTargetSource.cs");
        int signatureStart = source.IndexOf("TryRetainEvidence(", StringComparison.Ordinal);
        Assert.True(signatureStart >= 0, "TryRetainEvidence method not found.");

        int parenClose = source.IndexOf(')', signatureStart);
        string signature = source.Substring(signatureStart, parenClose - signatureStart);

        Assert.Contains("fileHandle", signature, StringComparison.Ordinal);
    }

    // ---- Simple, self-contained brace-matching extraction of one method's body text, given its
    // name appears exactly once as a method declaration in the file. Good enough for a structural
    // regression test over this project's own consistently-formatted source; not a general C#
    // parser. ----
    private static string ExtractMethodBody(string source, string methodName)
    {
        int nameIndex = source.IndexOf(" " + methodName + "(", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Method '{methodName}' not found in source.");

        int bodyOpenBrace = source.IndexOf('{', nameIndex);
        Assert.True(bodyOpenBrace >= 0, $"Opening brace for '{methodName}' not found.");

        int depth = 0;
        int i = bodyOpenBrace;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) break;
            }
        }

        Assert.True(depth == 0, $"Could not find matching closing brace for '{methodName}'.");
        return source.Substring(bodyOpenBrace, i - bodyOpenBrace + 1);
    }

    // ==================================================================
    // RED-3 -- REVOCATION FAIL-CLOSED
    // ==================================================================

    // ---- For each of the three previously-tolerated offline-revocation HRESULTs, the
    // classification must no longer be Continue -- ANY nonzero WinVerifyTrust result must fail to
    // reach Trusted eligibility. Exercises the REAL production classification function directly
    // (ClassifyTrustResult, widened to internal for this exact purpose) with simulated integer
    // HRESULT values -- no real signed file, no native call, genuinely the actual mapping logic. ----
    [Theory]
    [InlineData(unchecked((int)0x800B010E))] // CERT_E_REVOCATION_FAILURE
    [InlineData(unchecked((int)0x80092013))] // CRYPT_E_REVOCATION_OFFLINE
    [InlineData(unchecked((int)0x80092012))] // CRYPT_E_NO_REVOCATION_CHECK
    public void Red3_RevocationInconclusiveHResults_MustNotClassifyAsContinue(int hresult)
    {
        var classification = Win32ExecutableSignatureVerifier.ClassifyTrustResult(hresult);

        Assert.NotEqual(Win32ExecutableSignatureVerifier.TrustClassification.Continue, classification);
    }

    // ---- S_OK must remain the one and only value that continues to signer/EKU/org evaluation --
    // regression lock, not new RED (already true today and must stay true). ----
    [Fact]
    public void Red3b_SOkStillClassifiesAsContinue()
    {
        var classification = Win32ExecutableSignatureVerifier.ClassifyTrustResult(0);

        Assert.Equal(Win32ExecutableSignatureVerifier.TrustClassification.Continue, classification);
    }

    // ==================================================================
    // RED-4 -- WINTRUST CLOSE ON EXCEPTION (structural precondition)
    // ==================================================================

    // ---- The verifier must expose an injectable seam for the post-VERIFY signer-extraction step,
    // so a test can force a failure between VERIFY and CLOSE and prove CLOSE still runs (see
    // Gate1C1_VerifierBehaviorTests.cs for the full behavioral proof, added once this seam exists).
    // No such constructor exists in current (Gate 1B) code -- RED now. ----
    [Fact]
    public void Red4_Verifier_HasNoInjectableExtractionSeamYet()
    {
        var injectingCtor = typeof(Win32ExecutableSignatureVerifier)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length > 0);

        Assert.True(injectingCtor is not null,
            "Win32ExecutableSignatureVerifier must gain a constructor overload accepting an " +
            "injectable seam for post-VERIFY signer/certificate extraction (Gate 1C.1 RED-4), so a " +
            "test can force a failure between WTD_STATEACTION_VERIFY and WTD_STATEACTION_CLOSE and " +
            "prove CLOSE still runs. Not present in the current Gate 1B source.");
    }

    // ==================================================================
    // RED-5 -- MARSHAL CLEANUP
    // ==================================================================

    // ---- No practical behavioral seam exists for observing an unmanaged memory leak from a unit
    // test without disproportionate machinery (a custom allocator/leak detector) -- this is
    // therefore a structural regression, the honest and proportionate choice here, not "testing
    // implementation trivia": WINTRUST_FILE_INFO carries a marshaled string field (pcwszFilePath,
    // LPWStr), so Marshal.StructureToPtr's own sub-allocation for it must be released via
    // Marshal.DestroyStructure BEFORE Marshal.FreeHGlobal frees the outer block -- otherwise the
    // string's native buffer leaks on every single verification. Absent in current (Gate 1B)
    // code -- RED now. ----
    [Fact]
    public void Red5_VerifierSource_CallsDestroyStructureBeforeFreeingWintrustFileInfo()
    {
        string source = ReadWindowsSourceFile("Win32ExecutableSignatureVerifier.cs");

        Assert.Contains("Marshal.DestroyStructure", source, StringComparison.Ordinal);

        int destroyIndex = source.IndexOf("Marshal.DestroyStructure", StringComparison.Ordinal);
        int freeIndex = source.IndexOf("Marshal.FreeHGlobal", StringComparison.Ordinal);
        Assert.True(destroyIndex >= 0 && freeIndex >= 0 && destroyIndex < freeIndex,
            "Marshal.DestroyStructure must appear, textually, before the Marshal.FreeHGlobal call " +
            "it protects (Gate 1C.1 RED-5).");
    }

    // ==================================================================
    // RED-6 -- COMPOSITION DISPOSAL (behavioral, through the real production constructor seam)
    // ==================================================================

    // ---- ClipboardChangeMonitor already accepts an injectable IForegroundTargetSource through the
    // SAME constructor parameter real production code uses when none is supplied (defaulting to a
    // real Win32ForegroundTargetSource -- now IDisposable per Gate 1B). Disposing the monitor must
    // reach it. Current (Gate 1B) Dispose() never touches _foregroundSource -- RED now. ----
    [Fact]
    public void Red6a_ClipboardChangeMonitorDispose_ReachesDisposableForegroundSource()
    {
        var native = new FakeClipboardMonitorNative();
        var foregroundSource = new FakeForegroundTargetSource();
        var monitor = new ClipboardChangeMonitor(native, foregroundSource: foregroundSource);
        monitor.Start();

        monitor.Dispose();

        Assert.Equal(1, foregroundSource.DisposeCallCount);
    }

    // ---- Same contract for ComposerTextReader. RED now. ----
    [Fact]
    public void Red6b_ComposerTextReaderDispose_ReachesDisposableForegroundSource()
    {
        var textSource = new FakeComposerTextSource();
        var foregroundSource = new FakeForegroundTargetSource();
        var reader = new ComposerTextReader(textSource, foregroundSource: foregroundSource);
        reader.Start();

        reader.Dispose();

        Assert.Equal(1, foregroundSource.DisposeCallCount);
    }

    // ---- The third independent production owner (ForegroundTargetInspector, behind
    // Privon.App.ForegroundTargetCapture) must also become disposable so PrivonAppComposition can
    // reach it. Reflection-based (not a direct .Dispose() call) because the type does not implement
    // IDisposable at all yet in current (Gate 1B) code -- a direct call would be a compile error,
    // not the intended RED evidence. RED now. ----
    [Fact]
    public void Red6c_ForegroundTargetInspector_ImplementsIDisposable()
    {
        bool implementsDisposable = typeof(IDisposable).IsAssignableFrom(typeof(ForegroundTargetInspector));

        Assert.True(implementsDisposable,
            "ForegroundTargetInspector must implement IDisposable (Gate 1C.1 RED-6) so its owner " +
            "(Privon.App.ForegroundTargetCapture, in turn owned by PrivonAppComposition) can " +
            "propagate disposal down to the real Win32ForegroundTargetSource it constructs by " +
            "default. Not implemented in the current Gate 1B source.");
    }

    // ==================================================================
    // RED-7 -- DOUBLE DISPOSE
    // ==================================================================

    // ---- Disposing ClipboardChangeMonitor twice must not throw, and -- once RED-6a is fixed --
    // must not double-dispose the foreground source either (proving the EXISTING _disposed guard
    // correctly short-circuits the second call before it ever reaches the new disposal step). RED
    // now only insofar as the underlying single-dispose behavior (RED-6a) is itself not yet wired;
    // once corrected, this test additionally locks the idempotency guarantee. ----
    [Fact]
    public void Red7_ClipboardChangeMonitorDoubleDispose_NoExceptionAndForegroundSourceDisposedOnce()
    {
        var native = new FakeClipboardMonitorNative();
        var foregroundSource = new FakeForegroundTargetSource();
        var monitor = new ClipboardChangeMonitor(native, foregroundSource: foregroundSource);
        monitor.Start();

        monitor.Dispose();
        var exception = Record.Exception(() => monitor.Dispose());

        Assert.Null(exception);
        Assert.Equal(1, foregroundSource.DisposeCallCount);
    }

    [Fact]
    public void Red7b_ComposerTextReaderDoubleDispose_NoExceptionAndForegroundSourceDisposedOnce()
    {
        var textSource = new FakeComposerTextSource();
        var foregroundSource = new FakeForegroundTargetSource();
        var reader = new ComposerTextReader(textSource, foregroundSource: foregroundSource);
        reader.Start();

        reader.Dispose();
        var exception = Record.Exception(() => reader.Dispose());

        Assert.Null(exception);
        Assert.Equal(1, foregroundSource.DisposeCallCount);
    }
}
