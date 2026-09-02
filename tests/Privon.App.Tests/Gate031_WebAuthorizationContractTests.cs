using System.Reflection;
using Privon.App;
using Privon.Browser;
using Privon.Core;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031D (RED) / Gate 031E1 (this revision) -- coverage for the frozen Web
// Authorization Contract (Gates 031A/031B/031C -- research/audit/measurement/contract-freeze).
//
// Gate 031E1 implemented the frozen mechanical fact model (Privon.Browser.WebForegroundEvidence)
// and the pure Privon.App.WebTargetGate policy (five-origin allowlist, Chrome/Edge identity rules,
// PID/focus/revision/epoch/challenge conjunction) -- so every test below that exercises PURE POLICY
// now binds directly to the real production API instead of reflection. What Gate 031E1 explicitly
// did NOT implement -- no Native Messaging channel, no extension, no protocol parser, no real
// decision-time challenge/response transport, no channel-lifecycle manager, and no
// ClipboardPrivacyCoordinator integration -- remains RED exactly where it was, and is labeled
// EXPECTED_DEFERRED_RED in this file's own comments rather than faked.
//
// TEST_BINDING_UPDATE (Gate 031E1 section 21): reflection is kept ONLY where it still serves a real
// structural purpose (Group 14/15/18's assembly/type-shape scans, which must keep working via
// reflection even after implementation, exactly like TargetGateTests.PrivonWindowsAssembly_Has...
// already does for Privon.Windows). Every call site that previously invoked the not-yet-existing
// WebTargetGate.Match via reflection now calls it directly -- WebDecisionContext (terms C/F/G/H)
// is the one addition to the invocation shape, added because Gate 031C froze the FORMULA and the
// FACT MODEL but never froze a concrete method signature (see Gate 031D's own note on this). No
// assertion's expected outcome (WEB_AUTHORIZED vs OUTSIDE) was changed by this update -- only how
// the production API is reached.
//
// RED_DESIGN_PRINCIPLE (still true where a test remains RED): every deferred test either targets a
// production seam that genuinely does not exist yet, or, for Group 15, records an unconditional
// failure describing the exact required behavior -- never a fabricated implementation.
//
// Note what is DELIBERATELY ABSENT from this file: there is no test asserting
// TargetGate.IsSupportedTarget(chromeOrEdgeSnapshot) should someday return true. Gate 031C froze
// WebTargetGate as an ADDITIVE, DISJOINT gate from TargetGate -- TargetGate must NEVER recognize a
// browser, today or after Web ships. TargetGateTests.cs / Gate0D_TargetGateAndClaudeRuleTests.cs
// already lock that disjointness (both remain GREEN, unmodified, in this gate's own regression run)
// and are not duplicated here.
public class Gate031_WebAuthorizationContractTests
{
    // ==================================================================
    // Frozen contract data (Gate 031C FINAL REPORT) -- duplicated here as named constants,
    // deliberately, exactly like TargetGateTests.SupportedPfn's own precedent: this suite must
    // fail loudly if a future implementation's allowlist/rules ever drift from what Gate 031C
    // actually froze, not silently track whatever production happens to contain.
    // ==================================================================
    private const string ChatGptOrigin = "https://chatgpt.com";
    private const string ClaudeOrigin = "https://claude.ai";
    private const string GeminiOrigin = "https://gemini.google.com";
    private const string GrokOrigin = "https://grok.com";
    private const string DeepSeekOrigin = "https://chat.deepseek.com";

    private const string GoogleOrganization = "Google LLC";
    private const string MicrosoftOrganization = "Microsoft Corporation";

    private static Assembly AppAssembly => typeof(TargetGate).Assembly;

    // Still reflection-based, deliberately -- Group 14/15/18 test the SHAPE of the Privon.Browser
    // assembly itself (does a seam exist, does it leak product strings), which must keep working by
    // reflection even after implementation, exactly like
    // TargetGateTests.PrivonWindowsAssembly_HasNoChatGptOrTargetGateNaming already does for
    // Privon.Windows.
    private static Assembly BrowserAssembly => typeof(WebForegroundEvidence).Assembly;

    // ==================================================================
    // Native browser-identity snapshots -- built from the REAL, already-shipping production types
    // (ForegroundTargetSnapshot / PackageIdentityResolution / ExecutableSignatureResolution), since
    // Gate 031C froze the Chrome/Edge rules to reuse the SAME NoPackage+Trusted+Organization shape
    // Claude's own rule already uses (Gate 1B).
    // ==================================================================

    private static ForegroundTargetSnapshot ChromeSnapshot(
        bool isResolved = true, string processName = "chrome",
        PackageIdentityResolution packageIdentity = PackageIdentityResolution.NoPackage,
        ExecutableSignatureResolution signature = ExecutableSignatureResolution.Trusted,
        string? organization = GoogleOrganization, uint processId = 100) =>
        new(IsResolved: isResolved, ProcessId: processId, ProcessName: processName,
            PackageIdentity: packageIdentity, PackageFamilyName: null,
            ExecutableSignature: signature, SignerOrganization: organization);

    private static ForegroundTargetSnapshot EdgeSnapshot(
        bool isResolved = true, string processName = "msedge",
        PackageIdentityResolution packageIdentity = PackageIdentityResolution.NoPackage,
        ExecutableSignatureResolution signature = ExecutableSignatureResolution.Trusted,
        string? organization = MicrosoftOrganization, uint processId = 200) =>
        new(IsResolved: isResolved, ProcessId: processId, ProcessName: processName,
            PackageIdentity: packageIdentity, PackageFamilyName: null,
            ExecutableSignature: signature, SignerOrganization: organization);

    // ==================================================================
    // Web evidence + decision-context construction, and the shared OUTSIDE assertion -- all direct
    // calls into the real production API now that it exists (Gate 031E1).
    // ==================================================================

    private static WebForegroundEvidence BuildEvidence(
        uint browserProcessId, long channelId, long evidenceRevision,
        BrowserFocus focus, OriginResolution originResolution, string? origin, long foregroundEpoch) =>
        new(browserProcessId, channelId, new RevisionId(evidenceRevision), focus, originResolution, origin, foregroundEpoch);

    // A bound proof that agrees with the evidence on all four of its own fields -- Gate 031F3's
    // three-way agreement requires this alongside a matching context for any test that isn't ITSELF
    // about proof mismatch (that axis is covered by Gate031D2_WebMechanicsRedTests' own R3 suite).
    private static WebChallengeProof MatchingProof(WebForegroundEvidence evidence) =>
        new(evidence.ChannelId, evidence.EvidenceRevision, evidence.ForegroundEpoch, evidence.BrowserProcessId);

    // A decision context that agrees with the evidence on every C/F/G term, carrying a matching
    // bound proof for term H -- so a test that is not ABOUT channel/revision/epoch/proof staleness
    // doesn't need to construct either one by hand.
    private static WebDecisionContext MatchingContext(WebForegroundEvidence evidence, WebChallengeProof? proofOverride = null) =>
        new(CurrentRevision: evidence.EvidenceRevision, CurrentChannelId: evidence.ChannelId,
            CurrentForegroundEpoch: evidence.ForegroundEpoch, ChallengeProof: proofOverride ?? MatchingProof(evidence));

    private static void AssertOutside(
        ForegroundTargetSnapshot native, WebForegroundEvidence evidence, WebDecisionContext context, string caseLabel)
    {
        SupportedWebTarget? result = WebTargetGate.Match(native, evidence, context);
        Assert.True(result is null, caseLabel + ": expected OUTSIDE, got " + result + ".");
    }

    private static void AssertOutside(ForegroundTargetSnapshot native, WebForegroundEvidence evidence, string caseLabel) =>
        AssertOutside(native, evidence, MatchingContext(evidence), caseLabel);

    private static void AssertOutsideForOrigin(string origin, string caseLabel)
    {
        var native = ChromeSnapshot();
        var evidence = BuildEvidence(4242, channelId: 1, evidenceRevision: 1,
            BrowserFocus.Focused, OriginResolution.Resolved, origin, foregroundEpoch: 1);

        AssertOutside(native, evidence, caseLabel);
    }

    // ==================================================================
    // GROUP 1 -- five exact origin positives, both frozen browsers (Gate 031C section 1/2)
    // ==================================================================
    [Theory]
    [InlineData("chrome", ChatGptOrigin)]
    [InlineData("chrome", ClaudeOrigin)]
    [InlineData("chrome", GeminiOrigin)]
    [InlineData("chrome", GrokOrigin)]
    [InlineData("chrome", DeepSeekOrigin)]
    [InlineData("msedge", ChatGptOrigin)]
    [InlineData("msedge", ClaudeOrigin)]
    [InlineData("msedge", GeminiOrigin)]
    [InlineData("msedge", GrokOrigin)]
    [InlineData("msedge", DeepSeekOrigin)]
    public void Group1_ExactOriginOnSupportedBrowser_Authorizes(string browser, string origin)
    {
        var native = browser == "chrome" ? ChromeSnapshot() : EdgeSnapshot();
        uint pid = browser == "chrome" ? 100u : 200u;

        var evidence = BuildEvidence(pid, channelId: 1, evidenceRevision: 1,
            BrowserFocus.Focused, OriginResolution.Resolved, origin, foregroundEpoch: 1);

        SupportedWebTarget? result = WebTargetGate.Match(native, evidence, MatchingContext(evidence));

        Assert.True(result is not null, $"Group1 ({browser}, {origin}): expected WEB_AUTHORIZED, got OUTSIDE.");
    }

    // ==================================================================
    // GROUP 2 -- origin lookalike negatives (Gate 031C exact-origin semantics: no substring, no
    // EndsWith, no wildcard, no subdomain, no scheme/port/trailing-dot leniency)
    // ==================================================================
    [Theory]
    [InlineData("https://evil-chatgpt.example")]
    [InlineData("https://chatgpt.com.evil.example")]
    [InlineData("https://foo.chatgpt.com")]
    [InlineData("http://chatgpt.com")]           // wrong scheme -- HTTPS only
    [InlineData("https://chatgpt.com:444")]      // non-default port -- exact port, no leniency
    [InlineData("https://chatgpt.com.")]         // trailing dot
    [InlineData("https://xn--chatgpt-de3c.com")] // punycode/IDN lookalike shape
    [InlineData("HTTPS://CHATGPT.COM")]          // case variant -- Ordinal comparison, no case-folding
    public void Group2_OriginLookalike_MustRemainOutside(string lookalikeOrigin) =>
        AssertOutsideForOrigin(lookalikeOrigin, $"Group2 ({lookalikeOrigin})");

    // ==================================================================
    // GROUP 3 -- x.com/Grok negative (Gate 031C explicit exclusion; path is not part of the fact
    // model at all -- the origin IS x.com regardless of any conceptual /i/grok path)
    // ==================================================================
    [Fact]
    public void Group3_XDotComOrigin_NeverAuthorizesAsGrok() =>
        AssertOutsideForOrigin("https://x.com", "Group3 (x.com)");

    // ==================================================================
    // GROUP 4 -- redirect-only / marketing / API-console excluded origins (Gate 031C: policy sees
    // only the settled origin; redirects are never modeled in App policy)
    // ==================================================================
    [Theory]
    [InlineData("https://chat.openai.com")]
    [InlineData("https://chat.com")]
    [InlineData("https://bard.google.com")]
    [InlineData("https://deepseek.com")]
    [InlineData("https://www.deepseek.com")]
    [InlineData("https://platform.deepseek.com")]
    [InlineData("https://claude.com")]
    public void Group4_RedirectOrMarketingOrigin_MustRemainOutside(string excludedOrigin) =>
        AssertOutsideForOrigin(excludedOrigin, $"Group4 ({excludedOrigin})");

    // ==================================================================
    // GROUP 5 -- unsupported browser, even with an otherwise-fully-satisfying supported origin
    // (Gate 031C section 12: browser scope is explicit, not Chromium-generic)
    // ==================================================================
    [Fact]
    public void Group5_FirefoxShapedIdentity_NeverAuthorizesEvenWithSupportedOrigin()
    {
        var firefox = new ForegroundTargetSnapshot(
            IsResolved: true, ProcessId: 300, ProcessName: "firefox",
            PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
            ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Mozilla Corporation");

        var evidence = BuildEvidence(300, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, 1);

        AssertOutside(firefox, evidence, "Group5 (firefox, valid signer, supported origin)");
    }

    [Fact]
    public void Group5_UnlistedChromiumForkIdentity_NeverAuthorizesEvenWithSupportedOrigin()
    {
        var brave = new ForegroundTargetSnapshot(
            IsResolved: true, ProcessId: 301, ProcessName: "brave",
            PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
            ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Brave Software, Inc.");

        var evidence = BuildEvidence(301, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, 1);

        AssertOutside(brave, evidence, "Group5 (brave, valid signer, supported origin, Chromium-derived)");
    }

    // ==================================================================
    // GROUP 6 -- Chrome identity rule: one positive plus every individually-isolated negative axis
    // ==================================================================
    [Fact]
    public void Group6_Chrome_FullyValidIdentity_SatisfiesBrowserRule()
    {
        var native = ChromeSnapshot();
        var evidence = BuildEvidence(100, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, 1);

        SupportedWebTarget? result = WebTargetGate.Match(native, evidence, MatchingContext(evidence));

        Assert.True(result is not null,
            "Group6 (valid Chrome): expected the browser-identity half of the formula to be satisfiable.");
    }

    [Theory]
    [InlineData(false, "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, GoogleOrganization)]
    [InlineData(true, "chromium", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, GoogleOrganization)]
    [InlineData(true, "chrome", PackageIdentityResolution.Resolved, ExecutableSignatureResolution.Trusted, GoogleOrganization)]
    [InlineData(true, "chrome", PackageIdentityResolution.Unresolved, ExecutableSignatureResolution.Trusted, GoogleOrganization)]
    [InlineData(true, "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.NotInspected, GoogleOrganization)]
    [InlineData(true, "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Unresolved, GoogleOrganization)]
    [InlineData(true, "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Untrusted, GoogleOrganization)]
    [InlineData(true, "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "Alphabet Inc.")]
    [InlineData(true, "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "google llc")]
    [InlineData(true, "chrome", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, null)]
    public void Group6_Chrome_EachIsolatedNegativeAxis_RemainsOutside(
        bool isResolved, string processName, PackageIdentityResolution packageIdentity,
        ExecutableSignatureResolution signature, string? organization)
    {
        var native = ChromeSnapshot(isResolved, processName, packageIdentity, signature, organization);
        var evidence = BuildEvidence(100, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, 1);

        AssertOutside(native, evidence, $"Group6 negative ({processName}/{packageIdentity}/{signature}/{organization ?? "null"})");
    }

    // ==================================================================
    // GROUP 7 -- Edge identity rule: positive plus the same isolated-negative-axis matrix
    // ==================================================================
    [Fact]
    public void Group7_Edge_FullyValidIdentity_SatisfiesBrowserRule()
    {
        var native = EdgeSnapshot();
        var evidence = BuildEvidence(200, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, 1);

        SupportedWebTarget? result = WebTargetGate.Match(native, evidence, MatchingContext(evidence));

        Assert.True(result is not null,
            "Group7 (valid Edge): expected the browser-identity half of the formula to be satisfiable.");
    }

    [Theory]
    [InlineData(false, "msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, MicrosoftOrganization)]
    [InlineData(true, "edge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, MicrosoftOrganization)]
    [InlineData(true, "msedge", PackageIdentityResolution.Resolved, ExecutableSignatureResolution.Trusted, MicrosoftOrganization)]
    [InlineData(true, "msedge", PackageIdentityResolution.Unresolved, ExecutableSignatureResolution.Trusted, MicrosoftOrganization)]
    [InlineData(true, "msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.NotInspected, MicrosoftOrganization)]
    [InlineData(true, "msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Unresolved, MicrosoftOrganization)]
    [InlineData(true, "msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Untrusted, MicrosoftOrganization)]
    [InlineData(true, "msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "Microsoft Corp")]
    [InlineData(true, "msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "microsoft corporation")]
    [InlineData(true, "msedge", PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, null)]
    public void Group7_Edge_EachIsolatedNegativeAxis_RemainsOutside(
        bool isResolved, string processName, PackageIdentityResolution packageIdentity,
        ExecutableSignatureResolution signature, string? organization)
    {
        var native = EdgeSnapshot(isResolved, processName, packageIdentity, signature, organization);
        var evidence = BuildEvidence(200, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, 1);

        AssertOutside(native, evidence, $"Group7 negative ({processName}/{packageIdentity}/{signature}/{organization ?? "null"})");
    }

    // ==================================================================
    // GROUP 8 -- packaged Edge fail-closed negative (Gate 031C: NO fallback for an alternate
    // legitimate-looking Edge deployment identity -- a safe false negative, by design)
    // ==================================================================
    [Fact]
    public void Group8_PackagedEdgeIdentity_RemainsOutsideDespiteValidSigner()
    {
        var packagedEdge = new ForegroundTargetSnapshot(
            IsResolved: true, ProcessId: 200, ProcessName: "msedge",
            PackageIdentity: PackageIdentityResolution.Resolved,
            PackageFamilyName: "Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe",
            ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: MicrosoftOrganization);

        var evidence = BuildEvidence(200, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, 1);

        AssertOutside(packagedEdge, evidence, "Group8 (packaged Edge, valid Microsoft signer, legitimate-looking PFN)");
    }

    // ==================================================================
    // GROUP 9 -- background tab (BrowserFocus != Focused), otherwise fully valid
    // ==================================================================
    [Theory]
    [InlineData(BrowserFocus.Unresolved)]
    [InlineData(BrowserFocus.NotFocused)]
    public void Group9_BrowserNotFocused_RemainsOutside(BrowserFocus focusState)
    {
        var native = ChromeSnapshot();
        var evidence = BuildEvidence(100, 1, 1, focusState, OriginResolution.Resolved, ChatGptOrigin, 1);

        AssertOutside(native, evidence, $"Group9 (BrowserFocus={focusState})");
    }

    // ==================================================================
    // GROUP 10 -- non-foreground browser window: the OS foreground is a DIFFERENT application
    // entirely while the browser evidence claims a fully valid, focused, supported tab (Gate 031C
    // section 6: "browser process equality alone NEVER authorizes" -- proven here from the other
    // direction: browser evidence alone, with no matching native foreground, never authorizes
    // either)
    // ==================================================================
    [Fact]
    public void Group10_NativeForegroundIsUnrelatedApplication_BrowserEvidenceAloneNeverAuthorizes()
    {
        var notepadForeground = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 555, ProcessName: "notepad");
        var evidence = BuildEvidence(100, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, 1);

        AssertOutside(notepadForeground, evidence, "Group10 (native foreground is Notepad, not any browser)");
    }

    // ==================================================================
    // GROUP 11 -- PID/channel binding negatives
    // ==================================================================
    [Fact]
    public void Group11_BrowserProcessIdMismatch_RemainsOutside()
    {
        var native = ChromeSnapshot(); // real foreground PID 100
        var evidence = BuildEvidence(999, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, 1);

        AssertOutside(native, evidence, "Group11 (evidence.BrowserProcessId=999 != native.ProcessId=100)");
    }

    [Fact]
    public void Group11_SamePidStaleChannel_RemainsOutside()
    {
        // Same BrowserProcessId as the live foreground Chrome, but the DECISION CONTEXT's own
        // "currently live" ChannelId does not match this evidence's ChannelId -- documents Gate
        // 031C's requirement that a stale/superseded channel identity on a REUSED PID must not be
        // treated as current. WebDecisionContext.CurrentChannelId is what a real live-channel
        // registry would report; Gate 031E1 implements no such registry, so this test supplies it
        // explicitly, exactly as Gate 031C section 10 permits.
        var native = ChromeSnapshot();
        var evidence = BuildEvidence(browserProcessId: 100, channelId: 5, evidenceRevision: 1,
            BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, foregroundEpoch: 1);
        var context = new WebDecisionContext(
            CurrentRevision: evidence.EvidenceRevision, CurrentChannelId: 999999 /* not this evidence's channel */,
            CurrentForegroundEpoch: evidence.ForegroundEpoch, ChallengeProof: MatchingProof(evidence));

        AssertOutside(native, evidence, context, "Group11 (same PID, ChannelId does not match the live channel)");
    }

    // ==================================================================
    // GROUP 12 -- stale revision (Gate 031C: no grace period, no timestamp fallback)
    // ==================================================================
    [Fact]
    public void Group12_SupersededEvidenceRevision_RemainsOutside()
    {
        var native = ChromeSnapshot();
        var staleEvidence = BuildEvidence(100, 1, evidenceRevision: 5, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, 1);
        var context = new WebDecisionContext(
            CurrentRevision: new RevisionId(6) /* newer than the evidence's own revision 5 */,
            CurrentChannelId: staleEvidence.ChannelId, CurrentForegroundEpoch: staleEvidence.ForegroundEpoch,
            ChallengeProof: MatchingProof(staleEvidence));

        AssertOutside(native, staleEvidence, context, "Group12 (revision 5 superseded by revision 6, no grace period)");
    }

    // ==================================================================
    // GROUP 13 -- foreground epoch (Gate 031C load-bearing addition; required for guarded-transport
    // CHECK1/CHECK2 re-verification, mirroring ForegroundIdentityCapture.Matches' own role for the
    // Windows path)
    // ==================================================================
    [Fact]
    public void Group13_ForegroundEpochField_ExistsOnWebForegroundEvidence()
    {
        var evidence = BuildEvidence(1, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, foregroundEpoch: 42);

        Assert.Equal(42, evidence.ForegroundEpoch);
    }

    [Fact]
    public void Group13_SupersededForegroundEpoch_RemainsOutside()
    {
        // Pure comparison semantics (Gate 031E1 section 19) -- proves the FACT/POLICY side of the
        // epoch race. The guarded-transport CHECK1/CHECK2 re-verification this fact is FOR (actually
        // re-reading the native foreground epoch immediately before real clipboard I/O) is a
        // separate, still-EXPECTED_DEFERRED_RED integration concern -- no ClipboardTransport code is
        // exercised by this test.
        var native = ChromeSnapshot();
        var evidenceAtOldEpoch = BuildEvidence(100, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, ChatGptOrigin, foregroundEpoch: 7);
        var context = new WebDecisionContext(
            CurrentRevision: evidenceAtOldEpoch.EvidenceRevision, CurrentChannelId: evidenceAtOldEpoch.ChannelId,
            CurrentForegroundEpoch: 8 /* one past the evidence's own epoch */, ChallengeProof: MatchingProof(evidenceAtOldEpoch));

        AssertOutside(native, evidenceAtOldEpoch, context, "Group13 (evidence epoch=7 superseded by current epoch=8)");
    }

    // ==================================================================
    // GROUP 14 / GROUP 15 -- REMOVED this gate (Gate 031D2). Group14's absence-confirmation and
    // Group15's unconditional Assert.Fail were both identified by Gate 031E2's own audit as
    // insufficient final RED evidence (an absence-confirmation that merely PASSES today proves
    // nothing about ordering; an unconditional failure carries no scenario). Both are replaced by
    // real, scenario-by-scenario behavioral contract coverage in
    // Gate031D2_WebMechanicsRedTests.cs -- R5 (INVALIDATE_BEFORE_ASSERT_RED, 9 named lifecycle
    // events) and R2 (CHANNEL_LIFECYCLE_RED, 11 named cases A-K) respectively. See that file for
    // the frozen channel-registry/state-machine semantics (Gate 031E2 sections C/D) both groups
    // now test against.
    // ==================================================================

    // ==================================================================
    // GROUP 16 -- invalid fact-model combinations (Gate 031E1: pure policy, not a real protocol
    // parser -- a genuine malformed-wire-message RED suite remains a future mechanics-gate item;
    // see the FINAL REPORT).
    // ==================================================================
    [Fact]
    public void Group16_ResolvedOriginResolutionWithNullOrigin_IsAnInvalidCombination()
    {
        // OriginResolution.Resolved with a null Origin is a malformed combination under the frozen
        // fact model ("Origin: string? -- only meaningful when Resolved").
        var evidence = BuildEvidence(100, 1, 1, BrowserFocus.Focused, OriginResolution.Resolved, origin: null, foregroundEpoch: 1);

        AssertOutside(ChromeSnapshot(), evidence, "Group16 (OriginResolution=Resolved but Origin=null)");
    }

    [Fact]
    public void Group16_UnsupportedOriginResolution_NeverAuthorizes()
    {
        var evidence = BuildEvidence(100, 1, 1, BrowserFocus.Focused, OriginResolution.Unsupported, origin: null, foregroundEpoch: 1);

        AssertOutside(ChromeSnapshot(), evidence, "Group16 (OriginResolution=Unsupported)");
    }

    // ==================================================================
    // GROUP 17 -- default(WebForegroundEvidence) must be safely OUTSIDE
    // ==================================================================
    [Fact]
    public void Group17_DefaultConstructedEvidence_IsSafelyOutside()
    {
        var defaultEvidence = default(WebForegroundEvidence);

        AssertOutside(ChromeSnapshot(), defaultEvidence, "Group17 (default(WebForegroundEvidence))");
    }

    // ==================================================================
    // GROUP 18 -- privacy structural tests
    // ==================================================================
    [Fact]
    public void Group18_PrivonBrowserAssembly_ExistsAsFactsOnlyBoundary()
    {
        Assert.Equal("Privon.Browser", BrowserAssembly.GetName().Name);
    }

    [Fact]
    public void Group18_WebForegroundEvidence_CannotExpressPageContentOrHistory()
    {
        var type = typeof(WebForegroundEvidence);
        var forbidden = new[] { "Path", "Query", "Fragment", "Content", "Title", "Favicon", "Cookie", "Token", "Account", "History", "Keystroke" };
        var offending = type.GetProperties().Select(p => p.Name)
            .Concat(type.GetFields().Select(f => f.Name))
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void Group18_PrivonBrowserAssembly_ContainsNoProductPolicyStrings()
    {
        var forbidden = new[]
        {
            "ChatGPT", "Claude", "Gemini", "Grok", "DeepSeek",
            "chatgpt.com", "claude.ai", "gemini.google.com", "grok.com", "chat.deepseek.com",
            "Google LLC", "Microsoft Corporation", "chrome", "msedge",
        };

        var offending = BrowserAssembly.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offending);
    }

    // ---- GREEN trip-wire (not RED): today's ClipboardDiagnosticEvent must remain free of any
    // Web-origin string field. This must stay GREEN through and after implementation -- if Web
    // support ever needs a diagnostic fact, Gate 031C requires it be an enum, never a URL/origin
    // string, preserving the type's existing two-string privacy boundary
    // (TargetProcessName/ExceptionTypeName). ----
    [Fact]
    public void Group18_ClipboardDiagnosticEvent_GainsNoOriginOrUrlStringField()
    {
        var type = typeof(ClipboardDiagnosticEvent);
        var forbidden = new[] { "Origin", "Url", "Uri", "WebSite", "Domain", "Host" };

        var offending = type.GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offending);
    }

    // ==================================================================
    // GROUP 19 -- Web composer UIA exclusion (Gate 031C B2). Gate 031E1 deliberately did NOT touch
    // ClipboardPrivacyCoordinator (section 20 of that gate's own instructions) -- this test only
    // proves the PREREQUISITE (WebTargetGate exists and is structurally distinct from TargetGate),
    // which is now true. It does NOT verify the actual behavioral exclusion at
    // ClipboardPrivacyCoordinator's own _verificationHandoff.Publish call site -- that remains
    // EXPECTED_DEFERRED_RED / unverified until a dedicated integration gate adds the real check and
    // a real behavioral test for it. See this gate's own FINAL REPORT.
    // ==================================================================
    [Fact]
    public void Group19_WebTargetGate_ExistsAndIsDisjointFromTargetGate()
    {
        var webGateType = AppAssembly.GetTypes().FirstOrDefault(t => t.Name == "WebTargetGate");

        Assert.True(webGateType is not null, "Group19: WebTargetGate must exist in Privon.App.");
        Assert.NotEqual(typeof(TargetGate), webGateType);
    }

    // ==================================================================
    // GROUP 20 -- Windows regression lock. Deliberately NOT duplicated here: TargetGateTests.cs and
    // Gate0D_TargetGateAndClaudeRuleTests.cs already lock SupportedTarget to exactly
    // {ChatGpt, Claude} and the full ChatGPT PFN / Claude Authenticode / S_OK-only-trust matrix.
    // Those suites are re-run unmodified as part of this gate's pre-existing regression pass (see
    // the Gate 031E1 FINAL REPORT) rather than re-asserted here.
    // ==================================================================
}
