using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. O-3: raw personal text that is still in the clipboard when detection, rewriting or verification fails.
//
// Every test drives the REAL ClipboardPrivacyCoordinator, ClipboardPrivacyProcessor, ClipboardDecisionActionResolver, decision-scope lifecycle and
// protection-status tracker with the same fake clipboard transports the other suites use. Nothing touches the real clipboard, a hook or a window.
//
// WHAT IS KNOWN, AND WHAT IS NOT. These tests pin what the App layer does with the facts the Windows layer reports (a guarded write that is conditional
// on the clipboard sequence number and is read back). They cannot prove the Windows layer's own atomicity, which is a property of the real clipboard
// APIs. The product cannot block a paste or a send (A-1), so "raw text remains" always means "may be pasted".
public sealed class ClipboardRawResidueTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
    }

    private const string Phone = "연락처 010-1234-5678";
    private const string Clean = "회의는 오후 세 시입니다";
    private const string UsSsn = "SSN: 321-54-9876";

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(), new GpsCoordinateDetector(),
    ];

    private static DetectionPipeline UsPipeline() => UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOn);

    // A write transport the test can hold open: the write is "in flight" until released, like a real queued clipboard write.
    private sealed class GatedWrite : IClipboardWriteTransport
    {
        private readonly object _lock = new();
        private TaskCompletionSource<ClipboardWriteResult>? _pending;
        public int CallCount;
        public int MaxConcurrent;
        private int _active;
        public List<string> Texts { get; } = [];
        public List<uint> Sequences { get; } = [];
        public ClipboardWriteResult DefaultResult { get; set; } = ClipboardWriteResult.Success(resultSequence: 2);
        public bool Hold { get; set; }
        public Task Entered => _entered.Task;
        private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ClipboardWriteResult> WriteTextIfSequenceMatchesAsync(
            ForegroundTargetSnapshot expectedTarget, uint expectedSequence, string replacementText, IClipboardAuthorizationFreshness? authorizationFreshness = null)
        {
            lock (_lock)
            {
                Texts.Add(replacementText);
                Sequences.Add(expectedSequence);
                int active = Interlocked.Increment(ref _active);
                MaxConcurrent = Math.Max(MaxConcurrent, active);
                Interlocked.Increment(ref CallCount);
                if (!Hold)
                {
                    Interlocked.Decrement(ref _active);
                    return Task.FromResult(DefaultResult);
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

        public void ReArmEntered() => _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Rig : IDisposable
    {
        private uint _sequence;

        public Rig(Func<DetectionPipeline> source, GatedWrite? write = null, IClipboardWriteTransport? writeOverride = null)
        {
            Lifecycle = new ClipboardDecisionScopeLifecycle();
            Tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
            Gated = write ?? new GatedWrite();
            Processor = new ClipboardPrivacyProcessor(source, new FakeTrustExceptionProvider());
            Handoff = new FakeClipboardComposerVerificationHandoff();
            Coordinator = new ClipboardPrivacyCoordinator(
                Read, Target, Processor, writeOverride ?? Gated, Lifecycle, new ClipboardDecisionSessionPublisher(Lifecycle), new ClipboardOperationGate(),
                Handoff, new FakeClipboardComposerVerificationInvalidation(), diagnostics: Tracker, foregroundTrigger: Foreground);
            Coordinator.Start();
        }

        public FakeClipboardReadTransport Read { get; } = new();
        public FakeForegroundTargetCapture Target { get; } = new();
        public FakeClipboardForegroundTrigger Foreground { get; } = new();
        public GatedWrite Gated { get; }
        public FakeClipboardComposerVerificationHandoff Handoff { get; }
        public ClipboardDecisionScopeLifecycle Lifecycle { get; }
        public ClipboardProtectionStatusTracker Tracker { get; }
        public ClipboardPrivacyProcessor Processor { get; }
        public ClipboardPrivacyCoordinator Coordinator { get; }

        public uint NextSequence() => ++_sequence;

        public uint Copy(string text, bool reliable = true)
        {
            uint sequence = NextSequence();
            Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(sequence, reliable, text));
            Read.RaiseChanged(new ClipboardChangeNotification(sequence, true, true));
            return sequence;
        }

        public async Task<ClipboardProtectionStatus> ScanAsync(string text, bool reliable = true)
        {
            long before = Tracker.Current.CompletedAttempts;
            Copy(text, reliable);
            await WaitAsync(() => Tracker.Current.CompletedAttempts > before);
            return Tracker.Current;
        }

        public void Dispose() => Coordinator.Dispose();
    }

    private Rig NewRig(Func<DetectionPipeline> source, GatedWrite? write = null, IClipboardWriteTransport? writeOverride = null)
    {
        var rig = new Rig(source, write, writeOverride);
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

    // ===== 1. the write fails: raw text remains, and that is what the user is told ================================================

    public static TheoryData<ClipboardWriteOutcome> RejectedBeforeAnyChange => new()
    {
        ClipboardWriteOutcome.NotRunning, ClipboardWriteOutcome.Busy, ClipboardWriteOutcome.SequenceChanged, ClipboardWriteOutcome.NativeFailure,
        ClipboardWriteOutcome.TargetUnavailable, ClipboardWriteOutcome.TargetChanged, ClipboardWriteOutcome.InvalidText,
        ClipboardWriteOutcome.InvalidExpectedSequence, ClipboardWriteOutcome.InvalidExpectedTarget,
    };

    [Theory]
    [MemberData(nameof(RejectedBeforeAnyChange))]
    public async Task AWriteRejectedBeforeAnyChange_LeavesTheRawTextAsLimited_NeverVerified_AndPublishesNoComposerCheck(ClipboardWriteOutcome outcome)
    {
        var rig = NewRig(UsPipeline);
        rig.Gated.DefaultResult = ClipboardWriteResult.Failure(outcome, mutated: false);

        var status = await rig.ScanAsync(Phone);

        Assert.Equal(ProtectionState.Limited, status.State);
        Assert.True(status.UnresolvedFailure);
        Assert.True(status.UnprotectedTextMayRemain);                       // the raw text is still there, and the status says so
        Assert.False(status.ClaimsProtected);
        Assert.Equal(0, rig.Handoff.CallCount);                              // a protected text was never promised to the composer check
    }

    public static TheoryData<ClipboardWriteOutcome> MutatedButNotVerified => new()
    {
        ClipboardWriteOutcome.ReadBackMismatch, ClipboardWriteOutcome.VerificationUnavailable, ClipboardWriteOutcome.Superseded,
        ClipboardWriteOutcome.Busy, ClipboardWriteOutcome.NativeFailure,
    };

    [Theory]
    [MemberData(nameof(MutatedButNotVerified))]
    public async Task AWriteThatChangedTheClipboardButWasNotVerified_IsLimited_NeverVerified(ClipboardWriteOutcome outcome)
    {
        var rig = NewRig(UsPipeline);
        rig.Gated.DefaultResult = ClipboardWriteResult.Failure(outcome, mutated: true);

        var status = await rig.ScanAsync(Phone);

        Assert.Equal(ProtectionState.Limited, status.State);
        Assert.True(status.UnprotectedTextMayRemain);
        Assert.False(status.ClaimsProtected);
        Assert.Equal(0, rig.Handoff.CallCount);
    }

    [Fact]
    public async Task ASuccessThatDidNotMutateTheClipboard_IsNotAVerifiedRewrite()
    {
        var rig = NewRig(UsPipeline);
        rig.Gated.DefaultResult = new ClipboardWriteResult { Outcome = ClipboardWriteOutcome.Success, ClipboardMutated = false, ResultSequence = 2 };

        var status = await rig.ScanAsync(Phone);

        Assert.NotEqual(ProtectionState.Verified, status.State);
        Assert.False(status.ClaimsProtected);
        Assert.Equal(0, rig.Handoff.CallCount);
    }

    [Fact]
    public async Task AWriteTransportThatThrows_IsAnError_AndNotASuccess()
    {
        var rig = NewRig(UsPipeline, writeOverride: new FakeClipboardWriteTransport { ThrowOnWrite = new InvalidOperationException("synthetic transport failure") });

        var status = await rig.ScanAsync(Phone);

        Assert.Equal(ProtectionState.Error, status.State);
        Assert.True(status.UnprotectedTextMayRemain);
        Assert.Equal(0, rig.Handoff.CallCount);
    }

    [Fact]
    public async Task OnlyAVerifiedRewrite_ReachesVerified_AndOnlyThenIsTheComposerCheckPublished()
    {
        var rig = NewRig(UsPipeline);

        var status = await rig.ScanAsync(Phone);

        Assert.Equal(ProtectionState.Verified, status.State);
        Assert.False(status.UnresolvedFailure);
        Assert.Equal(1, rig.Handoff.CallCount);
        Assert.DoesNotContain("010-1234-5678", rig.Handoff.ReceivedExpectedProtectedTexts[0]);     // what is promised is the protected text, not the original
    }

    // ===== 2. the rewrite cannot even be built =================================================================================

    private sealed class NoCanonicalDetector : IDetector
    {
        private readonly EmailDetector _identity = new();
        public string Name => "NoCanonicalDetector";
        public PiiType PiiType => PiiType.Email;
        public DetectorId Id => _identity.Id;
        public int ResolutionRank => _identity.ResolutionRank;
        public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
        {
            string text = context.View.RawText;
            int at = text.IndexOf("NOCANON", StringComparison.Ordinal);
            return at < 0 ? [] : [new DetectionCandidate(
                PiiType.Email, new RawSpan(at, 7), RiskLevel.Level2, DetectionConfidence.High, Canonical: null, Name, DetectorId: Id, ResolutionRank: ResolutionRank)];
        }
    }

    [Fact]
    public async Task ARewriteThatCannotBeBuilt_IsAnError_NothingIsWritten_AndTheRawTextIsReportedAsRemaining()
    {
        var rig = NewRig(() => new DetectionPipeline([new NoCanonicalDetector()]));

        var status = await rig.ScanAsync("see NOCANON here");

        Assert.Equal(ProtectionState.Error, status.State);
        Assert.True(status.UnprotectedTextMayRemain);
        Assert.Equal(0, rig.Gated.CallCount);                                // a failed rewrite never reaches the clipboard
        Assert.Equal(0, rig.Handoff.CallCount);
    }

    // ===== 3. the clipboard changes under us: nothing old is written over new content ==========================================

    [Fact]
    public async Task EveryWriteCarriesTheSequenceOfTheExactTextItWasBuiltFrom_AndAnUnreliableSequenceNeverWrites()
    {
        var rig = NewRig(UsPipeline);

        uint first = rig.Copy(Phone);
        await WaitAsync(() => rig.Gated.CallCount == 1);
        uint second = rig.Copy("연락처 010-9999-8888");
        await WaitAsync(() => rig.Gated.CallCount == 2);

        Assert.Equal([first, second], rig.Gated.Sequences);                  // never a stale or guessed sequence: the write is conditional on THIS content
        Assert.DoesNotContain("010-1234-5678", rig.Gated.Texts[1]);

        long before = rig.Tracker.Current.CompletedAttempts;
        rig.Copy(Phone, reliable: false);
        await WaitAsync(() => rig.Tracker.Current.CompletedAttempts > before);
        Assert.Equal(2, rig.Gated.CallCount);                                // no sequence to condition on -> no write at all
        Assert.Equal(ProtectionState.Limited, rig.Tracker.Current.State);
    }

    [Fact]
    public async Task WhenTheClipboardChangedAfterTheRead_TheRejectedWriteIsRetriedOnlyFromAFreshRead_NeverWithTheStaleText()
    {
        var rig = NewRig(UsPipeline);
        // the first write finds that the clipboard has moved on (another program, or the user); the retry must start from a NEW read
        rig.Gated.DefaultResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.SequenceChanged, mutated: false);
        uint first = rig.Copy(Phone);
        await WaitAsync(() => rig.Gated.CallCount >= 1);

        rig.Gated.DefaultResult = ClipboardWriteResult.Success(resultSequence: 9);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(first + 10, true, "연락처 010-7777-6666"));
        rig.Foreground.Raise();                                              // the retry trigger the product already has
        await WaitAsync(() => rig.Tracker.Current.State == ProtectionState.Verified);

        Assert.Equal(first, rig.Gated.Sequences[0]);
        Assert.Equal(first + 10, rig.Gated.Sequences[^1]);                   // the retry is conditional on the NEW content's sequence
        Assert.DoesNotContain("010-1234-5678", rig.Gated.Texts[^1]);
        Assert.Contains("[", rig.Gated.Texts[^1]);                           // and it is built from the new text, not the stale one
    }

    // Every state the status passes through is recorded, so a transient wrong claim cannot hide behind a correct final state.
    private static List<ClipboardProtectionStatus> Watch(ClipboardProtectionStatusTracker tracker)
    {
        var seen = new List<ClipboardProtectionStatus>();
        tracker.Changed += (_, _) => { lock (seen) seen.Add(tracker.Current); };
        return seen;
    }

    [Fact]
    public async Task ALateSuccessOfAnOlderAttempt_NeverShowsVerifiedForNewerContent_AtAnyMoment()
    {
        var gate = new GatedWrite { Hold = true };
        var rig = NewRig(UsPipeline, gate);
        var seen = Watch(rig.Tracker);

        rig.Copy(Phone);                                                     // attempt 1: its write is held in flight
        await gate.Entered;
        gate.Hold = false;
        rig.Copy(Clean);                                                     // attempt 2: new, clean content arrives while attempt 1 is still writing
        await Task.Delay(50);
        gate.Release(ClipboardWriteResult.Success(resultSequence: 2));       // attempt 1's write now reports a verified success
        await WaitAsync(() => rig.Tracker.Current.AttemptId >= 2 && rig.Tracker.Current.State == ProtectionState.Ready);

        Assert.Equal(1, rig.Gated.CallCount);                                // the clean content was never written over
        lock (seen)
        {
            var afterNewCopy = seen.Where(s => s.AttemptId >= 2).ToList();
            Assert.NotEmpty(afterNewCopy);
            Assert.DoesNotContain(afterNewCopy, s => s.State == ProtectionState.Verified);       // the older write's success was never claimed for newer content
        }
    }

    [Fact]
    public async Task ALateFailureOfAnOlderAttempt_NeverMarksNewContentAsFailed_AtAnyMoment()
    {
        var gate = new GatedWrite { Hold = true };
        var rig = NewRig(UsPipeline, gate);
        var seen = Watch(rig.Tracker);

        rig.Copy(Phone);
        await gate.Entered;
        gate.Hold = false;
        rig.Copy(Clean);
        await Task.Delay(50);
        gate.Release(ClipboardWriteResult.Failure(ClipboardWriteOutcome.SequenceChanged, mutated: false));
        await WaitAsync(() => rig.Tracker.Current.AttemptId >= 2 && rig.Tracker.Current.State == ProtectionState.Ready);

        // the clean content that replaced the old raw text is scanned and found clean: the earlier failure no longer describes the clipboard
        Assert.False(rig.Tracker.Current.UnresolvedFailure);
        lock (seen)
        {
            Assert.DoesNotContain(seen.Where(s => s.AttemptId >= 2), s => s.State is ProtectionState.Limited or ProtectionState.Error);
        }
    }

    // ===== 4. concurrency and teardown =========================================================================================

    [Fact]
    public async Task TwoCopiesInARow_AreWrittenOneAtATime_EachFromItsOwnText()
    {
        var rig = NewRig(UsPipeline);
        for (int i = 0; i < 12; i++)
        {
            rig.Copy($"연락처 010-1234-{5000 + i}");
        }

        await WaitAsync(() => rig.Tracker.Current.State == ProtectionState.Verified && rig.Gated.CallCount >= 1);
        await Task.Delay(100);

        Assert.Equal(1, rig.Gated.MaxConcurrent);                            // the shared operation gate serializes every write
        Assert.Equal(rig.Gated.Sequences.Count, rig.Gated.Sequences.Distinct().Count());
        Assert.Equal(rig.Gated.Sequences.Order().ToList(), rig.Gated.Sequences);   // never an older content written after a newer one
    }

    [Fact]
    public async Task DisposingWhileAWriteIsInFlight_NeverWritesAgain_AndNeverPublishesAProtectedTextOrThrows()
    {
        var gate = new GatedWrite { Hold = true };
        var rig = NewRig(UsPipeline, gate);
        rig.Copy(Phone);
        await gate.Entered;

        var disposing = Task.Run(rig.Coordinator.Dispose);
        await Task.Delay(100);
        gate.Release(ClipboardWriteResult.Success(resultSequence: 2));
        await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, gate.CallCount);                                     // nothing more was written after the teardown began
        Assert.True(disposing.IsCompletedSuccessfully || !disposing.IsFaulted);
        Assert.Throws<ObjectDisposedException>(() => rig.Coordinator.Start());
    }

    // ===== 5. Level 3 and the user's decision: the raw text stays until the user decides, and the status says so ================

    [Fact]
    public async Task ALevel3Candidate_IsNeverRewrittenOrWritten_UntilTheUserDecides_AndTheStatusSaysTheRawTextRemains()
    {
        var rig = NewRig(UsPipeline);

        var status = await rig.ScanAsync(UsSsn);

        Assert.Equal(ProtectionState.NeedsDecision, status.State);
        Assert.True(status.UnprotectedTextMayRemain);
        Assert.False(status.ClaimsProtected);
        Assert.Equal(0, rig.Gated.CallCount);
        var presentation = ProtectionStatusPresenter.Present(status);
        Assert.Contains("original text remains on the clipboard", presentation.Detail);
    }

    [Fact]
    public async Task AProtectCandidateBesideALevel3One_StaysRawUntilTheDecision_ByDesign_AndTheStatusSaysSo()
    {
        var rig = NewRig(UsPipeline);

        var status = await rig.ScanAsync("연락처 010-1234-5678 " + UsSsn);

        // NEEDSDECISION_BLOCKS_REPLACEMENT (frozen): one pending decision holds back the WHOLE rewrite, so the phone number is not replaced either.
        Assert.Equal(ProtectionState.NeedsDecision, status.State);
        Assert.Equal(0, rig.Gated.CallCount);
        Assert.True(status.UnprotectedTextMayRemain);
    }

    [Fact]
    public async Task IfTheUserNeverDecides_TheStateDoesNotDecayIntoReadyOrVerified()
    {
        var rig = NewRig(UsPipeline);
        await rig.ScanAsync(UsSsn);

        await Task.Delay(300);                                               // no resolver call: the prompt was closed or ignored

        Assert.Equal(ProtectionState.NeedsDecision, rig.Tracker.Current.State);
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
        Assert.True(rig.Lifecycle.HasActiveScope);                           // and the pending decision is still answerable, not silently gone
    }

    [Fact]
    public async Task AFailedWriteAfterTheUsersProtectDecision_LeavesTheStatusUnprotected_NeverVerified()
    {
        var rig = NewRig(UsPipeline);
        await rig.ScanAsync(UsSsn);
        var scope = rig.Lifecycle.GetActiveScope()!;
        var item = Assert.Single(scope.Items);
        var resolverWrite = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true) };
        var read = new FakeClipboardReadTransport { NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, UsSsn)) };
        var resolver = new ClipboardDecisionActionResolver(
            new ClipboardOperationGate(), rig.Lifecycle, rig.Target, read, resolverWrite, rig.Processor, new FakeClipboardComposerVerificationHandoff());

        var result = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.MutatedUnverified, result.Outcome);
        Assert.False(result.ScopeCommitted);                                 // nothing is committed, no pass is minted from a failed rewrite
        Assert.Equal(ProtectionState.NeedsDecision, rig.Tracker.Current.State);
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
    }

    // ===== 6. regressions: Korean / global and the U.S. opt-in pipeline =========================================================

    [Fact]
    public async Task KoreanPhoneAndGlobalEmail_AreStillRewrittenAndVerified_WithTheUsPackOnOrOff()
    {
        foreach (var setting in new[] { CountrySettingObservation.AuthenticatedOn, CountrySettingObservation.AuthenticatedOff })
        {
            var rig = NewRig(() => UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting));

            var phone = await rig.ScanAsync(Phone);
            Assert.Equal(ProtectionState.Verified, phone.State);
            var email = await rig.ScanAsync("이메일 user@example.test");
            Assert.Equal(ProtectionState.Verified, email.State);
            Assert.Equal(2, rig.Gated.CallCount);
        }
    }

    [Fact]
    public async Task AUsPhone_IsRewrittenAndVerifiedWhenTheUsPackIsOn_AndLeftAloneWhenItIsOff()
    {
        var on = NewRig(() => UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOn, authority: UsAuthoritySet.Production));
        var off = NewRig(() => UsOptInPipeline.Create(Legacy(), unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, authority: UsAuthoritySet.Production));

        var onStatus = await on.ScanAsync("Phone: 212-555-1212");
        var offStatus = await off.ScanAsync("Phone: 212-555-1212");

        Assert.Equal(ProtectionState.Verified, onStatus.State);
        Assert.DoesNotContain("212-555-1212", on.Gated.Texts[0]);
        Assert.Equal(ProtectionState.Ready, offStatus.State);                // nothing is scanned for the U.S. when it is OFF: nothing is claimed either
        Assert.Equal(0, off.Gated.CallCount);
    }

    [Fact]
    public void TheShippedComposition_NowBuildsTheStatusTracker_SoAFailureIsSurfacedThere()
    {
        // This pinned the O-3 residual "the shipped app has no status tracker". The Controller approved showing the protection status in the shipped app
        // (see ShippedProtectionWarningTests), so the tracker is built for every composition and no longer only behind the opt-in condition.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.App", "PrivonAppComposition.cs"))) dir = dir.Parent;
        string composition = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Privon.App", "PrivonAppComposition.cs"));

        string normalized = composition.Replace("\r\n", "\n");
        Assert.Contains("_protectionStatus = new ClipboardProtectionStatusTracker(_diagnostics);", normalized);
        Assert.DoesNotContain("if (_detectionPipelineSource is not null)\n        {\n            _protectionStatus", normalized);
    }
}
