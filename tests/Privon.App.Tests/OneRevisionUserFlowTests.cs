using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. Does any REAL event of the product consume the one-revision pass that a BypassOnce decision issued?
//
// The real coordinator, resolver, decision-scope lifecycle, tracker and presenter run with fake transports. Nothing here touches a real clipboard,
// raises a made-up foreground change outside the fake trigger the coordinator already listens to, or sends anything.
public sealed class OneRevisionUserFlowTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
    }

    private const string Gps = "37.5665,126.9780";                       // a Level 1 decision

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

    // ===== RED, recorded before this file became a characterization =========================================================
    //
    // The expectation was: after BypassOnce issued the pass, a real event of the product (the foreground trigger, the only one that reaches the same
    // clipboard revision) consumes it. Run against the real coordinator it FAILED: the pass was still there after two foreground triggers.
    // The tests below pin WHY, so the gap cannot be mistaken for a connection and cannot silently close without these tests being revisited.

    private static async Task<(ClipboardDecisionScope Scope, ClipboardDecisionItem Item)> KeepOriginalAsync(Rig rig)
    {
        await rig.CopyAsync(Gps);
        var scope = rig.Lifecycle.GetActiveScope()!;
        var item = Assert.Single(scope.Items);
        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        Assert.True(result.ScopeCommitted);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));       // events 1 and 2: chosen, and the pass is issued
        return (scope, item);
    }

    [Fact]
    public async Task NoRealEventConsumesTheIssuedPass_TheForegroundTriggerIsAbsorbedForAnEvaluatedGeneration()
    {
        var rig = NewRig();
        var (scope, item) = await KeepOriginalAsync(rig);
        int reads = rig.Read.ReadCallCount;
        long completed = rig.Tracker.Current.CompletedAttempts;

        rig.Foreground.Raise();
        rig.Foreground.Raise();
        await Task.Delay(400);                                                    // the mailbox is drained long before this

        Assert.Equal(reads, rig.Read.ReadCallCount);                              // the trigger did not even read: the single claim for this generation is spent
        Assert.Equal(completed, rig.Tracker.Current.CompletedAttempts);           // and it produced no terminal fact
        Assert.True(rig.Lifecycle.IsActive(scope));                               // the committed scope is still the active one ...
        Assert.True(scope.Committed);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));   // ... and its pass is untouched: nothing consumed it
        Assert.True(rig.Tracker.Current.DecisionKeptOriginal);
        Assert.Contains("The one-time allowance has not been confirmed as used", ProtectionStatusPresenter.Present(rig.Tracker.Current).Detail);   // so the status does not say it
    }

    [Fact]
    public async Task TheForegroundTrigger_IsLive_ForAGenerationWhoseEvaluationWasAbandoned()
    {
        // The contrast that shows the previous test is not an artifact of the rig: the very same trigger DOES run a new attempt when the generation
        // can be claimed again (a retryable failure releases the claim), and does not when the evaluation finished.
        var rig = NewRig();
        rig.Read.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.Busy);
        rig.Read.RaiseChanged(new ClipboardChangeNotification(1, true, true));
        await WaitAsync(() => rig.Read.ReadCallCount >= ClipboardPrivacyCoordinator.MaxAttemptsContractDefault);
        await Task.Delay(100);
        int reads = rig.Read.ReadCallCount;
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, "회의는 오후 세 시입니다"));

        rig.Foreground.Raise();
        await WaitAsync(() => rig.Read.ReadCallCount > reads);

        Assert.True(rig.Read.ReadCallCount > reads);
    }

    [Fact]
    public async Task ANewCopyOfTheSameText_IsANewRevision_TheOldPassCannotBeSpent_AndTheUserIsAskedAgain()
    {
        var rig = NewRig();
        var (oldScope, oldItem) = await KeepOriginalAsync(rig);

        await rig.CopyAsync(Gps);                                                 // the identical text, copied again

        var newScope = rig.Lifecycle.GetActiveScope()!;
        Assert.NotSame(oldScope, newScope);
        Assert.False(rig.Lifecycle.IsActive(oldScope));
        Assert.False(newScope.Committed);
        Assert.False(newScope.HasBypassGrant(newScope.InitialStamp, oldItem.DetectorId, oldItem.Canonical));   // the new decision starts without any pass
        Assert.Contains("Awaiting your decision", ProtectionStatusPresenter.Present(rig.Tracker.Current).Tooltip);

        var stale = await rig.Resolver.EvaluateOneRevisionPassAsync(oldScope);    // spending the old pass is refused outright
        Assert.Equal(ClipboardOneRevisionPassOutcome.Stale, stale.Outcome);
        Assert.True(oldScope.HasBypassGrant(oldScope.InitialStamp, oldItem.DetectorId, oldItem.Canonical));   // still unspent, and now unusable for ever
    }

    [Fact]
    public async Task AnyNewClipboardCopy_EndsTheScope_EvenWhenTheNewContentAsksNothing_AndTheOldPassCannotBeSpent()
    {
        var rig = NewRig();
        var (scope, item) = await KeepOriginalAsync(rig);

        await rig.CopyAsync("회의는 오후 세 시입니다");                              // nothing to decide about: no new scope replaces the old one

        Assert.False(rig.Lifecycle.HasActiveScope);
        Assert.False(rig.Lifecycle.IsActive(scope));                              // the old scope ended because the clipboard changed, not because it was replaced
        var stale = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);
        Assert.Equal(ClipboardOneRevisionPassOutcome.Stale, stale.Outcome);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
        Assert.DoesNotContain("Original kept by choice", ProtectionStatusPresenter.Present(rig.Tracker.Current).Tooltip);   // and the status no longer describes the old content
    }

    [Fact]
    public async Task ASessionLockEndsTheScope_AndTheOldPassCannotBeSpent()
    {
        var rig = NewRig();
        var (scope, item) = await KeepOriginalAsync(rig);

        rig.Lifecycle.Reset();                                                    // what the session-lock callback does

        Assert.False(rig.Lifecycle.IsActive(scope));
        var stale = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);
        Assert.Equal(ClipboardOneRevisionPassOutcome.Stale, stale.Outcome);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
    }

    [Fact]
    public async Task TheOnlyWayToSpendThePass_IsToCallTheEvaluationByHand_AndThenItIsSpentExactlyOnce()
    {
        // Not a product flow: the engine is intact and correct, it just has no caller. Called by hand here, which proves the engine and NOT the connection.
        var rig = NewRig();
        var (scope, item) = await KeepOriginalAsync(rig);

        var first = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);
        var second = await rig.Resolver.EvaluateOneRevisionPassAsync(scope);

        Assert.Equal(ClipboardOneRevisionPassDisposition.AllowOriginalOnce, Assert.Single(first.Evaluation!.Candidates).Disposition);    // R1
        Assert.Equal(ClipboardOneRevisionPassDisposition.NeedsDecision, Assert.Single(second.Evaluation!.Candidates).Disposition);      // R2
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, item.DetectorId, item.Canonical));
    }
}
