using System.Diagnostics;
using System.Reflection;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3B STEP26 -- ClipboardDecisionActionResolver regression: decision-time revalidation
// (Phase 3B STEP22 audit's frozen contract), per-item runtime choice application, Level3 two-step
// confirmation, and the final overlay/write/commit/grant sequence (Phase 3B STEP24/STEP24.1
// audits' frozen model). Uses the REAL ClipboardPrivacyProcessor -- backed by the REAL
// DetectionPipeline.CreateDefault() and REAL ExceptionTrustedEvaluator/CandidatePolicyEvaluator/
// AliasAssigner/AliasReplacer -- and the REAL ClipboardDecisionScope/ClipboardOperationGate; only
// the trust/exception PROVIDER and the lifecycle/target/transport seams are hand-written fakes,
// matching the same "prove the real integration boundary, not a fabricated stand-in" discipline
// already established for ClipboardPrivacyProcessorTests.cs.
public class ClipboardDecisionActionResolverTests
{
    // BUG-004 Gate 2G: migrated to carry the approved current-product package identity, and to
    // exactly match FakeForegroundTargetCapture's own (likewise migrated) default -- this constant
    // represents "the officially supported target" and is asserted, at line ~808, against whatever
    // FakeForegroundTargetCapture.Capture() actually returned; NotChatGpt below is a negative
    // fixture and is intentionally left untouched.
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");
    private static readonly ForegroundTargetSnapshot NotChatGpt = new(IsResolved: true, ProcessId: 9999, ProcessName: "notepad");

    private const string Level1Text = "37.5665,126.9780"; // GPS, order-unambiguous bare pair -> Level1/Medium
    private const string Level3Text = "901231-1234567"; // synthetic RRN -> Level3, always NeedsDecision
    private const string ProtectPhoneText = "연락처 010-1234-5678"; // Level2 phone -> base Protect
    private const string NoPiiText = "오늘 날씨가 좋네요";
    private const string DifferentRawText = "완전히 다른 텍스트입니다";

    // A deliberately-throwing real IDetector -- the smallest seam that proves ERROR_BOUNDARY for
    // an unexpected Detection-layer failure, matching ClipboardPrivacyProcessorTests's own
    // ThrowingDetector precedent -- only Privon.Detection's own public IDetector is needed.
    private sealed class ThrowingDetector : IDetector
    {
        public string Name => "ThrowingDetector";
        public PiiType PiiType => PiiType.Secret;
        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) =>
            throw new InvalidOperationException("synthetic detector failure for ClipboardDecisionActionResolver test");
    }

    private static ClipboardTextSnapshot Snapshot(string text, uint sequence = 1, bool reliable = true) =>
        new(SequenceNumber: sequence, HasReliableSequence: reliable, Text: text);

    private static CanonicalValue DiscoverCanonical(string rawText, PiiType expectedType, RiskLevel expectedLevel)
    {
        var probe = DetectionPipeline.CreateDefault().Detect(rawText);
        var candidate = Assert.Single(probe.Candidates);
        Assert.Equal(expectedType, candidate.PiiType);
        Assert.Equal(expectedLevel, candidate.RiskLevel);
        return candidate.Canonical;
    }

    private static ClipboardDecisionItem MakeLevel1Item(string text = Level1Text) =>
        new(DiscoverCanonical(text, PiiType.GpsCoordinate, RiskLevel.Level1), RiskLevel.Level1);

    private static ClipboardDecisionItem MakeLevel3Item(string text = Level3Text) =>
        new(DiscoverCanonical(text, PiiType.ResidentRegistrationNumber, RiskLevel.Level3), RiskLevel.Level3);

    private static ClipboardDecisionScope CreateScope(string sourceText, params ClipboardDecisionItem[] items) =>
        CreateScope(sourceText, generation: 0, items);

    private static ClipboardDecisionScope CreateScope(string sourceText, long generation, params ClipboardDecisionItem[] items)
    {
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe(sourceText);
        return new ClipboardDecisionScope(tracker, stamp, items, generation);
    }

    private static void SetSuccessfulRead(FakeClipboardReadTransport transport, string text, uint sequence = 1, bool reliable = true) =>
        transport.NextReadResult = ClipboardTextReadResult.Success(Snapshot(text, sequence, reliable));

    private static (ClipboardDecisionActionResolver Resolver, FakeClipboardDecisionScopeLifecycle Lifecycle,
        FakeForegroundTargetCapture TargetCapture, FakeClipboardReadTransport ReadTransport,
        FakeClipboardWriteTransport WriteTransport, ClipboardOperationGate Gate) CreateResolver(
        ITrustExceptionProvider? trustProvider = null)
    {
        var (resolver, lifecycle, targetCapture, readTransport, writeTransport, gate, _) =
            CreateResolverWithVerificationHandoff(trustProvider);
        return (resolver, lifecycle, targetCapture, readTransport, writeTransport, gate);
    }

    // Phase 3C STEP34 -- innermost helper exposing the FakeClipboardComposerVerificationHandoff for
    // tests that need to observe Publish calls; CreateResolver (above) delegates here and simply
    // discards the extra element, so every pre-existing call site continues to compile unchanged
    // (mirrors ClipboardPrivacyCoordinatorTests' own CreateStartedWithVerification precedent).
    private static (ClipboardDecisionActionResolver Resolver, FakeClipboardDecisionScopeLifecycle Lifecycle,
        FakeForegroundTargetCapture TargetCapture, FakeClipboardReadTransport ReadTransport,
        FakeClipboardWriteTransport WriteTransport, ClipboardOperationGate Gate,
        FakeClipboardComposerVerificationHandoff VerificationHandoff) CreateResolverWithVerificationHandoff(
        ITrustExceptionProvider? trustProvider = null)
    {
        var lifecycle = new FakeClipboardDecisionScopeLifecycle();
        var targetCapture = new FakeForegroundTargetCapture();
        var readTransport = new FakeClipboardReadTransport();
        var writeTransport = new FakeClipboardWriteTransport();
        var gate = new ClipboardOperationGate();
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var processor = new ClipboardPrivacyProcessor(trustProvider ?? new FakeTrustExceptionProvider());
        var resolver = new ClipboardDecisionActionResolver(
            gate, lifecycle, targetCapture, readTransport, writeTransport, processor, verificationHandoff);
        return (resolver, lifecycle, targetCapture, readTransport, writeTransport, gate, verificationHandoff);
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

    // ==================================================================
    // AO. REVALIDATION
    // ==================================================================

    // ---- 1. inactive original scope -> Stale ----
    [Fact]
    public async Task InactiveOriginalScope_ReturnsStale()
    {
        var (resolver, lifecycle, targetCapture, readTransport, _, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        lifecycle.DefaultIsActiveResult = false;

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
        Assert.Equal(0, targetCapture.CaptureCallCount);
        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), readTransport.CallLog);
    }

    // ---- 2. committed scope -> Stale ----
    [Fact]
    public async Task CommittedScope_ReturnsStale()
    {
        var (resolver, _, _, _, _, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        scope.ApplyIntent(item, ClipboardDecisionIntent.BypassOnce);
        scope.CommitResolved(scope.InitialStamp);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
    }

    // ---- 3. foreign item -> Stale, no capture ----
    [Fact]
    public async Task ForeignItem_ReturnsStale_NoCapture()
    {
        var (resolver, _, targetCapture, _, _, _) = CreateResolver();
        var scopeItem = MakeLevel1Item(Level1Text);
        var foreignItem = MakeLevel3Item(Level3Text);
        var scope = CreateScope(Level1Text, scopeItem);

        var result = await resolver.ResolveAsync(scope, foreignItem, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.Equal(0, targetCapture.CaptureCallCount);
    }

    // ---- 4. unsupported current target -> Stale, no read ----
    [Fact]
    public async Task UnsupportedCurrentTarget_ReturnsStale_NoRead()
    {
        var (resolver, _, targetCapture, readTransport, _, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        targetCapture.SnapshotToReturn = NotChatGpt;

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardReadTransport.ReadTextSnapshotAsync), readTransport.CallLog);
    }

    // ---- 5. guarded read failure -> Stale ----
    [Fact]
    public async Task GuardedReadFailure_ReturnsStale()
    {
        var (resolver, _, _, readTransport, _, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        readTransport.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
    }

    // ---- 6. notification during guarded read -> Stale ----
    [Fact]
    public async Task NotificationDuringGuardedRead_ReturnsStale()
    {
        var (resolver, lifecycle, _, readTransport, _, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        readTransport.HoldReadsUntilReleased = true;
        lifecycle.EnqueueIsActiveResult(true); // H (initial)
        lifecycle.EnqueueIsActiveResult(false); // K (post-read) -- a notification arrived while the read was in flight

        var resolveTask = resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        await WaitUntilAsync(() => readTransport.PendingHeldReadCount > 0);
        readTransport.ReleaseNextRead(ClipboardTextReadResult.Success(Snapshot(Level1Text)));

        var result = await resolveTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
    }

    // ---- 8. A -> B -> A never revives the old action ----
    [Fact]
    public async Task AtoBtoA_NeverRevivesOldAction()
    {
        var (resolver, _, _, readTransport, _, _) = CreateResolver();
        var item = MakeLevel1Item(Level1Text);
        var scope = CreateScope(Level1Text, item);

        SetSuccessfulRead(readTransport, DifferentRawText); // B
        var firstResult = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.Stale, firstResult.Outcome);

        SetSuccessfulRead(readTransport, Level1Text); // back to A
        var secondResult = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, secondResult.Outcome);
    }

    // ---- 9. requested item missing from the fresh plan -> Stale ----
    [Fact]
    public async Task RequestedItemMissingFromFreshPlan_ReturnsStale()
    {
        var (resolver, _, _, readTransport, _, _) = CreateResolver();
        var item = MakeLevel3Item(Level3Text);
        var scope = CreateScope(Level3Text, item);
        SetSuccessfulRead(readTransport, NoPiiText); // nothing detectable anymore

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
    }

    // ---- 10. fresh plan contains a new, unexpected NeedsDecision item -> Stale ----
    [Fact]
    public async Task FreshPlanContainsExtraNeedsDecisionItem_ReturnsStale()
    {
        var (resolver, _, _, readTransport, _, _) = CreateResolver();
        var combinedText = $"{Level3Text} {Level1Text}";
        var item = MakeLevel3Item(Level3Text);
        // scope's own plan only ever covered the RRN -- the original attempt was constructed from
        // just that text, before the GPS pair was ever part of the composition.
        var scope = CreateScope(combinedText, item);
        SetSuccessfulRead(readTransport, combinedText);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
    }

    // ---- 11. fresh plan lost a sibling item (a trust/exception change dropped it to Bypass) -> Stale ----
    [Fact]
    public async Task PolicyChangeDropsSiblingFromNeedsDecision_ReturnsStale()
    {
        var level1Canonical = DiscoverCanonical(Level1Text, PiiType.GpsCoordinate, RiskLevel.Level1);
        var trustSnapshot = new TrustExceptionSnapshot(
            trustedPublic: [],
            exceptions: [new AmbiguousExceptionValue(PiiType.GpsCoordinate, level1Canonical)]);
        var (resolver, _, _, readTransport, _, _) = CreateResolver(new FakeTrustExceptionProvider(trustSnapshot));

        var level1Item = new ClipboardDecisionItem(level1Canonical, RiskLevel.Level1);
        var level3Item = MakeLevel3Item(Level3Text);
        var combinedText = $"{Level1Text} {Level3Text}";
        var scope = CreateScope(combinedText, level1Item, level3Item);
        SetSuccessfulRead(readTransport, combinedText);

        var result = await resolver.ResolveAsync(scope, level3Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
    }

    // ---- 12. same canonical, but a mismatched RiskLevel -> exact-tuple mismatch -> Stale ----
    [Fact]
    public async Task SameCanonicalDifferentRiskLevel_ReturnsStale()
    {
        var (resolver, _, _, readTransport, _, _) = CreateResolver();
        var canonical = DiscoverCanonical(Level3Text, PiiType.ResidentRegistrationNumber, RiskLevel.Level3);
        var mismatchedItem = new ClipboardDecisionItem(canonical, RiskLevel.Level1); // wrong RiskLevel, on purpose
        var scope = CreateScope(Level3Text, mismatchedItem);
        SetSuccessfulRead(readTransport, Level3Text);

        var result = await resolver.ResolveAsync(scope, mismatchedItem, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
    }

    // ---- 13. a policy/trust change resolves the ENTIRE original plan away -> Stale ----
    [Fact]
    public async Task PolicyChangeResolvesEntireOriginalPlan_ReturnsStale()
    {
        var level1Canonical = DiscoverCanonical(Level1Text, PiiType.GpsCoordinate, RiskLevel.Level1);
        var trustSnapshot = new TrustExceptionSnapshot(
            trustedPublic: [],
            exceptions: [new AmbiguousExceptionValue(PiiType.GpsCoordinate, level1Canonical)]);
        var (resolver, _, _, readTransport, _, _) = CreateResolver(new FakeTrustExceptionProvider(trustSnapshot));

        var level1Item = new ClipboardDecisionItem(level1Canonical, RiskLevel.Level1);
        var scope = CreateScope(Level1Text, level1Item);
        SetSuccessfulRead(readTransport, Level1Text);

        var result = await resolver.ResolveAsync(scope, level1Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
    }

    // ---- 14. revalidation never republishes a scope -- TryPublish/Reset are never called ----
    [Fact]
    public async Task Revalidation_DoesNotRepublishScope()
    {
        var (resolver, lifecycle, _, readTransport, _, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);

        await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(0, lifecycle.TryPublishCallCount);
        Assert.Equal(0, lifecycle.ResetCallCount);
    }

    // ---- 15. exactly one real privacy evaluation per action ----
    [Fact]
    public async Task OnlyOneRealEvaluationPerAction()
    {
        var trustProvider = new FakeTrustExceptionProvider();
        var (resolver, _, _, readTransport, _, _) = CreateResolver(trustProvider);
        var item = MakeLevel3Item(Level3Text);
        var scope = CreateScope(Level3Text, item);
        SetSuccessfulRead(readTransport, Level3Text);

        await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(1, trustProvider.LoadCount);
    }

    // ==================================================================
    // AP. CHOICES
    // ==================================================================

    // ---- 1. Level1 Protect -> Applied, write occurs ----
    [Fact]
    public async Task Level1_Protect_Applied_WriteOccurs()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.True(result.ScopeCommitted);
        Assert.Equal(1, writeTransport.CallCount);
    }

    // ---- 2. Level1 BypassOnce -> Applied, no write, grant created ----
    [Fact]
    public async Task Level1_BypassOnce_Applied_NoWrite_GrantCreated()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.True(result.ScopeCommitted);
        Assert.Equal(0, writeTransport.CallCount);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.Canonical));
    }

    // ---- 3. Level3 first BypassOnce -> AwaitingSecondConfirmation, no write/grant/commit ----
    [Fact]
    public async Task Level3_FirstBypassOnce_ReturnsAwaitingSecondConfirmation()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel3Item();
        var scope = CreateScope(Level3Text, item);
        SetSuccessfulRead(readTransport, Level3Text);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
        Assert.Equal(0, writeTransport.CallCount);
        Assert.False(scope.Committed);
        Assert.Equal(ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation, scope.GetChoice(item));
    }

    // ---- 4. Level3 second, fully-revalidated BypassOnce -> final Applied ----
    [Fact]
    public async Task Level3_SecondBypassOnce_AfterFullRevalidation_FinalApplied()
    {
        var (resolver, _, _, readTransport, _, _) = CreateResolver();
        var item = MakeLevel3Item();
        var scope = CreateScope(Level3Text, item);
        SetSuccessfulRead(readTransport, Level3Text);

        var first = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, first.Outcome);

        SetSuccessfulRead(readTransport, Level3Text); // a brand-new, fully independent revalidation
        var second = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, second.Outcome);
        Assert.False(second.ClipboardMutated);
        Assert.True(second.ScopeCommitted);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.Canonical));
    }

    // ---- 5. a notification between the two Level3 clicks -> the second click is Stale ----
    [Fact]
    public async Task Level3_NotificationBetweenClicks_SecondClickStale()
    {
        var (resolver, lifecycle, _, readTransport, _, _) = CreateResolver();
        var item = MakeLevel3Item();
        var scope = CreateScope(Level3Text, item);
        SetSuccessfulRead(readTransport, Level3Text);

        var first = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, first.Outcome);

        lifecycle.DefaultIsActiveResult = false; // a new clipboard notification superseded this scope

        var second = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, second.Outcome);
        Assert.Equal(ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation, scope.GetChoice(item));
    }

    // ---- 6. Level3 Awaiting -> Protect switches straight to final Protect ----
    [Fact]
    public async Task Level3_AwaitingThenProtect_FinalProtect_Applied()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel3Item();
        var scope = CreateScope(Level3Text, item);
        SetSuccessfulRead(readTransport, Level3Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        var first = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, first.Outcome);

        SetSuccessfulRead(readTransport, Level3Text);
        var second = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, second.Outcome);
        Assert.True(second.ClipboardMutated);
        Assert.True(second.ScopeCommitted);
        Assert.Equal(ClipboardRuntimeItemChoice.Protect, scope.GetChoice(item));
    }

    // ---- 7/8. multi-item, one choice resolved -> Applied but not committed; zero write/grant ----
    [Fact]
    public async Task MultiItem_OneChoiceResolved_Applied_ButNotCommitted_NoWriteNoGrant()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var level1Item = MakeLevel1Item(Level1Text);
        var level3Item = MakeLevel3Item(Level3Text);
        var combinedText = $"{Level1Text} {Level3Text}";
        var scope = CreateScope(combinedText, level1Item, level3Item);
        SetSuccessfulRead(readTransport, combinedText);

        var result = await resolver.ResolveAsync(scope, level1Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
        Assert.Equal(0, writeTransport.CallCount);
        Assert.False(scope.Committed);
        Assert.Equal(ClipboardRuntimeItemChoice.BypassOnce, scope.GetChoice(level1Item));
        Assert.Equal(ClipboardRuntimeItemChoice.Unresolved, scope.GetChoice(level3Item));
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, level1Item.Canonical));
    }

    // ---- 9. choice mutation followed by lifecycle invalidation BEFORE the post-mutation check
    // (STEP24.1 linearization) -> Stale, even though the mutation happened on the (now detached)
    // scope object itself ----
    [Fact]
    public async Task ChoiceMutation_ThenInvalidationBeforePostCheck_ReturnsStale()
    {
        var (resolver, lifecycle, _, readTransport, _, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        lifecycle.EnqueueIsActiveResult(true); // H
        lifecycle.EnqueueIsActiveResult(true); // K
        lifecycle.EnqueueIsActiveResult(true); // Q
        lifecycle.EnqueueIsActiveResult(false); // S -- the linearization point observes invalidation

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.Equal(ClipboardRuntimeItemChoice.BypassOnce, scope.GetChoice(item));
        Assert.False(scope.Committed);
    }

    // ---- Phase 3C STEP38 (item 27) -- session-lock-flavored regression: a real Windows session
    // lock is, from IClipboardDecisionScopeLifecycle's own perspective, indistinguishable from any
    // other event that makes IsActive(scope) false (Phase 3C STEP37.1's RESET_GENERATION_PROOF --
    // ClipboardDecisionScopeLifecycle.Reset() and AdvanceOnClipboardNotification() perform the
    // IDENTICAL `_generation++; _activeScope = null;` critical section). This test names that
    // scenario explicitly at the resolver's own final linearization checkpoint (AH, immediately
    // before CommitResolved) -- a lock firing there means the pre-lock scope can never be finalized
    // as current, exactly like every other checkpoint above. No resolver-specific session-lock code
    // exists or is needed (frozen, Phase 3C STEP38 instruction). ----
    [Fact]
    public async Task SessionLockFlavored_SimulatedResetAtFinalCheckpoint_PreLockScopeCannotFinalizeAsCurrent()
    {
        var (resolver, lifecycle, _, readTransport, _, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        lifecycle.EnqueueIsActiveResult(true); // H
        lifecycle.EnqueueIsActiveResult(true); // K
        lifecycle.EnqueueIsActiveResult(true); // Q
        lifecycle.EnqueueIsActiveResult(true); // S
        lifecycle.EnqueueIsActiveResult(false); // AA-pre (all-Bypass path's own final commit checkpoint) -- simulated session lock Reset() lands here

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.False(scope.Committed);
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.Canonical));
    }

    // ==================================================================
    // AQ. FINAL COMMIT
    // ==================================================================

    // ---- 1. all runtime Bypass + zero base Protect -> no write, committed against currentStamp,
    // grants created ----
    [Fact]
    public async Task AllBypass_NoBaseProtect_NoWrite_CommittedAgainstCurrentStamp_GrantsCreated()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var level1Item = MakeLevel1Item(Level1Text);
        var level3Item = MakeLevel3Item(Level3Text);
        var combinedText = $"{Level1Text} {Level3Text}";
        var scope = CreateScope(combinedText, level1Item, level3Item);
        SetSuccessfulRead(readTransport, combinedText);

        // level1Item (Level1) finalizes in one BypassOnce; level3Item (Level3) needs two.
        await resolver.ResolveAsync(scope, level1Item, ClipboardDecisionIntent.BypassOnce);
        SetSuccessfulRead(readTransport, combinedText);
        var awaiting = await resolver.ResolveAsync(scope, level3Item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, awaiting.Outcome);
        SetSuccessfulRead(readTransport, combinedText);
        var final = await resolver.ResolveAsync(scope, level3Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, final.Outcome);
        Assert.False(final.ClipboardMutated);
        Assert.True(final.ScopeCommitted);
        Assert.Equal(0, writeTransport.CallCount);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, level1Item.Canonical));
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, level3Item.Canonical));
    }

    // ---- 2. runtime Bypass but an existing BASE Protect candidate is present in the same text ->
    // a write is still required ----
    [Fact]
    public async Task RuntimeAllBypass_ButBaseProtectPresent_WriteStillRequired()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var combinedText = $"{Level3Text} {ProtectPhoneText}";
        var level3Item = MakeLevel3Item(Level3Text);
        var scope = CreateScope(combinedText, level3Item);
        SetSuccessfulRead(readTransport, combinedText);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        var first = await resolver.ResolveAsync(scope, level3Item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, first.Outcome);
        SetSuccessfulRead(readTransport, combinedText);
        var result = await resolver.ResolveAsync(scope, level3Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.True(result.ScopeCommitted);
        Assert.Equal(1, writeTransport.CallCount);
        // grant #6: no pre-write grant exists -- the runtime-Bypass item's grant is bound to the
        // post-write revision, never the scope's own InitialStamp.
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, level3Item.Canonical));
    }

    // ---- 3. all Protect -> aliased replacement, write once, committed, zero bypass grants ----
    [Fact]
    public async Task AllProtect_WriteOnce_Committed_ZeroGrants()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.Equal(1, writeTransport.CallCount);
        Assert.NotEqual(Level1Text, writeTransport.ReceivedReplacementTexts[0]);
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.Canonical));
    }

    // ---- 4. mixed runtime Protect + Bypass -> Protect aliased, Bypass raw remains, post-write
    // grant minted only for the Bypass item ----
    [Fact]
    public async Task MixedRuntimeProtectAndBypass_ProtectAliased_BypassRawRemains_GrantForBypassOnly()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var level1Item = MakeLevel1Item(Level1Text); // will end up Protect
        var level3Item = MakeLevel3Item(Level3Text); // will end up BypassOnce, after two steps
        var combinedText = $"{Level1Text} {Level3Text}";
        var scope = CreateScope(combinedText, level1Item, level3Item);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        SetSuccessfulRead(readTransport, combinedText);
        var step1 = await resolver.ResolveAsync(scope, level3Item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, step1.Outcome);

        SetSuccessfulRead(readTransport, combinedText);
        var step2 = await resolver.ResolveAsync(scope, level1Item, ClipboardDecisionIntent.Protect);
        Assert.Equal(ClipboardDecisionActionOutcome.Applied, step2.Outcome);
        Assert.False(step2.ScopeCommitted); // level3Item is still Awaiting

        SetSuccessfulRead(readTransport, combinedText);
        var step3 = await resolver.ResolveAsync(scope, level3Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, step3.Outcome);
        Assert.True(step3.ClipboardMutated);
        Assert.True(step3.ScopeCommitted);
        Assert.Equal(1, writeTransport.CallCount);
        var replacement = writeTransport.ReceivedReplacementTexts[0];
        Assert.Contains(Level3Text, replacement); // final Bypass -> left raw
        Assert.DoesNotContain("126.9780", replacement); // final Protect -> aliased away
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, level3Item.Canonical)); // grant #5/#6
    }

    // ---- 7. unreliable sequence + a required Protect -> WriteFailed, no write, no grant ----
    [Fact]
    public async Task UnreliableSequence_WithRequiredProtect_ReturnsWriteFailed_NoWriteNoGrant()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        readTransport.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Level1Text, reliable: false));

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.WriteFailed, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
        Assert.Equal(0, writeTransport.CallCount);
        Assert.False(scope.Committed);
    }

    // ---- 8. write transport returns an unmutated failure -> WriteFailed, no grant ----
    [Fact]
    public async Task WriteTransportReturnsUnmutatedFailure_ReturnsWriteFailed_NoGrant()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.SequenceChanged, mutated: false);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.WriteFailed, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
        Assert.False(scope.Committed);
    }

    // ---- 9. write transport mutates but is unverified -> MutatedUnverified, no grant, no success
    // claim ----
    [Fact]
    public async Task WriteTransportReturnsMutatedButUnverified_ReturnsMutatedUnverified_NoGrant()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.MutatedUnverified, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
        Assert.False(scope.Committed);
    }

    // ---- 10. verified write, THEN a notification arrives before the scope could be committed ->
    // Stale, but ClipboardMutated=true, ScopeCommitted=false ----
    [Fact]
    public async Task VerifiedWrite_ThenNotificationBeforeCommit_ReturnsStale_MutatedTrue_CommittedFalse()
    {
        var (resolver, lifecycle, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        // Single-item Protect flow's IsActive call order: H, K, Q, S, AC (pre-write), AG (post-write
        // fail-fast #1), AH (pre-commit fail-fast #2), AH (final linearization). The notification
        // arrives right after the verified write -- AG is the first check to observe it.
        lifecycle.EnqueueIsActiveResult(true); // H
        lifecycle.EnqueueIsActiveResult(true); // K
        lifecycle.EnqueueIsActiveResult(true); // Q
        lifecycle.EnqueueIsActiveResult(true); // S
        lifecycle.EnqueueIsActiveResult(true); // AC
        lifecycle.EnqueueIsActiveResult(false); // AG

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
        Assert.Equal(1, writeTransport.CallCount);
        Assert.False(scope.Committed);
    }

    // ---- 10b. same scenario, but the notification arrives one check later (AH's pre-commit
    // fail-fast) -- still Stale/mutated=true/committed=false ----
    [Fact]
    public async Task VerifiedWrite_ThenNotificationJustBeforeCommit_ReturnsStale_MutatedTrue_CommittedFalse()
    {
        var (resolver, lifecycle, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        lifecycle.EnqueueIsActiveResult(true); // H
        lifecycle.EnqueueIsActiveResult(true); // K
        lifecycle.EnqueueIsActiveResult(true); // Q
        lifecycle.EnqueueIsActiveResult(true); // S
        lifecycle.EnqueueIsActiveResult(true); // AC
        lifecycle.EnqueueIsActiveResult(true); // AG
        lifecycle.EnqueueIsActiveResult(false); // AH pre-commit fail-fast

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
        Assert.False(scope.Committed);
    }

    // ---- 11. verified write + successful linearization -> Applied/mutated=true/committed=true
    // (already proven by AllProtect_WriteOnce_Committed_ZeroGrants and Level1_Protect_Applied_WriteOccurs
    // above -- restated here for direct traceability to the required test list) ----
    [Fact]
    public async Task VerifiedWrite_SuccessfulLinearization_AppliedMutatedCommitted()
    {
        var (resolver, _, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.True(result.ScopeCommitted);
    }

    // ==================================================================
    // AT. COMPOSER_VERIFICATION_HANDOFF (Phase 3C STEP33 audit, implemented Phase 3C STEP34)
    // ==================================================================

    // ---- 1/2/3/4. verified write + successful commit -> exactly one Publish call, carrying the
    // EXACT target/replacementText this attempt's own write used and the EXACT scope.Generation the
    // scope was constructed with (never a fresh generation read) ----
    [Fact]
    public async Task VerifiedWriteAndCommit_PublishesOnce_WithExactTargetTextAndGeneration()
    {
        var (resolver, _, _, readTransport, writeTransport, _, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, generation: 77, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.Equal(1, verificationHandoff.CallCount);
        Assert.Equal(ChatGpt, verificationHandoff.ReceivedExpectedTargets[0]);
        Assert.Equal(writeTransport.ReceivedReplacementTexts[0], verificationHandoff.ReceivedExpectedProtectedTexts[0]);
        Assert.Equal(77, verificationHandoff.ReceivedExpectedGenerations[0]);
    }

    // ---- 5. write failure -> no Publish ----
    [Fact]
    public async Task WriteFailure_NoPublish()
    {
        var (resolver, _, _, readTransport, writeTransport, _, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.SequenceChanged, mutated: false);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.WriteFailed, result.Outcome);
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    // ---- 6. mutated-unverified -> no Publish ----
    [Fact]
    public async Task MutatedUnverified_NoPublish()
    {
        var (resolver, _, _, readTransport, writeTransport, _, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.MutatedUnverified, result.Outcome);
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    // ---- 7. AwaitingSecondConfirmation -> no Publish ----
    [Fact]
    public async Task AwaitingSecondConfirmation_NoPublish()
    {
        var (resolver, _, _, readTransport, _, _, verificationHandoff) = CreateResolverWithVerificationHandoff();
        var item = MakeLevel3Item();
        var scope = CreateScope(Level3Text, item);
        SetSuccessfulRead(readTransport, Level3Text);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, result.Outcome);
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    // ---- 8. sibling unresolved / Applied(false,false) -> no Publish ----
    [Fact]
    public async Task SiblingUnresolved_AppliedButNotCommitted_NoPublish()
    {
        var (resolver, _, _, readTransport, _, _, verificationHandoff) = CreateResolverWithVerificationHandoff();
        var level1Item = MakeLevel1Item(Level1Text);
        var level3Item = MakeLevel3Item(Level3Text);
        var combinedText = $"{Level1Text} {Level3Text}";
        var scope = CreateScope(combinedText, level1Item, level3Item);
        SetSuccessfulRead(readTransport, combinedText);

        var result = await resolver.ResolveAsync(scope, level1Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.False(result.ScopeCommitted);
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    // ---- 9. all-Bypass / no-Protect Applied(false,true) -> no Publish (nothing was ever written to
    // the clipboard, so there is nothing for a composer to be verified against) ----
    [Fact]
    public async Task AllBypass_AppliedAndCommitted_NoWrite_NoPublish()
    {
        var (resolver, _, _, readTransport, writeTransport, _, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ScopeCommitted);
        Assert.Equal(0, writeTransport.CallCount);
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    // ---- 10. AG stale after a successful physical write -> Stale(mutated:true), no Publish ----
    [Fact]
    public async Task AgStaleAfterVerifiedWrite_NoPublish()
    {
        var (resolver, lifecycle, _, readTransport, writeTransport, _, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        // IsActive call order for a single-item Protect flow: H, K, Q, S, AC (pre-write), AG
        // (post-write fail-fast #1), AH (pre-commit fail-fast #2), AH (final linearization). The
        // notification lands right after the verified write -- AG is the first check to see it.
        lifecycle.EnqueueIsActiveResult(true); // H
        lifecycle.EnqueueIsActiveResult(true); // K
        lifecycle.EnqueueIsActiveResult(true); // Q
        lifecycle.EnqueueIsActiveResult(true); // S
        lifecycle.EnqueueIsActiveResult(true); // AC
        lifecycle.EnqueueIsActiveResult(false); // AG

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.False(scope.Committed);
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    // ---- 11. AH-pre stale (notification lands after ObserveCurrent, before CommitResolved) ->
    // Stale(mutated:true), no Publish ----
    [Fact]
    public async Task AhPreStaleBeforeCommit_NoPublish()
    {
        var (resolver, lifecycle, _, readTransport, writeTransport, _, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        lifecycle.EnqueueIsActiveResult(true); // H
        lifecycle.EnqueueIsActiveResult(true); // K
        lifecycle.EnqueueIsActiveResult(true); // Q
        lifecycle.EnqueueIsActiveResult(true); // S
        lifecycle.EnqueueIsActiveResult(true); // AC
        lifecycle.EnqueueIsActiveResult(true); // AG
        lifecycle.EnqueueIsActiveResult(false); // AH pre-commit fail-fast

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.False(scope.Committed);
        Assert.Equal(0, verificationHandoff.CallCount);
    }

    // ---- 12. AH-post stale AFTER CommitResolved already ran -- the critical linearization race:
    // scope.Committed is already true (STALE_OBJECT_MUTATION_POLICY -- the mutation lands on the
    // now-detached scope) yet the resolver still reports Stale and must NOT publish ----
    [Fact]
    public async Task AhPostStaleAfterCommitResolved_ScopeCommittedButNoPublish()
    {
        var (resolver, lifecycle, _, readTransport, writeTransport, _, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        lifecycle.EnqueueIsActiveResult(true); // H
        lifecycle.EnqueueIsActiveResult(true); // K
        lifecycle.EnqueueIsActiveResult(true); // Q
        lifecycle.EnqueueIsActiveResult(true); // S
        lifecycle.EnqueueIsActiveResult(true); // AC
        lifecycle.EnqueueIsActiveResult(true); // AG
        lifecycle.EnqueueIsActiveResult(true); // AH pre-commit fail-fast
        lifecycle.EnqueueIsActiveResult(false); // AH-post -- the load-bearing linearization check

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted); // the REPORTED result, not the scope's own raw state
        Assert.True(scope.Committed); // but CommitResolved DID already run, on the now-detached scope
        Assert.Equal(0, verificationHandoff.CallCount); // and Publish is gated on the SAME failed check
    }

    // ---- 13. Publish returning false -> no retry, Applied(true,true) still reported unchanged ----
    [Fact]
    public async Task PublishReturnsFalse_NoRetry_AppliedStillReported()
    {
        var (resolver, _, _, readTransport, writeTransport, _, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);
        verificationHandoff.ResultToReturn = false;

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.True(result.ScopeCommitted);
        Assert.Equal(1, verificationHandoff.CallCount); // exactly one attempt, never retried
    }

    // ---- 14. Publish throwing -> Failed(clipboardMutated:true), never a fabricated Applied, gate
    // still released (falls through to the resolver's own existing outer catch -- no local swallow) ----
    [Fact]
    public async Task PublishThrows_ReturnsFailedWithClipboardMutatedTrue_GateReleased()
    {
        var (resolver, _, _, readTransport, writeTransport, gate, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);
        verificationHandoff.ThrowOnPublish = new InvalidOperationException("synthetic handoff failure");

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Failed, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);

        // The gate must have been released -- a subsequent acquisition must not hang.
        await gate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
    }

    // ---- 15. mixed Protect + Bypass final replacement -- the exact final string (raw Bypass
    // spans still present, Protect spans aliased away) is handed off unchanged, never recomputed,
    // never classified/rejected as PII-bearing ----
    [Fact]
    public async Task MixedProtectAndBypass_HandsOffExactFinalReplacementTextUnchanged()
    {
        var (resolver, _, _, readTransport, writeTransport, _, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        var level1Item = MakeLevel1Item(Level1Text); // will end up Protect
        var level3Item = MakeLevel3Item(Level3Text); // will end up BypassOnce, after two steps
        var combinedText = $"{Level1Text} {Level3Text}";
        var scope = CreateScope(combinedText, level1Item, level3Item);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        SetSuccessfulRead(readTransport, combinedText);
        await resolver.ResolveAsync(scope, level3Item, ClipboardDecisionIntent.BypassOnce); // -> Awaiting

        SetSuccessfulRead(readTransport, combinedText);
        await resolver.ResolveAsync(scope, level1Item, ClipboardDecisionIntent.Protect); // -> Applied, not committed

        SetSuccessfulRead(readTransport, combinedText);
        var final = await resolver.ResolveAsync(scope, level3Item, ClipboardDecisionIntent.BypassOnce); // -> final commit + write

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, final.Outcome);
        Assert.Equal(1, verificationHandoff.CallCount);
        var handedOff = verificationHandoff.ReceivedExpectedProtectedTexts[0];
        Assert.Equal(writeTransport.ReceivedReplacementTexts[0], handedOff); // identical string, no recomputation
        Assert.Contains(Level3Text, handedOff); // final Bypass -> left raw, still present
        Assert.DoesNotContain("126.9780", handedOff); // final Protect -> aliased away
    }

    // ==================================================================
    // AU. GENERATION_REBIND
    // ==================================================================

    // ---- resolver depends on no fresh-generation-read seam of any kind -- already enforced
    // structurally by Resolver_HasOnlyTheExpectedNarrowDependencyFields's forbidden-type list
    // (IClipboardGenerationSnapshot), restated here as its own focused, directly-named test ----
    [Fact]
    public void Resolver_HasNoGenerationSnapshotField()
    {
        var fields = typeof(ClipboardDecisionActionResolver).GetFields(BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(IClipboardGenerationSnapshot));
    }

    // ---- two independently-published scopes, at two different generations, each hand off their
    // OWN immutable Generation -- never shared, never rebound to the other's value, and the first
    // scope's own Generation is provably untouched after the second attempt completes ----
    [Fact]
    public async Task TwoScopesAtDifferentGenerations_EachPublishesItsOwnGeneration()
    {
        var (resolver, _, _, readTransport, writeTransport, _, verificationHandoff) =
            CreateResolverWithVerificationHandoff();
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        var itemA = MakeLevel1Item(Level1Text);
        var scopeAtFive = CreateScope(Level1Text, generation: 5, itemA);
        SetSuccessfulRead(readTransport, Level1Text);
        var resultA = await resolver.ResolveAsync(scopeAtFive, itemA, ClipboardDecisionIntent.Protect);
        Assert.Equal(ClipboardDecisionActionOutcome.Applied, resultA.Outcome);

        var itemB = MakeLevel1Item(Level1Text);
        var scopeAtSix = CreateScope(Level1Text, generation: 6, itemB);
        SetSuccessfulRead(readTransport, Level1Text);
        var resultB = await resolver.ResolveAsync(scopeAtSix, itemB, ClipboardDecisionIntent.Protect);
        Assert.Equal(ClipboardDecisionActionOutcome.Applied, resultB.Outcome);

        Assert.Equal(2, verificationHandoff.CallCount);
        Assert.Equal(5, verificationHandoff.ReceivedExpectedGenerations[0]);
        Assert.Equal(6, verificationHandoff.ReceivedExpectedGenerations[1]);

        // The first scope's own Generation remains exactly what it was constructed with -- the
        // second attempt (a completely different scope instance) never touched it.
        Assert.Equal(5, scopeAtFive.Generation);
        Assert.Equal(6, scopeAtSix.Generation);
    }

    // ==================================================================
    // AV. RACE_INTEGRATION (Phase 3C STEP33 audit's NOTIFICATION_RACE_ANALYSIS, case E)
    // ==================================================================

    // A pure pass-through decorator that advances a REAL ClipboardDecisionScopeLifecycle's
    // generation (via its narrow IClipboardNotificationLifecycle view -- exactly what a real
    // clipboard notification callback would do) immediately BEFORE delegating to the real
    // ClipboardComposerVerifier's own Publish -- deterministically simulates a clipboard
    // notification racing in between the resolver's own AH-post IsActive(scope) success check and
    // the Publish call it gates, without any sleep or real threading.
    private sealed class GenerationAdvancingHandoffDecorator(
        IClipboardNotificationLifecycle notificationLifecycle, IClipboardComposerVerificationHandoff inner)
        : IClipboardComposerVerificationHandoff
    {
        public bool Publish(ForegroundTargetSnapshot expectedTarget, string expectedProtectedText, long expectedGeneration)
        {
            notificationLifecycle.AdvanceOnClipboardNotification();
            return inner.Publish(expectedTarget, expectedProtectedText, expectedGeneration);
        }
    }

    // ---- a notification races in between the resolver's own successful AH-post linearization
    // check and its call to Publish: the resolver still reports its already-linearized Applied
    // semantics (the advance is strictly AFTER that success point), but the REAL
    // ClipboardComposerVerifier's own independent PRECHECK rejects the now-stale expectedGeneration
    // -- no pending record is installed, so the old (superseded) expected text is never rebound to
    // the new generation and no stale pending remains usable ----
    [Fact]
    public async Task NotificationRacesBetweenLinearizationAndPublish_VerifierRejectsStaleGeneration()
    {
        var sharedGate = new ClipboardOperationGate();
        var sharedLifecycle = new ClipboardDecisionScopeLifecycle();
        var composerReadTransport = new FakeComposerReadTransport();
        var realVerifier = new ClipboardComposerVerifier(sharedLifecycle, composerReadTransport, sharedGate);
        var decorator = new GenerationAdvancingHandoffDecorator(sharedLifecycle, realVerifier);

        var targetCapture = new FakeForegroundTargetCapture();
        var readTransport = new FakeClipboardReadTransport();
        var writeTransport = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 2) };
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());
        var resolver = new ClipboardDecisionActionResolver(
            sharedGate, sharedLifecycle, targetCapture, readTransport, writeTransport, processor, decorator);

        var g1 = sharedLifecycle.AdvanceOnClipboardNotification();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, g1, item);
        Assert.True(sharedLifecycle.TryPublish(g1, scope));
        SetSuccessfulRead(readTransport, Level1Text);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        // The resolver's own linearization already succeeded (IsActive(scope) was true at the
        // instant it checked, strictly before the decorator's advance) -- its own reported outcome
        // is unaffected by what happens afterward inside the handoff call.
        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.True(result.ScopeCommitted);
        Assert.Equal(g1, scope.Generation); // never rebound

        // But the scope was superseded by the decorator's own advance, and the real verifier's own
        // Publish correctly rejected the now-stale expectedGeneration -- nothing pending remains.
        Assert.False(sharedLifecycle.IsActive(scope));
        var verifyResult = await realVerifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.NotAttempted, verifyResult.Outcome);
    }

    // ==================================================================
    // AW. SAME_VERIFIER_INSTANCE
    // ==================================================================

    // A pure pass-through spy -- lets the test deterministically wait until a real (possibly
    // slow-to-observe, background-worker-driven) coordinator attempt has actually completed its own
    // Publish call, exactly mirroring how ClipboardPrivacyCoordinatorTests polls a Fake's own
    // CallCount for the identical purpose; CallCount is incremented only AFTER the real inner
    // Publish call has fully returned.
    private sealed class PublishCountingHandoffDecorator(IClipboardComposerVerificationHandoff inner)
        : IClipboardComposerVerificationHandoff
    {
        public int CallCount { get; private set; }

        public bool Publish(ForegroundTargetSnapshot expectedTarget, string expectedProtectedText, long expectedGeneration)
        {
            var result = inner.Publish(expectedTarget, expectedProtectedText, expectedGeneration);
            CallCount++;
            return result;
        }
    }

    // ---- the SAME concrete ClipboardComposerVerifier instance backs a real
    // ClipboardPrivacyCoordinator's write-handoff AND a real ClipboardDecisionActionResolver's
    // write-handoff -- its single pending slot reflects only the LATEST valid publication (the
    // resolver's), never a queue/history of both ----
    [Fact]
    public async Task SameConcreteVerifier_BacksCoordinatorAndResolverHandoffs_LatestPublicationWins()
    {
        var sharedGate = new ClipboardOperationGate();
        var generationSnapshot = new FakeClipboardGenerationSnapshot { Generation = 1 };
        var composerReadTransport = new FakeComposerReadTransport();
        var sharedVerifier = new ClipboardComposerVerifier(generationSnapshot, composerReadTransport, sharedGate);
        var spy = new PublishCountingHandoffDecorator(sharedVerifier);

        // ---- Coordinator side: a write-eligible clipboard notification whose verified rewrite
        // publishes through the shared verifier (via the spy). ----
        var coordinatorReadTransport = new FakeClipboardReadTransport
        {
            NextReadResult = ClipboardTextReadResult.Success(Snapshot("coordinator raw text")),
        };
        var coordinatorTargetCapture = new FakeForegroundTargetCapture();
        var coordinatorProcessor = new FakeClipboardPrivacyProcessor
        {
            WritePlanToReturn = new ClipboardWritePlan("coordinator-replacement"),
        };
        var coordinatorWriteTransport = new FakeClipboardWriteTransport
        {
            NextResult = ClipboardWriteResult.Success(resultSequence: 1),
        };
        var coordinatorNotificationLifecycle = new FakeClipboardNotificationLifecycle();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();

        var coordinator = new ClipboardPrivacyCoordinator(
            coordinatorReadTransport, coordinatorTargetCapture, coordinatorProcessor, coordinatorWriteTransport,
            coordinatorNotificationLifecycle, decisionSessionPublisher, sharedGate, spy, sharedVerifier);

        coordinator.Start();
        try
        {
            coordinatorReadTransport.RaiseChanged(
                new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            await WaitUntilAsync(() => spy.CallCount >= 1);
        }
        finally
        {
            coordinator.Stop();
        }

        Assert.Equal(1, spy.CallCount);

        // ---- Resolver side: a second, independent verified write publishes through the SAME
        // shared verifier -- this must become the new, sole pending record, overwriting the
        // coordinator's own, never queuing alongside it. ----
        var resolverLifecycle = new FakeClipboardDecisionScopeLifecycle();
        var resolverTargetCapture = new FakeForegroundTargetCapture();
        var resolverReadTransport = new FakeClipboardReadTransport();
        var resolverWriteTransport = new FakeClipboardWriteTransport
        {
            NextResult = ClipboardWriteResult.Success(resultSequence: 2),
        };
        var resolverProcessor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());
        var resolver = new ClipboardDecisionActionResolver(
            sharedGate, resolverLifecycle, resolverTargetCapture, resolverReadTransport, resolverWriteTransport,
            resolverProcessor, spy);

        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, generation: 1, item);
        SetSuccessfulRead(resolverReadTransport, Level1Text);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.Equal(2, spy.CallCount);

        // A single VerifyAsync call now matches ONLY the resolver's (latest) replacement text --
        // proving the coordinator's earlier pending record was overwritten, not queued behind it.
        composerReadTransport.NextReadResult =
            ComposerTextReadResult.Success(resolverWriteTransport.ReceivedReplacementTexts[0]);
        var verifyResult = await sharedVerifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.Verified, verifyResult.Outcome);
    }

    // ==================================================================
    // AR. RESULT / ERROR
    // ==================================================================

    // ---- 1. the result type contains no sensitive values -- structural ----
    [Fact]
    public void ClipboardDecisionActionResult_HasOnlyMetadataProperties()
    {
        var properties = typeof(ClipboardDecisionActionResult).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var names = properties.Select(p => p.Name).ToArray();

        Assert.Equal(3, names.Length);
        Assert.Contains(nameof(ClipboardDecisionActionResult.Outcome), names);
        Assert.Contains(nameof(ClipboardDecisionActionResult.ClipboardMutated), names);
        Assert.Contains(nameof(ClipboardDecisionActionResult.ScopeCommitted), names);
    }

    // ---- 2. Stale can carry ClipboardMutated=true (proven above by VerifiedWrite_ThenNotification*) ----
    [Fact]
    public void StaleFactory_CanCarryClipboardMutatedTrue()
    {
        var result = ClipboardDecisionActionResult.Stale(clipboardMutated: true);
        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
    }

    // ---- 3. MutatedUnverified always carries ClipboardMutated=true ----
    [Fact]
    public void MutatedUnverifiedFactory_AlwaysSetsClipboardMutatedTrue()
    {
        var result = ClipboardDecisionActionResult.MutatedUnverified();
        Assert.Equal(ClipboardDecisionActionOutcome.MutatedUnverified, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
    }

    // ---- 4. WriteFailed's no-mutation path carries ClipboardMutated=false ----
    [Fact]
    public void WriteFailedFactory_AlwaysSetsClipboardMutatedFalse()
    {
        var result = ClipboardDecisionActionResult.WriteFailed();
        Assert.Equal(ClipboardDecisionActionOutcome.WriteFailed, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
    }

    // ---- 5. an unexpected processor exception -> Failed, gate released afterward ----
    [Fact]
    public async Task UnexpectedProcessorException_ReturnsFailed_GateReleasedAfterward()
    {
        var lifecycle = new FakeClipboardDecisionScopeLifecycle();
        var targetCapture = new FakeForegroundTargetCapture();
        var readTransport = new FakeClipboardReadTransport();
        var writeTransport = new FakeClipboardWriteTransport();
        var gate = new ClipboardOperationGate();
        var throwingPipeline = new DetectionPipeline([new ThrowingDetector()]);
        var processor = new ClipboardPrivacyProcessor(throwingPipeline, new FakeTrustExceptionProvider());
        var verificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var resolver = new ClipboardDecisionActionResolver(
            gate, lifecycle, targetCapture, readTransport, writeTransport, processor, verificationHandoff);

        var item = MakeLevel1Item(); // the item's own identity is irrelevant -- Detect() always throws
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Failed, result.Outcome);
        Assert.False(result.ClipboardMutated);

        // The gate must have been released -- a subsequent acquisition must not hang.
        await gate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
    }

    // ---- 7. an unexpected failure discovered strictly AFTER a confirmed external write -> Failed,
    // with ClipboardMutated=true ----
    [Fact]
    public async Task UnexpectedFailureAfterConfirmedWrite_ReturnsFailed_WithClipboardMutatedTrue()
    {
        var (resolver, lifecycle, _, readTransport, writeTransport, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        SetSuccessfulRead(readTransport, Level1Text);
        writeTransport.NextResult = ClipboardWriteResult.Success(resultSequence: 2);

        lifecycle.EnqueueIsActiveResult(true); // H
        lifecycle.EnqueueIsActiveResult(true); // K
        lifecycle.EnqueueIsActiveResult(true); // Q
        lifecycle.EnqueueIsActiveResult(true); // S
        lifecycle.EnqueueIsActiveResult(true); // AC
        lifecycle.EnqueueIsActiveResult(true); // AG (post-write fail-fast #1)
        lifecycle.ThrowOnIsActiveCallNumber = 7; // AH pre-commit check
        lifecycle.ThrowOnIsActiveAtCall = new InvalidOperationException("synthetic unexpected failure");

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Failed, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.False(result.ScopeCommitted);
    }

    // ---- 8. a later resolve attempt can still acquire the gate after a prior failed attempt ----
    [Fact]
    public async Task AfterPriorFailure_LaterResolveAttempt_CanStillAcquireGate()
    {
        var (resolver, _, _, readTransport, _, _) = CreateResolver();
        var item = MakeLevel1Item();
        var scope = CreateScope(Level1Text, item);
        readTransport.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.NativeFailure);

        var first = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.Stale, first.Outcome);

        SetSuccessfulRead(readTransport, Level1Text);
        var second = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, second.Outcome);
    }

    // ==================================================================
    // AS. STRUCTURAL
    // ==================================================================

    [Fact]
    public void ClipboardDecisionActionResolver_IsNotPublic() =>
        Assert.False(typeof(ClipboardDecisionActionResolver).IsPublic);

    [Theory]
    [InlineData(typeof(ClipboardDecisionActionResult))]
    [InlineData(typeof(ClipboardDecisionActionOutcome))]
    public void ActionResultTypes_AreNotPublic(Type type) => Assert.False(type.IsPublic);

    [Fact]
    public void Resolver_HasOnlyTheExpectedNarrowDependencyFields()
    {
        var fields = typeof(ClipboardDecisionActionResolver).GetFields(BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.Equal(7, fields.Length);

        var forbiddenTypeNames = new HashSet<string>
        {
            "SemaphoreSlim",
            nameof(ClipboardDecisionScope),
            nameof(ClipboardDecisionItem),
            nameof(ClipboardDecisionPlan),
            nameof(ClipboardDecisionResolutionState),
            "CanonicalValue",
            "RevisionStamp",
            "String",
            "AliasMap",
            "AliasAssignment",
            "ClipboardTextSnapshot",
            "ForegroundTargetSnapshot",
            "ClipboardBypassGrant",
            // Phase 3C STEP34 -- the resolver must source its composer-verification handoff
            // generation from ClipboardDecisionScope.Generation, never a fresh snapshot read (see
            // ClipboardDecisionScope's own GENERATION doc and the Phase 3C STEP33 audit's
            // GENERATION_REBIND_POLICY).
            nameof(IClipboardGenerationSnapshot),
            nameof(IClipboardComposerVerificationInvalidation),
            nameof(ClipboardComposerVerifier),
            nameof(IComposerReadTransport),
        };

        foreach (var field in fields)
        {
            Assert.DoesNotContain(field.FieldType.Name, forbiddenTypeNames);
        }

        // Phase 3C STEP34 -- the one new dependency this STEP is allowed to add.
        Assert.Contains(fields, f => f.FieldType == typeof(IClipboardComposerVerificationHandoff));
    }

    [Theory]
    [InlineData(typeof(ClipboardDecisionActionResult))]
    [InlineData(typeof(ClipboardDecisionActionOutcome))]
    public void ActionResultTypes_HaveNoDebuggerAttributes(Type type)
    {
        Assert.Empty(type.GetCustomAttributes(typeof(DebuggerDisplayAttribute), inherit: false));
        Assert.Empty(type.GetCustomAttributes(typeof(DebuggerTypeProxyAttribute), inherit: false));
    }

    // ==================================================================
    // AN. SAME GATE INSTANCE (Coordinator + Resolver)
    // ==================================================================

    // Proves the operation gate a future composition root shares between ClipboardPrivacyCoordinator
    // and ClipboardDecisionActionResolver actually serializes the two, AND that the clipboard
    // notification callback still advances the shared lifecycle immediately even while the resolver
    // holds that same gate (Phase 3B STEP22 audit's CALLBACK_NONBLOCKING_PROOF, extended here to a
    // real cross-consumer scenario).
    [Fact]
    public async Task SameGateInstance_CoordinatorAndResolver_SerializeAndCallbackNeverBlocks()
    {
        var sharedGate = new ClipboardOperationGate();
        var sharedLifecycle = new ClipboardDecisionScopeLifecycle();

        var coordinatorReadTransport = new FakeClipboardReadTransport();
        var coordinatorTargetCapture = new FakeForegroundTargetCapture();
        var coordinatorProcessor = new FakeClipboardPrivacyProcessor();
        var coordinatorWriteTransport = new FakeClipboardWriteTransport();
        var decisionSessionPublisher = new FakeClipboardDecisionSessionPublisher();
        var coordinatorVerificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var coordinatorVerificationInvalidation = new FakeClipboardComposerVerificationInvalidation();

        var coordinator = new ClipboardPrivacyCoordinator(
            coordinatorReadTransport, coordinatorTargetCapture, coordinatorProcessor, coordinatorWriteTransport,
            sharedLifecycle, decisionSessionPublisher, sharedGate, coordinatorVerificationHandoff, coordinatorVerificationInvalidation);

        var resolverTargetCapture = new FakeForegroundTargetCapture();
        var resolverReadTransport = new FakeClipboardReadTransport();
        var resolverWriteTransport = new FakeClipboardWriteTransport();
        var resolverProcessor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());
        var resolverVerificationHandoff = new FakeClipboardComposerVerificationHandoff();
        var resolver = new ClipboardDecisionActionResolver(
            sharedGate, sharedLifecycle, resolverTargetCapture, resolverReadTransport, resolverWriteTransport, resolverProcessor,
            resolverVerificationHandoff);

        coordinator.Start();
        try
        {
            // The coordinator's own worker takes the shared gate for a held-in-flight attempt.
            coordinatorReadTransport.HoldReadsUntilReleased = true;
            coordinatorReadTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 1, HasReliableSequence: true, HasUnicodeText: true));
            await WaitUntilAsync(() => coordinatorReadTransport.PendingHeldReadCount > 0);

            // Make a scope the ACTIVE scope on the shared lifecycle (via the same TryPublish a real
            // ClipboardDecisionSessionPublisher would use) so the resolver's own IsActive checks
            // actually pass -- otherwise it would return Stale before ever touching the gate/target/
            // read seams this test needs to observe.
            var item = MakeLevel1Item();
            var scope = CreateScope(Level1Text, item);
            Assert.True(sharedLifecycle.TryPublish(sharedLifecycle.CurrentGeneration, scope));
            SetSuccessfulRead(resolverReadTransport, Level1Text);

            // A resolver attempt started now must not be able to proceed past target capture until
            // the coordinator's own attempt releases the shared gate.
            var resolveTask = resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

            await Task.Delay(50);
            Assert.Equal(0, resolverTargetCapture.CaptureCallCount); // still blocked on the shared gate

            coordinatorReadTransport.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable));

            var firstResult = await resolveTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(resolverTargetCapture.CaptureCallCount >= 1); // proceeded once the gate was released
            Assert.Equal(ClipboardDecisionActionOutcome.Applied, firstResult.Outcome);

            // Publish a SECOND scope active, then start a new resolver attempt held mid-read (so it
            // is holding the shared gate). A brand-new clipboard notification must STILL advance
            // the shared lifecycle immediately, proving the callback never waits on this gate.
            var secondScope = CreateScope(Level1Text, item);
            Assert.True(sharedLifecycle.TryPublish(sharedLifecycle.CurrentGeneration, secondScope));
            resolverReadTransport.HoldReadsUntilReleased = true;
            var secondResolveTask = resolver.ResolveAsync(secondScope, item, ClipboardDecisionIntent.BypassOnce);
            await WaitUntilAsync(() => resolverReadTransport.PendingHeldReadCount > 0);

            var generationBefore = sharedLifecycle.CurrentGeneration;
            coordinatorReadTransport.RaiseChanged(new ClipboardChangeNotification(SequenceNumber: 2, HasReliableSequence: true, HasUnicodeText: false));
            Assert.True(sharedLifecycle.CurrentGeneration > generationBefore); // advanced even though the gate is held elsewhere
            Assert.False(sharedLifecycle.IsActive(secondScope)); // and the held-mid-attempt scope was superseded

            resolverReadTransport.ReleaseNextRead(ClipboardTextReadResult.Success(Snapshot(Level1Text)));
            var secondResult = await secondResolveTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ClipboardDecisionActionOutcome.Stale, secondResult.Outcome); // superseded before it could finish
        }
        finally
        {
            coordinator.Stop();
        }
    }
}
