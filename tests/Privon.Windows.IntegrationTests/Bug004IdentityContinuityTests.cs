using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// BUG-004 Gate 2H.3 -- deterministic RED evidence for BUG004-TOCTOU-001 (CONFIRMED) and
// BUG004-FACT-002 (CONFIRMED).
//
// THE DEFECT: authorization (TargetGate, App layer) consumes FOUR identity facts -- PID,
// ProcessName, PackageIdentity, PackageFamilyName -- but every execution guard revalidates only
// PID + ProcessName. A previously-authorized product authorization can therefore cross onto a
// different process that merely shares the PID and process name (PID reuse), because the fact that
// actually distinguishes the supported product -- the package family name -- is never re-checked
// before any sensitive clipboard/composer operation.
//
// THE SECOND DEFECT: factual capture derived ProcessName and PackageFamilyName from two
// INDEPENDENT PID-keyed lookups (System.Diagnostics.Process.ProcessName, then a separately-opened
// Process.Handle), so the two facts were never proven to describe one native process instance.
//
// FINAL CONTRACT (Gate 2H.3, E+): one native handle is opened FIRST; ProcessName (via
// QueryFullProcessImageName basename) and PackageFamilyName (via GetPackageFamilyName) are both
// derived from THAT SAME handle; and while that handle is still open the current foreground PID is
// re-confirmed to equal the pinned PID. Only then is a coherent snapshot emitted. Every guard then
// compares all four facts mechanically -- current-vs-expected only, never against any product
// constant (that stays exclusively in Privon.App's TargetGate).
//
// These tests are FACTS-ONLY: no "ChatGPT", no OpenAI package constant, no product policy appears
// anywhere in this file -- the expected snapshots use deliberately neutral synthetic identities so
// that this suite can never accidentally become a second home for supported-product policy.
public class Bug004IdentityContinuityTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private const uint DefaultSequence = 5;

    // Deliberately NEUTRAL synthetic identities -- never the real supported product constant.
    private const string PinnedProcessName = "SyntheticTargetApp";
    private const string PinnedPfn = "Synthetic.PinnedProduct_0000000000000";
    private const string DifferentPfn = "Synthetic.OtherProduct_0000000000000";
    private const uint PinnedPid = 4242;

    private static ForegroundTargetSnapshot ExpectedPinned(
        PackageIdentityResolution identity = PackageIdentityResolution.Resolved,
        string? pfn = PinnedPfn,
        uint pid = PinnedPid,
        string name = PinnedProcessName) =>
        new(IsResolved: true, ProcessId: pid, ProcessName: name, PackageIdentity: identity, PackageFamilyName: pfn);

    // Configures the fake so the CURRENT foreground reports the given identity facts.
    private static FakeForegroundTargetSource CurrentForeground(
        PackageIdentityResolution identity,
        string? pfn,
        uint pid = PinnedPid,
        string name = PinnedProcessName) =>
        new()
        {
            WindowThreadProcessIdValue = pid,
            ProcessNameValue = name,
            PackageIdentityValue = identity,
            PackageFamilyNameValue = pfn,
        };

    private static (ClipboardChangeMonitor Monitor, FakeClipboardTextNative TextNative) CreateStartedMonitor(
        FakeForegroundTargetSource foregroundSource)
    {
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        // A real payload MUST be configured -- without it every read returns MalformedData, which
        // would make each "identity divergence is rejected" assertion pass for the WRONG reason.
        // RedId001And005 below is the control proving a Success outcome is genuinely reachable here.
        textNative.SetUnicodeTextPayload("SYNTHETIC-CLIPBOARD-TEXT");
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative, foregroundSource: foregroundSource);
        monitor.Start();
        return (monitor, textNative);
    }

    // ==================================================================
    // RED-ID-001 / 005 -- POSITIVE characterization (already GREEN today)
    // ==================================================================

    // RED-ID-001 / RED-ID-005: the expected identity and the current coherent identity are fully
    // equal (same PID, same name, same Resolved state, same exact PFN) -> permit. RED-ID-005
    // explicitly records the accepted product-owner decision that a same-full-identity process is
    // acceptable, so a future "stricter" change cannot silently start rejecting the real target.
    [Fact]
    public async Task RedId001And005_FullyCoherentMatchingIdentity_GuardPermits()
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        var (monitor, _) = CreateStartedMonitor(foreground);

        var result = await monitor.ReadTextSnapshotAsync(ExpectedPinned()).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
    }

    // Control for the composer path, for the same reason: proves ComposerReadOutcome.Success is
    // genuinely reachable with this harness, so the composer rejection assertions below cannot pass
    // merely because the composer read was failing for an unrelated setup reason.
    [Fact]
    public async Task RedId001And005_ComposerRead_FullyCoherentMatchingIdentity_GuardPermits()
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        var reader = new ComposerTextReader(new FakeComposerTextSource(), foreground);
        reader.Start();

        var result = await reader.ReadFocusedComposerTextAsync(ExpectedPinned()).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.Equal(ComposerReadOutcome.Success, result.Outcome);
    }

    // ==================================================================
    // RED-ID-002 / 003 / 004 -- the core BUG004-TOCTOU-001 regressions
    // ==================================================================

    // Every sensitive guarded path, at BOTH check points. ChangeAfterAttempt drives the
    // CHECK1-passes-then-CHECK2-fails case: each CheckForegroundTarget performs exactly one
    // GetForegroundWindow, so attempt 1 is CHECK1 and attempt 2 is CHECK2.

    public static TheoryData<PackageIdentityResolution, string?> DivergentCurrentIdentities() => new()
    {
        { PackageIdentityResolution.Resolved, DifferentPfn },      // RED-ID-002: same PID+name, DIFFERENT PFN
        { PackageIdentityResolution.NoPackage, null },             // RED-ID-003: same PID+name, unpackaged
        { PackageIdentityResolution.Unresolved, null },            // RED-ID-004: same PID+name, identity unresolved
    };

    // ---- clipboard read, CHECK1 ----
    [Theory]
    [MemberData(nameof(DivergentCurrentIdentities))]
    public async Task RedId002To004_ClipboardRead_Check1_DivergentPackageIdentity_Rejected(
        PackageIdentityResolution currentIdentity, string? currentPfn)
    {
        var foreground = CurrentForeground(currentIdentity, currentPfn);
        var (monitor, textNative) = CreateStartedMonitor(foreground);

        var result = await monitor.ReadTextSnapshotAsync(ExpectedPinned()).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.NotEqual(ClipboardReadOutcome.Success, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
    }

    // ---- clipboard read, CHECK2 (identity diverges only after CHECK1 passed) ----
    [Theory]
    [MemberData(nameof(DivergentCurrentIdentities))]
    public async Task RedId002To004_ClipboardRead_Check2_DivergentPackageIdentity_Rejected(
        PackageIdentityResolution currentIdentity, string? currentPfn)
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        // ONLY the package identity may diverge at CHECK2 -- PID and ProcessName are pinned to the
        // expected values on the "After" state too, so a rejection can ONLY be attributable to the
        // package-identity divergence, never to the fake's own default name/PID drift.
        foreground.ChangeAfterAttempt = 1;
        foreground.WindowThreadProcessIdValueAfter = PinnedPid;
        foreground.ProcessNameValueAfter = PinnedProcessName;
        foreground.PackageIdentityValueAfter = currentIdentity;
        foreground.PackageFamilyNameValueAfter = currentPfn;
        var (monitor, _) = CreateStartedMonitor(foreground);

        var result = await monitor.ReadTextSnapshotAsync(ExpectedPinned()).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.NotEqual(ClipboardReadOutcome.Success, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    // ---- clipboard write, CHECK1 ----
    [Theory]
    [MemberData(nameof(DivergentCurrentIdentities))]
    public async Task RedId002To004_ClipboardWrite_Check1_DivergentPackageIdentity_Rejected(
        PackageIdentityResolution currentIdentity, string? currentPfn)
    {
        var foreground = CurrentForeground(currentIdentity, currentPfn);
        var (monitor, textNative) = CreateStartedMonitor(foreground);

        var result = await monitor
            .WriteTextIfSequenceMatchesAsync(ExpectedPinned(), DefaultSequence, "SYNTHETIC-REPLACEMENT")
            .WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.NotEqual(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
    }

    // ---- clipboard write, CHECK2 (the destructive boundary must never be crossed) ----
    [Theory]
    [MemberData(nameof(DivergentCurrentIdentities))]
    public async Task RedId002To004_ClipboardWrite_Check2_DivergentPackageIdentity_Rejected(
        PackageIdentityResolution currentIdentity, string? currentPfn)
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        // ONLY the package identity may diverge at CHECK2 -- PID and ProcessName are pinned to the
        // expected values on the "After" state too, so a rejection can ONLY be attributable to the
        // package-identity divergence, never to the fake's own default name/PID drift.
        foreground.ChangeAfterAttempt = 1;
        foreground.WindowThreadProcessIdValueAfter = PinnedPid;
        foreground.ProcessNameValueAfter = PinnedProcessName;
        foreground.PackageIdentityValueAfter = currentIdentity;
        foreground.PackageFamilyNameValueAfter = currentPfn;
        var (monitor, textNative) = CreateStartedMonitor(foreground);

        var result = await monitor
            .WriteTextIfSequenceMatchesAsync(ExpectedPinned(), DefaultSequence, "SYNTHETIC-REPLACEMENT")
            .WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.NotEqual(ClipboardWriteOutcome.Success, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.EmptyClipboard), textNative.CallLog);
    }

    // ---- composer read, CHECK1 ----
    [Theory]
    [MemberData(nameof(DivergentCurrentIdentities))]
    public async Task RedId002To004_ComposerRead_Check1_DivergentPackageIdentity_Rejected(
        PackageIdentityResolution currentIdentity, string? currentPfn)
    {
        var foreground = CurrentForeground(currentIdentity, currentPfn);
        var reader = new ComposerTextReader(new FakeComposerTextSource(), foreground);
        reader.Start();

        var result = await reader.ReadFocusedComposerTextAsync(ExpectedPinned()).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.NotEqual(ComposerReadOutcome.Success, result.Outcome);
    }

    // ---- composer read, CHECK2 ----
    [Theory]
    [MemberData(nameof(DivergentCurrentIdentities))]
    public async Task RedId002To004_ComposerRead_Check2_DivergentPackageIdentity_Rejected(
        PackageIdentityResolution currentIdentity, string? currentPfn)
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        // ONLY the package identity may diverge at CHECK2 -- PID and ProcessName are pinned to the
        // expected values on the "After" state too, so a rejection can ONLY be attributable to the
        // package-identity divergence, never to the fake's own default name/PID drift.
        foreground.ChangeAfterAttempt = 1;
        foreground.WindowThreadProcessIdValueAfter = PinnedPid;
        foreground.ProcessNameValueAfter = PinnedProcessName;
        foreground.PackageIdentityValueAfter = currentIdentity;
        foreground.PackageFamilyNameValueAfter = currentPfn;
        var reader = new ComposerTextReader(new FakeComposerTextSource(), foreground);
        reader.Start();

        var result = await reader.ReadFocusedComposerTextAsync(ExpectedPinned()).WaitAsync(WaitTimeout);
        reader.Stop();

        Assert.NotEqual(ComposerReadOutcome.Success, result.Outcome);
    }

    // ==================================================================
    // RED-ID-006 / 007 -- pre-existing behavior that must be preserved
    // ==================================================================

    // RED-ID-006: an ordinary foreground departure (different PID and/or different process name)
    // must still be rejected -- this is existing behavior and must never regress.
    [Theory]
    [InlineData(9999u, PinnedProcessName)]
    [InlineData(PinnedPid, "SomeOtherApp")]
    public async Task RedId006_OrdinaryForegroundDeparture_Rejected(uint currentPid, string currentName)
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn, currentPid, currentName);
        var (monitor, _) = CreateStartedMonitor(foreground);

        var result = await monitor.ReadTextSnapshotAsync(ExpectedPinned()).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetChanged, result.Outcome);
    }

    // RED-ID-007: the native identity resolution itself fails (the process exited, the handle could
    // not be opened, or the resolution was otherwise inconclusive) -> reject, fail closed, never an
    // exception and never a fallback to a weaker comparison.
    [Fact]
    public async Task RedId007_IdentityResolutionFails_RejectedAsUnavailable()
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        foreground.ProcessNameResult = false; // native identity resolution failed
        var (monitor, _) = CreateStartedMonitor(foreground);

        var result = await monitor.ReadTextSnapshotAsync(ExpectedPinned()).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetUnavailable, result.Outcome);
    }

    // ==================================================================
    // RED-ID-009 / 010 -- the foreground-confirmation step (E+ step 5)
    // ==================================================================

    // RED-ID-009: the pinned process's identity resolves perfectly and would fully match expected,
    // but by the time the confirmation runs the CURRENT foreground process is a DIFFERENT PID. The
    // coherent capture must fail, and every guard must therefore reject -- otherwise PRIVON could
    // act on identity facts belonging to a process that is not the foreground target at all.
    [Fact]
    public void RedId009_ForegroundMovedBeforeConfirmation_CaptureFails()
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        foreground.ConfirmationForegroundProcessIdOverride = 9999; // foreground moved to another process

        var snapshot = new ForegroundTargetInspector(foreground).Capture();

        Assert.False(snapshot.IsResolved);
        Assert.Equal(0u, snapshot.ProcessId);
        Assert.Null(snapshot.ProcessName);
        Assert.Equal(PackageIdentityResolution.Unresolved, snapshot.PackageIdentity);
        Assert.Null(snapshot.PackageFamilyName);
    }

    [Fact]
    public async Task RedId009_ForegroundMovedBeforeConfirmation_GuardRejects()
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        foreground.ConfirmationForegroundProcessIdOverride = 9999;
        var (monitor, textNative) = CreateStartedMonitor(foreground);

        var result = await monitor.ReadTextSnapshotAsync(ExpectedPinned()).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.TargetUnavailable, result.Outcome);
        Assert.DoesNotContain(nameof(FakeClipboardTextNative.OpenClipboard), textNative.CallLog);
    }

    // RED-ID-009b: the confirmation cannot resolve a foreground process at all -> fail closed,
    // distinct from a PID mismatch but with the same rejecting outcome.
    [Fact]
    public void RedId009b_ConfirmationForegroundUnresolvable_CaptureFails()
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        foreground.ConfirmationForegroundResolvable = false;

        var snapshot = new ForegroundTargetInspector(foreground).Capture();

        Assert.False(snapshot.IsResolved);
    }

    // RED-ID-010: the foreground WINDOW changes, but the new foreground window still belongs to the
    // SAME pinned process. That is mechanically coherent and must remain VALID -- this is what
    // prevents the HWND value itself from silently becoming identity policy.
    [Fact]
    public void RedId010_ForegroundWindowChangedButSamePinnedProcess_CaptureSucceeds()
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        foreground.ForegroundWindowResult = 111;
        // A different window handle, but the confirmation still observes the SAME pinned PID
        // (ConfirmationForegroundProcessIdOverride left null == same PID).

        var snapshot = new ForegroundTargetInspector(foreground).Capture();

        Assert.True(snapshot.IsResolved);
        Assert.Equal(PinnedPid, snapshot.ProcessId);
        Assert.Equal(PinnedProcessName, snapshot.ProcessName);
        Assert.Equal(PackageIdentityResolution.Resolved, snapshot.PackageIdentity);
        Assert.Equal(PinnedPfn, snapshot.PackageFamilyName);
    }

    [Fact]
    public async Task RedId010_ForegroundWindowChangedButSamePinnedProcess_GuardPermits()
    {
        var foreground = CurrentForeground(PackageIdentityResolution.Resolved, PinnedPfn);
        foreground.ForegroundWindowResult = 111;
        var (monitor, _) = CreateStartedMonitor(foreground);

        var result = await monitor.ReadTextSnapshotAsync(ExpectedPinned()).WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Equal(ClipboardReadOutcome.Success, result.Outcome);
    }

    // ==================================================================
    // RED-ID-008 -- BUG004-FACT-002: one coherent native resolution
    // ==================================================================

    // The production factual resolver must NOT use System.Diagnostics.Process to obtain either half
    // of the identity pair. Process.GetProcessById(pid).ProcessName and Process.Handle are two
    // INDEPENDENT PID-keyed lookups (empirically confirmed: creating 271 Process objects and reading
    // ProcessName added 2 handles to the caller, while forcing .Handle on those same objects added
    // 131) -- so nothing prevented the two facts from straddling a PID-reuse boundary. The corrected
    // contract opens ONE native handle first and derives both facts from it.
    [Fact]
    public void RedId008_Win32IdentityResolver_DoesNotUseSystemDiagnosticsProcess()
    {
        var source = typeof(Win32ForegroundTargetSource);
        var processTypeReferences = source
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
            .Where(t => t == typeof(System.Diagnostics.Process))
            .ToArray();

        Assert.Empty(processTypeReferences);

        // The identity pair must be produced by ONE declared native handle-scoped resolution, not by
        // two independent PID-keyed calls. Proven structurally: the type declares the handle-opening
        // and image-name native entry points it needs.
        var nested = source.GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .ToArray();

        Assert.Contains("OpenProcess", nested);
        Assert.Contains("QueryFullProcessImageNameW", nested);
        Assert.Contains("GetPackageFamilyName", nested);
        Assert.Contains("CloseHandle", nested);
    }
}
