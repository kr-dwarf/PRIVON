using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. What the tray says after the user chose BypassOnce ("keep the original"). Three different events, never to be merged:
//   1. the user CHOSE to keep the original,
//   2. a one-revision pass (grant) was ISSUED by the resolver's commit,
//   3. a later evaluation of that same revision CONSUMED the pass and every decision ended.
// Only (1) and (2) happen in the product today; nothing in src calls the evaluation that performs (3). So the status may say the user kept the
// original, which is true, and must not say the matter is finished, protected or verified.
//
// The real coordinator, resolver, tracker and presenter run here with fake transports. No real clipboard, hook or window is touched, and nothing here
// blocks a paste or a send.
public sealed class BypassOnceTerminalStatusTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
    }

    private const string Gps = "37.5665,126.9780";                       // a Level 1 decision
    private const string Rrn = "901231-1234567";                        // a Level 3 decision
    private const string Email = "user@example.test";                   // protected without asking
    private const string MixedText = "메일 " + Email + " 주민번호 " + Rrn;   // one item is protected without asking, one waits for the user
    private const string TwoDecisions = Rrn + " " + Gps;

    private sealed class Rig : IDisposable
    {
        private uint _sequence;

        public Rig()
        {
            Lifecycle = new ClipboardDecisionScopeLifecycle();
            Tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
            Write = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 2) };
            var gate = new ClipboardOperationGate();
            var processor = new ClipboardPrivacyProcessor(DetectionPipeline.CreateDefault, new FakeTrustExceptionProvider());
            var handoff = new FakeClipboardComposerVerificationHandoff();
            Coordinator = new ClipboardPrivacyCoordinator(
                Read, Target, processor, Write, Lifecycle, new ClipboardDecisionSessionPublisher(Lifecycle), gate, handoff,
                new FakeClipboardComposerVerificationInvalidation(), diagnostics: Tracker, foregroundTrigger: Foreground);
            Coordinator.Start();
            Resolver = new ClipboardDecisionActionResolver(gate, Lifecycle, Target, Read, Write, processor, handoff, diagnostics: Tracker);
        }

        public FakeClipboardReadTransport Read { get; } = new();
        public FakeForegroundTargetCapture Target { get; } = new();
        public FakeClipboardForegroundTrigger Foreground { get; } = new();
        public FakeClipboardWriteTransport Write { get; }
        public ClipboardDecisionScopeLifecycle Lifecycle { get; }
        public ClipboardProtectionStatusTracker Tracker { get; }
        public ClipboardPrivacyCoordinator Coordinator { get; }
        public ClipboardDecisionActionResolver Resolver { get; }

        public async Task CopyAsync(string text)
        {
            long before = Tracker.Current.CompletedAttempts;
            uint sequence = ++_sequence;
            Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(sequence, true, text));
            Read.RaiseChanged(new ClipboardChangeNotification(sequence, true, true));
            await WaitAsync(() => Tracker.Current.CompletedAttempts > before);
        }

        public (ClipboardDecisionScope Scope, ClipboardDecisionItem[] Items) Pending()
        {
            var scope = Lifecycle.GetActiveScope() ?? throw new InvalidOperationException("no pending decision");
            return (scope, scope.Items.ToArray());
        }

        public ProtectionStatusPresentation Shown => ProtectionStatusPresenter.Present(Tracker.Current);

        public void Dispose() => Coordinator.Dispose();
    }

    private Rig NewRig()
    {
        var rig = new Rig();
        _disposables.Add(rig);
        _disposables.Add(rig.Tracker);
        return rig;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(5);
        }
    }

    private static List<ClipboardProtectionStatus> Watch(ClipboardProtectionStatusTracker tracker)
    {
        var seen = new List<ClipboardProtectionStatus>();
        tracker.Changed += (_, _) => { lock (seen) seen.Add(tracker.Current); };
        return seen;
    }

    private static void AssertNoClaimOfCompletionOrProtection(ProtectionStatusPresentation p)
    {
        string all = p.Tooltip + " " + p.Detail;
        Assert.DoesNotContain("complete", all);
        Assert.DoesNotContain("rewrite to protected form verified", all);
        Assert.DoesNotContain("is protected", all);
        Assert.DoesNotContain("Verified", all);
        Assert.DoesNotContain("content is safe", all);
    }

    // ===== the user kept the only pending item =================================================================================

    [Fact]
    public async Task KeepingTheOnlyPendingItem_IsNoLongerShownAsAPendingDecision_AndIsNotShownAsProtected()
    {
        var rig = NewRig();
        await rig.CopyAsync(Gps);
        Assert.Contains("Awaiting your decision", rig.Shown.Tooltip);                      // before the choice it really is pending
        var (scope, items) = rig.Pending();

        var result = await rig.Resolver.ResolveAsync(scope, Assert.Single(items), ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ScopeCommitted);                                   // events 1 and 2: chosen, and the pass is issued
        Assert.False(result.ClipboardMutated);
        Assert.Equal(0, rig.Write.CallCount);
        var shown = rig.Shown;
        Assert.DoesNotContain("Awaiting your decision", shown.Tooltip);                    // the decision is not pending any more
        Assert.DoesNotContain("Waiting for your decision", shown.Detail);
        Assert.Contains("Original kept by choice", shown.Tooltip);
        Assert.Contains("not protected", shown.Tooltip);
        Assert.Contains("original text remains on the clipboard", shown.Detail);
        Assert.False(shown.IsWarning);                                        // the user chose it: no alarm
        Assert.False(rig.Tracker.Current.ClaimsProtected);
        AssertNoClaimOfCompletionOrProtection(shown);
    }

    [Fact]
    public async Task TheKeptStatus_SaysTheOneTimePassWasNotConfirmedUsed_AndKeepsNotCheckedAndNotBlockedSeparate()
    {
        var rig = NewRig();
        await rig.CopyAsync(Gps);
        var (scope, items) = rig.Pending();

        await rig.Resolver.ResolveAsync(scope, Assert.Single(items), ClipboardDecisionIntent.BypassOnce);

        var shown = rig.Shown;
        Assert.Contains("The one-time allowance has not been confirmed as used", shown.Detail);   // event 3 is not observed, and the text says so
        Assert.Contains("PRIVON does not currently block sending", shown.Detail);
        Assert.True(shown.Tooltip.Length <= ProtectionStatusPresenter.MaxTooltipLength);
        Assert.DoesNotContain("block", shown.Tooltip);
    }

    // ===== the user kept one item but PRIVON still rewrote another =============================================================

    [Fact]
    public async Task KeepingOneItem_WhileAnotherIsRewritten_IsNeverShownAsAVerifiedRewrite()
    {
        var rig = NewRig();
        await rig.CopyAsync(MixedText);
        var (scope, items) = rig.Pending();
        var item = Assert.Single(items);
        var seen = Watch(rig.Tracker);

        await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);          // Level 3: the first request is not yet a decision
        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.True(result.ClipboardMutated);                                 // the e-mail address WAS rewritten and read back ...
        Assert.Equal(1, rig.Write.CallCount);
        string written = rig.Write.ReceivedReplacementTexts[0];
        Assert.DoesNotContain(Email, written);
        Assert.Contains(Rrn, written);                                        // ... but the kept item is still in the clipboard text
        Assert.DoesNotContain(seen, s => s.State == ProtectionState.Verified);   // so the status is never Verified, at any moment
        Assert.False(rig.Tracker.Current.ClaimsProtected);
        Assert.Contains("Original kept by choice", rig.Shown.Tooltip);
        AssertNoClaimOfCompletionOrProtection(rig.Shown);
    }

    [Fact]
    public async Task IfThatRewriteFails_TheFailureIsShown_NotTheKeptStatus()
    {
        var rig = NewRig();
        await rig.CopyAsync(MixedText);
        var (scope, items) = rig.Pending();
        var item = Assert.Single(items);
        rig.Write.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true);

        await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.MutatedUnverified, result.Outcome);
        Assert.False(result.ScopeCommitted);                                  // nothing committed, no pass issued
        Assert.Equal(ProtectionState.Limited, rig.Tracker.Current.State);
        Assert.True(rig.Shown.IsWarning);
    }

    // ===== the user has NOT finished deciding ==================================================================================

    [Fact]
    public async Task WhileASiblingDecisionRemains_TheStatusStaysPending()
    {
        var rig = NewRig();
        await rig.CopyAsync(TwoDecisions);
        var (scope, items) = rig.Pending();
        Assert.Equal(2, items.Length);
        var gps = items.Single(i => i.RiskLevel == RiskLevel.Level1);

        var result = await rig.Resolver.ResolveAsync(scope, gps, ClipboardDecisionIntent.BypassOnce);

        Assert.False(result.ScopeCommitted);                                  // no pass is issued while a sibling is undecided
        Assert.Contains("Awaiting your decision", rig.Shown.Tooltip);
        Assert.DoesNotContain("Original kept by choice", rig.Shown.Tooltip);
        Assert.Equal(ProtectionState.NeedsDecision, rig.Tracker.Current.State);
    }

    [Fact]
    public async Task ALevel3ItemNeedsTwoConfirmations_AndIsKeptOnlyAfterTheSecond()
    {
        var rig = NewRig();
        await rig.CopyAsync(Rrn);
        var (scope, items) = rig.Pending();
        var item = Assert.Single(items);
        Assert.Equal(RiskLevel.Level3, item.RiskLevel);

        var first = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, first.Outcome);
        Assert.Contains("Awaiting your decision", rig.Shown.Tooltip);                      // one request is not a decision

        var second = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.True(second.ScopeCommitted);
        Assert.Contains("Original kept by choice", rig.Shown.Tooltip);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.False(rig.Tracker.Current.ClaimsProtected);
    }

    // ===== re-entry, the previous generation, and the one-time pass itself ====================================================

    [Fact]
    public async Task AnotherBypassOnceForTheSameCommittedDecision_IsStale_AndChangesNothingTheUserSees()
    {
        var rig = NewRig();
        await rig.CopyAsync(Gps);
        var (scope, items) = rig.Pending();
        var item = Assert.Single(items);
        await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        var seen = Watch(rig.Tracker);
        string before = rig.Shown.Tooltip;

        var again = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, again.Outcome);
        Assert.Empty(seen);
        Assert.Equal(before, rig.Shown.Tooltip);
    }

    [Fact]
    public async Task AKeepChosenForContentThatWasReplaced_IsStale_AndNeverDescribesTheNewContent()
    {
        var rig = NewRig();
        await rig.CopyAsync(Gps);
        var (scope, items) = rig.Pending();
        await rig.CopyAsync("회의는 오후 세 시입니다");                          // the user copied something else: the pending decision is superseded
        var seen = Watch(rig.Tracker);

        var result = await rig.Resolver.ResolveAsync(scope, Assert.Single(items), ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.False(result.ScopeCommitted);
        Assert.Empty(seen);
        Assert.DoesNotContain("Original kept by choice", rig.Shown.Tooltip);
        Assert.Contains("Scan finished", rig.Shown.Tooltip);                        // the new content really was scanned
    }

    [Fact]
    public void ANewCopy_EndsTheKeptStatusAtOnce_SoItNeverDescribesNewContent()
    {
        using var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        tracker.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(1, 1, true, true));
        tracker.Record(ClipboardDiagnosticEvent.AttemptTerminal(1, ClipboardDiagnosticTerminalReason.DecisionKeptOriginal));
        Assert.True(tracker.Current.DecisionKeptOriginal);

        tracker.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(2, 2, true, true));   // something new is copied; its scan has not ended

        Assert.False(tracker.Current.DecisionKeptOriginal);
        Assert.Equal(ProtectionState.Scanning, tracker.Current.State);
        Assert.DoesNotContain("Original kept by choice", ProtectionStatusPresenter.Present(tracker.Current).Tooltip);
    }

    [Fact]
    public async Task TheOneTimePass_IsConsumedExactlyOnce_AndConsumingItDoesNotChangeTheStatusBecauseNothingObservesIt()
    {
        var rig = NewRig();
        await rig.CopyAsync(Gps);
        var (scope, items) = rig.Pending();
        var item = Assert.Single(items);
        await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));       // event 2: the pass exists

        var first = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);                            // event 3, called by hand: nothing in the product does
        var second = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassDisposition.AllowOriginalOnce, Assert.Single(first.Evaluation!.Candidates).Disposition);   // R1
        Assert.Equal(ClipboardOneRevisionPassDisposition.NeedsDecision, Assert.Single(second.Evaluation!.Candidates).Disposition);     // R2: not reusable
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
        Assert.Contains("Original kept by choice", rig.Shown.Tooltip);                      // still the user's choice, never "finished"
        Assert.Contains("The one-time allowance has not been confirmed as used", rig.Shown.Detail);
        AssertNoClaimOfCompletionOrProtection(rig.Shown);
    }

    [Fact]
    public void NothingInTheProduct_CallsTheEvaluationThatConsumesThePass_SoTheConsumptionIsNeverReported()
    {
        // Pinned, not hidden: if a caller is ever added, this fails and the status text above must be revisited.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App"))) dir = dir.Parent;
        var callers = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("EvaluateOneRevisionPass", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(["ClipboardDecisionActionResolver.cs", "ClipboardOneRevisionPass.cs", "ClipboardPrivacyProcessor.cs"], callers);
    }

    [Fact]
    public async Task TheKoreanAndGlobalDefaultsAreUnchanged_AndNoUsIdentifierIsDetected()
    {
        var rig = NewRig();

        await rig.CopyAsync("SSN: 321-54-9876");

        Assert.Equal(0, rig.Write.CallCount);
        Assert.DoesNotContain("Awaiting your decision", rig.Shown.Tooltip);
        Assert.False(rig.Lifecycle.HasActiveScope);                          // the shipped pipeline does not ask about a U.S. identifier
    }
}
