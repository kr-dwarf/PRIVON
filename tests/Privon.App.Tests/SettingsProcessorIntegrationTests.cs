using Privon.App;
using Privon.Detection;
using Privon.Storage;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3C -- end-to-end proof that a mutation made through the NEW Settings mutation
// services (ProtectionCategorySettingsService/UserExceptionService) is observed by the very next
// REAL ClipboardPrivacyProcessor attempt, through the SAME PrivonLocalStore instance a real
// PrivonAppComposition would share between them (SOURCE_OF_TRUTH). Mirrors the exact real-pipeline
// style already established in ClipboardPrivacyProcessorCategoryPolicyTests.cs/
// ClipboardPrivacyProcessorUserExceptionTests.cs -- this file is the Settings-mutation-side
// companion to those Gate 3A/3B provider-side tests. Synthetic data only.
public class SettingsProcessorIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonSettingsProcessorIntegrationTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");

    private static ClipboardTextSnapshot Snapshot(string text) =>
        new(SequenceNumber: 1, HasReliableSequence: true, Text: text);

    private sealed class Fixture
    {
        public required PrivonLocalStore Store { get; init; }
        public required ProtectionCategorySettingsService CategoryService { get; init; }
        public required UserExceptionService ExceptionService { get; init; }
        public required ClipboardPrivacyProcessor Processor { get; init; }
    }

    private Fixture CreateFixture()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var categoryProvider = new ProtectionCategorySettingsProvider(store);
        var exceptionProvider = new UserExceptionProvider(store);
        var processor = new ClipboardPrivacyProcessor(new FakeTrustExceptionProvider(), categoryProvider, exceptionProvider);

        return new Fixture
        {
            Store = store,
            CategoryService = new ProtectionCategorySettingsService(store),
            ExceptionService = new UserExceptionService(store),
            Processor = processor,
        };
    }

    // ==================================================================
    // UI-003 -- Phone OFF persists -> next real processor attempt bypasses Phone.
    // ==================================================================
    [Fact]
    public void PhoneDisabledViaService_NextProcessorAttempt_BypassesPhone()
    {
        var f = CreateFixture();
        const string phone = "010-1234-5678";

        f.CategoryService.SetPhoneEnabled(false);
        var outcome = f.Processor.Process(ChatGpt, Snapshot(phone));

        Assert.Equal(1, outcome.Result.BypassCount);
        Assert.Equal(0, outcome.Result.ProtectCount);
        Assert.Null(outcome.WritePlan);
    }

    // ==================================================================
    // UI-004 -- Phone ON (after having been OFF) -> protection resumes.
    // ==================================================================
    [Fact]
    public void PhoneReenabledViaService_NextProcessorAttempt_ProtectionResumes()
    {
        var f = CreateFixture();
        const string phone = "010-1234-5678";
        f.CategoryService.SetPhoneEnabled(false);
        f.Processor.Process(ChatGpt, Snapshot(phone)); // confirm it was off

        f.CategoryService.SetPhoneEnabled(true);
        var outcome = f.Processor.Process(ChatGpt, Snapshot(phone));

        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.NotNull(outcome.WritePlan);
        Assert.Equal("[전화번호1]", outcome.WritePlan!.ReplacementText);
    }

    // ==================================================================
    // UI-005 -- Email OFF/ON -> corresponding real processor behavior.
    // ==================================================================
    [Fact]
    public void EmailDisabledViaService_NextProcessorAttempt_BypassesEmail()
    {
        var f = CreateFixture();
        const string email = "user@example.test";

        f.CategoryService.SetEmailEnabled(false);
        var outcome = f.Processor.Process(ChatGpt, Snapshot(email));

        Assert.Equal(1, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan);
    }

    [Fact]
    public void EmailReenabledViaService_NextProcessorAttempt_ProtectionResumes()
    {
        var f = CreateFixture();
        const string email = "user@example.test";
        f.CategoryService.SetEmailEnabled(false);
        f.Processor.Process(ChatGpt, Snapshot(email));

        f.CategoryService.SetEmailEnabled(true);
        var outcome = f.Processor.Process(ChatGpt, Snapshot(email));

        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.NotNull(outcome.WritePlan);
    }

    // ==================================================================
    // UI-006 companion -- Reset protection scope resumes protection for every category.
    // ==================================================================
    [Fact]
    public void ResetProtectionScope_AfterBothDisabled_NextAttemptProtectsBoth()
    {
        var f = CreateFixture();
        const string phone = "010-1234-5678";
        const string email = "user@example.test";
        f.CategoryService.SetPhoneEnabled(false);
        f.CategoryService.SetEmailEnabled(false);

        f.CategoryService.Reset();
        var outcome = f.Processor.Process(ChatGpt, Snapshot($"{phone} {email}"));

        Assert.Equal(2, outcome.Result.ProtectCount);
        Assert.Equal(0, outcome.Result.BypassCount);
    }

    // ==================================================================
    // UI-008 -- valid synthetic Phone input, added via the mutation service using its OWN real
    // Detection-canonicalized value (mirroring exactly what SettingsCoordinator does) -> next real
    // processor attempt bypasses the exact value.
    // ==================================================================
    [Fact]
    public void PhoneExceptionAddedViaService_NextProcessorAttempt_BypassesExactValue()
    {
        var f = CreateFixture();
        const string phone = "010-1234-5678";
        var candidate = Assert.Single(DetectionPipeline.CreateDefault().Detect(phone).Candidates);

        f.ExceptionService.Add(candidate.PiiType, candidate.Canonical);
        var outcome = f.Processor.Process(ChatGpt, Snapshot(phone));

        Assert.Equal(1, outcome.Result.BypassCount);
        Assert.Equal(0, outcome.Result.ProtectCount);
        Assert.Null(outcome.WritePlan);
    }

    // ==================================================================
    // UI-009 -- same for Email.
    // ==================================================================
    [Fact]
    public void EmailExceptionAddedViaService_NextProcessorAttempt_BypassesExactValue()
    {
        var f = CreateFixture();
        const string email = "user@example.test";
        var candidate = Assert.Single(DetectionPipeline.CreateDefault().Detect(email).Candidates);

        f.ExceptionService.Add(candidate.PiiType, candidate.Canonical);
        var outcome = f.Processor.Process(ChatGpt, Snapshot(email));

        Assert.Equal(1, outcome.Result.BypassCount);
        Assert.Null(outcome.WritePlan);
    }

    // ---- a DIFFERENT phone number in the same attempt remains protected -- the exception is
    // value-specific, not category-wide. ----
    [Fact]
    public void PhoneExceptionAddedForOneValue_DifferentValueInSameAttempt_StillProtected()
    {
        var f = CreateFixture();
        const string exceptedPhone = "010-1234-5678";
        const string otherPhone = "010-9999-0000";
        var candidate = Assert.Single(DetectionPipeline.CreateDefault().Detect(exceptedPhone).Candidates);
        f.ExceptionService.Add(candidate.PiiType, candidate.Canonical);

        var outcome = f.Processor.Process(ChatGpt, Snapshot($"{exceptedPhone} {otherPhone}"));

        Assert.Equal(1, outcome.Result.BypassCount); // exceptedPhone
        Assert.Equal(1, outcome.Result.ProtectCount); // otherPhone
        Assert.NotNull(outcome.WritePlan);
        Assert.Contains(exceptedPhone, outcome.WritePlan!.ReplacementText); // raw, unmasked
        Assert.DoesNotContain(otherPhone, outcome.WritePlan!.ReplacementText); // masked
    }

    // ==================================================================
    // UI-011 -- Delete -> next real processor attempt resumes protection.
    // ==================================================================
    [Fact]
    public void DeleteException_NextProcessorAttempt_ProtectionResumes()
    {
        var f = CreateFixture();
        const string phone = "010-1234-5678";
        var candidate = Assert.Single(DetectionPipeline.CreateDefault().Detect(phone).Candidates);
        f.ExceptionService.Add(candidate.PiiType, candidate.Canonical);
        f.Processor.Process(ChatGpt, Snapshot(phone)); // confirm bypassed first

        f.ExceptionService.Delete(candidate.PiiType, candidate.Canonical);
        var outcome = f.Processor.Process(ChatGpt, Snapshot(phone));

        Assert.Equal(1, outcome.Result.ProtectCount);
        Assert.NotNull(outcome.WritePlan);
    }

    // ==================================================================
    // UI-012 -- Reset exceptions -> next real processor attempt resumes protection for every
    // previously-excepted value.
    // ==================================================================
    [Fact]
    public void ResetExceptions_NextProcessorAttempt_ProtectionResumesForEveryValue()
    {
        var f = CreateFixture();
        const string phone = "010-1234-5678";
        const string email = "user@example.test";
        var phoneCandidate = Assert.Single(DetectionPipeline.CreateDefault().Detect(phone).Candidates);
        var emailCandidate = Assert.Single(DetectionPipeline.CreateDefault().Detect(email).Candidates);
        f.ExceptionService.Add(phoneCandidate.PiiType, phoneCandidate.Canonical);
        f.ExceptionService.Add(emailCandidate.PiiType, emailCandidate.Canonical);

        f.ExceptionService.Reset();
        var outcome = f.Processor.Process(ChatGpt, Snapshot($"{phone} {email}"));

        Assert.Equal(2, outcome.Result.ProtectCount);
        Assert.Equal(0, outcome.Result.BypassCount);
    }
}
