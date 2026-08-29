using Privon.App;
using Privon.Detection;
using Privon.Storage;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3A -- CategoryPolicyEvaluator wired into the REAL ClipboardPrivacyProcessor
// chain (Detection -> Trust -> base CandidatePolicyEvaluator -> CategoryPolicyEvaluator ->
// AliasAssigner -> AliasReplacer), proving mixed-content behavior end to end rather than only at
// the pure-function level (see CategoryPolicyEvaluatorTests.cs for that). Same real-pipeline
// style already established in ClipboardPrivacyProcessorTests.cs. Synthetic data only.
public class ClipboardPrivacyProcessorCategoryPolicyTests
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");

    private static ClipboardTextSnapshot Snapshot(string text) =>
        new(SequenceNumber: 1, HasReliableSequence: true, Text: text);

    private static ClipboardPrivacyProcessor MakeProcessor(ProtectionCategorySettings settings) =>
        new(new FakeTrustExceptionProvider(), new FakeProtectionCategorySettingsProvider(settings));

    // ==================================================================
    // CAT-006 -- Phone OFF + Email ON, mixed synthetic content -> raw phone remains unchanged,
    // email becomes [이메일1]. Disabled Phone must not influence Email's own alias numbering.
    // ==================================================================
    [Fact]
    public void Cat006_PhoneOff_EmailOn_Mixed_PhoneRawRemains_EmailGetsAliasOne()
    {
        const string phone = "010-1234-5678";
        const string email = "user@example.test";
        var settings = ProtectionCategorySettings.AllOn with { PhoneEnabled = false };
        var processor = MakeProcessor(settings);

        var outcome = processor.Process(ChatGpt, Snapshot($"연락처 {phone}, 이메일 {email}"));
        var result = outcome.Result;

        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(1, result.ProtectCount); // email only
        Assert.Equal(1, result.BypassCount); // phone, category-filtered
        Assert.Equal(0, result.NeedsDecisionCount);
        Assert.NotNull(outcome.WritePlan);
        var text = outcome.WritePlan!.ReplacementText;

        Assert.Contains(phone, text); // raw phone text remains untouched
        Assert.DoesNotContain(email, text);
        Assert.Contains("[이메일1]", text); // never offset by the skipped phone candidate
    }

    // ==================================================================
    // CAT-007 -- Email OFF + Phone ON, mixed synthetic content -> raw email remains unchanged,
    // phone becomes [전화번호1] (existing, real alias token text -- see AliasLabelProvider).
    // ==================================================================
    [Fact]
    public void Cat007_EmailOff_PhoneOn_Mixed_EmailRawRemains_PhoneGetsAliasOne()
    {
        const string phone = "010-1234-5678";
        const string email = "user@example.test";
        var settings = ProtectionCategorySettings.AllOn with { EmailEnabled = false };
        var processor = MakeProcessor(settings);

        var outcome = processor.Process(ChatGpt, Snapshot($"연락처 {phone}, 이메일 {email}"));
        var result = outcome.Result;

        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(1, result.ProtectCount); // phone only
        Assert.Equal(1, result.BypassCount); // email, category-filtered
        Assert.Equal(0, result.NeedsDecisionCount);
        Assert.NotNull(outcome.WritePlan);
        var text = outcome.WritePlan!.ReplacementText;

        Assert.Contains(email, text); // raw email text remains untouched
        Assert.DoesNotContain(phone, text);
        Assert.Contains("[전화번호1]", text);
    }

    // ---- both OFF -> ALL_BYPASS -> neither plan produced ----
    [Fact]
    public void BothOff_Mixed_AllBypass_NoPlanProduced()
    {
        const string phone = "010-1234-5678";
        const string email = "user@example.test";
        var settings = new ProtectionCategorySettings(true, false, false, true, true);
        var processor = MakeProcessor(settings);

        var outcome = processor.Process(ChatGpt, Snapshot($"{phone} {email}"));
        var result = outcome.Result;

        Assert.Equal(2, result.BypassCount);
        Assert.Equal(0, result.ProtectCount);
        Assert.Null(outcome.WritePlan);
        Assert.Null(outcome.DecisionPlan);
    }

    // ==================================================================
    // CAT-009 (real-chain companion) -- ordinary category OFF (Phone) + a real Level3 candidate
    // (RRN) in the same attempt -> Level3 remains NeedsDecision, replacement stays blocked.
    // ==================================================================
    [Fact]
    public void Cat009_PhoneOff_PlusLevel3Rrn_Level3RemainsNeedsDecision_ReplacementBlocked()
    {
        const string phone = "010-1234-5678";
        const string rrn = "901231-1234567"; // synthetic, structurally-plausible RRN
        var settings = ProtectionCategorySettings.AllOn with { PhoneEnabled = false };
        var processor = MakeProcessor(settings);

        var outcome = processor.Process(ChatGpt, Snapshot($"{phone} {rrn}"));
        var result = outcome.Result;

        Assert.Equal(1, result.NeedsDecisionCount); // RRN
        Assert.Equal(1, result.BypassCount); // phone, category-filtered
        Assert.Equal(0, result.ProtectCount);
        Assert.Null(outcome.WritePlan); // NEEDSDECISION_BLOCKS_REPLACEMENT, unaffected by category filter
        Assert.NotNull(outcome.DecisionPlan);
        var item = Assert.Single(outcome.DecisionPlan!.Items);
        Assert.Equal(Privon.Core.RiskLevel.Level3, item.RiskLevel);
    }

    // ==================================================================
    // Provider wiring -- fresh load per attempt, no cache (mirrors trust/exception provider's own
    // NO_PII fast-path / load-count coverage in ClipboardPrivacyProcessorTests.cs).
    // ==================================================================
    [Fact]
    public void Process_NoPii_CategoryProviderNeverLoaded()
    {
        var categoryProvider = new FakeProtectionCategorySettingsProvider();
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider(), categoryProvider);

        processor.Process(ChatGpt, Snapshot("오늘 날씨가 좋네요"));

        Assert.Equal(0, categoryProvider.LoadCount);
    }

    [Fact]
    public void Process_PiiDetected_CategoryProviderLoadedExactlyOnce()
    {
        var categoryProvider = new FakeProtectionCategorySettingsProvider();
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider(), categoryProvider);

        processor.Process(ChatGpt, Snapshot("010-1234-5678"));

        Assert.Equal(1, categoryProvider.LoadCount);
    }

    // ---- no explicit category provider supplied (existing 1-arg/2-arg constructors) -> defaults
    // to AllOn -- proves every pre-existing ClipboardPrivacyProcessorTests.cs call site keeps its
    // exact prior behavior unchanged. ----
    [Fact]
    public void Constructor_NoExplicitCategoryProvider_DefaultsToAllOn_NoBehaviorChange()
    {
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider());

        var outcome = processor.Process(ChatGpt, Snapshot("010-1234-5678"));

        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.NotNull(outcome.WritePlan);
        Assert.Equal("[전화번호1]", outcome.WritePlan!.ReplacementText);
    }
}
