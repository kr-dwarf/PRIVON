using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.0 Gate 1B -- minimal pipeline-level guard coverage for the two new snapshot facts,
// now that IForegroundTargetSource/FakeForegroundTargetSource carry them. Deliberately scoped to
// ClipboardChangeMonitor's guarded READ only (CHECK1 + one CHECK2 case) rather than duplicating the
// full existing ClipboardChangeMonitorForegroundGuardTests/ComposerTextReader matrices -- both
// guards share the exact same ForegroundIdentityCapture.Matches primitive
// (Gate0D_ExecutableSignatureFactModelTests already locks that comparison directly at the unit
// level), so this file only needs to prove the widened facts actually reach the guard through the
// real pipeline wiring, not re-prove the comparison itself.
public class Gate1B_PipelineSignerGuardTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private const uint DefaultSequence = 5;

    // Matches FakeForegroundTargetSource's own defaults (PID 4242, "ChatGPT", NotInspected/null
    // signer facts) -- a guarded call using this target matches "out of the box" unless a test
    // deliberately reconfigures the fake or the expected target.
    private static readonly ForegroundTargetSnapshot DefaultExpectedTarget =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");

    private static (ClipboardChangeMonitor Monitor, FakeClipboardTextNative TextNative, FakeForegroundTargetSource ForegroundSource) CreateStarted()
    {
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        textNative.SetUnicodeTextPayload("ORIGINAL-CLIPBOARD-TEXT");
        var foregroundSource = new FakeForegroundTargetSource();
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative, foregroundSource: foregroundSource);
        monitor.Start();
        return (monitor, textNative, foregroundSource);
    }

    // ---- CHECK1: expected ExecutableSignature=Trusted, current (fake default) stays NotInspected
    // -> TargetChanged, no raw clipboard content API ever invoked. ----
    [Fact]
    public async Task GuardedRead_ExecutableSignatureDiffersAtCheck1_ReturnsTargetChanged_NoOpenClipboard()
    {
        var (monitor, textNative, _) = CreateStarted();
        var expectedTarget = DefaultExpectedTarget with { ExecutableSignature = ExecutableSignatureResolution.Trusted };

        var result = await monitor.ReadTextSnapshotAsync(expectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGetUnicodeTextHandle), textNative.CallLog);
    }

    // ---- CHECK1: expected and current both Trusted (so ExecutableSignature agrees), but the
    // organization differs ("Anthropic, PBC" vs "Google LLC") -- isolates the organization
    // comparison specifically, since a real Trusted result never carries a null organization (see
    // Win32ExecutableSignatureVerifier's own contract). -> TargetChanged, no raw clipboard content
    // API ever invoked. ----
    [Fact]
    public async Task GuardedRead_SignerOrganizationDiffersAtCheck1_ReturnsTargetChanged_NoOpenClipboard()
    {
        var (monitor, textNative, foregroundSource) = CreateStarted();
        foregroundSource.ExecutableSignatureValue = ExecutableSignatureResolution.Trusted;
        foregroundSource.SignerOrganizationValue = "Google LLC";

        var expectedTarget = DefaultExpectedTarget with
        {
            ExecutableSignature = ExecutableSignatureResolution.Trusted,
            SignerOrganization = "Anthropic, PBC",
        };

        var result = await monitor.ReadTextSnapshotAsync(expectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGetUnicodeTextHandle), textNative.CallLog);
    }

    // ---- CHECK1 passes (both sides at Trusted/"Anthropic, PBC"), CHECK2's ExecutableSignature then
    // differs (e.g. the process was replaced mid-attempt) -> TargetChanged, no raw content copy --
    // mirrors ClipboardChangeMonitorForegroundGuardTests's own existing CHECK1-vs-CHECK2 pattern. ----
    [Fact]
    public async Task GuardedRead_Check1Passes_Check2ExecutableSignatureDiffers_NoRawContentCopy()
    {
        var (monitor, textNative, foregroundSource) = CreateStarted();
        textNative.SetUnicodeTextPayload("must never be copied");
        foregroundSource.ExecutableSignatureValue = ExecutableSignatureResolution.Trusted;
        foregroundSource.SignerOrganizationValue = "Anthropic, PBC";
        foregroundSource.ChangeAfterAttempt = 1;
        foregroundSource.ExecutableSignatureValueAfter = ExecutableSignatureResolution.Untrusted;
        foregroundSource.SignerOrganizationValueAfter = null;

        var expectedTarget = DefaultExpectedTarget with
        {
            ExecutableSignature = ExecutableSignatureResolution.Trusted,
            SignerOrganization = "Anthropic, PBC",
        };

        var result = await monitor.ReadTextSnapshotAsync(expectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.TryGetUnicodeTextHandle), textNative.CallLog);
        Assert.Contains(nameof(FakeClipboardTextNative.CloseClipboard), textNative.CallLog);
    }

    // ---- Existing BUG004 protections must remain unaffected: an expected/current pair that agrees
    // on every fact -- including the two new ones -- still succeeds. ----
    [Fact]
    public async Task GuardedRead_SignerFactsAgreeOnBothSides_StillSucceeds()
    {
        var (monitor, _, foregroundSource) = CreateStarted();
        foregroundSource.ExecutableSignatureValue = ExecutableSignatureResolution.Trusted;
        foregroundSource.SignerOrganizationValue = "Anthropic, PBC";

        var expectedTarget = DefaultExpectedTarget with
        {
            ExecutableSignature = ExecutableSignatureResolution.Trusted,
            SignerOrganization = "Anthropic, PBC",
        };

        var result = await monitor.ReadTextSnapshotAsync(expectedTarget).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
        Assert.NotNull(result.Snapshot);
    }
}
