using Privon.App;
using Privon.Browser;
using Privon.Core;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031F5C -- Phase D Level3 (ClipboardDecisionActionResolver) Web routing
// behavioral suite, now GREEN against the real resolver + ClipboardAuthorizationRouter +
// IWebClipboardAuthorizationSource, using the REAL ClipboardPrivacyProcessor/DetectionPipeline and
// REAL ClipboardDecisionScope -- matching the discipline the existing
// ClipboardDecisionActionResolverTests.cs already establishes.
public class Gate031F5B_PhaseDResolverWebRedTests
{
    private static readonly ForegroundTargetSnapshot BrowserForeground = new(
        IsResolved: true, ProcessId: 100, ProcessName: "chrome",
        PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
        ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Google LLC");

    private const string Level3Text = "901231-1234567"; // synthetic RRN -> Level3, always NeedsDecision

    private static SupportedWebTarget RequireWebPrecondition()
    {
        var evidence = new WebForegroundEvidence(
            BrowserProcessId: 100, ChannelId: 1, EvidenceRevision: new RevisionId(1),
            BrowserFocus: BrowserFocus.Focused, OriginResolution: OriginResolution.Resolved,
            Origin: "https://chatgpt.com", ForegroundEpoch: 1);
        var proof = new WebChallengeProof(evidence.ChannelId, evidence.EvidenceRevision, evidence.ForegroundEpoch, evidence.BrowserProcessId);
        var context = new WebDecisionContext(
            CurrentRevision: evidence.EvidenceRevision, CurrentChannelId: evidence.ChannelId,
            CurrentForegroundEpoch: evidence.ForegroundEpoch, ChallengeProof: proof);

        var webMatch = WebTargetGate.Match(BrowserForeground, evidence, context);
        Assert.True(webMatch is not null, "Phase D precondition failed: this exact browser " +
            "foreground triple must already be recognized by the real, current WebTargetGate.Match.");
        return webMatch!.Value;
    }

    private sealed class ScriptedFreshness(bool result) : IClipboardAuthorizationFreshness
    {
        public bool IsStillCurrent() => result;
    }

    private static Privon.Detection.CanonicalValue DiscoverCanonical(string rawText, Privon.Detection.PiiType expectedType, Privon.Core.RiskLevel expectedLevel)
    {
        var probe = Privon.Detection.DetectionPipeline.CreateDefault().Detect(rawText);
        var candidate = Assert.Single(probe.Candidates);
        Assert.Equal(expectedType, candidate.PiiType);
        Assert.Equal(expectedLevel, candidate.RiskLevel);
        return candidate.Canonical;
    }

    private static ClipboardDecisionItem MakeLevel3Item(string text = Level3Text) =>
        new(DiscoverCanonical(text, Privon.Detection.PiiType.ResidentRegistrationNumber, Privon.Core.RiskLevel.Level3), Privon.Core.RiskLevel.Level3);

    private static ClipboardDecisionScope CreateScope(string sourceText, long generation, params ClipboardDecisionItem[] items)
    {
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe(sourceText);
        return new ClipboardDecisionScope(tracker, stamp, items, generation);
    }

    // ==================================================================
    // D-R11 / D-R12 -- Web Level3 Protect-All success + B2 (zero Publish, composer unreachable)
    // ==================================================================

    [Fact]
    public async Task D_R11_D_R12_WebLevel3ProtectAll_ReachesAppliedAndCommitted_WithZeroComposerPublish()
    {
        var webTarget = RequireWebPrecondition();
        var lifecycle = new FakeClipboardDecisionScopeLifecycle();
        var targetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = BrowserForeground };
        var readTransport = new FakeClipboardReadTransport
        {
            NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, Level3Text)),
        };
        var writeTransport = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 2) };
        var gate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());
        var webSource = new FakeWebClipboardAuthorizationSource
        {
            NextResult = new WebClipboardAuthorization(webTarget, new ScriptedFreshness(true)),
        };

        var resolver = new ClipboardDecisionActionResolver(
            gate, lifecycle, targetCapture, readTransport, writeTransport, processor, verificationHandoff,
            webAuthorizationSource: webSource);

        var item = MakeLevel3Item();
        var scope = CreateScope(Level3Text, generation: 1, item);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.True(result.ScopeCommitted);
        Assert.True(scope.Committed);
        Assert.Equal(1, webSource.CallCount);
        Assert.Equal(BrowserForeground, Assert.Single(webSource.ReceivedSnapshots));
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    [Fact]
    public async Task D_R12_WebLevel3Success_RealComposerVerifier_ReturnsNotAttempted()
    {
        var webTarget = RequireWebPrecondition();
        var lifecycle = new FakeClipboardDecisionScopeLifecycle();
        var targetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = BrowserForeground };
        var readTransport = new FakeClipboardReadTransport
        {
            NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, Level3Text)),
        };
        var writeTransport = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 2) };
        var gate = new ClipboardOperationGate();
        var composerReadTransport = new FakeComposerReadTransport();
        var realVerifier = new ClipboardComposerVerifier(new FakeClipboardGenerationSnapshot(), composerReadTransport, gate);
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());
        var webSource = new FakeWebClipboardAuthorizationSource
        {
            NextResult = new WebClipboardAuthorization(webTarget, new ScriptedFreshness(true)),
        };

        var resolver = new ClipboardDecisionActionResolver(
            gate, lifecycle, targetCapture, readTransport, writeTransport, processor, realVerifier,
            webAuthorizationSource: webSource);

        var item = MakeLevel3Item();
        var scope = CreateScope(Level3Text, generation: 1, item);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);

        var verifyResult = await realVerifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.NotAttempted, verifyResult.Outcome);
        Assert.Empty(composerReadTransport.CallLog);
    }

    // ==================================================================
    // D-R14 -- human-decision delay: resolver authorization is a NEW moment, never reusing the
    // pre-decision coordinator attempt's verifier. Load-bearing.
    // ==================================================================

    [Fact]
    public async Task D_R14_ResolverAuthorization_IsNewMoment_NeverReusesPreDecisionCoordinatorVerifier()
    {
        var webTarget = RequireWebPrecondition();
        var sharedWebSource = new FakeWebClipboardAuthorizationSource();
        var v1 = new ScriptedFreshness(true);
        var v2 = new ScriptedFreshness(true);
        sharedWebSource.ScriptResults(
            new WebClipboardAuthorization(webTarget, v1),
            new WebClipboardAuthorization(webTarget, v2));

        // ---- Coordinator attempt (Level3, NeedsDecision -> DecisionPending; ends without ever
        // touching a resolver) authorizes via the shared source, consuming v1. ----
        var coordReadTransport = new FakeClipboardReadTransport
        {
            NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, Level3Text)),
        };
        var coordTargetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = BrowserForeground };
        var coordLifecycle = new FakeClipboardNotificationLifecycle();
        var realProcessor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();

        var coordinator = new ClipboardPrivacyCoordinator(
            coordReadTransport, coordTargetCapture, realProcessor, new FakeClipboardWriteTransport(),
            coordLifecycle, decisionSessionPublisher, new ClipboardOperationGate(),
            new FakeClipboardComposerVerificationHandoff(), new FakeClipboardComposerVerificationInvalidation(),
            webAuthorizationSource: sharedWebSource);

        coordinator.Start();
        try
        {
            coordReadTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (sharedWebSource.CallCount < 1)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Coordinator never authorized.");
                await Task.Delay(10);
            }
        }
        finally
        {
            coordinator.Stop();
        }

        Assert.Equal(1, sharedWebSource.CallCount);

        // ---- Later, an independent resolver sharing the SAME Web source authorizes AGAIN, at its
        // own fresh-capture moment, for the human's Protect click. ----
        var resolverLifecycle = new FakeClipboardDecisionScopeLifecycle();
        var resolverTargetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = BrowserForeground };
        var resolverReadTransport = new FakeClipboardReadTransport
        {
            NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, Level3Text)),
        };
        var resolverWriteTransport = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 2) };
        var resolver = new ClipboardDecisionActionResolver(
            new ClipboardOperationGate(), resolverLifecycle, resolverTargetCapture, resolverReadTransport,
            resolverWriteTransport, new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider()),
            new FakeClipboardComposerVerificationHandoff(), webAuthorizationSource: sharedWebSource);

        var item = MakeLevel3Item();
        var scope = CreateScope(Level3Text, generation: 1, item);
        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.Equal(2, sharedWebSource.CallCount);
        Assert.Equal(v2, Assert.Single(resolverWriteTransport.ReceivedAuthorizationFreshness));
        Assert.NotSame(v1, resolverWriteTransport.ReceivedAuthorizationFreshness[0]);
    }

    // ==================================================================
    // D-R7 (resolver half) / D-R15 (resolver half) -- no source / malformed source, fail closed
    // ==================================================================

    [Fact]
    public async Task D_R7_ResolverWithNoWebAuthorizationSource_BrowserTarget_ProducesStale()
    {
        var lifecycle = new FakeClipboardDecisionScopeLifecycle();
        var targetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = BrowserForeground };
        var readTransport = new FakeClipboardReadTransport();
        var writeTransport = new FakeClipboardWriteTransport();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        // NO webAuthorizationSource argument at all -- the production default.
        var resolver = new ClipboardDecisionActionResolver(
            new ClipboardOperationGate(), lifecycle, targetCapture, readTransport, writeTransport,
            processor, verificationHandoff);

        var item = MakeLevel3Item();
        var scope = CreateScope(Level3Text, generation: 1, item);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), readTransport.CallLog);
        Assert.Equal(0, writeTransport.CallCount);
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    [Fact]
    public async Task D_R15_ResolverWithMalformedWebSourceResult_ProducesStale_NoClipboardIONoException()
    {
        var lifecycle = new FakeClipboardDecisionScopeLifecycle();
        var targetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = BrowserForeground };
        var readTransport = new FakeClipboardReadTransport();
        var writeTransport = new FakeClipboardWriteTransport();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());
        var webSource = new FakeWebClipboardAuthorizationSource { NextResult = null };

        var resolver = new ClipboardDecisionActionResolver(
            new ClipboardOperationGate(), lifecycle, targetCapture, readTransport, writeTransport,
            processor, verificationHandoff, webAuthorizationSource: webSource);

        var item = MakeLevel3Item();
        var scope = CreateScope(Level3Text, generation: 1, item);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), readTransport.CallLog);
        Assert.Equal(0, writeTransport.CallCount);
    }
}
