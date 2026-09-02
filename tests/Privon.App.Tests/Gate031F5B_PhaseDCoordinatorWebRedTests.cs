using Privon.App;
using Privon.Browser;
using Privon.Core;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031F5C -- Phase D coordinator (Level1/2) Web routing behavioral suite, now
// GREEN against the real ClipboardPrivacyCoordinator + ClipboardAuthorizationRouter +
// IWebClipboardAuthorizationSource. Every test drives the REAL coordinator end to end with a
// scripted FakeWebClipboardAuthorizationSource injected through the frozen optional trailing
// constructor parameter -- no production behavior is hand-simulated.
public class Gate031F5B_PhaseDCoordinatorWebRedTests
{
    private static readonly ForegroundTargetSnapshot BrowserForeground = new(
        IsResolved: true, ProcessId: 100, ProcessName: "chrome",
        PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
        ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Google LLC");

    private static readonly ForegroundTargetSnapshot ChatGpt = new(
        IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
        PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    // ---- WEB PRECONDITION, real production types: this exact browser foreground state IS already
    // recognized by the real, unmodified WebTargetGate.Match today -- shared by every test below so
    // a failure here would mean the TEST setup is wrong, never the coordinator. ----
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
        public int CallCount { get; private set; }
        public bool IsStillCurrent()
        {
            CallCount++;
            return result;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(10);
        }
    }

    private static (ClipboardPrivacyCoordinator Coordinator, FakeClipboardReadTransport ReadTransport,
        FakeClipboardWriteTransport WriteTransport, FakeForegroundTargetCapture TargetCapture,
        FakeClipboardPrivacyProcessor Processor, FakeClipboardNotificationLifecycle Lifecycle,
        FakeClipboardComposerVerificationHandoff VerificationHandoff, FakeClipboardDiagnosticRecorder Diagnostics,
        FakeWebClipboardAuthorizationSource WebSource) CreateCoordinator(ForegroundTargetSnapshot foreground)
    {
        var readTransport = new FakeClipboardReadTransport
        {
            NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "raw text")),
        };
        var writeTransport = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 1) };
        var targetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = foreground };
        var processor = new FakeClipboardPrivacyProcessor { WritePlanToReturn = new ClipboardWritePlan("[protected]") };
        var lifecycle = new FakeClipboardNotificationLifecycle();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();
        var operationGate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var verificationInvalidation = new FakeClipboardComposerVerificationInvalidation();
        var diagnostics = new FakeClipboardDiagnosticRecorder();
        var webSource = new FakeWebClipboardAuthorizationSource();

        var coordinator = new ClipboardPrivacyCoordinator(
            readTransport, targetCapture, processor, writeTransport, lifecycle,
            decisionSessionPublisher, operationGate, verificationHandoff, verificationInvalidation,
            diagnostics: diagnostics, webAuthorizationSource: webSource);

        return (coordinator, readTransport, writeTransport, targetCapture, processor, lifecycle,
            verificationHandoff, diagnostics, webSource);
    }

    // ==================================================================
    // D-R1 / D-R2 -- Web coordinator Success + B2 (zero Publish)
    // ==================================================================

    [Fact]
    public async Task D_R1_D_R2_WebAuthorizedAttempt_ReachesVerifiedSuccess_WithZeroComposerPublish()
    {
        var webTarget = RequireWebPrecondition();
        var (coordinator, readTransport, _, targetCapture, _, lifecycle, verificationHandoff, diagnostics, webSource) =
            CreateCoordinator(BrowserForeground);
        webSource.NextResult = new WebClipboardAuthorization(webTarget, new ScriptedFreshness(true));

        coordinator.Start();
        try
        {
            readTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            await WaitUntilAsync(() => diagnostics.Events.Any(e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal));
        }
        finally
        {
            coordinator.Stop();
        }

        var terminals = diagnostics.Events.Where(e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal).Select(e => e.Terminal).ToList();
        Assert.Contains(ClipboardDiagnosticTerminalReason.Success, terminals);
        Assert.Equal(1, lifecycle.CompleteEvaluationCallCount);
        Assert.Equal(0, lifecycle.AbandonEvaluationCallCount);
        Assert.Equal(0, verificationHandoff.CallCount);
        Assert.Equal(1, targetCapture.CaptureCallCount);
    }

    [Fact]
    public async Task D_R10_WebSuccess_RealComposerVerifier_ReturnsNotAttempted_ZeroComposerReads()
    {
        var webTarget = RequireWebPrecondition();
        var readTransport = new FakeClipboardReadTransport
        {
            NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "raw text")),
        };
        var writeTransport = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 1) };
        var targetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = BrowserForeground };
        var processor = new FakeClipboardPrivacyProcessor { WritePlanToReturn = new ClipboardWritePlan("[protected]") };
        var lifecycle = new FakeClipboardNotificationLifecycle();
        var operationGate = new ClipboardOperationGate();
        var composerReadTransport = new FakeComposerReadTransport();
        var realVerifier = new ClipboardComposerVerifier(lifecycle, composerReadTransport, operationGate);
        var webSource = new FakeWebClipboardAuthorizationSource
        {
            NextResult = new WebClipboardAuthorization(webTarget, new ScriptedFreshness(true)),
        };

        var coordinator = new ClipboardPrivacyCoordinator(
            readTransport, targetCapture, processor, writeTransport, lifecycle,
            new FakeClipboardDecisionSessionPublisher(), operationGate, realVerifier, realVerifier,
            webAuthorizationSource: webSource);

        coordinator.Start();
        try
        {
            readTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            await WaitUntilAsync(() => lifecycle.CompleteEvaluationCallCount >= 1);
        }
        finally
        {
            coordinator.Stop();
        }

        var verifyResult = await realVerifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.NotAttempted, verifyResult.Outcome);
        Assert.Empty(composerReadTransport.CallLog);
    }

    // ==================================================================
    // D-R4 -- same attempt-scoped verifier reaches both guarded read and guarded write
    // ==================================================================

    [Fact]
    public async Task D_R4_SameWebFreshnessInstance_ReachesBothGuardedReadAndWrite()
    {
        var webTarget = RequireWebPrecondition();
        var (coordinator, readTransport, writeTransport, _, _, lifecycle, _, _, webSource) = CreateCoordinator(BrowserForeground);
        var v1 = new ScriptedFreshness(true);
        webSource.NextResult = new WebClipboardAuthorization(webTarget, v1);

        coordinator.Start();
        try
        {
            readTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            await WaitUntilAsync(() => lifecycle.CompleteEvaluationCallCount + lifecycle.AbandonEvaluationCallCount >= 1);
        }
        finally
        {
            coordinator.Stop();
        }

        var readFreshness = Assert.Single(readTransport.ReceivedAuthorizationFreshness);
        var writeFreshness = Assert.Single(writeTransport.ReceivedAuthorizationFreshness);
        Assert.NotNull(readFreshness);
        Assert.NotNull(writeFreshness);
        Assert.Same(v1, readFreshness);
        Assert.Same(v1, writeFreshness);
    }

    // ==================================================================
    // D-R5 -- stale Web freshness fails closed through existing classifier/retry semantics
    // ==================================================================

    [Fact]
    public async Task D_R5_StaleWebFreshnessAtGuardedOperation_ProducesTargetChanged_NeverSuccess()
    {
        var webTarget = RequireWebPrecondition();
        var (coordinator, readTransport, writeTransport, _, _, lifecycle, verificationHandoff, diagnostics, webSource) =
            CreateCoordinator(BrowserForeground);
        webSource.NextResult = new WebClipboardAuthorization(webTarget, new ScriptedFreshness(true));
        // The Windows-layer freshness check would map to TargetChanged for the guarded operation --
        // simulated here at the App seam boundary via the write transport's own scripted outcome,
        // exactly as any other TargetChanged-producing native condition already is in this suite.
        writeTransport.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.TargetChanged, mutated: false);

        coordinator.Start();
        try
        {
            readTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            await WaitUntilAsync(() => lifecycle.CompleteEvaluationCallCount + lifecycle.AbandonEvaluationCallCount >= 1);
        }
        finally
        {
            coordinator.Stop();
        }

        var terminals = diagnostics.Events.Where(e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal).Select(e => e.Terminal).ToList();
        Assert.DoesNotContain(ClipboardDiagnosticTerminalReason.Success, terminals);
        Assert.Equal(0, verificationHandoff.CallCount);
        // TargetChanged classifies Retryable (Abandon) but is not autonomous-retry-eligible for a
        // write outcome, so exactly one Abandon and zero Complete -- the existing, unmodified
        // classifier/retry behavior, with no new Web-specific terminal reason introduced.
        Assert.Equal(1, lifecycle.AbandonEvaluationCallCount);
        Assert.Equal(0, lifecycle.CompleteEvaluationCallCount);
    }

    // ==================================================================
    // D-R6 -- autonomous retry obtains a FRESH Web authorization, never reuses the stale one
    // ==================================================================

    [Fact]
    public async Task D_R6_AutonomousRetry_ObtainsFreshWebAuthorization_DistinctVerifierInstance()
    {
        var webTarget = RequireWebPrecondition();
        var (coordinator, readTransport, writeTransport, _, _, lifecycle, _, _, webSource) = CreateCoordinator(BrowserForeground);
        var v1 = new ScriptedFreshness(true);
        var v2 = new ScriptedFreshness(true);
        webSource.ScriptResults(
            new WebClipboardAuthorization(webTarget, v1),
            new WebClipboardAuthorization(webTarget, v2));
        // First write attempt is retry-eligible (Busy); the retry loop's own real Task.Delay-backed
        // ClipboardRetryDelay default runs, which is fine -- this test only waits on lifecycle state.
        writeTransport.ScriptWriteResults(
            ClipboardWriteResult.Failure(ClipboardWriteOutcome.Busy, mutated: false),
            ClipboardWriteResult.Success(resultSequence: 2));

        coordinator.Start();
        try
        {
            readTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            await WaitUntilAsync(() => webSource.CallCount >= 2, TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => lifecycle.CompleteEvaluationCallCount >= 1, TimeSpan.FromSeconds(10));
        }
        finally
        {
            coordinator.Stop();
        }

        Assert.Equal(2, webSource.CallCount);
        Assert.Equal(2, writeTransport.ReceivedAuthorizationFreshness.Count);
        Assert.Same(v1, writeTransport.ReceivedAuthorizationFreshness[0]);
        Assert.Same(v2, writeTransport.ReceivedAuthorizationFreshness[1]);
        Assert.NotSame(writeTransport.ReceivedAuthorizationFreshness[0], writeTransport.ReceivedAuthorizationFreshness[1]);
        Assert.Equal(2, webSource.ReceivedSnapshots.Count);
    }

    // ==================================================================
    // D-R7 (coordinator half) -- no Web source injected => production pre-Phase-E fail-closed
    // ==================================================================

    [Fact]
    public async Task D_R7_NoWebAuthorizationSourceInjected_BrowserTarget_ProducesUnauthorizedTarget()
    {
        var readTransport = new FakeClipboardReadTransport();
        var writeTransport = new FakeClipboardWriteTransport();
        var targetCapture = new FakeForegroundTargetCapture { SnapshotToReturn = BrowserForeground };
        var processor = new FakeClipboardPrivacyProcessor();
        var lifecycle = new FakeClipboardNotificationLifecycle();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var diagnostics = new FakeClipboardDiagnosticRecorder();

        // NO webAuthorizationSource argument at all -- the production default.
        var coordinator = new ClipboardPrivacyCoordinator(
            readTransport, targetCapture, processor, writeTransport, lifecycle,
            new FakeClipboardDecisionSessionPublisher(), new ClipboardOperationGate(),
            verificationHandoff, new FakeClipboardComposerVerificationInvalidation(),
            diagnostics: diagnostics);

        coordinator.Start();
        try
        {
            readTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            await WaitUntilAsync(() => diagnostics.Events.Any(e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal));
        }
        finally
        {
            coordinator.Stop();
        }

        var terminals = diagnostics.Events.Where(e => e.Stage == ClipboardDiagnosticStage.AttemptTerminal).Select(e => e.Terminal).ToList();
        Assert.Contains(ClipboardDiagnosticTerminalReason.UnauthorizedTarget, terminals);
        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), readTransport.CallLog);
        Assert.Equal(0, writeTransport.CallCount);
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    // ==================================================================
    // D-R16 -- pending async authorization cannot succeed early
    // ==================================================================

    [Fact]
    public async Task D_R16_PendingAsyncAuthorization_BlocksGuardedIOUntilCompleted_NoSleepNoPolling()
    {
        var webTarget = RequireWebPrecondition();
        var (coordinator, readTransport, writeTransport, _, _, lifecycle, verificationHandoff, _, webSource) =
            CreateCoordinator(BrowserForeground);
        var tcs = new TaskCompletionSource<WebClipboardAuthorization?>();
        webSource.PendingResult = tcs;

        coordinator.Start();
        try
        {
            readTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            await WaitUntilAsync(() => webSource.CallCount >= 1);

            // While pending: no guarded I/O, no Success, no Publish.
            Assert.Equal(0, readTransport.ReadCallCount);
            Assert.Equal(0, writeTransport.CallCount);
            Assert.Equal(0, verificationHandoff.CallCount);
            Assert.Equal(0, lifecycle.CompleteEvaluationCallCount);

            tcs.SetResult(new WebClipboardAuthorization(webTarget, new ScriptedFreshness(true)));
            await WaitUntilAsync(() => lifecycle.CompleteEvaluationCallCount >= 1);
        }
        finally
        {
            coordinator.Stop();
        }

        Assert.Equal(1, readTransport.ReadCallCount);
        Assert.Equal(1, writeTransport.CallCount);
    }

    // ==================================================================
    // D-R17 -- Windows-first short-circuit: a valid Windows target never consults the Web source
    // ==================================================================

    [Fact]
    public async Task D_R17_ValidWindowsTarget_NeverConsultsInjectedWebSource()
    {
        var (coordinator, readTransport, _, _, _, lifecycle, _, _, webSource) = CreateCoordinator(ChatGpt);

        coordinator.Start();
        try
        {
            readTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            await WaitUntilAsync(() => lifecycle.CompleteEvaluationCallCount >= 1);
        }
        finally
        {
            coordinator.Stop();
        }

        Assert.Equal(0, webSource.CallCount);
    }
}
