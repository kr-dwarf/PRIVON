using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Storage;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3A -- CategoryPolicyEvaluator regression. Pure-function unit tests using
// hand-constructed CandidatePolicyDecision fixtures (never running the real DetectionPipeline) --
// mixed-content coverage through the REAL processor chain lives in
// ClipboardPrivacyProcessorCategoryPolicyTests.cs instead. Synthetic data only.
public class CategoryPolicyEvaluatorTests
{
    private static CandidatePolicyDecision Decision(
        PiiType type, RiskLevel level, CandidateDisposition disposition, int start = 0, string value = "SYNTHETIC") =>
        new(
            new EvaluatedCandidate(
                new DetectionCandidate(type, new RawSpan(start, 5), level, DetectionConfidence.High,
                    new CanonicalValue(type, value), DetectorName: "FakeDetector"),
                TrustState.Untrusted),
            disposition);

    // ==================================================================
    // CAT-004 -- Phone OFF -> Phone Protect becomes Bypass; Email unchanged.
    // ==================================================================
    [Fact]
    public void Cat004_PhoneOff_PhoneProtectBecomesBypass_EmailUnchanged()
    {
        var settings = ProtectionCategorySettings.AllOn with { PhoneEnabled = false };
        var phone = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect, start: 0);
        var email = Decision(PiiType.Email, RiskLevel.Level2, CandidateDisposition.Protect, start: 10);

        var result = CategoryPolicyEvaluator.Apply([phone, email], settings);

        Assert.Equal(CandidateDisposition.Bypass, result[0].Disposition);
        Assert.Equal(CandidateDisposition.Protect, result[1].Disposition);
    }

    // ==================================================================
    // CAT-005 -- Email OFF -> Email Protect becomes Bypass; Phone unchanged.
    // ==================================================================
    [Fact]
    public void Cat005_EmailOff_EmailProtectBecomesBypass_PhoneUnchanged()
    {
        var settings = ProtectionCategorySettings.AllOn with { EmailEnabled = false };
        var phone = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect, start: 0);
        var email = Decision(PiiType.Email, RiskLevel.Level2, CandidateDisposition.Protect, start: 10);

        var result = CategoryPolicyEvaluator.Apply([phone, email], settings);

        Assert.Equal(CandidateDisposition.Protect, result[0].Disposition);
        Assert.Equal(CandidateDisposition.Bypass, result[1].Disposition);
    }

    // ==================================================================
    // CAT-008 -- OFF then ON (two independent, sequential Apply calls sharing the same input
    // decisions) -> base protection resumes; the evaluator carries no state between calls.
    // ==================================================================
    [Fact]
    public void Cat008_OffThenOn_BaseProtectionResumes_EvaluatorIsStateless()
    {
        var phone = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect);

        var off = CategoryPolicyEvaluator.Apply([phone], ProtectionCategorySettings.AllOn with { PhoneEnabled = false });
        var on = CategoryPolicyEvaluator.Apply([phone], ProtectionCategorySettings.AllOn);

        Assert.Equal(CandidateDisposition.Bypass, off[0].Disposition);
        Assert.Equal(CandidateDisposition.Protect, on[0].Disposition);
        // The SAME input decision, unmutated -- Apply never mutates its input, only ever returns
        // a new list (records are immutable, `with` produces a new instance).
        Assert.Equal(CandidateDisposition.Protect, phone.Disposition);
    }

    // ==================================================================
    // CAT-009 -- ordinary category OFF + a Level3 candidate in the same batch -> Level3 remains
    // NeedsDecision, completely untouched by the category filter. Explicit lock, not incidental:
    // CategoryPolicyEvaluator only ever transforms entries whose Disposition is ALREADY Protect --
    // Level3 is forced to NeedsDecision upstream by CandidatePolicyEvaluator (never reached here as
    // Protect), so this also proves the guard holds even though Level3 is structurally
    // unreachable as Protect today.
    // ==================================================================
    [Fact]
    public void Cat009_OrdinaryCategoryOff_PlusLevel3Candidate_Level3RemainsNeedsDecisionUnchanged()
    {
        var settings = ProtectionCategorySettings.AllOn with { PhoneEnabled = false, EmailEnabled = false };
        var phone = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect, start: 0);
        var secret = Decision(PiiType.Secret, RiskLevel.Level3, CandidateDisposition.NeedsDecision, start: 10);

        var result = CategoryPolicyEvaluator.Apply([phone, secret], settings);

        Assert.Equal(CandidateDisposition.Bypass, result[0].Disposition);
        Assert.Equal(CandidateDisposition.NeedsDecision, result[1].Disposition);
        Assert.Same(secret, result[1]); // untouched -- same reference, not merely equal
    }

    // ---- companion: a Level3 candidate that (hypothetically) arrived as Protect must still never
    // be weakened -- CategoryPolicyEvaluator's own guard is "Disposition != Protect -> untouched",
    // so this proves the ONLY transformable state is Protect regardless of RiskLevel; a genuine
    // production Level3 candidate can never actually be Protect (CandidatePolicyEvaluator's own
    // defense-in-depth), but the guard here does not rely on that upstream invariant holding. ----
    [Fact]
    public void Cat009_NeedsDecisionAndBypass_NeverAlteredByCategoryFilter_RegardlessOfPiiType()
    {
        var settings = new ProtectionCategorySettings(false, false, false, false, false); // all OFF
        var needsDecision = Decision(PiiType.Phone, RiskLevel.Level1, CandidateDisposition.NeedsDecision, start: 0);
        var bypass = Decision(PiiType.Email, RiskLevel.Level1, CandidateDisposition.Bypass, start: 10);

        var result = CategoryPolicyEvaluator.Apply([needsDecision, bypass], settings);

        Assert.Same(needsDecision, result[0]);
        Assert.Same(bypass, result[1]);
    }

    // ==================================================================
    // CAT-011 -- existing IpAddress/MacAddress/GpsCoordinate base behavior unchanged: none of the
    // 5 category toggles maps to them -- they are never transformed by CategoryPolicyEvaluator
    // regardless of settings.
    // ==================================================================
    [Fact]
    public void Cat011_IpMacGps_NeverTransformed_RegardlessOfCategorySettings()
    {
        var settings = new ProtectionCategorySettings(false, false, false, false, false); // all OFF
        var ip = Decision(PiiType.IpAddress, RiskLevel.Level2, CandidateDisposition.Protect, start: 0);
        var mac = Decision(PiiType.MacAddress, RiskLevel.Level2, CandidateDisposition.Protect, start: 10);
        var gps = Decision(PiiType.GpsCoordinate, RiskLevel.Level2, CandidateDisposition.Protect, start: 20);

        var result = CategoryPolicyEvaluator.Apply([ip, mac, gps], settings);

        Assert.All(result, d => Assert.Equal(CandidateDisposition.Protect, d.Disposition));
    }

    // ==================================================================
    // Name/Address/Company -- no PiiType mapping exists; a Protect candidate of ANY currently-real
    // PiiType is never affected by those 3 booleans (there is nothing for them to gate yet). No
    // fabricated Name/Address/Company PiiType is used anywhere in this file.
    // ==================================================================
    [Fact]
    public void NameAddressCompanyToggles_HaveNoEffect_OnAnyCurrentlyRealPiiType()
    {
        var allDisabledExceptPhoneEmail = new ProtectionCategorySettings(
            NameEnabled: false, PhoneEnabled: true, EmailEnabled: true, AddressEnabled: false, CompanyEnabled: false);
        var phone = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect, start: 0);
        var email = Decision(PiiType.Email, RiskLevel.Level2, CandidateDisposition.Protect, start: 10);

        var result = CategoryPolicyEvaluator.Apply([phone, email], allDisabledExceptPhoneEmail);

        Assert.All(result, d => Assert.Equal(CandidateDisposition.Protect, d.Disposition));
    }

    // ---- empty input -> empty output, no exception ----
    [Fact]
    public void Apply_EmptyDecisionList_ReturnsEmptyList()
    {
        var result = CategoryPolicyEvaluator.Apply([], ProtectionCategorySettings.AllOn);

        Assert.Empty(result);
    }

    // ---- null guards ----
    [Fact]
    public void Apply_NullDecisions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CategoryPolicyEvaluator.Apply(null!, ProtectionCategorySettings.AllOn));
    }

    [Fact]
    public void Apply_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CategoryPolicyEvaluator.Apply([], null!));
    }

    // ==================================================================
    // CAT-012 (partial -- structural) -- CategoryPolicyEvaluator's own output never introduces a
    // raw/canonical value that was not already present in the input decision (it only ever
    // reuses/re-wraps the SAME EvaluatedCandidate, never constructs a new one from a string).
    // ==================================================================
    [Fact]
    public void Apply_NeverConstructsANewEvaluatedCandidate_OnlyReusesOrWrapsTheInputOne()
    {
        var phone = Decision(PiiType.Phone, RiskLevel.Level2, CandidateDisposition.Protect);

        var offResult = CategoryPolicyEvaluator.Apply([phone], ProtectionCategorySettings.AllOn with { PhoneEnabled = false });
        var onResult = CategoryPolicyEvaluator.Apply([phone], ProtectionCategorySettings.AllOn);

        Assert.Same(phone.Candidate, offResult[0].Candidate);
        Assert.Same(phone.Candidate, onResult[0].Candidate);
    }

    // ---- accessibility -- App-internal orchestration type, never public ----
    [Fact]
    public void CategoryPolicyEvaluator_IsNotPublic()
    {
        Assert.False(typeof(CategoryPolicyEvaluator).IsPublic);
    }
}
