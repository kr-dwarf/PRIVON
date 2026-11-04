using System.Reflection;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. Decision-time protection status: once the user decides on a pending NeedsDecision, the rewrite the resolver performs (or
// fails to perform) must reach the SAME ClipboardProtectionStatusTracker the coordinator feeds, or the tray keeps describing a decision that is no
// longer pending.
//
// Every test drives the REAL ClipboardPrivacyCoordinator, ClipboardPrivacyProcessor, ClipboardDecisionActionResolver, decision-scope lifecycle,
// shared operation gate and tracker, wired the way PrivonAppComposition wires them. Nothing touches the real clipboard, a hook or a window.
//
// Verified here means exactly what it means for the coordinator: the guarded write succeeded, changed the clipboard and was read back. It is not a
// claim that anything was pasted or sent safely -- the product cannot block either (A-1).
public sealed class ClipboardDecisionStatusSyncTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
    }

    private const string Level3Ssn = "SSN: 321-54-9876";
    private const string TwoLevel3 = "SSN: 321-54-9876 ITIN: 912-70-1234";
    private const string Level1Gps = "37.5665,126.9780";      // order-unambiguous bare pair: a Level 1 decision
    private const string Level3Rrn = "901231-1234567";      // a resident registration number: a Level 3 decision
    private const string Clean = "회의는 오후 세 시입니다";

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(), new GpsCoordinateDetector(),
    ];

    private static DetectionPipeline UsPipeline() => UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOn);

    private sealed class ScriptedWrite : IClipboardWriteTransport
    {
        private readonly object _lock = new();
        private TaskCompletionSource<ClipboardWriteResult>? _pending;
        private int _active;
        public int CallCount;
        public int MaxConcurrent;
        public List<string> Texts { get; } = [];
        public List<uint> Sequences { get; } = [];
        public ClipboardWriteResult Result { get; set; } = ClipboardWriteResult.Success(resultSequence: 2);
        public Exception? Throw { get; set; }
        public bool Hold { get; set; }
        public Task Entered => _entered.Task;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ClipboardWriteResult> WriteTextIfSequenceMatchesAsync(
            ForegroundTargetSnapshot expectedTarget, uint expectedSequence, string replacementText, IClipboardAuthorizationFreshness? authorizationFreshness = null)
        {
            lock (_lock)
            {
                Texts.Add(replacementText);
                Sequences.Add(expectedSequence);
                Interlocked.Increment(ref CallCount);
                if (Throw is not null) throw Throw;
                int active = Interlocked.Increment(ref _active);
                MaxConcurrent = Math.Max(MaxConcurrent, active);
                if (!Hold)
                {
                    Interlocked.Decrement(ref _active);
                    return Task.FromResult(Result);
                }

                _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _entered.TrySetResult();
                return _pending.Task.ContinueWith(t => { Interlocked.Decrement(ref _active); return t.Result; });
            }
        }

        public void Release(ClipboardWriteResult result)
        {
            lock (_lock) _pending?.TrySetResult(result);
        }
    }

    // A handoff that can run code at the exact moment the resolver publishes the verified text (after the write, before it reports).
    private sealed class HookedHandoff : IClipboardComposerVerificationHandoff
    {
        public int CallCount;
        public List<string> Texts { get; } = [];
        public Action? OnPublish { get; set; }

        public bool Publish(ForegroundTargetSnapshot expectedTarget, string expectedProtectedText, long expectedGeneration)
        {
            Interlocked.Increment(ref CallCount);
            Texts.Add(expectedProtectedText);
            OnPublish?.Invoke();
            return true;
        }
    }

    private sealed class ThrowingRecorder : IClipboardDiagnosticRecorder
    {
        public int Calls;
        public void Record(ClipboardDiagnosticEvent diagnosticEvent) { Interlocked.Increment(ref Calls); throw new InvalidOperationException("recorder failure"); }
    }

    // The graph PrivonAppComposition builds: ONE gate and ONE lifecycle shared by the coordinator and the resolver, the same transports, the tracker as
    // the coordinator's recorder.
    private sealed class Rig : IDisposable
    {
        private uint _sequence;

        public Rig(Func<DetectionPipeline> source, ScriptedWrite? write = null, IClipboardDiagnosticRecorder? resolverRecorder = null, bool wireResolverToTracker = true)
        {
            Gate = new ClipboardOperationGate();
            Lifecycle = new ClipboardDecisionScopeLifecycle();
            Tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
            Write = write ?? new ScriptedWrite();
            Processor = new ClipboardPrivacyProcessor(source, new FakeTrustExceptionProvider());
            Handoff = new HookedHandoff();
            Coordinator = new ClipboardPrivacyCoordinator(
                Read, Target, Processor, Write, Lifecycle, new ClipboardDecisionSessionPublisher(Lifecycle), Gate,
                Handoff, new FakeClipboardComposerVerificationInvalidation(), diagnostics: Tracker, foregroundTrigger: Foreground);
            Coordinator.Start();
            Resolver = NewResolver(resolverRecorder, wireResolverToTracker);
        }

        public ClipboardOperationGate Gate { get; }
        public FakeClipboardReadTransport Read { get; } = new();
        public FakeForegroundTargetCapture Target { get; } = new();
        public FakeClipboardForegroundTrigger Foreground { get; } = new();
        public ScriptedWrite Write { get; }
        public HookedHandoff Handoff { get; }
        public ClipboardDecisionScopeLifecycle Lifecycle { get; }
        public ClipboardProtectionStatusTracker Tracker { get; }
        public ClipboardPrivacyProcessor Processor { get; }
        public ClipboardPrivacyCoordinator Coordinator { get; }
        public ClipboardDecisionActionResolver Resolver { get; }

        private ClipboardDecisionActionResolver NewResolver(IClipboardDiagnosticRecorder? recorder, bool wireToTracker) =>
            new(Gate, Lifecycle, Target, Read, Write, Processor, Handoff, diagnostics: recorder ?? (wireToTracker ? Tracker : null));

        public uint Copy(string text)
        {
            uint sequence = ++_sequence;
            Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(sequence, true, text));
            Read.RaiseChanged(new ClipboardChangeNotification(sequence, true, true));
            return sequence;
        }

        public async Task<ClipboardProtectionStatus> ScanAsync(string text)
        {
            long before = Tracker.Current.CompletedAttempts;
            Copy(text);
            await WaitAsync(() => Tracker.Current.CompletedAttempts > before);
            return Tracker.Current;
        }

        public (ClipboardDecisionScope Scope, ClipboardDecisionItem[] Items) Pending()
        {
            var scope = Lifecycle.GetActiveScope() ?? throw new InvalidOperationException("no pending decision");
            return (scope, scope.Items.ToArray());
        }

        public void Dispose() => Coordinator.Dispose();
    }

    private Rig NewRig(Func<DetectionPipeline> source, ScriptedWrite? write = null, IClipboardDiagnosticRecorder? resolverRecorder = null)
    {
        var rig = new Rig(source, write, resolverRecorder);
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

    // Every state the status passes through, so a transient wrong claim cannot hide behind a correct final state.
    private static List<ClipboardProtectionStatus> Watch(ClipboardProtectionStatusTracker tracker)
    {
        var seen = new List<ClipboardProtectionStatus>();
        tracker.Changed += (_, _) => { lock (seen) seen.Add(tracker.Current); };
        return seen;
    }

    private static async Task<(Rig Rig, ClipboardDecisionScope Scope, ClipboardDecisionItem Item)> PendingLevel3(ClipboardDecisionStatusSyncTests t, ScriptedWrite? write = null)
    {
        var rig = t.NewRig(UsPipeline, write);
        var pending = await rig.ScanAsync(Level3Ssn);
        Assert.Equal(ProtectionState.NeedsDecision, pending.State);
        var (scope, items) = rig.Pending();
        return (rig, scope, Assert.Single(items));
    }

    // ===== 1. the reported defect: a verified Protect must reach the tracker ======================================================

    [Fact]
    public async Task Level3Protect_WithAVerifiedRewrite_ReportsVerified_AndNotTheStaleNeedsDecision()
    {
        var (rig, scope, item) = await PendingLevel3(this);

        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ClipboardMutated);
        Assert.True(result.ScopeCommitted);
        Assert.Equal(1, rig.Write.CallCount);
        Assert.DoesNotContain("321-54-9876", rig.Write.Texts[0]);
        Assert.Equal(ProtectionState.Verified, rig.Tracker.Current.State);
        Assert.False(rig.Tracker.Current.UnprotectedTextMayRemain);
    }

    [Fact]
    public async Task Level1Protect_WithAVerifiedRewrite_ReportsVerified()
    {
        var rig = NewRig(UsPipeline);
        var pending = await rig.ScanAsync(Level1Gps);
        Assert.Equal(ProtectionState.NeedsDecision, pending.State);
        var (scope, items) = rig.Pending();

        var result = await rig.Resolver.ResolveAsync(scope, Assert.Single(items), ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.Equal(ProtectionState.Verified, rig.Tracker.Current.State);
        Assert.DoesNotContain("37.5665,126.9780", rig.Write.Texts[0]);
    }

    [Fact]
    public async Task TheVerifiedDecision_IsPresentedAsVerified_WithoutAnyClaimOfBlockedTransmission()
    {
        var (rig, scope, item) = await PendingLevel3(this);

        await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
        var presentation = ProtectionStatusPresenter.Present(rig.Tracker.Current);

        Assert.Equal(ProtectionState.Verified, presentation.State);
        Assert.False(presentation.IsWarning);
        Assert.Contains("does not currently block sending", presentation.Detail);     // the existing wording: verified is never a transmission guarantee
        Assert.False(ClipboardProtectionStatusTracker.TransmitBlockingAvailable);
    }

    [Fact]
    public async Task TheHandoffToTheComposerCheck_CarriesTheRewrittenText_NeverTheOriginal()
    {
        var (rig, scope, item) = await PendingLevel3(this);

        await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(1, rig.Handoff.CallCount);
        Assert.DoesNotContain("321-54-9876", rig.Handoff.Texts[0]);
        Assert.Equal(rig.Write.Texts[0], rig.Handoff.Texts[0]);
    }

    // ===== 2. a decision that does not end in a verified rewrite is never Verified ================================================

    [Fact]
    public async Task AMutatedButUnverifiedProtect_IsLimited_NeverVerified_AndTheRawTextIsStillReportedAsRemaining()
    {
        var (rig, scope, item) = await PendingLevel3(this);
        var seen = Watch(rig.Tracker);
        rig.Write.Result = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true);

        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.MutatedUnverified, result.Outcome);
        Assert.False(result.ScopeCommitted);
        Assert.Equal(ProtectionState.Limited, rig.Tracker.Current.State);
        Assert.True(rig.Tracker.Current.UnresolvedFailure);
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
        Assert.False(rig.Tracker.Current.ClaimsProtected);
        Assert.DoesNotContain(seen, s => s.State == ProtectionState.Verified);
        Assert.True(ProtectionStatusPresenter.Present(rig.Tracker.Current).IsWarning);
        Assert.Equal(0, rig.Handoff.CallCount);                              // an unverified text is never offered to the composer check
    }

    public static TheoryData<ClipboardWriteOutcome> RejectedBeforeAnyChange => new()
    {
        ClipboardWriteOutcome.NotRunning, ClipboardWriteOutcome.Busy, ClipboardWriteOutcome.SequenceChanged, ClipboardWriteOutcome.NativeFailure,
        ClipboardWriteOutcome.TargetUnavailable, ClipboardWriteOutcome.TargetChanged, ClipboardWriteOutcome.InvalidText,
    };

    [Theory]
    [MemberData(nameof(RejectedBeforeAnyChange))]
    public async Task ARejectedProtectWrite_IsLimited_NeverVerified(ClipboardWriteOutcome outcome)
    {
        var (rig, scope, item) = await PendingLevel3(this);
        var seen = Watch(rig.Tracker);
        rig.Write.Result = ClipboardWriteResult.Failure(outcome, mutated: false);

        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.WriteFailed, result.Outcome);
        Assert.Equal(ProtectionState.Limited, rig.Tracker.Current.State);
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
        Assert.DoesNotContain(seen, s => s.State == ProtectionState.Verified);
        Assert.Equal(0, rig.Handoff.CallCount);
    }

    [Fact]
    public async Task ASuccessThatChangedNothing_IsNotAVerifiedRewrite_SoItIsLimited()
    {
        var (rig, scope, item) = await PendingLevel3(this);
        rig.Write.Result = new ClipboardWriteResult { Outcome = ClipboardWriteOutcome.Success, ClipboardMutated = false, ResultSequence = 2 };

        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.NotEqual(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.Equal(ProtectionState.Limited, rig.Tracker.Current.State);
        Assert.False(rig.Tracker.Current.ClaimsProtected);
    }

    [Fact]
    public async Task AnUnreliableSequenceAtDecisionTime_WritesNothing_AndIsLimited()
    {
        var (rig, scope, item) = await PendingLevel3(this);
        var seen = Watch(rig.Tracker);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(50, false, Level3Ssn));   // same text, but the sequence cannot be trusted

        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.WriteFailed, result.Outcome);
        Assert.Equal(0, rig.Write.CallCount);                                // an unconditional write is never attempted
        Assert.Equal(ProtectionState.Limited, rig.Tracker.Current.State);
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
        Assert.DoesNotContain(seen, s => s.State == ProtectionState.Verified);
    }

    [Fact]
    public async Task AnUnexpectedExceptionDuringTheProtectWrite_IsError_AndCarriesOnlyTheTypeName()
    {
        var (rig, scope, item) = await PendingLevel3(this);
        rig.Write.Throw = new InvalidOperationException("clipboard held SSN 321-54-9876");

        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Failed, result.Outcome);
        Assert.Equal(ProtectionState.Error, rig.Tracker.Current.State);
        Assert.Equal(nameof(InvalidOperationException), rig.Tracker.Current.FailureTypeName);
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
        var presentation = ProtectionStatusPresenter.Present(rig.Tracker.Current);
        Assert.DoesNotContain("321-54-9876", presentation.Detail + presentation.Tooltip + rig.Tracker.Current);
        Assert.True(presentation.IsWarning);
    }

    [Fact]
    public async Task AFailedProtect_CanBeRetried_AndOnlyTheVerifiedRetryReleasesTheLimitedState()
    {
        var (rig, scope, item) = await PendingLevel3(this);
        rig.Write.Result = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true);
        await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
        Assert.Equal(ProtectionState.Limited, rig.Tracker.Current.State);

        rig.Write.Result = ClipboardWriteResult.Success(resultSequence: 3);
        var retry = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, retry.Outcome);
        Assert.Equal(ProtectionState.Verified, rig.Tracker.Current.State);
        Assert.False(rig.Tracker.Current.UnresolvedFailure);
        Assert.Equal(2, rig.Write.CallCount);
    }

    // ===== 3. an unfinished or no-write decision is never Verified ================================================================

    [Fact]
    public async Task TheFirstOfTwoPendingItems_ProtectedAlone_WritesNothing_AndTheStatusStaysNeedsDecision()
    {
        var rig = NewRig(UsPipeline);
        await rig.ScanAsync(TwoLevel3);
        var (scope, items) = rig.Pending();
        Assert.Equal(2, items.Length);
        var seen = Watch(rig.Tracker);

        var first = await rig.Resolver.ResolveAsync(scope, items[0], ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, first.Outcome);
        Assert.False(first.ScopeCommitted);                                  // a sibling is still unresolved: no partial write, no partial commit
        Assert.Equal(0, rig.Write.CallCount);
        Assert.Equal(ProtectionState.NeedsDecision, rig.Tracker.Current.State);
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);

        var second = await rig.Resolver.ResolveAsync(scope, items[1], ClipboardDecisionIntent.Protect);

        Assert.True(second.ScopeCommitted);
        Assert.Equal(1, rig.Write.CallCount);
        Assert.Equal(ProtectionState.Verified, rig.Tracker.Current.State);
        Assert.DoesNotContain("321-54-9876", rig.Write.Texts[0]);
        Assert.DoesNotContain("912-70-1234", rig.Write.Texts[0]);
        Assert.DoesNotContain(seen.Take(seen.Count - 1), s => s.State == ProtectionState.Verified);   // Verified appears once, at the end
    }

    [Fact]
    public async Task Level1BypassOnce_WritesNothing_AndIsNeverReportedAsVerified()
    {
        var rig = NewRig(UsPipeline);
        await rig.ScanAsync(Level1Gps);
        var (scope, items) = rig.Pending();
        var seen = Watch(rig.Tracker);

        var result = await rig.Resolver.ResolveAsync(scope, Assert.Single(items), ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.False(result.ClipboardMutated);
        Assert.True(result.ScopeCommitted);
        Assert.Equal(0, rig.Write.CallCount);
        // The decision is no longer pending: the status says the user kept the original (BypassOnce_Terminal status, see BypassOnceTerminalStatusTests).
        // It is never Verified and never "protected": the text is exactly as copied.
        Assert.DoesNotContain(seen, s => s.State == ProtectionState.Verified);
        Assert.True(rig.Tracker.Current.DecisionKeptOriginal);
        Assert.False(rig.Tracker.Current.ClaimsProtected);
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
    }

    [Fact]
    public async Task Level3BypassOnce_NeedsTwoConfirmations_AndNeitherIsReportedAsVerified()
    {
        var (rig, scope, item) = await PendingLevel3(this);
        var seen = Watch(rig.Tracker);

        var first = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, first.Outcome);
        Assert.Equal(ProtectionState.NeedsDecision, rig.Tracker.Current.State);

        var second = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.True(second.ScopeCommitted);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.DoesNotContain(seen, s => s.State == ProtectionState.Verified);
        Assert.True(rig.Tracker.Current.DecisionKeptOriginal);               // only after the second confirmation, never after the first
        Assert.False(rig.Tracker.Current.ClaimsProtected);
    }

    [Fact]
    public async Task ACancelledDecision_NoResolverCall_LeavesNeedsDecision_AndTheScopeAnswerable()
    {
        var (rig, scope, _) = await PendingLevel3(this);

        await Task.Delay(200);

        Assert.Equal(ProtectionState.NeedsDecision, rig.Tracker.Current.State);
        Assert.True(rig.Lifecycle.IsActive(scope));
        Assert.Equal(0, rig.Write.CallCount);
    }

    [Fact]
    public async Task WhenTheTargetIsNoLongerSupported_TheDecisionIsStale_AndTheStatusIsUnchanged()
    {
        var (rig, scope, item) = await PendingLevel3(this);
        rig.Target.SnapshotToReturn = new(IsResolved: true, ProcessId: 9999, ProcessName: "notepad");
        var seen = Watch(rig.Tracker);

        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.Empty(seen);
        Assert.Equal(ProtectionState.NeedsDecision, rig.Tracker.Current.State);
    }

    // ===== 4. races: an old decision never changes the status of newer content ===============================================

    [Fact]
    public async Task ADecisionOnReplacedContent_IsStale_AndNeverShowsVerified()
    {
        var (rig, scope, item) = await PendingLevel3(this);
        await rig.ScanAsync(Clean);                                          // the user copied something else: the pending decision is superseded
        var seen = Watch(rig.Tracker);

        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.Empty(seen);
        Assert.Equal(ProtectionState.Ready, rig.Tracker.Current.State);
    }

    [Fact]
    public async Task ALateProtectSuccess_AfterANewCopy_NeverShowsVerifiedForTheNewContent_AtAnyMoment()
    {
        var write = new ScriptedWrite { Hold = true };
        var (rig, scope, item) = await PendingLevel3(this, write);
        var seen = Watch(rig.Tracker);

        var pending = rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
        await write.Entered;                                                 // the old Protect write is in flight
        rig.Copy(Clean);                                                     // meanwhile the user copies new, clean content
        write.Release(ClipboardWriteResult.Success(resultSequence: 9));
        var result = await pending;
        await WaitAsync(() => rig.Tracker.Current.State == ProtectionState.Ready && rig.Tracker.Current.AttemptId >= 2);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.True(result.ClipboardMutated);                                // honest: the old write did happen, it is just no longer the current content
        Assert.DoesNotContain(seen, s => s.State == ProtectionState.Verified);
        Assert.DoesNotContain(seen, s => s.State is ProtectionState.Limited or ProtectionState.Error);
        Assert.False(rig.Tracker.Current.UnresolvedFailure);
        Assert.Equal(0, rig.Handoff.CallCount);                              // and no composer check is published for superseded content
    }

    [Fact]
    public async Task ALateProtectFailure_AfterANewCopy_NeverMarksTheNewContentLimitedOrError_AtAnyMoment()
    {
        var write = new ScriptedWrite { Hold = true };
        var (rig, scope, item) = await PendingLevel3(this, write);
        var seen = Watch(rig.Tracker);

        var pending = rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
        await write.Entered;
        rig.Copy(Clean);
        write.Release(ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true));
        await pending;
        await WaitAsync(() => rig.Tracker.Current.State == ProtectionState.Ready && rig.Tracker.Current.AttemptId >= 2);

        Assert.DoesNotContain(seen, s => s.State is ProtectionState.Limited or ProtectionState.Error or ProtectionState.Verified);
        Assert.False(rig.Tracker.Current.UnresolvedFailure);
    }

    [Fact]
    public async Task AGenerationReplacedBetweenTheCommitAndTheReport_IsNotReportedAsVerified()
    {
        var (rig, scope, item) = await PendingLevel3(this);
        var seen = Watch(rig.Tracker);
        rig.Handoff.OnPublish = () => rig.Copy(Clean);                       // the new copy arrives at the last moment, after the write and the commit

        var result = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
        await WaitAsync(() => rig.Tracker.Current.State == ProtectionState.Ready && rig.Tracker.Current.AttemptId >= 2);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);   // the resolver cannot know; the status must not claim it for the new content
        Assert.DoesNotContain(seen, s => s.State == ProtectionState.Verified);
        Assert.Equal(ProtectionState.Ready, rig.Tracker.Current.State);
    }

    [Fact]
    public async Task ARepeatedDecisionForTheSameRevision_IsStale_WritesNothingMore_AndKeepsTheVerifiedStatus()
    {
        var (rig, scope, item) = await PendingLevel3(this);
        await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        var again = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, again.Outcome);
        Assert.Equal(1, rig.Write.CallCount);
        Assert.Equal(ProtectionState.Verified, rig.Tracker.Current.State);
    }

    [Fact]
    public async Task TwoConcurrentDecisions_WriteOnce_OneIsStale_AndTheStatusIsVerifiedExactlyOnce()
    {
        var write = new ScriptedWrite { Hold = true };
        var (rig, scope, item) = await PendingLevel3(this, write);
        var seen = Watch(rig.Tracker);

        var first = rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
        await write.Entered;
        var second = rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);   // waits on the shared gate behind the first
        write.Release(ClipboardWriteResult.Success(resultSequence: 9));
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, write.CallCount);
        Assert.Equal(1, write.MaxConcurrent);
        Assert.Single(results, r => r.Outcome == ClipboardDecisionActionOutcome.Applied);
        Assert.Single(results, r => r.Outcome == ClipboardDecisionActionOutcome.Stale);
        Assert.Single(seen, s => s.State == ProtectionState.Verified);
        Assert.Equal(ProtectionState.Verified, rig.Tracker.Current.State);
    }

    [Fact]
    public async Task DisposingTheCoordinatorAndTracker_WhileADecisionWriteIsInFlight_DoesNotThrow_AndNothingIsReportedAfterwards()
    {
        var write = new ScriptedWrite { Hold = true };
        var (rig, scope, item) = await PendingLevel3(this, write);
        var seen = Watch(rig.Tracker);

        var pending = rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);
        await write.Entered;
        rig.Tracker.Dispose();
        rig.Coordinator.Dispose();
        write.Release(ClipboardWriteResult.Success(resultSequence: 9));
        var ex = await Record.ExceptionAsync(() => pending);

        Assert.Null(ex);
        Assert.DoesNotContain(seen, s => s.State == ProtectionState.Verified);   // a disposed tracker reports nothing new
    }

    [Fact]
    public async Task AFailingRecorder_NeverChangesTheDecisionResult()
    {
        var recorder = new ThrowingRecorder();
        var rig = NewRig(UsPipeline, resolverRecorder: recorder);
        await rig.ScanAsync(Level3Ssn);
        var (scope, items) = rig.Pending();

        var result = await rig.Resolver.ResolveAsync(scope, Assert.Single(items), ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.True(result.ScopeCommitted);
        Assert.Equal(1, recorder.Calls);
    }

    // ===== 5. nothing else moves: Korean / global behavior and the shipped composition ===========================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AKoreanLevel3Decision_IsVerifiedAfterProtect_WithTheUsPackOnOrOff(bool usOn)
    {
        var setting = usOn ? CountrySettingObservation.AuthenticatedOn : CountrySettingObservation.AuthenticatedOff;
        var rig = NewRig(() => UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting));
        await rig.ScanAsync(Level3Rrn);
        var (scope, items) = rig.Pending();

        await rig.Resolver.ResolveAsync(scope, Assert.Single(items), ClipboardDecisionIntent.Protect);

        Assert.Equal(ProtectionState.Verified, rig.Tracker.Current.State);
    }

    [Fact]
    public async Task AGlobalEmail_IsStillVerifiedByTheCoordinatorAlone_WithoutAnyDecision()
    {
        var rig = NewRig(UsPipeline);

        var status = await rig.ScanAsync("이메일 user@example.test");

        Assert.Equal(ProtectionState.Verified, status.State);
        Assert.Equal(1, rig.Write.CallCount);
        Assert.False(rig.Lifecycle.HasActiveScope);
    }

    private static readonly FieldInfo ResolverRecorder =
        typeof(ClipboardDecisionActionResolver).GetField("_diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private PrivonAppComposition StartedComposition(Func<DetectionPipeline>? source)
    {
        var root = Path.Combine(Path.GetTempPath(), "DecisionStatusSync", Guid.NewGuid().ToString("N"));
        var composition = new PrivonAppComposition(
            root, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(),
            new ForegroundChangeMonitor(), enableDiagnostics: false, detectionPipelineSource: source);
        _disposables.Add(composition);
        _disposables.Add(new TempDirectory(root));
        composition.Start();
        return composition;
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public void Dispose()
        {
            try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void TheOptInComposition_HandsTheSameTrackerToTheResolverAndTheCoordinator()
    {
        var composition = StartedComposition(UsPipeline);

        Assert.NotNull(composition.ProtectionStatus);
        Assert.NotNull(ResolverRecorder);
        Assert.Same(composition.ProtectionStatus, ResolverRecorder.GetValue(composition.Resolver!));
    }

    [Fact]
    public void TheShippedComposition_HandsItsTrackerToTheResolverToo()
    {
        var composition = StartedComposition(null);

        // Changed by the Controller-approved shipped status warning: a decision outcome reaches the status the shipped user sees.
        Assert.NotNull(composition.ProtectionStatus);
        Assert.Same(composition.ProtectionStatus, ResolverRecorder.GetValue(composition.Resolver!));
    }
}
