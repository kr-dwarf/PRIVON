using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The Name / Address boundary of CategoryPolicyEvaluator against the frozen policy (D2.1 / D3A POL-*) and the
// existing Gate 3A contract, plus the user-visible protection status: a category switched OFF is a deliberate bypass, never a verified
// protection. Level 3 is never lowered by any category.
//   Level 3: category OFF, AmbiguousExceptionStore and Trusted Public never lower it (NeedsDecision always).
//   Level 2: only its category OFF and an exact Trusted Public value; an ambiguous exception does not apply.
//   Level 1: its category OFF, an exact ambiguous exception, and the per-revision pass (resolver BypassOnce).
public sealed class UsCategoryBoundaryTests
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    private static readonly UsAuthoritySet Authority =
        UsAuthoritySet.Load(UsPhoneAddressNameExecutionPathTests.TestAuthorityProvider.Valid());

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static ClipboardTextSnapshot Snapshot(string text, uint sequence = 1) =>
        new(SequenceNumber: sequence, HasReliableSequence: true, Text: text);

    private static ProtectionCategorySettings Settings(bool name = true, bool phone = true, bool email = true, bool address = true) =>
        new(NameEnabled: name, PhoneEnabled: phone, EmailEnabled: email, AddressEnabled: address, CompanyEnabled: true);

    private static ClipboardPrivacyProcessor Processor(
        ProtectionCategorySettings settings, TrustExceptionSnapshot? trust = null) =>
        new(UsOptInPipeline.Create(Legacy(), authority: Authority),
            new FakeTrustExceptionProvider(trust ?? new TrustExceptionSnapshot(trustedPublic: [], exceptions: [])),
            new FakeProtectionCategorySettingsProvider(settings));

    private static CanonicalValue CanonicalOf(string text) =>
        Assert.Single(UsOptInPipeline.Create(authority: Authority).Detect(text).Candidates).Canonical!.Value;

    // ===== the reproduction matrix ==========================================================================================
    // (text, switched-off category, expected counts: protect / bypass / needs-decision)

    public static TheoryData<string, string, int, int, int> Matrix => new()
    {
        // Name Level 1: needs a decision; its category OFF bypasses it (POL-L1-CATOFF)
        { "Name: Alice O'Neil", "none", 0, 0, 1 },
        { "Name: Alice O'Neil", "name", 0, 1, 0 },
        { "Dr. Alice O'Neil", "name", 0, 1, 0 },
        { "Name: Alice O'Neil", "phone", 0, 0, 1 },       // another category OFF changes nothing
        { "Name: Alice O'Neil", "address", 0, 0, 1 },
        // Name Level 2: Protect; its category OFF bypasses it (POL-L2-CATOFF)
        { "Full Name: Alice O'Neil", "none", 1, 0, 0 },
        { "Full Name: Alice O'Neil", "name", 0, 1, 0 },
        { "Full Name: Alice O'Neil", "address", 1, 0, 0 },
        // Address Level 2
        { "Address: 123 Main ST", "none", 1, 0, 0 },
        { "Address: 123 Main ST", "address", 0, 1, 0 },
        { "123 Main ST, New York, NY 10001", "address", 0, 1, 0 },
        { "Address: 123 Main ST", "name", 1, 0, 0 },
        // Phone Level 2: the US phone and the Korean phone follow the existing Phone toggle
        { "Phone: 212-555-1212", "none", 1, 0, 0 },
        { "Phone: 212-555-1212", "phone", 0, 1, 0 },
        { "연락처 010-1234-5678", "phone", 0, 1, 0 },
        { "연락처 010-1234-5678", "none", 1, 0, 0 },
        // Global email follows the existing Email toggle
        { "이메일 user@example.test", "email", 0, 1, 0 },
        { "이메일 user@example.test", "none", 1, 0, 0 },
        // Level 3 is never lowered, whichever category is OFF (all four are tried below, too)
        { "SSN: 321-54-9876", "name", 0, 0, 1 },
        { "SSN: 321-54-9876", "phone", 0, 0, 1 },
        { "SSN: 321-54-9876", "address", 0, 0, 1 },
        { "Bank Account: AB12CD34", "address", 0, 0, 1 },
        { "Driver License Number: D123-4567 State: CA", "name", 0, 0, 1 },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void TheFrozenPolicyMatrix_HoldsThroughTheRealProcessor(string text, string off, int protect, int bypass, int needsDecision)
    {
        var settings = Settings(name: off != "name", phone: off != "phone", email: off != "email", address: off != "address");

        var outcome = Processor(settings).Process(ChatGpt, Snapshot(text));

        Assert.Equal(protect, outcome.Result.ProtectCount);
        Assert.Equal(bypass, outcome.Result.BypassCount);
        Assert.Equal(needsDecision, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.TrustedCount);
    }

    [Theory]
    [InlineData("SSN: 321-54-9876")]
    [InlineData("Passport Number: P12345678")]
    [InlineData("A-Number: A1234567")]
    [InlineData("I-94 Number: 123456789AB")]
    [InlineData("Driver License Number: D123-4567 State: CA")]
    [InlineData("Routing Number: 120000003 | Account Number: AB12CD34")]
    public void AUsLevel3_IsNeverLowered_ByAnyCombinationOfCategories(string text)
    {
        foreach (bool name in new[] { true, false })
        foreach (bool phone in new[] { true, false })
        foreach (bool email in new[] { true, false })
        foreach (bool address in new[] { true, false })
        {
            var outcome = Processor(Settings(name, phone, email, address)).Process(ChatGpt, Snapshot(text));

            Assert.Equal(1, outcome.Result.NeedsDecisionCount);
            Assert.Equal(0, outcome.Result.BypassCount);
            Assert.Null(outcome.WritePlan);
        }
    }

    // ===== trust paths =====================================================================================================

    [Fact]
    public void NameLevel1_AnExactAmbiguousException_Bypasses_EvenWithTheCategoryOn()
    {
        const string text = "Name: Alice O'Neil";
        var exception = new AmbiguousExceptionValue(PiiType.Name, CanonicalOf(text));

        var outcome = Processor(Settings(), new TrustExceptionSnapshot(trustedPublic: [], exceptions: [exception])).Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.TrustedCount);
        Assert.Equal(0, outcome.Result.NeedsDecisionCount);
        Assert.Null(outcome.DecisionPlan);
    }

    [Theory]
    [InlineData("Address: 123 Main ST", PiiType.Address)]
    [InlineData("Full Name: Alice O'Neil", PiiType.Name)]
    [InlineData("Phone: 212-555-1212", PiiType.Phone)]
    public void ALevel2_ExactTrustedPublicValue_Bypasses_AnAmbiguousExceptionDoesNot(string text, PiiType type)
    {
        var canonical = CanonicalOf(text);

        var trusted = Processor(Settings(), new TrustExceptionSnapshot(trustedPublic: [new TrustedPublicValue(type, canonical)], exceptions: []))
            .Process(ChatGpt, Snapshot(text));
        var ambiguous = Processor(Settings(), new TrustExceptionSnapshot(trustedPublic: [], exceptions: [new AmbiguousExceptionValue(type, canonical)]))
            .Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, trusted.Result.TrustedCount);
        Assert.Equal(0, trusted.Result.ProtectCount);
        Assert.Equal(1, ambiguous.Result.ProtectCount);
    }

    [Fact]
    public void ALevel3_StaysNeedsDecision_WithBothStoresAndEveryCategoryOff()
    {
        const string text = "SSN: 321-54-9876";
        var canonical = CanonicalOf(text);
        var snapshot = new TrustExceptionSnapshot(
            trustedPublic: [new TrustedPublicValue(PiiType.UsSsn, canonical)], exceptions: [new AmbiguousExceptionValue(PiiType.UsSsn, canonical)]);

        var outcome = Processor(Settings(false, false, false, false), snapshot).Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.TrustedCount);
    }

    // ===== the per-revision pass (Level 1) ===================================================================================

    private static (ClipboardDecisionActionResolver Resolver, FakeClipboardReadTransport Read, FakeClipboardWriteTransport Write)
        CreateResolver(ClipboardPrivacyProcessor processor)
    {
        var read = new FakeClipboardReadTransport();
        var write = new FakeClipboardWriteTransport();
        var resolver = new ClipboardDecisionActionResolver(
            new ClipboardOperationGate(), new FakeClipboardDecisionScopeLifecycle(), new FakeForegroundTargetCapture(),
            read, write, processor, new FakeClipboardComposerVerificationHandoff());
        return (resolver, read, write);
    }

    [Fact]
    public async Task NameLevel1_BypassOnce_NeedsNoSecondConfirmation_WritesNothing_AndIsNotAProtection()
    {
        const string text = "Name: Alice O'Neil";
        var (resolver, read, write) = CreateResolver(Processor(Settings()));
        var item = Assert.Single(Processor(Settings()).Process(ChatGpt, Snapshot(text)).DecisionPlan!.Items);
        var tracker = new RevisionTracker();
        var scope = new ClipboardDecisionScope(tracker, tracker.Observe(text), [item], generation: 0);
        read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(text));

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.Equal(0, write.CallCount);
        // the pass belongs to this revision's scope; the processor itself is stateless and decides the same text again
        Assert.Equal(1, Processor(Settings()).Process(ChatGpt, Snapshot(text)).Result.NeedsDecisionCount);
    }

    // ===== frozen D3A rows, one by one (D3A.1 Fixtures B -- Level-1 policy fixtures, `Name: Alice O'Neil`) =========================
    // Base candidate of every row: us.name|Name|L1|C|6:12|Alice O'Neil|[이름1]|350

    private const string AliceRaw = "Name: Alice O'Neil";

    private static IReadOnlyList<CandidatePolicyDecision> BaseDecisionsOf(string text) =>
        CandidatePolicyEvaluator.Evaluate(
            ExceptionTrustedEvaluator.Evaluate(UsOptInPipeline.Create(authority: Authority).Detect(text), [], []));

    private static DetectionCandidate AliceCandidate()
    {
        var candidate = Assert.Single(UsOptInPipeline.Create(authority: Authority).Detect(AliceRaw).Candidates);
        Assert.Equal("us.name", candidate.DetectorId.Value);
        Assert.Equal(PiiType.Name, candidate.PiiType);
        Assert.Equal(RiskLevel.Level1, candidate.RiskLevel);
        Assert.Equal(ResolutionEvidence.C, candidate.Evidence);
        Assert.Equal(new RawSpan(6, 12), candidate.Span);
        Assert.Equal("Alice O'Neil", candidate.Canonical!.Value.Value);
        Assert.Equal(350, candidate.ResolutionRank);
        Assert.Equal("[이름1]", new AliasIdentity(AliasFamilyTable.FamilyOf(candidate.PiiType), 1).CanonicalContractToken);
        return candidate;
    }

    [Fact]
    public async Task POL_L1_CATOFF_TheNameCandidateStillExists_IsSuppressedByTheCategory_IsAllowedAsTyped_NothingIsWritten_NeverVerified()
    {
        var candidate = AliceCandidate();                                     // "detector candidate still exists"
        var offSettings = Settings(name: false);                              // CategoryState.Name=OFF

        // base policy: the Level-1 candidate waits for a decision; the CATEGORY is what suppresses it (same candidate, same span 6:12)
        var baseDecision = Assert.Single(BaseDecisionsOf(AliceRaw));
        var afterCategory = Assert.Single(CategoryPolicyEvaluator.Apply([baseDecision], offSettings));
        Assert.Equal(CandidateDisposition.NeedsDecision, baseDecision.Disposition);
        Assert.Equal(CandidateDisposition.Bypass, afterCategory.Disposition);  // AllowOriginal
        Assert.Equal(candidate.Span, afterCategory.Candidate.Candidate.Span);
        Assert.Equal(TrustState.Untrusted, afterCategory.Candidate.TrustState); // "TrustState=Untrusted"

        // the real processor: bypassed, nothing to decide, nothing to write -> RenderedOutput = the raw text
        var outcome = Processor(offSettings).Process(ChatGpt, Snapshot(AliceRaw));
        Assert.Equal(1, outcome.Result.CandidateCount);
        Assert.Equal(1, outcome.Result.BypassCount);
        Assert.Equal(0, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.TrustedCount);
        Assert.Null(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);

        // end to end: no write, and the status is Ready, never Verified
        var (tracker, write) = await ScanAsync(offSettings, AliceRaw);
        Assert.Equal(0, write.CallCount);
        Assert.Equal(ProtectionState.Ready, tracker.Current.State);
        Assert.False(tracker.Current.ClaimsProtected);
    }

    [Fact]
    public void POL_L1_CATOFF_IsANameLevel1Row_AndIsNotReadAsAnAddressLevel1Case_BecauseTheUsAddressIsLevel2Only()
    {
        foreach (string raw in new[] { "Address: 123 Main ST", "123 Main ST, New York, NY 10001", "Mailing Address: PO Box 123", "Address: RR 2 Box 15" })
        {
            Assert.All(UsOptInPipeline.Create(authority: Authority).Detect(raw).Candidates, c => Assert.Equal(RiskLevel.Level2, c.RiskLevel));
        }
    }

    // The scope the product publishes for this text: the item is the one ClipboardPrivacyProcessor itself builds (canonical, Level 1 and
    // the detector id us.name), the tracker is the scope's own, and RevisionStamp R-ONCE-1 is the revision of AliceRaw in it.
    private static ClipboardDecisionScope PublishedScopeFor(string text, ClipboardPrivacyProcessor processor, RevisionTracker tracker)
    {
        var item = Assert.Single(processor.Process(ChatGpt, Snapshot(text)).DecisionPlan!.Items);
        return new ClipboardDecisionScope(tracker, tracker.Observe(text), [item], generation: 0);
    }

    [Fact]
    public async Task POL_L1_ONCE_R1_AnExactRevisionDetectorIdCanonicalPass_IsConsumedOnce_AndTheOriginalIsAllowedWithoutRewriting()
    {
        var processor = Processor(Settings());
        var (resolver, read, write) = CreateResolver(processor);
        var scope = PublishedScopeFor(AliceRaw, processor, new RevisionTracker());
        var item = Assert.Single(scope.Items);
        read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(AliceRaw));
        Assert.Equal("us.name", item.DetectorId.Value);
        Assert.Equal(RiskLevel.Level1, item.RiskLevel);
        Assert.Equal("Alice O'Neil", item.Canonical.Value);

        // the user's real BypassOnce decision mints the pass, bound to {revision, detectorId, canonical}; before it there is none
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
        var decision = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.Applied, decision.Outcome);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));

        // R1: the evaluation under that revision
        var r1 = await resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassOutcome.Evaluated, r1.Outcome);
        var row = Assert.Single(r1.Evaluation!.Candidates);
        Assert.Equal(ClipboardOneRevisionPassTrust.OneRevisionPassExact, row.Trust);                 // TrustState = OneRevisionPassExact
        Assert.Equal(ClipboardOneRevisionPassDisposition.AllowOriginalOnce, row.Disposition);        // Disposition = AllowOriginalOnce
        Assert.False(row.PassRemainsAfter);                                                          // OneRevisionPassAfter = null
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));     //   ... the token was consumed
        Assert.Equal(item, row.Item);
        Assert.Equal(0, write.CallCount);                                                            // no rewrite, ever
        Assert.False(decision.ClipboardMutated);
        Assert.True(r1.Evaluation.AnyPassed);
        Assert.False(r1.Evaluation.HasProtect);
    }

    [Fact]
    public async Task POL_L1_ONCE_R2_TheSameRawInputAndRevisionAfterTheConsumption_IsUntrustedAndNeedsADecision_ThePassIsNeverReused()
    {
        var processor = Processor(Settings());
        var (resolver, read, write) = CreateResolver(processor);
        var scope = PublishedScopeFor(AliceRaw, processor, new RevisionTracker());
        var item = Assert.Single(scope.Items);
        read.NextReadResult = ClipboardTextReadResult.Success(Snapshot(AliceRaw));
        await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        var r1 = await resolver.EvaluateOneRevisionPassAsync(scope);
        Assert.True(r1.Evaluation!.AnyPassed);                                                       // R1 consumed the only token

        // R2: the identical RawInput under the identical revision -- no usable token (OneRevisionPassBefore = null)
        var r2 = await resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassOutcome.Evaluated, r2.Outcome);
        var row = Assert.Single(r2.Evaluation!.Candidates);
        Assert.Equal(ClipboardOneRevisionPassTrust.Untrusted, row.Trust);                            // TrustState = Untrusted
        Assert.Equal(ClipboardOneRevisionPassDisposition.NeedsDecision, row.Disposition);            // Disposition = NeedsDecision
        Assert.False(row.PassRemainsAfter);
        Assert.False(r2.Evaluation.AnyPassed);
        Assert.True(r2.Evaluation.HasUnresolved);
        Assert.Equal(0, write.CallCount);                                                            // the original is not auto-allowed

        // and a brand-new evaluation of the same text (the processor, no scope) decides it exactly like before the first pass
        var plain = processor.Process(ChatGpt, Snapshot(AliceRaw));
        Assert.Equal(1, plain.Result.NeedsDecisionCount);
        Assert.Equal(0, plain.Result.TrustedCount);
        Assert.Null(plain.WritePlan);
        Assert.Equal("us.name", Assert.Single(plain.DecisionPlan!.Items).DetectorId.Value);
    }

    // ===== a bypass is never reported as a verified protection =================================================================

    private static async Task<(ClipboardProtectionStatusTracker Tracker, FakeClipboardWriteTransport Write)> ScanAsync(
        ProtectionCategorySettings settings, params string[] texts)
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var read = new FakeClipboardReadTransport();
        var write = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 2) };
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var coordinator = new ClipboardPrivacyCoordinator(
            read, new FakeForegroundTargetCapture(), Processor(settings), write, lifecycle, new ClipboardDecisionSessionPublisher(lifecycle),
            new ClipboardOperationGate(), new FakeClipboardComposerVerificationHandoff(), new FakeClipboardComposerVerificationInvalidation(),
            diagnostics: tracker);
        coordinator.Start();

        uint sequence = 0;
        foreach (string text in texts)
        {
            sequence++;
            long before = tracker.Current.CompletedAttempts;
            read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(sequence, true, text));
            read.RaiseChanged(new ClipboardChangeNotification(sequence, true, true));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (tracker.Current.CompletedAttempts <= before)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException();
                await Task.Delay(5);
            }
        }

        coordinator.Dispose();
        tracker.Dispose();
        return (tracker, write);
    }

    [Theory]
    [InlineData("Full Name: Alice O'Neil", "name")]
    [InlineData("Address: 123 Main ST", "address")]
    [InlineData("Phone: 212-555-1212", "phone")]
    [InlineData("Name: Alice O'Neil", "name")]
    public async Task ACategoryOffBypass_IsReady_NeverVerified_AndNothingIsWritten(string text, string off)
    {
        var settings = Settings(name: off != "name", phone: off != "phone", address: off != "address");

        var (tracker, write) = await ScanAsync(settings, text);

        Assert.NotEqual(ProtectionState.Verified, tracker.Current.State);
        Assert.Equal(ProtectionState.Ready, tracker.Current.State);
        Assert.False(tracker.Current.ClaimsProtected);
        Assert.Equal(0, write.CallCount);
    }

    [Theory]
    [InlineData("Full Name: Alice O'Neil")]
    [InlineData("Address: 123 Main ST")]
    [InlineData("Phone: 212-555-1212")]
    public async Task TheSameTextWithTheCategoryOn_IsRewritten_AndOnlyThenVerified(string text)
    {
        var (tracker, write) = await ScanAsync(Settings(), text);

        Assert.Equal(ProtectionState.Verified, tracker.Current.State);
        Assert.Equal(1, write.CallCount);
    }

    [Fact]
    public async Task ALevel1NameAwaitingADecision_IsNeedsDecision_NotProtected()
    {
        var (tracker, write) = await ScanAsync(Settings(), "Name: Alice O'Neil");

        Assert.Equal(ProtectionState.NeedsDecision, tracker.Current.State);
        Assert.False(tracker.Current.ClaimsProtected);
        Assert.Equal(0, write.CallCount);
    }

    [Fact]
    public async Task AMixedClipboard_WithABypassedNameAndALevel3_IsNeverVerified()
    {
        var (tracker, write) = await ScanAsync(Settings(name: false), "Full Name: Alice O'Neil; SSN: 321-54-9876");

        Assert.Equal(ProtectionState.NeedsDecision, tracker.Current.State);
        Assert.Equal(0, write.CallCount);
    }

    // ===== the displayed status makes no claim about U.S. coverage ===========================================================

    [Fact]
    public void NoProtectionStatusText_ClaimsUnitedStatesCoverage_OrAThirteenTypeProtection()
    {
        foreach (var state in Enum.GetValues<ProtectionState>())
        foreach (bool unresolved in new[] { false, true })
        {
            var presentation = ProtectionStatusPresenter.Present(new ClipboardProtectionStatus(state, 1, unresolved, 1));

            foreach (string text in new[] { presentation.Tooltip, presentation.Detail })
            {
                Assert.DoesNotContain("미국", text);
                Assert.DoesNotContain("13", text);
                Assert.DoesNotContain("모든 개인정보", text);
                Assert.DoesNotContain("전체 보호", text);
                Assert.DoesNotContain("U.S.", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
