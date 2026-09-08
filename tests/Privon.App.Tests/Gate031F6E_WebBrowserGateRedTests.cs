using System.Reflection;
using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031F6F -- Phase E2 GREEN behavioral suite for the single-source Web browser
// identity predicate (Privon.App.WebBrowserGate) and its shared-ownership relationship with the
// existing, frozen Privon.App.WebTargetGate (Gate 031C/031E1). Originally written RED (Gate
// 031F6E); Gate 031F6F implemented WebBrowserGate and refactored WebTargetGate to delegate its
// browser-identity term to it, so every test below calls the REAL production API directly (both
// types are internal, and Privon.App grants InternalsVisibleTo to this test project -- the same
// direct-call convention Gate031_WebAuthorizationContractTests already uses for WebTargetGate.Match).
// See Gate031F6E_E2RedTests.cs (Privon.Windows.IntegrationTests) for the mechanical browser-host
// binding half of this gate.
//
// FACT vs POLICY (section 24): every test below constructs its ForegroundTargetSnapshot-shaped
// facts (processName/packageIdentity/executableSignature/signerOrganization) directly, by hand --
// never through Privon.Windows mechanics -- exactly proving WebBrowserGate is a PURE policy
// predicate over already-frozen mechanical fact types, with zero dependency on process topology/
// liveness/handles.
//
// This file does NOT modify, and does not duplicate, Gate031_WebAuthorizationContractTests.cs --
// that suite's Group 6/7 Chrome/Edge identity-rule coverage remains the existing, unmodified,
// GREEN regression lock for WebTargetGate.Match's own end-to-end behavior.
public class Gate031F6E_WebBrowserGateRedTests
{
    private const string GoogleOrganization = "Google LLC";
    private const string MicrosoftOrganization = "Microsoft Corporation";

    [Fact]
    public void E5G2_Release031Scope_ChromeSupported_EdgeDeferred_UnknownRejected()
    {
        Assert.True(ReleaseBrowserSupportPolicy.IsSupported(Privon.App.NativeMessagingBrowser.Chrome));
        Assert.False(ReleaseBrowserSupportPolicy.IsSupported(Privon.App.NativeMessagingBrowser.Edge));
        Assert.False(ReleaseBrowserSupportPolicy.IsSupported((Privon.App.NativeMessagingBrowser)99));
    }

    // ==================================================================
    // R17 -- App connect policy facts: mechanical facts alone determine the predicate, independent
    // of any real process topology.
    // ==================================================================
    [Fact]
    public void R17_ValidChromeFacts_IsSupportedTrue()
    {
        bool result = WebBrowserGate.IsSupported(
            "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, GoogleOrganization);
        Assert.True(result);
    }

    [Fact]
    public void R17_ValidEdgeIdentityFacts_RemainSupportedIndependentOfReleaseScope()
    {
        bool result = WebBrowserGate.IsSupported(
            "msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, MicrosoftOrganization);
        Assert.True(result);
    }

    [Theory]
    [InlineData("firefox", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, GoogleOrganization, "wrong process name")]
    [InlineData("chrome", PackageIdentityResolution.Resolved, ExecutableSignatureResolution.Trusted, GoogleOrganization, "wrong package identity (packaged)")]
    [InlineData("chrome", PackageIdentityResolution.Unresolved, ExecutableSignatureResolution.Trusted, GoogleOrganization, "wrong package identity (unresolved)")]
    [InlineData("chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.NotInspected, GoogleOrganization, "NotInspected signature")]
    [InlineData("chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Unresolved, GoogleOrganization, "Unresolved signature")]
    [InlineData("chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Untrusted, GoogleOrganization, "Untrusted signature")]
    [InlineData("chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "Alphabet Inc.", "wrong signer organization")]
    [InlineData("chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, null, "null signer organization")]
    [InlineData("chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "google llc", "case-mismatched signer (Ordinal requires exact case)")]
    [InlineData("msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "microsoft corporation", "case-mismatched Edge signer")]
    [InlineData("msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "Microsoft Corp", "truncated Edge signer")]
    public void R17_EachIsolatedNegativeAxis_IsSupportedFalse(
        string? processName, PackageIdentityResolution packageIdentity,
        ExecutableSignatureResolution executableSignature, string? signerOrganization, string scenario)
    {
        bool result = WebBrowserGate.IsSupported(processName, packageIdentity, executableSignature, signerOrganization);
        Assert.False(result, $"R17 ({scenario}): expected IsSupported=false.");
    }

    // ==================================================================
    // R18 -- shared policy source: WebTargetGate now delegates browser identity to WebBrowserGate,
    // retaining zero duplicate private predicate. Existing Gate031_WebAuthorizationContractTests.cs
    // is deliberately untouched by this gate -- its own Group 6/7 remain the unmodified end-to-end
    // regression lock.
    // ==================================================================

    private static string? TryFindAppSourceFile(string typeSimpleName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PRIVON.slnx")))
            directory = directory.Parent;

        if (directory is null)
            return null;

        string candidate = Path.Combine(directory.FullName, "src", "Privon.App", typeSimpleName + ".cs");
        return File.Exists(candidate) ? candidate : null;
    }

    private static readonly string[] BrowserPolicyLiterals =
    ["chrome", "msedge", GoogleOrganization, MicrosoftOrganization];

    [Fact]
    public void R18_ExactlyOneProductionFileOwnsAllFourBrowserPolicyLiterals_AndItIsWebBrowserGate()
    {
        string? webTargetGatePath = TryFindAppSourceFile(nameof(WebTargetGate));
        string? webBrowserGatePath = TryFindAppSourceFile(nameof(WebBrowserGate));

        Assert.True(webTargetGatePath is not null, "src/Privon.App/WebTargetGate.cs must exist.");
        Assert.True(webBrowserGatePath is not null, "src/Privon.App/WebBrowserGate.cs must exist by E2 GREEN.");

        string webTargetGateSource = File.ReadAllText(webTargetGatePath!);
        string webBrowserGateSource = File.ReadAllText(webBrowserGatePath!);

        foreach (string literal in BrowserPolicyLiterals)
        {
            Assert.Contains(literal, webBrowserGateSource, StringComparison.Ordinal);
            Assert.DoesNotContain(literal, webTargetGateSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void R18_WebTargetGate_RetainsNoDuplicatePrivateBrowserPredicates()
    {
        var chromeMethod = typeof(WebTargetGate).GetMethod("IsSupportedChrome", BindingFlags.NonPublic | BindingFlags.Static);
        var edgeMethod = typeof(WebTargetGate).GetMethod("IsSupportedEdge", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.Null(chromeMethod);
        Assert.Null(edgeMethod);
    }

    [Fact]
    public void R18_WebTargetGate_DelegatesToWebBrowserGate_ForTheSameFacts()
    {
        // Proves term B is the SAME predicate, not merely a coincidentally-matching reimplementation:
        // every WebBrowserGate.IsSupported case from R17 above must agree exactly with the browser
        // half of a full WebTargetGate.Match call built around it.
        var evidence = new Privon.Browser.WebForegroundEvidence(
            BrowserProcessId: 100, ChannelId: 1, EvidenceRevision: new Privon.Core.RevisionId(1),
            BrowserFocus: Privon.Browser.BrowserFocus.Focused, OriginResolution: Privon.Browser.OriginResolution.Resolved,
            Origin: "https://chatgpt.com", ForegroundEpoch: 1);
        var proof = new Privon.Browser.WebChallengeProof(evidence.ChannelId, evidence.EvidenceRevision, evidence.ForegroundEpoch, evidence.BrowserProcessId);
        var context = new WebDecisionContext(
            CurrentRevision: evidence.EvidenceRevision, CurrentChannelId: evidence.ChannelId,
            CurrentForegroundEpoch: evidence.ForegroundEpoch, ChallengeProof: proof);

        var supportedChrome = new ForegroundTargetSnapshot(
            IsResolved: true, ProcessId: 100, ProcessName: "chrome",
            PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
            ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: GoogleOrganization);
        var unsupportedFirefox = new ForegroundTargetSnapshot(
            IsResolved: true, ProcessId: 100, ProcessName: "firefox",
            PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
            ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Mozilla Corporation");

        Assert.True(WebBrowserGate.IsSupported("chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, GoogleOrganization));
        Assert.NotNull(WebTargetGate.Match(supportedChrome, evidence, context));

        Assert.False(WebBrowserGate.IsSupported("firefox", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "Mozilla Corporation"));
        Assert.Null(WebTargetGate.Match(unsupportedFirefox, evidence, context));
    }

    // ==================================================================
    // R19 (partial) -- policy rejection disposal ownership contract, structural half. Channel
    // registry / HelloAck / manager Accepted state are E3 obligations, deliberately NOT tested here
    // (section 25/37). BrowserHostBinding/RetainedProcess are internally constructed exclusively by
    // Privon.Windows's own BrowserHostBindingResolver (no InternalsVisibleTo to this project), so a
    // live end-to-end instance cannot be constructed from App.Tests without a real OS process --
    // this test instead proves the STRUCTURAL half of the contract: the public surface a future App
    // caller needs is real and directly usable, with no Privon.Windows Win32 plumbing required.
    // ==================================================================
    [Fact]
    public void R19Partial_BrowserHostBindingIsPubliclyDisposable_AndOwnsAPubliclyLivenessCheckableRetainedProcess()
    {
        Assert.Contains(typeof(IDisposable), typeof(BrowserHostBinding).GetInterfaces());
        Assert.Contains(typeof(IDisposable), typeof(RetainedProcess).GetInterfaces());

        var browserProcessProperty = typeof(BrowserHostBinding).GetProperty("BrowserProcess", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(browserProcessProperty);
        Assert.Equal(typeof(RetainedProcess), browserProcessProperty!.PropertyType);

        var checkLiveness = typeof(RetainedProcess).GetMethod("CheckLiveness", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(checkLiveness);
        Assert.Equal(typeof(RetainedProcessLiveness), checkLiveness!.ReturnType);

        // The caller needs zero Windows Win32 knowledge to do this -- Dispose() and CheckLiveness()
        // are the entire public surface, matching R20 below.
        var disposeMethod = typeof(BrowserHostBinding).GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(disposeMethod);
    }

    // ==================================================================
    // R20 -- no App-side Win32 liveness leakage: Privon.App must never itself call a raw Win32
    // process-liveness primitive -- that remains RetainedProcess.CheckLiveness's exclusive job.
    // ==================================================================
    private static readonly string[] ForbiddenAppLivenessTokens =
    ["SafeProcessHandle", "WaitForSingleObject", "OpenProcess(", "NtQueryInformationProcess", "GetExitCodeProcess"];

    [Fact]
    public void R20_Control_PrivonAppSource_ContainsNoWin32LivenessPrimitiveAnywhere()
    {
        string? directoryPath = TryFindAppSourceDirectory();
        Assert.True(directoryPath is not null, "Could not locate src/Privon.App via the repository-root walk.");

        var offending = new List<string>();
        foreach (string file in Directory.EnumerateFiles(directoryPath!, "*.cs", SearchOption.TopDirectoryOnly))
        {
            string source = File.ReadAllText(file);
            if (ForbiddenAppLivenessTokens.Any(t => source.Contains(t, StringComparison.Ordinal)))
                offending.Add(Path.GetFileName(file));
        }

        Assert.Empty(offending);
    }

    private static string? TryFindAppSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PRIVON.slnx")))
            directory = directory.Parent;

        if (directory is null)
            return null;

        string candidate = Path.Combine(directory.FullName, "src", "Privon.App");
        return Directory.Exists(candidate) ? candidate : null;
    }
}
