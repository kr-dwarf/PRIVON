using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The Level-1 one-revision pass (frozen D3A rows POL-L1-ONCE-R1 / POL-L1-ONCE-R2 live in UsCategoryBoundaryTests, driven
// through the same real path): a pass is the grant the user's BypassOnce decision mints, bound to EXACTLY {revision, detectorId, canonical};
// the processor evaluates it, and the scope consumes it atomically, at most once. It lowers nothing except the one Level-1 candidate it names,
// writes nothing, and says nothing about sending (A-1: nothing in the product blocks a send).
public sealed class OneRevisionPassTests
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    private static readonly UsAuthoritySet Authority =
        UsAuthoritySet.Load(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid());

    private const string Alice = "Name: Alice O'Neil";
    private const string Ssn = "SSN: 321-54-9876";

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(), new GpsCoordinateDetector(),
    ];

    private static ClipboardTextSnapshot Snapshot(string text) => new(SequenceNumber: 1, HasReliableSequence: true, Text: text);

    private static ProtectionCategorySettings Settings(bool name = true) =>
        new(NameEnabled: name, PhoneEnabled: true, EmailEnabled: true, AddressEnabled: true, CompanyEnabled: true);

    private static ClipboardPrivacyProcessor Processor(ProtectionCategorySettings? settings = null) =>
        new(UsOptInPipeline.Create(Legacy(), authority: Authority),
            new FakeTrustExceptionProvider(new TrustExceptionSnapshot(trustedPublic: [], exceptions: [])),
            new FakeProtectionCategorySettingsProvider(settings ?? Settings()));

    private sealed class Rig
    {
        public required ClipboardDecisionActionResolver Resolver { get; init; }
        public required FakeClipboardReadTransport Read { get; init; }
        public required FakeClipboardWriteTransport Write { get; init; }
        public required FakeClipboardDecisionScopeLifecycle Lifecycle { get; init; }
        public required ClipboardPrivacyProcessor Processor { get; init; }
    }

    private static Rig NewRig(ClipboardPrivacyProcessor? processor = null, IClipboardDecisionScopeLifecycle? lifecycle = null)
    {
        processor ??= Processor();
        var fake = new FakeClipboardDecisionScopeLifecycle();
        var read = new FakeClipboardReadTransport();
        var write = new FakeClipboardWriteTransport();
        return new Rig
        {
            Resolver = new ClipboardDecisionActionResolver(
                new ClipboardOperationGate(), lifecycle ?? fake, new FakeForegroundTargetCapture(), read, write, processor,
                new FakeClipboardComposerVerificationHandoff()),
            Read = read, Write = write, Lifecycle = fake, Processor = processor,
        };
    }

    /// <summary>A scope for <paramref name="text"/> holding the items the PROCESSOR builds for it (so each carries the real detector id).</summary>
    private static ClipboardDecisionScope ScopeFor(string text, ClipboardPrivacyProcessor processor, RevisionTracker? tracker = null)
    {
        tracker ??= new RevisionTracker();
        return new ClipboardDecisionScope(tracker, tracker.Observe(text), processor.Process(ChatGpt, Snapshot(text)).DecisionPlan!.Items, 0);
    }

    /// <summary>Commits a BypassOnce for every Level-1 item and the two-step BypassOnce for Level 3, directly on the scope (no clipboard).</summary>
    private static void BypassAll(ClipboardDecisionScope scope)
    {
        foreach (var item in scope.Items)
        {
            scope.ApplyIntent(item, ClipboardDecisionIntent.BypassOnce);
            if (item.RiskLevel == RiskLevel.Level3) scope.ApplyIntent(item, ClipboardDecisionIntent.BypassOnce);
        }

        scope.CommitResolved(scope.InitialStamp);
    }

    private static ClipboardDecisionItem AliceItem(ClipboardPrivacyProcessor processor) =>
        Assert.Single(processor.Process(ChatGpt, Snapshot(Alice)).DecisionPlan!.Items);

    // ===== the grant: minted only for what the user approved, and only when its owner is known ==================================

    [Fact]
    public void TheGrant_IsMintedOnlyFromAFinalBypassOnce_AndOnlyForAnItemWithADetectorId()
    {
        var processor = Processor();
        var item = AliceItem(processor);

        // BypassOnce -> a grant bound to {revision, detectorId, canonical}
        var bypass = ScopeFor(Alice, processor);
        BypassAll(bypass);
        Assert.True(bypass.HasBypassGrant(bypass.InitialStamp, item.DetectorId, item.Canonical));

        // Protect -> no grant
        var protect = ScopeFor(Alice, processor);
        protect.ApplyIntent(item, ClipboardDecisionIntent.Protect);
        protect.CommitResolved(protect.InitialStamp);
        Assert.False(protect.HasBypassGrant(protect.InitialStamp, item.DetectorId, item.Canonical));

        // the user changes their mind (BypassOnce, then Protect): the FINAL choice rules -- no grant
        var flipped = ScopeFor(Alice, processor);
        flipped.ApplyIntent(item, ClipboardDecisionIntent.BypassOnce);
        flipped.ApplyIntent(item, ClipboardDecisionIntent.Protect);
        flipped.CommitResolved(flipped.InitialStamp);
        Assert.False(flipped.HasBypassGrant(flipped.InitialStamp, item.DetectorId, item.Canonical));

        // an item whose detector stamped no id: the user may decide it, but no grant can be named for it (ownership unclear -> fail closed)
        var anonymous = new ClipboardDecisionItem(item.Canonical, RiskLevel.Level1);
        var tracker = new RevisionTracker();
        var scope = new ClipboardDecisionScope(tracker, tracker.Observe(Alice), [anonymous], 0);
        scope.ApplyIntent(anonymous, ClipboardDecisionIntent.BypassOnce);
        scope.CommitResolved(scope.InitialStamp);
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, default, anonymous.Canonical));
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, anonymous.Canonical));
        Assert.False(scope.TryConsumeBypassGrant(scope.InitialStamp, default, anonymous.Canonical));
        Assert.Contains("GrantCount = 0", scope.ToString());
    }

    [Fact]
    public void AGrantWithoutADetectorId_EvenIfSomehowPresent_IsNeitherReadNorConsumed()
    {
        // Defense in depth: the mint rule makes this state unreachable through the product, so it is forged here. Ownership unclear -> fail closed.
        var item = AliceItem(Processor());
        var anonymous = new ClipboardDecisionItem(item.Canonical, RiskLevel.Level1);
        var tracker = new RevisionTracker();
        var scope = new ClipboardDecisionScope(tracker, tracker.Observe(Alice), [anonymous], 0);
        var forged = ClipboardDecisionResolutionState.Empty
            .WithChoice(anonymous, ClipboardRuntimeItemChoice.BypassOnce)
            .WithCommit(new HashSet<ClipboardBypassGrant> { new(scope.InitialStamp, default, anonymous.Canonical) });
        typeof(ClipboardDecisionScope).GetField("_resolutionState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(scope, forged);
        Assert.True(scope.Committed);

        Assert.False(scope.HasBypassGrant(scope.InitialStamp, default, anonymous.Canonical));
        Assert.False(scope.TryConsumeBypassGrant(scope.InitialStamp, default, anonymous.Canonical));
        Assert.Contains("GrantCount = 1", scope.ToString());                     // and it was not removed either
    }

    [Fact]
    public void NoGrantExistsBeforeTheCommit_AndNothingCanBeConsumedBeforeIt()
    {
        var processor = Processor();
        var scope = ScopeFor(Alice, processor);
        var item = Assert.Single(scope.Items);
        scope.ApplyIntent(item, ClipboardDecisionIntent.BypassOnce);        // a provisional choice only

        Assert.False(scope.Committed);
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
        Assert.False(scope.TryConsumeBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
    }

    // ===== exact binding: revision + detectorId + canonical ======================================================================

    [Fact]
    public void TheScopeConsumesOnlyTheExactTriple_AndEveryOtherTripleChangesNothing()
    {
        var processor = Processor();
        var scope = ScopeFor(Alice, processor);
        var item = Assert.Single(scope.Items);
        BypassAll(scope);
        var otherRevision = new RevisionTracker().Observe("some other text");
        var otherCanonical = new CanonicalValue(PiiType.Name, "Someone Else");

        Assert.False(scope.TryConsumeBypassGrant(otherRevision, item.DetectorId, item.Canonical));                       // revision only
        Assert.False(scope.TryConsumeBypassGrant(scope.InitialStamp, DetectorId.Create("us.address"), item.Canonical));    // detectorId only
        Assert.False(scope.TryConsumeBypassGrant(scope.InitialStamp, item.DetectorId, otherCanonical));                  // canonical only
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));                           // nothing was spent by those

        Assert.True(scope.TryConsumeBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
        Assert.False(scope.TryConsumeBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));                  // at most once
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
    }

    [Fact]
    public async Task ARevisionThatChanged_CannotUseThePreviousGrant_EvenWhenTheTextReturnsToTheSameContent()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        var scope = ScopeFor(Alice, processor);
        var item = Assert.Single(scope.Items);
        BypassAll(scope);

        // the clipboard changed (revision advances in the scope's own tracker) ...
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot("Name: Alice O'Neil "));
        var changed = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);
        Assert.Equal(ClipboardOneRevisionPassOutcome.Evaluated, changed.Outcome);
        Assert.Equal(ClipboardOneRevisionPassDisposition.NeedsDecision, Assert.Single(changed.Evaluation!.Candidates).Disposition);

        // ... and changing it BACK is a strictly later revision: the identical content does not resurrect the pass
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));
        var back = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);
        var row = Assert.Single(back.Evaluation!.Candidates);
        Assert.Equal(ClipboardOneRevisionPassDisposition.NeedsDecision, row.Disposition);
        Assert.Equal(ClipboardOneRevisionPassTrust.Untrusted, row.Trust);

        // the grant still exists under its ORIGINAL revision only; it was not spent by either attempt
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
    }

    [Fact]
    public async Task AGrantForAnotherDetectorOrAnotherCanonical_NeverPassesTheCandidate()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));
        var real = AliceItem(processor);

        foreach (var forged in new[]
                 {
                     real with { DetectorId = DetectorId.Create("us.address") },                                  // same canonical, another detector
                     real with { Canonical = new CanonicalValue(PiiType.Name, "Someone Else") },                   // same detector, another canonical
                 })
        {
            var tracker = new RevisionTracker();
            var scope = new ClipboardDecisionScope(tracker, tracker.Observe(Alice), [forged], 0);
            scope.ApplyIntent(forged, ClipboardDecisionIntent.BypassOnce);
            scope.CommitResolved(scope.InitialStamp);

            var result = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

            var row = Assert.Single(result.Evaluation!.Candidates);
            Assert.Equal(ClipboardOneRevisionPassDisposition.NeedsDecision, row.Disposition);
            Assert.False(result.Evaluation.AnyPassed);
            Assert.True(scope.HasBypassGrant(scope.InitialStamp, forged.DetectorId, forged.Canonical));             // and its own grant is untouched
        }
    }

    // ===== only Level 1: Level 2 and Level 3 are never bypassed =================================================================

    [Fact]
    public void ALevel2Candidate_StaysProtected_EvenWhenAGrantWithTheSameKeyExists()
    {
        var processor = Processor();
        var level1 = AliceItem(processor);                                       // us.name | Name | Level 1 | "Alice O'Neil"
        var tracker = new RevisionTracker();
        const string level2 = "Full Name: Alice O'Neil";                         // the same name, labelled strongly: us.name Level 2 -> Protect
        var scope = new ClipboardDecisionScope(tracker, tracker.Observe(level2), [level1], 0);
        scope.ApplyIntent(level1, ClipboardDecisionIntent.BypassOnce);
        scope.CommitResolved(scope.InitialStamp);                                // a live pass for {this revision, us.name, Alice O'Neil}

        var evaluation = processor.EvaluateOneRevisionPass(ChatGpt, Snapshot(level2), scope);

        var row = Assert.Single(evaluation.Candidates);
        Assert.Equal(RiskLevel.Level2, row.Item.RiskLevel);
        Assert.Equal(ClipboardOneRevisionPassDisposition.Protect, row.Disposition);
        Assert.Equal(ClipboardOneRevisionPassTrust.Untrusted, row.Trust);
        Assert.True(evaluation.HasProtect);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, level1.DetectorId, level1.Canonical));                 // not spent
    }

    [Fact]
    public async Task ALevel3Candidate_NeverUsesAPass_AndALevel3GrantIsNeverConsumable()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        var scope = ScopeFor(Ssn, processor);
        var item = Assert.Single(scope.Items);
        Assert.Equal(RiskLevel.Level3, item.RiskLevel);
        BypassAll(scope);                                                        // Level 3 needs the second confirmation; both were given
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Ssn));

        var result = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        var row = Assert.Single(result.Evaluation!.Candidates);
        Assert.Equal(ClipboardOneRevisionPassDisposition.NeedsDecision, row.Disposition);                           // still waits for a decision
        Assert.Equal(ClipboardOneRevisionPassTrust.Untrusted, row.Trust);
        Assert.False(scope.TryConsumeBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));            // not consumable here either
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));                    // and left exactly as it was
    }

    [Fact]
    public async Task ALevel1PassCoversOnlyItsOwnCandidate_ASiblingLevel3AndProtectKeepTheirOwnDecisions()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        const string text = "Name: Alice O'Neil SSN: 321-54-9876 Phone: 212-555-1212";
        var scope = ScopeFor(text, processor);
        Assert.Equal(2, scope.Items.Count);                                      // Alice (Level 1) and the SSN (Level 3); the phone is base Protect
        BypassAll(scope);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(text));

        var result = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        var rows = result.Evaluation!.Candidates.ToDictionary(c => c.Item.Canonical.PiiType);
        Assert.Equal(ClipboardOneRevisionPassDisposition.AllowOriginalOnce, rows[PiiType.Name].Disposition);
        Assert.Equal(ClipboardOneRevisionPassDisposition.NeedsDecision, rows[PiiType.UsSsn].Disposition);           // the SSN is NOT carried along
        Assert.Equal(ClipboardOneRevisionPassDisposition.Protect, rows[PiiType.Phone].Disposition);                 // nor the phone
        Assert.True(result.Evaluation.AnyPassed);
        Assert.True(result.Evaluation.HasProtect);
        Assert.True(result.Evaluation.HasUnresolved);
        Assert.Equal(0, rig.Write.CallCount);
    }

    [Fact]
    public async Task TheSameNameTwiceInTheText_IsOneIdentity_AndOnePassCoversBothOccurrences()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        const string text = "Name: Alice O'Neil; Name: Alice O'Neil";
        var scope = ScopeFor(text, processor);
        BypassAll(scope);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(text));

        var result = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassDisposition.AllowOriginalOnce, Assert.Single(result.Evaluation!.Candidates).Disposition);
    }

    // ===== categories ============================================================================================================

    [Fact]
    public async Task WithTheNameCategoryOn_ThePassIsSpent_WithItOff_TheCandidateIsAlreadyBypassedAndNothingIsSpent()
    {
        // ON
        var onProcessor = Processor(Settings(name: true));
        var on = NewRig(onProcessor);
        var onScope = ScopeFor(Alice, onProcessor);
        BypassAll(onScope);
        on.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));
        var onResult = await on.Resolver.EvaluateOneRevisionPassAsync(onScope);
        Assert.Equal(ClipboardOneRevisionPassDisposition.AllowOriginalOnce, Assert.Single(onResult.Evaluation!.Candidates).Disposition);

        // OFF: the category (POL-L1-CATOFF) already allows the original; a pass for it is neither needed nor consumed
        var offProcessor = Processor(Settings(name: false));
        var off = NewRig(offProcessor);
        var item = AliceItem(onProcessor);
        var offScope = ScopeFor(Alice, onProcessor);                              // a scope that holds a live pass
        BypassAll(offScope);
        off.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));
        var offResult = await off.Resolver.EvaluateOneRevisionPassAsync(offScope);
        var row = Assert.Single(offResult.Evaluation!.Candidates);
        Assert.Equal(ClipboardOneRevisionPassDisposition.Bypass, row.Disposition);
        Assert.Equal(ClipboardOneRevisionPassTrust.Untrusted, row.Trust);
        Assert.True(offScope.HasBypassGrant(offScope.InitialStamp, item.DetectorId, item.Canonical));
    }

    [Fact]
    public async Task KoreanAndGlobalCandidates_AreEvaluatedExactlyAsBefore_ByTheSamePath()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        const string text = "연락처 010-1234-5678 이메일 user@example.test 901231-1234567";
        var scope = ScopeFor(text, processor);
        BypassAll(scope);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(text));

        var result = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        var rows = result.Evaluation!.Candidates.ToDictionary(c => c.Item.Canonical.PiiType);
        Assert.Equal(ClipboardOneRevisionPassDisposition.Protect, rows[PiiType.Phone].Disposition);                  // Gate 3A: phone / email Protect
        Assert.Equal(ClipboardOneRevisionPassDisposition.Protect, rows[PiiType.Email].Disposition);
        Assert.Equal(ClipboardOneRevisionPassDisposition.NeedsDecision, rows[PiiType.ResidentRegistrationNumber].Disposition);   // Level 3
        Assert.False(result.Evaluation.AnyPassed);
    }

    // ===== atomicity and concurrency =============================================================================================

    [Fact]
    public void SixteenThreadsRacingForOnePass_ExactlyOneWins_InEveryRound()
    {
        var processor = Processor();
        for (int round = 0; round < 300; round++)
        {
            var scope = ScopeFor(Alice, processor);
            var item = Assert.Single(scope.Items);
            BypassAll(scope);
            using var barrier = new Barrier(16);
            int wins = 0;

            var threads = Enumerable.Range(0, 16).Select(_ => new Thread(() =>
            {
                barrier.SignalAndWait();
                if (scope.TryConsumeBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical)) Interlocked.Increment(ref wins);
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Equal(1, wins);
            Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
        }
    }

    [Fact]
    public async Task TwoConcurrentEvaluationsOfTheSameScope_PassAtMostOnce()
    {
        var processor = Processor();
        for (int round = 0; round < 50; round++)
        {
            var rig = NewRig(processor);
            var scope = ScopeFor(Alice, processor);
            BypassAll(scope);
            rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));

            var results = await Task.WhenAll(
                Task.Run(() => rig.Resolver.EvaluateOneRevisionPassAsync(scope)),
                Task.Run(() => rig.Resolver.EvaluateOneRevisionPassAsync(scope)));

            var dispositions = results.Select(r => Assert.Single(r.Evaluation!.Candidates).Disposition).ToList();
            Assert.Equal(1, dispositions.Count(d => d == ClipboardOneRevisionPassDisposition.AllowOriginalOnce));
            Assert.Equal(1, dispositions.Count(d => d == ClipboardOneRevisionPassDisposition.NeedsDecision));
        }
    }

    [Fact]
    public void ALateCommit_AfterTheConsumption_IsRejected_SoASpentPassDoesNotComeBack()
    {
        var processor = Processor();
        var scope = ScopeFor(Alice, processor);
        var item = Assert.Single(scope.Items);
        BypassAll(scope);
        Assert.True(scope.TryConsumeBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));

        Assert.Throws<InvalidOperationException>(() => scope.CommitResolved(scope.InitialStamp));
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
    }

    [Fact]
    public void SixteenConcurrentCommits_OnOneScope_SucceedExactlyOnce()
    {
        // the commit is a compare-and-swap: of any number of racing commits only one installs its grant set, so a loser can never
        // overwrite a state in which that grant set has already been consumed
        var processor = Processor();
        for (int round = 0; round < 300; round++)
        {
            var tracker = new RevisionTracker();
            var item = AliceItem(processor);
            var scope = new ClipboardDecisionScope(tracker, tracker.Observe(Alice), [item], 0);
            scope.ApplyIntent(item, ClipboardDecisionIntent.BypassOnce);
            using var barrier = new Barrier(16);
            int committed = 0, rejected = 0;

            var threads = Enumerable.Range(0, 16).Select(_ => new Thread(() =>
            {
                barrier.SignalAndWait();
                try { scope.CommitResolved(scope.InitialStamp); Interlocked.Increment(ref committed); }
                catch (InvalidOperationException) { Interlocked.Increment(ref rejected); }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Equal(1, committed);
            Assert.Equal(15, rejected);
            Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
        }
    }

    // ===== failures are never a pass =============================================================================================

    private sealed class ThrowingTrust : ITrustExceptionProvider
    {
        public TrustExceptionSnapshot Load() => throw new InvalidOperationException("synthetic trust failure");
    }

    [Fact]
    public async Task AFailureBeforeTheConsumption_ReportsFailed_AndLeavesThePassUntouched()
    {
        var processor = Processor();
        var good = NewRig(processor);
        var scope = ScopeFor(Alice, processor);
        var item = Assert.Single(scope.Items);
        BypassAll(scope);
        good.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));

        // the trust provider fails during the evaluation: the evaluation is an exception, never "no candidates", never a pass
        var failing = NewRig(new ClipboardPrivacyProcessor(
            UsOptInPipeline.Create(Legacy(), authority: Authority), new ThrowingTrust(), new FakeProtectionCategorySettingsProvider(Settings())));
        failing.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));
        var failed = await failing.Resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassOutcome.Failed, failed.Outcome);
        Assert.Null(failed.Evaluation);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));                      // nothing was spent

        // and the next, healthy evaluation still honours it exactly once
        var ok = await good.Resolver.EvaluateOneRevisionPassAsync(scope);
        Assert.Equal(ClipboardOneRevisionPassDisposition.AllowOriginalOnce, Assert.Single(ok.Evaluation!.Candidates).Disposition);
    }

    [Fact]
    public async Task AFailureAfterTheConsumption_IsNeverReportedAsAPass_AndTheSpentPassDoesNotComeBack()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        var scope = ScopeFor(Alice, processor);
        var item = Assert.Single(scope.Items);
        BypassAll(scope);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));
        // IsActive is asked three times per evaluation: before, after the read, and at the linearization point AFTER the consumption
        rig.Lifecycle.ThrowOnIsActiveCallNumber = 3;
        rig.Lifecycle.ThrowOnIsActiveAtCall = new InvalidOperationException("synthetic failure at the linearization point");

        var failed = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassOutcome.Failed, failed.Outcome);
        Assert.Null(failed.Evaluation);                                                                              // no pass is reported ...
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));                     // ... and none is given back
        Assert.DoesNotContain("Alice", failed.ToString());
    }

    [Fact]
    public async Task AScopeSupersededWhileEvaluating_WithholdsThePass()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        var scope = ScopeFor(Alice, processor);
        BypassAll(scope);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));
        rig.Lifecycle.EnqueueIsActiveResult(true);    // before
        rig.Lifecycle.EnqueueIsActiveResult(true);    // after the read
        rig.Lifecycle.EnqueueIsActiveResult(false);   // the clipboard moved on during the evaluation

        var result = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassOutcome.Stale, result.Outcome);
        Assert.Null(result.Evaluation);
    }

    // ===== the guards in front of the evaluation =================================================================================

    [Fact]
    public async Task TheNextClipboardRevision_DropsTheScope_AndTheOldPassIsGoneWithIt()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var processor = Processor();
        var rig = NewRig(processor, lifecycle);
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        ClipboardDecisionScope? published = null;
        publisher.ScopePublished += (_, s) => published = s;
        long generation = lifecycle.AdvanceOnClipboardNotification();
        Assert.True(publisher.TryPublish(generation, Alice, processor.Process(ChatGpt, Snapshot(Alice)).DecisionPlan!));
        var scope = Assert.IsType<ClipboardDecisionScope>(published);
        var item = Assert.Single(scope.Items);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));
        Assert.Equal(ClipboardDecisionActionOutcome.Applied, (await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce)).Outcome);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));

        lifecycle.AdvanceOnClipboardNotification();                              // a new clipboard notification: a new revision of the world

        var result = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);
        Assert.Equal(ClipboardOneRevisionPassOutcome.Stale, result.Outcome);
        Assert.Null(result.Evaluation);
    }

    [Fact]
    public async Task AScopeThatWasNeverCommitted_HasNoPassToEvaluate()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        var scope = ScopeFor(Alice, processor);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));

        var result = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassOutcome.Stale, result.Outcome);
    }

    [Fact]
    public async Task ATargetOutsideTheProtectedApps_IsNotAPass_AndNothingIsRead()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        var scope = ScopeFor(Alice, processor);
        BypassAll(scope);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));
        var capture = new FakeForegroundTargetCapture
        {
            SnapshotToReturn = new(IsResolved: true, ProcessId: 7, ProcessName: "notepad",
                PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "NotAnAiApp_123"),
        };
        var resolver = new ClipboardDecisionActionResolver(
            new ClipboardOperationGate(), rig.Lifecycle, capture, rig.Read, rig.Write, processor, new FakeClipboardComposerVerificationHandoff());

        var result = await resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassOutcome.Stale, result.Outcome);
        Assert.Empty(rig.Read.ReceivedExpectedTargets);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, Assert.Single(scope.Items).DetectorId, Assert.Single(scope.Items).Canonical));
    }

    [Fact]
    public async Task AFailedGuardedRead_IsNotAPass()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        var scope = ScopeFor(Alice, processor);
        BypassAll(scope);
        rig.Read.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy);

        var result = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassOutcome.Stale, result.Outcome);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, Assert.Single(scope.Items).DetectorId, Assert.Single(scope.Items).Canonical));
    }

    // ===== the default product path is untouched =================================================================================

    [Fact]
    public void TheDefaultProcessor_HasNoUsDetectors_SoThereIsNothingToPass_AndTheNormalPathIsStateless()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());
        var tracker = new RevisionTracker();
        var scope = new ClipboardDecisionScope(tracker, tracker.Observe(Alice), [AliceItem(Processor())], 0);
        BypassAll(scope);

        var outcome = processor.Process(ChatGpt, Snapshot(Alice));                                                    // the shipped default pipeline
        var evaluation = processor.EvaluateOneRevisionPass(ChatGpt, Snapshot(Alice), scope);

        Assert.Equal(0, outcome.Result.CandidateCount);
        Assert.Empty(evaluation.Candidates);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, Assert.Single(scope.Items).DetectorId, Assert.Single(scope.Items).Canonical));
    }

    [Fact]
    public void ProcessItself_NeverReadsOrSpendsAPass_ItStaysStateless()
    {
        var processor = Processor();
        var scope = ScopeFor(Alice, processor);
        var item = Assert.Single(scope.Items);
        BypassAll(scope);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(1, processor.Process(ChatGpt, Snapshot(Alice)).Result.NeedsDecisionCount);
        }

        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
    }

    // ===== diagnostics never carry content =======================================================================================

    [Fact]
    public async Task NothingTheFeatureReports_ContainsTheTypedValue()
    {
        var processor = Processor();
        var rig = NewRig(processor);
        var scope = ScopeFor(Alice, processor);
        var item = Assert.Single(scope.Items);
        BypassAll(scope);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(Alice));

        var result = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        var texts = new[]
        {
            result.ToString(), result.Evaluation!.ToString(), Assert.Single(result.Evaluation.Candidates).ToString(),
            new ClipboardBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical).ToString(), item.ToString(), scope.ToString(),
        };
        Assert.All(texts, t =>
        {
            Assert.DoesNotContain("Alice", t);
            Assert.DoesNotContain("O'Neil", t);
        });
    }

    [Fact]
    public void ThePassIsNotPresentedAsSendBlocking_TheEvaluationOffersNoSendOrBlockMember()
    {
        var names = typeof(ClipboardOneRevisionPassEvaluation).GetMembers().Concat(typeof(ClipboardOneRevisionPassResult).GetMembers())
            .Select(m => m.Name).Concat(Enum.GetNames<ClipboardOneRevisionPassDisposition>()).Concat(Enum.GetNames<ClipboardOneRevisionPassOutcome>());

        Assert.DoesNotContain(names, n =>
            n.Contains("Send", StringComparison.OrdinalIgnoreCase) || n.Contains("Block", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Verified", StringComparison.OrdinalIgnoreCase) || n.Contains("Safe", StringComparison.OrdinalIgnoreCase));
    }
}
