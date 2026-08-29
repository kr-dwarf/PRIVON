using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Storage;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3B -- UserExceptionPolicyEvaluator wired into the REAL ClipboardPrivacyProcessor
// chain (Detection -> Trust -> base CandidatePolicyEvaluator -> CategoryPolicyEvaluator ->
// UserExceptionPolicyEvaluator -> AliasAssigner -> AliasReplacer), proving mixed-content and
// category-interaction behavior end to end. Mirrors ClipboardPrivacyProcessorCategoryPolicyTests.cs.
// Synthetic data only.
public class ClipboardPrivacyProcessorUserExceptionTests
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");

    private static ClipboardTextSnapshot Snapshot(string text) =>
        new(SequenceNumber: 1, HasReliableSequence: true, Text: text);

    private static ClipboardPrivacyProcessor MakeProcessor(
        IReadOnlyList<UserExceptionValue> exceptions, ProtectionCategorySettings? categories = null) =>
        new(new FakeTrustExceptionProvider(),
            new FakeProtectionCategorySettingsProvider(categories ?? ProtectionCategorySettings.AllOn),
            new FakeUserExceptionProvider(exceptions));

    private static CanonicalValue DiscoverCanonical(string rawText, PiiType expectedType) =>
        Assert.Single(DetectionPipeline.CreateDefault().Detect(rawText).Candidates) is { PiiType: var t, Canonical: var c } && t == expectedType
            ? c
            : throw new InvalidOperationException("fixture did not produce the expected single candidate");

    // ==================================================================
    // EXC-001 -- empty exception set -> ordinary Phone/Email protection unchanged.
    // ==================================================================
    [Fact]
    public void Exc001_EmptyExceptionSet_OrdinaryProtectionUnchanged()
    {
        var processor = MakeProcessor([]);

        var outcome = processor.Process(ChatGpt, Snapshot("010-1234-5678"));

        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.NotNull(outcome.WritePlan);
        Assert.Equal("[전화번호1]", outcome.WritePlan!.ReplacementText);
    }

    // ==================================================================
    // EXC-004 -- format-equivalent Phone values through REAL detector canonicalization: the
    // exception is registered against the canonical value discovered from "010-1234-5678", but the
    // clipboard text contains "01012345678" (a different raw format of the SAME number) -> still
    // matches, proving identity follows Detection's own canonical form, not raw display text.
    // ==================================================================
    [Fact]
    public void Exc004_FormatEquivalentPhoneValues_MatchThroughRealCanonicalization()
    {
        var canonical = DiscoverCanonical("010-1234-5678", PiiType.Phone);
        var processor = MakeProcessor([new UserExceptionValue(PiiType.Phone, canonical)]);

        var outcome = processor.Process(ChatGpt, Snapshot("01012345678")); // different raw format, same number

        Assert.Equal(1, outcome.Result.BypassCount);
        Assert.Equal(0, outcome.Result.ProtectCount);
        Assert.Null(outcome.WritePlan); // ALL_BYPASS
    }

    // ==================================================================
    // EXC-005 -- category ON + exact exception -> only the exact value bypass.
    // ==================================================================
    [Fact]
    public void Exc005_CategoryOn_PlusException_OnlyExactValueBypass()
    {
        var canonicalA = DiscoverCanonical("010-1234-5678", PiiType.Phone);
        var processor = MakeProcessor(
            [new UserExceptionValue(PiiType.Phone, canonicalA)], ProtectionCategorySettings.AllOn);

        var outcome = processor.Process(ChatGpt, Snapshot("A: 010-1234-5678, B: 010-9999-8888"));

        Assert.Equal(1, outcome.Result.BypassCount);
        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.NotNull(outcome.WritePlan);
        var text = outcome.WritePlan!.ReplacementText;
        Assert.Contains("010-1234-5678", text); // excepted value raw
        Assert.Contains("[전화번호1]", text); // other value protected, numbered 1 (never offset)
        Assert.DoesNotContain("010-9999-8888", text);
    }

    // ==================================================================
    // EXC-006 -- category OFF + exception present -> outcome identical to category OFF without
    // the exception (category already bypasses everything; the exception stage has no additional
    // effect).
    // ==================================================================
    [Fact]
    public void Exc006_CategoryOff_PlusException_IdenticalToCategoryOffWithoutException()
    {
        var canonicalA = DiscoverCanonical("010-1234-5678", PiiType.Phone);
        var offCategories = ProtectionCategorySettings.AllOn with { PhoneEnabled = false };
        const string text = "A: 010-1234-5678, B: 010-9999-8888";

        var withException = MakeProcessor([new UserExceptionValue(PiiType.Phone, canonicalA)], offCategories)
            .Process(ChatGpt, Snapshot(text));
        var withoutException = MakeProcessor([], offCategories)
            .Process(ChatGpt, Snapshot(text));

        Assert.Equal(withoutException.Result.BypassCount, withException.Result.BypassCount);
        Assert.Equal(withoutException.Result.ProtectCount, withException.Result.ProtectCount);
        Assert.Equal(withoutException.WritePlan, withException.WritePlan);
        Assert.Null(withException.WritePlan); // ALL_BYPASS either way
    }

    // ==================================================================
    // EXC-007 -- category OFF -> ON: stored exact exception survives, other values resume Protect.
    // ==================================================================
    [Fact]
    public void Exc007_CategoryOffThenOn_ExceptionSurvives_OtherValuesResumeProtect()
    {
        var canonicalA = DiscoverCanonical("010-1234-5678", PiiType.Phone);
        var exceptions = new[] { new UserExceptionValue(PiiType.Phone, canonicalA) };
        const string text = "A: 010-1234-5678, B: 010-9999-8888";

        var whileOff = MakeProcessor(exceptions, ProtectionCategorySettings.AllOn with { PhoneEnabled = false })
            .Process(ChatGpt, Snapshot(text));
        Assert.Null(whileOff.WritePlan); // ALL_BYPASS while off

        var afterOn = MakeProcessor(exceptions, ProtectionCategorySettings.AllOn)
            .Process(ChatGpt, Snapshot(text));

        Assert.Equal(1, afterOn.Result.BypassCount); // A, exact exception
        Assert.Equal(1, afterOn.Result.ProtectCount); // B, resumed
        Assert.NotNull(afterOn.WritePlan);
        var replacedText = afterOn.WritePlan!.ReplacementText;
        Assert.Contains("010-1234-5678", replacedText);
        Assert.Contains("[전화번호1]", replacedText);
    }

    // ==================================================================
    // EXC-013 (real-chain companion) -- Level3 RRN whose canonical value happens to be present as
    // a stored exception entry for the SAME PiiType -> still NeedsDecision, replacement blocked.
    // ==================================================================
    [Fact]
    public void Exc013_Level3RrnMatchingStoredExceptionShapedEntry_StillNeedsDecision()
    {
        const string rrn = "901231-1234567"; // synthetic, structurally-plausible RRN
        var canonical = DiscoverCanonical(rrn, PiiType.ResidentRegistrationNumber);
        var processor = MakeProcessor([new UserExceptionValue(PiiType.ResidentRegistrationNumber, canonical)]);

        var outcome = processor.Process(ChatGpt, Snapshot(rrn));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan);
        Assert.NotNull(outcome.DecisionPlan);
        var item = Assert.Single(outcome.DecisionPlan!.Items);
        Assert.Equal(RiskLevel.Level3, item.RiskLevel);
    }

    // ---- Secret (Level3) companion ----
    [Fact]
    public void Exc013_Level3SecretMatchingStoredExceptionShapedEntry_StillNeedsDecision()
    {
        const string secret = "Bearer sk-synthetic-0123456789abcdef0123456789";
        var canonical = DiscoverCanonical(secret, PiiType.Secret);
        var processor = MakeProcessor([new UserExceptionValue(PiiType.Secret, canonical)]);

        var outcome = processor.Process(ChatGpt, Snapshot(secret));

        Assert.Equal(1, outcome.Result.NeedsDecisionCount);
        Assert.Equal(0, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan);
    }

    // ==================================================================
    // EXC-014 -- excepted Phone A + protected Phone B + protected Email -> A raw, B [전화번호1],
    // Email [이메일1]. Exception candidate does not consume alias numbering.
    // ==================================================================
    [Fact]
    public void Exc014_MixedExceptedPhoneA_ProtectedPhoneB_ProtectedEmail_AliasNumberingUnaffected()
    {
        var canonicalA = DiscoverCanonical("010-1234-5678", PiiType.Phone);
        var processor = MakeProcessor([new UserExceptionValue(PiiType.Phone, canonicalA)]);

        var outcome = processor.Process(ChatGpt, Snapshot("A: 010-1234-5678, B: 010-9999-8888, C: user@example.test"));
        var result = outcome.Result;

        Assert.Equal(3, result.CandidateCount);
        Assert.Equal(1, result.BypassCount); // A
        Assert.Equal(2, result.ProtectCount); // B, Email
        Assert.NotNull(outcome.WritePlan);
        var text = outcome.WritePlan!.ReplacementText;

        Assert.Contains("010-1234-5678", text); // A raw
        Assert.Contains("[전화번호1]", text); // B -- never offset to 2
        Assert.Contains("[이메일1]", text); // Email -- never offset
        Assert.DoesNotContain("010-9999-8888", text);
        Assert.DoesNotContain("user@example.test", text);
    }

    // ==================================================================
    // EXC-015 (real-chain) -- same excepted canonical value appears twice -> both occurrences
    // bypass, BypassCount reflects both.
    // ==================================================================
    [Fact]
    public void Exc015_SameExceptedValueTwice_BothOccurrencesBypass_BypassCountReflectsBoth()
    {
        var canonical = DiscoverCanonical("010-1234-5678", PiiType.Phone);
        var processor = MakeProcessor([new UserExceptionValue(PiiType.Phone, canonical)]);

        var outcome = processor.Process(ChatGpt, Snapshot("010-1234-5678 그리고 다시 010-1234-5678"));

        Assert.Equal(2, outcome.Result.CandidateCount);
        Assert.Equal(2, outcome.Result.BypassCount);
        Assert.Equal(0, outcome.Result.ProtectCount);
        Assert.Null(outcome.WritePlan); // ALL_BYPASS
    }

    // ==================================================================
    // EXC-016 (real-chain) -- no exception value appears in outcome diagnostics.
    // ==================================================================
    [Fact]
    public void Exc016_ExceptionValue_NeverAppearsInOutcomeDiagnostics()
    {
        const string sentinelPhone = "010-1234-5678";
        var canonical = DiscoverCanonical(sentinelPhone, PiiType.Phone);
        var processor = MakeProcessor([new UserExceptionValue(PiiType.Phone, canonical)]);

        var outcome = processor.Process(ChatGpt, Snapshot($"별도텍스트 {sentinelPhone}"));

        Assert.DoesNotContain(sentinelPhone, outcome.ToString());
        Assert.DoesNotContain(sentinelPhone, $"{outcome}");
    }

    // ==================================================================
    // TRUST_REGRESSION -- a UserException match must never be counted as TrustedCount (that
    // metric belongs exclusively to ExceptionTrustedEvaluator's own Level1/Level2 Trust concept).
    // ==================================================================
    [Fact]
    public void UserExceptionMatch_NeverCountedAsTrustedCount()
    {
        var canonical = DiscoverCanonical("010-1234-5678", PiiType.Phone);
        var processor = MakeProcessor([new UserExceptionValue(PiiType.Phone, canonical)]);

        var outcome = processor.Process(ChatGpt, Snapshot("010-1234-5678"));

        Assert.Equal(0, outcome.Result.TrustedCount); // still zero -- Bypass here came from UserException, not Trust
        Assert.Equal(1, outcome.Result.BypassCount);
    }

    // ==================================================================
    // Provider wiring -- fresh load per attempt, no cache.
    // ==================================================================
    [Fact]
    public void Process_NoPii_UserExceptionProviderNeverLoaded()
    {
        var provider = new FakeUserExceptionProvider();
        var processor = new ClipboardPrivacyProcessor(
            new FakeTrustExceptionProvider(), new FakeProtectionCategorySettingsProvider(), provider);

        processor.Process(ChatGpt, Snapshot("오늘 날씨가 좋네요"));

        Assert.Equal(0, provider.LoadCount);
    }

    [Fact]
    public void Process_PiiDetected_UserExceptionProviderLoadedExactlyOnce()
    {
        var provider = new FakeUserExceptionProvider();
        var processor = new ClipboardPrivacyProcessor(
            new FakeTrustExceptionProvider(), new FakeProtectionCategorySettingsProvider(), provider);

        processor.Process(ChatGpt, Snapshot("010-1234-5678"));

        Assert.Equal(1, provider.LoadCount);
    }

    // ---- no explicit user-exception provider supplied -> defaults to empty -> no behavior change
    // for every pre-existing call site ----
    [Fact]
    public void Constructor_NoExplicitUserExceptionProvider_DefaultsToEmpty_NoBehaviorChange()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot("010-1234-5678"));

        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.NotNull(outcome.WritePlan);
        Assert.Equal("[전화번호1]", outcome.WritePlan!.ReplacementText);
    }
}
