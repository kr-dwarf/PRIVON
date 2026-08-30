using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.0 Gate 0D -- RED-only structural coverage for the "FROZEN PERFORMANCE CORRECTION":
// expensive Authenticode signer verification must be reusable ONLY while bound to one exact
// retained process kernel object + retained verified executable file identity, never via a
// TTL/PID-only/path-only cache, with fresh foreground/process observations still mandatory on
// every guarded check (sections 6-11 of Gate 0D).
//
// SEAM DESIGN THIS FILE HOLDS THE IMPLEMENTATION TO (specified here, not invented at GREEN time):
// a single new internal interface, Privon.Windows.IExecutableSignatureVerifier, with one method
// shaped like this codebase's existing out-parameter primitives (TryResolveConfirmedForegroundIdentity):
//
//     ExecutableSignatureResolution Verify(nint processHandle, string imagePath, out string? signerOrganization);
//
// injected into Win32ForegroundTargetSource via a constructor overload (defaulting to a real
// WinVerifyTrust-based implementation), exactly mirroring how IForegroundTargetSource itself is the
// injectable seam ForegroundTargetInspector depends on. The REUSE decision (is the just-opened
// process the same kernel object + same verified file identity as last time) is NOT a generic cache
// abstraction -- Gate 0D section 6 explicitly forbids inventing one -- it is ordinary retained
// instance state inside Win32ForegroundTargetSource, the same way this class already retains no
// state today but is the sole owner of the one pinned-handle CAPTURE_FLOW (Gate 2H.3). Only the
// expensive VERIFY primitive is injectable/fakeable; the reuse logic itself is real production code
// under test, once it exists.
//
// WHY MOST CASE-LEVEL BEHAVIOR IS STRUCTURAL, NOT A FAKE/SPY TEST: a test-only fake that implements
// IExecutableSignatureVerifier cannot be written today -- the interface does not exist, and writing
// one would be a production source change (forbidden this gate) disguised as a test file. Every
// CASE below therefore proves the SEAM's required existence/shape precisely enough that the
// implementation gate has no design decision left to make, while the true call-count/reuse
// BEHAVIOR (section 10) is recorded as DEFERRED_TO_GREEN_SEAM in this gate's own final report --
// not silently skipped, and not faked with a meaningless always-fails placeholder.
public class Gate0D_SignerVerificationReuseStructuralTests
{
    private static Assembly WindowsAssembly => typeof(ForegroundTargetSnapshot).Assembly;

    private static Type? FindType(string simpleName) =>
        WindowsAssembly.GetTypes().FirstOrDefault(t => t.Name == simpleName);

    // ==================================================================
    // Sections 6/7/10/11 -- the injectable verification seam itself.
    // ==================================================================

    // ---- Gate0D-C1 (unblocks CASE 1-6, section 6): the injectable verifier seam must exist.
    // Without it, expensive WinVerifyTrust work cannot be substituted with a test spy, so no
    // reuse/call-count behavior (CASE 1: first observation requires full verification; CASE 2: same
    // exact process object may reuse it; CASE 3: same PID but a different kernel object must NOT
    // reuse it -- defeating PID reuse; CASE 4: process exit invalidates; CASE 5: Claude restart is a
    // cache miss; CASE 6: Claude update is a cache miss) can ever be exercised outside a real,
    // slow, environment-dependent WinVerifyTrust call. ----
    [Fact]
    public void Gate0D_C1_IExecutableSignatureVerifier_SeamExists()
    {
        var type = FindType("IExecutableSignatureVerifier");

        Assert.True(type is not null,
            "Privon.Windows must define an injectable IExecutableSignatureVerifier seam so " +
            "Win32ForegroundTargetSource's own process-bound reuse logic (Gate 0D CASE 1-6) can be " +
            "exercised against a deterministic test double instead of a real, slow WinVerifyTrust " +
            "call -- exactly the role IForegroundTargetSource already plays for foreground/process " +
            "identity. Not present in the current 0.2.1 source.");
    }

    // ---- Gate0D-C2: the seam's one method must be shaped like this codebase's other native-fact
    // primitives -- takes the ALREADY-PINNED process handle and the already-resolved image path
    // (never re-resolving either itself, matching SINGLE_HANDLE_IDENTITY/PATH_IS_NEVER_IDENTITY),
    // and reports the same ExecutableSignatureResolution/SignerOrganization pair the fact model
    // (Gate0D_ExecutableSignatureFactModelTests) requires. ----
    [Fact]
    public void Gate0D_C2_IExecutableSignatureVerifier_HasExpectedVerifyShape()
    {
        var verifierType = FindType("IExecutableSignatureVerifier");
        Assert.True(verifierType is not null,
            "Cannot verify IExecutableSignatureVerifier's method shape because the type does not " +
            "exist yet -- see Gate0D-C1.");

        var resolutionType = FindType("ExecutableSignatureResolution");
        Assert.True(resolutionType is not null,
            "Cannot verify IExecutableSignatureVerifier's return type because " +
            "ExecutableSignatureResolution does not exist yet (see the fact-model RED suite).");

        bool hasExpectedMethod = verifierType!.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.ReturnType == resolutionType
                && m.GetParameters().Length == 3
                && (m.GetParameters()[0].ParameterType == typeof(nint) || m.GetParameters()[0].ParameterType == typeof(IntPtr))
                && m.GetParameters()[1].ParameterType == typeof(string)
                && m.GetParameters()[2].IsOut
                && m.GetParameters()[2].ParameterType == typeof(string).MakeByRefType());

        Assert.True(hasExpectedMethod,
            "IExecutableSignatureVerifier must expose exactly one method shaped " +
            "ExecutableSignatureResolution Verify(nint processHandle, string imagePath, out string? " +
            "signerOrganization) -- the pinned-handle-in, facts-out shape every other native " +
            "identity primitive in this assembly already follows.");
    }

    // ---- Gate0D-C3 (unblocks CASE 2/3, section 6 + section 10's call-count contract): the
    // injection point. Win32ForegroundTargetSource must accept an IExecutableSignatureVerifier so a
    // test can install a call-counting spy and drive REAL reuse-decision code with it -- the
    // reuse/PID-reuse-defeat logic itself must live in production Win32ForegroundTargetSource
    // (section 12: Privon.Windows owns mechanical signer verification/reuse), never duplicated in a
    // test fake. ----
    [Fact]
    public void Gate0D_C3_Win32ForegroundTargetSource_AcceptsInjectableSignatureVerifier()
    {
        var verifierType = FindType("IExecutableSignatureVerifier");
        Assert.True(verifierType is not null,
            "Cannot verify the injection point because IExecutableSignatureVerifier does not " +
            "exist yet -- see Gate0D-C1.");

        bool hasInjectingCtor = typeof(Win32ForegroundTargetSource)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(c => c.GetParameters().Any(p => p.ParameterType == verifierType));

        Assert.True(hasInjectingCtor,
            "Win32ForegroundTargetSource must gain a constructor overload accepting an " +
            "IExecutableSignatureVerifier (defaulting, in the parameterless constructor, to a real " +
            "WinVerifyTrust-based implementation) -- the same DI-friendly pattern this class " +
            "already has no need for today only because it currently owns no injectable " +
            "sub-primitive. Required so CASE 1-6 (section 6) and the call-count contract (section " +
            "10) can be exercised deterministically. See this gate's DEFERRED_TO_GREEN_SEAM note " +
            "for why the CASE-level reuse behavior itself is not further testable until this " +
            "constructor exists.");
    }

    // ==================================================================
    // Section 8/11 -- freshness must not be cached; reuse state must be instance-scoped.
    // ==================================================================

    // ---- Gate0D-C4 (section 11's "PRIVON restart / new mechanical identity owner lifetime"
    // trigger, section 8's "no TTL/shared cache"): any retained signer-evidence state must be
    // ordinary INSTANCE state on Win32ForegroundTargetSource, never static/shared across instances
    // or the process lifetime -- otherwise a fresh Win32ForegroundTargetSource (e.g. after a
    // hypothetical future re-composition) could inherit stale evidence it never itself verified,
    // and no in-process boundary could ever force re-verification. This is checkable NOW, and
    // already holds (no such field exists yet) -- a forward regression lock, not new RED, matching
    // this codebase's own "no generic cache abstraction" discipline (section 6). ----
    [Fact]
    public void Gate0D_C4_Win32ForegroundTargetSource_HasNoStaticMutableSignerEvidenceState()
    {
        var suspiciousStaticFields = typeof(Win32ForegroundTargetSource)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => !f.IsInitOnly && !f.IsLiteral)
            .Where(f => new[] { "Signature", "Signer", "Verifier", "Evidence", "Cache", "Trust" }
                .Any(k => f.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Name)
            .ToList();

        Assert.Empty(suspiciousStaticFields);
    }

    // ==================================================================
    // Section 12 -- architecture ownership boundaries.
    // ==================================================================

    // ---- Gate0D-C5: TargetGate must never itself touch a process/file handle or call
    // WinVerifyTrust -- it owns publisher/name/PFN POLICY only, never mechanical verification or
    // its reuse lifetime (section 12). Checkable now; already holds -- a forward regression lock
    // guarding against the implementation gate mis-placing the verifier call in Privon.App. ----
    [Fact]
    public void Gate0D_C5_TargetGateSource_NeverPerformsMechanicalSignatureWork()
    {
        string source = File.ReadAllText(FindAppSourceFile("TargetGate.cs"));
        string[] forbidden = { "WinVerifyTrust", "OpenProcess", "CloseHandle", "SafeFileHandle", "WINTRUST" };

        foreach (string token in forbidden)
        {
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindAppSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate src/Privon.App from the test output directory.");

        return Path.Combine(dir.FullName, "src", "Privon.App", fileName);
    }

    // ---- Gate0D-C6: Privon.Windows must never contain Anthropic/Claude/AnthropicClaude/Squirrel
    // product-policy naming, extending the existing PrivonWindowsAssembly_HasNoChatGptOrTargetGate-
    // Naming discipline (Privon.App.Tests/TargetGateTests.cs) to the new publisher this gate
    // introduces -- signer verification is mechanical (Privon.Windows owns HOW), publisher
    // acceptance is policy (Privon.App.TargetGate owns WHO). Checkable now; already holds. ----
    [Fact]
    public void Gate0D_C6_PrivonWindowsAssembly_HasNoAnthropicOrClaudeProductNaming()
    {
        string[] forbidden = { "Anthropic", "Claude", "AnthropicClaude", "Squirrel" };

        var offending = WindowsAssembly.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offending);
    }
}
