using Privon.App;
using Privon.Core;
using Privon.Detection;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3B -- UserExceptionPolicyEvaluator regression. Pure-function unit tests using
// hand-constructed CandidatePolicyDecision fixtures (never running the real DetectionPipeline) --
// mirrors CategoryPolicyEvaluatorTests.cs exactly. Mixed-content/real-chain coverage lives in
// ClipboardPrivacyProcessorUserExceptionTests.cs instead. Synthetic data only.
public class UserExceptionPolicyEvaluatorTests
{
    private static CandidatePolicyDecision Decision(
        PiiType type, RiskLevel level, CandidateDisposition disposition, int start = 0, string value = "SYNTHETIC") =>
        new(
            new EvaluatedCandidate(
                new DetectionCandidate(type, new RawSpan(start, 5), level, DetectionConfidence.High,
                    new CanonicalValue(type, value), DetectorName: "FakeDetector"),
                TrustState.Untrusted),
            disposition);

    private static UserExceptionValue Exception(PiiType type, string value) => new(type, new CanonicalValue(type, value));

    // ==================================================================
    // EXC-002 (pure-function) -- Phone exception A -> A Bypass, Phone B remains Protect.
    // ==================================================================
    [Fact]
    public void Cat002_PhoneExceptionA_ABecomesBypass_PhoneBUnchanged()
    {
        var exceptions = new[] { Exception(PiiType.Phone, "01012345678") };
        var a = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect, start: 0, value: "01012345678");
        var b = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect, start: 10, value: "01099998888");

        var result = UserExceptionPolicyEvaluator.Apply([a, b], exceptions);

        Assert.Equal(CandidateDisposition.Bypass, result[0].Disposition);
        Assert.Equal(CandidateDisposition.Protect, result[1].Disposition);
    }

    // ==================================================================
    // EXC-003 (pure-function) -- Email exception A -> exact canonical A Bypass, another Email
    // remains Protect.
    // ==================================================================
    [Fact]
    public void Exc003_EmailExceptionA_ABecomesBypass_OtherEmailUnchanged()
    {
        var exceptions = new[] { Exception(PiiType.Email, "user@example.test") };
        var a = Decision(PiiType.Email, RiskLevel.Level2, CandidateDisposition.Protect, start: 0, value: "user@example.test");
        var b = Decision(PiiType.Email, RiskLevel.Level2, CandidateDisposition.Protect, start: 10, value: "other@example.test");

        var result = UserExceptionPolicyEvaluator.Apply([a, b], exceptions);

        Assert.Equal(CandidateDisposition.Bypass, result[0].Disposition);
        Assert.Equal(CandidateDisposition.Protect, result[1].Disposition);
    }

    // ---- NeedsDecision and existing Bypass are NEVER altered -- same reference returned ----
    [Fact]
    public void NeedsDecisionAndExistingBypass_NeverAltered_SameReferenceReturned()
    {
        var exceptions = new[] { Exception(PiiType.Phone, "01012345678"), Exception(PiiType.Secret, "SYNTHETIC-SECRET") };
        var needsDecision = Decision(PiiType.Secret, RiskLevel.Level3, CandidateDisposition.NeedsDecision, start: 0, value: "SYNTHETIC-SECRET");
        var bypass = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Bypass, start: 10, value: "01012345678");

        var result = UserExceptionPolicyEvaluator.Apply([needsDecision, bypass], exceptions);

        Assert.Same(needsDecision, result[0]);
        Assert.Same(bypass, result[1]);
    }

    // ==================================================================
    // EXC-013 (pure-function guard) -- a Level3 candidate whose canonical value matches a stored
    // exception-shaped entry for the SAME PiiType, but whose Disposition is (correctly, per
    // upstream policy) NeedsDecision -- must remain NeedsDecision. The invariant holds because
    // this evaluator only ever transforms Disposition == Protect, never because it inspects
    // RiskLevel at all.
    // ==================================================================
    [Fact]
    public void Exc013_Level3CandidateMatchingStoredException_RemainsNeedsDecision()
    {
        var exceptions = new[] { Exception(PiiType.Secret, "SYNTHETIC-SECRET-VALUE") };
        var level3 = Decision(PiiType.Secret, RiskLevel.Level3, CandidateDisposition.NeedsDecision, start: 0, value: "SYNTHETIC-SECRET-VALUE");

        var result = UserExceptionPolicyEvaluator.Apply([level3], exceptions);

        Assert.Equal(CandidateDisposition.NeedsDecision, result[0].Disposition);
        Assert.Same(level3, result[0]);
    }

    // ==================================================================
    // EXC-015 (pure-function) -- same excepted canonical value appears twice -> both matching
    // candidates independently Bypass. Value-based, not span-based.
    // ==================================================================
    [Fact]
    public void Exc015_SameExceptedValueTwice_BothOccurrencesBypass()
    {
        var exceptions = new[] { Exception(PiiType.Phone, "01012345678") };
        var first = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect, start: 0, value: "01012345678");
        var second = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect, start: 20, value: "01012345678");

        var result = UserExceptionPolicyEvaluator.Apply([first, second], exceptions);

        Assert.All(result, d => Assert.Equal(CandidateDisposition.Bypass, d.Disposition));
    }

    // ---- exact typed identity: same string value, DIFFERENT PiiType -> no match ----
    [Fact]
    public void SameStringValue_DifferentPiiType_NoMatch()
    {
        var exceptions = new[] { Exception(PiiType.Phone, "SAME-STRING") };
        var email = Decision(PiiType.Email, RiskLevel.Level2, CandidateDisposition.Protect, value: "SAME-STRING");

        var result = UserExceptionPolicyEvaluator.Apply([email], exceptions);

        Assert.Equal(CandidateDisposition.Protect, result[0].Disposition);
    }

    // ---- empty exception set -> no-op ----
    [Fact]
    public void EmptyExceptionSet_NoOp()
    {
        var protect = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect);

        var result = UserExceptionPolicyEvaluator.Apply([protect], []);

        Assert.Same(protect, result[0]);
    }

    [Fact]
    public void Apply_NullDecisions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => UserExceptionPolicyEvaluator.Apply(null!, []));
    }

    [Fact]
    public void Apply_NullExceptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => UserExceptionPolicyEvaluator.Apply([], null!));
    }

    [Fact]
    public void UserExceptionPolicyEvaluator_IsNotPublic()
    {
        Assert.False(typeof(UserExceptionPolicyEvaluator).IsPublic);
    }
}
